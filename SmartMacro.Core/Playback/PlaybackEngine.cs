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

public sealed record PlaybackResult(PlaybackOutcome Outcome, int IterationsCompleted, int StepsExecuted, string? Error = null, string? Warning = null);

/// <summary>
/// Where playback currently is. StepNumber is the 1-based position in the macro (including disabled steps),
/// so it matches the numbers shown in the editor. TotalIterations is null when repeating until stopped.
/// LoopText describes the innermost Repeat block the step is in (e.g. "loop turn 2 of 5"), or null.
/// </summary>
public sealed record PlaybackProgress(int Iteration, int? TotalIterations, int StepNumber, int StepCount, MacroStep Step, string? LoopText = null);

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

    /// <summary>If playback is more than this far behind schedule, it skips ahead instead of catching up.</summary>
    public static readonly TimeSpan MaxTimelineLag = TimeSpan.FromMilliseconds(250);

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
    /// Smooth mouse movement: a cursor jump made by a Move mouse step (or a click / scroll at a position) that comes
    /// right after a Delay glides there during the end of that Delay (up to <see cref="MaxGlideMs"/>, one move every
    /// <see cref="GlideStepInterval"/>), instead of teleporting. Total timing doesn't change. The app turns this on
    /// from Settings; off by default (tests and callers that want exact jumps).
    /// </summary>
    public bool SmoothMouseMoves { get; set; }

    /// <summary>Longest glide before a cursor jump (ms of the preceding Delay that is used for gliding).</summary>
    public const int MaxGlideMs = 200;

    /// <summary>Time between cursor updates while gliding or dragging (~120 per second).</summary>
    public static readonly TimeSpan GlideStepInterval = TimeSpan.FromMilliseconds(8);

    // Where the engine last put the cursor with an absolute move (null = unknown, e.g. after relative / camera moves).
    private (int X, int Y)? _cursor;

    // Set just before a Delay step runs: the spot the next step jumps to (for the smooth glide), or null.
    private (int X, int Y)? _glideTarget;

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

    // Humanize (per run, from the macro): random click offset (px) and timing variation (%).
    private bool _humanize;
    private int _clickJitterPx;
    private int _delayJitterPercent;

    // Per-run playback state. Only touched by the playback flow itself (never by Pause/Stop callers).
    private HeldInput? _held;
    private TimeSpan _timelineTarget;   // clock time at which the current step / event is due
    private TimeSpan _timelineShift;    // total time the timeline was pushed back (pauses, stalls)
    private TimeSpan _pausedTotal;      // total time spent paused in this run (not counted by timeouts / loop limits)
    private bool _skipNextStep;         // set by a Find step that timed out with "skip the next step"

    private PlaybackState _state = PlaybackState.Idle;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource? _resumeSignal;

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
    private double Vary(double milliseconds)
    {
        if (!_humanize || _delayJitterPercent <= 0 || milliseconds <= 0) return milliseconds;
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
    /// "⏭ Play from here": plays from <paramref name="startStepIndex"/> to the end of the macro, once. A step inside a
    /// Repeat starts at that Repeat (so the loop works normally); a step inside an If starts right there.
    /// </summary>
    public Task<PlaybackResult> PlayFromStepAsync(Macro macro, int startStepIndex, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(macro);
        var start = PlaybackRanges.FromStep(macro.Steps, startStepIndex);
        return RunCoreAsync(macro, new PlaybackSpan(start, macro.Steps.Count, startStepIndex), cancellationToken);
    }

    /// <summary>The part of the step list a run plays: steps [Start, End). Null span = the whole macro with its repeats.</summary>
    private sealed record PlaybackSpan(int Start, int End, int RequestedIndex);

    private async Task<PlaybackResult> RunCoreAsync(Macro macro, PlaybackSpan? span, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(macro);

        var issues = span is null
            ? MacroValidator.ValidateForExecution(macro)
            : PlaybackRanges.ValidateRange(macro, span.Start, span.End);
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
            _skipNextStep = false;
            _cursor = null;
            _glideTarget = null;
        }

        SetState(PlaybackState.Running);

        // Humanize: small random click offsets and timing variation (off unless the macro turns it on).
        _humanize = macro.HumanizePlayback;
        _clickJitterPx = Math.Clamp(macro.EffectiveClickJitterPx, 0, Macro.MaxClickJitterPx);
        _delayJitterPercent = Math.Clamp(macro.EffectiveDelayJitterPercent, 0, Macro.MaxDelayJitterPercent);

        var held = new HeldInput();
        _held = held;
        _probes = new ScreenProbes(_ocr, _capture, _clock, _layout);
        var iterationsCompleted = 0;
        var token = cts.Token;

        // The validator above already refused unbalanced blocks, so this structure is always valid here.
        var steps = macro.Steps;
        var blocks = BlockStructure.Analyze(steps);
        int? totalIterations = span is not null ? 1 : macro.RepeatCount == 0 ? (int?)null : macro.RepeatCount;
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

            var current = new RunContext(steps, blocks, macro.Speed, held, new ScreenConditionEvaluator(_probes), totalIterations,
                span?.Start ?? 0, span?.End ?? steps.Count);
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
                        AdvanceTimeline(Vary(repeatDelayMs / current.Speed));
                        await WaitForTimelineAsync(token).ConfigureAwait(false);
                    }

                    current.Iteration = iteration;
                    _cursor = null;
                    _glideTarget = null;
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
            SetState(PlaybackState.Idle);
        }

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
        }

        StateChanged?.Invoke(this, PlaybackState.Paused);
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
        }

        StateChanged?.Invoke(this, PlaybackState.Running);
        return true;
    }

    /// <summary>Stops as soon as possible. Safe to call any time, from any thread.</summary>
    public bool Stop()
    {
        lock (_gate)
        {
            if (_state is not (PlaybackState.Running or PlaybackState.Paused)) return false;
            _state = PlaybackState.Stopping;
            _cts?.Cancel();
            _resumeSignal?.TrySetResult();
            _resumeSignal = null;
        }

        StateChanged?.Invoke(this, PlaybackState.Stopping);
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
        lock (_gate) wait = _resumeSignal?.Task;
        if (wait is null) return;

        var pausedAt = _clock.Elapsed;
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

    private void ResetTimeline() => _timelineTarget = _clock.Elapsed;

    private void AdvanceTimeline(double milliseconds)
    {
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
                if (-remaining > MaxTimelineLag)
                {
                    _timelineShift += now - _timelineTarget;
                    _timelineTarget = now;
                }

                return;
            }

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
        public double Speed { get; }
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
                    var answer = await EvaluateConditionAsync(run, ifStep.Condition, token).ConfigureAwait(false);
                    run.StepsExecuted++;
                    LogIfAnswer(run, pc, answer.IsTrue, answer.Text);

                    // Optional "when true" action on the spot the condition saw (not for "is NOT on screen").
                    if (answer.IsTrue && answer.Seen && ifStep.ThenAction != FoundTextAction.None)
                    {
                        await PerformFoundActionAsync(ifStep.ThenAction, answer.X + ifStep.ThenOffsetX, answer.Y + ifStep.ThenOffsetY,
                            ifStep.ThenHoldMs, FoundActionTiming.Default, run.Held, token, answer.Match).ConfigureAwait(false);
                        ResetTimeline();
                    }

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

                    break;
                }

                case FeedbackLoopStep feedback when feedback.Enabled:
                    Report(run, pc, loops);
                    await RunFeedbackLoopAsync(run, pc, loops, feedback, token).ConfigureAwait(false);
                    run.StepsExecuted++;
                    pc++;
                    break;

                default:
                    _skipNextStep = false;
                    if (step.Enabled)
                    {
                        // Smooth mouse: a Delay right before a cursor jump glides there (see SmoothMouseMoves).
                        _glideTarget = step is DelayStep && pc + 1 < steps.Count ? JumpTargetOf(steps[pc + 1]) : null;
                        Report(run, pc, loops);
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
        string? loopText = null;
        if (loops.Count > 0 && run.Steps[loops[^1].Start] is RepeatStep repeat)
        {
            var turn = loops[^1].Turns + 1;
            loopText = repeat.Mode == RepeatMode.Times ? $"loop turn {turn} of {repeat.Count}" : $"loop turn {turn}";
        }

        if (extra is not null) loopText = loopText is null ? extra : $"{loopText} · {extra}";

        ProgressChanged?.Invoke(this, new PlaybackProgress(run.Iteration, run.TotalIterations, index + 1, run.Steps.Count, run.Steps[index], loopText));
    }

    private void LogIfAnswer(RunContext run, int index, bool isTrue, string text)
    {
        // Inside loops the same If can run thousands of times: only log the first answer and changes.
        if (run.LastIfAnswer.TryGetValue(index, out var previous) && previous == isTrue) return;
        run.LastIfAnswer[index] = isTrue;
        Message?.Invoke(this, $"If (step {index + 1}): {(isTrue ? "yes" : "no")}. {text}");
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
                window: TimeSpan.FromMilliseconds(Math.Max(grace, condition.Search!.TimeoutMs))).ConfigureAwait(false)
            : await EvaluateConditionAsync(run, condition, token).ConfigureAwait(false);
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
        Func<ConditionCheck, bool>? stopWhen = null, TimeSpan? window = null)
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
            if (check.Seen)
            {
                _lastFound = (check.X, check.Y);
                _lastFoundBounds = check.Match;
            }

            if (stopWhen(check) || ActiveTime - started >= timeout) break;

            await WaitAsync(search.CheckIntervalMs, token).ConfigureAwait(false);
        }

        ResetTimeline(); // checking takes as long as it takes; start timing again from here
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
        var number = index + 1;
        var capture = _capture ?? throw new ScreenCaptureUnavailableException("Screen capture isn't available, so Feedback loop steps can't run.");
        var sensor = new FeedbackSensor(step, capture, _clock);
        var controller = new FeedbackController(FeedbackRules.From(step));
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

    private static bool IsConditionDue(Condition condition, TimeSpan? lastCheck, TimeSpan now) =>
        lastCheck is not { } last || now - last >= TimeSpan.FromMilliseconds(Math.Max(1, condition.Search?.CheckIntervalMs ?? 250));

    /// <summary>Looks once for a feedback loop's stop condition. A condition that sees its target also sets the "last found" spot.</summary>
    private async Task<ConditionCheck> CheckFeedbackConditionAsync(RunContext run, Condition condition, FeedbackStats stats, CancellationToken token)
    {
        var check = await run.Conditions.CheckAsync(condition, null, token).ConfigureAwait(false);
        stats.ConditionChecks++;
        if (check.Seen)
        {
            _lastFound = (check.X, check.Y);
            _lastFoundBounds = check.Match;
        }

        return check;
    }

    // ---------------- Steps ----------------

    private async Task ExecuteStepAsync(MacroStep step, double speed, HeldInput held, CancellationToken token)
    {
        switch (step)
        {
            case DelayStep d:
                if (SmoothMouseMoves && _glideTarget is { } target && d.DurationMs > 0)
                {
                    // Wait for the first part of the Delay, then glide to the next step's spot during the rest.
                    var total = Vary(d.DurationMs / speed);
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
                    AdvanceTimeline(Vary(d.DurationMs / speed));
                }

                await WaitForTimelineAsync(token).ConfigureAwait(false);
                break;

            case MouseMoveStep m:
                await MoveCursorAsync(m.X, m.Y, m.MoveStyle, m.MoveDurationMs, speed, onTimeline: true, token).ConfigureAwait(false);
                break;

            case MouseClickStep c:
                (int X, int Y)? start = null; // where the click happens, if known (needed for a smooth drag)
                // Humanize moves real clicks a few pixels (never a plain Down / Up, which may belong to a recorded drag).
                var jitters = c.Action is MouseClickAction.Click or MouseClickAction.DoubleClick or MouseClickAction.Hold;
                if (c.UseLastFoundText)
                {
                    var found = _lastFound ?? throw new TextNotFoundException(
                        "This click uses the last found spot, but no Find text / image / color step has found anything yet in this run.");
                    if (jitters) found = Jitter(found.X, found.Y, _lastFoundBounds);
                    await MoveCursorAsync(found.X, found.Y, c.MoveStyle, c.MoveDurationMs, speed, onTimeline: true, token).ConfigureAwait(false);
                    start = found;
                }
                else if (!c.UseCurrentPosition)
                {
                    (int X, int Y) clickAt = jitters ? Jitter(c.X, c.Y) : (c.X, c.Y);
                    await MoveCursorAsync(clickAt.X, clickAt.Y, c.MoveStyle, c.MoveDurationMs, speed, onTimeline: true, token).ConfigureAwait(false);
                    start = clickAt;
                }
                else if (_input.TryGetCursorPosition(out var cursorX, out var cursorY))
                {
                    start = (cursorX, cursorY);
                }

                switch (c.Action)
                {
                    case MouseClickAction.Click when c.HoldMs > 0:
                        // Keep the button down for the recorded time (tracked, so Stop still releases it).
                        _input.MouseButton(c.Button, true);
                        held.Buttons.Add(c.Button);
                        AdvanceTimeline(c.HoldMs / speed);
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
                        AdvanceTimeline(Math.Max(0, c.HoldMs) / speed);
                        await WaitForTimelineAsync(token).ConfigureAwait(false);
                        _input.MouseButton(c.Button, false);
                        held.Buttons.Remove(c.Button);
                        await WaitAfterClickAsync(c, speed, token).ConfigureAwait(false);
                        break;
                    case MouseClickAction.Drag:
                        await DragAsync(c, start, speed, held, token).ConfigureAwait(false);
                        break;
                }
                break;

            case MouseScrollStep s:
                if (!s.UseCurrentPosition) MoveTo(s.X, s.Y);
                await ScrollInNotchesAsync(s.Delta, s.Horizontal, token).ConfigureAwait(false);
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
                            AdvanceTimeline(k.HoldMs / speed);
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
                break;

            case TypeTextStep t:
                await TypeTextAsync(t, speed, token).ConfigureAwait(false);
                ResetTimeline(); // typing takes as long as it takes; start timing again from here
                break;

            case FindTextStep or FindImageStep or FindColorStep:
                await SearchAsync((ScreenSearchStep)step, token).ConfigureAwait(false);
                ResetTimeline(); // the search takes as long as it takes; start timing again from here
                break;

            case InputSequenceStep q:
                await PlaySequenceAsync(q, speed, held, token).ConfigureAwait(false);
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
        if (step.MoveCursorToStart) MoveTo(step.StartX, step.StartY);

        var segments = step.HasMouseSegments ? step.MouseSegments! : null;
        var segmentIndex = 0;
        var mode = segments is null ? MouseMovementMode.Relative : segments[0].Effective;
        (int X, int Y)? cursorSent = null; // last position a move put the cursor on (repeated moves are skipped)

        // If the previous steps left us far behind schedule, start this recording from "now".
        if (_clock.Elapsed - _timelineTarget > MaxTimelineLag) ResetTimeline();

        var stepStart = _timelineTarget;
        var shiftAtStart = _timelineShift;
        var keysBefore = held.Keys.ToHashSet();
        var buttonsBefore = held.Buttons.ToHashSet();

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
                        break;
                    case InputEventKind.MouseUp:
                        // Only if the cursor isn't already there (normally the last move put it there).
                        if (usePosition && cursorSent != (e.X, e.Y))
                        {
                            MoveTo(e.X, e.Y);
                            cursorSent = (e.X, e.Y);
                        }

                        _input.MouseButton(e.Button, false);
                        held.Buttons.Remove(e.Button);
                        break;
                    case InputEventKind.Wheel:
                    case InputEventKind.HorizontalWheel:
                        if (usePosition)
                        {
                            MoveTo(e.X, e.Y);
                            cursorSent = (e.X, e.Y);
                        }

                        _input.Scroll(e.Delta, e.Kind == InputEventKind.HorizontalWheel);
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

            if (step.Goal == WaitGoal.Appear && result.Found)
            {
                _lastFound = (result.X, result.Y);
                _lastFoundBounds = result.Match;
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
                    if (step.Goal == WaitGoal.Appear)
                    {
                        _lastFound = null;
                        _lastFoundBounds = default;
                    }

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
        switch (action)
        {
            case FoundTextAction.Click:
                await HeldClickAsync(MacroMouseButton.Left, timing.HoldMs, held, token).ConfigureAwait(false);
                break;
            case FoundTextAction.DoubleClick:
                await HeldClickAsync(MacroMouseButton.Left, timing.HoldMs, held, token).ConfigureAwait(false);
                await WaitAsync(Math.Max(0, timing.DoubleGapMs), token).ConfigureAwait(false);
                await HeldClickAsync(MacroMouseButton.Left, timing.HoldMs, held, token).ConfigureAwait(false);
                break;
            case FoundTextAction.RightClick:
                await HeldClickAsync(MacroMouseButton.Right, timing.HoldMs, held, token).ConfigureAwait(false);
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
        MouseMoveStep { Enabled: true, MoveStyle: null or MouseMoveStyle.Instant } m => (m.X, m.Y),
        MouseClickStep { Enabled: true, UseLastFoundText: false, UseCurrentPosition: false, MoveStyle: null or MouseMoveStyle.Instant } c => (c.X, c.Y),
        MouseScrollStep { Enabled: true, UseCurrentPosition: false } s => (s.X, s.Y),
        _ => null,
    };

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
        AdvanceTimeline(holdMs / speed);
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
        var plan = MouseMotion.Plan(from, (x, y), glide, duration, GlideStepInterval);
        if (onTimeline && _clock.Elapsed - _timelineTarget > MaxTimelineLag) ResetTimeline(); // far behind: glide from "now"

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
