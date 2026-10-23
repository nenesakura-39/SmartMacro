using System.Windows.Threading;
using SmartMacro.Core.Logging;
using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Recording;
using SmartMacro.Core.Storage;
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

    private AppPage _currentPage = AppPage.Library;
    private string _statusMessage = "Starting…";
    private StatusKind _statusKind = StatusKind.Info;
    private AppSettings _settings = new();
    private bool _isInitialized;

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

        Logs = new LogViewModel(log, dispatcher);

        Editor = new EditorViewModel(
            repository, log, dialogs, screen,
            defaultDelayMs: () => _settings.DefaultDelayMs,
            onSaved: saved => Library!.Upsert(saved),
            setStatus: SetStatus,
            goToLibrary: () => CurrentPage = AppPage.Library,
            findTextTools: automation.FindTextTools,
            visionTools: automation.VisionTools);

        Library = new LibraryViewModel(
            repository, log, dialogs,
            confirmBeforeDelete: () => _settings.ConfirmBeforeDelete,
            openInEditor: OpenInEditorAsync,
            afterDelete: id => Editor.OnMacroDeleted(id),
            setStatus: SetStatus,
            playMacro: macro => Automation!.PlayMacroAsync(macro),
            canPlay: () => Automation?.IsIdle ?? false);

        Settings = new SettingsViewModel(settingsStore, log, dialogs, paths, ApplySettings, SetStatus,
            automation.Ocr.StatusText, automation.Ocr.IsAvailable, () => BuildDiagnostics(paths, automation.Ocr, log));
        ShowLogsCommand = new RelayCommand(() => CurrentPage = AppPage.Logs);

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
                SetStatus = SetStatus,
            });

        Editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(EditorViewModel.IsDirty) or nameof(EditorViewModel.Name) or "")
                OnPropertyChanged(nameof(WindowTitle));
            if (e.PropertyName is nameof(EditorViewModel.HasMacro) or nameof(EditorViewModel.Name)
                or nameof(EditorViewModel.RepeatCount) or nameof(EditorViewModel.Speed) or "")
                Automation.RefreshTarget();

            // Live speed: changing the speed while this macro plays applies from its next step.
            if (e.PropertyName == nameof(EditorViewModel.Speed) && Automation.IsPlaying)
                Automation.SetLiveSpeed(Editor.MacroId, Editor.Speed);
        };

        Library.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LibraryViewModel.SelectedItem) or "")
                Automation.RefreshTarget();
        };

        // Live step highlight: the editor marks (and scrolls to) the step that is running.
        Automation.RunningStepChanged += (_, stepId) => Editor.SetRunningStep(stepId);

        // "▶ Run step" / "⏭ Play from here" in the editor play through the same automation (overlay, Esc stop, failsafe).
        Editor.RunSingleStepHandler = (macro, index) => Automation.RunSingleStepAsync(macro, index);
        Editor.PlayFromStepHandler = (macro, index) => Automation.PlayFromStepAsync(macro, index);
        Editor.CanRunSteps = () => Automation.IsIdle;
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
    public AutomationViewModel Automation { get; }
    public RelayCommand ShowLogsCommand { get; }

    /// <summary>The settings currently in effect (last saved).</summary>
    public AppSettings CurrentSettings => _settings;

    public AppPage CurrentPage
    {
        get => _currentPage;
        set
        {
            if (SetProperty(ref _currentPage, value))
                OnPropertyChanged(nameof(CurrentPageViewModel));
        }
    }

    public object CurrentPageViewModel => _currentPage switch
    {
        AppPage.Editor => Editor,
        AppPage.Logs => Logs,
        AppPage.Settings => Settings,
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
        macro.Description = "Recorded with SmartMacro.";

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
        SetStatus($"Recorded {steps.Count:N0} steps into \"{macro.Name}\". Review them, then press Play.", StatusKind.Success);
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
        OnPropertyChanged(nameof(RecordingMode));
        Automation?.RefreshShortcuts();
        SettingsApplied?.Invoke(this, settings);
    }

    /// <summary>Raised when settings are loaded or saved (the app re-registers global shortcuts).</summary>
    public event EventHandler<AppSettings>? SettingsApplied;

    public string VersionText => $"{Services.AppInfo.Name} {Services.AppInfo.Version}";

    public string OwnerText => $"by {Services.AppInfo.Owner}";

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
        return text.ToString();
    }
}
