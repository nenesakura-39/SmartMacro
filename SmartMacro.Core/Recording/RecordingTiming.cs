using SmartMacro.Core.Models;

namespace SmartMacro.Core.Recording;

/// <summary>
/// Turns high-resolution timestamps (Stopwatch ticks) into the whole-millisecond times the desktop recorder stores,
/// without rounding drift. The hook's own time stamp (GetTickCount) only moves in ~15.6 ms jumps, so two events 5 ms
/// apart could get the same time, or 16 ms apart. This clock rounds the EXACT time since the start each time, so the
/// error of one event is at most 0.5 ms and it never adds up: 1,000 events 16.667 ms apart end at 16,667 ms, not
/// 17,000 or 16,000. Times never go backwards.
/// </summary>
public sealed class RecordingClock
{
    private readonly long _start;
    private readonly double _ticksPerMs;
    private long _lastMs;

    public RecordingClock(long startTimestamp, long ticksPerSecond)
    {
        if (ticksPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(ticksPerSecond));
        _start = startTimestamp;
        _ticksPerMs = ticksPerSecond / 1000.0;
    }

    /// <summary>A clock that starts now (System.Diagnostics.Stopwatch).</summary>
    public static RecordingClock StartNew() =>
        new(System.Diagnostics.Stopwatch.GetTimestamp(), System.Diagnostics.Stopwatch.Frequency);

    /// <summary>Exact milliseconds since the start (with the fraction).</summary>
    public double ExactMs(long timestamp) => (timestamp - _start) / _ticksPerMs;

    /// <summary>Whole milliseconds since the start, rounded from the exact time (never earlier than the previous call).</summary>
    public long ToMilliseconds(long timestamp)
    {
        var ms = (long)Math.Round(Math.Max(0, ExactMs(timestamp)), MidpointRounding.AwayFromZero);
        if (ms < _lastMs) ms = _lastMs;
        _lastMs = ms;
        return ms;
    }
}

/// <summary>
/// Cuts exact (fractional) intervals into whole-millisecond delays and carries the left-over fraction into the next
/// one ("16.4 + 16.4 + 16.4 ms" becomes 16 + 17 + 16 ms = 49 ms, the same total). Summing the results is always within
/// 0.5 ms of summing the exact intervals, however many there are.
/// </summary>
public sealed class DelayQuantizer
{
    private double _fractionalMsRemainder;

    /// <summary>The fraction (−0.5 … 0.5 ms) owed to the next interval.</summary>
    public double Remainder => _fractionalMsRemainder;

    public int Next(double exactMs)
    {
        if (!double.IsFinite(exactMs) || exactMs < 0) exactMs = 0;
        var wanted = exactMs + _fractionalMsRemainder;
        var whole = (int)Math.Round(wanted, MidpointRounding.AwayFromZero);
        if (whole < 0) whole = 0;
        _fractionalMsRemainder = wanted - whole;
        return whole;
    }

    public void Reset() => _fractionalMsRemainder = 0;
}

/// <summary>
/// Recording clean-up shared by the recorders: what is still physically held down when recording stops, so the
/// recording can release it at that exact moment (the hold keeps its real length and the macro never leaves a key or
/// button stuck down).
/// </summary>
public static class RecordingCleanup
{
    /// <summary>
    /// Releases (KeyUp / MouseUp at <paramref name="stopTimeMs"/>) for every key and button that went down during the
    /// recording and wasn't released. A key's OS auto-repeat counts as the same press. Keys first (newest press first),
    /// then buttons, at the cursor's last known spot.
    /// </summary>
    public static List<RawInputEvent> ReleasesAtStop(IReadOnlyList<RawInputEvent> events, long stopTimeMs)
    {
        ArgumentNullException.ThrowIfNull(events);
        var keys = new List<int>();
        var buttons = new List<MacroMouseButton>();
        int x = 0, y = 0;
        var lastTime = 0L;
        foreach (var e in events)
        {
            lastTime = Math.Max(lastTime, e.TimeMs);
            switch (e.Kind)
            {
                case RawInputKind.KeyDown:
                    if (!keys.Contains(e.VirtualKey)) keys.Add(e.VirtualKey);
                    break;
                case RawInputKind.KeyUp:
                    keys.Remove(e.VirtualKey);
                    break;
                case RawInputKind.MouseDown:
                    if (!buttons.Contains(e.Button)) buttons.Add(e.Button);
                    (x, y) = (e.X, e.Y);
                    break;
                case RawInputKind.MouseUp:
                    buttons.Remove(e.Button);
                    (x, y) = (e.X, e.Y);
                    break;
                default:
                    (x, y) = (e.X, e.Y);
                    break;
            }
        }

        var at = Math.Max(stopTimeMs, lastTime);
        var releases = new List<RawInputEvent>(keys.Count + buttons.Count);
        for (var i = keys.Count - 1; i >= 0; i--) releases.Add(new RawInputEvent(at, RawInputKind.KeyUp, VirtualKey: keys[i]));
        foreach (var button in buttons) releases.Add(new RawInputEvent(at, RawInputKind.MouseUp, x, y, button));
        return releases;
    }

    /// <summary>The same for smart / game recordings (microsecond times, scan codes kept from the press).</summary>
    public static List<CapturedInputEvent> ReleasesAtStop(IReadOnlyList<CapturedInputEvent> events, long stopTimeUs)
    {
        ArgumentNullException.ThrowIfNull(events);
        var keys = new List<(int Vk, int Scan, bool Ext)>();
        var buttons = new List<MacroMouseButton>();
        var hasPosition = false;
        int x = 0, y = 0;
        var lastTime = 0L;
        foreach (var captured in events)
        {
            var e = captured.Input;
            lastTime = Math.Max(lastTime, e.TimeUs);
            if (e.HasPosition && e.IsMouse)
            {
                hasPosition = true;
                (x, y) = (e.X, e.Y);
            }

            switch (e.Kind)
            {
                case InputEventKind.KeyDown:
                    if (!keys.Contains((e.VirtualKey, e.ScanCode, e.Extended))) keys.Add((e.VirtualKey, e.ScanCode, e.Extended));
                    break;
                case InputEventKind.KeyUp:
                    keys.Remove((e.VirtualKey, e.ScanCode, e.Extended));
                    break;
                case InputEventKind.MouseDown:
                    if (!buttons.Contains(e.Button)) buttons.Add(e.Button);
                    break;
                case InputEventKind.MouseUp:
                    buttons.Remove(e.Button);
                    break;
            }
        }

        var at = Math.Max(stopTimeUs, lastTime);
        var releases = new List<CapturedInputEvent>(keys.Count + buttons.Count);
        for (var i = keys.Count - 1; i >= 0; i--)
            releases.Add(new CapturedInputEvent(InputEvent.KeyUp(at, keys[i].Vk, keys[i].Scan, keys[i].Ext)));
        foreach (var button in buttons)
            releases.Add(new CapturedInputEvent(hasPosition ? InputEvent.ButtonUp(at, button, x, y) : InputEvent.ButtonUp(at, button)));
        return releases;
    }
}
