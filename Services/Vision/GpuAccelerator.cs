using System.Runtime.InteropServices;
using SmartMacro.Core.Vision;

namespace SmartMacro.Services.Vision;

/// <summary>
/// Probes the system's Direct3D 11 / DXGI graphics adapter without external packages or heavy GPU frame allocations,
/// reporting GPU name + dedicated VRAM to <see cref="HardwareAccelerationInfo"/> and verifying D3D11 compute readiness.
/// </summary>
public static class GpuAccelerator
{
    private const uint DxgiErrorNotFound = 0x887A0002;
    private const uint DxgiAdapterFlagSoftware = 2;
    private const uint D3dDriverTypeHardware = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, nint data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, nint unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, nint data);
        [PreserveSig] int GetParent(ref Guid riid, out nint parent);
        [PreserveSig] int EnumAdapters(uint adapter, out nint ppAdapter);
        [PreserveSig] int MakeWindowAssociation(nint windowHandle, uint flags);
        [PreserveSig] int GetWindowAssociation(out nint windowHandle);
        [PreserveSig] int CreateSwapChain(nint device, nint desc, out nint swapChain);
        [PreserveSig] int CreateSoftwareAdapter(nint module, out nint adapter);
        [PreserveSig] int EnumAdapters1(uint adapter, out IDXGIAdapter1? ppAdapter);
        [PreserveSig] bool IsCurrent();
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, nint data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, nint unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, nint data);
        [PreserveSig] int GetParent(ref Guid riid, out nint parent);
        [PreserveSig] int EnumOutputs(uint output, out nint ppOutput);
        [PreserveSig] int GetDesc(out nint desc);
        [PreserveSig] int CheckInterfaceSupport(ref Guid interfaceName, out long umdVersion);
        [PreserveSig] int GetDesc1(out DxgiAdapterDesc1 desc);
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IDXGIFactory1? factory);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        nint adapter,
        uint driverType,
        nint software,
        uint flags,
        [In] uint[]? featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out nint device,
        out uint featureLevel,
        out nint immediateContext);

    public sealed record AdapterProbeResult(bool Available, string AdapterName, int DedicatedVramMb);

    private static readonly Lazy<AdapterProbeResult> CachedProbe = new(ProbeBestAdapter);

    /// <summary>Probes the best hardware DXGI/D3D11 adapter once per process and publishes it to Core.</summary>
    public static AdapterProbeResult Initialize()
    {
        var probe = CachedProbe.Value;
        HardwareAccelerationInfo.RegisterGpuAdapter(probe.Available, probe.AdapterName, probe.DedicatedVramMb);
        return probe;
    }

    private static AdapterProbeResult ProbeBestAdapter()
    {
        try
        {
            var factoryGuid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
            if (CreateDXGIFactory1(ref factoryGuid, out var factory) < 0 || factory is null)
                return new AdapterProbeResult(false, "None (software GDI)", 0);

            try
            {
                string bestName = "";
                ulong bestVram = 0;
                uint index = 0;

                while (true)
                {
                    var hr = factory.EnumAdapters1(index++, out var adapter);
                    if (unchecked((uint)hr) == DxgiErrorNotFound || hr < 0 || adapter is null)
                        break;

                    try
                    {
                        if (adapter.GetDesc1(out var desc) >= 0 && (desc.Flags & DxgiAdapterFlagSoftware) == 0)
                        {
                            var vram = (ulong)desc.DedicatedVideoMemory;
                            if (vram >= bestVram)
                            {
                                bestVram = vram;
                                bestName = desc.Description?.Trim() ?? "Direct3D 11 GPU";
                            }
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(adapter);
                    }
                }

                if (string.IsNullOrWhiteSpace(bestName))
                    return ProbeD3D11Fallback();

                var vramMb = (int)Math.Clamp(bestVram / (1024UL * 1024UL), 0UL, int.MaxValue);
                return new AdapterProbeResult(true, bestName, vramMb);
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        catch
        {
            return ProbeD3D11Fallback();
        }
    }

    private static AdapterProbeResult ProbeD3D11Fallback()
    {
        try
        {
            const uint sdkVersion = 7;
            var levels = new uint[] { 0xb000, 0xa100, 0xa000 };
            var hr = D3D11CreateDevice(nint.Zero, D3dDriverTypeHardware, nint.Zero, 0, levels, (uint)levels.Length, sdkVersion, out var dev, out _, out var ctx);
            if (ctx != nint.Zero) Marshal.Release(ctx);
            if (dev != nint.Zero) Marshal.Release(dev);
            return hr >= 0
                ? new AdapterProbeResult(true, "Direct3D 11 Hardware GPU", 0)
                : new AdapterProbeResult(false, "CPU fallback (no D3D11 hardware device)", 0);
        }
        catch
        {
            return new AdapterProbeResult(false, "CPU fallback", 0);
        }
    }
}
