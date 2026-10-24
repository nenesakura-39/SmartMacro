using System.Diagnostics;
using SmartMacro.Core.Playback;
using static SmartMacro.Services.Input.NativeMethods;

namespace SmartMacro.Services.Input;

/// <summary>
/// Playback clock with sub-millisecond waits (deadline-synced, see <see cref="HighPrecisionWait"/>). Task.Delay can
/// only wait whole milliseconds and usually overshoots by 1-2 ms (up to 15 ms without the 1 ms timer resolution),
/// which is too coarse for 1000 Hz game-mode recordings. Short waits here use a Windows high-resolution waitable timer
/// (Windows 10 1803+) and a brief yield-spin for the last fraction of a millisecond. Long waits use Task.Delay for all
/// but their last ~10 ms and then the same precise wait, so they end on their Stopwatch deadline too.
/// Only the playback engine calls this, one wait at a time.
/// </summary>
public sealed class PrecisePlaybackClock : IPlaybackClock, IDisposable
{
    private static readonly TimeSpan SpinBelow = TimeSpan.FromMilliseconds(0.3);

    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly object _lock = new();
    private IntPtr _timer;

    public PrecisePlaybackClock()
    {
        _timer = CreateWaitableTimerExW(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
    }

    /// <summary>False on older Windows versions, where waits fall back to 1 ms sleeps.</summary>
    public bool HasHighResolutionTimer => _timer != IntPtr.Zero;

    public TimeSpan Elapsed => _stopwatch.Elapsed;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero) return Task.CompletedTask;

        // One Stopwatch deadline for the whole wait. Long waits: Task.Delay for all but the last ~10 ms (it wakes up
        // late, by up to a few ms even with the 1 ms timer resolution), then the precise wait below for the rest, so the
        // wait ends on its deadline instead of 1–15 ms after it.
        var deadline = _stopwatch.Elapsed + delay;
        var coarse = HighPrecisionWait.CoarsePart(delay);
        if (coarse > TimeSpan.Zero) return LongDelayAsync(coarse, deadline, cancellationToken);

        WaitPreciselyUntil(deadline, cancellationToken);
        return Task.CompletedTask;
    }

    private async Task LongDelayAsync(TimeSpan coarse, TimeSpan deadline, CancellationToken cancellationToken)
    {
        await Task.Delay(coarse, cancellationToken).ConfigureAwait(false);
        WaitPreciselyUntil(deadline, cancellationToken);
    }

    /// <summary>
    /// Short wait (≤ ~16 ms): blocks this (background playback) thread briefly for accuracy: a high-resolution waitable
    /// timer for most of it, then a yield-spin for the last fraction of a millisecond.
    /// </summary>
    private void WaitPreciselyUntil(TimeSpan deadline, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = deadline - _stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero) break;

                if (remaining <= SpinBelow)
                {
                    Thread.Yield();
                }
                else if (_timer != IntPtr.Zero)
                {
                    // Negative = relative time, in 100 ns units. Wake a little early and spin the rest.
                    var due = -(remaining - SpinBelow).Ticks;
                    if (SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                        WaitForSingleObject(_timer, 100);
                    else
                        Thread.Sleep(1);
                }
                else
                {
                    Thread.Sleep(remaining.TotalMilliseconds >= 1.5 ? 1 : 0);
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_timer == IntPtr.Zero) return;
            CloseHandle(_timer);
            _timer = IntPtr.Zero;
        }
    }
}
