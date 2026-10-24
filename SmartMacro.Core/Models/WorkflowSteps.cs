using System.Text.Json.Serialization;

namespace SmartMacro.Core.Models;

// ---------------------------------------------------------------------------------------------------------------------
// Stage 3: conditions and blocks.
//
// The step list stays flat (the editor and old files keep working). Blocks are marked by pairs of steps:
//   If ... [Else ...] End If
//   Repeat ... End Repeat
// BlockStructure (Core/Workflow) checks that the markers match up and builds the jump table the engine uses.
// Everything here is plain data: no expressions, no scripts, no code in macro files.
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>What a condition looks for. Derived from the kind of search step inside it.</summary>
public enum ConditionKind
{
    /// <summary>Text read by OCR (the settings of a Find text step).</summary>
    TextVisible,

    /// <summary>A picture (the settings of a Find image step).</summary>
    ImageVisible,

    /// <summary>A patch of colour (the settings of a Find color step).</summary>
    ColorVisible,
}

/// <summary>
/// A yes/no question about the screen: "is this text / image / colour on screen?", optionally turned around
/// ("is it NOT on screen?").
/// The "what and where" settings are a normal Find text / Find image / Find color step stored in <see cref="Check"/>,
/// so a condition is checked by exactly the same detection code, validation and editor tools as those steps.
/// Of the search step's settings, a condition uses: what to look for, the search area, "keep checking for"
/// (TimeoutMs: how long to wait for the condition to become true; 0 = look once) and "check every".
/// The step's own Goal / Action / Offset / "when not found" settings are not used by conditions.
/// </summary>
public sealed class Condition
{
    /// <summary>
    /// The search settings: a <see cref="FindTextStep"/>, <see cref="FindImageStep"/> or <see cref="FindColorStep"/>.
    /// Typed as MacroStep so it is read and written with the same safe, closed list of step types as everything else.
    /// </summary>
    public MacroStep? Check { get; set; }

    /// <summary>True = the condition is true when the thing is NOT on screen.</summary>
    public bool Negate { get; set; }

    /// <summary>The search settings as a search step, or null if missing / the wrong type (the validator reports that).</summary>
    [JsonIgnore]
    public ScreenSearchStep? Search => Check is FindTextStep or FindImageStep or FindColorStep ? (ScreenSearchStep)Check : null;

    /// <summary>What the condition looks for, or null if <see cref="Check"/> is missing or unsupported.</summary>
    [JsonIgnore]
    public ConditionKind? Kind => Check switch
    {
        FindTextStep => ConditionKind.TextVisible,
        FindImageStep => ConditionKind.ImageVisible,
        FindColorStep => ConditionKind.ColorVisible,
        _ => null,
    };

    /// <summary>
    /// Text conditions: the text must be a whole word on screen ("art" doesn't match "apart"). Stored on the condition's
    /// <see cref="FindTextStep"/> (<see cref="FindTextStep.MatchWholeWord"/>); true for other kinds.
    /// </summary>
    [JsonIgnore]
    public bool MatchWholeWord
    {
        get => (Check as FindTextStep)?.MatchWholeWord ?? true;
        set { if (Check is FindTextStep text) text.MatchWholeWord = value; }
    }

    /// <summary>Text conditions: upper / lower case must match exactly (<see cref="FindTextStep.MatchCase"/>); true for other kinds.</summary>
    [JsonIgnore]
    public bool MatchCase
    {
        get => (Check as FindTextStep)?.MatchCase ?? true;
        set { if (Check is FindTextStep text) text.MatchCase = value; }
    }

    /// <summary>A new condition of the given kind with sensible defaults (look once, no click).</summary>
    public static Condition Create(ConditionKind kind) => new() { Check = CreateCheck(kind) };

    /// <summary>A fresh search step for a condition: looks once (timeout 0) and never clicks.</summary>
    public static ScreenSearchStep CreateCheck(ConditionKind kind)
    {
        ScreenSearchStep check = kind switch
        {
            ConditionKind.TextVisible => new FindTextStep { Text = "OK" },
            ConditionKind.ImageVisible => new FindImageStep(),
            ConditionKind.ColorVisible => new FindColorStep(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown condition kind."),
        };
        check.TimeoutMs = 0;
        check.Goal = WaitGoal.Appear;
        check.Action = FoundTextAction.None;
        check.WhenNotFound = TextNotFoundBehavior.Continue;
        return check;
    }
}

/// <summary>
/// Start of an If block: checks its condition once (waiting up to the condition's "keep checking for" time for it to
/// become true). True → runs the steps up to Else / End If. False → runs the steps after Else (if any).
/// </summary>
public sealed class IfStep : MacroStep
{
    public Condition Condition { get; set; } = Condition.Create(ConditionKind.TextVisible);

    /// <summary>Text condition: whole word only ("art" won't match "apart"). Same setting as <see cref="Condition.MatchWholeWord"/>.</summary>
    [JsonIgnore]
    public bool MatchWholeWord
    {
        get => Condition?.MatchWholeWord ?? true;
        set { if (Condition is not null) Condition.MatchWholeWord = value; }
    }

    /// <summary>Text condition: exact upper / lower case ("aRt" only matches "aRt"). Same setting as <see cref="Condition.MatchCase"/>.</summary>
    [JsonIgnore]
    public bool MatchCase
    {
        get => Condition?.MatchCase ?? true;
        set { if (Condition is not null) Condition.MatchCase = value; }
    }

    /// <summary>
    /// Optional "when true" action: when the condition is true because it SAW its text / image / colour (not a negated
    /// condition), move to / click / hold on that spot before the steps inside run. Saves a separate Mouse click step
    /// with "the last found spot". None (default) = only decide which steps run, like before.
    /// </summary>
    public FoundTextAction ThenAction { get; set; } = FoundTextAction.None;

    /// <summary>Offset from the center of what the condition saw, for <see cref="ThenAction"/>.</summary>
    public int ThenOffsetX { get; set; }
    public int ThenOffsetY { get; set; }

    /// <summary>Hold time for <see cref="FoundTextAction.Hold"/>, in ms.</summary>
    public int ThenHoldMs { get; set; } = ScreenSearchStep.DefaultActionHoldMs;

    /// <summary>
    /// "When found, also:" (AAA upgrade wording of <see cref="ThenAction"/>, the same stored value): when the image /
    /// colour / text "is on screen" check is true, click / double-click / right-click / move to the found spot
    /// (+ <see cref="FoundOffsetX"/>, <see cref="FoundOffsetY"/>) before the steps inside the If run.
    /// </summary>
    [JsonIgnore]
    public IfFoundAction WhenFoundAction
    {
        get => ThenAction switch
        {
            FoundTextAction.Click => IfFoundAction.ClickFoundSpot,
            FoundTextAction.DoubleClick => IfFoundAction.DoubleClickFoundSpot,
            FoundTextAction.RightClick => IfFoundAction.RightClickFoundSpot,
            FoundTextAction.Move => IfFoundAction.MoveToFoundSpot,
            FoundTextAction.Hold => IfFoundAction.HoldOnFoundSpot,
            _ => IfFoundAction.None,
        };
        set => ThenAction = value switch
        {
            IfFoundAction.ClickFoundSpot => FoundTextAction.Click,
            IfFoundAction.DoubleClickFoundSpot => FoundTextAction.DoubleClick,
            IfFoundAction.RightClickFoundSpot => FoundTextAction.RightClick,
            IfFoundAction.MoveToFoundSpot => FoundTextAction.Move,
            IfFoundAction.HoldOnFoundSpot => FoundTextAction.Hold,
            _ => FoundTextAction.None,
        };
    }

    /// <summary>Offset (px) from the center of what was found, for <see cref="WhenFoundAction"/> (same value as <see cref="ThenOffsetX"/>).</summary>
    [JsonIgnore]
    public int FoundOffsetX
    {
        get => ThenOffsetX;
        set => ThenOffsetX = value;
    }

    /// <inheritdoc cref="FoundOffsetX"/>
    [JsonIgnore]
    public int FoundOffsetY
    {
        get => ThenOffsetY;
        set => ThenOffsetY = value;
    }

    [JsonIgnore]
    public override StepKind Kind => StepKind.If;
}

/// <summary>"When found, also:" choices of an If step (see <see cref="IfStep.WhenFoundAction"/>).</summary>
public enum IfFoundAction
{
    /// <summary>Just run the steps inside the If.</summary>
    None,

    /// <summary>Click the found spot (+ offset).</summary>
    ClickFoundSpot,

    /// <summary>Double-click the found spot (+ offset).</summary>
    DoubleClickFoundSpot,

    /// <summary>Right-click the found spot (+ offset).</summary>
    RightClickFoundSpot,

    /// <summary>Move the mouse to the found spot (+ offset), no click.</summary>
    MoveToFoundSpot,

    /// <summary>Hold the left button on the found spot for <see cref="IfStep.ThenHoldMs"/> (older "Hold" choice).</summary>
    HoldOnFoundSpot,
}

/// <summary>Optional middle of an If block: the steps after it run when the condition was false.</summary>
public sealed class ElseStep : MacroStep
{
    [JsonIgnore]
    public override StepKind Kind => StepKind.Else;
}

/// <summary>End of an If block.</summary>
public sealed class EndIfStep : MacroStep
{
    [JsonIgnore]
    public override StepKind Kind => StepKind.EndIf;
}

/// <summary>How a Repeat block decides whether to run another turn.</summary>
public enum RepeatMode
{
    /// <summary>A fixed number of turns (<see cref="RepeatStep.Count"/>).</summary>
    Times,

    /// <summary>Before each turn: run it only while the condition is true.</summary>
    While,

    /// <summary>Before each turn: stop as soon as the condition is true.</summary>
    Until,
}

/// <summary>What happens when a loop hits its safety limit (max turns or max time).</summary>
public enum LoopLimitBehavior
{
    /// <summary>Stop the macro and show an error (default: something probably went wrong).</summary>
    StopMacro,

    /// <summary>Leave the loop and continue with the step after End Repeat.</summary>
    ExitLoop,
}

/// <summary>
/// Start of a Repeat block. The steps up to End Repeat run again and again:
/// - Times: exactly <see cref="Count"/> turns.
/// - While: before each turn the condition is checked; the loop ends when it is false.
/// - Until: before each turn the condition is checked; the loop ends when it is true.
/// While / Until loops must have a safety limit (<see cref="MaxIterations"/> and/or <see cref="MaxDurationMs"/>).
/// Condition checks in a loop are at least the condition's "check every" time apart, so even an empty loop
/// can't hammer the CPU with screen captures.
/// </summary>
public sealed class RepeatStep : MacroStep
{
    public RepeatMode Mode { get; set; } = RepeatMode.Times;

    /// <summary>Number of turns for <see cref="RepeatMode.Times"/>.</summary>
    public int Count { get; set; } = 3;

    /// <summary>The condition for While / Until (null for Times).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Condition? Condition { get; set; }

    /// <summary>Text condition: whole word only ("art" won't match "apart"). Same setting as <see cref="Condition.MatchWholeWord"/>.</summary>
    [JsonIgnore]
    public bool MatchWholeWord
    {
        get => Condition?.MatchWholeWord ?? true;
        set { if (Condition is not null) Condition.MatchWholeWord = value; }
    }

    /// <summary>Text condition: exact upper / lower case ("aRt" only matches "aRt"). Same setting as <see cref="Condition.MatchCase"/>.</summary>
    [JsonIgnore]
    public bool MatchCase
    {
        get => Condition?.MatchCase ?? true;
        set { if (Condition is not null) Condition.MatchCase = value; }
    }

    /// <summary>Safety limit for While / Until: at most this many turns. 0 = no turn limit (then a time limit is required).</summary>
    public int MaxIterations { get; set; } = 100;

    /// <summary>Safety limit for every mode: at most this long (ms, paused time not counted). 0 = no time limit.</summary>
    public int MaxDurationMs { get; set; }

    /// <summary>What happens when a safety limit is reached.</summary>
    public LoopLimitBehavior WhenLimitReached { get; set; } = LoopLimitBehavior.StopMacro;

    /// <summary>
    /// Grace window (ms) before While / Until ends because its target is NOT seen: the condition is checked again for
    /// up to this long and the loop only ends if the target stays away. Covers short gaps, like a game button that
    /// re-appears 100-250 ms after it was clicked. Null = automatic (<see cref="DefaultKeepCheckingForMs"/> for image
    /// and colour conditions, 0 for text, whose reads are slow). 0 = end on the first miss (the old behaviour).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? KeepCheckingForMs { get; set; }

    /// <summary>Automatic grace window for image / colour conditions.</summary>
    public const int DefaultKeepCheckingForMs = 800;

    /// <summary>Largest allowed grace window.</summary>
    public const int MaxKeepCheckingForMs = 60_000;

    /// <summary>The grace window that is actually used (see <see cref="KeepCheckingForMs"/>).</summary>
    [JsonIgnore]
    public int EffectiveKeepCheckingForMs => KeepCheckingForMs
        ?? (Condition?.Kind is ConditionKind.ImageVisible or ConditionKind.ColorVisible ? DefaultKeepCheckingForMs : 0);

    /// <summary>
    /// True when the loop ends because its target is not seen: While "… is on screen" or Until "… is NOT on screen".
    /// Only then the grace window applies (a target that IS seen is trusted right away).
    /// </summary>
    [JsonIgnore]
    public bool EndsWhenTargetMissing => Condition is { } c
        && ((Mode == RepeatMode.While && !c.Negate) || (Mode == RepeatMode.Until && c.Negate));

    [JsonIgnore]
    public override StepKind Kind => StepKind.Repeat;
}

/// <summary>End of a Repeat block: goes back to its Repeat step for the next turn.</summary>
public sealed class EndRepeatStep : MacroStep
{
    [JsonIgnore]
    public override StepKind Kind => StepKind.EndRepeat;
}

/// <summary>Small helpers for the screen checks inside steps (used by validation, file versions and the app).</summary>
public static class WorkflowSteps
{
    /// <summary>True for If / Else / End If / Repeat / End Repeat.</summary>
    public static bool IsBlockStep(MacroStep? step) => step is IfStep or ElseStep or EndIfStep or RepeatStep or EndRepeatStep;

    /// <summary>The condition of an If / Repeat step (null for other steps, or Repeat Times).</summary>
    public static Condition? ConditionOf(MacroStep? step) => step switch
    {
        IfStep i => i.Condition,
        RepeatStep { Mode: RepeatMode.While or RepeatMode.Until } r => r.Condition,
        _ => null,
    };

    /// <summary>
    /// Every condition a step checks: the condition of an If / Repeat while / until, or the "done when" / "failed when"
    /// conditions of a Feedback loop (only the ones that are set).
    /// </summary>
    public static IEnumerable<Condition> ConditionsOf(MacroStep? step)
    {
        if (ConditionOf(step) is { } condition) yield return condition;
        if (step is FeedbackLoopStep feedback)
        {
            if (feedback.CompleteWhen is { } complete) yield return complete;
            if (feedback.FailWhen is { } fail) yield return fail;
        }
    }

    /// <summary>Every screen search an enabled step does: the step itself (Find…) or its conditions' searches.</summary>
    public static IEnumerable<ScreenSearchStep> ScreenSearches(MacroStep? step)
    {
        if (step is null) yield break;
        if (step is ScreenSearchStep search) yield return search;
        foreach (var condition in ConditionsOf(step))
        {
            if (condition.Search is { } check) yield return check;
        }
    }

    /// <summary>True if any enabled step reads text with OCR (directly or in a condition).</summary>
    public static bool UsesTextRecognition(Macro macro) =>
        macro.Steps is { } steps && steps.Any(s => s is { Enabled: true } && ScreenSearches(s).Any(x => x is FindTextStep));
}
