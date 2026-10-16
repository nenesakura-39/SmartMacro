using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Vision;

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
/// </summary>
public sealed record PlaybackProgress(int Iteration, int? TotalIterations, int StepNumber, int StepCount, MacroStep Step);

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
/// </summary>
public sealed class PlaybackEngine
{
    private static readonly TimeSpan MaxDelayChunk = TimeSpan.FromMilliseconds(50);

    /// <summary>If playback is more than this far behind schedule, it skips ahead instead of catching up.</summary>
    public static readonly TimeSpan MaxTimelineLag = TimeSpan.FromMilliseconds(250);

    /// <summary>Short pause after clicking found text / an image so the clicked window can take focus before the next step.</summary>
    public const int ClickSettleMs = 100;

    private readonly IInputSimulator _input;
    private readonly IPlaybackClock _clock;
    private readonly IOcrService? _ocr;
    private readonly IScreenCapture? _capture;
    private readonly object _gate = new();

    // Where the most recent Find text / image / color step found something, in screen pixels
    // (only one run at a time, so a field is fine). Mouse click steps can click it.
    private (int X, int Y)? _lastFound;

    // Per-run playback state. Only touched by the playback flow itself (never by Pause/Stop callers).
    private HeldInput? _held;
    private TimeSpan _timelineTarget;   // clock time at which the current step / event is due
    private TimeSpan _timelineShift;    // total time the timeline was pushed back (pauses, stalls)

    private PlaybackState _state = PlaybackState.Idle;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource? _resumeSignal;

    public PlaybackEngine(IInputSimulator input, IPlaybackClock? clock = null, IOcrService? ocr = null, IScreenCapture? capture = null)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _clock = clock ?? new SystemPlaybackClock();
        _ocr = ocr;
        _capture = capture;
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

    public async Task<PlaybackResult> RunAsync(Macro macro, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(macro);

        var issues = MacroValidator.ValidateForExecution(macro);
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
        }

        SetState(PlaybackState.Running);

        var held = new HeldInput();
        _held = held;
        var iterationsCompleted = 0;
        var stepsExecuted = 0;
        var token = cts.Token;

        // Keep the original positions so progress numbers match the editor.
        var plan = macro.Steps
            .Select((step, index) => (Step: step, Number: index + 1))
            .Where(x => x.Step.Enabled)
            .ToList();
        var speed = macro.Speed;
        var repeatDelayMs = macro.RepeatDelayMs;
        int? totalIterations = macro.RepeatCount == 0 ? null : macro.RepeatCount;

        PlaybackResult result;
        string? releaseError;
        try
        {
            await Task.Run(async () =>
            {
                ResetTimeline();
                _timelineShift = TimeSpan.Zero;

                for (var iteration = 1; totalIterations is null || iteration <= totalIterations; iteration++)
                {
                    // Pause between loops (never before the first run or after the last one).
                    if (iteration > 1 && repeatDelayMs > 0)
                    {
                        AdvanceTimeline(repeatDelayMs / speed);
                        await WaitForTimelineAsync(token).ConfigureAwait(false);
                    }

                    foreach (var (step, number) in plan)
                    {
                        await WaitIfPausedAsync(token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();

                        ProgressChanged?.Invoke(this, new PlaybackProgress(iteration, totalIterations, number, macro.Steps.Count, step));
                        await ExecuteStepAsync(step, speed, held, token).ConfigureAwait(false);
                        stepsExecuted++;
                    }

                    iterationsCompleted++;
                }
            }, token).ConfigureAwait(false);

            result = new PlaybackResult(PlaybackOutcome.Completed, iterationsCompleted, stepsExecuted);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            result = new PlaybackResult(PlaybackOutcome.Stopped, iterationsCompleted, stepsExecuted);
        }
        catch (Exception ex)
        {
            result = new PlaybackResult(PlaybackOutcome.Failed, iterationsCompleted, stepsExecuted, ex.Message);
        }
        finally
        {
            // Never leave keys or mouse buttons stuck down.
            releaseError = held.ReleaseAll(_input);
            _held = null;

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
        }
    }

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
    private async Task WaitAsync(double milliseconds, CancellationToken token)
    {
        if (milliseconds <= 0) return;

        var remaining = TimeSpan.FromMilliseconds(milliseconds);
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

    // ---------------- Steps ----------------

    private async Task ExecuteStepAsync(MacroStep step, double speed, HeldInput held, CancellationToken token)
    {
        switch (step)
        {
            case DelayStep d:
                AdvanceTimeline(d.DurationMs / speed);
                await WaitForTimelineAsync(token).ConfigureAwait(false);
                break;

            case MouseMoveStep m:
                _input.MoveMouse(m.X, m.Y);
                break;

            case MouseClickStep c:
                if (c.UseLastFoundText)
                {
                    var found = _lastFound ?? throw new TextNotFoundException(
                        "This click uses the last found spot, but no Find text / image / color step has found anything yet in this run.");
                    _input.MoveMouse(found.X, found.Y);
                }
                else if (!c.UseCurrentPosition)
                {
                    _input.MoveMouse(c.X, c.Y);
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
                        break;
                    case MouseClickAction.Click:
                        Click(c.Button);
                        break;
                    case MouseClickAction.DoubleClick:
                        Click(c.Button);
                        Click(c.Button);
                        break;
                    case MouseClickAction.Down:
                        _input.MouseButton(c.Button, true);
                        held.Buttons.Add(c.Button);
                        break;
                    case MouseClickAction.Up:
                        _input.MouseButton(c.Button, false);
                        held.Buttons.Remove(c.Button);
                        break;
                }
                break;

            case MouseScrollStep s:
                if (!s.UseCurrentPosition) _input.MoveMouse(s.X, s.Y);
                _input.Scroll(s.Delta, s.Horizontal);
                break;

            case KeyPressStep k:
                switch (k.Action)
                {
                    case KeyAction.Press when k.HoldMs > 0:
                    {
                        // Keep the key down for the recorded time, e.g. a 120 ms W tap in a game.
                        var modifiers = ModifierKeys(k.Modifiers);
                        foreach (var vk in modifiers)
                        {
                            _input.Key(vk, true);
                            held.Keys.Add(new HeldKey(vk));
                        }

                        _input.Key(k.VirtualKey, true);
                        held.Keys.Add(new HeldKey(k.VirtualKey));
                        AdvanceTimeline(k.HoldMs / speed);
                        await WaitForTimelineAsync(token).ConfigureAwait(false);
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

            case FindTextStep f:
                await FindTextAsync(f, token).ConfigureAwait(false);
                ResetTimeline(); // the search takes as long as it takes; start timing again from here
                break;

            case FindImageStep i:
                await FindImageAsync(i, token).ConfigureAwait(false);
                ResetTimeline();
                break;

            case FindColorStep c:
                await FindColorAsync(c, token).ConfigureAwait(false);
                ResetTimeline();
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
        if (step.MoveCursorToStart) _input.MoveMouse(step.StartX, step.StartY);

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
                                _input.MoveMouse(e.X, e.Y);
                                cursorSent = (e.X, e.Y);
                            }
                        }
                        else if (e.Dx != 0 || e.Dy != 0)
                        {
                            _input.MoveMouseRelative(e.Dx, e.Dy);
                            cursorSent = null; // the cursor (if visible) has moved by an unknown number of pixels
                        }

                        break;
                    case InputEventKind.MouseDown:
                        if (usePosition)
                        {
                            _input.MoveMouse(e.X, e.Y);
                            cursorSent = (e.X, e.Y);
                        }

                        _input.MouseButton(e.Button, true);
                        held.Buttons.Add(e.Button);
                        break;
                    case InputEventKind.MouseUp:
                        // Only if the cursor isn't already there (normally the last move put it there).
                        if (usePosition && cursorSent != (e.X, e.Y))
                        {
                            _input.MoveMouse(e.X, e.Y);
                            cursorSent = (e.X, e.Y);
                        }

                        _input.MouseButton(e.Button, false);
                        held.Buttons.Remove(e.Button);
                        break;
                    case InputEventKind.Wheel:
                    case InputEventKind.HorizontalWheel:
                        if (usePosition)
                        {
                            _input.MoveMouse(e.X, e.Y);
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

    // ---------------- Screen search (Find text / image / color) ----------------

    /// <summary>
    /// Result of one look at the screen. X / Y is the spot to act on (screen pixels). FoundText describes the
    /// find for the log; MissText (starting with a space) explains a miss, e.g. what OCR read instead.
    /// </summary>
    private readonly record struct Probe(bool Found, int X, int Y, string FoundText, string MissText)
    {
        public static Probe Miss(string missText) => new(false, 0, 0, string.Empty, missText);
    }

    /// <summary>
    /// The shared waiting loop: looks at the screen until the goal is met (it appeared / it's gone) or the
    /// timeout passes. Timeouts and check intervals are real time (not affected by the Speed setting), and time
    /// spent paused doesn't count. Pause / Stop / Esc are honoured between and during checks.
    /// </summary>
    private async Task SearchAsync(ScreenSearchStep step, string what, Func<CancellationToken, Task<Probe>> probe,
        DetectionTimer? timer, CancellationToken token)
    {
        var started = _clock.Elapsed;
        var shiftAtStart = _timelineShift;
        var timeout = TimeSpan.FromMilliseconds(step.TimeoutMs);
        TimeSpan Elapsed() => _clock.Elapsed - started - (_timelineShift - shiftAtStart);
        string Stats() => timer is null ? string.Empty : " " + timer.Describe(Elapsed());

        while (true)
        {
            await WaitIfPausedAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            var result = await probe(token).ConfigureAwait(false);

            if (step.Goal == WaitGoal.Appear && result.Found)
            {
                _lastFound = (result.X, result.Y);
                Message?.Invoke(this, result.FoundText + Stats());
                DoFoundAction(step, result.X, result.Y);
                if (step.Action is FoundTextAction.Click or FoundTextAction.DoubleClick or FoundTextAction.RightClick)
                    await WaitAsync(ClickSettleMs, token).ConfigureAwait(false);
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

                if (step.WhenNotFound == TextNotFoundBehavior.Continue)
                {
                    if (step.Goal == WaitGoal.Appear) _lastFound = null;
                    Message?.Invoke(this, message + " Continuing.");
                    return;
                }

                throw new DetectionTimeoutException(message);
            }

            await WaitAsync(step.CheckIntervalMs, token).ConfigureAwait(false);
        }
    }

    /// <summary>Reads the screen with OCR until the text shows up (or goes away) or the timeout passes.</summary>
    private Task FindTextAsync(FindTextStep step, CancellationToken token)
    {
        var ocr = _ocr;
        if (ocr is null || !ocr.IsAvailable)
            throw new OcrUnavailableException(ocr?.StatusText ?? "Text recognition (OCR) isn't available.");

        var region = step.SearchFullScreen ? ocr.GetVirtualScreen() : step.Region;
        return SearchAsync(step, $"\"{step.Text}\"", async t =>
        {
            var read = await ocr.ReadAsync(region, t).ConfigureAwait(false);
            var match = OcrTextMatcher.Find(read, step.Text, step.MatchMode, step.CaseSensitive, step.MinSimilarityPercent / 100.0)
                .FirstOrDefault();
            return match is null
                ? Probe.Miss(DescribeLastRead(read.FullText))
                : new Probe(true, match.Bounds.CenterX, match.Bounds.CenterY,
                    $"Found \"{match.Text}\" at ({match.Bounds.CenterX}, {match.Bounds.CenterY}), {match.Similarity:P0} match.", string.Empty);
        }, timer: null, token);
    }

    /// <summary>Captures the screen and looks for the step's picture until it shows up (or goes away).</summary>
    private Task FindImageAsync(FindImageStep step, CancellationToken token)
    {
        var capture = RequireCapture();
        if (!TemplateCodec.TryDecode(step.TemplateData, step.TemplateWidth, step.TemplateHeight, out var decoded, out var error))
            throw new InvalidOperationException($"The image of this step can't be used: {error}");
        var template = decoded!;

        var region = step.SearchFullScreen ? capture.GetVirtualScreen() : step.Region;
        var options = new TemplateMatchOptions { MinScore = step.MinSimilarityPercent / 100.0, IgnoreColor = step.IgnoreColor };
        var timer = new DetectionTimer();

        return SearchAsync(step, "The image", async t =>
        {
            var t0 = _clock.Elapsed;
            var frame = await capture.CaptureAsync(region, t).ConfigureAwait(false);
            var t1 = _clock.Elapsed;
            var result = TemplateMatcher.Find(frame.Image, template, options, t);
            timer.Add(t1 - t0, _clock.Elapsed - t1);

            if (frame.Image.Width < template.Width || frame.Image.Height < template.Height)
                return Probe.Miss(" The search area (on screen) is smaller than the image.");

            if (result.Best is not { } best)
            {
                var miss = $" The best similarity was {result.BestScore:P0} (needs {step.MinSimilarityPercent}%).";
                if (result.RejectedForColor > 0)
                    miss += $" {result.RejectedForColor} spot(s) had the right shape but a different color (turn on \"Ignore color\" if the color changes).";
                return Probe.Miss(miss);
            }

            var bounds = frame.ToScreen(best.Bounds);
            var more = result.Matches.Count > 1 ? $" ({result.Matches.Count} matches; the best one is used)" : "";
            return new Probe(true, bounds.CenterX, bounds.CenterY,
                $"Found the image at ({bounds.CenterX}, {bounds.CenterY}), {best.Score:P0} match{more}.", string.Empty);
        }, timer, token);
    }

    /// <summary>Captures the screen and looks for the step's colour until it shows up (or goes away).</summary>
    private Task FindColorAsync(FindColorStep step, CancellationToken token)
    {
        var capture = RequireCapture();
        if (!RgbColor.TryParse(step.Color, out var color))
            throw new InvalidOperationException($"\"{step.Color}\" is not a valid color. Use #RRGGBB.");

        var region = step.SearchFullScreen ? capture.GetVirtualScreen() : step.Region;
        var timer = new DetectionTimer();

        return SearchAsync(step, $"Color {color}", async t =>
        {
            var t0 = _clock.Elapsed;
            var frame = await capture.CaptureAsync(region, t).ConfigureAwait(false);
            var t1 = _clock.Elapsed;
            var result = ColorFinder.Find(frame.Image, color, step.Tolerance, step.MinPixels, maxResults: 20, t);
            timer.Add(t1 - t0, _clock.Elapsed - t1);

            if (result.Largest is not { } blob)
            {
                return Probe.Miss(result.MatchingPixels > 0
                    ? $" {result.MatchingPixels:N0} pixel(s) of that color were found, but no patch had at least {step.MinPixels} pixels."
                    : " No pixels of that color were found in the search area.");
            }

            var x = frame.Bounds.X + (int)Math.Round(blob.CenterX);
            var y = frame.Bounds.Y + (int)Math.Round(blob.CenterY);
            var more = result.Blobs.Count > 1 ? $", {result.Blobs.Count} patches; the biggest is used" : "";
            return new Probe(true, x, y, $"Found color {color} at ({x}, {y}): {blob.PixelCount:N0} pixels{more}.", string.Empty);
        }, timer, token);
    }

    private IScreenCapture RequireCapture() =>
        _capture ?? throw new ScreenCaptureUnavailableException("Screen capture isn't available, so Find image / Find color steps can't run.");

    /// <summary>Adds what OCR actually saw, which makes "not found" problems much easier to fix.</summary>
    private static string DescribeLastRead(string text)
    {
        var oneLine = string.Join("  |  ", text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        if (oneLine.Length == 0) return " No text could be read in the search area.";
        if (oneLine.Length > 160) oneLine = oneLine[..160] + "…";
        return $" Text read in the search area: \"{oneLine}\"";
    }

    private void DoFoundAction(ScreenSearchStep step, int x, int y)
    {
        if (step.Action == FoundTextAction.None) return;

        _input.MoveMouse(x + step.OffsetX, y + step.OffsetY);
        switch (step.Action)
        {
            case FoundTextAction.Click:
                Click(MacroMouseButton.Left);
                break;
            case FoundTextAction.DoubleClick:
                Click(MacroMouseButton.Left);
                Click(MacroMouseButton.Left);
                break;
            case FoundTextAction.RightClick:
                Click(MacroMouseButton.Right);
                break;
        }
    }

    private void Click(MacroMouseButton button)
    {
        _input.MouseButton(button, true);
        _input.MouseButton(button, false);
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
