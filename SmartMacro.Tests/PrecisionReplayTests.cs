using SmartMacro.Core.Input;
using SmartMacro.Core.Models;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Recording;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Workflow;

namespace SmartMacro.Tests;

/// <summary>
/// "1:1 Precision Replay" upgrades: recording fidelity presets (Exact 1:1 / Smart Optimized / Clicks &amp; Keys Only),
/// relative mouse deltas for 3D game cameras, pre-click anchors / absolute-move precision, and the 1:1 guarantees
/// (zero drift, chords + auto-repeat, exact click hold, relative move files).
/// Fake input and a fake clock: they prove what SmartMacro sends and when, not how a game reacts.
/// </summary>
public class PrecisionReplayTests
{
    private const int W = 0x57;
    private const int Space = 0x20;

    private static RawInputEvent Down(long t, int x, int y, MacroMouseButton b = MacroMouseButton.Left) => new(t, RawInputKind.MouseDown, x, y, b);
    private static RawInputEvent Up(long t, int x, int y, MacroMouseButton b = MacroMouseButton.Left) => new(t, RawInputKind.MouseUp, x, y, b);
    private static RawInputEvent Move(long t, int x, int y) => new(t, RawInputKind.MouseMove, x, y);
    private static RawInputEvent KeyDown(long t, int vk) => new(t, RawInputKind.KeyDown, VirtualKey: vk);
    private static RawInputEvent KeyUp(long t, int vk) => new(t, RawInputKind.KeyUp, VirtualKey: vk);

    private static string Describe(IEnumerable<MacroStep> steps) => string.Join(" | ", steps.Select(StepDescriber.Describe));

    /// <summary>How long playback spends on these steps (delays + click / key holds), in ms.</summary>
    private static long Played(IEnumerable<MacroStep> steps) => steps.Sum(s => (long)(s switch
    {
        DelayStep d => d.DurationMs,
        MouseClickStep { Action: MouseClickAction.Click } c => c.HoldMs,
        KeyPressStep { Action: KeyAction.Press } k => k.HoldMs,
        _ => 0,
    }));

    private static RecordingOptions With(RecordingFidelity fidelity, RelativeMouseMode relative = RelativeMouseMode.Off) =>
        new() { Fidelity = fidelity, RelativeMouse = relative };

    /// <summary>A 1 kHz mouse moving 0 → 199 px in a straight line, then a 60 ms click at (200, 100).</summary>
    private static List<RawInputEvent> StraightPathThenClick()
    {
        var events = Enumerable.Range(0, 200).Select(t => Move(t, t, 100)).ToList();
        events.Add(Down(200, 200, 100));
        events.Add(Up(260, 200, 100));
        return events;
    }

    private static async Task<(FakeInput Input, FakeClock Clock)> PlayAsync(IEnumerable<MacroStep> steps)
    {
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };
        var result = await new PlaybackEngine(input, clock).RunAsync(Flow.Macro(steps.ToArray()));
        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        return (input, clock);
    }

    // ---------------- Upgrade 1: fidelity presets ----------------

    [Fact]
    public void Exact_1to1_samples_the_mouse_at_125_Hz_and_keeps_the_exact_click_spot_and_timing()
    {
        var steps = RecordingConverter.Convert(StraightPathThenClick(), With(RecordingFidelity.Exact1To1));

        var moves = steps.OfType<MouseMoveStep>().ToList();
        Assert.InRange(moves.Count, 25, 27);                       // one every 8 ms over 200 ms (+ the spot before the press)
        Assert.Equal((0, 100), (moves[0].X, moves[0].Y));
        Assert.Equal((199, 100), (moves[^1].X, moves[^1].Y));      // the last spot before the press is never thinned away
        Assert.All(steps.OfType<DelayStep>(), d => Assert.InRange(d.DurationMs, 1, 8));
        var click = Assert.Single(steps.OfType<MouseClickStep>());
        Assert.Equal((200, 100, MouseClickAction.Click, 60), (click.X, click.Y, click.Action, click.HoldDurationMs));
        Assert.Equal(260, Played(steps));                          // recorded time from the first move to the release
    }

    [Fact]
    public void Smart_optimized_simplifies_the_straight_path_but_keeps_the_click_and_its_time()
    {
        var steps = RecordingConverter.Convert(StraightPathThenClick(), With(RecordingFidelity.SmartOptimized));

        Assert.InRange(steps.OfType<MouseMoveStep>().Count(), 1, 3);
        var click = Assert.Single(steps.OfType<MouseClickStep>());
        Assert.Equal((200, 100, 60), (click.X, click.Y, click.HoldDurationMs));
        // Gaps under 15 ms are carried into the next Delay (only the very last one can't be): within one minimum delay.
        Assert.InRange(Played(steps), 260 - new RecordingOptions().MinDelayMs, 260);
    }

    [Fact]
    public void Clicks_and_keys_only_skips_hover_movement_but_keeps_clicks_and_drags()
    {
        var hover = RecordingConverter.Convert(StraightPathThenClick(), With(RecordingFidelity.ClicksAndKeysOnly));
        Assert.Empty(hover.OfType<MouseMoveStep>());
        Assert.Equal((200, 100), (Assert.Single(hover.OfType<MouseClickStep>()).X, hover.OfType<MouseClickStep>().Single().Y));

        var drag = RecordingConverter.Convert(new[]
        {
            Move(0, 10, 10), Move(50, 40, 10), Down(100, 50, 10), Move(150, 150, 10), Move(200, 250, 10), Up(250, 250, 10),
        }, With(RecordingFidelity.ClicksAndKeysOnly));
        Assert.Equal(new[] { MouseClickAction.Down, MouseClickAction.Up }, drag.OfType<MouseClickStep>().Select(c => c.Action));
        Assert.Contains(drag.OfType<MouseMoveStep>(), m => m.X == 250);  // the drag path is kept
        Assert.DoesNotContain(drag.OfType<MouseMoveStep>(), m => m.X == 40); // the hover before it isn't
    }

    [Fact]
    public void Fidelity_preset_overrides_the_old_individual_options()
    {
        var applied = RecordingFidelityPresets.Apply(
            new RecordingOptions { RecordMouseMoves = false, KeepHoldTime = false, SimplifyMousePath = true, StartX = 5, StartY = 6 },
            RecordingFidelity.Exact1To1);

        Assert.True(applied.RecordMouseMoves);
        Assert.True(applied.KeepHoldTime);
        Assert.False(applied.SimplifyMousePath);
        Assert.False(applied.CollapseMousePaths);
        Assert.Equal(RecordingFidelityPresets.ExactMoveIntervalMs, applied.MinMoveIntervalMs);
        Assert.Equal(MacroValidator.MaxHoldMs, applied.MaxTapDurationMs);
        Assert.Equal((5, 6), (applied.StartX, applied.StartY)); // the caller's other options are kept
    }

    [Fact]
    public void Settings_default_to_exact_1to1_and_old_no_movement_settings_become_clicks_and_keys()
    {
        Assert.Equal(RecordingFidelity.Exact1To1, new AppSettings().RecordingFidelity);
        Assert.Equal(RelativeMouseMode.CameraDrags, new AppSettings().RelativeMouseMode);
        Assert.Equal(RelativeMouseMode.Always, new AppSettings { UseRelativeMouseDeltas = true }.RelativeMouseMode);

        var old = new AppSettings { SchemaVersion = 3, RecordMouseMovement = false };
        old.Sanitize();
        Assert.Equal(RecordingFidelity.ClicksAndKeysOnly, old.RecordingFidelity);

        var oldWithMovement = new AppSettings { SchemaVersion = 3 };
        oldWithMovement.Sanitize();
        Assert.Equal(RecordingFidelity.Exact1To1, oldWithMovement.RecordingFidelity);
    }

    [Fact]
    public void Smart_recordings_use_the_fidelity_for_their_editable_parts()
    {
        var events = new List<CapturedInputEvent>();
        for (var t = 0; t < 200; t++) events.Add(new CapturedInputEvent(InputEvent.Move(t * 1_000L, 1, 0, t, 100)));
        events.Add(new CapturedInputEvent(InputEvent.ButtonDown(200_000, MacroMouseButton.Left, 200, 100)));
        events.Add(new CapturedInputEvent(InputEvent.ButtonUp(260_000, MacroMouseButton.Left, 200, 100)));

        var exact = SmartRecordingConverter.Convert(events, new SmartRecordingOptions { Fidelity = RecordingFidelity.Exact1To1 }).Steps;
        var clicksOnly = SmartRecordingConverter.Convert(events, new SmartRecordingOptions { Fidelity = RecordingFidelity.ClicksAndKeysOnly }).Steps;

        Assert.InRange(exact.OfType<MouseMoveStep>().Count(), 20, 30);
        Assert.Empty(clicksOnly.OfType<MouseMoveStep>());
        Assert.Equal(60, Assert.Single(exact.OfType<MouseClickStep>()).HoldDurationMs);
    }

    // ---------------- Upgrade 2: relative mouse deltas (3D camera) ----------------

    [Fact]
    public async Task Right_button_camera_turns_are_recorded_and_replayed_as_relative_deltas()
    {
        var steps = RecordingConverter.Convert(new[]
        {
            Move(0, 500, 500), Down(100, 500, 500, MacroMouseButton.Right),
            Move(120, 520, 500), Move(140, 540, 490), Move(160, 530, 480),
            Up(200, 530, 480, MacroMouseButton.Right),
        }, With(RecordingFidelity.Exact1To1, RelativeMouseMode.CameraDrags));

        var relative = steps.OfType<MouseMoveStep>().Where(m => m.UseRelativeDelta).Select(m => (m.DeltaX, m.DeltaY)).ToList();
        Assert.Equal(new[] { (20, 0), (20, -10), (-10, -10) }, relative);
        Assert.Equal((30, -20), (relative.Sum(d => d.DeltaX), relative.Sum(d => d.DeltaY))); // the whole turn, nothing lost
        var release = steps.OfType<MouseClickStep>().Single(c => c.Action == MouseClickAction.Up);
        Assert.True(release.UseCurrentPosition); // released where the camera turn left the cursor: no jump, no camera jerk

        var (input, _) = await PlayAsync(steps);
        Assert.Equal(new[] { "move 500,500", "move 500,500", "mouse Right down", "rel 20,0", "rel 20,-10", "rel -10,-10", "mouse Right up" }, input.Calls);
    }

    [Fact]
    public void Left_button_drags_keep_exact_positions_in_camera_drag_mode()
    {
        var steps = RecordingConverter.Convert(new[]
        {
            Down(0, 100, 100), Move(50, 140, 100), Move(100, 180, 100), Up(150, 180, 100),
        }, With(RecordingFidelity.Exact1To1, RelativeMouseMode.CameraDrags));

        Assert.All(steps.OfType<MouseMoveStep>(), m => Assert.False(m.UseRelativeDelta));
        Assert.False(steps.OfType<MouseClickStep>().Single(c => c.Action == MouseClickAction.Up).UseCurrentPosition);
    }

    [Fact]
    public void Always_relative_records_every_move_as_deltas_and_clicks_where_the_cursor_is()
    {
        var steps = RecordingConverter.Convert(new[]
        {
            Move(0, 960, 540), Move(20, 970, 545), Move(40, 1000, 530), Down(100, 1003, 531), Up(160, 1003, 531), Move(200, 990, 531),
        }, With(RecordingFidelity.Exact1To1, RelativeMouseMode.Always));

        var moves = steps.OfType<MouseMoveStep>().ToList();
        Assert.All(moves, m => Assert.True(m.UseRelativeDelta));
        // The deltas add up to the whole movement (960,540 → 990,531), including the 3 px wobble inside the click.
        Assert.Equal((30, -9), (moves.Sum(m => m.DeltaX), moves.Sum(m => m.DeltaY)));
        Assert.True(Assert.Single(steps.OfType<MouseClickStep>()).UseCurrentPosition);
    }

    [Fact]
    public async Task Playback_sends_relative_moves_and_big_turns_lose_nothing()
    {
        var (input, _) = await PlayAsync(new MacroStep[]
        {
            new MouseMoveStep { UseRelativeDelta = true, DeltaX = -27, DeltaY = 12, X = 960, Y = 540 },
            new MouseMoveStep { UseRelativeDelta = true, DeltaX = 400, DeltaY = -100 },
        });

        Assert.Equal("rel -27,12", input.Calls[0]);
        Assert.DoesNotContain(input.Calls, c => c.StartsWith("move ", StringComparison.Ordinal)); // never an absolute jump
        var sums = input.Calls.Skip(1).Select(c => c["rel ".Length..].Split(',').Select(int.Parse).ToArray()).ToList();
        Assert.Equal((400, -100), (sums.Sum(d => d[0]), sums.Sum(d => d[1])));
    }

    [Fact]
    public void Relative_mouse_moves_round_trip_through_a_macro_file_as_format_14()
    {
        var move = new MouseMoveStep { UseRelativeDelta = true, DeltaX = -37, DeltaY = 12, X = 960, Y = 540 };

        var json = MacroFileFormat.Serialize(Flow.Macro(move));

        Assert.Contains("\"useRelativeDelta\": true", json);
        Assert.Contains("\"schemaVersion\": 14", json);
        Assert.True(MacroFileFormat.TryParse(json, out var loaded, out var error), error);
        var back = Assert.IsType<MouseMoveStep>(Assert.Single(loaded!.Steps));
        Assert.Equal((true, -37, 12, 960, 540), (back.UseRelativeDelta, back.DeltaX, back.DeltaY, back.X, back.Y));

        // A normal move doesn't write the new fields and keeps the old format.
        var plain = MacroFileFormat.Serialize(Flow.Macro(new MouseMoveStep { X = 1, Y = 2 }));
        Assert.DoesNotContain("delta", plain, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"schemaVersion\": 1", plain);
    }

    [Fact]
    public void Relative_moves_are_validated_described_and_never_folded_by_clean_up()
    {
        var offScreen = new MouseMoveStep { UseRelativeDelta = true, DeltaX = 5, DeltaY = -5, X = -999_999, Y = 999_999 };
        Assert.DoesNotContain(MacroValidator.ValidateStep(offScreen), i => i.Severity == IssueSeverity.Error);
        Assert.Contains(MacroValidator.ValidateStep(new MouseMoveStep { UseRelativeDelta = true, DeltaX = MacroValidator.MaxRelativeMove + 1 }),
            i => i.Severity == IssueSeverity.Error);
        Assert.Equal("Turn the mouse by (+5, -5) · relative (3D camera)", StepDescriber.Describe(offScreen));

        var steps = new List<MacroStep>
        {
            new DelayStep { DurationMs = 10 }, new MouseMoveStep { UseRelativeDelta = true, DeltaX = 3 },
            new DelayStep { DurationMs = 10 }, new MouseMoveStep { UseRelativeDelta = true, DeltaX = 4 },
            new DelayStep { DurationMs = 10 }, new MouseMoveStep { UseRelativeDelta = true, DeltaX = 5 },
        };
        var cleaned = StepCleanup.CollapseMouseMoves(steps).Steps;
        Assert.Equal(12, cleaned.OfType<MouseMoveStep>().Sum(m => m.DeltaX));
        Assert.Equal(3, cleaned.OfType<MouseMoveStep>().Count());
    }

    // ---------------- Upgrade 3: pre-click anchor + absolute precision ----------------

    [Fact]
    public void The_press_always_happens_at_the_exact_spot_even_inside_throttled_movement()
    {
        // 1 kHz movement right up to the press: the press lands 2 ms after the last 8 ms sample.
        var events = Enumerable.Range(0, 51).Select(t => Move(t, 300 + t, 200)).ToList();
        events.Add(Down(52, 352, 201));
        events.Add(Move(53, 360, 201)); // the hand keeps moving while pressing: a drag
        events.Add(Move(70, 420, 201));
        events.Add(Up(90, 420, 201));

        foreach (var fidelity in Enum.GetValues<RecordingFidelity>())
        {
            var steps = RecordingConverter.Convert(events, With(fidelity));
            var press = steps.OfType<MouseClickStep>().First();
            Assert.Equal((MouseClickAction.Down, 352, 201), (press.Action, press.X, press.Y));
        }
    }

    [Theory]
    [InlineData(-2560, 6400)]  // a 1440p monitor left of a 4K main monitor
    [InlineData(0, 3840)]      // one 4K monitor
    [InlineData(0, 2560)]      // one 1440p monitor
    public void Every_pixel_survives_the_65535_absolute_coordinates(int origin, int size)
    {
        for (var x = origin; x < origin + size; x++)
            Assert.Equal(x, AbsoluteMouseMath.ToPixel(AbsoluteMouseMath.Normalize(x, origin, size), origin, size));
    }

    // ---------------- Upgrade 4: the 1:1 guarantees ----------------

    [Fact]
    public void A_hundred_sub_millisecond_events_have_zero_rounding_loss()
    {
        // 100 events 0.37 ms apart on a 10 MHz Stopwatch: 37 ms in total.
        var clock = new RecordingClock(startTimestamp: 0, ticksPerSecond: 10_000_000);
        var times = Enumerable.Range(0, 101).Select(i => clock.ToMilliseconds(i * 3_700L)).ToList();
        var gaps = times.Zip(times.Skip(1), (a, b) => b - a).ToList();

        Assert.Equal(37, times[^1]);
        Assert.Equal(37, gaps.Sum());

        var quantizer = new DelayQuantizer();
        Assert.Equal(37, Enumerable.Range(0, 100).Sum(_ => quantizer.Next(0.37)));
    }

    [Fact]
    public async Task Holding_W_pressing_Space_and_W_auto_repeat_give_the_exact_chord_sequence()
    {
        var events = new[]
        {
            KeyDown(0, W), KeyDown(300, W) /* repeat */, KeyDown(350, Space), KeyDown(366, W) /* repeat */,
            KeyDown(400, W) /* repeat */, KeyUp(420, Space), KeyDown(433, W) /* repeat */, KeyUp(1000, W),
        };

        var steps = RecordingConverter.Convert(events, With(RecordingFidelity.Exact1To1));

        Assert.Equal("Hold down W | Wait 350 ms | Press Space (hold 70 ms) | Wait 580 ms | Release W", Describe(steps));
        var (input, _) = await PlayAsync(steps);
        Assert.Equal(new[] { "key 87 down", "key 32 down", "key 32 up", "key 87 up" }, input.Calls);
        Assert.Equal(new[] { 0.0, 350, 420, 1000 }, input.Times.Select(t => Math.Round(t.TotalMilliseconds, 3)));
    }

    [Fact]
    public async Task A_550ms_left_button_hold_is_recorded_and_replayed_for_550ms()
    {
        var steps = RecordingConverter.Convert(new[] { Down(0, 640, 360), Up(550, 640, 360) }, With(RecordingFidelity.Exact1To1));

        var click = Assert.IsType<MouseClickStep>(Assert.Single(steps));
        Assert.Equal((MacroMouseButton.Left, MouseClickAction.Click, 550), (click.Button, click.Action, click.HoldDurationMs));

        var (input, _) = await PlayAsync(steps);
        Assert.Equal(new[] { "move 640,360", "mouse Left down", "mouse Left up" }, input.Calls);
        Assert.Equal(550, (input.Times[2] - input.Times[1]).TotalMilliseconds, 3);
    }
}
