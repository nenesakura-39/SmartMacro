namespace SmartMacro.Core.Models;

/// <summary>One-click game presets for Smart Predictive feedback loops (Feedback Loop 2.0).</summary>
public enum FeedbackGamePreset
{
    /// <summary>"🎣 Fisch / Momentum Bar (Smart PD + Hover Pulse)": lead 0.18 s, dead zone 6 px, hover pulse every 80 ms, 120 captures/s.</summary>
    FischMomentumBar,

    /// <summary>"⚡ Fast Reflex Tracker (Instant Snap)": short lead 0.08 s, dead zone 3 px, no pulsing, 120 captures/s.</summary>
    FastReflexTracker,

    /// <summary>"⚖️ Smooth Balance Bar (Gentle Pulse)": long lead 0.25 s, dead zone 10 px, gentle 120 ms pulse, 60 captures/s.</summary>
    SmoothBalanceBar,
}

/// <summary>
/// Ready-made settings for the two common kinds of minigame, so a Feedback loop can be set up with three picks
/// (area, marker colour, zone colour) instead of every raw parameter. Applying a preset only changes the settings
/// that define how the loop behaves; the area and colours the user picked are kept.
/// </summary>
public static class FeedbackPresets
{
    /// <summary>Default max runtime for "follow the target" (a reeling minigame rarely lasts longer).</summary>
    public const int FollowMaxRuntimeMs = 60_000;

    /// <summary>Default max runtime for "click when it enters the zone".</summary>
    public const int ZoneClickMaxRuntimeMs = 10_000;

    /// <summary>"Follow the target": the bar counts as gone after this long without a clear reading.</summary>
    public const int FollowLostTimeoutMs = 1_500;

    /// <summary>Smart Predictive loops look this many times per second (the sensor captures only the bar area).</summary>
    public const int SmartCapturesPerSecond = 120;

    /// <summary>Smart Predictive loops: minimum time between input changes (one 120 Hz frame).</summary>
    public const int SmartMinInputChangeMs = 8;

    /// <summary>Smart Predictive hover pulse period (Fisch preset).</summary>
    public const int SmartPulsePeriodMs = 80;

    /// <summary>The label of a game preset, as shown on its button.</summary>
    public static string Label(FeedbackGamePreset preset) => preset switch
    {
        FeedbackGamePreset.FischMomentumBar => "🎣 Fisch / Momentum Bar (Smart PD + Hover Pulse)",
        FeedbackGamePreset.FastReflexTracker => "⚡ Fast Reflex Tracker (Instant Snap)",
        _ => "⚖️ Smooth Balance Bar (Gentle Pulse)",
    };

    /// <summary>
    /// One-click game preset: the "follow the target" setup (zone = your bar found by its colour, marker = the target)
    /// with Smart Predictive control tuned for that kind of bar. The area, colours and direction the user picked are kept.
    /// </summary>
    public static void ApplyGamePreset(FeedbackLoopStep step, FeedbackGamePreset preset)
    {
        ArgumentNullException.ThrowIfNull(step);
        Apply(step, FeedbackPreset.FollowTargetBar); // Smart Predictive, Fisch values
        switch (preset)
        {
            case FeedbackGamePreset.FastReflexTracker:
                SetSmart(step, lead: 0.08, deadzonePx: 3, pwm: false, pulseMs: SmartPulsePeriodMs, capturesPerSecond: SmartCapturesPerSecond, minChangeMs: SmartMinInputChangeMs);
                break;
            case FeedbackGamePreset.SmoothBalanceBar:
                SetSmart(step, lead: 0.25, deadzonePx: 10, pwm: true, pulseMs: 120, capturesPerSecond: 60, minChangeMs: 16);
                break;
        }
    }

    private static void SetSmart(FeedbackLoopStep step, double lead, int deadzonePx, bool pwm, int pulseMs, int capturesPerSecond, int minChangeMs)
    {
        step.ControlMode = FeedbackControlMode.SmartPredictive;
        step.LeadPredictionFactor = lead;
        step.DeadzonePx = deadzonePx;
        step.UsePwmMicroPulse = pwm;
        step.PulsePeriodMs = pulseMs;
        step.MaxCapturesPerSecond = capturesPerSecond;
        step.MinInputChangeMs = minChangeMs;
    }

    /// <summary>Applies a preset. Custom changes nothing (it just shows every setting).</summary>
    public static void Apply(FeedbackLoopStep step, FeedbackPreset preset)
    {
        ArgumentNullException.ThrowIfNull(step);
        step.Preset = preset;
        switch (preset)
        {
            case FeedbackPreset.FollowTargetBar:
                step.ZoneMode = FeedbackZoneMode.DetectedByColor;
                ApplyFollowRules(step);
                step.DeadBandPercent = 2;
                // Feedback Loop 2.0: follow-the-target bars have momentum, so they use Smart Predictive (PD + brake + hover
                // pulse) with the Fisch values. (The rules above still say which way holding pushes the bar.)
                SetSmart(step, FeedbackLoopStep.DefaultLeadPredictionFactor, FeedbackLoopStep.DefaultDeadzonePx, pwm: true,
                    pulseMs: SmartPulsePeriodMs, capturesPerSecond: SmartCapturesPerSecond, minChangeMs: SmartMinInputChangeMs);
                step.FinishWhenInZone = false;
                step.InputKind = FeedbackInputKind.MouseButton;
                step.MouseButton = MacroMouseButton.Left;
                step.MaxRuntimeMs = FollowMaxRuntimeMs;
                step.WhenTimeUp = LoopLimitBehavior.ExitLoop;
                step.LostTimeoutMs = FollowLostTimeoutMs;
                step.WhenTargetLost = FeedbackLostAction.ExitLoop; // the minigame closed: carry on with the macro
                break;

            case FeedbackPreset.ClickWhenInZone:
                // Applying it again keeps the "release the held input" variant if that was chosen.
                SetZoneEntryAction(step, releaseHeldInput: step.FinishWhenInZone && step.WhenInsideZone == FeedbackAction.Release);
                step.DeadBandPercent = 0; // react the moment it touches the zone
                step.FinishWhenInZone = true;
                step.ControlMode = FeedbackControlMode.ClassicSimpleHold; // a timing click, not tracking
                step.MaxRuntimeMs = ZoneClickMaxRuntimeMs;
                step.WhenTimeUp = LoopLimitBehavior.ExitLoop;
                step.WhenTargetLost = FeedbackLostAction.ReleaseAndWait;
                break;
        }
    }

    /// <summary>
    /// "Follow the target" rules for the step's direction: hold while the target (marker) is ahead of the bar you control
    /// (right of it, or above it on a vertical bar), release while it's behind, pulse while it's inside.
    /// </summary>
    public static void ApplyFollowRules(FeedbackLoopStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        var horizontal = step.Orientation == Vision.BarOrientation.Horizontal;
        step.WhenBeforeZone = horizontal ? FeedbackAction.Release : FeedbackAction.Hold; // left / above
        step.WhenAfterZone = horizontal ? FeedbackAction.Hold : FeedbackAction.Release;  // right / below
        step.WhenInsideZone = FeedbackAction.Pulse;
    }

    /// <summary>
    /// "Click when it enters the zone": false = wait with the input released and click once when the marker gets in
    /// (timing bars, skill checks); true = hold the input while waiting and let go when it gets in (casting meters).
    /// </summary>
    public static void SetZoneEntryAction(FeedbackLoopStep step, bool releaseHeldInput)
    {
        ArgumentNullException.ThrowIfNull(step);
        var outside = releaseHeldInput ? FeedbackAction.Hold : FeedbackAction.Release;
        step.WhenBeforeZone = outside;
        step.WhenAfterZone = outside;
        step.WhenInsideZone = releaseHeldInput ? FeedbackAction.Release : FeedbackAction.Tap;
    }
}
