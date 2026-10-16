using SmartMacro.Core.Input;
using SmartMacro.Core.Models;

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
                    "This click uses the last found text, but there's no \"Find text\" step before it.", i, step.Id, nameof(MouseClickStep.UseLastFoundText)));
            if (step is FindTextStep { Enabled: true }) hasFindTextBefore = true;

            if (step.Id == Guid.Empty || !seenIds.Add(step.Id))
                issues.Add(new ValidationIssue(IssueSeverity.Error, "This step has a missing or duplicate ID.", i, step.Id));

            foreach (var issue in ValidateStep(step, screen))
                issues.Add(issue with { StepIndex = i, StepId = step.Id });
        }

        return issues;
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

                if (f.MinSimilarityPercent < 50 || f.MinSimilarityPercent > 100)
                    issues.Add(Error("Match accuracy must be between 50% and 100%.", nameof(FindTextStep.MinSimilarityPercent)));
                if (f.TimeoutMs < 0 || f.TimeoutMs > MaxDelayMs)
                    issues.Add(Error($"The wait time must be 0 to {MaxDelayMs:N0} ms.", nameof(FindTextStep.TimeoutMs)));
                if (f.CheckIntervalMs < MinCheckIntervalMs || f.CheckIntervalMs > MaxCheckIntervalMs)
                    issues.Add(Error($"Check every must be {MinCheckIntervalMs} to {MaxCheckIntervalMs:N0} ms.", nameof(FindTextStep.CheckIntervalMs)));
                if (Math.Abs((long)f.OffsetX) > MaxOffset || Math.Abs((long)f.OffsetY) > MaxOffset)
                    issues.Add(Error($"Offsets must be between -{MaxOffset} and {MaxOffset} pixels.", "Offset"));
                if (!Enum.IsDefined(f.MatchMode) || !Enum.IsDefined(f.Action) || !Enum.IsDefined(f.WhenNotFound))
                    issues.Add(Error("This step has an unknown option value."));
                break;

            default:
                issues.Add(Error($"Unsupported step type: {step.GetType().Name}."));
                break;
        }

        return issues;
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
