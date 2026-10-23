using System.Globalization;
using SmartMacro.Core.Models;
using SmartMacro.Core.Vision;

namespace SmartMacro.Core.Playback;

/// <summary>
/// Post-run telemetry of one playback session (Play, Run step or Play from here), shown as the editor's summary pill:
/// "✓ 3 runs in 8.4s · 24 steps · 6 vision locks (avg 0.8ms)".
/// - <see cref="CompletedRuns"/>: whole runs (repeats) that finished.
/// - <see cref="TotalStepsExecuted"/>: steps that ran (If / Repeat headers count, End markers don't).
/// - <see cref="VisionMatchesCount"/> / <see cref="AverageVisionMatchMs"/>: targets found ("vision locks": Find image /
///   colour / text, If / Repeat conditions that saw their target, Smart-Aim) and their average look time (capture +
///   search; 0 when none was timed).
/// - <see cref="TotalElapsed"/>: playing time without the time spent paused.
/// </summary>
public sealed record PlaybackSessionStats(
    PlaybackOutcome Outcome,
    int CompletedRuns,
    int TotalStepsExecuted,
    int VisionMatchesCount,
    double AverageVisionMatchMs,
    TimeSpan TotalElapsed)
{
    /// <summary>The pill text, e.g. "✓ 3 runs in 8.4s · 24 steps · 6 vision locks (avg 0.8ms)".</summary>
    public string SummaryText => Format(this);

    /// <summary>Formats the summary (invariant culture, so "8.4s" never becomes "8,4s").</summary>
    public static string Format(PlaybackSessionStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        var time = FormatDuration(stats.TotalElapsed);
        var runs = Plural(stats.CompletedRuns, "run");
        var head = stats.Outcome switch
        {
            PlaybackOutcome.Completed => $"✓ {runs} in {time}",
            PlaybackOutcome.Stopped => $"⏹ Stopped after {time} · {runs} finished",
            _ => $"⚠ Failed after {time} · {runs} finished",
        };

        var text = $"{head} · {Plural(stats.TotalStepsExecuted, "step")}";
        if (stats.VisionMatchesCount > 0)
        {
            text += $" · {Plural(stats.VisionMatchesCount, "vision lock")}";
            if (stats.AverageVisionMatchMs > 0 && double.IsFinite(stats.AverageVisionMatchMs))
                text += $" (avg {TargetDetection.FormatMs(stats.AverageVisionMatchMs)})";
        }

        return text;
    }

    /// <summary>"0.4s", "8.4s", "59.9s", "1m 05s", "1h 02m".</summary>
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        if (duration.TotalSeconds < 60)
            return Math.Min(59.9, Math.Round(duration.TotalSeconds, 1)).ToString("0.0", CultureInfo.InvariantCulture) + "s";
        if (duration.TotalHours < 1)
            return $"{(int)duration.TotalMinutes}m {duration.Seconds:00}s";
        return $"{(int)duration.TotalHours}h {duration.Minutes:00}m";
    }

    private static string Plural(int count, string word) =>
        count.ToString("N0", CultureInfo.InvariantCulture) + " " + word + (count == 1 ? "" : "s");
}

/// <summary>
/// Live progress of the step that is playing right now, for the 2 px progress bar on its card: <see cref="Fraction"/>
/// (0..1) of <see cref="ExpectedMs"/> (a Delay's length, a key / click hold, a Find step's or If condition's "keep
/// looking for" time, a feedback loop's max runtime). Paused time doesn't count.
/// </summary>
public readonly record struct StepProgress(Guid StepId, double Fraction, double ExpectedMs)
{
    /// <summary>Steps shorter than this get no bar (it would only flash).</summary>
    public const double MinBarMs = 200;

    /// <summary>True when the bar is worth showing.</summary>
    public bool ShowBar => ExpectedMs > MinBarMs;
}
