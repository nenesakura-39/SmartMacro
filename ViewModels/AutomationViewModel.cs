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

    public Action<RecordingMode>? SetRecordingMode { get; init; }

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
    private Guid? _runningStepId;

    // Mini-HUD timer: time spent paused doesn't count; and what the recording HUD says.
    private DateTime? _pausedSinceUtc;
    private TimeSpan _pausedTotal;
    private string _recordingHudText = string.Empty;
    private Guid? _runningMacroId;

    // Run counter: written by the engine's RunStarted event (playback thread), shown by the UI timer.
    private volatile int _currentRun;
    private volatile int _configuredRuns;
    private string _runCounterText = string.Empty;
    private string _stepCounterText = string.Empty;
    private string _visionTelemetryText = string.Empty;

    /// <summary>
    /// Playback UI refresh rate: engine events (step started, run started) only store the latest numbers on the playback
    /// thread; the UI shows them at most this often (~30 FPS), so a fast loop running hundreds of steps per second never
    /// floods the UI thread. Pause, resume, stop and the end of a run are shown at once.
    /// </summary>
    public static readonly TimeSpan UiRefreshInterval = TimeSpan.FromMilliseconds(33);

    // Bumped (playback thread) whenever there is something new to show; a UI tick with nothing new does no work.
    private int _progressVersion;

    // Ghost-highlight guard: bumped on every Play and every Stop. A throttled UI update (timer tick, state change, target
    // lock) only draws when it still belongs to the session that is playing NOW, so a callback queued just before Stop
    // or the end of playback can never re-highlight a step, flip the mode back or overwrite the final status.
    private long _playbackSessionId;
    private long _activeSessionId = -1;
    private int _renderedVersion = -1;
    private long _renderedSecond = -1;
    private AutomationMode _renderedMode = AutomationMode.Idle;
    private double _renderedProbeMs = -1;

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
        _engine.RunStarted += OnEngineRunStarted;
        _engine.TargetDetected += OnTargetDetected;
        _engine.TargetOverlayClearRequested += OnTargetOverlayClearRequested;
        _emergency.EmergencyStopRequested += OnEmergencyStop;
        _host.PauseRequested += OnHudPauseRequested;
        _host.StopRequested += OnHudStopRequested;

        _progressTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = UiRefreshInterval };
        _progressTimer.Tick += (_, _) => RefreshProgress();

        RecordCommand = new AsyncRelayCommand(StartRecordingAsync, HandleError, () => Mode == AutomationMode.Idle);
        StopRecordingCommand = new AsyncRelayCommand(StopRecordingAsync, HandleError, () => Mode == AutomationMode.Recording);
        PlayCommand = new AsyncRelayCommand(PlayAsync, HandleError, () => Mode == AutomationMode.Idle);
        PauseResumeCommand = new RelayCommand(TogglePause, () => Mode is AutomationMode.Playing or AutomationMode.Paused);
        StopCommand = new RelayCommand(StopPlayback, () => Mode is AutomationMode.Playing or AutomationMode.Paused);
        DismissRunSummaryCommand = new RelayCommand(() => LastRunSummaryText = string.Empty, () => HasLastRunSummary);
    }

    // ---------------- Post-run telemetry pill ----------------

    private string _lastRunSummaryText = string.Empty;

    /// <summary>
    /// What the last playback did, e.g. "✓ 3 runs in 8.4s · 24 steps · 6 vision locks (avg 0.8ms)" (from the engine's
    /// <see cref="PlaybackSessionStats"/>); empty while playing, before the first run and after ✕.
    /// </summary>
    public string LastRunSummaryText
    {
        get => _lastRunSummaryText;
        private set
        {
            if (SetProperty(ref _lastRunSummaryText, value ?? string.Empty)) OnPropertyChanged(nameof(HasLastRunSummary));
        }
    }

    /// <summary>The result of the latest playback (null before the first one), e.g. for "🧪 Test Step" on an action step.</summary>
    public PlaybackResult? LastResult { get; private set; }

    /// <summary>True while the summary pill has something to show.</summary>
    public bool HasLastRunSummary => _lastRunSummaryText.Length > 0;

    /// <summary>The pill's ✕.</summary>
    public RelayCommand DismissRunSummaryCommand { get; }

    // Live step progress bar: what was sent last (so a 30 FPS tick only raises the event when something changed).
    private Guid _sentProgressStep;
    private double _sentProgress = -1;
    private bool _sentProgressShown;

    private void PushStepProgress(StepProgress? progress)
    {
        if (progress is { ShowBar: true } p)
        {
            if (_sentProgressShown && p.StepId == _sentProgressStep && Math.Abs(p.Fraction - _sentProgress) < 0.002) return;
            _sentProgressShown = true;
            _sentProgressStep = p.StepId;
            _sentProgress = p.Fraction;
            RunningStepProgressChanged?.Invoke(this, p);
            return;
        }

        HideStepProgress();
    }

    private void HideStepProgress()
    {
        if (!_sentProgressShown) return;
        _sentProgressShown = false;
        _sentProgress = -1;
        _sentProgressStep = Guid.Empty;
        RunningStepProgressChanged?.Invoke(this, default);
    }

    /// <summary>
    /// Raised on the UI thread when playback moves to another step (its ID), and with null when playback ends, so the
    /// editor can highlight the running step.
    /// </summary>
    public event EventHandler<Guid?>? RunningStepChanged;

    /// <summary>
    /// Raised on the UI thread (~30 FPS, only when it changed) with the live progress of the playing step, for the 2 px
    /// bar on its card; <see cref="StepProgress.ShowBar"/> false hides the bar (short steps, step finished, stopped).
    /// </summary>
    public event EventHandler<StepProgress>? RunningStepProgressChanged;

    public AsyncRelayCommand RecordCommand { get; }
    public AsyncRelayCommand StopRecordingCommand { get; }
    public AsyncRelayCommand PlayCommand { get; }
    public RelayCommand PauseResumeCommand { get; }
    public RelayCommand StopCommand { get; }

    public static IReadOnlyList<Steps.Choice<RecordingMode>> RecordingModes => SettingsViewModel.ToolbarRecordingModes;

    public RecordingMode RecordingMode
    {
        get => _hooks.Settings().RecordingMode;
        set
        {
            if (_hooks.Settings().RecordingMode == value) return;
            _hooks.SetRecordingMode?.Invoke(value);
            OnPropertyChanged();
        }
    }

    // Shortcuts come from Settings (they can be customized).
    public string RecordShortcut => _hooks.Settings().RecordHotkey.ToString();
    public string PlayShortcut => _hooks.Settings().PlayHotkey.ToString();
    public string PauseShortcut => _hooks.Settings().PauseHotkey.ToString();

    public string RecordTooltip => $"Record your mouse and keyboard into a new macro ({RecordShortcut})";
    public string StopRecordingTooltip => $"Finish recording ({RecordShortcut})";
    public string PlayTooltip => $"Play the macro open in the editor, or the one selected in the library ({PlayShortcut})";
    public string PauseTooltip => $"Pause or resume ({PauseShortcut}). Pausing lets go of every held key and mouse button right away; Resume continues from the same step, and the paused time isn't counted.";
    public string StopTooltip => $"Stop playback (Esc or {PlayShortcut})";

    public string EmergencyHint => $"Safety: press Esc to stop playback. {RecordShortcut} records, {PlayShortcut} plays.";

    /// <summary>Call after the shortcut settings change so labels and tooltips update.</summary>
    public void RefreshShortcuts()
    {
        foreach (var name in new[] { nameof(RecordingMode), nameof(RecordShortcut), nameof(PlayShortcut), nameof(PauseShortcut), nameof(RecordTooltip),
                     nameof(StopRecordingTooltip), nameof(PlayTooltip), nameof(PauseTooltip), nameof(StopTooltip), nameof(EmergencyHint),
                     nameof(PauseButtonText) })
            OnPropertyChanged(name);
    }

    public AutomationMode Mode
    {
        get => _mode;
        private set
        {
            var previous = _mode;
            if (!SetProperty(ref _mode, value)) return;

            // The HUD's elapsed timer stops while paused.
            if (value == AutomationMode.Paused) _pausedSinceUtc = DateTime.UtcNow;
            else if (previous == AutomationMode.Paused && _pausedSinceUtc is { } since)
            {
                _pausedTotal += DateTime.UtcNow - since;
                _pausedSinceUtc = null;
            }

            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(IsRecording));
            OnPropertyChanged(nameof(IsPlaying));
            OnPropertyChanged(nameof(IsPaused));
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(PauseButtonText));
            OnPropertyChanged(nameof(PauseButtonGlyph));
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

    /// <summary>"⏸ Pause (F8)" / "▶ Resume (F8)" (the glyph is <see cref="PauseButtonGlyph"/>).</summary>
    public string PauseButtonText => _mode == AutomationMode.Paused ? $"Resume ({PauseShortcut})" : $"Pause ({PauseShortcut})";

    /// <summary>Segoe Fluent icon: ▶ while paused, ⏸ otherwise.</summary>
    public string PauseButtonGlyph => _mode == AutomationMode.Paused ? "\uE768" : "\uE769";

    /// <summary>Time since playback started, without paused time (the mini-HUD timer).</summary>
    public TimeSpan PlayingElapsed
    {
        get
        {
            if (_runStartedUtc == default) return TimeSpan.Zero;
            var paused = _pausedTotal + (_pausedSinceUtc is { } since ? DateTime.UtcNow - since : TimeSpan.Zero);
            var elapsed = DateTime.UtcNow - _runStartedUtc - paused;
            return elapsed > TimeSpan.Zero ? elapsed : TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Live speed: the editor's speed selector changed while this macro plays, so the run continues at the new speed from
    /// its next step. Ignored when something else (or nothing) plays.
    /// </summary>
    public void SetLiveSpeed(Guid? macroId, double speed)
    {
        if (!IsPlaying || macroId is null || macroId != _runningMacroId) return;
        _engine.SetPlaybackSpeed(speed);
        _hooks.SetStatus($"Speed {speed:0.##}× from the next step.", StatusKind.Info);
    }

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

    /// <summary>The run (repeat of the whole macro) that is playing now, 1-based; 0 when nothing plays.</summary>
    public int CurrentRunNumber => IsPlaying ? _currentRun : 0;

    /// <summary>How many runs the playing macro is set to (its "Play n times"); 0 = until stopped.</summary>
    public int ConfiguredRunCount => _configuredRuns;

    /// <summary>"Run #3 / ∞" (Play = 0) or "Run 3 / 10"; empty when nothing plays.</summary>
    public string RunCounterText
    {
        get => _runCounterText;
        private set
        {
            if (!SetProperty(ref _runCounterText, value)) return;
            OnPropertyChanged(nameof(HasRunCounter));
            OnPropertyChanged(nameof(CurrentRunNumber));
            OnPropertyChanged(nameof(ConfiguredRunCount));
        }
    }

    /// <summary>"Step 4 / 12" while playing; empty otherwise.</summary>
    public string StepCounterText
    {
        get => _stepCounterText;
        private set => SetProperty(ref _stepCounterText, value);
    }

    /// <summary>True while a run counter is shown (playing or paused).</summary>
    public bool HasRunCounter => _runCounterText.Length > 0;

    /// <summary>Live vision speed while playing, e.g. "⚡ Vision: 0.6ms" (empty until an image / colour look has run).</summary>
    public string VisionTelemetryText
    {
        get => _visionTelemetryText;
        private set
        {
            if (SetProperty(ref _visionTelemetryText, value)) OnPropertyChanged(nameof(HasVisionTelemetry));
        }
    }

    /// <summary>True while <see cref="VisionTelemetryText"/> has something to show.</summary>
    public bool HasVisionTelemetry => _visionTelemetryText.Length > 0;

    /// <summary>"⚡ Vision: 0.6ms" for a look time in ms; empty for 0 / unknown.</summary>
    public static string FormatVisionTelemetry(double milliseconds) =>
        milliseconds > 0 && double.IsFinite(milliseconds) ? $"⚡ Vision: {TargetDetection.FormatMs(milliseconds)}" : string.Empty;

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
                case RecordingMode.Smart:
                case RecordingMode.Rhythm:
                    _smartRecorder.Stop();
                    break;
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
        _engine.RunStarted -= OnEngineRunStarted;
        _engine.TargetDetected -= OnTargetDetected;
        _engine.TargetOverlayClearRequested -= OnTargetOverlayClearRequested;
        _emergency.EmergencyStopRequested -= OnEmergencyStop;
        _host.PauseRequested -= OnHudPauseRequested;
        _host.StopRequested -= OnHudStopRequested;
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
        _recordingHudText = _recordingMode switch
        {
            RecordingMode.Game => $"Recording (game mode)…   {RecordShortcut} to stop",
            RecordingMode.Desktop => $"Recording (desktop mode)…   {RecordShortcut} to stop",
            RecordingMode.Rhythm => $"Recording (exact 1:1 / rhythm)…   {RecordShortcut} to stop",
            _ => $"Recording…   {RecordShortcut} to stop",
        };
        _runStartedUtc = DateTime.UtcNow;
        _pausedTotal = TimeSpan.Zero;
        _pausedSinceUtc = null;
        _host.Enter(OverlayKind.Recording, _recordingHudText);

        // Give the window a moment to minimize so that isn't recorded.
        await Task.Delay(300);
        if (!IsRecording) return;

        try
        {
            // Smart Click Snapshots (Settings): a small picture of what each recorded click hit.
            var snapshots = _hooks.Settings().CaptureClickSnapshots;
            if (_recorder is WindowsInputRecorder desktopRecorder) desktopRecorder.CaptureClickSnapshots = snapshots;
            if (_smartRecorder is SmartInputRecorder smartRecorder) smartRecorder.CaptureClickSnapshots = snapshots && _recordingMode == RecordingMode.Smart;

            switch (_recordingMode)
            {
                case RecordingMode.Game: _gameRecorder.Start(); break;
                case RecordingMode.Smart:
                case RecordingMode.Rhythm:
                    _smartRecorder.Start();
                    break;
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
            RecordingMode.Rhythm => $"Recording in exact 1:1 / rhythm mode (zero simplification, sub-ms lock)… press {RecordShortcut} to finish.",
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

        if (_recordingMode is RecordingMode.Smart or RecordingMode.Rhythm)
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

        var settings = _hooks.Settings();
        var options = new RecordingOptions
        {
            RecordMouseMoves = settings.RecordMouseMovement,
            KeepHoldTime = settings.KeepTapHoldTime,
            TrimTrailingKeys = settings.RecordHotkey.AllKeys(),
            SimplifyMousePath = true, // a few meaningful Move steps instead of one every 30 ms
            CollapseMousePaths = false, // Desktop-only mode keeps simplified mouse paths so pure movements & oscillations work
            MaxTapDurationMs = settings.KeepTapHoldTime ? MacroValidator.MaxHoldMs : 500,
            // Where the cursor was when recording started: the first Move step goes there, so Run #2+ start from the
            // same spot instead of wherever Run #1 ended. (0, 0) = unknown.
            StartX = _recorder.StartX == 0 && _recorder.StartY == 0 ? null : _recorder.StartX,
            StartY = _recorder.StartX == 0 && _recorder.StartY == 0 ? null : _recorder.StartY,
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
        LogClickSnapshots(steps);
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
        var isRhythm = _recordingMode == RecordingMode.Rhythm;
        var options = new SmartRecordingOptions
        {
            TrimTrailingKeys = settings.RecordHotkey.AllKeys(),
            RecordMouseMoves = isRhythm || settings.RecordMouseMovement,
            KeepHoldTime = isRhythm || settings.KeepTapHoldTime,
            SimplifyMousePath = !isRhythm,
            CollapseMousePaths = !isRhythm && settings.SimplifyMousePaths,
            ExactTimeline = isRhythm,
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

        LogClickSnapshots(result.Steps);
        await _hooks.RecordingFinished(result.Steps);
        RefreshTarget();
    }

    /// <summary>One log line about Smart Click Snapshots, when the recording has any.</summary>
    private void LogClickSnapshots(IReadOnlyList<MacroStep> steps)
    {
        var withPicture = steps.Count(s => s is MouseClickStep { HasTargetThumbnail: true });
        var inRecordedInput = steps.Sum(s => s is InputSequenceStep { ClickTargets: { } targets } ? targets.Count : 0);
        if (withPicture == 0 && inRecordedInput == 0) return;
        if (withPicture > 0)
            _log.Info($"✨ Smart Click Snapshots: {withPicture} click(s) remember what they clicked. Select one in the editor to see it, turn on Smart-Aim, or convert it to a \"Find image & click\" step.");
        if (inRecordedInput > 0)
            _log.Info($"✨ Smart Click Snapshots: {inRecordedInput} click(s) inside \"Recorded input\" steps kept their picture too; \"✂ Break into steps\" gives them back to the Mouse click steps.");
    }

    // ---------------- Playback ----------------

    private Task PlayAsync() => PlayMacroAsync(_hooks.GetPlayTarget());

    /// <summary>Plays a specific macro (e.g. from the library's Play button). Ignored if something is already running.</summary>
    public Task PlayMacroAsync(Macro? macro) => PlayMacroAsync(macro, null);

    /// <summary>"▶ Run step": plays only the step at <paramref name="stepIndex"/> (or its whole If / Repeat block) once.</summary>
    public Task RunSingleStepAsync(Macro macro, int stepIndex) =>
        PlayMacroAsync(macro, new PlayRange(stepIndex, SingleStep: true));

    /// <summary>"⏭ Play from here": plays from the step at <paramref name="stepIndex"/> to the end of the macro, once.</summary>
    public Task PlayFromStepAsync(Macro macro, int stepIndex) =>
        PlayMacroAsync(macro, new PlayRange(stepIndex, SingleStep: false));

    /// <summary>Part of a macro to play (null = all of it, with its repeats).</summary>
    private sealed record PlayRange(int StepIndex, bool SingleStep);

    private async Task PlayMacroAsync(Macro? macro, PlayRange? range)
    {
        if (!IsIdle) return;

        if (macro is null)
        {
            _hooks.SetStatus("Open a macro in the editor, or select one in the library, then press Play.", StatusKind.Warning);
            return;
        }

        var checkedMacro = macro;
        IReadOnlyList<ValidationIssue> issues;
        if (range is not null)
        {
            if (range.StepIndex < 0 || range.StepIndex >= macro.Steps.Count)
            {
                _hooks.SetStatus("Select a step first.", StatusKind.Warning);
                return;
            }

            if (range.SingleStep)
            {
                var (start, end) = PlaybackRanges.SingleStep(macro.Steps, range.StepIndex);
                checkedMacro = macro.CopyWithSteps(macro.Steps.GetRange(start, end - start));
                issues = PlaybackRanges.ValidateRange(macro, start, end);
            }
            else
            {
                // "Play from here" (Shift+F5) plays a block-safe slice: Else parts / End markers of blocks that started
                // above the selected step are left out, so the played list is always a valid workflow.
                checkedMacro = PlaybackRanges.BuildPlayFromHereMacro(macro, range.StepIndex).Macro;
                issues = PlaybackRanges.ValidatePlayFromHere(macro, range.StepIndex);
            }
        }
        else
        {
            issues = MacroValidator.ValidateForExecution(macro);
        }
        if (issues.HasErrors())
        {
            var list = string.Join("\n", issues.Where(i => i.Severity == IssueSeverity.Error).Take(8).Select(i => "• " + i.DisplayText));
            var what = range is null ? "this macro" : range.SingleStep ? "this step" : "from this step";
            _dialogs.ShowWarning($"Can't play {what}", $"Fix these problems first:\n\n{list}");
            _hooks.SetStatus($"\"{macro.Name}\" can't play {(range is null ? "" : what + " ")}until its problems are fixed.", StatusKind.Error);
            return;
        }

        if (!_ocr.IsAvailable && WorkflowSteps.UsesTextRecognition(checkedMacro))
        {
            _dialogs.ShowWarning("Text recognition isn't available", $"This macro has \"Find text\" steps or text conditions, but:\n\n{_ocr.StatusText}");
            _hooks.SetStatus("Can't play: text recognition (OCR) isn't available on this PC.", StatusKind.Error);
            return;
        }

        _activeSessionId = Interlocked.Increment(ref _playbackSessionId);
        _pendingTarget = null;
        LastRunSummaryText = string.Empty;
        _runningMacroName = range is null ? macro.Name
            : range.SingleStep ? $"{macro.Name}, step {range.StepIndex + 1}" : $"{macro.Name} from step {range.StepIndex + 1}";
        _runningMacroId = macro.Id;
        _runStartedUtc = default;
        _pausedTotal = TimeSpan.Zero;
        _pausedSinceUtc = null;
        _latestProgress = null;
        _currentRun = 0;
        _configuredRuns = range is null ? Math.Max(0, macro.RepeatCount) : 1;
        Mode = AutomationMode.Playing;
        IsProgressIndeterminate = range is null && macro.RepeatCount == 0;
        ProgressValue = 0;
        ProgressText = "Starting…";
        RunCounterText = PlaybackRunInfo.FormatCounter(1, _configuredRuns);
        StepCounterText = string.Empty;
        _host.Enter(OverlayKind.Playing, OverlayText(), RunCounterText);
        _host.UpdateHud(BuildHud());

        try
        {
            _emergency.Start();
        }
        catch (Exception ex)
        {
            // Not fatal: the Stop button and Ctrl+Shift+F10 still work.
            _log.Warning("The Esc emergency stop could not be enabled; use Ctrl+Shift+F10 or the Stop button.", ex);
        }

        var run = RunPlaybackAsync(macro, range);
        _runTask = run;
        await run;
    }

    private async Task RunPlaybackAsync(Macro macro, PlayRange? range)
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
                result = range switch
                {
                    null => await _engine.RunAsync(macro),
                    { SingleStep: true } => await _engine.PlaySingleStepAsync(macro, range.StepIndex),
                    _ => await _engine.PlayFromStepAsync(macro, range.StepIndex),
                };
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
            // Playback is over: no queued ~30 FPS update of this session may draw any more.
            Interlocked.Increment(ref _playbackSessionId);
            _activeSessionId = -1;
            _pendingTarget = null;
            _progressTimer.Stop();
            _emergency.Stop();
            _host.Exit();
            HideStepProgress();
            SetRunningStep(null);
            LastRunSummaryText = result?.Stats?.SummaryText ?? string.Empty;
            LastResult = result;
            Mode = AutomationMode.Idle;
            ProgressValue = 0;
            ProgressText = string.Empty;
            IsProgressIndeterminate = false;
            _runTask = null;
            ReportResult(result);
            RunCounterText = string.Empty;
            StepCounterText = string.Empty;
            VisionTelemetryText = string.Empty;
            _renderedVersion = -1;
            _renderedProbeMs = -1;
            _currentRun = 0;
            _runningMacroId = null;
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
            {
                // The run that was playing when it stopped counts (stopped during run 4 = "after 4 run(s)").
                var runs = Math.Max(_currentRun, result.IterationsCompleted);
                var finished = result.IterationsCompleted;
                _hooks.SetStatus(runs <= 0
                    ? $"Stopped \"{name}\" before it started."
                    : $"Stopped \"{name}\" after {runs} run(s) ({finished} finished, {result.StepsExecuted:N0} step(s), {elapsed.TotalSeconds:0.0} s).",
                    StatusKind.Warning);
                break;
            }
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
            if (_engine.Pause())
            {
                var where = _latestProgress is { } p ? $" at step {p.StepNumber}" : "";
                _log.Info($"⏸ Paused{where}: every held key and mouse button was released. {PauseShortcut} resumes.");
                _hooks.SetStatus($"⏸ Paused{where}. Held keys and buttons were released. Press {PauseShortcut} or Resume to continue.", StatusKind.Info);
            }
        }
        else if (Mode == AutomationMode.Paused)
        {
            if (_engine.Resume())
            {
                _log.Info("▶ Resumed.");
                _hooks.SetStatus("▶ Resumed.", StatusKind.Info);
            }
        }
    }

    // The mini-HUD's "⏸ F8" / "⏹ Esc" buttons (UI thread).
    private void OnHudPauseRequested(object? sender, EventArgs e) => TogglePause();

    private void OnHudStopRequested(object? sender, EventArgs e)
    {
        if (!IsPlaying) return;
        _log.Info("Stopped from the mini overlay.");
        StopPlayback();
    }

    // The newest detection that the UI thread hasn't drawn yet (null = nothing waiting). With the Hot-Spot fast path a
    // loop can find its target hundreds of times per second: only ONE dispatcher call is queued at a time and it draws
    // the latest target, so the UI thread never gets a backlog of reticles.
    private TargetDetection? _pendingTarget;

    // Playback thread: something was found; draw the target-lock reticle there (mini overlay on only).
    private void OnTargetDetected(object? sender, TargetDetection target)
    {
        if (!_hooks.Settings().ShowRunOverlay) return;
        var session = Interlocked.Read(ref _playbackSessionId);
        if (Interlocked.Exchange(ref _pendingTarget, target) is not null) return; // a draw is already queued; it takes this one
        _dispatcher.InvokeAsync(() =>
        {
            var latest = Interlocked.Exchange(ref _pendingTarget, null);
            if (latest is not null && IsCurrentSession(session)) _host.ShowTargetLock(latest);
        });
    }

    // Playback thread, right before Wait for screen change takes its starting picture. A reticle that captures can see
    // ("🎥 Show overlays in OBS / Discord") would be in that picture, then fade away and look like a change: drop a
    // queued one and hide a visible one now (synchronously, so it's gone before the capture).
    private void OnTargetOverlayClearRequested(object? sender, OverlayClearRequest request)
    {
        Interlocked.Exchange(ref _pendingTarget, null);
        if (!Services.Vision.OverlayCaptureService.AllowObsCapture) return; // left out of captures anyway: nothing to do
        try
        {
            request.Cleared = _dispatcher.Invoke(() => _host.HideTargetLock(), DispatcherPriority.Send, CancellationToken.None,
                TimeSpan.FromMilliseconds(200));
        }
        catch (Exception)
        {
            // Purely visual: never break playback over the reticle.
        }
    }

    private void StopPlayback()
    {
        if (!IsPlaying) return;

        // From now on nothing queued by the throttled UI updates may draw (no "ghost" highlight after Stop): new session
        // number, the pending tick is cancelled and the running-step highlight goes away right now.
        Interlocked.Increment(ref _playbackSessionId);
        _progressTimer.Stop();
        _pendingTarget = null;
        HideStepProgress();
        SetRunningStep(null);

        if (Mode != AutomationMode.Stopping) Mode = AutomationMode.Stopping;
        _engine.Stop();
    }

    /// <summary>
    /// True when a UI update captured in session <paramref name="session"/> may still draw: something is playing and it
    /// is still that same session (no Stop / end of playback / new Play since).
    /// </summary>
    private bool IsCurrentSession(long session) =>
        IsPlaying && session == _activeSessionId && session == Interlocked.Read(ref _playbackSessionId);

    private void OnEmergencyStop(object? sender, EventArgs e)
    {
        if (!IsPlaying) return;
        _log.Warning("Emergency stop (Esc) pressed.");
        StopPlayback();
    }

    // Engine events arrive on the playback thread (the log service is thread-safe).
    // Engine notes starting with "⚠" (e.g. a click on the last found spot that was skipped) go to the log as warnings.
    private void OnEngineMessage(object? sender, string message)
    {
        if (message.StartsWith('⚠')) _log.Warning(message);
        else _log.Info(message);
    }

    // Playback thread: just remember the latest step (no dispatcher call); the ~30 FPS UI timer shows it.
    private void OnEngineProgress(object? sender, PlaybackProgress progress)
    {
        _latestProgress = progress;
        Interlocked.Increment(ref _progressVersion);
    }

    // Playback thread: just remember the numbers; the ~30 FPS UI timer shows them (RefreshProgress).
    private void OnEngineRunStarted(object? sender, PlaybackRunInfo info)
    {
        _configuredRuns = Math.Max(0, info.TotalRuns);
        _currentRun = info.CurrentRun;
        Interlocked.Increment(ref _progressVersion);
    }

    // Pause / resume / stopping: shown at once (forced refresh), not at the next ~30 FPS tick.
    private void OnEngineStateChanged(object? sender, PlaybackState state)
    {
        var session = Interlocked.Read(ref _playbackSessionId);
        _dispatcher.InvokeAsync(() =>
        {
            if (Mode is AutomationMode.Idle or AutomationMode.Recording) return;
            if (!IsCurrentSession(session)) return; // queued before Stop / the end of playback: stale
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

            RefreshProgress(force: true);
        });
    }

    /// <summary>
    /// Shows the latest playback numbers (step, run counter, progress, HUD, vision speed). Called by the ~30 FPS UI timer,
    /// which skips the work when nothing changed (same step report, same mode, same second on the HUD clock, same vision
    /// time); <paramref name="force"/> = show now (pause / resume / state change).
    /// </summary>
    private void RefreshProgress(bool force = false)
    {
        if (IsRecording)
        {
            var seconds = (DateTime.UtcNow - _runStartedUtc).TotalSeconds;
            var count = _recordingMode switch
            {
                RecordingMode.Game => _gameRecorder.EventCount,
                RecordingMode.Smart or RecordingMode.Rhythm => _smartRecorder.EventCount,
                _ => _recorder.EventCount,
            };
            ProgressText = $"{count:N0} input events · {seconds:0} s";
            _host.UpdateHud(new RunHud(OverlayKind.Recording, _recordingHudText, null, $"Recording · {count:N0} input events", 0,
                DateTime.UtcNow - _runStartedUtc, PauseShortcut));
            return;
        }

        if (!IsPlaying) return;

        // A tick that was already queued when Stop was pressed (or playback ended) must not draw a ghost highlight.
        if (!IsCurrentSession(_activeSessionId)) return;

        // Live 2 px progress bar of the playing step: moves every tick while a long step (Delay, hold, Find wait,
        // feedback loop…) runs, even when nothing else changed.
        PushStepProgress(_engine.GetCurrentStepProgress());

        var version = Volatile.Read(ref _progressVersion);
        var second = (long)PlayingElapsed.TotalSeconds;
        var probeMs = _engine.LastProbeElapsedMs;
        if (!force && version == _renderedVersion && second == _renderedSecond && Mode == _renderedMode && probeMs.Equals(_renderedProbeMs))
            return;
        _renderedVersion = version;
        _renderedSecond = second;
        _renderedMode = Mode;
        _renderedProbeMs = probeMs;
        VisionTelemetryText = FormatVisionTelemetry(probeMs);

        if (_latestProgress is { } p && p.StepCount > 0)
        {
            // The engine's RunStarted event is the source of truth for the run number (it changes before the first
            // step of a run is reported); the progress report is the fallback.
            var runNumber = Math.Max(1, Math.Max(_currentRun, p.Iteration));
            RunCounterText = PlaybackRunInfo.FormatCounter(runNumber, _configuredRuns);
            StepCounterText = $"Step {p.StepNumber} / {p.StepCount}";
            var state = Mode == AutomationMode.Paused ? "⏸ Paused" : "▶ Playing";
            ProgressText = p.LoopText is null
                ? $"{state}: {RunCounterText} · Step {p.StepNumber}/{p.StepCount}"
                : $"{state}: {RunCounterText} · Step {p.StepNumber}/{p.StepCount} · {p.LoopText}";
            ProgressValue = p.TotalIterations is int t && t > 0
                ? ((p.Iteration - 1) + (double)p.StepNumber / p.StepCount) / t
                : (double)p.StepNumber / p.StepCount;
            SetRunningStep(p.Step.Id);
        }

        _host.UpdateHud(BuildHud());
    }

    /// <summary>Everything the mini-HUD shows right now: state, "Run #3 / ∞", timer, "Step 4/12: Find image", progress.</summary>
    private RunHud BuildHud()
    {
        var kind = Mode == AutomationMode.Paused ? OverlayKind.Paused : OverlayKind.Playing;
        string? stepText = null;
        double progress = 0;
        if (_latestProgress is { } p && p.StepCount > 0)
        {
            var name = string.IsNullOrWhiteSpace(p.Step.Comment) ? StepDescriber.DisplayName(p.Step.Kind) : p.Step.Comment.Trim();
            stepText = $"Step {p.StepNumber}/{p.StepCount}: {name}";
            progress = (double)p.StepNumber / p.StepCount;
        }

        var counter = string.IsNullOrEmpty(RunCounterText) ? null : RunCounterText;
        var telemetry = string.IsNullOrEmpty(VisionTelemetryText) ? null : VisionTelemetryText;
        return new RunHud(kind, HudActivityText(), counter, stepText, progress, PlayingElapsed, PauseShortcut, "Esc", telemetry);
    }

    /// <summary>The HUD's small second line: what the step is doing, or how to resume.</summary>
    private string HudActivityText()
    {
        if (Mode == AutomationMode.Paused) return $"Paused — {PauseShortcut} to resume, Esc to stop";
        if (_latestProgress is not { } p || p.StepCount == 0) return $"Starting \"{_runningMacroName}\"…";
        var loop = p.LoopText is null ? "" : $" · {p.LoopText}";
        return $"{StepDescriber.Activity(p.Step)}…{loop}";
    }

    private void SetRunningStep(Guid? id)
    {
        if (_runningStepId == id) return;
        _runningStepId = id;
        OnPropertyChanged(nameof(CurrentPlayingStepId));
        RunningStepChanged?.Invoke(this, id);
    }

    /// <summary>The step that is highlighted as playing right now (null when nothing plays, also right after Stop).</summary>
    public Guid? CurrentPlayingStepId => _runningStepId;

    /// <summary>
    /// The floating status pill's text, e.g. "SmartMacro: Step 2/5 — Finding image \"SHAKE\" on the main monitor… (Esc to stop)".
    /// </summary>
    private string OverlayText()
    {
        if (_latestProgress is not { } p || p.StepCount == 0)
            return Mode == AutomationMode.Paused
                ? $"SmartMacro: Paused \"{_runningMacroName}\" ({PauseShortcut} to resume, Esc to stop)"
                : $"SmartMacro: Starting \"{_runningMacroName}\"… (Esc to stop)";

        // The run counter ("Run #3 / ∞") is shown in front of this text by the overlay itself.
        var where = $"Step {p.StepNumber}/{p.StepCount}";
        if (Mode == AutomationMode.Paused)
            return $"Paused at {where} ({PauseShortcut} to resume, Esc to stop)";

        var loop = p.LoopText is null ? "" : $" · {p.LoopText}";
        return $"{where} — {StepDescriber.Activity(p.Step)}…{loop} (Esc to stop)";
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
