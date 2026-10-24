using System.Diagnostics;
using System.Globalization;
using SmartMacro.Core.Input;
using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Vision;
using SmartMacro.Core.Workflow;

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

public sealed record PlaybackResult(PlaybackOutcome Outcome, int IterationsCompleted, int StepsExecuted, string? Error = null, string? Warning = null)
{
    /// <summary>How many whole runs (repeats) of the macro finished (same as <see cref="IterationsCompleted"/>).</summary>
    public int CompletedRuns => IterationsCompleted;

    /// <summary>Post-run telemetry (steps, vision locks, time); null for results made outside the engine.</summary>
    public PlaybackSessionStats? Stats { get; init; }
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
    /// <summary>Short badge for the reticle / HUD, e.g. "🎯 99% · 0.6ms" (just "🎯 99%" when the time isn't known).</summary>
    public string Badge
    {
        get
        {
            var percent = (int)Math.Round(Math.Clamp(Confidence, 0, 1) * 100);
            return ElapsedMs > 0 ? $"🎯 {percent}% · {FormatMs(ElapsedMs)}" : $"🎯 {percent}%";
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

/// <summary>
/// Runs a macro's steps on a background thread.
/// - Only one run at a time (a second RunAsync throws).
/// - Pause / Resume take effect between steps and inside delays. While paused, keys and buttons the macro is
///   holding are released, and pressed again on resume (so a held W doesn't keep a game character running).
/// - Stop cancels right away, even in the middle of a long delay.
/// - Keys and mouse buttons that a macro left pressed are always released at the end,
///   whether it finished, was stopped, or crashed.
///
/// Timing: delays, hold times and recorded-input events are scheduled on one running timeline
/// (deadline = start + sum of the recorded gaps), not "sleep N ms after the previous step". So the time spent
/// sending input and timer overshoot don't add up over thousands of steps, and simultaneous keyboard and mouse
/// events keep their original order and spacing. If playback falls far behind (the PC stalled), the timeline
/// is moved forward instead of firing a burst of late input.
///
/// Blocks (Stage 3): steps run with a program counter (an index into the step list) instead of a foreach, so
/// If / Else / End If and Repeat / End Repeat can jump. The jumps come from <see cref="BlockStructure"/>, which the
/// validator has already checked before anything runs. Every Repeat has a limit (count, max turns and/or max time),
/// every condition check honours Pause / Stop / Esc, and loop state lives in a small per-run stack, so pausing
/// can't lose or corrupt it.
///
/// Feedback loops (Stage 4): a Feedback loop step runs its own capture → detect → decide → act loop
/// (<see cref="FeedbackSensor"/> + <see cref="FeedbackController"/>), sends input only when the decision changes,
/// checks Pause / Stop / Esc every frame, and always lets go of its key / button when it ends for any reason.
/// </summary>
public sealed class PlaybackEngine
{
    private static readonly TimeSpan MaxDelayChunk = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// If playback is more than this far behind schedule (e.g. after a step with its own internal wait or OS stall),
    /// clamp the timeline to now instead of fast-forwarding subsequent delays/movements in 0 ms.
    /// </summary>
    public static readonly TimeSpan MaxTimelineLag = TimeSpan.FromMilliseconds(15);

    /// <summary>
    /// Deadline-synced playback of recorded-style steps (Delay, Move mouse, Mouse click at a position, Key press): the time
    /// a click or key press itself takes to send (Windows' hover nudge + settle before a press can be ~35 ms, a stalled
    /// UI thread a few ms more) stays ON the virtual timeline and is taken out of the next wait, instead of being added
    /// to the macro after every step. Over hundreds of steps / a 5-hour AFK loop that extra time used to add up to
    /// seconds of drift. Up to this much is caught up this way; anything later than that (a real PC stall) is skipped,
    /// so playback never fires a burst of late input.
    /// </summary>
    public static readonly TimeSpan MaxStepOverheadCatchUp = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// A Mouse click step with a hold (recorded clicks always have one) keeps the button down at least this long
    /// (÷ speed, with the <see cref="MinScaledHoldMs"/> floor at high speeds), so games that read input once per frame see it.
    /// HoldMs 0 = instant click, as before.
    /// </summary>
    public const int MinClickHoldMs = 12;

    /// <summary>
    /// Pause after clicking found text / an image / a colour ("post-action delay") so the clicked window (or game UI)
    /// can react before the next step looks at the screen again.
    /// </summary>
    public const int ClickSettleMs = MouseMotionDefaults.PostActionDelayMs;

    /// <summary>
    /// How long a "when found" click keeps the button down. Games read input once per frame; a press and release in
    /// the same instant was sometimes ignored by Roblox (a 60 fps frame is ~16 ms, so 35 ms spans two frames).
    /// </summary>
    public const int FoundClickHoldMs = MouseMotionDefaults.ClickHoldMs;

    /// <summary>Gap between the two clicks of a "when found" double-click (0 ms was too fast for Roblox).</summary>
    public const int DoubleClickGapMs = MouseMotionDefaults.DoubleClickIntervalMs;

    /// <summary>
    /// Reliable game click on a found target: after the cursor reaches the target, it rests there this long before the
    /// button goes down, so games that read the cursor once per frame (aim trainers, Roblox / Unity) register the new
    /// position first. Used by every click a Find image / colour / text, Wait for screen change step (or an If "when
    /// found") makes on what it found, and by Mouse click steps that click the last found spot or a Smart-Aim target.
    /// Snappy for fast-moving targets: 6 ms is enough for the move to be applied first. Feedback loops never use it:
    /// they press / release directly, with no delay.
    /// </summary>
    public const int TargetHoverMs = 6;

    /// <summary>
    /// A click on a found target keeps the button down at least this long (MouseDown → 12 ms → MouseUp). A step's own
    /// longer click hold (default 35 ms) is kept; this is only the floor for holds set lower than it.
    /// </summary>
    public const int MinTargetClickHoldMs = 12;

    /// <summary>After SmartMacro hid its own reticle before a Wait for screen change look, it waits this long so the screen has updated.</summary>
    public const int OverlayClearSettleMs = 50;

    /// <summary>
    /// Smooth mouse movement: a cursor jump made by a Move mouse step (or a click / scroll at a position) that comes
    /// right after a Delay glides there during the end of that Delay (up to <see cref="MaxGlideMs"/>, one move every
    /// <see cref="GlideStepInterval"/>), instead of teleporting. Total timing doesn't change. The app turns this on
    /// from Settings; off by default (tests and callers that want exact jumps).
    /// </summary>
    public bool SmoothMouseMoves { get; set; }

    /// <summary>Longest glide before a cursor jump (ms of the preceding Delay that is used for gliding).</summary>
    public const int MaxGlideMs = 200;

    /// <summary>
    /// Speed never shortens a button / key hold below this (or below the original hold if that was shorter): games read
    /// input once per frame, so a 35 ms click played at 5× (7 ms) could be missed. 25 ms spans a frame at 60 fps.
    /// </summary>
    public const int MinScaledHoldMs = 25;

    /// <summary>Humanize only varies delays at least this long (short recorded gaps keep their exact rhythm).</summary>
    public const int HumanizeMinDelayMs = 40;

    /// <summary>Humanize: smooth glides bend sideways by up to this share of the distance (a natural hand arc)…</summary>
    public const double HumanizeArc = 0.05;

    /// <summary>…but never more than this many pixels.</summary>
    public const double HumanizeMaxArcPixels = 24;

    /// <summary>Slowest / fastest live playback speed.</summary>
    public const double MinLiveSpeed = 0.1;

    public const double MaxLiveSpeed = 10.0;

    // Live speed change requested from the UI (NaN = none pending). Read by the playback thread before each step.
    private double _pendingSpeed = double.NaN;
    private readonly object _speedGate = new();

    /// <summary>Time between cursor updates while gliding or dragging (~120 per second).</summary>
    public static readonly TimeSpan GlideStepInterval = TimeSpan.FromMilliseconds(8);

    // Where the engine last put the cursor with an absolute move (null = unknown, e.g. after relative / camera moves).
    private (int X, int Y)? _cursor;

    // Set just before a Delay step runs: the spot the next step jumps to (for the smooth glide), or null.
    private (int X, int Y)? _glideTarget;

    // Recorded points: a short Delay followed by a Move with a glide style glides DURING the Delay (set just before
    // the Delay runs), and that Move then only lands on its spot instead of adding its own glide time.
    private MouseMoveStep? _glideDuringDelay;
    private MouseMoveStep? _glideAbsorbedBy;

    /// <summary>
    /// A Delay this short (or shorter) right before a Move mouse step with Smooth glide / Human curve is treated as a
    /// recorded point: the glide uses the Delay's time instead of adding its own (120 ms) on top of every 16 ms point.
    /// Longer Delays (a manual "wait 1 s, then glide there") keep the old behaviour: wait, then glide.
    /// </summary>
    public const int RecordedPointMaxDelayMs = 50;

    /// <summary>How often the progress text is refreshed while a feedback loop runs.</summary>
    private static readonly TimeSpan FeedbackProgressInterval = TimeSpan.FromMilliseconds(250);

    private readonly IInputSimulator _input;
    private readonly IPlaybackClock _clock;
    private readonly IOcrService? _ocr;
    private readonly IScreenCapture? _capture;
    private readonly IScreenLayout? _layout;
    private readonly object _gate = new();

    // The detection code shared by Find steps and conditions (created per run).
    private ScreenProbes? _probes;

    // Where the most recent Find text / image / color step found something, in screen pixels
    // (only one run at a time, so a field is fine). Mouse click steps can click it.
    private (int X, int Y)? _lastFound;

    // The rectangle of what was last found (empty when unknown, e.g. a colour patch's centre only). Humanize keeps a
    // jittered click inside it.
    private ScreenRect _lastFoundBounds;

    // Smart-Aim (Smart Click Snapshots): the last log line per step so a click inside a loop doesn't log the same thing
    // every turn. Per run. (The decoded thumbnails live in the shared TemplateCache.)
    private readonly Dictionary<Guid, string> _smartAimLastNote = new();

    // Vision Engine Turbo – Hot-Spot fast path: where each image search found its picture last time. Kept for the whole
    // playback session (poll iterations, loop turns, macro repeats); cleared when a new playback starts.
    private readonly HotSpotMemory _hotSpots = new();

    // Telemetry: the latest image / colour / Smart-Aim look time in ms (double bits, so any thread can read it safely).
    private long _lastProbeElapsedBits;

    // Post-run telemetry of the current session (Upgrade 3): targets found ("vision locks") and their look times. Written
    // by the playback flow, read by the UI: under _statsGate.
    private readonly object _statsGate = new();
    private int _statVisionLocks;
    private int _statVisionTimed;
    private double _statVisionMsTotal;

    // Paused time of the current session as the UI sees it (Pause() to Resume() / Stop()), under _gate: the live step
    // progress bar and the post-run total time don't count it.
    private TimeSpan _statPausedTotal;
    private TimeSpan? _statPausedSince;

    // The step playing right now and how long it is expected to take (live 2 px progress bar). Replaced, never mutated.
    private StepTiming? _stepTiming;

    private sealed record StepTiming(Guid StepId, TimeSpan StartedAt, double ExpectedMs);

    // Humanize (per run, from the macro): random click offset (px) and timing variation (%).
    private bool _humanize;
    private int _clickJitterPx;
    private int _delayJitterPercent;

    // True while a feedback loop runs (no target-lock reticle then; see ReportTarget).
    private bool _inFeedbackLoop;

    // Per-run playback state. Only touched by the playback flow itself (never by Pause/Stop callers).
    private HeldInput? _held;
    private TimeSpan _timelineTarget;   // clock time at which the current step / event is due
    private TimeSpan _timelineShift;    // total time the timeline was pushed back (pauses, stalls)
    private TimeSpan _lagAllowance = MaxTimelineLag; // how far behind the timeline may be and still catch up (see MaxStepOverheadCatchUp)
    private TimeSpan _pausedTotal;      // total time spent paused in this run (not counted by timeouts / loop limits)
    private bool _skipNextStep;         // set by a Find step that timed out with "skip the next step"

    private PlaybackState _state = PlaybackState.Idle;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource? _resumeSignal;
    private TimeSpan? _pauseRequestedAt; // clock time of the latest Pause() call (under _gate)

    /// <param name="layout">
    /// Monitors and app windows, for Find steps / conditions that look at one monitor or inside one window. Without it the
    /// main monitor means the whole desktop, and a specific monitor / window can't be found.
    /// </param>
    public PlaybackEngine(IInputSimulator input, IPlaybackClock? clock = null, IOcrService? ocr = null, IScreenCapture? capture = null,
        IScreenLayout? layout = null)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _clock = clock ?? new SystemPlaybackClock();
        _ocr = ocr;
        _capture = capture;
        _layout = layout;
    }

    /// <summary>
    /// The random numbers "Humanize" uses (tests set a seeded one so runs repeat exactly). Only used on the playback thread.
    /// </summary>
    public Random HumanizeRandom { get; set; } = new();

    /// <summary>
    /// Humanize: a duration in ms with up to ± the macro's timing variation (e.g. 1000 ms → 920…1080 ms at 8 %).
    /// Unchanged when Humanize is off.
    /// </summary>
    private double Vary(double milliseconds, double minimumMs = 0)
    {
        if (!_humanize || _delayJitterPercent <= 0 || milliseconds <= 0 || milliseconds < minimumMs) return milliseconds;
        var factor = 1.0 + (HumanizeRandom.NextDouble() * 2.0 - 1.0) * _delayJitterPercent / 100.0;
        return Math.Max(0, milliseconds * factor);
    }

    /// <summary>
    /// Humanize: a click spot moved by up to ± the macro's click jitter in each direction. When the spot lies inside
    /// <paramref name="bounds"/> (what a Find step / condition matched) the result stays inside it too, so a small
    /// button is never missed. Unchanged when Humanize is off.
    /// </summary>
    private (int X, int Y) Jitter(int x, int y, ScreenRect bounds = default)
    {
        if (!_humanize || _clickJitterPx <= 0) return (x, y);
        var jx = x + HumanizeRandom.Next(-_clickJitterPx, _clickJitterPx + 1);
        var jy = y + HumanizeRandom.Next(-_clickJitterPx, _clickJitterPx + 1);
        if (!bounds.IsEmpty && x >= bounds.X && x < bounds.Right && y >= bounds.Y && y < bounds.Bottom)
        {
            jx = Math.Clamp(jx, bounds.X, bounds.Right - 1);
            jy = Math.Clamp(jy, bounds.Y, bounds.Bottom - 1);
        }

        return (jx, jy);
    }

    /// <summary>
    /// Release held keys and buttons while paused, and press them again on resume. On by default.
    /// </summary>
    public bool ReleaseHeldInputWhilePaused { get; set; } = true;

    /// <summary>Human-readable notes from the playback thread (e.g. where text was found), for the log.</summary>
    public event EventHandler<string>? Message;

    /// <summary>Raised on whichever thread changed the state.</summary>
    public event EventHandler<PlaybackState>? StateChanged;

    /// <summary>Raised on the playback thread before each step. Keep handlers fast.</summary>
    public event EventHandler<PlaybackProgress>? ProgressChanged;

    /// <summary>Raised (true / false) whenever playback is paused or resumed, on the thread that did it.</summary>
    public event EventHandler<bool>? PausedChanged;

    /// <summary>
    /// AAA live vision: raised on the playback thread whenever a Find image / color / text step, an If / Repeat condition or
    /// a Smart-Aim click finds its target. The app draws a short "target lock" reticle there. Keep handlers fast.
    /// </summary>
    public event EventHandler<TargetDetection>? TargetDetected;

    /// <summary>
    /// Raised on the playback thread right before a look that compares the screen over time (Wait for screen change takes
    /// its starting picture). When overlays can be captured ("🎥 Show overlays in OBS / Discord"), the handler hides the
    /// target-lock reticle at once and sets <see cref="OverlayClearRequest.Cleared"/>, so the reticle (or it fading away)
    /// is never mistaken for a change. Handlers must not throw.
    /// </summary>
    public event EventHandler<OverlayClearRequest>? TargetOverlayClearRequested;

    /// <summary>True while playback is paused (Pause / F8).</summary>
    public bool IsPaused => State == PlaybackState.Paused;

    /// <summary>
    /// Vision telemetry: how long the latest image / colour look or Smart-Aim search took (capture + search, real time,
    /// ms; 0 before the first one of a playback). Safe to read from any thread (the UI shows "⚡ Vision: 0.6ms").
    /// </summary>
    public double LastProbeElapsedMs => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _lastProbeElapsedBits));

    /// <summary>The Hot-Spot memory of the current (or latest) playback session: where each image search found its picture.</summary>
    public HotSpotMemory HotSpots => _hotSpots;

    /// <summary>Post-run telemetry of the latest finished (or stopped / failed) playback session; null before the first one.</summary>
    public PlaybackSessionStats? LastSessionStats { get; private set; }

    /// <summary>Targets found so far in this session ("vision locks"). Safe from any thread.</summary>
    public int VisionMatchesCount
    {
        get { lock (_statsGate) return _statVisionLocks; }
    }

    /// <summary>Average look time (ms) of the vision locks so far in this session (0 when none was timed). Safe from any thread.</summary>
    public double AverageVisionMatchMs
    {
        get { lock (_statsGate) return _statVisionTimed == 0 ? 0 : _statVisionMsTotal / _statVisionTimed; }
    }

    /// <summary>Counts one found target for the post-run summary (with its look time when known).</summary>
    private void RecordVisionLock(double elapsedMs)
    {
        lock (_statsGate)
        {
            _statVisionLocks++;
            if (elapsedMs > 0 && double.IsFinite(elapsedMs))
            {
                _statVisionTimed++;
                _statVisionMsTotal += elapsedMs;
            }
        }
    }

    /// <summary>Clock time minus the time spent paused (as seen by Pause / Resume), frozen while paused.</summary>
    private TimeSpan UiActiveTime()
    {
        lock (_gate)
        {
            var now = _statPausedSince ?? _clock.Elapsed;
            return now - _statPausedTotal;
        }
    }

    /// <summary>
    /// Live progress of the step that is playing (for its card's 2 px progress bar), or null when nothing with a known
    /// length plays. Safe to call from the UI thread at ~30 FPS: it only reads a snapshot and the clock.
    /// </summary>
    public StepProgress? GetCurrentStepProgress()
    {
        if (Volatile.Read(ref _stepTiming) is not { } timing) return null;
        if (State is not (PlaybackState.Running or PlaybackState.Paused)) return null;
        var elapsed = (UiActiveTime() - timing.StartedAt).TotalMilliseconds;
        var fraction = timing.ExpectedMs > 0 ? Math.Clamp(elapsed / timing.ExpectedMs, 0, 1) : 0;
        return new StepProgress(timing.StepId, fraction, timing.ExpectedMs);
    }

    /// <summary>
    /// How long a step is expected to take on the playback timeline at <paramref name="speed"/>, in ms, or null when it
    /// has no meaningful length: a Delay (÷ speed), a key / click hold or drag (÷ speed, never below the hold floor), Type
    /// text (characters × delay ÷ speed), a Find step's or If condition's "keep looking for" time and a feedback loop's
    /// max runtime (real time, not scaled).
    /// </summary>
    public static double? ExpectedDurationMs(MacroStep step, double speed)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (speed <= 0 || !double.IsFinite(speed)) speed = 1;
        double ms = step switch
        {
            DelayStep d => d.DurationMs / speed,
            KeyPressStep { Action: KeyAction.Press } k => ScaledHoldMs(k.HoldMs, speed),
            MouseClickStep { Action: MouseClickAction.Drag } c => c.DragDurationMs / speed,
            MouseClickStep { Action: MouseClickAction.Click, HoldMs: > 0 } c => ScaledHoldMs(Math.Max(MinClickHoldMs, c.HoldMs), speed),
            MouseClickStep c => ScaledHoldMs(c.HoldMs, speed),
            TypeTextStep t => (t.Text?.Length ?? 0) * Math.Max(0, t.CharacterDelayMs) / speed,
            ScreenSearchStep s => s.TimeoutMs,
            IfStep { Condition.Search: { } check } => check.TimeoutMs,
            FeedbackLoopStep f => f.MaxRuntimeMs,
            WaitForChangeStep w => w.TimeoutMs,
            _ => 0,
        };
        return ms > 0 && double.IsFinite(ms) ? ms : null;
    }

    private void RecordProbeTime(double milliseconds)
    {
        if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds <= 0) return;
        Interlocked.Exchange(ref _lastProbeElapsedBits, BitConverter.DoubleToInt64Bits(milliseconds));
    }

    /// <summary>The speed the current run plays at (1 = recorded speed); the macro's Speed when idle.</summary>
    public double CurrentSpeed { get; private set; } = 1.0;

    /// <summary>
    /// Live speed change (the editor's speed selector while playing): the running macro continues at
    /// <paramref name="speed"/> (clamped to 0.1–10) from its next step. A step already running (a long Delay or recorded
    /// input) finishes at the old speed. No effect when nothing plays.
    /// </summary>
    public void SetPlaybackSpeed(double speed)
    {
        if (double.IsNaN(speed) || double.IsInfinity(speed)) return;
        lock (_speedGate) _pendingSpeed = Math.Clamp(speed, MinLiveSpeed, MaxLiveSpeed);
    }

    private double? TakePendingSpeed()
    {
        lock (_speedGate)
        {
            if (double.IsNaN(_pendingSpeed)) return null;
            var speed = _pendingSpeed;
            _pendingSpeed = double.NaN;
            return speed;
        }
    }

    /// <summary>Tells the overlay what was found (bounds may be empty: then a small square around the spot is used).</summary>
    private void ReportTarget(int x, int y, ScreenRect bounds, double confidence, string label, double elapsedMs = 0, bool fromHotSpot = false)
    {
        RecordVisionLock(elapsedMs);
        // Never flash the reticle from inside a feedback loop: at 120 looks per second it would sit on the bar it watches
        // (and in streamer mode the loop's own captures would see it).
        if (_inFeedbackLoop) return;
        if (TargetDetected is not { } handler) return;
        var box = bounds.IsEmpty ? new ScreenRect(x - 12, y - 12, 24, 24) : bounds;
        try { handler(this, new TargetDetection(box, Math.Clamp(confidence, 0, 1), label, x, y, Math.Max(0, elapsedMs), fromHotSpot)); }
        catch (Exception ex) { Message?.Invoke(this, $"Target overlay error: {ex.Message}"); }
    }

    /// <summary>
    /// A hold time (ms) on the playback timeline at <paramref name="speed"/>: divided by the speed, but never below
    /// <see cref="MinScaledHoldMs"/> (or the original hold, if that was even shorter), so fast playback still registers.
    /// </summary>
    public static double ScaledHoldMs(int holdMs, double speed)
    {
        if (holdMs <= 0) return 0;
        var scaled = holdMs / (speed > 0 ? speed : 1.0);
        return Math.Max(scaled, Math.Min(holdMs, MinScaledHoldMs));
    }

    /// <summary>Raised on the playback thread at the start of each macro repeat iteration (1-based run number, 0 = infinite).</summary>
    public event EventHandler<PlaybackRunInfo>? RunStarted;

    /// <summary>Optional callback invoked at the start of each macro repeat iteration: (currentRun, totalRuns).</summary>
    public Action<int, int>? OnRunStarted { get; set; }

    /// <summary>1-based run number of the current (or most recent) repeat iteration.</summary>
    public int CurrentRun { get; private set; }

    /// <summary>Total configured runs for the current playback (0 when repeating infinitely).</summary>
    public int TotalRuns { get; private set; }

    /// <summary>
    /// "Last found target" of the current (or most recent) run: what the latest Find step / If / Repeat / feedback
    /// condition found, or null when nothing has been found yet (or a Find step that "continues" timed out since).
    /// </summary>
    public FoundRect? LastFoundRect { get; private set; }

    /// <summary>Remembers what a Find step / condition just found ("last found target").</summary>
    private void RememberFound(int x, int y, ScreenRect match)
    {
        _lastFound = (x, y);
        _lastFoundBounds = match;
        LastFoundRect = FoundRect.From(x, y, match);
    }

    /// <summary>Forgets the last found target (a Find step that was set to continue didn't find anything).</summary>
    private void ForgetFound()
    {
        _lastFound = null;
        _lastFoundBounds = default;
        LastFoundRect = null;
    }

    public PlaybackState State
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>Plays the whole macro (all its repeats).</summary>
    public Task<PlaybackResult> RunAsync(Macro macro, CancellationToken cancellationToken = default) =>
        RunCoreAsync(macro, null, cancellationToken);

    /// <summary>
    /// "▶ Run step": plays only the step at <paramref name="stepIndex"/>, once. When that step is an If or a Repeat (or
    /// one of its Else / End markers), its whole block runs, through its End If / End Repeat. Repeats of the macro and
    /// the wait between repeats are ignored. Only problems in that part of the macro (and macro-wide problems) stop it.
    /// </summary>
    public Task<PlaybackResult> PlaySingleStepAsync(Macro macro, int stepIndex, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(macro);
        var (start, end) = PlaybackRanges.SingleStep(macro.Steps, stepIndex);
        return RunCoreAsync(macro, new PlaybackSpan(start, end, stepIndex), cancellationToken);
    }

    /// <summary>
    /// "⏭ Play from here" (Shift+F5): plays from <paramref name="startStepIndex"/> to the end of the macro, once.
    /// It plays <see cref="PlaybackRanges.BuildPlayFromHereSlice"/>: a self-contained copy of the list whose blocks always
    /// match up, even when the start is inside nested If / Repeat blocks (the Else part of an If that started above is
    /// left out, End If / End Repeat markers whose opener is above the start are dropped, so the rest of a loop body
    /// plays once). Progress keeps the macro's own step numbers.
    /// </summary>
    public Task<PlaybackResult> PlayFromStepAsync(Macro macro, int startStepIndex, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(macro);
        var (slice, info) = PlaybackRanges.BuildPlayFromHereMacro(macro, startStepIndex);
        var issues = PlaybackRanges.ValidatePlayFromHere(macro, startStepIndex);
        if (info.DroppedMarkers > 0 && !issues.HasErrors())
            Message?.Invoke(this, $"Playing from step {startStepIndex + 1}: left out {info.DroppedMarkers} block marker(s)" +
                                  (info.DroppedSteps > 0 ? $" and {info.DroppedSteps} step(s) of an \"otherwise\" part" : "") +
                                  " that belong to blocks started above it.");
        return RunCoreAsync(slice, new PlaybackSpan(0, slice.Steps.Count, 0, issues, info.SourceIndexes, macro.Steps.Count), cancellationToken);
    }

    /// <summary>
    /// The part of the step list a run plays: steps [Start, End). Null span = the whole macro with its repeats.
    /// <paramref name="Issues"/>: validation already done by the caller (null = validate [Start, End) here).
    /// <paramref name="SourceIndexes"/> / <paramref name="SourceCount"/>: when the played list is a slice of a bigger macro,
    /// the macro's index of every played step and the macro's step count (progress shows the macro's step numbers).
    /// </summary>
    private sealed record PlaybackSpan(int Start, int End, int RequestedIndex,
        IReadOnlyList<ValidationIssue>? Issues = null, IReadOnlyList<int>? SourceIndexes = null, int SourceCount = 0);

    private async Task<PlaybackResult> RunCoreAsync(Macro macro, PlaybackSpan? span, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(macro);

        var issues = span is null
            ? MacroValidator.ValidateForExecution(macro)
            : span.Issues ?? PlaybackRanges.ValidateRange(macro, span.Start, span.End);
        if (issues.HasErrors())
            throw new MacroValidationException(issues);

        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_state != PlaybackState.Idle)
                throw new InvalidOperationException("A macro is already playing.");
            cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _cts = cts;
            _resumeSignal = null;
            _lastFound = null;
            _lastFoundBounds = default;
            LastFoundRect = null;
            _pendingSpeed = double.NaN;
            _smartAimLastNote.Clear();
            _hotSpots.Clear();
            Interlocked.Exchange(ref _lastProbeElapsedBits, 0);
            _statPausedTotal = TimeSpan.Zero;
            _statPausedSince = null;
            _skipNextStep = false;
            _cursor = null;
            _glideTarget = null;
            _glideDuringDelay = null;
            _glideAbsorbedBy = null;
            CurrentRun = 0;
            TotalRuns = span is not null ? 1 : macro.RepeatCount;
        }

        lock (_statsGate)
        {
            _statVisionLocks = 0;
            _statVisionTimed = 0;
            _statVisionMsTotal = 0;
        }

        Volatile.Write(ref _stepTiming, null);
        var sessionStartedAt = UiActiveTime();
        SetState(PlaybackState.Running);

        // Humanize: small random click offsets and timing variation (off unless the macro turns it on).
        _humanize = macro.HumanizePlayback;
        _clickJitterPx = Math.Clamp(macro.EffectiveClickJitterPx, 0, Macro.MaxClickJitterPx);
        _delayJitterPercent = Math.Clamp(macro.EffectiveDelayJitterPercent, 0, Macro.MaxDelayJitterPercent);

        var held = new HeldInput();
        _held = held;
        _probes = new ScreenProbes(_ocr, _capture, _clock, _layout, _hotSpots);
        var iterationsCompleted = 0;
        var token = cts.Token;

        // The validator above already refused unbalanced blocks, so this structure is always valid here.
        var steps = macro.Steps;
        var blocks = BlockStructure.Analyze(steps);
        int? totalIterations = span is not null ? 1 : macro.RepeatCount == 0 ? (int?)null : macro.RepeatCount;
        var configuredTotalRuns = totalIterations ?? 0;
        var repeatDelayMs = span is null ? macro.RepeatDelayMs : 0;

        PlaybackResult result;
        string? releaseError;
        RunContext? run = null;
        try
        {
            // "Auto-adjust coordinates": play a copy whose positions are mapped into the target window / screen as it
            // is right now (the same step IDs, so the editor's running-step highlight still works).
            if (CoordinateScaling.MappingFor(macro, _layout) is { } mapping && !mapping.IsIdentity)
            {
                steps = CoordinateScaling.ApplyToCopy(steps, mapping);
                var targetName = string.IsNullOrWhiteSpace(macro.TargetWindowTitle) ? "main screen" : "\"" + macro.TargetWindowTitle.Trim() + "\" window";
                Message?.Invoke(this, $"Coordinates adjusted to the {targetName}: {mapping}.");
            }

            CurrentSpeed = macro.Speed;
            var current = new RunContext(steps, blocks, macro.Speed, held, new ScreenConditionEvaluator(_probes), totalIterations,
                span?.Start ?? 0, span?.End ?? steps.Count)
            {
                SourceIndexes = span?.SourceIndexes,
                SourceCount = span?.SourceIndexes is not null ? span.SourceCount : 0,
            };
            run = current;

            if (span is not null && span.Start != span.RequestedIndex)
                Message?.Invoke(this, $"Starting at step {span.Start + 1} (the start of the block that contains step {span.RequestedIndex + 1}).");

            await Task.Run(async () =>
            {
                if (!blocks.IsValid) // can't happen after validation; checked anyway so a bug can't run a broken list
                    throw new InvalidOperationException("The If / Repeat blocks of this macro don't match up.");

                ResetTimeline();
                _timelineShift = TimeSpan.Zero;
                _pausedTotal = TimeSpan.Zero;

                for (var iteration = 1; current.TotalIterations is null || iteration <= current.TotalIterations; iteration++)
                {
                    // Pause between loops (never before the first run or after the last one).
                    if (iteration > 1 && repeatDelayMs > 0)
                    {
                        ResetTimeline();
                        AdvanceTimeline(Vary(repeatDelayMs / current.Speed, HumanizeMinDelayMs));
                        await WaitForTimelineAsync(token).ConfigureAwait(false);
                    }

                    // Reset the master timeline at the start of every single repeat iteration so any wall-clock
                    // time spent in Run #N never spills over or skips delays/movements in Run #(N+1).
                    ResetTimeline();

                    current.Iteration = iteration;
                    CurrentRun = iteration;
                    RunStarted?.Invoke(this, new PlaybackRunInfo(iteration, configuredTotalRuns));
                    OnRunStarted?.Invoke(iteration, configuredTotalRuns);
                    _cursor = null;
                    _glideTarget = null;
                    _glideDuringDelay = null;
                    _glideAbsorbedBy = null;
                    await RunStepsAsync(current, token).ConfigureAwait(false);
                    if (current.TotalIterations is null || iteration < current.TotalIterations)
                    {
                        held.ReleaseAll(_input);
                        try { _input.ReleaseAllHeldInputs(); } catch { }
                    }

                    iterationsCompleted++;
                }
            }, token).ConfigureAwait(false);

            result = new PlaybackResult(PlaybackOutcome.Completed, iterationsCompleted, current.StepsExecuted);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            result = new PlaybackResult(PlaybackOutcome.Stopped, iterationsCompleted, run?.StepsExecuted ?? 0);
        }
        catch (Exception ex)
        {
            result = new PlaybackResult(PlaybackOutcome.Failed, iterationsCompleted, run?.StepsExecuted ?? 0, ex.Message);
        }
        finally
        {
            // Never leave keys or mouse buttons stuck down (also when stopped inside a loop or a condition check).
            releaseError = held.ReleaseAll(_input);

            // Failsafe on top of that: the simulator releases anything it still thinks is down (input the engine didn't
            // track, or a release that was interrupted by the stop / error).
            string? failsafeError;
            try
            {
                failsafeError = _input.ReleaseAllHeldInputs();
            }
            catch (Exception ex)
            {
                failsafeError = $"Could not release held input: {ex.Message}";
            }

            releaseError ??= failsafeError;
            _held = null;
            _probes = null;
            _humanize = false;

            lock (_gate)
            {
                _cts = null;
                _resumeSignal?.TrySetResult();
                _resumeSignal = null;
            }

            cts.Dispose();
            Volatile.Write(ref _stepTiming, null);
            lock (_gate)
            {
                // Stopped while paused: that last pause doesn't count either.
                if (_statPausedSince is { } since) _statPausedTotal += _clock.Elapsed - since;
                _statPausedSince = null;
            }

            SetState(PlaybackState.Idle);
        }

        var stats = new PlaybackSessionStats(result.Outcome, result.IterationsCompleted, result.StepsExecuted,
            VisionMatchesCount, AverageVisionMatchMs, UiActiveTime() - sessionStartedAt);
        LastSessionStats = stats;
        result = result with { Stats = stats };
        return releaseError is null ? result : result with { Warning = releaseError };
    }

    /// <summary>Pauses before the next step (or inside the current delay). No effect unless running.</summary>
    public bool Pause()
    {
        lock (_gate)
        {
            if (_state != PlaybackState.Running) return false;
            _resumeSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _state = PlaybackState.Paused;
            _pauseRequestedAt = _clock.Elapsed; // the pause counts from the key press, not from when playback notices it
            _statPausedSince = _clock.Elapsed;
        }

        // Let go of everything right now (not only when the playback thread reaches its next check, up to 50 ms later),
        // so no key or button stays stuck while paused. The playback thread presses what it was holding again on resume.
        if (ReleaseHeldInputWhilePaused)
        {
            try { _input.ReleaseAllHeldInputs(); }
            catch (Exception ex) { Message?.Invoke(this, $"Could not release held input when pausing: {ex.Message}"); }
        }

        StateChanged?.Invoke(this, PlaybackState.Paused);
        PausedChanged?.Invoke(this, true);
        return true;
    }

    public bool Resume()
    {
        lock (_gate)
        {
            if (_state != PlaybackState.Paused) return false;
            _resumeSignal?.TrySetResult();
            _resumeSignal = null;
            _state = PlaybackState.Running;
            if (_statPausedSince is { } since) _statPausedTotal += _clock.Elapsed - since;
            _statPausedSince = null;
        }

        StateChanged?.Invoke(this, PlaybackState.Running);
        PausedChanged?.Invoke(this, false);
        return true;
    }

    /// <summary>Pause when playing, resume when paused (the F8 / ⏸ button). Returns true if it changed something.</summary>
    public bool TogglePause() => State == PlaybackState.Paused ? Resume() : Pause();

    /// <summary>Stops as soon as possible. Safe to call any time, from any thread.</summary>
    public bool Stop()
    {
        bool wasPaused;
        lock (_gate)
        {
            if (_state is not (PlaybackState.Running or PlaybackState.Paused)) return false;
            wasPaused = _state == PlaybackState.Paused;
            _state = PlaybackState.Stopping;
            _cts?.Cancel();
            _resumeSignal?.TrySetResult();
            _resumeSignal = null;
        }

        StateChanged?.Invoke(this, PlaybackState.Stopping);
        if (wasPaused) PausedChanged?.Invoke(this, false);
        return true;
    }

    private void SetState(PlaybackState state)
    {
        lock (_gate) _state = state;
        StateChanged?.Invoke(this, state);
    }

    /// <summary>
    /// Waits while paused. Held input is released for the pause and pressed again afterwards, and the
    /// timeline is pushed back by the paused time so playback continues with the same spacing.
    /// </summary>
    private async Task WaitIfPausedAsync(CancellationToken token)
    {
        Task? wait;
        TimeSpan? requestedAt;
        lock (_gate)
        {
            wait = _resumeSignal?.Task;
            requestedAt = _pauseRequestedAt;
        }

        if (wait is null) return;

        // Paused since F8 was pressed (up to one 50 ms wait chunk before this check noticed it), so the rest of a delay,
        // key hold or recorded input after resume is exactly what was left at that moment.
        var pausedAt = _clock.Elapsed;
        if (requestedAt is { } pressed && pressed < pausedAt) pausedAt = pressed;
        var held = _held;
        List<HeldKey>? keys = null;
        List<MacroMouseButton>? buttons = null;
        if (ReleaseHeldInputWhilePaused && held is { IsEmpty: false })
        {
            keys = held.Keys.ToList();
            buttons = held.Buttons.ToList();
            var error = held.ReleaseAll(_input);
            if (error is not null) Message?.Invoke(this, error);
        }

        await wait.WaitAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested(); // Stop while paused: don't press anything again

        if (held is not null)
        {
            foreach (var key in keys ?? Enumerable.Empty<HeldKey>())
            {
                SendKey(key, down: true);
                held.Keys.Add(key);
            }

            foreach (var button in buttons ?? Enumerable.Empty<MacroMouseButton>())
            {
                _input.MouseButton(button, true);
                held.Buttons.Add(button);
            }
        }

        var paused = _clock.Elapsed - pausedAt;
        if (paused > TimeSpan.Zero)
        {
            _timelineTarget += paused;
            _timelineShift += paused;
            _pausedTotal += paused;
        }
    }

    /// <summary>Clock time minus the time spent paused: what condition timeouts and loop time limits measure.</summary>
    private TimeSpan ActiveTime => _clock.Elapsed - _pausedTotal;

    // ---------------- Timeline ----------------

    private void ResetTimeline()
    {
        _timelineTarget = _clock.Elapsed;
        _lagAllowance = MaxTimelineLag;
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
        if (lag > _lagAllowance)
        {
            _timelineShift += lag;
            _timelineTarget = now;
        }
    }

    /// <summary>
    /// Resynchronises <see cref="_timelineTarget"/> to <see cref="_clock"/> after a step that performs its own internal
    /// waits or sleeps (clicks, smooth glides, key holds, scrolls, searches, sequences, or condition checks).
    /// </summary>
    private void SyncTimelineToNow()
    {
        _lagAllowance = MaxTimelineLag;
        var now = _clock.Elapsed;
        if (now > _timelineTarget)
        {
            _timelineShift += now - _timelineTarget;
            _timelineTarget = now;
        }
    }

    private void AdvanceTimeline(double milliseconds)
    {
        ClampTimelineBacklog();
        if (milliseconds > 0) _timelineTarget += TimeSpan.FromTicks((long)Math.Round(milliseconds * TimeSpan.TicksPerMillisecond));
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
                if (-remaining > _lagAllowance)
                {
                    _timelineShift += now - _timelineTarget;
                    _timelineTarget = now;
                    _lagAllowance = MaxTimelineLag;
                }

                return;
            }

            // Ahead of the deadline again: any step overhead has been caught up.
            _lagAllowance = MaxTimelineLag;

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

    // ---------------- Blocks: If / Else / End If, Repeat / End Repeat ----------------

    /// <summary>Everything one run needs, so the block runner doesn't depend on loose locals.</summary>
    private sealed class RunContext
    {
        public RunContext(List<MacroStep> steps, BlockStructure blocks, double speed, HeldInput held,
            IConditionEvaluator conditions, int? totalIterations, int start, int end)
        {
            Start = Math.Clamp(start, 0, steps.Count);
            End = Math.Clamp(end, Start, steps.Count);
            Steps = steps;
            Blocks = blocks;
            Speed = speed;
            Held = held;
            Conditions = conditions;
            TotalIterations = totalIterations;
        }

        public List<MacroStep> Steps { get; }
        public BlockStructure Blocks { get; }
        /// <summary>Playback speed; the editor's live speed selector can change it between steps.</summary>
        public double Speed { get; set; }

        public HeldInput Held { get; }
        public IConditionEvaluator Conditions { get; }
        public int? TotalIterations { get; }

        /// <summary>First step to play (0 for a normal run).</summary>
        public int Start { get; }

        /// <summary>One past the last step to play (Steps.Count for a normal run).</summary>
        public int End { get; }

        /// <summary>True for "Run step" / "Play from here" (only part of the list plays).</summary>
        public bool IsPartial => Start != 0 || End != Steps.Count;

        public int Iteration { get; set; }
        public int StepsExecuted { get; set; }

        /// <summary>Last answer of each If (by step index), so a loop doesn't log the same answer every turn.</summary>
        public Dictionary<int, bool> LastIfAnswer { get; } = new();

        /// <summary>"Play from here" slice: the macro's index of each played step (null = the list IS the macro).</summary>
        public IReadOnlyList<int>? SourceIndexes { get; init; }

        /// <summary>"Play from here" slice: the macro's step count (0 = the list IS the macro).</summary>
        public int SourceCount { get; init; }

        /// <summary>1-based step number as the editor shows it (the macro's numbering, also for a slice).</summary>
        public int StepNumberOf(int index) =>
            SourceIndexes is { } map && index >= 0 && index < map.Count ? map[index] + 1 : index + 1;

        /// <summary>Step count as the editor shows it.</summary>
        public int StepCountShown => SourceCount > 0 ? SourceCount : Steps.Count;
    }

    /// <summary>State of one running Repeat block. Lives on a stack, innermost last.</summary>
    private sealed class LoopFrame
    {
        public LoopFrame(int start, TimeSpan startedAt)
        {
            Start = start;
            StartedAt = startedAt;
        }

        /// <summary>Index of the Repeat step.</summary>
        public int Start { get; }

        /// <summary>Active time (paused time not counted) when the loop started.</summary>
        public TimeSpan StartedAt { get; }

        /// <summary>Completed turns.</summary>
        public int Turns { get; set; }

        /// <summary>Active time of the last condition check (While / Until), to space the checks.</summary>
        public TimeSpan? LastCheckAt { get; set; }
    }

    /// <summary>
    /// Runs the step list once, from top to bottom, following the block jumps.
    /// pc ("program counter") is the index of the step to run next.
    /// </summary>
    private async Task RunStepsAsync(RunContext run, CancellationToken token)
    {
        var steps = run.Steps;
        var blocks = run.Blocks;
        var loops = new List<LoopFrame>();
        var pc = run.Start;

        while (pc < run.End)
        {
            await WaitIfPausedAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            if (TakePendingSpeed() is { } newSpeed && !newSpeed.Equals(run.Speed))
            {
                run.Speed = newSpeed;
                CurrentSpeed = newSpeed;
                Message?.Invoke(this, $"Playback speed changed to {newSpeed:0.##}× (from step {run.StepNumberOf(pc)}).");
            }

            var step = steps[pc];
            switch (step)
            {
                case IfStep ifStep:
                {
                    // A disabled If skips its whole block (both branches).
                    if (!ifStep.Enabled)
                    {
                        pc = blocks.EndOf(pc) + 1;
                        break;
                    }

                    Report(run, pc, loops);
                    var answer = await EvaluateConditionAsync(run, ifStep.Condition, token, label: "If").ConfigureAwait(false);
                    run.StepsExecuted++;
                    LogIfAnswer(run, pc, answer.IsTrue, answer.Text);

                    // Optional "when true" action on the spot the condition saw (not for "is NOT on screen").
                    if (answer.IsTrue && answer.Seen && ifStep.ThenAction != FoundTextAction.None)
                    {
                        await PerformFoundActionAsync(ifStep.ThenAction, answer.X + ifStep.ThenOffsetX, answer.Y + ifStep.ThenOffsetY,
                            ifStep.ThenHoldMs, FoundActionTiming.Default, run.Held, token, answer.Match).ConfigureAwait(false);
                    }

                    SyncTimelineToNow();
                    var elseIndex = blocks.ElseOf(pc);
                    pc = answer.IsTrue ? pc + 1 : (elseIndex >= 0 ? elseIndex + 1 : blocks.EndOf(pc) + 1);
                    break;
                }

                case ElseStep:
                    // Only reached at the end of the "true" part: skip the Else part.
                    pc = blocks.EndOf(pc) + 1;
                    break;

                case EndIfStep:
                    pc++;
                    break;

                case RepeatStep repeat:
                {
                    // A disabled Repeat skips its whole block.
                    if (!repeat.Enabled)
                    {
                        pc = blocks.EndOf(pc) + 1;
                        break;
                    }

                    var frame = new LoopFrame(pc, ActiveTime);
                    loops.Add(frame);
                    Report(run, pc, loops);
                    run.StepsExecuted++;
                    if (await StartNextTurnAsync(run, repeat, frame, token).ConfigureAwait(false))
                    {
                        pc++;
                    }
                    else
                    {
                        loops.RemoveAt(loops.Count - 1);
                        pc = blocks.EndOf(pc) + 1;
                    }

                    SyncTimelineToNow();
                    break;
                }

                case EndRepeatStep:
                {
                    var start = blocks.StartOf(pc);
                    if (run.IsPartial && (loops.Count == 0 || loops[^1].Start != start))
                    {
                        // "Play from here" started inside this loop's body: this was its one (partial) turn.
                        pc++;
                        break;
                    }

                    if (loops.Count == 0 || loops[^1].Start != start || steps[start] is not RepeatStep repeat)
                        throw new InvalidOperationException($"End Repeat at step {pc + 1} doesn't belong to the running loop."); // can't happen after validation

                    var frame = loops[^1];
                    frame.Turns++;
                    if (await StartNextTurnAsync(run, repeat, frame, token).ConfigureAwait(false))
                    {
                        pc = start + 1;
                    }
                    else
                    {
                        loops.RemoveAt(loops.Count - 1);
                        pc++;
                    }

                    SyncTimelineToNow();
                    break;
                }

                case FeedbackLoopStep feedback when feedback.Enabled:
                    Report(run, pc, loops);
                    await RunFeedbackLoopAsync(run, pc, loops, feedback, token).ConfigureAwait(false);
                    SyncTimelineToNow();
                    run.StepsExecuted++;
                    pc++;
                    break;

                default:
                    _skipNextStep = false;
                    if (step.Enabled)
                    {
                        // Smooth mouse: a Delay right before a cursor jump glides there (see SmoothMouseMoves).
                        _glideTarget = step is DelayStep && pc + 1 < steps.Count ? JumpTargetOf(steps[pc + 1]) : null;
                        _glideDuringDelay = step is DelayStep delay && pc + 1 < steps.Count ? RecordedGlideAfter(delay, steps[pc + 1]) : null;
                        Report(run, pc, loops);
                        if (IsDeadlineSynced(step))
                            await ExecuteDeadlineSyncedAsync(step, run.Speed, run.Held, token).ConfigureAwait(false);
                        else
                            await ExecuteStepAsync(step, run.Speed, run.Held, token).ConfigureAwait(false);
                        run.StepsExecuted++;
                    }

                    pc++;
                    if (_skipNextStep)
                    {
                        _skipNextStep = false;
                        pc = SkipTarget(steps, blocks, pc);
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Where to continue when a Find step says "skip the next step": past the step at <paramref name="next"/>, or past
    /// the whole If / Repeat block that starts there. A block end (Else / End If / End Repeat) isn't a step to skip,
    /// so playback simply carries on there (the block keeps working normally).
    /// </summary>
    private static int SkipTarget(List<MacroStep> steps, BlockStructure blocks, int next)
    {
        if (next >= steps.Count) return next;
        return steps[next] switch
        {
            IfStep or RepeatStep => blocks.EndOf(next) + 1,
            ElseStep or EndIfStep or EndRepeatStep => next,
            _ => next + 1,
        };
    }

    private void Report(RunContext run, int index, List<LoopFrame> loops, string? extra = null)
    {
        // Live progress bar: what plays now and how long it should take (only when it has a known length).
        var step = run.Steps[index];
        Volatile.Write(ref _stepTiming, ExpectedDurationMs(step, run.Speed) is { } expected
            ? new StepTiming(step.Id, UiActiveTime(), expected)
            : null);

        string? loopText = null;
        if (loops.Count > 0 && run.Steps[loops[^1].Start] is RepeatStep repeat)
        {
            var turn = loops[^1].Turns + 1;
            loopText = repeat.Mode == RepeatMode.Times ? $"loop turn {turn} of {repeat.Count}" : $"loop turn {turn}";
        }

        if (extra is not null) loopText = loopText is null ? extra : $"{loopText} · {extra}";

        ProgressChanged?.Invoke(this, new PlaybackProgress(run.Iteration, run.TotalIterations, run.StepNumberOf(index), run.StepCountShown, run.Steps[index], loopText));
    }

    private void LogIfAnswer(RunContext run, int index, bool isTrue, string text)
    {
        // Inside loops the same If can run thousands of times: only log the first answer and changes.
        if (run.LastIfAnswer.TryGetValue(index, out var previous) && previous == isTrue) return;
        run.LastIfAnswer[index] = isTrue;
        Message?.Invoke(this, $"If (step {run.StepNumberOf(index)}): {(isTrue ? "yes" : "no")}. {text}");
    }

    /// <summary>
    /// Decides whether a Repeat block runs another turn: count reached, safety limits, then (While / Until) the
    /// condition. Returns false to leave the loop normally; throws <see cref="LoopLimitException"/> when a limit is
    /// reached and the loop is set to stop the macro.
    /// </summary>
    private async Task<bool> StartNextTurnAsync(RunContext run, RepeatStep repeat, LoopFrame frame, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var number = frame.Start + 1;
        var turns = frame.Turns;
        string Turns(int n) => n == 1 ? "1 turn" : $"{n:N0} turns";

        if (repeat.Mode == RepeatMode.Times && turns >= repeat.Count)
        {
            Message?.Invoke(this, $"Repeat (step {number}) finished: {Turns(turns)}.");
            return false;
        }

        if (repeat.Mode != RepeatMode.Times && repeat.MaxIterations > 0 && turns >= repeat.MaxIterations)
            return LimitReached(repeat, number, $"reached its limit of {Turns(repeat.MaxIterations)}");

        Condition? condition = null;
        if (repeat.Mode != RepeatMode.Times)
        {
            condition = repeat.Condition ?? throw new InvalidOperationException($"The Repeat at step {number} has no condition.");
            var search = condition.Search ?? throw new InvalidOperationException($"The condition of the Repeat at step {number} is damaged.");

            // Keep condition checks at least "check every" apart, so a short (or empty) loop can't hammer the CPU.
            if (frame.LastCheckAt is { } lastCheck)
            {
                var wait = TimeSpan.FromMilliseconds(search.CheckIntervalMs) - (ActiveTime - lastCheck);
                if (wait > TimeSpan.Zero) await WaitAsync(wait.TotalMilliseconds, token).ConfigureAwait(false);
            }
        }

        if (repeat.MaxDurationMs > 0 && ActiveTime - frame.StartedAt >= TimeSpan.FromMilliseconds(repeat.MaxDurationMs))
            return LimitReached(repeat, number, $"reached its time limit of {repeat.MaxDurationMs / 1000.0:0.#} s after {Turns(turns)}");

        if (condition is null) return true; // Times: no condition to check

        // When the loop would end because its target is NOT seen (While "… is on screen", Until "… is NOT on screen"),
        // a single missed frame isn't trusted: the target may just be re-spawning (a game button that appears again
        // 100-250 ms after a click). Keep looking for up to the grace window and only end when it stays away.
        bool KeepGoing(bool isTrue) => repeat.Mode == RepeatMode.While ? isTrue : !isTrue;
        var grace = repeat.EndsWhenTargetMissing ? repeat.EffectiveKeepCheckingForMs : 0;
        var answer = grace > 0
            ? await EvaluateConditionAsync(run, condition, token,
                stopWhen: c => KeepGoing(c.IsTrue),
                window: TimeSpan.FromMilliseconds(Math.Max(grace, condition.Search!.TimeoutMs)), label: "Repeat").ConfigureAwait(false)
            : await EvaluateConditionAsync(run, condition, token, label: "Repeat").ConfigureAwait(false);
        frame.LastCheckAt = ActiveTime;

        var keepGoing = KeepGoing(answer.IsTrue);
        if (!keepGoing)
        {
            var reason = repeat.Mode == RepeatMode.While ? "its condition is false" : "its condition is true";
            if (grace > 0) reason += $" (kept checking for {grace} ms)";
            Message?.Invoke(this, $"Repeat (step {number}) finished after {Turns(turns)}: {reason}. {answer.Text}");
        }

        return keepGoing;
    }

    private bool LimitReached(RepeatStep repeat, int number, string what)
    {
        var text = $"Repeat (step {number}) {what}.";
        if (repeat.WhenLimitReached == LoopLimitBehavior.ExitLoop)
        {
            Message?.Invoke(this, text + " Leaving the loop and continuing after its End Repeat.");
            return false;
        }

        throw new LoopLimitException(text + " The macro was stopped to be safe. If that many turns are expected, raise the limit, or set the loop to continue when the limit is reached.");
    }

    /// <summary>The answer to a condition plus a sentence for the log.</summary>
    private readonly record struct ConditionAnswer(bool IsTrue, string Text, bool Seen = false, int X = 0, int Y = 0, ScreenRect Match = default);

    /// <summary>
    /// Checks a condition, waiting up to its "keep checking for" time for it to become true (0 = look once).
    /// Uses the same detection as the Find steps; paused time doesn't count; Pause / Stop / Esc work between and during
    /// checks. When the condition sees its text / image / colour, that spot becomes the "last found" spot for clicks.
    /// </summary>
    private async Task<ConditionAnswer> EvaluateConditionAsync(RunContext run, Condition condition, CancellationToken token,
        Func<ConditionCheck, bool>? stopWhen = null, TimeSpan? window = null, string label = "Condition")
    {
        var conditions = run.Conditions;
        var search = condition.Search ?? throw new InvalidOperationException("This condition has no valid search settings. The macro may be damaged.");
        var timeout = window ?? TimeSpan.FromMilliseconds(search.TimeoutMs);
        stopWhen ??= c => c.IsTrue;
        var timer = conditions.NewTimer(condition);
        var started = ActiveTime;

        ConditionCheck check;
        while (true)
        {
            await WaitIfPausedAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            check = await conditions.CheckAsync(condition, timer, token).ConfigureAwait(false);
            if (timer is not null) RecordProbeTime(check.ElapsedMs);
            if (check.Seen) RememberFound(check.X, check.Y, check.Match);

            if (stopWhen(check) || ActiveTime - started >= timeout) break;

            await WaitAsync(search.CheckIntervalMs, token).ConfigureAwait(false);
        }

        ResetTimeline(); // checking takes as long as it takes; start timing again from here
        if (check.Seen) ReportTarget(check.X, check.Y, check.Match, check.Confidence, label, check.ElapsedMs);
        var stats = timer is null ? string.Empty : " " + timer.Describe(ActiveTime - started);
        return new ConditionAnswer(check.IsTrue, $"({conditions.Describe(condition)}: {(check.IsTrue ? "true" : "false")}) {check.Details}{stats}",
            check.Seen, check.X, check.Y, check.Match);
    }

    // ---------------- Feedback loop (Stage 4) ----------------

    /// <summary>
    /// Runs a Feedback loop step: every frame (at most "captures per second"):
    /// 1. Pause / Stop / Esc are checked (pause releases the key / button and presses it again on resume, like every step).
    /// 2. Max runtime (paused time not counted).
    /// 3. The "failed when" / "done when" conditions, each at most once per its "check every".
    /// 4. Capture the bar area, find the marker and zone (<see cref="FeedbackSensor"/>).
    /// 5. Decide (<see cref="FeedbackController"/>) and send input only when the decision changes.
    /// When the loop ends for any reason (done, failed, time up, target lost, Stop / Esc, an error) the key / button is
    /// released and ONE summary line is logged (frames, frame rate, capture / detection time, input changes, why it
    /// ended). Nothing is logged per frame.
    /// </summary>
    private async Task RunFeedbackLoopAsync(RunContext run, int index, List<LoopFrame> loops, FeedbackLoopStep step, CancellationToken token)
    {
        _inFeedbackLoop = true;
        try
        {
            await RunFeedbackLoopCoreAsync(run, index, loops, step, token).ConfigureAwait(false);
        }
        finally
        {
            _inFeedbackLoop = false;
        }
    }

    private async Task RunFeedbackLoopCoreAsync(RunContext run, int index, List<LoopFrame> loops, FeedbackLoopStep step, CancellationToken token)
    {
        var number = run.StepNumberOf(index);
        var capture = _capture ?? throw new ScreenCaptureUnavailableException("Screen capture isn't available, so Feedback loop steps can't run.");
        var sensor = new FeedbackSensor(step, capture, _clock);
        // Classic region rules, or Feedback Loop 2.0 Smart Predictive (PD lead + hover pulse + reversal brake).
        var controller = FeedbackControllers.Create(step);
        var stats = new FeedbackStats();
        var held = run.Held;
        var key = new HeldKey(step.VirtualKey);
        var maxRuntime = TimeSpan.FromMilliseconds(step.MaxRuntimeMs);
        var frameInterval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / Math.Clamp(step.MaxCapturesPerSecond, 1, MacroValidator.MaxCapturesPerSecond));
        var startedAt = ActiveTime;
        TimeSpan? lastCompleteCheck = null;
        TimeSpan? lastFailCheck = null;
        var nextProgress = TimeSpan.Zero;
        var waiting = false;
        string? ending = null;        // why the loop ended, for the summary line
        Exception? failure = null;    // thrown after the summary when the loop ends as an error

        // Time since the loop started, without paused time (the controller, runtime and rate limit all use this).
        TimeSpan Now() => ActiveTime - startedAt;

        bool IsDown() => step.InputKind == FeedbackInputKind.MouseButton
            ? held.Buttons.Contains(step.MouseButton)
            : held.Keys.Contains(key);

        // All input goes through here, so the engine always knows what is held (Stop / pause / errors release it).
        void SetInput(bool down)
        {
            if (step.InputKind == FeedbackInputKind.MouseButton)
            {
                _input.MouseButton(step.MouseButton, down);
                if (down) held.Buttons.Add(step.MouseButton);
                else held.Buttons.Remove(step.MouseButton);
            }
            else
            {
                _input.Key(step.VirtualKey, down);
                if (down) held.Keys.Add(key);
                else held.Keys.Remove(key);
            }
        }

        // "Stop the macro" or "continue": sets the ending text and, for stop, the error to throw.
        void End(string what, LoopLimitBehavior behavior)
        {
            if (behavior == LoopLimitBehavior.StopMacro)
            {
                ending = what;
                failure = new FeedbackLoopException($"Feedback loop (step {number}) {what}. The macro was stopped.");
            }
            else
            {
                ending = what + "; continuing with the next step";
            }
        }

        try
        {
            // The loop takes over this key / button and starts from "released".
            if (IsDown()) SetInput(false);

            while (true)
            {
                await WaitIfPausedAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var frameStart = Now();

                if (frameStart >= maxRuntime)
                {
                    End($"reached its max runtime of {step.MaxRuntimeMs / 1000.0:0.###} s", step.WhenTimeUp);
                    break;
                }

                // Stop conditions: failure first (if both are true, the failure wins).
                if (step.FailWhen is { } fail && IsConditionDue(fail, lastFailCheck, frameStart))
                {
                    lastFailCheck = frameStart;
                    var check = await CheckFeedbackConditionAsync(run, fail, stats, token).ConfigureAwait(false);
                    if (check.IsTrue)
                    {
                        End($"failed: its \"failed when\" condition is true ({check.Details.TrimEnd('.')})", step.WhenFailed);
                        break;
                    }
                }

                if (step.CompleteWhen is { } complete && IsConditionDue(complete, lastCompleteCheck, frameStart))
                {
                    lastCompleteCheck = frameStart;
                    var check = await CheckFeedbackConditionAsync(run, complete, stats, token).ConfigureAwait(false);
                    if (check.IsTrue)
                    {
                        ending = $"finished: its \"done when\" condition is true ({check.Details.TrimEnd('.')})";
                        break;
                    }
                }

                // Look, decide, act.
                var frame = await sensor.ReadAsync(token).ConfigureAwait(false);
                stats.Add(frame);
                var decision = controller.Update(frame.Observation, Now());
                if (decision.Deferred) stats.Deferred++;
                if (decision.Braked) stats.Brakes++;

                switch (decision.Command)
                {
                    case FeedbackCommand.Press:
                        if (!IsDown()) SetInput(true);
                        stats.Presses++;
                        break;
                    case FeedbackCommand.Release:
                        if (IsDown()) SetInput(false);
                        stats.Releases++;
                        break;
                    case FeedbackCommand.Tap:
                        if (IsDown()) SetInput(false);
                        SetInput(true);
                        await WaitAsync(TimeSpan.FromMilliseconds(step.TapMs), token).ConfigureAwait(false);
                        if (IsDown()) SetInput(false);
                        stats.Taps++;
                        break;
                }

                // "Finish when it reaches the zone": do the inside rule once (if this frame didn't already) and end.
                if (step.FinishWhenInZone && decision.Region == FeedbackRegion.Inside)
                {
                    switch (step.WhenInsideZone)
                    {
                        case FeedbackAction.Tap when decision.Command != FeedbackCommand.Tap:
                            if (IsDown()) SetInput(false);
                            SetInput(true);
                            await WaitAsync(TimeSpan.FromMilliseconds(step.TapMs), token).ConfigureAwait(false);
                            if (IsDown()) SetInput(false);
                            stats.Taps++;
                            break;
                        case FeedbackAction.Release when IsDown():
                            SetInput(false);
                            stats.Releases++;
                            break;
                    }

                    ending = $"finished: the marker reached the zone after {frameStart.TotalSeconds:0.00} s";
                    break;
                }

                if (controller.IsWaiting && !waiting) stats.LostEpisodes++;
                waiting = controller.IsWaiting;

                if (decision.TargetLost)
                {
                    var what = $"lost its target: the marker{(step.ZoneMode == FeedbackZoneMode.DetectedByColor ? " or zone" : "")} wasn't seen clearly for {step.LostTimeoutMs} ms";
                    End(what, step.WhenTargetLost == FeedbackLostAction.StopMacro ? LoopLimitBehavior.StopMacro : LoopLimitBehavior.ExitLoop);
                    break;
                }

                if (frameStart >= nextProgress)
                {
                    var state = waiting ? "waiting for the target" : controller.IsHeld ? "holding" : "released";
                    if (controller is PredictiveFeedbackController smart && !waiting)
                        state += SmartTelemetry(smart);
                    Report(run, index, loops, $"feedback {frameStart.TotalSeconds:0.0} s, {state}");
                    nextProgress = frameStart + FeedbackProgressInterval;
                }

                // Rate limit: at most "captures per second" frames.
                var rest = frameInterval - (Now() - frameStart);
                if (rest > TimeSpan.Zero) await WaitAsync(rest, token).ConfigureAwait(false);
            }
        }
        finally
        {
            // Whatever ended the loop, let go of the key / button (Stop / Esc / errors: the engine releases everything too).
            if (IsDown()) TryRelease(() => SetInput(false));
            ResetTimeline(); // the loop took as long as it took; start timing again from here

            var why = ending ?? (token.IsCancellationRequested ? "was stopped" : "ended because of an error");
            Message?.Invoke(this, $"Feedback loop (step {number}) {why}. {stats.Describe(Now(), frameInterval)}");
        }

        if (failure is not null) throw failure;
    }

    /// <summary>", gap 12 px, bar 340 px/s, hover 55%" for the progress text of a Smart Predictive loop (invariant culture).</summary>
    internal static string SmartTelemetry(PredictiveFeedbackController smart)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var text = string.Format(culture, ", gap {0:0} px, bar {1:0} px/s", smart.PredictedError, smart.PlayerVelocity);
        if (smart.Duty is { } duty) text += string.Format(culture, ", hover {0:0}%", duty * 100);
        return text;
    }

    private static bool IsConditionDue(Condition condition, TimeSpan? lastCheck, TimeSpan now) =>
        lastCheck is not { } last || now - last >= TimeSpan.FromMilliseconds(Math.Max(1, condition.Search?.CheckIntervalMs ?? 250));

    /// <summary>Looks once for a feedback loop's stop condition. A condition that sees its target also sets the "last found" spot.</summary>
    private async Task<ConditionCheck> CheckFeedbackConditionAsync(RunContext run, Condition condition, FeedbackStats stats, CancellationToken token)
    {
        var check = await run.Conditions.CheckAsync(condition, null, token).ConfigureAwait(false);
        if (condition.Kind != ConditionKind.TextVisible) RecordProbeTime(check.ElapsedMs);
        stats.ConditionChecks++;
        if (check.Seen) RememberFound(check.X, check.Y, check.Match);

        return check;
    }

    // ---------------- Steps ----------------

    /// <summary>
    /// Recorded-style input steps that play on the deadline-synced virtual timeline: a key press and a mouse click at a
    /// position (or where the cursor is). Clicks on something vision found (last found spot, Smart-Aim) are not: looking
    /// takes as long as it takes, so their timing starts again after the look.
    /// </summary>
    private static bool IsDeadlineSynced(MacroStep step) =>
        step is KeyPressStep || step is MouseClickStep { UseLastFoundText: false, SmartAimAtTarget: false };

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
        if (skipped > TimeSpan.Zero && owed + skipped <= MaxStepOverheadCatchUp)
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

            case DelayStep d:
                if (SmoothMouseMoves && _glideTarget is { } target && d.DurationMs > 0)
                {
                    // Wait for the first part of the Delay, then glide to the next step's spot during the rest.
                    var total = Vary(d.DurationMs / speed, HumanizeMinDelayMs);
                    var glide = Math.Min(total, MaxGlideMs);
                    AdvanceTimeline(total - glide);
                    await WaitForTimelineAsync(token).ConfigureAwait(false);
                    var before = _timelineTarget;
                    await GlideAsync(target, TimeSpan.FromMilliseconds(glide), token).ConfigureAwait(false);
                    // Whatever the glide didn't use (it skipped, or rounding) is still waited for.
                    var used = (_timelineTarget - before).TotalMilliseconds;
                    AdvanceTimeline(glide - used);
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
                // Its glide already happened during the Delay before it: just make sure it ends exactly on the spot.
                _glideAbsorbedBy = null;
                if (_cursor != (m.X, m.Y)) MoveTo(m.X, m.Y);
                break;

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
                _glideAbsorbedBy = null;
                await MoveCursorAsync(m.X, m.Y, m.MoveStyle, m.MoveDurationMs, speed, onTimeline: true, token).ConfigureAwait(false);
                break;

            case MouseClickStep c:
                (int X, int Y)? start = null; // where the click happens, if known (needed for a smooth drag)
                var onFoundTarget = false;     // clicking something vision found (last found spot / Smart-Aim match)
                // Humanize moves real clicks a few pixels (never a plain Down / Up, which may belong to a recorded drag).
                var jitters = c.Action is MouseClickAction.Click or MouseClickAction.DoubleClick or MouseClickAction.Hold;
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
                    // Smart-Aim: the recorded target may have moved a little; click the same spot on it (or X / Y if not found).
                    (int X, int Y) aimSpot = (c.X, c.Y);
                    var aimBounds = default(ScreenRect);
                    if (c.SmartAimAtTarget && c.HasTargetThumbnail)
                    {
                        (aimSpot, aimBounds) = await SmartAimAsync(c, token).ConfigureAwait(false);
                        SyncTimelineToNow(); // looking takes as long as it takes; the click's own timing starts now
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
                        AdvanceTimeline(ScaledHoldMs(Math.Max(MinClickHoldMs, c.HoldDurationMs), speed));
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
                        _input.MouseButton(c.Button, true);
                        held.Buttons.Add(c.Button);
                        break;
                    case MouseClickAction.Up:
                        _input.MouseButton(c.Button, false);
                        held.Buttons.Remove(c.Button);
                        break;
                    case MouseClickAction.Hold:
                        // Press, keep it down for HoldMs (on the timeline, so Speed applies), release.
                        _input.MouseButton(c.Button, true);
                        held.Buttons.Add(c.Button);
                        AdvanceTimeline(ScaledHoldMs(c.HoldMs, speed));
                        await WaitForTimelineAsync(token).ConfigureAwait(false);
                        _input.MouseButton(c.Button, false);
                        held.Buttons.Remove(c.Button);
                        await WaitAfterClickAsync(c, speed, token).ConfigureAwait(false);
                        break;
                    case MouseClickAction.Drag:
                        await DragAsync(c, start, speed, held, token).ConfigureAwait(false);
                        break;
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
                switch (k.Action)
                {
                    case KeyAction.Press when k.HoldMs > 0:
                    {
                        // Keep the key down for the recorded time, e.g. a 120 ms W tap in a game, or a 2 s hold in Notepad.
                        var modifiers = ModifierKeys(k.Modifiers);
                        foreach (var vk in modifiers)
                        {
                            _input.Key(vk, true);
                            held.Keys.Add(new HeldKey(vk));
                        }

                        _input.Key(k.VirtualKey, true);
                        held.Keys.Add(new HeldKey(k.VirtualKey));

                        // Synthetic SendInput does not trigger OS hardware typematic auto-repeat. For non-modifier keys
                        // held >= 300 ms, wait the standard ~250 ms typematic delay and then emit repeated KeyDown
                        // pulses every ~33 ms (~30 Hz) without intermediate KeyUp, followed by a single KeyUp at the end.
                        if (k.HoldMs >= 300 && !VirtualKeys.IsModifier(k.VirtualKey))
                        {
                            const int typematicDelayMs = 250;
                            const int typematicRepeatMs = 33;
                            var elapsedHoldMs = Math.Min(typematicDelayMs, k.HoldMs);
                            AdvanceTimeline(elapsedHoldMs / speed);
                            await WaitForTimelineAsync(token).ConfigureAwait(false);

                            while (elapsedHoldMs < k.HoldMs)
                            {
                                _input.Key(k.VirtualKey, true);
                                var stepMs = Math.Min(typematicRepeatMs, k.HoldMs - elapsedHoldMs);
                                AdvanceTimeline(stepMs / speed);
                                await WaitForTimelineAsync(token).ConfigureAwait(false);
                                elapsedHoldMs += stepMs;
                            }
                        }
                        else
                        {
                            AdvanceTimeline(ScaledHoldMs(k.HoldMs, speed));
                            await WaitForTimelineAsync(token).ConfigureAwait(false);
                        }

                        _input.Key(k.VirtualKey, false);
                        held.Keys.Remove(new HeldKey(k.VirtualKey));
                        for (var i = modifiers.Count - 1; i >= 0; i--)
                        {
                            _input.Key(modifiers[i], false);
                            held.Keys.Remove(new HeldKey(modifiers[i]));
                        }

                        break;
                    }
                    case KeyAction.Press:
                    {
                        var modifiers = ModifierKeys(k.Modifiers);
                        foreach (var vk in modifiers) _input.Key(vk, true);
                        _input.Key(k.VirtualKey, true);
                        _input.Key(k.VirtualKey, false);
                        for (var i = modifiers.Count - 1; i >= 0; i--) _input.Key(modifiers[i], false);
                        break;
                    }
                    case KeyAction.Down:
                        _input.Key(k.VirtualKey, true);
                        held.Keys.Add(new HeldKey(k.VirtualKey));
                        break;
                    case KeyAction.Up:
                        _input.Key(k.VirtualKey, false);
                        held.Keys.Remove(new HeldKey(k.VirtualKey));
                        break;
                }

                // Deadline-synced like clicks: sending the key doesn't push the rest of the macro back.
                KeepStepOverheadOnTimeline();
                break;

            case TypeTextStep t:
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

    // ---------------- Screen search (Find text / image / color) ----------------

    /// <summary>
    /// The shared waiting loop of the Find steps: looks at the screen until the goal is met (it appeared / it's gone)
    /// or the timeout passes. Timeouts and check intervals are real time (not affected by the Speed setting), and time
    /// spent paused doesn't count. Pause / Stop / Esc are honoured between and during checks.
    /// The looking itself is done by <see cref="ScreenProbes"/>, which conditions use too.
    /// </summary>
    private async Task SearchAsync(ScreenSearchStep step, CancellationToken token)
    {
        var search = (_probes ?? throw new InvalidOperationException("Playback isn't running.")).Prepare(step);
        var what = search.What;
        var timer = search.NewTimer();

        var started = _clock.Elapsed;
        var shiftAtStart = _timelineShift;
        var timeout = TimeSpan.FromMilliseconds(step.TimeoutMs);
        TimeSpan Elapsed() => _clock.Elapsed - started - (_timelineShift - shiftAtStart);
        string Stats() => timer is null ? string.Empty : " " + timer.Describe(Elapsed());

        while (true)
        {
            await WaitIfPausedAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            var result = await search.LookAsync(timer, token).ConfigureAwait(false);
            if (search.IsTimed) RecordProbeTime(result.ElapsedMs);

            if (step.Goal == WaitGoal.Appear && result.Found)
            {
                RememberFound(result.X, result.Y, result.Match);
                ReportTarget(result.X, result.Y, result.Match, result.Confidence, StepDescriber.DisplayName(step.Kind), result.ElapsedMs,
                    result.FromHotSpot);
                Message?.Invoke(this, result.FoundText + Stats());
                await PerformFoundActionAsync(step.Action, result.X + step.OffsetX, result.Y + step.OffsetY, step.ActionHoldMs,
                    FoundActionTiming.Of(step), _held ?? throw new InvalidOperationException("Playback isn't running."), token, result.Match).ConfigureAwait(false);
                if (step.MoveCursorAwayAfterAction && step.Action is not (FoundTextAction.None or FoundTextAction.Move))
                {
                    // "Park" the cursor so it (and its hover effect) doesn't cover the spot where the next button appears.
                    var (awayX, awayY) = AwaySpot(step, result.X, result.Y, result.Area);
                    await MoveCursorAsync(awayX, awayY, step.MoveStyle, step.MoveDurationMs, 1.0, onTimeline: false, token).ConfigureAwait(false);
                }

                return;
            }

            if (step.Goal == WaitGoal.Disappear && !result.Found)
            {
                Message?.Invoke(this, $"{what} is no longer on screen." + Stats());
                return;
            }

            if (Elapsed() >= timeout)
            {
                var seconds = timeout.TotalSeconds;
                string message;
                if (step.Goal == WaitGoal.Appear)
                {
                    message = step.TimeoutMs == 0
                        ? $"{what} wasn't found on screen."
                        : $"{what} didn't appear within {seconds:0.#} s.";
                    message += result.MissText;
                }
                else
                {
                    message = step.TimeoutMs == 0
                        ? $"{what} is still on screen."
                        : $"{what} was still on screen after {seconds:0.#} s.";
                    message += " " + result.FoundText;
                }

                message += Stats();

                if (step.WhenNotFound is TextNotFoundBehavior.Continue or TextNotFoundBehavior.SkipNextStep)
                {
                    if (step.Goal == WaitGoal.Appear) ForgetFound();

                    var skip = step.WhenNotFound == TextNotFoundBehavior.SkipNextStep;
                    _skipNextStep = skip;
                    Message?.Invoke(this, message + (skip ? " Skipping the next step." : " Continuing."));
                    return;
                }

                throw new DetectionTimeoutException(message);
            }

            await WaitAsync(step.CheckIntervalMs, token).ConfigureAwait(false);
        }
    }

    // ---------------- Wait for screen change / Focus window / Take screenshot ----------------

    /// <summary>The file the latest Take screenshot step saved (null before the first one).</summary>
    public string? LastScreenshotPath { get; private set; }

    /// <summary>
    /// "Wait for screen change": takes a starting picture of the area, then looks again every check interval until enough
    /// of it differs. The centre of the changed pixels becomes the last found spot (and gets the step's action). Time spent
    /// paused doesn't count, and after a pause the starting picture is taken again (the screen may have changed while
    /// you were doing something else).
    /// </summary>
    private async Task WaitForChangeAsync(WaitForChangeStep step, HeldInput held, CancellationToken token)
    {
        var capture = _capture ?? throw new ScreenCaptureUnavailableException("Screen capture isn't available, so Wait for screen change steps can't run.");
        var area = step.Region;
        var started = _clock.Elapsed;
        var shiftAtStart = _timelineShift;
        var timeout = TimeSpan.FromMilliseconds(Math.Max(0, step.TimeoutMs));
        TimeSpan Elapsed() => _clock.Elapsed - started - (_timelineShift - shiftAtStart);

        await WaitIfPausedAsync(token).ConfigureAwait(false);
        await ClearTargetOverlayAsync(token).ConfigureAwait(false); // the reticle must not be in (or vanish from) the starting picture
        var baseline = await capture.CaptureAsync(area, token).ConfigureAwait(false);
        var best = default(ScreenChange);

        while (true)
        {
            var shiftBeforeWait = _timelineShift;
            await WaitAsync(Math.Max(1, step.CheckIntervalMs), token).ConfigureAwait(false);
            await WaitIfPausedAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            var lookStarted = _clock.Elapsed;
            var frame = await capture.CaptureAsync(area, token).ConfigureAwait(false);
            if (_timelineShift != shiftBeforeWait || frame.Bounds != baseline.Bounds)
            {
                // Paused (or the area moved partly off screen): start comparing from what is there now.
                if (_timelineShift != shiftBeforeWait && await ClearTargetOverlayAsync(token).ConfigureAwait(false))
                    frame = await capture.CaptureAsync(area, token).ConfigureAwait(false);
                baseline = frame;
            }
            else
            {
                var change = ScreenChangeDetector.Compare(baseline, frame, step.PixelTolerance);
                RecordProbeTime((_clock.Elapsed - lookStarted).TotalMilliseconds);
                if (change.ChangedPercent > best.ChangedPercent) best = change;
                if (change.Any && change.ChangedPercent >= step.MinChangedPercent)
                {
                    var (x, y) = (change.CenterX, change.CenterY);
                    RememberFound(x, y, change.Bounds);
                    ReportTarget(x, y, change.Bounds, 1.0, StepDescriber.DisplayName(step.Kind));
                    Message?.Invoke(this, string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "The watched area changed: {0:0.##} % of its pixels, around ({1}, {2}).", change.ChangedPercent, x, y));
                    await PerformFoundActionAsync(step.Action, x, y, step.ActionHoldMs, FoundActionTiming.Snappy, held, token, change.Bounds)
                        .ConfigureAwait(false);
                    return;
                }
            }

            if (Elapsed() >= timeout)
            {
                var message = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "The watched area didn't change {0} (largest change: {1:0.##} % of its pixels, needed {2:0.##} %).",
                    step.TimeoutMs == 0 ? "when checked" : string.Format(System.Globalization.CultureInfo.InvariantCulture, "within {0:0.#} s", timeout.TotalSeconds),
                    best.ChangedPercent, step.MinChangedPercent);
                if (step.WhenNotFound is TextNotFoundBehavior.Continue or TextNotFoundBehavior.SkipNextStep)
                {
                    ForgetFound();
                    var skip = step.WhenNotFound == TextNotFoundBehavior.SkipNextStep;
                    _skipNextStep = skip;
                    Message?.Invoke(this, message + (skip ? " Skipping the next step." : " Continuing."));
                    return;
                }

                throw new DetectionTimeoutException(message);
            }
        }
    }

    /// <summary>
    /// Asks the app to hide the target-lock reticle now (see <see cref="TargetOverlayClearRequested"/>); when one was hidden,
    /// waits <see cref="OverlayClearSettleMs"/> so the next capture shows the screen without it. Returns true when it hid one.
    /// </summary>
    private async Task<bool> ClearTargetOverlayAsync(CancellationToken token)
    {
        if (TargetOverlayClearRequested is not { } handler) return false;
        var request = new OverlayClearRequest();
        try
        {
            handler(this, request);
        }
        catch (Exception ex)
        {
            Message?.Invoke(this, $"Target overlay error: {ex.Message}");
            return false;
        }

        if (!request.Cleared) return false;
        await WaitAsync(OverlayClearSettleMs, token).ConfigureAwait(false);
        return true;
    }

    /// <summary>How often "Focus window" looks for a window that isn't open yet.</summary>
    private const int FocusWindowPollMs = 250;

    /// <summary>
    /// "Focus window": finds the window by (part of) its title (waiting for it up to the step's time), restores it when
    /// minimized and brings it to the front, then pauses briefly so the app is ready for the next input.
    /// </summary>
    private async Task FocusWindowAsync(FocusWindowStep step, CancellationToken token)
    {
        var title = step.WindowTitle?.Trim() ?? string.Empty;
        var layout = _layout ?? throw new InvalidOperationException("The window list isn't available, so Focus window steps can't run.");
        var started = _clock.Elapsed;
        var shiftAtStart = _timelineShift;
        var timeout = TimeSpan.FromMilliseconds(Math.Max(0, step.WaitForWindowMs));
        TimeSpan Elapsed() => _clock.Elapsed - started - (_timelineShift - shiftAtStart);

        while (true)
        {
            await WaitIfPausedAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            if (SearchArea.FindWindowByTitleOrApp(layout.GetWindows(), title) is { } window)
            {
                if (layout is not IWindowFocuser focuser)
                {
                    Message?.Invoke(this, $"Found the window \"{window.Title}\", but this SmartMacro can't bring windows to the front.");
                    return;
                }

                var focused = focuser.TryFocus(window, step.RestoreIfMinimized);
                Message?.Invoke(this, focused
                    ? $"Brought the window \"{window.Title}\" to the front."
                    : $"Found the window \"{window.Title}\", but Windows didn't let SmartMacro bring it to the front (click it once by hand, or run SmartMacro as the same user / admin level as the game).");
                if (step.SettleMs > 0) await WaitAsync(step.SettleMs, token).ConfigureAwait(false);
                return;
            }

            if (Elapsed() >= timeout)
            {
                var message = step.WaitForWindowMs == 0
                    ? $"No open window has \"{title}\" in its title or app name."
                    : string.Format(System.Globalization.CultureInfo.InvariantCulture, "No window with \"{0}\" in its title or app name opened within {1:0.#} s.", title, timeout.TotalSeconds);
                if (step.WhenNotFound is TextNotFoundBehavior.Continue or TextNotFoundBehavior.SkipNextStep)
                {
                    var skip = step.WhenNotFound == TextNotFoundBehavior.SkipNextStep;
                    _skipNextStep = skip;
                    Message?.Invoke(this, message + (skip ? " Skipping the next step." : " Continuing."));
                    return;
                }

                throw new DetectionTimeoutException(message);
            }

            await WaitAsync(Math.Min(FocusWindowPollMs, Math.Max(1, (timeout - Elapsed()).TotalMilliseconds)), token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// "Take screenshot": captures the main monitor / all monitors / an area / an app window and saves it as a PNG. A
    /// screenshot that can't be taken or saved is only reported: the macro carries on (it's a log, not a goal).
    /// </summary>
    private async Task TakeScreenshotAsync(ScreenshotStep step, CancellationToken token)
    {
        var capture = _capture ?? throw new ScreenCaptureUnavailableException("Screen capture isn't available, so Take screenshot steps can't run.");
        ScreenRect area;
        try
        {
            area = ScreenshotFiles.ResolveArea(step, capture.GetVirtualScreen(), _layout);
        }
        catch (SearchAreaUnavailableException ex)
        {
            Message?.Invoke(this, $"📸 No screenshot taken: {ex.Message}");
            return;
        }

        ScreenFrame frame;
        try
        {
            frame = await capture.CaptureAsync(area, token).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            Message?.Invoke(this, $"📸 No screenshot taken: {ex.Message}");
            return;
        }

        var folder = ScreenshotFiles.FolderOf(step);
        var now = DateTime.Now;
        try
        {
            var path = await Task.Run(() => ScreenshotFiles.Save(frame.Image, folder, step.FileNamePrefix, now), token).ConfigureAwait(false);
            LastScreenshotPath = path;
            Message?.Invoke(this, $"📸 Screenshot saved: {path} ({frame.Bounds.Width}×{frame.Bounds.Height})");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Message?.Invoke(this, $"📸 Couldn't save the screenshot in \"{folder}\": {ex.Message}");
        }
    }

    /// <summary>How far below the clicked spot the cursor goes when the step searches the whole screen.</summary>
    public const int MoveAwayDistance = 150;

    /// <summary>
    /// Where "move the cursor away after clicking" puts the cursor: just outside the bottom-right corner of the search
    /// area (a custom area, or the app window that was searched), or — when a whole monitor / all monitors are searched —
    /// <see cref="MoveAwayDistance"/> pixels below the spot that was found.
    /// </summary>
    internal static (int X, int Y) AwaySpot(ScreenSearchStep step, int foundX, int foundY, ScreenRect searchedArea = default)
    {
        switch (step.EffectiveScope)
        {
            case SearchScope.CustomArea:
                return (step.RegionX + step.RegionWidth + 8, step.RegionY + step.RegionHeight + 8);
            case SearchScope.Window when !searchedArea.IsEmpty:
                return (searchedArea.Right + 8, searchedArea.Bottom + 8);
            default:
                return (foundX, foundY + MoveAwayDistance);
        }
    }

    /// <summary>
    /// How a "when found" / "when true" action moves and clicks: movement style + glide time, click hold, double-click
    /// gap and the wait after the click. If steps (which have no such settings) use the defaults: jump, 35 / 65 / 120 ms.
    /// </summary>
    private readonly record struct FoundActionTiming(MouseMoveStyle? Style, int MoveMs, int HoldMs, int DoubleGapMs, int SettleMs)
    {
        public static FoundActionTiming Default => new(null, 0, FoundClickHoldMs, DoubleClickGapMs, ClickSettleMs);

        /// <summary>Wait for screen change (no click settings of its own): jump, 6 ms rest, 12 ms hold – for things that move fast.</summary>
        public static FoundActionTiming Snappy => new(null, 0, MinTargetClickHoldMs, DoubleClickGapMs, ClickSettleMs);

        public static FoundActionTiming Of(ScreenSearchStep step) =>
            new(step.MoveStyle, step.MoveDurationMs, step.ClickHoldMs, step.DoubleClickIntervalMs, step.PostActionDelayMs);
    }

    /// <summary>
    /// The "when found" action of a Find step (and the "when true" action of an If) at a spot on screen (offset already
    /// added): move there, click / double-click / right-click, or hold the left button for <paramref name="holdMs"/>.
    /// The hold is tracked, so Stop / Esc / pause release it. Clicks and holds are followed by a short settle so the
    /// clicked window can react before the next step.
    /// </summary>
    private async Task PerformFoundActionAsync(FoundTextAction action, int x, int y, int holdMs, FoundActionTiming timing, HeldInput held,
        CancellationToken token, ScreenRect matchBounds = default)
    {
        if (action == FoundTextAction.None) return;
        if (action != FoundTextAction.Move) (x, y) = Jitter(x, y, matchBounds); // Humanize: stays inside what was found

        await MoveCursorAsync(x, y, timing.Style, timing.MoveMs, 1.0, onTimeline: false, token).ConfigureAwait(false);

        // Reliable game click: the cursor rests on the target for a moment before the press (games read the cursor once
        // per frame), and the button stays down at least MinTargetClickHoldMs.
        if (action != FoundTextAction.Move) await WaitAsync(TargetHoverMs, token).ConfigureAwait(false);
        var clickHold = Math.Max(MinTargetClickHoldMs, timing.HoldMs);
        switch (action)
        {
            case FoundTextAction.Click:
                await HeldClickAsync(MacroMouseButton.Left, clickHold, held, token).ConfigureAwait(false);
                break;
            case FoundTextAction.DoubleClick:
                await HeldClickAsync(MacroMouseButton.Left, clickHold, held, token).ConfigureAwait(false);
                await WaitAsync(Math.Max(0, timing.DoubleGapMs), token).ConfigureAwait(false);
                await HeldClickAsync(MacroMouseButton.Left, clickHold, held, token).ConfigureAwait(false);
                break;
            case FoundTextAction.RightClick:
                await HeldClickAsync(MacroMouseButton.Right, clickHold, held, token).ConfigureAwait(false);
                break;
            case FoundTextAction.Hold:
                _input.MouseButton(MacroMouseButton.Left, true);
                held.Buttons.Add(MacroMouseButton.Left);
                await WaitAsync(TimeSpan.FromMilliseconds(Math.Max(0, holdMs)), token).ConfigureAwait(false);
                _input.MouseButton(MacroMouseButton.Left, false);
                held.Buttons.Remove(MacroMouseButton.Left);
                break;
        }

        if (action != FoundTextAction.Move) await WaitAsync(Math.Max(0, timing.SettleMs), token).ConfigureAwait(false);
    }

    /// <summary>Time between cursor updates while dragging (about 120 per second, so sliders follow smoothly).</summary>
    public static readonly TimeSpan DragStepInterval = TimeSpan.FromMilliseconds(8);

    /// <summary>
    /// Drag: press at the start, move the cursor to (EndX, EndY) in small even steps over DragDurationMs (on the
    /// timeline, so Speed applies and Pause / Stop work in between), release at the end. Every step is a real move
    /// event with the button held, which is what sliders and drag-and-drop listen for. If the start isn't known (the
    /// cursor position couldn't be read), the cursor goes straight to the end.
    /// </summary>
    private async Task DragAsync(MouseClickStep c, (int X, int Y)? start, double speed, HeldInput held, CancellationToken token)
    {
        _input.MouseButton(c.Button, true);
        held.Buttons.Add(c.Button);
        ClampTimelineBacklog();

        var duration = TimeSpan.FromMilliseconds(Vary(Math.Max(0, c.DragDurationMs) / speed));
        var steps = start is null ? 1 : Math.Max(1, (int)Math.Ceiling(duration / DragStepInterval));
        var stepTime = duration / steps;
        for (var i = 1; i <= steps; i++)
        {
            if (stepTime > TimeSpan.Zero)
            {
                _timelineTarget += stepTime;
                await WaitForTimelineAsync(token).ConfigureAwait(false);
            }

            if (start is { } from && i < steps)
            {
                var t = i / (double)steps;
                MoveTo((int)Math.Round(from.X + (c.EndX - from.X) * t), (int)Math.Round(from.Y + (c.EndY - from.Y) * t));
            }
            else
            {
                MoveTo(c.EndX, c.EndY); // always end exactly on the target
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

    /// <summary>Largest Smart-Aim search distance (px each way).</summary>
    public const int MaxSmartAimRadiusPx = SmartAim.MaxRadiusPx;

    /// <summary>Logged (as a warning) when a click "on the last found spot" runs before anything was found.</summary>
    public const string SkippedClickNoTarget = "⚠ Skipped click at Last Found Target because no prior step has found a target in this run.";

    /// <summary>Logged (as a warning) when a move "to the last found spot" runs before anything was found.</summary>
    public const string SkippedMoveNoTarget = "⚠ Skipped move to Last Found Target because no prior step has found a target in this run.";

    /// <summary>
    /// Smart-Aim (Smart Click Snapshots): looks for the click's recorded thumbnail in a small area around where it was
    /// recorded (the thumbnail's rectangle grown by SmartAimRadiusPx on every side) and returns the same spot on it, so
    /// a button that moved a bit is still hit. When several copies are found, the one nearest the recorded spot wins.
    /// Not found (or no screen capture / a damaged picture) = the recorded X / Y. Never throws except for Stop.
    /// Returns the matched rectangle too (empty when not found), so Humanize keeps its jitter inside the target.
    /// </summary>
    private async Task<((int X, int Y) Spot, ScreenRect Bounds)> SmartAimAsync(MouseClickStep c, CancellationToken token)
    {
        (int X, int Y) recorded = (c.X, c.Y);
        if (_capture is not { } capture)
        {
            NoteSmartAim(c, "Smart-Aim: screen capture isn't available, so the click uses its recorded spot.");
            return (recorded, default);
        }

        // Decoded once per picture and shared (TemplateCache), so a Smart-Aim click inside a fast loop doesn't decode its
        // PNG again every turn.
        var template = SmartClickConversion.TryDecodeThumbnail(c, out var error);
        if (template is null)
        {
            NoteSmartAim(c, $"Smart-Aim: the recorded picture can't be used ({error}); clicking the recorded spot.");
            return (recorded, default);
        }

        var started = Stopwatch.GetTimestamp();
        var radius = SmartAim.RadiusOf(c);
        var region = SmartAim.SearchArea(c, template);

        ScreenFrame frame;
        try
        {
            frame = await capture.CaptureAsync(region, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            NoteSmartAim(c, $"Smart-Aim: couldn't look at the screen ({ex.Message}); clicking the recorded spot.");
            return (recorded, default);
        }

        if (frame.Image.Width < template.Width || frame.Image.Height < template.Height)
        {
            NoteSmartAim(c, "Smart-Aim: the area around the click is off screen; clicking the recorded spot.");
            return (recorded, default);
        }

        var (hit, bestScore) = SmartAim.Locate(c, template, frame, token);
        var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        RecordProbeTime(elapsedMs);
        if (hit is null)
        {
            NoteSmartAim(c, $"Smart-Aim: the clicked target wasn't found within {radius} px (best {bestScore:P0}, needs {SmartAim.OptionsFor(c).MinScore:P0}); clicking the recorded spot ({c.X}, {c.Y}).");
            return (recorded, default);
        }

        ReportTarget(hit.Bounds.CenterX, hit.Bounds.CenterY, hit.Bounds, hit.Score, "Smart-Aim", elapsedMs);
        NoteSmartAim(c, !hit.Moved
            ? $"Smart-Aim: the clicked target is where it was recorded ({hit.Score:P0} match)."
            : $"Smart-Aim: the clicked target moved by ({hit.ShiftX:+0;-0;0}, {hit.ShiftY:+0;-0;0}) px; clicking it at ({hit.X}, {hit.Y}) ({hit.Score:P0} match).");
        return ((hit.X, hit.Y), hit.Bounds);
    }

    /// <summary>Logs a Smart-Aim note, but not the same note for the same step twice in a row (clicks inside loops).</summary>
    private void NoteSmartAim(MouseClickStep c, string note)
    {
        if (_smartAimLastNote.TryGetValue(c.Id, out var last) && last == note) return;
        _smartAimLastNote[c.Id] = note;
        Message?.Invoke(this, note);
    }

    /// <summary>Absolute cursor move; remembers the spot so a later glide knows where it starts.</summary>
    private void MoveTo(int x, int y)
    {
        _input.MoveMouse(x, y);
        _cursor = (x, y);
    }

    /// <summary>
    /// Where a step jumps the cursor to before acting (null if it doesn't move the cursor to a fixed spot). Steps with their
    /// own movement style (Smooth glide / Human curve) glide by themselves, so they aren't listed here.
    /// </summary>
    private static (int X, int Y)? JumpTargetOf(MacroStep? step) => step switch
    {
        MouseMoveStep { Enabled: true, UseRelativeDelta: false, TargetMode: ClickTargetMode.Coordinates, MoveStyle: null or MouseMoveStyle.Instant } m => (m.X, m.Y),
        MouseClickStep { Enabled: true, UseLastFoundText: false, UseCurrentPosition: false, SmartAimAtTarget: false, MoveStyle: null or MouseMoveStyle.Instant } c => (c.X, c.Y),
        MouseScrollStep { Enabled: true, UseCurrentPosition: false } s => (s.X, s.Y),
        _ => null,
    };

    /// <summary>
    /// The Move step that should glide during <paramref name="delay"/> (see <see cref="RecordedPointMaxDelayMs"/>), or
    /// null: only an enabled Move mouse step with Smooth glide / Human curve whose glide is longer than a short Delay.
    /// </summary>
    private static MouseMoveStep? RecordedGlideAfter(DelayStep delay, MacroStep next) =>
        next is MouseMoveStep { Enabled: true, UseRelativeDelta: false, TargetMode: ClickTargetMode.Coordinates, MoveStyle: MouseMoveStyle.SmoothGlide or MouseMoveStyle.HumanCurve } move
        && delay.DurationMs > 0 && delay.DurationMs <= RecordedPointMaxDelayMs && move.MoveDurationMs > delay.DurationMs
            ? move
            : null;

    /// <summary>
    /// Smooth mouse movement (see <see cref="SmoothMouseMoves"/>): glides the cursor from where it is to
    /// <paramref name="target"/> over <paramref name="duration"/> (on the timeline, so the time is taken out of the
    /// Delay that comes before the jump). Does nothing when the start is unknown or the jump is tiny.
    /// </summary>
    private async Task GlideAsync((int X, int Y) target, TimeSpan duration, CancellationToken token)
    {
        (int X, int Y) from;
        if (_cursor is { } known) from = known;
        else if (_input.TryGetCursorPosition(out var cx, out var cy)) from = (cx, cy);
        else return;
        if (Math.Abs(target.X - from.X) + Math.Abs(target.Y - from.Y) < 3) return;

        ClampTimelineBacklog();
        var steps = Math.Max(1, (int)Math.Floor(duration / GlideStepInterval));
        var stepTime = duration / steps;
        for (var i = 1; i <= steps; i++)
        {
            _timelineTarget += stepTime;
            await WaitForTimelineAsync(token).ConfigureAwait(false);
            var t = i / (double)steps;
            // Ease in and out, like a hand: slow start, fast middle, slow arrival.
            var e = t * t * (3 - 2 * t);
            MoveTo((int)Math.Round(from.X + (target.X - from.X) * e), (int)Math.Round(from.Y + (target.Y - from.Y) * e));
        }
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
        AdvanceTimeline(ScaledHoldMs(holdMs, speed));
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
        if (onTimeline) ClampTimelineBacklog(); // far behind: glide from "now"

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
                    _timelineTarget += wait;
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
        var text = (step.Text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        for (var i = 0; i < text.Length; i++)
        {
            token.ThrowIfCancellationRequested();

            var length = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            _input.TypeCharacters(text.Substring(i, length));
            i += length - 1;

            if (step.CharacterDelayMs > 0 && i < text.Length - 1)
                await WaitAsync(step.CharacterDelayMs / speed, token).ConfigureAwait(false);
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

    /// <summary>A key the macro is holding. ScanCode 0 = "look it up from the virtual-key code".</summary>
    private readonly record struct HeldKey(int VirtualKey, int ScanCode = 0, bool Extended = false);

    /// <summary>Tracks what the macro is currently holding down so it can always be released.</summary>
    private sealed class HeldInput
    {
        public HashSet<HeldKey> Keys { get; } = new();
        public HashSet<MacroMouseButton> Buttons { get; } = new();

        public bool IsEmpty => Keys.Count == 0 && Buttons.Count == 0;

        /// <summary>Releases everything. Returns an error message if Windows refused, otherwise null.</summary>
        public string? ReleaseAll(IInputSimulator input)
        {
            string? error = null;
            foreach (var key in Keys)
            {
                try
                {
                    if (key.ScanCode == 0 && !key.Extended) input.Key(key.VirtualKey, false);
                    else input.KeyWithScanCode(key.VirtualKey, key.ScanCode, key.Extended, false);
                }
                catch (Exception ex) { error ??= $"Could not release a held key: {ex.Message}"; }
            }

            foreach (var button in Buttons)
            {
                try { input.MouseButton(button, false); }
                catch (Exception ex) { error ??= $"Could not release a held mouse button: {ex.Message}"; }
            }

            Keys.Clear();
            Buttons.Clear();
            return error;
        }
    }
}
