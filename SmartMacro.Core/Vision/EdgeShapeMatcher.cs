using SmartMacro.Core.Ocr;

namespace SmartMacro.Core.Vision;

/// <summary>
/// Vision 2.0 Partial-Visibility (Occlusion-Tolerant) Matcher &amp; Edge/Shape Gradient Orientation Matcher.
/// Designed for complex game scenes where moving objects are partially covered by particles, hit flashes, floating damage
/// numbers, or rotation.
/// </summary>
public static class EdgeShapeMatcher
{
    /// <summary>
    /// Evaluates a template against <paramref name="image"/> using a grid of independent sub-cells so particles or effects
    /// covering up to <c>1.0 - minVisibleFraction</c> of the target do not sink the match score.
    /// </summary>
    public static TemplateMatchResult FindWithPartialVisibility(
        BgraImage image,
        BgraImage template,
        TemplateMatchOptions options,
        double minVisibleFraction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(template);
        options ??= new TemplateMatchOptions();
        minVisibleFraction = Math.Clamp(minVisibleFraction, 0.35, 1.0);

        int width = image.Width, height = image.Height, tw = template.Width, th = template.Height;
        if (tw > width || th > height || tw < TemplateMatcher.MinInnerSize || th < TemplateMatcher.MinInnerSize)
            return TemplateMatchResult.None;

        // Partition template into a 3×3 or 4×4 grid of cells (each cell at least 4×4 pixels).
        int cols = tw >= 24 ? 4 : tw >= 12 ? 3 : 2;
        int rows = th >= 24 ? 4 : th >= 12 ? 3 : 2;
        var cells = BuildCells(template, cols, rows);
        if (cells.Count == 0) return TemplateMatchResult.None;

        // Gather candidate top-left positions:
        // 1) Run a relaxed single-scale search for the whole template, and
        // 2) Run quick searches for up to 3 most-detailed sub-cells (in case particles cover half the template and shift the whole-template peak).
        var candidatePositions = new HashSet<(int X, int Y)>();
        var relaxedOptions = options with
        {
            MinScore = Math.Max(0.35, options.MinScore * minVisibleFraction * 0.75),
            AllowMultiScale = false,
            MaxResults = Math.Max(16, options.MaxResults * 2),
            MinVisibleFraction = 1.0,
            Detector = Models.ImageDetectorKind.Template,
            RotationRangeDeg = 0,
            AdditionalTemplates = null,
        };

        var wholePass = TemplateMatcher.Find(image, template, relaxedOptions, cancellationToken);
        if (!wholePass.BestBounds.IsEmpty)
            AddNeighborhood(candidatePositions, wholePass.BestBounds.X, wholePass.BestBounds.Y, width - tw, height - th, radius: 2);
        foreach (var m in wholePass.Matches)
            AddNeighborhood(candidatePositions, m.Bounds.X, m.Bounds.Y, width - tw, height - th, radius: 1);

        // Anchor cells: pick up to 3 highest-detail cells from distinct quadrants so if one side is covered by particles,
        // the uncovered side still proposes the exact (x - cell.OffsetX, y - cell.OffsetY) candidate position.
        foreach (var anchor in SelectAnchorCells(cells, maxAnchors: 3))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (anchor.Crop.Width < TemplateMatcher.MinInnerSize || anchor.Crop.Height < TemplateMatcher.MinInnerSize)
                continue;
            var anchorRes = TemplateMatcher.Find(image, anchor.Crop, relaxedOptions with { MaxResults = 8 }, cancellationToken);
            foreach (var hit in anchorRes.Matches)
            {
                int ox = hit.Bounds.X - anchor.OffsetX;
                int oy = hit.Bounds.Y - anchor.OffsetY;
                if (ox >= 0 && oy >= 0 && ox <= width - tw && oy <= height - th)
                    AddNeighborhood(candidatePositions, ox, oy, width - tw, height - th, radius: 1);
            }
        }

        int requiredCells = Math.Max(1, (int)Math.Ceiling(cells.Count * minVisibleFraction));
        double cellPassThreshold = Math.Max(0.55, options.MinScore - 0.08);
        var cellScores = new double[cells.Count];

        var matches = new List<VisualMatch>();
        double bestScore = 0;
        ScreenRect bestBounds = default;
        int rejectedForColor = 0;

        foreach (var (cx, cy) in candidatePositions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int passingCells = 0;
            int colorMatchingCells = 0;

            for (var i = 0; i < cells.Count; i++)
            {
                var cell = cells[i];
                var s = ScoreCellAt(image, cell, cx + cell.OffsetX, cy + cell.OffsetY);
                cellScores[i] = s;
                if (s >= cellPassThreshold)
                {
                    passingCells++;
                    if (options.IgnoreColor || CellSameColor(image, cell, cx + cell.OffsetX, cy + cell.OffsetY, options.MaxMeanColorDifference, options.AdaptToLighting))
                        colorMatchingCells++;
                }
            }

            Array.Sort(cellScores);
            Array.Reverse(cellScores); // highest scores first

            // Trimmed mean over the required visible cells (the unoccluded portion of the target).
            double sumTop = 0;
            for (var i = 0; i < requiredCells; i++) sumTop += cellScores[i];
            double trimmedScore = sumTop / requiredCells;
            double visibleFraction = (double)passingCells / cells.Count;

            if (!options.IgnoreColor && colorMatchingCells < requiredCells)
            {
                if (trimmedScore >= options.MinScore && visibleFraction >= minVisibleFraction)
                    rejectedForColor++;
                continue;
            }

            // Effective confidence reflects the unoccluded cells once the visibility requirement is met.
            double effectiveScore = visibleFraction >= minVisibleFraction
                ? trimmedScore
                : trimmedScore * (visibleFraction / minVisibleFraction);

            if (effectiveScore > bestScore)
            {
                bestScore = effectiveScore;
                bestBounds = new ScreenRect(cx, cy, tw, th);
            }

            if (visibleFraction >= minVisibleFraction && trimmedScore >= options.MinScore)
            {
                matches.Add(new VisualMatch(new ScreenRect(cx, cy, tw, th), Math.Min(1.0, trimmedScore))
                {
                    VisibleFraction = visibleFraction,
                });
            }
        }

        var kept = TemplateMatcher.KeepBest(matches, options.MaxResults);
        return new TemplateMatchResult(kept, Math.Min(1.0, bestScore), kept.Count > 0 ? 0 : rejectedForColor, 1)
        {
            BestBounds = kept.Count > 0 ? kept[0].Bounds : bestBounds,
        };
    }

    /// <summary>
    /// Finds <paramref name="template"/> inside <paramref name="image"/> by comparing Sobel edge gradient orientations.
    /// Immune to colour changes, brightness inversions, and flat fills; focuses strictly on geometric contours.
    /// </summary>
    public static TemplateMatchResult FindByEdgeShape(
        BgraImage image,
        BgraImage template,
        TemplateMatchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(template);
        options ??= new TemplateMatchOptions();

        int width = image.Width, height = image.Height, tw = template.Width, th = template.Height;
        if (tw > width || th > height || tw < 4 || th < 4) return TemplateMatchResult.None;

        var edgeSamples = ExtractTemplateEdgeSamples(template);
        if (edgeSamples.Count == 0) return TemplateMatchResult.None;

        ComputeImageGradients(image, out var gx, out var gy, out var mag);

        // Coarse step (stride 2 when search area is large, then refine ±2 around peaks).
        int maxX = width - tw;
        int maxY = height - th;
        int step = (long)(maxX + 1) * (maxY + 1) * edgeSamples.Count > 12_000_000 ? 2 : 1;

        var coarsePeaks = new List<(int X, int Y, double Score)>();
        for (int y = 0; y <= maxY; y += step)
        {
            if ((y & 31) == 0) cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x <= maxX; x += step)
            {
                double s = ScoreEdgesAt(gx, gy, mag, width, edgeSamples, x, y);
                if (s >= Math.Max(0.45, options.MinScore - 0.22))
                    coarsePeaks.Add((x, y, s));
            }
        }

        coarsePeaks.Sort((a, b) => b.Score.CompareTo(a.Score));
        int maxRefine = Math.Min(coarsePeaks.Count, Math.Max(40, options.MaxResults * 4));

        var checkedSpots = new HashSet<(int X, int Y)>();
        var matches = new List<VisualMatch>();
        double bestScore = 0;
        ScreenRect bestBounds = default;

        for (int i = 0; i < maxRefine; i++)
        {
            var peak = coarsePeaks[i];
            int r = step > 1 ? 2 : 0;
            double localBest = -1;
            int bestX = peak.X, bestY = peak.Y;

            for (int dy = -r; dy <= r; dy++)
            {
                int ny = peak.Y + dy;
                if (ny < 0 || ny > maxY) continue;
                for (int dx = -r; dx <= r; dx++)
                {
                    int nx = peak.X + dx;
                    if (nx < 0 || nx > maxX) continue;
                    double s = (dx == 0 && dy == 0) ? peak.Score : ScoreEdgesAt(gx, gy, mag, width, edgeSamples, nx, ny);
                    if (s > localBest)
                    {
                        localBest = s;
                        bestX = nx;
                        bestY = ny;
                    }
                }
            }

            if (!checkedSpots.Add((bestX, bestY))) continue;
            if (localBest > bestScore)
            {
                bestScore = localBest;
                bestBounds = new ScreenRect(bestX, bestY, tw, th);
            }

            if (localBest >= options.MinScore)
            {
                matches.Add(new VisualMatch(new ScreenRect(bestX, bestY, tw, th), Math.Min(1.0, localBest)));
            }
        }

        var kept = TemplateMatcher.KeepBest(matches, options.MaxResults);
        return new TemplateMatchResult(kept, Math.Min(1.0, bestScore), 0, step)
        {
            BestBounds = kept.Count > 0 ? kept[0].Bounds : bestBounds,
        };
    }

    /// <summary>
    /// Rotates <paramref name="template"/> by <paramref name="angleDegrees"/> around its center, filling exposed corners
    /// with the template's border average so rotated templates can be searched cleanly.
    /// </summary>
    public static BgraImage Rotate(BgraImage template, double angleDegrees)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (Math.Abs(angleDegrees) < 0.1) return template.Clone();

        int w = template.Width, h = template.Height;
        double rad = -angleDegrees * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        double cx = (w - 1) * 0.5, cy = (h - 1) * 0.5;

        // Compute average border pixel for out-of-bounds corners.
        long br = 0, bg = 0, bb = 0, count = 0;
        var src = template.Pixels;
        for (int x = 0; x < w; x++)
        {
            int top = x * 4, bot = ((h - 1) * w + x) * 4;
            bb += src[top] + src[bot];
            bg += src[top + 1] + src[bot + 1];
            br += src[top + 2] + src[bot + 2];
            count += 2;
        }

        byte fillB = (byte)(bb / Math.Max(1, count));
        byte fillG = (byte)(bg / Math.Max(1, count));
        byte fillR = (byte)(br / Math.Max(1, count));

        var dst = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            double dy = y - cy;
            for (int x = 0; x < w; x++)
            {
                double dx = x - cx;
                double sx = cos * dx - sin * dy + cx;
                double sy = sin * dx + cos * dy + cy;
                int dstIdx = (y * w + x) * 4;

                int x0 = (int)Math.Floor(sx);
                int y0 = (int)Math.Floor(sy);
                if (x0 < 0 || y0 < 0 || x0 >= w - 1 || y0 >= h - 1)
                {
                    int nx = (int)Math.Round(sx), ny = (int)Math.Round(sy);
                    if (nx >= 0 && ny >= 0 && nx < w && ny < h)
                    {
                        int sIdx = (ny * w + nx) * 4;
                        dst[dstIdx] = src[sIdx];
                        dst[dstIdx + 1] = src[sIdx + 1];
                        dst[dstIdx + 2] = src[sIdx + 2];
                    }
                    else
                    {
                        dst[dstIdx] = fillB;
                        dst[dstIdx + 1] = fillG;
                        dst[dstIdx + 2] = fillR;
                    }

                    dst[dstIdx + 3] = 255;
                    continue;
                }

                double fx = sx - x0, fy = sy - y0;
                double w00 = (1 - fx) * (1 - fy);
                double w10 = fx * (1 - fy);
                double w01 = (1 - fx) * fy;
                double w11 = fx * fy;

                int i00 = (y0 * w + x0) * 4;
                int i10 = i00 + 4;
                int i01 = i00 + w * 4;
                int i11 = i01 + 4;

                dst[dstIdx] = (byte)Math.Clamp((int)Math.Round(src[i00] * w00 + src[i10] * w10 + src[i01] * w01 + src[i11] * w11), 0, 255);
                dst[dstIdx + 1] = (byte)Math.Clamp((int)Math.Round(src[i00 + 1] * w00 + src[i10 + 1] * w10 + src[i01 + 1] * w01 + src[i11 + 1] * w11), 0, 255);
                dst[dstIdx + 2] = (byte)Math.Clamp((int)Math.Round(src[i00 + 2] * w00 + src[i10 + 2] * w10 + src[i01 + 2] * w01 + src[i11 + 2] * w11), 0, 255);
                dst[dstIdx + 3] = 255;
            }
        }

        return new BgraImage(w, h, dst);
    }

    private sealed record TemplateCell(
        int OffsetX,
        int OffsetY,
        int Width,
        int Height,
        BgraImage Crop,
        float[] ZeroMeanGray,
        double Norm,
        float MeanB,
        float MeanG,
        float MeanR);

    private readonly record struct EdgeSample(int X, int Y, float Ux, float Uy);

    private static List<TemplateCell> BuildCells(BgraImage template, int cols, int rows)
    {
        var list = new List<TemplateCell>(cols * rows);
        int tw = template.Width, th = template.Height;
        for (int r = 0; r < rows; r++)
        {
            int y0 = r * th / rows;
            int y1 = (r + 1) * th / rows;
            int ch = y1 - y0;
            if (ch < 3) continue;

            for (int c = 0; c < cols; c++)
            {
                int x0 = c * tw / cols;
                int x1 = (c + 1) * tw / cols;
                int cw = x1 - x0;
                if (cw < 3) continue;

                var crop = template.Crop(new ScreenRect(x0, y0, cw, ch));
                int n = cw * ch;
                var zm = new float[n];
                double sumG = 0, sumB = 0, sumGr = 0, sumR = 0;
                var p = crop.Pixels;
                for (int i = 0, k = 0; i < n; i++, k += 4)
                {
                    float g = TemplateMatcher.Gray(p, k);
                    zm[i] = g;
                    sumG += g;
                    sumB += p[k];
                    sumGr += p[k + 1];
                    sumR += p[k + 2];
                }

                float meanG = (float)(sumG / n);
                double normSq = 0;
                for (int i = 0; i < n; i++)
                {
                    zm[i] -= meanG;
                    normSq += zm[i] * zm[i];
                }

                list.Add(new TemplateCell(
                    x0, y0, cw, ch, crop, zm, Math.Sqrt(normSq),
                    (float)(sumB / n), (float)(sumGr / n), (float)(sumR / n)));
            }
        }

        return list;
    }

    private static IEnumerable<TemplateCell> SelectAnchorCells(List<TemplateCell> cells, int maxAnchors) =>
        cells.Where(c => c.Norm > 12.0)
            .OrderByDescending(c => c.Norm)
            .Take(maxAnchors);

    private static void AddNeighborhood(HashSet<(int X, int Y)> set, int cx, int cy, int maxX, int maxY, int radius)
    {
        for (int dy = -radius; dy <= radius; dy++)
        {
            int y = cy + dy;
            if (y < 0 || y > maxY) continue;
            for (int dx = -radius; dx <= radius; dx++)
            {
                int x = cx + dx;
                if (x >= 0 && x <= maxX) set.Add((x, y));
            }
        }
    }

    private static double ScoreCellAt(BgraImage image, TemplateCell cell, int startX, int startY)
    {
        int cw = cell.Width, ch = cell.Height;
        int n = cw * ch;
        var imgPixels = image.Pixels;
        var tplPixels = cell.Crop.Pixels;
        int imgStride = image.Width * 4;
        int tplStride = cw * 4;

        double sumImg = 0, sumImgSq = 0, dot = 0, diffSum = 0;
        var zm = cell.ZeroMeanGray;

        for (int y = 0; y < ch; y++)
        {
            int iRow = (startY + y) * imgStride + startX * 4;
            int tRow = y * tplStride;
            int zRow = y * cw;
            for (int x = 0; x < cw; x++)
            {
                int ii = iRow + x * 4;
                int ti = tRow + x * 4;
                float g = TemplateMatcher.Gray(imgPixels, ii);
                sumImg += g;
                sumImgSq += g * g;
                dot += g * zm[zRow + x];

                diffSum += Math.Abs(imgPixels[ii] - tplPixels[ti])
                         + Math.Abs(imgPixels[ii + 1] - tplPixels[ti + 1])
                         + Math.Abs(imgPixels[ii + 2] - tplPixels[ti + 2]);
            }
        }

        double direct = 1.0 - diffSum / (n * 3.0 * 255.0);
        double imgVar = sumImgSq - (sumImg * sumImg) / n;
        double ncc = (cell.Norm > 1e-4 && imgVar > 1e-4)
            ? dot / (cell.Norm * Math.Sqrt(imgVar))
            : 0.0;
        return TemplateMatcher.CombinedScore(ncc, direct);
    }

    private static bool CellSameColor(BgraImage image, TemplateCell cell, int startX, int startY, int maxDiff, bool adaptToLighting)
    {
        int cw = cell.Width, ch = cell.Height, n = cw * ch;
        var p = image.Pixels;
        int stride = image.Width * 4;
        long sumB = 0, sumG = 0, sumR = 0;
        for (int y = 0; y < ch; y++)
        {
            int row = (startY + y) * stride + startX * 4;
            for (int x = 0; x < cw; x++)
            {
                int i = row + x * 4;
                sumB += p[i];
                sumG += p[i + 1];
                sumR += p[i + 2];
            }
        }

        float mb = (float)sumB / n, mg = (float)sumG / n, mr = (float)sumR / n;
        if (Math.Abs(mb - cell.MeanB) <= maxDiff && Math.Abs(mg - cell.MeanG) <= maxDiff && Math.Abs(mr - cell.MeanR) <= maxDiff)
            return true;

        if (!adaptToLighting) return false;
        float tSum = cell.MeanB + cell.MeanG + cell.MeanR;
        float iSum = mb + mg + mr;
        if (tSum < 30f || iSum < 30f) return false;
        float scale = tSum / iSum;
        if (scale < 0.45f || scale > 2.2f) return false;
        float chromaTol = maxDiff * 0.85f;
        return Math.Abs(mb * scale - cell.MeanB) <= chromaTol
            && Math.Abs(mg * scale - cell.MeanG) <= chromaTol
            && Math.Abs(mr * scale - cell.MeanR) <= chromaTol;
    }

    private static List<EdgeSample> ExtractTemplateEdgeSamples(BgraImage template)
    {
        ComputeImageGradients(template, out var gx, out var gy, out var mag);
        int w = template.Width, h = template.Height;

        float maxMag = 0;
        for (int i = 0; i < mag.Length; i++)
            if (mag[i] > maxMag) maxMag = mag[i];

        var samples = new List<EdgeSample>();
        if (maxMag < 12f) return samples;
        float threshold = Math.Max(14f, maxMag * 0.22f);

        for (int y = 1; y < h - 1; y++)
        {
            for (int x = 1; x < w - 1; x++)
            {
                int idx = y * w + x;
                float m = mag[idx];
                if (m < threshold) continue;
                samples.Add(new EdgeSample(x, y, gx[idx] / m, gy[idx] / m));
            }
        }

        // Subsample if there are more than 250 edge points so full-screen edge scans stay under a few milliseconds.
        if (samples.Count > 250)
        {
            int stride = samples.Count / 200;
            var reduced = new List<EdgeSample>(220);
            for (int i = 0; i < samples.Count; i += stride)
                reduced.Add(samples[i]);
            return reduced;
        }

        return samples;
    }

    private static void ComputeImageGradients(BgraImage image, out float[] gx, out float[] gy, out float[] mag)
    {
        int w = image.Width, h = image.Height;
        var gray = new float[w * h];
        var p = image.Pixels;
        for (int i = 0, k = 0; i < gray.Length; i++, k += 4)
            gray[i] = TemplateMatcher.Gray(p, k);

        gx = new float[w * h];
        gy = new float[w * h];
        mag = new float[w * h];

        for (int y = 1; y < h - 1; y++)
        {
            int row = y * w;
            for (int x = 1; x < w - 1; x++)
            {
                int i = row + x;
                float dx = (gray[i - w + 1] + 2f * gray[i + 1] + gray[i + w + 1])
                         - (gray[i - w - 1] + 2f * gray[i - 1] + gray[i + w - 1]);
                float dy = (gray[i + w - 1] + 2f * gray[i + w] + gray[i + w + 1])
                         - (gray[i - w - 1] + 2f * gray[i - w] + gray[i - w + 1]);
                gx[i] = dx;
                gy[i] = dy;
                mag[i] = MathF.Sqrt(dx * dx + dy * dy);
            }
        }
    }

    private static double ScoreEdgesAt(float[] gx, float[] gy, float[] mag, int imgWidth, List<EdgeSample> samples, int ox, int oy)
    {
        double sum = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            int idx = (oy + s.Y) * imgWidth + (ox + s.X);
            float m = mag[idx];
            if (m < 10f) continue;
            // Absolute cosine similarity between template edge normal and image edge normal (polarity-invariant).
            float dot = Math.Abs(gx[idx] * s.Ux + gy[idx] * s.Uy) / m;
            sum += dot;
        }

        return sum / samples.Count;
    }
}
