using SmartMacro.Core.Input;

namespace SmartMacro.Core.Models;

/// <summary>Short human-readable text for each step, used in the step list.</summary>
public static class StepDescriber
{
    public static string DisplayName(StepKind kind) => kind switch
    {
        StepKind.Delay => "Delay",
        StepKind.MouseMove => "Move mouse",
        StepKind.MouseClick => "Mouse click",
        StepKind.MouseScroll => "Scroll wheel",
        StepKind.KeyPress => "Key press",
        StepKind.TypeText => "Type text",
        StepKind.FindText => "Find text (OCR)",
        StepKind.InputSequence => "Recorded input",
        StepKind.FindImage => "Find image",
        StepKind.FindColor => "Find color",
        StepKind.If => "If",
        StepKind.Else => "Else",
        StepKind.EndIf => "End If",
        StepKind.Repeat => "Repeat",
        StepKind.EndRepeat => "End Repeat",
        StepKind.FeedbackLoop => "Feedback loop",
        _ => kind.ToString(),
    };

    public static string Describe(MacroStep step) => step switch
    {
        DelayStep d => d.DurationMs >= 1000
            ? $"Wait {d.DurationMs / 1000.0:0.###} s"
            : $"Wait {d.DurationMs} ms",
        MouseMoveStep m => $"Move to ({m.X}, {m.Y})",
        MouseClickStep c => $"{ClickVerb(c.Action)} {c.Button.ToString().ToLowerInvariant()} button {(c.UseLastFoundText ? "at the last found spot" : Where(c.UseCurrentPosition, c.X, c.Y))}{Hold(c.Action == MouseClickAction.Click, c.HoldMs)}",
        MouseScrollStep s => $"Scroll {ScrollDirection(s)} {Notches(s.Delta)} {Where(s.UseCurrentPosition, s.X, s.Y)}",
        KeyPressStep k => $"{KeyVerb(k.Action)} {KeyCombo(k)}{Hold(k.Action == KeyAction.Press, k.HoldMs)}",
        TypeTextStep t => $"Type \"{Preview(t.Text)}\"",
        FindTextStep f => DescribeSearch(f, $"\"{Preview(f.Text)}\""),
        FindImageStep i => DescribeSearch(i, i.TemplateWidth > 0 ? $"image ({i.TemplateWidth}×{i.TemplateHeight})" : "image (not captured yet)"),
        FindColorStep c => DescribeSearch(c, $"color {c.Color} (±{c.Tolerance})"),
        InputSequenceStep q => DescribeSequence(q),
        IfStep i => $"If {DescribeCondition(i.Condition)}",
        ElseStep => "Otherwise (when the If's condition is false)",
        EndIfStep => "End of the If block",
        RepeatStep r => DescribeRepeat(r),
        EndRepeatStep => "End of the Repeat block (go back for the next turn)",
        FeedbackLoopStep f => DescribeFeedback(f),
        _ => step.GetType().Name,
    };

    /// <summary>E.g. "\"OK\" is on screen", "the image (40×30) is not in area (…)", plus the wait time if any.</summary>
    public static string DescribeCondition(Condition? condition)
    {
        if (condition?.Search is not { } search) return "(no condition yet)";

        var what = search switch
        {
            FindTextStep t => $"\"{Preview(t.Text)}\"",
            FindImageStep i => i.TemplateWidth > 0 ? $"the image ({i.TemplateWidth}×{i.TemplateHeight})" : "the image (not captured yet)",
            FindColorStep c => $"color {c.Color} (±{c.Tolerance})",
            _ => "it",
        };
        var where = search.SearchFullScreen ? "on screen" : $"in area ({search.RegionX}, {search.RegionY}, {search.RegionWidth}×{search.RegionHeight})";
        var verb = condition.Negate ? "is not" : "is";
        var wait = search.TimeoutMs > 0 ? $" (wait up to {search.TimeoutMs / 1000.0:0.#} s)" : "";
        return $"{what} {verb} {where}{wait}";
    }

    /// <summary>E.g. "Keep marker #FF0000 in zone 40–60 %: hold before, release after (left button; max 30 s)".</summary>
    public static string DescribeFeedback(FeedbackLoopStep f)
    {
        var zone = f.ZoneMode == FeedbackZoneMode.Fixed
            ? $"zone {f.ZoneStartPercent}–{f.ZoneEndPercent} %"
            : $"zone {f.ZoneColor}";
        var input = f.InputKind == FeedbackInputKind.MouseButton
            ? $"{f.MouseButton.ToString().ToLowerInvariant()} button"
            : VirtualKeys.GetName(f.VirtualKey);

        var rules = new List<string>();
        AddRule(rules, f.WhenBeforeZone, "before");
        AddRule(rules, f.WhenInsideZone, "inside");
        AddRule(rules, f.WhenAfterZone, "after");
        var what = rules.Count == 0 ? "no rules" : string.Join(", ", rules);

        var stops = new List<string> { $"max {f.MaxRuntimeMs / 1000.0:0.#} s" };
        if (f.CompleteWhen is not null) stops.Add("until done");
        if (f.FailWhen is not null) stops.Add("fail check");
        return $"Keep marker {f.MarkerColor} in {zone}: {what} ({input}; {string.Join(", ", stops)})";
    }

    private static void AddRule(List<string> rules, FeedbackAction action, string where)
    {
        var verb = action switch
        {
            FeedbackAction.Hold => "hold",
            FeedbackAction.Release => "release",
            FeedbackAction.Tap => "tap",
            _ => null,
        };
        if (verb is not null) rules.Add($"{verb} {where}");
    }

    private static string DescribeRepeat(RepeatStep r)
    {
        var text = r.Mode switch
        {
            RepeatMode.Times => r.Count == 1 ? "Repeat 1 time" : $"Repeat {r.Count:N0} times",
            RepeatMode.While => $"Repeat while {DescribeCondition(r.Condition)}",
            RepeatMode.Until => $"Repeat until {DescribeCondition(r.Condition)}",
            _ => "Repeat",
        };

        var limits = new List<string>();
        if (r.Mode != RepeatMode.Times && r.MaxIterations > 0) limits.Add($"max {r.MaxIterations:N0} turns");
        if (r.MaxDurationMs > 0) limits.Add($"max {r.MaxDurationMs / 1000.0:0.#} s");
        return limits.Count == 0 ? text : $"{text} ({string.Join(", ", limits)})";
    }

    /// <summary>Counts for the recorded-input editor and the step list.</summary>
    public static InputSequenceSummary Summarize(InputSequenceStep step)
    {
        var events = step.Events ?? new List<InputEvent>();
        int moves = 0, keys = 0, buttons = 0, wheels = 0, positioned = 0;
        long dx = 0, dy = 0;
        var keyNames = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var e in events)
        {
            switch (e.Kind)
            {
                case InputEventKind.MouseMove:
                    moves++;
                    if (e.HasPosition) positioned++;
                    dx += Math.Abs((long)e.Dx);
                    dy += Math.Abs((long)e.Dy);
                    break;
                case InputEventKind.KeyDown:
                    keys++;
                    if (keyNames.Count < 12) keyNames.Add(VirtualKeys.GetName(e.VirtualKey));
                    break;
                case InputEventKind.MouseDown:
                    buttons++;
                    break;
                case InputEventKind.Wheel:
                case InputEventKind.HorizontalWheel:
                    wheels++;
                    break;
            }
        }

        var duration = events.Count == 0 ? 0 : Math.Max(0, events[^1].TimeUs) / 1000.0;
        var cursorParts = 0;
        var relativeParts = 0;
        if (step.MouseSegments is { } segments)
        {
            foreach (var segment in segments)
            {
                if (segment is null) continue;
                if (segment.Effective == MouseMovementMode.Relative) relativeParts++;
                else cursorParts++;
            }
        }

        return new InputSequenceSummary(events.Count, duration, moves, keys, buttons, wheels, dx, dy, keyNames.ToList(),
            positioned, cursorParts, relativeParts);
    }

    private static string DescribeSequence(InputSequenceStep step)
    {
        var s = Summarize(step);
        var seconds = s.DurationMs / 1000.0;
        if (!step.HasMouseSegments)
            return $"Play recorded input: {seconds:0.0} s, {s.EventCount:N0} events ({s.MouseMoves:N0} mouse moves, {s.KeyPresses:N0} key presses)";

        var mouse = (s.CursorParts, s.RelativeParts) switch
        {
            (_, 0) => "cursor positions",
            (0, _) => "camera / relative movement",
            _ => $"{s.CursorParts} cursor part{(s.CursorParts == 1 ? "" : "s")} + {s.RelativeParts} camera part{(s.RelativeParts == 1 ? "" : "s")}",
        };
        return $"Play recorded input: {seconds:0.0} s, {mouse}, {s.KeyPresses:N0} key presses, {s.ButtonPresses:N0} clicks";
    }

    private static string Hold(bool applies, int holdMs) => applies && holdMs > 0 ? $" (hold {holdMs} ms)" : "";

    public static string KeyCombo(KeyPressStep step)
    {
        var parts = new List<string>();
        if (step.Action == KeyAction.Press)
        {
            if (step.Modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
            if (step.Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
            if (step.Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
            if (step.Modifiers.HasFlag(KeyModifiers.Windows)) parts.Add("Win");
        }

        parts.Add(VirtualKeys.GetName(step.VirtualKey));
        return string.Join(" + ", parts);
    }

    private static string DescribeSearch(ScreenSearchStep f, string what)
    {
        var seconds = $"{f.TimeoutMs / 1000.0:0.#} s";
        var where = f.SearchFullScreen ? "on screen" : $"in area ({f.RegionX}, {f.RegionY}, {f.RegionWidth}×{f.RegionHeight})";
        if (f.Goal == WaitGoal.Disappear)
            return f.TimeoutMs > 0 ? $"Wait up to {seconds} until {what} is gone {where}" : $"Check that {what} is not {where}";

        var verb = f.TimeoutMs > 0 ? $"Wait up to {seconds} for" : "Look for";
        var then = f.Action switch
        {
            FoundTextAction.Move => ", then move to it",
            FoundTextAction.Click => ", then click it",
            FoundTextAction.DoubleClick => ", then double-click it",
            FoundTextAction.RightClick => ", then right-click it",
            _ => "",
        };
        return $"{verb} {what} {where}{then}";
    }

    private static string ClickVerb(MouseClickAction action) => action switch
    {
        MouseClickAction.Click => "Click",
        MouseClickAction.DoubleClick => "Double-click",
        MouseClickAction.Down => "Press",
        MouseClickAction.Up => "Release",
        _ => action.ToString(),
    };

    private static string KeyVerb(KeyAction action) => action switch
    {
        KeyAction.Press => "Press",
        KeyAction.Down => "Hold down",
        KeyAction.Up => "Release",
        _ => action.ToString(),
    };

    private static string Where(bool current, int x, int y) => current ? "at cursor" : $"at ({x}, {y})";

    private static string ScrollDirection(MouseScrollStep s) =>
        s.Horizontal ? (s.Delta >= 0 ? "right" : "left") : (s.Delta >= 0 ? "up" : "down");

    private static string Notches(int delta)
    {
        var notches = Math.Abs((long)delta) / 120.0;
        return notches == 1 ? "1 notch" : $"{notches:0.##} notches";
    }

    private static string Preview(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var oneLine = text.Replace("\r\n", "⏎").Replace('\n', '⏎').Replace('\r', '⏎').Replace('\t', ' ');
        return oneLine.Length <= 40 ? oneLine : oneLine[..40] + "…";
    }
}

/// <summary>
/// Counts shown for a recorded-input step. DurationMs is the time of the last event.
/// MovesWithPosition: mouse moves that also have a cursor position (smart recordings).
/// CursorParts / RelativeParts: how many mouse segments play cursor positions / relative movement (0 / 0 for game mode).
/// </summary>
public sealed record InputSequenceSummary(
    int EventCount,
    double DurationMs,
    int MouseMoves,
    int KeyPresses,
    int ButtonPresses,
    int WheelEvents,
    long TotalAbsDx,
    long TotalAbsDy,
    IReadOnlyList<string> KeysUsed,
    int MovesWithPosition = 0,
    int CursorParts = 0,
    int RelativeParts = 0);
