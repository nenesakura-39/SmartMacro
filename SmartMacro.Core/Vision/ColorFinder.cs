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
}
