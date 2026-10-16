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
        StepKind.InputSequence => "Recorded input (game mode)",
        _ => kind.ToString(),
    };

    public static string Describe(MacroStep step) => step switch
    {
        DelayStep d => d.DurationMs >= 1000
            ? $"Wait {d.DurationMs / 1000.0:0.###} s"
            : $"Wait {d.DurationMs} ms",
        MouseMoveStep m => $"Move to ({m.X}, {m.Y})",
        MouseClickStep c => $"{ClickVerb(c.Action)} {c.Button.ToString().ToLowerInvariant()} button {(c.UseLastFoundText ? "at found text" : Where(c.UseCurrentPosition, c.X, c.Y))}{Hold(c.Action == MouseClickAction.Click, c.HoldMs)}",
        MouseScrollStep s => $"Scroll {ScrollDirection(s)} {Notches(s.Delta)} {Where(s.UseCurrentPosition, s.X, s.Y)}",
        KeyPressStep k => $"{KeyVerb(k.Action)} {KeyCombo(k)}{Hold(k.Action == KeyAction.Press, k.HoldMs)}",
        TypeTextStep t => $"Type \"{Preview(t.Text)}\"",
        FindTextStep f => DescribeFindText(f),
        InputSequenceStep q => DescribeSequence(q),
        _ => step.GetType().Name,
    };

    /// <summary>Counts for the recorded-input editor and the step list.</summary>
    public static InputSequenceSummary Summarize(InputSequenceStep step)
    {
        var events = step.Events ?? new List<InputEvent>();
        int moves = 0, keys = 0, buttons = 0, wheels = 0;
        long dx = 0, dy = 0;
        var keyNames = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var e in events)
        {
            switch (e.Kind)
            {
                case InputEventKind.MouseMove:
                    moves++;
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
        return new InputSequenceSummary(events.Count, duration, moves, keys, buttons, wheels, dx, dy, keyNames.ToList());
    }

    private static string DescribeSequence(InputSequenceStep step)
    {
        var s = Summarize(step);
        var seconds = s.DurationMs / 1000.0;
        return $"Play recorded input: {seconds:0.0} s, {s.EventCount:N0} events ({s.MouseMoves:N0} mouse moves, {s.KeyPresses:N0} key presses)";
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

    private static string DescribeFindText(FindTextStep f)
    {
        var verb = f.TimeoutMs > 0 ? $"Wait up to {f.TimeoutMs / 1000.0:0.#} s for" : "Look for";
        var where = f.SearchFullScreen ? "on screen" : $"in area ({f.RegionX}, {f.RegionY}, {f.RegionWidth}×{f.RegionHeight})";
        var then = f.Action switch
        {
            FoundTextAction.Move => ", then move to it",
            FoundTextAction.Click => ", then click it",
            FoundTextAction.DoubleClick => ", then double-click it",
            FoundTextAction.RightClick => ", then right-click it",
            _ => "",
        };
        return $"{verb} \"{Preview(f.Text)}\" {where}{then}";
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

/// <summary>Counts shown for a recorded-input step. DurationMs is the time of the last event.</summary>
public sealed record InputSequenceSummary(
    int EventCount,
    double DurationMs,
    int MouseMoves,
    int KeyPresses,
    int ButtonPresses,
    int WheelEvents,
    long TotalAbsDx,
    long TotalAbsDy,
    IReadOnlyList<string> KeysUsed);
