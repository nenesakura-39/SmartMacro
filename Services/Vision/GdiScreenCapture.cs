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
public sealed class GdiScreenCapture : IScreenCapture, IDisposable
{
    /// <summary>
    /// All monitors (physical pixels). Shared with OCR and re-read from Windows at most every 500 ms
    /// (<see cref="ScreenCapture.VirtualScreen"/>): a fast loop asks for it on every look.
    /// </summary>
    public ScreenRect GetVirtualScreen() => ScreenCapture.VirtualScreen();

    /// <summary>
    /// GDI copies only the requested pixels, so a 100×100 px Hot-Spot re-check costs far less than a whole monitor:
    /// the playback engine captures just that spot first.
    /// </summary>
    public bool PrefersSmallCaptures => true;

    public Task<ScreenFrame> CaptureAsync(ScreenRect region, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Capture(region));
    }

    /// <summary>
    /// Captures the on-screen part of the area right away (on the calling thread). Only these pixels are copied
    /// (BitBlt of exactly this rectangle into one of the two process-wide, lock-protected GDI buffers, which are only
    /// re-created when the size changes; the pixels are copied into a new array before the lock is released, so the
    /// returned frame is never shared and no GDI handle is tied to the calling thread).
    /// </summary>
    public ScreenFrame CaptureRegion(ScreenRect region) => Capture(region);

    /// <summary>Captures the on-screen part of the area right away (on the calling thread).</summary>
    public ScreenFrame Capture(ScreenRect region)
    {
        var area = region.Intersect(GetVirtualScreen());
        if (area.IsEmpty)
            throw new ArgumentException($"The search area {region} is outside the screen. Pick the area again.");

        var pixels = ScreenCapture.CaptureBgra(area);
        return new ScreenFrame(area, new BgraImage(area.Width, area.Height, pixels));
    }

    /// <summary>Frees the shared GDI capture buffers (also done by App.OnExit). Later captures re-create them.</summary>
    public void Dispose() => ScreenCapture.ReleaseAllResources();
}
