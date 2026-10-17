using SmartMacro.Core.Ocr;

namespace SmartMacro.Core.Vision;

public enum BarOrientation
{
    /// <summary>The marker moves left / right. 0 % = left edge of the area, 100 % = right edge.</summary>
    Horizontal,

    /// <summary>The marker moves up / down. 0 % = top edge of the area, 100 % = bottom edge.</summary>
    Vertical,
}

public enum MarkerStatus
{
    /// <summary>One clear marker was found.</summary>
    Found,

    /// <summary>Two or more similar patches: the position is not reliable this frame (the last good one is kept).</summary>
    Ambiguous,

    /// <summary>Nothing of the marker colour (big enough) is in the area.</summary>
    Lost,

    /// <summary>The marker seemed to jump too far in one frame; ignored once in case it was a glitch.</summary>
    IgnoredJump,
}

/// <summary>
/// One reading of the tracker. PositionPercent is along the bar (see <see cref="BarOrientation"/>) and is the
/// last trusted value when the status isn't Found. Confidence (0..1) = share of the marker-coloured pixels that
/// belong to the chosen patch (1 = nothing else of that colour in the area). Bounds are in picture pixels.
/// </summary>
public sealed record MarkerReading(MarkerStatus Status, double? PositionPercent, double Confidence, int Candidates, ScreenRect? Bounds);

/// <summary>
/// Follows a coloured marker (indicator, slider knob, fish icon…) inside a bar-shaped area across successive
/// screen captures and reports its position as 0–100 % along the bar. Building block for Stage 4 feedback
/// loops; here it's used by the editor's tracking test.
///
/// Honest limits: it only knows colours. Similar colours, glow effects or overlapping UI make it ambiguous
/// (reported, never hidden). A one-frame jump bigger than <see cref="MaxJumpPercent"/> is ignored once; if the
/// marker is still there next frame the new position is accepted (it really moved).
/// </summary>
public sealed class MarkerTracker
{
    private double? _last;
    private double? _pendingJump;

    public MarkerTracker(RgbColor color, int tolerance, BarOrientation orientation, int minPixels = 3, double maxJumpPercent = 35,
        RgbColor? secondaryColor = null)
    {
        Color = color;
        SecondaryColor = secondaryColor;
        Tolerance = Math.Clamp(tolerance, 0, 255);
        Orientation = orientation;
        MinPixels = Math.Max(1, minPixels);
        MaxJumpPercent = Math.Clamp(maxJumpPercent, 1, 100);
    }

    public RgbColor Color { get; }
    public RgbColor? SecondaryColor { get; init; }
    public int Tolerance { get; }
    public BarOrientation Orientation { get; }
    public int MinPixels { get; }
    public double MaxJumpPercent { get; }

    /// <summary>Second patch at least this big (relative to the biggest) and far enough away = ambiguous.</summary>
    public double AmbiguityRatio { get; init; } = 0.5;

    public void Reset()
    {
        _last = null;
        _pendingJump = null;
    }

    public MarkerReading Update(BgraImage frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var result = ColorFinder.Find(frame, Color, SecondaryColor, Tolerance, MinPixels, maxResults: 5, cancellationToken);
        if (result.Largest is not { } main)
        {
            _pendingJump = null;
            return new MarkerReading(MarkerStatus.Lost, _last, 0, 0, null);
        }

        var length = Orientation == BarOrientation.Horizontal ? frame.Width : frame.Height;
        var position = ToPercent(Orientation == BarOrientation.Horizontal ? main.CenterX : main.CenterY, length);
        var confidence = result.MatchingPixels == 0 ? 0 : main.PixelCount / (double)result.MatchingPixels;

        // Another patch nearly as big somewhere else along the bar: don't guess.
        if (result.Blobs.Count > 1)
        {
            var second = result.Blobs[1];
            var secondPosition = ToPercent(Orientation == BarOrientation.Horizontal ? second.CenterX : second.CenterY, length);
            if (second.PixelCount >= main.PixelCount * AmbiguityRatio && Math.Abs(secondPosition - position) > 5)
                return new MarkerReading(MarkerStatus.Ambiguous, _last, confidence, result.Blobs.Count, main.Bounds);
        }

        if (_last is { } last && Math.Abs(position - last) > MaxJumpPercent)
        {
            // Accept a big jump only if the next frame confirms it.
            if (_pendingJump is not { } pending || Math.Abs(position - pending) > MaxJumpPercent / 2)
            {
                _pendingJump = position;
                return new MarkerReading(MarkerStatus.IgnoredJump, _last, confidence, result.Blobs.Count, main.Bounds);
            }
        }

        _pendingJump = null;
        _last = position;
        return new MarkerReading(MarkerStatus.Found, position, confidence, result.Blobs.Count, main.Bounds);
    }

    private static double ToPercent(double center, int length) =>
        length <= 1 ? 0 : Math.Clamp(center / (length - 1) * 100.0, 0, 100);
}
