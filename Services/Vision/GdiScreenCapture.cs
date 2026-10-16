using SmartMacro.Core.Ocr;
using SmartMacro.Core.Vision;
using SmartMacro.Services.Input;
using SmartMacro.Services.Ocr;

namespace SmartMacro.Services.Vision;

/// <summary>
/// Screen capture for Find image / Find color, using the same GDI BitBlt code as OCR (everything stays on this PC).
/// Coordinates are physical pixels (the app is per-monitor DPI aware), so they match mouse steps.
///
/// Known limit: some games using exclusive full-screen or certain GPU overlays show up black to GDI. Running the
/// game in windowed / borderless mode usually fixes that. SmartMacro's own windows are excluded from captures
/// while a macro runs or a test is done.
/// </summary>
public sealed class GdiScreenCapture : IScreenCapture
{
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    public ScreenRect GetVirtualScreen()
    {
        // The virtual-screen size must be in the same physical pixels as the capture and the mouse steps.
        using var dpi = NativeMethods.PhysicalPixelsScope.Enter();
        return new ScreenRect(
            NativeMethods.GetSystemMetrics(SM_XVIRTUALSCREEN),
            NativeMethods.GetSystemMetrics(SM_YVIRTUALSCREEN),
            NativeMethods.GetSystemMetrics(SM_CXVIRTUALSCREEN),
            NativeMethods.GetSystemMetrics(SM_CYVIRTUALSCREEN));
    }

    public Task<ScreenFrame> CaptureAsync(ScreenRect region, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Capture(region));
    }

    /// <summary>Captures the on-screen part of the area right away (on the calling thread).</summary>
    public ScreenFrame Capture(ScreenRect region)
    {
        var area = region.Intersect(GetVirtualScreen());
        if (area.IsEmpty)
            throw new ArgumentException($"The search area {region} is outside the screen. Pick the area again.");

        var pixels = ScreenCapture.CaptureBgra(area);
        return new ScreenFrame(area, new BgraImage(area.Width, area.Height, pixels));
    }
}
