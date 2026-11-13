using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SmartMacro.Core.Vision;

/// <summary>
/// Cross-correlation of a grey map with a kernel (a template or a foreground mask) for every position where the
/// kernel fits completely, computed with a pooled SIMD 2D FFT. The cost depends only on the padded map size, not on
/// the kernel size, which makes big templates (grids, full panels) fast without downsampling away thin details.
///
/// Result[y * px + x] = Σ kernel[j, i] × map[y + j, x + i], with px = mapWidth − kernelWidth + 1 and
/// py = mapHeight − kernelHeight + 1. Buffers are rented from <see cref="ArrayPool{T}.Shared"/> to avoid LOH churn
/// during continuous macro vision polling.
/// Trick: the mean-centered map goes into the real part and the kernel into the imaginary part, so one forward FFT
/// computes both transforms, and centering the map removes huge DC dynamic-range loss in float32 arithmetic.
/// </summary>
internal static class FftCorrelator
{
    /// <summary>Above this many FFT points the rows / columns / spectrum split execute in parallel.</summary>
    private const int ParallelThreshold = 64 * 1024;

    private static readonly Vector256<int> Reverse8Mask = Vector256.Create(7, 6, 5, 4, 3, 2, 1, 0);

    public static float[] CorrelateValid(
        float[] map,
        int mapWidth,
        int mapHeight,
        float[] kernel,
        int kernelWidth,
        int kernelHeight,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(kernel);

        var px = mapWidth - kernelWidth + 1;
        var py = mapHeight - kernelHeight + 1;
        if (px <= 0 || py <= 0 || kernelWidth <= 0 || kernelHeight <= 0)
            return Array.Empty<float>();

        var nx = NextPowerOfTwo(mapWidth);
        var ny = NextPowerOfTwo(mapHeight);
        var size = nx * ny;

        var pool = ArrayPool<float>.Shared;
        var re = pool.Rent(size);
        var im = pool.Rent(size);
        var pr = pool.Rent(size);
        var pi = pool.Rent(size);

        try
        {
            // ArrayPool arrays can exceed 'size' and contain stale data; zero the working prefix.
            Array.Clear(re, 0, size);
            Array.Clear(im, 0, size);

            // Centering map around its mean keeps the DC bin near zero so float32 FFT avoids catastrophic cancellation
            // when correlating large non-zero-mean frames (e.g. 1920x1080). Adding (mapMean * kernelSum) at readback
            // restores the exact uncentered correlation value across all valid positions.
            var mapMean = ComputeMean(map, mapWidth, mapHeight);
            var kernelSum = ComputeSum(kernel, kernelWidth, kernelHeight);
            var bias = (float)(mapMean * kernelSum);
            var meanF = (float)mapMean;

            CopyCenteredMap(map, mapWidth, mapHeight, meanF, re, nx);
            CopyKernel(kernel, kernelWidth, kernelHeight, im, nx);

            cancellationToken.ThrowIfCancellationRequested();
            Transform2D(re, im, nx, ny, inverse: false, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            SplitAndMultiplyConjugate(re, im, pr, pi, nx, ny, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            Transform2D(pr, pi, nx, ny, inverse: true, cancellationToken);

            var invN = (float)(1.0 / ((double)nx * ny));
            var result = new float[px * py];
            ExtractScaledResult(pr, nx, invN, bias, result, px, py);
            return result;
        }
        finally
        {
            pool.Return(re);
            pool.Return(im);
            pool.Return(pr);
            pool.Return(pi);
        }
    }

    internal static int NextPowerOfTwo(int value)
    {
        var n = 1;
        while (n < value) n <<= 1;
        return n;
    }

    private static double ComputeMean(float[] map, int width, int height)
    {
        var total = width * height;
        if (total <= 0) return 0;

        var sum = SumSpan(map.AsSpan(0, total));
        return sum / total;
    }

    private static double ComputeSum(float[] kernel, int width, int height)
    {
        var total = width * height;
        return total <= 0 ? 0 : SumSpan(kernel.AsSpan(0, total));
    }

    private static double SumSpan(ReadOnlySpan<float> values)
    {
        double sum = 0;
        var i = 0;

        if (Avx2.IsSupported && values.Length >= 8)
        {
            var acc0 = Vector256<float>.Zero;
            var acc1 = Vector256<float>.Zero;
            ref var baseRef = ref MemoryMarshal.GetReference(values);
            var limit = values.Length - 8;

            while (i <= values.Length - 16)
            {
                acc0 = Avx.Add(acc0, Vector256.LoadUnsafe(ref baseRef, (nuint)i));
                acc1 = Avx.Add(acc1, Vector256.LoadUnsafe(ref baseRef, (nuint)(i + 8)));
                i += 16;
            }

            if (i <= limit)
            {
                acc0 = Avx.Add(acc0, Vector256.LoadUnsafe(ref baseRef, (nuint)i));
                i += 8;
            }

            sum += Vector256.Sum(Avx.Add(acc0, acc1));
        }

        for (; i < values.Length; i++)
            sum += values[i];

        return sum;
    }

    private static void CopyCenteredMap(float[] map, int mapWidth, int mapHeight, float mean, float[] re, int nx)
    {
        ref var srcBase = ref MemoryMarshal.GetArrayDataReference(map);
        ref var dstBase = ref MemoryMarshal.GetArrayDataReference(re);
        var meanVec = Avx2.IsSupported ? Vector256.Create(mean) : default;

        for (var y = 0; y < mapHeight; y++)
        {
            var srcRow = y * mapWidth;
            var dstRow = y * nx;
            var x = 0;

            if (Avx2.IsSupported)
            {
                while (x <= mapWidth - 8)
                {
                    var v = Vector256.LoadUnsafe(ref srcBase, (nuint)(srcRow + x));
                    Avx.Subtract(v, meanVec).StoreUnsafe(ref dstBase, (nuint)(dstRow + x));
                    x += 8;
                }
            }

            for (; x < mapWidth; x++)
                re[dstRow + x] = map[srcRow + x] - mean;
        }
    }

    private static void CopyKernel(float[] kernel, int kernelWidth, int kernelHeight, float[] im, int nx)
    {
        for (var y = 0; y < kernelHeight; y++)
        {
            Array.Copy(kernel, y * kernelWidth, im, y * nx, kernelWidth);
        }
    }

    // Split the packed spectrum Z = M + i·K into M and K, then multiply M by conj(K) (= correlation).
    //   M[k] = (Z[k] + conj(Z[-k])) / 2        K[k] = (Z[k] − conj(Z[-k])) / (2i)
    private static void SplitAndMultiplyConjugate(
        float[] re,
        float[] im,
        float[] pr,
        float[] pi,
        int nx,
        int ny,
        CancellationToken cancellationToken)
    {
        var size = nx * ny;
        if (size >= ParallelThreshold)
        {
            Parallel.For(0, ny, new ParallelOptions { CancellationToken = cancellationToken },
                v => SplitRow(re, im, pr, pi, nx, ny, v));
        }
        else
        {
            for (var v = 0; v < ny; v++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SplitRow(re, im, pr, pi, nx, ny, v);
            }
        }
    }

    private static void SplitRow(float[] re, float[] im, float[] pr, float[] pi, int nx, int ny, int v)
    {
        var vm = (ny - v) & (ny - 1);
        var rowK = v * nx;
        var rowKm = vm * nx;

        // u = 0 maps to um = 0
        SplitScalar(re, im, pr, pi, rowK, rowKm);

        var u = 1;
        if (Avx2.IsSupported && nx >= 16)
        {
            ref var reBase = ref MemoryMarshal.GetArrayDataReference(re);
            ref var imBase = ref MemoryMarshal.GetArrayDataReference(im);
            ref var prBase = ref MemoryMarshal.GetArrayDataReference(pr);
            ref var piBase = ref MemoryMarshal.GetArrayDataReference(pi);
            var half = Vector256.Create(0.5f);
            var revMask = Reverse8Mask;

            // For u .. u+7 in [1 .. nx-1], um = [nx-u, ..., nx-u-7] is a contiguous reversed block [nx-u-7 .. nx-u].
            for (; u <= nx - 8; u += 8)
            {
                var k = rowK + u;
                var kmReversedStart = rowKm + (nx - u - 7);

                var zr = Vector256.LoadUnsafe(ref reBase, (nuint)k);
                var zi = Vector256.LoadUnsafe(ref imBase, (nuint)k);
                var cr = Avx2.PermuteVar8x32(Vector256.LoadUnsafe(ref reBase, (nuint)kmReversedStart), revMask);
                var imKm = Avx2.PermuteVar8x32(Vector256.LoadUnsafe(ref imBase, (nuint)kmReversedStart), revMask);

                // ci = -imKm => zi + ci = zi - imKm, zi - ci = zi + imKm
                var mr = Avx.Multiply(Avx.Add(zr, cr), half);
                var mi = Avx.Multiply(Avx.Subtract(zi, imKm), half);
                var kr = Avx.Multiply(Avx.Add(zi, imKm), half);
                var ki = Avx.Multiply(Avx.Subtract(cr, zr), half);

                var outR = Fma.IsSupported
                    ? Fma.MultiplyAdd(mr, kr, Avx.Multiply(mi, ki))
                    : Avx.Add(Avx.Multiply(mr, kr), Avx.Multiply(mi, ki));
                var outI = Fma.IsSupported
                    ? Fma.MultiplySubtract(mi, kr, Avx.Multiply(mr, ki))
                    : Avx.Subtract(Avx.Multiply(mi, kr), Avx.Multiply(mr, ki));

                outR.StoreUnsafe(ref prBase, (nuint)k);
                outI.StoreUnsafe(ref piBase, (nuint)k);
            }
        }

        for (; u < nx; u++)
        {
            var um = (nx - u) & (nx - 1);
            SplitScalar(re, im, pr, pi, rowK + u, rowKm + um);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SplitScalar(float[] re, float[] im, float[] pr, float[] pi, int k, int km)
    {
        var zr = re[k];
        var zi = im[k];
        var cr = re[km];
        var ci = -im[km];
        var mr = (zr + cr) * 0.5f;
        var mi = (zi + ci) * 0.5f;
        var kr = (zi - ci) * 0.5f;
        var ki = -(zr - cr) * 0.5f;
        pr[k] = mr * kr + mi * ki;
        pi[k] = mi * kr - mr * ki;
    }

    private static void ExtractScaledResult(float[] pr, int nx, float invN, float bias, float[] result, int px, int py)
    {
        ref var srcBase = ref MemoryMarshal.GetArrayDataReference(pr);
        ref var dstBase = ref MemoryMarshal.GetArrayDataReference(result);
        var invVec = Avx2.IsSupported ? Vector256.Create(invN) : default;
        var biasVec = Avx2.IsSupported ? Vector256.Create(bias) : default;

        for (var y = 0; y < py; y++)
        {
            var srcRow = y * nx;
            var dstRow = y * px;
            var x = 0;

            if (Avx2.IsSupported)
            {
                while (x <= px - 8)
                {
                    var v = Vector256.LoadUnsafe(ref srcBase, (nuint)(srcRow + x));
                    var scaled = Fma.IsSupported
                        ? Fma.MultiplyAdd(v, invVec, biasVec)
                        : Avx.Add(Avx.Multiply(v, invVec), biasVec);
                    scaled.StoreUnsafe(ref dstBase, (nuint)(dstRow + x));
                    x += 8;
                }
            }

            for (; x < px; x++)
                result[dstRow + x] = pr[srcRow + x] * invN + bias;
        }
    }

    /// <summary>In-place 2D FFT (rows, then columns). The inverse transform stays unscaled and is scaled during readback.</summary>
    private static void Transform2D(float[] re, float[] im, int nx, int ny, bool inverse, CancellationToken cancellationToken)
    {
        var rowTwiddles = Twiddles.For(nx);
        var colTwiddles = Twiddles.For(ny);
        var parallel = nx * ny >= ParallelThreshold;
        var options = new ParallelOptions { CancellationToken = cancellationToken };

        if (parallel)
        {
            Parallel.For(0, ny, options, y => TransformRow1D(re, im, y * nx, nx, rowTwiddles, inverse));
        }
        else
        {
            for (var y = 0; y < ny; y++)
                TransformRow1D(re, im, y * nx, nx, rowTwiddles, inverse);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (Avx2.IsSupported && nx >= 8)
        {
            var blockCount = nx >> 3;
            if (parallel)
            {
                Parallel.For(0, blockCount, options,
                    block => Transform8ColumnsSimd(re, im, block << 3, nx, ny, colTwiddles, inverse));
            }
            else
            {
                for (var block = 0; block < blockCount; block++)
                    Transform8ColumnsSimd(re, im, block << 3, nx, ny, colTwiddles, inverse);
            }
        }
        else
        {
            // When nx < 8 (very narrow image strips) or AVX2 isn't available, run scalar in-place column FFT.
            if (parallel)
            {
                Parallel.For(0, nx, options,
                    x => TransformColumnScalar(re, im, x, nx, ny, colTwiddles, inverse));
            }
            else
            {
                for (var x = 0; x < nx; x++)
                    TransformColumnScalar(re, im, x, nx, ny, colTwiddles, inverse);
            }
        }
    }

    /// <summary>Iterative radix-2 FFT of n contiguous points starting at <paramref name="offset"/> (n must be a power of two).</summary>
    private static void TransformRow1D(float[] re, float[] im, int offset, int n, Twiddles twiddles, bool inverse)
    {
        if (n < 2) return;

        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                var a = offset + i;
                var b = offset + j;
                (re[a], re[b]) = (re[b], re[a]);
                (im[a], im[b]) = (im[b], im[a]);
            }
        }

        var cosArr = twiddles.PackedCos;
        var sinArr = inverse ? twiddles.PackedSin : twiddles.PackedNegSin;
        ref var reBase = ref MemoryMarshal.GetArrayDataReference(re);
        ref var imBase = ref MemoryMarshal.GetArrayDataReference(im);
        ref var cosBase = ref MemoryMarshal.GetArrayDataReference(cosArr);
        ref var sinBase = ref MemoryMarshal.GetArrayDataReference(sinArr);
        var stageOffset = 0;

        for (var length = 2; length <= n; length <<= 1)
        {
            var half = length >> 1;

            if (Avx2.IsSupported && half >= 8)
            {
                for (var start = 0; start < n; start += length)
                {
                    var baseA = offset + start;
                    var baseB = baseA + half;
                    for (var k = 0; k < half; k += 8)
                    {
                        var twIdx = (nuint)(stageOffset + k);
                        var wr = Vector256.LoadUnsafe(ref cosBase, twIdx);
                        var wi = Vector256.LoadUnsafe(ref sinBase, twIdx);

                        var aIdx = (nuint)(baseA + k);
                        var bIdx = (nuint)(baseB + k);

                        var ar = Vector256.LoadUnsafe(ref reBase, aIdx);
                        var ai = Vector256.LoadUnsafe(ref imBase, aIdx);
                        var br = Vector256.LoadUnsafe(ref reBase, bIdx);
                        var bi = Vector256.LoadUnsafe(ref imBase, bIdx);

                        var xr = Fma.IsSupported
                            ? Fma.MultiplySubtract(br, wr, Avx.Multiply(bi, wi))
                            : Avx.Subtract(Avx.Multiply(br, wr), Avx.Multiply(bi, wi));
                        var xi = Fma.IsSupported
                            ? Fma.MultiplyAdd(br, wi, Avx.Multiply(bi, wr))
                            : Avx.Add(Avx.Multiply(br, wi), Avx.Multiply(bi, wr));

                        Avx.Subtract(ar, xr).StoreUnsafe(ref reBase, bIdx);
                        Avx.Subtract(ai, xi).StoreUnsafe(ref imBase, bIdx);
                        Avx.Add(ar, xr).StoreUnsafe(ref reBase, aIdx);
                        Avx.Add(ai, xi).StoreUnsafe(ref imBase, aIdx);
                    }
                }
            }
            else
            {
                for (var start = 0; start < n; start += length)
                {
                    var baseA = offset + start;
                    var baseB = baseA + half;
                    for (var k = 0; k < half; k++)
                    {
                        var wr = cosArr[stageOffset + k];
                        var wi = sinArr[stageOffset + k];
                        var a = baseA + k;
                        var b = baseB + k;
                        var xr = re[b] * wr - im[b] * wi;
                        var xi = re[b] * wi + im[b] * wr;
                        re[b] = re[a] - xr;
                        im[b] = im[a] - xi;
                        re[a] += xr;
                        im[a] += xi;
                    }
                }
            }

            stageOffset += half;
        }
    }

    /// <summary>
    /// Transforms 8 adjacent columns [xStart .. xStart+7] simultaneously in-place using AVX2/FMA without copying to
    /// temporary column scratch arrays (row-major storage makes 8 adjacent columns contiguous in memory).
    /// </summary>
    private static void Transform8ColumnsSimd(
        float[] re,
        float[] im,
        int xStart,
        int nx,
        int ny,
        Twiddles twiddles,
        bool inverse)
    {
        if (ny < 2) return;

        ref var reBase = ref MemoryMarshal.GetArrayDataReference(re);
        ref var imBase = ref MemoryMarshal.GetArrayDataReference(im);

        for (int y = 1, j = 0; y < ny; y++)
        {
            var bit = ny >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (y < j)
            {
                var idxA = (nuint)(y * nx + xStart);
                var idxB = (nuint)(j * nx + xStart);

                var ar = Vector256.LoadUnsafe(ref reBase, idxA);
                var br = Vector256.LoadUnsafe(ref reBase, idxB);
                br.StoreUnsafe(ref reBase, idxA);
                ar.StoreUnsafe(ref reBase, idxB);

                var ai = Vector256.LoadUnsafe(ref imBase, idxA);
                var bi = Vector256.LoadUnsafe(ref imBase, idxB);
                bi.StoreUnsafe(ref imBase, idxA);
                ai.StoreUnsafe(ref imBase, idxB);
            }
        }

        var cosArr = twiddles.PackedCos;
        var sinArr = inverse ? twiddles.PackedSin : twiddles.PackedNegSin;
        var stageOffset = 0;

        for (var length = 2; length <= ny; length <<= 1)
        {
            var half = length >> 1;
            for (var start = 0; start < ny; start += length)
            {
                for (var k = 0; k < half; k++)
                {
                    var wr = Vector256.Create(cosArr[stageOffset + k]);
                    var wi = Vector256.Create(sinArr[stageOffset + k]);

                    var idxA = (nuint)((start + k) * nx + xStart);
                    var idxB = (nuint)((start + k + half) * nx + xStart);

                    var ar = Vector256.LoadUnsafe(ref reBase, idxA);
                    var ai = Vector256.LoadUnsafe(ref imBase, idxA);
                    var br = Vector256.LoadUnsafe(ref reBase, idxB);
                    var bi = Vector256.LoadUnsafe(ref imBase, idxB);

                    var xr = Fma.IsSupported
                        ? Fma.MultiplySubtract(br, wr, Avx.Multiply(bi, wi))
                        : Avx.Subtract(Avx.Multiply(br, wr), Avx.Multiply(bi, wi));
                    var xi = Fma.IsSupported
                        ? Fma.MultiplyAdd(br, wi, Avx.Multiply(bi, wr))
                        : Avx.Add(Avx.Multiply(br, wi), Avx.Multiply(bi, wr));

                    Avx.Subtract(ar, xr).StoreUnsafe(ref reBase, idxB);
                    Avx.Subtract(ai, xi).StoreUnsafe(ref imBase, idxB);
                    Avx.Add(ar, xr).StoreUnsafe(ref reBase, idxA);
                    Avx.Add(ai, xi).StoreUnsafe(ref imBase, idxA);
                }
            }

            stageOffset += half;
        }
    }

    private static void TransformColumnScalar(float[] re, float[] im, int x, int nx, int ny, Twiddles twiddles, bool inverse)
    {
        if (ny < 2) return;

        for (int y = 1, j = 0; y < ny; y++)
        {
            var bit = ny >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (y < j)
            {
                var a = y * nx + x;
                var b = j * nx + x;
                (re[a], re[b]) = (re[b], re[a]);
                (im[a], im[b]) = (im[b], im[a]);
            }
        }

        var cosArr = twiddles.PackedCos;
        var sinArr = inverse ? twiddles.PackedSin : twiddles.PackedNegSin;
        var stageOffset = 0;

        for (var length = 2; length <= ny; length <<= 1)
        {
            var half = length >> 1;
            for (var start = 0; start < ny; start += length)
            {
                for (var k = 0; k < half; k++)
                {
                    var wr = cosArr[stageOffset + k];
                    var wi = sinArr[stageOffset + k];
                    var a = (start + k) * nx + x;
                    var b = (start + k + half) * nx + x;
                    var xr = re[b] * wr - im[b] * wi;
                    var xi = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - xr;
                    im[b] = im[a] - xi;
                    re[a] += xr;
                    im[a] += xi;
                }
            }

            stageOffset += half;
        }
    }

    /// <summary>
    /// Stage-packed cos/sin twiddles for radix-2 stages of size 2..n so every inner butterfly reads sequential floats.
    /// Cached once per power of two.
    /// </summary>
    private sealed class Twiddles
    {
        private static readonly ConcurrentDictionary<int, Twiddles> Cache = new();

        private Twiddles(int n)
        {
            var packedLen = Math.Max(0, n - 1);
            PackedCos = new float[packedLen];
            PackedSin = new float[packedLen];
            PackedNegSin = new float[packedLen];

            var offset = 0;
            for (var length = 2; length <= n; length <<= 1)
            {
                var half = length >> 1;
                for (var k = 0; k < half; k++)
                {
                    var angle = 2.0 * Math.PI * k / length;
                    var c = (float)Math.Cos(angle);
                    var s = (float)Math.Sin(angle);
                    PackedCos[offset + k] = c;
                    PackedSin[offset + k] = s;
                    PackedNegSin[offset + k] = -s;
                }

                offset += half;
            }
        }

        public float[] PackedCos { get; }
        public float[] PackedSin { get; }
        public float[] PackedNegSin { get; }

        public static Twiddles For(int n) => Cache.GetOrAdd(n, static size => new Twiddles(size));
    }
}
