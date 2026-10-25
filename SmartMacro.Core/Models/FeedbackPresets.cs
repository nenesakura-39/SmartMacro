using SmartMacro.Core.Vision;

namespace SmartMacro.Core.Models;

/// <summary>One-click bar physics presets for Smart Predictive feedback loops (Feedback Loop 2.0 / 3.0).</summary>
public enum FeedbackGamePreset
{
    /// <summary>"🧲 Momentum Bar (Smart PD + Hover Pulse)": lead 0.18 s, dead zone 6 px, hover pulse every 80 ms, 120 captures/s.</summary>
    FischMomentumBar = 0,

    /// <summary>"⚡ Snap Bar (Fast Reflex Tracker)": short lead 0.08 s, dead zone 3 px, no pulsing, 120 captures/s.</summary>
    FastReflexTracker = 1,

    /// <summary>"🪶 Floaty Bar (Smooth Balance)": long lead 0.25 s, dead zone 10 px, gentle 120 ms pulse, 60 captures/s.</summary>
    SmoothBalanceBar = 2,

    /// <summary>"🪶 Floaty Bar (Gentle Drift)"</summary>
    Slayers2HorizontalBar = 3,

    /// <summary>"⬇️ Gravity Bar (Hold to Rise)"</summary>
    VerticalBarStardewGpo = 4,

    /// <summary>"⚡ Snap Bar (Zero Gravity)"</summary>
    InstantBinaryZeroGravity = 5,

    // Neutral behavior aliases:
    MomentumBar = FischMomentumBar,
    SnapBar = FastReflexTracker,
    FloatyBar = SmoothBalanceBar,
    GravityBar = VerticalBarStardewGpo,
}

/// <summary>Quick behavior presets for the Closed-Loop Adaptive Servo Controller.</summary>
public enum FeedbackQuickPreset
{
    MomentumBar = 0,
    GravityBar = 1,
    SnapBar = 2,
    FloatyBar = 3,

    // Backward-compatible aliases:
    FischHorizontal = MomentumBar,
    VerticalBar = GravityBar,
    InstantBinary = SnapBar,
    Slayers2Horizontal = FloatyBar,
}

/// <summary>
/// Metadata for a physics behavior card in the Feedback Loop inspector.
/// </summary>
public sealed record FeedbackBehaviorCard(
    FeedbackQuickPreset Preset,
    string Icon,
    string Title,
    string Subtitle);

/// <summary>
/// Ready-made settings for the common kinds of bar minigame, so a Feedback loop can be set up with three picks
/// (area, marker colour, zone colour) instead of every raw parameter. Applying a preset only changes the settings
/// that define how the loop behaves; the area, orientation, and colours the user picked are kept.
/// </summary>
public static class FeedbackPresets
{
    /// <summary>Default max runtime for "follow the target" (a bar minigame rarely lasts longer).</summary>
    public const int FollowMaxRuntimeMs = 60_000;

    /// <summary>Default max runtime for "click when it enters the zone".</summary>
    public const int ZoneClickMaxRuntimeMs = 10_000;

    /// <summary>"Follow the target": the bar counts as gone after this long without a clear reading.</summary>
    public const int FollowLostTimeoutMs = 1_500;

    /// <summary>Smart Predictive loops look this many times per second (the sensor captures only the bar area).</summary>
    public const int SmartCapturesPerSecond = 120;

    /// <summary>Smart Predictive loops: minimum time between input changes (one 120 Hz frame).</summary>
    public const int SmartMinInputChangeMs = 8;

    /// <summary>Smart Predictive hover pulse period.</summary>
    public const int SmartPulsePeriodMs = 80;

    /// <summary>The 4 distinct physics behavior presets shown in the UI.</summary>
    public static IReadOnlyList<FeedbackQuickPreset> DistinctQuickPresets { get; } = new[]
    {
        FeedbackQuickPreset.MomentumBar,
        FeedbackQuickPreset.GravityBar,
        FeedbackQuickPreset.SnapBar,
        FeedbackQuickPreset.FloatyBar,
    };

    /// <summary>The 4 distinct game presets for Feedback Loop 2.0 compatibility.</summary>
    public static IReadOnlyList<FeedbackGamePreset> DistinctGamePresets { get; } = new[]
    {
        FeedbackGamePreset.FischMomentumBar,
        FeedbackGamePreset.VerticalBarStardewGpo,
        FeedbackGamePreset.FastReflexTracker,
        FeedbackGamePreset.SmoothBalanceBar,
    };

    /// <summary>The 4 visual behavior cards shown in the Feedback Loop inspector.</summary>
    public static IReadOnlyList<FeedbackBehaviorCard> BehaviorCards { get; } = new[]
    {
        new FeedbackBehaviorCard(
            FeedbackQuickPreset.MomentumBar,
            "🧲",
            "Momentum Bar",
            "Keeps sliding after you let go — brakes early before crossing"),
        new FeedbackBehaviorCard(
            FeedbackQuickPreset.GravityBar,
            "⬇️",
            "Gravity Bar",
            "Falls when released, rises while held — steady hover balance"),
        new FeedbackBehaviorCard(
            FeedbackQuickPreset.SnapBar,
            "⚡",
            "Snap Bar",
            "Moves instantly at steady speed with minimal coasting"),
        new FeedbackBehaviorCard(
            FeedbackQuickPreset.FloatyBar,
            "🪶",
            "Floaty Bar",
            "Slow acceleration and soft damping for delicate drift bars"),
    };

    /// <summary>The label of a preset, as shown on its button.</summary>
    public static string Label(FeedbackGamePreset preset) => preset switch
    {
        FeedbackGamePreset.FischMomentumBar => "🧲 Momentum Bar (Smart PD + Hover Pulse)",
        FeedbackGamePreset.FastReflexTracker => "⚡ Snap Bar (Fast Reflex Tracker)",
        FeedbackGamePreset.SmoothBalanceBar => "🪶 Floaty Bar (Smooth Balance)",
        FeedbackGamePreset.Slayers2HorizontalBar => "🪶 Floaty Bar (Gentle Drift)",
        FeedbackGamePreset.VerticalBarStardewGpo => "⬇️ Gravity Bar (Hold to Rise)",
        FeedbackGamePreset.InstantBinaryZeroGravity => "⚡ Snap Bar (Zero Gravity)",
        _ => "🪶 Floaty Bar (Smooth Balance)",
    };

    /// <summary>The label of a quick tuning preset, as shown in the Behavior Card Selector.</summary>
    public static string QuickPresetLabel(FeedbackQuickPreset preset) => preset switch
    {
        FeedbackQuickPreset.MomentumBar => "🧲 Momentum Bar (Heavy Glide)",
        FeedbackQuickPreset.GravityBar => "⬇️ Gravity Bar (Hold to Rise)",
        FeedbackQuickPreset.SnapBar => "⚡ Snap Bar (Instant Response)",
        FeedbackQuickPreset.FloatyBar => "🪶 Floaty Bar (Gentle Drift)",
        _ => "🧲 Momentum Bar (Heavy Glide)",
    };

    /// <summary>The label of a control mode, as shown in the Control Mode selector.</summary>
    public static string ControlModeLabel(FeedbackControlMode mode) => mode switch
    {
        FeedbackControlMode.SmartPredictive => "Smart Predictive (Adaptive PD + Stopping-Distance Brake)",
        _ => "Classic Simple Hold",
    };

    /// <summary>Infers which behavior preset card best matches the step's current servo parameters.</summary>
    public static FeedbackQuickPreset InferQuickPreset(FeedbackLoopStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (step.Kp >= 1.80) return FeedbackQuickPreset.SnapBar;
        if (step.Kp <= 1.18) return FeedbackQuickPreset.FloatyBar;
        if (step.Orientation == BarOrientation.Vertical || Math.Abs(step.Kp - 1.30) < 0.02)
            return FeedbackQuickPreset.GravityBar;
        return FeedbackQuickPreset.MomentumBar;
    }

    /// <summary>Applies one of the 4 Behavior Presets for the Closed-Loop Adaptive Servo Controller.</summary>
    public static void ApplyQuickPreset(FeedbackLoopStep step, FeedbackQuickPreset preset)
    {
        ArgumentNullException.ThrowIfNull(step);
        var preservedOrientation = step.Orientation;
        Apply(step, FeedbackPreset.FollowTargetBar);
        step.Orientation = preservedOrientation;
        ApplyFollowRules(step);

        switch (preset)
        {
            case FeedbackQuickPreset.MomentumBar:
                step.Kp = 1.35;
                step.Kd = 0.85;
                step.DerivativeTargetGain = 0.55;
                step.NeutralHoverDutyCycle = 0.45;
                step.PwmCycleMs = 17;
                step.MinPulseMs = 17;
                step.ComfortZonePercent = 20.0;
                step.Responsiveness = 55;
                step.AnkleBreakBrakingMultiplier = 1.85;
                step.BoundaryLockThresholdPercent = 10.0;
                step.AutoCalibrate = true;
                step.AutoExitWhenBarDisappears = true;
                step.LearnedProfile = new BarProfile(
                    RiseAccelPxPerSec2: 850.0,
                    FallAccelPxPerSec2: 720.0,
                    MaxSpeedPxPerSec: 560.0,
                    GlidePx: 24.0,
                    LatencyMs: 32.0,
                    HoverDuty: 0.45,
                    IsCalibrated: false);
                break;

            case FeedbackQuickPreset.GravityBar:
                step.Kp = 1.30;
                step.Kd = 0.75;
                step.DerivativeTargetGain = 0.45;
                step.NeutralHoverDutyCycle = 0.46;
                step.PwmCycleMs = 17;
                step.MinPulseMs = 17;
                step.ComfortZonePercent = 20.0;
                step.Responsiveness = 50;
                step.AnkleBreakBrakingMultiplier = 1.65;
                step.BoundaryLockThresholdPercent = 10.0;
                step.AutoCalibrate = true;
                step.AutoExitWhenBarDisappears = true;
                step.LearnedProfile = new BarProfile(
                    RiseAccelPxPerSec2: 760.0,
                    FallAccelPxPerSec2: 680.0,
                    MaxSpeedPxPerSec: 480.0,
                    GlidePx: 18.0,
                    LatencyMs: 32.0,
                    HoverDuty: 0.46,
                    IsCalibrated: false);
                break;

            case FeedbackQuickPreset.SnapBar:
                step.NeutralHoverDutyCycle = 0.50;
                step.PwmCycleMs = 17;
                step.MinPulseMs = 15;
                step.ComfortZonePercent = 15.0;
                step.Responsiveness = 70;
                step.Kp = 2.20;
                step.Kd = 0.30;
                step.DerivativeTargetGain = 0.25;
                step.AnkleBreakBrakingMultiplier = 1.25;
                step.BoundaryLockThresholdPercent = 6.0;
                step.AutoCalibrate = true;
                step.AutoExitWhenBarDisappears = true;
                step.LearnedProfile = new BarProfile(
                    RiseAccelPxPerSec2: 1800.0,
                    FallAccelPxPerSec2: 1800.0,
                    MaxSpeedPxPerSec: 650.0,
                    GlidePx: 4.0,
                    LatencyMs: 25.0,
                    HoverDuty: 0.50,
                    IsCalibrated: false);
                break;

            case FeedbackQuickPreset.FloatyBar:
                step.Kp = 1.10;
                step.Kd = 0.90;
                step.DerivativeTargetGain = 0.40;
                step.NeutralHoverDutyCycle = 0.44;
                step.PwmCycleMs = 20;
                step.MinPulseMs = 18;
                step.ComfortZonePercent = 24.0;
                step.Responsiveness = 35;
                step.AnkleBreakBrakingMultiplier = 1.50;
                step.BoundaryLockThresholdPercent = 8.0;
                step.AutoCalibrate = true;
                step.AutoExitWhenBarDisappears = true;
                step.LearnedProfile = new BarProfile(
                    RiseAccelPxPerSec2: 520.0,
                    FallAccelPxPerSec2: 460.0,
                    MaxSpeedPxPerSec: 360.0,
                    GlidePx: 22.0,
                    LatencyMs: 36.0,
                    HoverDuty: 0.44,
                    IsCalibrated: false);
                break;
        }
    }

    /// <summary>
    /// One-click preset: the "follow the target" setup (zone = your bar found by its colour, marker = the target)
    /// with Smart Predictive control tuned for that kind of bar. The area, colours and direction the user picked are kept.
    /// </summary>
    public static void ApplyGamePreset(FeedbackLoopStep step, FeedbackGamePreset preset)
    {
        ArgumentNullException.ThrowIfNull(step);
        var preservedOrientation = step.Orientation;
        Apply(step, FeedbackPreset.FollowTargetBar);
        step.Orientation = preservedOrientation;
        ApplyFollowRules(step);

        switch (preset)
        {
            case FeedbackGamePreset.FischMomentumBar:
                ApplyQuickPreset(step, FeedbackQuickPreset.MomentumBar);
                SetSmart(step, lead: FeedbackLoopStep.DefaultLeadPredictionFactor, deadzonePx: FeedbackLoopStep.DefaultDeadzonePx,
                    pwm: true, pulseMs: SmartPulsePeriodMs, capturesPerSecond: SmartCapturesPerSecond, minChangeMs: SmartMinInputChangeMs);
                break;
            case FeedbackGamePreset.FastReflexTracker:
                ApplyQuickPreset(step, FeedbackQuickPreset.SnapBar);
                SetSmart(step, lead: 0.08, deadzonePx: 3, pwm: false, pulseMs: SmartPulsePeriodMs, capturesPerSecond: SmartCapturesPerSecond, minChangeMs: SmartMinInputChangeMs);
                break;
            case FeedbackGamePreset.SmoothBalanceBar:
                ApplyQuickPreset(step, FeedbackQuickPreset.FloatyBar);
                SetSmart(step, lead: 0.25, deadzonePx: 10, pwm: true, pulseMs: 120, capturesPerSecond: 60, minChangeMs: 16);
                break;
            case FeedbackGamePreset.Slayers2HorizontalBar:
                ApplyQuickPreset(step, FeedbackQuickPreset.FloatyBar);
                break;
            case FeedbackGamePreset.VerticalBarStardewGpo:
                ApplyQuickPreset(step, FeedbackQuickPreset.GravityBar);
                break;
            case FeedbackGamePreset.InstantBinaryZeroGravity:
                ApplyQuickPreset(step, FeedbackQuickPreset.SnapBar);
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
