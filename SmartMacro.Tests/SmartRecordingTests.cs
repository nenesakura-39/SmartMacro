using SmartMacro.Core.Models;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Recording;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Validation;

namespace SmartMacro.Tests;

/// <summary>
/// Stage 1: one smart recording that keeps cursor positions and relative mouse movement on one timeline.
/// These use fake recorded data, a fake clock and a fake input simulator. They prove what SmartMacro decides and
/// sends, and when — NOT that a real app or game accepts the simulated input. That needs the manual tests.
/// </summary>
public class SmartRecordingTests
{
    private const int W = 0x57;
    private const int WScan = 0x11;
    private const int A = 0x41;

    // ---------------- Helpers ----------------

    /// <summary>A mouse move where the visible cursor followed the mouse.</summary>
    private static CapturedInputEvent Free(long us, int dx, int dy, int x, int y) => new(InputEvent.Move(us, dx, dy, x, y));

    /// <summary>A mouse move while the cursor was hidden (a game turning the camera).</summary>
    private static CapturedInputEvent Hidden(long us, int dx, int dy, int x = 960, int y = 540) =>
        new(InputEvent.Move(us, dx, dy, x, y), CursorFlags.Hidden);

    /// <summary>A mouse move while the cursor stayed in place (visible, but locked by a game).</summary>
    private static CapturedInputEvent Locked(long us, int dx, int dy, int x, int y) => new(InputEvent.Move(us, dx, dy, x, y));

    private static CapturedInputEvent Down(long us, int x, int y, MacroMouseButton b = MacroMouseButton.Left) => new(InputEvent.ButtonDown(us, b, x, y));

    private static CapturedInputEvent Up(long us, int x, int y, MacroMouseButton b = MacroMouseButton.Left) => new(InputEvent.ButtonUp(us, b, x, y));

    private static CapturedInputEvent KeyDown(long us, int vk, int scan = 0, bool ext = false) => new(InputEvent.KeyDown(us, vk, scan, ext));

    private static CapturedInputEvent KeyUp(long us, int vk, int scan = 0, bool ext = false) => new(InputEvent.KeyUp(us, vk, scan, ext));

    private static Macro MacroWith(IEnumerable<MacroStep> steps)
    {
        var m = new Macro { Name = "Smart" };
        m.Steps.AddRange(steps);
        return m;
    }

    private static double Ms(TimeSpan t) => Math.Round(t.TotalMilliseconds, 3);

    /// <summary>10 free moves to the right, 10 ms apart, ending at (startX + 50, y).</summary>
    private static IEnumerable<CapturedInputEvent> FreeLine(long startUs, int startX, int y)
    {
        for (var k = 0; k < 10; k++)
            yield return Free(startUs + k * 10_000L, 5, 0, startX + 5 * (k + 1), y);
    }

    /// <summary>
    /// Free cursor to (500, 300), right button down, 300 ms of camera turning with the cursor locked in place,
    /// right button up, free cursor again.
    /// </summary>
    private static List<CapturedInputEvent> RightDragCamera()
    {
        var events = new List<CapturedInputEvent>();
        events.AddRange(FreeLine(0, 450, 300));                                   // t 0..90 ms, x 455..500
        events.Add(Down(100_000, 500, 300, MacroMouseButton.Right));
        for (var k = 0; k < 300; k++) events.Add(Locked(101_000 + k * 1_000L, 4, 0, 500, 300)); // t 101..400 ms
        events.Add(Up(400_000, 500, 300, MacroMouseButton.Right));
        events.AddRange(FreeLine(500_000, 500, 300));                             // t 500..590 ms, x 505..550
        return events;
    }

    // ---------------- Desktop-only recordings ----------------

    [Fact]
    public void Free_cursor_recording_becomes_normal_editable_steps()
    {
        var events = new List<CapturedInputEvent>();
        events.AddRange(FreeLine(0, 150, 100));                      // ends at (200, 100)
        events.Add(Down(200_000, 200, 100));
        events.Add(Up(280_000, 200, 100));
        events.Add(KeyDown(400_000, A, 0x1E));
        events.Add(KeyUp(450_000, A, 0x1E));

        var result = SmartRecordingConverter.Convert(events);

        Assert.Equal(0, result.RecordedInputParts);
        Assert.Equal(0, result.RelativeSegments);
        Assert.DoesNotContain(result.Steps, s => s is InputSequenceStep);
        Assert.Contains(result.Steps, s => s is MouseMoveStep);
        var click = Assert.Single(result.Steps.OfType<MouseClickStep>());
        Assert.Equal((MouseClickAction.Click, 200, 100, 80), (click.Action, click.X, click.Y, click.HoldMs));
        var key = Assert.Single(result.Steps.OfType<KeyPressStep>());
        Assert.Equal((A, KeyAction.Press, 50), (key.VirtualKey, key.Action, key.HoldMs));
    }

    [Fact]
    public void Pushing_the_cursor_against_a_screen_edge_is_not_mistaken_for_camera_movement()
    {
        var events = new List<CapturedInputEvent>();
        for (var k = 0; k < 10; k++) events.Add(Free(k * 10_000L, -5, 0, 45 - 5 * k, 300)); // x 45..0
        for (var k = 0; k < 100; k++)
            events.Add(new CapturedInputEvent(InputEvent.Move(100_000 + k * 1_000L, -5, 0, 0, 300), CursorFlags.AtEdge));
        events.Add(Down(250_000, 0, 300));
        events.Add(Up(300_000, 0, 300));

        var result = SmartRecordingConverter.Convert(events);

        Assert.Equal(0, result.RelativeSegments);
        Assert.DoesNotContain(result.Steps, s => s is InputSequenceStep);
    }

    [Fact]
    public void Cursor_still_hidden_from_typing_for_a_moment_is_ignored()
    {
        // Windows hides the cursor while you type; it can still be hidden for the first move afterwards.
        var events = new List<CapturedInputEvent>
        {
            KeyDown(0, A, 0x1E), KeyUp(40_000, A, 0x1E),
            Hidden(500_000, 5, 0, 105, 100), Hidden(501_000, 5, 0, 110, 100),
        };
        for (var k = 0; k < 20; k++) events.Add(Free(510_000 + k * 10_000L, 5, 0, 115 + 5 * k, 100));

        var result = SmartRecordingConverter.Convert(events);

        Assert.DoesNotContain(result.Steps, s => s is InputSequenceStep);
        Assert.Equal(0, result.RelativeSegments);
    }

    // ---------------- Camera (relative) recordings ----------------

    [Fact]
    public void Hidden_cursor_movement_becomes_one_relative_step_with_every_delta_kept()
    {
        var events = Enumerable.Range(0, 50).Select(k => Hidden(1_000_000 + k * 1_000L, 3, -1)).ToList();

        var result = SmartRecordingConverter.Convert(events);

        var step = Assert.IsType<InputSequenceStep>(Assert.Single(result.Steps));
        Assert.Equal(50, step.Events.Count);
        Assert.All(step.Events, e => Assert.Equal((InputEventKind.MouseMove, 3, -1), (e.Kind, e.Dx, e.Dy)));
        Assert.Equal(0, step.Events[0].TimeUs);              // starts with the first event, no leading wait
        Assert.Equal(49_000, step.Events[^1].TimeUs);
        var segment = Assert.Single(step.MouseSegments!);
        Assert.Equal((0L, MouseMovementMode.Relative, MouseContextEvidence.HiddenCursor), (segment.StartUs, segment.Detected, segment.Evidence));
    }

    [Fact]
    public void Right_drag_camera_keeps_the_button_hold_and_switches_mouse_mode_at_the_right_moments()
    {
        var result = SmartRecordingConverter.Convert(RightDragCamera());

        // Short cursor-only movement around the drag stays in the same recorded-input step.
        var step = Assert.IsType<InputSequenceStep>(Assert.Single(result.Steps));
        var segments = step.MouseSegments!;
        Assert.Equal(new[] { MouseMovementMode.CursorPosition, MouseMovementMode.Relative, MouseMovementMode.CursorPosition },
            segments.Select(s => s.Detected));
        Assert.Equal(new[] { 0L, 101_000, 500_000 }, segments.Select(s => s.StartUs));
        Assert.Equal(MouseContextEvidence.LockedCursor, segments[1].Evidence);

        // The right button is a real hold of 300 ms, not an instant click followed by unrelated movement.
        var down = step.Events.Single(e => e.Kind == InputEventKind.MouseDown);
        var up = step.Events.Single(e => e.Kind == InputEventKind.MouseUp);
        Assert.Equal((MacroMouseButton.Right, 100_000L, 400_000L), (down.Button, down.TimeUs, up.TimeUs));
        var downIndex = step.Events.IndexOf(down);
        var upIndex = step.Events.IndexOf(up);
        Assert.Equal(300, step.Events.Skip(downIndex + 1).Take(upIndex - downIndex - 1).Count(e => e.Kind == InputEventKind.MouseMove && e.Dx == 4));
        Assert.All(step.Events.Where(e => e.IsMouse), e => Assert.True(e.HasPosition));
    }

    [Fact]
    public async Task Right_drag_camera_plays_positions_then_relative_then_positions_never_both()
    {
        var step = Assert.IsType<InputSequenceStep>(Assert.Single(SmartRecordingConverter.Convert(RightDragCamera()).Steps));
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };

        var result = await new PlaybackEngine(input, clock).RunAsync(MacroWith(new[] { step }));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        var calls = input.Calls;
        var down = calls.IndexOf("mouse Right down");
        var up = calls.IndexOf("mouse Right up");
        Assert.Equal("move 500,300", calls[down - 1]);                         // pressed at the recorded spot
        Assert.Equal(300, calls.Count(c => c == "rel 4,0"));
        Assert.All(calls.Take(down), c => Assert.StartsWith("move ", c));     // cursor part: positions only
        Assert.All(calls.Skip(down + 1).Take(up - down - 1), c => Assert.Equal("rel 4,0", c)); // camera part: relative only
        Assert.All(calls.Skip(up + 1), c => Assert.StartsWith("move ", c));   // cursor again
        Assert.Equal("move 550,300", calls[^1]);

        // Hold duration and spacing are kept.
        Assert.Equal(100.0, Ms(input.Times[down]));
        Assert.Equal(400.0, Ms(input.Times[up]));
    }

    [Fact]
    public async Task A_mouse_part_can_be_switched_by_the_user()
    {
        var step = Assert.IsType<InputSequenceStep>(Assert.Single(SmartRecordingConverter.Convert(RightDragCamera()).Steps));
        step.MouseSegments![1].Choice = MouseModeChoice.CursorPosition;
        var input = new FakeInput();

        await new PlaybackEngine(input, new FakeClock()).RunAsync(MacroWith(new[] { step }));

        Assert.DoesNotContain(input.Calls, c => c.StartsWith("rel "));

        step.MouseSegments!.ForEach(s => s.Choice = MouseModeChoice.Relative);
        var relativeInput = new FakeInput();
        await new PlaybackEngine(relativeInput, new FakeClock()).RunAsync(MacroWith(new[] { step }));

        Assert.DoesNotContain(relativeInput.Calls, c => c.StartsWith("move "));
        Assert.Equal(320, relativeInput.Calls.Count(c => c.StartsWith("rel ")));
    }

    [Fact]
    public async Task Holding_W_while_the_camera_turns_keeps_order_scan_code_and_hold_time()
    {
        var events = new List<CapturedInputEvent> { KeyDown(0, W, WScan) };
        for (var k = 0; k <= 190; k++)
        {
            events.Add(Hidden(10_000 + k * 1_000L, 1, 0));
            if (k == 90) events.Add(KeyUp(100_000, W, WScan)); // W released while the camera keeps turning
        }

        var step = Assert.IsType<InputSequenceStep>(Assert.Single(SmartRecordingConverter.Convert(events).Steps));
        Assert.Equal(InputEvent.KeyDown(0, W, WScan), step.Events[0]);
        var upIndex = step.Events.FindIndex(e => e.Kind == InputEventKind.KeyUp);
        Assert.Equal(100_000, step.Events[upIndex].TimeUs);
        Assert.Equal(100_000, step.Events[upIndex - 1].TimeUs); // the move at the same moment stays before it
        Assert.Equal(InputEventKind.MouseMove, step.Events[upIndex + 1].Kind);

        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };
        await new PlaybackEngine(input, clock).RunAsync(MacroWith(new[] { step }));

        Assert.Equal("key 87 down sc17", input.Calls[0]);
        var keyUp = input.Calls.IndexOf("key 87 up sc17");
        Assert.Equal(100.0, Ms(input.Times[keyUp]));
        Assert.Equal(191, input.Calls.Count(c => c == "rel 1,0"));
        Assert.Equal("rel 1,0", input.Calls[^1]);
    }

    [Fact]
    public async Task Clicks_around_camera_movement_stay_editable_and_the_timeline_is_kept()
    {
        var events = new List<CapturedInputEvent> { Down(0, 100, 100), Up(50_000, 100, 100) };
        for (var k = 0; k < 300; k++) events.Add(Hidden(100_000 + k * 1_000L, 2, 0)); // t 100..399 ms
        events.Add(Down(500_000, 300, 300));
        events.Add(Up(560_000, 300, 300));

        var result = SmartRecordingConverter.Convert(events);

        Assert.Equal((2, 1), (result.EditableParts, result.RecordedInputParts));
        Assert.Collection(result.Steps,
            s => Assert.Equal((100, 100, 50), (((MouseClickStep)s).X, ((MouseClickStep)s).Y, ((MouseClickStep)s).HoldMs)),
            s => Assert.Equal(50, Assert.IsType<DelayStep>(s).DurationMs),
            s => Assert.Equal(MouseMovementMode.Relative, Assert.Single(Assert.IsType<InputSequenceStep>(s).MouseSegments!).Detected),
            s => Assert.Equal(101, Assert.IsType<DelayStep>(s).DurationMs),
            s => Assert.Equal((300, 300, 60), (((MouseClickStep)s).X, ((MouseClickStep)s).Y, ((MouseClickStep)s).HoldMs)));

        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };
        await new PlaybackEngine(input, clock).RunAsync(MacroWith(result.Steps));

        var firstRel = input.Calls.IndexOf("rel 2,0");
        var lastRel = input.Calls.LastIndexOf("rel 2,0");
        var secondClick = input.Calls.LastIndexOf("mouse Left down");
        Assert.Equal(100.0, Ms(input.Times[firstRel]));
        Assert.Equal(399.0, Ms(input.Times[lastRel]));
        Assert.Equal(500.0, Ms(input.Times[secondClick]));
        Assert.Equal("move 300,300", input.Calls[secondClick - 1]);
        Assert.Equal(560.0, Ms(clock.Elapsed));
    }

    [Fact]
    public void Events_with_equal_timestamps_keep_their_recorded_order()
    {
        var events = new List<CapturedInputEvent>();
        for (var k = 0; k < 100; k++)
        {
            var t = k * 1_000L;
            events.Add(Hidden(t, 1, 0));
            if (k == 50)
            {
                events.Add(new CapturedInputEvent(InputEvent.ButtonDown(t, MacroMouseButton.Left, 960, 540), CursorFlags.Hidden));
                events.Add(Hidden(t, 0, 2));
            }

            if (k == 80) events.Add(new CapturedInputEvent(InputEvent.ButtonUp(t, MacroMouseButton.Left, 960, 540), CursorFlags.Hidden));
        }

        var step = Assert.IsType<InputSequenceStep>(Assert.Single(SmartRecordingConverter.Convert(events).Steps));
        var at50 = step.Events.Where(e => e.TimeUs == 50_000).Select(e => (e.Kind, e.Dx, e.Dy)).ToList();
        Assert.Equal(new[] { (InputEventKind.MouseMove, 1, 0), (InputEventKind.MouseDown, 0, 0), (InputEventKind.MouseMove, 0, 2) }, at50);
    }

    // ---------------- Clean-up ----------------

    [Fact]
    public void Stop_shortcut_is_trimmed_from_the_end()
    {
        var events = new List<CapturedInputEvent>();
        events.AddRange(FreeLine(0, 0, 10));
        events.Add(Down(200_000, 50, 10));
        events.Add(Up(250_000, 50, 10));
        events.Add(KeyDown(900_000, 0x11, 0x1D));   // Ctrl
        events.Add(KeyDown(950_000, 0x10, 0x2A));   // Shift
        events.Add(Free(960_000, 1, 0, 51, 10));
        events.Add(KeyDown(1_000_000, 0x78, 0x43)); // F9

        var result = SmartRecordingConverter.Convert(events, new SmartRecordingOptions { TrimTrailingKeys = HotkeyBinding.DefaultRecord.AllKeys() });

        Assert.DoesNotContain(result.Steps, s => s is KeyPressStep);
        Assert.IsType<MouseClickStep>(result.Steps[^1]);
    }

    [Fact]
    public void Early_releases_auto_repeat_and_duplicate_presses_are_dropped()
    {
        var events = new List<CapturedInputEvent>
        {
            KeyUp(0, A, 0x1E),                       // was down before recording started
            Up(1_000, 10, 10, MacroMouseButton.Right),
            KeyDown(10_000, A, 0x1E),
            KeyDown(40_000, A, 0x1E),                // auto-repeat
            KeyUp(60_000, A, 0x1E),
            Down(100_000, 10, 10),
            Down(110_000, 10, 10),                   // duplicate
            Up(150_000, 10, 10),
        };

        var steps = SmartRecordingConverter.Convert(events).Steps;

        var key = Assert.Single(steps.OfType<KeyPressStep>());
        Assert.Equal((KeyAction.Press, 50), (key.Action, key.HoldMs));
        var click = Assert.Single(steps.OfType<MouseClickStep>());
        Assert.Equal((MouseClickAction.Click, MacroMouseButton.Left, 50), (click.Action, click.Button, click.HoldMs));
    }

    [Fact]
    public void Anything_still_held_at_the_end_is_released()
    {
        var events = new List<CapturedInputEvent> { KeyDown(0, W, WScan), Down(0, 960, 540, MacroMouseButton.Right) };
        for (var k = 0; k < 100; k++) events.Add(Hidden(1_000 + k * 1_000L, 2, 0));

        var step = Assert.IsType<InputSequenceStep>(Assert.Single(SmartRecordingConverter.Convert(events).Steps));

        Assert.Contains(InputEvent.KeyUp(100_000, W, WScan), step.Events);
        Assert.Contains(step.Events, e => e.Kind == InputEventKind.MouseUp && e.Button == MacroMouseButton.Right && e.TimeUs == 100_000);
        Assert.DoesNotContain(MacroValidator.ValidateStep(step), i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void Left_and_right_modifiers_become_the_specific_keys_in_editable_steps()
    {
        var events = new List<CapturedInputEvent>
        {
            KeyDown(0, 0x10, 0x36), KeyUp(30_000, 0x10, 0x36),             // right Shift
            KeyDown(100_000, 0x11, 0x1D, ext: true), KeyUp(130_000, 0x11, 0x1D, ext: true), // right Ctrl
            KeyDown(200_000, 0x12, 0x38), KeyUp(230_000, 0x12, 0x38),       // left Alt
        };

        var keys = SmartRecordingConverter.Convert(events).Steps.OfType<KeyPressStep>().Select(k => k.VirtualKey);

        Assert.Equal(new[] { 0xA1, 0xA3, 0xA4 }, keys);
    }

    [Fact]
    public void Empty_recording_gives_no_steps()
    {
        Assert.Empty(SmartRecordingConverter.Convert(Array.Empty<CapturedInputEvent>()).Steps);
        Assert.Empty(SmartRecordingConverter.Convert(new[] { KeyUp(0, A), Up(0, 1, 1) }).Steps);
    }

    // ---------------- Playback safety ----------------

    [Fact]
    public async Task Stopping_in_the_middle_releases_the_held_camera_button()
    {
        var step = Assert.IsType<InputSequenceStep>(Assert.Single(SmartRecordingConverter.Convert(RightDragCamera()).Steps));
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, new FakeClock());
        var relCount = 0;
        input.OnCall = call =>
        {
            if (call.StartsWith("rel ") && ++relCount == 10) engine.Stop();
        };

        var result = await engine.RunAsync(MacroWith(new[] { step }));

        Assert.Equal(PlaybackOutcome.Stopped, result.Outcome);
        Assert.Equal("mouse Right up", input.Calls[^1]);
        Assert.Equal(10, input.Calls.Count(c => c.StartsWith("rel ")));
    }

    [Fact]
    public async Task Game_mode_recordings_still_play_only_relative_movement()
    {
        var legacy = new InputSequenceStep
        {
            Events = new List<InputEvent> { InputEvent.Move(0, 5, 5), InputEvent.ButtonDown(1_000, MacroMouseButton.Left), InputEvent.ButtonUp(2_000, MacroMouseButton.Left) },
        };
        var input = new FakeInput();

        await new PlaybackEngine(input, new FakeClock()).RunAsync(MacroWith(new[] { legacy }));

        Assert.Equal(new[] { "rel 5,5", "mouse Left down", "mouse Left up" }, input.Calls);
    }

    // ---------------- Files ----------------

    [Fact]
    public void Event_text_round_trips_positions_and_game_mode_text_is_unchanged()
    {
        var unified = new List<InputEvent>
        {
            InputEvent.Move(0, -3, 7, -1920, 1079),
            InputEvent.ButtonDown(10, MacroMouseButton.Right, 5, 6),
            InputEvent.WheelAt(20, -120, false, 7, 8),
            InputEvent.WheelAt(30, 120, true, 9, 10),
            InputEvent.KeyDown(40, W, WScan),
            InputEvent.Move(50, 1, 1),                 // a move without a position is allowed too
            InputEvent.ButtonUp(60, MacroMouseButton.Right, 5, 6),
        };
        var text = InputEventCodec.Encode(unified);

        Assert.StartsWith("u1 ", text);
        Assert.Equal(unified, InputEventCodec.Decode(text));

        var game = new List<InputEvent> { InputEvent.Move(0, 1, 2), InputEvent.KeyDown(5, W, WScan) };
        Assert.Equal("v1 0,m,1,2 5,kd,87,17,0", InputEventCodec.Encode(game));
    }

    [Theory]
    [InlineData("v1 0,m,1,1,5,5")]     // positions aren't allowed in game-mode text
    [InlineData("v1 0,bd,1,5,5")]
    [InlineData("u1 0,m,1,1,5")]       // incomplete position
    [InlineData("u1 0,kd,87,17,0,1,1")]
    [InlineData("u1 0,m,1,1,x,5")]
    [InlineData("u2 0,m,1,1")]
    public void Damaged_smart_recordings_are_refused(string events)
    {
        var json = $$"""{ "schemaVersion": 3, "macro": { "name": "x", "steps": [ { "type": "inputSequence", "events": "{{events}}" } ] } }""";

        Assert.False(MacroFileFormat.TryParse(json, out var macro, out var error));
        Assert.Null(macro);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Smart_recordings_save_as_version_3_and_load_back_exactly()
    {
        var step = Assert.IsType<InputSequenceStep>(Assert.Single(SmartRecordingConverter.Convert(RightDragCamera()).Steps));
        step.MouseSegments![2].Choice = MouseModeChoice.Relative;
        var macro = MacroWith(new[] { step });
        macro.Name = "Smart";

        var json = MacroFileFormat.Serialize(macro);
        Assert.Contains("\"schemaVersion\": 3", json);

        Assert.True(MacroFileFormat.TryParse(json, out var loaded, out var error), error);
        var loadedStep = Assert.IsType<InputSequenceStep>(loaded!.Steps[0]);
        Assert.Equal(step.Events, loadedStep.Events);
        Assert.Equal(
            step.MouseSegments!.Select(s => (s.StartUs, s.Detected, s.Evidence, s.Choice)),
            loadedStep.MouseSegments!.Select(s => (s.StartUs, s.Detected, s.Evidence, s.Choice)));
        Assert.Equal(MouseMovementMode.Relative, loadedStep.MouseSegments![2].Effective);
        Assert.False(MacroValidator.Validate(loaded).HasErrors());
    }

    [Fact]
    public void Game_mode_recordings_are_still_saved_as_version_2_without_mouse_segments()
    {
        var macro = MacroWith(new MacroStep[] { new InputSequenceStep { Events = new List<InputEvent> { InputEvent.Move(0, 1, 1) } } });

        var json = MacroFileFormat.Serialize(macro);

        Assert.Contains("\"schemaVersion\": 2", json);
        Assert.DoesNotContain("mouseSegments", json);
        Assert.True(MacroFileFormat.TryParse(json, out var loaded, out _));
        Assert.Null(Assert.IsType<InputSequenceStep>(loaded!.Steps[0]).MouseSegments);
    }

    [Fact]
    public void Files_from_a_newer_version_are_refused()
    {
        // Always one above the newest format this build understands (so the test keeps working after format bumps).
        var json = $$"""{ "schemaVersion": {{MacroDocument.CurrentSchemaVersion + 1}}, "macro": { "name": "x", "steps": [] } }""";
        Assert.False(MacroFileFormat.TryParse(json, out _, out var error));
        Assert.Contains("newer version", error);
    }

    // ---------------- Validation ----------------

    [Fact]
    public void Damaged_mouse_parts_are_reported()
    {
        InputSequenceStep Make(params MouseSegment[] segments) => new()
        {
            Events = new List<InputEvent> { InputEvent.Move(0, 1, 1, 10, 10), InputEvent.Move(1_000, 1, 1, 11, 11) },
            MouseSegments = segments.ToList(),
        };

        Assert.DoesNotContain(MacroValidator.ValidateStep(Make(new MouseSegment { StartUs = 0 })), i => i.Severity == IssueSeverity.Error);
        Assert.Contains(MacroValidator.ValidateStep(Make()), i => i.Severity == IssueSeverity.Error);
        Assert.Contains(MacroValidator.ValidateStep(Make(new MouseSegment { StartUs = 5 })), i => i.Severity == IssueSeverity.Error);
        Assert.Contains(MacroValidator.ValidateStep(Make(new MouseSegment { StartUs = 0 }, new MouseSegment { StartUs = 0 })), i => i.Severity == IssueSeverity.Error);
        Assert.Contains(MacroValidator.ValidateStep(Make(new MouseSegment { StartUs = 0, Choice = (MouseModeChoice)7 })), i => i.Severity == IssueSeverity.Error);

        var far = new InputSequenceStep { Events = new List<InputEvent> { InputEvent.Move(0, 1, 1, 500_000, 0) }, MouseSegments = new List<MouseSegment> { new() } };
        Assert.Contains(MacroValidator.ValidateStep(far), i => i.Severity == IssueSeverity.Error);
    }

    // ---------------- Settings ----------------

    [Fact]
    public async Task Old_settings_switch_to_smart_recording_once()
    {
        using var dir = new TempDir();
        var path = dir.Combine("settings.json");
        await File.WriteAllTextAsync(path, """{ "schemaVersion": 1, "recordingMode": "Desktop" }""");

        var old = await new JsonSettingsStore(path).LoadAsync();
        Assert.Equal(RecordingMode.Smart, old.Settings.RecordingMode);

        await File.WriteAllTextAsync(path, """{ "schemaVersion": 2, "recordingMode": "Game" }""");
        var current = await new JsonSettingsStore(path).LoadAsync();
        Assert.Equal(RecordingMode.Game, current.Settings.RecordingMode); // a deliberate choice is kept

        Assert.Equal(RecordingMode.Smart, new AppSettings().RecordingMode);
    }
}
