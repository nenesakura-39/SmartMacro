using SmartMacro.Core.Input;
using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Vision;

namespace SmartMacro.Core.Playback;

public sealed partial class PlaybackEngine
{
    // ---------------- Steps ----------------

    /// <summary>
    /// Recorded-style input steps that play on the deadline-synced virtual timeline: a key press and a mouse click at a
    /// position (or where the cursor is). Clicks on something vision/intent found (last found spot, Smart-Aim, Smart Intent)
    /// are not: looking takes as long as it takes, so their timing starts again after the look.
    /// </summary>
    private static bool IsDeadlineSynced(MacroStep step) =>
        step is KeyPressStep || step is MouseClickStep { UseLastFoundText: false, SmartAimAtTarget: false, UseSmartIntent: false };

    /// <summary>
    /// Plays a recorded-style step so its own schedule (its hold) is exact AND the macro's timeline doesn't move: time
    /// the timeline had to skip while sending the input (Windows' hover nudge + settle before a press, ~35 ms) is put
    /// back as "owed" time, which the next Delay / Move then takes out of its wait (up to
    /// <see cref="MaxStepOverheadCatchUp"/>). Paused time is never put back. Before this, every click and key press
    /// pushed the rest of the macro back by its sending time: seconds of drift over a 5-hour AFK loop.
    /// </summary>
    private async Task ExecuteDeadlineSyncedAsync(MacroStep step, double speed, HeldInput held, CancellationToken token)
    {
        var shiftAtStart = _timelineShift;
        var pausedAtStart = _pausedTotal;
        await ExecuteStepAsync(step, speed, held, token).ConfigureAwait(false);

        var skipped = (_timelineShift - shiftAtStart) - (_pausedTotal - pausedAtStart);
        var owed = _clock.Elapsed - _timelineTarget;
        if (skipped > TimeSpan.Zero && owed + skipped + _carriedDebt <= MaxStepOverheadCatchUp)
        {
            _timelineTarget -= skipped;
            _timelineShift -= skipped;
        }

        KeepStepOverheadOnTimeline();
    }

    private async Task ExecuteStepAsync(MacroStep step, double speed, HeldInput held, CancellationToken token)
    {
        switch (step)
        {
            case DelayStep d when _glideDuringDelay is { } next:
            {
                // Recorded point: glide to the next Move's spot over this Delay (its recorded gap), not after it.
                _glideDuringDelay = null;
                ClampTimelineBacklog();
                var deadline = _timelineTarget + TimeSpan.FromTicks((long)Math.Round(d.DurationMs / speed * TimeSpan.TicksPerMillisecond));
                await MoveCursorAsync(next.X, next.Y, next.MoveStyle, d.DurationMs, speed, onTimeline: true, token).ConfigureAwait(false);
                _glideAbsorbedBy = next;
                if (_timelineTarget < deadline)
                {
                    // The glide was skipped (unknown start) or ended early: the Delay still lasts its full time.
                    _timelineTarget = deadline;
                    await WaitForTimelineAsync(token).ConfigureAwait(false);
                }

                break;
            }

            case DelayStep { Mode: not DelayMode.Fixed } d:
                await ExecuteAdaptiveDelayAsync(d, speed, token).ConfigureAwait(false);
                break;

            case DelayStep d:
                if (SmoothMouseMoves && held.Buttons.Count == 0 && _glideTarget is { } target && d.DurationMs > 0)
                {
                    // Wait for the first part of the Delay, then glide to the next step's spot during the rest.
                    var total = Vary(d.DurationMs / speed, HumanizeMinDelayMs);
                    var glide = Math.Min(total, MaxGlideMs);
                    AdvanceTimeline(total - glide);
                    await WaitForTimelineAsync(token).ConfigureAwait(false);
                    var used = await GlideAsync(target, TimeSpan.FromMilliseconds(glide), token).ConfigureAwait(false);
                    // Whatever the glide didn't use (it skipped, or rounding) is still waited for.
                    AdvanceTimeline(TimeSpan.FromMilliseconds(glide) - used);
                }
                else
                {
                    AdvanceTimeline(Vary(d.DurationMs / speed, HumanizeMinDelayMs));
                }

                await WaitForTimelineAsync(token).ConfigureAwait(false);
                break;

            case MouseMoveStep { UseRelativeDelta: true } r:
                // 3D camera / cursor-locked game: relative mouse counts (SendInput MOUSEEVENTF_MOVE, not ABSOLUTE), the
                // way a real mouse turns the camera. Big deltas are sent in small slices so nothing is lost or clamped.
                _glideAbsorbedBy = null;
                SendRelativeMoveLossless(r.DeltaX, r.DeltaY);
                _cursor = null; // where the (possibly hidden) cursor ended up is now unknown
                break;

            case MouseMoveStep m when ReferenceEquals(_glideAbsorbedBy, m):
            {
                // Its glide already happened during the Delay before it: just make sure it ends exactly on the spot.
                _glideAbsorbedBy = null;
                var absorbedTo = ApplyActiveDragShift((m.X, m.Y), held);
                if (_cursor != absorbedTo) MoveTo(absorbedTo.X, absorbedTo.Y);
                break;
            }

            case MouseMoveStep m when m.TargetMode != ClickTargetMode.Coordinates:
            {
                // "To the last found spot (+ offset)" / "By the offset from where the cursor is".
                _glideAbsorbedBy = null;
                if (MoveTargetOf(m) is { } moveTo)
                    await MoveCursorAsync(moveTo.X, moveTo.Y, m.MoveStyle, m.MoveDurationMs, speed, onTimeline: true, token).ConfigureAwait(false);
                else if (m.TargetMode == ClickTargetMode.LastFoundMatch)
                    Message?.Invoke(this, SkippedMoveNoTarget);
                else
                    Message?.Invoke(this, "Move by an offset: the cursor position isn't known, so the cursor wasn't moved.");
                break;
            }

            case MouseMoveStep m:
            {
                _glideAbsorbedBy = null;
                var shiftedMove = ApplyActiveDragShift((m.X, m.Y), held);
                await MoveCursorAsync(shiftedMove.X, shiftedMove.Y, m.MoveStyle, m.MoveDurationMs, speed, onTimeline: true, token).ConfigureAwait(false);
                break;
            }

            case MouseClickStep c:
                (int X, int Y)? start = null; // where the click happens, if known (needed for a smooth drag)
                var onFoundTarget = false;     // clicking something vision found (last found spot / Smart-Aim match)
                // Humanize moves real clicks a few pixels (never a plain Down / Up, which may belong to a recorded drag).
                var jitters = c.Action is MouseClickAction.Click or MouseClickAction.DoubleClick or MouseClickAction.Hold;
                if (c.SendInBackground && c.Action != MouseClickAction.Drag)
                {
                    var bgTitle = InterpolateActive(c.BackgroundWindowTitle);
                    (int X, int Y) bgSpot = (c.X, c.Y);
                    if (c.UseLastFoundText)
                    {
                        if (_lastFound is not { } center)
                        {
                            Message?.Invoke(this, SkippedClickNoTarget);
                            break;
                        }
                        bgSpot = (center.X + c.OffsetX, center.Y + c.OffsetY);
                    }
                    else if (!c.UseCurrentPosition)
                    {
                        if (c.UseSmartIntent)
                        {
                            (bgSpot, _) = await SmartIntentAsync(c, token).ConfigureAwait(false);
                            SyncTimelineToNow();
                        }
                        else if (c.SmartAimAtTarget && c.HasTargetThumbnail)
                        {
                            (bgSpot, _) = await SmartAimAsync(c, token).ConfigureAwait(false);
                            SyncTimelineToNow();
                        }
                    }
                    else if (_input.TryGetCursorPosition(out var cx, out var cy))
                    {
                        bgSpot = (cx, cy);
                    }

                    var bgHold = c.HoldMs > 0 ? (int)Math.Round(ScaledHoldMs(c.HoldMs, speed)) : 0;
                    var uiaHandled = false;
                    if (c.Action == MouseClickAction.Click
                        && (c.BackgroundBackend == BackgroundInputBackend.UiAutomationInvoke
                            || (c.BackgroundBackend == BackgroundInputBackend.Auto && c.Button == MacroMouseButton.Left))
                        && _uiLocator is { IsAvailable: true })
                    {
                        var uiaResult = await _uiLocator.TryBackgroundClickAsync(bgTitle, c.Target, bgSpot, token).ConfigureAwait(false);
                        if (uiaResult.Succeeded)
                        {
                            uiaHandled = true;
                            SyncTimelineToNow();
                        }
                        else if (c.BackgroundBackend == BackgroundInputBackend.UiAutomationInvoke)
                        {
                            Message?.Invoke(this, $"👻 Background click (UI Automation): {uiaResult.Error ?? $"no matching control found in \"{bgTitle}\"."}");
                            uiaHandled = true;
                        }
                    }

                    if (!uiaHandled && !_input.SendBackgroundMouseClick(bgTitle, c.Button, c.Action, bgSpot.X, bgSpot.Y, bgHold))
                    {
                        Message?.Invoke(this, $"👻 Background click: window \"{bgTitle}\" was not found.");
                    }
                    await WaitAfterClickAsync(c, speed, token).ConfigureAwait(false);
                    SyncTimelineToNow();
                    break;
                }

                if (c.UseLastFoundText)
                {
                    // "On the last found spot": its center plus the step's offset (e.g. a cell 100 px right of a board).
                    // Nothing found yet (a Find step set to continue missed, or "Run step" on this step alone): skip it,
                    // never click some default spot like (0, 0).
                    if (_lastFound is not { } center)
                    {
                        Message?.Invoke(this, SkippedClickNoTarget);
                        break;
                    }

                    // The found spot is already in live screen pixels (it was found on this screen in this run), so
                    // "auto-adjust coordinates" never scales it; the offset isn't scaled either, because the template
                    // was matched at its recorded pixel size, so the UI around it has that size too.
                    (int X, int Y) found = (center.X + c.OffsetX, center.Y + c.OffsetY);
                    if (jitters) found = Jitter(found.X, found.Y, _lastFoundBounds);
                    await MoveCursorAsync(found.X, found.Y, c.MoveStyle, c.MoveDurationMs, speed, onTimeline: true, token).ConfigureAwait(false);
                    start = found;
                    onFoundTarget = true;
                }
                else if (!c.UseCurrentPosition)
                {
                    // Smart Intent (5-tier chain) or Smart-Aim (thumbnail): find where the target moved, or fall back to X / Y.
                    (int X, int Y) aimSpot = (c.X, c.Y);
                    var aimBounds = default(ScreenRect);
                    if (c.UseSmartIntent)
                    {
                        (aimSpot, aimBounds) = await SmartIntentAsync(c, token).ConfigureAwait(false);
                        SyncTimelineToNow(); // looking takes as long as it takes; the click's own timing starts now
                    }
                    else if (c.SmartAimAtTarget && c.HasTargetThumbnail)
                    {
                        (aimSpot, aimBounds) = await SmartAimAsync(c, token).ConfigureAwait(false);
                        SyncTimelineToNow(); // looking takes as long as it takes; the click's own timing starts now
                    }
                    else if (c.Action == MouseClickAction.Up && _activeDragShifts.TryGetValue(c.Button, out var upShift))
                    {
                        aimSpot = (c.X + upShift.ShiftX, c.Y + upShift.ShiftY);
                    }

                    (int X, int Y) clickAt = jitters ? Jitter(aimSpot.X, aimSpot.Y, aimBounds) : aimSpot;
                    await MoveCursorAsync(clickAt.X, clickAt.Y, c.MoveStyle, c.MoveDurationMs, speed, onTimeline: true, token).ConfigureAwait(false);
                    start = clickAt;
                    onFoundTarget = !aimBounds.IsEmpty;
                }
                else if (_input.TryGetCursorPosition(out var cursorX, out var cursorY))
                {
                    start = (cursorX, cursorY);
                }

                // Reliable game click on a found target: rest on it TargetHoverMs, then press for at least MinTargetClickHoldMs.
                // (Fixed-position clicks keep their exact recorded timing; the Windows input layer already settles the
                // cursor before a press that follows a jump.)
                if (onFoundTarget && c.Action is MouseClickAction.Click or MouseClickAction.DoubleClick)
                {
                    AdvanceTimeline(TargetHoverMs);
                    await WaitForTimelineAsync(token).ConfigureAwait(false);
                    var hold = Math.Max(MinTargetClickHoldMs, c.HoldMs);
                    await TimelineClickAsync(c.Button, hold, speed, held, token).ConfigureAwait(false);
                    if (c.Action == MouseClickAction.DoubleClick)
                    {
                        AdvanceTimeline(Math.Max(0, c.DoubleClickIntervalMs) / speed);
                        await WaitForTimelineAsync(token).ConfigureAwait(false);
                        await TimelineClickAsync(c.Button, hold, speed, held, token).ConfigureAwait(false);
                    }

                    await WaitAfterClickAsync(c, speed, token).ConfigureAwait(false);
                    SyncTimelineToNow();
                    break;
                }

                switch (c.Action)
                {
                    case MouseClickAction.Click when c.HoldMs > 0:
                        // Keep the button down for the recorded time (HoldDurationMs, at least MinClickHoldMs, ÷ speed;
                        // tracked, so Stop still releases it): a 550 ms rod cast stays a 550 ms cast.
                        _input.MouseButton(c.Button, true);
                        held.Buttons.Add(c.Button);
                        AdvanceHold(ScaledHoldMs(Math.Max(MinClickHoldMs, c.HoldDurationMs), speed));
                        await WaitForTimelineAsync(token).ConfigureAwait(false);
                        _input.MouseButton(c.Button, false);
                        held.Buttons.Remove(c.Button);
                        await WaitAfterClickAsync(c, speed, token).ConfigureAwait(false);
                        break;
                    case MouseClickAction.Click:
                        Click(c.Button);
                        await WaitAfterClickAsync(c, speed, token).ConfigureAwait(false);
                        break;
                    case MouseClickAction.DoubleClick when c.HoldMs > 0 || c.DoubleClickIntervalMs > 0:
                        // Timed double click: hold each click for the click hold, with the gap in between (on the timeline).
                        await TimelineClickAsync(c.Button, c.HoldMs, speed, held, token).ConfigureAwait(false);
                        AdvanceTimeline(Math.Max(0, c.DoubleClickIntervalMs) / speed);
                        await WaitForTimelineAsync(token).ConfigureAwait(false);
                        await TimelineClickAsync(c.Button, c.HoldMs, speed, held, token).ConfigureAwait(false);
                        await WaitAfterClickAsync(c, speed, token).ConfigureAwait(false);
                        break;
                    case MouseClickAction.DoubleClick:
                        Click(c.Button);
                        Click(c.Button);
                        await WaitAfterClickAsync(c, speed, token).ConfigureAwait(false);
                        break;
                    case MouseClickAction.Down:
                        if (onFoundTarget && start is { } foundDown && !c.UseCurrentPosition && !c.UseLastFoundText)
                            _activeDragShifts[c.Button] = (foundDown.X - c.X, foundDown.Y - c.Y);
                        else
                            _activeDragShifts.Remove(c.Button);
                        _input.MouseButton(c.Button, true);
                        held.Buttons.Add(c.Button);
                        break;
                    case MouseClickAction.Up:
                        _activeDragShifts.Remove(c.Button);
                        _input.MouseButton(c.Button, false);
                        held.Buttons.Remove(c.Button);
                        break;
                    case MouseClickAction.Hold:
                        // Press, keep it down for HoldMs (on the timeline, so Speed applies), release.
                        _input.MouseButton(c.Button, true);
                        held.Buttons.Add(c.Button);
                        AdvanceHold(ScaledHoldMs(c.HoldMs, speed));
                        await WaitForTimelineAsync(token).ConfigureAwait(false);
                        _input.MouseButton(c.Button, false);
                        held.Buttons.Remove(c.Button);
                        await WaitAfterClickAsync(c, speed, token).ConfigureAwait(false);
                        break;
                    case MouseClickAction.Drag:
                    {
                        var dragShift = onFoundTarget && start is { } foundDrag && !c.UseCurrentPosition && !c.UseLastFoundText
                            ? (foundDrag.X - c.X, foundDrag.Y - c.Y)
                            : (0, 0);
                        await DragAsync(c, start, dragShift, speed, held, token).ConfigureAwait(false);
                        break;
                    }
                }

                // Deadline-synced: the time Windows took to send the click is taken out of the next Delay, not added.
                KeepStepOverheadOnTimeline();
                break;

            case MouseScrollStep s:
                if (!s.UseCurrentPosition) MoveTo(s.X, s.Y);
                await ScrollInNotchesAsync(s.Delta, s.Horizontal, token).ConfigureAwait(false);
                SyncTimelineToNow();
                break;

            case KeyPressStep k:
            {
                if (k.SendInBackground)
                {
                    var bgTitle = InterpolateActive(k.BackgroundWindowTitle);
                    var bgHold = k.HoldMs > 0 ? (int)Math.Round(ScaledHoldMs(k.HoldMs, speed)) : 0;
                    if (!_input.SendBackgroundKey(bgTitle, k.VirtualKey, k.Action, bgHold, k.Modifiers))
                    {
                        Message?.Invoke(this, $"👻 Background key: window \"{bgTitle}\" was not found.");
                    }
                    SyncTimelineToNow();
                    break;
                }

                var mainKey = new HeldKey(k.VirtualKey, k.ScanCode, k.Extended);
                switch (k.Action)
                {
                    case KeyAction.Press when k.HoldMs > 0:
                    {
                        // Keep the key down for the recorded time, e.g. a 120 ms W tap in a game, or a 2 s hold in Notepad.
                        var modifiers = ModifierKeys(k.Modifiers);
                        foreach (var vk in modifiers)
                        {
                            _input.Key(vk, true);
                            held.PressKey(new HeldKey(vk));
                        }

                        SendKey(mainKey, down: true);
                        held.PressKey(mainKey);

                        // Synthetic SendInput does not trigger OS hardware typematic auto-repeat. For non-modifier keys
                        // held >= 300 ms, wait the standard ~250 ms typematic delay and then emit repeated KeyDown
                        // pulses every ~33 ms (~30 Hz) without intermediate KeyUp, followed by a single KeyUp at the end.
                        if (k.HoldMs >= 300 && !VirtualKeys.IsModifier(k.VirtualKey))
                        {
                            const int typematicDelayMs = 250;
                            const int typematicRepeatMs = 33;
                            var elapsedHoldMs = Math.Min(typematicDelayMs, k.HoldMs);
                            AdvanceHold(elapsedHoldMs / speed);
                            await WaitForTimelineAsync(token).ConfigureAwait(false);

                            while (elapsedHoldMs < k.HoldMs)
                            {
                                SendKey(mainKey, down: true);
                                var stepMs = Math.Min(typematicRepeatMs, k.HoldMs - elapsedHoldMs);
                                AdvanceHold(stepMs / speed);
                                await WaitForTimelineAsync(token).ConfigureAwait(false);
                                elapsedHoldMs += stepMs;
                            }
                        }
                        else
                        {
                            AdvanceHold(ScaledHoldMs(k.HoldMs, speed));
                            await WaitForTimelineAsync(token).ConfigureAwait(false);
                        }

                        SendKey(mainKey, down: false);
                        held.ReleaseKey(mainKey);
                        for (var i = modifiers.Count - 1; i >= 0; i--)
                        {
                            _input.Key(modifiers[i], false);
                            held.ReleaseKey(new HeldKey(modifiers[i]));
                        }

                        break;
                    }
                    case KeyAction.Press:
                    {
                        var modifiers = ModifierKeys(k.Modifiers);
                        foreach (var vk in modifiers) _input.Key(vk, true);
                        SendKey(mainKey, down: true);
                        SendKey(mainKey, down: false);
                        for (var i = modifiers.Count - 1; i >= 0; i--) _input.Key(modifiers[i], false);
                        break;
                    }
                    case KeyAction.Down:
                        SendKey(mainKey, down: true);
                        held.PressKey(mainKey);
                        break;
                    case KeyAction.Up:
                        SendKey(mainKey, down: false);
                        held.ReleaseKey(mainKey);
                        break;
                }

                // Deadline-synced like clicks: sending the key doesn't push the rest of the macro back.
                KeepStepOverheadOnTimeline();
                break;
            }

            case TypeTextStep t:
                if (t.SendInBackground)
                {
                    var bgTitle = InterpolateActive(t.BackgroundWindowTitle);
                    var bgText = InterpolateActive(t.Text);
                    var bgDelay = t.CharacterDelayMs > 0 ? (int)Math.Round(t.CharacterDelayMs / Math.Max(0.1, speed)) : 0;
                    var uiaHandled = false;
                    if (t.BackgroundBackend != BackgroundInputBackend.Win32PostMessage
                        && _uiLocator is { IsAvailable: true })
                    {
                        var uiaResult = await _uiLocator.TryBackgroundSetValueAsync(bgTitle, bgText, token).ConfigureAwait(false);
                        if (uiaResult.Succeeded)
                        {
                            uiaHandled = true;
                        }
                        else if (t.BackgroundBackend == BackgroundInputBackend.UiAutomationInvoke)
                        {
                            Message?.Invoke(this, $"👻 Background type (UI Automation): {uiaResult.Error ?? $"no editable control found in \"{bgTitle}\"."}");
                            uiaHandled = true;
                        }
                    }

                    if (!uiaHandled && !_input.SendBackgroundText(bgTitle, bgText, bgDelay))
                    {
                        Message?.Invoke(this, $"👻 Background type: window \"{bgTitle}\" was not found.");
                    }
                    SyncTimelineToNow();
                    break;
                }
                await TypeTextAsync(t, speed, token).ConfigureAwait(false);
                SyncTimelineToNow(); // typing takes as long as it takes; start timing again from here
                break;

            case FindTextStep or FindImageStep or FindColorStep:
                await SearchAsync((ScreenSearchStep)step, token).ConfigureAwait(false);
                SyncTimelineToNow(); // the search takes as long as it takes; start timing again from here
                break;

            case InputSequenceStep q:
                await PlaySequenceAsync(q, speed, held, token).ConfigureAwait(false);
                break;

            case WaitForChangeStep w:
                await WaitForChangeAsync(w, held, token).ConfigureAwait(false);
                SyncTimelineToNow();
                break;

            case FocusWindowStep w:
                await FocusWindowAsync(w, token).ConfigureAwait(false);
                SyncTimelineToNow();
                break;

            case ScreenshotStep s:
                await TakeScreenshotAsync(s, token).ConfigureAwait(false);
                SyncTimelineToNow();
                break;

            default:
                throw new NotSupportedException($"Step type {step.GetType().Name} can't be played.");
        }
    }

    private (int X, int Y) ApplyActiveDragShift((int X, int Y) point, HeldInput held)
    {
        if (held.Buttons.Count == 0 || _activeDragShifts.Count == 0)
            return point;

        foreach (var btn in held.Buttons)
        {
            if (_activeDragShifts.TryGetValue(btn, out var shift) && (shift.ShiftX != 0 || shift.ShiftY != 0))
            {
                return (point.X + shift.ShiftX, point.Y + shift.ShiftY);
            }
        }

        return point;
    }

    /// <summary>Time between cursor updates while dragging (about 120 per second, so sliders follow smoothly).</summary>
    public static readonly TimeSpan DragStepInterval = TimeSpan.FromMilliseconds(8);

    /// <summary>
    /// Drag: press at the start, move the cursor to (EndX, EndY) (translated by <paramref name="shift"/> when Smart Intent
    /// or Smart-Aim relocated the drag's start point) in small steps over DragDurationMs (on the timeline, so Speed
    /// applies and Pause / Stop work in between), release at the end. Supports <see cref="MouseMoveStyle.SmoothGlide"/>
    /// and <see cref="MouseMoveStyle.HumanCurve"/> trajectories via <see cref="MouseMotion.Plan"/>.
    /// </summary>
    private async Task DragAsync(
        MouseClickStep c,
        (int X, int Y)? start,
        (int ShiftX, int ShiftY) shift,
        double speed,
        HeldInput held,
        CancellationToken token)
    {
        _input.MouseButton(c.Button, true);
        held.Buttons.Add(c.Button);

        var end = (X: c.EndX + shift.ShiftX, Y: c.EndY + shift.ShiftY);
        var duration = TimeSpan.FromMilliseconds(Vary(Math.Max(0, c.DragDurationMs) / speed));

        if (start is { } curveFrom
            && duration > TimeSpan.Zero
            && c.MoveStyle is { } dragStyle
            && dragStyle is MouseMoveStyle.SmoothGlide or MouseMoveStyle.HumanCurve)
        {
            var arc = 0.0;
            if ((_humanize && dragStyle == MouseMoveStyle.SmoothGlide) || dragStyle == MouseMoveStyle.HumanCurve)
            {
                var length = Math.Sqrt(Math.Pow(end.X - curveFrom.X, 2) + Math.Pow(end.Y - curveFrom.Y, 2));
                arc = Math.Min(length * HumanizeArc, HumanizeMaxArcPixels);
            }

            var plan = MouseMotion.Plan(curveFrom, end, dragStyle, duration, DragStepInterval, arc);
            var previousAt = TimeSpan.Zero;
            (int X, int Y)? sent = curveFrom;
            var moved = false;

            foreach (var point in plan)
            {
                var wait = point.At - previousAt;
                previousAt = point.At;
                if (wait > TimeSpan.Zero)
                {
                    AdvanceTimeline(wait);
                    await WaitForTimelineAsync(token).ConfigureAwait(false);
                }

                if (sent == (point.X, point.Y)) continue;
                MoveTo(point.X, point.Y);
                sent = (point.X, point.Y);
                moved = true;
            }

            if (!moved || sent != end)
                MoveTo(end.X, end.Y);
        }
        else
        {
            var steps = start is null ? 1 : Math.Max(1, (int)Math.Ceiling(duration / DragStepInterval));
            var stepTime = duration / steps;
            for (var i = 1; i <= steps; i++)
            {
                if (stepTime > TimeSpan.Zero)
                {
                    AdvanceTimeline(stepTime); // burst-safe: each drag point keeps at least half its spacing
                    await WaitForTimelineAsync(token).ConfigureAwait(false);
                }

                if (start is { } from && i < steps)
                {
                    var t = i / (double)steps;
                    MoveTo((int)Math.Round(from.X + (end.X - from.X) * t), (int)Math.Round(from.Y + (end.Y - from.Y) * t));
                }
                else
                {
                    MoveTo(end.X, end.Y); // always end exactly on the target
                }
            }
        }

        _input.MouseButton(c.Button, false);
        held.Buttons.Remove(c.Button);
        SyncTimelineToNow();
    }

    /// <summary>
    /// Where a Move mouse step with a non-coordinate target goes: the last found spot + offset, or the cursor + offset.
    /// Null when the cursor position isn't known (relative / camera movement before, and the simulator can't tell).
    /// Throws when "the last found spot" is used before anything was found.
    /// </summary>
    private (int X, int Y)? MoveTargetOf(MouseMoveStep m)
    {
        switch (m.TargetMode)
        {
            case ClickTargetMode.LastFoundMatch:
            {
                if (_lastFound is not { } center) return null; // nothing found yet: the caller skips the move
                return (center.X + m.OffsetX, center.Y + m.OffsetY);
            }

            case ClickTargetMode.CurrentCursor:
            {
                (int X, int Y) from;
                if (_input.TryGetCursorPosition(out var cx, out var cy)) from = (cx, cy);
                else if (_cursor is { } known) from = known;
                else return null;
                return (from.X + m.OffsetX, from.Y + m.OffsetY);
            }

            default:
                return (m.X, m.Y);
        }
    }

    /// <summary>Absolute cursor move; remembers the spot so a later glide or adaptive delay knows where it happened.</summary>
    private void MoveTo(int x, int y)
    {
        _input.MoveMouse(x, y);
        _cursor = (x, y);
        _lastActionSpot = (x, y);
    }

    private const int AdaptiveWatchWidth = 320;
    private const int AdaptiveWatchHeight = 240;
    private const int AdaptivePollMs = 40;
    private const double SettleMaxChangedPercent = 0.25;
    private const double ChangeMinChangedPercent = 0.40;

    /// <summary>
    /// Adaptive Timing (v1.1): watches a region around the most recent action spot and advances early when screen motion
    /// settles (<see cref="DelayMode.UntilScreenSettles"/>) or when a visible change appears (<see cref="DelayMode.UntilScreenChanges"/>),
    /// bounded by <see cref="DelayStep.MinDurationMs"/> and <see cref="DelayStep.EffectiveMaxDurationMs"/>.
    /// Falls back cleanly to <see cref="DelayStep.DurationMs"/> when screen capture is unavailable.
    /// </summary>
    private async Task ExecuteAdaptiveDelayAsync(DelayStep d, double speed, CancellationToken token)
    {
        if (speed <= 0 || !double.IsFinite(speed)) speed = 1.0;

        if (_capture is not { } capture)
        {
            AdvanceTimeline(Vary(d.DurationMs / speed, HumanizeMinDelayMs));
            await WaitForTimelineAsync(token).ConfigureAwait(false);
            return;
        }

        var virtualScreen = capture.GetVirtualScreen();
        if (virtualScreen.IsEmpty)
        {
            AdvanceTimeline(Vary(d.DurationMs / speed, HumanizeMinDelayMs));
            await WaitForTimelineAsync(token).ConfigureAwait(false);
            return;
        }

        var minMs = Math.Max(0, d.MinDurationMs) / speed;
        var maxMs = Math.Max(minMs, d.EffectiveMaxDurationMs / speed);
        var quietMs = Math.Max(AdaptivePollMs, d.EffectiveQuietWindowMs / speed);

        var center = _lastActionSpot ?? _cursor ?? (virtualScreen.CenterX, virtualScreen.CenterY);
        var watched = new ScreenRect(
            center.X - AdaptiveWatchWidth / 2,
            center.Y - AdaptiveWatchHeight / 2,
            AdaptiveWatchWidth,
            AdaptiveWatchHeight).Intersect(virtualScreen);
        if (watched.IsEmpty) watched = virtualScreen;

        await WaitIfPausedAsync(token).ConfigureAwait(false);
        await ClearTargetOverlayAsync(token).ConfigureAwait(false);

        var startedAt = ActiveTime;
        double ElapsedActiveMs() => (ActiveTime - startedAt).TotalMilliseconds;

        if (d.Mode == DelayMode.UntilScreenChanges)
        {
            ScreenFrame baseline;
            try
            {
                baseline = await capture.CaptureAsync(watched, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                await WaitAsync(d.DurationMs / speed, token).ConfigureAwait(false);
                SyncTimelineToNow();
                return;
            }

            if (minMs > 0) await WaitAsync(minMs, token).ConfigureAwait(false);

            while (true)
            {
                await WaitIfPausedAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                try
                {
                    var frame = await capture.CaptureAsync(watched, token).ConfigureAwait(false);
                    var diff = ScreenChangeDetector.Compare(baseline, frame, tolerance: 12);
                    if (diff.ChangedPercent >= ChangeMinChangedPercent)
                    {
                        break;
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    break;
                }

                var remaining = maxMs - ElapsedActiveMs();
                if (remaining <= 0) break;
                await WaitAsync(Math.Min(AdaptivePollMs, remaining), token).ConfigureAwait(false);
            }

            SyncTimelineToNow();
            return;
        }

        // UntilScreenSettles: wait at least minMs, then continue once consecutive frames stay calm for quietMs.
        if (minMs > 0) await WaitAsync(minMs, token).ConfigureAwait(false);

        ScreenFrame previous;
        try
        {
            previous = await capture.CaptureAsync(watched, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            var fallback = Math.Max(0, d.DurationMs / speed - minMs);
            if (fallback > 0) await WaitAsync(fallback, token).ConfigureAwait(false);
            SyncTimelineToNow();
            return;
        }

        var quietSince = ActiveTime;
        while (true)
        {
            var remaining = maxMs - ElapsedActiveMs();
            if (remaining <= 0) break;

            await WaitAsync(Math.Min(AdaptivePollMs, remaining), token).ConfigureAwait(false);
            await WaitIfPausedAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            try
            {
                var current = await capture.CaptureAsync(watched, token).ConfigureAwait(false);
                var diff = ScreenChangeDetector.Compare(previous, current, tolerance: 10);
                previous = current;

                if (diff.ChangedPercent <= SettleMaxChangedPercent)
                {
                    if ((ActiveTime - quietSince).TotalMilliseconds >= quietMs)
                    {
                        break;
                    }
                }
                else
                {
                    quietSince = ActiveTime;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                break;
            }
        }

        SyncTimelineToNow();
    }

    /// <summary>
    /// Where a step jumps the cursor to before acting (null if it doesn't move the cursor to a fixed spot). Steps with their
    /// own movement style (Smooth glide / Human curve) glide by themselves, so they aren't listed here.
    /// </summary>
    private static (int X, int Y)? JumpTargetOf(MacroStep? step) => step switch
    {
        MouseMoveStep { Enabled: true, UseRelativeDelta: false, TargetMode: ClickTargetMode.Coordinates, MoveStyle: null or MouseMoveStyle.Instant } m => (m.X, m.Y),
        MouseClickStep { Enabled: true, UseLastFoundText: false, UseCurrentPosition: false, SmartAimAtTarget: false, UseSmartIntent: false, MoveStyle: null or MouseMoveStyle.Instant } c => (c.X, c.Y),
        MouseScrollStep { Enabled: true, UseCurrentPosition: false } s => (s.X, s.Y),
        _ => null,
    };

    /// <summary>
    /// The Move step that should glide during <paramref name="delay"/> (see <see cref="RecordedPointMaxDelayMs"/>), or
    /// null: only an enabled Move mouse step with Smooth glide / Human curve whose glide is longer than a short Delay.
    /// </summary>
    private static MouseMoveStep? RecordedGlideAfter(DelayStep delay, MacroStep next) =>
        delay.Mode == DelayMode.Fixed
        && next is MouseMoveStep { Enabled: true, UseRelativeDelta: false, TargetMode: ClickTargetMode.Coordinates, MoveStyle: MouseMoveStyle.SmoothGlide or MouseMoveStyle.HumanCurve } move
        && delay.DurationMs > 0 && delay.DurationMs <= RecordedPointMaxDelayMs && move.MoveDurationMs > delay.DurationMs
            ? move
            : null;

    /// <summary>
    /// Smooth mouse movement (see <see cref="SmoothMouseMoves"/>): glides the cursor from where it is to
    /// <paramref name="target"/> over <paramref name="duration"/> (on the timeline, so the time is taken out of the
    /// Delay that comes before the jump). Does nothing when the start is unknown or the jump is tiny.
    /// </summary>
    /// <returns>How much of <paramref name="duration"/> the glide scheduled (zero when it didn't glide).</returns>
    private async Task<TimeSpan> GlideAsync((int X, int Y) target, TimeSpan duration, CancellationToken token)
    {
        (int X, int Y) from;
        if (_cursor is { } known) from = known;
        else if (_input.TryGetCursorPosition(out var cx, out var cy)) from = (cx, cy);
        else return TimeSpan.Zero;
        if (Math.Abs(target.X - from.X) + Math.Abs(target.Y - from.Y) < 3) return TimeSpan.Zero;

        var steps = Math.Max(1, (int)Math.Floor(duration / GlideStepInterval));
        var stepTime = duration / steps;
        var scheduled = TimeSpan.Zero;
        for (var i = 1; i <= steps; i++)
        {
            // Through the scheduler (not "target += step"), so a glide that starts behind schedule never sends its
            // points in a 0 ms burst: each one keeps at least half of its spacing.
            AdvanceTimeline(stepTime);
            scheduled += stepTime;
            await WaitForTimelineAsync(token).ConfigureAwait(false);
            var t = i / (double)steps;
            // Ease in and out, like a hand: slow start, fast middle, slow arrival.
            var e = t * t * (3 - 2 * t);
            MoveTo((int)Math.Round(from.X + (target.X - from.X) * e), (int)Math.Round(from.Y + (target.Y - from.Y) * e));
        }

        return scheduled;
    }

    /// <summary>
    /// A click for "when found" actions: press, keep it down for the click hold (default <see cref="FoundClickHoldMs"/>;
    /// tracked, so Stop releases it), release. Games that read input once per frame then always see the press.
    /// </summary>
    private async Task HeldClickAsync(MacroMouseButton button, int holdMs, HeldInput held, CancellationToken token)
    {
        _input.MouseButton(button, true);
        held.Buttons.Add(button);
        try
        {
            await WaitAsync(Math.Max(0, holdMs), token).ConfigureAwait(false);
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                _input.MouseButton(button, false);
                held.Buttons.Remove(button);
            }
        }
    }

    private void Click(MacroMouseButton button)
    {
        _input.MouseButton(button, true);
        _input.MouseButton(button, false);
    }

    /// <summary>One click on the timeline: press, keep it down <paramref name="holdMs"/> (÷ speed; 0 = instant), release.</summary>
    private async Task TimelineClickAsync(MacroMouseButton button, int holdMs, double speed, HeldInput held, CancellationToken token)
    {
        if (holdMs <= 0)
        {
            Click(button);
            return;
        }

        _input.MouseButton(button, true);
        held.Buttons.Add(button);
        AdvanceHold(ScaledHoldMs(holdMs, speed));
        await WaitForTimelineAsync(token).ConfigureAwait(false);
        _input.MouseButton(button, false);
        held.Buttons.Remove(button);
    }

    /// <summary>A Mouse click step's "wait after click" (on the timeline, so Speed applies). 0 = none (older files).</summary>
    private async Task WaitAfterClickAsync(MouseClickStep c, double speed, CancellationToken token)
    {
        if (c.PostActionDelayMs <= 0) return;
        AdvanceTimeline(c.PostActionDelayMs / speed);
        await WaitForTimelineAsync(token).ConfigureAwait(false);
    }

    /// <summary>
    /// Moves the cursor to (x, y) in the given style (see <see cref="MouseMotion"/>): null / Instant jump straight there
    /// (the old behaviour); Smooth glide and Human curve send a real mouse move every ~8 ms for
    /// <paramref name="durationMs"/> (÷ speed), ending exactly on the target. On the timeline (Move / Click steps) the
    /// glide time is part of the macro's schedule; otherwise (Find steps, conditions) it's a plain wait. Pause / Stop /
    /// Esc work during the glide. When the start is unknown (after camera movement), the cursor simply jumps.
    /// </summary>
    private async Task MoveCursorAsync(int x, int y, MouseMoveStyle? style, int durationMs, double speed, bool onTimeline, CancellationToken token)
    {
        if (style is not { } glide || glide == MouseMoveStyle.Instant || durationMs <= 0)
        {
            MoveTo(x, y);
            return;
        }

        (int X, int Y) from;
        if (_cursor is { } known) from = known;
        else if (_input.TryGetCursorPosition(out var cx, out var cy)) from = (cx, cy);
        else
        {
            MoveTo(x, y);
            return;
        }

        var duration = TimeSpan.FromMilliseconds(Vary(durationMs / (speed > 0 ? speed : 1.0)));

        // Humanize: a smooth glide bends into a slight, random natural arc instead of a ruler-straight line.
        var arc = 0.0;
        if (_humanize && glide == MouseMoveStyle.SmoothGlide)
        {
            var length = Math.Sqrt(Math.Pow(x - from.X, 2) + Math.Pow(y - from.Y, 2));
            arc = Math.Min(length * HumanizeArc, HumanizeMaxArcPixels) * (0.4 + 0.6 * HumanizeRandom.NextDouble())
                  * (HumanizeRandom.Next(2) == 0 ? 1 : -1);
        }

        var plan = MouseMotion.Plan(from, (x, y), glide, duration, GlideStepInterval, arc);

        var previousAt = TimeSpan.Zero;
        (int X, int Y)? sent = from;
        var moved = false;
        foreach (var point in plan)
        {
            var wait = point.At - previousAt;
            previousAt = point.At;
            if (wait > TimeSpan.Zero)
            {
                if (onTimeline)
                {
                    AdvanceTimeline(wait); // burst-safe (far behind: the glide starts from "now")
                    await WaitForTimelineAsync(token).ConfigureAwait(false);
                }
                else
                {
                    await WaitAsync(wait, token).ConfigureAwait(false);
                }
            }

            if (sent == (point.X, point.Y)) continue; // a slow start can repeat a pixel: don't send duplicates
            MoveTo(point.X, point.Y);
            sent = (point.X, point.Y);
            moved = true;
        }

        if (!moved || sent != (x, y)) MoveTo(x, y);
        if (onTimeline) SyncTimelineToNow();
    }

    private async Task TypeTextAsync(TypeTextStep step, double speed, CancellationToken token)
    {
        var text = InterpolateActive(step.Text).Replace("\r\n", "\n").Replace('\r', '\n');
        for (var i = 0; i < text.Length; i++)
        {
            token.ThrowIfCancellationRequested();

            var length = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            var t0 = _clock.Elapsed;
            _input.TypeCharacters(text.Substring(i, length));
            var typedMs = (_clock.Elapsed - t0).TotalMilliseconds;
            i += length - 1;

            if (step.CharacterDelayMs > 0 && i < text.Length - 1)
            {
                var remainingMs = (step.CharacterDelayMs / speed) - typedMs;
                if (remainingMs > 0.25)
                    await WaitAsync(remainingMs, token).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Windows' wheel amount for one notch (WHEEL_DELTA).</summary>
    public const int WheelNotch = 120;

    /// <summary>Gap between the notches of a multi-notch Scroll step.</summary>
    public static readonly TimeSpan ScrollNotchSpacing = TimeSpan.FromMilliseconds(15);

    /// <summary>
    /// Sends a Scroll step's amount as separate one-notch (120) wheel events, 15 ms apart, like a real wheel
    /// turning. Games (Roblox camera zoom, scrollable shop lists) and some browsers only count one notch per wheel
    /// message, so a single -360 event scrolled just once. An amount that isn't whole notches (from a touchpad or an
    /// old recording) sends the rest as one smaller event at the end. The spacing is on the playback timeline, so
    /// it never shortens the delays that follow.
    /// </summary>
    private async Task ScrollInNotchesAsync(int delta, bool horizontal, CancellationToken token)
    {
        var sign = Math.Sign(delta);
        var remaining = Math.Abs((long)delta);
        var first = true;
        // Space the notches from "now" (if earlier steps ran late, don't fire the notches as one burst).
        if (remaining > WheelNotch && _timelineTarget < _clock.Elapsed) _timelineTarget = _clock.Elapsed;
        while (remaining > 0)
        {
            if (!first)
            {
                _timelineTarget += ScrollNotchSpacing;
                await WaitForTimelineAsync(token).ConfigureAwait(false);
            }

            first = false;
            var part = (int)Math.Min(remaining, WheelNotch);
            _input.Scroll(sign * part, horizontal);
            remaining -= part;
        }
    }

    private static List<int> ModifierKeys(KeyModifiers modifiers)
    {
        var keys = new List<int>(4);
        if (modifiers.HasFlag(KeyModifiers.Control)) keys.Add(0x11);
        if (modifiers.HasFlag(KeyModifiers.Shift)) keys.Add(0x10);
        if (modifiers.HasFlag(KeyModifiers.Alt)) keys.Add(0x12);
        if (modifiers.HasFlag(KeyModifiers.Windows)) keys.Add(0x5B);
        return keys;
    }
}
