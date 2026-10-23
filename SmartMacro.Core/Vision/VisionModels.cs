using SmartMacro.Core.Ocr;

namespace SmartMacro.Core.Vision;

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
    /// True when capturing a small rectangle is much cheaper than capturing a whole search area (the real GDI capture
    /// copies only the requested pixels). The Hot-Spot fast path then captures just the spot where the picture was last
    /// found and only captures the whole area when it isn't there any more. False (the default, e.g. test fakes): every
    /// look captures the whole area once and the spot is checked inside that picture, so one look = one capture.
    /// Wrappers (decorators) should pass the inner capture's value on.
    /// </summary>
    bool PrefersSmallCaptures => false;
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
/// </summary>
public sealed record VisualMatch(ScreenRect Bounds, double Score);

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
