using SmartMacro.Core.Ocr;

namespace SmartMacro.Core.Vision;

/// <summary>
/// The result of comparing two pictures of the same area: how many pixels changed, where (bounding box, screen
/// pixels) and the centre of the change (the average position of the changed pixels, so one big change wins over a
/// few stray pixels at the far edge).
/// </summary>
public readonly record struct ScreenChange(int ChangedPixels, int ComparedPixels, ScreenRect Bounds, int CenterX, int CenterY)
{
    /// <summary>Changed pixels as a share of the compared ones, 0–100.</summary>
    public double ChangedPercent => ComparedPixels == 0 ? 0 : ChangedPixels * 100.0 / ComparedPixels;

    /// <summary>True when at least one pixel changed.</summary>
    public bool Any => ChangedPixels > 0;
}

/// <summary>Finds what changed between two captures (used by the "Wait for screen change" step).</summary>
public static class ScreenChangeDetector
{
    /// <summary>
    /// Compares the part both frames cover (in screen coordinates). A pixel counts as changed when its red, green or
    /// blue value differs by more than <paramref name="tolerance"/> (0 = any difference). Alpha is ignored.
    /// </summary>
    public static ScreenChange Compare(ScreenFrame before, ScreenFrame after, int tolerance)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        tolerance = Math.Clamp(tolerance, 0, 255);

        var overlap = before.Bounds.Intersect(after.Bounds);
        if (overlap.IsEmpty) return default;

        var a = before.Image;
        var b = after.Image;
        var pa = a.Pixels;
        var pb = b.Pixels;
        long sumX = 0, sumY = 0;
        int changed = 0;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;

        for (var y = overlap.Y; y < overlap.Bottom; y++)
        {
            var rowA = ((y - before.Bounds.Y) * a.Width + (overlap.X - before.Bounds.X)) * 4;
            var rowB = ((y - after.Bounds.Y) * b.Width + (overlap.X - after.Bounds.X)) * 4;
            for (var i = 0; i < overlap.Width; i++)
            {
                var ia = rowA + i * 4;
                var ib = rowB + i * 4;
                if (Math.Abs(pa[ia] - pb[ib]) <= tolerance
                    && Math.Abs(pa[ia + 1] - pb[ib + 1]) <= tolerance
                    && Math.Abs(pa[ia + 2] - pb[ib + 2]) <= tolerance)
                    continue;

                var x = overlap.X + i;
                changed++;
                sumX += x;
                sumY += y;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }

        var compared = overlap.Width * overlap.Height;
        if (changed == 0) return new ScreenChange(0, compared, default, 0, 0);

        return new ScreenChange(changed, compared,
            new ScreenRect(minX, minY, maxX - minX + 1, maxY - minY + 1),
            (int)Math.Round((double)sumX / changed, MidpointRounding.AwayFromZero),
            (int)Math.Round((double)sumY / changed, MidpointRounding.AwayFromZero));
    }
}
