using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Recording;
using SmartMacro.Core.Vision;
using SmartMacro.Core.Workflow;

namespace SmartMacro.Tests;

/// <summary>
/// Regression tests covering the four user feedback areas:
/// 1) Exact 1:1 recording duration + configurable CPU/GPU compute engine &amp; RAM cache budget,
/// 2) Multi-monitor custom ROI radar normalization ("Pick area" crash fix),
/// 3) Ultra-fast Find Image target-game clicking &amp; wasted hotspot miss skip,
/// 4) True 0.5x..10x speed scaling for recorded typing bursts and vision actions.
/// </summary>
public class FeedbackBugfixTests
{
    private sealed class CapturingInput : IInputSimulator
    {
        private readonly FakeClock _clock;
        public List<string> Events { get; } = [];
        public double CurrentSpeed { get; private set; } = 1.0;
        public bool CurrentRapidMode { get; private set; }
        public double LastRunMaxSpeed { get; private set; } = 1.0;
        public bool SawRapidModeEnabled { get; private set; }
        public bool SimulateTypedDelayOnClock { get; set; }

        public CapturingInput(FakeClock clock) => _clock = clock;

        public void SetPlaybackSpeed(double speed)
        {
            CurrentSpeed = speed;
            if (speed > LastRunMaxSpeed) LastRunMaxSpeed = speed;
            Events.Add($"speed:{speed:0.##}");
        }

        public void SetRapidActionMode(bool enabled)
        {
            CurrentRapidMode = enabled;
            if (enabled) SawRapidModeEnabled = true;
            Events.Add($"rapid:{enabled}");
        }

        public void MoveMouse(int x, int y) => Events.Add($"move:{x},{y}");
        public void MoveMouseRelative(int dx, int dy) => Events.Add($"rel:{dx},{dy}");
        public void MouseButton(MacroMouseButton button, bool down) => Events.Add($"btn:{button}:{(down ? "down" : "up")}");
        public void Scroll(int delta, bool horizontal) => Events.Add($"scroll:{delta}");
        public void Key(int virtualKey, bool down) => Events.Add($"key:{virtualKey}:{(down ? "down" : "up")}");
        public void KeyWithScanCode(int virtualKey, int scanCode, bool extended, bool down) =>
            Events.Add($"scankey:{virtualKey}:{scanCode}:{extended}:{(down ? "down" : "up")}");

        public void TypeCharacters(string text)
        {
            if (SimulateTypedDelayOnClock)
            {
                // Mimic Win32InputSimulator's scaled MinCharacterGapMs + MinKeyHoldMs overhead.
                var overheadMs = CurrentSpeed <= 1.0 ? 14.0 : Math.Max(2.0, 14.0 / CurrentSpeed);
                ((IPlaybackClock)_clock).DelayAsync(TimeSpan.FromMilliseconds(overheadMs), CancellationToken.None).GetAwaiter().GetResult();
            }

            Events.Add($"type:{text}");
        }
    }

    private static BgraImage PlaceOnBlackScreen(BgraImage picture, int x, int y, int width = 360, int height = 240)
    {
        var screen = BgraImage.Create(width, height);
        for (var row = 0; row < picture.Height; row++)
        {
            for (var col = 0; col < picture.Width; col++)
                screen.SetPixel(x + col, y + row, picture.GetPixel(col, row));
        }
        return screen;
    }

    [Theory]
    [InlineData(2657, 419, 456, 376, 0, 0, 1920, 1080)]     // User's exact secondary monitor coordinates
    [InlineData(2657, 419, 456, 376, 0, 0, 3840, 1080)]     // Dual-monitor virtual desktop
    [InlineData(-1200, -300, 320, 240, -1920, -300, 3840, 1380)] // Left-side / negative-origin monitor
    [InlineData(1880, 1040, 40, 40, 0, 0, 1920, 1080)]      // Tight bottom-right corner
    [InlineData(2657, 419, 0, 0, 0, 0, 1920, 1080)]         // Unsized ROI right edge
    public void SearchRegionLayout_MultiMonitorCustomArea_NeverThrowsAndClampsSafely(
        int rxPx, int ryPx, int wPx, int hPx, double deskL, double deskT, double deskW, double deskH)
    {
        var (rx, ry, rw, rh) = SearchRegionLayout.ComputeNormalizedZone(
            rxPx, ryPx, wPx, hPx, deskL, deskT, deskW, deskH);

        Assert.InRange(rx, 0.0, 0.90);
        Assert.InRange(ry, 0.0, 0.90);
        Assert.True(rw > 0.0 && rx + rw <= 1.0001, $"Invalid rx+rw: {rx}+{rw}");
        Assert.True(rh > 0.0 && ry + rh <= 1.0001, $"Invalid ry+rh: {ry}+{rh}");
    }

    [Fact]
    public void Exact1To1_Recording_WithSmartTimingDisabled_KeepsExactPauseDuration()
    {
        var raw = new List<RawInputEvent>
        {
            new(0, RawInputKind.MouseDown, X: 100, Y: 100, Button: MacroMouseButton.Left),
            new(40, RawInputKind.MouseUp, X: 100, Y: 100, Button: MacroMouseButton.Left),
            new(340, RawInputKind.MouseDown, X: 200, Y: 200, Button: MacroMouseButton.Left),
            new(380, RawInputKind.MouseUp, X: 200, Y: 200, Button: MacroMouseButton.Left),
            new(3100, RawInputKind.MouseDown, X: 250, Y: 250, Button: MacroMouseButton.Left),
            new(3140, RawInputKind.MouseUp, X: 250, Y: 250, Button: MacroMouseButton.Left),
        };

        var options = new RecordingOptions
        {
            Fidelity = RecordingFidelity.Exact1To1,
            SmartTiming = false
        };

        var steps = RecordingConverter.Convert(raw, options);

        // No pause should be silently inflated into DelayMode.UntilScreenSettles in Exact 1:1 mode.
        Assert.DoesNotContain(steps.OfType<DelayStep>(), d => d.Mode == DelayMode.UntilScreenSettles);
        var totalFixedDelayMs = steps.OfType<DelayStep>().Sum(d => d.DurationMs);
        var totalHoldMs = steps.OfType<MouseClickStep>().Sum(c => c.HoldMs);
        // Expected total spans from t=0 to t=3100ms + last hold (40ms) = 3140ms (within rounding).
        Assert.InRange(totalFixedDelayMs + totalHoldMs, 3050, 3200);
    }

    [Fact]
    public void CoalesceTypingBurst_PreservesRecordedKeyHoldTimeInCharacterDelay()
    {
        var steps = new MacroStep[]
        {
            new KeyPressStep { VirtualKey = 'H', Action = KeyAction.Press, HoldMs = 25 },
            new DelayStep { Mode = DelayMode.Fixed, DurationMs = 55 },
            new KeyPressStep { VirtualKey = 'I', Action = KeyAction.Press, HoldMs = 25 },
            new DelayStep { Mode = DelayMode.Fixed, DurationMs = 55 },
            new KeyPressStep { VirtualKey = 'O', Action = KeyAction.Press, HoldMs = 25 },
        };

        var coalesced = StepCleanup.CoalesceKeystrokesToTypeText(steps);
        var typed = Assert.Single(coalesced.Steps.OfType<TypeTextStep>());

        Assert.Equal("hio", typed.Text);
        // Each interval between typed chars is 55ms Delay + 25ms preceding key hold = 80ms!
        Assert.Equal(80, typed.CharacterDelayMs);
    }

    [Theory]
    [InlineData(1.0, 240)]
    [InlineData(2.0, 120)]
    [InlineData(5.0, 48)]
    public async Task TypeTextStep_ScalesCleanlyAcross1x2x5xSpeed(double speed, double expectedTotalMs)
    {
        var clock = new FakeClock();
        var input = new CapturingInput(clock) { SimulateTypedDelayOnClock = true };
        var engine = new PlaybackEngine(input, clock);

        var macro = Flow.Macro(new TypeTextStep { Text = "hello", CharacterDelayMs = 60 });
        macro.Speed = speed;

        var result = await engine.RunAsync(macro);

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(speed, input.LastRunMaxSpeed);
        Assert.Equal(1.0, input.CurrentSpeed); // cleanly reset on completion

        // 4 inter-character gaps of 60ms / speed; wall Stopwatch typedMs ~0 in test double so total clock wait + fake sim overhead stays bounded,
        // and critically never shrinks slower than expectedTotalMs vs scales downward by speed.
        var actualMs = clock.Elapsed.TotalMilliseconds;
        Assert.True(actualMs <= expectedTotalMs + 25,
            $"At {speed}x speed, expected <= {expectedTotalMs + 25} ms total clock advance, got {actualMs:0.##} ms.");
    }

    [Fact]
    public void FastReaction_Preset_ConfiguresInstantReflexWithZeroPredictiveOffset()
    {
        var step = new FindImageStep();
        TemplateAutoTuner.ApplyScenario(step, VisionScenario.FastReaction);

        Assert.InRange(step.CheckIntervalMs, 10, 20);
        Assert.Equal(VisionSearchStrategy.Fastest, step.Strategy);
        Assert.Equal(MouseMoveStyle.Instant, step.MoveStyle);
        Assert.Equal(0, step.MoveDurationMs);
        Assert.Equal(0, step.PostActionDelayMs);
        Assert.False(step.TrackTarget);
        Assert.False(step.PredictiveClick);
        Assert.Equal(0, step.PredictiveLeadMs);
    }

    [Fact]
    public async Task FindImageStep_20msPolling_UsesRapidModeWithMinimalActionDelay()
    {
        var clock = new FakeClock();
        var icon = TestImages.Icon(20, 20);
        var screen = PlaceOnBlackScreen(icon, 160, 120);
        var capture = new FakeCapture(screen);
        var input = new CapturingInput(clock);

        // User left MoveStyle/MoveDurationMs/ClickHoldMs/PostActionDelayMs at defaults and set CheckIntervalMs = 20.
        var step = new FindImageStep
        {
            TemplateWidth = icon.Width,
            TemplateHeight = icon.Height,
            TemplateData = TemplateCodec.Encode(icon),
            CheckIntervalMs = 20,
            TimeoutMs = 1000,
            Action = FoundTextAction.Click,
        };

        var result = await new PlaybackEngine(input, clock, capture: capture).RunAsync(Flow.Macro(step));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.True(input.SawRapidModeEnabled, "Fast-polling FindImageStep should engage rapid action mode.");
        Assert.False(input.CurrentRapidMode, "Rapid action mode must reset after the action finishes.");
        // Without rapid optimization this waited 120ms glide + 6ms hover + 35ms hold + 120ms settle = ~281ms.
        // With rapid optimization it takes <= 25ms total!
        Assert.True(clock.Elapsed.TotalMilliseconds <= 25.0,
            $"Expected rapid click action overhead <= 25 ms, got {clock.Elapsed.TotalMilliseconds:0.##} ms.");
    }

    [Fact]
    public async Task HotSpotMemory_ForgetOnMiss_PreventsRepeatedWastedSpotCaptures()
    {
        var icon = TestImages.Icon(20, 20);
        var screenA = PlaceOnBlackScreen(icon, 40, 40);
        var screenB = PlaceOnBlackScreen(icon, 240, 180);
        var capture = new FakeCapture(screenA, screenB, screenB);
        var hotSpots = new HotSpotMemory();
        var probes = new ScreenProbes(null, capture, new FakeClock(), hotSpots: hotSpots);

        var step = new FindImageStep
        {
            TemplateWidth = icon.Width,
            TemplateHeight = icon.Height,
            TemplateData = TemplateCodec.Encode(icon),
            CheckIntervalMs = 20,
        };

        var prepared = probes.Prepare(step);

        // First search finds target at (40,40) and records its hot spot.
        var first = await prepared.LookAsync(null, CancellationToken.None);
        Assert.True(first.Found);
        Assert.Equal(1, hotSpots.Count);

        // Target teleports to (240,180). Next poll tries hotspot once (misses), forgets stale hotspot, and finds (240,180).
        var second = await prepared.LookAsync(null, CancellationToken.None);
        Assert.True(second.Found);
        Assert.Equal(240 + 10, second.X);

        // Make target vanish completely for one check -> hot spot is forgotten when it misses, not kept stale.
        var emptyScreen = TestImages.Solid(320, 240, new RgbColor(0, 0, 0));
        var missingProbes = new ScreenProbes(null, new FakeCapture(emptyScreen, emptyScreen), new FakeClock(), hotSpots: hotSpots);
        var miss = await missingProbes.Prepare(step).LookAsync(null, CancellationToken.None);
        Assert.False(miss.Found);
        Assert.Equal(0, hotSpots.Count);
    }

    [Fact]
    public void HardwareAccelerationAndTemplateCache_ConfigureModeAndEvictRamBudget()
    {
        var originalMode = HardwareAccelerationInfo.ComputeMode;
        var originalRam = HardwareAccelerationInfo.MemoryLimitMb;
        var originalGpu = HardwareAccelerationInfo.GpuAvailable;
        var originalGpuName = HardwareAccelerationInfo.GpuAdapterName;
        var originalGpuVram = HardwareAccelerationInfo.GpuDedicatedVramMb;

        try
        {
            HardwareAccelerationInfo.RegisterGpuAdapter(true, "NVIDIA GeForce GTX 1660 SUPER", 6144);
            HardwareAccelerationInfo.Configure(HardwareAccelerationMode.GpuAccelerated, 512);
            Assert.True(HardwareAccelerationInfo.IsGpuActive);
            Assert.Equal(512, HardwareAccelerationInfo.MemoryLimitMb);
            Assert.Contains("GTX 1660 SUPER", HardwareAccelerationInfo.StatusText);

            HardwareAccelerationInfo.Configure(HardwareAccelerationMode.CpuOnly, 64);
            Assert.False(HardwareAccelerationInfo.IsGpuActive);
            Assert.Equal(64, HardwareAccelerationInfo.MemoryLimitMb);
            Assert.Contains("CPU Only", HardwareAccelerationInfo.StatusText);
            Assert.Equal(64L * 1024 * 1024, TemplateCache.Shared.MaxBytes);

            // Runtime cache eviction check on an isolated TemplateCache
            var cache = new TemplateCache(capacity: 32, maxBytes: 128L * 1024 * 1024);
            cache.ConfigureMemoryBudget(64);
            Assert.Equal(64L * 1024 * 1024, cache.MaxBytes);

            // Settings validation clamps out-of-range RAM values cleanly for low-end & high-end presets
            var settings = new AppSettings { VisionMemoryBudgetMb = 99999, ComputeEngineMode = (HardwareAccelerationMode)99 };
            settings.Sanitize();
            Assert.Equal(1024, settings.VisionMemoryBudgetMb);
            Assert.Equal(HardwareAccelerationMode.Auto, settings.ComputeEngineMode);
        }
        finally
        {
            HardwareAccelerationInfo.RegisterGpuAdapter(originalGpu, originalGpuName, originalGpuVram);
            HardwareAccelerationInfo.Configure(originalMode, originalRam);
        }
    }
}
