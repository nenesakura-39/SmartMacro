using SmartMacro.Core.Ocr;

namespace SmartMacro.Core.Vision;

/// <summary>
/// One connected patch of matching pixels. Bounds are in picture pixels. CenterX / CenterY is the average
/// position of the patch's pixels (better than the middle of the box for odd shapes). PixelCount = size.
/// </summary>
public sealed record ColorBlob(ScreenRect Bounds, int PixelCount, double CenterX, double CenterY);

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
        for (int j = 0, i = 0; j < mask.Length; j++, i += 4)
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
        int maxResults = 20, CancellationToken cancellationToken = default, bool matchShades = false)
    {
        ArgumentNullException.ThrowIfNull(image);
        tolerance = Math.Clamp(tolerance, 0, 255);
        minPixels = Math.Max(1, minPixels);
        maxResults = Math.Max(1, maxResults);

        int width = image.Width, height = image.Height;
        var p = image.Pixels;
        var hasSecondary = secondaryColor is not null;
        int sr = secondaryColor?.R ?? 0, sg = secondaryColor?.G ?? 0, sb = secondaryColor?.B ?? 0;
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
        for (var y = 0; y < height; y++)
        {
            if ((y & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
            var row = y * width * 4;
            var cellRow = y / cell * cw;
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                var hit = (Math.Abs(p[i] - color.B) <= tolerance && Math.Abs(p[i + 1] - color.G) <= tolerance && Math.Abs(p[i + 2] - color.R) <= tolerance)
                          || (hasSecondary && Math.Abs(p[i] - sb) <= tolerance && Math.Abs(p[i + 1] - sg) <= tolerance && Math.Abs(p[i + 2] - sr) <= tolerance)
                          || (matchShades && (IsShadeOf(p[i], p[i + 1], p[i + 2], color.B, color.G, color.R, tolerance)
                                              || (hasSecondary && IsShadeOf(p[i], p[i + 1], p[i + 2], sb, sg, sr, tolerance))));
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

            if (pixels >= minPixels)
                clusters.Add(new ColorBlob(new ScreenRect(left, top, right - left + 1, bottom - top + 1), pixels, sx / (double)pixels, sy / (double)pixels));
        }

        clusters.Sort((a, b) => b.PixelCount.CompareTo(a.PixelCount));
        if (clusters.Count > maxResults) clusters.RemoveRange(maxResults, clusters.Count - maxResults);
        return new ColorSearchResult(clusters, matching);
    }
}
