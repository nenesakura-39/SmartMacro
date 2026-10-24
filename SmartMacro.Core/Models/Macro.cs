using System.Text.Json.Serialization;

namespace SmartMacro.Core.Models;

/// <summary>A saved macro: a name plus an ordered list of steps.</summary>
public sealed class Macro
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ModifiedAt { get; set; }
    public List<MacroStep> Steps { get; set; } = new();

    /// <summary>How many times playback runs the steps. 0 = repeat until stopped.</summary>
    public int RepeatCount { get; set; } = 1;

    /// <summary>Playback speed multiplier for delays (2 = twice as fast). 0.1 to 10.</summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>
    /// AAA name for <see cref="Speed"/> (the same stored value): 0.5× / 1× / 1.5× / 2× / 5×… Delays, glides, recorded
    /// input and hold times are divided by it; holds never get shorter than 25 ms so games still see the clicks.
    /// Setting it clamps to <see cref="MinPlaybackSpeed"/>–<see cref="MaxPlaybackSpeed"/>.
    /// </summary>
    [JsonIgnore]
    public double PlaybackSpeed
    {
        get => Speed;
        set => Speed = double.IsNaN(value) ? 1.0 : Math.Clamp(value, MinPlaybackSpeed, MaxPlaybackSpeed);
    }

    public const double MinPlaybackSpeed = 0.25;
    public const double MaxPlaybackSpeed = 10.0;

    /// <summary>The speeds offered by the editor's speed selector.</summary>
    public static IReadOnlyList<double> SpeedPresets { get; } = new[] { 0.5, 1.0, 1.5, 2.0, 5.0 };

    /// <summary>Pause between repeats, in ms (not after the last one). Also scaled by Speed.</summary>
    public int RepeatDelayMs { get; set; }

    // ---------------- Target window + resolution scaling (format 11) ----------------

    /// <summary>
    /// The window the macro is made for (part of its title, e.g. "Roblox"), or null for "Any / main screen".
    /// Only used when <see cref="ScaleCoordinatesToWindow"/> is on.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetWindowTitle { get; set; }

    /// <summary>
    /// Where the target window (or the main monitor) was, and how big it was, when the positions in this macro were picked
    /// or recorded: physical pixels in screen coordinates (e.g. 0, 0, 1920×1080).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ReferenceLeft { get; set; }

    /// <inheritdoc cref="ReferenceLeft"/>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ReferenceTop { get; set; }

    /// <inheritdoc cref="ReferenceLeft"/>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ReferenceWidth { get; set; }

    /// <inheritdoc cref="ReferenceLeft"/>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ReferenceHeight { get; set; }

    /// <summary>
    /// "Auto-adjust coordinates if the window moves or the screen resolution changes": at playback time every stored
    /// position (Move / Click / Scroll, drag ends, custom search areas, feedback bar areas) is moved and scaled from the
    /// reference rectangle into the target window's current bounds (or the current main monitor). Off = exact pixels.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ScaleCoordinatesToWindow { get; set; }

    // ---------------- Humanize (format 11) ----------------

    /// <summary>
    /// "Humanize": small random variation so repeated clicks don't hit the exact same pixel with the exact same timing:
    /// ± <see cref="EffectiveClickJitterPx"/> around click spots (kept inside what was found) and
    /// ± <see cref="EffectiveDelayJitterPercent"/> % on delays and mouse glide times. Off by default.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool HumanizePlayback { get; set; }

    /// <summary>Largest random click offset in pixels (null = <see cref="DefaultClickJitterPx"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ClickJitterPx { get; set; }

    /// <summary>Largest random delay / glide change in percent (null = <see cref="DefaultDelayJitterPercent"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DelayJitterPercent { get; set; }

    /// <summary>Humanize defaults: ±2 px around click spots, ±4 % on delays of 40 ms or more.</summary>
    public const int DefaultClickJitterPx = 2;
    public const int DefaultDelayJitterPercent = 4;
    public const int MaxClickJitterPx = 50;
    public const int MaxDelayJitterPercent = 50;

    [JsonIgnore]
    public int EffectiveClickJitterPx => ClickJitterPx ?? DefaultClickJitterPx;

    [JsonIgnore]
    public int EffectiveDelayJitterPercent => DelayJitterPercent ?? DefaultDelayJitterPercent;

    /// <summary>
    /// A shallow copy of this macro (same ID, name, speed, target window, Humanize, …) that plays
    /// <paramref name="steps"/> instead of its own list. The steps themselves aren't copied. Used by "Play from here",
    /// which plays a cleaned-up slice of the list with all the macro's other settings.
    /// </summary>
    public Macro CopyWithSteps(List<MacroStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var copy = (Macro)MemberwiseClone();
        copy.Steps = steps;
        return copy;
    }
}

/// <summary>
/// What actually gets written to disk. The schema version lets future versions
/// migrate old files and lets old versions refuse files they don't understand.
/// </summary>
public sealed class MacroDocument
{
    /// <summary>
    /// Newest format this version understands.
    /// 1 = original format. 2 = adds hold times on Press / Click steps and "Recorded input (game mode)" steps.
    /// 3 = adds smart recordings: recorded input with cursor positions ("u1" event text) and mouse segments.
    /// 4 = adds visual detection: "Find image" and "Find color" steps, and "wait until it disappears" on Find text.
    /// 5 = adds workflow blocks: If / Else / End If and Repeat / End Repeat steps with conditions.
    /// 6 = adds "Feedback loop" steps (continuous visual feedback).
    /// 7 = adds "skip the next step" as a Find step's "when the time is up" choice and "ignore the edges" on Find image.
    /// 8 = adds quality-of-life options: "hold the left button on it" as a Find step's "when found" action, a "when true"
    ///     action on If steps, Hold / Drag mouse click actions, and feedback loop presets, pulse rule and
    ///     "finish when it reaches the zone".
    /// 9 = adds "ignore background / match bright text and edges" on Find image (and image conditions),
    ///     "keep checking before leaving" on Repeat While / Until, and "move the cursor away after clicking" on Find steps.
    /// 10 = adds "where to look" (main monitor / a specific monitor / an app window) on Find steps and conditions,
    ///     movement styles (smooth glide / human curve) with a glide time on Move / Click / Find steps, and click timing
    ///     (click hold, double-click gap, wait after click) on Mouse click and Find steps.
    /// 11 = adds macro settings: a target window with "auto-adjust coordinates" (reference rectangle) and "Humanize"
    ///     (click jitter / delay variation).
    /// 12 = adds Smart Click Snapshots (a recorded "what you clicked" picture + Smart-Aim on Mouse click steps, and the
    ///     pictures of clicks inside recorded input) and "on the last found spot + offset" / "by an offset from the
    ///     cursor" targets with X / Y offsets on Move mouse and Mouse click steps.
    /// 13 = adds Feedback Loop 2.0: "Smart Predictive" control (PD lead prediction, dead zone, hover micro-pulse) on
    ///     Feedback loop steps. Older versions would silently run such a loop with the classic rules, so it needs 13.
    ///     Also adds the "Wait for screen change", "Focus window" and "Take screenshot" steps.
    /// </summary>
    public const int CurrentSchemaVersion = 13;

    public int SchemaVersion { get; set; }
    public Macro? Macro { get; set; }

    /// <summary>
    /// The oldest format that can hold this macro without losing anything. Macros that don't use the
    /// version 2 to 7 features are still written as the older version, so older copies of SmartMacro can keep
    /// opening them. Older files need no migration: the new fields simply default to 0 / off / null, which is
    /// exactly the old behavior (game-mode recordings stay relative-only).
    /// </summary>
    public static int RequiredSchemaVersion(Macro macro)
    {
        ArgumentNullException.ThrowIfNull(macro);
        if (macro.Steps is null) return UsesVersion11Features(macro) ? 11 : 1;

        var required = UsesVersion11Features(macro) ? 11 : 1;
        foreach (var step in macro.Steps)
        {
            var needed = step switch
            {
                FeedbackLoopStep { ControlMode: not FeedbackControlMode.ClassicSimpleHold } => 13,
                WaitForChangeStep or FocusWindowStep or ScreenshotStep => 13,
                _ when UsesVersion12Features(step) => 12,
                _ when UsesVersion10Features(step) => 10,
                _ when UsesVersion9Features(step) => 9,
                _ when UsesQualityOfLifeFeatures(step) => 8,
                ScreenSearchStep { WhenNotFound: TextNotFoundBehavior.SkipNextStep } => 7,
                FindImageStep { IgnoreEdgesPercent: not 0 } => 7,
                FeedbackLoopStep => 6,
                IfStep or ElseStep or EndIfStep or RepeatStep or EndRepeatStep => 5,
                FindImageStep or FindColorStep => 4,
                FindTextStep { Goal: not WaitGoal.Appear } => 4,
                InputSequenceStep q when UsesSmartRecordingFeatures(q) => 3,
                InputSequenceStep => 2,
                KeyPressStep { HoldMs: not 0 } => 2,
                MouseClickStep { HoldMs: not 0 } => 2,
                _ => 1,
            };
            if (needed > required) required = needed;
        }

        return required;
    }

    /// <summary>True if the macro uses a macro-level option added in format 11 (see <see cref="CurrentSchemaVersion"/>).</summary>
    private static bool UsesVersion11Features(Macro macro) =>
        macro.HumanizePlayback || macro.ScaleCoordinatesToWindow || macro.TargetWindowTitle is not null
        || macro.ClickJitterPx is not null || macro.DelayJitterPercent is not null
        || macro.ReferenceWidth != 0 || macro.ReferenceHeight != 0;

    /// <summary>True if the step uses an option added in format 12 (see <see cref="CurrentSchemaVersion"/>).</summary>
    private static bool UsesVersion12Features(MacroStep step) => step switch
    {
        MouseClickStep c => c.TargetThumbnailBase64 is not null || c.SmartAimAtTarget || c.TargetAnchorX is not null
                            || c.TargetAnchorY is not null || c.SmartAimAccuracyOverride is not null || c.SmartAimRadiusOverride is not null
                            || c.OffsetX != 0 || c.OffsetY != 0,
        MouseMoveStep m => m.TargetMode != ClickTargetMode.Coordinates || m.OffsetX != 0 || m.OffsetY != 0,
        InputSequenceStep q => q.ClickTargets is { Count: > 0 },
        _ => false,
    };

    /// <summary>True if the step uses an option added in format 10 (see <see cref="CurrentSchemaVersion"/>).</summary>
    private static bool UsesVersion10Features(MacroStep step)
    {
        switch (step)
        {
            case MouseMoveStep { MoveStyle: not null and not MouseMoveStyle.Instant }:
                return true;
            case MouseClickStep c when c.MoveStyle is not null and not MouseMoveStyle.Instant
                                       || c.DoubleClickIntervalMs != 0 || c.PostActionDelayMs != 0:
                return true;
            case ScreenSearchStep search when UsesVersion10Search(search):
                return true;
        }

        foreach (var condition in WorkflowSteps.ConditionsOf(step))
            if (condition.Search is { } check && UsesVersion10Search(check)) return true;
        return false;
    }

    /// <summary>
    /// A new "where to look" (all monitors and the custom area are what older versions already understand through
    /// "search the whole screen"), a movement style, or click timing other than the old fixed 35 / 65 / 120 ms.
    /// </summary>
    private static bool UsesVersion10Search(ScreenSearchStep search) =>
        search.Scope is Vision.SearchScope.PrimaryMonitor or Vision.SearchScope.Monitor or Vision.SearchScope.Window
        || search.MoveStyle is not null and not MouseMoveStyle.Instant
        || search.ClickHoldMs != MouseMotionDefaults.ClickHoldMs
        || search.DoubleClickIntervalMs != MouseMotionDefaults.DoubleClickIntervalMs
        || search.PostActionDelayMs != MouseMotionDefaults.PostActionDelayMs;

    /// <summary>True if the step uses an option added in format 9 (see <see cref="CurrentSchemaVersion"/>).</summary>
    private static bool UsesVersion9Features(MacroStep step)
    {
        if (step is FindImageStep { MatchForeground: true }) return true;
        if (step is ScreenSearchStep { MoveCursorAwayAfterAction: true }) return true;
        if (step is RepeatStep { KeepCheckingForMs: not null }) return true;
        foreach (var condition in WorkflowSteps.ConditionsOf(step))
            if (condition.Check is FindImageStep { MatchForeground: true }) return true;
        return false;
    }

    /// <summary>True if the step uses an option added in format 8 (see <see cref="CurrentSchemaVersion"/>).</summary>
    private static bool UsesQualityOfLifeFeatures(MacroStep step) => step switch
    {
        ScreenSearchStep { Action: FoundTextAction.Hold } => true,
        IfStep { ThenAction: not FoundTextAction.None } => true,
        MouseClickStep { Action: MouseClickAction.Hold or MouseClickAction.Drag } => true,
        FeedbackLoopStep f => f.Preset != FeedbackPreset.Custom || f.FinishWhenInZone
                              || f.WhenBeforeZone == FeedbackAction.Pulse || f.WhenInsideZone == FeedbackAction.Pulse
                              || f.WhenAfterZone == FeedbackAction.Pulse,
        _ => false,
    };

    private static bool UsesSmartRecordingFeatures(InputSequenceStep step)
    {
        if (step.MouseSegments is not null) return true;
        if (step.Events is null) return false;
        foreach (var e in step.Events)
        {
            if (e.HasPosition && e.IsMouse) return true;
        }

        return false;
    }
}
