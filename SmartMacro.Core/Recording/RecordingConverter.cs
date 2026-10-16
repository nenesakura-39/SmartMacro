using SmartMacro.Core.Input;
using SmartMacro.Core.Models;
using SmartMacro.Core.Validation;

namespace SmartMacro.Core.Recording;

public enum RawInputKind
{
    MouseMove,
    MouseDown,
    MouseUp,
    Wheel,
    HorizontalWheel,
    KeyDown,
    KeyUp,
}

/// <summary>One raw input event captured by the recorder. TimeMs is relative to the recording start.</summary>
public readonly record struct RawInputEvent(
    long TimeMs,
    RawInputKind Kind,
    int X = 0,
    int Y = 0,
    MacroMouseButton Button = MacroMouseButton.Left,
    int Delta = 0,
    int VirtualKey = 0);

/// <summary>Captures raw desktop input. The real version uses Windows low-level hooks.</summary>
public interface IInputRecorder
{
    bool IsRecording { get; }

    /// <summary>Number of events captured so far.</summary>
    int EventCount { get; }

    /// <summary>True when the event limit was hit and further input was ignored.</summary>
    bool LimitReached { get; }

    void Start();

    /// <summary>Stops recording and returns everything captured, oldest first.</summary>
    IReadOnlyList<RawInputEvent> Stop();
}

public sealed class RecordingOptions
{
    /// <summary>Keep mouse movement, not only clicks.</summary>
    public bool RecordMouseMoves { get; init; } = true;

    /// <summary>Mouse moves closer together than this are thinned out.</summary>
    public int MinMoveIntervalMs { get; init; } = 30;

    /// <summary>Pauses shorter than this don't get their own Delay step.</summary>
    public int MinDelayMs { get; init; } = 15;

    /// <summary>A button or key released within this time becomes a single Click / Press step.</summary>
    public int MaxTapDurationMs { get; init; } = 500;

    /// <summary>If the mouse moved more than this between down and up, it's a drag, not a click.</summary>
    public int MaxClickDistance { get; init; } = 4;

    /// <summary>
    /// Keys to strip from the end of the recording (e.g. the hotkey used to stop recording).
    /// </summary>
    public IReadOnlyCollection<int> TrimTrailingKeys { get; init; } = Array.Empty<int>();
}

/// <summary>
/// Turns raw recorded input into editable workflow steps:
/// quick down+up pairs become Click / Press steps, gaps become Delay steps,
/// mouse movement is thinned out, and anything left held down at the end is released.
/// </summary>
public static class RecordingConverter
{
    public static List<MacroStep> Convert(IReadOnlyList<RawInputEvent> rawEvents, RecordingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(rawEvents);
        options ??= new RecordingOptions();

        var events = rawEvents.ToList();
        TrimTrailing(events, options.TrimTrailingKeys);

        var steps = new List<MacroStep>();
        var consumed = new bool[events.Count];
        var keysDown = new List<int>();
        var buttonsDown = new List<MacroMouseButton>();
        long? lastActionTime = null;
        long? lastMoveTime = null;
        (int X, int Y)? lastMovePosition = null;

        void Emit(MacroStep step, long time, long endTime)
        {
            if (lastActionTime is long last)
            {
                var gap = time - last;
                if (gap >= options.MinDelayMs)
                    steps.Add(new DelayStep { DurationMs = (int)Math.Min(gap, MacroValidator.MaxDelayMs) });
            }

            steps.Add(step);
            lastActionTime = endTime;
        }

        for (var i = 0; i < events.Count; i++)
        {
            if (consumed[i]) continue;
            var e = events[i];

            switch (e.Kind)
            {
                case RawInputKind.MouseMove:
                {
                    if (!options.RecordMouseMoves) break;
                    if (lastMovePosition == (e.X, e.Y)) break;
                    if (lastMoveTime is long lastMove && e.TimeMs - lastMove < options.MinMoveIntervalMs) break;

                    Emit(new MouseMoveStep { X = e.X, Y = e.Y }, e.TimeMs, e.TimeMs);
                    lastMoveTime = e.TimeMs;
                    lastMovePosition = (e.X, e.Y);
                    break;
                }

                case RawInputKind.MouseDown:
                {
                    var up = FindPartner(events, i, RawInputKind.MouseUp, e.Button, 0);
                    if (up >= 0
                        && events[up].TimeMs - e.TimeMs <= options.MaxTapDurationMs
                        && Distance(e, events[up]) <= options.MaxClickDistance)
                    {
                        Emit(new MouseClickStep { Button = e.Button, Action = MouseClickAction.Click, X = e.X, Y = e.Y },
                            e.TimeMs, events[up].TimeMs);
                        for (var k = i; k <= up; k++) consumed[k] = true;
                        lastMovePosition = (events[up].X, events[up].Y);
                    }
                    else
                    {
                        Emit(new MouseClickStep { Button = e.Button, Action = MouseClickAction.Down, X = e.X, Y = e.Y },
                            e.TimeMs, e.TimeMs);
                        if (!buttonsDown.Contains(e.Button)) buttonsDown.Add(e.Button);
                        lastMovePosition = (e.X, e.Y);
                    }

                    break;
                }

                case RawInputKind.MouseUp:
                {
                    // Ignore releases of buttons that were pressed before recording started.
                    if (!buttonsDown.Remove(e.Button)) break;
                    Emit(new MouseClickStep { Button = e.Button, Action = MouseClickAction.Up, X = e.X, Y = e.Y }, e.TimeMs, e.TimeMs);
                    lastMovePosition = (e.X, e.Y);
                    break;
                }

                case RawInputKind.Wheel:
                case RawInputKind.HorizontalWheel:
                {
                    if (e.Delta == 0) break;
                    var delta = Math.Clamp(e.Delta, -MacroValidator.MaxScrollDelta, MacroValidator.MaxScrollDelta);
                    Emit(new MouseScrollStep
                    {
                        Delta = delta,
                        Horizontal = e.Kind == RawInputKind.HorizontalWheel,
                        UseCurrentPosition = false,
                        X = e.X,
                        Y = e.Y,
                    }, e.TimeMs, e.TimeMs);
                    break;
                }

                case RawInputKind.KeyDown:
                {
                    if (!VirtualKeys.IsValid(e.VirtualKey)) break;

                    // Auto-repeat (key already down) is kept as extra "Hold down" steps.
                    if (!keysDown.Contains(e.VirtualKey))
                    {
                        var up = FindPartner(events, i, RawInputKind.KeyUp, default, e.VirtualKey);
                        if (up >= 0 && events[up].TimeMs - e.TimeMs <= options.MaxTapDurationMs)
                        {
                            Emit(new KeyPressStep { VirtualKey = e.VirtualKey, Action = KeyAction.Press }, e.TimeMs, events[up].TimeMs);
                            consumed[i] = true;
                            consumed[up] = true;
                            break;
                        }

                        keysDown.Add(e.VirtualKey);
                    }

                    Emit(new KeyPressStep { VirtualKey = e.VirtualKey, Action = KeyAction.Down }, e.TimeMs, e.TimeMs);
                    break;
                }

                case RawInputKind.KeyUp:
                {
                    // Ignore releases of keys pressed before recording started (e.g. the start hotkey).
                    if (!keysDown.Remove(e.VirtualKey)) break;
                    Emit(new KeyPressStep { VirtualKey = e.VirtualKey, Action = KeyAction.Up }, e.TimeMs, e.TimeMs);
                    break;
                }
            }
        }

        // Anything still held at the end gets released, so the macro never leaves keys stuck down.
        for (var k = keysDown.Count - 1; k >= 0; k--)
            steps.Add(new KeyPressStep { VirtualKey = keysDown[k], Action = KeyAction.Up });
        foreach (var button in buttonsDown)
            steps.Add(new MouseClickStep { Button = button, Action = MouseClickAction.Up, UseCurrentPosition = true });

        return steps;
    }

    /// <summary>
    /// Finds the matching release for a press, as long as only mouse movement happens in between.
    /// Any other key or button event in between means it isn't a simple tap.
    /// </summary>
    private static int FindPartner(List<RawInputEvent> events, int start, RawInputKind partnerKind, MacroMouseButton button, int virtualKey)
    {
        for (var j = start + 1; j < events.Count; j++)
        {
            var e = events[j];
            if (e.Kind == RawInputKind.MouseMove) continue;

            var matches = partnerKind == RawInputKind.MouseUp
                ? e.Kind == RawInputKind.MouseUp && e.Button == button
                : e.Kind == RawInputKind.KeyUp && e.VirtualKey == virtualKey;
            return matches ? j : -1;
        }

        return -1;
    }

    private static void TrimTrailing(List<RawInputEvent> events, IReadOnlyCollection<int> trimKeys)
    {
        if (trimKeys.Count == 0) return;

        while (events.Count > 0)
        {
            var last = events[^1];
            var isTrimmableKey = (last.Kind is RawInputKind.KeyDown or RawInputKind.KeyUp) && trimKeys.Contains(last.VirtualKey);
            if (last.Kind == RawInputKind.MouseMove || isTrimmableKey)
                events.RemoveAt(events.Count - 1);
            else
                break;
        }
    }

    private static double Distance(RawInputEvent a, RawInputEvent b)
    {
        var dx = (double)a.X - b.X;
        var dy = (double)a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
