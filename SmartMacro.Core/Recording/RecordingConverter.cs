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

    /// <summary>Where the cursor was when recording started (physical pixels).</summary>
    int StartX => 0;

    int StartY => 0;

    void Start();

    /// <summary>Stops recording and returns everything captured, oldest first.</summary>
    IReadOnlyList<RawInputEvent> Stop();
}

public sealed class RecordingOptions
{
    /// <summary>Keep mouse movement, not only clicks.</summary>
    public bool RecordMouseMoves { get; init; } = true;

    /// <summary>
    /// Mouse moves closer together than this are thinned out. Fine for desktop recordings, because every
    /// Move step is an absolute position: skipping in-between samples only smooths the path, it never shifts
    /// where the cursor ends up (the last position before a pause or click is always kept).
    /// Relative (game) movement must never be thinned like this; see <see cref="GameRecordingConverter"/>.
    /// </summary>
    public int MinMoveIntervalMs { get; init; } = 30;

    /// <summary>
    /// Pauses shorter than this don't get their own Delay step. The skipped time is carried over into the
    /// next Delay step, so the total timing of the recording is kept.
    /// </summary>
    public int MinDelayMs { get; init; } = 15;

    /// <summary>A button or key released within this time becomes a single Click / Press step.</summary>
    public int MaxTapDurationMs { get; init; } = 500;

    /// <summary>
    /// Keep how long each tap was held (stored in the Click / Press step's HoldMs). On by default, so a short
    /// W tap still lasts as long as it did. Off = the old behavior: instant taps, and the hold time is dropped.
    /// </summary>
    public bool KeepHoldTime { get; init; } = true;

    /// <summary>
    /// Replace the time-based thinning with path simplification: from every stretch of cursor movement only the
    /// points needed to redraw the path within <see cref="PathTolerancePx"/> are kept, plus the start and end,
    /// every direction reversal (e.g. a slider dragged to 80 % and back to 50 %), and every spot the cursor rested
    /// at. A 10 s recording of hovering then gives a handful of Move steps instead of ~300 Delay + Move steps,
    /// while the exact position before every press / release is still there. Off by default for compatibility;
    /// smart recordings and the app's desktop recordings turn it on.
    /// </summary>
    public bool SimplifyMousePath { get; init; }

    /// <summary>
    /// "Simplify mouse paths": cursor movement while no mouse button is held is not kept as Move steps at all. A click
    /// or wheel step already stores where it happens, so wandering to a button and clicking it becomes just
    /// Delay + Click. Only where the cursor ends up before a key press (hovering over something, then pressing a key)
    /// is kept, as ONE Move step. Movement while a button is held (dragging a slider or an item) is always kept.
    /// Off by default here; the app turns it on from Settings ("Simplify mouse paths", on by default).
    /// </summary>
    public bool CollapseMousePaths { get; init; }

    /// <summary>How far (pixels) the simplified path may stray from the recorded one.</summary>
    public int PathTolerancePx { get; init; } = 3;

    /// <summary>A cursor that stays still at least this long keeps that spot (it may have been hovering on purpose).</summary>
    public int RestMs { get; init; } = 120;

    /// <summary>If the mouse moved more than this between down and up, it's a drag, not a click.</summary>
    public int MaxClickDistance { get; init; } = 4;

    /// <summary>Optional initial cursor coordinate at t = 0 so repeated runs reset to the starting point.</summary>
    public int? StartX { get; init; }

    /// <summary>Optional initial cursor coordinate at t = 0 so repeated runs reset to the starting point.</summary>
    public int? StartY { get; init; }

    /// <summary>
    /// Keys to strip from the end of the recording (e.g. the hotkey used to stop recording).
    /// </summary>
    public IReadOnlyCollection<int> TrimTrailingKeys { get; init; } = Array.Empty<int>();
}

/// <summary>
/// Turns raw recorded desktop input into editable workflow steps:
/// quick down+up pairs become Click / Press steps (keeping their hold time), gaps become Delay steps,
/// mouse movement is thinned out, and anything left held down at the end is released.
/// A down+up pair is only combined into one step when nothing else that gets recorded happens in between,
/// so the original order of keyboard and mouse input is always kept.
/// </summary>
public static class RecordingConverter
{
    public static List<MacroStep> Convert(IReadOnlyList<RawInputEvent> rawEvents, RecordingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(rawEvents);
        options ??= new RecordingOptions();

        var events = rawEvents.ToList();
        TrimTrailing(events, options.TrimTrailingKeys);
        if (events.Count > 0
            && options.RecordMouseMoves
            && !options.CollapseMousePaths
            && options.StartX is int sx
            && options.StartY is int sy
            && events.Any(e => e.Kind == RawInputKind.MouseMove)
            && (events[0].Kind != RawInputKind.MouseMove || events[0].X != sx || events[0].Y != sy))
        {
            events.Insert(0, new RawInputEvent(Math.Min(0, events[0].TimeMs), RawInputKind.MouseMove, X: sx, Y: sy));
        }

        var simplified = options.SimplifyMousePath && options.RecordMouseMoves;
        if (simplified) events = MousePathSimplifier.Simplify(events, options.PathTolerancePx, options.RestMs);
        if (options.CollapseMousePaths && options.RecordMouseMoves) events = CollapseFreeMoves(events);

        var steps = new List<MacroStep>();
        var consumed = new bool[events.Count];
        var keysDown = new List<int>();
        var buttonsDown = new List<MacroMouseButton>();

        // How far into the recording playback has got so far (recording time, ms). Gaps are measured from here,
        // and gaps too short for their own Delay step stay "owed" until the next one instead of being lost.
        long? accounted = null;
        long? lastMoveTime = null;
        (int X, int Y)? lastMovePosition = null;
        RawInputEvent? pendingMove = null; // last thinned-out move, kept if the cursor rests there

        void Emit(MacroStep step, long time, long endTime)
        {
            if (accounted is long acc)
            {
                var gap = time - acc;
                var minDelay = keysDown.Count > 0 && options.MinDelayMs <= 15 ? 1 : options.MinDelayMs;
                if (gap >= minDelay)
                {
                    steps.Add(new DelayStep { DurationMs = (int)Math.Min(gap, MacroValidator.MaxDelayMs) });
                    accounted = time;
                }
            }
            else
            {
                accounted = time; // no leading delay
            }

            steps.Add(step);

            // A Click / Press covers the time until its release: either it's played (HoldMs) or,
            // with KeepHoldTime off, deliberately skipped like before.
            if (endTime > time) accounted += endTime - time;
        }

        void EmitMove(RawInputEvent move)
        {
            Emit(new MouseMoveStep { X = move.X, Y = move.Y }, move.TimeMs, move.TimeMs);
            lastMoveTime = move.TimeMs;
            lastMovePosition = (move.X, move.Y);
        }

        void FlushPendingMove()
        {
            if (pendingMove is { } p && lastMovePosition != (p.X, p.Y)) EmitMove(p);
            pendingMove = null;
        }

        int HoldFor(long downTime, long upTime) =>
            options.KeepHoldTime ? (int)Math.Clamp(upTime - downTime, 0, MacroValidator.MaxHoldMs) : 0;

        for (var i = 0; i < events.Count; i++)
        {
            if (consumed[i]) continue;
            var e = events[i];

            if (e.Kind != RawInputKind.MouseMove)
                FlushPendingMove();

            switch (e.Kind)
            {
                case RawInputKind.MouseMove:
                {
                    if (!options.RecordMouseMoves) break;
                    if (lastMovePosition == (e.X, e.Y)) { pendingMove = null; break; }
                    // (Simplified paths are already reduced to the points that matter; don't thin those again.)
                    if (!simplified && lastMoveTime is long lastMove && e.TimeMs - lastMove < options.MinMoveIntervalMs)
                    {
                        pendingMove = e; // thinned out for now
                        break;
                    }

                    // The cursor rested at the last thinned-out position before moving again: keep it.
                    if (pendingMove is { } p && e.TimeMs - p.TimeMs >= options.MinMoveIntervalMs) FlushPendingMove();
                    pendingMove = null;
                    EmitMove(e);
                    break;
                }

                case RawInputKind.MouseDown:
                {
                    var up = FindPartner(events, i, RawInputKind.MouseUp, e.Button, 0, skipMoves: true, out _);
                    if (up >= 0
                        && events[up].TimeMs - e.TimeMs <= options.MaxTapDurationMs
                        && Distance(e, events[up]) <= options.MaxClickDistance)
                    {
                        Emit(new MouseClickStep
                            {
                                Button = e.Button, Action = MouseClickAction.Click, X = e.X, Y = e.Y,
                                HoldMs = HoldFor(e.TimeMs, events[up].TimeMs),
                            },
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

                    // Auto-repeat (key already down) is kept as extra "Hold down" steps unless collapsed into a single Press step.
                    if (!keysDown.Contains(e.VirtualKey))
                    {
                        // Only a tap/hold if nothing else that gets recorded happens in between.
                        var up = FindPartner(events, i, RawInputKind.KeyUp, default, e.VirtualKey, skipMoves: !options.RecordMouseMoves, out var hadAutoRepeat);
                        var maxHold = hadAutoRepeat && options.KeepHoldTime ? MacroValidator.MaxHoldMs : options.MaxTapDurationMs;
                        if (up >= 0 && events[up].TimeMs - e.TimeMs <= maxHold)
                        {
                            Emit(new KeyPressStep
                                {
                                    VirtualKey = e.VirtualKey, Action = KeyAction.Press,
                                    HoldMs = HoldFor(e.TimeMs, events[up].TimeMs),
                                },
                                e.TimeMs, events[up].TimeMs);
                            for (var k = i; k <= up; k++)
                            {
                                if (k == i || k == up || (events[k].Kind == RawInputKind.KeyDown && events[k].VirtualKey == e.VirtualKey))
                                    consumed[k] = true;
                            }

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

        FlushPendingMove();

        // Anything still held at the end gets released, so the macro never leaves keys stuck down.
        for (var k = keysDown.Count - 1; k >= 0; k--)
            steps.Add(new KeyPressStep { VirtualKey = keysDown[k], Action = KeyAction.Up });
        foreach (var button in buttonsDown)
            steps.Add(new MouseClickStep { Button = button, Action = MouseClickAction.Up, UseCurrentPosition = true });

        return steps;
    }

    /// <summary>
    /// Drops cursor movement made while no mouse button is held, except the last position before a key event (the spot
    /// the cursor hovered on while a key was pressed). Clicks and wheel turns carry their own position, so nothing is
    /// lost for them. Movement while a button is held (drags) is untouched. If the recording contains only cursor
    /// movement (no clicks, wheel turns, or keys), all movement is preserved so pure mouse-move recordings aren't emptied.
    /// </summary>
    internal static List<RawInputEvent> CollapseFreeMoves(List<RawInputEvent> events)
    {
        if (events.All(e => e.Kind == RawInputKind.MouseMove))
            return events;

        var result = new List<RawInputEvent>(events.Count);
        var held = new HashSet<MacroMouseButton>();
        var i = 0;
        while (i < events.Count)
        {
            var e = events[i];
            if (e.Kind != RawInputKind.MouseMove || held.Count > 0)
            {
                if (e.Kind == RawInputKind.MouseDown) held.Add(e.Button);
                else if (e.Kind == RawInputKind.MouseUp) held.Remove(e.Button);
                result.Add(e);
                i++;
                continue;
            }

            // A stretch of free cursor movement: keep at most its last point, and only if a key event comes next.
            var last = i;
            while (last + 1 < events.Count && events[last + 1].Kind == RawInputKind.MouseMove) last++;
            var next = last + 1 < events.Count ? events[last + 1].Kind : (RawInputKind?)null;
            if (next is RawInputKind.KeyDown or RawInputKind.KeyUp) result.Add(events[last]);
            i = last + 1;
        }

        return result;
    }

    /// <summary>
    /// Finds the matching release for a press when nothing else happens in between (mouse movement is
    /// allowed only when <paramref name="skipMoves"/> is true). Anything else in between means it isn't a simple tap.
    /// </summary>
    private static int FindPartner(List<RawInputEvent> events, int start, RawInputKind partnerKind, MacroMouseButton button, int virtualKey, bool skipMoves, out bool hadAutoRepeat)
    {
        hadAutoRepeat = false;
        for (var j = start + 1; j < events.Count; j++)
        {
            var e = events[j];
            if (e.Kind == RawInputKind.MouseMove)
            {
                if (skipMoves) continue;
                return -1;
            }

            if (partnerKind == RawInputKind.KeyUp && e.Kind == RawInputKind.KeyDown && e.VirtualKey == virtualKey)
            {
                hadAutoRepeat = true;
                continue;
            }

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

        var cut = events.Count;
        for (var j = events.Count - 1; j >= 0; j--)
        {
            var e = events[j];
            if (e.Kind is RawInputKind.KeyDown or RawInputKind.KeyUp && trimKeys.Contains(e.VirtualKey))
            {
                cut = j;
                continue;
            }

            if (e.Kind == RawInputKind.MouseMove) continue;
            break;
        }

        if (cut < events.Count)
            events.RemoveRange(cut, events.Count - cut);
    }

    private static double Distance(RawInputEvent a, RawInputEvent b)
    {
        var dx = (double)a.X - b.X;
        var dy = (double)a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
