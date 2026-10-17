using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Vision;
using SmartMacro.Core.Workflow;

namespace SmartMacro.Tests;

/// <summary>A fake simulator that also counts the failsafe calls (the plain FakeInput uses the do-nothing default).</summary>
internal sealed class FailsafeCountingInput : IInputSimulator
{
    public FakeInput Inner { get; } = new();
    public int FailsafeCalls { get; private set; }

    public List<string> Calls => Inner.Calls;

    public void MoveMouse(int x, int y) => Inner.MoveMouse(x, y);
    public void MoveMouseRelative(int dx, int dy) => Inner.MoveMouseRelative(dx, dy);
    public void MouseButton(MacroMouseButton button, bool down) => Inner.MouseButton(button, down);
    public void Scroll(int delta, bool horizontal) => Inner.Scroll(delta, horizontal);
    public void Key(int virtualKey, bool down) => Inner.Key(virtualKey, down);
    public void KeyWithScanCode(int virtualKey, int scanCode, bool extended, bool down) => Inner.KeyWithScanCode(virtualKey, scanCode, extended, down);
    public void TypeCharacters(string text) => Inner.TypeCharacters(text);

    public string? ReleaseAllHeldInputs()
    {
        FailsafeCalls++;
        return null;
    }
}

// ---------------- 1. Failsafe: release everything that is still held ----------------

public class ReleaseAllHeldInputsTests
{
    private const int W = 0x57;
    private const int Ctrl = 0x11;
    private const int A = 0x41;

    [Fact]
    public void Tracking_simulator_releases_held_buttons_and_keys_once()
    {
        var fake = new FakeInput();
        var input = new TrackingInputSimulator(fake);

        input.MouseButton(MacroMouseButton.Left, true);
        input.Key(W, true);
        input.KeyWithScanCode(0xA0, 0x2A, false, true); // left Shift by scan code
        input.Key(A, true);
        input.Key(A, false); // released normally: not held any more

        Assert.False(input.Tracker.IsEmpty);
        Assert.Null(input.ReleaseAllHeldInputs());

        // Buttons first, then keys in reverse press order.
        Assert.Equal(new[] { "mouse Left up", "key 160 up sc42", $"key {W} up" }, fake.Calls.Skip(5));
        Assert.True(input.Tracker.IsEmpty);

        var before = fake.Calls.Count;
        Assert.Null(input.ReleaseAllHeldInputs()); // nothing left: nothing sent
        Assert.Equal(before, fake.Calls.Count);
    }

    [Fact]
    public void Tracker_matches_a_release_however_the_key_was_pressed()
    {
        var tracker = new HeldInputTracker();
        tracker.NoteKey(W, true);
        tracker.NoteScanKey(W, 0x11, false, false); // the same key released by scan code (recorded input)
        Assert.True(tracker.IsEmpty);

        tracker.NoteButton(MacroMouseButton.Right, true);
        tracker.NoteButton(MacroMouseButton.Right, true); // pressed twice: still one held button
        Assert.Single(tracker.HeldButtons);
        tracker.NoteButton(MacroMouseButton.Right, false);
        Assert.True(tracker.IsEmpty);
    }

    [Fact]
    public void Tracker_tries_every_release_even_when_one_fails()
    {
        var tracker = new HeldInputTracker();
        tracker.NoteButton(MacroMouseButton.Left, true);
        tracker.NoteKey(W, true);
        var released = new List<string>();

        var error = tracker.ReleaseAll(_ => throw new InputSimulationException("blocked"), key => released.Add($"key {key.VirtualKey}"));

        Assert.NotNull(error);
        Assert.Contains("blocked", error);
        Assert.Equal(new[] { $"key {W}" }, released);
        Assert.True(tracker.IsEmpty);
    }

    [Fact]
    public async Task Engine_calls_the_failsafe_when_playback_finishes()
    {
        var input = new FailsafeCountingInput();
        var result = await new PlaybackEngine(input, new FakeClock()).RunAsync(Flow.Macro(Flow.Move(1)));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(1, input.FailsafeCalls);
    }

    [Fact]
    public async Task Engine_calls_the_failsafe_when_stopped()
    {
        var input = new FailsafeCountingInput();
        var engine = new PlaybackEngine(input, new FakeClock());
        input.Inner.OnCall = call =>
        {
            if (call == $"key {W} down") engine.Stop();
        };

        var result = await engine.RunAsync(Flow.Macro(
            new KeyPressStep { VirtualKey = W, Action = KeyAction.Down },
            new DelayStep { DurationMs = 60_000 },
            Flow.Move(1)));

        Assert.Equal(PlaybackOutcome.Stopped, result.Outcome);
        Assert.Equal(1, input.FailsafeCalls);
        Assert.Contains($"key {W} up", input.Calls); // the engine's own release still happens too
        Assert.DoesNotContain("move 1,0", input.Calls);
    }

    [Fact]
    public async Task Engine_calls_the_failsafe_when_a_step_fails()
    {
        var input = new FailsafeCountingInput();
        input.Inner.ThrowOn = new InputSimulationException("Windows blocked it");
        input.Inner.ThrowWhen = "move 2,0";

        var result = await new PlaybackEngine(input, new FakeClock()).RunAsync(Flow.Macro(Flow.Move(1), Flow.Move(2), Flow.Move(3)));

        Assert.Equal(PlaybackOutcome.Failed, result.Outcome);
        Assert.Equal(1, input.FailsafeCalls);
    }

    [Fact]
    public async Task Failsafe_releases_a_modifier_left_down_by_a_crash_in_the_middle_of_a_key_combo()
    {
        // Ctrl+A where Windows refuses the "A down": the engine never got to track Ctrl, the failsafe still releases it.
        var fake = new FakeInput { ThrowOn = new InputSimulationException("blocked"), ThrowWhen = $"key {A} down" };
        var input = new TrackingInputSimulator(fake);

        var result = await new PlaybackEngine(input, new FakeClock()).RunAsync(Flow.Macro(
            new KeyPressStep { VirtualKey = A, Modifiers = KeyModifiers.Control }));

        Assert.Equal(PlaybackOutcome.Failed, result.Outcome);
        Assert.Equal(new[] { $"key {Ctrl} down", $"key {A} down", $"key {A} up", $"key {Ctrl} up" }, fake.Calls);
        Assert.True(input.Tracker.IsEmpty);
    }

    [Fact]
    public async Task Failsafe_errors_become_a_warning_not_a_crash()
    {
        var input = new ThrowingFailsafeInput();
        var result = await new PlaybackEngine(input, new FakeClock()).RunAsync(Flow.Macro(Flow.Move(1)));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.NotNull(result.Warning);
        Assert.Contains("refused", result.Warning);
    }

    private sealed class ThrowingFailsafeInput : IInputSimulator
    {
        public void MoveMouse(int x, int y) { }
        public void MoveMouseRelative(int dx, int dy) { }
        public void MouseButton(MacroMouseButton button, bool down) { }
        public void Scroll(int delta, bool horizontal) { }
        public void Key(int virtualKey, bool down) { }
        public void KeyWithScanCode(int virtualKey, int scanCode, bool extended, bool down) { }
        public void TypeCharacters(string text) { }
        public string? ReleaseAllHeldInputs() => throw new InputSimulationException("refused");
    }
}

// ---------------- 2. Run step / Play from here ----------------

public class PlayRangeTests
{
    [Fact]
    public async Task Single_step_plays_only_that_step_once()
    {
        var input = new FakeInput();
        var macro = Flow.Macro(Flow.Move(1), Flow.Move(2), Flow.Move(3));
        macro.RepeatCount = 5;

        var result = await new PlaybackEngine(input, new FakeClock()).PlaySingleStepAsync(macro, 1);

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 2,0" }, input.Calls);
        Assert.Equal(1, result.IterationsCompleted);
    }

    [Theory]
    [InlineData(1)] // the Repeat header
    [InlineData(3)] // its End Repeat
    public async Task Single_step_on_a_block_runs_the_whole_block(int index)
    {
        var input = new FakeInput();
        var macro = Flow.Macro(Flow.Move(1), Flow.Times(2), Flow.Move(5), new EndRepeatStep(), Flow.Move(9));

        await new PlaybackEngine(input, new FakeClock()).PlaySingleStepAsync(macro, index);

        Assert.Equal(new[] { "move 5,0", "move 5,0" }, input.Calls);
    }

    [Fact]
    public async Task Single_step_ignores_problems_in_other_steps()
    {
        var macro = Flow.Macro(Flow.Move(1), new DelayStep { DurationMs = -5 });
        var input = new FakeInput();

        var result = await new PlaybackEngine(input, new FakeClock()).PlaySingleStepAsync(macro, 0);

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        await Assert.ThrowsAsync<MacroValidationException>(() => new PlaybackEngine(new FakeInput(), new FakeClock()).RunAsync(macro));
    }

    [Fact]
    public async Task Single_step_still_refuses_problems_in_the_step_itself()
    {
        var macro = Flow.Macro(Flow.Move(1), new DelayStep { DurationMs = -5 });
        await Assert.ThrowsAsync<MacroValidationException>(() => new PlaybackEngine(new FakeInput(), new FakeClock()).PlaySingleStepAsync(macro, 1));
    }

    [Fact]
    public async Task A_disabled_step_is_refused_with_a_reason()
    {
        var macro = Flow.Macro(Flow.Move(1), new MouseMoveStep { X = 2, Enabled = false });
        var ex = await Assert.ThrowsAsync<MacroValidationException>(() => new PlaybackEngine(new FakeInput(), new FakeClock()).PlaySingleStepAsync(macro, 1));
        Assert.Contains("turned off", ex.Message);
    }

    [Fact]
    public async Task Play_from_here_plays_to_the_end_once()
    {
        var input = new FakeInput();
        var macro = Flow.Macro(Flow.Move(1), Flow.Move(2), Flow.Move(3), Flow.Move(4));
        macro.RepeatCount = 3;
        macro.RepeatDelayMs = 1000;

        var result = await new PlaybackEngine(input, new FakeClock()).PlayFromStepAsync(macro, 2);

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 3,0", "move 4,0" }, input.Calls);
    }

    [Fact]
    public async Task Play_from_a_step_inside_a_loop_starts_at_the_loop()
    {
        var input = new FakeInput();
        var macro = Flow.Macro(Flow.Move(1), Flow.Times(2), Flow.Move(2), Flow.Move(3), new EndRepeatStep(), Flow.Move(4));
        var messages = new List<string>();
        var engine = new PlaybackEngine(input, new FakeClock());
        engine.Message += (_, m) => messages.Add(m);

        await engine.PlayFromStepAsync(macro, 3);

        Assert.Equal(new[] { "move 2,0", "move 3,0", "move 2,0", "move 3,0", "move 4,0" }, input.Calls);
        Assert.Contains(messages, m => m.Contains("Starting at step 2"));
    }

    [Fact]
    public async Task Play_from_inside_a_nested_loop_finishes_the_outer_turn_once()
    {
        var input = new FakeInput();
        var macro = Flow.Macro(
            Flow.Times(3),            // 0
            Flow.Times(2),            // 1
            Flow.Move(5),             // 2
            new EndRepeatStep(),      // 3
            Flow.Move(6),             // 4
            new EndRepeatStep(),      // 5
            Flow.Move(7));            // 6

        var result = await new PlaybackEngine(input, new FakeClock()).PlayFromStepAsync(macro, 2);

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 5,0", "move 5,0", "move 6,0", "move 7,0" }, input.Calls);
    }

    [Fact]
    public async Task Play_from_inside_an_if_starts_right_there()
    {
        var input = new FakeInput();
        var macro = Flow.Macro(Flow.If(Flow.Text("OK")), Flow.Move(1), Flow.Move(2), new ElseStep(), Flow.Move(3), new EndIfStep(), Flow.Move(4));

        // Starting inside the "true" part: the rest of it runs, the Else part is skipped as usual. No OCR needed.
        await new PlaybackEngine(input, new FakeClock()).PlayFromStepAsync(macro, 2);

        Assert.Equal(new[] { "move 2,0", "move 4,0" }, input.Calls);
    }

    [Fact]
    public void Ranges_are_worked_out_from_the_block_structure()
    {
        var steps = Flow.Macro(Flow.Move(1), Flow.If(Flow.Text("OK")), Flow.Move(2), new ElseStep(), Flow.Move(3), new EndIfStep()).Steps;

        Assert.Equal((0, 1), PlaybackRanges.SingleStep(steps, 0));
        Assert.Equal((1, 6), PlaybackRanges.SingleStep(steps, 1));
        Assert.Equal((1, 6), PlaybackRanges.SingleStep(steps, 3)); // Else → the whole If block
        Assert.Equal((1, 6), PlaybackRanges.SingleStep(steps, 5)); // End If → the whole If block
        Assert.Equal(4, PlaybackRanges.FromStep(steps, 3));        // Else → its "otherwise" part
        Assert.Throws<ArgumentOutOfRangeException>(() => PlaybackRanges.SingleStep(steps, 6));
    }

    [Fact]
    public async Task Run_step_also_calls_the_failsafe()
    {
        var input = new FailsafeCountingInput();
        await new PlaybackEngine(input, new FakeClock()).PlaySingleStepAsync(Flow.Macro(Flow.Move(1), Flow.Move(2)), 0);
        Assert.Equal(1, input.FailsafeCalls);
    }
}

// ---------------- 3. Undo / Redo + copy / paste ----------------

public class UndoRedoTests
{
    [Fact]
    public void Undo_and_redo_walk_back_and_forth()
    {
        var history = new UndoHistory();
        Assert.False(history.CanUndo);

        history.Record("A");          // before edit 1 → now "B"
        history.Record("B");          // before edit 2 → now "C"

        Assert.True(history.TryUndo("C", out var s1));
        Assert.Equal("B", s1);
        Assert.True(history.TryUndo("B", out var s2));
        Assert.Equal("A", s2);
        Assert.False(history.TryUndo("A", out _));

        Assert.True(history.CanRedo);
        Assert.True(history.TryRedo("A", out var r1));
        Assert.Equal("B", r1);
        Assert.True(history.TryRedo("B", out var r2));
        Assert.Equal("C", r2);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void A_new_edit_clears_redo()
    {
        var history = new UndoHistory();
        history.Record("A");
        history.TryUndo("B", out _);
        Assert.True(history.CanRedo);

        history.Record("A");
        Assert.False(history.CanRedo);
        Assert.Equal(1, history.UndoCount);
    }

    [Fact]
    public void History_is_capped_at_fifty_and_drops_the_oldest()
    {
        var history = new UndoHistory();
        for (var i = 0; i < 60; i++) history.Record($"s{i}");

        Assert.Equal(UndoHistory.DefaultCapacity, history.UndoCount);
        var last = string.Empty;
        while (history.TryUndo("now", out var snapshot)) last = snapshot;
        Assert.Equal("s10", last); // s0 … s9 were dropped
    }

    [Fact]
    public void The_same_snapshot_is_not_stored_twice_in_a_row()
    {
        var history = new UndoHistory();
        history.Record("A");
        history.Record("A");
        Assert.Equal(1, history.UndoCount);
    }

    [Fact]
    public void Clear_forgets_everything()
    {
        var history = new UndoHistory();
        history.Record("A");
        history.TryUndo("B", out _);
        history.Record("C");
        history.Clear();
        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void Snapshots_round_trip_every_step_type_with_the_same_ids()
    {
        var steps = Flow.Macro(Flow.Move(4), Flow.Times(2), new KeyPressStep { VirtualKey = 0x41 }, new EndRepeatStep(),
            new FindColorStep { Color = "#112233" }, new FeedbackLoopStep()).Steps;

        var restored = StepSnapshots.Restore(StepSnapshots.Capture(steps));

        Assert.Equal(steps.Select(s => s.GetType()), restored.Select(s => s.GetType()));
        Assert.Equal(steps.Select(s => s.Id), restored.Select(s => s.Id));
        Assert.Equal(4, ((MouseMoveStep)restored[0]).X);
        Assert.Equal("#112233", ((FindColorStep)restored[4]).Color);
    }

    [Fact]
    public void Undo_restores_a_deleted_block_exactly()
    {
        // The editor's flow: snapshot before the edit, edit, then undo hands back the snapshot.
        var history = new UndoHistory();
        var steps = Flow.Macro(Flow.Move(1), Flow.Times(3), Flow.Move(2), new EndRepeatStep()).Steps;
        history.Record(StepSnapshots.Capture(steps));
        var afterDelete = steps.Take(1).ToList();

        Assert.True(history.TryUndo(StepSnapshots.Capture(afterDelete), out var snapshot));
        var back = StepSnapshots.Restore(snapshot);

        Assert.Equal(4, back.Count);
        Assert.Equal(3, ((RepeatStep)back[1]).Count);
        Assert.True(BlockStructure.Analyze(back).IsValid);
    }

    [Fact]
    public void Copied_steps_paste_with_fresh_ids()
    {
        var steps = new List<MacroStep> { Flow.Move(7), new DelayStep { DurationMs = 250 } };
        var text = StepClipboard.Serialize(steps);

        Assert.True(StepClipboard.LooksLikeSteps(text));
        Assert.True(StepClipboard.TryParse(text, out var pasted, out var error), error);
        Assert.Equal(2, pasted.Count);
        Assert.Equal(7, ((MouseMoveStep)pasted[0]).X);
        Assert.Equal(250, ((DelayStep)pasted[1]).DurationMs);
        Assert.DoesNotContain(pasted, p => steps.Any(s => s.Id == p.Id));
        Assert.Equal(2, pasted.Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public void Paste_refuses_other_text_and_partial_blocks()
    {
        Assert.False(StepClipboard.TryParse("hello", out _, out var notSteps));
        Assert.NotNull(notSteps);
        Assert.False(StepClipboard.TryParse(null, out _, out _));

        var partial = StepClipboard.Serialize(new List<MacroStep> { Flow.Times(2), Flow.Move(1) });
        Assert.False(StepClipboard.TryParse(partial, out _, out var blockError));
        Assert.Contains("block", blockError);
    }

    [Fact]
    public void Copy_widens_a_lone_block_header_to_its_whole_block()
    {
        var steps = Flow.Macro(Flow.Move(1), Flow.Times(2), Flow.Move(2), new EndRepeatStep(), Flow.Move(3)).Steps;

        Assert.Equal(new[] { 1, 2, 3 }, StepClipboard.ExpandSelection(steps, new[] { 1 }));
        Assert.Equal(new[] { 1, 2, 3 }, StepClipboard.ExpandSelection(steps, new[] { 3 }));
        Assert.Equal(new[] { 0, 4 }, StepClipboard.ExpandSelection(steps, new[] { 4, 0 }));
        Assert.Null(StepClipboard.ExpandSelection(steps, new[] { 1, 2 })); // Repeat without its End
    }
}

// ---------------- 4. Window-relative coordinates + resolution scaling ----------------

public class CoordinateScalingTests
{
    private static readonly ScreenRect FullHd = new(0, 0, 1920, 1080);
    private static readonly ScreenRect Qhd = new(0, 0, 2560, 1440);

    [Fact]
    public void Points_scale_with_the_resolution()
    {
        var mapping = new CoordinateMapping(FullHd, Qhd);

        Assert.Equal((1280, 720), mapping.Map(960, 540));
        Assert.Equal((0, 0), mapping.Map(0, 0));
        Assert.Equal((1729, 997), mapping.Map(1297, 748)); // the example from the bug report
        Assert.Equal(new ScreenRect(400, 200, 800, 400), mapping.MapRect(new ScreenRect(300, 150, 600, 300)));
    }

    [Fact]
    public void A_moved_window_only_shifts_the_points()
    {
        var mapping = new CoordinateMapping(new ScreenRect(100, 100, 800, 600), new ScreenRect(-700, 250, 800, 600));
        Assert.Equal((-600, 300), mapping.Map(200, 150));
        Assert.False(mapping.IsIdentity);
        Assert.True(new CoordinateMapping(FullHd, FullHd).IsIdentity);
    }

    [Fact]
    public void Every_stored_position_is_adjusted()
    {
        var steps = new List<MacroStep>
        {
            new MouseMoveStep { X = 960, Y = 540 },
            new MouseClickStep { X = 192, Y = 108, Action = MouseClickAction.Drag, EndX = 1920, EndY = 1080 },
            new MouseScrollStep { UseCurrentPosition = false, X = 960, Y = 0 },
            new FindImageStep { Scope = SearchScope.CustomArea, RegionX = 960, RegionY = 540, RegionWidth = 192, RegionHeight = 108 },
            new FindColorStep { Scope = SearchScope.PrimaryMonitor, RegionX = 10, RegionY = 10 },
            new IfStep { Condition = new Condition { Check = new FindColorStep { Scope = SearchScope.CustomArea, RegionX = 96, RegionY = 54, RegionWidth = 96, RegionHeight = 54 } } },
            new FeedbackLoopStep { RegionX = 960, RegionY = 540, RegionWidth = 300, RegionHeight = 30 },
        };

        CoordinateScaling.ApplyInPlace(steps, new CoordinateMapping(FullHd, Qhd));

        var move = (MouseMoveStep)steps[0];
        Assert.Equal((1280, 720), (move.X, move.Y));
        var click = (MouseClickStep)steps[1];
        Assert.Equal((256, 144, 2560, 1440), (click.X, click.Y, click.EndX, click.EndY));
        Assert.Equal((1280, 0), (((MouseScrollStep)steps[2]).X, ((MouseScrollStep)steps[2]).Y));
        Assert.Equal(new ScreenRect(1280, 720, 256, 144), ((FindImageStep)steps[3]).Region);
        Assert.Equal((10, 10), (((FindColorStep)steps[4]).RegionX, ((FindColorStep)steps[4]).RegionY)); // monitor scope: found at playback
        Assert.Equal(new ScreenRect(128, 72, 128, 72), ((IfStep)steps[5]).Condition.Search!.Region);
        var bar = (FeedbackLoopStep)steps[6];
        Assert.Equal((1280, 720, 400, 40), (bar.RegionX, bar.RegionY, bar.RegionWidth, bar.RegionHeight));
    }

    [Fact]
    public async Task Playback_follows_the_target_window()
    {
        var layout = new FakeLayout();
        layout.Monitors.Add(new MonitorInfo(1, FullHd, true));
        layout.Windows.Add(new WindowInfo(1, "Roblox", new ScreenRect(100, 50, 1600, 1200)));
        var macro = Flow.Macro(new MouseMoveStep { X = 400, Y = 300 }, new MouseClickStep { X = 0, Y = 0 });
        macro.TargetWindowTitle = "Roblox";
        macro.ScaleCoordinatesToWindow = true;
        (macro.ReferenceLeft, macro.ReferenceTop, macro.ReferenceWidth, macro.ReferenceHeight) = (0, 0, 800, 600);
        var input = new FakeInput();

        var result = await new PlaybackEngine(input, new FakeClock(), layout: layout).RunAsync(macro);

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 900,650", "move 100,50", "mouse Left down", "mouse Left up" }, input.Calls);
        Assert.Equal(400, ((MouseMoveStep)macro.Steps[0]).X); // the macro itself isn't changed
    }

    [Fact]
    public async Task Playback_scales_to_the_main_screen_without_a_target_window()
    {
        var layout = new FakeLayout();
        layout.Monitors.Add(new MonitorInfo(1, Qhd, true));
        var macro = Flow.Macro(new MouseMoveStep { X = 960, Y = 540 });
        macro.ScaleCoordinatesToWindow = true;
        (macro.ReferenceWidth, macro.ReferenceHeight) = (1920, 1080);
        var input = new FakeInput();

        await new PlaybackEngine(input, new FakeClock(), layout: layout).RunAsync(macro);

        Assert.Equal(new[] { "move 1280,720" }, input.Calls);
    }

    [Fact]
    public async Task A_missing_target_window_stops_with_a_clear_message()
    {
        var layout = new FakeLayout();
        layout.Monitors.Add(new MonitorInfo(1, FullHd, true));
        var macro = Flow.Macro(new MouseMoveStep { X = 1, Y = 1 });
        macro.TargetWindowTitle = "Roblox";
        macro.ScaleCoordinatesToWindow = true;
        (macro.ReferenceWidth, macro.ReferenceHeight) = (800, 600);
        var input = new FakeInput();

        var result = await new PlaybackEngine(input, new FakeClock(), layout: layout).RunAsync(macro);

        Assert.Equal(PlaybackOutcome.Failed, result.Outcome);
        Assert.Contains("isn't open", result.Error);
        Assert.Empty(input.Calls);
    }

    [Fact]
    public async Task Scaling_off_plays_exact_pixels()
    {
        var macro = Flow.Macro(new MouseMoveStep { X = 960, Y = 540 });
        macro.TargetWindowTitle = "Roblox"; // bound, but not adjusting
        (macro.ReferenceWidth, macro.ReferenceHeight) = (800, 600);
        var input = new FakeInput();

        await new PlaybackEngine(input, new FakeClock()).RunAsync(macro);

        Assert.Equal(new[] { "move 960,540" }, input.Calls);
    }

    [Fact]
    public void Scaling_needs_a_reference_size()
    {
        var macro = Flow.Macro(Flow.Move(1));
        macro.ScaleCoordinatesToWindow = true;

        Assert.Contains(MacroValidator.Validate(macro), i => i.Severity == IssueSeverity.Error && i.Property == nameof(Macro.ReferenceWidth));

        (macro.ReferenceWidth, macro.ReferenceHeight) = (1920, 1080);
        Assert.DoesNotContain(MacroValidator.Validate(macro), i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void New_macro_settings_use_format_11_and_round_trip()
    {
        var plain = Flow.Macro(Flow.Move(1));
        Assert.Contains("\"schemaVersion\": 1", MacroFileFormat.Serialize(plain));
        Assert.DoesNotContain("humanizePlayback", MacroFileFormat.Serialize(plain));

        var macro = Flow.Macro(Flow.Move(1));
        macro.TargetWindowTitle = "Roblox";
        macro.ScaleCoordinatesToWindow = true;
        (macro.ReferenceLeft, macro.ReferenceTop, macro.ReferenceWidth, macro.ReferenceHeight) = (-1920, 0, 1920, 1080);
        macro.HumanizePlayback = true;
        macro.ClickJitterPx = 5;

        var json = MacroFileFormat.Serialize(macro);
        Assert.Contains("\"schemaVersion\": 11", json);
        Assert.True(MacroFileFormat.TryParse(json, out var loaded, out var error), error);
        Assert.Equal("Roblox", loaded!.TargetWindowTitle);
        Assert.True(loaded.ScaleCoordinatesToWindow);
        Assert.Equal((-1920, 0, 1920, 1080), (loaded.ReferenceLeft, loaded.ReferenceTop, loaded.ReferenceWidth, loaded.ReferenceHeight));
        Assert.True(loaded.HumanizePlayback);
        Assert.Equal(5, loaded.EffectiveClickJitterPx);
        Assert.Equal(Macro.DefaultDelayJitterPercent, loaded.EffectiveDelayJitterPercent);
    }
}

// ---------------- 6. Humanize ----------------

public class HumanizeTests
{
    [Fact]
    public async Task Clicks_land_within_the_jitter_and_vary()
    {
        var macro = Flow.Macro(Enumerable.Range(0, 40).Select(_ => (MacroStep)new MouseClickStep { X = 500, Y = 300 }).ToArray());
        macro.HumanizePlayback = true;
        var input = new FakeInput();

        await new PlaybackEngine(input, new FakeClock()) { HumanizeRandom = new Random(7) }.RunAsync(macro);

        var moves = input.Calls.Where(c => c.StartsWith("move ")).Select(c => c[5..].Split(',').Select(int.Parse).ToArray()).ToList();
        Assert.Equal(40, moves.Count);
        Assert.All(moves, m =>
        {
            Assert.InRange(m[0], 497, 503);
            Assert.InRange(m[1], 297, 303);
        });
        Assert.True(moves.Select(m => (m[0], m[1])).Distinct().Count() > 5);
    }

    [Fact]
    public async Task Delays_vary_by_at_most_the_percentage()
    {
        var macro = Flow.Macro(Enumerable.Range(0, 20).Select(_ => (MacroStep)new DelayStep { DurationMs = 1000 }).Append(Flow.Move(1)).ToArray());
        macro.HumanizePlayback = true;
        var clock = new FakeClock();

        await new PlaybackEngine(new FakeInput(), clock) { HumanizeRandom = new Random(3) }.RunAsync(macro);

        // 20 × 1000 ms ± 8 % each: never more than 1.6 s off in total, and not exactly 20 s.
        Assert.InRange(clock.Elapsed.TotalMilliseconds, 18_400, 21_600);
        Assert.NotEqual(20_000, Math.Round(clock.Elapsed.TotalMilliseconds));
    }

    [Fact]
    public async Task Humanize_off_is_exact()
    {
        var macro = Flow.Macro(new DelayStep { DurationMs = 1000 }, new MouseClickStep { X = 500, Y = 300 });
        var clock = new FakeClock();
        var input = new FakeInput();

        await new PlaybackEngine(input, clock) { HumanizeRandom = new Random(1) }.RunAsync(macro);

        Assert.Equal(1000, Math.Round(clock.Elapsed.TotalMilliseconds));
        Assert.Contains("move 500,300", input.Calls);
    }

    [Fact]
    public async Task Found_clicks_stay_inside_what_was_found()
    {
        // A 3×3 green patch at (200, 100): even with a 20 px jitter the click stays on the patch.
        var capture = new FakeCapture(GreenPatch());
        var step = new FindColorStep { Color = "#00C800", Tolerance = 10, MinPixels = 1, TimeoutMs = 0, Action = FoundTextAction.Click, PostActionDelayMs = 0 };
        var macro = Flow.Macro(Enumerable.Range(0, 15).Select(_ => (MacroStep)MacroFactory.DuplicateStep(step)).ToArray());
        macro.HumanizePlayback = true;
        macro.ClickJitterPx = 20;
        var input = new FakeInput();

        var result = await new PlaybackEngine(input, new FakeClock(), capture: capture) { HumanizeRandom = new Random(11) }.RunAsync(macro);

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        var moves = input.Calls.Where(c => c.StartsWith("move ")).Select(c => c[5..].Split(',').Select(int.Parse).ToArray()).ToList();
        Assert.Equal(15, moves.Count);
        Assert.All(moves, m =>
        {
            Assert.InRange(m[0], 200, 202);
            Assert.InRange(m[1], 100, 102);
        });
    }

    [Fact]
    public void Jitter_settings_are_validated()
    {
        var macro = Flow.Macro(Flow.Move(1));
        macro.ClickJitterPx = 500;
        macro.DelayJitterPercent = -1;
        var issues = MacroValidator.Validate(macro);
        Assert.Contains(issues, i => i.Property == nameof(Macro.ClickJitterPx));
        Assert.Contains(issues, i => i.Property == nameof(Macro.DelayJitterPercent));
    }

    private static BgraImage GreenPatch()
    {
        var image = TestImages.Solid(300, 200, RgbColor.Black);
        image.FillRectangle(new ScreenRect(200, 100, 3, 3), new RgbColor(0, 200, 0));
        return image;
    }
}
