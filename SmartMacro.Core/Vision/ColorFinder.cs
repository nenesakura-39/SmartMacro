using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;

namespace SmartMacro.Core.Vision;

/// <summary>
/// One connected patch of matching pixels. Bounds are in picture pixels. CenterX / CenterY is the average
/// position of the patch's pixels (better than the middle of the box for odd shapes). PixelCount = size.
/// </summary>
public sealed record ColorBlob(ScreenRect Bounds, int PixelCount, double CenterX, double CenterY)
{
    /// <summary>Share of the bounding box filled by matching pixels (0..1).</summary>
    public double FillRatio => Bounds.IsEmpty ? 0 : Math.Clamp((double)PixelCount / Math.Max(1, Bounds.Width * Bounds.Height), 0, 1);

    /// <summary>
    /// Compactness / circularity estimate (0..1): combines fill density and aspect ratio (<c>min(W,H)/max(W,H)</c>).
    /// A solid circle or compact square scores high (~0.75–1.0); a long thin bar or sparse particle streak scores low (&lt; 0.25).
    /// </summary>
    public double Circularity
    {
        get
        {
            if (Bounds.IsEmpty || Bounds.Width <= 0 || Bounds.Height <= 0) return 0;
            double aspect = (double)Math.Min(Bounds.Width, Bounds.Height) / Math.Max(Bounds.Width, Bounds.Height);
            return Math.Clamp(FillRatio * aspect * 1.2732395, 0, 1); // 4/pi ≈ 1.2732 so an ideal filled circle is ~1.0
        }
    }
}

/// <summary>Vision 2.0 extended options for <see cref="ColorFinder.FindObjects(BgraImage, RgbColor, RgbColor?, int, ColorSearchOptions, CancellationToken)"/>.</summary>
public sealed record ColorSearchOptions
{
    public int MinPixels { get; init; } = 1;
    public int MaxPixels { get; init; }
    public int MaxResults { get; init; } = 20;
    public bool MatchShades { get; init; }
    public ColorSpaceKind ColorSpace { get; init; } = ColorSpaceKind.Rgb;
    public ColorSelectRule SelectRule { get; init; } = ColorSelectRule.Largest;
    public int MinCircularityPercent { get; init; }
    public int MinFillPercent { get; init; }
    public (int X, int Y)? ReferencePoint { get; init; }
}

/// <summary>
/// Result of a colour search: the patches found (biggest first, at most the requested number) and how many pixels
/// matched in total (also counting patches that were too small).
/// </summary>
public sealed record ColorSearchResult(IReadOnlyList<ColorBlob> Blobs, int MatchingPixels)
{
    public ColorBlob? Largest => Blobs.Count > 0 ? Blobs[0] : null;
}

/// <summary>
/// Looks for pixels close to a colour and groups touching ones into patches ("blobs").
/// This is simple colour matching, NOT object recognition: anything else on screen with a similar colour
/// (effects, backgrounds, other UI) also matches, so pick a small search area and a distinctive colour.
/// </summary>
public static class ColorFinder
{
    /// <summary>Stops collecting after this many patches (a noisy screen could have huge numbers).</summary>
    public const int MaxBlobsTracked = 20_000;

    /// <param name="tolerance">Allowed difference per red / green / blue channel (0 = exact colour, 255 = anything).</param>
    /// <param name="minPixels">Patches smaller than this are ignored (removes single stray pixels).</param>
    /// <param name="maxResults">How many of the biggest patches to return.</param>
    public static ColorSearchResult Find(BgraImage image, RgbColor color, int tolerance, int minPixels = 1, int maxResults = 20,
        CancellationToken cancellationToken = default) =>
        Find(image, color, null, tolerance, minPixels, maxResults, cancellationToken);

    public static ColorSearchResult Find(BgraImage image, RgbColor color, RgbColor? secondaryColor, int tolerance, int minPixels = 1, int maxResults = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        tolerance = Math.Clamp(tolerance, 0, 255);
        minPixels = Math.Max(1, minPixels);
        maxResults = Math.Max(1, maxResults);

        int width = image.Width, height = image.Height;
        var p = image.Pixels;
        var hasSecondary = secondaryColor is not null;
        int sr = secondaryColor?.R ?? 0, sg = secondaryColor?.G ?? 0, sb = secondaryColor?.B ?? 0;

        // 0 = no match, 1 = match not visited yet, 2 = visited.
        var mask = new byte[width * height];
        var matching = 0;
        int j = 0, i = 0;
        if (Avx2.IsSupported && mask.Length >= 8)
        {
            var rgbMask = Vector256.Create(0x00FFFFFFu).AsByte();
            var tolVec = Vector256.Create((byte)tolerance);
            var target1 = Vector256.Create((uint)(color.B | (color.G << 8) | (color.R << 16))).AsByte();
            var target2 = hasSecondary ? Vector256.Create((uint)(sb | (sg << 8) | (sr << 16))).AsByte() : default;

            for (; j <= mask.Length - 8; j += 8, i += 32)
            {
                var vp = Vector256.Create<byte>(p.AsSpan(i, 32));
                var diff1 = Avx2.And(Avx2.Max(Avx2.SubtractSaturate(vp, target1), Avx2.SubtractSaturate(target1, vp)), rgbMask);
                var ex1 = Avx2.SubtractSaturate(diff1, tolVec);
                var hitVec = Avx2.CompareEqual(ex1.AsUInt32(), Vector256<uint>.Zero);

                if (hasSecondary)
                {
                    var diff2 = Avx2.And(Avx2.Max(Avx2.SubtractSaturate(vp, target2), Avx2.SubtractSaturate(target2, vp)), rgbMask);
                    var ex2 = Avx2.SubtractSaturate(diff2, tolVec);
                    hitVec = Avx2.Or(hitVec, Avx2.CompareEqual(ex2.AsUInt32(), Vector256<uint>.Zero));
                }

                uint hitMask = (uint)Avx2.MoveMask(hitVec.AsSingle());
                while (hitMask != 0)
                {
                    int k = BitOperations.TrailingZeroCount(hitMask);
                    mask[j + k] = 1;
                    matching++;
                    hitMask &= hitMask - 1;
                }
            }
        }

        if (Sse2.IsSupported && j <= mask.Length - 4)
        {
            var rgbMask128 = Vector128.Create(0x00FFFFFFu).AsByte();
            var tolVec128 = Vector128.Create((byte)tolerance);
            var target1_128 = Vector128.Create((uint)(color.B | (color.G << 8) | (color.R << 16))).AsByte();
            var target2_128 = hasSecondary ? Vector128.Create((uint)(sb | (sg << 8) | (sr << 16))).AsByte() : default;

            for (; j <= mask.Length - 4; j += 4, i += 16)
            {
                var vp = Vector128.Create<byte>(p.AsSpan(i, 16));
                var diff1 = Sse2.And(Sse2.Max(Sse2.SubtractSaturate(vp, target1_128), Sse2.SubtractSaturate(target1_128, vp)), rgbMask128);
                var ex1 = Sse2.SubtractSaturate(diff1, tolVec128);
                var hitVec = Sse2.CompareEqual(ex1.AsUInt32(), Vector128<uint>.Zero);

                if (hasSecondary)
                {
                    var diff2 = Sse2.And(Sse2.Max(Sse2.SubtractSaturate(vp, target2_128), Sse2.SubtractSaturate(target2_128, vp)), rgbMask128);
                    var ex2 = Sse2.SubtractSaturate(diff2, tolVec128);
                    hitVec = Sse2.Or(hitVec, Sse2.CompareEqual(ex2.AsUInt32(), Vector128<uint>.Zero));
                }

                uint hitMask = (uint)Sse2.MoveMask(hitVec.AsSingle());
                while (hitMask != 0)
                {
                    int k = BitOperations.TrailingZeroCount(hitMask);
                    mask[j + k] = 1;
                    matching++;
                    hitMask &= hitMask - 1;
                }
            }
        }

        for (; j < mask.Length; j++, i += 4)
        {
            if ((Math.Abs(p[i] - color.B) <= tolerance && Math.Abs(p[i + 1] - color.G) <= tolerance && Math.Abs(p[i + 2] - color.R) <= tolerance)
                || (hasSecondary && Math.Abs(p[i] - sb) <= tolerance && Math.Abs(p[i + 1] - sg) <= tolerance && Math.Abs(p[i + 2] - sr) <= tolerance))
            {
                mask[j] = 1;
                matching++;
            }
        }

        var blobs = new List<ColorBlob>();
        if (matching == 0) return new ColorSearchResult(blobs, 0);

        var stack = new Stack<int>();
        for (var start = 0; start < mask.Length && blobs.Count < MaxBlobsTracked; start++)
        {
            if (mask[start] != 1) continue;
            if (start % width == 0) cancellationToken.ThrowIfCancellationRequested();

            // Flood fill (4-neighbours) without recursion.
            int count = 0, minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            long sumX = 0, sumY = 0;
            mask[start] = 2;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var index = stack.Pop();
                int x = index % width, y = index / width;
                count++;
                sumX += x;
                sumY += y;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;

                if (x > 0 && mask[index - 1] == 1) { mask[index - 1] = 2; stack.Push(index - 1); }
                if (x < width - 1 && mask[index + 1] == 1) { mask[index + 1] = 2; stack.Push(index + 1); }
                if (y > 0 && mask[index - width] == 1) { mask[index - width] = 2; stack.Push(index - width); }
                if (y < height - 1 && mask[index + width] == 1) { mask[index + width] = 2; stack.Push(index + width); }
            }

            if (count >= minPixels)
                blobs.Add(new ColorBlob(new ScreenRect(minX, minY, maxX - minX + 1, maxY - minY + 1), count,
                    sumX / (double)count, sumY / (double)count));
        }

        blobs.Sort((a, b) => b.PixelCount.CompareTo(a.PixelCount));
        if (blobs.Count > maxResults) blobs.RemoveRange(maxResults, blobs.Count - maxResults);
        return new ColorSearchResult(blobs, matching);
    }

    /// <summary>Darkest / brightest shade (relative brightness) that still counts for "shade variation".</summary>
    public const double MinShadeBrightness = 0.5;

    /// <inheritdoc cref="MinShadeBrightness"/>
    public const double MaxShadeBrightness = 2.0;

    /// <summary>
    /// Is the pixel (b, g, r) a darker / lighter shade of the colour (cb, cg, cr)? Scaled to the colour's brightness it
    /// must be within the tolerance on every channel, and its brightness must be 50 %–200 % of the colour's.
    /// </summary>
    internal static bool IsShadeOf(int b, int g, int r, int cb, int cg, int cr, int tolerance)
    {
        var pixel = b + g + r;
        var target = cb + cg + cr;
        if (pixel < 24 || target < 24) return false; // (almost) black has no colour to compare
        var k = target / (double)pixel;              // brings the pixel to the colour's brightness
        if (k < 1 / MaxShadeBrightness || k > 1 / MinShadeBrightness) return false;
        return Math.Abs(b * k - cb) <= tolerance && Math.Abs(g * k - cg) <= tolerance && Math.Abs(r * k - cr) <= tolerance;
    }

    /// <summary>Cell size (px) of <see cref="FindObjects"/>' grid clustering.</summary>
    public const int ClusterCellSize = 8;

    /// <summary>
    /// Like <see cref="Find(BgraImage, RgbColor, RgbColor?, int, int, int, CancellationToken)"/>, but groups NEARBY matching
    /// pixels into one object even when they don't touch: a shaded / glowing / anti-aliased ball often matches as many
    /// small speckles (only some of its shades are within the tolerance), and picking the biggest speckle clicked its
    /// edge. Here the picture is divided into <see cref="ClusterCellSize"/>×<see cref="ClusterCellSize"/> px cells; cells
    /// with a matching pixel that touch (also diagonally) form one cluster. Each result's Bounds is the box around its
    /// matching pixels, PixelCount their number and CenterX / CenterY their centre of mass – so the biggest object is
    /// clicked dead centre, and a single stray pixel elsewhere never wins over it. Biggest first.
    /// </summary>
    /// <param name="matchShades">
    /// "Shade variation": a pixel also counts when it is a darker / lighter shade of the colour – brought to the colour's
    /// brightness (it may be 50 %–200 % as bright) it is within the tolerance. The shadowed and highlighted sides of a 3D
    /// ball then belong to the ball. White, grey and other hues still don't match.
    /// </param>
    public static ColorSearchResult FindObjects(BgraImage image, RgbColor color, RgbColor? secondaryColor, int tolerance, int minPixels = 1,
        int maxResults = 20, CancellationToken cancellationToken = default, bool matchShades = false) =>
        FindObjects(image, color, secondaryColor, tolerance, new ColorSearchOptions
        {
            MinPixels = minPixels,
            MaxResults = maxResults,
            MatchShades = matchShades,
        }, cancellationToken);

    /// <summary>
    /// Vision 2.0 colour object finder: supports RGB, HSV, and perceptual CIE L*a*b* colour spaces, min/max pixel size,
    /// circularity &amp; fill density filters (to reject ragged particle sprays), and target selection rules.
    /// </summary>
    public static ColorSearchResult FindObjects(BgraImage image, RgbColor color, RgbColor? secondaryColor, int tolerance,
        ColorSearchOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        options ??= new ColorSearchOptions();
        tolerance = Math.Clamp(tolerance, 0, 255);
        var minPixels = Math.Max(1, options.MinPixels);
        var maxPixels = options.MaxPixels > 0 ? Math.Max(minPixels, options.MaxPixels) : 0;
        var maxResults = Math.Max(1, options.MaxResults);
        var matchShades = options.MatchShades;
        var colorSpace = options.ColorSpace;

        int width = image.Width, height = image.Height;
        var p = image.Pixels;
        var hasSecondary = secondaryColor is not null;
        int sr = secondaryColor?.R ?? 0, sg = secondaryColor?.G ?? 0, sb = secondaryColor?.B ?? 0;

        RgbToHsv(color.R, color.G, color.B, out var th1, out var ts1, out var tv1);
        RgbToHsv((byte)sr, (byte)sg, (byte)sb, out var th2, out var ts2, out var tv2);
        RgbToLab(color.R, color.G, color.B, out var tl1, out var ta1, out var tb1);
        RgbToLab((byte)sr, (byte)sg, (byte)sb, out var tl2, out var ta2, out var tb2);

        const int cell = ClusterCellSize;
        int cw = (width + cell - 1) / cell, ch = (height + cell - 1) / cell;

        // Per cell: matching pixel count, coordinate sums and the box around them.
        var count = new int[cw * ch];
        var sumX = new long[cw * ch];
        var sumY = new long[cw * ch];
        var minX = new int[cw * ch];
        var minY = new int[cw * ch];
        var maxX = new int[cw * ch];
        var maxY = new int[cw * ch];
        var matching = 0;
        bool useAvx2Rgb = Avx2.IsSupported && colorSpace == ColorSpaceKind.Rgb && !matchShades && width >= 8;
        var rgbMaskObj = useAvx2Rgb ? Vector256.Create(0x00FFFFFFu).AsByte() : default;
        var tolVecObj = useAvx2Rgb ? Vector256.Create((byte)tolerance) : default;
        var target1Obj = useAvx2Rgb ? Vector256.Create((uint)(color.B | (color.G << 8) | (color.R << 16))).AsByte() : default;
        var target2Obj = useAvx2Rgb && hasSecondary ? Vector256.Create((uint)(sb | (sg << 8) | (sr << 16))).AsByte() : default;

        for (var y = 0; y < height; y++)
        {
            if ((y & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
            var row = y * width * 4;
            var cellRow = y / cell * cw;
            var x = 0;

            if (useAvx2Rgb)
            {
                for (; x <= width - 8; x += 8)
                {
                    var i = row + x * 4;
                    var vp = Vector256.Create<byte>(p.AsSpan(i, 32));
                    var diff1 = Avx2.And(Avx2.Max(Avx2.SubtractSaturate(vp, target1Obj), Avx2.SubtractSaturate(target1Obj, vp)), rgbMaskObj);
                    var ex1 = Avx2.SubtractSaturate(diff1, tolVecObj);
                    var hitVec = Avx2.CompareEqual(ex1.AsUInt32(), Vector256<uint>.Zero);

                    if (hasSecondary)
                    {
                        var diff2 = Avx2.And(Avx2.Max(Avx2.SubtractSaturate(vp, target2Obj), Avx2.SubtractSaturate(target2Obj, vp)), rgbMaskObj);
                        var ex2 = Avx2.SubtractSaturate(diff2, tolVecObj);
                        hitVec = Avx2.Or(hitVec, Avx2.CompareEqual(ex2.AsUInt32(), Vector256<uint>.Zero));
                    }

                    uint hitMask = (uint)Avx2.MoveMask(hitVec.AsSingle());
                    while (hitMask != 0)
                    {
                        int k = BitOperations.TrailingZeroCount(hitMask);
                        int px = x + k;
                        matching++;
                        var c = cellRow + px / cell;
                        if (count[c] == 0)
                        {
                            minX[c] = maxX[c] = px;
                            minY[c] = maxY[c] = y;
                        }
                        else
                        {
                            if (px < minX[c]) minX[c] = px;
                            if (px > maxX[c]) maxX[c] = px;
                            if (y > maxY[c]) maxY[c] = y;
                        }

                        count[c]++;
                        sumX[c] += px;
                        sumY[c] += y;
                        hitMask &= hitMask - 1;
                    }
                }
            }

            for (; x < width; x++)
            {
                var i = row + x * 4;
                byte pb = p[i], pg = p[i + 1], pr = p[i + 2];
                bool hit = colorSpace switch
                {
                    ColorSpaceKind.Hsv => MatchesHsv(pr, pg, pb, th1, ts1, tv1, tolerance, matchShades)
                                          || (hasSecondary && MatchesHsv(pr, pg, pb, th2, ts2, tv2, tolerance, matchShades)),
                    ColorSpaceKind.LabDeltaE => MatchesLab(pr, pg, pb, tl1, ta1, tb1, tolerance, matchShades)
                                                || (hasSecondary && MatchesLab(pr, pg, pb, tl2, ta2, tb2, tolerance, matchShades)),
                    _ => (Math.Abs(pb - color.B) <= tolerance && Math.Abs(pg - color.G) <= tolerance && Math.Abs(pr - color.R) <= tolerance)
                         || (hasSecondary && Math.Abs(pb - sb) <= tolerance && Math.Abs(pg - sg) <= tolerance && Math.Abs(pr - sr) <= tolerance)
                         || (matchShades && (IsShadeOf(pb, pg, pr, color.B, color.G, color.R, tolerance)
                                             || (hasSecondary && IsShadeOf(pb, pg, pr, sb, sg, sr, tolerance)))),
                };
                if (!hit) continue;

                matching++;
                var c = cellRow + x / cell;
                if (count[c] == 0)
                {
                    minX[c] = maxX[c] = x;
                    minY[c] = maxY[c] = y;
                }
                else
                {
                    if (x < minX[c]) minX[c] = x;
                    if (x > maxX[c]) maxX[c] = x;
                    if (y > maxY[c]) maxY[c] = y; // rows are visited top to bottom, so minY is the first one
                }

                count[c]++;
                sumX[c] += x;
                sumY[c] += y;
            }
        }

        var clusters = new List<ColorBlob>();
        if (matching == 0) return new ColorSearchResult(clusters, 0);

        double minCirc = Math.Clamp(options.MinCircularityPercent / 100.0, 0, 1);
        double minFill = Math.Clamp(options.MinFillPercent / 100.0, 0, 1);

        // Join touching occupied cells (8 neighbours) without recursion.
        var visited = new bool[cw * ch];
        var queue = new int[cw * ch];
        for (var start = 0; start < count.Length; start++)
        {
            if (count[start] == 0 || visited[start]) continue;
            int head = 0, tail = 0, pixels = 0, left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
            long sx = 0, sy = 0;
            visited[start] = true;
            queue[tail++] = start;
            while (head < tail)
            {
                var c = queue[head++];
                pixels += count[c];
                sx += sumX[c];
                sy += sumY[c];
                left = Math.Min(left, minX[c]);
                top = Math.Min(top, minY[c]);
                right = Math.Max(right, maxX[c]);
                bottom = Math.Max(bottom, maxY[c]);
                int cx = c % cw, cy = c / cw;
                for (var dy = -1; dy <= 1; dy++)
                {
                    var ny = cy + dy;
                    if (ny < 0 || ny >= ch) continue;
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var nx = cx + dx;
                        if ((dx == 0 && dy == 0) || nx < 0 || nx >= cw) continue;
                        var n = ny * cw + nx;
                        if (count[n] == 0 || visited[n]) continue;
                        visited[n] = true;
                        queue[tail++] = n;
                    }
                }
            }

            if (pixels < minPixels) continue;
            if (maxPixels > 0 && pixels > maxPixels) continue;

            var blob = new ColorBlob(new ScreenRect(left, top, right - left + 1, bottom - top + 1), pixels, sx / (double)pixels, sy / (double)pixels);
            if (minFill > 0 && blob.FillRatio + 1e-6 < minFill) continue;
            if (minCirc > 0 && blob.Circularity + 1e-6 < minCirc) continue;
            clusters.Add(blob);
        }

        SortBlobs(clusters, options.SelectRule, options.ReferencePoint ?? (width / 2, height / 2));
        if (clusters.Count > maxResults) clusters.RemoveRange(maxResults, clusters.Count - maxResults);
        return new ColorSearchResult(clusters, matching);
    }

    private static void SortBlobs(List<ColorBlob> blobs, ColorSelectRule rule, (int X, int Y) refPt)
    {
        switch (rule)
        {
            case ColorSelectRule.NearestToCenter:
            case ColorSelectRule.NearestToLastSpot:
                blobs.Sort((a, b) =>
                {
                    double da = (a.CenterX - refPt.X) * (a.CenterX - refPt.X) + (a.CenterY - refPt.Y) * (a.CenterY - refPt.Y);
                    double db = (b.CenterX - refPt.X) * (b.CenterX - refPt.X) + (b.CenterY - refPt.Y) * (b.CenterY - refPt.Y);
                    int cmp = da.CompareTo(db);
                    return cmp != 0 ? cmp : b.PixelCount.CompareTo(a.PixelCount);
                });
                break;

            case ColorSelectRule.TopMost:
                blobs.Sort((a, b) =>
                {
                    int cmp = a.Bounds.Y.CompareTo(b.Bounds.Y);
                    return cmp != 0 ? cmp : a.Bounds.X.CompareTo(b.Bounds.X);
                });
                break;

            default:
                blobs.Sort((a, b) => b.PixelCount.CompareTo(a.PixelCount));
                break;
        }
    }

    internal static void RgbToHsv(byte r, byte g, byte b, out float h, out float s, out float v)
    {
        float rf = r / 255f, gf = g / 255f, bf = b / 255f;
        float max = Math.Max(rf, Math.Max(gf, bf));
        float min = Math.Min(rf, Math.Min(gf, bf));
        float delta = max - min;
        v = max * 255f;
        s = max <= 1e-5f ? 0f : (delta / max) * 255f;
        if (delta <= 1e-5f) { h = 0f; return; }
        if (max == rf) h = 60f * (((gf - bf) / delta) % 6f);
        else if (max == gf) h = 60f * (((bf - rf) / delta) + 2f);
        else h = 60f * (((rf - gf) / delta) + 4f);
        if (h < 0f) h += 360f;
    }

    private static bool MatchesHsv(byte r, byte g, byte b, float th, float ts, float tv, int tolerance, bool matchShades)
    {
        RgbToHsv(r, g, b, out float h, out float s, out float v);
        // For near-achromatic colours (white/grey/black), compare value and saturation directly.
        if (ts < 25f)
            return Math.Abs(s - ts) <= tolerance + 15 && Math.Abs(v - tv) <= (matchShades ? tolerance * 2f : tolerance);

        float hueDiff = Math.Abs(h - th);
        if (hueDiff > 180f) hueDiff = 360f - hueDiff;
        float allowedHue = Math.Max(4f, tolerance * 0.7f);
        float allowedSat = matchShades ? Math.Max(35f, tolerance * 2.2f) : Math.Max(15f, tolerance * 1.4f);
        float allowedVal = matchShades ? Math.Max(65f, tolerance * 3.0f) : Math.Max(15f, tolerance * 1.4f);
        return hueDiff <= allowedHue && Math.Abs(s - ts) <= allowedSat && Math.Abs(v - tv) <= allowedVal;
    }

    internal static void RgbToLab(byte r, byte g, byte b, out float l, out float a, out float bLab)
    {
        // Fast sRGB -> CIE L*a*b* approximation for perceptual Delta-E comparisons.
        float rf = r / 255f, gf = g / 255f, bf = b / 255f;
        float x = 0.4124f * rf + 0.3576f * gf + 0.1805f * bf;
        float y = 0.2126f * rf + 0.7152f * gf + 0.0722f * bf;
        float z = 0.0193f * rf + 0.1192f * gf + 0.9505f * bf;
        float fx = LabF(x / 0.95047f), fy = LabF(y), fz = LabF(z / 1.08883f);
        l = 116f * fy - 16f;
        a = 500f * (fx - fy);
        bLab = 200f * (fy - fz);

        static float LabF(float t) => t > 0.008856f ? MathF.Cbrt(t) : 7.787f * t + 16f / 116f;
    }

    private static bool MatchesLab(byte r, byte g, byte b, float tl, float ta, float tb, int tolerance, bool matchShades)
    {
        RgbToLab(r, g, b, out float l, out float a, out float bLab);
        float dl = (l - tl) * (matchShades ? 0.45f : 1.0f);
        float da = a - ta;
        float db = bLab - tb;
        float deltaE = MathF.Sqrt(dl * dl + da * da + db * db);
        return deltaE <= tolerance * 0.65f + 2f;
    }
}
