namespace SmartMacro.Core.Vision;

/// <summary>
/// Cross-correlation of a grey map with a kernel (a template or a foreground mask) for every position where the
/// kernel fits completely, computed with a 2D FFT. The cost depends only on the map size, not on the kernel size,
/// which is what makes big templates (a 301×288 Tic-Tac-Toe grid, a 640×480 panel) affordable without throwing away
/// pixels: the old "large template" path sampled every 4th pixel and lost 1-pixel lines.
///
/// Result[y * px + x] = Σ kernel[j, i] × map[y + j, x + i], with px = mapWidth − kernelWidth + 1 and
/// py = mapHeight − kernelHeight + 1. The map is padded to powers of two; because only positions where the kernel
/// fits are returned, the circular wrap-around of the FFT never touches the result.
/// Trick: the map goes into the real part and the kernel into the imaginary part, so one forward FFT does both.
/// </summary>
internal static class FftCorrelator
{
    /// <summary>Above this many FFT points the rows / columns are transformed in parallel.</summary>
    private const int ParallelThreshold = 64 * 1024;

    public static float[] CorrelateValid(float[] map, int mapWidth, int mapHeight, float[] kernel, int kernelWidth, int kernelHeight,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(kernel);
        var px = mapWidth - kernelWidth + 1;
        var py = mapHeight - kernelHeight + 1;
        if (px <= 0 || py <= 0 || kernelWidth <= 0 || kernelHeight <= 0) return Array.Empty<float>();

        var nx = NextPowerOfTwo(mapWidth);
        var ny = NextPowerOfTwo(mapHeight);
        var size = nx * ny;
        var re = new double[size];
        var im = new double[size];

        for (var y = 0; y < mapHeight; y++)
        {
            var src = y * mapWidth;
            var dst = y * nx;
            for (var x = 0; x < mapWidth; x++) re[dst + x] = map[src + x];
        }

        for (var y = 0; y < kernelHeight; y++)
        {
            var src = y * kernelWidth;
            var dst = y * nx;
            for (var x = 0; x < kernelWidth; x++) im[dst + x] = kernel[src + x];
        }

        cancellationToken.ThrowIfCancellationRequested();
        Transform2D(re, im, nx, ny, inverse: false, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        // Split the packed spectrum Z = M + i·K into M and K, then multiply M by conj(K) (= correlation).
        //   M[k] = (Z[k] + conj(Z[-k])) / 2        K[k] = (Z[k] − conj(Z[-k])) / (2i)
        var pr = new double[size];
        var pi = new double[size];
        for (var v = 0; v < ny; v++)
        {
            var vm = (ny - v) & (ny - 1);
            for (var u = 0; u < nx; u++)
            {
                var um = (nx - u) & (nx - 1);
                var k = v * nx + u;
                var km = vm * nx + um;
                double zr = re[k], zi = im[k];
                double cr = re[km], ci = -im[km];
                var mr = (zr + cr) * 0.5;
                var mi = (zi + ci) * 0.5;
                var kr = (zi - ci) * 0.5;
                var ki = -(zr - cr) * 0.5;
                pr[k] = mr * kr + mi * ki;
                pi[k] = mi * kr - mr * ki;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        Transform2D(pr, pi, nx, ny, inverse: true, cancellationToken);

        var result = new float[px * py];
        for (var y = 0; y < py; y++)
        {
            var src = y * nx;
            var dst = y * px;
            for (var x = 0; x < px; x++) result[dst + x] = (float)pr[src + x];
        }

        return result;
    }

    internal static int NextPowerOfTwo(int value)
    {
        var n = 1;
        while (n < value) n <<= 1;
        return n;
    }

    /// <summary>In-place 2D FFT (rows, then columns). The inverse includes the 1 / (nx·ny) factor.</summary>
    private static void Transform2D(double[] re, double[] im, int nx, int ny, bool inverse, CancellationToken cancellationToken)
    {
        var rowTwiddles = Twiddles.For(nx);
        var colTwiddles = Twiddles.For(ny);
        var parallel = nx * ny >= ParallelThreshold;
        var options = new ParallelOptions { CancellationToken = cancellationToken };

        if (parallel)
            Parallel.For(0, ny, options, y => Transform1D(re, im, y * nx, nx, rowTwiddles, inverse));
        else
            for (var y = 0; y < ny; y++) Transform1D(re, im, y * nx, nx, rowTwiddles, inverse);

        cancellationToken.ThrowIfCancellationRequested();

        if (parallel)
        {
            Parallel.For(0, nx, options,
                () => (new double[ny], new double[ny]),
                (x, _, buffers) =>
                {
                    TransformColumn(re, im, x, nx, ny, buffers.Item1, buffers.Item2, colTwiddles, inverse);
                    return buffers;
                },
                _ => { });
        }
        else
        {
            var tr = new double[ny];
            var ti = new double[ny];
            for (var x = 0; x < nx; x++) TransformColumn(re, im, x, nx, ny, tr, ti, colTwiddles, inverse);
        }

        if (inverse)
        {
            var factor = 1.0 / ((double)nx * ny);
            for (var i = 0; i < re.Length; i++)
            {
                re[i] *= factor;
                im[i] *= factor;
            }
        }
    }

    private static void TransformColumn(double[] re, double[] im, int x, int nx, int ny, double[] tr, double[] ti, Twiddles twiddles, bool inverse)
    {
        for (var y = 0; y < ny; y++)
        {
            tr[y] = re[y * nx + x];
            ti[y] = im[y * nx + x];
        }

        Transform1D(tr, ti, 0, ny, twiddles, inverse);

        for (var y = 0; y < ny; y++)
        {
            re[y * nx + x] = tr[y];
            im[y * nx + x] = ti[y];
        }
    }

    /// <summary>Iterative radix-2 FFT of n points starting at <paramref name="offset"/> (n must be a power of two).</summary>
    private static void Transform1D(double[] re, double[] im, int offset, int n, Twiddles twiddles, bool inverse)
    {
        if (n < 2) return;

        // Bit-reversal permutation.
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

        var cos = twiddles.Cos;
        var sin = twiddles.Sin;
        for (var length = 2; length <= n; length <<= 1)
        {
            var half = length >> 1;
            var step = n / length;
            for (var start = 0; start < n; start += length)
            {
                for (var k = 0; k < half; k++)
                {
                    var wr = cos[k * step];
                    var wi = inverse ? sin[k * step] : -sin[k * step];
                    var a = offset + start + k;
                    var b = a + half;
                    var xr = re[b] * wr - im[b] * wi;
                    var xi = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - xr;
                    im[b] = im[a] - xi;
                    re[a] += xr;
                    im[a] += xi;
                }
            }
        }
    }

    /// <summary>cos / sin of 2πk / n for k &lt; n / 2, cached per size (sizes are powers of two, so there are few).</summary>
    private sealed class Twiddles
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, Twiddles> Cache = new();

        private Twiddles(int n)
        {
            var half = Math.Max(1, n / 2);
            Cos = new double[half];
            Sin = new double[half];
            for (var k = 0; k < half; k++)
            {
                var angle = 2 * Math.PI * k / n;
                Cos[k] = Math.Cos(angle);
                Sin[k] = Math.Sin(angle);
            }
        }

        public double[] Cos { get; }
        public double[] Sin { get; }

        public static Twiddles For(int n) => Cache.GetOrAdd(n, size => new Twiddles(size));
    }
}
