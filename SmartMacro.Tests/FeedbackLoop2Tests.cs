using SmartMacro.Core.Models;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Vision;

namespace SmartMacro.Tests;

/// <summary>
/// Feedback Loop 2.0 "Smart Predictive (PD + Brake)": the controller (lead prediction, reversal brake, hover pulse),
/// the one-click game presets, validation and the file format.
/// </summary>
public class FeedbackLoop2Tests
{
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(8); // 120 Hz

    /// <summary>A follow-the-target observation: the target (marker) at <paramref name="target"/> %, your bar centred on <paramref name="bar"/> %.</summary>
    private static FeedbackObservation Follow(double target, double bar, double halfWidth = 5) =>
        new(MarkerStatus.Found, target, true, bar - halfWidth, bar + halfWidth);

    private static PredictiveFeedbackSettings Settings(bool pwm = false, int minChangeMs = 8, double lead = 0.18, double deadzonePx = 6) =>
        new(BarLengthPx: 300, FollowMode: true, HoldDirection: +1, LeadSeconds: lead, DeadzonePx: deadzonePx, UsePwm: pwm,
            PulsePeriod: TimeSpan.FromMilliseconds(80), MinInputChange: TimeSpan.FromMilliseconds(minChangeMs),
            LostTimeout: TimeSpan.FromSeconds(1), WhenLost: FeedbackLostAction.ReleaseAndWait, DeadBandPercent: 0);

    private static FeedbackLoopStep FischStep()
    {
        var step = new FeedbackLoopStep { RegionX = 100, RegionY = 500, RegionWidth = 301, RegionHeight = 24, MarkerColor = "#FF8800", ZoneColor = "#FFFFFF" };
        FeedbackPresets.ApplyGamePreset(step, FeedbackGamePreset.FischMomentumBar);
        return step;
    }

    // ---------------- PD controller ----------------

    [Fact]
    public void Smart_PD_releases_before_a_fast_bar_crosses_the_target_while_classic_still_holds()
    {
        var smartStep = FischStep();
        var classicStep = (FeedbackLoopStep)MacroJson.CloneStep(smartStep);
        classicStep.ControlMode = FeedbackControlMode.ClassicSimpleHold;
        var smart = Assert.IsType<PredictiveFeedbackController>(FeedbackControllers.Create(smartStep));
        var classic = Assert.IsType<FeedbackController>(FeedbackControllers.Create(classicStep));

        // The target sits at 60 %; your bar races toward it at 100 % / s (300 px/s on a 301 px bar), one frame every 8 ms.
        const double target = 60;
        double? releasedAt = null;
        for (var i = 0; i < 60 && releasedAt is null; i++)
        {
            var bar = 20 + 0.8 * i;
            var now = Frame * i;
            var decision = smart.Update(Follow(target, bar), now);
            classic.Update(Follow(target, bar), now);

            if (i == 0) Assert.Equal(FeedbackCommand.Press, decision.Command); // far behind: push
            if (decision.Command == FeedbackCommand.Release)
            {
                releasedAt = bar;
                Assert.True(classic.IsHeld, "classic still holds: the target is still ahead of the bar");
                Assert.Equal(FeedbackRegion.After, classic.Region);
            }
        }

        Assert.NotNull(releasedAt);
        Assert.True(releasedAt + 5 < target - 5, $"released with the bar's edge at {releasedAt + 5} %, well before it reaches the target");
        Assert.True(smart.PlayerVelocity > 250, $"measured bar speed {smart.PlayerVelocity:0} px/s");
        Assert.True(smart.TargetPosition - smart.PlayerPosition > 30);
        Assert.True(smart.PredictedError <= 6, $"predicted gap {smart.PredictedError:0.0} px"); // the bar is about to arrive: on target or past it
    }

    [Fact]
    public void A_target_reversal_brakes_at_once_even_within_the_minimum_change_time()
    {
        var controller = new PredictiveFeedbackController(Settings(minChangeMs: 500));
        Assert.Equal(FeedbackCommand.Press, controller.Update(Follow(60, 50), TimeSpan.Zero).Command);

        // The target moves away (up) for a few frames: keep holding.
        Assert.Equal(FeedbackCommand.None, controller.Update(Follow(60.8, 50), Frame).Command);
        Assert.Equal(FeedbackCommand.None, controller.Update(Follow(61.6, 50), Frame * 2).Command);
        Assert.Equal(FeedbackCommand.None, controller.Update(Follow(62.4, 50), Frame * 3).Command);

        // It darts back: the input flips right away (32 ms after the press, the minimum gap is 500 ms).
        var brake = controller.Update(Follow(60.8, 50), Frame * 4);
        Assert.Equal(FeedbackCommand.Release, brake.Command);
        Assert.True(brake.Braked);
        Assert.False(brake.Deferred);
        Assert.False(controller.IsHeld);
        Assert.Equal(1, controller.Brakes);
        Assert.True(controller.TargetVelocity < 0);
    }

    [Fact]
    public void Ordinary_changes_still_wait_for_the_minimum_change_time()
    {
        var controller = new PredictiveFeedbackController(Settings(minChangeMs: 500));
        Assert.Equal(FeedbackCommand.Press, controller.Update(Follow(80, 50), TimeSpan.Zero).Command);

        // The bar jumps past the target without the target turning around: not a brake, so it's deferred.
        var decision = controller.Update(Follow(80, 95), Frame);
        Assert.Equal(FeedbackCommand.None, decision.Command);
        Assert.True(decision.Deferred);
        Assert.False(decision.Braked);
        Assert.True(controller.IsHeld);
    }

    [Fact]
    public void PWM_hover_pulses_inside_the_dead_zone()
    {
        var controller = new PredictiveFeedbackController(Settings(pwm: true));
        int presses = 0, releases = 0;
        for (var i = 0; i < 50; i++) // 400 ms on target, nothing moving
        {
            var decision = controller.Update(Follow(50, 50), Frame * i);
            if (decision.Command == FeedbackCommand.Press) presses++;
            if (decision.Command == FeedbackCommand.Release) releases++;
            Assert.NotNull(controller.Duty);
            Assert.Equal(FeedbackRegion.Inside, decision.Region);
        }

        Assert.True(presses >= 4, $"{presses} presses");
        Assert.True(releases >= 4, $"{releases} releases");
        Assert.Equal(PredictiveFeedbackController.InitialHoverDuty, controller.HoverDuty, precision: 6);
        Assert.Equal(0, controller.Brakes);
    }

    [Fact]
    public void Without_PWM_the_dead_zone_keeps_the_current_state()
    {
        var controller = new PredictiveFeedbackController(Settings(pwm: false));
        for (var i = 0; i < 20; i++)
            Assert.Equal(FeedbackCommand.None, controller.Update(Follow(50, 50), Frame * i).Command);
        Assert.False(controller.IsHeld);
        Assert.Null(controller.Duty);
    }

    [Fact]
    public void A_short_dropout_coasts_and_a_long_loss_releases_and_resets_the_speeds()
    {
        var controller = new PredictiveFeedbackController(Settings());
        controller.Update(Follow(90, 20), TimeSpan.Zero);
        controller.Update(Follow(90, 22), Frame);
        Assert.True(controller.IsHeld);
        var speed = controller.PlayerVelocity;
        Assert.NotEqual(0, speed);

        var lost = new FeedbackObservation(MarkerStatus.Lost, null, false, 0, 0);
        Assert.Equal(FeedbackCommand.None, controller.Update(lost, Frame * 2).Command); // a short gap changes nothing
        Assert.True(controller.IsCoasting);
        Assert.Equal(speed * PredictiveFeedbackSettings.CoastVelocityFactor, controller.PlayerVelocity, precision: 6);

        var released = controller.Update(lost, TimeSpan.FromSeconds(2));
        Assert.Equal(0, controller.PlayerVelocity);
        Assert.Equal(FeedbackCommand.Release, released.Command);
        Assert.True(released.Waiting);
    }

    [Fact]
    public void The_hold_direction_comes_from_the_rules()
    {
        var step = new FeedbackLoopStep();
        FeedbackPresets.Apply(step, FeedbackPreset.FollowTargetBar); // horizontal: hold when the target is right of your bar
        Assert.Equal(1, PredictiveFeedbackSettings.HoldDirectionOf(step));

        step.Orientation = BarOrientation.Vertical;
        FeedbackPresets.ApplyFollowRules(step); // hold when the target is above your bar
        Assert.Equal(-1, PredictiveFeedbackSettings.HoldDirectionOf(step));

        var fixedZone = new FeedbackLoopStep(); // hold before the zone, release after: holding moves the marker up
        Assert.Equal(1, PredictiveFeedbackSettings.HoldDirectionOf(fixedZone));
        fixedZone.WhenBeforeZone = FeedbackAction.Release;
        fixedZone.WhenAfterZone = FeedbackAction.Hold;
        Assert.Equal(-1, PredictiveFeedbackSettings.HoldDirectionOf(fixedZone));
        fixedZone.WhenBeforeZone = FeedbackAction.Hold;
        Assert.Equal(0, PredictiveFeedbackSettings.HoldDirectionOf(fixedZone));

        Assert.Equal(300, PredictiveFeedbackSettings.From(FischStep()).BarLengthPx);
    }

    [Fact]
    public void The_factory_picks_the_controller_for_the_control_mode()
    {
        Assert.IsType<FeedbackController>(FeedbackControllers.Create(new FeedbackLoopStep()));
        Assert.IsType<PredictiveFeedbackController>(FeedbackControllers.Create(new FeedbackLoopStep { ControlMode = FeedbackControlMode.SmartPredictive }));

        var created = (FeedbackLoopStep)MacroFactory.CreateStep(StepKind.FeedbackLoop);
        Assert.Equal(FeedbackControlMode.SmartPredictive, created.ControlMode);
        Assert.Equal(120, created.MaxCapturesPerSecond);
    }

    // ---------------- Presets ----------------

    [Theory]
    [InlineData(FeedbackGamePreset.FischMomentumBar, "🎣 Fisch / Momentum Bar (Smart PD + Hover Pulse)", 0.18, 6, true, 80, 120, 8)]
    [InlineData(FeedbackGamePreset.FastReflexTracker, "⚡ Fast Reflex Tracker (Instant Snap)", 0.08, 3, false, 80, 120, 8)]
    [InlineData(FeedbackGamePreset.SmoothBalanceBar, "⚖️ Smooth Balance Bar (Gentle Pulse)", 0.25, 10, true, 120, 60, 16)]
    public void Game_presets_set_smart_values_and_keep_the_area_and_colours(FeedbackGamePreset preset, string label, double lead, int deadzone,
        bool pwm, int pulseMs, int capturesPerSecond, int minChangeMs)
    {
        var step = new FeedbackLoopStep { RegionX = 100, RegionY = 500, RegionWidth = 301, RegionHeight = 24, MarkerColor = "#FF8800", ZoneColor = "#FFFFFF" };
        FeedbackPresets.ApplyGamePreset(step, preset);

        Assert.Equal(label, FeedbackPresets.Label(preset));
        Assert.Equal(FeedbackControlMode.SmartPredictive, step.ControlMode);
        Assert.Equal(FeedbackPreset.FollowTargetBar, step.Preset);
        Assert.Equal(FeedbackZoneMode.DetectedByColor, step.ZoneMode);
        Assert.Equal(lead, step.LeadPredictionFactor, precision: 6);
        Assert.Equal((deadzone, pwm, pulseMs, capturesPerSecond, minChangeMs),
            (step.DeadzonePx, step.UsePwmMicroPulse, step.PulsePeriodMs, step.MaxCapturesPerSecond, step.MinInputChangeMs));
        Assert.Equal((100, 500, 301, 24, "#FF8800", "#FFFFFF"),
            (step.RegionX, step.RegionY, step.RegionWidth, step.RegionHeight, step.MarkerColor, step.ZoneColor));
        Assert.False(step.FinishWhenInZone);
        Assert.False(MacroValidator.ValidateStep(step).HasErrors());
        Assert.DoesNotContain(MacroValidator.ValidateStep(step), i => i.Severity == IssueSeverity.Warning);
    }

    [Fact]
    public void Timing_checks_stay_classic()
    {
        var step = FischStep();
        FeedbackPresets.Apply(step, FeedbackPreset.ClickWhenInZone);
        Assert.Equal(FeedbackControlMode.ClassicSimpleHold, step.ControlMode);
        Assert.False(MacroValidator.ValidateStep(step).HasErrors());
    }

    // ---------------- Validation ----------------

    private static ValidationIssue? ErrorOn(FeedbackLoopStep step, string property) =>
        MacroValidator.ValidateStep(step).FirstOrDefault(i => i.Severity == IssueSeverity.Error && i.Property == property);

    [Fact]
    public void Smart_settings_are_validated()
    {
        Assert.Empty(MacroValidator.ValidateStep(FischStep()));

        var both = FischStep();
        both.WhenBeforeZone = FeedbackAction.Hold;
        both.WhenAfterZone = FeedbackAction.Hold;
        Assert.NotNull(ErrorOn(both, "Rules"));

        var finish = FischStep();
        finish.FinishWhenInZone = true;
        finish.WhenInsideZone = FeedbackAction.Tap;
        Assert.NotNull(ErrorOn(finish, nameof(FeedbackLoopStep.FinishWhenInZone)));

        var lead = FischStep();
        lead.LeadPredictionFactor = 2;
        Assert.NotNull(ErrorOn(lead, nameof(FeedbackLoopStep.LeadPredictionFactor)));
        lead.LeadPredictionFactor = double.NaN;
        Assert.NotNull(ErrorOn(lead, nameof(FeedbackLoopStep.LeadPredictionFactor)));

        var deadzone = FischStep();
        deadzone.DeadzonePx = -1;
        Assert.NotNull(ErrorOn(deadzone, nameof(FeedbackLoopStep.DeadzonePx)));

        var pulse = FischStep();
        pulse.PulsePeriodMs = 5;
        Assert.NotNull(ErrorOn(pulse, nameof(FeedbackLoopStep.PulsePeriodMs)));
        pulse.UsePwmMicroPulse = false; // the pulse cycle isn't used then
        Assert.Null(ErrorOn(pulse, nameof(FeedbackLoopStep.PulsePeriodMs)));
    }

    [Fact]
    public void Smart_mode_runs_at_120_Hz_without_warnings_but_classic_still_warns()
    {
        var smart = FischStep();
        Assert.DoesNotContain(MacroValidator.ValidateStep(smart), i => i.Property is nameof(FeedbackLoopStep.MaxCapturesPerSecond) or nameof(FeedbackLoopStep.MinInputChangeMs));

        smart.ControlMode = FeedbackControlMode.ClassicSimpleHold;
        var issues = MacroValidator.ValidateStep(smart);
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.Property == nameof(FeedbackLoopStep.MaxCapturesPerSecond));
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.Property == nameof(FeedbackLoopStep.MinInputChangeMs));
    }

    // ---------------- File format ----------------

    [Fact]
    public void Smart_settings_round_trip_through_a_file()
    {
        var step = FischStep();
        step.LeadPredictionFactor = 0.22;
        step.DeadzonePx = 9;
        step.UsePwmMicroPulse = false;

        var json = MacroFileFormat.Serialize(Flow.Macro(step));
        Assert.Contains("\"controlMode\"", json);
        Assert.Contains("\"schemaVersion\": 13", json); // Smart loops need format 13 (14 only adds relative mouse moves)
        Assert.True(MacroFileFormat.TryParse(json, out var loaded, out var error), error);

        var loop = Assert.IsType<FeedbackLoopStep>(Assert.Single(loaded!.Steps));
        Assert.Equal(FeedbackControlMode.SmartPredictive, loop.ControlMode);
        Assert.Equal(0.22, loop.LeadPredictionFactor, precision: 6);
        Assert.Equal((9, false), (loop.DeadzonePx, loop.UsePwmMicroPulse));
    }

    [Fact]
    public void Classic_loops_keep_their_old_format_and_old_files_load_as_classic()
    {
        var classic = new FeedbackLoopStep();
        Assert.DoesNotContain("controlMode", MacroJson.Serialize<MacroStep>(classic), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(6, MacroDocument.RequiredSchemaVersion(Flow.Macro(classic)));
        Assert.Equal(13, MacroDocument.RequiredSchemaVersion(Flow.Macro(FischStep())));

        const string json = """{ "schemaVersion": 8, "macro": { "name": "old", "steps": [ { "type": "feedbackLoop", "preset": "FollowTargetBar", "zoneMode": "DetectedByColor" } ] } }""";
        Assert.True(MacroFileFormat.TryParse(json, out var loaded, out var error), error);
        var loop = Assert.IsType<FeedbackLoopStep>(Assert.Single(loaded!.Steps));
        Assert.Equal(FeedbackControlMode.ClassicSimpleHold, loop.ControlMode);
        Assert.Equal(FeedbackLoopStep.DefaultLeadPredictionFactor, loop.LeadPredictionFactor);
        Assert.Equal(FeedbackLoopStep.DefaultDeadzonePx, loop.DeadzonePx);
        Assert.True(loop.UsePwmMicroPulse);
    }

    // ---------------- Plain-English preview ----------------

    [Fact]
    public void The_rule_preview_explains_smart_mode()
    {
        var lines = StepDescriber.ExplainFeedbackRules(FischStep());
        Assert.Contains(lines, l => l.StartsWith("Smart Predictive: decides on where both bars will be 0.18 s from now", StringComparison.Ordinal));
        Assert.Contains("Within 6 px of the target: taps the left mouse button in quick 80 ms pulses so your bar hovers.", lines);

        var classic = FischStep();
        classic.ControlMode = FeedbackControlMode.ClassicSimpleHold;
        Assert.DoesNotContain(StepDescriber.ExplainFeedbackRules(classic), l => l.Contains("Smart Predictive", StringComparison.Ordinal));
    }
}
