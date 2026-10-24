using SmartMacro.Core.Ocr;

namespace SmartMacro.Core.Vision;

/// <summary>
/// Find image for a target on a plain background (a red circle captured inside a white square): scores the target's
/// own pixels (the foreground of <see cref="TemplateBackground"/>) and ignores what shows up where the picture only had
/// background. So a dark wall or a screen corner that moves into the picture's white corners / margins no longer
/// pulls the score down, and a few target pixels covered at the edge cost only their share.
///
/// Score of a spot (0..1):
/// - colour: per-pixel colour difference over the foreground (biggest channel difference, capped at 128). With
///   "adapt to lighting" the average shift is removed first (a darker / brighter screen still matches), and a shift
///   bigger than <see cref="TemplateMatchOptions.MaxMeanColorDifference"/> costs extra (a blue circle never matches a red one).
/// - distinct: a light penalty when the target isn't distinct from the screen behind it: the share of background
///   pixels that look like the target's colour (the middle of a big red area is NOT a red circle) costs up to
///   <see cref="DistinctWeight"/>.
///
/// Search: a strided pass with a sample of the pixels, the best spots refined around their position, then the exact
/// score with every foreground pixel. Only used when the normal search found nothing (see TemplateMatcher.Find), so
/// it never changes a match the normal search already made.
/// </summary>
internal static class BackgroundMaskMatcher
{
    /// <summary>Most a spot can lose for "the screen behind the target looks like the target itself".</summary>
    public const double DistinctWeight = 0.25;

    // A colour difference of this much (or more) on a pixel counts as a full miss for that pixel.
    private const int MaxResidual = 128;

    private const int CoarseForeground = 256;
    private const int CoarseBackground = 64;
    private const int FineForeground = 1024;
    private const int FineBackground = 256;
    private const int ExactBackground = 4096;

    // Rough number of pixel compares the strided pass may use.
    private const long Budget = 30_000_000;

    public static TemplateMatchResult Find(BgraImage image, BgraImage template, TemplateBackground background, TemplateMatchOptions options,
        CancellationToken cancellationToken)
    {
        int width = image.Width, height = image.Height, tw = template.Width, th = template.Height;
        if (tw > width || th > height || background.Width != tw || background.Height != th || background.ForegroundCount == 0)
            return TemplateMatchResult.None;

        var stride = image.Width * 4;
        var coarse = Samples.Build(template, background, stride, CoarseForeground, CoarseBackground);
        var fine = Samples.Build(template, background, stride, FineForeground, FineBackground);
        var exact = Samples.Build(template, background, stride, int.MaxValue, ExactBackground);

        int px = width - tw + 1, py = height - th + 1;
        var perPosition = coarse.ForegroundCount + coarse.BackgroundCount;
        var step = (int)Math.Ceiling(Math.Sqrt((double)px * py * perPosition / Budget));
        step = Math.Clamp(step, 1, Math.Max(1, Math.Min(tw, th) / 4));

        // 1. Strided pass.
        int gx = (px + step - 1) / step, gy = (py + step - 1) / step;
        var scores = new float[gx * gy];
        for (var j = 0; j < gy; j++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var y = Math.Min(j * step, py - 1);
            for (var i = 0; i < gx; i++)
                scores[j * gx + i] = (float)Score(image, Math.Min(i * step, px - 1), y, coarse, options);
        }

        var peaks = TemplateMatcher.PickPeaks(scores, gx, gy, Math.Max(1, tw / step), Math.Max(1, th / step),
            Math.Max(TemplateMatcher.MinRefineCandidates, options.MaxResults * 2));

        // 2. Refine each peak (window ± step, then uphill ± 1), 3. exact score.
        var found = new List<VisualMatch>();
        var seen = new HashSet<(int, int)>();
        var best = 0.0;
        var bestBounds = default(ScreenRect);
        foreach (var (cx, cy) in peaks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var x0 = Math.Min(cx * step, px - 1);
            var y0 = Math.Min(cy * step, py - 1);
            var (x, y, _) = BestNear(image, fine, options, x0, y0, step == 1 ? 1 : step, px, py);
            (x, y, _) = Climb(image, fine, options, x, y, px, py);
            if (!seen.Add((x, y))) continue;

            var score = Score(image, x, y, exact, options);
            if (score > best)
            {
                best = score;
                bestBounds = new ScreenRect(x, y, tw, th);
            }

            if (score >= options.MinScore) found.Add(new VisualMatch(new ScreenRect(x, y, tw, th), score));
        }

        return new TemplateMatchResult(TemplateMatcher.KeepBest(found, options.MaxResults), Math.Min(1.0, best), 0, step)
        {
            BestBounds = bestBounds,
        };
    }

    /// <summary>
    /// For matches the normal search already made: looks within a few pixels of each one (at least 4, 1/8 of the
    /// picture's short side) for the spot where the target's own pixels fit best, and moves the match there – with the
    /// better of both scores – when the masked score there is higher. Matches never get a lower score or disappear.
    /// </summary>
    public static TemplateMatchResult Recentre(BgraImage image, BgraImage template, TemplateBackground background, TemplateMatchResult result,
        TemplateMatchOptions options, CancellationToken cancellationToken)
    {
        int tw = template.Width, th = template.Height;
        if (tw > image.Width || th > image.Height || background.Width != tw || background.Height != th || background.ForegroundCount == 0)
            return result;

        var stride = image.Width * 4;
        var fine = Samples.Build(template, background, stride, FineForeground, FineBackground);
        var exact = Samples.Build(template, background, stride, int.MaxValue, ExactBackground);
        int px = image.Width - tw + 1, py = image.Height - th + 1;
        var radius = Math.Max(4, Math.Min(tw, th) / 8);

        var changed = false;
        var matches = new List<VisualMatch>(result.Matches.Count);
        foreach (var match in result.Matches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (x, y, _) = BestNear(image, fine, options, Math.Clamp(match.Bounds.X, 0, px - 1), Math.Clamp(match.Bounds.Y, 0, py - 1), radius, px, py);
            (x, y, _) = Climb(image, fine, options, x, y, px, py);
            var score = Score(image, x, y, exact, options);
            if (score > match.Score && (x != match.Bounds.X || y != match.Bounds.Y))
            {
                matches.Add(new VisualMatch(new ScreenRect(x, y, tw, th), score));
                changed = true;
            }
            else
            {
                matches.Add(match);
            }
        }

        if (!changed) return result;
        var kept = TemplateMatcher.KeepBest(matches, options.MaxResults);
        return result with { Matches = kept, BestScore = Math.Max(result.BestScore, kept[0].Score), BestBounds = kept[0].Bounds };
    }

    private static (int X, int Y, double Score) BestNear(BgraImage image, Samples s, TemplateMatchOptions options, int x0, int y0, int radius,
        int px, int py)
    {
        (int, int, double) best = (x0, y0, -1);
        for (var y = Math.Max(0, y0 - radius); y <= Math.Min(py - 1, y0 + radius); y++)
        {
            for (var x = Math.Max(0, x0 - radius); x <= Math.Min(px - 1, x0 + radius); x++)
            {
                var score = Score(image, x, y, s, options);
                if (score > best.Item3) best = (x, y, score);
            }
        }

        return best;
    }

    private static (int X, int Y, double Score) Climb(BgraImage image, Samples s, TemplateMatchOptions options, int x, int y, int px, int py)
    {
        var current = (X: x, Y: y, Score: Score(image, x, y, s, options));
        for (var i = 0; i < 4; i++)
        {
            var next = BestNear(image, s, options, current.X, current.Y, 1, px, py);
            if (next.Score <= current.Score || (next.X == current.X && next.Y == current.Y)) break;
            current = next;
        }

        return current;
    }

    /// <summary>The masked score of the template placed at (x, y) of the image (see the class summary).</summary>
    internal static double Score(BgraImage image, int x, int y, Samples s, TemplateMatchOptions options)
    {
        var p = image.Pixels;
        var origin = y * image.Width * 4 + x * 4;
        var n = s.ForegroundCount;

        // Average difference (screen − template) per channel = the lighting shift.
        long dB = 0, dG = 0, dR = 0;
        for (var k = 0; k < n; k++)
        {
            var i = origin + s.ForegroundOffsets[k];
            var t = k * 3;
            dB += p[i] - s.ForegroundColors[t];
            dG += p[i + 1] - s.ForegroundColors[t + 1];
            dR += p[i + 2] - s.ForegroundColors[t + 2];
        }

        double biasB = (double)dB / n, biasG = (double)dG / n, biasR = (double)dR / n;
        var maxBias = Math.Max(Math.Abs(biasB), Math.Max(Math.Abs(biasG), Math.Abs(biasR)));
        if (!options.AdaptToLighting) biasB = biasG = biasR = 0;

        double residualSum = 0;
        for (var k = 0; k < n; k++)
        {
            var i = origin + s.ForegroundOffsets[k];
            var t = k * 3;
            var rb = Math.Abs(p[i] - s.ForegroundColors[t] - biasB);
            var rg = Math.Abs(p[i + 1] - s.ForegroundColors[t + 1] - biasG);
            var rr = Math.Abs(p[i + 2] - s.ForegroundColors[t + 2] - biasR);
            residualSum += Math.Min(MaxResidual, Math.Max(rb, Math.Max(rg, rr)));
        }

        var colour = 1.0 - residualSum / ((double)n * MaxResidual);
        if (options.AdaptToLighting)
            colour -= Math.Max(0, maxBias - options.MaxMeanColorDifference) / MaxResidual;
        if (colour <= 0) return 0;

        // How distinct the target is from what's behind it: background pixels that look like the target's colour.
        var distinct = 1.0;
        if (s.BackgroundCount > 0)
        {
            var fgB = s.MeanB + (double)dB / n;
            var fgG = s.MeanG + (double)dG / n;
            var fgR = s.MeanR + (double)dR / n;
            var unlike = 0;
            for (var k = 0; k < s.BackgroundCount; k++)
            {
                var i = origin + s.BackgroundOffsets[k];
                if (Math.Abs(p[i] - fgB) > TemplateBackground.ColorTolerance || Math.Abs(p[i + 1] - fgG) > TemplateBackground.ColorTolerance
                    || Math.Abs(p[i + 2] - fgR) > TemplateBackground.ColorTolerance)
                    unlike++;
            }

            distinct = unlike / (double)s.BackgroundCount;
        }

        return Math.Clamp(colour * (1 - DistinctWeight + DistinctWeight * distinct), 0, 1);
    }

    /// <summary>
    /// A fixed, evenly spread sample of foreground pixels (byte offsets relative to the template's top-left corner in the
    /// searched image, plus their colours) and of background pixels (offsets only).
    /// </summary>
    internal sealed class Samples
    {
        private Samples(int[] foregroundOffsets, byte[] foregroundColors, int[] backgroundOffsets, double meanB, double meanG, double meanR)
        {
            ForegroundOffsets = foregroundOffsets;
            ForegroundColors = foregroundColors;
            BackgroundOffsets = backgroundOffsets;
            MeanB = meanB;
            MeanG = meanG;
            MeanR = meanR;
        }

        public int[] ForegroundOffsets { get; }
        public byte[] ForegroundColors { get; }
        public int[] BackgroundOffsets { get; }
        public int ForegroundCount => ForegroundOffsets.Length;
        public int BackgroundCount => BackgroundOffsets.Length;

        /// <summary>Average template colour of the sampled foreground pixels.</summary>
        public double MeanB { get; }
        public double MeanG { get; }
        public double MeanR { get; }

        public static Samples Build(BgraImage template, TemplateBackground background, int imageStride, int maxForeground, int maxBackground)
        {
            int tw = template.Width, th = template.Height;
            var fg = new List<int>(background.ForegroundCount);
            var bg = new List<int>(tw * th - background.ForegroundCount);
            for (var y = 0; y < th; y++)
                for (var x = 0; x < tw; x++)
                    (background.IsForeground(x, y) ? fg : bg).Add(y * tw + x);

            var fgPick = Spread(fg, maxForeground);
            var bgPick = Spread(bg, maxBackground);
            var p = template.Pixels;
            var offsets = new int[fgPick.Count];
            var colors = new byte[fgPick.Count * 3];
            double sb = 0, sg = 0, sr = 0;
            for (var k = 0; k < fgPick.Count; k++)
            {
                int x = fgPick[k] % tw, y = fgPick[k] / tw;
                offsets[k] = y * imageStride + x * 4;
                var i = fgPick[k] * 4;
                colors[k * 3] = p[i];
                colors[k * 3 + 1] = p[i + 1];
                colors[k * 3 + 2] = p[i + 2];
                sb += p[i];
                sg += p[i + 1];
                sr += p[i + 2];
            }

            var bgOffsets = new int[bgPick.Count];
            for (var k = 0; k < bgPick.Count; k++)
                bgOffsets[k] = bgPick[k] / tw * imageStride + bgPick[k] % tw * 4;

            var count = Math.Max(1, fgPick.Count);
            return new Samples(offsets, colors, bgOffsets, sb / count, sg / count, sr / count);
        }

        /// <summary>At most <paramref name="max"/> items, evenly spread over the list (all of them when it's shorter).</summary>
        private static List<int> Spread(List<int> items, int max)
        {
            if (items.Count <= max) return items;
            var picked = new List<int>(max);
            for (var k = 0; k < max; k++) picked.Add(items[(int)((long)k * items.Count / max)]);
            return picked;
        }
    }
}
