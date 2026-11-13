using System.Windows.Threading;
using SmartMacro.Core.Logging;
using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Recording;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Workflow;
using SmartMacro.Mvvm;
using SmartMacro.Services;
using SmartMacro.Services.Input;
using SmartMacro.Services.Ocr;
using SmartMacro.Services.Vision;

namespace SmartMacro.ViewModels;

public enum AppPage
{
    Library,
    Editor,
    Logs,
    Settings,
    Manual,
}

public enum StatusKind
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>The Windows-specific pieces recording and playback need (swappable in tests).</summary>
public sealed record AutomationServices(
    PlaybackEngine Engine,
    IInputRecorder Recorder,
    IRelativeInputRecorder GameRecorder,
    ISmartInputRecorder SmartRecorder,
    EmergencyStopWatcher Emergency,
    IRunModeHost Host,
    IOcrService Ocr,
    IFindTextTools FindTextTools,
    IVisionTools? VisionTools = null);

/// <summary>Top-level view model: owns the pages, navigation, the automation bar and the status bar.</summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly ILogService _log;
    private readonly IDialogService _dialogs;
    private readonly IMacroRepository _repository;
    private readonly IScreenInfoProvider _screen;
    private readonly Dispatcher _dispatcher;
    private readonly IWindowEventMonitor? _windowMonitor;
    private readonly DispatcherTimer _triggerTimer;
    private readonly object _snapshotGate = new();
    private bool _snapshotInFlight;
    private bool _snapshotPending;
    private bool _snapshotPendingEvaluate;

    private AppPage _currentPage = AppPage.Library;
    private string _statusMessage = "Starting…";
    private StatusKind _statusKind = StatusKind.Info;
    private AppSettings _settings = new();
    private bool _isInitialized;
    private string? _lastForegroundTitle;
    private string _lastForegroundProcess = string.Empty;
    private string? _lastExternalForegroundTitle;
    private string _lastExternalForegroundProcess = string.Empty;
    private IReadOnlyList<Core.Vision.WindowInfo>? _lastOpenWindows;
    private string? _lastOpenWindowsSignature;
    private bool _hasWindowSnapshot;
    private volatile bool _needsOpenWindowsSnapshot;
    private volatile bool _hasScheduleTriggers;
    private List<ScopedHotkeyBinding> _lastNotifiedHotkeys = new();
    private string? _lastNotifiedProfile;
    private bool _lastNotifiedArmed = true;

    /// <summary>v1.4 Reflex: deterministic trigger engine (per-macro hotkeys, schedules, window triggers, Run Governor).</summary>
    public TriggerEvaluator TriggerEngine { get; } = new();

    /// <summary>Raised when active per-macro hotkey triggers change so the Windows hotkey service can re-register them.</summary>
    public event EventHandler? MacroHotkeysChanged;

    public MainViewModel(
        IMacroRepository repository,
        ISettingsStore settingsStore,
        ILogService log,
        IDialogService dialogs,
        IScreenInfoProvider screen,
        AppPaths paths,
        Dispatcher dispatcher,
        AutomationServices automation,
        IWindowEventMonitor? windowMonitor = null)
    {
        _log = log;
        _dialogs = dialogs;
        _repository = repository;
        _screen = screen;
        _dispatcher = dispatcher;
        _windowMonitor = windowMonitor;

        if (_windowMonitor is not null)
            _windowMonitor.WindowStateChanged += (_, _) => _ = ProcessMonitorWindowChangeAsync();

        _triggerTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _triggerTimer.Tick += (_, _) => _ = EvaluateScheduleOrFallbackTickAsync();
        ToggleTriggersArmedCommand = new RelayCommand(() => AreTriggersArmed = !AreTriggersArmed);

        Logs = new LogViewModel(log, dispatcher);

        Editor = new EditorViewModel(
            repository, log, dialogs, screen,
            defaultDelayMs: () => _settings.DefaultDelayMs,
            onSaved: saved =>
            {
                Library!.Upsert(saved);
                RefreshTriggerCapabilities();
                NotifyHotkeysChanged(force: true);
                _windowMonitor?.RequestRefresh();
            },
            setStatus: SetStatus,
            goToLibrary: () => CurrentPage = AppPage.Library,
            findTextTools: automation.FindTextTools,
            visionTools: automation.VisionTools,
            allowLocalWebhooks: () => _settings.AllowLocalWebhooks);

        Library = new LibraryViewModel(
            repository, log, dialogs,
            confirmBeforeDelete: () => _settings.ConfirmBeforeDelete,
            openInEditor: OpenInEditorAsync,
            afterDelete: id =>
            {
                Editor.OnMacroDeleted(id);
                RefreshTriggerCapabilities();
                NotifyHotkeysChanged(force: true);
            },
            setStatus: SetStatus,
            playMacro: macro => Automation!.PlayMacroAsync(macro),
            canPlay: () => Automation?.IsIdle ?? false);

        Settings = new SettingsViewModel(
            settingsStore, log, dialogs, paths, ApplySettings, SetStatus,
            automation.Ocr.StatusText, automation.Ocr.IsAvailable, () => BuildDiagnostics(paths, automation.Ocr, log),
            getForegroundContext: GetSettingsForegroundContext,
            getLibraryProfiles: GetLibraryProfileNames,
            getOpenWindows: GetSettingsOpenWindows)
        {
            ExportStudioBackupHandler = ExportStudioBackupAsync,
            RestoreStudioBackupHandler = RestoreStudioBackupAsync,
        };
        ShowLogsCommand = new RelayCommand(() => CurrentPage = AppPage.Logs);
        OpenCommandPaletteCommand = new RelayCommand(OpenCommandPalette);
        OpenMissionControlCommand = new RelayCommand(() => MissionControlRequested?.Invoke(this, EventArgs.Empty));

        Automation = new AutomationViewModel(
            automation.Engine, automation.Recorder, automation.GameRecorder, automation.SmartRecorder, automation.Emergency, automation.Host, automation.Ocr, log, dialogs, dispatcher,
            new AutomationHooks
            {
                GetPlayTarget = GetPlayTarget,
                GetPlayTargetInfo = GetPlayTargetInfo,
                BeforeRecording = () => Editor.ConfirmLeaveAsync(),
                RecordingFinished = SaveRecordingAsync,
                Settings = () => _settings,
                SetRecordingMode = mode => RecordingMode = mode,
                SetRecordingFidelity = fidelity => _ = Settings.QuickSetAsync(s => s.RecordingFidelity = fidelity),
                ValidateCallGraph = root => MacroValidator.ValidateCallGraph(root, Library.Items.Select(i => i.Macro).ToList()),
                SetStatus = SetStatus,
            });

        // v1.2 Brain: sub-macro lookup, live variable readouts, and opt-in Self-Heal suggestions.
        Editor.GetLibraryMacros = () => Library.Items.Select(i => i.Macro).ToList();
        Editor.OpenSubMacroHandler = OpenSubMacroByLinkAsync;
        automation.Engine.MacroResolver = (id, name) =>
        {
            var resolved = MacroFileFormat.ResolveMacro(Library.Items.Select(i => i.Macro).ToList(), id, name);
            return resolved is null ? null : MacroJson.Clone(resolved);
        };
        automation.Engine.VariablesChanged += (_, snap) => dispatcher.InvokeAsync(() => Editor.UpdateLiveVariables(snap));
        automation.Engine.CallStackChanged += (_, frames) => dispatcher.InvokeAsync(() => Editor.UpdateCallStack(frames));
        automation.Engine.TraceRecorded += (_, entry) => dispatcher.InvokeAsync(() => Editor.AppendTraceEntry(entry));
        automation.Engine.PausedChanged += (_, paused) => dispatcher.InvokeAsync(() => Editor.IsPlaybackPaused = paused);
        var wasPlaying = Automation.IsPlaying;
        Automation.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AutomationViewModel.IsPlaying) or "")
            {
                var nowPlaying = Automation.IsPlaying;
                if (nowPlaying && !wasPlaying)
                    Editor.BeginRuntimeDebugSession();
                wasPlaying = nowPlaying;
            }
            if (e.PropertyName is nameof(AutomationViewModel.IsPaused) or "")
                Editor.IsPlaybackPaused = Automation.IsPaused;
        };
        Automation.PlaybackFinished += (_, info) =>
        {
            Editor.ShowPlaybackResult(info.MacroId, info.Result);
            if (TriggerEngine.DequeueNext() is { Kind: TriggerDecisionKind.StartMacro, Macro: { } nextMacro })
            {
                _ = dispatcher.InvokeAsync(async () =>
                {
                    if (Automation.IsIdle)
                    {
                        _log.Info($"⚡ Trigger queue: starting queued macro \"{nextMacro.Name}\".");
                        await Automation.PlayMacroAsync(MacroJson.Clone(nextMacro));
                    }
                });
            }
        };

        Editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(EditorViewModel.IsDirty) or nameof(EditorViewModel.Name) or nameof(EditorViewModel.HasMacro) or "")
            {
                OnPropertyChanged(nameof(WindowTitle));
                OnPropertyChanged(nameof(ActiveMacroBreadcrumb));
                OnPropertyChanged(nameof(HasActiveMacroBreadcrumb));
            }
            if (e.PropertyName is nameof(EditorViewModel.HasMacro) or nameof(EditorViewModel.Name)
                or nameof(EditorViewModel.RepeatCount) or nameof(EditorViewModel.Speed) or "")
                Automation.RefreshTarget();

            // Live speed: changing the speed while this macro plays applies from its next step.
            if (e.PropertyName == nameof(EditorViewModel.Speed) && Automation.IsPlaying)
                Automation.SetLiveSpeed(Editor.MacroId, Editor.Speed);
        };

        Library.MacroRenamed = renamed => Editor.SyncExternalRename(renamed.Id, renamed.Name);
        Library.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LibraryViewModel.SelectedItem) or nameof(LibraryViewModel.CountText) or nameof(LibraryViewModel.SelectedProfileFilter) or "")
            {
                Automation.RefreshTarget();
                if (Editor.HasMacro) Editor.RefreshSubMacroLinks();
                if (e.PropertyName is nameof(LibraryViewModel.CountText) or nameof(LibraryViewModel.SelectedProfileFilter) or "")
                {
                    RefreshTriggerCapabilities();
                    NotifyHotkeysChanged(force: true);
                }
            }
        };

        // Live step highlight: the editor marks (and scrolls to) the step that is running.
        Automation.RunningStepChanged += (_, stepId) => Editor.SetRunningStep(stepId);

        // "▶ Run step" / "⏭ Play from here" in the editor play through the same automation (overlay, Esc stop, failsafe).
        Editor.RunSingleStepHandler = (macro, index) => Automation.RunSingleStepAsync(macro, index);
        Editor.PlayFromStepHandler = (macro, index) => Automation.PlayFromStepAsync(macro, index);
        Editor.CanRunSteps = () => Automation.IsIdle;

        // AAA Studio UI: the live progress bar on the playing step's card, and the Ctrl+K palette's "▶ Play Macro".
        Automation.RunningStepProgressChanged += (_, p) => Editor.SetRunningStepProgress(p.StepId, p.Fraction, p.ShowBar);
        Editor.PlayHandler = () =>
        {
            if (Automation.PlayCommand.CanExecute(null)) Automation.PlayCommand.Execute(null);
        };
        Editor.PlayShortcutText = () => Automation.PlayShortcut;

        // 🧪 Test Step on an action step reports how its one-step run went.
        Editor.LastPlaybackResult = () => Automation.LastResult;

        // v1.8 + Debugger 2.0 Interactive Debugger: breakpoints (F9), conditional rules, watchpoints, Call Stack & stepping (F10/F11/Shift+F11).
        automation.Engine.HasBreakpoint = id => Editor.HasStepBreakpoint(id);
        automation.Engine.BreakpointRuleProvider = id => Editor.GetBreakpointRule(id);
        automation.Engine.WatchpointProvider = () => Editor.GetCoreWatchpoints();
        Editor.StepOverHandler = () => automation.Engine.StepOver();
        Editor.StepIntoHandler = () => automation.Engine.StepInto();
        Editor.StepOutHandler = () => automation.Engine.StepOut();
        Editor.SetLiveVariableHandler = (name, val) =>
        {
            var ok = automation.Engine.TrySetLiveVariable(name, val, out var err);
            return (ok, err);
        };

        // 📚 In-App Manual: recipes load into the editor; the cheat-sheet shows the shortcuts set in Settings.
        Manual = new ManualViewModel(LoadRecipeAsync, () => (Automation.RecordShortcut, Automation.PlayShortcut, Automation.PauseShortcut),
            ex =>
            {
                _log.Error("Loading the example failed.", ex);
                _dialogs.ShowError("Could not load the example", ex.Message);
            });
    }

    public static IReadOnlyList<Steps.Choice<RecordingMode>> RecordingModes => SettingsViewModel.ToolbarRecordingModes;

    /// <summary>Toolbar selector: recording mode (saved in Settings immediately).</summary>
    public RecordingMode RecordingMode
    {
        get => _settings.RecordingMode;
        set
        {
            if (_settings.RecordingMode == value) return;
            _ = Settings.QuickSetAsync(s => s.RecordingMode = value);
        }
    }

    public static IReadOnlyList<Steps.Choice<RecordingFidelity>> RecordingFidelities => SettingsViewModel.RecordingFidelityChoices;

    /// <summary>Recording fidelity selector (Exact 1:1 / Smart Optimized / Clicks &amp; Keys Only; saved in Settings immediately).</summary>
    public RecordingFidelity RecordingFidelity
    {
        get => _settings.RecordingFidelity;
        set
        {
            if (_settings.RecordingFidelity == value) return;
            _ = Settings.QuickSetAsync(s => s.RecordingFidelity = value);
        }
    }

    /// <summary>Toolbar toggle: show the small click-through status pill while playing (saved in Settings).</summary>
    public bool ShowMiniOverlay
    {
        get => _settings.ShowRunOverlay;
        set
        {
            if (_settings.ShowRunOverlay == value) return;
            _ = Settings.QuickSetAsync(s => s.ShowRunOverlay = value);
        }
    }

    /// <summary>
    /// Editor top bar pill "🎥 Show overlays in OBS / Discord" (streamer mode; saved in Settings at once and applied live to
    /// open overlays).
    /// </summary>
    public bool ShowOverlaysInObs
    {
        get => _settings.ShowOverlaysInObsAndRecording;
        set
        {
            if (_settings.ShowOverlaysInObsAndRecording == value) return;
            _ = Settings.QuickSetAsync(s => s.ShowOverlaysInObsAndRecording = value);
        }
    }

    /// <summary>Toolbar toggle: minimize SmartMacro while playing (saved in Settings).</summary>
    public bool MinimizeWhilePlaying
    {
        get => _settings.MinimizeDuringRun;
        set
        {
            if (_settings.MinimizeDuringRun == value) return;
            _ = Settings.QuickSetAsync(s => s.MinimizeDuringRun = value);
        }
    }

    public LibraryViewModel Library { get; }
    public EditorViewModel Editor { get; }
    public LogViewModel Logs { get; }
    public SettingsViewModel Settings { get; }

    /// <summary>The In-App Manual (Studio Knowledge Base & Recipe Center).</summary>
    public ManualViewModel Manual { get; }
    public AutomationViewModel Automation { get; }
    public RelayCommand ShowLogsCommand { get; }
    public RelayCommand OpenCommandPaletteCommand { get; }
    public RelayCommand OpenMissionControlCommand { get; }

    /// <summary>Raised when the user clicks Mission Control or presses Ctrl+M.</summary>
    public event EventHandler? MissionControlRequested;

    /// <summary>Whether the left Studio navigation rail is pinned open (saved in Settings).</summary>
    public bool IsSidebarPinned
    {
        get => _settings.SidebarPinned;
        set
        {
            if (_settings.SidebarPinned == value) return;
            _settings.SidebarPinned = value;
            OnPropertyChanged();
            _ = Settings.QuickSetAsync(s => s.SidebarPinned = value);
        }
    }

    /// <summary>Breadcrumb label for the active Studio page.</summary>
    public string PageBreadcrumbText => _currentPage switch
    {
        AppPage.Editor => "Workflow Editor",
        AppPage.Manual => "Studio Manual",
        AppPage.Logs => "Activity Logs",
        AppPage.Settings => "Settings",
        _ => "Macro Library",
    };

    /// <summary>Open macro name shown in the Studio Title Bar breadcrumb when a macro is loaded.</summary>
    public string ActiveMacroBreadcrumb =>
        Editor.HasMacro ? (string.IsNullOrWhiteSpace(Editor.Name) ? "Untitled" : Editor.Name.Trim()) : string.Empty;

    public bool HasActiveMacroBreadcrumb => Editor.HasMacro;

    private void OpenCommandPalette()
    {
        if (!Editor.HasMacro && Library.SelectedItem is { } selected && Library.GetSaved(selected.Id) is { } saved)
            Editor.Load(saved);
        if (CurrentPage != AppPage.Editor)
            CurrentPage = AppPage.Editor;
        Editor.IsCommandPaletteOpen = !Editor.IsCommandPaletteOpen;
    }

    /// <summary>Opens a macro by ID or name in the Workflow Editor (used by Call Macro "Open ↗").</summary>
    public Task OpenSubMacroByLinkAsync(Guid targetId, string? targetName)
    {
        var resolved = MacroFileFormat.ResolveMacro(Library.Items.Select(i => i.Macro).ToList(), targetId, targetName);
        if (resolved is null)
        {
            SetStatus($"Sub-macro \"{(string.IsNullOrWhiteSpace(targetName) ? targetId.ToString() : targetName)}\" wasn't found in your Library.", StatusKind.Warning);
            return Task.CompletedTask;
        }
        return OpenInEditorAsync(resolved.Id);
    }

    /// <summary>The settings currently in effect (last saved).</summary>
    public AppSettings CurrentSettings => _settings;

    public AppPage CurrentPage
    {
        get => _currentPage;
        set
        {
            if (!SetProperty(ref _currentPage, value)) return;
            OnPropertyChanged(nameof(CurrentPageViewModel));
            OnPropertyChanged(nameof(PageBreadcrumbText));
            if (value == AppPage.Manual) Manual.Refresh(); // shortcut keys may have changed in Settings
            if (value == AppPage.Settings) _ = Settings.RefreshWindowContextAsync();
        }
    }

    public object CurrentPageViewModel => _currentPage switch
    {
        AppPage.Editor => Editor,
        AppPage.Logs => Logs,
        AppPage.Settings => Settings,
        AppPage.Manual => Manual,
        _ => Library,
    };

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public StatusKind StatusKind
    {
        get => _statusKind;
        private set => SetProperty(ref _statusKind, value);
    }

    public bool ShowLogPanel => _settings.ShowLogPanel;

    public string WindowTitle =>
        Editor.HasMacro
            ? $"SmartMacro - {(string.IsNullOrWhiteSpace(Editor.Name) ? "Untitled" : Editor.Name.Trim())}{(Editor.IsDirty ? " •" : "")}"
            : "SmartMacro";

    public async Task InitializeAsync()
    {
        if (_isInitialized) return;
        _isInitialized = true;

        _log.Info("SmartMacro started.");
        await Settings.LoadAsync();
        await Library.LoadAsync();

        if (StatusKind is StatusKind.Info)
            SetStatus(Library.Items.Count == 0 ? "Ready. Create your first macro to get started." : $"Ready. {Library.CountText} loaded.", StatusKind.Info);
        Automation.RefreshTarget();
        RefreshTriggerCapabilities();
        _windowMonitor?.Start();
        await CaptureAndApplyWindowSnapshotAsync(evaluateTriggers: true);
        NotifyHotkeysChanged(force: true);
        _triggerTimer.Start();
    }

    // ---------------- v1.4 Reflex: Per-macro triggers & Run Governor ----------------

    public RelayCommand ToggleTriggersArmedCommand { get; }

    /// <summary>Global master switch for per-macro triggers (hotkeys, schedules, window triggers).</summary>
    public bool AreTriggersArmed
    {
        get => TriggerEngine.IsArmed;
        set
        {
            if (TriggerEngine.IsArmed == value) return;
            TriggerEngine.IsArmed = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TriggersArmedStatusText));
            RefreshTriggerCapabilities();
            NotifyHotkeysChanged(force: true);
            if (value) _windowMonitor?.RequestRefresh();
            SetStatus(value ? "⚡ Macro triggers armed." : "⚡ Macro triggers disarmed.", StatusKind.Info);
        }
    }

    private string? _activeProfileName;

    public string? ActiveProfileName
    {
        get => _activeProfileName;
        private set
        {
            if (SetProperty(ref _activeProfileName, value))
                OnPropertyChanged(nameof(TriggersArmedStatusText));
        }
    }

    public string TriggersArmedStatusText =>
        !TriggerEngine.IsArmed
            ? "⚡ Triggers Paused"
            : string.IsNullOrWhiteSpace(_activeProfileName)
                ? "⚡ Triggers Armed"
                : $"⚡ Armed ({_activeProfileName})";

    private IReadOnlyList<Macro> GetArmedLibraryMacros(string? fgTitle, string? fgProc = null)
    {
        var all = Library.Items.Select(i => i.Macro).ToList();
        var manualFilter = Library.SelectedProfileFilter;
        var isManual = !string.IsNullOrWhiteSpace(manualFilter)
            && !string.Equals(manualFilter, ProfileRouter.AllProfilesLabel, StringComparison.OrdinalIgnoreCase);
        var activeProfile = ProfileRouter.ResolveActiveProfile(
            _settings.ProfileRules,
            all,
            fgTitle,
            manualFilter,
            _settings.AutoSwitchProfileByWindow,
            fgProc);
        ActiveProfileName = activeProfile;
        return ProfileRouter.FilterArmedMacros(all, activeProfile, fgTitle, _settings.AutoSwitchProfileByWindow, isManual, fgProc);
    }

    /// <summary>Returns all distinct valid hotkeys active for the current profile &amp; foreground context (empty when disarmed).</summary>
    public IReadOnlyList<ScopedHotkeyBinding> GetActiveTriggerHotkeyEntries()
    {
        if (!TriggerEngine.IsArmed) return Array.Empty<ScopedHotkeyBinding>();
        var armed = GetArmedLibraryMacros(_lastForegroundTitle, _lastForegroundProcess);
        return ProfileRouter.ResolveActiveHotkeyEntries(armed, _lastForegroundTitle, _lastForegroundProcess);
    }

    /// <summary>Returns all distinct valid hotkeys used by active macro triggers in the current context (empty when disarmed).</summary>
    public IReadOnlyList<HotkeyBinding> GetActiveTriggerHotkeys()
    {
        var entries = GetActiveTriggerHotkeyEntries();
        if (entries.Count == 0) return Array.Empty<HotkeyBinding>();
        var list = new List<HotkeyBinding>(entries.Count);
        foreach (var entry in entries)
            list.Add(entry.Binding);
        return list;
    }

    private void RefreshTriggerCapabilities()
    {
        var macros = Library.Items.Select(i => i.Macro).ToList();
        _needsOpenWindowsSnapshot = TriggerEvaluator.RequiresOpenWindowsSnapshot(macros);
        _hasScheduleTriggers = TriggerEvaluator.HasScheduleTriggers(macros);
    }

    private void NotifyHotkeysChanged(bool force = false)
    {
        var armed = GetArmedLibraryMacros(_lastForegroundTitle, _lastForegroundProcess);
        var isArmed = TriggerEngine.IsArmed;
        var entries = isArmed
            ? ProfileRouter.ResolveActiveHotkeyEntries(armed, _lastForegroundTitle, _lastForegroundProcess)
            : Array.Empty<ScopedHotkeyBinding>();

        var profileChanged = !string.Equals(_lastNotifiedProfile, ActiveProfileName, StringComparison.OrdinalIgnoreCase);
        var armedChanged = _lastNotifiedArmed != isArmed;
        var hotkeysChanged = _lastNotifiedHotkeys.Count != entries.Count;
        if (!hotkeysChanged)
        {
            for (var i = 0; i < entries.Count; i++)
            {
                if (_lastNotifiedHotkeys[i] != entries[i])
                {
                    hotkeysChanged = true;
                    break;
                }
            }
        }

        if (!force && !hotkeysChanged && !profileChanged && !armedChanged)
            return;

        _lastNotifiedHotkeys = new List<ScopedHotkeyBinding>(entries);
        _lastNotifiedProfile = ActiveProfileName;
        _lastNotifiedArmed = isArmed;
        MacroHotkeysChanged?.Invoke(this, EventArgs.Empty);
    }

    public void HandleMacroHotkeyDown(HotkeyBinding binding)
    {
        if (Automation.IsRecording) return;
        var (fgTitle, fgProc) = _screen.GetForegroundWindowInfo();
        _lastForegroundTitle = fgTitle;
        _lastForegroundProcess = fgProc ?? string.Empty;
        var macros = GetArmedLibraryMacros(fgTitle, fgProc);
        var decision = TriggerEngine.EvaluateHotkeyDown(
            macros,
            binding,
            fgTitle,
            Automation.RunningMacroId,
            DateTimeOffset.UtcNow,
            fgProc);
        _ = ExecuteTriggerDecisionAsync(decision);
    }

    public void HandleMacroHotkeyUp(HotkeyBinding binding)
    {
        if (Automation.IsRecording) return;
        var decision = TriggerEngine.EvaluateHotkeyUp(
            binding,
            Automation.RunningMacroId);
        _ = ExecuteTriggerDecisionAsync(decision);
    }

    private async Task ProcessMonitorWindowChangeAsync()
    {
        if (Automation.IsRecording) return;
        await CaptureAndApplyWindowSnapshotAsync(evaluateTriggers: TriggerEngine.IsArmed);
    }

    private async Task EvaluateScheduleOrFallbackTickAsync()
    {
        if (!TriggerEngine.IsArmed || Automation.IsRecording || Library.Items.Count == 0) return;

        if (_windowMonitor is null || !_windowMonitor.IsRunning || !_hasWindowSnapshot)
        {
            await CaptureAndApplyWindowSnapshotAsync(evaluateTriggers: true);
            return;
        }

        if (_hasScheduleTriggers)
            await EvaluateBackgroundTriggersAsync();
    }

    private async Task CaptureAndApplyWindowSnapshotAsync(bool evaluateTriggers)
    {
        bool needsOpenWindows;
        lock (_snapshotGate)
        {
            if (_snapshotInFlight)
            {
                _snapshotPending = true;
                _snapshotPendingEvaluate |= evaluateTriggers;
                return;
            }
            _snapshotInFlight = true;
            needsOpenWindows = _needsOpenWindowsSnapshot;
        }

        try
        {
            while (true)
            {
                var snapshot = await Task.Run(() =>
                {
                    var (fgTitle, fgProc) = _screen.GetForegroundWindowInfo();
                    IReadOnlyList<Core.Vision.WindowInfo>? openWindows = null;
                    string? signature = null;

                    if (needsOpenWindows && _screen is Core.Vision.IScreenLayout layout)
                    {
                        var windows = layout.GetWindows();
                        openWindows = windows;
                        signature = BuildOpenWindowsSignature(windows);
                    }

                    return (fgTitle, fgProc, openWindows, signature);
                }).ConfigureAwait(false);

                if (_dispatcher.HasShutdownStarted)
                {
                    lock (_snapshotGate)
                    {
                        _snapshotPending = false;
                        _snapshotPendingEvaluate = false;
                        _snapshotInFlight = false;
                    }
                    return;
                }

                if (_dispatcher.CheckAccess())
                {
                    ApplyWindowSnapshotOnUiThread(
                        snapshot.fgTitle,
                        snapshot.fgProc,
                        snapshot.openWindows,
                        snapshot.signature,
                        evaluateTriggers);
                }
                else
                {
                    await _dispatcher.InvokeAsync(() =>
                        ApplyWindowSnapshotOnUiThread(
                            snapshot.fgTitle,
                            snapshot.fgProc,
                            snapshot.openWindows,
                            snapshot.signature,
                            evaluateTriggers));
                }

                lock (_snapshotGate)
                {
                    if (_snapshotPending)
                    {
                        _snapshotPending = false;
                        evaluateTriggers = _snapshotPendingEvaluate;
                        _snapshotPendingEvaluate = false;
                        needsOpenWindows = _needsOpenWindowsSnapshot;
                        continue;
                    }
                    _snapshotInFlight = false;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            lock (_snapshotGate)
            {
                _snapshotPending = false;
                _snapshotPendingEvaluate = false;
                _snapshotInFlight = false;
            }
            _log.Warning($"Window snapshot failed: {ex.Message}", ex);
        }
    }

    private void ApplyWindowSnapshotOnUiThread(
        string? fgTitle,
        string? fgProc,
        IReadOnlyList<Core.Vision.WindowInfo>? openWindows,
        string? openWindowsSignature,
        bool evaluateTriggers)
    {
        var normalizedProc = fgProc ?? string.Empty;
        var fgChanged = !_hasWindowSnapshot
            || !string.Equals(_lastForegroundTitle, fgTitle, StringComparison.Ordinal)
            || !string.Equals(_lastForegroundProcess, normalizedProc, StringComparison.OrdinalIgnoreCase);

        var windowsChanged = openWindows is not null
            && !string.Equals(_lastOpenWindowsSignature, openWindowsSignature, StringComparison.Ordinal);

        _lastForegroundTitle = fgTitle;
        _lastForegroundProcess = normalizedProc;
        RememberExternalForeground(fgTitle, normalizedProc);
        if (openWindows is not null)
        {
            _lastOpenWindows = openWindows;
            _lastOpenWindowsSignature = openWindowsSignature;
        }
        else if (!_needsOpenWindowsSnapshot)
        {
            _lastOpenWindows = null;
            _lastOpenWindowsSignature = null;
        }
        _hasWindowSnapshot = true;

        if (!fgChanged && !windowsChanged)
            return;

        if (fgChanged)
        {
            NotifyHotkeysChanged();
            Settings?.NotifyLiveContextChanged();
        }

        if (evaluateTriggers && TriggerEngine.IsArmed && !Automation.IsRecording && Library.Items.Count > 0)
            _ = EvaluateBackgroundTriggersWithSnapshotAsync(_lastForegroundTitle, _lastForegroundProcess, _lastOpenWindows);
    }

    private static string BuildOpenWindowsSignature(IReadOnlyList<Core.Vision.WindowInfo> windows)
    {
        if (windows.Count == 0) return string.Empty;
        var sb = new System.Text.StringBuilder(windows.Count * 24);
        for (var i = 0; i < windows.Count; i++)
        {
            var w = windows[i];
            if (i > 0) sb.Append('\n');
            sb.Append(w.Handle);
            sb.Append('|');
            sb.Append(w.Title);
            sb.Append('|');
            sb.Append(w.ProcessName);
        }
        return sb.ToString();
    }

    private void RememberExternalForeground(string? fgTitle, string? fgProc)
    {
        if (string.IsNullOrWhiteSpace(fgTitle) && string.IsNullOrWhiteSpace(fgProc))
            return;
        _lastExternalForegroundTitle = fgTitle;
        _lastExternalForegroundProcess = fgProc ?? string.Empty;
    }

    private (string? Title, string ProcessName) GetSettingsForegroundContext()
    {
        if (!string.IsNullOrWhiteSpace(_lastForegroundTitle) || !string.IsNullOrWhiteSpace(_lastForegroundProcess))
            return (_lastForegroundTitle, _lastForegroundProcess);
        return (_lastExternalForegroundTitle, _lastExternalForegroundProcess);
    }

    private IReadOnlyList<string> GetLibraryProfileNames()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var item in Library.Items)
        {
            var profile = item.ProfileName;
            if (!string.IsNullOrWhiteSpace(profile) && seen.Add(profile))
                list.Add(profile);
        }
        return list;
    }

    private IReadOnlyList<OpenWindowChoice> GetSettingsOpenWindows()
    {
        if (_screen is not Core.Vision.IScreenLayout layout)
            return Array.Empty<OpenWindowChoice>();

        var windows = layout.GetWindows();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var choices = new List<OpenWindowChoice>(windows.Count);
        foreach (var win in windows)
        {
            var title = win.Title?.Trim() ?? string.Empty;
            var proc = win.ProcessName?.Trim() ?? string.Empty;
            if (title.Length == 0 && proc.Length == 0)
                continue;
            var key = $"{title}\n{proc}";
            if (seen.Add(key))
                choices.Add(new OpenWindowChoice(title, proc));
        }
        choices.Sort((a, b) => string.Compare(a.DisplayLabel, b.DisplayLabel, StringComparison.OrdinalIgnoreCase));
        return choices;
    }

    public async Task EvaluateBackgroundTriggersAsync()
    {
        if (!TriggerEngine.IsArmed || Automation.IsRecording) return;
        if (Library.Items.Count == 0) return;

        string? fgTitle;
        string fgProc;
        IReadOnlyList<Core.Vision.WindowInfo>? openWindows;

        if (_windowMonitor is { IsRunning: true } && _hasWindowSnapshot)
        {
            fgTitle = _lastForegroundTitle;
            fgProc = _lastForegroundProcess;
            openWindows = _lastOpenWindows;
        }
        else
        {
            var snap = await Task.Run(() =>
            {
                var (title, proc) = _screen.GetForegroundWindowInfo();
                IReadOnlyList<Core.Vision.WindowInfo>? wins = _needsOpenWindowsSnapshot && _screen is Core.Vision.IScreenLayout layout
                    ? layout.GetWindows()
                    : null;
                return (title, proc, wins);
            });
            fgTitle = snap.title;
            fgProc = snap.proc ?? string.Empty;
            openWindows = snap.wins;
            _lastForegroundTitle = fgTitle;
            _lastForegroundProcess = fgProc;
            if (openWindows is not null)
            {
                _lastOpenWindows = openWindows;
                _lastOpenWindowsSignature = BuildOpenWindowsSignature(openWindows);
            }
            _hasWindowSnapshot = true;
            NotifyHotkeysChanged();
        }

        await EvaluateBackgroundTriggersWithSnapshotAsync(fgTitle, fgProc, openWindows);
    }

    private async Task EvaluateBackgroundTriggersWithSnapshotAsync(
        string? fgTitle,
        string fgProc,
        IReadOnlyList<Core.Vision.WindowInfo>? openWindows)
    {
        if (!TriggerEngine.IsArmed || Automation.IsRecording) return;
        if (Library.Items.Count == 0) return;

        var macros = GetArmedLibraryMacros(fgTitle, fgProc);
        var decisions = TriggerEngine.EvaluateTick(
            macros,
            DateTimeOffset.Now,
            openWindows,
            fgTitle,
            fgProc,
            Automation.RunningMacroId);

        foreach (var decision in decisions)
            await ExecuteTriggerDecisionAsync(decision);
    }

    private async Task ExecuteTriggerDecisionAsync(TriggerDecision decision)
    {
        switch (decision.Kind)
        {
            case TriggerDecisionKind.StartMacro when decision.Macro is { } macro:
                _log.Info($"⚡ {decision.Reason} → starting \"{macro.Name}\".");
                await Automation.PlayMacroAsync(MacroJson.Clone(macro));
                break;

            case TriggerDecisionKind.StopPlaying:
                _log.Info($"⚡ {decision.Reason} → stopping playback.");
                if (Automation.StopCommand.CanExecute(null))
                    Automation.StopCommand.Execute(null);
                break;

            case TriggerDecisionKind.StopAndStartMacro when decision.Macro is { } macro:
                _log.Info($"⚡ {decision.Reason} → stopping current run and starting \"{macro.Name}\".");
                await Automation.StopAllAsync();
                await Automation.PlayMacroAsync(MacroJson.Clone(macro));
                break;

            case TriggerDecisionKind.QueueMacro when decision.Macro is { } macro:
                SetStatus($"⚡ Queued \"{macro.Name}\" ({TriggerEngine.QueuedCount} in queue).", StatusKind.Info);
                break;
        }
    }

    public void SetStatus(string message, StatusKind kind)
    {
        StatusMessage = message;
        StatusKind = kind;

        var level = kind switch
        {
            StatusKind.Success => AppLogLevel.Success,
            StatusKind.Warning => AppLogLevel.Warning,
            StatusKind.Error => AppLogLevel.Error,
            _ => AppLogLevel.Info,
        };
        _log.Log(level, message);
    }

    /// <summary>
    /// Before the window closes: stops recording/playback (so no keys stay held) and asks about unsaved changes.
    /// Returns true if it may close.
    /// </summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        await Automation.StopAllAsync();
        if (Editor.IsDirty) CurrentPage = AppPage.Editor;
        return await Editor.ConfirmLeaveAsync();
    }

    public void Dispose()
    {
        _triggerTimer.Stop();
        _windowMonitor?.Dispose();
        _log.Info("SmartMacro closed.");
        Automation.Dispose();
        Logs.Dispose();
    }

    /// <summary>Play runs the macro open in the editor (including unsaved edits), otherwise the library selection.</summary>
    private Macro? GetPlayTarget()
    {
        if (Editor.HasMacro) return MacroJson.Clone(Editor.BuildMacro());
        return Library.SelectedItem is { } item ? MacroJson.Clone(item.Macro) : null;
    }

    private PlayTargetInfo? GetPlayTargetInfo()
    {
        if (Editor.HasMacro) return new PlayTargetInfo(Editor.Name, Editor.RepeatCount, Editor.Speed);
        return Library.SelectedItem is { } item ? new PlayTargetInfo(item.Name, item.Macro.RepeatCount, item.Macro.Speed) : null;
    }

    /// <summary>Saves a finished recording as a new macro and opens it in the editor.</summary>
    private async Task SaveRecordingAsync(List<MacroStep> steps)
    {
        var macro = MacroFactory.CreateNew(Library.Names, $"Recording {DateTime.Now:yyyy-MM-dd HH:mm}");
        macro.Steps = steps;

        var dominantWindow = steps
            .OfType<MouseClickStep>()
            .Select(c => c.Target?.WindowTitle)
            .Concat(steps.OfType<InputSequenceStep>()
                .SelectMany(s => s.ClickTargets ?? Enumerable.Empty<SequenceClickTarget>())
                .Select(t => t.Target?.WindowTitle))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .GroupBy(t => t!.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();

        macro.Description = !string.IsNullOrWhiteSpace(dominantWindow)
            ? $"Recorded in {dominantWindow} with SmartMacro."
            : "Recorded with SmartMacro.";

        try
        {
            var refBounds = CoordinateScaling.CurrentTarget(macro, _screen as Core.Vision.IScreenLayout);
            macro.ReferenceLeft = refBounds.X;
            macro.ReferenceTop = refBounds.Y;
            macro.ReferenceWidth = refBounds.Width;
            macro.ReferenceHeight = refBounds.Height;
        }
        catch
        {
            // Fallback to virtual screen bounds if layout probe is unavailable.
            var vs = _screen.GetVirtualScreen();
            if (vs.Width > 0 && vs.Height > 0)
            {
                macro.ReferenceLeft = vs.Left;
                macro.ReferenceTop = vs.Top;
                macro.ReferenceWidth = vs.Width;
                macro.ReferenceHeight = vs.Height;
            }
        }

        try
        {
            await _repository.SaveAsync(macro);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Core.Validation.MacroValidationException)
        {
            _log.Error("Saving the recording failed.", ex);
            _dialogs.ShowError("Could not save the recording", ex.Message);
            return;
        }

        Library.Upsert(MacroJson.Clone(macro));
        Editor.Load(macro);
        CurrentPage = AppPage.Editor;
        Editor.RequestFocusMacroName();
        SetStatus($"Recorded {steps.Count:N0} steps into \"{macro.Name}\". Type a name and press Enter to save, or press Play.", StatusKind.Success);
    }

    /// <summary>
    /// The Manual's "➕ Load Example into Editor": adds the recipe's blueprint to the macro open in the editor (after the
    /// selected step; one Undo step) and switches to the Editor. With no macro open, a new empty macro named after the
    /// recipe is created first.
    /// </summary>
    private async Task LoadRecipeAsync(MacroBlueprint blueprint)
    {
        var info = MacroBlueprints.Info(blueprint);
        if (!Editor.HasMacro)
        {
            var macro = MacroFactory.CreateNew(Library.Names, $"{info.Title} (example)");
            macro.Description = $"Example from the Manual: {info.Shape}.";
            try
            {
                await _repository.SaveAsync(macro);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Error("Creating a macro for the example failed.", ex);
                _dialogs.ShowError("Could not create a macro for the example", ex.Message);
                return;
            }

            Library.Upsert(MacroJson.Clone(macro));
            Editor.Load(macro);
        }

        CurrentPage = AppPage.Editor;
        if (Editor.AddBlueprint(blueprint))
            _log.Info($"📚 Loaded the \"{info.Title}\" example into \"{Editor.Name}\".");
    }

    private async Task OpenInEditorAsync(Guid id)
    {
        if (Editor.HasMacro && Editor.MacroId == id)
        {
            CurrentPage = AppPage.Editor;
            return;
        }

        if (!await Editor.ConfirmLeaveAsync()) return;

        var macro = Library.GetSaved(id);
        if (macro is null)
        {
            SetStatus("That macro no longer exists. Try Reload.", StatusKind.Warning);
            return;
        }

        Editor.Load(macro);
        CurrentPage = AppPage.Editor;
        StatusMessage = $"Editing \"{macro.Name}\".";
        StatusKind = StatusKind.Info;
    }

    private void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        SmartMacro.Core.Vision.HardwareAccelerationInfo.Configure(
            settings.ComputeEngineMode,
            settings.VisionMemoryBudgetMb);
        OnPropertyChanged(nameof(ShowLogPanel));
        OnPropertyChanged(nameof(ShowMiniOverlay));
        OnPropertyChanged(nameof(MinimizeWhilePlaying));
        OnPropertyChanged(nameof(ShowOverlaysInObs));
        OnPropertyChanged(nameof(RecordingMode));
        OnPropertyChanged(nameof(RecordingFidelity));
        OnPropertyChanged(nameof(IsSidebarPinned));
        OnPropertyChanged(nameof(HardwareAccelerationStatus));
        OnPropertyChanged(nameof(HardwareBadgeText));
        Automation?.RefreshShortcuts();
        SettingsApplied?.Invoke(this, settings);
        NotifyHotkeysChanged(force: true);
        _windowMonitor?.RequestRefresh();
    }

    /// <summary>Raised when settings are loaded or saved (the app re-registers global shortcuts).</summary>
    public event EventHandler<AppSettings>? SettingsApplied;

    public string VersionText => $"{Services.AppInfo.Name} {Services.AppInfo.Version}";

    public string OwnerText => $"by {Services.AppInfo.Owner}";

    /// <summary>Live SIMD &amp; CPU worker telemetry shown in the Studio status bar.</summary>
    public string HardwareAccelerationStatus => SmartMacro.Core.Vision.HardwareAccelerationInfo.StatusText;

    /// <summary>Short badge for the Studio status bar (e.g. "GPU+AVX2 · 4w · 256MB").</summary>
    public string HardwareBadgeText
    {
        get
        {
            var workers = SmartMacro.Core.Vision.HardwareAccelerationInfo.MaxVisionWorkers;
            var simd = SmartMacro.Core.Vision.HardwareAccelerationInfo.IsAvx2Supported
                ? "AVX2"
                : SmartMacro.Core.Vision.HardwareAccelerationInfo.IsSse2Supported
                    ? "SSE2"
                    : "CPU";
            var engine = SmartMacro.Core.Vision.HardwareAccelerationInfo.IsGpuActive ? "GPU+" : "";
            var ramMb = SmartMacro.Core.Vision.HardwareAccelerationInfo.MemoryLimitMb;
            return $"⚡ {engine}{simd} · {workers}w · {ramMb}MB";
        }
    }

    /// <summary>Plain-text system info for bug reports (no macro contents, no screenshots).</summary>
    private static string BuildDiagnostics(AppPaths paths, IOcrService ocr, ILogService log)
    {
        var screen = ocr.GetVirtualScreen();
        var text = new System.Text.StringBuilder()
            .AppendLine($"{Services.AppInfo.Name} {Services.AppInfo.Version}")
            .AppendLine($"Windows: {System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})")
            .AppendLine($".NET: {Environment.Version}, 64-bit process: {Environment.Is64BitProcess}")
            .AppendLine($"Screen area (all monitors): {screen}")
            .AppendLine($"OCR: {ocr.StatusText}")
            .AppendLine($"Data folder: {paths.RootDirectory}")
            .AppendLine()
            .AppendLine("Recent log:");
        foreach (var entry in log.GetRecent().TakeLast(40))
            text.Append(entry.Timestamp.ToLocalTime().ToString("HH:mm:ss")).Append(' ').Append(entry.Level).Append(": ").Append(entry.Message)
                .AppendLine(entry.Details is null ? "" : $" ({entry.Details})");
        if (CrashReporter.GetLatestCrashSummary(paths.LogsDirectory) is { } latestCrash)
            text.AppendLine().AppendLine(latestCrash);
        return text.ToString();
    }

    private const string BackupFileFilter = "SmartMacro Studio Backup (*.smbackup;*.zip)|*.smbackup;*.zip|All files (*.*)|*.*";

    private async Task ExportStudioBackupAsync()
    {
        var path = _dialogs.PickSaveFile("Export Full Studio Backup", BackupFileFilter, StudioBackupService.DefaultBackupFileName());
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            var macros = Library.Items.Select(i => MacroJson.Clone(i.Macro)).ToList();
            var manifest = await StudioBackupService.CreateBackupAsync(path, macros, _settings, Services.AppInfo.Version);
            SetStatus($"📦 Exported full Studio backup ({manifest.MacroCount} macro(s) + settings) to {path}.", StatusKind.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("Could not export Studio backup.", ex);
            _dialogs.ShowError("Backup export failed", ex.Message);
        }
    }

    private async Task RestoreStudioBackupAsync()
    {
        var files = _dialogs.PickFilesToOpen("Restore Studio Backup (.smbackup / .zip)", BackupFileFilter);
        if (files is null || files.Length == 0 || string.IsNullOrWhiteSpace(files[0])) return;

        var file = files[0];
        try
        {
            var existingIds = Library.Items.Select(i => i.Id).ToList();
            var result = await StudioBackupService.ReadBackupAsync(file, Library.Names, existingIds);

            foreach (var macro in result.RestoredMacros)
            {
                await _repository.SaveAsync(macro);
                Library.Upsert(MacroJson.Clone(macro));
            }

            if (result.RestoredSettings is not null
                && _dialogs.Confirm("Restore Studio Settings?",
                    "This backup archive also includes Studio Settings (theme, hotkeys, and defaults).\n\nWould you like to restore those settings as well?"))
            {
                await Settings.ApplyRestoredSettingsAsync(result.RestoredSettings);
            }

            Editor.RefreshSubMacroLinks();
            RefreshTriggerCapabilities();
            NotifyHotkeysChanged(force: true);
            _windowMonitor?.RequestRefresh();
            var msg = $"📥 Restored {result.RestoredMacros.Count} macro(s) from {Path.GetFileName(file)}.";
            if (result.SkippedFiles.Count > 0)
                msg += $" ({result.SkippedFiles.Count} entry/entries skipped.)";
            SetStatus(msg, result.SkippedFiles.Count == 0 ? StatusKind.Success : StatusKind.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _log.Error("Could not restore Studio backup.", ex);
            _dialogs.ShowError("Backup restore failed", ex.Message);
        }
    }
}
