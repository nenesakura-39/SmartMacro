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
}

/// <summary>
/// Finds a small picture (the "template", e.g. a button) inside a bigger one (a screen capture).
///
/// How: both pictures are turned into grey levels and compared with normalized cross-correlation (NCC), which
/// scores how well the light/dark pattern lines up (1 = identical, 0 = unrelated) and doesn't care about overall
/// brightness. To stay fast on a full screen, the search first runs on shrunken copies (coarse), then the best
/// few spots are checked again at full size (fine). Pure C#, no extra libraries, runs on the calling thread.
///
/// Limits (by design, documented for users): the template must appear at the same size. Different display
/// scaling, game resolution or graphics quality, rotation, or heavy transparency/animation lower the score.
/// </summary>
public static class TemplateMatcher
{
    /// <summary>Grey-level spread below which a template is "one flat colour" (NCC can't match those; use Find colour).</summary>
    public const double FlatTemplateStdDev = 2.0;

    // Rough number of multiply-adds the coarse pass may use. Picks the shrink factor.
    private const long CoarseBudget = 40_000_000;

    // The shrunken template must stay at least this many pixels on its short side, or it loses its shape.
    private const int MinCoarseTemplateSize = 8;

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

        if (options.MatchForeground)
            return FindWithForeground(image, template, fine, options, cancellationToken);

        return FindStandard(image, template, fine, options, cancellationToken);
    }

    /// <summary>The normal (whole picture, grey NCC) search.</summary>
    private static TemplateMatchResult FindStandard(BgraImage image, BgraImage template, FineTemplate fine, TemplateMatchOptions options,
        CancellationToken cancellationToken)
    {
        int width = image.Width, height = image.Height, tw = template.Width, th = template.Height;
        var scale = ChooseScale(width, height, tw, th);
        var candidateCount = Math.Max(12, options.MaxResults * 2);
        // A small template that still had to be shrunk to 1/4 or less (a big search area) loses detail: check more
        // candidate spots at full size so the real one isn't missed.
        if (scale > 2 && Math.Min(tw, th) < SmallTemplateSize) candidateCount *= 3;
        var candidates = CoarseCandidates(image, template, scale, candidateCount, cancellationToken);

        var found = new List<VisualMatch>();
        var best = 0.0;
        var rejectedForColor = 0;
        var radius = scale == 1 ? 0 : scale * 2 - 1;
        var checkedSpots = new HashSet<(int, int)>();

        foreach (var (cx, cy) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (x, y, score) = Refine(image, fine, cx * scale, cy * scale, radius);
            if (score <= 0 || !checkedSpots.Add((x, y))) continue;

            if (!options.IgnoreColor && !fine.SameColor(image, x, y, options.MaxMeanColorDifference))
            {
                // Only count spots that would have matched by shape (so the hint to the user is meaningful).
                if (score >= options.MinScore) rejectedForColor++;
                continue;
            }

            if (score > best) best = score;
            if (score >= options.MinScore) found.Add(new VisualMatch(new ScreenRect(x, y, tw, th), score));
        }

        return new TemplateMatchResult(KeepBest(found, options.MaxResults), Math.Min(1.0, best), rejectedForColor, scale);
    }

    /// <summary>Best first; drops matches that overlap a better one (the same thing found twice).</summary>
    private static List<VisualMatch> KeepBest(IEnumerable<VisualMatch> found, int maxResults)
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
    /// spot keeps its better score. When the picture has no clear foreground (nothing stands out from its
    /// background), only the normal search runs.
    /// </summary>
    private static TemplateMatchResult FindWithForeground(BgraImage image, BgraImage template, FineTemplate fine,
        TemplateMatchOptions options, CancellationToken cancellationToken)
    {
        var standard = FindStandard(image, template, fine, options, cancellationToken);
        var model = ForegroundModel.Build(template);
        if (model is null) return standard;

        var foreground = ForegroundSearch.Find(image, model, options, cancellationToken);
        var merged = KeepBest(standard.Matches.Concat(foreground.Matches), options.MaxResults);
        return new TemplateMatchResult(merged, Math.Max(standard.BestScore, foreground.BestScore),
            merged.Count > 0 ? 0 : standard.RejectedForColor + foreground.RejectedForColor, Math.Max(standard.Scale, foreground.Scale));
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
        if (result.Matches.Count == 0) return result;
        var matches = result.Matches
            .Select(m => m with { Bounds = new ScreenRect(m.Bounds.X - offsetX, m.Bounds.Y - offsetY, fullWidth, fullHeight) })
            .ToList();
        return result with { Matches = matches };
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
    /// Templates smaller than this (on their short side) are never shrunk to 1/4 or less: shrinking a small picture
    /// that much blurs thin text and 1-pixel lines away, so the coarse pass missed the real spot (a Notepad "Hi" scored
    /// 76 % one try and 87 % the next). They use 1/2 at most.
    /// </summary>
    public const int SmallTemplateSize = 120;

    // A small template may spend this much more than the normal budget before it is allowed to go below 1/2.
    private const long SmallTemplateBudgetFactor = 8;

    /// <summary>The shrink factor (1, 2, 4 or 8) for the coarse pass.</summary>
    internal static int ChooseScale(int width, int height, int tw, int th)
    {
        var small = Math.Min(tw, th) < SmallTemplateSize;
        var chosen = 1;
        foreach (var s in new[] { 1, 2, 4, 8 })
        {
            if (s > 1 && Math.Min(tw, th) / s < MinCoarseTemplateSize) break;
            if (s > 2 && small && Cost(s / 2) <= CoarseBudget * SmallTemplateBudgetFactor) break;
            chosen = s;
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

    /// <summary>Grey version of the picture, shrunk by averaging scale×scale blocks.</summary>
    internal static float[] GrayDownscaled(BgraImage image, int scale, out int width, out int height)
    {
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
    /// Coarse pass: scores every position of the shrunken template on the shrunken picture and returns the best
    /// local peaks (in shrunken coordinates), spread out so one bright spot doesn't take every slot.
    /// </summary>
    private static List<(int X, int Y)> CoarseCandidates(BgraImage image, BgraImage template, int scale, int count,
        CancellationToken cancellationToken)
    {
        var hay = GrayDownscaled(image, scale, out var cw, out var ch);
        var tpl = GrayDownscaled(template, scale, out var tw, out var th);
        var result = new List<(int, int)>();
        if (tw < 1 || th < 1 || tw > cw || th > ch) return result;

        var n = tw * th;
        double mean = 0;
        foreach (var v in tpl) mean += v;
        mean /= n;
        double norm = 0;
        for (var i = 0; i < tpl.Length; i++)
        {
            tpl[i] -= (float)mean;
            norm += tpl[i] * (double)tpl[i];
        }

        norm = Math.Sqrt(norm);
        if (norm < 1e-6) return result;

        var (sum, sumSq) = Integrals(hay, cw, ch);
        var px = cw - tw + 1;
        var py = ch - th + 1;
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

                scores[y * px + x] = (float)(dot / (norm * Math.Sqrt(variance)));
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

        var (sum, sumSq) = Integrals(patch, pw, ph);
        var n = t.Width * t.Height;
        var iw = pw + 1;
        (int, int, double) best = (x0, y0, 0);

        for (var y = 0; y <= bottom - top; y++)
        {
            for (var x = 0; x <= right - left; x++)
            {
                var s = Box(sum, iw, x, y, t.Width, t.Height);
                var s2 = Box(sumSq, iw, x, y, t.Width, t.Height);
                var variance = s2 - s * s / n;
                if (variance < 1e-3) continue;

                double dot = 0;
                for (var r = 0; r < t.Height; r++)
                    dot += Dot(new ReadOnlySpan<float>(t.ZeroMeanGray, r * t.Width, t.Width),
                        new ReadOnlySpan<float>(patch, (y + r) * pw + x, t.Width));

                var score = dot / (t.Norm * Math.Sqrt(variance));
                if (score > best.Item3) best = (left + x, top + y, score);
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

    /// <summary>The template prepared for the fine pass: zero-mean grey values, their length, and average colour.</summary>
    private sealed class FineTemplate
    {
        public FineTemplate(BgraImage template)
        {
            Width = template.Width;
            Height = template.Height;
            var p = template.Pixels;
            var n = Width * Height;
            ZeroMeanGray = new float[n];
            double mean = 0, b = 0, g = 0, r = 0;
            for (int i = 0, j = 0; j < n; i += 4, j++)
            {
                var v = Gray(p, i);
                ZeroMeanGray[j] = v;
                mean += v;
                b += p[i];
                g += p[i + 1];
                r += p[i + 2];
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
        }

        public int Width { get; }
        public int Height { get; }
        public float[] ZeroMeanGray { get; }
        public double Norm { get; }
        public double MeanB { get; }
        public double MeanG { get; }
        public double MeanR { get; }

        /// <summary>Is the average colour of the spot at (x, y) close to the template's?</summary>
        public bool SameColor(BgraImage image, int x, int y, int maxDifference)
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
            var diff = Math.Max(Math.Abs(b / n - MeanB), Math.Max(Math.Abs(g / n - MeanG), Math.Abs(r / n - MeanR)));
            return diff <= maxDifference;
        }
    }
}
