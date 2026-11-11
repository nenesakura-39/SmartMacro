using System.Runtime.InteropServices;

namespace SmartMacro.Services.Audio;

/// <summary>
/// Zero-dependency Windows CoreAudio (WASAPI) master peak meter reader for
/// <see cref="SmartMacro.Core.Models.VariableSourceKind.SystemAudioPeakPercent"/>.
/// Returns a value in <c>0.0..100.0</c> without recording or capturing audio streams.
/// </summary>
public static class WasapiAudioPeakReader
{
    private static readonly Guid ClsidMmDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidAudioMeterInformation = new("C02216F6-8C67-4B5B-9D00-D008E73E0064");

    public static double ReadMasterPeakPercent()
    {
        object? enumeratorObj = null;
        IMMDevice? device = null;
        object? meterObj = null;
        try
        {
            var type = Type.GetTypeFromCLSID(ClsidMmDeviceEnumerator, throwOnError: false);
            if (type is null) return 0.0;

            enumeratorObj = Activator.CreateInstance(type);
            if (enumeratorObj is not IMMDeviceEnumerator enumerator) return 0.0;

            // eRender = 0, eMultimedia = 1
            var hr = enumerator.GetDefaultAudioEndpoint(0, 1, out device);
            if (hr != 0 || device is null) return 0.0;

            var iid = IidAudioMeterInformation;
            hr = device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out meterObj);
            if (hr != 0 || meterObj is not IAudioMeterInformation meter) return 0.0;

            hr = meter.GetPeakValue(out var peak);
            if (hr != 0 || float.IsNaN(peak) || float.IsInfinity(peak)) return 0.0;

            return Math.Round(Math.Clamp(peak * 100.0, 0.0, 100.0), 1);
        }
        catch
        {
            return 0.0;
        }
        finally
        {
            if (meterObj is not null && Marshal.IsComObject(meterObj)) Marshal.ReleaseComObject(meterObj);
            if (device is not null && Marshal.IsComObject(device)) Marshal.ReleaseComObject(device);
            if (enumeratorObj is not null && Marshal.IsComObject(enumeratorObj)) Marshal.ReleaseComObject(enumeratorObj);
        }
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? endpoint);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? ppInterface);
    }

    [ComImport]
    [Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig]
        int GetPeakValue(out float pfPeak);
    }
}
