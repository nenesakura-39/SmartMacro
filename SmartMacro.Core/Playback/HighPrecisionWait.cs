namespace SmartMacro.Core.Playback;

/// <summary>
/// How the playback clock splits a wait so it ends on its deadline instead of 1–15 ms after it. Task.Delay (the OS
/// scheduler) wakes up late, so it is only used for the long part of a wait, ending <see cref="SpinMargin"/> early;
/// the rest (always under ~10 ms) is waited precisely (a high-resolution timer, then a short yield-spin) up to the
/// Stopwatch deadline. Waits of <see cref="CoarseAbove"/> or less are waited precisely as a whole.
/// </summary>
public static class HighPrecisionWait
{
    /// <summary>Waits longer than this use Task.Delay for their first part.</summary>
    public static readonly TimeSpan CoarseAbove = TimeSpan.FromMilliseconds(16);

    /// <summary>How early the Task.Delay part ends, leaving room for its wake-up jitter (≤ ~2 ms with a 1 ms timer).</summary>
    public static readonly TimeSpan SpinMargin = TimeSpan.FromMilliseconds(10);

    /// <summary>The Task.Delay part of a wait of <paramref name="remaining"/> (zero = wait it all precisely).</summary>
    public static TimeSpan CoarsePart(TimeSpan remaining) =>
        remaining > CoarseAbove ? remaining - SpinMargin : TimeSpan.Zero;

    /// <summary>
    /// Milliseconds still to wait until a Stopwatch deadline:
    /// (deadline − now) × 1000 / frequency. Negative when the deadline has passed.
    /// </summary>
    public static double RemainingMs(long deadlineTimestamp, long nowTimestamp, long frequency) =>
        (deadlineTimestamp - nowTimestamp) * 1000.0 / frequency;
}
