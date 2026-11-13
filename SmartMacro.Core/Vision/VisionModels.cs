using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using SmartMacro.Core.Ocr;

namespace SmartMacro.Core.Vision;

/// <summary>Preferred vision & frame-processing compute engine.</summary>
public enum HardwareAccelerationMode
{
    Auto = 0,
    CpuOnly = 1,
    GpuAccelerated = 2
}

/// <summary>
/// Reports the active CPU SIMD vision tier (AVX2 256-bit, SSE2/Vector128 128-bit, or Scalar), optional GPU adapter,
/// RAM template-cache budget, and adaptive worker thread budget so SmartMacro scales cleanly from low-end PCs to
/// dedicated GPU gaming desktops.
/// </summary>
public static class HardwareAccelerationInfo
{
    private static readonly object Sync = new();
    private static HardwareAccelerationMode _computeMode = HardwareAccelerationMode.Auto;
    private static int _memoryLimitMb = 256;
    private static bool _gpuAvailable;
    private static string _gpuAdapterName = "";
    private static int _gpuDedicatedVramMb;
    private static string _captureBackend = "GDI BitBlt";

    /// <summary>Raised outside the lock whenever <see cref="Configure"/> updates the active compute mode or RAM budget.</summary>
    public static event Action? SettingsChanged;

    public static bool IsAvx2Supported => Avx2.IsSupported;

    public static bool IsSse2Supported => Sse2.IsSupported;

    public static bool IsVector128Accelerated => Vector128.IsHardwareAccelerated;

    public static int LogicalProcessorCount => Math.Max(1, Environment.ProcessorCount);

    public static HardwareAccelerationMode ComputeMode
    {
        get { lock (Sync) return _computeMode; }
    }

    public static int MemoryLimitMb
    {
        get { lock (Sync) return _memoryLimitMb; }
    }

    public static bool GpuAvailable
    {
        get { lock (Sync) return _gpuAvailable; }
    }

    public static string GpuAdapterName
    {
        get { lock (Sync) return _gpuAdapterName; }
    }

    public static int GpuDedicatedVramMb
    {
        get { lock (Sync) return _gpuDedicatedVramMb; }
    }

    public static string RegisteredCaptureBackend
    {
        get { lock (Sync) return _captureBackend; }
    }

    /// <summary>
    /// Active screen-capture backend after applying the current compute mode and GPU availability.
    /// Zero-GPU mode (<see cref="HardwareAccelerationMode.CpuOnly"/>) or missing D3D11 GPUs always fall back to GDI BitBlt.
    /// </summary>
    public static string EffectiveCaptureBackend
    {
        get
        {
            lock (Sync)
            {
                if (_computeMode == HardwareAccelerationMode.CpuOnly || !_gpuAvailable)
                    return "GDI BitBlt";
                return string.IsNullOrWhiteSpace(_captureBackend) ? "GDI BitBlt" : _captureBackend;
            }
        }
    }

    /// <summary>True when GPU acceleration is both requested/auto-selected and backed by a hardware D3D11 adapter.</summary>
    public static bool IsGpuActive
    {
        get
        {
            lock (Sync)
                return _gpuAvailable && _computeMode != HardwareAccelerationMode.CpuOnly;
        }
    }

    /// <summary>Registers the detected Direct3D 11 / DXGI GPU adapter (called at startup or by tests).</summary>
    public static void RegisterGpuAdapter(bool available, string? adapterName, int dedicatedVramMb = 0)
    {
        lock (Sync)
        {
            _gpuAvailable = available;
            _gpuAdapterName = adapterName?.Trim() ?? "";
            _gpuDedicatedVramMb = Math.Max(0, dedicatedVramMb);
        }
    }

    /// <summary>Registers the initialized capture service backend name (e.g. "WGC / D3D11" or "GDI BitBlt").</summary>
    public static void RegisterCaptureBackend(string? backendName)
    {
        lock (Sync)
        {
            _captureBackend = string.IsNullOrWhiteSpace(backendName) ? "GDI BitBlt" : backendName.Trim();
        }
    }

    /// <summary>Applies user compute mode and RAM cache budget immediately.</summary>
    public static void Configure(HardwareAccelerationMode mode, int memoryLimitMb)
    {
        var clampedMb = Math.Clamp(memoryLimitMb, 64, 1024);
        lock (Sync)
        {
            _computeMode = Enum.IsDefined(mode) ? mode : HardwareAccelerationMode.Auto;
            _memoryLimitMb = clampedMb;
        }
        TemplateCache.Shared.ConfigureMemoryBudget(clampedMb);
        SettingsChanged?.Invoke();
    }

    /// <summary>
    /// Reserves at least 1 logical processor for the foreground game / OS and caps vision worker threads at 8
    /// (or slightly lower on strict 2-core low-end setups) so playback stays responsive without starving the app.
    /// </summary>
    public static int MaxVisionWorkers => Math.Clamp(LogicalProcessorCount - 1, 1, 8);

    public static string SimdTierName =>
        Avx2.IsSupported ? "AVX2 256-bit SIMD (8 px/op + SSE2 4 px tail)"
        : Sse2.IsSupported ? "SSE2 128-bit SIMD (4 px/op)"
        : Vector128.IsHardwareAccelerated ? "Vector128 128-bit SIMD"
        : "Scalar fallback";

    public static string StatusText
    {
        get
        {
            HardwareAccelerationMode mode;
            int ramMb;
            bool gpu;
            string gpuName;
            int vramMb;
            string captureBackend;
            lock (Sync)
            {
                mode = _computeMode;
                ramMb = _memoryLimitMb;
                gpu = _gpuAvailable;
                gpuName = _gpuAdapterName;
                vramMb = _gpuDedicatedVramMb;
                captureBackend = _captureBackend;
            }

            var gpuPart = mode == HardwareAccelerationMode.CpuOnly
                ? "CPU Only (Zero-GPU forced)"
                : gpu
                    ? $"GPU + SIMD ({gpuName}{(vramMb > 0 ? $" · {vramMb} MB VRAM" : "")})"
                    : mode == HardwareAccelerationMode.GpuAccelerated
                        ? "GPU requested (D3D11 adapter unavailable · SIMD fallback)"
                        : "Auto (CPU SIMD)";

            var capturePart = mode == HardwareAccelerationMode.CpuOnly || !gpu || string.IsNullOrWhiteSpace(captureBackend)
                ? "GDI BitBlt"
                : captureBackend;

            return $"{gpuPart} · Capture: {capturePart} · {SimdTierName} · {LogicalProcessorCount} cores (up to {MaxVisionWorkers} worker(s)) · {ramMb} MB RAM cache.";
        }
    }
}

/// <summary>
/// Takes pictures of the screen. The real version (WPF project) uses Windows GDI; tests use a fake.
/// All coordinates are physical screen pixels in virtual-screen coordinates (the same ones mouse steps use).
/// </summary>
public interface IScreenCapture
{
    /// <summary>The rectangle covering all monitors.</summary>
    ScreenRect GetVirtualScreen();

    /// <summary>
    /// Captures the part of <paramref name="region"/> that is on screen. The returned frame's Bounds say exactly
    /// which pixels were captured (it can be smaller than the request near a screen edge).
    /// Throws <see cref="ArgumentException"/> if the area is completely off screen.
    /// </summary>
    Task<ScreenFrame> CaptureAsync(ScreenRect region, CancellationToken cancellationToken);

    /// <summary>
    /// Captures <paramref name="region"/> from a specific top-level window (<paramref name="windowHandle"/>) when possible
    /// (e.g. via DWM <c>PrintWindow(PW_RENDERFULLCONTENT)</c> when another window is in front of it), falling back to
    /// <see cref="CaptureAsync"/>.
    /// </summary>
    Task<ScreenFrame> CaptureWindowAsync(long windowHandle, ScreenRect region, CancellationToken cancellationToken) =>
        CaptureAsync(region, cancellationToken);

    /// <summary>
    /// True when capturing a small rectangle is much cheaper than capturing a whole search area (the real GDI capture
    /// copies only the requested pixels). The Hot-Spot fast path then captures just the spot where the picture was last
    /// found and only captures the whole area when it isn't there any more. False (the default, e.g. test fakes): every
    /// look captures the whole area once and the spot is checked inside that picture, so one look = one capture.
    /// Wrappers (decorators) should pass the inner capture's value on.
    /// </summary>
    bool PrefersSmallCaptures => false;

    /// <summary>
    /// Monotonically increasing compositor frame sequence when backed by a streaming capture source (such as WGC);
    /// returns <c>0</c> for stateless captures (GDI BitBlt, test doubles).
    /// </summary>
    long LatestFrameSequence => 0;

    /// <summary>
    /// Optional notification raised when a streaming compositor frame arrives; default implementation is a no-op so
    /// stateless capture implementations and test doubles remain unchanged.
    /// </summary>
    event Action? FrameUpdated
    {
        add { }
        remove { }
    }
}

/// <summary>Thrown when an image / colour / text is not found (or doesn't go away) in time.</summary>
public sealed class DetectionTimeoutException : Exception
{
    public DetectionTimeoutException(string message) : base(message)
    {
    }
}

/// <summary>Thrown when screen capture isn't possible (no capture service, screen locked…).</summary>
public sealed class ScreenCaptureUnavailableException : Exception
{
    public ScreenCaptureUnavailableException(string message) : base(message)
    {
    }
}

/// <summary>
/// A place where an image was found. Bounds are in whatever coordinates the searched picture used
/// (picture pixels inside the matcher, screen pixels once converted). Score is 0..1 (1 = identical).
/// <see cref="MatchedScale"/> is the template size multiplier that matched (1.0 unless multi-scale matched a resized copy).
/// </summary>
public sealed record VisualMatch(ScreenRect Bounds, double Score)
{
    public double MatchedScale { get; init; } = 1.0;

    /// <summary>
    /// Fraction of the template's cells that were visible / unoccluded (0..1; 1.0 when not occluded).
    /// </summary>
    public double VisibleFraction { get; init; } = 1.0;
}

/// <summary>
/// Adds up how long screen captures and searches take, so the log can say what a detection step costs
/// (instead of guessing). "Busy" = time spent capturing + searching ÷ total time of the step.
/// </summary>
public sealed class DetectionTimer
{
    public int Checks { get; private set; }
    public TimeSpan CaptureTime { get; private set; }
    public TimeSpan SearchTime { get; private set; }

    public void Add(TimeSpan capture, TimeSpan search)
    {
        Checks++;
        if (capture > TimeSpan.Zero) CaptureTime += capture;
        if (search > TimeSpan.Zero) SearchTime += search;
    }

    public double AverageCaptureMs => Checks == 0 ? 0 : CaptureTime.TotalMilliseconds / Checks;
    public double AverageSearchMs => Checks == 0 ? 0 : SearchTime.TotalMilliseconds / Checks;

    /// <summary>Share of the total time spent working (0..1). Roughly the share of one CPU core used.</summary>
    public double BusyShare(TimeSpan total) =>
        total <= TimeSpan.Zero ? 0 : Math.Clamp((CaptureTime + SearchTime).TotalMilliseconds / total.TotalMilliseconds, 0, 1);

    public string Describe(TimeSpan total) =>
        $"Checked {Checks} time{(Checks == 1 ? "" : "s")} in {total.TotalSeconds:0.0} s; capture {AverageCaptureMs:0} ms, search {AverageSearchMs:0} ms on average (busy {BusyShare(total):P0} of the time).";
}
