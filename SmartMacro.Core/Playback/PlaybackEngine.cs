using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Validation;

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

    /// <summary>Short pause after clicking found text so the clicked window can take focus before the next step.</summary>
    public const int ClickSettleMs = 100;

    private readonly IInputSimulator _input;
    private readonly IPlaybackClock _clock;
    private readonly IOcrService? _ocr;
    private readonly object _gate = new();

    // Where the most recent "Find text" step found its text (only one run at a time, so a field is fine).
    private TextMatch? _lastFound;

    // Per-run playback state. Only touched by the playback flow itself (never by Pause/Stop callers).
    private HeldInput? _held;
    private TimeSpan _timelineTarget;   // clock time at which the current step / event is due
    private TimeSpan _timelineShift;    // total time the timeline was pushed back (pauses, stalls)

    private PlaybackState _state = PlaybackState.Idle;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource? _resumeSignal;

    public PlaybackEngine(IInputSimulator input, IPlaybackClock? clock = null, IOcrService? ocr = null)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _clock = clock ?? new SystemPlaybackClock();
        _ocr = ocr;
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
                        "This click uses the last found text, but no text has been found yet in this run.");
                    _input.MoveMouse(found.Bounds.CenterX, found.Bounds.CenterY);
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

            case InputSequenceStep q:
                await PlaySequenceAsync(q, speed, held, token).ConfigureAwait(false);
                break;

            default:
                throw new NotSupportedException($"Step type {step.GetType().Name} can't be played.");
        }
    }

    /// <summary>
    /// Plays a game-mode recording: every event is sent at (step start + its recorded time ÷ speed), in the
    /// recorded order. Mouse movement is relative. Anything the step pressed and didn't release is released
    /// at the end of the step.
    /// </summary>
    private async Task PlaySequenceAsync(InputSequenceStep step, double speed, HeldInput held, CancellationToken token)
    {
        var events = step.Events;
        if (step.MoveCursorToStart) _input.MoveMouse(step.StartX, step.StartY);

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
                await WaitForTimelineAsync(token).ConfigureAwait(false);

                switch (e.Kind)
                {
                    case InputEventKind.MouseMove:
                        if (e.Dx != 0 || e.Dy != 0) _input.MoveMouseRelative(e.Dx, e.Dy);
                        break;
                    case InputEventKind.MouseDown:
                        _input.MouseButton(e.Button, true);
                        held.Buttons.Add(e.Button);
                        break;
                    case InputEventKind.MouseUp:
                        _input.MouseButton(e.Button, false);
                        held.Buttons.Remove(e.Button);
                        break;
                    case InputEventKind.Wheel:
                    case InputEventKind.HorizontalWheel:
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

    /// <summary>
    /// Reads the screen with OCR until the text shows up or the timeout passes, then does the step's action.
    /// Timeouts and check intervals are real time (not affected by the Speed setting).
    /// </summary>
    private async Task FindTextAsync(FindTextStep step, CancellationToken token)
    {
        if (_ocr is null || !_ocr.IsAvailable)
            throw new OcrUnavailableException(_ocr?.StatusText ?? "Text recognition (OCR) isn't available.");

        var region = step.SearchFullScreen
            ? _ocr.GetVirtualScreen()
            : new ScreenRect(step.RegionX, step.RegionY, step.RegionWidth, step.RegionHeight);
        var started = _clock.Elapsed;
        var timeout = TimeSpan.FromMilliseconds(step.TimeoutMs);
        var lastText = string.Empty;

        while (true)
        {
            await WaitIfPausedAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            var read = await _ocr.ReadAsync(region, token).ConfigureAwait(false);
            lastText = read.FullText;
            var match = OcrTextMatcher.Find(read, step.Text, step.MatchMode, step.CaseSensitive, step.MinSimilarityPercent / 100.0)
                .FirstOrDefault();

            if (match is not null)
            {
                _lastFound = match;
                Message?.Invoke(this, $"Found \"{match.Text}\" at ({match.Bounds.CenterX}, {match.Bounds.CenterY}), {match.Similarity:P0} match.");
                DoFoundTextAction(step, match);
                if (step.Action is FoundTextAction.Click or FoundTextAction.DoubleClick or FoundTextAction.RightClick)
                    await WaitAsync(ClickSettleMs, token).ConfigureAwait(false);
                return;
            }

            if (_clock.Elapsed - started >= timeout)
            {
                var seconds = timeout.TotalSeconds;
                var message = step.TimeoutMs == 0
                    ? $"\"{step.Text}\" wasn't found on screen."
                    : $"\"{step.Text}\" didn't appear within {seconds:0.#} s.";
                message += DescribeLastRead(lastText);

                if (step.WhenNotFound == TextNotFoundBehavior.Continue)
                {
                    _lastFound = null;
                    Message?.Invoke(this, message + " Continuing.");
                    return;
                }

                throw new TextNotFoundException(message);
            }

            await WaitAsync(step.CheckIntervalMs, token).ConfigureAwait(false);
        }
    }

    /// <summary>Adds what OCR actually saw, which makes "not found" problems much easier to fix.</summary>
    private static string DescribeLastRead(string text)
    {
        var oneLine = string.Join("  |  ", text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        if (oneLine.Length == 0) return " No text could be read in the search area.";
        if (oneLine.Length > 160) oneLine = oneLine[..160] + "…";
        return $" Text read in the search area: \"{oneLine}\"";
    }

    private void DoFoundTextAction(FindTextStep step, TextMatch match)
    {
        if (step.Action == FoundTextAction.None) return;

        _input.MoveMouse(match.Bounds.CenterX + step.OffsetX, match.Bounds.CenterY + step.OffsetY);
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
