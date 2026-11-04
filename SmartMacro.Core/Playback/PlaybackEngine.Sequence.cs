using SmartMacro.Core.Models;

namespace SmartMacro.Core.Playback;

public sealed partial class PlaybackEngine
{
    /// <summary>
    /// Plays a recorded-input step: every event is sent at (step start + its recorded time ÷ speed), in the
    /// recorded order. Anything the step pressed and didn't release is released at the end of the step.
    /// Mouse movement:
    /// - Game-mode recordings (no mouse segments): always relative, exactly as before.
    /// - Smart recordings: each event uses its segment's mode. Cursor-position parts move the cursor to the
    ///   recorded position (also right before a click / wheel there); relative parts send the recorded counts.
    ///   Exactly one of the two is sent per event, never both. Movement without a recorded position is relative.
    /// </summary>
    private async Task PlaySequenceAsync(InputSequenceStep step, double speed, HeldInput held, CancellationToken token)
    {
        var events = step.Events;
        var segments = step.HasMouseSegments ? step.MouseSegments! : null;
        var segmentIndex = 0;
        var mode = segments is null ? MouseMovementMode.Relative : segments[0].Effective;
        (int X, int Y)? cursorSent = null; // last position a move put the cursor on (repeated moves are skipped)

        if (step.MoveCursorToStart)
        {
            MoveTo(step.StartX, step.StartY);
            if (mode == MouseMovementMode.CursorPosition)
                cursorSent = (step.StartX, step.StartY);
        }
        else if (CurrentRun > 1 && mode == MouseMovementMode.CursorPosition && (step.StartX != 0 || step.StartY != 0))
        {
            MoveTo(step.StartX, step.StartY);
            cursorSent = (step.StartX, step.StartY);
        }

        // Start this sequence's relative timeline cleanly at t = 0 from the current clock position.
        SyncTimelineToNow();

        var stepStart = _timelineTarget;
        var shiftAtStart = _timelineShift;
        var keysBefore = held.Keys.ToHashSet();
        var buttonsBefore = held.Buttons.ToHashSet();

        // Faster than recorded: remember when each button went down, so a click is never held shorter than
        // MinScaledHoldMs (or its recorded hold, if that was shorter) and games still see it.
        var pressedAt = new Dictionary<MacroMouseButton, (TimeSpan Clock, long TimeUs)>();

        try
        {
            foreach (var e in events)
            {
                // Absolute deadline from the step start (plus any pause / stall shift since then), so rounding
                // never accumulates across thousands of events.
                var offset = TimeSpan.FromTicks((long)Math.Round(e.TimeUs * 10.0 / speed));
                _timelineTarget = stepStart + offset + (_timelineShift - shiftAtStart);
                var shiftBeforeWait = _timelineShift;
                await WaitForTimelineAsync(token).ConfigureAwait(false);
                if (_timelineShift != shiftBeforeWait) cursorSent = null; // paused or stalled: the cursor may have been moved

                if (segments is not null)
                {
                    while (segmentIndex + 1 < segments.Count && segments[segmentIndex + 1].StartUs <= e.TimeUs)
                    {
                        segmentIndex++;
                        mode = segments[segmentIndex].Effective;
                    }
                }

                // In a cursor-position part, mouse events go to their recorded spot first.
                var usePosition = mode == MouseMovementMode.CursorPosition && e.HasPosition && e.IsMouse;

                switch (e.Kind)
                {
                    case InputEventKind.MouseMove:
                        if (usePosition)
                        {
                            if (cursorSent != (e.X, e.Y))
                            {
                                MoveTo(e.X, e.Y);
                                cursorSent = (e.X, e.Y);
                            }
                        }
                        else if (e.Dx != 0 || e.Dy != 0)
                        {
                            SendRelativeMoveLossless(e.Dx, e.Dy);
                            cursorSent = null; // the cursor (if visible) has moved by an unknown number of pixels
                            _cursor = null;
                        }

                        break;
                    case InputEventKind.MouseDown:
                        if (usePosition)
                        {
                            MoveTo(e.X, e.Y);
                            cursorSent = (e.X, e.Y);
                        }

                        _input.MouseButton(e.Button, true);
                        held.Buttons.Add(e.Button);
                        pressedAt[e.Button] = (_clock.Elapsed, e.TimeUs);
                        ClampTimelineBacklog();
                        break;
                    case InputEventKind.MouseUp:
                        if (speed > 1 && pressedAt.Remove(e.Button, out var down))
                        {
                            var recordedHold = TimeSpan.FromTicks(Math.Max(0, e.TimeUs - down.TimeUs) * 10);
                            var minimum = TimeSpan.FromMilliseconds(MinScaledHoldMs) < recordedHold ? TimeSpan.FromMilliseconds(MinScaledHoldMs) : recordedHold;
                            var heldFor = _clock.Elapsed - down.Clock;
                            if (heldFor < minimum) await WaitAsync(minimum - heldFor, token).ConfigureAwait(false);
                        }

                        // Only if the cursor isn't already there (normally the last move put it there).
                        if (usePosition && cursorSent != (e.X, e.Y))
                        {
                            MoveTo(e.X, e.Y);
                            cursorSent = (e.X, e.Y);
                        }

                        _input.MouseButton(e.Button, false);
                        held.Buttons.Remove(e.Button);
                        ClampTimelineBacklog();
                        break;
                    case InputEventKind.Wheel:
                    case InputEventKind.HorizontalWheel:
                        if (usePosition)
                        {
                            MoveTo(e.X, e.Y);
                            cursorSent = (e.X, e.Y);
                        }

                        _input.Scroll(e.Delta, e.Kind == InputEventKind.HorizontalWheel);
                        ClampTimelineBacklog();
                        break;
                    case InputEventKind.KeyDown:
                    {
                        var key = new HeldKey(e.VirtualKey, e.ScanCode, e.Extended);
                        SendKey(key, down: true);
                        held.Keys.Add(key);
                        break;
                    }
                    case InputEventKind.KeyUp:
                    {
                        var key = new HeldKey(e.VirtualKey, e.ScanCode, e.Extended);
                        SendKey(key, down: false);
                        held.Keys.Remove(key);
                        break;
                    }
                }
            }
        }
        finally
        {
            // Normal end only: on stop/failure the engine releases everything anyway.
            if (!token.IsCancellationRequested)
            {
                foreach (var key in held.Keys.Where(k => !keysBefore.Contains(k)).ToList())
                {
                    TryRelease(() => SendKey(key, down: false));
                    held.Keys.Remove(key);
                }

                foreach (var button in held.Buttons.Where(b => !buttonsBefore.Contains(b)).ToList())
                {
                    TryRelease(() => _input.MouseButton(button, false));
                    held.Buttons.Remove(button);
                }
            }

            SyncTimelineToNow();
        }
    }

    private void TryRelease(Action release)
    {
        try { release(); }
        catch (Exception ex) { Message?.Invoke(this, $"Could not release held input: {ex.Message}"); }
    }

    private void SendKey(HeldKey key, bool down)
    {
        if (key.ScanCode == 0 && !key.Extended) _input.Key(key.VirtualKey, down);
        else _input.KeyWithScanCode(key.VirtualKey, key.ScanCode, key.Extended, down);
    }

    private void SendRelativeMoveLossless(int dx, int dy)
    {
        const int maxSingleStepDelta = 30;
        const int targetSliceDelta = 25;
        var maxAxis = Math.Max(Math.Abs(dx), Math.Abs(dy));
        if (maxAxis <= maxSingleStepDelta)
        {
            _input.MoveMouseRelative(dx, dy);
            return;
        }

        var slices = (maxAxis + targetSliceDelta - 1) / targetSliceDelta;
        var prevX = 0;
        var prevY = 0;
        for (var s = 1; s <= slices; s++)
        {
            var curX = (int)Math.Round((double)dx * s / slices);
            var curY = (int)Math.Round((double)dy * s / slices);
            var stepDx = curX - prevX;
            var stepDy = curY - prevY;
            if (stepDx != 0 || stepDy != 0)
                _input.MoveMouseRelative(stepDx, stepDy);
            prevX = curX;
            prevY = curY;
        }
    }
}
