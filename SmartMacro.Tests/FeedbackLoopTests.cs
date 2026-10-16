using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Vision;

namespace SmartMacro.Tests;

// =====================================================================================================================
// Stage 4: continuous visual feedback (Feedback loop step).
// These tests use a fake screen and a fake clock. They prove the loop's logic, timing rules and safety (what it would
// send and when), NOT that a real game shows up in a GDI capture or reacts to SendInput in time.
// =====================================================================================================================

/// <summary>What the fake bar shows in one frame. Positions are % along the bar; null = not drawn.</summary>
internal sealed record BarFrame(
    double? Marker,
    double? SecondMarker = null,
    double? ZoneStart = null,
    double? ZoneEnd = null,
    bool Done = false,
    bool Failed = false);

/// <summary>
/// Fake screen (600×300) with a bar at <see cref="Bar"/>. Every capture of the bar area uses the next frame of the
/// script; captures of other areas (conditions) show the state of the latest bar frame, so "done" / "failed" squares
/// appear in step with the bar.
/// Marker = red 5×20 block, zone = white band, done = green square in <see cref="DoneArea"/>, failed = blue square in
/// <see cref="FailArea"/>.
/// </summary>
internal sealed class ScriptedBarCapture : IScreenCapture
{
    public static readonly ScreenRect Bar = new(100, 100, 201, 20);
    public static readonly ScreenRect DoneArea = new(400, 200, 40, 40);
    public static readonly ScreenRect FailArea = new(480, 200, 40, 40);
    public static readonly RgbColor Red = new(255, 0, 0);
    public static readonly RgbColor White = new(255, 255, 255);
    public static readonly RgbColor Green = new(0, 200, 0);
    public static readonly RgbColor Blue = new(0, 0, 200);

    private readonly Func<int, BarFrame> _script;
    private int _barFrames;

    public ScriptedBarCapture(Func<int, BarFrame> script) => _script = script;

    /// <summary>How many times the bar was captured (= frames the loop looked at).</summary>
    public int BarCaptures => Volatile.Read(ref _barFrames);

    public int OtherCaptures { get; private set; }

    public ScreenRect GetVirtualScreen() => new(0, 0, 600, 300);

    public Task<ScreenFrame> CaptureAsync(ScreenRect region, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int index;
        if (region == Bar)
        {
            index = Interlocked.Increment(ref _barFrames) - 1;
        }
        else
        {
            OtherCaptures++;
            index = Math.Max(0, BarCaptures - 1);
        }

        var screen = Draw(_script(index));
        var area = region.Intersect(GetVirtualScreen());
        if (area.IsEmpty) throw new ArgumentException("The area is outside the screen.");
        return Task.FromResult(new ScreenFrame(area, screen.Crop(area)));
    }

    public static BgraImage Draw(BarFrame f)
    {
        var screen = TestImages.Solid(600, 300, RgbColor.Black);
        screen.FillRectangle(Bar, new RgbColor(60, 60, 60));
        if (f.ZoneStart is { } zs && f.ZoneEnd is { } ze)
        {
            var a = Bar.X + (int)Math.Round(zs * 2);
            var b = Bar.X + (int)Math.Round(ze * 2);
            screen.FillRectangle(new ScreenRect(a, Bar.Y, b - a + 1, Bar.Height), White);
        }

        if (f.Marker is { } m) DrawMarker(screen, m);
        if (f.SecondMarker is { } m2) DrawMarker(screen, m2);
        if (f.Done) screen.FillRectangle(DoneArea, Green);
        if (f.Failed) screen.FillRectangle(FailArea, Blue);
        return screen;
    }

    /// <summary>A 5 px wide red block centred on the position (200 px = 100 %, so whole / half percents land on pixels).</summary>
    private static void DrawMarker(BgraImage screen, double percent)
    {
        var center = Bar.X + (int)Math.Round(percent * 2);
        screen.FillRectangle(new ScreenRect(center - 2, Bar.Y, 5, Bar.Height), Red);
    }
}

/// <summary>Builders shared by the Stage 4 tests.</summary>
internal static class Feedback
{
    /// <summary>
    /// A loop on the fake bar: fixed zone 40-60 %, hold before / keep inside / release after, left mouse button,
    /// 20 frames per second, 1 s max runtime then continue, no dead band, no glitch filter, no minimum change time.
    /// </summary>
    public static FeedbackLoopStep Loop(Action<FeedbackLoopStep>? configure = null)
    {
        var step = new FeedbackLoopStep
        {
            RegionX = ScriptedBarCapture.Bar.X,
            RegionY = ScriptedBarCapture.Bar.Y,
            RegionWidth = ScriptedBarCapture.Bar.Width,
            RegionHeight = ScriptedBarCapture.Bar.Height,
            Orientation = BarOrientation.Horizontal,
            MarkerColor = "#FF0000",
            MarkerTolerance = 24,
            MarkerMinPixels = 3,
            MaxJumpPercent = 100,
            ZoneMode = FeedbackZoneMode.Fixed,
            ZoneStartPercent = 40,
            ZoneEndPercent = 60,
            WhenBeforeZone = FeedbackAction.Hold,
            WhenInsideZone = FeedbackAction.Keep,
            WhenAfterZone = FeedbackAction.Release,
            DeadBandPercent = 0,
            InputKind = FeedbackInputKind.MouseButton,
            MouseButton = MacroMouseButton.Left,
            MaxCapturesPerSecond = 20,
            MinInputChangeMs = 0,
            MaxRuntimeMs = 1000,
            WhenTimeUp = LoopLimitBehavior.ExitLoop,
            LostTimeoutMs = 500,
            WhenTargetLost = FeedbackLostAction.ReleaseAndWait,
        };
        configure?.Invoke(step);
        return step;
    }

    public static Condition DoneCondition() => new()
    {
        Check = new FindColorStep
        {
            Color = "#00C800", Tolerance = 10, MinPixels = 20, TimeoutMs = 0, CheckIntervalMs = 50, SearchFullScreen = false,
            RegionX = ScriptedBarCapture.DoneArea.X, RegionY = ScriptedBarCapture.DoneArea.Y,
            RegionWidth = ScriptedBarCapture.DoneArea.Width, RegionHeight = ScriptedBarCapture.DoneArea.Height,
        },
    };

    public static Condition FailCondition() => new()
    {
        Check = new FindColorStep
        {
            Color = "#0000C8", Tolerance = 10, MinPixels = 20, TimeoutMs = 0, CheckIntervalMs = 50, SearchFullScreen = false,
            RegionX = ScriptedBarCapture.FailArea.X, RegionY = ScriptedBarCapture.FailArea.Y,
            RegionWidth = ScriptedBarCapture.FailArea.Width, RegionHeight = ScriptedBarCapture.FailArea.Height,
        },
    };

    public static (PlaybackEngine Engine, FakeInput Input, FakeClock Clock, List<string> Messages) Engine(ScriptedBarCapture capture)
    {
        var input = new FakeInput();
        var clock = new FakeClock();
        var engine = new PlaybackEngine(input, clock, capture: capture);
        var messages = new List<string>();
        engine.Message += (_, m) => { lock (messages) messages.Add(m); };
        return (engine, input, clock, messages);
    }

    public static string[] Calls(FakeInput input)
    {
        lock (input.Calls) return input.Calls.ToArray();
    }

    public static string Summary(List<string> messages)
    {
        lock (messages) return Assert.Single(messages, m => m.StartsWith("Feedback loop (step"));
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// The controller: pure decisions from scripted readings.
// ---------------------------------------------------------------------------------------------------------------------

public class FeedbackControllerTests
{
    private static FeedbackRules Rules(
        FeedbackAction before = FeedbackAction.Hold,
        FeedbackAction inside = FeedbackAction.Keep,
        FeedbackAction after = FeedbackAction.Release,
        double deadBand = 0,
        int minChangeMs = 0,
        int lostMs = 500,
        FeedbackLostAction whenLost = FeedbackLostAction.ReleaseAndWait) =>
        new(before, inside, after, deadBand, TimeSpan.FromMilliseconds(minChangeMs), TimeSpan.FromMilliseconds(lostMs), whenLost);

    private static FeedbackObservation At(double percent, double zoneStart = 40, double zoneEnd = 60) =>
        new(MarkerStatus.Found, percent, true, zoneStart, zoneEnd);

    private static FeedbackObservation Seen(MarkerStatus status) => new(status, 50, true, 40, 60);

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void Holds_before_the_zone_keeps_inside_and_releases_after()
    {
        var c = new FeedbackController(Rules());

        Assert.Equal(FeedbackCommand.Press, c.Update(At(10), Ms(0)).Command);
        Assert.True(c.IsHeld);
        var inside = c.Update(At(50), Ms(50));
        Assert.Equal(FeedbackCommand.None, inside.Command);
        Assert.Equal(FeedbackRegion.Inside, inside.Region);
        Assert.True(c.IsHeld); // "keep" leaves it held
        Assert.Equal(FeedbackCommand.Release, c.Update(At(90), Ms(100)).Command);
        Assert.Equal(FeedbackCommand.None, c.Update(At(50), Ms(150)).Command);
        Assert.False(c.IsHeld); // "keep" leaves it released
        Assert.Equal(FeedbackCommand.Press, c.Update(At(10), Ms(200)).Command);
    }

    [Fact]
    public void Input_changes_only_when_the_wanted_state_changes()
    {
        var c = new FeedbackController(Rules());
        var commands = Enumerable.Range(0, 10).Select(i => c.Update(At(10 + i), Ms(i * 50)).Command).ToList();

        Assert.Equal(FeedbackCommand.Press, commands[0]);
        Assert.All(commands.Skip(1), command => Assert.Equal(FeedbackCommand.None, command));
    }

    [Fact]
    public void Dead_band_stops_flicker_at_a_zone_edge()
    {
        // Before = hold, inside / after = release, zone 40-60, dead band 3 %.
        var c = new FeedbackController(Rules(inside: FeedbackAction.Release, deadBand: 3));

        Assert.Equal(FeedbackCommand.Press, c.Update(At(30), Ms(0)).Command);
        var nearEdge = c.Update(At(41), Ms(50)); // inside by 1 %, not clearly across
        Assert.Equal(FeedbackCommand.None, nearEdge.Command);
        Assert.Equal(FeedbackRegion.Before, nearEdge.Region);
        Assert.Equal(FeedbackCommand.None, c.Update(At(39), Ms(100)).Command);
        Assert.Equal(FeedbackCommand.Release, c.Update(At(43), Ms(150)).Command); // 3 % in: crossed
        var back = c.Update(At(39.5), Ms(200)); // just outside again, but not 3 % past the edge
        Assert.Equal(FeedbackCommand.None, back.Command);
        Assert.Equal(FeedbackRegion.Inside, back.Region);
        Assert.Equal(FeedbackCommand.Press, c.Update(At(37), Ms(250)).Command);
    }

    [Fact]
    public void Without_a_dead_band_every_edge_crossing_counts()
    {
        var c = new FeedbackController(Rules(inside: FeedbackAction.Release));
        var commands = new[] { 39.0, 41, 39, 41 }.Select((p, i) => c.Update(At(p), Ms(i * 50)).Command).ToList();

        Assert.Equal(new[] { FeedbackCommand.Press, FeedbackCommand.Release, FeedbackCommand.Press, FeedbackCommand.Release }, commands);
    }

    [Theory]
    [InlineData(10.0, null, FeedbackRegion.Before)]
    [InlineData(50.0, null, FeedbackRegion.Inside)]
    [InlineData(40.0, null, FeedbackRegion.Inside)]   // the edges belong to the zone
    [InlineData(60.0, null, FeedbackRegion.Inside)]
    [InlineData(61.0, null, FeedbackRegion.After)]
    [InlineData(42.0, FeedbackRegion.Before, FeedbackRegion.Before)]  // needs 45 (40 + 5) to count as inside
    [InlineData(45.0, FeedbackRegion.Before, FeedbackRegion.Inside)]
    [InlineData(37.0, FeedbackRegion.Inside, FeedbackRegion.Inside)]  // needs 35 to count as before
    [InlineData(35.0, FeedbackRegion.Inside, FeedbackRegion.Before)]
    [InlineData(64.0, FeedbackRegion.Inside, FeedbackRegion.Inside)]  // needs 65 to count as after
    [InlineData(65.0, FeedbackRegion.Inside, FeedbackRegion.After)]
    [InlineData(58.0, FeedbackRegion.After, FeedbackRegion.After)]    // needs 55 to count as inside
    [InlineData(62.0, FeedbackRegion.Before, FeedbackRegion.Inside)]  // jumped across, not clearly past the far edge
    [InlineData(80.0, FeedbackRegion.Before, FeedbackRegion.After)]   // jumped right across
    [InlineData(20.0, FeedbackRegion.After, FeedbackRegion.Before)]
    public void Classify_uses_the_dead_band_only_when_the_region_changes(double position, FeedbackRegion? previous, FeedbackRegion expected)
    {
        Assert.Equal(expected, FeedbackController.Classify(position, 40, 60, previous, deadBand: 5));
    }

    [Theory]
    [InlineData(MarkerStatus.Lost)]
    [InlineData(MarkerStatus.Ambiguous)]
    [InlineData(MarkerStatus.IgnoredJump)]
    public void Untrusted_frames_change_nothing(MarkerStatus status)
    {
        var c = new FeedbackController(Rules());
        c.Update(At(10), Ms(0)); // held

        var decision = c.Update(Seen(status), Ms(50));

        Assert.Equal(FeedbackCommand.None, decision.Command);
        Assert.Null(decision.Region);
        Assert.True(c.IsHeld);
        Assert.False(decision.TargetLost);
    }

    [Fact]
    public void A_missing_zone_is_not_trusted_either()
    {
        var c = new FeedbackController(Rules());
        c.Update(At(10), Ms(0));

        var decision = c.Update(new FeedbackObservation(MarkerStatus.Found, 90, false, 0, 0), Ms(50));

        Assert.Equal(FeedbackCommand.None, decision.Command);
        Assert.True(c.IsHeld); // 90 % would mean "release", but without a zone nothing is decided
    }

    [Fact]
    public void Lost_for_too_long_releases_and_waits_then_resumes()
    {
        var c = new FeedbackController(Rules(lostMs: 200));
        Assert.Equal(FeedbackCommand.Press, c.Update(At(10), Ms(0)).Command);

        Assert.Equal(FeedbackCommand.None, c.Update(Seen(MarkerStatus.Lost), Ms(100)).Command); // short gap: keep holding
        Assert.True(c.IsHeld);
        var lost = c.Update(Seen(MarkerStatus.Lost), Ms(200));
        Assert.Equal(FeedbackCommand.Release, lost.Command);
        Assert.True(lost.Waiting);
        Assert.False(lost.TargetLost);
        Assert.True(c.IsWaiting);
        Assert.Equal(FeedbackCommand.None, c.Update(Seen(MarkerStatus.Ambiguous), Ms(250)).Command);

        var back = c.Update(At(10), Ms(300));
        Assert.Equal(FeedbackCommand.Press, back.Command);
        Assert.False(c.IsWaiting);
    }

    [Theory]
    [InlineData(FeedbackLostAction.ExitLoop)]
    [InlineData(FeedbackLostAction.StopMacro)]
    public void Lost_for_too_long_ends_the_loop_when_set_to(FeedbackLostAction action)
    {
        var c = new FeedbackController(Rules(lostMs: 200, whenLost: action));
        c.Update(At(10), Ms(0));

        Assert.False(c.Update(Seen(MarkerStatus.Lost), Ms(150)).TargetLost);
        Assert.True(c.Update(Seen(MarkerStatus.Lost), Ms(200)).TargetLost);
    }

    [Fact]
    public void The_lost_timer_counts_from_the_start_when_nothing_was_ever_seen()
    {
        var c = new FeedbackController(Rules(lostMs: 300, whenLost: FeedbackLostAction.ExitLoop), startedAt: Ms(1000));

        Assert.False(c.Update(Seen(MarkerStatus.Lost), Ms(1200)).TargetLost);
        Assert.True(c.Update(Seen(MarkerStatus.Lost), Ms(1300)).TargetLost);
    }

    [Fact]
    public void Minimum_time_between_changes_defers_a_change_until_allowed()
    {
        var c = new FeedbackController(Rules(minChangeMs: 100));
        Assert.Equal(FeedbackCommand.Press, c.Update(At(10), Ms(0)).Command);

        var early = c.Update(At(90), Ms(30));
        Assert.Equal(FeedbackCommand.None, early.Command);
        Assert.True(early.Deferred);
        Assert.True(c.IsHeld);
        Assert.Equal(FeedbackCommand.None, c.Update(At(90), Ms(99)).Command);
        Assert.Equal(FeedbackCommand.Release, c.Update(At(90), Ms(100)).Command);
    }

    [Fact]
    public void Safety_release_when_lost_ignores_the_minimum_change_time()
    {
        var c = new FeedbackController(Rules(minChangeMs: 1000, lostMs: 0));
        c.Update(At(10), Ms(0));

        var lost = c.Update(Seen(MarkerStatus.Lost), Ms(10));

        Assert.Equal(FeedbackCommand.Release, lost.Command);
        Assert.False(c.IsHeld);
    }

    [Fact]
    public void Tap_happens_once_each_time_the_marker_moves_into_the_region()
    {
        var c = new FeedbackController(Rules(before: FeedbackAction.Release, inside: FeedbackAction.Tap, after: FeedbackAction.Release));

        Assert.Equal(FeedbackCommand.None, c.Update(At(50), Ms(0)).Command);   // already inside at the start: no guess
        Assert.Equal(FeedbackCommand.None, c.Update(At(20), Ms(50)).Command);  // before (released already)
        Assert.Equal(FeedbackCommand.Tap, c.Update(At(50), Ms(100)).Command);  // moved in
        Assert.Equal(FeedbackCommand.None, c.Update(At(52), Ms(150)).Command); // still inside: no second tap
        Assert.Equal(FeedbackCommand.None, c.Update(At(80), Ms(200)).Command); // out
        Assert.Equal(FeedbackCommand.Tap, c.Update(At(55), Ms(250)).Command);  // in again
        Assert.False(c.IsHeld);
    }

    [Fact]
    public void A_deferred_tap_still_happens_while_the_marker_stays_in_the_region()
    {
        var c = new FeedbackController(Rules(before: FeedbackAction.Hold, inside: FeedbackAction.Tap, after: FeedbackAction.Release, minChangeMs: 100));
        Assert.Equal(FeedbackCommand.Press, c.Update(At(20), Ms(0)).Command);

        Assert.True(c.Update(At(50), Ms(40)).Deferred);
        Assert.Equal(FeedbackCommand.Tap, c.Update(At(50), Ms(100)).Command);
        Assert.False(c.IsHeld); // a tap ends released
    }

    [Fact]
    public void A_marker_that_changes_direction_is_followed_without_double_presses()
    {
        var c = new FeedbackController(Rules(deadBand: 2));
        var positions = new List<double>();
        for (var p = 0; p <= 100; p += 5) positions.Add(p);
        for (var p = 95; p >= 0; p -= 5) positions.Add(p);
        for (var p = 5; p <= 100; p += 5) positions.Add(p);

        var commands = positions.Select((p, i) => c.Update(At(p), Ms(i * 33)).Command).Where(x => x != FeedbackCommand.None).ToList();

        // Hold at the start, release after passing the zone, hold again on the way back, release again.
        Assert.Equal(new[] { FeedbackCommand.Press, FeedbackCommand.Release, FeedbackCommand.Press, FeedbackCommand.Release }, commands);
    }

    [Fact]
    public void A_zone_that_moves_is_followed()
    {
        var c = new FeedbackController(Rules());

        Assert.Equal(FeedbackCommand.Press, c.Update(At(50, zoneStart: 60, zoneEnd: 80), Ms(0)).Command);   // marker before the zone
        Assert.Equal(FeedbackCommand.Release, c.Update(At(50, zoneStart: 20, zoneEnd: 40), Ms(50)).Command); // zone moved past it
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Zone finder and sensor.
// ---------------------------------------------------------------------------------------------------------------------

public class FeedbackSensorTests
{
    private static BgraImage BarImage(BarFrame frame) => ScriptedBarCapture.Draw(frame).Crop(ScriptedBarCapture.Bar);

    [Fact]
    public void Zone_finder_reports_the_zone_edges_in_percent()
    {
        var zone = ZoneFinder.Find(BarImage(new BarFrame(null, ZoneStart: 40, ZoneEnd: 60)), ScriptedBarCapture.White, 10, 20, BarOrientation.Horizontal);

        Assert.True(zone.Found);
        Assert.Equal(40, zone.StartPercent, precision: 6);
        Assert.Equal(60, zone.EndPercent, precision: 6);
    }

    [Fact]
    public void A_marker_on_top_of_the_zone_doesnt_split_it_when_the_gap_is_bridged()
    {
        var image = BarImage(new BarFrame(50, ZoneStart: 40, ZoneEnd: 60));

        var bridged = ZoneFinder.Find(image, ScriptedBarCapture.White, 10, 20, BarOrientation.Horizontal, bridgeGapPixels: 7);
        var split = ZoneFinder.Find(image, ScriptedBarCapture.White, 10, 20, BarOrientation.Horizontal, bridgeGapPixels: 0);

        Assert.Equal(40, bridged.StartPercent, precision: 6);
        Assert.Equal(60, bridged.EndPercent, precision: 6);
        Assert.True(split.EndPercent - split.StartPercent < 10); // only one half without bridging
    }

    [Fact]
    public void Zone_finder_reports_a_missing_zone()
    {
        var zone = ZoneFinder.Find(BarImage(new BarFrame(50)), ScriptedBarCapture.White, 10, 20, BarOrientation.Horizontal);

        Assert.False(zone.Found);
    }

    [Fact]
    public void Zone_finder_works_on_a_vertical_bar()
    {
        var image = TestImages.Solid(20, 101, new RgbColor(60, 60, 60));
        image.FillRectangle(new ScreenRect(0, 25, 20, 26), ScriptedBarCapture.White); // rows 25..50

        var zone = ZoneFinder.Find(image, ScriptedBarCapture.White, 10, 20, BarOrientation.Vertical);

        Assert.Equal(25, zone.StartPercent, precision: 6);
        Assert.Equal(50, zone.EndPercent, precision: 6);
    }

    [Fact]
    public async Task Sensor_reads_marker_and_fixed_zone()
    {
        var sensor = new FeedbackSensor(Feedback.Loop(), new ScriptedBarCapture(_ => new BarFrame(25)), new FakeClock());

        var frame = await sensor.ReadAsync(CancellationToken.None);

        Assert.Equal(MarkerStatus.Found, frame.Marker.Status);
        Assert.Equal(25, frame.Observation.MarkerPercent!.Value, precision: 6);
        Assert.True(frame.Observation.IsTrusted);
        Assert.Equal(40, frame.Observation.ZoneStartPercent);
        Assert.Equal(60, frame.Observation.ZoneEndPercent);
    }

    [Fact]
    public async Task Sensor_detects_a_coloured_zone_even_with_the_marker_on_it()
    {
        var step = Feedback.Loop(s =>
        {
            s.ZoneMode = FeedbackZoneMode.DetectedByColor;
            s.ZoneColor = "#FFFFFF";
            s.ZoneTolerance = 10;
            s.ZoneMinPixels = 20;
        });
        var sensor = new FeedbackSensor(step, new ScriptedBarCapture(_ => new BarFrame(50, ZoneStart: 30, ZoneEnd: 70)), new FakeClock());

        var frame = await sensor.ReadAsync(CancellationToken.None);

        Assert.True(frame.Zone.Found);
        Assert.Equal(30, frame.Zone.StartPercent, precision: 6);
        Assert.Equal(70, frame.Zone.EndPercent, precision: 6);
        Assert.True(frame.Observation.IsTrusted);
    }

    [Fact]
    public async Task Two_similar_markers_are_reported_as_unclear()
    {
        var sensor = new FeedbackSensor(Feedback.Loop(), new ScriptedBarCapture(_ => new BarFrame(20, SecondMarker: 80)), new FakeClock());

        var frame = await sensor.ReadAsync(CancellationToken.None);

        Assert.Equal(MarkerStatus.Ambiguous, frame.Marker.Status);
        Assert.False(frame.Observation.IsTrusted);
    }

    [Fact]
    public async Task A_bar_area_partly_off_screen_is_refused()
    {
        var step = Feedback.Loop(s => s.RegionX = 500); // 500 + 201 > 600
        var sensor = new FeedbackSensor(step, new ScriptedBarCapture(_ => new BarFrame(50)), new FakeClock());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sensor.ReadAsync(CancellationToken.None));
        Assert.Contains("partly outside the screen", ex.Message);
    }

    [Fact]
    public void Sensor_refuses_a_bad_color()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new FeedbackSensor(Feedback.Loop(s => s.MarkerColor = "red"), new ScriptedBarCapture(_ => new BarFrame(50))));
    }

    [Fact]
    public void Stats_describe_frames_rate_timing_and_input_changes()
    {
        var stats = new FeedbackStats { Presses = 2, Releases = 1 };
        var frame = new FeedbackFrame(new ScreenFrame(new ScreenRect(0, 0, 1, 1), BgraImage.Create(1, 1)),
            new MarkerReading(MarkerStatus.Found, 50, 1, 1, null), ZoneReading.Fixed(40, 60),
            new FeedbackObservation(MarkerStatus.Found, 50, true, 40, 60), TimeSpan.FromMilliseconds(3), TimeSpan.FromMilliseconds(1));
        for (var i = 0; i < 10; i++) stats.Add(frame);

        var text = stats.Describe(TimeSpan.FromSeconds(0.5), TimeSpan.FromMilliseconds(50));

        Assert.Contains("10 frames in 0.5 s (20.0 per second)", text);
        Assert.Contains("capture 3.0 ms + detection 1.0 ms", text);
        Assert.Contains("3 input changes (2 press, 1 release, 0 tap", text);
        Assert.Contains("reacts about 4-54 ms", text);
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// The engine runner: capture → track → decide → act, with the fake screen and clock.
// ---------------------------------------------------------------------------------------------------------------------

public class FeedbackLoopEngineTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Frames 0-4 before the zone, 5-9 inside, 10-14 after, then before again.</summary>
    private static BarFrame Sweep(int i) => new(i switch { < 5 => 20, < 10 => 50, < 15 => 80, _ => 20 });

    [Fact]
    public async Task Holds_and_releases_as_the_marker_moves_and_sends_input_only_on_changes()
    {
        var capture = new ScriptedBarCapture(Sweep);
        var (engine, input, _, messages) = Feedback.Engine(capture);

        var result = await engine.RunAsync(Flow.Macro(Feedback.Loop(), Flow.Move(7))).WaitAsync(TestTimeout);

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "mouse Left down", "mouse Left up", "mouse Left down", "mouse Left up", "move 7,0" }, Feedback.Calls(input));
        Assert.Equal(20, capture.BarCaptures); // 1 s at 20 per second
        var summary = Feedback.Summary(messages);
        Assert.Contains("reached its max runtime of 1 s; continuing with the next step", summary);
        Assert.Contains("20 frames in 1.0 s (20.0 per second)", summary);
        Assert.Contains("3 input changes (2 press, 1 release, 0 tap)", summary);
    }

    [Fact]
    public async Task The_rate_limit_sets_the_number_of_captures()
    {
        var capture = new ScriptedBarCapture(_ => new BarFrame(20));
        var (engine, _, clock, _) = Feedback.Engine(capture);

        await engine.RunAsync(Flow.Macro(Feedback.Loop(s => s.MaxCapturesPerSecond = 10))).WaitAsync(TestTimeout);

        Assert.Equal(10, capture.BarCaptures);
        Assert.Equal(1000, clock.Elapsed.TotalMilliseconds, precision: 3);
    }

    [Fact]
    public async Task Max_runtime_can_stop_the_macro_and_releases_the_button()
    {
        var (engine, input, _, _) = Feedback.Engine(new ScriptedBarCapture(_ => new BarFrame(20)));

        var result = await engine.RunAsync(Flow.Macro(Feedback.Loop(s => s.WhenTimeUp = LoopLimitBehavior.StopMacro), Flow.Move(7)))
            .WaitAsync(TestTimeout);

        Assert.Equal(PlaybackOutcome.Failed, result.Outcome);
        Assert.Contains("max runtime", result.Error);
        Assert.Equal(new[] { "mouse Left down", "mouse Left up" }, Feedback.Calls(input));
    }

    [Fact]
    public async Task Done_condition_ends_the_loop_and_the_macro_continues()
    {
        var capture = new ScriptedBarCapture(i => new BarFrame(20, Done: i >= 6));
        var (engine, input, _, messages) = Feedback.Engine(capture);

        var result = await engine.RunAsync(Flow.Macro(Feedback.Loop(s => s.CompleteWhen = Feedback.DoneCondition()), Flow.Move(7)))
            .WaitAsync(TestTimeout);

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "mouse Left down", "mouse Left up", "move 7,0" }, Feedback.Calls(input));
        Assert.Equal(7, capture.BarCaptures); // the condition sees frame 6 before frame 7 is read
        Assert.Contains("finished: its \"done when\" condition is true", Feedback.Summary(messages));
    }

    [Fact]
    public async Task Fail_condition_stops_the_macro_by_default()
    {
        var capture = new ScriptedBarCapture(i => new BarFrame(20, Failed: i >= 3));
        var (engine, input, _, _) = Feedback.Engine(capture);

        var result = await engine.RunAsync(Flow.Macro(
                Feedback.Loop(s => { s.CompleteWhen = Feedback.DoneCondition(); s.FailWhen = Feedback.FailCondition(); }), Flow.Move(7)))
            .WaitAsync(TestTimeout);

        Assert.Equal(PlaybackOutcome.Failed, result.Outcome);
        Assert.Contains("\"failed when\" condition is true", result.Error);
        Assert.Equal(new[] { "mouse Left down", "mouse Left up" }, Feedback.Calls(input));
    }

    [Fact]
    public async Task Fail_condition_can_continue_with_the_next_step()
    {
        var (engine, input, _, messages) = Feedback.Engine(new ScriptedBarCapture(i => new BarFrame(20, Failed: i >= 3)));

        var result = await engine.RunAsync(Flow.Macro(
                Feedback.Loop(s => { s.FailWhen = Feedback.FailCondition(); s.WhenFailed = LoopLimitBehavior.ExitLoop; }), Flow.Move(7)))
            .WaitAsync(TestTimeout);

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal("move 7,0", Feedback.Calls(input)[^1]);
        Assert.Contains("continuing with the next step", Feedback.Summary(messages));
    }

    [Fact]
    public async Task When_done_and_failed_are_both_true_failure_wins()
    {
        var (engine, _, _, _) = Feedback.Engine(new ScriptedBarCapture(_ => new BarFrame(20, Done: true, Failed: true)));

        var result = await engine.RunAsync(Flow.Macro(
                Feedback.Loop(s => { s.CompleteWhen = Feedback.DoneCondition(); s.FailWhen = Feedback.FailCondition(); })))
            .WaitAsync(TestTimeout);

        Assert.Equal(PlaybackOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task Lost_target_can_end_the_loop_and_continue()
    {
        // Seen in frames 0-2 (0-100 ms), then gone. With 100 ms allowed, the frame at 200 ms ends the loop.
        var capture = new ScriptedBarCapture(i => new BarFrame(i < 3 ? 20 : null));
        var (engine, input, _, messages) = Feedback.Engine(capture);

        var result = await engine.RunAsync(Flow.Macro(
                Feedback.Loop(s => { s.LostTimeoutMs = 100; s.WhenTargetLost = FeedbackLostAction.ExitLoop; }), Flow.Move(7)))
            .WaitAsync(TestTimeout);

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "mouse Left down", "mouse Left up", "move 7,0" }, Feedback.Calls(input));
        Assert.Equal(5, capture.BarCaptures);
        Assert.Contains("lost its target", Feedback.Summary(messages));
    }

    [Fact]
    public async Task Lost_target_can_stop_the_macro()
    {
        var (engine, input, _, _) = Feedback.Engine(new ScriptedBarCapture(i => new BarFrame(i < 3 ? 20 : null)));

        var result = await engine.RunAsync(Flow.Macro(
                Feedback.Loop(s => { s.LostTimeoutMs = 100; s.WhenTargetLost = FeedbackLostAction.StopMacro; }), Flow.Move(7)))
            .WaitAsync(TestTimeout);

        Assert.Equal(PlaybackOutcome.Failed, result.Outcome);
        Assert.Contains("lost its target", result.Error);
        Assert.Equal(new[] { "mouse Left down", "mouse Left up" }, Feedback.Calls(input));
    }

    [Fact]
    public async Task Release_and_wait_lets_go_while_the_target_is_gone_and_holds_again_when_it_is_back()
    {
        var capture = new ScriptedBarCapture(i => new BarFrame(i is >= 5 and < 12 ? null : 20));
        var (engine, input, _, messages) = Feedback.Engine(capture);

        var result = await engine.RunAsync(Flow.Macro(Feedback.Loop(s => s.LostTimeoutMs = 100))).WaitAsync(TestTimeout);

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "mouse Left down", "mouse Left up", "mouse Left down", "mouse Left up" }, Feedback.Calls(input));
        Assert.Equal(20, capture.BarCaptures); // it kept looking until the max runtime
        Assert.Contains("target lost 1 time", Feedback.Summary(messages));
    }

    [Fact]
    public async Task A_short_gap_or_unclear_frames_change_nothing()
    {
        // Held before the zone. Frames 5-7: two similar markers, one of them right of the zone. Frame 8: nothing.
        var capture = new ScriptedBarCapture(i => i switch
        {
            >= 5 and < 8 => new BarFrame(20, SecondMarker: 90),
            8 => new BarFrame(null),
            _ => new BarFrame(20),
        });
        var (engine, input, _, messages) = Feedback.Engine(capture);

        await engine.RunAsync(Flow.Macro(Feedback.Loop())).WaitAsync(TestTimeout);

        Assert.Equal(new[] { "mouse Left down", "mouse Left up" }, Feedback.Calls(input)); // only the final release
        var summary = Feedback.Summary(messages);
        Assert.Contains("unclear in 15", summary); // 3 of 20 frames
        Assert.Contains("missing in 5", summary);  // 1 of 20 frames
    }

    [Fact]
    public async Task A_one_frame_jump_is_ignored()
    {
        // Frame 6 jumps from 20 % to 90 % for one frame only: with the glitch filter on, no release happens.
        var capture = new ScriptedBarCapture(i => new BarFrame(i == 6 ? 90 : 20));
        var (engine, input, _, messages) = Feedback.Engine(capture);

        await engine.RunAsync(Flow.Macro(Feedback.Loop(s => s.MaxJumpPercent = 35))).WaitAsync(TestTimeout);

        Assert.Equal(new[] { "mouse Left down", "mouse Left up" }, Feedback.Calls(input));
        Assert.Contains("1 jump ignored", Feedback.Summary(messages));
    }

    [Fact]
    public async Task A_real_jump_is_accepted_on_the_next_frame()
    {
        var capture = new ScriptedBarCapture(i => new BarFrame(i >= 6 ? 90 : 20));
        var (engine, input, _, _) = Feedback.Engine(capture);

        await engine.RunAsync(Flow.Macro(Feedback.Loop(s => s.MaxJumpPercent = 35))).WaitAsync(TestTimeout);

        Assert.Equal(new[] { "mouse Left down", "mouse Left up" }, Feedback.Calls(input).Take(2));
        Assert.Equal(2, Feedback.Calls(input).Length); // released at frame 7 (and nothing left to release at the end)
    }

    [Fact]
    public async Task Minimum_time_between_changes_is_respected()
    {
        // The marker flips sides every frame (every 50 ms); changes may only happen every 200 ms.
        var capture = new ScriptedBarCapture(i => new BarFrame(i % 2 == 0 ? 20 : 80));
        var (engine, input, clock, _) = Feedback.Engine(capture);
        input.Clock = clock;

        await engine.RunAsync(Flow.Macro(Feedback.Loop(s => s.MinInputChangeMs = 200))).WaitAsync(TestTimeout);

        // Changes made by the rules happen while frames run (before 1 s); a final release, if any, happens at 1 s.
        var times = input.Times.Where(t => t < TimeSpan.FromMilliseconds(1000)).ToList();
        Assert.True(times.Count is >= 4 and <= 6, $"{times.Count} changes");
        for (var i = 1; i < times.Count; i++)
            Assert.True(times[i] - times[i - 1] >= TimeSpan.FromMilliseconds(200), $"changes {i - 1} and {i} were {(times[i] - times[i - 1]).TotalMilliseconds} ms apart");
    }

    [Fact]
    public async Task Tap_presses_briefly_when_the_marker_moves_into_the_zone()
    {
        var capture = new ScriptedBarCapture(i => new BarFrame(i < 3 ? 20 : 50));
        var (engine, input, clock, messages) = Feedback.Engine(capture);
        input.Clock = clock;

        await engine.RunAsync(Flow.Macro(Feedback.Loop(s =>
        {
            s.WhenBeforeZone = FeedbackAction.Release;
            s.WhenInsideZone = FeedbackAction.Tap;
            s.WhenAfterZone = FeedbackAction.Release;
            s.TapMs = 40;
        }))).WaitAsync(TestTimeout);

        Assert.Equal(new[] { "mouse Left down", "mouse Left up" }, Feedback.Calls(input));
        Assert.Equal(TimeSpan.FromMilliseconds(40), input.Times[1] - input.Times[0]);
        Assert.Contains("(0 press, 0 release, 1 tap)", Feedback.Summary(messages));
    }

    [Fact]
    public async Task A_key_can_be_controlled_instead_of_a_mouse_button()
    {
        var (engine, input, _, _) = Feedback.Engine(new ScriptedBarCapture(Sweep));

        await engine.RunAsync(Flow.Macro(Feedback.Loop(s => { s.InputKind = FeedbackInputKind.Key; s.VirtualKey = 0x20; }))).WaitAsync(TestTimeout);

        Assert.Equal(new[] { "key 32 down", "key 32 up", "key 32 down", "key 32 up" }, Feedback.Calls(input));
    }

    [Fact]
    public async Task A_detected_zone_that_moves_is_followed()
    {
        // The marker stays at 50 %. The zone starts right of it (hold), then moves left of it (release).
        var capture = new ScriptedBarCapture(i => i < 5 ? new BarFrame(50, ZoneStart: 60, ZoneEnd: 80) : new BarFrame(50, ZoneStart: 20, ZoneEnd: 40));
        var (engine, input, _, _) = Feedback.Engine(capture);

        await engine.RunAsync(Flow.Macro(Feedback.Loop(s =>
        {
            s.ZoneMode = FeedbackZoneMode.DetectedByColor;
            s.ZoneColor = "#FFFFFF";
            s.ZoneTolerance = 10;
            s.ZoneMinPixels = 20;
        }))).WaitAsync(TestTimeout);

        Assert.Equal(new[] { "mouse Left down", "mouse Left up" }, Feedback.Calls(input));
    }

    [Fact]
    public async Task The_loop_takes_over_a_button_that_is_already_held()
    {
        var (engine, input, _, _) = Feedback.Engine(new ScriptedBarCapture(_ => new BarFrame(80)));

        await engine.RunAsync(Flow.Macro(
            new MouseClickStep { UseCurrentPosition = true, Action = MouseClickAction.Down },
            Feedback.Loop())).WaitAsync(TestTimeout);

        // Pressed by the click step, released when the loop starts (marker after the zone = released), nothing stuck.
        Assert.Equal(new[] { "mouse Left down", "mouse Left up" }, Feedback.Calls(input));
    }

    [Fact]
    public async Task Stop_inside_the_loop_releases_the_button_right_away()
    {
        var capture = new ScriptedBarCapture(_ => new BarFrame(20));
        var (engine, input, _, messages) = Feedback.Engine(capture);
        input.OnCall = call => { if (call == "mouse Left down") engine.Stop(); };

        var result = await engine.RunAsync(Flow.Macro(Feedback.Loop(s => s.MaxRuntimeMs = 3_600_000), Flow.Move(7))).WaitAsync(TestTimeout);

        Assert.Equal(PlaybackOutcome.Stopped, result.Outcome);
        Assert.Equal(new[] { "mouse Left down", "mouse Left up" }, Feedback.Calls(input));
        Assert.Equal(1, capture.BarCaptures);
        Assert.Contains("was stopped", Feedback.Summary(messages));
    }

    [Fact]
    public async Task Stop_with_a_real_clock_ends_a_long_loop_quickly()
    {
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, clock: null, capture: new ScriptedBarCapture(_ => new BarFrame(20)));
        var run = engine.RunAsync(Flow.Macro(Feedback.Loop(s => s.MaxRuntimeMs = 3_600_000)));

        await Task.Delay(200);
        Assert.True(engine.Stop());
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(PlaybackOutcome.Stopped, result.Outcome);
        Assert.Equal("mouse Left up", Feedback.Calls(input)[^1]);
    }

    [Fact]
    public async Task Pause_inside_the_loop_releases_and_presses_again_and_paused_time_does_not_count()
    {
        var capture = new ScriptedBarCapture(_ => new BarFrame(20));
        var (engine, input, clock, _) = Feedback.Engine(capture);
        var paused = 0;
        input.OnCall = call =>
        {
            if (call != "mouse Left down" || Interlocked.Increment(ref paused) != 1) return;
            Assert.True(engine.Pause());
            _ = Task.Run(async () =>
            {
                await Task.Delay(50);
                clock.Advance(TimeSpan.FromSeconds(5)); // 5 s pass while paused
                engine.Resume();
            });
        };

        var result = await engine.RunAsync(Flow.Macro(Feedback.Loop())).WaitAsync(TestTimeout);

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        // Held, released for the pause, pressed again on resume, released at the end.
        Assert.Equal(new[] { "mouse Left down", "mouse Left up", "mouse Left down", "mouse Left up" }, Feedback.Calls(input));
        Assert.Equal(20, capture.BarCaptures); // still a full second of frames: the 5 paused seconds didn't count
    }

    [Fact]
    public async Task Without_screen_capture_the_loop_fails_clearly()
    {
        var input = new FakeInput();
        var result = await new PlaybackEngine(input, new FakeClock()).RunAsync(Flow.Macro(Feedback.Loop())).WaitAsync(TestTimeout);

        Assert.Equal(PlaybackOutcome.Failed, result.Outcome);
        Assert.Contains("Screen capture isn't available", result.Error);
        Assert.Empty(input.Calls);
    }

    [Fact]
    public async Task A_bar_partly_off_screen_fails_and_nothing_stays_held()
    {
        var (engine, input, _, messages) = Feedback.Engine(new ScriptedBarCapture(_ => new BarFrame(20)));

        var result = await engine.RunAsync(Flow.Macro(Feedback.Loop(s => s.RegionX = 500))).WaitAsync(TestTimeout);

        Assert.Equal(PlaybackOutcome.Failed, result.Outcome);
        Assert.Contains("partly outside the screen", result.Error);
        Assert.Empty(input.Calls);
        Assert.Contains("ended because of an error", Feedback.Summary(messages));
    }

    [Fact]
    public async Task A_disabled_loop_is_skipped()
    {
        var capture = new ScriptedBarCapture(_ => new BarFrame(20));
        var (engine, input, _, _) = Feedback.Engine(capture);

        await engine.RunAsync(Flow.Macro(Feedback.Loop(s => s.Enabled = false), Flow.Move(7))).WaitAsync(TestTimeout);

        Assert.Equal(new[] { "move 7,0" }, Feedback.Calls(input));
        Assert.Equal(0, capture.BarCaptures);
    }

    [Fact]
    public async Task A_loop_inside_a_repeat_runs_each_turn_and_releases_between_turns()
    {
        var (engine, input, _, messages) = Feedback.Engine(new ScriptedBarCapture(_ => new BarFrame(20)));

        await engine.RunAsync(Flow.Macro(Flow.Times(2), Feedback.Loop(s => s.MaxRuntimeMs = 200), new EndRepeatStep())).WaitAsync(TestTimeout);

        Assert.Equal(new[] { "mouse Left down", "mouse Left up", "mouse Left down", "mouse Left up" }, Feedback.Calls(input));
        lock (messages) Assert.Equal(2, messages.Count(m => m.StartsWith("Feedback loop (step 2)")));
    }

    [Fact]
    public async Task Progress_text_shows_the_feedback_state()
    {
        var (engine, _, _, _) = Feedback.Engine(new ScriptedBarCapture(_ => new BarFrame(20)));
        var texts = new List<string?>();
        engine.ProgressChanged += (_, p) => { lock (texts) texts.Add(p.LoopText); };

        await engine.RunAsync(Flow.Macro(Feedback.Loop())).WaitAsync(TestTimeout);

        lock (texts) Assert.Contains(texts, t => t is not null && t.StartsWith("feedback ") && t.EndsWith("holding"));
    }

    [Fact]
    public async Task A_done_condition_that_sees_its_target_sets_the_last_found_spot()
    {
        var (engine, input, _, _) = Feedback.Engine(new ScriptedBarCapture(i => new BarFrame(80, Done: i >= 2)));

        await engine.RunAsync(Flow.Macro(
            Feedback.Loop(s => s.CompleteWhen = Feedback.DoneCondition()),
            new MouseClickStep { UseLastFoundText = true })).WaitAsync(TestTimeout);

        var area = ScriptedBarCapture.DoneArea;
        Assert.Contains($"move {area.X + 20},{area.Y + 20}", Feedback.Calls(input)); // centre of the green square (40×40 → 19.5 rounded)
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Validation, file format 6, old files, damaged data, factory and descriptions.
// ---------------------------------------------------------------------------------------------------------------------

public class FeedbackLoopValidationTests
{
    private static IReadOnlyList<ValidationIssue> Validate(FeedbackLoopStep step) => MacroValidator.ValidateStep(step);

    private static IEnumerable<ValidationIssue> Errors(FeedbackLoopStep step) => Validate(step).Where(i => i.Severity == IssueSeverity.Error);

    private static IEnumerable<ValidationIssue> Warnings(FeedbackLoopStep step) => Validate(step).Where(i => i.Severity == IssueSeverity.Warning);

    [Fact]
    public void A_new_feedback_loop_has_no_errors_or_warnings()
    {
        var step = (FeedbackLoopStep)MacroFactory.CreateStep(StepKind.FeedbackLoop);

        Assert.Empty(Validate(step));
        Assert.Empty(Errors(Feedback.Loop()));
    }

    public static IEnumerable<object[]> BadSettings()
    {
        object[] Case(string name, Action<FeedbackLoopStep> change, string property) => new object[] { name, change, property };

        yield return Case("area too small", s => s.RegionHeight = 4, "Region");
        yield return Case("area too big", s => s.RegionWidth = 20_001, "Region");
        yield return Case("bar too short along its direction", s => { s.RegionWidth = 9; s.RegionHeight = 30; }, "Region");
        yield return Case("vertical bar too short", s => { s.Orientation = BarOrientation.Vertical; s.RegionHeight = 9; }, "Region");
        yield return Case("marker color", s => s.MarkerColor = "red", nameof(FeedbackLoopStep.MarkerColor));
        yield return Case("marker tolerance low", s => s.MarkerTolerance = -1, nameof(FeedbackLoopStep.MarkerTolerance));
        yield return Case("marker tolerance high", s => s.MarkerTolerance = 256, nameof(FeedbackLoopStep.MarkerTolerance));
        yield return Case("marker min pixels", s => s.MarkerMinPixels = 0, nameof(FeedbackLoopStep.MarkerMinPixels));
        yield return Case("max jump low", s => s.MaxJumpPercent = 0, nameof(FeedbackLoopStep.MaxJumpPercent));
        yield return Case("max jump high", s => s.MaxJumpPercent = 101, nameof(FeedbackLoopStep.MaxJumpPercent));
        yield return Case("zone start after end", s => { s.ZoneStartPercent = 60; s.ZoneEndPercent = 40; }, nameof(FeedbackLoopStep.ZoneStartPercent));
        yield return Case("zone empty", s => { s.ZoneStartPercent = 50; s.ZoneEndPercent = 50; }, nameof(FeedbackLoopStep.ZoneStartPercent));
        yield return Case("zone below 0", s => s.ZoneStartPercent = -1, nameof(FeedbackLoopStep.ZoneStartPercent));
        yield return Case("zone above 100", s => s.ZoneEndPercent = 101, nameof(FeedbackLoopStep.ZoneStartPercent));
        yield return Case("zone color", s => { s.ZoneMode = FeedbackZoneMode.DetectedByColor; s.ZoneColor = "#12345"; }, nameof(FeedbackLoopStep.ZoneColor));
        yield return Case("zone tolerance", s => { s.ZoneMode = FeedbackZoneMode.DetectedByColor; s.ZoneTolerance = 300; }, nameof(FeedbackLoopStep.ZoneTolerance));
        yield return Case("zone min pixels", s => { s.ZoneMode = FeedbackZoneMode.DetectedByColor; s.ZoneMinPixels = 0; }, nameof(FeedbackLoopStep.ZoneMinPixels));
        yield return Case("dead band low", s => s.DeadBandPercent = -1, nameof(FeedbackLoopStep.DeadBandPercent));
        yield return Case("dead band high", s => s.DeadBandPercent = 26, nameof(FeedbackLoopStep.DeadBandPercent));
        yield return Case("rules never press", s => { s.WhenBeforeZone = FeedbackAction.Release; s.WhenInsideZone = FeedbackAction.Keep; s.WhenAfterZone = FeedbackAction.Release; }, "Rules");
        yield return Case("mouse button", s => s.MouseButton = (MacroMouseButton)9, nameof(FeedbackLoopStep.MouseButton));
        yield return Case("key 0", s => { s.InputKind = FeedbackInputKind.Key; s.VirtualKey = 0; }, nameof(FeedbackLoopStep.VirtualKey));
        yield return Case("key 300", s => { s.InputKind = FeedbackInputKind.Key; s.VirtualKey = 300; }, nameof(FeedbackLoopStep.VirtualKey));
        yield return Case("tap 0", s => s.TapMs = 0, nameof(FeedbackLoopStep.TapMs));
        yield return Case("tap too long", s => s.TapMs = 1001, nameof(FeedbackLoopStep.TapMs));
        yield return Case("captures 0", s => s.MaxCapturesPerSecond = 0, nameof(FeedbackLoopStep.MaxCapturesPerSecond));
        yield return Case("captures 121", s => s.MaxCapturesPerSecond = 121, nameof(FeedbackLoopStep.MaxCapturesPerSecond));
        yield return Case("min change negative", s => s.MinInputChangeMs = -1, nameof(FeedbackLoopStep.MinInputChangeMs));
        yield return Case("min change too long", s => s.MinInputChangeMs = 5001, nameof(FeedbackLoopStep.MinInputChangeMs));
        yield return Case("no max runtime", s => s.MaxRuntimeMs = 0, nameof(FeedbackLoopStep.MaxRuntimeMs));
        yield return Case("max runtime too short", s => s.MaxRuntimeMs = 99, nameof(FeedbackLoopStep.MaxRuntimeMs));
        yield return Case("max runtime over an hour", s => s.MaxRuntimeMs = 3_600_001, nameof(FeedbackLoopStep.MaxRuntimeMs));
        yield return Case("lost timeout negative", s => s.LostTimeoutMs = -1, nameof(FeedbackLoopStep.LostTimeoutMs));
        yield return Case("lost timeout too long", s => s.LostTimeoutMs = 60_001, nameof(FeedbackLoopStep.LostTimeoutMs));
        yield return Case("done condition without a search", s => s.CompleteWhen = new Condition(), nameof(FeedbackLoopStep.CompleteWhen));
        yield return Case("done condition with a bad search", s => s.CompleteWhen = new Condition { Check = new FindTextStep { Text = "" } }, nameof(FeedbackLoopStep.CompleteWhen) + ".Text");
        yield return Case("fail condition that is another step", s => s.FailWhen = new Condition { Check = new DelayStep() }, nameof(FeedbackLoopStep.FailWhen));
        yield return Case("fail condition with a bad color", s => s.FailWhen = new Condition { Check = new FindColorStep { Color = "nope", TimeoutMs = 0 } }, nameof(FeedbackLoopStep.FailWhen) + ".Color");
    }

    [Theory]
    [MemberData(nameof(BadSettings))]
    public void Every_setting_is_checked(string name, Action<FeedbackLoopStep> change, string property)
    {
        var step = Feedback.Loop(s => s.MinInputChangeMs = 50);
        change(step);

        var found = Errors(step).Any(i => i.Property == property);
        Assert.True(found, $"{name}: expected an error for {property}, got: {string.Join(" | ", Validate(step).Select(i => $"{i.Property}: {i.Message}"))}");
    }

    public static IEnumerable<object[]> UnknownOptions()
    {
        yield return new object[] { (Action<FeedbackLoopStep>)(s => s.Orientation = (BarOrientation)9) };
        yield return new object[] { (Action<FeedbackLoopStep>)(s => s.ZoneMode = (FeedbackZoneMode)9) };
        yield return new object[] { (Action<FeedbackLoopStep>)(s => s.InputKind = (FeedbackInputKind)9) };
        yield return new object[] { (Action<FeedbackLoopStep>)(s => s.WhenBeforeZone = (FeedbackAction)9) };
        yield return new object[] { (Action<FeedbackLoopStep>)(s => s.WhenInsideZone = (FeedbackAction)9) };
        yield return new object[] { (Action<FeedbackLoopStep>)(s => s.WhenAfterZone = (FeedbackAction)9) };
        yield return new object[] { (Action<FeedbackLoopStep>)(s => s.WhenTargetLost = (FeedbackLostAction)9) };
        yield return new object[] { (Action<FeedbackLoopStep>)(s => s.WhenTimeUp = (LoopLimitBehavior)9) };
        yield return new object[] { (Action<FeedbackLoopStep>)(s => s.WhenFailed = (LoopLimitBehavior)9) };
    }

    [Theory]
    [MemberData(nameof(UnknownOptions))]
    public void Unknown_option_values_are_errors(Action<FeedbackLoopStep> change)
    {
        var step = Feedback.Loop();
        change(step);

        Assert.Contains(Errors(step), i => i.Message.Contains("unknown option value"));
    }

    [Fact]
    public void Hints_are_warnings_not_errors()
    {
        Assert.Contains(Warnings(Feedback.Loop(s => s.MinInputChangeMs = 5)), i => i.Property == nameof(FeedbackLoopStep.MinInputChangeMs));
        Assert.Contains(Warnings(Feedback.Loop(s => s.MaxCapturesPerSecond = 90)), i => i.Property == nameof(FeedbackLoopStep.MaxCapturesPerSecond));
        Assert.Contains(Warnings(Feedback.Loop(s => { s.RegionWidth = 1000; s.RegionHeight = 500; })), i => i.Property == "Region");
        Assert.Contains(Warnings(Feedback.Loop(s => s.DeadBandPercent = 10)), i => i.Property == nameof(FeedbackLoopStep.DeadBandPercent));
        Assert.Contains(Warnings(Feedback.Loop(s => { s.WhenAfterZone = FeedbackAction.Keep; })), i => i.Property == "Rules");
        Assert.Contains(Warnings(Feedback.Loop(s => { s.InputKind = FeedbackInputKind.Key; s.VirtualKey = 0x1B; })), i => i.Property == nameof(FeedbackLoopStep.VirtualKey));
        Assert.Contains(Warnings(Feedback.Loop(s =>
        {
            s.ZoneMode = FeedbackZoneMode.DetectedByColor;
            s.ZoneColor = "#F00A0A";
        })), i => i.Property == nameof(FeedbackLoopStep.ZoneColor));
        Assert.Contains(Warnings(Feedback.Loop(s => s.CompleteWhen = new Condition { Check = new FindTextStep { Text = "Caught" } })),
            i => i.Property == nameof(FeedbackLoopStep.CompleteWhen) && i.Message.Contains("OCR"));
        var waiting = Feedback.DoneCondition();
        waiting.Search!.TimeoutMs = 5000;
        Assert.Contains(Warnings(Feedback.Loop(s => s.CompleteWhen = waiting)), i => i.Message.Contains("isn't used here"));
        Assert.Contains(Warnings(Feedback.Loop(s => s.WhenTimeUp = LoopLimitBehavior.StopMacro)), i => i.Property == nameof(FeedbackLoopStep.WhenTimeUp));
    }

    [Fact]
    public void Area_outside_the_screen_is_a_warning()
    {
        var issues = MacroValidator.ValidateStep(Feedback.Loop(s => s.RegionX = 5000), new ScreenBounds(0, 0, 1920, 1080));

        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.Property == "Region");
    }

    [Fact]
    public async Task A_macro_with_an_invalid_loop_is_refused_before_anything_runs()
    {
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, new FakeClock(), capture: new ScriptedBarCapture(_ => new BarFrame(20)));

        await Assert.ThrowsAsync<MacroValidationException>(() => engine.RunAsync(Flow.Macro(Flow.Move(1), Feedback.Loop(s => s.MaxRuntimeMs = 0))));
        Assert.Empty(input.Calls);
    }

    [Fact]
    public void Text_conditions_in_a_loop_count_as_using_ocr()
    {
        var macro = Flow.Macro(Feedback.Loop(s => s.FailWhen = new Condition { Check = new FindTextStep { Text = "Lost" } }));

        Assert.True(WorkflowSteps.UsesTextRecognition(macro));
        Assert.False(WorkflowSteps.UsesTextRecognition(Flow.Macro(Feedback.Loop(s => s.CompleteWhen = Feedback.DoneCondition()))));
    }

    [Fact]
    public void Factory_description_and_duplicate()
    {
        var step = Feedback.Loop(s => s.CompleteWhen = Feedback.DoneCondition());
        var copy = (FeedbackLoopStep)MacroFactory.DuplicateStep(step);

        Assert.NotEqual(step.Id, copy.Id);
        Assert.Equal(step.MaxRuntimeMs, copy.MaxRuntimeMs);
        Assert.NotNull(copy.CompleteWhen);
        Assert.Equal("Feedback loop", StepDescriber.DisplayName(StepKind.FeedbackLoop));
        Assert.Equal("Keep marker #FF0000 in zone 40–60 %: hold before, release after (left button; max 1 s, until done)", StepDescriber.Describe(step));
    }
}

public class FeedbackLoopFileTests
{
    [Fact]
    public void Format_6_round_trip_keeps_every_setting()
    {
        var step = Feedback.Loop(s =>
        {
            s.Orientation = BarOrientation.Vertical;
            s.RegionWidth = 20;
            s.RegionHeight = 201;
            s.ZoneMode = FeedbackZoneMode.DetectedByColor;
            s.ZoneColor = "#FFFFFF";
            s.WhenInsideZone = FeedbackAction.Tap;
            s.InputKind = FeedbackInputKind.Key;
            s.VirtualKey = 0x45;
            s.TapMs = 25;
            s.DeadBandPercent = 4;
            s.MinInputChangeMs = 80;
            s.MaxCapturesPerSecond = 45;
            s.MaxRuntimeMs = 12_345;
            s.WhenTimeUp = LoopLimitBehavior.StopMacro;
            s.LostTimeoutMs = 750;
            s.WhenTargetLost = FeedbackLostAction.ExitLoop;
            s.CompleteWhen = Feedback.DoneCondition();
            s.FailWhen = Feedback.FailCondition();
            s.FailWhen.Negate = true;
            s.WhenFailed = LoopLimitBehavior.ExitLoop;
        });

        var json = MacroFileFormat.Serialize(Flow.Macro(step));
        Assert.Contains("\"schemaVersion\": 6", json);
        Assert.Contains("\"type\": \"feedbackLoop\"", json);
        Assert.Contains("\"whenInsideZone\": \"Tap\"", json); // enums are stored as words, not numbers

        Assert.True(MacroFileFormat.TryParse(json, out var loaded, out var error), error);
        var parsed = loaded!;
        var back = Assert.IsType<FeedbackLoopStep>(Assert.Single(parsed.Steps));
        Assert.Equal(MacroJson.Serialize<MacroStep>(step), MacroJson.Serialize<MacroStep>(back));
        Assert.Equal(BarOrientation.Vertical, back.Orientation);
        Assert.True(back.FailWhen!.Negate);
        Assert.IsType<FindColorStep>(back.CompleteWhen!.Check);
        Assert.False(MacroValidator.Validate(parsed).HasErrors());
    }

    [Fact]
    public void Macros_without_a_feedback_loop_keep_their_older_format()
    {
        Assert.Contains("\"schemaVersion\": 1", MacroFileFormat.Serialize(Flow.Macro(Flow.Move(1))));
        Assert.Contains("\"schemaVersion\": 5", MacroFileFormat.Serialize(Flow.Macro(Flow.Times(2), Flow.Move(1), new EndRepeatStep())));
        Assert.Contains("\"schemaVersion\": 6", MacroFileFormat.Serialize(Flow.Macro(Flow.Times(2), Feedback.Loop(), new EndRepeatStep())));
    }

    [Fact]
    public void Unset_conditions_are_not_written()
    {
        var json = MacroFileFormat.Serialize(Flow.Macro(Feedback.Loop()));

        Assert.DoesNotContain("completeWhen", json);
        Assert.DoesNotContain("failWhen", json);
    }

    [Theory]
    [InlineData(1, """{ "type": "delay", "durationMs": 250 }""")]
    [InlineData(2, """{ "type": "keyPress", "virtualKey": 87, "holdMs": 120 }""")]
    [InlineData(3, """{ "type": "mouseClick", "button": "Left", "action": "Click", "x": 10, "y": 20 }""")]
    [InlineData(4, """{ "type": "findColor", "color": "#00FF00", "searchFullScreen": false, "regionX": 0, "regionY": 0, "regionWidth": 50, "regionHeight": 50, "goal": "Disappear", "timeoutMs": 0, "checkIntervalMs": 250 }""")]
    [InlineData(5, """{ "type": "repeat", "mode": "Times", "count": 2 }, { "type": "delay", "durationMs": 5 }, { "type": "endRepeat" }""")]
    public void Older_files_load_unchanged(int version, string steps)
    {
        var json = $$"""{ "schemaVersion": {{version}}, "macro": { "id": "6f9619ff-8b86-d011-b42d-00c04fc964ff", "name": "Old v{{version}}", "steps": [ {{steps}} ] } }""";

        Assert.True(MacroFileFormat.TryParse(json, out var loaded, out var error), error);
        var macro = loaded!;
        Assert.NotEmpty(macro.Steps);
        Assert.DoesNotContain(macro.Steps, s => s is FeedbackLoopStep);
        Assert.False(MacroValidator.Validate(macro).HasErrors());
        Assert.True(MacroDocument.RequiredSchemaVersion(macro) <= version);
    }

    [Fact]
    public void A_minimal_feedback_loop_from_a_file_gets_safe_defaults()
    {
        const string json = """{ "schemaVersion": 6, "macro": { "name": "x", "steps": [ { "type": "feedbackLoop", "regionX": 10, "regionY": 10 } ] } }""";

        Assert.True(MacroFileFormat.TryParse(json, out var loaded, out _));
        var macro = loaded!;
        var step = Assert.IsType<FeedbackLoopStep>(Assert.Single(macro.Steps));
        Assert.Equal(30_000, step.MaxRuntimeMs);
        Assert.Equal(FeedbackLostAction.ReleaseAndWait, step.WhenTargetLost);
        Assert.False(MacroValidator.Validate(macro).HasErrors());
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 6, "macro": { "name": "x", "steps": [ { "type": "feedbackLoop", "whenBeforeZone": "Explode" } ] } }""")]
    [InlineData("""{ "schemaVersion": 6, "macro": { "name": "x", "steps": [ { "type": "feedbackLoop", "maxRuntimeMs": "forever" } ] } }""")]
    [InlineData("""{ "schemaVersion": 6, "macro": { "name": "x", "steps": [ { "type": "feedbackLoop", "completeWhen": "the fish is caught" } ] } }""")]
    [InlineData("""{ "schemaVersion": 6, "macro": { "name": "x", "steps": [ { "type": "feedbackLoop", "failWhen": { "check": { "type": "runProgram", "path": "cmd.exe" } } } ] } }""")]
    [InlineData("""{ "schemaVersion": 6, "macro": { "name": "x", "steps": [ { "type": "feedbackLoop", "markerColor": 12 } ] } }""")]
    public void Damaged_feedback_data_is_refused_when_loading(string json)
    {
        Assert.False(MacroFileFormat.TryParse(json, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 6, "macro": { "name": "x", "steps": [ { "type": "feedbackLoop", "whenBeforeZone": 42 } ] } }""", "unknown option value")]
    [InlineData("""{ "schemaVersion": 6, "macro": { "name": "x", "steps": [ { "type": "feedbackLoop", "maxRuntimeMs": 999999999 } ] } }""", "max runtime")]
    [InlineData("""{ "schemaVersion": 6, "macro": { "name": "x", "steps": [ { "type": "feedbackLoop", "maxCapturesPerSecond": 100000 } ] } }""", "Captures per second")]
    [InlineData("""{ "schemaVersion": 6, "macro": { "name": "x", "steps": [ { "type": "feedbackLoop", "completeWhen": { "check": { "type": "feedbackLoop" } } } ] } }""", "can't use")]
    public void Loadable_but_unsafe_feedback_data_is_refused_by_the_validator(string json, string message)
    {
        Assert.True(MacroFileFormat.TryParse(json, out var macro, out var error), error);

        Assert.Contains(MacroValidator.Validate(macro!), i => i.Severity == IssueSeverity.Error && i.Message.Contains(message));
    }

    [Fact]
    public void Files_from_a_newer_version_than_the_current_one_are_refused()
    {
        Assert.True(MacroDocument.CurrentSchemaVersion >= 6);
        var json = $$"""{ "schemaVersion": {{MacroDocument.CurrentSchemaVersion + 1}}, "macro": { "name": "x", "steps": [] } }""";
        Assert.False(MacroFileFormat.TryParse(json, out _, out var error));
        Assert.Contains("newer version", error);
    }
}
