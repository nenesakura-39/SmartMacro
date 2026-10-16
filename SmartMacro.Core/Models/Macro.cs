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

    /// <summary>Pause between repeats, in ms (not after the last one). Also scaled by Speed.</summary>
    public int RepeatDelayMs { get; set; }
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
    /// </summary>
    public const int CurrentSchemaVersion = 8;

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
        if (macro.Steps is null) return 1;

        var required = 1;
        foreach (var step in macro.Steps)
        {
            var needed = step switch
            {
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
