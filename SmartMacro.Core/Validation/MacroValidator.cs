using SmartMacro.Core.Input;
using SmartMacro.Core.Models;
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
/// </summary>
public static class MacroValidator
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
    public const int MaxTemplateSize = 400;          // pixels, each side
    public const int MaxTemplateDataLength = 1_500_000; // characters of Base64
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
    public const int MaxInputChangeMs = 5_000;
    public const int MaxLostTimeoutMs = 60_000;
    public const int MaxTapMs = 1_000;
    public const int MaxDeadBandPercent = 25;
    public const int MinBarLength = 10;              // pixels along the bar
    public const long LargeFeedbackAreaPixels = 250_000; // bigger areas make each frame slow

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
        var hasFindTextBefore = false;
        for (var i = 0; i < macro.Steps.Count; i++)
        {
            var step = macro.Steps[i];
            if (step is null)
            {
                issues.Add(new ValidationIssue(IssueSeverity.Error, "This step is empty.", i));
                continue;
            }

            if (step is MouseClickStep { UseLastFoundText: true } && !hasFindTextBefore)
                issues.Add(new ValidationIssue(IssueSeverity.Warning,
                    "This click uses the last found spot, but there's no \"Find text\", \"Find image\" or \"Find color\" step (or If / Repeat condition) before it.", i, step.Id, nameof(MouseClickStep.UseLastFoundText)));
            if (step is ScreenSearchStep { Enabled: true, Goal: WaitGoal.Appear }) hasFindTextBefore = true;
            if (step.Enabled && WorkflowSteps.ConditionsOf(step).Any()) hasFindTextBefore = true; // conditions remember what they saw

            if (step.Id == Guid.Empty || !seenIds.Add(step.Id))
                issues.Add(new ValidationIssue(IssueSeverity.Error, "This step has a missing or duplicate ID.", i, step.Id));

            foreach (var issue in ValidateStep(step, screen))
                issues.Add(issue with { StepIndex = i, StepId = step.Id });
        }

        ValidateBlocks(issues, macro.Steps);
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

    /// <summary>Checks a single step's own fields (no index information).</summary>
    public static IReadOnlyList<ValidationIssue> ValidateStep(MacroStep step, ScreenBounds? screen = null)
    {
        ArgumentNullException.ThrowIfNull(step);
        var issues = new List<ValidationIssue>();

        if (step.Comment is { Length: > MaxCommentLength })
            issues.Add(Error($"The comment can be at most {MaxCommentLength} characters.", nameof(MacroStep.Comment)));

        switch (step)
        {
            case DelayStep d:
                if (d.DurationMs < 0)
                    issues.Add(Error("The delay can't be negative.", nameof(DelayStep.DurationMs)));
                else if (d.DurationMs > MaxDelayMs)
                    issues.Add(Error($"The delay can be at most {MaxDelayMs:N0} ms (1 hour).", nameof(DelayStep.DurationMs)));
                break;

            case MouseMoveStep m:
                CheckPosition(issues, m.X, m.Y, screen);
                break;

            case MouseClickStep c:
                if (!Enum.IsDefined(c.Button))
                    issues.Add(Error("Unknown mouse button.", nameof(MouseClickStep.Button)));
                if (!Enum.IsDefined(c.Action))
                    issues.Add(Error("Unknown click action.", nameof(MouseClickStep.Action)));
                if (!c.UseCurrentPosition && !c.UseLastFoundText)
                    CheckPosition(issues, c.X, c.Y, screen);
                CheckHold(issues, c.HoldMs, c.Action == MouseClickAction.Click, nameof(MouseClickStep.HoldMs), "Click");
                break;

            case MouseScrollStep s:
                if (s.Delta == 0)
                    issues.Add(Error("The scroll amount can't be 0.", nameof(MouseScrollStep.Delta)));
                else if (Math.Abs((long)s.Delta) > MaxScrollDelta)
                    issues.Add(Error($"The scroll amount must be between -{MaxScrollDelta} and {MaxScrollDelta}.", nameof(MouseScrollStep.Delta)));
                if (!s.UseCurrentPosition)
                    CheckPosition(issues, s.X, s.Y, screen);
                break;

            case KeyPressStep k:
                if (!VirtualKeys.IsValid(k.VirtualKey))
                    issues.Add(Error("Choose a valid key.", nameof(KeyPressStep.VirtualKey)));
                if (!Enum.IsDefined(k.Action))
                    issues.Add(Error("Unknown key action.", nameof(KeyPressStep.Action)));
                const KeyModifiers allModifiers = KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Windows;
                if ((k.Modifiers & ~allModifiers) != 0)
                    issues.Add(Error("Unknown modifier key.", nameof(KeyPressStep.Modifiers)));
                else if (k.Modifiers != KeyModifiers.None && k.Action != KeyAction.Press)
                    issues.Add(Warning("Modifier keys are only used with the Press action.", nameof(KeyPressStep.Modifiers)));
                CheckHold(issues, k.HoldMs, k.Action == KeyAction.Press, nameof(KeyPressStep.HoldMs), "Press");
                break;

            case InputSequenceStep q:
                ValidateSequence(issues, q);
                break;

            case TypeTextStep t:
                if (string.IsNullOrEmpty(t.Text))
                    issues.Add(Error("Enter the text to type.", nameof(TypeTextStep.Text)));
                else if (t.Text.Length > MaxTextLength)
                    issues.Add(Error($"The text can be at most {MaxTextLength:N0} characters.", nameof(TypeTextStep.Text)));
                if (t.CharacterDelayMs < 0 || t.CharacterDelayMs > MaxCharacterDelayMs)
                    issues.Add(Error($"The delay between characters must be 0 to {MaxCharacterDelayMs:N0} ms.", nameof(TypeTextStep.CharacterDelayMs)));
                break;

            case FindTextStep f:
                if (string.IsNullOrWhiteSpace(f.Text))
                    issues.Add(Error("Enter the text to look for.", nameof(FindTextStep.Text)));
                else if (f.Text.Length > MaxSearchTextLength)
                    issues.Add(Error($"The search text can be at most {MaxSearchTextLength} characters.", nameof(FindTextStep.Text)));
                if (f.MinSimilarityPercent < 50 || f.MinSimilarityPercent > 100)
                    issues.Add(Error("Match accuracy must be between 50% and 100%.", nameof(FindTextStep.MinSimilarityPercent)));
                if (!Enum.IsDefined(f.MatchMode))
                    issues.Add(Error("This step has an unknown option value."));
                ValidateSearch(issues, f, screen, MinCheckIntervalMs);
                break;

            case FindImageStep img:
                ValidateTemplate(issues, img);
                if (img.MinSimilarityPercent < 50 || img.MinSimilarityPercent > 100)
                    issues.Add(Error("Match accuracy must be between 50% and 100%.", nameof(FindImageStep.MinSimilarityPercent)));
                else if (img.MinSimilarityPercent < 70)
                    issues.Add(Warning("Match accuracy below 70% often matches the wrong thing. Test it with \"Test now\".", nameof(FindImageStep.MinSimilarityPercent)));
                ValidateSearch(issues, img, screen, MinVisionCheckIntervalMs);
                if (!img.SearchFullScreen && img.TemplateWidth > 0
                    && (img.RegionWidth < img.TemplateWidth || img.RegionHeight < img.TemplateHeight))
                    issues.Add(Error("The search area is smaller than the image to find. Make the area bigger.", "Region"));
                break;

            case IfStep ifStep:
                ValidateCondition(issues, ifStep.Condition, screen);
                break;

            case ElseStep or EndIfStep or EndRepeatStep:
                break; // nothing to set; their place in the list is checked by ValidateBlocks

            case RepeatStep r:
                ValidateRepeat(issues, r, screen);
                break;

            case FeedbackLoopStep feedback:
                ValidateFeedbackLoop(issues, feedback, screen);
                break;

            case FindColorStep c:
                if (!RgbColor.TryParse(c.Color, out _))
                    issues.Add(Error("Enter the color as #RRGGBB (for example #FF0000 for red), or pick it from the screen.", nameof(FindColorStep.Color)));
                if (c.Tolerance < 0 || c.Tolerance > 255)
                    issues.Add(Error("Tolerance must be 0 to 255.", nameof(FindColorStep.Tolerance)));
                else if (c.Tolerance > 100)
                    issues.Add(Warning("A tolerance above 100 matches many different colors.", nameof(FindColorStep.Tolerance)));
                if (c.MinPixels < 1 || c.MinPixels > MaxColorPixels)
                    issues.Add(Error($"Minimum size must be 1 to {MaxColorPixels:N0} pixels.", nameof(FindColorStep.MinPixels)));
                ValidateSearch(issues, c, screen, MinVisionCheckIntervalMs);
                if (c.SearchFullScreen)
                    issues.Add(Warning("Searching the whole screen for a color often finds something else. Pick a small area if you can.", "Region"));
                break;

            default:
                issues.Add(Error($"Unsupported step type: {step.GetType().Name}."));
                break;
        }

        return issues;
    }

    /// <summary>
    /// Repeat: known mode; a count for Times; a condition plus a required safety limit (max turns and/or max time)
    /// for While / Until; limits in range.
    /// </summary>
    private static void ValidateRepeat(List<ValidationIssue> issues, RepeatStep r, ScreenBounds? screen)
    {
        if (!Enum.IsDefined(r.Mode) || !Enum.IsDefined(r.WhenLimitReached))
        {
            issues.Add(Error("This Repeat step has an unknown option value. The macro may be damaged."));
            return;
        }

        if (r.MaxDurationMs < 0 || r.MaxDurationMs > MaxLoopDurationMs)
            issues.Add(Error($"Max time must be 0 (no time limit) to {MaxLoopDurationMs:N0} ms (24 hours).", nameof(RepeatStep.MaxDurationMs)));

        if (r.Mode == RepeatMode.Times)
        {
            if (r.Count < 1 || r.Count > MaxLoopCount)
                issues.Add(Error($"Repeat count must be 1 to {MaxLoopCount:N0}.", nameof(RepeatStep.Count)));
            return;
        }

        if (r.MaxIterations < 0 || r.MaxIterations > MaxLoopCount)
            issues.Add(Error($"Max turns must be 0 (no turn limit) to {MaxLoopCount:N0}.", nameof(RepeatStep.MaxIterations)));
        else if (r.MaxIterations == 0 && r.MaxDurationMs == 0)
            issues.Add(Error("A Repeat while / until loop needs a safety limit: set Max turns and/or Max time, so it can't run forever.", nameof(RepeatStep.MaxIterations)));

        if (r.Condition is null)
            issues.Add(Error("Choose the condition for this loop.", nameof(RepeatStep.Condition)));
        else
            ValidateCondition(issues, r.Condition, screen);
    }

    /// <summary>
    /// A condition needs search settings of a supported kind; those are checked with the same rules as the
    /// Find text / image / color step (messages start with "Condition:").
    /// </summary>
    private static void ValidateCondition(List<ValidationIssue> issues, Condition? condition, ScreenBounds? screen,
        string label = "Condition", string property = "Condition")
    {
        if (condition?.Check is null)
        {
            issues.Add(Error($"{label}: choose what the condition looks for (text, image or color).", property));
            return;
        }

        if (condition.Search is not { } search)
        {
            issues.Add(Error($"{label}: a condition can't use a \"{condition.Check.GetType().Name}\" step. Use text, image or color. The macro may be damaged.", property));
            return;
        }

        foreach (var issue in ValidateStep(search, screen))
        {
            // The "when found" action, wait goal and "when not found" choice aren't used by conditions.
            if (issue.Property is nameof(ScreenSearchStep.Action) or nameof(ScreenSearchStep.WhenNotFound) or nameof(ScreenSearchStep.Goal)) continue;
            issues.Add(issue with { Message = label + ": " + issue.Message, Property = property + "." + (issue.Property ?? "") });
        }
    }

    /// <summary>
    /// Feedback loop (Stage 4): a bar area on screen, a valid marker colour, a zone, rules that press something,
    /// a key or button, rate limits, a required max runtime, a lost-target policy and optional stop conditions.
    /// Every number has a range, so a damaged or hand-edited file can't make the loop run wild.
    /// </summary>
    private static void ValidateFeedbackLoop(List<ValidationIssue> issues, FeedbackLoopStep f, ScreenBounds? screen)
    {
        if (!Enum.IsDefined(f.Orientation) || !Enum.IsDefined(f.ZoneMode) || !Enum.IsDefined(f.InputKind)
            || !Enum.IsDefined(f.WhenBeforeZone) || !Enum.IsDefined(f.WhenInsideZone) || !Enum.IsDefined(f.WhenAfterZone)
            || !Enum.IsDefined(f.WhenTargetLost) || !Enum.IsDefined(f.WhenTimeUp) || !Enum.IsDefined(f.WhenFailed))
        {
            issues.Add(Error("This Feedback loop step has an unknown option value. The macro may be damaged."));
            return;
        }

        // Observe: the bar area.
        const string region = "Region";
        if (f.RegionWidth < 5 || f.RegionHeight < 5)
            issues.Add(Error("The bar area must be at least 5×5 pixels. Use \"Pick bar area\".", region));
        else if (f.RegionWidth > MaxRegionSize || f.RegionHeight > MaxRegionSize)
            issues.Add(Error($"The bar area can be at most {MaxRegionSize:N0} pixels wide or tall.", region));
        else
        {
            var length = f.Orientation == BarOrientation.Horizontal ? f.RegionWidth : f.RegionHeight;
            if (length < MinBarLength)
                issues.Add(Error($"The bar area must be at least {MinBarLength} pixels long in the direction the marker moves. Check \"Direction\".", region));
            if ((long)f.RegionWidth * f.RegionHeight > LargeFeedbackAreaPixels)
                issues.Add(Warning("The bar area is large, so every frame takes longer. Pick only the bar itself.", region));
            if (screen is not null && !f.Region.IntersectsWith(new Ocr.ScreenRect(screen.Left, screen.Top, screen.Width, screen.Height)))
                issues.Add(Warning("The bar area is outside your current screen.", region));
        }

        // Observe: the marker.
        if (!RgbColor.TryParse(f.MarkerColor, out var marker))
            issues.Add(Error("Enter the marker color as #RRGGBB, or pick it from the screen.", nameof(FeedbackLoopStep.MarkerColor)));
        CheckTolerance(issues, f.MarkerTolerance, nameof(FeedbackLoopStep.MarkerTolerance), "marker");
        if (f.MarkerMinPixels < 1 || f.MarkerMinPixels > MaxColorPixels)
            issues.Add(Error($"The marker's minimum size must be 1 to {MaxColorPixels:N0} pixels.", nameof(FeedbackLoopStep.MarkerMinPixels)));
        if (f.MaxJumpPercent < 1 || f.MaxJumpPercent > 100)
            issues.Add(Error("The glitch filter (largest jump in one frame) must be 1 to 100 %.", nameof(FeedbackLoopStep.MaxJumpPercent)));

        // Target zone.
        if (f.ZoneMode == FeedbackZoneMode.Fixed)
        {
            if (f.ZoneStartPercent < 0 || f.ZoneEndPercent > 100 || f.ZoneStartPercent >= f.ZoneEndPercent)
                issues.Add(Error("The zone must start before it ends, between 0 % and 100 % (for example 40 to 60).", nameof(FeedbackLoopStep.ZoneStartPercent)));
            else if (f.DeadBandPercent > 0 && f.DeadBandPercent * 2 >= f.ZoneEndPercent - f.ZoneStartPercent)
                issues.Add(Warning("The dead band is as wide as half the zone or more, so the marker may never count as inside it. Use a smaller dead band.", nameof(FeedbackLoopStep.DeadBandPercent)));
        }
        else
        {
            if (!RgbColor.TryParse(f.ZoneColor, out var zone))
                issues.Add(Error("Enter the zone color as #RRGGBB, or pick it from the screen.", nameof(FeedbackLoopStep.ZoneColor)));
            else if (RgbColor.TryParse(f.MarkerColor, out _) && Overlaps(marker, f.MarkerTolerance, zone, f.ZoneTolerance))
                issues.Add(Warning("The marker and zone colors are so close (with their tolerances) that one can be mistaken for the other. Lower a tolerance or pick more different colors.", nameof(FeedbackLoopStep.ZoneColor)));
            CheckTolerance(issues, f.ZoneTolerance, nameof(FeedbackLoopStep.ZoneTolerance), "zone");
            if (f.ZoneMinPixels < 1 || f.ZoneMinPixels > MaxColorPixels)
                issues.Add(Error($"The zone's minimum size must be 1 to {MaxColorPixels:N0} pixels.", nameof(FeedbackLoopStep.ZoneMinPixels)));
        }

        // Decide.
        if (f.DeadBandPercent < 0 || f.DeadBandPercent > MaxDeadBandPercent)
            issues.Add(Error($"The dead band must be 0 to {MaxDeadBandPercent} %.", nameof(FeedbackLoopStep.DeadBandPercent)));
        var actions = new[] { f.WhenBeforeZone, f.WhenInsideZone, f.WhenAfterZone };
        if (!actions.Any(a => a is FeedbackAction.Hold or FeedbackAction.Tap))
            issues.Add(Error("None of the rules presses anything. Set at least one place to \"Hold\" or \"Tap\".", "Rules"));
        else if (actions.Contains(FeedbackAction.Hold) && !actions.Contains(FeedbackAction.Release) && !actions.Contains(FeedbackAction.Tap))
            issues.Add(Warning("A rule holds the input but no rule releases it, so it stays held until the loop ends.", "Rules"));

        // Act.
        if (f.InputKind == FeedbackInputKind.MouseButton)
        {
            if (!Enum.IsDefined(f.MouseButton))
                issues.Add(Error("Unknown mouse button.", nameof(FeedbackLoopStep.MouseButton)));
        }
        else if (!VirtualKeys.IsValid(f.VirtualKey))
        {
            issues.Add(Error("Choose a valid key.", nameof(FeedbackLoopStep.VirtualKey)));
        }
        else if (f.VirtualKey == 0x1B)
        {
            issues.Add(Warning("Esc is also SmartMacro's emergency stop key (only when you press it yourself). Another key is clearer.", nameof(FeedbackLoopStep.VirtualKey)));
        }

        if (f.TapMs < 1 || f.TapMs > MaxTapMs)
            issues.Add(Error($"The tap length must be 1 to {MaxTapMs:N0} ms.", nameof(FeedbackLoopStep.TapMs)));

        // Limits.
        if (f.MaxCapturesPerSecond < 1 || f.MaxCapturesPerSecond > MaxCapturesPerSecond)
            issues.Add(Error($"Captures per second must be 1 to {MaxCapturesPerSecond}.", nameof(FeedbackLoopStep.MaxCapturesPerSecond)));
        else if (f.MaxCapturesPerSecond > 60)
            issues.Add(Warning("More than 60 captures per second is usually faster than the screen updates; it mostly uses more CPU. Check \"Measure loop speed\".", nameof(FeedbackLoopStep.MaxCapturesPerSecond)));

        if (f.MinInputChangeMs < 0 || f.MinInputChangeMs > MaxInputChangeMs)
            issues.Add(Error($"The minimum time between input changes must be 0 to {MaxInputChangeMs:N0} ms.", nameof(FeedbackLoopStep.MinInputChangeMs)));
        else if (f.MinInputChangeMs < 16)
            issues.Add(Warning("Changes less than 16 ms apart can be faster than a game's frame, so the game may miss some.", nameof(FeedbackLoopStep.MinInputChangeMs)));

        if (f.MaxRuntimeMs < MinFeedbackRuntimeMs || f.MaxRuntimeMs > MaxFeedbackRuntimeMs)
            issues.Add(Error($"A feedback loop needs a max runtime of {MinFeedbackRuntimeMs} to {MaxFeedbackRuntimeMs:N0} ms (1 hour), so it can't run forever.", nameof(FeedbackLoopStep.MaxRuntimeMs)));

        if (f.LostTimeoutMs < 0 || f.LostTimeoutMs > MaxLostTimeoutMs)
            issues.Add(Error($"\"Target missing for\" must be 0 to {MaxLostTimeoutMs:N0} ms.", nameof(FeedbackLoopStep.LostTimeoutMs)));

        // Stop when.
        if (f.CompleteWhen is not null)
            ValidateFeedbackCondition(issues, f.CompleteWhen, screen, "Done when", nameof(FeedbackLoopStep.CompleteWhen));
        if (f.FailWhen is not null)
            ValidateFeedbackCondition(issues, f.FailWhen, screen, "Failed when", nameof(FeedbackLoopStep.FailWhen));

        if (f.CompleteWhen is null && f.WhenTargetLost == FeedbackLostAction.ReleaseAndWait && f.WhenTimeUp == LoopLimitBehavior.StopMacro)
            issues.Add(Warning("This loop can only end with an error (when its max runtime is reached). Add a \"done when\" condition, or let it continue when the time is up or the target is gone.", nameof(FeedbackLoopStep.WhenTimeUp)));
    }

    /// <summary>A stop condition of a feedback loop: the normal condition rules, plus hints about what costs time here.</summary>
    private static void ValidateFeedbackCondition(List<ValidationIssue> issues, Condition condition, ScreenBounds? screen, string label, string property)
    {
        ValidateCondition(issues, condition, screen, label, property);
        if (condition.Search is not { } search) return;

        if (search is FindTextStep)
            issues.Add(Warning($"{label}: reading text (OCR) takes about 50-300 ms, and the loop can't react while it reads. An image or color condition is much faster.", property));
        if (search.TimeoutMs > 0)
            issues.Add(Warning($"{label}: \"keep checking for\" isn't used here; the loop looks once every \"check every\" while it runs.", property));
    }

    private static void CheckTolerance(List<ValidationIssue> issues, int tolerance, string property, string what)
    {
        if (tolerance < 0 || tolerance > 255)
            issues.Add(Error($"The {what} tolerance must be 0 to 255.", property));
        else if (tolerance > 100)
            issues.Add(Warning($"A {what} tolerance above 100 matches many different colors.", property));
    }

    /// <summary>True if some colour would match both colours (their tolerance boxes overlap on every channel).</summary>
    private static bool Overlaps(RgbColor a, int toleranceA, RgbColor b, int toleranceB)
    {
        var reach = Math.Max(0, toleranceA) + Math.Max(0, toleranceB);
        return Math.Abs(a.R - b.R) <= reach && Math.Abs(a.G - b.G) <= reach && Math.Abs(a.B - b.B) <= reach;
    }

    private static void CheckHold(List<ValidationIssue> issues, int holdMs, bool applies, string property, string actionName)
    {
        if (holdMs < 0 || holdMs > MaxHoldMs)
            issues.Add(Error($"The hold time must be 0 to {MaxHoldMs:N0} ms.", property));
        else if (holdMs > 0 && !applies)
            issues.Add(Warning($"The hold time is only used with the {actionName} action.", property));
    }

    /// <summary>
    /// Checks every recorded event. A damaged event (out-of-order time, unknown key, huge movement) is an error,
    /// so a broken or hand-edited file can't make playback do something wild.
    /// </summary>
    private static void ValidateSequence(List<ValidationIssue> issues, InputSequenceStep step)
    {
        const string property = nameof(InputSequenceStep.Events);
        var events = step.Events;
        if (events is null || events.Count == 0)
        {
            issues.Add(Error("This step has no recorded input.", property));
            return;
        }

        if (events.Count > MaxSequenceEvents)
        {
            issues.Add(Error($"A recorded input step can have at most {MaxSequenceEvents:N0} events.", property));
            return;
        }

        long previous = 0;
        var keysDown = new HashSet<int>();
        var buttonsDown = new HashSet<MacroMouseButton>();
        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            string? problem = null;

            if (e.TimeUs < previous)
                problem = "is earlier than the event before it";
            else if (e.TimeUs > MaxSequenceDurationUs)
                problem = "is more than 1 hour after the start";
            else
            {
                if (e.HasPosition && (Math.Abs((long)e.X) > MaxScreenCoordinate || Math.Abs((long)e.Y) > MaxScreenCoordinate))
                    problem = "has a cursor position far outside any screen";
                else switch (e.Kind)
                {
                    case InputEventKind.MouseMove:
                        if (Math.Abs((long)e.Dx) > MaxRelativeMove || Math.Abs((long)e.Dy) > MaxRelativeMove)
                            problem = "moves the mouse too far in one go";
                        break;
                    case InputEventKind.MouseDown:
                    case InputEventKind.MouseUp:
                        if (!Enum.IsDefined(e.Button)) problem = "uses an unknown mouse button";
                        else if (e.Kind == InputEventKind.MouseDown) buttonsDown.Add(e.Button);
                        else buttonsDown.Remove(e.Button);
                        break;
                    case InputEventKind.Wheel:
                    case InputEventKind.HorizontalWheel:
                        if (e.Delta == 0 || Math.Abs((long)e.Delta) > MaxScrollDelta) problem = "has an invalid scroll amount";
                        break;
                    case InputEventKind.KeyDown:
                    case InputEventKind.KeyUp:
                        if (!VirtualKeys.IsValid(e.VirtualKey)) problem = "uses an invalid key";
                        else if (e.ScanCode is < 0 or > 0xFF) problem = "has an invalid scan code";
                        else if (e.Kind == InputEventKind.KeyDown) keysDown.Add(e.VirtualKey);
                        else keysDown.Remove(e.VirtualKey);
                        break;
                    default:
                        problem = "has an unknown type";
                        break;
                }
            }

            if (problem is not null)
            {
                issues.Add(Error($"Recorded event {i + 1:N0} {problem}. The recording may be damaged.", property));
                return; // one message is enough; a damaged list usually has many bad events
            }

            previous = e.TimeUs;
        }

        if (keysDown.Count > 0 || buttonsDown.Count > 0)
            issues.Add(Warning("Some keys or buttons are still held at the end of the recording; they'll be released when this step finishes.", property));

        ValidateMouseSegments(issues, step);
    }

    /// <summary>
    /// Smart recordings: the segments must be sorted, start at a valid time and use known modes. A cursor-position
    /// segment needs recorded positions; movement without one is always played as relative movement.
    /// </summary>
    private static void ValidateMouseSegments(List<ValidationIssue> issues, InputSequenceStep step)
    {
        const string property = nameof(InputSequenceStep.MouseSegments);
        var segments = step.MouseSegments;
        if (segments is null) return;

        if (segments.Count == 0)
        {
            issues.Add(Error("The mouse playback parts of this recording are missing. The recording may be damaged.", property));
            return;
        }

        if (segments.Count > MaxMouseSegments)
        {
            issues.Add(Error($"A recorded input step can have at most {MaxMouseSegments:N0} mouse parts.", property));
            return;
        }

        long previous = -1;
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            string? problem = null;
            if (segment is null) problem = "is empty";
            else if (i == 0 && segment.StartUs != 0) problem = "doesn't start at the beginning of the recording";
            else if (segment.StartUs <= previous) problem = "starts before the part before it ends";
            else if (segment.StartUs > MaxSequenceDurationUs) problem = "starts more than 1 hour after the beginning";
            else if (!Enum.IsDefined(segment.Detected) || !Enum.IsDefined(segment.Choice) || !Enum.IsDefined(segment.Evidence))
                problem = "has an unknown option value";

            if (problem is not null)
            {
                issues.Add(Error($"Mouse part {i + 1:N0} {problem}. The recording may be damaged.", property));
                return;
            }

            previous = segment!.StartUs;
        }

        if (segments.Any(s => s.Effective == MouseMovementMode.CursorPosition)
            && step.Events is { } events && events.Any(e => e.Kind == InputEventKind.MouseMove && !e.HasPosition))
            issues.Add(Warning("Some mouse movement has no recorded cursor position; it's played as relative movement.", property));
    }

    /// <summary>Shared checks for Find text / image / color: area, timing, goal, action, offsets.</summary>
    private static void ValidateSearch(List<ValidationIssue> issues, ScreenSearchStep f, ScreenBounds? screen, int minInterval)
    {
        if (!f.SearchFullScreen)
        {
            if (f.RegionWidth < 5 || f.RegionHeight < 5)
                issues.Add(Error("The search area must be at least 5×5 pixels.", "Region"));
            else if (f.RegionWidth > MaxRegionSize || f.RegionHeight > MaxRegionSize)
                issues.Add(Error($"The search area can be at most {MaxRegionSize:N0} pixels wide or tall.", "Region"));
            else if (screen is not null && !new Ocr.ScreenRect(f.RegionX, f.RegionY, f.RegionWidth, f.RegionHeight)
                         .IntersectsWith(new Ocr.ScreenRect(screen.Left, screen.Top, screen.Width, screen.Height)))
                issues.Add(Warning("The search area is outside your current screen.", "Region"));
        }

        if (f.TimeoutMs < 0 || f.TimeoutMs > MaxDelayMs)
            issues.Add(Error($"The wait time must be 0 to {MaxDelayMs:N0} ms.", nameof(ScreenSearchStep.TimeoutMs)));
        if (f.CheckIntervalMs < minInterval || f.CheckIntervalMs > MaxCheckIntervalMs)
            issues.Add(Error($"Check every must be {minInterval} to {MaxCheckIntervalMs:N0} ms.", nameof(ScreenSearchStep.CheckIntervalMs)));
        if (Math.Abs((long)f.OffsetX) > MaxOffset || Math.Abs((long)f.OffsetY) > MaxOffset)
            issues.Add(Error($"Offsets must be between -{MaxOffset} and {MaxOffset} pixels.", "Offset"));
        if (!Enum.IsDefined(f.Action) || !Enum.IsDefined(f.WhenNotFound) || !Enum.IsDefined(f.Goal))
            issues.Add(Error("This step has an unknown option value."));
        else if (f.Goal == WaitGoal.Disappear && f.Action != FoundTextAction.None)
            issues.Add(Warning("The \"when found\" action isn't used while waiting for something to disappear.", nameof(ScreenSearchStep.Action)));
    }

    /// <summary>The stored picture must exist, decode to its stated size, and not be one flat color.</summary>
    private static void ValidateTemplate(List<ValidationIssue> issues, FindImageStep step)
    {
        const string property = nameof(FindImageStep.TemplateData);
        if (string.IsNullOrEmpty(step.TemplateData))
        {
            issues.Add(Error("Capture the image to look for first (\"Capture image\").", property));
            return;
        }

        if (step.TemplateWidth < MinTemplateSize || step.TemplateHeight < MinTemplateSize
            || step.TemplateWidth > MaxTemplateSize || step.TemplateHeight > MaxTemplateSize)
        {
            issues.Add(Error($"The image must be {MinTemplateSize} to {MaxTemplateSize} pixels wide and tall. Capture a different area.", property));
            return;
        }

        if (step.TemplateData.Length > MaxTemplateDataLength)
        {
            issues.Add(Error("The stored image is too large. The macro may be damaged; capture the image again.", property));
            return;
        }

        if (!TemplateCodec.TryDecode(step.TemplateData, step.TemplateWidth, step.TemplateHeight, out var image, out var error))
        {
            issues.Add(Error(error ?? "The stored image is damaged.", property));
            return;
        }

        if (TemplateMatcher.IsFlat(image!))
            issues.Add(Error("The image is a single flat color, so its shape can't be matched. Capture an area with some detail, or use a \"Find color\" step.", property));
    }

    private static void CheckPosition(List<ValidationIssue> issues, int x, int y, ScreenBounds? screen)
    {
        if (screen is not null && !screen.Contains(x, y))
            issues.Add(Warning($"Position ({x}, {y}) is outside your current screen area ({screen}).", "Position"));
    }

    private static ValidationIssue Error(string message, string? property = null) =>
        new(IssueSeverity.Error, message, Property: property);

    private static ValidationIssue Warning(string message, string? property = null) =>
        new(IssueSeverity.Warning, message, Property: property);
}
