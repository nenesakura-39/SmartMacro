using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Vision;
using SmartMacro.Core.Workflow;

namespace SmartMacro.Tests;

/// <summary>
/// OCR fake for the Stage 3 tests: read number n (1-based) sees the word if <c>Visible(n)</c> is true.
/// OnRead runs on the playback thread before the result is returned (e.g. to pause or stop playback).
/// </summary>
internal sealed class ScriptedOcr : IOcrService
{
    private int _reads;

    public ScriptedOcr(string word, Func<int, bool> visible)
    {
        Word = word;
        Visible = visible;
    }

    public string Word { get; }
    public Func<int, bool> Visible { get; }
    public Action<int>? OnRead { get; set; }
    public int Reads => Volatile.Read(ref _reads);

    public bool IsAvailable => true;
    public string StatusText => "ready";
    public ScreenRect GetVirtualScreen() => new(0, 0, 1920, 1080);

    public Task<OcrReadResult> ReadAsync(ScreenRect region, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var n = Interlocked.Increment(ref _reads);
        OnRead?.Invoke(n);
        var lines = Visible(n)
            ? new[] { new OcrLineBox(new[] { new OcrWordBox(Word, new ScreenRect(100, 200, Word.Length * 10, 20)) }) }
            : Array.Empty<OcrLineBox>();
        return Task.FromResult(new OcrReadResult(GetVirtualScreen(), lines));
    }
}

/// <summary>Shared builders for the Stage 3 tests.</summary>
internal static class Flow
{
    public static Macro Macro(params MacroStep[] steps)
    {
        var m = new Macro { Name = "Flow test" };
        m.Steps.AddRange(steps);
        return m;
    }

    public static MouseMoveStep Move(int x) => new() { X = x };

    public static Condition Text(string text, bool negate = false, int timeoutMs = 0, int intervalMs = 500) => new()
    {
        Check = new FindTextStep { Text = text, TimeoutMs = timeoutMs, CheckIntervalMs = intervalMs },
        Negate = negate,
    };

    public static Condition Green(bool negate = false) => new()
    {
        Check = new FindColorStep { Color = "#00C800", Tolerance = 10, MinPixels = 20, TimeoutMs = 0, CheckIntervalMs = 50 },
        Negate = negate,
    };

    public static IfStep If(Condition condition) => new() { Condition = condition };

    public static RepeatStep Times(int count) => new() { Mode = RepeatMode.Times, Count = count };

    public static RepeatStep While(Condition condition, int maxTurns = 100, int maxMs = 0,
        LoopLimitBehavior whenLimit = LoopLimitBehavior.StopMacro) =>
        new() { Mode = RepeatMode.While, Condition = condition, MaxIterations = maxTurns, MaxDurationMs = maxMs, WhenLimitReached = whenLimit };

    public static RepeatStep Until(Condition condition, int maxTurns = 100) =>
        new() { Mode = RepeatMode.Until, Condition = condition, MaxIterations = maxTurns };

    /// <summary>A black screen, optionally with a green 11×11 square at (200, 100) (center 205, 105).</summary>
    public static BgraImage Screen(bool green)
    {
        var screen = TestImages.Solid(300, 200, RgbColor.Black);
        if (green) screen.FillRectangle(new ScreenRect(200, 100, 11, 11), new RgbColor(0, 200, 0));
        return screen;
    }

    public static int Count(FakeInput input, string call)
    {
        lock (input.Calls) return input.Calls.Count(c => c == call);
    }
}

public class BlockStructureTests
{
    private static BlockStructure Analyze(params MacroStep[] steps) => BlockStructure.Analyze(steps);

    [Fact]
    public void Builds_the_jump_table_and_indentation()
    {
        var b = Analyze(Flow.If(Flow.Text("OK")), Flow.Move(1), new ElseStep(), Flow.Move(2), new EndIfStep(), Flow.Move(3));

        Assert.True(b.IsValid);
        Assert.Equal(2, b.ElseOf(0));
        Assert.Equal(4, b.EndOf(0));
        Assert.Equal(4, b.EndOf(2));
        Assert.Equal(0, b.StartOf(2));
        Assert.Equal(0, b.StartOf(4));
        Assert.Equal(new[] { 0, 1, 0, 1, 0, 0 }, b.Depth);
    }

    [Fact]
    public void Nested_blocks_indent_deeper()
    {
        var b = Analyze(Flow.Times(2), Flow.If(Flow.Text("OK")), Flow.Move(1), new EndIfStep(), new EndRepeatStep());

        Assert.True(b.IsValid);
        Assert.Equal(new[] { 0, 1, 2, 1, 0 }, b.Depth);
        Assert.Equal(4, b.EndOf(0));
        Assert.Equal(0, b.StartOf(4));
        Assert.Equal(-1, b.ElseOf(1));
        Assert.Equal(2, b.MaxDepthUsed);
    }

    public static IEnumerable<object[]> BrokenShapes()
    {
        yield return new object[] { "If without End If", new MacroStep[] { Flow.If(Flow.Text("OK")), Flow.Move(1) }, 0, "has no End If" };
        yield return new object[] { "End If without If", new MacroStep[] { Flow.Move(1), new EndIfStep() }, 1, "has no If before it" };
        yield return new object[] { "Else without If", new MacroStep[] { new ElseStep(), Flow.Move(1) }, 0, "has no If before it" };
        yield return new object[] { "Repeat without End", new MacroStep[] { Flow.Times(2), Flow.Move(1) }, 0, "has no End Repeat" };
        yield return new object[] { "End Repeat without Repeat", new MacroStep[] { new EndRepeatStep() }, 0, "has no Repeat before it" };
        yield return new object[] { "Two Elses", new MacroStep[] { Flow.If(Flow.Text("OK")), new ElseStep(), new ElseStep(), new EndIfStep() }, 2, "only one Else" };
        yield return new object[] { "Else inside a Repeat", new MacroStep[] { Flow.If(Flow.Text("OK")), Flow.Times(2), new ElseStep(), new EndRepeatStep(), new EndIfStep() }, 2, "inside the Repeat" };
        yield return new object[] { "Overlapping blocks", new MacroStep[] { Flow.If(Flow.Text("OK")), Flow.Times(2), new EndIfStep(), new EndRepeatStep() }, 2, "doesn't match" };
    }

    [Theory]
    [MemberData(nameof(BrokenShapes))]
    public void Unbalanced_or_wrongly_nested_blocks_are_problems(string shape, MacroStep[] steps, int badIndex, string message)
    {
        var b = BlockStructure.Analyze(steps);

        Assert.False(b.IsValid, shape);
        Assert.Contains(b.Problems, p => p.StepIndex == badIndex && p.Message.Contains(message));
    }

    [Fact]
    public void Eight_levels_are_allowed_nine_are_too_deep()
    {
        static MacroStep[] Nested(int depth) =>
            Enumerable.Range(0, depth).Select(_ => (MacroStep)Flow.Times(1))
                .Append(Flow.Move(1))
                .Concat(Enumerable.Range(0, depth).Select(_ => (MacroStep)new EndRepeatStep()))
                .ToArray();

        Assert.True(BlockStructure.Analyze(Nested(BlockStructure.MaxDepth)).IsValid);

        var tooDeep = BlockStructure.Analyze(Nested(BlockStructure.MaxDepth + 1));
        Assert.False(tooDeep.IsValid);
        Assert.Contains(tooDeep.Problems, p => p.StepIndex == BlockStructure.MaxDepth && p.Message.Contains("nested too deeply"));
    }

    [Fact]
    public void Plain_lists_have_no_blocks_and_no_problems()
    {
        var b = Analyze(Flow.Move(1), Flow.Move(2));
        Assert.True(b.IsValid);
        Assert.Equal(new[] { 0, 0 }, b.Depth);
        Assert.Equal(0, b.MaxDepthUsed);
    }
}

public class ConditionalPlaybackTests
{
    [Theory]
    [InlineData(true, "move 1,0")]
    [InlineData(false, "move 2,0")]
    public async Task If_else_runs_the_matching_part(bool textOnScreen, string expectedBranch)
    {
        var input = new FakeInput();
        var ocr = new ScriptedOcr("OK", _ => textOnScreen);

        var result = await new PlaybackEngine(input, new FakeClock(), ocr).RunAsync(Flow.Macro(
            Flow.If(Flow.Text("OK")), Flow.Move(1), new ElseStep(), Flow.Move(2), new EndIfStep(), Flow.Move(3)));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { expectedBranch, "move 3,0" }, input.Calls);
        Assert.Equal(1, ocr.Reads);
    }

    [Fact]
    public async Task False_if_without_else_skips_to_after_end_if()
    {
        var input = new FakeInput();

        await new PlaybackEngine(input, new FakeClock(), new ScriptedOcr("OK", _ => false)).RunAsync(Flow.Macro(
            Flow.If(Flow.Text("OK")), Flow.Move(1), new EndIfStep(), Flow.Move(3)));

        Assert.Equal(new[] { "move 3,0" }, input.Calls);
    }

    [Fact]
    public async Task Not_on_screen_turns_the_answer_around()
    {
        var input = new FakeInput();

        await new PlaybackEngine(input, new FakeClock(), new ScriptedOcr("Error", _ => false)).RunAsync(Flow.Macro(
            Flow.If(Flow.Text("Error", negate: true)), Flow.Move(1), new ElseStep(), Flow.Move(2), new EndIfStep()));

        Assert.Equal(new[] { "move 1,0" }, input.Calls);
    }

    [Fact]
    public async Task If_waits_up_to_its_timeout_for_the_condition_to_become_true()
    {
        var input = new FakeInput();
        var ocr = new ScriptedOcr("OK", n => n >= 3);

        await new PlaybackEngine(input, new FakeClock(), ocr).RunAsync(Flow.Macro(
            Flow.If(Flow.Text("OK", timeoutMs: 2000, intervalMs: 500)), Flow.Move(1), new ElseStep(), Flow.Move(2), new EndIfStep()));

        Assert.Equal(3, ocr.Reads);
        Assert.Equal(new[] { "move 1,0" }, input.Calls);
    }

    [Fact]
    public async Task If_times_out_and_takes_the_else_part()
    {
        var input = new FakeInput();
        var clock = new FakeClock();
        var ocr = new ScriptedOcr("OK", _ => false);
        var messages = new List<string>();
        var engine = new PlaybackEngine(input, clock, ocr);
        engine.Message += (_, m) => messages.Add(m);

        var result = await engine.RunAsync(Flow.Macro(
            Flow.If(Flow.Text("OK", timeoutMs: 1000, intervalMs: 250)), Flow.Move(1), new ElseStep(), Flow.Move(2), new EndIfStep()));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(5, ocr.Reads); // t = 0, 0.25, 0.5, 0.75, 1 s
        Assert.Equal(1000, clock.Elapsed.TotalMilliseconds, precision: 3);
        Assert.Equal(new[] { "move 2,0" }, input.Calls);
        Assert.Contains(messages, m => m.StartsWith("If (step 1): no.") && m.Contains("is not on screen"));
    }

    [Fact]
    public async Task Pausing_inside_a_condition_wait_does_not_count_toward_its_timeout()
    {
        var clock = new FakeClock();
        var input = new FakeInput();
        var ocr = new ScriptedOcr("OK", _ => false);
        var engine = new PlaybackEngine(input, clock, ocr);
        ocr.OnRead = n =>
        {
            if (n != 2) return;
            Assert.True(engine.Pause());
            _ = Task.Run(async () =>
            {
                await Task.Delay(50);
                clock.Advance(TimeSpan.FromSeconds(5)); // 5 s pass while paused
                engine.Resume();
            });
        };

        var result = await engine.RunAsync(Flow.Macro(
                Flow.If(Flow.Text("OK", timeoutMs: 1000, intervalMs: 250)), Flow.Move(1), new ElseStep(), Flow.Move(2), new EndIfStep()))
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(5, ocr.Reads); // still 1 s of checking; the 5 paused seconds didn't end the wait early
        Assert.Equal(new[] { "move 2,0" }, input.Calls);
    }

    [Fact]
    public async Task Condition_that_sees_its_target_sets_the_last_found_spot()
    {
        var input = new FakeInput();

        var result = await new PlaybackEngine(input, new FakeClock(), capture: new FakeCapture(Flow.Screen(green: true))).RunAsync(Flow.Macro(
            Flow.If(Flow.Green()), new MouseClickStep { UseLastFoundText = true }, new EndIfStep()));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 205,105", "mouse Left down", "mouse Left up" }, input.Calls);
    }

    [Fact]
    public async Task Disabled_if_or_repeat_skips_the_whole_block()
    {
        var input = new FakeInput();
        var ocr = new ScriptedOcr("OK", _ => false);

        var result = await new PlaybackEngine(input, new FakeClock(), ocr).RunAsync(Flow.Macro(
            new IfStep { Condition = Flow.Text("OK"), Enabled = false }, Flow.Move(1), new ElseStep(), Flow.Move(2), new EndIfStep(),
            new RepeatStep { Mode = RepeatMode.Times, Count = 3, Enabled = false }, Flow.Move(4), new EndRepeatStep(),
            Flow.Move(9)));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 9,0" }, input.Calls);
        Assert.Equal(0, ocr.Reads);
    }

    [Fact]
    public async Task Missing_ocr_stops_with_a_clear_error_instead_of_taking_a_branch()
    {
        var input = new FakeInput();

        var result = await new PlaybackEngine(input, new FakeClock()).RunAsync(Flow.Macro(
            Flow.If(Flow.Text("OK")), Flow.Move(1), new ElseStep(), Flow.Move(2), new EndIfStep()));

        Assert.Equal(PlaybackOutcome.Failed, result.Outcome);
        Assert.Contains("OCR", result.Error);
        Assert.Empty(input.Calls);
    }

    [Fact]
    public async Task Nested_Case_A_outer_true_inner_false_without_inner_else_jumps_to_inner_end_if_and_skips_outer_else()
    {
        // Case A: Outer If (true) -> Inner If (false) [no inner Else] -> Inner EndIf -> Outer Else -> Outer EndIf.
        // Inner If (false) MUST jump to Inner EndIf, NOT Outer Else!
        // And after Inner EndIf, execution MUST skip Outer Else and jump to Outer EndIf!
        var input = new FakeInput();
        var capture = new FakeCapture(Flow.Screen(green: true));

        var steps = new MacroStep[]
        {
            Flow.If(Flow.Green(negate: false)),         // 0: Outer If (true)
            Flow.Move(1),                               // 1: runs before Inner If
            Flow.If(Flow.Green(negate: true)),          // 2: Inner If (false, no inner Else)
            Flow.Move(2),                               // 3: skipped
            new EndIfStep(),                            // 4: Inner EndIf
            Flow.Move(3),                               // 5: after Inner EndIf, still inside Outer If then-branch!
            new ElseStep(),                             // 6: Outer Else (must be skipped!)
            Flow.Move(4),                               // 7: inside Outer Else (skipped)
            new EndIfStep(),                            // 8: Outer EndIf
            Flow.Move(5),                               // 9: after Outer EndIf
        };

        var blocks = BlockStructure.Analyze(steps);
        Assert.True(blocks.IsValid);
        Assert.Equal(6, blocks.ElseOf(0));
        Assert.Equal(8, blocks.EndOf(0));
        Assert.Equal(-1, blocks.ElseOf(2));
        Assert.Equal(4, blocks.EndOf(2));
        Assert.Equal(8, blocks.EndOf(6));

        var result = await new PlaybackEngine(input, new FakeClock(), capture: capture).RunAsync(Flow.Macro(steps));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 1,0", "move 3,0", "move 5,0" }, input.Calls);
    }

    [Fact]
    public async Task Nested_Case_B_outer_false_skips_inner_if_else_end_if_and_lands_on_outer_else()
    {
        // Case B: Outer If (false) -> [Inner If ... Inner Else ... Inner EndIf] -> Outer Else -> Outer EndIf.
        // Outer If (false) MUST skip the entire inner If/Else/EndIf and land on Outer Else, NOT Inner Else!
        var input = new FakeInput();
        var capture = new FakeCapture(Flow.Screen(green: false));

        var steps = new MacroStep[]
        {
            Flow.If(Flow.Green(negate: false)),         // 0: Outer If (false)
            Flow.Move(1),                               // 1: skipped
            Flow.If(Flow.Green(negate: true)),          // 2: Inner If (skipped)
            Flow.Move(2),                               // 3: skipped
            new ElseStep(),                             // 4: Inner Else (must NOT be landed on!)
            Flow.Move(3),                               // 5: skipped
            new EndIfStep(),                            // 6: Inner EndIf (skipped)
            Flow.Move(4),                               // 7: skipped
            new ElseStep(),                             // 8: Outer Else (must land here!)
            Flow.Move(5),                               // 9: runs inside Outer Else
            new EndIfStep(),                            // 10: Outer EndIf
            Flow.Move(6),                               // 11: runs after Outer EndIf
        };

        var blocks = BlockStructure.Analyze(steps);
        Assert.True(blocks.IsValid);
        Assert.Equal(8, blocks.ElseOf(0));
        Assert.Equal(10, blocks.EndOf(0));
        Assert.Equal(4, blocks.ElseOf(2));
        Assert.Equal(6, blocks.EndOf(2));

        var result = await new PlaybackEngine(input, new FakeClock(), capture: capture).RunAsync(Flow.Macro(steps));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 5,0", "move 6,0" }, input.Calls);
        Assert.Equal(1, capture.Captures); // Inner condition is never even evaluated when Outer If is false
    }

    [Fact]
    public async Task Nested_Case_C_three_level_deep_nested_if_else_inside_repeat_loop()
    {
        // Case C: 3-level deep nested If/Else inside a Repeat loop.
        var input = new FakeInput();
        // Turn 1: green = true  -> Level 1 True -> Level 2 False (negate) -> Level 2 Else -> Level 3 True -> move 121
        // Turn 2: green = false -> Level 1 Else -> Level 2 True (negate)  -> Level 3 False -> Level 3 Else -> move 212
        var capture = new FakeCapture(
            Flow.Screen(green: true),
            Flow.Screen(green: true),
            Flow.Screen(green: true),
            Flow.Screen(green: false),
            Flow.Screen(green: false),
            Flow.Screen(green: false));

        var result = await new PlaybackEngine(input, new FakeClock(), capture: capture).RunAsync(Flow.Macro(
            Flow.Times(2),
                Flow.If(Flow.Green(negate: false)),                     // Level 1 If
                    Flow.If(Flow.Green(negate: true)),                  // Level 2 If (in L1 Then)
                        Flow.Move(110),
                    new ElseStep(),                                     // Level 2 Else
                        Flow.If(Flow.Green(negate: false)),             // Level 3 If (in L2 Else)
                            Flow.Move(121),
                        new ElseStep(),                                 // Level 3 Else
                            Flow.Move(122),
                        new EndIfStep(),                                // Level 3 EndIf
                    new EndIfStep(),                                    // Level 2 EndIf
                new ElseStep(),                                         // Level 1 Else
                    Flow.If(Flow.Green(negate: true)),                  // Level 2 If (in L1 Else)
                        Flow.If(Flow.Green(negate: false)),             // Level 3 If (in L2 Then)
                            Flow.Move(211),
                        new ElseStep(),                                 // Level 3 Else
                            Flow.Move(212),
                        new EndIfStep(),                                // Level 3 EndIf
                    new ElseStep(),                                     // Level 2 Else
                        Flow.Move(220),
                    new EndIfStep(),                                    // Level 2 EndIf
                new EndIfStep(),                                        // Level 1 EndIf
            new EndRepeatStep(),
            Flow.Move(999)));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 121,0", "move 212,0", "move 999,0" }, input.Calls);
    }

    [Fact]
    public void Smart_insertion_puts_nested_if_and_repeat_inside_selected_block_even_when_add_at_end_was_on()
    {
        var steps = new List<MacroStep>();

        static int AddPair(List<MacroStep> list, int selectedIndex, InsertPosition position, StepKind kind)
        {
            var index = StepListEditing.InsertIndex(list, selectedIndex, position, kind);
            list.Insert(index, MacroFactory.CreateStep(kind));
            if (kind is StepKind.If or StepKind.Repeat)
                list.Insert(index + 1, MacroFactory.CreateStep(kind == StepKind.If ? StepKind.EndIf : StepKind.EndRepeat));
            return index;
        }

        // 1. Add outer If -> inserts If (0), EndIf (1)
        var sel = AddPair(steps, -1, InsertPosition.AtEnd, StepKind.If);
        Assert.Equal(0, sel);
        Assert.Equal(new[] { StepKind.If, StepKind.EndIf }, steps.Select(s => s.Kind));

        // 2. Even when InsertPosition.AtEnd is passed while outer If (or a step inside it) is selected,
        // adding a nested If or Repeat inserts it INSIDE the outer block right after the selected step!
        sel = AddPair(steps, sel, InsertPosition.AtEnd, StepKind.If);
        Assert.Equal(1, sel);
        Assert.Equal(new[] { StepKind.If, StepKind.If, StepKind.EndIf, StepKind.EndIf }, steps.Select(s => s.Kind));
        Assert.True(BlockStructure.Analyze(steps).IsValid);

        // 3. Adding Else when EndIf is selected inserts Else right before EndIf; adding a step after Else inserts inside Else!
        var elseIdx = AddPair(steps, 2, InsertPosition.AtEnd, StepKind.Else); // selected = inner EndIf (index 2)
        Assert.Equal(2, elseIdx);
        var moveIdx = AddPair(steps, elseIdx, InsertPosition.AfterSelected, StepKind.MouseMove);
        Assert.Equal(3, moveIdx);
        Assert.Equal(
            new[] { StepKind.If, StepKind.If, StepKind.Else, StepKind.MouseMove, StepKind.EndIf, StepKind.EndIf },
            steps.Select(s => s.Kind));
        Assert.True(BlockStructure.Analyze(steps).IsValid);

        // 4. Moving inner EndIf down across outer EndIf or outer Else is refused so block partners never silently swap.
        var outerIf = Flow.If(Flow.Text("A"));
        var innerIf = Flow.If(Flow.Text("B"));
        var innerEnd = new EndIfStep();
        var outerElse = new ElseStep();
        var outerEnd = new EndIfStep();
        var list = new List<MacroStep> { outerIf, innerIf, Flow.Move(1), innerEnd, outerElse, Flow.Move(2), outerEnd };
        var badMove = StepListEditing.PlanMove(list, 3, 3, MoveDirection.Down); // innerEnd across outerElse
        Assert.False(badMove.Allowed);
    }
}

public class LoopPlaybackTests
{
    [Fact]
    public async Task Repeat_times_runs_the_block_that_many_times_and_skips_disabled_steps()
    {
        var input = new FakeInput();

        var result = await new PlaybackEngine(input, new FakeClock()).RunAsync(Flow.Macro(
            Flow.Move(0), Flow.Times(3), Flow.Move(1), new MouseMoveStep { X = 7, Enabled = false }, new EndRepeatStep(), Flow.Move(2)));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 0,0", "move 1,0", "move 1,0", "move 1,0", "move 2,0" }, input.Calls);
    }

    [Fact]
    public async Task Nested_repeats_multiply()
    {
        var input = new FakeInput();

        await new PlaybackEngine(input, new FakeClock()).RunAsync(Flow.Macro(
            Flow.Times(2), Flow.Move(1), Flow.Times(3), Flow.Move(2), new EndRepeatStep(), new EndRepeatStep()));

        Assert.Equal(2, Flow.Count(input, "move 1,0"));
        Assert.Equal(6, Flow.Count(input, "move 2,0"));
        Assert.Equal(new[] { "move 1,0", "move 2,0", "move 2,0", "move 2,0", "move 1,0", "move 2,0", "move 2,0", "move 2,0" }, input.Calls);
    }

    [Fact]
    public async Task If_inside_a_repeat_is_checked_every_turn()
    {
        var input = new FakeInput();
        var capture = new FakeCapture(Flow.Screen(green: true), Flow.Screen(green: false), Flow.Screen(green: true));

        await new PlaybackEngine(input, new FakeClock(), capture: capture).RunAsync(Flow.Macro(
            Flow.Times(3), Flow.If(Flow.Green()), Flow.Move(1), new ElseStep(), Flow.Move(2), new EndIfStep(), new EndRepeatStep()));

        Assert.Equal(new[] { "move 1,0", "move 2,0", "move 1,0" }, input.Calls);
        Assert.Equal(3, capture.Captures);
    }

    [Fact]
    public async Task Repeat_while_stops_when_the_condition_becomes_false()
    {
        var input = new FakeInput();
        var ocr = new ScriptedOcr("Fishing", n => n <= 3);

        var result = await new PlaybackEngine(input, new FakeClock(), ocr).RunAsync(Flow.Macro(
            Flow.While(Flow.Text("Fishing")), Flow.Move(1), new EndRepeatStep(), Flow.Move(2)));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(4, ocr.Reads);
        Assert.Equal(new[] { "move 1,0", "move 1,0", "move 1,0", "move 2,0" }, input.Calls);
    }

    [Fact]
    public async Task Repeat_while_can_run_zero_times()
    {
        var input = new FakeInput();

        await new PlaybackEngine(input, new FakeClock(), new ScriptedOcr("Go", _ => false)).RunAsync(Flow.Macro(
            Flow.While(Flow.Text("Go")), Flow.Move(1), new EndRepeatStep(), Flow.Move(2)));

        Assert.Equal(new[] { "move 2,0" }, input.Calls);
    }

    [Fact]
    public async Task Repeat_until_stops_when_the_condition_becomes_true()
    {
        var input = new FakeInput();
        var capture = new FakeCapture(Flow.Screen(green: false), Flow.Screen(green: false), Flow.Screen(green: true));

        var result = await new PlaybackEngine(input, new FakeClock(), capture: capture).RunAsync(Flow.Macro(
            Flow.Until(Flow.Green()), Flow.Move(1), new EndRepeatStep(), Flow.Move(2)));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 1,0", "move 1,0", "move 2,0" }, input.Calls);
    }

    [Fact]
    public async Task Max_turns_stops_the_macro_with_an_error_by_default()
    {
        var input = new FakeInput();

        var result = await new PlaybackEngine(input, new FakeClock(), new ScriptedOcr("Go", _ => true)).RunAsync(Flow.Macro(
            Flow.While(Flow.Text("Go"), maxTurns: 5), Flow.Move(1), new EndRepeatStep(), Flow.Move(2)));

        Assert.Equal(PlaybackOutcome.Failed, result.Outcome);
        Assert.Contains("limit of 5 turns", result.Error);
        Assert.Equal(5, Flow.Count(input, "move 1,0"));
        Assert.DoesNotContain("move 2,0", input.Calls);
    }

    [Fact]
    public async Task Max_turns_can_leave_the_loop_and_continue()
    {
        var input = new FakeInput();
        var messages = new List<string>();
        var engine = new PlaybackEngine(input, new FakeClock(), new ScriptedOcr("Go", _ => true));
        engine.Message += (_, m) => messages.Add(m);

        var result = await engine.RunAsync(Flow.Macro(
            Flow.While(Flow.Text("Go"), maxTurns: 5, whenLimit: LoopLimitBehavior.ExitLoop), Flow.Move(1), new EndRepeatStep(), Flow.Move(2)));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(5, Flow.Count(input, "move 1,0"));
        Assert.Equal("move 2,0", input.Calls[^1]);
        Assert.Contains(messages, m => m.Contains("limit of 5 turns") && m.Contains("Leaving the loop"));
    }

    [Fact]
    public async Task Max_time_stops_a_loop_that_runs_too_long()
    {
        var input = new FakeInput();
        var clock = new FakeClock();

        // Each turn waits 300 ms; checks are at least 500 ms apart; the loop may run for 1 s.
        var result = await new PlaybackEngine(input, clock, new ScriptedOcr("Go", _ => true)).RunAsync(Flow.Macro(
            Flow.While(Flow.Text("Go"), maxTurns: 0, maxMs: 1000), Flow.Move(1), new DelayStep { DurationMs = 300 }, new EndRepeatStep()));

        Assert.Equal(PlaybackOutcome.Failed, result.Outcome);
        Assert.Contains("time limit of 1 s", result.Error);
        Assert.Equal(2, Flow.Count(input, "move 1,0")); // turns start at 0 and 0.5 s; at 1 s the limit is reached
        Assert.Equal(1000, clock.Elapsed.TotalMilliseconds, precision: 3);
    }

    [Fact]
    public async Task Max_time_also_limits_a_repeat_times_loop()
    {
        var input = new FakeInput();
        var loop = Flow.Times(1000);
        loop.MaxDurationMs = 1000;
        loop.WhenLimitReached = LoopLimitBehavior.ExitLoop;

        var result = await new PlaybackEngine(input, new FakeClock()).RunAsync(Flow.Macro(
            loop, Flow.Move(1), new DelayStep { DurationMs = 250 }, new EndRepeatStep(), Flow.Move(2)));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(4, Flow.Count(input, "move 1,0"));
        Assert.Equal("move 2,0", input.Calls[^1]);
    }

    [Fact]
    public async Task Condition_checks_of_an_empty_loop_are_spaced_out()
    {
        var clock = new FakeClock();
        var ocr = new ScriptedOcr("Go", _ => true);

        var result = await new PlaybackEngine(new FakeInput(), clock, ocr).RunAsync(Flow.Macro(
            Flow.While(Flow.Text("Go", intervalMs: 500), maxTurns: 10, whenLimit: LoopLimitBehavior.ExitLoop), new EndRepeatStep(),
            Flow.Move(1)));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(10, ocr.Reads);
        Assert.Equal(4500, clock.Elapsed.TotalMilliseconds, precision: 3); // 9 gaps of 500 ms, not a busy loop
    }

    [Fact]
    public async Task Stop_inside_a_loop_ends_playback_and_releases_held_input()
    {
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, new FakeClock(), new ScriptedOcr("Go", _ => true));
        var moves = 0;
        input.OnCall = call =>
        {
            if (call == "move 1,0" && Interlocked.Increment(ref moves) == 3) engine.Stop();
        };

        var result = await engine.RunAsync(Flow.Macro(
                new KeyPressStep { VirtualKey = 0x57, Action = KeyAction.Down },
                Flow.While(Flow.Text("Go", intervalMs: 100), maxTurns: 1_000_000), Flow.Move(1), new EndRepeatStep()))
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(PlaybackOutcome.Stopped, result.Outcome);
        Assert.Equal(3, Flow.Count(input, "move 1,0"));
        Assert.Equal("key 87 up", input.Calls[^1]);
    }

    [Fact]
    public async Task Stop_while_a_loop_condition_waits_ends_quickly_and_releases_held_input()
    {
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, clock: null, capture: new FakeCapture(Flow.Screen(green: false)));
        var green = Flow.Green();
        green.Search!.TimeoutMs = 3_600_000; // wait up to an hour for the colour
        var run = engine.RunAsync(Flow.Macro(
            new MouseClickStep { UseCurrentPosition = true, Action = MouseClickAction.Down },
            Flow.Until(green), Flow.Move(1), new EndRepeatStep()));

        await Task.Delay(150);
        Assert.True(engine.Stop());
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(PlaybackOutcome.Stopped, result.Outcome);
        Assert.Equal(new[] { "mouse Left down", "mouse Left up" }, input.Calls);
    }

    [Fact]
    public async Task Pause_and_resume_inside_a_loop_keeps_the_loop_state()
    {
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, new FakeClock());
        var moves = 0;
        input.OnCall = call =>
        {
            if (call != "move 1,0" || Interlocked.Increment(ref moves) != 2) return;
            Assert.True(engine.Pause());
            _ = Task.Run(async () => { await Task.Delay(50); engine.Resume(); });
        };

        var result = await engine.RunAsync(Flow.Macro(Flow.Times(4), Flow.Move(1), new EndRepeatStep(), Flow.Move(2)))
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 1,0", "move 1,0", "move 1,0", "move 1,0", "move 2,0" }, input.Calls);
    }

    [Fact]
    public async Task Progress_says_which_loop_turn_is_running()
    {
        var engine = new PlaybackEngine(new FakeInput(), new FakeClock());
        var texts = new List<string?>();
        engine.ProgressChanged += (_, p) => { if (p.Step is MouseMoveStep) texts.Add(p.LoopText); };

        await engine.RunAsync(Flow.Macro(Flow.Times(2), Flow.Move(1), new EndRepeatStep(), Flow.Move(2)));

        Assert.Equal(new string?[] { "loop turn 1 of 2", "loop turn 2 of 2", null }, texts);
    }

    [Fact]
    public async Task The_whole_macro_can_still_repeat_around_blocks()
    {
        var input = new FakeInput();
        var macro = Flow.Macro(Flow.Times(2), Flow.Move(1), new EndRepeatStep());
        macro.RepeatCount = 3;

        var result = await new PlaybackEngine(input, new FakeClock()).RunAsync(macro);

        Assert.Equal(3, result.IterationsCompleted);
        Assert.Equal(6, Flow.Count(input, "move 1,0"));
    }

    [Fact]
    public async Task Unbalanced_blocks_are_refused_before_anything_runs()
    {
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, new FakeClock());

        var ex = await Assert.ThrowsAsync<MacroValidationException>(() => engine.RunAsync(Flow.Macro(Flow.Times(2), Flow.Move(1))));

        Assert.Contains("has no End Repeat", ex.Message);
        Assert.Empty(input.Calls);
        Assert.Equal(PlaybackState.Idle, engine.State);
    }
}

public class ConditionEvaluatorTests
{
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    public async Task Answers_with_the_same_detection_as_find_color(bool green, bool negate, bool expected)
    {
        var evaluator = new ScreenConditionEvaluator(new ScreenProbes(null, new FakeCapture(Flow.Screen(green)), new FakeClock()));
        var condition = Flow.Green(negate);

        var check = await evaluator.CheckAsync(condition, evaluator.NewTimer(condition), CancellationToken.None);

        Assert.Equal(green, check.Seen);
        Assert.Equal(expected, check.IsTrue);
        if (green) Assert.Equal((205, 105), (check.X, check.Y));
        Assert.Equal(negate ? "color #00C800 is not on screen" : "color #00C800 is on screen", evaluator.Describe(condition));
    }

    [Fact]
    public async Task Text_conditions_use_ocr()
    {
        var evaluator = new ScreenConditionEvaluator(new ScreenProbes(new ScriptedOcr("Shop", _ => true), null));

        var check = await evaluator.CheckAsync(Flow.Text("Shop"), null, CancellationToken.None);

        Assert.True(check.IsTrue);
        Assert.Contains("Found \"Shop\"", check.Details);
    }

    [Fact]
    public async Task Missing_screen_capture_is_an_error_not_a_false_answer()
    {
        var evaluator = new ScreenConditionEvaluator(new ScreenProbes(null, null));

        await Assert.ThrowsAsync<ScreenCaptureUnavailableException>(() => evaluator.CheckAsync(Flow.Green(), null, CancellationToken.None));
    }
}

public class WorkflowValidationTests
{
    private static IReadOnlyList<ValidationIssue> Validate(params MacroStep[] steps) => MacroValidator.Validate(Flow.Macro(steps));

    [Fact]
    public void A_complete_if_else_block_is_valid()
    {
        Assert.False(Validate(Flow.If(Flow.Text("OK")), Flow.Move(1), new ElseStep(), Flow.Move(2), new EndIfStep()).HasErrors());
    }

    [Fact]
    public void Block_problems_are_errors_on_the_right_step()
    {
        var issues = Validate(Flow.Move(1), new EndIfStep());
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.StepIndex == 1 && i.Message.Contains("has no If before it"));
    }

    [Fact]
    public void Too_deep_nesting_is_an_error()
    {
        var steps = Enumerable.Range(0, 9).Select(_ => (MacroStep)Flow.Times(1)).Append(Flow.Move(1))
            .Concat(Enumerable.Range(0, 9).Select(_ => (MacroStep)new EndRepeatStep())).ToArray();
        Assert.Contains(Validate(steps), i => i.Severity == IssueSeverity.Error && i.Message.Contains("nested too deeply"));
    }

    [Fact]
    public void While_and_until_loops_need_a_safety_limit()
    {
        var noLimit = Flow.While(Flow.Text("Go"), maxTurns: 0, maxMs: 0);
        Assert.Contains(Validate(noLimit, Flow.Move(1), new EndRepeatStep()),
            i => i.Severity == IssueSeverity.Error && i.Message.Contains("safety limit"));

        Assert.False(Validate(Flow.While(Flow.Text("Go"), maxTurns: 0, maxMs: 5000), Flow.Move(1), new EndRepeatStep()).HasErrors());
        Assert.False(Validate(Flow.Until(Flow.Text("Go"), maxTurns: 10), Flow.Move(1), new EndRepeatStep()).HasErrors());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(MacroValidator.MaxLoopCount + 1)]
    public void Repeat_count_must_be_in_range(int count)
    {
        Assert.True(MacroValidator.ValidateStep(Flow.Times(count)).HasErrors());
    }

    [Fact]
    public void Bad_limits_and_unknown_values_are_errors()
    {
        Assert.True(MacroValidator.ValidateStep(new RepeatStep { MaxDurationMs = -1 }).HasErrors());
        Assert.True(MacroValidator.ValidateStep(new RepeatStep { MaxDurationMs = MacroValidator.MaxLoopDurationMs + 1 }).HasErrors());
        Assert.True(MacroValidator.ValidateStep(new RepeatStep { Mode = RepeatMode.While, Condition = Flow.Text("x"), MaxIterations = -5 }).HasErrors());
        Assert.True(MacroValidator.ValidateStep(new RepeatStep { Mode = (RepeatMode)9 }).HasErrors());
        Assert.True(MacroValidator.ValidateStep(new RepeatStep { WhenLimitReached = (LoopLimitBehavior)9 }).HasErrors());
    }

    [Fact]
    public void While_loop_needs_a_condition()
    {
        var issues = MacroValidator.ValidateStep(new RepeatStep { Mode = RepeatMode.While, Condition = null });
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Property == nameof(RepeatStep.Condition));
    }

    [Fact]
    public void Condition_settings_are_checked_like_the_find_step()
    {
        var noImage = MacroValidator.ValidateStep(new IfStep { Condition = MacroCondition(new FindImageStep()) });
        Assert.Contains(noImage, i => i.Severity == IssueSeverity.Error && i.Message.StartsWith("Condition: Capture the image"));

        var emptyText = MacroValidator.ValidateStep(new IfStep { Condition = MacroCondition(new FindTextStep { Text = " " }) });
        Assert.Contains(emptyText, i => i.Severity == IssueSeverity.Error && i.Message.StartsWith("Condition:"));
    }

    [Fact]
    public void Condition_without_search_or_with_the_wrong_step_type_is_an_error()
    {
        Assert.True(MacroValidator.ValidateStep(new IfStep { Condition = new Condition() }).HasErrors());
        Assert.True(MacroValidator.ValidateStep(new IfStep { Condition = MacroCondition(new DelayStep()) }).HasErrors());
    }

    [Fact]
    public void Conditions_ignore_the_find_step_click_and_goal_settings()
    {
        var check = new FindTextStep { Text = "OK", TimeoutMs = 0, Goal = WaitGoal.Disappear, Action = FoundTextAction.Click };
        Assert.Empty(MacroValidator.ValidateStep(new IfStep { Condition = MacroCondition(check) }));
    }

    [Fact]
    public void A_click_after_a_condition_does_not_warn_about_a_missing_find_step()
    {
        var issues = Validate(Flow.If(Flow.Text("OK")), new MouseClickStep { UseLastFoundText = true }, new EndIfStep());
        Assert.DoesNotContain(issues, i => i.Property == nameof(MouseClickStep.UseLastFoundText));
    }

    [Fact]
    public void Empty_blocks_and_disabled_markers_get_warnings()
    {
        var issues = Validate(Flow.Times(2), new EndRepeatStep(), Flow.If(Flow.Text("OK")), Flow.Move(1), new EndIfStep { Enabled = false });

        Assert.False(issues.HasErrors());
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.StepIndex == 0 && i.Message.Contains("no enabled steps"));
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.StepIndex == 4 && i.Message.Contains("has no effect"));
    }

    [Fact]
    public void New_block_steps_from_the_factory_are_valid_and_described()
    {
        foreach (var kind in new[] { StepKind.If, StepKind.Else, StepKind.EndIf, StepKind.Repeat, StepKind.EndRepeat })
        {
            var step = MacroFactory.CreateStep(kind);
            Assert.Equal(kind, step.Kind);
            Assert.False(MacroValidator.ValidateStep(step).HasErrors(), kind.ToString());
            Assert.False(string.IsNullOrWhiteSpace(StepDescriber.Describe(step)));
        }
    }

    [Fact]
    public void Descriptions_read_like_sentences()
    {
        Assert.Equal("If \"OK\" is on screen", StepDescriber.Describe(Flow.If(Flow.Text("OK"))));
        Assert.Equal("If \"Error\" is not on screen (wait up to 2 s)", StepDescriber.Describe(Flow.If(Flow.Text("Error", negate: true, timeoutMs: 2000))));
        Assert.Equal("Repeat 3 times", StepDescriber.Describe(Flow.Times(3)));
        Assert.Equal("Repeat while \"Go\" is on screen (max 50 turns, max 30 s)",
            StepDescriber.Describe(Flow.While(Flow.Text("Go"), maxTurns: 50, maxMs: 30_000)));
    }

    private static Condition MacroCondition(MacroStep check) => new() { Check = check };
}

public class WorkflowFileTests
{
    private static Macro SampleWithBlocks()
    {
        var image = new FindImageStep
        {
            TemplateWidth = 20,
            TemplateHeight = 16,
            TemplateData = TemplateCodec.Encode(TestImages.Icon(20, 16)),
            TimeoutMs = 0,
            SearchFullScreen = false,
            RegionX = 10, RegionY = 20, RegionWidth = 300, RegionHeight = 200,
        };

        var macro = Flow.Macro(
            Flow.If(Flow.Text("Shop", negate: true, timeoutMs: 1500)), Flow.Move(1), new ElseStep(), Flow.Move(2), new EndIfStep(),
            new RepeatStep
            {
                Mode = RepeatMode.Until,
                Condition = new Condition { Check = image },
                MaxIterations = 40,
                MaxDurationMs = 90_000,
                WhenLimitReached = LoopLimitBehavior.ExitLoop,
            },
            Flow.Move(3), new EndRepeatStep());
        macro.Id = Guid.NewGuid();
        return macro;
    }

    [Fact]
    public void Blocks_round_trip_as_format_5()
    {
        var macro = SampleWithBlocks();
        Assert.Equal(5, MacroDocument.RequiredSchemaVersion(macro));

        var json = MacroFileFormat.Serialize(macro);
        Assert.Contains("\"schemaVersion\": 5", json);
        Assert.Contains("\"type\": \"if\"", json);
        Assert.Contains("\"type\": \"endRepeat\"", json);

        Assert.True(MacroFileFormat.TryParse(json, out var parsed, out var error), error);
        Assert.Equal(macro.Steps.Select(s => s.Kind), parsed!.Steps.Select(s => s.Kind));

        var ifStep = Assert.IsType<IfStep>(parsed.Steps[0]);
        Assert.True(ifStep.Condition.Negate);
        Assert.Equal(ConditionKind.TextVisible, ifStep.Condition.Kind);
        Assert.Equal("Shop", Assert.IsType<FindTextStep>(ifStep.Condition.Check).Text);
        Assert.Equal(1500, ifStep.Condition.Search!.TimeoutMs);

        var repeat = Assert.IsType<RepeatStep>(parsed.Steps[5]);
        Assert.Equal(RepeatMode.Until, repeat.Mode);
        Assert.Equal(40, repeat.MaxIterations);
        Assert.Equal(90_000, repeat.MaxDurationMs);
        Assert.Equal(LoopLimitBehavior.ExitLoop, repeat.WhenLimitReached);
        var image = Assert.IsType<FindImageStep>(repeat.Condition!.Check);
        Assert.Equal(300, image.RegionWidth);
        Assert.True(TemplateCodec.TryDecode(image.TemplateData, image.TemplateWidth, image.TemplateHeight, out _, out _));

        Assert.False(MacroValidator.Validate(parsed).HasErrors());
    }

    [Fact]
    public void Repeat_times_does_not_write_a_condition()
    {
        var json = MacroFileFormat.Serialize(Flow.Macro(Flow.Times(2), Flow.Move(1), new EndRepeatStep()));
        Assert.DoesNotContain("\"condition\"", json);
    }

    [Fact]
    public void Macros_without_blocks_keep_their_old_format_version()
    {
        Assert.Equal(1, MacroDocument.RequiredSchemaVersion(Flow.Macro(Flow.Move(1), new DelayStep())));
        Assert.Equal(4, MacroDocument.RequiredSchemaVersion(Flow.Macro(new FindColorStep())));
        Assert.Contains("\"schemaVersion\": 1", MacroFileFormat.Serialize(Flow.Macro(Flow.Move(1))));
    }

    [Fact]
    public void Old_format_4_files_still_load_unchanged()
    {
        const string json = """
            { "schemaVersion": 4, "macro": { "id": "6f9619ff-8b86-d011-b42d-00c04fc964ff", "name": "Old v4",
              "steps": [ { "type": "findText", "text": "OK", "goal": "Disappear" }, { "type": "mouseMove", "x": 3, "y": 4 } ] } }
            """;

        Assert.True(MacroFileFormat.TryParse(json, out var macro, out var error), error);
        Assert.Equal(new[] { StepKind.FindText, StepKind.MouseMove }, macro!.Steps.Select(s => s.Kind));
        Assert.Equal(WaitGoal.Disappear, ((FindTextStep)macro.Steps[0]).Goal);
        Assert.False(MacroValidator.Validate(macro).HasErrors());
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 5, "macro": { "name": "x", "steps": [ { "type": "while" } ] } }""")]
    [InlineData("""{ "schemaVersion": 5, "macro": { "name": "x", "steps": [ { "type": "repeat", "mode": "Forever" } ] } }""")]
    [InlineData("""{ "schemaVersion": 5, "macro": { "name": "x", "steps": [ { "type": "if", "condition": { "check": { "type": "runProgram", "path": "cmd.exe" } } } ] } }""")]
    [InlineData("""{ "schemaVersion": 5, "macro": { "name": "x", "steps": [ { "type": "if", "condition": "Shop is visible" } ] } }""")]
    public void Damaged_or_script_like_block_files_are_refused(string json)
    {
        Assert.False(MacroFileFormat.TryParse(json, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Files_with_broken_block_structure_load_but_are_reported_and_cannot_play()
    {
        const string json = """
            { "schemaVersion": 5, "macro": { "name": "Broken", "steps": [
                { "type": "repeat", "mode": "Times", "count": 2 },
                { "type": "mouseMove", "x": 1, "y": 1 },
                { "type": "endIf" } ] } }
            """;

        Assert.True(MacroFileFormat.TryParse(json, out var macro, out var error), error);
        var issues = MacroValidator.ValidateForExecution(macro!);
        Assert.True(issues.HasErrors());
        Assert.Contains(issues, i => i.StepIndex == 2 && i.Message.Contains("doesn't match"));
    }

    [Fact]
    public void Condition_with_a_non_search_step_loads_but_is_an_error()
    {
        const string json = """
            { "schemaVersion": 5, "macro": { "name": "x", "steps": [
                { "type": "if", "condition": { "check": { "type": "delay", "durationMs": 5 } } },
                { "type": "endIf" } ] } }
            """;

        Assert.True(MacroFileFormat.TryParse(json, out var macro, out _));
        Assert.Contains(MacroValidator.Validate(macro!), i => i.Severity == IssueSeverity.Error && i.Message.Contains("can't use"));
    }

    [Fact]
    public void Import_gives_block_steps_new_ids_and_keeps_the_structure()
    {
        var macro = SampleWithBlocks();
        var oldIds = macro.Steps.Select(s => s.Id).ToList();

        var imported = MacroFileFormat.PrepareForImport(MacroJson.Clone(macro), Array.Empty<string>());

        Assert.All(imported.Steps.Zip(oldIds), p => Assert.NotEqual(p.Second, p.First.Id));
        Assert.True(BlockStructure.Analyze(imported.Steps).IsValid);
    }
}
