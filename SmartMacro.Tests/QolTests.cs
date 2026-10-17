using SmartMacro.Core.Models;
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
