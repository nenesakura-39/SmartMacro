using SmartMacro.Core.Models;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Workflow;

namespace SmartMacro.Tests;

// =====================================================================================================================
// UX task: adding / moving steps in the editor (pure rules in Core/Workflow/StepListEditing) and the mouse steps the
// new Pick position editors fill in. The WPF parts (overlay, buttons, drag and drop) need the manual tests in
// docs/EDITOR_UX_MANUAL_TESTS.md; these tests prove the rules and what playback sends.
// =====================================================================================================================

public class StepInsertTests
{
    [Theory]
    [InlineData(5, 2, InsertPosition.AfterSelected, 3)]
    [InlineData(5, 4, InsertPosition.AfterSelected, 5)]   // after the last step = the end
    [InlineData(5, -1, InsertPosition.AfterSelected, 5)]  // nothing selected = the end
    [InlineData(5, 9, InsertPosition.AfterSelected, 5)]   // stale selection = the end
    [InlineData(5, 0, InsertPosition.AtEnd, 5)]
    [InlineData(5, 2, InsertPosition.AtEnd, 5)]
    [InlineData(0, -1, InsertPosition.AtEnd, 0)]
    [InlineData(0, -1, InsertPosition.AfterSelected, 0)]
    public void New_steps_go_where_the_user_chose(int count, int selected, InsertPosition position, int expected)
    {
        Assert.Equal(expected, StepListEditing.InsertIndex(count, selected, position));
    }
}

public class StepMoveTests
{
    private static MouseMoveStep S(int n) => new() { X = n, Comment = $"step {n}" };

    private static List<MacroStep> Plain(int count) => Enumerable.Range(0, count).Select(i => (MacroStep)S(i)).ToList();

    private static int[] Order(IEnumerable<MacroStep> steps) => steps.Select(s => s is MouseMoveStep m ? m.X : -1).ToArray();

    [Fact]
    public void Move_to_bottom_puts_a_step_after_the_last_one()
    {
        var steps = Plain(5);

        var plan = StepListEditing.PlanMove(steps, 1, 1, MoveDirection.ToBottom);

        Assert.True(plan.Allowed);
        Assert.Equal(new[] { 0, 2, 3, 4, 1 }, Order(StepListEditing.Apply(steps, plan)));
    }

    [Fact]
    public void Move_to_top_moves_a_group_and_keeps_its_order()
    {
        var steps = Plain(6);

        var plan = StepListEditing.PlanMove(steps, 3, 4, MoveDirection.ToTop);

        Assert.True(plan.Allowed);
        Assert.Equal(0, plan.NewFirst);
        Assert.Equal(new[] { 3, 4, 0, 1, 2, 5 }, Order(StepListEditing.Apply(steps, plan)));
    }

    [Theory]
    [InlineData(MoveDirection.Up, new[] { 0, 2, 1, 3 })]
    [InlineData(MoveDirection.Down, new[] { 0, 1, 3, 2 })]
    public void Up_and_down_move_one_place(MoveDirection direction, int[] expected)
    {
        var steps = Plain(4);

        var plan = StepListEditing.PlanMove(steps, 2, 2, direction);

        Assert.Equal(expected, Order(StepListEditing.Apply(steps, plan)));
    }

    [Theory]
    [InlineData(0, MoveDirection.Up)]
    [InlineData(0, MoveDirection.ToTop)]
    [InlineData(3, MoveDirection.Down)]
    [InlineData(3, MoveDirection.ToBottom)]
    public void Moving_past_the_ends_does_nothing_quietly(int index, MoveDirection direction)
    {
        var plan = StepListEditing.PlanMove(Plain(4), index, index, direction);

        Assert.False(plan.Allowed);
        Assert.Null(plan.Problem);
    }

    [Theory]
    [InlineData(1, 1, 4, new[] { 0, 2, 3, 1, 4 })]   // drop in front of step 4
    [InlineData(1, 1, 5, new[] { 0, 2, 3, 4, 1 })]   // drop after the last step
    [InlineData(3, 4, 0, new[] { 3, 4, 0, 1, 2 })]   // group to the front
    [InlineData(0, 1, 3, new[] { 2, 0, 1, 3, 4 })]
    public void Drag_and_drop_lands_in_front_of_the_target(int first, int last, int dropIndex, int[] expected)
    {
        var steps = Plain(5);

        var plan = StepListEditing.PlanDrop(steps, first, last, dropIndex);

        Assert.True(plan.Allowed);
        Assert.Equal(expected, Order(StepListEditing.Apply(steps, plan)));
    }

    [Theory]
    [InlineData(2, 2)] // onto itself
    [InlineData(2, 3)] // right after itself
    public void Dropping_onto_itself_changes_nothing(int index, int dropIndex)
    {
        var plan = StepListEditing.PlanDrop(Plain(5), index, index, dropIndex);

        Assert.False(plan.Allowed);
        Assert.Null(plan.Problem);
    }

    [Fact]
    public void Moving_keeps_every_step_object_and_its_settings()
    {
        var steps = Plain(5);
        steps[2].Enabled = false;
        var before = steps.ToList();

        var after = StepListEditing.Apply(steps, StepListEditing.PlanMove(steps, 2, 2, MoveDirection.ToTop));

        Assert.Equal(before.Count, after.Count);
        Assert.All(before, s => Assert.Contains(s, after)); // same objects, nothing copied or lost
        Assert.False(after[0].Enabled);
        Assert.Equal("step 2", after[0].Comment);
    }

    // ---- If / Repeat safety ----

    /// <summary>0: Move 0, 1: Repeat, 2: Move 2, 3: End Repeat, 4: Move 4, 5: If, 6: Move 6, 7: Else, 8: Move 8, 9: End If, 10: Move 10</summary>
    private static List<MacroStep> WithBlocks() => new()
    {
        S(0), Flow.Times(2), S(2), new EndRepeatStep(), S(4),
        Flow.If(Flow.Text("OK")), S(6), new ElseStep(), S(8), new EndIfStep(), S(10),
    };

    [Fact]
    public void Move_to_bottom_takes_a_whole_repeat_block_along()
    {
        var steps = WithBlocks();

        var plan = StepListEditing.PlanMove(steps, 1, 1, MoveDirection.ToBottom); // only the Repeat is selected

        Assert.True(plan.Allowed);
        Assert.True(plan.TookWholeBlocks);
        Assert.Equal((1, 3), (plan.First, plan.Last));
        var after = StepListEditing.Apply(steps, plan);
        Assert.IsType<RepeatStep>(after[^3]);
        Assert.IsType<EndRepeatStep>(after[^1]);
        Assert.True(BlockStructure.Analyze(after).IsValid);
    }

    [Fact]
    public void Dragging_an_end_if_takes_the_whole_if_block_along()
    {
        var steps = WithBlocks();

        var plan = StepListEditing.PlanDrop(steps, 9, 9, 0); // drag only "End If" to the top

        Assert.True(plan.Allowed);
        Assert.Equal((5, 9), (plan.First, plan.Last));
        var after = StepListEditing.Apply(steps, plan);
        Assert.IsType<IfStep>(after[0]);
        Assert.True(BlockStructure.Analyze(after).IsValid);
    }

    [Fact]
    public void A_move_that_would_break_the_blocks_is_refused_with_a_reason()
    {
        var steps = WithBlocks();

        // "End Repeat" one place up past the only step... fine; but "Repeat" down past its own End Repeat isn't.
        var plan = StepListEditing.PlanMove(steps, 3, 3, MoveDirection.Down); // End Repeat down past Move 4: still fine
        Assert.True(plan.Allowed);

        var broken = StepListEditing.PlanMove(new List<MacroStep> { Flow.Times(2), new EndRepeatStep(), S(1) }, 0, 0, MoveDirection.Down);
        Assert.False(broken.Allowed);
        Assert.Contains("would break the If / Repeat blocks", broken.Problem);
    }

    [Fact]
    public void Moving_a_marker_into_another_block_is_refused()
    {
        var steps = WithBlocks();

        // Selecting Move 4 + If (indexes 4-5) and moving them up one place would put the If inside the Repeat.
        var plan = StepListEditing.PlanMove(steps, 4, 5, MoveDirection.Up);

        Assert.False(plan.Allowed);
        Assert.NotNull(plan.Problem);
    }

    [Fact]
    public void A_normal_step_can_move_into_and_out_of_blocks()
    {
        var steps = WithBlocks();

        var into = StepListEditing.PlanMove(steps, 4, 4, MoveDirection.Up);      // Move 4 into the Repeat
        var top = StepListEditing.PlanMove(steps, 6, 6, MoveDirection.ToTop);    // Move 6 out of the If, to the top
        var drop = StepListEditing.PlanDrop(steps, 0, 0, 7);                     // Move 0 into the If's true part

        Assert.True(into.Allowed && top.Allowed && drop.Allowed);
        Assert.True(BlockStructure.Analyze(StepListEditing.Apply(steps, into)).IsValid);
        Assert.True(BlockStructure.Analyze(StepListEditing.Apply(steps, top)).IsValid);
        Assert.True(BlockStructure.Analyze(StepListEditing.Apply(steps, drop)).IsValid);
    }

    [Fact]
    public void When_the_blocks_are_already_broken_moves_are_allowed_so_they_can_be_fixed()
    {
        // End If above its If: broken. Moving the End If down fixes it.
        var steps = new List<MacroStep> { new EndIfStep(), Flow.If(Flow.Text("OK")), S(1) };

        var plan = StepListEditing.PlanMove(steps, 0, 0, MoveDirection.ToBottom);

        Assert.True(plan.Allowed);
        Assert.True(BlockStructure.Analyze(StepListEditing.Apply(steps, plan)).IsValid);
    }

    [Fact]
    public void Moved_steps_save_and_load_in_their_new_order()
    {
        var steps = WithBlocks();
        var moved = StepListEditing.Apply(steps, StepListEditing.PlanMove(steps, 1, 1, MoveDirection.ToBottom));
        var macro = Flow.Macro(moved.ToArray());

        Assert.True(MacroFileFormat.TryParse(MacroFileFormat.Serialize(macro), out var loaded, out var error), error);
        var back = loaded!.Steps;

        Assert.Equal(moved.Select(s => s.Id), back.Select(s => s.Id));
        Assert.Equal(moved.Select(s => s.Kind), back.Select(s => s.Kind));
        Assert.False(MacroValidator.Validate(loaded).HasErrors());
    }
}

public class MouseStepPlaybackTests
{
    private static async Task<List<string>> Play(params MacroStep[] steps)
    {
        var input = new FakeInput();
        var result = await new PlaybackEngine(input, new FakeClock()).RunAsync(Flow.Macro(steps));
        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        return input.Calls;
    }

    [Fact]
    public async Task Move_mouse_goes_to_the_picked_spot_including_monitors_left_of_the_main_one()
    {
        var calls = await Play(new MouseMoveStep { X = 1250, Y = 640 }, new MouseMoveStep { X = -1500, Y = -200 });

        Assert.Equal(new[] { "move 1250,640", "move -1500,-200" }, calls);
    }

    [Fact]
    public async Task Click_at_a_chosen_position_moves_there_then_clicks()
    {
        var calls = await Play(new MouseClickStep { X = 1250, Y = 640, Button = MacroMouseButton.Right });

        Assert.Equal(new[] { "move 1250,640", "mouse Right down", "mouse Right up" }, calls);
    }

    [Fact]
    public async Task Click_where_the_cursor_is_does_not_move()
    {
        var calls = await Play(new MouseClickStep { UseCurrentPosition = true, X = 1250, Y = 640 });

        Assert.Equal(new[] { "mouse Left down", "mouse Left up" }, calls);
    }

    [Theory]
    [InlineData(-360, false, "scroll -120|scroll -120|scroll -120")] // 3 notches down, one wheel event each
    [InlineData(120, false, "scroll 120")]                           // 1 notch up
    [InlineData(-240, true, "scroll -120 h|scroll -120 h")]          // 2 notches left
    [InlineData(240, true, "scroll 120 h|scroll 120 h")]             // 2 notches right
    [InlineData(-300, false, "scroll -120|scroll -120|scroll -60")]  // odd amount: the rest is sent last
    [InlineData(-60, false, "scroll -60")]                           // less than a notch stays one event
    public async Task Scroll_direction_and_amount_are_sent_as_one_event_per_notch(int delta, bool horizontal, string expected)
    {
        var calls = await Play(new MouseScrollStep { Delta = delta, Horizontal = horizontal });

        Assert.Equal(expected.Split('|'), calls);
    }

    [Fact]
    public async Task Scroll_at_a_chosen_position_moves_the_cursor_first()
    {
        var calls = await Play(new MouseScrollStep { Delta = -120, UseCurrentPosition = false, X = 800, Y = 450 });

        Assert.Equal(new[] { "move 800,450", "scroll -120" }, calls);
    }

    [Fact]
    public void Old_scroll_steps_without_a_position_setting_still_scroll_where_the_cursor_is()
    {
        const string json = """{ "schemaVersion": 1, "macro": { "name": "Old", "steps": [ { "type": "mouseScroll", "delta": -240 }, { "type": "mouseClick", "x": 10, "y": 20 }, { "type": "mouseMove", "x": 3, "y": 4 } ] } }""";

        Assert.True(MacroFileFormat.TryParse(json, out var macro, out var error), error);
        var scroll = Assert.IsType<MouseScrollStep>(macro!.Steps[0]);
        Assert.True(scroll.UseCurrentPosition);
        Assert.False(scroll.Horizontal);
        Assert.Equal(-240, scroll.Delta);
        Assert.False(((MouseClickStep)macro.Steps[1]).UseCurrentPosition);
        Assert.Equal(1, MacroDocument.RequiredSchemaVersion(macro)); // nothing new is needed to save them
    }

    [Fact]
    public void Scroll_amounts_that_are_not_whole_notches_are_kept_exactly()
    {
        var step = new MouseScrollStep { Delta = -60 };
        var json = MacroFileFormat.Serialize(Flow.Macro(step));

        Assert.True(MacroFileFormat.TryParse(json, out var macro, out _));
        Assert.Equal(-60, ((MouseScrollStep)macro!.Steps[0]).Delta);
    }

    [Fact]
    public void Positions_far_outside_any_screen_are_errors_and_off_screen_ones_only_warnings()
    {
        var screen = new ScreenBounds(0, 0, 1920, 1080);

        Assert.Contains(MacroValidator.ValidateStep(new MouseMoveStep { X = 250_000, Y = 0 }, screen), i => i.Severity == IssueSeverity.Error);
        Assert.Contains(MacroValidator.ValidateStep(new MouseClickStep { X = 0, Y = -250_000 }, screen), i => i.Severity == IssueSeverity.Error);
        Assert.Contains(MacroValidator.ValidateStep(new MouseScrollStep { UseCurrentPosition = false, X = 300_000 }, screen), i => i.Severity == IssueSeverity.Error);

        var offScreen = MacroValidator.ValidateStep(new MouseMoveStep { X = 2500, Y = 10 }, screen);
        Assert.DoesNotContain(offScreen, i => i.Severity == IssueSeverity.Error);
        Assert.Contains(offScreen, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("Pick position"));

        // Not used = not checked: clicking where the cursor is ignores a stale X.
        Assert.Empty(MacroValidator.ValidateStep(new MouseClickStep { UseCurrentPosition = true, X = 250_000 }, screen));
    }

    [Fact]
    public void A_zero_scroll_says_how_to_fix_it()
    {
        var issue = Assert.Single(MacroValidator.ValidateStep(new MouseScrollStep { Delta = 0 }));

        Assert.Contains("at least 1 notch", issue.Message);
    }
}
