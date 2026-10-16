using System.Diagnostics;
using SmartMacro.Core.Playback;
using static SmartMacro.Services.Input.NativeMethods;

namespace SmartMacro.Services.Input;

/// <summary>
/// Playback clock with sub-millisecond waits. Task.Delay can only wait whole milliseconds and usually
/// overshoots by 1-2 ms, which is too coarse for 1000 Hz game-mode recordings. Short waits here use a
/// Windows high-resolution waitable timer (Windows 10 1803+) and a brief yield-spin for the last fraction
/// of a millisecond. Long waits still use Task.Delay; the engine's timeline makes up any overshoot.
/// Only the playback engine calls this, one wait at a time.
/// </summary>
public sealed class PrecisePlaybackClock : IPlaybackClock, IDisposable
{
    private static readonly TimeSpan PreciseBelow = TimeSpan.FromMilliseconds(20);
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
        if (delay >= PreciseBelow) return Task.Delay(delay, cancellationToken);

        // Short wait: block this (background playback) thread briefly for accuracy.
        var deadline = _stopwatch.Elapsed + delay;
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

        return Task.CompletedTask;
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
