using System.Windows.Threading;
using SmartMacro.Core.Logging;
using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Recording;
using SmartMacro.Core.Validation;
using SmartMacro.Mvvm;
using SmartMacro.Services;
using SmartMacro.Services.Input;

namespace SmartMacro.ViewModels;

public enum AutomationMode
{
    Idle,
    Recording,
    Playing,
    Paused,
    Stopping,
}

public sealed record PlayTargetInfo(string Name, int RepeatCount, double Speed);

/// <summary>Things the automation bar needs from the rest of the app.</summary>
public sealed class AutomationHooks
{
    /// <summary>The macro Play should run (a copy), or null if nothing is open/selected.</summary>
    public required Func<Macro?> GetPlayTarget { get; init; }

    /// <summary>Cheap summary of what Play would run (no copying), for the automation bar.</summary>
    public required Func<PlayTargetInfo?> GetPlayTargetInfo { get; init; }

    /// <summary>Asked before recording starts (e.g. save unsaved editor changes). False cancels.</summary>
    public required Func<Task<bool>> BeforeRecording { get; init; }

    /// <summary>Called with the converted steps after a recording finishes.</summary>
    public required Func<List<MacroStep>, Task> RecordingFinished { get; init; }

    public required Func<AppSettings> Settings { get; init; }

    public required Action<string, StatusKind> SetStatus { get; init; }
}

/// <summary>
/// Controls recording and playback: the Record / Play / Pause / Stop buttons, the global shortcuts,
/// the Esc emergency stop, and the progress shown while a macro runs.
/// Only one thing runs at a time.
/// </summary>
public sealed class AutomationViewModel : ObservableObject, IDisposable
{
    private readonly PlaybackEngine _engine;
    private readonly IInputRecorder _recorder;
    private readonly IRelativeInputRecorder _gameRecorder;
    private readonly ISmartInputRecorder _smartRecorder;
    private readonly EmergencyStopWatcher _emergency;
    private readonly IRunModeHost _host;
    private readonly IOcrService _ocr;
    private readonly ILogService _log;
    private readonly IDialogService _dialogs;
    private readonly Dispatcher _dispatcher;
    private readonly AutomationHooks _hooks;
    private readonly DispatcherTimer _progressTimer;

    private const int SettleBeforeRestoreMs = 400;

    private AutomationMode _mode = AutomationMode.Idle;
    private string _targetText = "Open or select a macro to play it.";
    private string _progressText = string.Empty;
    private double _progressValue;
    private bool _isProgressIndeterminate;
    private string _runningMacroName = string.Empty;
    private Task? _runTask;
    private volatile PlaybackProgress? _latestProgress;
    private DateTime _runStartedUtc;
    private RecordingMode _recordingMode = RecordingMode.Smart;

    public AutomationViewModel(
        PlaybackEngine engine,
        IInputRecorder recorder,
        IRelativeInputRecorder gameRecorder,
        ISmartInputRecorder smartRecorder,
        EmergencyStopWatcher emergency,
        IRunModeHost host,
        IOcrService ocr,
        ILogService log,
        IDialogService dialogs,
        Dispatcher dispatcher,
        AutomationHooks hooks)
    {
        _engine = engine;
        _recorder = recorder;
        _gameRecorder = gameRecorder;
        _smartRecorder = smartRecorder;
        _emergency = emergency;
        _host = host;
        _ocr = ocr;
        _log = log;
        _dialogs = dialogs;
        _dispatcher = dispatcher;
        _hooks = hooks;

        _engine.ProgressChanged += OnEngineProgress;
        _engine.StateChanged += OnEngineStateChanged;
        _engine.Message += OnEngineMessage;
        _emergency.EmergencyStopRequested += OnEmergencyStop;

        _progressTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
        _progressTimer.Tick += (_, _) => RefreshProgress();

        RecordCommand = new AsyncRelayCommand(StartRecordingAsync, HandleError, () => Mode == AutomationMode.Idle);
        StopRecordingCommand = new AsyncRelayCommand(StopRecordingAsync, HandleError, () => Mode == AutomationMode.Recording);
        PlayCommand = new AsyncRelayCommand(PlayAsync, HandleError, () => Mode == AutomationMode.Idle);
        PauseResumeCommand = new RelayCommand(TogglePause, () => Mode is AutomationMode.Playing or AutomationMode.Paused);
        StopCommand = new RelayCommand(StopPlayback, () => Mode is AutomationMode.Playing or AutomationMode.Paused);
    }

    public AsyncRelayCommand RecordCommand { get; }
    public AsyncRelayCommand StopRecordingCommand { get; }
    public AsyncRelayCommand PlayCommand { get; }
    public RelayCommand PauseResumeCommand { get; }
    public RelayCommand StopCommand { get; }

    // Shortcuts come from Settings (they can be customized).
    public string RecordShortcut => _hooks.Settings().RecordHotkey.ToString();
    public string PlayShortcut => _hooks.Settings().PlayHotkey.ToString();
    public string PauseShortcut => _hooks.Settings().PauseHotkey.ToString();

    public string RecordTooltip => $"Record your mouse and keyboard into a new macro ({RecordShortcut})";
    public string StopRecordingTooltip => $"Finish recording ({RecordShortcut})";
    public string PlayTooltip => $"Play the macro open in the editor, or the one selected in the library ({PlayShortcut})";
    public string PauseTooltip => $"Pause or resume ({PauseShortcut})";
    public string StopTooltip => $"Stop playback (Esc or {PlayShortcut})";

    public string EmergencyHint => $"Safety: press Esc to stop playback. {RecordShortcut} records, {PlayShortcut} plays.";

    /// <summary>Call after the shortcut settings change so labels and tooltips update.</summary>
    public void RefreshShortcuts()
    {
        foreach (var name in new[] { nameof(RecordShortcut), nameof(PlayShortcut), nameof(PauseShortcut), nameof(RecordTooltip),
                     nameof(StopRecordingTooltip), nameof(PlayTooltip), nameof(PauseTooltip), nameof(StopTooltip), nameof(EmergencyHint) })
            OnPropertyChanged(name);
    }

    public AutomationMode Mode
    {
        get => _mode;
        private set
        {
            if (!SetProperty(ref _mode, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(IsRecording));
            OnPropertyChanged(nameof(IsPlaying));
            OnPropertyChanged(nameof(IsPaused));
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(PauseButtonText));
        }
    }

    public bool IsIdle => _mode == AutomationMode.Idle;
    public bool IsRecording => _mode == AutomationMode.Recording;
    public bool IsPlaying => _mode is AutomationMode.Playing or AutomationMode.Paused or AutomationMode.Stopping;
    public bool IsPaused => _mode == AutomationMode.Paused;
    public bool IsBusy => _mode != AutomationMode.Idle;

    public string StateText => _mode switch
    {
        AutomationMode.Recording => "Recording",
        AutomationMode.Playing => "Playing",
        AutomationMode.Paused => "Paused",
        AutomationMode.Stopping => "Stopping…",
        _ => "Ready",
    };

    public string PauseButtonText => _mode == AutomationMode.Paused ? "Resume" : "Pause";

    /// <summary>Which macro Play will run (when idle).</summary>
    public string TargetText
    {
        get => _targetText;
        private set => SetProperty(ref _targetText, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    /// <summary>0..1 through the current run (or current loop when repeating forever).</summary>
    public double ProgressValue
    {
        get => _progressValue;
        private set => SetProperty(ref _progressValue, value);
    }

    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set => SetProperty(ref _isProgressIndeterminate, value);
    }

    /// <summary>Called by the main view model when the open/selected macro changes.</summary>
    public void RefreshTarget()
    {
        if (!IsIdle) return;
        var target = _hooks.GetPlayTargetInfo();
        TargetText = target is null
            ? "Open or select a macro to play it."
            : $"Play: {(string.IsNullOrWhiteSpace(target.Name) ? "Untitled" : target.Name.Trim())}  ·  {DescribeRepeat(target.RepeatCount, target.Speed)}";
    }

    public void HandleHotkey(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.ToggleRecording:
                if (IsRecording) StopRecordingCommand.Execute(null);
                else if (IsIdle) RecordCommand.Execute(null);
                break;
            case HotkeyAction.TogglePlayback:
                if (IsPlaying) StopPlayback();
                else if (IsIdle) PlayCommand.Execute(null);
                break;
            case HotkeyAction.TogglePause:
                TogglePause();
                break;
        }
    }

    /// <summary>Used when the app closes: stops whatever is running and waits for cleanup (releasing keys).</summary>
    public async Task StopAllAsync()
    {
        if (IsRecording)
        {
            switch (_recordingMode)
            {
                case RecordingMode.Game: _gameRecorder.Stop(); break;
                case RecordingMode.Smart: _smartRecorder.Stop(); break;
                default: _recorder.Stop(); break;
            }

            _host.Exit();
            Mode = AutomationMode.Idle;
            _log.Warning("Recording was discarded because SmartMacro is closing.");
        }

        if (IsPlaying)
        {
            _engine.Stop();
            if (_runTask is not null) await _runTask;
        }
    }

    public void Dispose()
    {
        _progressTimer.Stop();
        _engine.ProgressChanged -= OnEngineProgress;
        _engine.StateChanged -= OnEngineStateChanged;
        _engine.Message -= OnEngineMessage;
        _emergency.EmergencyStopRequested -= OnEmergencyStop;
        _emergency.Dispose();
        if (_recorder is IDisposable disposable) disposable.Dispose();
        if (_gameRecorder is IDisposable gameDisposable) gameDisposable.Dispose();
        if (_smartRecorder is IDisposable smartDisposable) smartDisposable.Dispose();
    }

    // ---------------- Recording ----------------

    private async Task StartRecordingAsync()
    {
        if (!IsIdle) return;
        if (!await _hooks.BeforeRecording()) return;

        _recordingMode = _hooks.Settings().RecordingMode;
        Mode = AutomationMode.Recording;
        _host.Enter(OverlayKind.Recording, _recordingMode switch
        {
            RecordingMode.Game => $"Recording (game mode)…   {RecordShortcut} to stop",
            RecordingMode.Desktop => $"Recording (desktop mode)…   {RecordShortcut} to stop",
            _ => $"Recording…   {RecordShortcut} to stop",
        });

        // Give the window a moment to minimize so that isn't recorded.
        await Task.Delay(300);
        if (!IsRecording) return;

        try
        {
            switch (_recordingMode)
            {
                case RecordingMode.Game: _gameRecorder.Start(); break;
                case RecordingMode.Smart: _smartRecorder.Start(); break;
                default: _recorder.Start(); break;
            }
        }
        catch (Exception ex)
        {
            Mode = AutomationMode.Idle;
            _host.Exit();
            _log.Error("Recording could not start.", ex);
            _dialogs.ShowError("Recording failed", $"Windows didn't allow SmartMacro to watch the mouse and keyboard.\n\n{ex.Message}");
            return;
        }

        _runStartedUtc = DateTime.UtcNow;
        _progressTimer.Start();
        _hooks.SetStatus(_recordingMode switch
        {
            RecordingMode.Game => $"Recording in game mode (relative mouse + exact key timing)… press {RecordShortcut} to finish.",
            RecordingMode.Desktop => $"Recording in desktop mode… press {RecordShortcut} (or the Stop button) to finish.",
            _ => $"Recording… press {RecordShortcut} to finish.",
        }, StatusKind.Info);
    }

    private async Task StopRecordingAsync()
    {
        if (!IsRecording) return;

        if (_recordingMode == RecordingMode.Game)
        {
            await StopGameRecordingAsync();
            return;
        }

        if (_recordingMode == RecordingMode.Smart)
        {
            await StopSmartRecordingAsync();
            return;
        }

        var raw = _recorder.Stop();
        var limitReached = _recorder.LimitReached;
        _progressTimer.Stop();
        _host.Exit();
        Mode = AutomationMode.Idle;
        ProgressText = string.Empty;

        var options = new RecordingOptions
        {
            RecordMouseMoves = _hooks.Settings().RecordMouseMovement,
            KeepHoldTime = _hooks.Settings().KeepTapHoldTime,
            TrimTrailingKeys = _hooks.Settings().RecordHotkey.AllKeys(),
            SimplifyMousePath = true, // a few meaningful Move steps instead of one every 30 ms
            CollapseMousePaths = _hooks.Settings().SimplifyMousePaths, // clicks / drags only, no hover paths
        };
        var steps = RecordingConverter.Convert(raw, options);

        if (limitReached)
            _log.Warning($"The recording hit the limit of {WindowsInputRecorder.MaxEvents:N0} events; input after that was not recorded.");

        if (steps.Count == 0)
        {
            _hooks.SetStatus("Nothing was recorded.", StatusKind.Warning);
            RefreshTarget();
            return;
        }

        _log.Info($"Recorded {raw.Count:N0} input events → {steps.Count:N0} steps.");
        await _hooks.RecordingFinished(steps);
        RefreshTarget();
    }

    /// <summary>Game mode: the whole recording becomes one "Recorded input (game mode)" step.</summary>
    private async Task StopGameRecordingAsync()
    {
        var raw = _gameRecorder.Stop();
        var limitReached = _gameRecorder.LimitReached;
        _progressTimer.Stop();
        _host.Exit();
        Mode = AutomationMode.Idle;
        ProgressText = string.Empty;

        var step = GameRecordingConverter.Convert(raw, new GameRecordingOptions
        {
            TrimTrailingKeys = _hooks.Settings().RecordHotkey.AllKeys(),
            StartX = _gameRecorder.StartX,
            StartY = _gameRecorder.StartY,
        });

        if (limitReached)
            _log.Warning($"The recording hit the limit of {GameRecordingConverter.MaxRecordedEvents:N0} events; input after that was not recorded.");
        if (_gameRecorder is RawInputRecorder { LastHandlerError: { } handlerError })
            _log.Warning("Some raw input couldn't be read during recording; the recording may be incomplete.", handlerError);

        if (step is null)
        {
            _hooks.SetStatus("Nothing was recorded. (In game mode, input made while SmartMacro is in front isn't recorded.)", StatusKind.Warning);
            RefreshTarget();
            return;
        }

        var summary = StepDescriber.Summarize(step);
        _log.Info($"Recorded {raw.Count:N0} raw input events in game mode → {summary.EventCount:N0} events over {summary.DurationMs / 1000.0:0.0} s " +
                  $"({summary.MouseMoves:N0} mouse moves, {summary.KeyPresses:N0} key presses).");
        await _hooks.RecordingFinished(new List<MacroStep> { step });
        RefreshTarget();
    }

    /// <summary>
    /// Smart recording: cursor parts become editable steps, camera parts become "Recorded input" steps, on one timeline.
    /// </summary>
    private async Task StopSmartRecordingAsync()
    {
        var raw = _smartRecorder.Stop();
        var limitReached = _smartRecorder.LimitReached;
        _progressTimer.Stop();
        _host.Exit();
        Mode = AutomationMode.Idle;
        ProgressText = string.Empty;

        var settings = _hooks.Settings();
        var options = new SmartRecordingOptions
        {
            TrimTrailingKeys = settings.RecordHotkey.AllKeys(),
            RecordMouseMoves = settings.RecordMouseMovement,
            KeepHoldTime = settings.KeepTapHoldTime,
            CollapseMousePaths = settings.SimplifyMousePaths,
            StartX = _smartRecorder.StartX,
            StartY = _smartRecorder.StartY,
        };

        // Long recordings can have hundreds of thousands of events; keep the window responsive while converting.
        var result = await Task.Run(() => SmartRecordingConverter.Convert(raw, options));

        if (limitReached)
            _log.Warning($"The recording hit the limit of {SmartRecordingConverter.MaxRecordedEvents:N0} events; input after that was not recorded.");
        if (_smartRecorder is SmartInputRecorder { LastHandlerError: { } handlerError })
            _log.Warning("Some input couldn't be read during recording; the recording may be incomplete.", handlerError);

        if (result.Steps.Count == 0)
        {
            _hooks.SetStatus("Nothing was recorded. (Input made while SmartMacro is in front isn't recorded.)", StatusKind.Warning);
            RefreshTarget();
            return;
        }

        _log.Info($"Smart recording: {raw.Count:N0} input events over {result.DurationMs / 1000.0:0.0} s → {result.Steps.Count:N0} steps " +
                  $"({result.EditableParts} editable part(s), {result.RecordedInputParts} recorded-input part(s); " +
                  $"mouse: {result.CursorSegments} cursor-position part(s), {result.RelativeSegments} camera / relative part(s)).");
        if (result.RelativeSegments > 0)
            _log.Info("Camera / relative parts were detected because the cursor was hidden or locked while the mouse moved. " +
                      "If a part was detected wrongly, open the \"Recorded input\" step and change its mouse playback.");

        await _hooks.RecordingFinished(result.Steps);
        RefreshTarget();
    }

    // ---------------- Playback ----------------

    private Task PlayAsync() => PlayMacroAsync(_hooks.GetPlayTarget());

    /// <summary>Plays a specific macro (e.g. from the library's Play button). Ignored if something is already running.</summary>
    public async Task PlayMacroAsync(Macro? macro)
    {
        if (!IsIdle) return;

        if (macro is null)
        {
            _hooks.SetStatus("Open a macro in the editor, or select one in the library, then press Play.", StatusKind.Warning);
            return;
        }

        var issues = MacroValidator.ValidateForExecution(macro);
        if (issues.HasErrors())
        {
            var list = string.Join("\n", issues.Where(i => i.Severity == IssueSeverity.Error).Take(8).Select(i => "• " + i.DisplayText));
            _dialogs.ShowWarning("Can't play this macro", $"Fix these problems first:\n\n{list}");
            _hooks.SetStatus($"\"{macro.Name}\" can't play until its problems are fixed.", StatusKind.Error);
            return;
        }

        if (!_ocr.IsAvailable && WorkflowSteps.UsesTextRecognition(macro))
        {
            _dialogs.ShowWarning("Text recognition isn't available", $"This macro has \"Find text\" steps or text conditions, but:\n\n{_ocr.StatusText}");
            _hooks.SetStatus("Can't play: text recognition (OCR) isn't available on this PC.", StatusKind.Error);
            return;
        }

        _runningMacroName = macro.Name;
        _latestProgress = null;
        Mode = AutomationMode.Playing;
        IsProgressIndeterminate = macro.RepeatCount == 0;
        ProgressValue = 0;
        ProgressText = "Starting…";
        _host.Enter(OverlayKind.Playing, OverlayText());

        try
        {
            _emergency.Start();
        }
        catch (Exception ex)
        {
            // Not fatal: the Stop button and Ctrl+Shift+F10 still work.
            _log.Warning("The Esc emergency stop could not be enabled; use Ctrl+Shift+F10 or the Stop button.", ex);
        }

        var run = RunPlaybackAsync(macro);
        _runTask = run;
        await run;
    }

    private async Task RunPlaybackAsync(Macro macro)
    {
        PlaybackResult? result = null;
        try
        {
            // Let the window minimize first so clicks don't land on SmartMacro itself.
            await Task.Delay(400);
            if (Mode == AutomationMode.Stopping)
            {
                result = new PlaybackResult(PlaybackOutcome.Stopped, 0, 0);
                return;
            }

            _runStartedUtc = DateTime.UtcNow;
            _progressTimer.Start();
            _engine.SmoothMouseMoves = _hooks.Settings().SmoothMouseMoves;
            using (new HighResolutionTimerScope())
            {
                result = await _engine.RunAsync(macro);
            }

            // Simulated keys are queued; the target app may still be processing them. If SmartMacro
            // restored itself right away it would grab the keyboard and swallow the rest (e.g. "Sma" instead
            // of "SmartMacro"). Give the app a moment to catch up first.
            await Task.Delay(SettleBeforeRestoreMs);
        }
        catch (Exception ex)
        {
            result = new PlaybackResult(PlaybackOutcome.Failed, 0, 0, ex.Message);
        }
        finally
        {
            _progressTimer.Stop();
            _emergency.Stop();
            _host.Exit();
            Mode = AutomationMode.Idle;
            ProgressValue = 0;
            ProgressText = string.Empty;
            IsProgressIndeterminate = false;
            _runTask = null;
            ReportResult(result);
            RefreshTarget();
        }
    }

    private void ReportResult(PlaybackResult? result)
    {
        if (result is null) return;
        var elapsed = DateTime.UtcNow - _runStartedUtc;
        var name = _runningMacroName;

        switch (result.Outcome)
        {
            case PlaybackOutcome.Completed:
                _hooks.SetStatus($"Finished \"{name}\" ({result.IterationsCompleted} run{(result.IterationsCompleted == 1 ? "" : "s")}, {elapsed.TotalSeconds:0.0} s).", StatusKind.Success);
                break;
            case PlaybackOutcome.Stopped:
                _hooks.SetStatus($"Stopped \"{name}\" after {result.StepsExecuted:N0} step(s).", StatusKind.Warning);
                break;
            case PlaybackOutcome.Failed:
                _hooks.SetStatus($"\"{name}\" stopped because of an error: {result.Error}", StatusKind.Error);
                _dialogs.ShowError("Playback stopped", $"\"{name}\" stopped because of an error:\n\n{result.Error}");
                break;
        }

        if (result.Warning is not null) _log.Warning(result.Warning);
    }

    private void TogglePause()
    {
        if (Mode == AutomationMode.Playing)
        {
            if (_engine.Pause()) _hooks.SetStatus($"Paused. Press {PauseShortcut} or Resume to continue.", StatusKind.Info);
        }
        else if (Mode == AutomationMode.Paused)
        {
            _engine.Resume();
        }
    }

    private void StopPlayback()
    {
        if (!IsPlaying) return;
        if (Mode != AutomationMode.Stopping) Mode = AutomationMode.Stopping;
        _engine.Stop();
    }

    private void OnEmergencyStop(object? sender, EventArgs e)
    {
        if (!IsPlaying) return;
        _log.Warning("Emergency stop (Esc) pressed.");
        StopPlayback();
    }

    // Engine events arrive on the playback thread (the log service is thread-safe).
    private void OnEngineMessage(object? sender, string message) => _log.Info(message);

    private void OnEngineProgress(object? sender, PlaybackProgress progress) => _latestProgress = progress;

    private void OnEngineStateChanged(object? sender, PlaybackState state)
    {
        _dispatcher.InvokeAsync(() =>
        {
            if (Mode is AutomationMode.Idle or AutomationMode.Recording) return;
            switch (state)
            {
                case PlaybackState.Running:
                    Mode = AutomationMode.Playing;
                    break;
                case PlaybackState.Paused:
                    Mode = AutomationMode.Paused;
                    break;
                case PlaybackState.Stopping:
                    Mode = AutomationMode.Stopping;
                    break;
            }

            RefreshProgress();
        });
    }

    private void RefreshProgress()
    {
        if (IsRecording)
        {
            var seconds = (DateTime.UtcNow - _runStartedUtc).TotalSeconds;
            var count = _recordingMode switch
            {
                RecordingMode.Game => _gameRecorder.EventCount,
                RecordingMode.Smart => _smartRecorder.EventCount,
                _ => _recorder.EventCount,
            };
            ProgressText = $"{count:N0} input events · {seconds:0} s";
            return;
        }

        if (!IsPlaying) return;

        if (_latestProgress is { } p && p.StepCount > 0)
        {
            var run = p.TotalIterations is int total ? $"run {p.Iteration} of {total}" : $"run {p.Iteration} (until stopped)";
            ProgressText = p.LoopText is null
                ? $"Step {p.StepNumber} of {p.StepCount} · {run}"
                : $"Step {p.StepNumber} of {p.StepCount} · {p.LoopText} · {run}";
            ProgressValue = p.TotalIterations is int t && t > 0
                ? ((p.Iteration - 1) + (double)p.StepNumber / p.StepCount) / t
                : (double)p.StepNumber / p.StepCount;
        }

        _host.Update(Mode == AutomationMode.Paused ? OverlayKind.Paused : OverlayKind.Playing, OverlayText());
    }

    private string OverlayText()
    {
        var state = Mode == AutomationMode.Paused ? "Paused" : "Playing";
        var progress = string.IsNullOrEmpty(ProgressText) ? "" : $"  ·  {ProgressText}";
        return $"{state} \"{_runningMacroName}\"{progress}   —   Esc to stop";
    }

    private static string DescribeRepeat(int repeatCount, double speed)
    {
        var repeat = repeatCount switch
        {
            0 => "repeats until stopped",
            1 => "runs once",
            var n => $"runs {n:N0} times",
        };
        return speed == 1.0 ? repeat : $"{repeat} at {speed:0.##}× speed";
    }

    private void HandleError(Exception ex)
    {
        _log.Error("Unexpected automation error.", ex);
        _dialogs.ShowError("Unexpected error", ex.Message);
    }
}
