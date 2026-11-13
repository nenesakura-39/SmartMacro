using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Vision;
using SmartMacro.Services.Input;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace SmartMacro.Services.Vision;

/// <summary>
/// Hardware-accelerated screen capture powered by <c>Windows.Graphics.Capture</c> (WGC) and Direct3D 11 staging
/// textures (<c>CopySubresourceRegion</c> of only the requested region). Falls back automatically and transparently to
/// <see cref="GdiScreenCapture"/> when GPU acceleration is disabled (<see cref="HardwareAccelerationMode.CpuOnly"/>),
/// no hardware D3D11 GPU is present, a region spans multiple monitors, a specific window handle is requested
/// (preserving occluded <c>PrintWindow</c> support), or WGC session creation / frame capture fails transiently.
/// </summary>
public sealed class WindowsGraphicsCaptureService : IScreenCapture, IDisposable
{
    private const int DxgiFormatB8G8R8A8Unorm = 87;
    private const int D3dDriverTypeHardware = 1;
    private const uint D3d11CreateDeviceBgraSupport = 0x20;
    private const uint D3d11SdkVersion = 7;
    private const uint D3d11UsageStaging = 3;
    private const uint D3d11CpuAccessRead = 0x20000;
    private const uint D3d11MapRead = 1;
    private const int DxgiErrorDeviceRemoved = unchecked((int)0x887A0005);
    private const int DxgiErrorDeviceReset = unchecked((int)0x887A0007);

    private const int SmallStagingSize = 512;
    private const int FirstFrameTimeoutMs = 250;
    private const long IdleSessionTimeoutMs = 15_000;
    private const int IdleSweepIntervalMs = 5_000;

    private static readonly Guid IidDxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid IidD3d11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid IidDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid IidGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IidGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IidGraphicsCaptureSession3 = new("F2CDD966-22AE-5EA1-9596-3A289344C3BE");

    private readonly GdiScreenCapture _gdiFallback;
    private readonly bool _ownsGdiFallback;
    private readonly Action<string>? _logInfo;
    private readonly Action<string, Exception?>? _logWarning;

    private readonly object _d3dLock = new();
    private readonly object _sessionRegistryLock = new();
    private readonly ConcurrentDictionary<nint, MonitorSession> _sessions = new();
    private readonly FrameBus _liveFrameBus = new(capacity: 8);
    private readonly System.Threading.Timer _idleTimer;

    private nint _d3dDevicePtr;
    private nint _d3dContextPtr;
    private IDirect3DDevice? _winrtDevice;
    private nint _itemInteropPtr;
    private CreateTexture2DFn? _createTexture2D;
    private CopySubresourceRegionFn? _copySubresourceRegion;
    private MapFn? _map;
    private UnmapFn? _unmap;
    private CreateForMonitorFn? _createForMonitor;

    private nint _smallStagingPtr;
    private nint _largeStagingPtr;
    private int _largeStagingWidth;
    private int _largeStagingHeight;

    private bool _wgcOsSupported;
    private bool _wgcInitialized;
    private bool _wgcFaulted;
    private bool _disposed;

    private long _frameSequence;
    private long _wgcCaptureCount;
    private long _gdiFallbackCount;
    private string _lastCaptureBackend = "GDI BitBlt";

    public WindowsGraphicsCaptureService(
        GdiScreenCapture? gdiFallback = null,
        Action<string>? logInfo = null,
        Action<string, Exception?>? logWarning = null)
    {
        _gdiFallback = gdiFallback ?? new GdiScreenCapture();
        _ownsGdiFallback = gdiFallback is null;
        _logInfo = logInfo;
        _logWarning = logWarning;

        _idleTimer = new System.Threading.Timer(
            static state => ((WindowsGraphicsCaptureService)state!).SweepIdleSessions(),
            this,
            Timeout.Infinite,
            Timeout.Infinite);

        HardwareAccelerationInfo.SettingsChanged += OnHardwareSettingsChanged;
        ProbeWgcCapability();
        SyncReportedCaptureBackend();
    }

    /// <summary>True because GPU sub-region staging copies only the requested pixels instead of the entire desktop.</summary>
    public bool PrefersSmallCaptures => true;

    public long LatestFrameSequence => Interlocked.Read(ref _frameSequence);

    public event Action? FrameUpdated;

    /// <summary>Recent frames captured through this service, updated whenever frames are read back.</summary>
    public FrameBus LiveFrameBus => _liveFrameBus;

    public long WgcCaptureCount => Interlocked.Read(ref _wgcCaptureCount);

    public long GdiFallbackCount => Interlocked.Read(ref _gdiFallbackCount);

    public int ActiveSessionCount => _sessions.Count;

    public string LastCaptureBackend
    {
        get { lock (_sessionRegistryLock) return _lastCaptureBackend; }
    }

    public ScreenRect GetVirtualScreen() => _gdiFallback.GetVirtualScreen();

    public Task<ScreenFrame> CaptureAsync(ScreenRect region, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var frame = CaptureCore(region, cancellationToken);
        return Task.FromResult(frame);
    }

    public Task<ScreenFrame> CaptureWindowAsync(long windowHandle, ScreenRect region, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (windowHandle == 0)
            return CaptureAsync(region, cancellationToken);

        // Preserve GDI / PrintWindow(PW_RENDERFULLCONTENT) capture for occluded background windows.
        var frame = _gdiFallback.CaptureWindowAsync(windowHandle, region, cancellationToken).GetAwaiter().GetResult();
        RecordGdiCapture(frame);
        return Task.FromResult(frame);
    }

    /// <summary>Synchronous capture entry point for interactive UI diagnostics (<see cref="VisionTools"/>).</summary>
    public ScreenFrame Capture(ScreenRect region) => CaptureCore(region, CancellationToken.None);

    /// <summary>
    /// Closes idle or all active WGC monitor capture sessions immediately so Windows releases compositor capture hooks
    /// and hides any Windows 10 capture indicator border as soon as playback finishes or switches to CPU Only mode.
    /// </summary>
    public void ReleaseIdleSessions(bool forceAll = true)
    {
        var now = Environment.TickCount64;
        foreach (var pair in _sessions.ToArray())
        {
            var session = pair.Value;
            if (!forceAll && now - session.LastUsedTick < IdleSessionTimeoutMs)
                continue;

            if (_sessions.TryRemove(pair.Key, out var removed))
                removed.Dispose();
        }

        UpdateIdleTimer();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        HardwareAccelerationInfo.SettingsChanged -= OnHardwareSettingsChanged;
        _idleTimer.Dispose();
        ReleaseIdleSessions(forceAll: true);
        ReleaseD3DResources();

        if (_ownsGdiFallback)
            _gdiFallback.Dispose();
    }

    private void OnHardwareSettingsChanged()
    {
        if (_disposed)
            return;

        if (!HardwareAccelerationInfo.IsGpuActive)
        {
            ReleaseIdleSessions(forceAll: true);
        }
        else if (!_wgcInitialized && !_wgcFaulted)
        {
            ProbeWgcCapability();
        }

        SyncReportedCaptureBackend();
    }

    private void SyncReportedCaptureBackend()
    {
        var backend = CanUseWgc() ? "WGC / D3D11" : "GDI BitBlt";
        HardwareAccelerationInfo.RegisterCaptureBackend(backend);
    }

    private bool CanUseWgc() =>
        !_disposed
        && HardwareAccelerationInfo.IsGpuActive
        && _wgcOsSupported
        && !_wgcFaulted
        && _wgcInitialized;

    private void ProbeWgcCapability()
    {
        try
        {
            _wgcOsSupported =
                ApiInformation.IsTypePresent("Windows.Graphics.Capture.GraphicsCaptureSession")
                && GraphicsCaptureSession.IsSupported();

            if (!_wgcOsSupported)
            {
                _logInfo?.Invoke("Windows.Graphics.Capture is unavailable on this Windows build; using GDI BitBlt.");
                return;
            }

            if (TryEnsureD3DResources())
                _logInfo?.Invoke("Windows.Graphics.Capture (D3D11 staging) ready.");
        }
        catch (Exception ex)
        {
            _wgcOsSupported = false;
            _logWarning?.Invoke("Windows.Graphics.Capture probe failed; falling back to GDI BitBlt.", ex);
        }
    }

    private ScreenFrame CaptureCore(ScreenRect region, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (region.IsEmpty)
            throw new ArgumentException("The capture area is empty.", nameof(region));

        CaptureShield.BeforeCapture();

        var clipped = region.Intersect(GetVirtualScreen());
        if (clipped.IsEmpty)
            throw new ArgumentException($"The search area {region} is outside the screen. Pick the area again.");

        if (CanUseWgc() && TryFindSingleMonitor(clipped, out var monitorHandle, out var monitorBounds))
        {
            if (TryCaptureViaWgc(monitorHandle, monitorBounds, clipped, cancellationToken, out var wgcFrame))
            {
                RecordWgcCapture(wgcFrame!);
                return wgcFrame!;
            }
        }

        var gdiFrame = _gdiFallback.Capture(clipped);
        RecordGdiCapture(gdiFrame);
        return gdiFrame;
    }

    private void RecordWgcCapture(ScreenFrame frame)
    {
        Interlocked.Increment(ref _wgcCaptureCount);
        lock (_sessionRegistryLock)
            _lastCaptureBackend = "WGC / D3D11";
        _liveFrameBus.Publish(frame, Environment.TickCount64);
    }

    private void RecordGdiCapture(ScreenFrame frame)
    {
        Interlocked.Increment(ref _gdiFallbackCount);
        lock (_sessionRegistryLock)
            _lastCaptureBackend = "GDI BitBlt";
        _liveFrameBus.Publish(frame, Environment.TickCount64);
    }

    private bool TryCaptureViaWgc(
        nint monitorHandle,
        ScreenRect monitorBounds,
        ScreenRect clippedRegion,
        CancellationToken cancellationToken,
        out ScreenFrame? frame)
    {
        frame = null;

        try
        {
            if (!TryEnsureD3DResources())
                return false;

            var session = GetOrCreateSession(monitorHandle, monitorBounds);
            if (session is null)
                return false;

            session.Touch();
            if (!session.WaitForAvailableFrame(FirstFrameTimeoutMs, cancellationToken))
                return false;

            byte[]? pixels;
            bool deviceReset;
            lock (_d3dLock)
            {
                if (_disposed || _d3dDevicePtr == 0 || _d3dContextPtr == 0)
                    return false;

                pixels = ReadRegionFromSession(session, clippedRegion, out deviceReset);
            }

            if (deviceReset)
            {
                HandleDeviceReset();
                return false;
            }

            if (pixels is null)
                return false;

            frame = new ScreenFrame(clippedRegion, new BgraImage(clippedRegion.Width, clippedRegion.Height, pixels));
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException)
        {
            _logWarning?.Invoke("WGC frame capture failed; falling back cleanly to GDI BitBlt.", ex);
            if (_sessions.TryRemove(monitorHandle, out var stale))
                stale.Dispose();
            return false;
        }
    }

    private MonitorSession? GetOrCreateSession(nint monitorHandle, ScreenRect monitorBounds)
    {
        if (_sessions.TryGetValue(monitorHandle, out var existing))
        {
            if (!existing.IsClosed && existing.MonitorBounds == monitorBounds)
                return existing;

            if (_sessions.TryRemove(monitorHandle, out var obsolete))
                obsolete.Dispose();
        }

        lock (_sessionRegistryLock)
        {
            if (_disposed)
                return null;

            if (_sessions.TryGetValue(monitorHandle, out existing) && !existing.IsClosed && existing.MonitorBounds == monitorBounds)
                return existing;

            if (!TryEnsureD3DResources() || _winrtDevice is null || _itemInteropPtr == 0 || _createForMonitor is null)
                return null;

            var iidItem = IidGraphicsCaptureItem;
            var hr = _createForMonitor(_itemInteropPtr, monitorHandle, in iidItem, out var itemPtr);
            if (hr < 0 || itemPtr == 0)
                return null;

            GraphicsCaptureItem item;
            try
            {
                item = GraphicsCaptureItem.FromAbi(itemPtr);
            }
            finally
            {
                Marshal.Release(itemPtr);
            }

            if (item.Size.Width <= 0 || item.Size.Height <= 0)
                return null;

            var session = new MonitorSession(monitorHandle, monitorBounds, _winrtDevice, item, OnSessionFrameArrived, OnSessionClosed);
            _sessions[monitorHandle] = session;
            UpdateIdleTimer();
            return session;
        }
    }

    private void OnSessionFrameArrived()
    {
        Interlocked.Increment(ref _frameSequence);
        FrameUpdated?.Invoke();
    }

    private void OnSessionClosed(nint monitorHandle)
    {
        if (_sessions.TryRemove(monitorHandle, out var session))
            session.Dispose();
        UpdateIdleTimer();
    }

    private byte[]? ReadRegionFromSession(MonitorSession session, ScreenRect clippedRegion, out bool deviceReset)
    {
        deviceReset = false;

        return session.WithLatestFrameTexture((srcTexPtr, contentSize) =>
        {
            if (srcTexPtr == 0)
                return null;

            var texDesc = GetTexture2DDesc(srcTexPtr);
            if (texDesc.Format != DxgiFormatB8G8R8A8Unorm)
                return null;

            var localX = clippedRegion.X - session.MonitorBounds.X;
            var localY = clippedRegion.Y - session.MonitorBounds.Y;
            var width = clippedRegion.Width;
            var height = clippedRegion.Height;

            if (localX < 0 || localY < 0 || width <= 0 || height <= 0
                || localX + width > (int)texDesc.Width
                || localY + height > (int)texDesc.Height
                || contentSize.Width <= 0 || contentSize.Height <= 0)
            {
                return null;
            }

            var staging = EnsureStagingTexture(width, height);
            if (staging == 0 || _copySubresourceRegion is null || _map is null || _unmap is null)
                return null;

            var srcBox = new D3D11_BOX
            {
                Left = (uint)localX,
                Top = (uint)localY,
                Front = 0,
                Right = (uint)(localX + width),
                Bottom = (uint)(localY + height),
                Back = 1,
            };

            _copySubresourceRegion(_d3dContextPtr, staging, 0, 0, 0, 0, srcTexPtr, 0, in srcBox);
            return MapAndCopy(staging, width, height);
        }, out deviceReset);
    }

    private byte[]? MapAndCopy(nint stagingPtr, int width, int height)
    {
        if (_map is null || _unmap is null)
            return null;

        var hr = _map(_d3dContextPtr, stagingPtr, 0, D3d11MapRead, 0, out var mapped);
        if (hr < 0 || mapped.pData == 0)
        {
            if (hr is DxgiErrorDeviceRemoved or DxgiErrorDeviceReset)
                throw new COMException("Direct3D11 device was reset during capture readback.", hr);
            return null;
        }

        try
        {
            var stride = width * 4;
            var buffer = new byte[stride * height];
            var rowPitch = (int)mapped.RowPitch;
            if (rowPitch == stride)
            {
                Marshal.Copy(mapped.pData, buffer, 0, buffer.Length);
            }
            else if (rowPitch > stride)
            {
                for (var y = 0; y < height; y++)
                {
                    Marshal.Copy(nint.Add(mapped.pData, y * rowPitch), buffer, y * stride, stride);
                }
            }
            else
            {
                return null;
            }

            return buffer;
        }
        finally
        {
            _unmap(_d3dContextPtr, stagingPtr, 0);
        }
    }

    private nint EnsureStagingTexture(int width, int height)
    {
        if (_d3dDevicePtr == 0 || _createTexture2D is null)
            return 0;

        if (width <= SmallStagingSize && height <= SmallStagingSize)
        {
            if (_smallStagingPtr == 0)
                _smallStagingPtr = CreateStagingTexture(SmallStagingSize, SmallStagingSize);
            return _smallStagingPtr;
        }

        if (_largeStagingPtr == 0 || _largeStagingWidth < width || _largeStagingHeight < height)
        {
            if (_largeStagingPtr != 0)
            {
                Marshal.Release(_largeStagingPtr);
                _largeStagingPtr = 0;
            }

            var created = CreateStagingTexture(width, height);
            if (created != 0)
            {
                _largeStagingPtr = created;
                _largeStagingWidth = width;
                _largeStagingHeight = height;
            }
        }

        return _largeStagingPtr;
    }

    private nint CreateStagingTexture(int width, int height)
    {
        if (_d3dDevicePtr == 0 || _createTexture2D is null)
            return 0;

        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DxgiFormatB8G8R8A8Unorm,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = D3d11UsageStaging,
            BindFlags = 0,
            CPUAccessFlags = D3d11CpuAccessRead,
            MiscFlags = 0,
        };

        var hr = _createTexture2D(_d3dDevicePtr, in desc, 0, out var texPtr);
        return hr >= 0 ? texPtr : 0;
    }

    private bool TryEnsureD3DResources()
    {
        lock (_d3dLock)
        {
            if (_disposed || _wgcFaulted)
                return false;

            if (_wgcInitialized && _d3dDevicePtr != 0 && _d3dContextPtr != 0 && _winrtDevice is not null)
                return true;

            nint devicePtr = 0;
            nint contextPtr = 0;
            nint dxgiDevicePtr = 0;
            nint graphicsDevicePtr = 0;
            nint interopPtr = 0;
            nint hString = 0;

            try
            {
                var hr = D3D11CreateDevice(
                    0,
                    D3dDriverTypeHardware,
                    0,
                    D3d11CreateDeviceBgraSupport,
                    null,
                    0,
                    D3d11SdkVersion,
                    out devicePtr,
                    out _,
                    out contextPtr);
                if (hr < 0 || devicePtr == 0 || contextPtr == 0)
                    return false;

                var iidDxgi = IidDxgiDevice;
                if (Marshal.QueryInterface(devicePtr, in iidDxgi, out dxgiDevicePtr) < 0 || dxgiDevicePtr == 0)
                    return false;

                if (CreateDirect3D11DeviceFromDXGIDevice(dxgiDevicePtr, out graphicsDevicePtr) < 0 || graphicsDevicePtr == 0)
                    return false;

                var winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(graphicsDevicePtr);

                const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
                if (WindowsCreateString(className, (uint)className.Length, out hString) < 0 || hString == 0)
                    return false;

                var iidInterop = IidGraphicsCaptureItemInterop;
                if (RoGetActivationFactory(hString, in iidInterop, out interopPtr) < 0 || interopPtr == 0)
                    return false;

                _d3dDevicePtr = devicePtr;
                _d3dContextPtr = contextPtr;
                _winrtDevice = winrtDevice;
                _itemInteropPtr = interopPtr;
                _createTexture2D = GetVtblMethod<CreateTexture2DFn>(_d3dDevicePtr, 5);
                _map = GetVtblMethod<MapFn>(_d3dContextPtr, 14);
                _unmap = GetVtblMethod<UnmapFn>(_d3dContextPtr, 15);
                _copySubresourceRegion = GetVtblMethod<CopySubresourceRegionFn>(_d3dContextPtr, 46);
                _createForMonitor = GetVtblMethod<CreateForMonitorFn>(_itemInteropPtr, 4);
                _wgcInitialized = true;

                devicePtr = 0;
                contextPtr = 0;
                interopPtr = 0;
                return true;
            }
            catch (Exception ex)
            {
                _wgcFaulted = true;
                _logWarning?.Invoke("Could not initialize WGC Direct3D 11 pipeline; falling back to GDI BitBlt.", ex);
                return false;
            }
            finally
            {
                if (hString != 0)
                    _ = WindowsDeleteString(hString);
                if (graphicsDevicePtr != 0)
                    Marshal.Release(graphicsDevicePtr);
                if (dxgiDevicePtr != 0)
                    Marshal.Release(dxgiDevicePtr);
                if (interopPtr != 0)
                    Marshal.Release(interopPtr);
                if (contextPtr != 0)
                    Marshal.Release(contextPtr);
                if (devicePtr != 0)
                    Marshal.Release(devicePtr);
            }
        }
    }

    private void HandleDeviceReset()
    {
        _logWarning?.Invoke("Direct3D11 device reset detected; resetting WGC sessions and falling back for this frame.", null);
        ReleaseIdleSessions(forceAll: true);
        ReleaseD3DResources();
        SyncReportedCaptureBackend();
    }

    private void ReleaseD3DResources()
    {
        lock (_d3dLock)
        {
            _wgcInitialized = false;
            _createTexture2D = null;
            _copySubresourceRegion = null;
            _map = null;
            _unmap = null;
            _createForMonitor = null;

            if (_smallStagingPtr != 0)
            {
                Marshal.Release(_smallStagingPtr);
                _smallStagingPtr = 0;
            }

            if (_largeStagingPtr != 0)
            {
                Marshal.Release(_largeStagingPtr);
                _largeStagingPtr = 0;
                _largeStagingWidth = 0;
                _largeStagingHeight = 0;
            }

            if (_itemInteropPtr != 0)
            {
                Marshal.Release(_itemInteropPtr);
                _itemInteropPtr = 0;
            }

            if (_d3dContextPtr != 0)
            {
                Marshal.Release(_d3dContextPtr);
                _d3dContextPtr = 0;
            }

            if (_d3dDevicePtr != 0)
            {
                Marshal.Release(_d3dDevicePtr);
                _d3dDevicePtr = 0;
            }

            if (_winrtDevice is IDisposable disposableDevice)
                disposableDevice.Dispose();
            _winrtDevice = null;
        }
    }

    private void SweepIdleSessions()
    {
        if (_disposed)
            return;

        ReleaseIdleSessions(forceAll: false);
    }

    private void UpdateIdleTimer()
    {
        if (_disposed)
            return;

        try
        {
            var due = _sessions.IsEmpty ? Timeout.Infinite : IdleSweepIntervalMs;
            _idleTimer.Change(due, due);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static bool TryFindSingleMonitor(ScreenRect region, out nint monitorHandle, out ScreenRect monitorBounds)
    {
        monitorHandle = 0;
        monitorBounds = default;

        using var _ = NativeMethods.PhysicalPixelsScope.Enter();
        var matchCount = 0;
        nint matchedHandle = 0;
        ScreenRect matchedBounds = default;

        NativeMethods.MonitorEnumProc callback = (hMon, _, _, _) =>
        {
            var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (!NativeMethods.GetMonitorInfo(hMon, ref info))
                return true;

            var bounds = new ScreenRect(
                info.rcMonitor.Left,
                info.rcMonitor.Top,
                info.rcMonitor.Right - info.rcMonitor.Left,
                info.rcMonitor.Bottom - info.rcMonitor.Top);

            if (!bounds.Intersect(region).IsEmpty)
            {
                matchCount++;
                matchedHandle = hMon;
                matchedBounds = bounds;
                if (matchCount > 1)
                    return false;
            }

            return true;
        };

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);

        if (matchCount != 1 || matchedHandle == 0 || matchedBounds.IsEmpty)
            return false;

        monitorHandle = matchedHandle;
        monitorBounds = matchedBounds;
        return true;
    }

    private static D3D11_TEXTURE2D_DESC GetTexture2DDesc(nint texturePtr)
    {
        var getDesc = GetVtblMethod<GetTexture2DDescFn>(texturePtr, 10);
        getDesc(texturePtr, out var desc);
        return desc;
    }

    private static T GetVtblMethod<T>(nint comObj, int slot) where T : Delegate
    {
        var vtbl = Marshal.ReadIntPtr(comObj);
        var fnPtr = Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(fnPtr);
    }

    private sealed class MonitorSession : IDisposable
    {
        private readonly nint _monitorHandle;
        private readonly IDirect3DDevice _d3dDevice;
        private readonly GraphicsCaptureItem _item;
        private readonly Direct3D11CaptureFramePool _framePool;
        private readonly GraphicsCaptureSession _captureSession;
        private readonly Action _onFrameArrivedCallback;
        private readonly Action<nint> _onClosedCallback;
        private readonly object _frameLock = new();

        private SizeInt32 _contentSize;
        private Direct3D11CaptureFrame? _latestFrame;
        private long _lastUsedTick;
        private bool _closed;
        private bool _disposed;

        public MonitorSession(
            nint monitorHandle,
            ScreenRect monitorBounds,
            IDirect3DDevice d3dDevice,
            GraphicsCaptureItem item,
            Action onFrameArrivedCallback,
            Action<nint> onClosedCallback)
        {
            _monitorHandle = monitorHandle;
            MonitorBounds = monitorBounds;
            _d3dDevice = d3dDevice;
            _item = item;
            _onFrameArrivedCallback = onFrameArrivedCallback;
            _onClosedCallback = onClosedCallback;
            _contentSize = item.Size;
            _lastUsedTick = Environment.TickCount64;

            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _d3dDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                numberOfBuffers: 2,
                _contentSize);

            _framePool.FrameArrived += OnPoolFrameArrived;
            _item.Closed += OnItemClosed;

            _captureSession = _framePool.CreateCaptureSession(_item);
            TryDisableCursor(_captureSession);
            TryDisableBorder(_captureSession);
            _captureSession.StartCapture();
        }

        public ScreenRect MonitorBounds { get; }

        public long LastUsedTick => Volatile.Read(ref _lastUsedTick);

        public bool IsClosed
        {
            get { lock (_frameLock) return _closed || _disposed; }
        }

        public void Touch() => Volatile.Write(ref _lastUsedTick, Environment.TickCount64);

        public bool WaitForAvailableFrame(int timeoutMs, CancellationToken cancellationToken)
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            using var cancelReg = cancellationToken.Register(() =>
            {
                lock (_frameLock) Monitor.PulseAll(_frameLock);
            });

            lock (_frameLock)
            {
                while (_latestFrame is null && !_closed && !_disposed)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var remaining = (int)(deadline - Environment.TickCount64);
                    if (remaining <= 0)
                        return false;
                    Monitor.Wait(_frameLock, remaining);
                }

                return _latestFrame is not null && !_closed && !_disposed;
            }
        }

        public byte[]? WithLatestFrameTexture(Func<nint, SizeInt32, byte[]?> reader, out bool deviceReset)
        {
            deviceReset = false;
            nint surfaceAbi = 0;
            nint accessPtr = 0;
            nint texPtr = 0;
            bool needReleaseSurfaceAbi = false;

            try
            {
                // Keep _frameLock held during GPU CopySubresourceRegion (~20 µs) so FrameArrived cannot dispose 'frame'.
                lock (_frameLock)
                {
                    var frame = _latestFrame;
                    if (_disposed || _closed || frame is null)
                        return null;

                    var size = _contentSize;
                    var surface = frame.Surface;
                    if (surface is IWinRTObject winrtObj)
                    {
                        surfaceAbi = winrtObj.NativeObject.ThisPtr;
                    }
                    else
                    {
                        surfaceAbi = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
                        needReleaseSurfaceAbi = true;
                    }

                    if (surfaceAbi == 0)
                        return null;

                    var iidAccess = IidDirect3DDxgiInterfaceAccess;
                    if (Marshal.QueryInterface(surfaceAbi, in iidAccess, out accessPtr) < 0 || accessPtr == 0)
                        return null;

                    var getInterface = GetVtblMethod<GetInterfaceFn>(accessPtr, 3);
                    var iidTex = IidD3d11Texture2D;
                    if (getInterface(accessPtr, in iidTex, out texPtr) < 0 || texPtr == 0)
                        return null;

                    return reader(texPtr, size);
                }
            }
            catch (COMException ex) when (ex.HResult is DxgiErrorDeviceRemoved or DxgiErrorDeviceReset)
            {
                deviceReset = true;
                return null;
            }
            finally
            {
                if (texPtr != 0)
                    Marshal.Release(texPtr);
                if (accessPtr != 0)
                    Marshal.Release(accessPtr);
                if (needReleaseSurfaceAbi && surfaceAbi != 0)
                    Marshal.Release(surfaceAbi);
            }
        }

        public void Dispose()
        {
            Direct3D11CaptureFrame? frameToDispose;
            lock (_frameLock)
            {
                if (_disposed)
                    return;

                _disposed = true;
                _closed = true;
                frameToDispose = _latestFrame;
                _latestFrame = null;
                Monitor.PulseAll(_frameLock);
            }

            try { _item.Closed -= OnItemClosed; } catch { }
            try { _framePool.FrameArrived -= OnPoolFrameArrived; } catch { }
            try { frameToDispose?.Dispose(); } catch { }
            try { _captureSession.Dispose(); } catch { }
            try { _framePool.Dispose(); } catch { }
        }

        private void OnPoolFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            Direct3D11CaptureFrame? nextFrame = null;
            Direct3D11CaptureFrame? oldFrame = null;
            var notify = false;

            try
            {
                nextFrame = sender.TryGetNextFrame();
                if (nextFrame is null)
                    return;

                lock (_frameLock)
                {
                    if (_disposed || _closed)
                    {
                        oldFrame = nextFrame;
                        nextFrame = null;
                    }
                    else
                    {
                        var newSize = nextFrame.ContentSize;
                        if (newSize.Width > 0 && newSize.Height > 0
                            && (newSize.Width != _contentSize.Width || newSize.Height != _contentSize.Height))
                        {
                            _contentSize = newSize;
                            sender.Recreate(_d3dDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, newSize);
                        }

                        oldFrame = _latestFrame;
                        _latestFrame = nextFrame;
                        nextFrame = null;
                        notify = true;
                        Monitor.PulseAll(_frameLock);
                    }
                }
            }
            catch
            {
                oldFrame = nextFrame;
            }
            finally
            {
                try { oldFrame?.Dispose(); } catch { }
            }

            if (notify)
                _onFrameArrivedCallback();
        }

        private void OnItemClosed(GraphicsCaptureItem sender, object args)
        {
            lock (_frameLock)
            {
                _closed = true;
                Monitor.PulseAll(_frameLock);
            }

            _onClosedCallback(_monitorHandle);
        }

        private static void TryDisableCursor(GraphicsCaptureSession session)
        {
            try
            {
                if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", nameof(GraphicsCaptureSession.IsCursorCaptureEnabled)))
                    session.IsCursorCaptureEnabled = false;
            }
            catch
            {
            }
        }

        private static void TryDisableBorder(GraphicsCaptureSession session)
        {
            try
            {
                if (!ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
                    return;

                var sessionAbi = (session as IWinRTObject)?.NativeObject.ThisPtr ?? 0;
                if (sessionAbi == 0)
                    return;

                var iidS3 = IidGraphicsCaptureSession3;
                if (Marshal.QueryInterface(sessionAbi, in iidS3, out var s3Ptr) < 0 || s3Ptr == 0)
                    return;

                try
                {
                    var putBorder = GetVtblMethod<PutBoolFn>(s3Ptr, 7);
                    _ = putBorder(s3Ptr, 0);
                }
                finally
                {
                    Marshal.Release(s3Ptr);
                }
            }
            catch
            {
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_SAMPLE_DESC
    {
        public uint Count;
        public uint Quality;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_TEXTURE2D_DESC
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public DXGI_SAMPLE_DESC SampleDesc;
        public uint Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_BOX
    {
        public uint Left;
        public uint Top;
        public uint Front;
        public uint Right;
        public uint Bottom;
        public uint Back;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_MAPPED_SUBRESOURCE
    {
        public nint pData;
        public uint RowPitch;
        public uint DepthPitch;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateTexture2DFn(nint thisPtr, in D3D11_TEXTURE2D_DESC pDesc, nint pInitialData, out nint ppTexture2D);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GetTexture2DDescFn(nint thisPtr, out D3D11_TEXTURE2D_DESC pDesc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void CopySubresourceRegionFn(
        nint thisPtr,
        nint pDstResource,
        uint dstSubresource,
        uint dstX,
        uint dstY,
        uint dstZ,
        nint pSrcResource,
        uint srcSubresource,
        in D3D11_BOX pSrcBox);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int MapFn(
        nint thisPtr,
        nint pResource,
        uint subresource,
        uint mapType,
        uint mapFlags,
        out D3D11_MAPPED_SUBRESOURCE mappedResource);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void UnmapFn(nint thisPtr, nint pResource, uint subresource);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetInterfaceFn(nint thisPtr, in Guid iid, out nint ppv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateForMonitorFn(nint thisPtr, nint monitor, in Guid riid, out nint result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PutBoolFn(nint thisPtr, byte value);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        nint pAdapter,
        int driverType,
        nint software,
        uint flags,
        [In] uint[]? pFeatureLevels,
        uint featureLevels,
        uint sdkVersion,
        out nint ppDevice,
        out int pFeatureLevel,
        out nint ppImmediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [DllImport("api-ms-win-core-winrt-l1-1-0.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(nint activatableClassId, in Guid iid, out nint factory);

    [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WindowsCreateString(string sourceString, uint length, out nint hstring);

    [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(nint hstring);
}
