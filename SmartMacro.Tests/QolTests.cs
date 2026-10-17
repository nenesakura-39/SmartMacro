using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Recording;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Vision;
using SmartMacro.Core.Workflow;

namespace SmartMacro.Tests;

// =====================================================================================================================
// Quality-of-life batch: "when found" / "when true" actions (incl. hold), Hold / Drag mouse clicks, feedback loop
// presets (pulse, finish when in zone), simplified mouse paths in recordings and the "clean up mouse paths" command.
// Fake screen / input / clock only: these prove what SmartMacro sends and when, not how a real game reacts.
// =====================================================================================================================

public class FoundActionTests
{
    private static FindColorStep GreenSearch(FoundTextAction action) => new()
    {
        Color = "#00C800", Tolerance = 10, MinPixels = 20, TimeoutMs = 0, CheckIntervalMs = 50, Action = action,
    };

    [Fact]
    public async Task Find_step_can_hold_the_left_button_on_the_match()
    {
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };
        var step = GreenSearch(FoundTextAction.Hold);
        step.ActionHoldMs = 500;

        var result = await new PlaybackEngine(input, clock, capture: new FakeCapture(Flow.Screen(green: true))).RunAsync(Flow.Macro(step));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 205,105", "mouse Left down", "mouse Left up" }, input.Calls);
        Assert.True(input.Times[2] - input.Times[1] >= TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task Find_step_clicks_the_center_of_a_match_wherever_it_is()
    {
        // The "Shake" button case: the same step clicks wherever the colour shows up this time.
        var input = new FakeInput();
        var moved = TestImages.Solid(300, 200, RgbColor.Black);
        moved.FillRectangle(new Core.Ocr.ScreenRect(20, 30, 11, 11), new RgbColor(0, 200, 0));

        var result = await new PlaybackEngine(input, new FakeClock(), capture: new FakeCapture(Flow.Screen(green: true), moved))
            .RunAsync(Flow.Macro(GreenSearch(FoundTextAction.Click), GreenSearch(FoundTextAction.Click)));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 205,105", "mouse Left down", "mouse Left up", "move 25,35", "mouse Left down", "mouse Left up" }, input.Calls);
    }

    [Fact]
    public async Task If_when_true_action_clicks_what_the_condition_saw_before_the_inner_steps()
    {
        var input = new FakeInput();
        var ifStep = Flow.If(Flow.Green());
        ifStep.ThenAction = FoundTextAction.Click;
        ifStep.ThenOffsetX = 10;

        var result = await new PlaybackEngine(input, new FakeClock(), capture: new FakeCapture(Flow.Screen(green: true)))
            .RunAsync(Flow.Macro(ifStep, Flow.Move(1), new EndIfStep()));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 215,105", "mouse Left down", "mouse Left up", "move 1,0" }, input.Calls);
    }

    [Fact]
    public async Task If_when_true_action_does_nothing_for_a_negated_condition_or_when_false()
    {
        var input = new FakeInput();
        var negated = Flow.If(Flow.Green(negate: true));
        negated.ThenAction = FoundTextAction.Click;
        var normal = Flow.If(Flow.Green());
        normal.ThenAction = FoundTextAction.Click;

        var result = await new PlaybackEngine(input, new FakeClock(), capture: new FakeCapture(Flow.Screen(green: false)))
            .RunAsync(Flow.Macro(negated, Flow.Move(1), new EndIfStep(), normal, Flow.Move(2), new EndIfStep()));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 1,0" }, input.Calls); // NOT-on-screen is true but there is nothing to click
    }

    [Fact]
    public void Validation_of_found_and_when_true_actions()
    {
        var hold = GreenSearch(FoundTextAction.Hold);
        hold.ActionHoldMs = 0;
        Assert.Contains(MacroValidator.ValidateStep(hold), i => i.Severity == IssueSeverity.Error && i.Property == nameof(ScreenSearchStep.ActionHoldMs));

        var negated = Flow.If(Flow.Green(negate: true));
        negated.ThenAction = FoundTextAction.Click;
        Assert.Contains(MacroValidator.ValidateStep(negated), i => i.Severity == IssueSeverity.Warning && i.Property == nameof(IfStep.ThenAction));

        var far = Flow.If(Flow.Green());
        far.ThenAction = FoundTextAction.Click;
        far.ThenOffsetX = 99_999;
        Assert.Contains(MacroValidator.ValidateStep(far), i => i.Severity == IssueSeverity.Error);

        // A plain condition never reports the hold time (conditions don't use the "when found" action).
        var condition = Flow.If(Flow.Green());
        Assert.DoesNotContain(MacroValidator.ValidateStep(condition), i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void Describes_find_steps_as_find_then_action()
    {
        var image = new FindImageStep { TemplateWidth = 121, TemplateHeight = 134, Action = FoundTextAction.Click };
        Assert.Equal("Find image (121×134) on screen → Click center when found (wait up to 10 s)", StepDescriber.Describe(image));

        var shake = new FindImageStep
        {
            TemplateWidth = 40, TemplateHeight = 20, Action = FoundTextAction.Hold, ActionHoldMs = 300, OffsetX = 5, OffsetY = -3,
            TimeoutMs = 0, WhenNotFound = TextNotFoundBehavior.SkipNextStep,
        };
        Assert.Equal("Find image (40×20) on screen → Hold left button 300 ms on center (+5, -3) when found (look once; if not found: skip next step)",
            StepDescriber.Describe(shake));

        var ifStep = Flow.If(Flow.Text("Buy"));
        ifStep.ThenAction = FoundTextAction.DoubleClick;
        Assert.Equal("If \"Buy\" is on screen → Double-click center when true", StepDescriber.Describe(ifStep));
    }
}

public class ClickHoldAndDragTests
{
    [Fact]
    public async Task Hold_presses_waits_and_releases_at_the_position()
    {
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };

        var result = await new PlaybackEngine(input, clock).RunAsync(Flow.Macro(
            new MouseClickStep { Action = MouseClickAction.Hold, HoldMs = 850, X = 10, Y = 20 }));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 10,20", "mouse Left down", "mouse Left up" }, input.Calls);
        Assert.Equal(850, (input.Times[2] - input.Times[1]).TotalMilliseconds, precision: 3);
    }

    [Fact]
    public async Task Drag_glides_from_start_to_end_with_the_button_held()
    {
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };

        var result = await new PlaybackEngine(input, clock).RunAsync(Flow.Macro(
            new MouseClickStep { Action = MouseClickAction.Drag, X = 0, Y = 0, EndX = 100, EndY = 50, DragDurationMs = 160 }));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        var calls = input.Calls;
        Assert.Equal("move 0,0", calls[0]);
        Assert.Equal("mouse Left down", calls[1]);
        Assert.Equal("move 100,50", calls[^2]);
        Assert.Equal("mouse Left up", calls[^1]);

        // 160 ms in 8 ms steps (~120 per second since the live-test fixes): 20 moves after the press, evenly spaced.
        var moves = calls.Skip(2).Take(calls.Count - 3).ToList();
        Assert.Equal(20, moves.Count);
        Assert.Equal("move 5,2", moves[0]);   // 2.5 rounds to even
        Assert.Equal("move 50,25", moves[9]);
        Assert.Equal(160, (input.Times[^1] - input.Times[1]).TotalMilliseconds, precision: 3);
    }

    [Fact]
    public async Task Drag_from_an_unknown_cursor_position_goes_straight_to_the_end()
    {
        var input = new FakeInput(); // the fake can't report the cursor position

        var result = await new PlaybackEngine(input, new FakeClock()).RunAsync(Flow.Macro(
            new MouseClickStep { Action = MouseClickAction.Drag, UseCurrentPosition = true, EndX = 300, EndY = 40, DragDurationMs = 200 }));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "mouse Left down", "move 300,40", "mouse Left up" }, input.Calls);
    }

    [Fact]
    public async Task Stop_during_a_hold_releases_the_button()
    {
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, new FakeClock());
        input.OnCall = call =>
        {
            if (call == "mouse Left down") engine.Stop();
        };

        var result = await engine.RunAsync(Flow.Macro(new MouseClickStep { Action = MouseClickAction.Hold, HoldMs = 5_000, X = 1, Y = 1 }));

        Assert.Equal(PlaybackOutcome.Stopped, result.Outcome);
        Assert.Equal("mouse Left up", input.Calls[^1]);
    }

    [Fact]
    public void Validation_and_descriptions()
    {
        Assert.Contains(MacroValidator.ValidateStep(new MouseClickStep { Action = MouseClickAction.Hold, HoldMs = 0 }),
            i => i.Severity == IssueSeverity.Error && i.Property == nameof(MouseClickStep.HoldMs));
        Assert.DoesNotContain(MacroValidator.ValidateStep(new MouseClickStep { Action = MouseClickAction.Hold, HoldMs = 850 }),
            i => i.Severity == IssueSeverity.Warning && i.Property == nameof(MouseClickStep.HoldMs));
        Assert.Contains(MacroValidator.ValidateStep(new MouseClickStep { Action = MouseClickAction.Drag, X = 5, Y = 5, EndX = 5, EndY = 5 }),
            i => i.Severity == IssueSeverity.Warning && i.Property == "EndPosition");
        Assert.Contains(MacroValidator.ValidateStep(new MouseClickStep { Action = MouseClickAction.Drag, EndX = 500_000 }),
            i => i.Severity == IssueSeverity.Error && i.Property == "EndPosition");
        Assert.Contains(MacroValidator.ValidateStep(new MouseClickStep { Action = MouseClickAction.Drag, EndX = 10, DragDurationMs = -1 }),
            i => i.Severity == IssueSeverity.Error && i.Property == nameof(MouseClickStep.DragDurationMs));

        Assert.Equal("Hold left button for 850 ms at (10, 20)",
            StepDescriber.Describe(new MouseClickStep { Action = MouseClickAction.Hold, HoldMs = 850, X = 10, Y = 20 }));
        Assert.Equal("Drag left button from (1, 2) to (300, 40) over 0.5 s",
            StepDescriber.Describe(new MouseClickStep { Action = MouseClickAction.Drag, X = 1, Y = 2, EndX = 300, EndY = 40 }));
        // Old click descriptions are unchanged.
        Assert.Equal("Click left button at (1, 2)", StepDescriber.Describe(new MouseClickStep { X = 1, Y = 2 }));
    }
}

public class FeedbackPresetTests
{
    private static FeedbackObservation At(double percent, double zoneStart = 40, double zoneEnd = 60) =>
        new(MarkerStatus.Found, percent, true, zoneStart, zoneEnd);

    private static FeedbackRules PulseRules(int periodMs = 200) =>
        new(FeedbackAction.Release, FeedbackAction.Pulse, FeedbackAction.Hold, 0, TimeSpan.Zero, TimeSpan.FromMilliseconds(500),
            FeedbackLostAction.ReleaseAndWait, TimeSpan.FromMilliseconds(periodMs));

    [Theory]
    [InlineData(41, 0.0)]   // near the "release" edge
    [InlineData(50, 0.5)]   // middle
    [InlineData(55, 0.75)]
    [InlineData(59, 1.0)]   // near the "hold" edge
    public void Pulse_duty_follows_the_marker_inside_the_zone(double position, double expected)
    {
        Assert.Equal(expected, FeedbackController.PulseDuty(position, 40, 60, FeedbackAction.Release, FeedbackAction.Hold), precision: 6);
        // Vertical "follow" (hold when the target is above = before): the same spot gives the opposite share.
        Assert.Equal(1 - expected, FeedbackController.PulseDuty(position, 40, 60, FeedbackAction.Hold, FeedbackAction.Release), precision: 6);
    }

    [Fact]
    public void Pulse_holds_for_part_of_each_cycle()
    {
        var controller = new FeedbackController(PulseRules());

        Assert.Equal(FeedbackCommand.Press, controller.Update(At(50), TimeSpan.FromMilliseconds(10)).Command);    // phase 5 %  < 50 %
        Assert.Equal(FeedbackCommand.None, controller.Update(At(50), TimeSpan.FromMilliseconds(60)).Command);     // phase 30 % still held
        Assert.Equal(FeedbackCommand.Release, controller.Update(At(50), TimeSpan.FromMilliseconds(150)).Command); // phase 75 %
        Assert.Equal(FeedbackCommand.Press, controller.Update(At(50), TimeSpan.FromMilliseconds(210)).Command);   // next cycle
        Assert.Equal(FeedbackCommand.Release, controller.Update(At(41), TimeSpan.FromMilliseconds(220)).Command); // near the release edge: never held
        Assert.Equal(FeedbackCommand.Press, controller.Update(At(59), TimeSpan.FromMilliseconds(390)).Command);   // near the hold edge: always held
        Assert.Equal(FeedbackCommand.None, controller.Update(At(80), TimeSpan.FromMilliseconds(400)).Command);   // after: Hold, already held
        Assert.True(controller.IsHeld);
        Assert.Equal(FeedbackCommand.Release, controller.Update(At(10), TimeSpan.FromMilliseconds(410)).Command); // before: Release
    }

    [Fact]
    public void Follow_target_preset_sets_rules_for_the_bar_direction()
    {
        var step = new FeedbackLoopStep();
        FeedbackPresets.Apply(step, FeedbackPreset.FollowTargetBar);

        Assert.Equal(FeedbackPreset.FollowTargetBar, step.Preset);
        Assert.Equal(FeedbackZoneMode.DetectedByColor, step.ZoneMode);
        Assert.Equal((FeedbackAction.Release, FeedbackAction.Pulse, FeedbackAction.Hold), (step.WhenBeforeZone, step.WhenInsideZone, step.WhenAfterZone));
        Assert.Equal(FeedbackLostAction.ExitLoop, step.WhenTargetLost);
        Assert.False(step.FinishWhenInZone);
        Assert.False(MacroValidator.ValidateStep(step).HasErrors());

        step.Orientation = BarOrientation.Vertical;
        step.RegionWidth = 30;
        step.RegionHeight = 300;
        FeedbackPresets.ApplyFollowRules(step);
        Assert.Equal((FeedbackAction.Hold, FeedbackAction.Pulse, FeedbackAction.Release), (step.WhenBeforeZone, step.WhenInsideZone, step.WhenAfterZone));
        Assert.False(MacroValidator.ValidateStep(step).HasErrors());
    }

    [Fact]
    public void Click_when_in_zone_preset_and_its_release_variant()
    {
        var step = new FeedbackLoopStep();
        FeedbackPresets.Apply(step, FeedbackPreset.ClickWhenInZone);
        Assert.True(step.FinishWhenInZone);
        Assert.Equal((FeedbackAction.Release, FeedbackAction.Tap, FeedbackAction.Release), (step.WhenBeforeZone, step.WhenInsideZone, step.WhenAfterZone));
        Assert.False(MacroValidator.ValidateStep(step).HasErrors());

        FeedbackPresets.SetZoneEntryAction(step, releaseHeldInput: true);
        Assert.Equal((FeedbackAction.Hold, FeedbackAction.Release, FeedbackAction.Hold), (step.WhenBeforeZone, step.WhenInsideZone, step.WhenAfterZone));
        Assert.False(MacroValidator.ValidateStep(step).HasErrors());

        FeedbackPresets.Apply(step, FeedbackPreset.ClickWhenInZone); // applying again keeps the chosen variant
        Assert.Equal(FeedbackAction.Release, step.WhenInsideZone);

        FeedbackPresets.Apply(step, FeedbackPreset.Custom); // custom only shows everything
        Assert.Equal(FeedbackAction.Release, step.WhenInsideZone);
        Assert.True(step.FinishWhenInZone);
    }

    [Fact]
    public async Task Click_when_in_zone_taps_once_when_the_marker_arrives_and_ends()
    {
        var capture = new ScriptedBarCapture(i => new BarFrame(10 + i * 10));
        var (engine, input, _, messages) = Feedback.Engine(capture);
        var step = Feedback.Loop(s => FeedbackPresets.Apply(s, FeedbackPreset.ClickWhenInZone));
        step.RegionX = ScriptedBarCapture.Bar.X; // the preset keeps the area
        step.ZoneStartPercent = 40;
        step.ZoneEndPercent = 60;

        var result = await engine.RunAsync(Flow.Macro(step, Flow.Move(7)));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "mouse Left down", "mouse Left up", "move 7,0" }, Feedback.Calls(input));
        Assert.Equal(4, capture.BarCaptures); // 10, 20, 30 % then 40 % = inside
        Assert.Contains("the marker reached the zone", Feedback.Summary(messages));
    }

    [Fact]
    public async Task Release_variant_holds_until_the_marker_arrives_then_lets_go()
    {
        var capture = new ScriptedBarCapture(i => new BarFrame(10 + i * 10));
        var (engine, input, _, messages) = Feedback.Engine(capture);
        var step = Feedback.Loop(s =>
        {
            FeedbackPresets.Apply(s, FeedbackPreset.ClickWhenInZone);
            FeedbackPresets.SetZoneEntryAction(s, releaseHeldInput: true);
        });

        var result = await engine.RunAsync(Flow.Macro(step));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "mouse Left down", "mouse Left up" }, Feedback.Calls(input));
        Assert.Contains("the marker reached the zone", Feedback.Summary(messages));
    }

    [Fact]
    public async Task Marker_already_inside_at_the_start_is_clicked_once()
    {
        var capture = new ScriptedBarCapture(_ => new BarFrame(50));
        var (engine, input, _, _) = Feedback.Engine(capture);
        var step = Feedback.Loop(s => FeedbackPresets.Apply(s, FeedbackPreset.ClickWhenInZone));

        var result = await engine.RunAsync(Flow.Macro(step));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "mouse Left down", "mouse Left up" }, Feedback.Calls(input));
        Assert.Equal(1, capture.BarCaptures);
    }

    [Fact]
    public void Validation_of_pulse_and_finish()
    {
        var pulseOutside = new FeedbackLoopStep { WhenBeforeZone = FeedbackAction.Pulse };
        Assert.Contains(MacroValidator.ValidateStep(pulseOutside), i => i.Severity == IssueSeverity.Error && i.Property == "Rules");

        var badPeriod = new FeedbackLoopStep { WhenInsideZone = FeedbackAction.Pulse, PulsePeriodMs = 10 };
        Assert.Contains(MacroValidator.ValidateStep(badPeriod), i => i.Property == nameof(FeedbackLoopStep.PulsePeriodMs));

        var finishKeep = new FeedbackLoopStep { FinishWhenInZone = true }; // inside rule = Keep
        Assert.Contains(MacroValidator.ValidateStep(finishKeep), i => i.Severity == IssueSeverity.Error && i.Property == nameof(FeedbackLoopStep.FinishWhenInZone));

        // Pulse alone counts as "presses something".
        var pulseOnly = new FeedbackLoopStep { WhenBeforeZone = FeedbackAction.Release, WhenInsideZone = FeedbackAction.Pulse, WhenAfterZone = FeedbackAction.Release };
        Assert.DoesNotContain(MacroValidator.ValidateStep(pulseOnly), i => i.Severity == IssueSeverity.Error && i.Property == "Rules");
    }

    [Fact]
    public void Describes_presets()
    {
        var follow = new FeedbackLoopStep { MarkerColor = "#FF8800", ZoneColor = "#FFFFFF" };
        FeedbackPresets.Apply(follow, FeedbackPreset.FollowTargetBar);
        Assert.StartsWith("Follow target #FF8800 with bar #FFFFFF: release before, pulse inside, hold after", StepDescriber.Describe(follow));

        var click = new FeedbackLoopStep();
        FeedbackPresets.Apply(click, FeedbackPreset.ClickWhenInZone);
        Assert.StartsWith("Click when marker #FF0000 enters zone 40–60 %", StepDescriber.Describe(click));
        Assert.Contains("ends when it reaches the zone", StepDescriber.Describe(click));
    }
}

public class QolFileFormatTests
{
    [Fact]
    public void New_options_need_format_8_and_old_macros_keep_their_format()
    {
        Assert.True(MacroDocument.CurrentSchemaVersion >= 8); // 9 since the live-test fixes
        Assert.Equal(8, MacroDocument.RequiredSchemaVersion(Flow.Macro(new FindColorStep { Action = FoundTextAction.Hold })));
        Assert.Equal(8, MacroDocument.RequiredSchemaVersion(Flow.Macro(new IfStep { Condition = Flow.Green(), ThenAction = FoundTextAction.Click }, new EndIfStep())));
        Assert.Equal(8, MacroDocument.RequiredSchemaVersion(Flow.Macro(new MouseClickStep { Action = MouseClickAction.Hold, HoldMs = 5 })));
        Assert.Equal(8, MacroDocument.RequiredSchemaVersion(Flow.Macro(new MouseClickStep { Action = MouseClickAction.Drag })));
        var preset = new FeedbackLoopStep();
        FeedbackPresets.Apply(preset, FeedbackPreset.ClickWhenInZone);
        Assert.Equal(8, MacroDocument.RequiredSchemaVersion(Flow.Macro(preset)));

        Assert.Equal(1, MacroDocument.RequiredSchemaVersion(Flow.Macro(new MouseClickStep { X = 3 })));
        Assert.Equal(4, MacroDocument.RequiredSchemaVersion(Flow.Macro(new FindColorStep { Action = FoundTextAction.Click })));
        Assert.Equal(5, MacroDocument.RequiredSchemaVersion(Flow.Macro(Flow.If(Flow.Green()), new EndIfStep())));
        Assert.Equal(6, MacroDocument.RequiredSchemaVersion(Flow.Macro(new FeedbackLoopStep())));
    }

    [Fact]
    public void New_options_round_trip_through_a_file()
    {
        var feedback = new FeedbackLoopStep();
        FeedbackPresets.Apply(feedback, FeedbackPreset.FollowTargetBar);
        feedback.PulsePeriodMs = 240;
        var macro = Flow.Macro(
            new FindImageStep { Action = FoundTextAction.Hold, ActionHoldMs = 777 },
            new IfStep { Condition = Flow.Green(), ThenAction = FoundTextAction.RightClick, ThenOffsetX = 4, ThenOffsetY = -6, ThenHoldMs = 900 },
            new EndIfStep(),
            new MouseClickStep { Action = MouseClickAction.Drag, X = 1, Y = 2, EndX = 30, EndY = 40, DragDurationMs = 650 },
            feedback);

        var json = MacroFileFormat.Serialize(macro);
        Assert.True(MacroFileFormat.TryParse(json, out var loaded, out var error), error);

        var image = Assert.IsType<FindImageStep>(loaded!.Steps[0]);
        Assert.Equal((FoundTextAction.Hold, 777), (image.Action, image.ActionHoldMs));
        var ifStep = Assert.IsType<IfStep>(loaded.Steps[1]);
        Assert.Equal((FoundTextAction.RightClick, 4, -6, 900), (ifStep.ThenAction, ifStep.ThenOffsetX, ifStep.ThenOffsetY, ifStep.ThenHoldMs));
        var drag = Assert.IsType<MouseClickStep>(loaded.Steps[3]);
        Assert.Equal((MouseClickAction.Drag, 30, 40, 650), (drag.Action, drag.EndX, drag.EndY, drag.DragDurationMs));
        var loop = Assert.IsType<FeedbackLoopStep>(loaded.Steps[4]);
        Assert.Equal((FeedbackPreset.FollowTargetBar, FeedbackAction.Pulse, 240), (loop.Preset, loop.WhenInsideZone, loop.PulsePeriodMs));
    }

    [Fact]
    public void Files_made_before_these_options_load_with_the_old_behaviour()
    {
        const string json = """
            { "schemaVersion": 6, "macro": { "name": "old", "steps": [
              { "type": "mouseClick", "x": 5, "y": 6 },
              { "type": "findColor", "color": "#00C800", "action": "Click" },
              { "type": "feedbackLoop" } ] } }
            """;
        Assert.True(MacroFileFormat.TryParse(json, out var macro, out var error), error);

        var click = Assert.IsType<MouseClickStep>(macro!.Steps[0]);
        Assert.Equal((MouseClickAction.Click, MouseClickStep.DefaultDragDurationMs), (click.Action, click.DragDurationMs));
        var color = Assert.IsType<FindColorStep>(macro.Steps[1]);
        Assert.Equal(ScreenSearchStep.DefaultActionHoldMs, color.ActionHoldMs);
        var loop = Assert.IsType<FeedbackLoopStep>(macro.Steps[2]);
        Assert.Equal((FeedbackPreset.Custom, false, FeedbackLoopStep.DefaultPulsePeriodMs), (loop.Preset, loop.FinishWhenInZone, loop.PulsePeriodMs));

        // A plain feedback loop doesn't write the preset fields at all.
        var written = MacroFileFormat.Serialize(Flow.Macro(new FeedbackLoopStep()));
        Assert.DoesNotContain("\"preset\"", written);
        Assert.DoesNotContain("finishWhenInZone", written);
    }
}

public class SimplifiedRecordingTests
{
    private static RawInputEvent Down(long t, int x, int y) => new(t, RawInputKind.MouseDown, x, y);
    private static RawInputEvent Up(long t, int x, int y) => new(t, RawInputKind.MouseUp, x, y);
    private static RawInputEvent Move(long t, int x, int y) => new(t, RawInputKind.MouseMove, x, y);
    private static RawInputEvent KeyDown(long t, int vk) => new(t, RawInputKind.KeyDown, VirtualKey: vk);
    private static RawInputEvent KeyUp(long t, int vk) => new(t, RawInputKind.KeyUp, VirtualKey: vk);

    /// <summary>Five shop clicks with the mouse wandering ~60 times between each (a few seconds of hovering).</summary>
    private static List<RawInputEvent> FiveShopClicks()
    {
        var events = new List<RawInputEvent>();
        var t = 0L;
        var x = 100;
        var y = 100;
        for (var click = 0; click < 5; click++)
        {
            for (var i = 0; i < 60; i++)
            {
                t += 16;
                x += (i % 7) - 2;
                y += (i % 5) - 1;
                events.Add(Move(t, x, y));
            }

            events.Add(Down(t + 200, x, y));
            events.Add(Up(t + 260, x, y));
            t += 260;
        }

        return events;
    }

    [Fact]
    public void Simplified_mouse_paths_turn_five_clicks_into_five_click_steps()
    {
        var raw = FiveShopClicks();
        var steps = RecordingConverter.Convert(raw, new RecordingOptions { SimplifyMousePath = true, CollapseMousePaths = true });

        Assert.Equal(5, steps.OfType<MouseClickStep>().Count());
        Assert.Empty(steps.OfType<MouseMoveStep>());
        Assert.Equal(9, steps.Count); // Click, Delay, Click, Delay, ... Click

        // Timing is kept: waits + hold times add up to the time between the first and last press.
        var downs = raw.Where(e => e.Kind == RawInputKind.MouseDown).ToList();
        var clicks = steps.OfType<MouseClickStep>().ToList();
        var played = steps.OfType<DelayStep>().Sum(d => (long)d.DurationMs) + clicks.Take(4).Sum(c => (long)c.HoldMs);
        Assert.Equal(downs[^1].TimeMs - downs[0].TimeMs, played);

        // Without the option the path is still there (simplified, but many Move steps).
        var full = RecordingConverter.Convert(raw, new RecordingOptions { SimplifyMousePath = true });
        Assert.NotEmpty(full.OfType<MouseMoveStep>());
    }

    [Fact]
    public void Drags_keep_their_path()
    {
        var raw = new List<RawInputEvent> { Down(0, 10, 10) };
        for (var i = 1; i <= 20; i++) raw.Add(Move(i * 50, 10 + i * 10, 10));
        raw.Add(Up(1100, 210, 10));

        var steps = RecordingConverter.Convert(raw, new RecordingOptions { SimplifyMousePath = true, CollapseMousePaths = true });

        Assert.Equal(MouseClickAction.Down, Assert.IsType<MouseClickStep>(steps[0]).Action);
        Assert.Contains(steps.OfType<MouseMoveStep>(), m => m.X == 210);
        Assert.Equal(MouseClickAction.Up, Assert.IsType<MouseClickStep>(steps[^1]).Action);
    }

    [Fact]
    public void Hovering_before_a_key_press_keeps_one_move()
    {
        var raw = new List<RawInputEvent>();
        for (var i = 1; i <= 30; i++) raw.Add(Move(i * 16, 100 + i * 5, 200));
        raw.Add(KeyDown(700, 0x45));
        raw.Add(KeyUp(760, 0x45));

        var steps = RecordingConverter.Convert(raw, new RecordingOptions { SimplifyMousePath = true, CollapseMousePaths = true });

        var move = Assert.Single(steps.OfType<MouseMoveStep>());
        Assert.Equal((250, 200), (move.X, move.Y));
        Assert.IsType<KeyPressStep>(steps[^1]);
    }

    [Fact]
    public void Settings_turn_simplified_paths_on_by_default()
    {
        Assert.True(new AppSettings().SimplifyMousePaths);
        var loaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{ \"schemaVersion\": 2 }", MacroJson.Options)!;
        Assert.True(loaded.SimplifyMousePaths);
    }
}

public class StepCleanupTests
{
    private static DelayStep Wait(int ms) => new() { DurationMs = ms };
    private static MouseMoveStep Move(int x) => new() { X = x, Y = 0 };

    [Fact]
    public void Moves_before_a_positioned_click_disappear_and_timing_is_kept()
    {
        var click = new MouseClickStep { X = 5, Y = 5 };
        var steps = new List<MacroStep> { Wait(10), Move(1), Wait(10), Move(2), Wait(10), Move(3), Wait(30), click };

        var result = StepCleanup.CollapseMouseMoves(steps);

        Assert.Equal(2, result.Steps.Count);
        Assert.Equal(60, Assert.IsType<DelayStep>(result.Steps[0]).DurationMs);
        Assert.Same(click, result.Steps[1]);
        Assert.Equal(6, result.RemovedSteps);
        Assert.Equal(1, result.CollapsedRuns);
    }

    [Fact]
    public void Last_position_is_kept_before_a_key_or_a_click_at_the_cursor()
    {
        var key = new KeyPressStep();
        var steps = new List<MacroStep> { Wait(10), Move(1), Wait(10), Move(2), Wait(20), key };

        var result = StepCleanup.CollapseMouseMoves(steps);

        Assert.Equal(4, result.Steps.Count);
        Assert.Equal(20, Assert.IsType<DelayStep>(result.Steps[0]).DurationMs);
        Assert.Equal(2, Assert.IsType<MouseMoveStep>(result.Steps[1]).X);
        Assert.Equal(20, Assert.IsType<DelayStep>(result.Steps[2]).DurationMs);
        Assert.Same(key, result.Steps[3]);
    }

    [Fact]
    public void Drags_comments_and_disabled_steps_are_left_alone()
    {
        var steps = new List<MacroStep>
        {
            new MouseClickStep { Action = MouseClickAction.Down, X = 0, Y = 0 }, Wait(10), Move(1), Wait(10), Move(2),
            new MouseClickStep { Action = MouseClickAction.Up, X = 2, Y = 0 },
            Move(5), new MouseMoveStep { X = 6, Comment = "hover the shop" }, Move(7), new DelayStep { DurationMs = 5, Enabled = false },
        };

        var result = StepCleanup.CollapseMouseMoves(steps);

        Assert.Equal(steps, result.Steps);
        Assert.Equal(0, result.RemovedSteps);
    }

    [Fact]
    public void Only_the_given_range_is_cleaned()
    {
        var steps = new List<MacroStep> { Wait(10), Move(1), Wait(10), Move(2), new KeyPressStep(), Wait(10), Move(3), Wait(10), Move(4), new KeyPressStep() };

        var result = StepCleanup.CollapseMouseMoves(steps, 5, 9);

        Assert.Equal(steps.Take(5), result.Steps.Take(5));
        Assert.Equal(new[] { typeof(DelayStep), typeof(MouseMoveStep), typeof(KeyPressStep) }, result.Steps.Skip(5).Select(s => s.GetType()));
        Assert.Equal(20, ((DelayStep)result.Steps[5]).DurationMs);
        Assert.Equal(4, ((MouseMoveStep)result.Steps[6]).X);
    }

    [Fact]
    public void Cleaned_macro_is_still_valid_and_inside_blocks_works()
    {
        var steps = new List<MacroStep>
        {
            new RepeatStep { Mode = RepeatMode.Times, Count = 2 }, Wait(10), Move(1), Wait(10), Move(2), new MouseClickStep { X = 9, Y = 9 }, new EndRepeatStep(),
        };

        var result = StepCleanup.CollapseMouseMoves(steps);
        var macro = Flow.Macro(result.Steps.ToArray());

        Assert.Equal(4, result.Steps.Count);
        Assert.False(MacroValidator.Validate(macro).HasErrors());
    }
}

// =====================================================================================================================
// Next-gen QoL batch: "where to look" (main monitor / a specific monitor / an app window), smooth / human-curve mouse
// movement, explicit click timing, Quick patterns, the HUD activity text and the plain-English summaries.
// Fake screen / input / clock / layout only: these prove what SmartMacro searches and sends and when.
// =====================================================================================================================

/// <summary>A fake monitor / window list.</summary>
internal sealed class FakeLayout : IScreenLayout
{
    public List<MonitorInfo> Monitors { get; } = new();
    public List<WindowInfo> Windows { get; } = new();
    public int WindowLookups { get; private set; }

    public IReadOnlyList<MonitorInfo> GetMonitors() => Monitors;

    public IReadOnlyList<WindowInfo> GetWindows()
    {
        WindowLookups++;
        return Windows;
    }

    /// <summary>Three 100×100 monitors side by side: main at (0, 0), one left of it, one right of it.</summary>
    public static FakeLayout ThreeMonitors()
    {
        var layout = new FakeLayout();
        layout.Monitors.AddRange(SearchArea.NumberMonitors(new[]
        {
            (new ScreenRect(100, 0, 100, 100), false, "\\\\.\\DISPLAY3"),
            (new ScreenRect(0, 0, 100, 100), true, "\\\\.\\DISPLAY1"),
            (new ScreenRect(-100, 0, 100, 100), false, "\\\\.\\DISPLAY2"),
        }));
        return layout;
    }
}

internal static class ScopeScreens
{
    /// <summary>
    /// A black desktop from x = -100 to 200 (three 100 px monitors) with an 11×11 green square on the RIGHT monitor at
    /// (150, 40) – center (155, 45).
    /// </summary>
    public static FakeCapture GreenOnRightMonitor()
    {
        var screen = TestImages.Solid(300, 100, RgbColor.Black);
        screen.FillRectangle(new ScreenRect(250, 40, 11, 11), new RgbColor(0, 200, 0));
        return new FakeCapture(-100, 0, screen);
    }

    public static FindColorStep Green(SearchScope scope, FoundTextAction action = FoundTextAction.None) => new()
    {
        Color = "#00C800", Tolerance = 10, MinPixels = 20, TimeoutMs = 0, CheckIntervalMs = 50,
        Scope = scope, Action = action, WhenNotFound = TextNotFoundBehavior.Continue,
    };
}

public class SearchScopeTests
{
    private static readonly ScreenRect Desktop = new(-100, 0, 300, 100);

    [Fact]
    public void Monitors_are_numbered_main_first_then_left_to_right()
    {
        var monitors = FakeLayout.ThreeMonitors().Monitors;

        Assert.Equal(new[] { 1, 2, 3 }, monitors.Select(m => m.Number));
        Assert.True(monitors[0].IsPrimary);
        Assert.Equal(new ScreenRect(0, 0, 100, 100), monitors[0].Bounds);
        Assert.Equal(new ScreenRect(-100, 0, 100, 100), monitors[1].Bounds);
        Assert.Equal(new ScreenRect(100, 0, 100, 100), monitors[2].Bounds);
        Assert.Equal("Monitor 1 — Primary (100×100)", monitors[0].Label);
        Assert.Equal("Monitor 3 (100×100)", monitors[2].Label);
    }

    [Fact]
    public void Each_scope_resolves_to_the_right_rectangle()
    {
        var layout = FakeLayout.ThreeMonitors();
        layout.Windows.Add(new WindowInfo(1, "Roblox", new ScreenRect(120, 20, 60, 60)));

        Assert.Equal(Desktop, SearchArea.Resolve(new FindColorStep { Scope = SearchScope.AllMonitors }, Desktop, layout));
        Assert.Equal(new ScreenRect(0, 0, 100, 100), SearchArea.Resolve(new FindColorStep { Scope = SearchScope.PrimaryMonitor }, Desktop, layout));
        Assert.Equal(new ScreenRect(-100, 0, 100, 100), SearchArea.Resolve(new FindColorStep { Scope = SearchScope.Monitor, MonitorNumber = 2 }, Desktop, layout));
        Assert.Equal(new ScreenRect(120, 20, 60, 60), SearchArea.Resolve(new FindColorStep { Scope = SearchScope.Window, WindowTitle = "roblox" }, Desktop, layout));
        var area = new FindColorStep { Scope = SearchScope.CustomArea, RegionX = 5, RegionY = 6, RegionWidth = 50, RegionHeight = 40 };
        Assert.Equal(new ScreenRect(5, 6, 50, 40), SearchArea.Resolve(area, Desktop, layout));
    }

    [Fact]
    public void Without_a_layout_the_main_monitor_means_the_whole_desktop()
    {
        Assert.Equal(Desktop, SearchArea.Resolve(new FindColorStep { Scope = SearchScope.PrimaryMonitor }, Desktop, layout: null));
    }

    [Fact]
    public void A_missing_monitor_or_window_is_explained_in_plain_words()
    {
        var layout = FakeLayout.ThreeMonitors();
        layout.Windows.Add(new WindowInfo(1, "Notepad", default, IsMinimized: true));

        var monitor = Assert.Throws<SearchAreaUnavailableException>(() =>
            SearchArea.Resolve(new FindColorStep { Scope = SearchScope.Monitor, MonitorNumber = 4 }, Desktop, layout));
        Assert.Contains("Monitor 4 isn't connected (3 monitors are connected)", monitor.Message);

        var closed = Assert.Throws<SearchAreaUnavailableException>(() =>
            SearchArea.Resolve(new FindColorStep { Scope = SearchScope.Window, WindowTitle = "Roblox" }, Desktop, layout));
        Assert.Contains("The window \"Roblox\" isn't open", closed.Message);

        var minimized = Assert.Throws<SearchAreaUnavailableException>(() =>
            SearchArea.Resolve(new FindColorStep { Scope = SearchScope.Window, WindowTitle = "Notepad" }, Desktop, layout));
        Assert.Contains("minimized", minimized.Message);
    }

    [Fact]
    public void Window_titles_match_exactly_first_then_by_contained_text_and_skip_minimized_windows()
    {
        var windows = new[]
        {
            new WindowInfo(1, "Roblox Studio", new ScreenRect(0, 0, 10, 10)),
            new WindowInfo(2, "Roblox", new ScreenRect(0, 0, 20, 20)),
            new WindowInfo(3, "My Roblox notes - Notepad", default, IsMinimized: true),
            new WindowInfo(4, "Discord", new ScreenRect(0, 0, 30, 30)),
        };

        Assert.Equal(2, SearchArea.FindWindow(windows, "ROBLOX")!.Handle);       // exact match wins
        Assert.Equal(1, SearchArea.FindWindow(windows, "studio")!.Handle);       // contained text
        Assert.Equal(3, SearchArea.FindWindow(windows, "notes")!.Handle);        // only a minimized one matches
        Assert.Null(SearchArea.FindWindow(windows, "Chrome"));
        Assert.Null(SearchArea.FindWindow(windows, "  "));
    }

    [Fact]
    public void A_window_partly_off_screen_is_clipped_to_the_desktop()
    {
        var layout = FakeLayout.ThreeMonitors();
        layout.Windows.Add(new WindowInfo(1, "Game", new ScreenRect(150, -50, 400, 100)));

        Assert.Equal(new ScreenRect(150, 0, 50, 50), SearchArea.Resolve(new FindColorStep { Scope = SearchScope.Window, WindowTitle = "Game" }, Desktop, layout));
    }

    [Fact]
    public void Old_files_keep_their_meaning_and_scope_stays_in_sync_with_the_old_switch()
    {
        const string json = """
            { "schemaVersion": 4, "macro": { "name": "old", "steps": [
              { "type": "findColor", "searchFullScreen": true, "color": "#00C800" },
              { "type": "findColor", "searchFullScreen": false, "regionX": 5, "regionY": 6, "regionWidth": 50, "regionHeight": 40, "color": "#00C800" },
              { "type": "mouseMove", "x": 3, "y": 4 },
              { "type": "mouseClick", "x": 3, "y": 4 } ] } }
            """;
        Assert.True(MacroFileFormat.TryParse(json, out var macro, out var error), error);

        var whole = Assert.IsType<FindColorStep>(macro!.Steps[0]);
        Assert.Null(whole.Scope);
        Assert.Equal(SearchScope.AllMonitors, whole.EffectiveScope);
        Assert.Null(whole.MoveStyle);
        Assert.Equal(PlaybackEngine.FoundClickHoldMs, whole.ClickHoldMs);
        Assert.Equal(SearchScope.CustomArea, Assert.IsType<FindColorStep>(macro.Steps[1]).EffectiveScope);
        Assert.Null(Assert.IsType<MouseMoveStep>(macro.Steps[2]).MoveStyle);
        var click = Assert.IsType<MouseClickStep>(macro.Steps[3]);
        Assert.Null(click.MoveStyle);
        Assert.Equal(0, click.PostActionDelayMs);
        Assert.Equal(0, click.DoubleClickIntervalMs);

        // The old switch and the new choice always agree (older builds read "searchFullScreen").
        var step = new FindColorStep { Scope = SearchScope.Window };
        Assert.True(step.SearchFullScreen);
        step.Scope = SearchScope.CustomArea;
        Assert.False(step.SearchFullScreen);
        step.Scope = SearchScope.PrimaryMonitor;
        step.SearchFullScreen = false;
        Assert.Equal(SearchScope.CustomArea, step.EffectiveScope);
        var legacy = new FindColorStep { SearchFullScreen = false };
        Assert.Null(legacy.Scope);
        Assert.Equal(SearchScope.CustomArea, legacy.EffectiveScope);
    }

    [Fact]
    public void New_steps_from_the_toolbox_look_at_the_main_monitor_and_glide()
    {
        foreach (var kind in new[] { StepKind.FindText, StepKind.FindImage, StepKind.FindColor })
        {
            var step = Assert.IsAssignableFrom<ScreenSearchStep>(MacroFactory.CreateStep(kind));
            Assert.Equal(SearchScope.PrimaryMonitor, step.Scope);
            Assert.Equal(MouseMoveStyle.SmoothGlide, step.MoveStyle);
            Assert.Equal(MouseMotionDefaults.MoveDurationMs, step.MoveDurationMs);
        }

        var ifStep = Assert.IsType<IfStep>(MacroFactory.CreateStep(StepKind.If));
        Assert.Equal(SearchScope.PrimaryMonitor, ifStep.Condition.Search!.Scope);
        Assert.Equal(SearchScope.PrimaryMonitor, MacroFactory.NewCondition(ConditionKind.ImageVisible).Search!.Scope);
        // Steps made in code (and recordings) keep the old behaviour.
        Assert.Equal(SearchScope.AllMonitors, new FindImageStep().EffectiveScope);
    }

    [Fact]
    public async Task Main_monitor_search_ignores_what_is_on_another_monitor()
    {
        var capture = ScopeScreens.GreenOnRightMonitor();
        var input = new FakeInput();

        var result = await new PlaybackEngine(input, new FakeClock(), capture: capture, layout: FakeLayout.ThreeMonitors())
            .RunAsync(Flow.Macro(ScopeScreens.Green(SearchScope.PrimaryMonitor, FoundTextAction.Click)));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Empty(input.Calls); // not found on the main monitor → continue, nothing clicked
        Assert.Equal(new ScreenRect(0, 0, 100, 100), capture.Regions.Single());
    }

    [Fact]
    public async Task A_specific_monitor_is_searched_and_clicked()
    {
        var capture = ScopeScreens.GreenOnRightMonitor();
        var input = new FakeInput();
        var step = ScopeScreens.Green(SearchScope.Monitor, FoundTextAction.Click);
        step.MonitorNumber = 3;

        await new PlaybackEngine(input, new FakeClock(), capture: capture, layout: FakeLayout.ThreeMonitors()).RunAsync(Flow.Macro(step));

        Assert.Equal(new ScreenRect(100, 0, 100, 100), capture.Regions.Single());
        Assert.Equal(new[] { "move 155,45", "mouse Left down", "mouse Left up" }, input.Calls);
    }

    [Fact]
    public async Task An_app_window_is_searched_where_it_is_now_on_every_look()
    {
        var capture = ScopeScreens.GreenOnRightMonitor();
        var layout = FakeLayout.ThreeMonitors();
        layout.Windows.Add(new WindowInfo(1, "Discord", new ScreenRect(-100, 0, 100, 100)));
        layout.Windows.Add(new WindowInfo(2, "Roblox", new ScreenRect(120, 20, 60, 60)));
        var input = new FakeInput();
        var step = ScopeScreens.Green(SearchScope.Window, FoundTextAction.Click);
        step.WindowTitle = "Roblox";
        step.MoveCursorAwayAfterAction = true;

        await new PlaybackEngine(input, new FakeClock(), capture: capture, layout: layout).RunAsync(Flow.Macro(step));

        Assert.Equal(new ScreenRect(120, 20, 60, 60), capture.Regions.Single());
        Assert.Equal(1, layout.WindowLookups);
        // Clicked the match, then parked the cursor just outside the window's bottom-right corner.
        Assert.Equal(new[] { "move 155,45", "mouse Left down", "mouse Left up", "move 188,88" }, input.Calls);
    }

    [Fact]
    public async Task A_window_that_is_not_open_is_reported_when_the_time_is_up()
    {
        var step = ScopeScreens.Green(SearchScope.Window);
        step.WindowTitle = "Roblox";
        step.WhenNotFound = TextNotFoundBehavior.StopMacro;
        var capture = ScopeScreens.GreenOnRightMonitor();

        var result = await new PlaybackEngine(new FakeInput(), new FakeClock(), capture: capture, layout: FakeLayout.ThreeMonitors())
            .RunAsync(Flow.Macro(step));

        Assert.Equal(PlaybackOutcome.Failed, result.Outcome);
        Assert.Contains("The window \"Roblox\" isn't open", result.Error);
        Assert.Equal(0, capture.Captures); // nothing to capture
    }

    [Fact]
    public async Task Conditions_use_their_own_where_to_look()
    {
        Condition Seen(SearchScope scope)
        {
            var check = ScopeScreens.Green(scope);
            check.MonitorNumber = 3;
            return new Condition { Check = check };
        }

        async Task<bool> RanInside(SearchScope scope)
        {
            var input = new FakeInput();
            await new PlaybackEngine(input, new FakeClock(), capture: ScopeScreens.GreenOnRightMonitor(), layout: FakeLayout.ThreeMonitors())
                .RunAsync(Flow.Macro(Flow.If(Seen(scope)), Flow.Move(1), new EndIfStep()));
            return input.Calls.Contains("move 1,0");
        }

        Assert.True(await RanInside(SearchScope.Monitor));
        Assert.True(await RanInside(SearchScope.AllMonitors));
        Assert.False(await RanInside(SearchScope.PrimaryMonitor));
    }

    [Fact]
    public void Where_to_look_is_validated()
    {
        Assert.Contains(MacroValidator.ValidateStep(new FindColorStep { Scope = SearchScope.Window }),
            i => i.Severity == IssueSeverity.Error && i.Property == nameof(ScreenSearchStep.WindowTitle));
        Assert.Contains(MacroValidator.ValidateStep(new FindColorStep { Scope = SearchScope.Monitor, MonitorNumber = 0 }),
            i => i.Severity == IssueSeverity.Error && i.Property == nameof(ScreenSearchStep.MonitorNumber));
        Assert.Contains(MacroValidator.ValidateStep(new FindColorStep { Scope = (SearchScope)42 }),
            i => i.Severity == IssueSeverity.Error && i.Property == nameof(ScreenSearchStep.Scope));
        Assert.False(MacroValidator.ValidateStep(new FindColorStep { Scope = SearchScope.Window, WindowTitle = "Roblox" }).HasErrors());
        // A colour inside one app window isn't "the whole screen": no warning about that.
        Assert.DoesNotContain(MacroValidator.ValidateStep(new FindColorStep { Scope = SearchScope.Window, WindowTitle = "Roblox" }),
            i => i.Property == "Region");
        // The custom area rules only apply to the custom area.
        Assert.False(MacroValidator.ValidateStep(new FindColorStep { Scope = SearchScope.PrimaryMonitor, RegionWidth = 1 }).HasErrors());
        Assert.True(MacroValidator.ValidateStep(new FindColorStep { Scope = SearchScope.CustomArea, RegionWidth = 1 }).HasErrors());
    }

    [Fact]
    public void The_list_and_the_banner_say_where_it_looks()
    {
        Assert.StartsWith("Find color #00C800 (±10) on the main monitor", StepDescriber.Describe(ScopeScreens.Green(SearchScope.PrimaryMonitor)));
        var monitor = ScopeScreens.Green(SearchScope.Monitor);
        monitor.MonitorNumber = 2;
        Assert.StartsWith("Find color #00C800 (±10) on monitor 2", StepDescriber.Describe(monitor));
        var window = ScopeScreens.Green(SearchScope.Window);
        window.WindowTitle = "Roblox";
        Assert.StartsWith("Find color #00C800 (±10) in window \"Roblox\"", StepDescriber.Describe(window));
        Assert.StartsWith("Find color #00C800 (±10) on screen", StepDescriber.Describe(ScopeScreens.Green(SearchScope.AllMonitors)));
        Assert.Equal("If color #00C800 (±10) is on monitor 2", StepDescriber.Describe(Flow.If(new Condition { Check = monitor })));
    }
}

public class MouseMotionTests
{
    private static double DistanceFromLine(MotionPoint p, (int X, int Y) a, (int X, int Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        return Math.Abs(dy * (p.X - a.X) - dx * (p.Y - a.Y)) / Math.Sqrt(dx * dx + dy * dy);
    }

    [Fact]
    public void Instant_and_tiny_moves_are_one_point()
    {
        Assert.Equal(new[] { new MotionPoint(TimeSpan.Zero, 300, 150) },
            MouseMotion.Plan((0, 0), (300, 150), MouseMoveStyle.Instant, TimeSpan.FromMilliseconds(120)));
        Assert.Single(MouseMotion.Plan((0, 0), (1, 1), MouseMoveStyle.SmoothGlide, TimeSpan.FromMilliseconds(120)));
        Assert.Single(MouseMotion.Plan((0, 0), (300, 150), MouseMoveStyle.SmoothGlide, TimeSpan.Zero));
    }

    [Fact]
    public void Smooth_glide_is_a_straight_line_at_120_hz_with_a_gentle_start_and_stop()
    {
        var plan = MouseMotion.Plan((0, 0), (300, 150), MouseMoveStyle.SmoothGlide, TimeSpan.FromMilliseconds(120));

        Assert.Equal(15, plan.Count); // 120 ms / 8 ms
        Assert.Equal(new MotionPoint(TimeSpan.FromMilliseconds(120), 300, 150), plan[^1]);
        Assert.All(plan.Select((p, i) => (p, i)), x => Assert.Equal(TimeSpan.FromMilliseconds(8 * (x.i + 1)), x.p.At));
        Assert.All(plan, p => Assert.True(DistanceFromLine(p, (0, 0), (300, 150)) <= 1.0, $"({p.X}, {p.Y}) is off the line"));
        Assert.Equal(plan.Select(p => p.X).OrderBy(x => x), plan.Select(p => p.X)); // always forward
        var firstStep = plan[0].X;
        var middleStep = plan[8].X - plan[7].X;
        var lastStep = plan[^1].X - plan[^2].X;
        Assert.True(firstStep < middleStep && lastStep < middleStep, $"{firstStep} / {middleStep} / {lastStep}");
    }

    [Fact]
    public void Human_curve_bends_a_little_and_still_lands_exactly_on_the_target()
    {
        var plan = MouseMotion.Plan((0, 0), (300, 0), MouseMoveStyle.HumanCurve, TimeSpan.FromMilliseconds(200));

        Assert.Equal(25, plan.Count);
        Assert.Equal((300, 0), (plan[^1].X, plan[^1].Y));
        var bend = plan.Max(p => Math.Abs(p.Y));
        Assert.True(bend >= 5 && bend <= MouseMotion.MaxCurveBendPixels + 1, $"bend {bend}");
        Assert.True(plan.All(p => Math.Sign(p.Y) >= 0) || plan.All(p => Math.Sign(p.Y) <= 0)); // one smooth arc, no zig-zag
        Assert.Equal(plan.Select(p => p.X).OrderBy(x => x), plan.Select(p => p.X));
        // The same move always takes the same path (repeatable playback).
        Assert.Equal(plan, MouseMotion.Plan((0, 0), (300, 0), MouseMoveStyle.HumanCurve, TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task Move_mouse_glides_on_the_timeline()
    {
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };

        await new PlaybackEngine(input, clock).RunAsync(Flow.Macro(
            Flow.Move(0), new MouseMoveStep { X = 300, MoveStyle = MouseMoveStyle.SmoothGlide, MoveDurationMs = 120 }));

        Assert.Equal("move 0,0", input.Calls[0]);
        Assert.Equal("move 300,0", input.Calls[^1]);
        Assert.Equal(16, input.Calls.Count);
        Assert.Equal(120, input.Times[^1].TotalMilliseconds, precision: 3);
        Assert.Equal(8, input.Times[1].TotalMilliseconds, precision: 3);
    }

    [Fact]
    public async Task Speed_shortens_the_glide_and_instant_or_unknown_start_jumps()
    {
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };
        var macro = Flow.Macro(Flow.Move(0), new MouseMoveStep { X = 300, MoveStyle = MouseMoveStyle.SmoothGlide, MoveDurationMs = 120 });
        macro.Speed = 2;
        await new PlaybackEngine(input, clock).RunAsync(macro);
        Assert.Equal(60, input.Times[^1].TotalMilliseconds, precision: 3);

        var instant = new FakeInput();
        await new PlaybackEngine(instant, new FakeClock()).RunAsync(Flow.Macro(Flow.Move(0), new MouseMoveStep { X = 300, MoveStyle = MouseMoveStyle.Instant }));
        Assert.Equal(new[] { "move 0,0", "move 300,0" }, instant.Calls);

        var unknown = new FakeInput(); // doesn't know where the cursor is at the start
        await new PlaybackEngine(unknown, new FakeClock()).RunAsync(Flow.Macro(new MouseMoveStep { X = 300, MoveStyle = MouseMoveStyle.HumanCurve }));
        Assert.Equal(new[] { "move 300,0" }, unknown.Calls);
    }

    [Fact]
    public async Task A_styled_step_glides_by_itself_instead_of_using_the_delay_glide()
    {
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };
        var engine = new PlaybackEngine(input, clock) { SmoothMouseMoves = true };

        await engine.RunAsync(Flow.Macro(Flow.Move(0), new DelayStep { DurationMs = 300 },
            new MouseMoveStep { X = 300, MoveStyle = MouseMoveStyle.SmoothGlide, MoveDurationMs = 120 }));

        // Nothing moves during the Delay; the step's own 120 ms glide follows it.
        Assert.Equal(308, input.Times[1].TotalMilliseconds, precision: 3);
        Assert.Equal(420, input.Times[^1].TotalMilliseconds, precision: 3);
        Assert.Equal(16, input.Calls.Count);
    }

    [Fact]
    public async Task Find_step_glides_to_the_match_then_clicks_with_its_own_timing()
    {
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };
        var find = new FindColorStep
        {
            Color = "#00C800", Tolerance = 10, MinPixels = 20, TimeoutMs = 0, CheckIntervalMs = 50, Action = FoundTextAction.Click,
            MoveStyle = MouseMoveStyle.HumanCurve, MoveDurationMs = 120, ClickHoldMs = 50, PostActionDelayMs = 200,
        };

        await new PlaybackEngine(input, clock, capture: new FakeCapture(Flow.Screen(green: true))).RunAsync(Flow.Macro(Flow.Move(0), find));

        var down = input.Calls.IndexOf("mouse Left down");
        Assert.True(down > 3, string.Join(" | ", input.Calls)); // several glide moves first
        Assert.Equal("move 205,105", input.Calls[down - 1]);
        Assert.Equal(120, input.Times[down].TotalMilliseconds, precision: 3);
        Assert.Equal(50, (input.Times[down + 1] - input.Times[down]).TotalMilliseconds, precision: 3);
        Assert.Equal(120 + 50 + 200, clock.Elapsed.TotalMilliseconds, precision: 3); // the wait after the click
    }
}

public class ClickTimingTests
{
    [Fact]
    public async Task Mouse_click_holds_the_button_and_waits_after_the_click()
    {
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };

        await new PlaybackEngine(input, clock).RunAsync(Flow.Macro(
            new MouseClickStep { X = 10, Y = 10, HoldMs = 35, PostActionDelayMs = 120 }, Flow.Move(0)));

        Assert.Equal(new[] { "move 10,10", "mouse Left down", "mouse Left up", "move 0,0" }, input.Calls);
        Assert.Equal(35, (input.Times[2] - input.Times[1]).TotalMilliseconds, precision: 3);
        Assert.Equal(155, input.Times[3].TotalMilliseconds, precision: 3);
    }

    [Fact]
    public async Task Timed_double_click_uses_the_hold_and_the_gap()
    {
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };

        await new PlaybackEngine(input, clock).RunAsync(Flow.Macro(new MouseClickStep
        {
            UseCurrentPosition = true, Action = MouseClickAction.DoubleClick, HoldMs = 35, DoubleClickIntervalMs = 65,
        }));

        Assert.Equal(new[] { "mouse Left down", "mouse Left up", "mouse Left down", "mouse Left up" }, input.Calls);
        Assert.Equal(new[] { 0.0, 35, 100, 135 }, input.Times.Select(t => Math.Round(t.TotalMilliseconds, 3)));
    }

    [Fact]
    public async Task Old_double_clicks_stay_back_to_back()
    {
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };

        await new PlaybackEngine(input, clock).RunAsync(Flow.Macro(new MouseClickStep { UseCurrentPosition = true, Action = MouseClickAction.DoubleClick }));

        Assert.Equal(4, input.Calls.Count);
        Assert.All(input.Times, t => Assert.Equal(TimeSpan.Zero, t));
    }

    [Fact]
    public void New_mouse_clicks_get_the_game_ready_defaults()
    {
        var click = Assert.IsType<MouseClickStep>(MacroFactory.CreateStep(StepKind.MouseClick));

        Assert.Equal(MouseMoveStyle.SmoothGlide, click.MoveStyle);
        Assert.Equal(120, click.MoveDurationMs);
        Assert.Equal(35, click.ClickHoldMs);
        Assert.Equal(35, click.HoldMs); // one stored value
        Assert.Equal(65, click.DoubleClickIntervalMs);
        Assert.Equal(120, click.PostActionDelayMs);
        Assert.False(MacroValidator.ValidateStep(click).HasErrors());
        Assert.Equal(MouseMoveStyle.SmoothGlide, Assert.IsType<MouseMoveStep>(MacroFactory.CreateStep(StepKind.MouseMove)).MoveStyle);
    }

    [Fact]
    public async Task Find_step_double_click_uses_its_own_gap()
    {
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };
        var find = new FindColorStep
        {
            Color = "#00C800", Tolerance = 10, MinPixels = 20, TimeoutMs = 0, CheckIntervalMs = 50, Action = FoundTextAction.DoubleClick,
            ClickHoldMs = 20, DoubleClickIntervalMs = 100, PostActionDelayMs = 0,
        };

        await new PlaybackEngine(input, clock, capture: new FakeCapture(Flow.Screen(green: true))).RunAsync(Flow.Macro(find));

        Assert.Equal(new[] { "move 205,105", "mouse Left down", "mouse Left up", "mouse Left down", "mouse Left up" }, input.Calls);
        Assert.Equal(new[] { 0.0, 0, 20, 120, 140 }, input.Times.Select(t => Math.Round(t.TotalMilliseconds, 3)));
        Assert.Equal(140, clock.Elapsed.TotalMilliseconds, precision: 3); // no wait after the click
    }

    [Fact]
    public void Movement_and_timing_values_are_validated()
    {
        Assert.Contains(MacroValidator.ValidateStep(new MouseClickStep { PostActionDelayMs = -1 }), i => i.Property == nameof(MouseClickStep.PostActionDelayMs));
        Assert.Contains(MacroValidator.ValidateStep(new MouseClickStep { DoubleClickIntervalMs = 999_999 }), i => i.Property == nameof(MouseClickStep.DoubleClickIntervalMs));
        Assert.Contains(MacroValidator.ValidateStep(new MouseMoveStep { MoveStyle = MouseMoveStyle.SmoothGlide, MoveDurationMs = 6_000 }), i => i.Property == "MoveDurationMs");
        Assert.Contains(MacroValidator.ValidateStep(new MouseMoveStep { MoveStyle = (MouseMoveStyle)9 }), i => i.Property == "MoveStyle");
        Assert.Contains(MacroValidator.ValidateStep(new FindColorStep { ClickHoldMs = -1 }), i => i.Property == nameof(ScreenSearchStep.ClickHoldMs));
        Assert.Contains(MacroValidator.ValidateStep(new FindColorStep { PostActionDelayMs = 70_000 }), i => i.Property == nameof(ScreenSearchStep.PostActionDelayMs));
        // A long glide time is fine while the style is Instant (it isn't used).
        Assert.DoesNotContain(MacroValidator.ValidateStep(new MouseMoveStep { MoveStyle = MouseMoveStyle.Instant, MoveDurationMs = 6_000 }), i => i.Property == "MoveDurationMs");
    }

    [Fact]
    public void New_options_need_format_10_and_round_trip()
    {
        Assert.True(MacroDocument.CurrentSchemaVersion >= 10);
        Assert.Equal(4, MacroDocument.RequiredSchemaVersion(Flow.Macro(new FindColorStep())));
        Assert.Equal(4, MacroDocument.RequiredSchemaVersion(Flow.Macro(new FindColorStep { Scope = SearchScope.AllMonitors })));
        Assert.Equal(4, MacroDocument.RequiredSchemaVersion(Flow.Macro(new FindColorStep { Scope = SearchScope.CustomArea })));
        Assert.Equal(10, MacroDocument.RequiredSchemaVersion(Flow.Macro(new FindColorStep { Scope = SearchScope.PrimaryMonitor })));
        Assert.Equal(10, MacroDocument.RequiredSchemaVersion(Flow.Macro(new FindColorStep { ClickHoldMs = 50 })));
        Assert.Equal(10, MacroDocument.RequiredSchemaVersion(Flow.Macro(new FindColorStep { MoveStyle = MouseMoveStyle.SmoothGlide })));
        Assert.Equal(1, MacroDocument.RequiredSchemaVersion(Flow.Macro(new MouseMoveStep { MoveStyle = MouseMoveStyle.Instant })));
        Assert.Equal(10, MacroDocument.RequiredSchemaVersion(Flow.Macro(new MouseMoveStep { MoveStyle = MouseMoveStyle.HumanCurve })));
        Assert.Equal(10, MacroDocument.RequiredSchemaVersion(Flow.Macro(new MouseClickStep { PostActionDelayMs = 120 })));
        var windowCondition = Flow.If(new Condition { Check = new FindColorStep { Scope = SearchScope.Window, WindowTitle = "Roblox" } });
        Assert.Equal(10, MacroDocument.RequiredSchemaVersion(Flow.Macro(windowCondition, new EndIfStep())));

        var json = MacroFileFormat.Serialize(Flow.Macro(
            new FindColorStep { Scope = SearchScope.Window, WindowTitle = "Roblox", MoveStyle = MouseMoveStyle.HumanCurve, ClickHoldMs = 50 },
            new MouseClickStep { MoveStyle = MouseMoveStyle.SmoothGlide, DoubleClickIntervalMs = 65, PostActionDelayMs = 120 }));
        Assert.Contains("\"schemaVersion\": 10", json);
        Assert.Contains("\"scope\": \"Window\"", json);
        Assert.True(MacroFileFormat.TryParse(json, out var loaded, out var error), error);
        var find = Assert.IsType<FindColorStep>(loaded!.Steps[0]);
        Assert.Equal((SearchScope.Window, "Roblox", MouseMoveStyle.HumanCurve, 50), (find.EffectiveScope, find.WindowTitle, find.MoveStyle!.Value, find.ClickHoldMs));
        Assert.True(find.SearchFullScreen);
        var click = Assert.IsType<MouseClickStep>(loaded.Steps[1]);
        Assert.Equal((MouseMoveStyle.SmoothGlide, 65, 120), (click.MoveStyle!.Value, click.DoubleClickIntervalMs, click.PostActionDelayMs));
    }
}

public class QuickPatternTests
{
    [Theory]
    [InlineData(QuickPattern.AutoClickButtonLoop)]
    [InlineData(QuickPattern.FishingCastShakeReel)]
    [InlineData(QuickPattern.ShopClickAndBuy)]
    public void Patterns_are_complete_blocks_that_only_need_their_images_captured(QuickPattern pattern)
    {
        var steps = QuickPatterns.Build(pattern);

        Assert.True(BlockStructure.Analyze(steps).IsValid);
        var errors = MacroValidator.Validate(Flow.Macro(steps.ToArray())).Where(i => i.Severity == IssueSeverity.Error).ToList();
        Assert.All(errors, e => Assert.Contains("capture the image", e.Message, StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual(QuickPatterns.Build(pattern)[0].Id, steps[0].Id); // fresh steps every time
        Assert.False(string.IsNullOrWhiteSpace(QuickPatterns.Info(pattern).Description));
    }

    [Fact]
    public void Auto_click_loop_repeats_while_the_button_is_there_and_clicks_it()
    {
        var steps = QuickPatterns.Build(QuickPattern.AutoClickButtonLoop);

        var repeat = Assert.IsType<RepeatStep>(steps[0]);
        Assert.Equal(RepeatMode.While, repeat.Mode);
        Assert.Equal(ConditionKind.ImageVisible, repeat.Condition!.Kind);
        Assert.False(repeat.Condition.Negate);
        Assert.Equal(800, repeat.EffectiveKeepCheckingForMs);
        Assert.True(repeat.MaxDurationMs > 0);
        var find = Assert.IsType<FindImageStep>(steps[1]);
        Assert.Equal(FoundTextAction.Click, find.Action);
        Assert.Equal(MouseMoveStyle.SmoothGlide, find.MoveStyle);
        Assert.Equal(120, find.PostActionDelayMs);
        Assert.True(find.MoveCursorAwayAfterAction);
        Assert.Equal(SearchScope.PrimaryMonitor, find.Scope);
        Assert.IsType<EndRepeatStep>(steps[2]);
    }

    [Fact]
    public void Fishing_template_casts_shakes_and_reels()
    {
        var steps = QuickPatterns.Build(QuickPattern.FishingCastShakeReel);

        var cast = Assert.IsType<MouseClickStep>(steps[0]);
        Assert.Equal((MouseClickAction.Hold, 800), (cast.Action, cast.HoldMs));
        Assert.IsType<DelayStep>(steps[1]);
        Assert.IsType<RepeatStep>(steps[2]);
        Assert.IsType<FindImageStep>(steps[3]);
        Assert.IsType<EndRepeatStep>(steps[4]);
        var reel = Assert.IsType<FeedbackLoopStep>(steps[5]);
        Assert.Equal(FeedbackPreset.FollowTargetBar, reel.Preset);
        Assert.Equal(BarOrientation.Horizontal, reel.Orientation);
        Assert.Equal(FeedbackAction.Hold, reel.WhenAfterZone); // fish right of your bar → hold
    }

    [Fact]
    public void Shop_template_clicks_scrolls_and_buys()
    {
        var steps = QuickPatterns.Build(QuickPattern.ShopClickAndBuy);

        Assert.Equal(FoundTextAction.Click, Assert.IsType<FindImageStep>(steps[0]).Action);
        Assert.Equal(-360, Assert.IsType<MouseScrollStep>(steps[1]).Delta);
        Assert.IsType<MouseClickStep>(steps[2]);
    }
}

public class PlainEnglishTests
{
    [Fact]
    public void Status_pill_says_what_the_running_step_does()
    {
        var image = new FindImageStep { Comment = "SHAKE", Scope = SearchScope.PrimaryMonitor };
        Assert.Equal("Finding image \"SHAKE\" on the main monitor", StepDescriber.Activity(image));
        Assert.Equal("Waiting 500 ms", StepDescriber.Activity(new DelayStep { DurationMs = 500 }));
        Assert.Equal("Clicking at (10, 20)", StepDescriber.Activity(new MouseClickStep { X = 10, Y = 20 }));
        Assert.Equal("Finding text \"OK\" on screen", StepDescriber.Activity(new FindTextStep { Text = "OK" }));
    }

    [Fact]
    public void Find_steps_explain_themselves_in_one_paragraph()
    {
        var step = (FindImageStep)MacroFactory.CreateStep(StepKind.FindImage);
        step.Action = FoundTextAction.Click;

        Assert.Equal("Searches Main Monitor every 250 ms for up to 10 s. When found: smoothly glides to its center (120 ms) and left-clicks (35 ms hold), then waits 120 ms. If it's not found in time: the macro stops.",
            StepDescriber.Explain(step));

        step.Action = FoundTextAction.None;
        step.WhenNotFound = TextNotFoundBehavior.SkipNextStep;
        step.TimeoutMs = 0;
        Assert.Equal("Searches Main Monitor once. When found: just continues with the next step. If the time runs out: skips the next step.",
            StepDescriber.Explain(step));
    }

    [Fact]
    public void Feedback_loop_rules_are_previewed_in_plain_sentences()
    {
        var reel = (FeedbackLoopStep)MacroFactory.CreateStep(StepKind.FeedbackLoop);
        reel.Orientation = BarOrientation.Horizontal;
        FeedbackPresets.Apply(reel, FeedbackPreset.FollowTargetBar);
        var lines = StepDescriber.ExplainFeedbackRules(reel);
        Assert.Contains("While the target is to the RIGHT of your bar: holds the left mouse button down.", lines);
        Assert.Contains("While the target is to the LEFT of your bar: releases the left mouse button.", lines);
        Assert.Contains(lines, l => l.StartsWith("While the target is inside your bar: pulses", StringComparison.Ordinal));

        reel.Orientation = BarOrientation.Vertical;
        FeedbackPresets.ApplyFollowRules(reel);
        lines = StepDescriber.ExplainFeedbackRules(reel);
        Assert.Contains("While the target is ABOVE your bar: holds the left mouse button down.", lines);
        Assert.Contains("Holding pushes your bar up; releasing lets it drift back.", lines);
    }
}
