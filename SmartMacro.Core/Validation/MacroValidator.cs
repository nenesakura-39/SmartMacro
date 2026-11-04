using System.Globalization;
using SmartMacro.Core.Models;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Vision;
using SmartMacro.Core.Workflow;

namespace SmartMacro.Core.Validation;

public enum IssueSeverity
{
    Warning,
    Error,
}

/// <summary>One validation finding. StepIndex is 0-based; null means it's about the macro itself.</summary>
public sealed record ValidationIssue(
    IssueSeverity Severity,
    string Message,
    int? StepIndex = null,
    Guid? StepId = null,
    string? Property = null)
{
    public string DisplayText => StepIndex is int i ? $"Step {i + 1}: {Message}" : Message;
}

/// <summary>The virtual screen rectangle in physical pixels (covers all monitors).</summary>
public sealed record ScreenBounds(int Left, int Top, int Width, int Height)
{
    public bool Contains(int x, int y) =>
        x >= Left && y >= Top && x < Left + Width && y < Top + Height;

    public override string ToString() =>
        $"X {Left} to {Left + Width - 1}, Y {Top} to {Top + Height - 1}";
}

/// <summary>Thrown when someone tries to save a macro that has validation errors.</summary>
public sealed class MacroValidationException : Exception
{
    public MacroValidationException(IReadOnlyList<ValidationIssue> issues)
        : base(BuildMessage(issues))
    {
        Issues = issues;
    }

    public IReadOnlyList<ValidationIssue> Issues { get; }

    private static string BuildMessage(IReadOnlyList<ValidationIssue> issues)
    {
        var errors = issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.DisplayText).Take(5);
        return "The macro has validation errors: " + string.Join("; ", errors);
    }
}

/// <summary>
/// Checks macros and steps. Errors block saving (and later, playback). Warnings are just hints.
/// Split across <c>MacroValidator.cs</c>, <c>MacroValidator.Steps.cs</c>, and <c>MacroValidator.VisionAndSequence.cs</c>.
/// </summary>
public static partial class MacroValidator
{
    public const int MaxNameLength = 100;
    public const int MaxDescriptionLength = 2_000;
    public const int MaxCommentLength = 500;
    public const int MaxSteps = 100_000;
    public const int MaxDelayMs = 3_600_000;       // 1 hour
    public const int MaxTextLength = 10_000;
    public const int MaxCharacterDelayMs = 10_000;
    public const int MaxScrollDelta = 12_000;      // 100 notches
    public const int MaxRepeatCount = 100_000;
    public const int MaxSearchTextLength = 200;
    public const int MinCheckIntervalMs = 100;
    public const int MinVisionCheckIntervalMs = 50;  // Find image / Find color (cheaper than OCR)
    public const int MinTemplateSize = 6;            // pixels, each side
    public const int MaxTemplateSize = MaxRegionSize; // pixels, each side (no 400x400 cap; up to full monitor/region resolution)
    public const int MaxTemplateDataLength = 50_000_000; // characters of Base64
    public const int MaxColorPixels = 10_000_000;
    public const int MaxCheckIntervalMs = 60_000;
    public const int MaxOffset = 5_000;
    public const int MaxRegionSize = 20_000;
    public const int MaxHoldMs = 60_000;           // 1 minute
    public const int MaxSequenceEvents = 1_000_000;
    public const long MaxSequenceDurationUs = 3_600_000_000L; // 1 hour
    public const int MaxRelativeMove = 100_000;    // per event, in mouse counts
    public const int MaxScreenCoordinate = 100_000; // recorded cursor positions, physical pixels (any sign)
    public const int MaxMouseSegments = 10_000;
    public const double MinSpeed = 0.1;
    public const double MaxSpeed = 10.0;
    public const int MaxLoopCount = 1_000_000;       // turns of one Repeat block
    public const int MaxLoopDurationMs = 86_400_000; // 24 hours

    // Stage 4: feedback loops.
    public const int MinFeedbackRuntimeMs = 100;
    public const int MaxFeedbackRuntimeMs = 3_600_000; // 1 hour
    public const int MaxCapturesPerSecond = 120;

    /// <summary>Smart Predictive feedback loops: largest lead prediction (seconds) and dead zone (px).</summary>
    public const double MaxLeadPredictionSeconds = 1.0;
    public const int MaxDeadzonePx = 200;
    public const int MaxInputChangeMs = 5_000;
    public const int MaxLostTimeoutMs = 60_000;
    public const int MaxTapMs = 1_000;
    public const int MaxDeadBandPercent = 25;
    public const int MinBarLength = 10;              // pixels along the bar

    // Wait for screen change / Screenshot steps.
    public const double MinChangedPercent = 0.01;
    public const long LargeWatchAreaPixels = 4_000_000; // about 2560×1600
    public const int MaxFolderPathLength = 240;
    public const long LargeFeedbackAreaPixels = 250_000; // bigger areas make each frame slow

    // v1.2 "Brain": variables, watchers, failure policies & sub-macros.
    public const int MaxVariables = 200;
    public const int MaxWatchers = 32;
    public const int MaxRetryCount = 20;
    public const int MaxVariableBindings = 64;

    // v1.4 "Reflex": per-macro triggers.
    public const int MaxTriggers = MacroTrigger.MaxTriggersPerMacro;

    /// <summary>Longest stored "what you clicked" picture (Base64 characters). A 56×56 thumbnail is about 10,000.</summary>
    public const int MaxThumbnailLength = 1_000_000;

    /// <summary>Smart-Aim accuracy limits (0.5 = 50 % similar … 1.0 = identical).</summary>
    public const double MinSmartAimAccuracy = 0.5;

    public const double MaxSmartAimAccuracy = 1.0;

    /// <summary>Smart-Aim search distance limits (px each way).</summary>
    public const int MinSmartAimRadiusPx = 0;

    public const int MaxSmartAimRadiusPx = 2_000;

    public static bool HasErrors(this IEnumerable<ValidationIssue> issues) =>
        issues.Any(i => i.Severity == IssueSeverity.Error);

    /// <summary>Checks whether a macro can be saved.</summary>
    public static IReadOnlyList<ValidationIssue> Validate(Macro macro, ScreenBounds? screen = null)
    {
        ArgumentNullException.ThrowIfNull(macro);
        var issues = new List<ValidationIssue>();

        if (macro.Id == Guid.Empty)
            issues.Add(Error("The macro has no ID."));

        var name = macro.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
            issues.Add(Error("The macro needs a name.", property: nameof(Macro.Name)));
        else if (name.Length > MaxNameLength)
            issues.Add(Error($"The name can be at most {MaxNameLength} characters.", property: nameof(Macro.Name)));
        else if (name.Any(char.IsControl))
            issues.Add(Error("The name can't contain line breaks or control characters.", property: nameof(Macro.Name)));

        if (macro.Description is { Length: > MaxDescriptionLength })
            issues.Add(Error($"The description can be at most {MaxDescriptionLength} characters.", property: nameof(Macro.Description)));

        if (macro.RepeatCount < 0 || macro.RepeatCount > MaxRepeatCount)
            issues.Add(Error($"Repeat count must be 0 (until stopped) to {MaxRepeatCount:N0}.", property: nameof(Macro.RepeatCount)));

        if (macro.RepeatDelayMs < 0 || macro.RepeatDelayMs > MaxDelayMs)
            issues.Add(Error($"The wait between repeats must be 0 to {MaxDelayMs:N0} ms.", property: nameof(Macro.RepeatDelayMs)));

        if (double.IsNaN(macro.Speed) || macro.Speed < MinSpeed || macro.Speed > MaxSpeed)
            issues.Add(Error($"Speed must be between {MinSpeed} and {MaxSpeed}.", property: nameof(Macro.Speed)));

        ValidateMacroSettings(issues, macro);

        if (macro.Steps is null)
        {
            issues.Add(Error("The step list is missing."));
            return issues;
        }

        if (macro.Steps.Count > MaxSteps)
            issues.Add(Error($"A macro can have at most {MaxSteps:N0} steps."));

        if (macro.Steps.Count == 0)
            issues.Add(Warning("This macro has no steps yet."));

        var seenIds = new HashSet<Guid>();
        var topLevelCheckpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenCheckpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blockDepth = 0;
        var hasFindTextBefore = false;
        for (var i = 0; i < macro.Steps.Count; i++)
        {
            var step = macro.Steps[i];
            if (step is null)
            {
                issues.Add(new ValidationIssue(IssueSeverity.Error, "This step is empty.", i));
                continue;
            }

            if (step is EndIfStep or EndRepeatStep && blockDepth > 0)
                blockDepth--;

            if (step is CheckpointStep cp)
            {
                if (blockDepth > 0)
                {
                    issues.Add(new ValidationIssue(IssueSeverity.Error,
                        "Checkpoints must be placed at the top level (outside If and Repeat blocks) so jumping to a checkpoint never leaves a block open.",
                        i, step.Id, nameof(CheckpointStep.Name)));
                }
                else if (CheckpointStep.IsValidName(cp.Name))
                {
                    var trimmedCp = cp.Name.Trim();
                    if (!seenCheckpoints.Add(trimmedCp))
                    {
                        issues.Add(new ValidationIssue(IssueSeverity.Error,
                            $"Duplicate checkpoint name \"{trimmedCp}\". Each checkpoint in a macro needs a unique name.",
                            i, step.Id, nameof(CheckpointStep.Name)));
                    }
                    else
                    {
                        topLevelCheckpoints.Add(trimmedCp);
                    }
                }
            }

            if (step is CallMacroStep selfCall && selfCall.TargetMacroId != Guid.Empty && selfCall.TargetMacroId == macro.Id)
            {
                issues.Add(new ValidationIssue(IssueSeverity.Error,
                    "A macro cannot call itself directly.", i, step.Id, nameof(CallMacroStep.TargetMacroId)));
            }

            if (step is MouseClickStep { UseLastFoundText: true } && !hasFindTextBefore)
                issues.Add(new ValidationIssue(IssueSeverity.Warning,
                    "This click uses the last found spot, but there's no \"Find text\", \"Find image\" or \"Find color\" step (or If / Repeat condition) before it, so when played it's skipped (it never clicks a default spot).", i, step.Id, nameof(MouseClickStep.UseLastFoundText)));
            if (step is MouseMoveStep { TargetMode: ClickTargetMode.LastFoundMatch } && !hasFindTextBefore)
                issues.Add(new ValidationIssue(IssueSeverity.Warning,
                    "This move goes to the last found spot, but there's no \"Find text\", \"Find image\" or \"Find color\" step (or If / Repeat condition) before it, so when played it's skipped.", i, step.Id, nameof(MouseMoveStep.TargetMode)));
            if (step is SetVariableStep { Source: VariableSourceKind.LastFoundX or VariableSourceKind.LastFoundY } && !hasFindTextBefore)
                issues.Add(new ValidationIssue(IssueSeverity.Warning,
                    "This variable reads the last found X/Y, but no screen search appears before it.", i, step.Id, nameof(SetVariableStep.Source)));
            if (step is ScreenSearchStep { Enabled: true, Goal: WaitGoal.Appear }) hasFindTextBefore = true;
            if (step.Enabled && WorkflowSteps.ConditionsOf(step).Any(c => c.Search is not null)) hasFindTextBefore = true; // conditions remember what they saw

            if (step is ScreenSearchStep search)
            {
                var hasLaterEnabledStep = macro.Steps.Skip(i + 1).Any(later => later is { Enabled: true } && later is not (ElseStep or EndIfStep or EndRepeatStep));
                if (!search.Enabled && hasLaterEnabledStep)
                    issues.Add(new ValidationIssue(IssueSeverity.Warning,
                        "This Find step is turned off, so the steps after it run without waiting for anything. Turn it on, or use an If step to guard them.", i, step.Id, nameof(MacroStep.Enabled)));
                else if (search.Enabled && search.EffectiveFailureAction == StepFailureAction.SkipNextStep
                         && (i + 1 >= macro.Steps.Count || macro.Steps[i + 1] is ElseStep or EndIfStep or EndRepeatStep))
                    issues.Add(new ValidationIssue(IssueSeverity.Warning,
                        "\"Skip the next step\" has nothing to skip here: there's no step right after this one in the same block.", i, step.Id, nameof(ScreenSearchStep.WhenNotFound)));
            }

            if (step.Id == Guid.Empty || !seenIds.Add(step.Id))
                issues.Add(new ValidationIssue(IssueSeverity.Error, "This step has a missing or duplicate ID.", i, step.Id));

            foreach (var issue in ValidateStep(step, screen))
                issues.Add(issue with { StepIndex = i, StepId = step.Id });

            if (step is IfStep or RepeatStep)
                blockDepth++;
        }

        // Cross-check RestartFromCheckpoint targets against top-level checkpoints.
        for (var i = 0; i < macro.Steps.Count; i++)
        {
            var step = macro.Steps[i];
            if (step is null) continue;
            var policy = step switch
            {
                ScreenSearchStep s => s.FailurePolicy,
                WaitForChangeStep w => w.FailurePolicy,
                FocusWindowStep f => f.FailurePolicy,
                _ => null,
            };
            if (policy is { Action: StepFailureAction.RestartFromCheckpoint, CheckpointName: { } cpTarget }
                && CheckpointStep.IsValidName(cpTarget)
                && !topLevelCheckpoints.Contains(cpTarget.Trim()))
            {
                issues.Add(new ValidationIssue(IssueSeverity.Error,
                    $"Checkpoint \"{cpTarget.Trim()}\" does not exist at the top level of this macro.",
                    i, step.Id, nameof(StepFailurePolicy.CheckpointName)));
            }
        }

        ValidateWatchers(issues, macro, topLevelCheckpoints, screen);
        ValidateTriggers(issues, macro);
        ValidateBlocks(issues, macro.Steps);
        return issues;
    }

    /// <summary>Target window / coordinate scaling and Humanize (macro settings added in format 11).</summary>
    private static void ValidateMacroSettings(List<ValidationIssue> issues, Macro macro)
    {
        if (macro.TargetWindowTitle is { } title && (title.Trim().Length == 0 || title.Length > SearchArea.MaxWindowTitleLength))
            issues.Add(Error($"The target window title must be 1 to {SearchArea.MaxWindowTitleLength} characters (or choose \"Any / main screen\").",
                property: nameof(Macro.TargetWindowTitle)));

        if (macro.ProfileName is { } profile && (profile.Trim().Length == 0 || profile.Trim().Length > MacroProfileRule.MaxProfileNameLength))
            issues.Add(Error($"The profile name must be 1 to {MacroProfileRule.MaxProfileNameLength} characters (or leave it empty for Global).",
                property: nameof(Macro.ProfileName)));

        if (macro.ScaleCoordinatesToWindow)
        {
            if (macro.ReferenceWidth <= 0 || macro.ReferenceHeight <= 0 || macro.ReferenceWidth > 100_000 || macro.ReferenceHeight > 100_000)
                issues.Add(Error("\"Auto-adjust coordinates\" needs the reference size (the window or screen size the positions were picked on). Click \"Use current size\" in the macro settings.",
                    property: nameof(Macro.ReferenceWidth)));
            else if (Math.Abs((long)macro.ReferenceLeft) > 100_000 || Math.Abs((long)macro.ReferenceTop) > 100_000)
                issues.Add(Error("The reference position is far outside any screen. Click \"Use current size\" in the macro settings.",
                    property: nameof(Macro.ReferenceLeft)));
        }

        if (macro.ClickJitterPx is < 0 or > Macro.MaxClickJitterPx)
            issues.Add(Error($"Humanize click jitter must be 0 to {Macro.MaxClickJitterPx} px.", property: nameof(Macro.ClickJitterPx)));
        if (macro.DelayJitterPercent is < 0 or > Macro.MaxDelayJitterPercent)
            issues.Add(Error($"Humanize timing variation must be 0 to {Macro.MaxDelayJitterPercent} %.", property: nameof(Macro.DelayJitterPercent)));

        if (macro.Variables is { } variables)
        {
            if (variables.Count > MaxVariables)
            {
                issues.Add(Error($"A macro can declare at most {MaxVariables} variables.", nameof(Macro.Variables)));
            }
            else
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var v in variables)
                {
                    if (v is null)
                    {
                        issues.Add(Error("A variable entry is empty.", nameof(Macro.Variables)));
                        continue;
                    }

                    if (!MacroVariableDefinition.IsValidName(v.Name))
                    {
                        issues.Add(Error($"Invalid variable name \"{v.Name}\". Start with a letter or underscore and use letters, digits, or underscores (up to {MacroVariableDefinition.MaxNameLength} chars).", nameof(Macro.Variables)));
                    }
                    else if (!names.Add(v.Name.Trim()))
                    {
                        issues.Add(Error($"Duplicate variable name \"{v.Name.Trim()}\".", nameof(Macro.Variables)));
                    }

                    if (!Enum.IsDefined(v.Type))
                    {
                        issues.Add(Error($"Variable \"{v.Name}\" has an unknown type.", nameof(Macro.Variables)));
                    }
                    else if ((v.DefaultValue?.Length ?? 0) > MacroVariableDefinition.MaxValueLength)
                    {
                        issues.Add(Error($"Variable \"{v.Name}\" default value is too long.", nameof(Macro.Variables)));
                    }
                    else if (!string.IsNullOrWhiteSpace(v.DefaultValue))
                    {
                        var def = v.DefaultValue.Trim();
                        if (v.Type == MacroVariableType.Number && !double.TryParse(def, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out _))
                            issues.Add(Error($"Variable \"{v.Name}\" is a Number, so its default \"{def}\" must be numeric.", nameof(Macro.Variables)));
                        else if (v.Type == MacroVariableType.Boolean && !bool.TryParse(def, out _) && def is not ("0" or "1"))
                            issues.Add(Error($"Variable \"{v.Name}\" is a Boolean, so its default must be true or false.", nameof(Macro.Variables)));
                    }
                }
            }
        }
    }

    private static void ValidateWatchers(
        List<ValidationIssue> issues,
        Macro macro,
        HashSet<string> topLevelCheckpoints,
        ScreenBounds? screen)
    {
        if (macro.Watchers is not { Count: > 0 } watchers) return;
        if (watchers.Count > MaxWatchers)
        {
            issues.Add(Error($"A macro can have at most {MaxWatchers} watchers.", nameof(Macro.Watchers)));
            return;
        }

        var seenWatcherIds = new HashSet<Guid>();
        for (var i = 0; i < watchers.Count; i++)
        {
            var w = watchers[i];
            var label = $"Watcher #{i + 1}";
            if (w is null)
            {
                issues.Add(Error($"{label} is empty.", nameof(Macro.Watchers)));
                continue;
            }

            if (!string.IsNullOrWhiteSpace(w.Name))
                label = $"Watcher \"{w.Name.Trim()}\"";

            if (w.Id == Guid.Empty || !seenWatcherIds.Add(w.Id))
                issues.Add(Error($"{label} has a missing or duplicate ID.", nameof(Macro.Watchers)));

            if (string.IsNullOrWhiteSpace(w.Name) || w.Name.Trim().Length > MaxNameLength)
                issues.Add(Error($"{label}: enter a watcher name up to {MaxNameLength} characters.", nameof(Macro.Watchers)));

            if (w.CheckIntervalMs < MacroWatcher.MinCheckIntervalMs || w.CheckIntervalMs > MaxCheckIntervalMs)
                issues.Add(Error($"{label}: check interval must be {MacroWatcher.MinCheckIntervalMs} to {MaxCheckIntervalMs:N0} ms.", nameof(Macro.Watchers)));

            if (w.CooldownMs < 0 || w.CooldownMs > MacroWatcher.MaxCooldownMs)
                issues.Add(Error($"{label}: cooldown must be 0 to {MacroWatcher.MaxCooldownMs:N0} ms.", nameof(Macro.Watchers)));

            ValidateCondition(issues, w.Condition, screen, label, nameof(Macro.Watchers));

            if (!Enum.IsDefined(w.Action))
            {
                issues.Add(Error($"{label}: unknown watcher action.", nameof(Macro.Watchers)));
                continue;
            }

            switch (w.Action)
            {
                case WatcherActionKind.ClickFoundSpot when w.Condition?.Kind == ConditionKind.VariableCompare:
                    issues.Add(Error($"{label}: \"Click found spot\" requires a screen condition (text, image, or color).", nameof(Macro.Watchers)));
                    break;
                case WatcherActionKind.RunSubMacro:
                    if ((w.HandlerMacroId is null || w.HandlerMacroId == Guid.Empty) && string.IsNullOrWhiteSpace(w.HandlerMacroName))
                        issues.Add(Error($"{label}: choose a sub-macro to run when triggered.", nameof(Macro.Watchers)));
                    else if (w.HandlerMacroId is { } hId && hId != Guid.Empty && hId == macro.Id)
                        issues.Add(Error($"{label}: a watcher cannot run the same macro it belongs to.", nameof(Macro.Watchers)));
                    break;
                case WatcherActionKind.RestartFromCheckpoint:
                    if (w.CheckpointName is not { } wCp || !CheckpointStep.IsValidName(wCp))
                        issues.Add(Error($"{label}: enter the checkpoint name to restart from.", nameof(Macro.Watchers)));
                    else if (!topLevelCheckpoints.Contains(wCp.Trim()))
                        issues.Add(Error($"{label}: checkpoint \"{wCp.Trim()}\" does not exist at the top level of this macro.", nameof(Macro.Watchers)));
                    break;
            }
        }
    }

    private static void ValidateTriggers(List<ValidationIssue> issues, Macro macro)
    {
        if (macro.Triggers is not { Count: > 0 } triggers) return;
        if (triggers.Count > MaxTriggers)
        {
            issues.Add(Error($"A macro can have at most {MaxTriggers} triggers.", nameof(Macro.Triggers)));
            return;
        }

        var seenIds = new HashSet<Guid>();
        var seenHotkeys = new HashSet<HotkeyBinding>();
        for (var i = 0; i < triggers.Count; i++)
        {
            var t = triggers[i];
            var label = $"Trigger #{i + 1}";
            if (t is null)
            {
                issues.Add(Error($"{label} is empty.", nameof(Macro.Triggers)));
                continue;
            }

            if (!string.IsNullOrWhiteSpace(t.Name))
                label = $"Trigger \"{t.Name.Trim()}\"";

            if (t.Id == Guid.Empty || !seenIds.Add(t.Id))
                issues.Add(Error($"{label} has a missing or duplicate ID.", nameof(Macro.Triggers)));

            if (string.IsNullOrWhiteSpace(t.Name) || t.Name.Trim().Length > MaxNameLength)
                issues.Add(Error($"{label}: enter a trigger name up to {MaxNameLength} characters.", nameof(Macro.Triggers)));

            if (!Enum.IsDefined(t.Kind) || !Enum.IsDefined(t.ConflictPolicy))
            {
                issues.Add(Error($"{label}: unknown trigger option.", nameof(Macro.Triggers)));
                continue;
            }

            if (t.CooldownMs < 0 || t.CooldownMs > MacroTrigger.MaxCooldownMs)
                issues.Add(Error($"{label}: cooldown must be 0 to {MacroTrigger.MaxCooldownMs:N0} ms.", nameof(Macro.Triggers)));

            if (t.OnlyWhenWindowActive && (string.IsNullOrWhiteSpace(t.WindowTitle) || t.WindowTitle.Trim().Length > SearchArea.MaxWindowTitleLength))
                issues.Add(Error($"{label}: enter the active window title to filter by (1 to {SearchArea.MaxWindowTitleLength} characters).", nameof(Macro.Triggers)));

            switch (t.Kind)
            {
                case TriggerKind.Hotkey:
                    if (!Enum.IsDefined(t.HotkeyMode))
                        issues.Add(Error($"{label}: unknown hotkey trigger mode.", nameof(Macro.Triggers)));
                    if (t.Hotkey is null)
                        issues.Add(Error($"{label}: choose a shortcut key.", nameof(Macro.Triggers)));
                    else if (t.Hotkey.Problem() is { } hkProblem)
                        issues.Add(Error($"{label}: {hkProblem}", nameof(Macro.Triggers)));
                    else if (t.Enabled && !seenHotkeys.Add(t.Hotkey))
                        issues.Add(Warning($"{label}: shortcut {t.Hotkey} is used more than once on this macro.", nameof(Macro.Triggers)));
                    break;

                case TriggerKind.Schedule:
                    if (!Enum.IsDefined(t.Recurrence))
                    {
                        issues.Add(Error($"{label}: unknown schedule recurrence.", nameof(Macro.Triggers)));
                        break;
                    }
                    if (t.Recurrence == ScheduleRecurrence.EveryNMinutes
                        && (t.IntervalMinutes < MacroTrigger.MinIntervalMinutes || t.IntervalMinutes > MacroTrigger.MaxIntervalMinutes))
                    {
                        issues.Add(Error($"{label}: interval must be {MacroTrigger.MinIntervalMinutes} to {MacroTrigger.MaxIntervalMinutes:N0} minutes.", nameof(Macro.Triggers)));
                    }
                    else if (t.Recurrence == ScheduleRecurrence.DailyAtTime && !MacroTrigger.TryParseTimeOfDay(t.TimeOfDay, out _, out _))
                    {
                        issues.Add(Error($"{label}: enter the daily time as 24-hour HH:mm (for example 09:00 or 18:30).", nameof(Macro.Triggers)));
                    }
                    break;

                case TriggerKind.WindowEvent:
                    if (!Enum.IsDefined(t.WindowEvent))
                        issues.Add(Error($"{label}: unknown window event.", nameof(Macro.Triggers)));
                    if (string.IsNullOrWhiteSpace(t.WindowTitle) || t.WindowTitle.Trim().Length > SearchArea.MaxWindowTitleLength)
                        issues.Add(Error($"{label}: enter (part of) the window title to watch for (1 to {SearchArea.MaxWindowTitleLength} characters).", nameof(Macro.Triggers)));
                    break;
            }
        }
    }

    /// <summary>
    /// Validates cross-macro sub-macro links, detecting missing target macros, call cycles (A → B → A),
    /// and nesting deeper than <see cref="CallMacroStep.MaxCallDepth"/>.
    /// </summary>
    public static IReadOnlyList<ValidationIssue> ValidateCallGraph(Macro root, IReadOnlyList<Macro> library)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(library);
        var issues = new List<ValidationIssue>();

        void Traverse(Macro current, List<Guid> chainIds, List<string> chainNames, int depth, int? rootStepIndex, Guid? rootStepId)
        {
            if (depth > CallMacroStep.MaxCallDepth)
            {
                issues.Add(new ValidationIssue(
                    IssueSeverity.Error,
                    $"Sub-macro nesting exceeds the maximum depth of {CallMacroStep.MaxCallDepth}.",
                    rootStepIndex,
                    rootStepId,
                    nameof(CallMacroStep.TargetMacroId)));
                return;
            }

            if (current.Steps is { } steps)
            {
                for (var i = 0; i < steps.Count; i++)
                {
                    var step = steps[i];
                    if (step is null) continue;
                    var stepIdx = depth == 0 ? i : rootStepIndex;
                    var stepId = depth == 0 ? step.Id : rootStepId;

                    switch (step)
                    {
                        case CallMacroStep call when call.TargetMacroId != Guid.Empty || !string.IsNullOrWhiteSpace(call.TargetMacroName):
                            CheckTarget(call.TargetMacroId, call.TargetMacroName, chainIds, chainNames, depth, stepIdx, stepId, nameof(CallMacroStep.TargetMacroId));
                            break;
                        case ScreenSearchStep { FailurePolicy: { Action: StepFailureAction.RunRecoveryMacro } p }:
                            CheckTarget(p.RecoveryMacroId ?? Guid.Empty, p.RecoveryMacroName, chainIds, chainNames, depth, stepIdx, stepId, nameof(StepFailurePolicy.RecoveryMacroId));
                            break;
                        case WaitForChangeStep { FailurePolicy: { Action: StepFailureAction.RunRecoveryMacro } p }:
                            CheckTarget(p.RecoveryMacroId ?? Guid.Empty, p.RecoveryMacroName, chainIds, chainNames, depth, stepIdx, stepId, nameof(StepFailurePolicy.RecoveryMacroId));
                            break;
                        case FocusWindowStep { FailurePolicy: { Action: StepFailureAction.RunRecoveryMacro } p }:
                            CheckTarget(p.RecoveryMacroId ?? Guid.Empty, p.RecoveryMacroName, chainIds, chainNames, depth, stepIdx, stepId, nameof(StepFailurePolicy.RecoveryMacroId));
                            break;
                    }
                }
            }

            if (current.Watchers is { } watchers)
            {
                foreach (var w in watchers)
                {
                    if (w is { Action: WatcherActionKind.RunSubMacro }
                        && ((w.HandlerMacroId is { } hId && hId != Guid.Empty) || !string.IsNullOrWhiteSpace(w.HandlerMacroName)))
                    {
                        CheckTarget(w.HandlerMacroId ?? Guid.Empty, w.HandlerMacroName, chainIds, chainNames, depth, rootStepIndex, rootStepId, nameof(Macro.Watchers));
                    }
                }
            }
        }

        void CheckTarget(Guid targetId, string? targetName, List<Guid> chainIds, List<string> chainNames, int depth, int? stepIdx, Guid? stepId, string prop)
        {
            var target = MacroFileFormat.ResolveMacro(library, targetId, targetName);
            var display = !string.IsNullOrWhiteSpace(targetName) ? targetName.Trim() : targetId.ToString();
            if (target is null)
            {
                issues.Add(new ValidationIssue(
                    IssueSeverity.Error,
                    $"Sub-macro \"{display}\" was not found in the library.",
                    stepIdx,
                    stepId,
                    prop));
                return;
            }

            if (chainIds.Contains(target.Id))
            {
                var cyclePath = string.Join(" → ", chainNames.Append(target.Name));
                issues.Add(new ValidationIssue(
                    IssueSeverity.Error,
                    $"Sub-macro call cycle detected: {cyclePath}.",
                    stepIdx,
                    stepId,
                    prop));
                return;
            }

            var nextIds = new List<Guid>(chainIds) { target.Id };
            var nextNames = new List<string>(chainNames) { target.Name };
            Traverse(target, nextIds, nextNames, depth + 1, stepIdx, stepId);
        }

        Traverse(root, [root.Id], [string.IsNullOrWhiteSpace(root.Name) ? "Macro" : root.Name], 0, null, null);
        return issues;
    }

    /// <summary>
    /// If / Else / End If and Repeat / End Repeat must match up and nest properly (at most
    /// <see cref="BlockStructure.MaxDepth"/> levels). Also hints about empty blocks and disabled markers.
    /// </summary>
    private static void ValidateBlocks(List<ValidationIssue> issues, List<MacroStep> steps)
    {
        if (!steps.Any(WorkflowSteps.IsBlockStep)) return;

        var blocks = BlockStructure.Analyze(steps);
        foreach (var problem in blocks.Problems)
            issues.Add(new ValidationIssue(IssueSeverity.Error, problem.Message, problem.StepIndex, steps[problem.StepIndex]?.Id, "Block"));
        if (!blocks.IsValid) return;

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            switch (step)
            {
                case ElseStep or EndIfStep or EndRepeatStep when !step.Enabled:
                    issues.Add(new ValidationIssue(IssueSeverity.Warning,
                        "Turning off an Else / End step has no effect. To skip a whole block, turn off its If or Repeat step.", i, step.Id, nameof(MacroStep.Enabled)));
                    break;

                case RepeatStep { Enabled: true } when !HasEnabledStepBetween(steps, i, blocks.EndOf(i)):
                    issues.Add(new ValidationIssue(IssueSeverity.Warning,
                        "This Repeat block has no enabled steps inside, so it only waits.", i, step.Id, "Block"));
                    break;

                case IfStep { Enabled: true } when !HasEnabledStepBetween(steps, i, blocks.EndOf(i)):
                    issues.Add(new ValidationIssue(IssueSeverity.Warning,
                        "This If block has no enabled steps inside, so it does nothing.", i, step.Id, "Block"));
                    break;
            }
        }
    }

    /// <summary>True if a normal (non-marker) enabled step sits between the two indexes.</summary>
    private static bool HasEnabledStepBetween(List<MacroStep> steps, int start, int end)
    {
        for (var i = start + 1; i < end; i++)
        {
            if (steps[i] is { Enabled: true } s && !WorkflowSteps.IsBlockStep(s)) return true;
        }

        return false;
    }

    /// <summary>
    /// Stricter check used before playback: everything from Validate, plus the macro must actually do something.
    /// </summary>
    public static IReadOnlyList<ValidationIssue> ValidateForExecution(Macro macro, ScreenBounds? screen = null)
    {
        var issues = Validate(macro, screen).ToList();
        if (macro.Steps is { } steps && !steps.Any(s => s is { Enabled: true }))
            issues.Add(Error("The macro has no enabled steps to run."));
        return issues;
    }
}
