using SmartMacro.Core.Ocr;

namespace SmartMacro.Core.Vision;

/// <summary>
/// A captured screen frame paired with its monotonic capture timestamp (ms) and sequence number in the <see cref="FrameBus"/>.
/// </summary>
public sealed record TimestampedFrame(ScreenFrame Frame, double TimestampMs, long SequenceNumber);

/// <summary>
/// Summary of pixel motion between two consecutive frames of the same screen region.
/// </summary>
public readonly record struct MotionAnalysis(
    bool HasMotion,
    ScreenRect MotionBounds,
    double ChangedFraction,
    IReadOnlyList<ScreenRect> ActiveCells)
{
    public static MotionAnalysis None { get; } = new(false, default, 0, Array.Empty<ScreenRect>());
}

/// <summary>
/// Vision 2.0 Frame Bus: maintains a bounded ring buffer of recent screen captures with high-resolution timestamps,
/// deduplicates captures taken within the same frame window, and computes fast cell-based motion bounds between consecutive
/// frames so moving-target searches can focus on the part of the screen that actually moved.
/// Thread-safe.
/// </summary>
public sealed class FrameBus
{
    public const int DefaultCapacity = 8;
    public const int MotionCellSize = 16;

    private readonly object _gate = new();
    private readonly TimestampedFrame[] _ring;
    private int _head;
    private int _count;
    private long _nextSequence = 1;

    public FrameBus(int capacity = DefaultCapacity)
    {
        if (capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity), "FrameBus capacity must be at least 2.");
        _ring = new TimestampedFrame[capacity];
    }

    public int Capacity => _ring.Length;

    public int Count
    {
        get { lock (_gate) return _count; }
    }

    /// <summary>Pushes a newly captured frame into the ring buffer and returns its <see cref="TimestampedFrame"/> wrapper.</summary>
    public TimestampedFrame Publish(ScreenFrame frame, double timestampMs)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            var entry = new TimestampedFrame(frame, timestampMs, _nextSequence++);
            _ring[_head] = entry;
            _head = (_head + 1) % _ring.Length;
            if (_count < _ring.Length) _count++;
            return entry;
        }
    }

    /// <summary>Returns the most recent frame in the buffer, or null if none has been published.</summary>
    public TimestampedFrame? Latest()
    {
        lock (_gate)
        {
            if (_count == 0) return null;
            var idx = (_head - 1 + _ring.Length) % _ring.Length;
            return _ring[idx];
        }
    }

    /// <summary>
    /// Returns a cached frame covering <paramref name="region"/> if one was captured within <paramref name="maxAgeMs"/>,
    /// cropped to <paramref name="region"/>; otherwise null.
    /// </summary>
    public TimestampedFrame? TryReuse(ScreenRect region, double nowMs, double maxAgeMs)
    {
        if (maxAgeMs <= 0 || region.IsEmpty) return null;
        lock (_gate)
        {
            for (var i = 0; i < _count; i++)
            {
                var idx = (_head - 1 - i + _ring.Length) % _ring.Length;
                var candidate = _ring[idx];
                if (nowMs - candidate.TimestampMs > maxAgeMs) break;

                var b = candidate.Frame.Bounds;
                if (b.X <= region.X && b.Y <= region.Y && b.Right >= region.Right && b.Bottom >= region.Bottom)
                {
                    if (b == region) return candidate;
                    var cropped = candidate.Frame.CropFromScreen(region);
                    return cropped is null ? null : candidate with { Frame = cropped };
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Compares the latest two frames in the buffer that cover <paramref name="region"/> and returns the bounding rectangle
    /// of cells where pixels changed by more than <paramref name="pixelTolerance"/>.
    /// </summary>
    public MotionAnalysis AnalyzeRecentMotion(ScreenRect region, int pixelTolerance = 22)
    {
        ScreenFrame? prev = null;
        ScreenFrame? curr = null;
        lock (_gate)
        {
            for (var i = 0; i < _count; i++)
            {
                var idx = (_head - 1 - i + _ring.Length) % _ring.Length;
                var cropped = _ring[idx].Frame.CropFromScreen(region);
                if (cropped is null || cropped.Bounds != region) continue;
                if (curr is null) curr = cropped;
                else { prev = cropped; break; }
            }
        }

        if (prev is null || curr is null) return MotionAnalysis.None;
        return DetectMotion(prev.Image, curr.Image, pixelTolerance);
    }

    /// <summary>
    /// Fast grid-based motion detector between two equal-sized images. Returns the bounding box of active cells (in local
    /// image coordinates) and the fraction of pixels that changed.
    /// </summary>
    public static MotionAnalysis DetectMotion(BgraImage previous, BgraImage current, int pixelTolerance = 22)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        if (previous.Width != current.Width || previous.Height != current.Height)
            return MotionAnalysis.None;

        int width = previous.Width, height = previous.Height;
        var p0 = previous.Pixels;
        var p1 = current.Pixels;
        int cell = MotionCellSize;
        int cw = (width + cell - 1) / cell;
        int ch = (height + cell - 1) / cell;
        var cellHits = new int[cw * ch];
        var changedPixels = 0;

        for (var y = 0; y < height; y++)
        {
            var row = y * width * 4;
            var cy = y / cell * cw;
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                if (Math.Abs(p0[i] - p1[i]) > pixelTolerance
                    || Math.Abs(p0[i + 1] - p1[i + 1]) > pixelTolerance
                    || Math.Abs(p0[i + 2] - p1[i + 2]) > pixelTolerance)
                {
                    changedPixels++;
                    cellHits[cy + x / cell]++;
                }
            }
        }

        if (changedPixels == 0) return MotionAnalysis.None;

        var activeCells = new List<ScreenRect>();
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        // A cell counts as active when at least 3 pixels (or 10% of a tiny cell) changed, filtering out single-pixel compression noise.
        var minCellPixels = Math.Min(3, Math.Max(1, width * height / 16));

        for (var cy = 0; cy < ch; cy++)
        {
            for (var cx = 0; cx < cw; cx++)
            {
                if (cellHits[cy * cw + cx] < minCellPixels) continue;
                var rx = cx * cell;
                var ry = cy * cell;
                var rw = Math.Min(cell, width - rx);
                var rh = Math.Min(cell, height - ry);
                activeCells.Add(new ScreenRect(rx, ry, rw, rh));
                if (rx < minX) minX = rx;
                if (ry < minY) minY = ry;
                if (rx + rw > maxX) maxX = rx + rw;
                if (ry + rh > maxY) maxY = ry + rh;
            }
        }

        if (activeCells.Count == 0) return MotionAnalysis.None;

        var bounds = new ScreenRect(minX, minY, maxX - minX, maxY - minY);
        var fraction = (double)changedPixels / (width * height);
        return new MotionAnalysis(true, bounds, fraction, activeCells);
    }

    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_ring);
            _head = 0;
            _count = 0;
        }
    }
}
