using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using SmartMacro.Core.Ocr;

namespace SmartMacro.Core.Vision;

/// <summary>
/// Reports the active CPU SIMD vision tier (AVX2 256-bit, SSE2/Vector128 128-bit, or Scalar) and adaptive worker thread budget
/// so SmartMacro scales cleanly from 2-core budget laptops and ARM64 devices up to 6-core Ryzen 5 and 16-core Ryzen 9 / Core i9 desktops.
/// </summary>
public static class HardwareAccelerationInfo
{
    public static bool IsAvx2Supported => Avx2.IsSupported;

    public static bool IsSse2Supported => Sse2.IsSupported;

    public static bool IsVector128Accelerated => Vector128.IsHardwareAccelerated;

    public static int LogicalProcessorCount => Math.Max(1, Environment.ProcessorCount);

    /// <summary>
    /// Reserves at least 1 logical processor for the foreground game / OS and caps vision worker threads at 8
    /// so neither 2-core budget PCs nor 16-core high-end desktops suffer thread-pool contention.
    /// </summary>
    public static int MaxVisionWorkers => Math.Clamp(LogicalProcessorCount - 1, 1, 8);

    public static string SimdTierName =>
        Avx2.IsSupported ? "AVX2 256-bit SIMD (8 px/op + SSE2 4 px tail)"
        : Sse2.IsSupported ? "SSE2 128-bit SIMD (4 px/op)"
        : Vector128.IsHardwareAccelerated ? "Vector128 128-bit SIMD"
        : "Scalar fallback";

    public static string StatusText =>
        $"Vision hardware acceleration: {SimdTierName} · {LogicalProcessorCount} logical cores (up to {MaxVisionWorkers} vision worker(s)).";
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
