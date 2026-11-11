using System.Globalization;
using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Targeting;

namespace SmartMacro.Core.Playback;

public enum PlaybackState
{
    Idle,
    Running,
    Paused,
    Stopping,
}

public enum PlaybackOutcome
{
    Completed,
    Stopped,
    Failed,
}

/// <summary>Per-step execution statistics recorded during a playback session.</summary>
public readonly record struct StepExecutionStat(int ExecutionCount, double TotalElapsedMs)
{
    public double AverageMs => ExecutionCount > 0 ? TotalElapsedMs / ExecutionCount : 0.0;
}

public sealed record PlaybackResult(PlaybackOutcome Outcome, int IterationsCompleted, int StepsExecuted, string? Error = null, string? Warning = null)
{
    /// <summary>How many whole runs (repeats) of the macro finished (same as <see cref="IterationsCompleted"/>).</summary>
    public int CompletedRuns => IterationsCompleted;

    /// <summary>Post-run telemetry (steps, vision locks, time); null for results made outside the engine.</summary>
    public PlaybackSessionStats? Stats { get; init; }

    /// <summary>Opt-in self-healing suggestions discovered during Smart Intent clicks (never applied automatically).</summary>
    public IReadOnlyList<SelfHealSuggestion> Suggestions { get; init; } = Array.Empty<SelfHealSuggestion>();

    /// <summary>Final snapshot of runtime variables at the end of the run.</summary>
    public IReadOnlyList<VariableSnapshot> FinalVariables { get; init; } = Array.Empty<VariableSnapshot>();

    /// <summary>Per-step execution counts and active durations keyed by step ID.</summary>
    public IReadOnlyDictionary<Guid, StepExecutionStat>? StepStats { get; init; }

    /// <summary>Path to the Blackbox Failure Snapshot PNG saved when a vision/wait step timed out and stopped the macro.</summary>
    public string? FailureSnapshotPath { get; init; }
}

/// <summary>
/// Raised when a run (one repeat of the whole macro) starts. CurrentRun is 1-based; TotalRuns is 0 when the macro
/// repeats until stopped.
/// </summary>
public sealed record PlaybackRunInfo(int CurrentRun, int TotalRuns)
{
    public bool IsInfinite => TotalRuns <= 0;

    /// <summary>"Run #3 / ∞" when repeating until stopped, "Run 3 / 10" for a fixed number of runs.</summary>
    public string CounterText => FormatCounter(CurrentRun, TotalRuns);

    /// <summary>
    /// The run counter shown in the editor header, the status bar and the mini overlay: "Run #3 / ∞" when the macro
    /// repeats until stopped (Play = 0), "Run 3 / 10" for a fixed number of runs.
    /// </summary>
    public static string FormatCounter(int currentRun, int totalRuns) =>
        totalRuns <= 0 ? $"Run #{Math.Max(1, currentRun)} / ∞" : $"Run {Math.Max(1, currentRun)} / {totalRuns}";
}

/// <summary>
/// Where playback currently is. StepNumber is the 1-based position in the macro (including disabled steps),
/// so it matches the numbers shown in the editor. TotalIterations is null when repeating until stopped.
/// LoopText describes the innermost Repeat block the step is in (e.g. "loop turn 2 of 5"), or null.
/// </summary>
public sealed record PlaybackProgress(int Iteration, int? TotalIterations, int StepNumber, int StepCount, MacroStep Step, string? LoopText = null);

/// <summary>
/// <see cref="PlaybackEngine.TargetOverlayClearRequested"/>: the handler sets <see cref="Cleared"/> when it hid a reticle
/// that screen captures could see (then the engine waits a moment before capturing).
/// </summary>
public sealed class OverlayClearRequest : EventArgs
{
    public bool Cleared { get; set; }
}

/// <summary>
/// "Last found target" memory: where the most recent Find text / image / color step, If / Repeat condition or feedback
/// loop condition found something in this run (screen pixels). X / Y / Width / Height = the rectangle of what was found
/// (0 × 0 when only a point is known); CenterX / CenterY = the spot clicks use (a colour patch's middle, a match's center).
/// Mouse click / Move mouse steps with "On the last found spot" act at (CenterX + OffsetX, CenterY + OffsetY).
/// </summary>
public readonly record struct FoundRect(int X, int Y, int Width, int Height, int CenterX, int CenterY)
{
    public ScreenRect Bounds => new(X, Y, Width, Height);

    /// <summary>The rectangle of a match (its center as the click spot unless another spot is given).</summary>
    public static FoundRect From(int centerX, int centerY, ScreenRect bounds) =>
        bounds.IsEmpty
            ? new FoundRect(centerX, centerY, 0, 0, centerX, centerY)
            : new FoundRect(bounds.X, bounds.Y, bounds.Width, bounds.Height, centerX, centerY);
}

/// <summary>
/// AAA live vision: something a step just found on screen, for the on-screen "target lock" reticle. Bounds = the
/// rectangle of what was found (screen pixels; a small square around the spot when only a point is known), X / Y = the
/// spot clicks use, Confidence = 0..1 (how similar it was), Label = what found it ("Find image", "If", "Smart-Aim"…).
/// ElapsedMs = how long that look took (capture + search, real time; 0 = not measured), FromHotSpot = found by the
/// Hot-Spot fast path at / near the spot where it was found last time.
/// </summary>
public sealed record TargetDetection(ScreenRect Bounds, double Confidence, string Label, int X, int Y, double ElapsedMs = 0, bool FromHotSpot = false)
{
    /// <summary>All matched rectangles when multiple occurrences were found on screen.</summary>
    public IReadOnlyList<ScreenRect>? AllMatches { get; init; }

    /// <summary>Total number of matches represented by this detection.</summary>
    public int MatchCount => AllMatches is { Count: > 0 } list ? list.Count : 1;

    /// <summary>Short badge for the reticle / HUD, e.g. "🎯 99% · 0.6ms" (just "🎯 99%" when the time isn't known).</summary>
    public string Badge
    {
        get
        {
            var percent = (int)Math.Round(Math.Clamp(Confidence, 0, 1) * 100);
            var countPrefix = MatchCount > 1 ? $"{MatchCount}× · " : string.Empty;
            return ElapsedMs > 0 ? $"🎯 {countPrefix}{percent}% · {FormatMs(ElapsedMs)}" : $"🎯 {countPrefix}{percent}%";
        }
    }

    /// <summary>A duration for the telemetry badges: "0.6ms", "12ms" (always with a dot, whatever the PC's language).</summary>
    public static string FormatMs(double milliseconds)
    {
        if (double.IsNaN(milliseconds) || milliseconds < 0) milliseconds = 0;
        return milliseconds < 10
            ? milliseconds.ToString("0.0", CultureInfo.InvariantCulture) + "ms"
            : milliseconds.ToString("0", CultureInfo.InvariantCulture) + "ms";
    }
}

/// <summary>Thrown when a Repeat block reaches its safety limit (max turns / max time) and is set to stop the macro.</summary>
public sealed class LoopLimitException : Exception
{
    public LoopLimitException(string message) : base(message)
    {
    }
}
