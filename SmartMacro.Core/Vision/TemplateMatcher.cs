using System.Numerics;
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
}

/// <summary>
/// Finds a small picture (the "template", e.g. a button) inside a bigger one (a screen capture).
///
/// How: both pictures are turned into grey levels and compared with normalized cross-correlation (NCC), which
/// scores how well the light/dark pattern lines up (1 = identical, 0 = unrelated) and doesn't care about overall
/// brightness. To stay fast on a full screen, the search first runs on shrunken copies (coarse), then the best
/// spots are checked again at full size (fine). Shrinking always averages every pixel of a block (area average),
/// never skips pixels, so 1-pixel lines survive as fainter lines instead of disappearing. Pure C#, no extra libraries.
///
/// Every reported score is the better of the NCC score and a direct colour comparison
/// (FineTemplate.DirectSimilarity), so a pixel-exact copy of the template always scores 100 %.
///
/// Limits (by design, documented for users): the template must appear at the same size. Different display
/// scaling, game resolution or graphics quality, rotation, or heavy transparency/animation lower the score.
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

    // Rough number of multiply-adds the coarse pass may use. Picks the shrink factor.
    private const long CoarseBudget = 40_000_000;

    // Above this many multiply-adds the coarse pass is computed with an FFT instead of position by position.
    private const long FftThreshold = CoarseBudget * 2;

    // The shrunken template must stay at least this many pixels on its short side, or it loses its shape.
    private const int MinCoarseTemplateSize = 8;

    // Large templates: how many of the best spots (after the 1/2 check) are checked at full size.
    private const int LargeFinalCandidates = 8;
    private const int LargeFinalCandidatesMax = 12;
    private const double LargeFinalScoreMargin = 0.15;

    public static TemplateMatchResult Find(BgraImage image, BgraImage template, TemplateMatchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(template);
        options ??= new TemplateMatchOptions();

        int width = image.Width, height = image.Height, tw = template.Width, th = template.Height;

        if (options.IgnoreEdgeFraction > 0)
        {
            var inner = InnerArea(tw, th, options.IgnoreEdgeFraction);
            if (inner is { } crop && !IsFlat(template.Crop(crop)))
                return FindInner(image, template.Crop(crop), crop.X, crop.Y, tw, th, options with { IgnoreEdgeFraction = 0 }, cancellationToken);
        }

        if (tw > width || th > height) return TemplateMatchResult.None;

        var fine = new FineTemplate(template);
        if (fine.Norm < 1e-6) return TemplateMatchResult.None; // flat template

        var result = options.MatchForeground
            ? FindWithForeground(image, template, fine, options, cancellationToken)
            : FindStandard(image, template, fine, options, cancellationToken);
        return WithBackgroundMask(image, template, result, options, cancellationToken);
    }

    /// <summary>
    /// Automatic foreground masking: when nothing was found and the picture is a target on a plain background (see
    /// <see cref="TemplateBackground.CanMask"/>), search again scoring only the target's pixels. A match found that way is
    /// returned; otherwise the better near-miss of both searches is kept. A match of the normal search is never changed.
    /// </summary>
    private static TemplateMatchResult WithBackgroundMask(BgraImage image, BgraImage template, TemplateMatchResult result,
        TemplateMatchOptions options, CancellationToken cancellationToken)
    {
        if (options.IgnoreColor || !options.AutoMaskBackground) return result;
        var background = TemplateBackground.Detect(template);
        if (background is null || !background.CanMask) return result;

        // Found already: a wall in the picture's background corners can pull the whole-picture peak a few pixels off the
        // target. Re-centre each match on the target's own pixels when that scores better.
        if (result.Matches.Count > 0)
            return BackgroundMaskMatcher.Recentre(image, template, background, result, options, cancellationToken);

        var masked = BackgroundMaskMatcher.Find(image, template, background, options, cancellationToken);
        if (masked.Matches.Count > 0) return masked;
        return masked.BestScore > result.BestScore && !masked.BestBounds.IsEmpty
            ? result with { BestScore = masked.BestScore, BestBounds = masked.BestBounds }
            : result;
    }

    /// <summary>The normal (whole picture, grey NCC) search.</summary>
    private static TemplateMatchResult FindStandard(BgraImage image, BgraImage template, FineTemplate fine, TemplateMatchOptions options,
        CancellationToken cancellationToken)
    {
        if (fine.IsLarge)
            return FindStandardLarge(image, template, fine, options, cancellationToken);

        int width = image.Width, height = image.Height, tw = template.Width, th = template.Height;
        var scale = ChooseScale(width, height, tw, th);
        var candidateCount = Math.Max(MinRefineCandidates, options.MaxResults * 2);
        // A small template that still had to be shrunk to 1/4 (a big search area) loses detail: check more
        // candidate spots at full size so the real one isn't missed.
        if (scale > 2 && Math.Min(tw, th) < SmallTemplateSize) candidateCount = Math.Max(candidateCount, 36);
        var candidates = CoarseCandidates(image, template, scale, candidateCount, cancellationToken);

        var found = new List<VisualMatch>();
        var best = 0.0;
        var bestBounds = default(ScreenRect);
        var rejectedForColor = 0;
        var radius = scale == 1 ? 0 : scale * 2 - 1;
        var checkedSpots = new HashSet<(int, int)>();

        foreach (var (cx, cy) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (x, y, nccScore) = Refine(image, fine, cx * scale, cy * scale, radius);
            if (!checkedSpots.Add((x, y))) continue;
            var score = CombinedScore(nccScore, fine.DirectSimilarity(image, x, y));
            if (score <= 0) continue;

            if (!options.IgnoreColor && !fine.SameColor(image, x, y, options.MaxMeanColorDifference, options.AdaptToLighting))
            {
                // Only count spots that would have matched by shape (so the hint to the user is meaningful).
                if (score >= options.MinScore) rejectedForColor++;
                continue;
            }

            if (score > best)
            {
                best = score;
                bestBounds = new ScreenRect(x, y, tw, th);
            }

            if (score >= options.MinScore) found.Add(new VisualMatch(new ScreenRect(x, y, tw, th), score));
        }

        return new TemplateMatchResult(KeepBest(found, options.MaxResults), Math.Min(1.0, best), rejectedForColor, scale)
        {
            BestBounds = bestBounds,
        };
    }

    /// <summary>
    /// Search for large templates (wider or taller than <see cref="LargeTemplateThreshold"/>), without skipping pixels:
    /// 1. Grey pyramid by area averaging: full size, 1/2, 1/4.
    /// 2. Coarse NCC of the whole 1/4 template over the whole 1/4 picture, computed with an FFT (cost doesn't grow with
    ///    the template size). The picture side uses 8×8 blocks every 4 pixels, so a 1-pixel line of the template
    ///    always falls inside the block it is compared with, whatever the template's position modulo 4 is.
    /// 3. The best <see cref="MinRefineCandidates"/> coarse spots (no score threshold) are checked at 1/2 size, the
    ///    best of those at full size, where an exact copy scores 100 %.
    /// </summary>
    private static TemplateMatchResult FindStandardLarge(BgraImage image, BgraImage template, FineTemplate fine,
        TemplateMatchOptions options, CancellationToken cancellationToken)
    {
        int tw = template.Width, th = template.Height;
        var minSide = Math.Min(tw, th);
        var scale = minSide / 4 >= MinCoarseTemplateSize ? 4 : minSide / 2 >= MinCoarseTemplateSize ? 2 : 1;

        // Pyramid levels: [0] = full size, [1] = 1/2, [2] = 1/4 (as far as the scale goes).
        var fullGray = GrayDownscaled(image, 1, out var w1, out var h1);
        var templateGray = GrayDownscaled(template, 1, out _, out _);
        var imageLevels = new List<GrayMap> { new GrayMap(fullGray, w1, h1) };
        var templateLevels = new List<GrayMap> { new GrayMap(templateGray, tw, th) };
        for (var s = 2; s <= scale; s *= 2)
        {
            imageLevels.Add(imageLevels[^1].Halve());
            templateLevels.Add(templateLevels[^1].Halve());
        }

        // A template that is flat at a coarse size (almost never) is searched one size finer.
        var top = templateLevels.Count - 1;
        var coarseTemplate = ZeroMeanTemplate.From(templateLevels[top]);
        while (top > 0 && coarseTemplate.Norm < 1e-6)
        {
            top--;
            coarseTemplate = ZeroMeanTemplate.From(templateLevels[top]);
        }

        if (coarseTemplate.Norm < 1e-6) return new TemplateMatchResult(Array.Empty<VisualMatch>(), 0, 0, 1);
        scale = 1 << top;

        cancellationToken.ThrowIfCancellationRequested();
        var coarseMap = top == 0 ? imageLevels[0] : imageLevels[top].PhaseTolerant(coarseTemplate.Width, coarseTemplate.Height);
        var scores = NccScores(coarseMap, coarseTemplate, cancellationToken, out var px, out var py);
        var coarse = PickPeaks(scores, px, py, coarseTemplate.Width, coarseTemplate.Height, Math.Max(MinRefineCandidates, options.MaxResults * 2));

        // Refine level by level: each level doubles the coordinates and checks a small window around them.
        var current = coarse.Select(c => (X: c.X, Y: c.Y, Score: 0.0)).ToList();
        if (top == 0)
        {
            // The coarse pass already ran at full size: just take the exact score at (and uphill from) each peak.
            current = coarse
                .Select(c => ClimbOnMap(imageLevels[0], coarseTemplate, RefineOnMap(imageLevels[0], coarseTemplate, c.X, c.Y, c.X, c.Y)))
                .ToList();
        }

        for (var level = top - 1; level >= 0; level--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isFinal = level == 0;
            var tpl = isFinal ? new ZeroMeanTemplate(fine.ZeroMeanGray, tw, th, fine.Norm) : ZeroMeanTemplate.From(templateLevels[level]);
            var map = isFinal ? imageLevels[0] : imageLevels[level].PhaseTolerant(tpl.Width, tpl.Height);

            if (isFinal && current.Count > LargeFinalCandidates)
            {
                // Full-size checks are the expensive part: keep the best few after the 1/2 check (and any close to
                // the best one).
                var bestSoFar = current[0].Score;
                current = current
                    .Where((c, i) => i < LargeFinalCandidates || c.Score >= bestSoFar - LargeFinalScoreMargin)
                    .Take(LargeFinalCandidatesMax)
                    .ToList();
            }

            var next = new List<(int X, int Y, double Score)>();
            var seen = new HashSet<(int, int)>();
            foreach (var (cx, cy, _) in current)
            {
                var bx = cx * 2;
                var by = cy * 2;
                var spot = RefineOnMap(map, tpl, bx - 1, by - 1, bx + 2, by + 2);
                // Best spot on the window's edge: the true one may lie just outside, so walk uphill from there.
                var onEdge = spot.X == bx - 1 || spot.X == bx + 2 || spot.Y == by - 1 || spot.Y == by + 2;
                if (isFinal && onEdge) spot = ClimbOnMap(map, tpl, spot);
                if (seen.Add((spot.X, spot.Y))) next.Add(spot);
            }

            next.Sort((a, b) => b.Score.CompareTo(a.Score));
            current = next;
        }

        var found = new List<VisualMatch>();
        var best = 0.0;
        var bestBounds = default(ScreenRect);
        var rejectedForColor = 0;
        foreach (var (x, y, nccScore) in current)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var score = CombinedScore(nccScore, fine.DirectSimilarity(image, x, y));
            if (score <= 0) continue;

            if (!options.IgnoreColor && !fine.SameColor(image, x, y, options.MaxMeanColorDifference, options.AdaptToLighting))
            {
                if (score >= options.MinScore) rejectedForColor++;
                continue;
            }

            if (score > best)
            {
                best = score;
                bestBounds = new ScreenRect(x, y, tw, th);
            }

            if (score >= options.MinScore) found.Add(new VisualMatch(new ScreenRect(x, y, tw, th), score));
        }

        return new TemplateMatchResult(KeepBest(found, options.MaxResults), Math.Min(1.0, best), rejectedForColor, scale)
        {
            BestBounds = bestBounds,
        };
    }

    /// <summary>
    /// The score of a spot: the better of the shape score (NCC) and the direct colour comparison. Not-a-number or
    /// infinite values (from a division by an almost-zero spread) count as 0, so they can never hide a real match.
    /// </summary>
    internal static double CombinedScore(double nccScore, double directScore)
    {
        if (!double.IsFinite(nccScore)) nccScore = 0;
        if (!double.IsFinite(directScore)) directScore = 0;
        return Math.Clamp(Math.Max(nccScore, directScore), 0, 1);
    }

    /// <summary>Best first; drops matches that overlap a better one (the same thing found twice).</summary>
    internal static List<VisualMatch> KeepBest(IEnumerable<VisualMatch> found, int maxResults)
    {
        var kept = new List<VisualMatch>();
        foreach (var match in found.OrderByDescending(m => m.Score))
        {
            if (kept.Any(k => Overlap(k.Bounds, match.Bounds) > 0.3)) continue;
            kept.Add(match);
            if (kept.Count >= Math.Max(1, maxResults)) break;
        }

        return kept;
    }

    /// <summary>
    /// Runs the normal search (shape + whole-picture colour) and the foreground search, and merges both: every
    /// spot keeps its better score, so turning the foreground option on can never make a match score lower than
    /// the normal search. When the picture has no clear foreground (nothing stands out from its background), only
    /// the normal search runs.
    /// </summary>
    private static TemplateMatchResult FindWithForeground(BgraImage image, BgraImage template, FineTemplate fine,
        TemplateMatchOptions options, CancellationToken cancellationToken)
    {
        var standard = FindStandard(image, template, fine, options, cancellationToken);
        if (fine.IsLarge && standard.BestScore >= Math.Max(0.95, options.MinScore) && standard.Matches.Count >= 1)
            return standard;

        var model = ForegroundModel.Build(template);
        if (model is null) return standard;

        var foreground = ForegroundSearch.Find(image, model, options, cancellationToken);
        var merged = KeepBest(standard.Matches.Concat(foreground.Matches), options.MaxResults);
        return new TemplateMatchResult(merged, Math.Max(standard.BestScore, foreground.BestScore),
            merged.Count > 0 ? 0 : standard.RejectedForColor + foreground.RejectedForColor, Math.Max(standard.Scale, foreground.Scale))
        {
            BestBounds = merged.Count > 0 ? merged[0].Bounds
                : foreground.BestScore > standard.BestScore && !foreground.BestBounds.IsEmpty ? foreground.BestBounds
                : standard.BestBounds,
        };
    }

    /// <summary>Smallest inner part (pixels per side) an edge-ignoring search keeps, so the shape still means something.</summary>
    public const int MinInnerSize = 6;

    /// <summary>
    /// The part of a tw×th template that is compared when the outer <paramref name="fraction"/> is ignored, or null
    /// when nothing would be cut off (or the rest would be too small to match reliably).
    /// </summary>
    public static ScreenRect? InnerArea(int tw, int th, double fraction)
    {
        fraction = Math.Clamp(fraction, 0, 0.4);
        var bx = (int)Math.Round(tw * fraction);
        var by = (int)Math.Round(th * fraction);
        if (bx == 0 && by == 0) return null;
        // Shrink the border until the inner part is big enough.
        while (tw - 2 * bx < MinInnerSize && bx > 0) bx--;
        while (th - 2 * by < MinInnerSize && by > 0) by--;
        if (bx == 0 && by == 0) return null;
        return new ScreenRect(bx, by, tw - 2 * bx, th - 2 * by);
    }

    /// <summary>
    /// Matches the inner part of the template and reports each match as the full template rectangle (shifted back
    /// by the ignored border). The rectangle can stick out of the captured area a little when the element sits at
    /// the edge; its centre is still the element's centre.
    /// </summary>
    private static TemplateMatchResult FindInner(BgraImage image, BgraImage inner, int offsetX, int offsetY, int fullWidth, int fullHeight,
        TemplateMatchOptions options, CancellationToken cancellationToken)
    {
        var result = Find(image, inner, options, cancellationToken);
        var bestBounds = result.BestBounds.IsEmpty
            ? default
            : new ScreenRect(result.BestBounds.X - offsetX, result.BestBounds.Y - offsetY, fullWidth, fullHeight);
        if (result.Matches.Count == 0) return result with { BestBounds = bestBounds };
        var matches = result.Matches
            .Select(m => m with { Bounds = new ScreenRect(m.Bounds.X - offsetX, m.Bounds.Y - offsetY, fullWidth, fullHeight) })
            .ToList();
        return result with { Matches = matches, BestBounds = bestBounds };
    }

    /// <summary>True when the picture is basically one colour (template matching can't work with it).</summary>
    public static bool IsFlat(BgraImage template)
    {
        ArgumentNullException.ThrowIfNull(template);
        double sum = 0, sumSq = 0;
        var n = template.Width * template.Height;
        var p = template.Pixels;
        for (var i = 0; i < p.Length; i += 4)
        {
            var g = Gray(p, i);
            sum += g;
            sumSq += g * g;
        }

        var variance = sumSq / n - (sum / n) * (sum / n);
        return Math.Sqrt(Math.Max(0, variance)) < FlatTemplateStdDev;
    }

    /// <summary>
    /// Templates smaller than this (on their short side) are never shrunk to 1/4 unless 1/2 would be far too slow:
    /// shrinking a small picture that much blurs thin text and 1-pixel lines away, so the coarse pass missed the real
    /// spot (a Notepad "Hi" scored 76 % one try and 87 % the next). They use 1/2 at most.
    /// </summary>
    public const int SmallTemplateSize = 120;

    /// <summary>
    /// Templates wider or taller than this use <see cref="FindStandardLarge"/> (FFT coarse pass, 1/4 → 1/2 → full
    /// size refinement) so even full-screen-sized captures stay fast without skipping pixels.
    /// </summary>
    public const int LargeTemplateThreshold = 300;

    // A small template may spend this much more than the normal budget before it is allowed to go below 1/2.
    private const long SmallTemplateBudgetFactor = 8;

    /// <summary>The shrink factor (1, 2 or 4) for the coarse pass. Never 1/8 or smaller: that wiped out thin lines.</summary>
    internal static int ChooseScale(int width, int height, int tw, int th)
    {
        var small = Math.Min(tw, th) < SmallTemplateSize;
        var large = tw > LargeTemplateThreshold || th > LargeTemplateThreshold;
        var chosen = 1;
        for (var s = 1; s <= MaxCoarseScale; s *= 2)
        {
            if (s > 1 && Math.Min(tw, th) / s < MinCoarseTemplateSize) break;
            if (s > 2 && small && !large && Cost(s / 2) <= CoarseBudget * SmallTemplateBudgetFactor) break;
            chosen = s;
            if (large) continue; // large templates always go as coarse as allowed (their coarse pass uses an FFT)
            if (Cost(s) <= CoarseBudget) break;
        }

        return chosen;

        long Cost(int factor)
        {
            long positions = (long)((width - tw) / factor + 1) * ((height - th) / factor + 1);
            return positions * (tw / factor) * (th / factor);
        }
    }

    internal static float Gray(byte[] p, int i) => 0.114f * p[i] + 0.587f * p[i + 1] + 0.299f * p[i + 2];

    /// <summary>
    /// Grey version of the picture, shrunk by averaging every pixel of each scale×scale block (area average, no
    /// pixel is skipped, so a 1-pixel white line on black becomes a 1/scale-bright line instead of vanishing).
    /// </summary>
    internal static float[] GrayDownscaled(BgraImage image, int scale, out int width, out int height)
    {
        scale = Math.Max(1, scale);
        width = image.Width / scale;
        height = image.Height / scale;
        var result = new float[width * height];
        var p = image.Pixels;
        var stride = image.Width * 4;
        var inv = 1f / (scale * scale);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                float sum = 0;
                for (var dy = 0; dy < scale; dy++)
                {
                    var row = (y * scale + dy) * stride + x * scale * 4;
                    for (var dx = 0; dx < scale; dx++) sum += Gray(p, row + dx * 4);
                }

                result[y * width + x] = sum * inv;
            }
        }

        return result;
    }

    /// <summary>
    /// Grey version of a sub-region shrunk by area averaging (every pixel of each scale×scale block counts).
    /// Kept for callers that need a region; the matchers use <see cref="GrayDownscaled"/>.
    /// </summary>
    internal static float[] GrayDownscaledRegion(BgraImage image, ScreenRect region, int scale, out int width, out int height)
    {
        scale = Math.Max(1, scale);
        width = region.Width / scale;
        height = region.Height / scale;
        var result = new float[width * height];
        var p = image.Pixels;
        var stride = image.Width * 4;
        var inv = 1f / (scale * scale);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                float sum = 0;
                for (var dy = 0; dy < scale; dy++)
                {
                    var row = (region.Y + y * scale + dy) * stride + (region.X + x * scale) * 4;
                    for (var dx = 0; dx < scale; dx++) sum += Gray(p, row + dx * 4);
                }

                result[y * width + x] = sum * inv;
            }
        }

        return result;
    }

    /// <summary>
    /// Half-size copy of a grey map (2×2 area average).
    /// </summary>
    internal static float[] Halve(float[] values, int width, int height, out int halfWidth, out int halfHeight)
    {
        halfWidth = width / 2;
        halfHeight = height / 2;
        var result = new float[halfWidth * halfHeight];
        for (var y = 0; y < halfHeight; y++)
        {
            var r0 = 2 * y * width;
            var r1 = r0 + width;
            var dst = y * halfWidth;
            for (var x = 0; x < halfWidth; x++)
            {
                var i = r0 + 2 * x;
                var j = r1 + 2 * x;
                result[dst + x] = (values[i] + values[i + 1] + values[j] + values[j + 1]) * 0.25f;
            }
        }

        return result;
    }

    /// <summary>
    /// "Phase-tolerant" copy of a shrunken map: each value is the average of a 2×2 group of neighbours, i.e. of a
    /// 2s×2s block of the original picture starting every s pixels. A template block (s×s pixels) placed at any of
    /// the s possible sub-positions then lies completely inside the picture block it is compared with, so a 1-pixel
    /// line is never split between two blocks and lost. One value smaller than the input on each side.
    /// </summary>
    internal static float[] PairAverage(float[] values, int width, int height, out int resultWidth, out int resultHeight)
    {
        resultWidth = Math.Max(0, width - 1);
        resultHeight = Math.Max(0, height - 1);
        var result = new float[resultWidth * resultHeight];
        for (var y = 0; y < resultHeight; y++)
        {
            var r0 = y * width;
            var r1 = r0 + width;
            var dst = y * resultWidth;
            for (var x = 0; x < resultWidth; x++)
                result[dst + x] = (values[r0 + x] + values[r0 + x + 1] + values[r1 + x] + values[r1 + x + 1]) * 0.25f;
        }

        return result;
    }

    /// <summary>
    /// NCC score of the zero-mean template at every position of the map, using an FFT for the dot products and
    /// summed-area tables for the spread of each window. Flat windows (no spread) and negative scores stay 0.
    /// </summary>
    private static float[] NccScores(GrayMap map, ZeroMeanTemplate template, CancellationToken cancellationToken, out int px, out int py)
    {
        px = map.Width - template.Width + 1;
        py = map.Height - template.Height + 1;
        if (px <= 0 || py <= 0)
        {
            px = py = 0;
            return Array.Empty<float>();
        }

        var dots = FftCorrelator.CorrelateValid(map.Values, map.Width, map.Height, template.Values, template.Width, template.Height,
            cancellationToken);
        return ScoresFromDots(dots, map.Values, map.Width, map.Height, template.Width, template.Height, template.Norm, cancellationToken);
    }

    /// <summary>Turns dot products (template · window) into NCC scores using the window's spread.</summary>
    private static float[] ScoresFromDots(float[] dots, float[] hay, int hw, int hh, int tw, int th, double norm,
        CancellationToken cancellationToken)
    {
        var px = hw - tw + 1;
        var py = hh - th + 1;
        var (sum, sumSq) = Integrals(hay, hw, hh);
        var iw = hw + 1;
        var n = (double)tw * th;
        var scores = new float[px * py];
        for (var y = 0; y < py; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < px; x++)
            {
                var s = Box(sum, iw, x, y, tw, th);
                var s2 = Box(sumSq, iw, x, y, tw, th);
                var variance = s2 - s * s / n;
                if (variance < 1e-3) continue; // flat area: can't match a patterned template

                var score = dots[y * px + x] / (norm * Math.Sqrt(variance));
                if (double.IsFinite(score) && score > 0) scores[y * px + x] = (float)Math.Min(1.0, score);
            }
        }

        return scores;
    }

    /// <summary>
    /// Coarse pass: scores every position of the shrunken template on the shrunken picture and returns the best
    /// local peaks (in shrunken coordinates), spread out so one bright spot doesn't take every slot. Expensive
    /// passes (big template on a big area) use an FFT; the scores are the same.
    /// </summary>
    private static List<(int X, int Y)> CoarseCandidates(BgraImage image, BgraImage template, int scale, int count,
        CancellationToken cancellationToken)
    {
        var hay = GrayDownscaled(image, scale, out var cw, out var ch);
        var tpl = GrayDownscaled(template, scale, out var tw, out var th);
        var result = new List<(int, int)>();
        if (tw < 1 || th < 1 || tw > cw || th > ch) return result;

        var n = tw * th;
        var mean = tpl.Average();
        double norm = 0;
        for (var i = 0; i < n; i++)
        {
            tpl[i] -= mean;
            norm += tpl[i] * (double)tpl[i];
        }

        norm = Math.Sqrt(norm);
        if (norm < 1e-6) return result;

        var px = cw - tw + 1;
        var py = ch - th + 1;

        if ((long)px * py * n > FftThreshold)
        {
            var dots = FftCorrelator.CorrelateValid(hay, cw, ch, tpl, tw, th, cancellationToken);
            var fftScores = ScoresFromDots(dots, hay, cw, ch, tw, th, norm, cancellationToken);
            result.AddRange(PickPeaks(fftScores, px, py, tw, th, count));
            return result;
        }

        var (sum, sumSq) = Integrals(hay, cw, ch);
        var scores = new float[px * py];
        var iw = cw + 1;

        for (var y = 0; y < py; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < px; x++)
            {
                var s = Box(sum, iw, x, y, tw, th);
                var s2 = Box(sumSq, iw, x, y, tw, th);
                var variance = s2 - s * s / n;
                if (variance < 1e-3) continue; // flat area: can't match a patterned template

                double dot = 0;
                for (var r = 0; r < th; r++)
                    dot += Dot(new ReadOnlySpan<float>(tpl, r * tw, tw), new ReadOnlySpan<float>(hay, (y + r) * cw + x, tw));

                var score = dot / (norm * Math.Sqrt(variance));
                if (double.IsFinite(score)) scores[y * px + x] = (float)score;
            }
        }

        result.AddRange(PickPeaks(scores, px, py, tw, th, count));
        return result;
    }

    /// <summary>
    /// The best local peaks (3×3) of a score map, best first, kept at least half a template apart so repeated items
    /// each get a slot. A bounded heap keeps memory small on big screens.
    /// </summary>
    internal static List<(int X, int Y)> PickPeaks(float[] scores, int px, int py, int tw, int th, int count)
    {
        var heap = new PriorityQueue<(int X, int Y), float>();
        var limit = count * 4;
        for (var y = 0; y < py; y++)
        {
            for (var x = 0; x < px; x++)
            {
                var v = scores[y * px + x];
                if (v <= 0.05f || !IsPeak(scores, px, py, x, y, v)) continue;
                if (heap.Count < limit) heap.Enqueue((x, y), v);
                else if (heap.TryPeek(out _, out var lowest) && v > lowest) heap.EnqueueDequeue((x, y), v);
            }
        }

        var peaks = new List<(int X, int Y, float Score)>(heap.Count);
        while (heap.TryDequeue(out var spot, out var score)) peaks.Add((spot.X, spot.Y, score));
        peaks.Sort((a, b) => b.Score.CompareTo(a.Score));

        var result = new List<(int X, int Y)>();
        var minDx = Math.Max(1, tw / 2);
        var minDy = Math.Max(1, th / 2);
        foreach (var peak in peaks)
        {
            if (result.Any(c => Math.Abs(c.X - peak.X) < minDx && Math.Abs(c.Y - peak.Y) < minDy)) continue;
            result.Add((peak.X, peak.Y));
            if (result.Count >= count) break;
        }

        return result;
    }

    internal static bool IsPeak(float[] scores, int px, int py, int x, int y, float v)
    {
        for (var dy = -1; dy <= 1; dy++)
        {
            var yy = y + dy;
            if (yy < 0 || yy >= py) continue;
            for (var dx = -1; dx <= 1; dx++)
            {
                var xx = x + dx;
                if ((dx == 0 && dy == 0) || xx < 0 || xx >= px) continue;
                var other = scores[yy * px + xx];
                // Ties: only the first (top-left) one counts, so a flat plateau gives one peak.
                if (other > v || (other == v && (dy < 0 || (dy == 0 && dx < 0)))) return false;
            }
        }

        return true;
    }

    /// <summary>Fine pass: exact score at full size around (x0, y0), ± radius. Returns the best spot.</summary>
    private static (int X, int Y, double Score) Refine(BgraImage image, FineTemplate t, int x0, int y0, int radius)
    {
        var maxX = image.Width - t.Width;
        var maxY = image.Height - t.Height;
        var left = Math.Clamp(x0 - radius, 0, maxX);
        var right = Math.Clamp(x0 + radius, 0, maxX);
        var top = Math.Clamp(y0 - radius, 0, maxY);
        var bottom = Math.Clamp(y0 + radius, 0, maxY);

        // Grey patch covering every position we try.
        var pw = right - left + t.Width;
        var ph = bottom - top + t.Height;
        var patch = new float[pw * ph];
        var p = image.Pixels;
        var stride = image.Width * 4;
        for (var y = 0; y < ph; y++)
        {
            var row = (top + y) * stride + left * 4;
            for (var x = 0; x < pw; x++) patch[y * pw + x] = Gray(p, row + x * 4);
        }

        var (bx, by, score) = BestInPatch(patch, pw, ph, t.ZeroMeanGray, t.Width, t.Height, t.Norm, right - left, bottom - top);
        return bx < 0 ? (Math.Clamp(x0, left, right), Math.Clamp(y0, top, bottom), 0) : (left + bx, top + by, score);
    }

    /// <summary>Exact NCC on a grey map for every position from (left, top) to (right, bottom) (clamped). Best spot.</summary>
    private static (int X, int Y, double Score) RefineOnMap(GrayMap map, ZeroMeanTemplate t, int left, int top, int right, int bottom)
    {
        var maxX = map.Width - t.Width;
        var maxY = map.Height - t.Height;
        if (maxX < 0 || maxY < 0) return (0, 0, 0);
        left = Math.Clamp(left, 0, maxX);
        right = Math.Clamp(right, left, maxX);
        top = Math.Clamp(top, 0, maxY);
        bottom = Math.Clamp(bottom, top, maxY);

        var pw = right - left + t.Width;
        var ph = bottom - top + t.Height;
        var patch = new float[pw * ph];
        for (var y = 0; y < ph; y++)
            Array.Copy(map.Values, (top + y) * map.Width + left, patch, y * pw, pw);

        var (bx, by, score) = BestInPatch(patch, pw, ph, t.Values, t.Width, t.Height, t.Norm, right - left, bottom - top);
        return bx < 0 ? (left, top, 0) : (left + bx, top + by, score);
    }

    /// <summary>
    /// Moves a full-size spot uphill (3×3 neighbourhood) while a neighbour scores better, at most a few steps, so a
    /// spot that ended on the edge of its search window still reaches the true best position.
    /// </summary>
    private static (int X, int Y, double Score) ClimbOnMap(GrayMap map, ZeroMeanTemplate t, (int X, int Y, double Score) spot)
    {
        for (var step = 0; step < 4; step++)
        {
            var next = RefineOnMap(map, t, spot.X - 1, spot.Y - 1, spot.X + 1, spot.Y + 1);
            if (next.Score <= spot.Score || (next.X == spot.X && next.Y == spot.Y)) break;
            spot = next;
        }

        return spot;
    }

    /// <summary>
    /// Best NCC position inside a grey patch: positions (0..positionsX, 0..positionsY) of a tw×th zero-mean template.
    /// Returns (-1, -1, 0) when no position has any spread.
    /// </summary>
    private static (int X, int Y, double Score) BestInPatch(float[] patch, int pw, int ph, float[] zeroMeanTemplate, int tw, int th,
        double norm, int positionsX, int positionsY)
    {
        var (sum, sumSq) = Integrals(patch, pw, ph);
        var n = (double)tw * th;
        var iw = pw + 1;
        (int, int, double) best = (-1, -1, 0);

        for (var y = 0; y <= positionsY; y++)
        {
            for (var x = 0; x <= positionsX; x++)
            {
                var s = Box(sum, iw, x, y, tw, th);
                var s2 = Box(sumSq, iw, x, y, tw, th);
                var variance = s2 - s * s / n;
                if (variance < 1e-3) continue;

                double dot = 0;
                for (var r = 0; r < th; r++)
                    dot += Dot(new ReadOnlySpan<float>(zeroMeanTemplate, r * tw, tw),
                        new ReadOnlySpan<float>(patch, (y + r) * pw + x, tw));

                var score = dot / (norm * Math.Sqrt(variance));
                if (double.IsFinite(score) && score > best.Item3) best = (x, y, score);
            }
        }

        return best;
    }

    /// <summary>Summed-area tables (sum and sum of squares), (width+1)×(height+1), for O(1) box sums.</summary>
    internal static (double[] Sum, double[] SumSq) Integrals(float[] values, int width, int height)
    {
        var iw = width + 1;
        var sum = new double[iw * (height + 1)];
        var sumSq = new double[iw * (height + 1)];
        for (var y = 0; y < height; y++)
        {
            double rowSum = 0, rowSq = 0;
            for (var x = 0; x < width; x++)
            {
                double v = values[y * width + x];
                rowSum += v;
                rowSq += v * v;
                sum[(y + 1) * iw + x + 1] = sum[y * iw + x + 1] + rowSum;
                sumSq[(y + 1) * iw + x + 1] = sumSq[y * iw + x + 1] + rowSq;
            }
        }

        return (sum, sumSq);
    }

    internal static double Box(double[] table, int iw, int x, int y, int w, int h) =>
        table[(y + h) * iw + x + w] - table[y * iw + x + w] - table[(y + h) * iw + x] + table[y * iw + x];

    /// <summary>Dot product using SIMD where available.</summary>
    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var i = 0;
        var total = 0f;
        if (Vector.IsHardwareAccelerated && a.Length >= Vector<float>.Count)
        {
            var acc = Vector<float>.Zero;
            for (; i <= a.Length - Vector<float>.Count; i += Vector<float>.Count)
                acc += new Vector<float>(a.Slice(i)) * new Vector<float>(b.Slice(i));
            total = Vector.Dot(acc, Vector<float>.One);
        }

        for (; i < a.Length; i++) total += a[i] * b[i];
        return total;
    }

    /// <summary>How much two rectangles overlap (intersection ÷ smaller area), 0..1.</summary>
    internal static double Overlap(ScreenRect a, ScreenRect b)
    {
        var i = a.Intersect(b);
        if (i.IsEmpty) return 0;
        var smaller = Math.Min((double)a.Width * a.Height, (double)b.Width * b.Height);
        return i.Width * (double)i.Height / smaller;
    }

    /// <summary>A grey picture (or a shrunken / phase-tolerant copy of one).</summary>
    private sealed class GrayMap
    {
        public GrayMap(float[] values, int width, int height)
        {
            Values = values;
            Width = width;
            Height = height;
        }

        public float[] Values { get; }
        public int Width { get; }
        public int Height { get; }

        public GrayMap Halve() => new(TemplateMatcher.Halve(Values, Width, Height, out var w, out var h), w, h);

        /// <summary>
        /// The phase-tolerant copy (see <see cref="PairAverage"/>) when a template of the given size still fits in it,
        /// otherwise this map itself (the area is then barely bigger than the template, so there's little to search).
        /// </summary>
        public GrayMap PhaseTolerant(int templateWidth, int templateHeight)
        {
            if (Width - 1 < templateWidth || Height - 1 < templateHeight) return this;
            return new GrayMap(PairAverage(Values, Width, Height, out var w, out var h), w, h);
        }
    }

    /// <summary>Grey template values with their mean removed, and their length (for NCC).</summary>
    private sealed class ZeroMeanTemplate
    {
        public ZeroMeanTemplate(float[] values, int width, int height, double norm)
        {
            Values = values;
            Width = width;
            Height = height;
            Norm = norm;
        }

        public float[] Values { get; }
        public int Width { get; }
        public int Height { get; }
        public double Norm { get; }

        public static ZeroMeanTemplate From(GrayMap map)
        {
            var n = map.Width * map.Height;
            var values = new float[n];
            double mean = 0;
            for (var i = 0; i < n; i++) mean += map.Values[i];
            mean = n > 0 ? mean / n : 0;

            double norm = 0;
            for (var i = 0; i < n; i++)
            {
                values[i] = (float)(map.Values[i] - mean);
                norm += values[i] * (double)values[i];
            }

            return new ZeroMeanTemplate(values, map.Width, map.Height, Math.Sqrt(norm));
        }
    }

    /// <summary>The template prepared for the fine pass: zero-mean grey values, their length, and average colour.</summary>
    private sealed class FineTemplate
    {
        private readonly byte[] _pixels;
        private readonly byte _medianB;
        private readonly byte _medianG;
        private readonly byte _medianR;
        private readonly long _directBaseline;

        public FineTemplate(BgraImage template)
        {
            Width = template.Width;
            Height = template.Height;
            IsLarge = Width > LargeTemplateThreshold || Height > LargeTemplateThreshold;
            var p = template.Pixels;
            _pixels = p;
            var n = Width * Height;

            ZeroMeanGray = new float[n];
            double mean = 0, b = 0, g = 0, r = 0;
            var histB = new int[256];
            var histG = new int[256];
            var histR = new int[256];
            for (int i = 0, j = 0; j < n; i += 4, j++)
            {
                var v = Gray(p, i);
                ZeroMeanGray[j] = v;
                mean += v;
                b += p[i];
                g += p[i + 1];
                r += p[i + 2];
                histB[p[i]]++;
                histG[p[i + 1]]++;
                histR[p[i + 2]]++;
            }

            mean /= n;
            double norm = 0;
            for (var j = 0; j < n; j++)
            {
                ZeroMeanGray[j] -= (float)mean;
                norm += ZeroMeanGray[j] * (double)ZeroMeanGray[j];
            }

            Norm = Math.Sqrt(norm);
            MeanB = b / n;
            MeanG = g / n;
            MeanR = r / n;

            // Direct comparison baseline: how far the template is from its own "best flat colour" (per-channel median).
            _medianB = Median(histB, n);
            _medianG = Median(histG, n);
            _medianR = Median(histR, n);
            long baseline = 0;
            for (var i = 0; i < p.Length; i += 4)
                baseline += Math.Abs(p[i] - _medianB) + Math.Abs(p[i + 1] - _medianG) + Math.Abs(p[i + 2] - _medianR);
            _directBaseline = baseline;
        }

        public int Width { get; }
        public int Height { get; }
        public bool IsLarge { get; }
        public float[] ZeroMeanGray { get; }
        public double Norm { get; }
        public double MeanB { get; }
        public double MeanG { get; }
        public double MeanR { get; }

        /// <summary>
        /// Direct RGB similarity of the spot at (x, y): 1 − (sum of |R|,|G|,|B| differences) ÷ (the same sum between
        /// the template and its own median colour). A pixel-exact copy scores 1.0 (100 %); a plain patch of any single
        /// colour scores 0, so a mostly-black template (thin white lines on black) does NOT match an empty black
        /// area, which a plain "1 − mean difference ÷ 255" would score ~98 %. Alpha is never compared (screen
        /// captures leave it 0, decoded templates have 255). Never NaN: a flat template returns 0.
        /// </summary>
        public double DirectSimilarity(BgraImage image, int x, int y)
        {
            if (_directBaseline <= 0) return 0;
            if (x < 0 || y < 0 || x + Width > image.Width || y + Height > image.Height) return 0;
            var p = image.Pixels;
            var t = _pixels;
            var stride = image.Width * 4;
            var rowBytes = Width * 4;
            long difference = 0;
            for (var row = 0; row < Height; row++)
            {
                var i = (y + row) * stride + x * 4;
                var j = row * rowBytes;
                for (var col = 0; col < Width; col++, i += 4, j += 4)
                    difference += Math.Abs(p[i] - t[j]) + Math.Abs(p[i + 1] - t[j + 1]) + Math.Abs(p[i + 2] - t[j + 2]);
                if (difference >= _directBaseline) return 0; // already no better than a plain patch
            }

            return Math.Clamp(1.0 - difference / (double)_directBaseline, 0, 1);
        }

        /// <summary>Is the average colour of the spot at (x, y) close to the template's?</summary>
        public bool SameColor(BgraImage image, int x, int y, int maxDifference, bool adaptToLighting = true)
        {
            double b = 0, g = 0, r = 0;
            var p = image.Pixels;
            var stride = image.Width * 4;
            for (var row = 0; row < Height; row++)
            {
                var i = (y + row) * stride + x * 4;
                for (var col = 0; col < Width; col++, i += 4)
                {
                    b += p[i];
                    g += p[i + 1];
                    r += p[i + 2];
                }
            }

            var n = (double)Width * Height;
            return IsSameColor(r / n, g / n, b / n, MeanR, MeanG, MeanB, maxDifference, adaptToLighting);
        }

        private static byte Median(int[] histogram, int count)
        {
            var half = (count + 1) / 2;
            var seen = 0;
            for (var v = 0; v < 256; v++)
            {
                seen += histogram[v];
                if (seen >= half) return (byte)v;
            }

            return 255;
        }
    }

    internal static bool IsSameColor(double candR, double candG, double candB, double tplR, double tplG, double tplB,
        int maxDifference, bool adaptToLighting)
    {
        var rawDiff = Math.Max(Math.Abs(candB - tplB), Math.Max(Math.Abs(candG - tplG), Math.Abs(candR - tplR)));
        if (rawDiff <= maxDifference) return true;
        if (!adaptToLighting) return false;

        // Zero-mean / illumination-normalized chroma comparison: subtract local mean luminance so day/night
        // brightness shifts (additive and multiplicative dimming) don't reject matching UI elements, while still
        // rejecting elements of a genuinely different colour.
        var tplLum = (tplR + tplG + tplB) / 3.0;
        var candLum = (candR + candG + candB) / 3.0;
        double cTplR = tplR - tplLum, cTplG = tplG - tplLum, cTplB = tplB - tplLum;
        double cCandR = candR - candLum, cCandG = candG - candLum, cCandB = candB - candLum;

        var unscaledDiff = Math.Max(Math.Abs(cCandB - cTplB), Math.Max(Math.Abs(cCandG - cTplG), Math.Abs(cCandR - cTplR)));
        if (unscaledDiff <= maxDifference) return true;

        if (tplLum > 10.0 && candLum > 10.0)
        {
            var scale = Math.Clamp(tplLum / candLum, 0.35, 2.85);
            var scaledDiff = Math.Max(
                Math.Abs(cCandB * scale - cTplB),
                Math.Max(Math.Abs(cCandG * scale - cTplG), Math.Abs(cCandR * scale - cTplR)));
            if (scaledDiff <= maxDifference) return true;
        }

        return false;
    }
}
