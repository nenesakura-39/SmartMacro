using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Vision;

namespace SmartMacro.Tests;

/// <summary>
/// Stability &amp; accuracy pass over the uncommitted features: streamer-mode reticle vs. vision (Fix 1), Find image
/// trimming keeps the click on the target and offsets exact (Fix 2), Smart Predictive coasting / clamping (Fix 3),
/// snappy target clicks (Fix 4) and the new steps' polish (Fix 5).
/// </summary>
public class StabilityFixesTests : IDisposable
{
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(8);
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static Macro MacroWith(params MacroStep[] steps)
    {
        var macro = new Macro { Name = "Stability" };
        macro.Steps.AddRange(steps);
        return macro;
    }

    // ---------------- Fix 1: streamer mode never disturbs the next look ----------------

    [Fact]
    public void The_streamer_reticle_keeps_a_24px_hollow_core_around_the_click_point()
    {
        // A tiny target (a 6×6 colour patch): the hollow core grows to 24×24 around the click point.
        var tiny = OverlayCapturePolicy.HollowArea(new ScreenRect(100, 100, 6, 6), 103, 103);
        Assert.Equal(new ScreenRect(91, 91, 24, 24), tiny);

        // A big target keeps its own rectangle (it already contains the core).
        var big = new ScreenRect(200, 200, 60, 40);
        Assert.Equal(big, OverlayCapturePolicy.HollowArea(big, 230, 220));

        // Only a point is known: the core alone.
        Assert.Equal(new ScreenRect(38, 38, 24, 24), OverlayCapturePolicy.HollowArea(default, 50, 50));

        // The reticle's box is drawn outside the keep-clear ring of the hollow area, so it never touches the core.
        var box = new ScreenRect(tiny.X - 8, tiny.Y - 8, tiny.Width + 16, 1); // the box's top edge (gap = clearance + 2)
        Assert.True(OverlayCapturePolicy.IsClearOf(box, tiny));
        Assert.False(OverlayCapturePolicy.IsClearOf(new ScreenRect(99, 99, 10, 10), tiny));
    }

    /// <summary>Records the clock time of every capture.</summary>
    private sealed class TimedCapture(IScreenCapture inner, IPlaybackClock clock) : IScreenCapture
    {
        public List<TimeSpan> Times { get; } = new();
        public ScreenRect GetVirtualScreen() => inner.GetVirtualScreen();

        public Task<ScreenFrame> CaptureAsync(ScreenRect region, CancellationToken cancellationToken)
        {
            Times.Add(clock.Elapsed);
            return inner.CaptureAsync(region, cancellationToken);
        }
    }

    private static BgraImage Screen()
    {
        var image = BgraImage.Create(100, 80);
        image.FillRectangle(new ScreenRect(0, 0, 100, 80), new RgbColor(30, 40, 50));
        return image;
    }

    private static BgraImage ScreenWithBlock()
    {
        var image = Screen();
        image.FillRectangle(new ScreenRect(40, 30, 20, 10), RgbColor.White);
        return image;
    }

    [Fact]
    public async Task Wait_for_change_hides_a_capturable_reticle_before_its_starting_picture()
    {
        var clock = new FakeClock();
        var capture = new TimedCapture(new FakeCapture(Screen(), ScreenWithBlock()), clock);
        var engine = new PlaybackEngine(new FakeInput(), clock, capture: capture);
        var capturesWhenAsked = new List<int>();
        engine.TargetOverlayClearRequested += (_, request) =>
        {
            capturesWhenAsked.Add(capture.Times.Count);
            request.Cleared = true; // as if a streamer-mode reticle was on screen and got hidden
        };

        var result = await engine.RunAsync(MacroWith(new WaitForChangeStep { RegionWidth = 100, RegionHeight = 80 }));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { 0 }, capturesWhenAsked);                                 // asked once, before any capture
        Assert.Equal(PlaybackEngine.OverlayClearSettleMs, capture.Times[0].TotalMilliseconds, precision: 3); // then waited
    }

    [Fact]
    public async Task Without_a_reticle_to_hide_the_starting_picture_is_taken_at_once()
    {
        var clock = new FakeClock();
        var capture = new TimedCapture(new FakeCapture(Screen(), ScreenWithBlock()), clock);
        var engine = new PlaybackEngine(new FakeInput(), clock, capture: capture);
        engine.TargetOverlayClearRequested += (_, _) => { }; // normal mode: overlays are left out of captures

        await engine.RunAsync(MacroWith(new WaitForChangeStep { RegionWidth = 100, RegionHeight = 80 }));

        Assert.Equal(0, capture.Times[0].TotalMilliseconds);
    }

    // ---------------- Fix 2: trimming never moves the click off the target ----------------

    /// <summary>A sloppy capture: a red ball (r 14, centre (17, 37)) with extra white on the TOP and RIGHT of a 64×56 picture.</summary>
    private static BgraImage SloppyBallTemplate()
    {
        var template = TestImages.Solid(64, 56, RgbColor.White);
        FindImageBackgroundMaskTests.Disc(template, 17, 37, 14, new RgbColor(220, 30, 30));
        return template;
    }

    private static FindImageStep BallStep(BgraImage template) => new()
    {
        TemplateWidth = template.Width, TemplateHeight = template.Height, TemplateData = TemplateCodec.Encode(template),
        MinSimilarityPercent = 90, TimeoutMs = 0, SearchFullScreen = true,
    };

    [Fact]
    public async Task Asymmetric_margins_still_give_the_exact_ball_centre_on_every_look()
    {
        var template = SloppyBallTemplate();
        var screen = TestImages.Solid(400, 300, RgbColor.White);
        TestImages.Paste(screen, template, 100, 80); // the ball's centre is at (117, 117) on screen
        var step = BallStep(template);
        var probes = new ScreenProbes(null, new FakeCapture(screen), new FakeClock(), null, new HotSpotMemory());

        var first = await probes.ProbeImageAsync(step);
        var second = await probes.ProbeImageAsync(step); // Hot-Spot fast path

        Assert.True(first.Found, first.MissText);
        Assert.Equal((117, 117), (first.X, first.Y));
        Assert.True(second.FromHotSpot);
        Assert.Equal((117, 117), (second.X, second.Y));
        Assert.Equal((first.X, first.Y), (first.Match.CenterX, first.Match.CenterY)); // the match box is centred on the ball
    }

    [Fact]
    public async Task A_click_offset_is_applied_exactly_from_the_ball_centre()
    {
        var template = SloppyBallTemplate();
        var screen = TestImages.Solid(400, 300, RgbColor.White);
        TestImages.Paste(screen, template, 100, 80);
        var step = BallStep(template);
        step.Action = FoundTextAction.Click;
        step.OffsetX = 10;
        step.OffsetY = -5;
        step.PostActionDelayMs = 0;
        var input = new FakeInput();

        var result = await new PlaybackEngine(input, new FakeClock(), capture: new FakeCapture(screen)).RunAsync(MacroWith(step));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 127,112", "mouse Left down", "mouse Left up" }, input.Calls);
    }

    // ---------------- Fix 3: Smart Predictive vs. 1-frame occlusion / velocity spikes ----------------

    private static FeedbackObservation Follow(double target, double bar, double halfWidth = 5) =>
        new(MarkerStatus.Found, target, true, bar - halfWidth, bar + halfWidth);

    private static FeedbackObservation TargetHidden(double bar, double halfWidth = 5) =>
        new(MarkerStatus.Lost, null, true, bar - halfWidth, bar + halfWidth);

    private static PredictiveFeedbackController Controller(int minChangeMs = 8) => new(new PredictiveFeedbackSettings(
        BarLengthPx: 300, FollowMode: true, HoldDirection: +1, LeadSeconds: 0.18, DeadzonePx: 6, UsePwm: false,
        PulsePeriod: TimeSpan.FromMilliseconds(80), MinInputChange: TimeSpan.FromMilliseconds(minChangeMs),
        LostTimeout: TimeSpan.FromSeconds(1), WhenLost: FeedbackLostAction.ReleaseAndWait, DeadBandPercent: 0));

    [Fact]
    public void The_first_frame_never_has_a_speed()
    {
        var controller = Controller();
        controller.Update(Follow(90, 10), TimeSpan.FromSeconds(3)); // first sighting far from "0"
        Assert.Equal(0, controller.TargetVelocity);
        Assert.Equal(0, controller.PlayerVelocity);

        // After a long loss the speeds start from 0 again, however far the target moved meanwhile.
        controller.Update(new FeedbackObservation(MarkerStatus.Lost, null, false, 0, 0), TimeSpan.FromSeconds(5));
        controller.Update(Follow(10, 90), TimeSpan.FromSeconds(5) + Frame);
        Assert.Equal(0, controller.TargetVelocity);
        Assert.Equal(0, controller.PlayerVelocity);
    }

    [Fact]
    public void Speeds_are_clamped_so_a_jump_cant_spike_the_derivative()
    {
        var controller = Controller();
        controller.Update(Follow(10, 50), TimeSpan.Zero);
        controller.Update(Follow(90, 50), Frame); // 240 px in 8 ms = 30 000 px/s

        Assert.InRange(controller.TargetVelocity, 0, PredictiveFeedbackSettings.MaxSpeedPxPerSecond);
        for (var i = 2; i < 10; i++) controller.Update(Follow(i % 2 == 0 ? 10 : 90, 50), Frame * i);
        Assert.InRange(Math.Abs(controller.TargetVelocity), 0, PredictiveFeedbackSettings.MaxSpeedPxPerSecond);
    }

    [Fact]
    public void A_3_frame_occlusion_coasts_smoothly_without_a_false_brake()
    {
        var controller = Controller(minChangeMs: 0);
        var time = TimeSpan.Zero;
        double target = 40, bar = 40;
        for (var i = 0; i < 10; i++, time += Frame)
        {
            controller.Update(Follow(target, bar), time);
            target += 0.8; // the fish swims right: 0.8 % (2.4 px) per 8 ms = 300 px/s, your bar right behind it
            bar += 0.8;
        }

        var speed = controller.TargetVelocity;
        var lastSeenPx = controller.TargetPosition;
        Assert.True(speed > 200, $"{speed:0} px/s");

        // The fish passes behind a splash for 3 frames: the target coasts on (less each frame), nothing jumps.
        for (var i = 0; i < 3; i++, time += Frame)
        {
            var decision = controller.Update(TargetHidden(bar), time);
            Assert.True(controller.IsCoasting);
            Assert.False(decision.Braked);
            bar += 0.8;
        }

        Assert.Equal(speed * Math.Pow(PredictiveFeedbackSettings.CoastVelocityFactor, 3), controller.TargetVelocity, precision: 6);
        Assert.True(controller.TargetPosition > lastSeenPx, "the coasted target kept moving right");

        // It reappears where it really is: no reversal brake, no coasting any more.
        target += 2.4;
        var back = controller.Update(Follow(target, bar), time);
        Assert.False(back.Braked);
        Assert.False(controller.IsCoasting);
        Assert.Equal(0, controller.Brakes);
        Assert.Equal(3, controller.CoastedFrames);
    }

    [Fact]
    public void Coasting_stops_after_5_frames_and_then_the_loss_is_a_real_one()
    {
        var controller = Controller();
        controller.Update(Follow(50, 50), TimeSpan.Zero);
        controller.Update(Follow(51, 50), Frame);

        for (var i = 2; i < 7; i++) controller.Update(TargetHidden(50), Frame * i);
        Assert.Equal(PredictiveFeedbackSettings.MaxCoastFrames, controller.CoastedFrames);

        controller.Update(TargetHidden(50), Frame * 7); // the 6th missing frame: no more coasting
        Assert.False(controller.IsCoasting);
        Assert.Equal(0, controller.TargetVelocity);
    }

    [Fact]
    public void A_marker_hidden_behind_the_bar_coasts_instead_of_jumping_to_the_bar_centre()
    {
        var controller = Controller();
        controller.Update(Follow(60, 40), TimeSpan.Zero);
        controller.Update(Follow(61, 40.5), Frame);

        // The sensor replaced the hidden fish with the bar's centre (trusted, but flagged as occluded).
        var occluded = new FeedbackObservation(MarkerStatus.Found, 40.5, true, 35.5, 45.5, MarkerOccluded: true);
        controller.Update(occluded, Frame * 2);
        Assert.True(controller.IsCoasting);
        Assert.True(controller.TargetPosition > 61 * 3 - 1, "kept the real fish position, not the bar's centre");
    }

    [Fact]
    public async Task Smart_loops_need_at_least_3_matching_pixels_for_a_centroid()
    {
        var bar = BgraImage.Create(100, 10);
        bar.FillRectangle(new ScreenRect(0, 0, 100, 10), new RgbColor(20, 20, 20));
        bar.FillRectangle(new ScreenRect(50, 5, 2, 1), new RgbColor(255, 0, 0)); // a 2-pixel stray match

        FeedbackLoopStep Step(FeedbackControlMode mode) => new()
        {
            ControlMode = mode, RegionX = 0, RegionY = 0, RegionWidth = 100, RegionHeight = 10, MarkerColor = "#FF0000",
            MarkerTolerance = 10, MarkerMinPixels = 1, ZoneMode = FeedbackZoneMode.Fixed,
        };

        var smart = await new FeedbackSensor(Step(FeedbackControlMode.SmartPredictive), new FakeCapture(bar), new FakeClock()).ReadAsync(CancellationToken.None);
        var classic = await new FeedbackSensor(Step(FeedbackControlMode.ClassicSimpleHold), new FakeCapture(bar), new FakeClock()).ReadAsync(CancellationToken.None);

        Assert.NotEqual(MarkerStatus.Found, smart.Marker.Status);
        Assert.Equal(MarkerStatus.Found, classic.Marker.Status); // Classic keeps the user's own minimum
    }

    // ---------------- Fix 4: snappy target clicks ----------------

    [Fact]
    public async Task Target_clicks_rest_6_ms_and_hold_12_ms()
    {
        Assert.Equal(6, PlaybackEngine.TargetHoverMs);
        Assert.Equal(12, PlaybackEngine.MinTargetClickHoldMs);

        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };
        var engine = new PlaybackEngine(input, clock, capture: new FakeCapture(Screen(), ScreenWithBlock()));

        await engine.RunAsync(MacroWith(new WaitForChangeStep { RegionWidth = 100, RegionHeight = 80, Action = FoundTextAction.Click }));

        Assert.Equal(new[] { "move 50,35", "mouse Left down", "mouse Left up" }, input.Calls);
        Assert.Equal(6, (input.Times[1] - input.Times[0]).TotalMilliseconds, precision: 3);
        Assert.Equal(12, (input.Times[2] - input.Times[1]).TotalMilliseconds, precision: 3);
    }

    // ---------------- Fix 5: new steps ----------------

    [Fact]
    public void Focus_window_matches_the_title_or_the_app_name()
    {
        var windows = new[]
        {
            new WindowInfo(1, "Roblox wiki - Chrome", new ScreenRect(0, 0, 10, 10), "chrome"),
            new WindowInfo(2, "Roblox", default, "RobloxPlayerBeta", IsMinimized: true),
            new WindowInfo(3, "My game", new ScreenRect(0, 0, 10, 10), "GameLauncher.exe"),
        };

        Assert.Equal(2, SearchArea.FindWindowByTitleOrApp(windows, "roblox")!.Handle);            // exact title wins
        Assert.Equal(1, SearchArea.FindWindowByTitleOrApp(windows, "wiki")!.Handle);              // title contains
        Assert.Equal(2, SearchArea.FindWindowByTitleOrApp(windows, "RobloxPlayerBeta.exe")!.Handle); // app name
        Assert.Equal(3, SearchArea.FindWindowByTitleOrApp(windows, "gamelauncher")!.Handle);      // app name with .exe stored
        Assert.Equal(1, SearchArea.FindWindowByTitleOrApp(windows, "CHROME")!.Handle);            // any case
        Assert.Null(SearchArea.FindWindowByTitleOrApp(windows, "Discord"));
    }

    [Fact]
    public async Task Focus_window_by_app_name_restores_and_focuses_it()
    {
        var layout = new FakeFocusLayout();
        layout.Windows.Add(new WindowInfo(9, "Game", default, "RobloxPlayerBeta", IsMinimized: true));
        var engine = new PlaybackEngine(new FakeInput(), new FakeClock(), layout: layout);

        var result = await engine.RunAsync(MacroWith(new FocusWindowStep { WindowTitle = "robloxplayerbeta" }));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        var (window, restore) = Assert.Single(layout.Focused);
        Assert.Equal(9, window.Handle);
        Assert.True(restore);
    }

    [Fact]
    public async Task Screenshot_creates_missing_folders_and_uses_a_timestamped_name()
    {
        var folder = _dir.Combine("new", "nested", "shots");
        Assert.False(Directory.Exists(folder));
        var engine = new PlaybackEngine(new FakeInput(), new FakeClock(), capture: new FakeCapture(Screen()));

        var result = await engine.RunAsync(MacroWith(new ScreenshotStep { Area = ScreenshotArea.AllMonitors, FolderPath = folder }));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        var file = Path.GetFileName(Assert.Single(Directory.GetFiles(folder)));
        Assert.Matches(@"^SmartMacro_\d{8}_\d{6}_\d{3}\.png$", file);
    }

    [Fact]
    public void Nothing_of_the_postponed_sound_alert_step_exists()
    {
        Assert.DoesNotContain(Enum.GetNames<StepKind>(), n => n.Contains("Alert", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(MacroStep).Assembly.GetTypes(), t => t.Name.Contains("AlertStep", StringComparison.Ordinal));
        Assert.DoesNotContain(Core.Workflow.StudioCommandCatalog.All, c => c.Title.Contains("Alert", StringComparison.OrdinalIgnoreCase));
    }
}
