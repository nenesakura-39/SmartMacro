using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Targeting;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Vision;
using SmartMacro.Core.Workflow;

namespace SmartMacro.Core.Playback;

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
public sealed partial class PlaybackEngine
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
    /// seconds of drift. Up to this much is caught up this way; anything later than that (a slow step, an OS lag spike,
    /// a real PC stall) is dropped: the timeline restarts from "now", so playback never fires a burst of late input.
    /// </summary>
    public static readonly TimeSpan MaxStepOverheadCatchUp = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// No 0 ms catch-up bursts: when playback is behind, one wait is shortened by at most this share of its own length
    /// (a 16 ms gap between two recorded moves still lasts ≥ 8 ms). Whatever is still owed is taken out of the next waits,
    /// a little at a time, so games always see the inputs spaced out and the total time still matches the recording.
    /// </summary>
    public const double MaxCatchUpShare = 0.5;

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

    // Where the most recent click / move / vision target happened (watched by adaptive Delay steps).
    private (int X, int Y)? _lastActionSpot;

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
    private readonly IUiElementLocator? _uiLocator;
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
    private TimeSpan _carriedDebt;      // time still owed that the next waits take out a little at a time (see MaxCatchUpShare)
    private TimeSpan _pausedTotal;      // total time spent paused in this run (not counted by timeouts / loop limits)
    private bool _skipNextStep;         // set by a Find step that timed out with "skip the next step"

    private PlaybackState _state = PlaybackState.Idle;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource? _resumeSignal;
    private TimeSpan? _pauseRequestedAt; // clock time of the latest Pause() call (under _gate)

    // v1.8 Studio X Interactive Debugger: breakpoints (F9) and single-step execution (F10).
    private volatile bool _pauseBeforeNextStep;
    private volatile bool _stepOverRequested;
    private Guid? _lastBreakpointPausedStepId;

    /// <summary>
    /// v1.8 Studio X Debugger: optional predicate returning true when a step ID has an active breakpoint.
    /// When hit on a top-level macro step, playback pauses before running that step and reports its position.
    /// </summary>
    public Func<Guid, bool>? HasBreakpoint { get; set; }

    /// <param name="layout">
    /// Monitors and app windows, for Find steps / conditions that look at one monitor or inside one window. Without it the
    /// main monitor means the whole desktop, and a specific monitor / window can't be found.
    /// </param>
    public PlaybackEngine(IInputSimulator input, IPlaybackClock? clock = null, IOcrService? ocr = null, IScreenCapture? capture = null,
        IScreenLayout? layout = null, IUiElementLocator? uiLocator = null)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _clock = clock ?? new SystemPlaybackClock();
        _ocr = ocr;
        _capture = capture;
        _layout = layout;
        _uiLocator = uiLocator;
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

    /// <summary>
    /// Raised on the playback thread during a FeedbackLoopStep (~30–60 FPS) with live servo telemetry,
    /// and raised with <c>null</c> in <c>finally</c> when the loop ends so the HUD overlay hides immediately.
    /// </summary>
    public event EventHandler<FeedbackTelemetryData?>? FeedbackTelemetryUpdated;

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
            DelayStep { Mode: not DelayMode.Fixed } d => d.EffectiveMaxDurationMs / speed,
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
    private void ReportTarget(int x, int y, ScreenRect bounds, double confidence, string label, double elapsedMs = 0, bool fromHotSpot = false,
        IReadOnlyList<ScreenRect>? allMatches = null)
    {
        RecordVisionLock(elapsedMs);
        // Never flash the reticle from inside a feedback loop: at 120 looks per second it would sit on the bar it watches
        // (and in streamer mode the loop's own captures would see it).
        if (_inFeedbackLoop) return;
        if (TargetDetected is not { } handler) return;
        var box = bounds.IsEmpty ? new ScreenRect(x - 12, y - 12, 24, 24) : bounds;
        try
        {
            handler(this, new TargetDetection(box, Math.Clamp(confidence, 0, 1), label, x, y, Math.Max(0, elapsedMs), fromHotSpot)
            {
                AllMatches = allMatches,
            });
        }
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
            LastFailureSnapshotPath = null;
            _pendingSpeed = double.NaN;
            _smartAimLastNote.Clear();
            _sessionSuggestions.Clear();
            _hotSpots.Clear();
            Interlocked.Exchange(ref _lastProbeElapsedBits, 0);
            _statPausedTotal = TimeSpan.Zero;
            _statPausedSince = null;
            _skipNextStep = false;
            _pauseBeforeNextStep = false;
            _stepOverRequested = false;
            _lastBreakpointPausedStepId = null;
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
        var rootVariables = new VariableStore(macro.Variables);
        _probes = new ScreenProbes(_ocr, _capture, _clock, _layout, _hotSpots, rootVariables)
        {
            CursorPositionProvider = () => _cursor ?? _lastActionSpot,
            RandomGenerator = HumanizeRandom,
        };
        var iterationsCompleted = 0;
        var token = cts.Token;

        // The validator above already refused unbalanced blocks, so this structure is always valid here.
        var steps = macro.Steps;
        var watchers = macro.Watchers;
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
                watchers = CoordinateScaling.ApplyToWatchersCopy(watchers, mapping);
                var targetName = string.IsNullOrWhiteSpace(macro.TargetWindowTitle) ? "main screen" : "\"" + macro.TargetWindowTitle.Trim() + "\" window";
                Message?.Invoke(this, $"Coordinates adjusted to the {targetName}: {mapping}.");
            }

            CurrentSpeed = macro.Speed;
            var activeIds = new HashSet<Guid>();
            if (macro.Id != Guid.Empty) activeIds.Add(macro.Id);
            var current = new RunContext(steps, blocks, macro.Speed, held, new ScreenConditionEvaluator(_probes, rootVariables), totalIterations,
                span?.Start ?? 0, span?.End ?? steps.Count, rootVariables)
            {
                SourceIndexes = span?.SourceIndexes,
                SourceCount = span?.SourceIndexes is not null ? span.SourceCount : 0,
                Watchers = watchers,
                ActiveMacroIds = activeIds,
                RunStartedAt = ActiveTime,
            };
            rootVariables.Changed += (_, _) => NotifyVariablesChanged(current);
            if (macro.Variables is { Count: > 0 })
                NotifyVariablesChanged(current);

            run = current;
            _activeRun = current;

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
                    _lastActionSpot = null;
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
        catch (MacroStopRequestedSignal)
        {
            result = new PlaybackResult(PlaybackOutcome.Completed, iterationsCompleted, run?.StepsExecuted ?? 0);
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
            _activeRun = null;
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
        var suggestions = _sessionSuggestions.Count == 0
            ? Array.Empty<SelfHealSuggestion>()
            : (IReadOnlyList<SelfHealSuggestion>)_sessionSuggestions.Values.OrderBy(s => s.StepNumber).ToList();
        var finalVars = run?.Variables.Snapshot() ?? Array.Empty<VariableSnapshot>();
        result = result with
        {
            Stats = stats,
            Suggestions = suggestions,
            FinalVariables = finalVars,
            StepStats = run?.StepStats,
            FailureSnapshotPath = LastFailureSnapshotPath,
        };
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

    /// <summary>
    /// v1.8 Studio X Debugger: when paused, resumes for one step and automatically pauses before the next step.
    /// Returns true if playback was paused and stepped forward.
    /// </summary>
    public bool StepOver()
    {
        lock (_gate)
        {
            if (_state != PlaybackState.Paused) return false;
            _stepOverRequested = true;
        }

        return Resume();
    }

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

    /// <summary>A key the macro is holding. ScanCode 0 = "look it up from the virtual-key code".</summary>
    private readonly record struct HeldKey(int VirtualKey, int ScanCode = 0, bool Extended = false);

    /// <summary>Tracks what the macro is currently holding down so it can always be released.</summary>
    private sealed class HeldInput
    {
        public HashSet<HeldKey> Keys { get; } = new();
        public HashSet<MacroMouseButton> Buttons { get; } = new();

        public bool IsEmpty => Keys.Count == 0 && Buttons.Count == 0;

        public void PressKey(HeldKey key)
        {
            ReleaseKey(key);
            Keys.Add(key);
        }

        public void ReleaseKey(HeldKey key)
        {
            Keys.RemoveWhere(k => (key.VirtualKey != 0 && k.VirtualKey == key.VirtualKey)
                                  || (key.ScanCode != 0 && k.ScanCode == key.ScanCode && k.Extended == key.Extended));
        }

        public bool ContainsKey(int virtualKey) =>
            virtualKey != 0 && Keys.Any(k => k.VirtualKey == virtualKey);

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
