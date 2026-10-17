using SmartMacro.Core.Ocr;

namespace SmartMacro.Core.Vision;

/// <summary>
/// The "foreground" of a template: the pixels that stand out from its background, e.g. the white letters and ring
/// of a semi-transparent game button. Found with Otsu's threshold on the grey levels; the side (bright or dark)
/// that differs most from the template's border is the foreground, because the border is almost always background.
///
/// Why: on a semi-transparent button (Roblox Fisch "SHAKE") only the white strokes are opaque; the rest shows
/// whatever is behind it (a brown pier one time, blue water the next). Comparing the whole picture fails when the
/// background changes. Comparing "are the foreground pixels brighter than the rest, in the same pattern?" doesn't.
/// </summary>
internal sealed class ForegroundModel
{
    /// <summary>The foreground must cover at least / at most this share of the template.</summary>
    public const double MinShare = 0.02;
    public const double MaxShare = 0.75;

    /// <summary>Foreground and background must differ by at least this many grey levels (0..255).</summary>
    public const double MinContrast = 25;

    /// <summary>How well the mask must describe the template itself (raw score 0..1); below this it's no real foreground.</summary>
    public const double MinSelfScore = 0.6;

    private readonly BgraImage _template;

    private ForegroundModel(BgraImage template, float threshold, int sign, ForegroundLevel full, double meanB, double meanG, double meanR)
    {
        _template = template;
        Threshold = threshold;
        Sign = sign;
        Full = full;
        MeanB = meanB;
        MeanG = meanG;
        MeanR = meanR;
    }

    public int Width => _template.Width;
    public int Height => _template.Height;

    /// <summary>Grey level that separates foreground from background.</summary>
    public float Threshold { get; }

    /// <summary>+1 = the foreground is brighter than the threshold, -1 = darker.</summary>
    public int Sign { get; }

    /// <summary>The mask at full size.</summary>
    public ForegroundLevel Full { get; }

    /// <summary>Average colour of the foreground pixels (used for the colour check instead of the whole picture's).</summary>
    public double MeanB { get; }
    public double MeanG { get; }
    public double MeanR { get; }

    /// <summary>Null when the template has no clear foreground (nothing stands out from its background).</summary>
    public static ForegroundModel? Build(BgraImage template)
    {
        ArgumentNullException.ThrowIfNull(template);
        int w = template.Width, h = template.Height, n = w * h;
        if (w < 4 || h < 4) return null;

        var gray = TemplateMatcher.GrayDownscaled(template, 1, out _, out _);
        var threshold = OtsuThreshold(gray);

        var ring = Math.Max(1, Math.Min(w, h) / 10);
        double borderSum = 0, brightSum = 0, darkSum = 0;
        int borderCount = 0, bright = 0, dark = 0;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var v = gray[y * w + x];
                if (x < ring || y < ring || x >= w - ring || y >= h - ring)
                {
                    borderSum += v;
                    borderCount++;
                }

                if (v > threshold)
                {
                    brightSum += v;
                    bright++;
                }
                else
                {
                    darkSum += v;
                    dark++;
                }
            }
        }

        if (bright == 0 || dark == 0 || borderCount == 0) return null;
        double meanBright = brightSum / bright, meanDark = darkSum / dark, border = borderSum / borderCount;
        if (meanBright - meanDark < MinContrast) return null;

        var sign = Math.Abs(meanBright - border) >= Math.Abs(meanDark - border) ? 1 : -1;
        var full = ForegroundLevel.Create(gray, w, h, threshold, sign);
        if (full is null) return null;
        var share = full.Count / (double)n;
        if (share < MinShare || share > MaxShare || full.SelfScore < MinSelfScore) return null;

        double b = 0, g = 0, r = 0;
        var p = template.Pixels;
        for (var k = 0; k < full.Count; k++)
        {
            var i = (full.Rows[k] * w + full.Cols[k]) * 4;
            b += p[i];
            g += p[i + 1];
            r += p[i + 2];
        }

        return new ForegroundModel(template, threshold, sign, full, b / full.Count, g / full.Count, r / full.Count);
    }

    /// <summary>The mask for a template shrunk by <paramref name="scale"/> (null if it has no usable foreground left).</summary>
    public ForegroundLevel? At(int scale)
    {
        if (scale <= 1) return Full;
        var gray = TemplateMatcher.GrayDownscaled(_template, scale, out var w, out var h);
        if (w < 2 || h < 2) return null;
        return ForegroundLevel.Create(gray, w, h, Threshold, Sign);
    }

    /// <summary>Is the average colour of the foreground pixels at (x, y) close to the template's foreground colour?</summary>
    public bool SameColor(BgraImage image, int x, int y, int maxDifference)
    {
        double b = 0, g = 0, r = 0;
        var p = image.Pixels;
        var stride = image.Stride;
        for (var k = 0; k < Full.Count; k++)
        {
            var i = (y + Full.Rows[k]) * stride + (x + Full.Cols[k]) * 4;
            b += p[i];
            g += p[i + 1];
            r += p[i + 2];
        }

        var c = (double)Full.Count;
        var diff = Math.Max(Math.Abs(b / c - MeanB), Math.Max(Math.Abs(g / c - MeanG), Math.Abs(r / c - MeanR)));
        return diff <= maxDifference;
    }

    /// <summary>Otsu's method: the grey level that best splits the values into two groups.</summary>
    internal static float OtsuThreshold(float[] gray)
    {
        var histogram = new int[256];
        foreach (var v in gray) histogram[Math.Clamp((int)v, 0, 255)]++;

        double total = gray.Length, sumAll = 0;
        for (var i = 0; i < 256; i++) sumAll += i * (double)histogram[i];

        double sumBelow = 0, weightBelow = 0, bestBetween = -1;
        var best = 127;
        for (var t = 0; t < 256; t++)
        {
            weightBelow += histogram[t];
            if (weightBelow == 0) continue;
            var weightAbove = total - weightBelow;
            if (weightAbove == 0) break;
            sumBelow += t * (double)histogram[t];
            var meanBelow = sumBelow / weightBelow;
            var meanAbove = (sumAll - sumBelow) / weightAbove;
            var between = weightBelow * weightAbove * (meanBelow - meanAbove) * (meanBelow - meanAbove);
            if (between > bestBetween)
            {
                bestBetween = between;
                best = t;
            }
        }

        // Values above "best" are the bright group. +0.5 so a value exactly on the level counts as dark.
        return best + 0.5f;
    }
}

/// <summary>
/// The foreground mask of a template at one size. The score of a spot is the correlation between the mask (1 on the
/// foreground, 0 elsewhere) and the screen's grey levels there, signed so "foreground brighter" (or darker) is
/// positive, divided by the template's own score (so the template itself scores 100 %).
/// </summary>
internal sealed class ForegroundLevel
{
    private ForegroundLevel(int width, int height, int[] rows, int[] cols, double maskVariance, int sign)
    {
        Width = width;
        Height = height;
        Rows = rows;
        Cols = cols;
        MaskVariance = maskVariance;
        Sign = sign;
    }

    public int Width { get; }
    public int Height { get; }
    public int[] Rows { get; }
    public int[] Cols { get; }
    public int Count => Rows.Length;

    /// <summary>Σ(mask − mean)² = Count − Count² / n.</summary>
    public double MaskVariance { get; }

    public int Sign { get; }

    /// <summary>The raw score of the template against itself (0..1).</summary>
    public double SelfScore { get; private set; }

    public static ForegroundLevel? Create(float[] gray, int width, int height, float threshold, int sign)
    {
        var rows = new List<int>();
        var cols = new List<int>();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (sign * (gray[y * width + x] - threshold) > 0)
                {
                    rows.Add(y);
                    cols.Add(x);
                }
            }
        }

        var n = width * height;
        if (rows.Count == 0 || rows.Count == n) return null;
        var level = new ForegroundLevel(width, height, rows.ToArray(), cols.ToArray(), rows.Count - rows.Count * (double)rows.Count / n, sign);

        // Score of the template against itself.
        double s = 0, s2 = 0;
        foreach (var v in gray)
        {
            s += v;
            s2 += v * (double)v;
        }

        level.SelfScore = level.RawScore(gray, width, 0, 0, s, s2);
        return level.SelfScore > 1e-6 ? level : null;
    }

    /// <summary>
    /// Raw score at (x, y) of a grey picture <paramref name="hayWidth"/> wide. <paramref name="boxSum"/> and
    /// <paramref name="boxSumSq"/> are the sum and sum of squares of the Width×Height box there.
    /// </summary>
    public double RawScore(float[] hay, int hayWidth, int x, int y, double boxSum, double boxSumSq)
    {
        var n = (double)Width * Height;
        var variance = boxSumSq - boxSum * boxSum / n;
        if (variance < 1e-3) return 0;

        double foreground = 0;
        var rows = Rows;
        var cols = Cols;
        for (var k = 0; k < rows.Length; k++) foreground += hay[(y + rows[k]) * hayWidth + x + cols[k]];

        var covariance = foreground - Count * boxSum / n;
        return Sign * covariance / Math.Sqrt(MaskVariance * variance);
    }

    /// <summary>Score normalised so the template itself is 1, clamped to 0..1.</summary>
    public double Score(float[] hay, int hayWidth, int x, int y, double boxSum, double boxSumSq) =>
        Math.Clamp(RawScore(hay, hayWidth, x, y, boxSum, boxSumSq) / SelfScore, 0, 1);
}

/// <summary>Searches a screen picture for a template's foreground (coarse pass on a shrunken copy, then exact).</summary>
internal static class ForegroundSearch
{
    // Rough number of additions the coarse pass may use (one per foreground pixel per position).
    private const long Budget = 60_000_000;

    public static TemplateMatchResult Find(BgraImage image, ForegroundModel model, TemplateMatchOptions options,
        CancellationToken cancellationToken)
    {
        int width = image.Width, height = image.Height, tw = model.Width, th = model.Height;
        if (tw > width || th > height) return TemplateMatchResult.None;

        var scale = 1;
        ForegroundLevel level = model.Full;
        foreach (var s in new[] { 1, 2, 4 })
        {
            if (s > 1 && Math.Min(tw, th) / s < 8) break;
            var candidate = model.At(s);
            if (candidate is null) break;
            scale = s;
            level = candidate;
            long positions = (long)((width - tw) / s + 1) * ((height - th) / s + 1);
            if (positions * candidate.Count <= Budget) break;
        }

        var hay = TemplateMatcher.GrayDownscaled(image, scale, out var cw, out var ch);
        if (level.Width > cw || level.Height > ch) return TemplateMatchResult.None;
        var (sum, sumSq) = TemplateMatcher.Integrals(hay, cw, ch);
        var iw = cw + 1;
        var px = cw - level.Width + 1;
        var py = ch - level.Height + 1;
        var scores = new float[px * py];
        for (var y = 0; y < py; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < px; x++)
            {
                var s = TemplateMatcher.Box(sum, iw, x, y, level.Width, level.Height);
                var s2 = TemplateMatcher.Box(sumSq, iw, x, y, level.Width, level.Height);
                scores[y * px + x] = (float)level.Score(hay, cw, x, y, s, s2);
            }
        }

        var candidates = TemplateMatcher.PickPeaks(scores, px, py, level.Width, level.Height, Math.Max(12, options.MaxResults * 2));
        var radius = scale == 1 ? 0 : scale * 2 - 1;
        var found = new List<VisualMatch>();
        var checkedSpots = new HashSet<(int, int)>();
        double best = 0;
        var rejectedForColor = 0;

        foreach (var (cx, cy) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (x, y, score) = Refine(image, model.Full, cx * scale, cy * scale, radius);
            if (score <= 0 || !checkedSpots.Add((x, y))) continue;

            if (!options.IgnoreColor && !model.SameColor(image, x, y, options.MaxMeanColorDifference))
            {
                if (score >= options.MinScore) rejectedForColor++;
                continue;
            }

            if (score > best) best = score;
            if (score >= options.MinScore) found.Add(new VisualMatch(new ScreenRect(x, y, tw, th), score));
        }

        var kept = found.OrderByDescending(m => m.Score).ToList();
        return new TemplateMatchResult(kept, Math.Min(1.0, best), rejectedForColor, scale);
    }

    /// <summary>Exact score at full size around (x0, y0), ± radius. Returns the best spot.</summary>
    private static (int X, int Y, double Score) Refine(BgraImage image, ForegroundLevel level, int x0, int y0, int radius)
    {
        var maxX = image.Width - level.Width;
        var maxY = image.Height - level.Height;
        var left = Math.Clamp(x0 - radius, 0, maxX);
        var right = Math.Clamp(x0 + radius, 0, maxX);
        var top = Math.Clamp(y0 - radius, 0, maxY);
        var bottom = Math.Clamp(y0 + radius, 0, maxY);

        var pw = right - left + level.Width;
        var ph = bottom - top + level.Height;
        var patch = new float[pw * ph];
        var p = image.Pixels;
        var stride = image.Stride;
        for (var y = 0; y < ph; y++)
        {
            var row = (top + y) * stride + left * 4;
            for (var x = 0; x < pw; x++) patch[y * pw + x] = TemplateMatcher.Gray(p, row + x * 4);
        }

        var (sum, sumSq) = TemplateMatcher.Integrals(patch, pw, ph);
        var iw = pw + 1;
        (int, int, double) best = (x0, y0, 0);
        for (var y = 0; y <= bottom - top; y++)
        {
            for (var x = 0; x <= right - left; x++)
            {
                var s = TemplateMatcher.Box(sum, iw, x, y, level.Width, level.Height);
                var s2 = TemplateMatcher.Box(sumSq, iw, x, y, level.Width, level.Height);
                var score = level.Score(patch, pw, x, y, s, s2);
                if (score > best.Item3) best = (left + x, top + y, score);
            }
        }

        return best;
    }
}
