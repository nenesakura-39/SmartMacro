using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;

namespace SmartMacro.Core.Vision;

/// <summary>Settings for <see cref="TemplateMatcher.Find"/>.</summary>
public sealed record TemplateMatchOptions
{
    /// <summary>How similar a spot must be to count as a match (0..1). 0.9 is a good start.</summary>
    public double MinScore { get; init; } = 0.9;

    /// <summary>
    /// The score only compares light and dark patterns (brightness-independent). Unless this is set, a match must
    /// also have roughly the same average colour as the image, so e.g. a grey "disabled" button doesn't match a
    /// green one with the same shape.
    /// </summary>
    public bool IgnoreColor { get; init; }

    /// <summary>Largest allowed difference of the average red / green / blue (0..255) when colours are compared.</summary>
    public int MaxMeanColorDifference { get; init; } = 48;

    /// <summary>At most this many matches are returned (best first).</summary>
    public int MaxResults { get; init; } = 10;

    /// <summary>
    /// Share of the template's width / height to ignore on every side (0 to 0.4). 0.15 ignores the outer 15 %:
    /// the corners of a round button (or the background showing around any game UI element) then no longer count,
    /// so a changing 3D scene behind the button doesn't push the score below the threshold. Shape and colour are
    /// compared on the inner part only; the match still reports the full template's rectangle and centre.
    /// </summary>
    public double IgnoreEdgeFraction { get; init; }

    /// <summary>
    /// Also match the picture's "foreground" only: the bright (or dark) strokes that stand out from its background,
    /// like the white text and ring of a semi-transparent game button. The background (whatever shows through,
    /// e.g. a wooden pier one time and water the next) doesn't count. Each spot gets the better of the normal score
    /// and the foreground score. The colour check then only compares the foreground's colour.
    /// </summary>
    public bool MatchForeground { get; init; }

    /// <summary>
    /// True (default) to use Zero-Mean Normalized Cross-Correlation (ZNCC) and illumination-normalized colour
    /// comparison so daytime captures still match darkened, brightened or night-tinted screens without turning off
    /// colour verification.
    /// </summary>
    public bool AdaptToLighting { get; init; } = true;

    /// <summary>
    /// True (default): when the picture is a target on a plain background (a red circle inside a white square, or a
    /// picture with see-through pixels) and the normal search finds nothing good enough, search again comparing only the
    /// target's own pixels (<see cref="TemplateBackground"/>, <see cref="BackgroundMaskMatcher"/>), so a wall or screen
    /// corner showing up in the picture's background corners / margins doesn't sink the score. Never used with
    /// <see cref="IgnoreColor"/> (that comparison is colour based).
    /// </summary>
    public bool AutoMaskBackground { get; init; } = true;

    /// <summary>Default smallest scale factor checked when <see cref="AllowMultiScale"/> is on (0.75 = 75 %).</summary>
    public const double DefaultMinScale = 0.75;

    /// <summary>Default largest scale factor checked when <see cref="AllowMultiScale"/> is on (1.50 = 150 %).</summary>
    public const double DefaultMaxScale = 1.50;

    /// <summary>
    /// When true, also checks resized copies of the template across <see cref="MinScale"/>..<see cref="MaxScale"/>
    /// (covering 100 %, 125 %, 150 % Windows DPI scaling and 75 %–150 % UI/resolution changes) when 1:1 doesn't
    /// already give a strong match. Off by default so 1:1 searches pay zero extra cost.
    /// </summary>
    public bool AllowMultiScale { get; init; }

    /// <summary>Smallest template scale multiplier tried when <see cref="AllowMultiScale"/> is true.</summary>
    public double MinScale { get; init; } = DefaultMinScale;

    /// <summary>Largest template scale multiplier tried when <see cref="AllowMultiScale"/> is true.</summary>
    public double MaxScale { get; init; } = DefaultMaxScale;

    /// <summary>Vision 2.0: detector algorithm (Template = default, EdgeShape, or Combined).</summary>
    public ImageDetectorKind Detector { get; init; } = ImageDetectorKind.Template;

    /// <summary>
    /// Vision 2.0 Partial Visibility ("Occlusion tolerance"): minimum fraction of the template's cells that must match
    /// (0 = compare the whole template as one unit; 0.40..0.95 = match even when particles cover up to <c>1 - MinVisibleFraction</c>).
    /// </summary>
    public double MinVisibleFraction { get; init; }

    /// <summary>Vision 2.0: optional rotation range in degrees (0 = upright only; 1..45 = checks ±RotationRangeDeg).</summary>
    public int RotationRangeDeg { get; init; }

    /// <summary>Vision 2.0: optional additional template appearances (animation frames, glow states, learned samples).</summary>
    public IReadOnlyList<BgraImage>? AdditionalTemplates { get; init; }
}

/// <summary>
/// What a search found. Matches are best first and never overlap much. BestScore is the best similarity seen
/// anywhere (even below the threshold), which helps choosing a threshold. RejectedForColor counts spots that
/// had the right shape but the wrong colour. Scale is how much the search was shrunk for speed (1 = not).
/// </summary>
public sealed record TemplateMatchResult(IReadOnlyList<VisualMatch> Matches, double BestScore, int RejectedForColor, int Scale)
{
    public static TemplateMatchResult None { get; } = new(Array.Empty<VisualMatch>(), 0, 0, 1);

    public VisualMatch? Best => Matches.Count > 0 ? Matches[0] : null;

    /// <summary>
    /// Near-miss diagnostics: where (in the searched picture's pixels, full template size) the <see cref="BestScore"/>
    /// spot was, also when it was below the threshold. Empty when nothing scored above 0 (or the search path doesn't
    /// know it).
    /// </summary>
    public ScreenRect BestBounds { get; init; }

    /// <summary>The template scale factor (1.0 = original size) that produced <see cref="Best"/> or <see cref="BestScore"/>.</summary>
    public double BestScale { get; init; } = 1.0;
}

/// <summary>
/// Public facade for finding a small picture ("template") inside a larger picture using coarse-to-fine ZNCC pyramid
/// matching (<see cref="TemplatePyramidMatcher"/>), optional scale sweeps (<see cref="TemplateScaleSearch"/>),
/// and optional rotation sweeps (<see cref="TemplateRotationSearch"/>).
/// </summary>
public static class TemplateMatcher
{
    /// <summary>Grey-level spread below which a template is "one flat colour" (NCC can't match those; use Find colour).</summary>
    public const double FlatTemplateStdDev = 2.0;

    /// <summary>
    /// At least this many of the best coarse spots are always checked again at a finer size, whatever their coarse
    /// score. Thin line art (a Tic-Tac-Toe grid) scores low on the shrunken copy even at the right spot; checking
    /// many spots makes sure the right one is never thrown away.
    /// </summary>
    public const int MinRefineCandidates = 25;

    /// <summary>The coarse pass never shrinks more than this (1/8 and smaller wiped out thin lines).</summary>
    public const int MaxCoarseScale = 4;

    /// <summary>Smallest inner part (pixels per side) an edge-ignoring search keeps, so the shape still means something.</summary>
    public const int MinInnerSize = 6;

    /// <summary>
    /// Templates smaller than this (on their short side) are never shrunk to 1/4 unless 1/2 would be far too slow:
    /// shrinking a small picture that much blurs thin text and 1-pixel lines away.
    /// </summary>
    public const int SmallTemplateSize = 120;

    /// <summary>
    /// Templates wider or taller than this use <see cref="TemplatePyramidMatcher.FindStandardLarge"/> (FFT coarse pass,
    /// 1/4 → 1/2 → full size refinement) so even full-screen-sized captures stay fast without skipping pixels.
    /// </summary>
    public const int LargeTemplateThreshold = 300;

    public static TemplateMatchResult Find(BgraImage image, BgraImage template, TemplateMatchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(template);
        options ??= new TemplateMatchOptions();

        var primaryResult = TemplateRotationSearch.FindWithRotation(image, template, options, cancellationToken);
        if (options.AdditionalTemplates is not { Count: > 0 } extras)
            return primaryResult;

        if (primaryResult.Matches.Count > 0 && primaryResult.BestScore >= Math.Max(options.MinScore, 0.96))
            return primaryResult;

        var singleTemplateOptions = options with { AdditionalTemplates = null };
        var mergedMatches = new List<VisualMatch>(primaryResult.Matches);
        var bestScore = primaryResult.BestScore;
        var bestBounds = primaryResult.BestBounds;
        var bestScale = primaryResult.BestScale;
        var rejectedColor = primaryResult.RejectedForColor;
        var scale = primaryResult.Scale;

        foreach (var extra in extras)
        {
            if (extra is null) continue;
            cancellationToken.ThrowIfCancellationRequested();
            var res = TemplateRotationSearch.FindWithRotation(image, extra, singleTemplateOptions, cancellationToken);
            mergedMatches.AddRange(res.Matches);
            if (res.BestScore > bestScore)
            {
                bestScore = res.BestScore;
                bestBounds = res.BestBounds;
                bestScale = res.BestScale;
            }

            rejectedColor += res.RejectedForColor;
            if (res.Scale > scale) scale = res.Scale;
            if (res.Matches.Count > 0 && res.BestScore >= Math.Max(options.MinScore, 0.96))
                break;
        }

        var kept = KeepBest(mergedMatches, options.MaxResults);
        return new TemplateMatchResult(kept, Math.Min(1.0, bestScore), kept.Count > 0 ? 0 : rejectedColor, scale)
        {
            BestBounds = kept.Count > 0 ? kept[0].Bounds : bestBounds,
            BestScale = kept.Count > 0 ? kept[0].MatchedScale : bestScale,
        };
    }

    public static ScreenRect? InnerArea(int tw, int th, double fraction) =>
        TemplatePyramidMatcher.InnerArea(tw, th, fraction);

    public static bool IsFlat(BgraImage template) =>
        TemplatePyramidMatcher.IsFlat(template);

    internal static double CombinedScore(double nccScore, double directScore) =>
        TemplatePyramidMatcher.CombinedScore(nccScore, directScore);

    internal static List<VisualMatch> KeepBest(IEnumerable<VisualMatch> found, int maxResults) =>
        TemplatePyramidMatcher.KeepBest(found, maxResults);

    internal static float Gray(byte[] p, int i) =>
        TemplatePyramidMatcher.Gray(p, i);

    internal static float[] GrayDownscaled(BgraImage image, int scale, out int width, out int height) =>
        TemplatePyramidMatcher.GrayDownscaled(image, scale, out width, out height);

    internal static float[] GrayDownscaledRegion(BgraImage image, ScreenRect region, int scale, out int width, out int height) =>
        TemplatePyramidMatcher.GrayDownscaledRegion(image, region, scale, out width, out height);

    internal static float[] Halve(float[] values, int width, int height, out int halfWidth, out int halfHeight) =>
        TemplatePyramidMatcher.Halve(values, width, height, out halfWidth, out halfHeight);

    internal static float[] PairAverage(float[] values, int width, int height, out int resultWidth, out int resultHeight) =>
        TemplatePyramidMatcher.PairAverage(values, width, height, out resultWidth, out resultHeight);

    internal static (double[] Sum, double[] SumSq) Integrals(float[] values, int width, int height) =>
        TemplatePyramidMatcher.Integrals(values, width, height);

    internal static double Box(double[] table, int iw, int x, int y, int w, int h) =>
        TemplatePyramidMatcher.Box(table, iw, x, y, w, h);

    internal static List<(int X, int Y)> PickPeaks(float[] scores, int px, int py, int tw, int th, int count) =>
        TemplatePyramidMatcher.PickPeaks(scores, px, py, tw, th, count);

    internal static bool IsPeak(float[] scores, int px, int py, int x, int y, float v) =>
        TemplatePyramidMatcher.IsPeak(scores, px, py, x, y, v);

    internal static int ChooseScale(int width, int height, int tw, int th) =>
        TemplatePyramidMatcher.ChooseScale(width, height, tw, th);

    internal static double Overlap(ScreenRect a, ScreenRect b) =>
        TemplatePyramidMatcher.Overlap(a, b);

    internal static bool IsSameColor(double candR, double candG, double candB, double tplR, double tplG, double tplB,
        int maxDifference, bool adaptToLighting) =>
        TemplatePyramidMatcher.IsSameColor(candR, candG, candB, tplR, tplG, tplB, maxDifference, adaptToLighting);
}
