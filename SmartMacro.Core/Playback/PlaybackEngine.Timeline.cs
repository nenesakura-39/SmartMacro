namespace SmartMacro.Core.Playback;

public sealed partial class PlaybackEngine
{
    // ---------------- Timeline ----------------

    private void ResetTimeline()
    {
        _timelineTarget = _clock.Elapsed;
        _lagAllowance = MaxTimelineLag;
        _carriedDebt = TimeSpan.Zero;
    }

    /// <summary>
    /// After a recorded-style step (click at a position, key press): the time the step took beyond its own schedule
    /// (sending the input) stays on the timeline and the next waits are shortened by it (up to
    /// <see cref="MaxStepOverheadCatchUp"/>), so the macro keeps the recording's exact rhythm instead of drifting.
    /// </summary>
    private void KeepStepOverheadOnTimeline() => _lagAllowance = MaxStepOverheadCatchUp;

    /// <summary>
    /// Ensures <see cref="_timelineTarget"/> is never more than <see cref="MaxTimelineLag"/> behind <see cref="_clock"/>
    /// before scheduling the next wait or glide, preventing accumulated clock debt from skipping steps in 0 ms.
    /// </summary>
    private void ClampTimelineBacklog()
    {
        var now = _clock.Elapsed;
        var lag = now - _timelineTarget;
        var owed = (lag > TimeSpan.Zero ? lag : TimeSpan.Zero) + _carriedDebt;
        if (owed > _lagAllowance)
        {
            // Too far behind to catch up without a burst: restart the timeline from now.
            if (lag > TimeSpan.Zero)
            {
                _timelineShift += lag;
                _timelineTarget = now;
            }

            _carriedDebt = TimeSpan.Zero;
            _lagAllowance = MaxTimelineLag;
        }
    }

    /// <summary>
    /// Resynchronises <see cref="_timelineTarget"/> to <see cref="_clock"/> after a step that performs its own internal
    /// waits or sleeps (clicks, smooth glides, key holds, scrolls, searches, sequences, or condition checks).
    /// </summary>
    private void SyncTimelineToNow()
    {
        _lagAllowance = MaxTimelineLag;
        _carriedDebt = TimeSpan.Zero;
        var now = _clock.Elapsed;
        if (now > _timelineTarget)
        {
            _timelineShift += now - _timelineTarget;
            _timelineTarget = now;
        }
    }

    private void AdvanceTimeline(double milliseconds) => AdvanceTimeline(ToDuration(milliseconds));

    private static TimeSpan ToDuration(double milliseconds) =>
        milliseconds > 0 && double.IsFinite(milliseconds)
            ? TimeSpan.FromTicks((long)Math.Round(milliseconds * TimeSpan.TicksPerMillisecond))
            : TimeSpan.Zero;

    /// <summary>
    /// Schedules a hold (a key or button kept down: a click hold, a key hold, typematic repeats): the hold always lasts its
    /// full length from the moment it really starts, because its length IS the input (a 550 ms rod cast must stay 550 ms).
    /// Time the timeline is behind at that moment isn't taken out of the hold; it stays owed and comes out of the next
    /// gaps (Delays, glides) instead, a little at a time like any other catch-up.
    /// </summary>
    private void AdvanceHold(double milliseconds)
    {
        ClampTimelineBacklog();
        var duration = ToDuration(milliseconds);
        if (duration <= TimeSpan.Zero) return;

        var now = _clock.Elapsed;
        if (_timelineTarget < now)
        {
            _carriedDebt += now - _timelineTarget; // owed, repaid by the following gaps (never by this hold)
            _timelineTarget = now;
        }

        _timelineTarget += duration;
    }

    private void AdvanceTimeline(TimeSpan duration)
    {
        ClampTimelineBacklog();
        if (duration <= TimeSpan.Zero) return;

        var now = _clock.Elapsed;
        var lag = now - _timelineTarget;
        if (lag <= TimeSpan.Zero && _carriedDebt <= TimeSpan.Zero)
        {
            _timelineTarget += duration; // on time: the normal case
            return;
        }

        // Behind schedule (time spent sending input, a slow step, timer overshoot): this wait is shortened by what is
        // owed, but never by more than MaxCatchUpShare of its length, so no input ever fires in a 0 ms burst. The rest
        // stays owed and comes out of the following waits.
        var owed = (lag > TimeSpan.Zero ? lag : TimeSpan.Zero) + _carriedDebt;
        var maxRepay = TimeSpan.FromTicks((long)(duration.Ticks * MaxCatchUpShare));
        var repay = owed < maxRepay ? owed : maxRepay;
        _carriedDebt = owed - repay;
        var from = _timelineTarget > now ? _timelineTarget : now;
        _timelineTarget = from + duration - repay;
    }

    /// <summary>
    /// Waits until the timeline's current deadline. Waits in small chunks so pause and stop respond quickly.
    /// Arriving late (timer overshoot, time spent sending input) is made up by the next wait, so it doesn't add up.
    /// </summary>
    private async Task WaitForTimelineAsync(CancellationToken token)
    {
        while (true)
        {
            await WaitIfPausedAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            var now = _clock.Elapsed;
            var remaining = _timelineTarget - now;
            if (remaining <= TimeSpan.Zero)
            {
                // Far behind (the PC stalled)? Skip ahead rather than firing a burst of late input.
                if (-remaining + _carriedDebt > _lagAllowance)
                {
                    _timelineShift += now - _timelineTarget;
                    _timelineTarget = now;
                    _lagAllowance = MaxTimelineLag;
                    _carriedDebt = TimeSpan.Zero;
                }

                return;
            }

            // Ahead of the deadline again: any step overhead has been caught up (unless some is still owed).
            if (_carriedDebt <= TimeSpan.Zero) _lagAllowance = MaxTimelineLag;

            await _clock.DelayAsync(remaining < MaxDelayChunk ? remaining : MaxDelayChunk, token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A plain relative wait (used inside Type text and Find text, whose own duration isn't part of a recording).
    /// Uses the real elapsed time, so timer inaccuracy doesn't add up over long delays.
    /// </summary>
    private Task WaitAsync(double milliseconds, CancellationToken token) =>
        milliseconds <= 0 ? Task.CompletedTask : WaitAsync(TimeSpan.FromMilliseconds(milliseconds), token);

    /// <summary>The same relative wait, for an exact <see cref="TimeSpan"/> (no rounding through milliseconds).</summary>
    private async Task WaitAsync(TimeSpan duration, CancellationToken token)
    {
        var remaining = duration;
        while (remaining > TimeSpan.Zero)
        {
            await WaitIfPausedAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            var chunk = remaining < MaxDelayChunk ? remaining : MaxDelayChunk;
            var before = _clock.Elapsed;
            await _clock.DelayAsync(chunk, token).ConfigureAwait(false);
            var waited = _clock.Elapsed - before;
            remaining -= waited > TimeSpan.Zero ? waited : chunk;
        }
    }
}
