using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
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
    /// Uses AVX2 256-bit SIMD fast-skip (8 BGRA pixels per instruction) when supported on the CPU.
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

        bool useAvx2 = Avx2.IsSupported && overlap.Width >= 8;
        bool useSse2 = Sse2.IsSupported && overlap.Width >= 4;
        Vector256<byte> rgbMask256 = useAvx2 ? Vector256.Create(0x00FFFFFFu).AsByte() : default;
        Vector256<byte> tolVec256 = useAvx2 ? Vector256.Create((byte)tolerance) : default;
        Vector128<byte> rgbMask128 = useSse2 ? Vector128.Create(0x00FFFFFFu).AsByte() : default;
        Vector128<byte> tolVec128 = useSse2 ? Vector128.Create((byte)tolerance) : default;

        for (var y = overlap.Y; y < overlap.Bottom; y++)
        {
            var rowA = ((y - before.Bounds.Y) * a.Width + (overlap.X - before.Bounds.X)) * 4;
            var rowB = ((y - after.Bounds.Y) * b.Width + (overlap.X - after.Bounds.X)) * 4;
            var i = 0;

            if (useAvx2)
            {
                for (; i <= overlap.Width - 8; i += 8)
                {
                    var ia = rowA + i * 4;
                    var ib = rowB + i * 4;
                    var va = Vector256.Create<byte>(pa.AsSpan(ia, 32));
                    var vb = Vector256.Create<byte>(pb.AsSpan(ib, 32));
                    var absDiff = Avx2.Max(Avx2.SubtractSaturate(va, vb), Avx2.SubtractSaturate(vb, va));
                    var rgbDiff = Avx2.And(absDiff, rgbMask256);
                    var exceed = Avx2.SubtractSaturate(rgbDiff, tolVec256);

                    // Fast path: all 8 pixels unchanged -> skip 8 pixels in a single branch.
                    if (Avx2.MoveMask(Avx2.CompareEqual(exceed, Vector256<byte>.Zero)) == -1)
                        continue;

                    for (var k = 0; k < 8; k++)
                    {
                        var pka = ia + k * 4;
                        var pkb = ib + k * 4;
                        if (Math.Abs(pa[pka] - pb[pkb]) <= tolerance
                            && Math.Abs(pa[pka + 1] - pb[pkb + 1]) <= tolerance
                            && Math.Abs(pa[pka + 2] - pb[pkb + 2]) <= tolerance)
                            continue;

                        var x = overlap.X + i + k;
                        changed++;
                        sumX += x;
                        sumY += y;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }

            if (useSse2)
            {
                for (; i <= overlap.Width - 4; i += 4)
                {
                    var ia = rowA + i * 4;
                    var ib = rowB + i * 4;
                    var va = Vector128.Create<byte>(pa.AsSpan(ia, 16));
                    var vb = Vector128.Create<byte>(pb.AsSpan(ib, 16));
                    var absDiff = Sse2.Max(Sse2.SubtractSaturate(va, vb), Sse2.SubtractSaturate(vb, va));
                    var rgbDiff = Sse2.And(absDiff, rgbMask128);
                    var exceed = Sse2.SubtractSaturate(rgbDiff, tolVec128);

                    // Fast path: all 4 pixels unchanged -> skip 4 pixels in a single branch.
                    if (Sse2.MoveMask(Sse2.CompareEqual(exceed, Vector128<byte>.Zero)) == 0xFFFF)
                        continue;

                    for (var k = 0; k < 4; k++)
                    {
                        var pka = ia + k * 4;
                        var pkb = ib + k * 4;
                        if (Math.Abs(pa[pka] - pb[pkb]) <= tolerance
                            && Math.Abs(pa[pka + 1] - pb[pkb + 1]) <= tolerance
                            && Math.Abs(pa[pka + 2] - pb[pkb + 2]) <= tolerance)
                            continue;

                        var x = overlap.X + i + k;
                        changed++;
                        sumX += x;
                        sumY += y;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }

            for (; i < overlap.Width; i++)
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
