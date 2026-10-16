using SmartMacro.Core.Ocr;

namespace SmartMacro.Core.Vision;

/// <summary>
/// Where a coloured target zone is along a bar, in % (0 = left / top edge, 100 = right / bottom edge), measured at
/// the zone's first and last pixel. Bounds are in picture pixels. Found = false means no patch of the zone colour
/// (big enough) was in the area.
/// </summary>
public sealed record ZoneReading(bool Found, double StartPercent, double EndPercent, ScreenRect? Bounds)
{
    public static ZoneReading Missing { get; } = new(false, 0, 0, null);

    /// <summary>A zone that is not detected but set by the user (always "found").</summary>
    public static ZoneReading Fixed(double startPercent, double endPercent) => new(true, startPercent, endPercent, null);
}

/// <summary>
/// Finds a coloured zone (the "safe area", "green part", "catch bar"…) inside a bar-shaped area.
/// The biggest patch of the zone colour is the zone. Smaller patches next to it (along the bar) are joined to it when
/// the gap between them is small: that happens when the marker sits on top of the zone and cuts it in two.
///
/// Simple colour matching, not object recognition: anything else of that colour in the area counts too, so keep
/// the area tight around the bar.
/// </summary>
public static class ZoneFinder
{
    /// <param name="bridgeGapPixels">
    /// Patches separated (along the bar) by at most this many pixels are treated as one zone. Use the marker's width
    /// plus a little, so a marker covering the middle of the zone doesn't split it.
    /// </param>
    public static ZoneReading Find(BgraImage frame, RgbColor color, int tolerance, int minPixels, BarOrientation orientation,
        int bridgeGapPixels = 2, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var result = ColorFinder.Find(frame, color, tolerance, minPixels, maxResults: 20, cancellationToken);
        if (result.Largest is not { } main) return ZoneReading.Missing;

        var horizontal = orientation == BarOrientation.Horizontal;
        int Low(ScreenRect r) => horizontal ? r.X : r.Y;
        int High(ScreenRect r) => horizontal ? r.Right - 1 : r.Bottom - 1;

        var gap = Math.Max(0, bridgeGapPixels);
        var low = Low(main.Bounds);
        var high = High(main.Bounds);
        var bounds = main.Bounds;
        var used = new bool[result.Blobs.Count];
        used[0] = true;

        // Keep joining neighbours until nothing else is close enough (each join can bring a new neighbour in range).
        var grew = true;
        while (grew)
        {
            grew = false;
            for (var i = 1; i < result.Blobs.Count; i++)
            {
                if (used[i]) continue;
                var b = result.Blobs[i].Bounds;
                if (Low(b) > high + gap + 1 || High(b) < low - gap - 1) continue;

                used[i] = true;
                low = Math.Min(low, Low(b));
                high = Math.Max(high, High(b));
                bounds = bounds.Union(b);
                grew = true;
            }
        }

        var length = horizontal ? frame.Width : frame.Height;
        return new ZoneReading(true, ToPercent(low, length), ToPercent(high, length), bounds);
    }

    /// <summary>Same scale as <see cref="MarkerTracker"/>: pixel 0 = 0 %, the last pixel = 100 %.</summary>
    internal static double ToPercent(double pixel, int length) =>
        length <= 1 ? 0 : Math.Clamp(pixel / (length - 1) * 100.0, 0, 100);
}
