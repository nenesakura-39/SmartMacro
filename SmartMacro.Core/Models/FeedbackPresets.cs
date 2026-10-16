namespace SmartMacro.Core.Models;

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
                step.PulsePeriodMs = FeedbackLoopStep.DefaultPulsePeriodMs;
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
