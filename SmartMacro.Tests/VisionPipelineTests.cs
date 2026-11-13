using SmartMacro.Core.Ocr;
using SmartMacro.Core.Vision;

namespace SmartMacro.Tests;

public sealed class VisionPipelineTests
{
    [Theory]
    [InlineData(3, 3, 1, 1)]
    [InlineData(7, 5, 3, 4)]
    [InlineData(16, 16, 5, 5)]
    [InlineData(37, 23, 9, 6)]
    [InlineData(192, 108, 25, 17)]
    [InlineData(320, 180, 31, 21)]
    public void FftCorrelator_PooledAvx2_MatchesDirectSumAcrossDimensions(
        int mapWidth,
        int mapHeight,
        int kernelWidth,
        int kernelHeight)
    {
        var rng = new Random(mapWidth * 1009 + mapHeight * 37 + kernelWidth * 7);
        var map = new float[mapWidth * mapHeight];
        var kernel = new float[kernelWidth * kernelHeight];

        // Use non-zero-mean image intensities (20..240) to verify mean-centering prevents float32 DC cancellation.
        for (var i = 0; i < map.Length; i++)
            map[i] = 20f + (float)(rng.NextDouble() * 220.0);
        for (var i = 0; i < kernel.Length; i++)
            kernel[i] = (float)(rng.NextDouble() * 2.0 - 1.0);

        var actual = FftCorrelator.CorrelateValid(map, mapWidth, mapHeight, kernel, kernelWidth, kernelHeight);
        var px = mapWidth - kernelWidth + 1;
        var py = mapHeight - kernelHeight + 1;
        Assert.Equal(px * py, actual.Length);

        var stepY = Math.Max(1, py / 16);
        var stepX = Math.Max(1, px / 16);
        double maxWindowEnergy = kernelWidth * (double)kernelHeight * 240.0;
        for (var y = 0; y < py; y += stepY)
        {
            for (var x = 0; x < px; x += stepX)
            {
                var expected = DirectDot(map, mapWidth, kernel, kernelWidth, kernelHeight, x, y);
                var diff = Math.Abs(expected - actual[y * px + x]);
                var tol = Math.Max(0.2, Math.Max(Math.Abs(expected), maxWindowEnergy) * 2e-5);
                Assert.True(diff < tol,
                    $"Mismatch at ({x},{y}) for {mapWidth}x{mapHeight} vs {kernelWidth}x{kernelHeight}: expected {expected:F3}, got {actual[y * px + x]:F3} (diff {diff:F4}, tol {tol:F4})");
            }
        }
    }

    [Fact]
    public void FftCorrelator_LargeHdSliceWithZeroMeanTemplate_PreservesPeakLocationAndPrecision()
    {
        const int mapW = 640;
        const int mapH = 360;
        const int tplW = 64;
        const int tplH = 64;
        const int targetX = 417;
        const int targetY = 223;

        var rng = new Random(4242);
        var map = new float[mapW * mapH];
        for (var i = 0; i < map.Length; i++)
            map[i] = 30f + (float)(rng.NextDouble() * 190.0);

        // Build a synthetic patterned patch and embed it at (targetX, targetY).
        var kernel = new float[tplW * tplH];
        double sum = 0;
        for (var j = 0; j < tplH; j++)
        {
            for (var i = 0; i < tplW; i++)
            {
                var val = ((i + j) % 8 < 4 ? 210f : 40f);
                map[(targetY + j) * mapW + (targetX + i)] = val;
                kernel[j * tplW + i] = val;
                sum += val;
            }
        }

        var kMean = (float)(sum / kernel.Length);
        for (var i = 0; i < kernel.Length; i++)
            kernel[i] -= kMean;

        var dots = FftCorrelator.CorrelateValid(map, mapW, mapH, kernel, tplW, tplH);
        var px = mapW - tplW + 1;
        var py = mapH - tplH + 1;
        Assert.Equal(px * py, dots.Length);

        var expectedPeak = DirectDot(map, mapW, kernel, tplW, tplH, targetX, targetY);
        var actualPeak = dots[targetY * px + targetX];
        var relErr = Math.Abs(expectedPeak - actualPeak) / Math.Max(1.0, Math.Abs(expectedPeak));
        Assert.True(relErr < 1e-5, $"Peak relative error {relErr} exceeded 1e-5 (expected {expectedPeak}, actual {actualPeak}).");

        // Verify the global argmax matches the planted coordinates.
        var bestIdx = 0;
        for (var i = 1; i < dots.Length; i++)
        {
            if (dots[i] > dots[bestIdx])
                bestIdx = i;
        }

        Assert.Equal(targetX, bestIdx % px);
        Assert.Equal(targetY, bestIdx / px);
    }

    [Fact]
    public void FftCorrelator_AlternatingAndConcurrentCalls_DoNotLeakPooledBufferData()
    {
        // First run a large non-zero correlation so pooled buffers contain large values upon return.
        var bigMap = Enumerable.Repeat(200f, 256 * 256).ToArray();
        var bigKernel = Enumerable.Repeat(5f, 16 * 16).ToArray();
        var bigResult = FftCorrelator.CorrelateValid(bigMap, 256, 256, bigKernel, 16, 16);
        Assert.Equal(200f * 5f * 256f, bigResult[0], 1.0f);

        // Run concurrent smaller correlations reusing those pooled buffers from ArrayPool.Shared.
        Parallel.For(0, 16, worker =>
        {
            var map = Enumerable.Repeat((float)(worker + 1), 19 * 13).ToArray();
            var kernel = new float[5 * 4];
            Array.Fill(kernel, 2f);
            var res = FftCorrelator.CorrelateValid(map, 19, 13, kernel, 5, 4);
            var expected = (worker + 1) * 2f * kernel.Length;
            Assert.Equal((19 - 5 + 1) * (13 - 4 + 1), res.Length);
            Assert.InRange(res[0], expected - 0.1f, expected + 0.1f);
            Assert.InRange(res[^1], expected - 0.1f, expected + 0.1f);
        });
    }

    [Fact]
    public void FftCorrelator_InvalidOrOversizedKernel_ReturnsEmptyArray()
    {
        var map = new float[16];
        var bigKernel = new float[25];
        Assert.Empty(FftCorrelator.CorrelateValid(map, 4, 4, bigKernel, 5, 5));
        Assert.Empty(FftCorrelator.CorrelateValid(map, 4, 4, Array.Empty<float>(), 0, 2));
    }

    [Fact]
    public void HardwareAccelerationInfo_CaptureBackendAndCpuOnlyFallback_BehaveDeterministically()
    {
        var originalMode = HardwareAccelerationInfo.ComputeMode;
        var originalRam = HardwareAccelerationInfo.MemoryLimitMb;
        var originalGpu = HardwareAccelerationInfo.GpuAvailable;
        var originalGpuName = HardwareAccelerationInfo.GpuAdapterName;
        var originalGpuVram = HardwareAccelerationInfo.GpuDedicatedVramMb;
        var originalCapture = HardwareAccelerationInfo.RegisteredCaptureBackend;
        var settingsNotifications = 0;
        Action handler = () => Interlocked.Increment(ref settingsNotifications);

        try
        {
            HardwareAccelerationInfo.SettingsChanged += handler;

            HardwareAccelerationInfo.RegisterGpuAdapter(true, "Test RTX GPU", 8192);
            HardwareAccelerationInfo.RegisterCaptureBackend("WGC / D3D11");
            HardwareAccelerationInfo.Configure(HardwareAccelerationMode.Auto, 512);

            Assert.Equal("WGC / D3D11", HardwareAccelerationInfo.EffectiveCaptureBackend);
            Assert.Contains("Capture: WGC / D3D11", HardwareAccelerationInfo.StatusText);
            Assert.Equal(1, settingsNotifications);

            HardwareAccelerationInfo.Configure(HardwareAccelerationMode.CpuOnly, 256);
            Assert.Equal("GDI BitBlt", HardwareAccelerationInfo.EffectiveCaptureBackend);
            Assert.Contains("CPU Only", HardwareAccelerationInfo.StatusText);
            Assert.Contains("Capture: GDI BitBlt", HardwareAccelerationInfo.StatusText);
            Assert.Equal(2, settingsNotifications);

            // Missing GPU forces GDI BitBlt even when GpuAccelerated mode is requested.
            HardwareAccelerationInfo.RegisterGpuAdapter(false, "", 0);
            HardwareAccelerationInfo.Configure(HardwareAccelerationMode.GpuAccelerated, 256);
            Assert.Equal("GDI BitBlt", HardwareAccelerationInfo.EffectiveCaptureBackend);
            Assert.Contains("Capture: GDI BitBlt", HardwareAccelerationInfo.StatusText);
            Assert.Equal(3, settingsNotifications);
        }
        finally
        {
            HardwareAccelerationInfo.SettingsChanged -= handler;
            HardwareAccelerationInfo.RegisterGpuAdapter(originalGpu, originalGpuName, originalGpuVram);
            HardwareAccelerationInfo.RegisterCaptureBackend(originalCapture);
            HardwareAccelerationInfo.Configure(originalMode, originalRam);
        }
    }

    [Fact]
    public void IScreenCapture_StatelessImplementation_PreservesDefaultStreamingProperties()
    {
        IScreenCapture stateless = new StatelessCaptureDouble();
        Assert.False(stateless.PrefersSmallCaptures);
        Assert.Equal(0L, stateless.LatestFrameSequence);

        Action noOp = () => { };
        stateless.FrameUpdated += noOp;
        stateless.FrameUpdated -= noOp;
    }

    private static double DirectDot(
        float[] map,
        int mapWidth,
        float[] kernel,
        int kernelWidth,
        int kernelHeight,
        int offsetX,
        int offsetY)
    {
        double sum = 0;
        for (var j = 0; j < kernelHeight; j++)
        {
            for (var i = 0; i < kernelWidth; i++)
                sum += kernel[j * kernelWidth + i] * (double)map[(offsetY + j) * mapWidth + (offsetX + i)];
        }

        return sum;
    }

    private sealed class StatelessCaptureDouble : IScreenCapture
    {
        public ScreenRect GetVirtualScreen() => new(0, 0, 100, 100);

        public Task<ScreenFrame> CaptureAsync(ScreenRect region, CancellationToken cancellationToken) =>
            Task.FromResult(new ScreenFrame(region, new BgraImage(region.Width, region.Height, new byte[region.Width * region.Height * 4])));
    }
}
