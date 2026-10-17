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

        var isLarge = w > TemplateMatcher.LargeTemplateThreshold || h > TemplateMatcher.LargeTemplateThreshold;
        if (isLarge)
            return BuildLarge(template);

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
        var share = full.EffectiveCount / (double)n;
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

    private static ForegroundModel? BuildLarge(BgraImage template)
    {
        int w = template.Width, h = template.Height, n = w * h;
        var sampleGray = TemplateMatcher.GrayDownscaledRegion(template, new ScreenRect(0, 0, w, h), 4, out var sw, out var sh);
        if (sw < 2 || sh < 2) return null;
        var threshold = OtsuThreshold(sampleGray);

        var ringX = Math.Max(1, sw / 10);
        var ringY = Math.Max(1, sh / 10);
        double borderSum = 0, brightSum = 0, darkSum = 0;
        int borderCount = 0, bright = 0, dark = 0;
        for (var y = 0; y < sh; y++)
        {
            for (var x = 0; x < sw; x++)
            {
                var v = sampleGray[y * sw + x];
                if (x < ringX || y < ringY || x >= sw - ringX || y >= sh - ringY)
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
        var full = ForegroundLevel.CreateLargeFromImage(template, threshold, sign);
        if (full is null) return null;
        var share = full.EffectiveCount / (double)n;
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
        var isLarge = _template.Width > TemplateMatcher.LargeTemplateThreshold || _template.Height > TemplateMatcher.LargeTemplateThreshold;
        var gray = isLarge
            ? TemplateMatcher.GrayDownscaledRegion(_template, new ScreenRect(0, 0, _template.Width, _template.Height), scale, out var w, out var h)
            : TemplateMatcher.GrayDownscaled(_template, scale, out w, out h);
        if (w < 2 || h < 2) return null;
        return ForegroundLevel.Create(gray, w, h, Threshold, Sign, isLarge);
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
        var step = gray.Length > 90_000 ? 4 : 1;
        for (var i = 0; i < gray.Length; i += step) histogram[Math.Clamp((int)gray[i], 0, 255)]++;

        double total = 0, sumAll = 0;
        for (var i = 0; i < 256; i++)
        {
            total += histogram[i];
            sumAll += i * (double)histogram[i];
        }

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

    public static ForegroundLevel? CreateLargeFromImage(BgraImage template, float threshold, int sign)
    {
        int width = template.Width, height = template.Height, n = width * height;
        var stepX = Math.Max(1, width / 48);
        var stepY = Math.Max(1, height / 48);
        var rows = new List<int>();
        var cols = new List<int>();
        var p = template.Pixels;
        var stride = template.Stride;
        double boxSum = 0, boxSumSq = 0, fgSum = 0;
        var gridCount = 0;

        for (var y = 0; y < height; y += stepY)
        {
            var row = y * stride;
            for (var x = 0; x < width; x += stepX)
            {
                var v = TemplateMatcher.Gray(p, row + x * 4);
                boxSum += v;
                boxSumSq += v * (double)v;
                gridCount++;
                if (sign * (v - threshold) > 0)
                {
                    rows.Add(y);
                    cols.Add(x);
                    fgSum += v;
                }
            }
        }

        if (rows.Count == 0 || rows.Count == gridCount) return null;
        var scale = n / (double)gridCount;
        var estimatedFullCount = (int)Math.Round(rows.Count * scale);
        if (estimatedFullCount <= 0 || estimatedFullCount >= n) return null;

        var maskVariance = estimatedFullCount - estimatedFullCount * (double)estimatedFullCount / n;
        var variance = (boxSumSq - boxSum * boxSum / gridCount) * scale;
        if (variance < 1e-3) return null;

        var covariance = fgSum * scale - estimatedFullCount * (boxSum * scale) / n;
        var selfScore = sign * covariance / Math.Sqrt(maskVariance * variance);
        if (selfScore <= 1e-6) return null;

        var level = new ForegroundLevel(width, height, rows.ToArray(), cols.ToArray(), maskVariance, sign, estimatedFullCount, stepX, stepY)
        {
            SelfScore = selfScore,
        };
        return level;
    }

    public static ForegroundLevel? Create(float[] gray, int width, int height, float threshold, int sign, bool subsampleCoarse = false)
    {
        var isLarge = width > TemplateMatcher.LargeTemplateThreshold || height > TemplateMatcher.LargeTemplateThreshold;
        var stepX = isLarge ? Math.Max(1, width / 48) : (subsampleCoarse ? Math.Max(1, width / 12) : 1);
        var stepY = isLarge ? Math.Max(1, height / 48) : (subsampleCoarse ? Math.Max(1, height / 12) : 1);
        var rows = new List<int>();
        var cols = new List<int>();
        for (var y = 0; y < height; y += stepY)
        {
            for (var x = 0; x < width; x += stepX)
            {
                if (sign * (gray[y * width + x] - threshold) > 0)
                {
                    rows.Add(y);
                    cols.Add(x);
                }
            }
        }

        var sampled = isLarge || subsampleCoarse;
        var gridCount = ((width + stepX - 1) / stepX) * ((height + stepY - 1) / stepY);
        var n = width * height;
        if (rows.Count == 0 || rows.Count == gridCount) return null;
        var estimatedFullCount = sampled ? (int)Math.Round(rows.Count * (n / (double)gridCount)) : rows.Count;
        if (estimatedFullCount <= 0 || estimatedFullCount >= n) return null;
        var level = new ForegroundLevel(width, height, rows.ToArray(), cols.ToArray(),
            estimatedFullCount - estimatedFullCount * (double)estimatedFullCount / n, sign, estimatedFullCount, stepX, stepY);

        // Score of the template against itself.
        if (sampled)
        {
            level.SelfScore = level.RawScoreSampled(gray, width, 0, 0);
        }
        else
        {
            double s = 0, s2 = 0;
            foreach (var v in gray)
            {
                s += v;
                s2 += v * (double)v;
            }

            level.SelfScore = level.RawScore(gray, width, 0, 0, s, s2);
        }

        return level.SelfScore > 1e-6 ? level : null;
    }

    private readonly int _fullCount;
    private readonly int _stepX;
    private readonly int _stepY;

    private ForegroundLevel(int width, int height, int[] rows, int[] cols, double maskVariance, int sign, int fullCount, int stepX, int stepY)
        : this(width, height, rows, cols, maskVariance, sign)
    {
        _fullCount = fullCount;
        _stepX = stepX;
        _stepY = stepY;
    }

    public int EffectiveCount => _fullCount > 0 ? _fullCount : Rows.Length;
    public bool IsLarge => _stepX > 1 || _stepY > 1;

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

        var count = EffectiveCount;
        if (count != rows.Length) foreground *= count / (double)rows.Length;
        var covariance = foreground - count * boxSum / n;
        return Sign * covariance / Math.Sqrt(MaskVariance * variance);
    }

    /// <summary>Score normalised so the template itself is 1, clamped to 0..1.</summary>
    public double Score(float[] hay, int hayWidth, int x, int y, double boxSum, double boxSumSq) =>
        Math.Clamp(RawScore(hay, hayWidth, x, y, boxSum, boxSumSq) / SelfScore, 0, 1);

    /// <summary>Stride-sampled raw score for large templates without computing a full patch integral table.</summary>
    public double RawScoreSampled(float[] gray, int grayWidth, int x0, int y0)
    {
        double boxSum = 0, boxSumSq = 0;
        var gridCount = 0;
        for (var y = 0; y < Height; y += _stepY)
        {
            var row = (y0 + y) * grayWidth + x0;
            for (var x = 0; x < Width; x += _stepX)
            {
                var v = gray[row + x];
                boxSum += v;
                boxSumSq += v * (double)v;
                gridCount++;
            }
        }

        var n = (double)Width * Height;
        var scale = n / gridCount;
        return RawScore(gray, grayWidth, x0, y0, boxSum * scale, boxSumSq * scale);
    }

    /// <summary>Stride-sampled score directly on a <see cref="BgraImage"/> for large templates.</summary>
    public double ScoreSampled(BgraImage image, int x0, int y0)
    {
        var p = image.Pixels;
        var stride = image.Stride;
        double boxSum = 0, boxSumSq = 0;
        var gridCount = 0;
        for (var y = 0; y < Height; y += _stepY)
        {
            var row = (y0 + y) * stride + x0 * 4;
            for (var x = 0; x < Width; x += _stepX)
            {
                var v = TemplateMatcher.Gray(p, row + x * 4);
                boxSum += v;
                boxSumSq += v * (double)v;
                gridCount++;
            }
        }

        var n = (double)Width * Height;
        var scale = n / gridCount;
        var variance = (boxSumSq - boxSum * boxSum / gridCount) * scale;
        if (variance < 1e-3) return 0;

        double foreground = 0;
        var rows = Rows;
        var cols = Cols;
        for (var k = 0; k < rows.Length; k++)
            foreground += TemplateMatcher.Gray(p, (y0 + rows[k]) * stride + (x0 + cols[k]) * 4);

        var count = EffectiveCount;
        if (count != rows.Length) foreground *= count / (double)rows.Length;
        var covariance = foreground - count * (boxSum * scale) / n;
        var raw = Sign * covariance / Math.Sqrt(MaskVariance * variance);
        return Math.Clamp(raw / SelfScore, 0, 1);
    }
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

        var large = tw > TemplateMatcher.LargeTemplateThreshold || th > TemplateMatcher.LargeTemplateThreshold;
        var budget = large ? Budget / 8 : Budget;
        var scale = 1;
        ForegroundLevel level = model.Full;
        foreach (var s in new[] { 1, 2, 4, 8 })
        {
            if (s > 1 && Math.Min(tw, th) / s < 8) break;
            var candidate = model.At(s);
            if (candidate is null) break;
            scale = s;
            level = candidate;
            if (large && s < 8 && (width > tw || height > th)) continue;
            long positions = (long)((width - tw) / s + 1) * ((height - th) / s + 1);
            if (positions * candidate.Count <= budget) break;
        }

        var hay = large
            ? TemplateMatcher.GrayDownscaledRegion(image, new ScreenRect(0, 0, width, height), scale, out var cw, out var ch)
            : TemplateMatcher.GrayDownscaled(image, scale, out cw, out ch);
        if (level.Width > cw || level.Height > ch) return TemplateMatchResult.None;
        var (sum, sumSq) = TemplateMatcher.Integrals(hay, cw, ch);
        var iw = cw + 1;
        var px = cw - level.Width + 1;
        var py = ch - level.Height + 1;
        var scores = new float[px * py];
        if (large)
        {
            var lw = level.Width;
            var lh = level.Height;
            var invN = 1.0 / (lw * lh);
            var rows = level.Rows;
            var cols = level.Cols;
            var fgLen = rows.Length;
            var offsets = new int[fgLen];
            for (var k = 0; k < fgLen; k++) offsets[k] = rows[k] * cw + cols[k];
            var effCount = level.EffectiveCount;
            var fgScale = effCount != fgLen ? effCount / (double)fgLen : 1.0;
            var boxToCov = effCount * invN;
            var sign = level.Sign;
            var invDenomBase = 1.0 / (level.SelfScore * Math.Sqrt(level.MaskVariance));

            for (var y = 0; y < py; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var yBase = y * cw;
                var i0Base = y * iw;
                var i1Base = (y + lh) * iw;
                var scoreRow = y * px;
                for (var x = 0; x < px; x++)
                {
                    var i0 = i0Base + x;
                    var i1 = i1Base + x;
                    var s = sum[i1 + lw] - sum[i0 + lw] - sum[i1] + sum[i0];
                    var s2 = sumSq[i1 + lw] - sumSq[i0 + lw] - sumSq[i1] + sumSq[i0];
                    var variance = s2 - s * s * invN;
                    if (variance < 1e-3) continue;

                    var baseIdx = yBase + x;
                    double fg = 0;
                    for (var k = 0; k < fgLen; k++) fg += hay[baseIdx + offsets[k]];
                    var cov = sign * (fg * fgScale - s * boxToCov);
                    if (cov <= 0) continue;

                    var score = cov * invDenomBase / Math.Sqrt(variance);
                    if (score > 0.35) scores[scoreRow + x] = (float)Math.Min(1.0, score);
                }
            }
        }
        else
        {
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
        }

        var candidateLimit = large ? Math.Min(4, Math.Max(2, options.MaxResults)) : Math.Max(12, options.MaxResults * 2);
        var candidates = TemplateMatcher.PickPeaks(scores, px, py, level.Width, level.Height, candidateLimit);
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

        if (level.IsLarge)
        {
            var bestX = Math.Clamp(x0, left, right);
            var bestY = Math.Clamp(y0, top, bottom);
            var bestScore = level.ScoreSampled(image, bestX, bestY);
            var step = Math.Max(1, (radius + 1) / 4);
            while (true)
            {
                int cx = bestX, cy = bestY;
                var span = step * 2;
                for (var dy = -span; dy <= span; dy += step)
                {
                    var y = cy + dy;
                    if (y < top || y > bottom) continue;
                    for (var dx = -span; dx <= span; dx += step)
                    {
                        var x = cx + dx;
                        if (x < left || x > right || (x == bestX && y == bestY)) continue;
                        var score = level.ScoreSampled(image, x, y);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestX = x;
                            bestY = y;
                        }
                    }
                }

                if (step == 1) break;
                step = Math.Max(1, step / 2);
            }

            return (bestX, bestY, bestScore);
        }

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
