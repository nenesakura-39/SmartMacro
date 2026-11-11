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
    private readonly DispatcherTimer _triggerTimer;

    private AppPage _currentPage = AppPage.Library;
    private string _statusMessage = "Starting…";
    private StatusKind _statusKind = StatusKind.Info;
    private AppSettings _settings = new();
    private bool _isInitialized;

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
        AutomationServices automation)
    {
        _log = log;
        _dialogs = dialogs;
        _repository = repository;
        _screen = screen;

        _triggerTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _triggerTimer.Tick += (_, _) => _ = EvaluateBackgroundTriggersAsync();
        ToggleTriggersArmedCommand = new RelayCommand(() => AreTriggersArmed = !AreTriggersArmed);

        Logs = new LogViewModel(log, dispatcher);

        Editor = new EditorViewModel(
            repository, log, dialogs, screen,
            defaultDelayMs: () => _settings.DefaultDelayMs,
            onSaved: saved =>
            {
                Library!.Upsert(saved);
                MacroHotkeysChanged?.Invoke(this, EventArgs.Empty);
            },
            setStatus: SetStatus,
            goToLibrary: () => CurrentPage = AppPage.Library,
            findTextTools: automation.FindTextTools,
            visionTools: automation.VisionTools);

        Library = new LibraryViewModel(
            repository, log, dialogs,
            confirmBeforeDelete: () => _settings.ConfirmBeforeDelete,
            openInEditor: OpenInEditorAsync,
            afterDelete: id =>
            {
                Editor.OnMacroDeleted(id);
                MacroHotkeysChanged?.Invoke(this, EventArgs.Empty);
            },
            setStatus: SetStatus,
            playMacro: macro => Automation!.PlayMacroAsync(macro),
            canPlay: () => Automation?.IsIdle ?? false);

        Settings = new SettingsViewModel(settingsStore, log, dialogs, paths, ApplySettings, SetStatus,
            automation.Ocr.StatusText, automation.Ocr.IsAvailable, () => BuildDiagnostics(paths, automation.Ocr, log))
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
            if (e.PropertyName is nameof(LibraryViewModel.SelectedItem) or nameof(LibraryViewModel.CountText) or "")
            {
                Automation.RefreshTarget();
                if (Editor.HasMacro) Editor.RefreshSubMacroLinks();
                if (e.PropertyName is nameof(LibraryViewModel.CountText) or "")
                    MacroHotkeysChanged?.Invoke(this, EventArgs.Empty);
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

        // v1.8 Studio X Interactive Debugger: breakpoints (F9) and single-step execution (F10).
        automation.Engine.HasBreakpoint = id => Editor.HasStepBreakpoint(id);
        Editor.StepOverHandler = () => automation.Engine.StepOver();

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
        MacroHotkeysChanged?.Invoke(this, EventArgs.Empty);
        _triggerTimer.Start();
        await EvaluateBackgroundTriggersAsync();
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
            MacroHotkeysChanged?.Invoke(this, EventArgs.Empty);
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

    private IReadOnlyList<Macro> GetArmedLibraryMacros(string? fg)
    {
        var all = Library.Items.Select(i => i.Macro).ToList();
        var manualFilter = Library.SelectedProfileFilter;
        var isManual = !string.IsNullOrWhiteSpace(manualFilter)
            && !string.Equals(manualFilter, ProfileRouter.AllProfilesLabel, StringComparison.OrdinalIgnoreCase);
        var activeProfile = ProfileRouter.ResolveActiveProfile(
            _settings.ProfileRules,
            all,
            fg,
            manualFilter,
            _settings.AutoSwitchProfileByWindow);
        ActiveProfileName = activeProfile;
        return ProfileRouter.FilterArmedMacros(all, activeProfile, fg, _settings.AutoSwitchProfileByWindow, isManual);
    }

    /// <summary>Returns all distinct valid hotkeys used by enabled macro triggers across the library (empty when disarmed).</summary>
    public IReadOnlyList<HotkeyBinding> GetActiveTriggerHotkeys()
    {
        if (!TriggerEngine.IsArmed) return Array.Empty<HotkeyBinding>();
        var list = new List<HotkeyBinding>();
        foreach (var item in Library.Items)
        {
            if (item.Macro.Triggers is not { Count: > 0 } triggers) continue;
            foreach (var trigger in triggers)
            {
                if (!trigger.Enabled || trigger.Kind != TriggerKind.Hotkey || trigger.Hotkey is not { VirtualKey: > 0 } hk)
                    continue;
                if (!list.Contains(hk))
                    list.Add(hk);
            }
        }
        return list;
    }

    public void HandleMacroHotkeyDown(HotkeyBinding binding)
    {
        if (Automation.IsRecording) return;
        var fg = _screen.GetForegroundWindowTitle();
        var macros = GetArmedLibraryMacros(fg);
        var decision = TriggerEngine.EvaluateHotkeyDown(
            macros,
            binding,
            fg,
            Automation.RunningMacroId,
            DateTimeOffset.UtcNow);
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

    public async Task EvaluateBackgroundTriggersAsync()
    {
        if (!TriggerEngine.IsArmed || Automation.IsRecording) return;
        if (Library.Items.Count == 0) return;

        var fg = _screen.GetForegroundWindowTitle();
        var macros = GetArmedLibraryMacros(fg);
        if (macros.Count == 0) return;

        IReadOnlyList<string> openWindows = _screen is Core.Vision.IScreenLayout layout
            ? layout.GetWindows().Select(w => w.Title).Where(t => !string.IsNullOrWhiteSpace(t)).ToList()
            : Array.Empty<string>();

        var decisions = TriggerEngine.EvaluateTick(
            macros,
            DateTimeOffset.Now,
            openWindows,
            fg,
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
        OnPropertyChanged(nameof(ShowLogPanel));
        OnPropertyChanged(nameof(ShowMiniOverlay));
        OnPropertyChanged(nameof(MinimizeWhilePlaying));
        OnPropertyChanged(nameof(ShowOverlaysInObs));
        OnPropertyChanged(nameof(RecordingMode));
        OnPropertyChanged(nameof(RecordingFidelity));
        OnPropertyChanged(nameof(IsSidebarPinned));
        Automation?.RefreshShortcuts();
        SettingsApplied?.Invoke(this, settings);
    }

    /// <summary>Raised when settings are loaded or saved (the app re-registers global shortcuts).</summary>
    public event EventHandler<AppSettings>? SettingsApplied;

    public string VersionText => $"{Services.AppInfo.Name} {Services.AppInfo.Version}";

    public string OwnerText => $"by {Services.AppInfo.Owner}";

    /// <summary>Live SIMD &amp; CPU worker telemetry shown in the Studio status bar.</summary>
    public string HardwareAccelerationStatus => SmartMacro.Core.Vision.HardwareAccelerationInfo.StatusText;

    /// <summary>Short badge for the Studio status bar (e.g. "AVX2 · 4w").</summary>
    public string HardwareBadgeText =>
        SmartMacro.Core.Vision.HardwareAccelerationInfo.IsAvx2Supported
            ? $"⚡ AVX2 · {SmartMacro.Core.Vision.HardwareAccelerationInfo.MaxVisionWorkers}w"
            : SmartMacro.Core.Vision.HardwareAccelerationInfo.IsSse2Supported
                ? $"⚡ SSE2 · {SmartMacro.Core.Vision.HardwareAccelerationInfo.MaxVisionWorkers}w"
                : $"CPU · {SmartMacro.Core.Vision.HardwareAccelerationInfo.MaxVisionWorkers}w";

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
            MacroHotkeysChanged?.Invoke(this, EventArgs.Empty);
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
