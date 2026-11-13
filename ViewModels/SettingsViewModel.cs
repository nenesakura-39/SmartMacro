using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using SmartMacro.Core.Logging;
using SmartMacro.Core.Models;
using SmartMacro.Core.Network;
using SmartMacro.Core.Recording;
using SmartMacro.Core.Storage;
using SmartMacro.Mvvm;
using SmartMacro.Services;
using SmartMacro.ViewModels.Steps;

namespace SmartMacro.ViewModels;

/// <summary>Snapshot of an open desktop window offered in the Profile Routing picker.</summary>
public sealed record OpenWindowChoice(string Title, string ProcessName)
{
    public string DisplayLabel =>
        string.IsNullOrWhiteSpace(ProcessName) ? Title : $"{Title}  [{ProcessName}]";
}

/// <summary>Editable row ViewModel for one <see cref="MacroProfileRule"/> in Settings.</summary>
public sealed class ProfileRuleItemViewModel : ObservableObject
{
    private readonly Action _changed;
    private bool _enabled;
    private string _profileName;
    private string _windowTitleMatch;
    private string _processNameMatch;
    private int _priorityNumber;
    private bool _canMoveUp;
    private bool _canMoveDown;

    public ProfileRuleItemViewModel(MacroProfileRule model, Action changed)
    {
        ArgumentNullException.ThrowIfNull(model);
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
        Id = model.Id == Guid.Empty ? Guid.NewGuid() : model.Id;
        _enabled = model.Enabled;
        _profileName = model.ProfileName ?? string.Empty;
        _windowTitleMatch = model.WindowTitleMatch ?? string.Empty;
        _processNameMatch = model.ProcessNameMatch ?? string.Empty;
    }

    public Guid Id { get; }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!SetProperty(ref _enabled, value)) return;
            OnPropertyChanged(nameof(SummaryText));
            _changed();
        }
    }

    public string ProfileName
    {
        get => _profileName;
        set
        {
            if (!SetProperty(ref _profileName, value ?? string.Empty)) return;
            NotifyRuleChanged();
        }
    }

    public string WindowTitleMatch
    {
        get => _windowTitleMatch;
        set
        {
            if (!SetProperty(ref _windowTitleMatch, value ?? string.Empty)) return;
            NotifyRuleChanged();
        }
    }

    public string ProcessNameMatch
    {
        get => _processNameMatch;
        set
        {
            if (!SetProperty(ref _processNameMatch, value ?? string.Empty)) return;
            NotifyRuleChanged();
        }
    }

    public int PriorityNumber
    {
        get => _priorityNumber;
        private set
        {
            if (SetProperty(ref _priorityNumber, value))
                OnPropertyChanged(nameof(PriorityLabel));
        }
    }

    public string PriorityLabel => $"#{_priorityNumber}";

    public bool CanMoveUp
    {
        get => _canMoveUp;
        private set => SetProperty(ref _canMoveUp, value);
    }

    public bool CanMoveDown
    {
        get => _canMoveDown;
        private set => SetProperty(ref _canMoveDown, value);
    }

    public string? ValidationError => BuildDraftModel().Validate();

    public bool HasValidationError => !string.IsNullOrEmpty(ValidationError);

    /// <summary>Write-only helper for the quick-pick existing-profile ComboBox.</summary>
    public string? SelectedPresetProfile
    {
        get => null;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
                ProfileName = value;
            OnPropertyChanged();
        }
    }

    public string SummaryText
    {
        get
        {
            var title = WindowTitleMatch.Trim();
            var proc = ProcessNameMatch.Trim();
            var profile = string.IsNullOrWhiteSpace(ProfileName) ? "(unnamed profile)" : ProfileName.Trim();
            var filter = (title.Length > 0, proc.Length > 0) switch
            {
                (true, true) => $"\"{title}\" [{proc}]",
                (true, false) => $"Title: \"{title}\"",
                (false, true) => $"Process: {proc}",
                _ => "Any window (incomplete filter)",
            };
            var statePrefix = Enabled ? string.Empty : "[Paused] ";
            return $"{statePrefix}{filter} → {profile}";
        }
    }

    public void UpdatePosition(int index, int count)
    {
        PriorityNumber = index + 1;
        CanMoveUp = index > 0;
        CanMoveDown = index >= 0 && index < count - 1;
    }

    public MacroProfileRule ToModel()
    {
        var model = BuildDraftModel();
        model.Sanitize();
        return model;
    }

    private MacroProfileRule BuildDraftModel() => new()
    {
        Id = Id,
        Enabled = Enabled,
        ProfileName = ProfileName,
        WindowTitleMatch = WindowTitleMatch,
        ProcessNameMatch = string.IsNullOrWhiteSpace(ProcessNameMatch) ? null : ProcessNameMatch,
    };

    private void NotifyRuleChanged()
    {
        OnPropertyChanged(nameof(ValidationError));
        OnPropertyChanged(nameof(HasValidationError));
        OnPropertyChanged(nameof(SummaryText));
        _changed();
    }
}

/// <summary>Edits a copy of the settings; nothing is applied until Save.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _store;
    private readonly ILogService _log;
    private readonly IDialogService _dialogs;
    private readonly Action<AppSettings> _apply;
    private readonly Action<string, StatusKind> _setStatus;

    private AppSettings _saved = new();
    private bool _confirmBeforeDelete;
    private int _defaultDelayMs;
    private bool _showLogPanel;
    private bool _minimizeDuringRun;
    private bool _showRunOverlay;
    private bool _minimizeToTrayOnClose;
    private bool _startWithWindows;
    private bool _autoSwitchProfileByWindow = true;
    private bool _allowLocalWebhooks;
    private string _failureWebhookUrl = string.Empty;
    private string _updateManifestUrl = string.Empty;
    private string? _updateStatusMessage;
    private string? _failureWebhookTestResult;
    private bool _isCheckingUpdates;
    private bool _isTestingFailureWebhook;
    private bool _recordMouseMovement;
    private RecordingMode _recordingMode;
    private RecordingFidelity _recordingFidelity = RecordingFidelity.Exact1To1;
    private SmartMacro.Core.Vision.HardwareAccelerationMode _computeEngineMode = SmartMacro.Core.Vision.HardwareAccelerationMode.Auto;
    private int _visionMemoryBudgetMb = 256;
    private bool _useRelativeMouseDeltas;
    private bool _keepTapHoldTime;
    private bool _simplifyMousePaths;
    private bool _captureClickSnapshots = true;
    private bool _captureClickIntent = true;
    private bool _smartTimingOnRecord = true;
    private bool _coalesceTypedTextOnRecord = true;
    private bool _smoothMouseMoves;
    private StudioTheme _theme = StudioTheme.ObsidianNeon;
    private StudioAccent _accent = StudioAccent.NeonBrand;
    private StudioDensity _density = StudioDensity.Comfortable;
    private ExperienceLevel _experienceLevel = ExperienceLevel.Beginner;
    private bool _reduceMotion;
    private HotkeyBinding _recordHotkey = HotkeyBinding.DefaultRecord;
    private HotkeyBinding _playHotkey = HotkeyBinding.DefaultPlay;
    private HotkeyBinding _pauseHotkey = HotkeyBinding.DefaultPause;
    private bool _isDirty;
    private readonly Func<string> _buildDiagnostics;
    private readonly Func<(string? Title, string ProcessName)> _getForegroundContext;
    private readonly Func<IReadOnlyList<string>> _getLibraryProfiles;
    private readonly Func<IReadOnlyList<OpenWindowChoice>> _getOpenWindows;
    private readonly UpdateCheckService _updateChecker;
    private readonly WebhookDeliveryService _webhookClient;
    private OpenWindowChoice? _selectedOpenWindow;

    public SettingsViewModel(ISettingsStore store, ILogService log, IDialogService dialogs, AppPaths paths,
        Action<AppSettings> apply, Action<string, StatusKind> setStatus, string ocrStatus = "", bool ocrAvailable = false,
        Func<string>? buildDiagnostics = null,
        Func<(string? Title, string ProcessName)>? getForegroundContext = null,
        Func<IReadOnlyList<string>>? getLibraryProfiles = null,
        Func<IReadOnlyList<OpenWindowChoice>>? getOpenWindows = null,
        UpdateCheckService? updateChecker = null,
        WebhookDeliveryService? webhookClient = null)
    {
        _buildDiagnostics = buildDiagnostics ?? (() => $"{AppInfo.Name} {AppInfo.Version}");
        _getForegroundContext = getForegroundContext ?? (() => (null, string.Empty));
        _getLibraryProfiles = getLibraryProfiles ?? (() => Array.Empty<string>());
        _getOpenWindows = getOpenWindows ?? (() => Array.Empty<OpenWindowChoice>());
        _updateChecker = updateChecker ?? UpdateCheckService.Shared;
        _webhookClient = webhookClient ?? WebhookDeliveryService.Shared;
        OcrStatus = ocrStatus;
        IsOcrAvailable = ocrAvailable;
        _store = store;
        _log = log;
        _dialogs = dialogs;
        _apply = apply;
        _setStatus = setStatus;
        DataFolder = paths.RootDirectory;

        SaveCommand = new AsyncRelayCommand(SaveAsync, ex => _log.Error("Saving settings failed.", ex), () => IsDirty && IsValid);
        RevertCommand = new RelayCommand(() => LoadFrom(_saved), () => IsDirty);
        ResetCommand = new RelayCommand(() => { LoadFrom(new AppSettings()); IsDirty = true; });
        OpenDataFolderCommand = new RelayCommand(OpenDataFolder);
        ResetShortcutsCommand = new RelayCommand(() =>
        {
            RecordHotkey = HotkeyBinding.DefaultRecord;
            PlayHotkey = HotkeyBinding.DefaultPlay;
            PauseHotkey = HotkeyBinding.DefaultPause;
        });
        CopyDiagnosticsCommand = new RelayCommand(CopyDiagnostics);
        ExportBackupCommand = new AsyncRelayCommand(ExportBackupAsync, ex => _log.Error("Backup export failed.", ex));
        RestoreBackupCommand = new AsyncRelayCommand(RestoreBackupAsync, ex => _log.Error("Backup restore failed.", ex));
        CheckForUpdatesCommand = new AsyncRelayCommand(
            CheckForUpdatesAsync,
            ex => _log.Warning("Update check failed.", ex),
            () => !_isCheckingUpdates && UpdateManifestUrlError is null);
        TestFailureWebhookCommand = new AsyncRelayCommand(
            TestFailureWebhookAsync,
            ex => _log.Warning("Failure webhook test failed.", ex),
            () => !_isTestingFailureWebhook && !string.IsNullOrWhiteSpace(_failureWebhookUrl) && FailureWebhookError is null);
        AddProfileRuleCommand = new RelayCommand(AddBlankProfileRule);
        AddRuleFromWindowProcessCommand = new RelayCommand(
            () => AddRuleFromWindow(preferProcess: true),
            () => !string.IsNullOrWhiteSpace(SelectedOpenWindow?.ProcessName));
        AddRuleFromWindowTitleCommand = new RelayCommand(
            () => AddRuleFromWindow(preferProcess: false),
            () => !string.IsNullOrWhiteSpace(SelectedOpenWindow?.Title));
        MoveRuleUpCommand = new RelayCommand(
            p => MoveRule(p as ProfileRuleItemViewModel, -1),
            p => p is ProfileRuleItemViewModel r && r.CanMoveUp);
        MoveRuleDownCommand = new RelayCommand(
            p => MoveRule(p as ProfileRuleItemViewModel, +1),
            p => p is ProfileRuleItemViewModel r && r.CanMoveDown);
        DeleteProfileRuleCommand = new RelayCommand(
            p => DeleteRule(p as ProfileRuleItemViewModel),
            p => p is ProfileRuleItemViewModel);
        RefreshWindowContextCommand = new AsyncRelayCommand(RefreshWindowContextAsync, ex => _log.Warning("Refreshing open windows failed.", ex));
        SetAccentCommand = new RelayCommand(p =>
        {
            if (p is StudioAccent accent) Accent = accent;
            else if (p is string text && Enum.TryParse<StudioAccent>(text, out var parsed)) Accent = parsed;
        });
    }

    public RelayCommand SetAccentCommand { get; }
    public AsyncRelayCommand ExportBackupCommand { get; }
    public AsyncRelayCommand RestoreBackupCommand { get; }
    public AsyncRelayCommand CheckForUpdatesCommand { get; }
    public AsyncRelayCommand TestFailureWebhookCommand { get; }
    public RelayCommand AddProfileRuleCommand { get; }
    public RelayCommand AddRuleFromWindowProcessCommand { get; }
    public RelayCommand AddRuleFromWindowTitleCommand { get; }
    public RelayCommand MoveRuleUpCommand { get; }
    public RelayCommand MoveRuleDownCommand { get; }
    public RelayCommand DeleteProfileRuleCommand { get; }
    public AsyncRelayCommand RefreshWindowContextCommand { get; }

    public ObservableCollection<ProfileRuleItemViewModel> ProfileRules { get; } = new();
    public ObservableCollection<OpenWindowChoice> OpenWindows { get; } = new();
    public ObservableCollection<string> KnownProfiles { get; } = new();

    /// <summary>Optional delegate wired by <see cref="MainViewModel"/> to export all library macros + settings into a <c>.smbackup</c> archive.</summary>
    public Func<Task>? ExportStudioBackupHandler { get; set; }

    /// <summary>Optional delegate wired by <see cref="MainViewModel"/> to restore a <c>.smbackup</c> archive.</summary>
    public Func<Task>? RestoreStudioBackupHandler { get; set; }

    // ---- Studio X Appearance & Motion ----
    public static IReadOnlyList<Choice<StudioTheme>> ThemeChoices { get; } = new[]
    {
        new Choice<StudioTheme>(StudioTheme.ObsidianNeon, "🌌 Obsidian Neon (Deep Space #060913 — Default)"),
        new Choice<StudioTheme>(StudioTheme.MidnightBlue, "🌠 Midnight Blue (High-Contrast Slate #0A1020)"),
    };

    public static IReadOnlyList<Choice<StudioAccent>> AccentChoices { get; } = new[]
    {
        new Choice<StudioAccent>(StudioAccent.NeonBrand, "⚡ Neon Brand (Sky & Violet)"),
        new Choice<StudioAccent>(StudioAccent.ElectricBlue, "💙 Electric Blue (#3B82F6)"),
        new Choice<StudioAccent>(StudioAccent.CyberViolet, "💜 Cyber Violet (#8B5CF6)"),
        new Choice<StudioAccent>(StudioAccent.EmeraldPulse, "💚 Emerald Pulse (#10B981)"),
        new Choice<StudioAccent>(StudioAccent.RoseQuartz, "💗 Rose Quartz (#F43F5E)"),
    };

    public static IReadOnlyList<Choice<StudioDensity>> DensityChoices { get; } = new[]
    {
        new Choice<StudioDensity>(StudioDensity.Comfortable, "Comfortable (Studio Default)"),
        new Choice<StudioDensity>(StudioDensity.Compact, "Compact (High-Density Pro)"),
    };

    public static IReadOnlyList<Choice<ExperienceLevel>> ExperienceLevelChoices { get; } = new[]
    {
        new Choice<ExperienceLevel>(ExperienceLevel.Beginner, "✨ Beginner (Guided Scenarios & 1-Click Auto-Tune)"),
        new Choice<ExperienceLevel>(ExperienceLevel.Advanced, "🔬 Advanced (All Vision 2.0 & Engine Controls)"),
    };

    public IReadOnlyList<Choice<StudioTheme>> Themes => ThemeChoices;
    public IReadOnlyList<Choice<StudioAccent>> Accents => AccentChoices;
    public IReadOnlyList<Choice<StudioDensity>> Densities => DensityChoices;
    public IReadOnlyList<Choice<ExperienceLevel>> ExperienceLevels => ExperienceLevelChoices;

    public ExperienceLevel ExperienceLevel
    {
        get => _experienceLevel;
        set
        {
            if (!SetProperty(ref _experienceLevel, value)) return;
            IsDirty = true;
        }
    }

    public StudioTheme Theme
    {
        get => _theme;
        set
        {
            if (!SetProperty(ref _theme, value)) return;
            IsDirty = true;
            PreviewAppearance();
        }
    }

    public StudioAccent Accent
    {
        get => _accent;
        set
        {
            if (!SetProperty(ref _accent, value)) return;
            IsDirty = true;
            PreviewAppearance();
        }
    }

    public StudioDensity Density
    {
        get => _density;
        set
        {
            if (!SetProperty(ref _density, value)) return;
            IsDirty = true;
            PreviewAppearance();
        }
    }

    public bool ReduceMotion
    {
        get => _reduceMotion;
        set
        {
            if (!SetProperty(ref _reduceMotion, value)) return;
            IsDirty = true;
            PreviewAppearance();
        }
    }

    private void PreviewAppearance()
    {
        var preview = new AppSettings
        {
            Theme = _theme,
            Accent = _accent,
            Density = _density,
            ReduceMotion = _reduceMotion,
        };
        if (_store is ISettingsPersistence persistence)
            persistence.ApplyAppearance(preview);
        else
            ThemeManager.Apply(preview);
    }

    // ---- About ----
    public string AppName => AppInfo.Name;
    public string VersionText => $"Version {AppInfo.Version}";
    public string OwnerText => $"Owner & developer: {AppInfo.Owner}";
    public string Tagline => AppInfo.Tagline;

    public RelayCommand ResetShortcutsCommand { get; }
    public RelayCommand CopyDiagnosticsCommand { get; }

    // ---- Shortcuts ----
    public HotkeyBinding RecordHotkey
    {
        get => _recordHotkey;
        set { if (SetProperty(ref _recordHotkey, value)) ShortcutsChanged(); }
    }

    public HotkeyBinding PlayHotkey
    {
        get => _playHotkey;
        set { if (SetProperty(ref _playHotkey, value)) ShortcutsChanged(); }
    }

    public HotkeyBinding PauseHotkey
    {
        get => _pauseHotkey;
        set { if (SetProperty(ref _pauseHotkey, value)) ShortcutsChanged(); }
    }

    public string RecordHotkeyText => _recordHotkey.ToString();
    public string PlayHotkeyText => _playHotkey.ToString();
    public string PauseHotkeyText => _pauseHotkey.ToString();

    /// <summary>Problem with the shortcuts (invalid key, duplicates), or null.</summary>
    public string? HotkeyError => HotkeyBinding.ProblemWithSet(_recordHotkey, _playHotkey, _pauseHotkey);

    /// <summary>Called by the view when the user presses a key combination in a shortcut box.</summary>
    public void CaptureHotkey(string which, HotkeyBinding binding)
    {
        switch (which)
        {
            case "Record":
                if (_playHotkey == binding && _recordHotkey != binding) _playHotkey = _recordHotkey;
                else if (_pauseHotkey == binding && _recordHotkey != binding) _pauseHotkey = _recordHotkey;
                RecordHotkey = binding;
                break;
            case "Play":
                if (_recordHotkey == binding && _playHotkey != binding) _recordHotkey = _playHotkey;
                else if (_pauseHotkey == binding && _playHotkey != binding) _pauseHotkey = _playHotkey;
                PlayHotkey = binding;
                break;
            case "Pause":
                if (_recordHotkey == binding && _pauseHotkey != binding) _recordHotkey = _pauseHotkey;
                else if (_playHotkey == binding && _pauseHotkey != binding) _playHotkey = _pauseHotkey;
                PauseHotkey = binding;
                break;
        }
    }

    private void ShortcutsChanged()
    {
        IsDirty = true;
        OnPropertyChanged(nameof(RecordHotkeyText));
        OnPropertyChanged(nameof(PlayHotkeyText));
        OnPropertyChanged(nameof(PauseHotkeyText));
        OnPropertyChanged(nameof(HotkeyError));
        OnPropertyChanged(nameof(IsValid));
    }

    private void CopyDiagnostics()
    {
        try
        {
            System.Windows.Clipboard.SetText(_buildDiagnostics());
            _setStatus("Diagnostics copied. Paste them into your bug report.", StatusKind.Success);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException)
        {
            _setStatus("The clipboard is busy; try Copy diagnostics again.", StatusKind.Warning);
        }
    }

    public AppSettings Saved => _saved;

    public string DataFolder { get; }

    /// <summary>Whether Windows text recognition works on this PC (and which language it uses).</summary>
    public string OcrStatus { get; }

    public bool IsOcrAvailable { get; }

    /// <summary>Hardware SIMD, GPU &amp; RAM telemetry shown on the Settings page.</summary>
    public string HardwareAccelerationStatus =>
        SmartMacro.Core.Vision.HardwareAccelerationInfo.StatusText;

    public static IReadOnlyList<Choice<SmartMacro.Core.Vision.HardwareAccelerationMode>> ComputeEngineModeChoices { get; } =
    [
        new(SmartMacro.Core.Vision.HardwareAccelerationMode.Auto, "Auto (GPU if available + CPU SIMD)"),
        new(SmartMacro.Core.Vision.HardwareAccelerationMode.CpuOnly, "CPU Only (Zero-GPU · Low-End PC Safe)"),
        new(SmartMacro.Core.Vision.HardwareAccelerationMode.GpuAccelerated, "GPU Accelerated (D3D11 DXGI + SIMD)")
    ];

    public IReadOnlyList<Choice<SmartMacro.Core.Vision.HardwareAccelerationMode>> ComputeEngineModes => ComputeEngineModeChoices;

    public SmartMacro.Core.Vision.HardwareAccelerationMode ComputeEngineMode
    {
        get => _computeEngineMode;
        set
        {
            if (!SetProperty(ref _computeEngineMode, value)) return;
            IsDirty = true;
            OnPropertyChanged(nameof(HardwareAccelerationStatus));
        }
    }

    public static IReadOnlyList<Choice<int>> VisionMemoryBudgetChoices { get; } =
    [
        new(64, "64 MB (Low-End PC / Light RAM)"),
        new(128, "128 MB (Balanced)"),
        new(256, "256 MB (Standard · Default)"),
        new(512, "512 MB (High-Performance)"),
        new(1024, "1024 MB (Max Template Cache)")
    ];

    public IReadOnlyList<Choice<int>> VisionMemoryBudgets => VisionMemoryBudgetChoices;

    public int VisionMemoryBudgetMb
    {
        get => _visionMemoryBudgetMb;
        set
        {
            if (!SetProperty(ref _visionMemoryBudgetMb, value)) return;
            IsDirty = true;
            OnPropertyChanged(nameof(HardwareAccelerationStatus));
        }
    }

    public AsyncRelayCommand SaveCommand { get; }
    public RelayCommand RevertCommand { get; }
    public RelayCommand ResetCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }

    public bool ConfirmBeforeDelete
    {
        get => _confirmBeforeDelete;
        set { if (SetProperty(ref _confirmBeforeDelete, value)) IsDirty = true; }
    }

    public int DefaultDelayMs
    {
        get => _defaultDelayMs;
        set
        {
            if (SetProperty(ref _defaultDelayMs, value))
            {
                IsDirty = true;
                OnPropertyChanged(nameof(IsValid));
                OnPropertyChanged(nameof(DelayError));
            }
        }
    }

    public bool ShowLogPanel
    {
        get => _showLogPanel;
        set { if (SetProperty(ref _showLogPanel, value)) IsDirty = true; }
    }

    public bool MinimizeDuringRun
    {
        get => _minimizeDuringRun;
        set { if (SetProperty(ref _minimizeDuringRun, value)) IsDirty = true; }
    }

    public bool ShowRunOverlay
    {
        get => _showRunOverlay;
        set { if (SetProperty(ref _showRunOverlay, value)) IsDirty = true; }
    }

    public bool MinimizeToTrayOnClose
    {
        get => _minimizeToTrayOnClose;
        set { if (SetProperty(ref _minimizeToTrayOnClose, value)) IsDirty = true; }
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set { if (SetProperty(ref _startWithWindows, value)) IsDirty = true; }
    }

    public bool AutoSwitchProfileByWindow
    {
        get => _autoSwitchProfileByWindow;
        set
        {
            if (!SetProperty(ref _autoSwitchProfileByWindow, value)) return;
            IsDirty = true;
            NotifyLiveContextChanged();
        }
    }

    public bool HasProfileRules => ProfileRules.Count > 0;

    public bool HasKnownProfiles => KnownProfiles.Count > 0;

    public bool HasOpenWindows => OpenWindows.Count > 0;

    public OpenWindowChoice? SelectedOpenWindow
    {
        get => _selectedOpenWindow;
        set
        {
            if (!SetProperty(ref _selectedOpenWindow, value)) return;
            OnPropertyChanged(nameof(HasSelectedOpenWindow));
            OnPropertyChanged(nameof(SelectedWindowPreviewDisplay));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool HasSelectedOpenWindow => _selectedOpenWindow is not null;

    public string? RulesError => ProfileRules.Select(r => r.ValidationError).FirstOrDefault(e => e is not null);

    public bool HasRulesError => !string.IsNullOrEmpty(RulesError);

    public string LiveForegroundDisplay
    {
        get
        {
            var (title, proc) = _getForegroundContext();
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(proc))
                return "No external app captured yet (focus another window or pick one below)";
            if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(proc))
                return $"{title.Trim()}  [{proc.Trim()}]";
            return !string.IsNullOrWhiteSpace(title) ? title.Trim() : $"[{proc.Trim()}]";
        }
    }

    public string LiveResolvedProfileDisplay
    {
        get
        {
            var (title, proc) = _getForegroundContext();
            return DescribeRoutingOutcome(title, proc);
        }
    }

    public string SelectedWindowPreviewDisplay
    {
        get
        {
            if (_selectedOpenWindow is not { } win)
                return string.Empty;
            return $"Test match → {DescribeRoutingOutcome(win.Title, win.ProcessName)}";
        }
    }

    /// <summary>
    /// "🎥 Show overlays in OBS / Discord" (streamer mode). Applied and saved at once (like the editor's top-bar pill), so it
    /// doesn't wait for Save and isn't undone by Revert.
    /// </summary>
    public bool ShowOverlaysInObsAndRecording
    {
        get => _saved.ShowOverlaysInObsAndRecording;
        set
        {
            if (_saved.ShowOverlaysInObsAndRecording == value) return;
            _ = QuickSetAsync(s => s.ShowOverlaysInObsAndRecording = value);
        }
    }

    public bool RecordMouseMovement
    {
        get => _recordMouseMovement;
        set { if (SetProperty(ref _recordMouseMovement, value)) IsDirty = true; }
    }

    public static IReadOnlyList<Choice<RecordingMode>> ToolbarRecordingModes { get; } = new[]
    {
        new Choice<RecordingMode>(RecordingMode.Smart, "✨ Smart (Auto-detect UI & Game)"),
        new Choice<RecordingMode>(RecordingMode.Desktop, "🖥 Desktop & UI (Smooth cursor & clicks)"),
        new Choice<RecordingMode>(RecordingMode.Game, "🎮 3D Game / FPS (High-precision camera & WASD)"),
        new Choice<RecordingMode>(RecordingMode.Rhythm, "🎵 Exact 1:1 / Rhythm (Zero simplification, sub-ms lock)"),
    };

    public IReadOnlyList<Choice<RecordingMode>> RecordingModes => ToolbarRecordingModes;

    /// <summary>🎯 100% Exact 1:1 Replay / ✨ Smart Optimized / 🖱️ Clicks &amp; Keys Only (Settings and the editor's Record bar).</summary>
    public static IReadOnlyList<Choice<RecordingFidelity>> RecordingFidelityChoices { get; } =
        Enum.GetValues<RecordingFidelity>().Select(f => new Choice<RecordingFidelity>(f, RecordingFidelityPresets.Label(f))).ToArray();

    public IReadOnlyList<Choice<RecordingFidelity>> RecordingFidelities => RecordingFidelityChoices;

    public RecordingFidelity RecordingFidelity
    {
        get => _recordingFidelity;
        set
        {
            if (!SetProperty(ref _recordingFidelity, value)) return;
            IsDirty = true;
            OnPropertyChanged(nameof(RecordingFidelityHelp));
        }
    }

    public string RecordingFidelityHelp => RecordingFidelityPresets.Help(_recordingFidelity);

    /// <summary>"Use relative mouse deltas for 3D camera" (every recorded movement relative; cursor-locked games).</summary>
    public bool UseRelativeMouseDeltas
    {
        get => _useRelativeMouseDeltas;
        set { if (SetProperty(ref _useRelativeMouseDeltas, value)) IsDirty = true; }
    }

    public RecordingMode RecordingMode
    {
        get => _recordingMode;
        set
        {
            if (!SetProperty(ref _recordingMode, value)) return;
            IsDirty = true;
            OnPropertyChanged(nameof(IsDesktopRecording));
            OnPropertyChanged(nameof(MakesEditableSteps));
            OnPropertyChanged(nameof(RecordingModeHelp));
        }
    }

    public bool IsDesktopRecording => _recordingMode == RecordingMode.Desktop;

    /// <summary>Desktop and Smart recordings make editable steps, so the movement / hold-time options apply.</summary>
    public bool MakesEditableSteps => _recordingMode is RecordingMode.Desktop or RecordingMode.Smart;

    public string RecordingModeHelp => _recordingMode switch
    {
        RecordingMode.Desktop =>
            "Desktop only (fallback): records with Windows hooks into editable steps with screen positions. It can't record camera turning in games that lock the cursor.",
        RecordingMode.Game =>
            "Game only (fallback): records what the mouse and keyboard report (Raw Input) as one \"Recorded input\" step with relative mouse movement and exact key timing. Menu clicks aren't moved to screen positions.",
        RecordingMode.Rhythm =>
            "Exact 1:1 / Rhythm: keeps every event on one sub-millisecond locked timeline with zero path simplification.",
        _ =>
            "One Record button for everything. SmartMacro keeps both the cursor position and the relative mouse movement for every mouse event. Parts where the cursor moves freely become editable steps (clicks, keys, delays). Parts where a game hides or locks the cursor (camera turning) become a \"Recorded input\" step that plays relative movement. Detection isn't perfect, so you can change each part in the editor. Games may still play back differently (frame rate, physics, network), and some ignore simulated input.",
    };

    public bool KeepTapHoldTime
    {
        get => _keepTapHoldTime;
        set { if (SetProperty(ref _keepTapHoldTime, value)) IsDirty = true; }
    }

    /// <summary>"Simplify mouse paths": only clicks, drags and the spot before key presses become steps.</summary>
    public bool SimplifyMousePaths
    {
        get => _simplifyMousePaths;
        set { if (SetProperty(ref _simplifyMousePaths, value)) IsDirty = true; }
    }

    /// <summary>"Save a picture of what I click" (Smart Click Snapshots for Smart-Aim / Convert to Find image).</summary>
    public bool CaptureClickSnapshots
    {
        get => _captureClickSnapshots;
        set { if (SetProperty(ref _captureClickSnapshots, value)) IsDirty = true; }
    }

    /// <summary>"Intent Recording: remember UI buttons, text &amp; windows when I click".</summary>
    public bool CaptureClickIntent
    {
        get => _captureClickIntent;
        set { if (SetProperty(ref _captureClickIntent, value)) IsDirty = true; }
    }

    /// <summary>"Smart Timing: turn pauses after clicks into adaptive 'Until screen settles' waits".</summary>
    public bool SmartTimingOnRecord
    {
        get => _smartTimingOnRecord;
        set { if (SetProperty(ref _smartTimingOnRecord, value)) IsDirty = true; }
    }

    /// <summary>"Smart Typing: group typed words &amp; sentences into 'Type text' steps".</summary>
    public bool CoalesceTypedTextOnRecord
    {
        get => _coalesceTypedTextOnRecord;
        set { if (SetProperty(ref _coalesceTypedTextOnRecord, value)) IsDirty = true; }
    }

    /// <summary>"Smooth mouse movement during playback" (glide instead of jumping).</summary>
    public bool SmoothMouseMoves
    {
        get => _smoothMouseMoves;
        set { if (SetProperty(ref _smoothMouseMoves, value)) IsDirty = true; }
    }

    /// <summary>Allow HTTPS webhooks to target localhost or private LAN addresses.</summary>
    public bool AllowLocalWebhooks
    {
        get => _allowLocalWebhooks;
        set
        {
            if (!SetProperty(ref _allowLocalWebhooks, value)) return;
            IsDirty = true;
            OnPropertyChanged(nameof(FailureWebhookError));
            OnPropertyChanged(nameof(IsValid));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>Optional HTTPS webhook URL notified automatically on macro execution failure.</summary>
    public string FailureWebhookUrl
    {
        get => _failureWebhookUrl;
        set
        {
            var next = value ?? string.Empty;
            if (!SetProperty(ref _failureWebhookUrl, next)) return;
            FailureWebhookTestResult = null;
            IsDirty = true;
            OnPropertyChanged(nameof(FailureWebhookError));
            OnPropertyChanged(nameof(IsValid));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string? FailureWebhookError
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_failureWebhookUrl))
                return null;

            return WebhookSecurity.ValidateUrlSyntax(_failureWebhookUrl, _allowLocalWebhooks, out _, out var error)
                ? null
                : error;
        }
    }

    /// <summary>Optional custom HTTPS manifest URL for manual update checks.</summary>
    public string UpdateManifestUrl
    {
        get => _updateManifestUrl;
        set
        {
            var next = value ?? string.Empty;
            if (!SetProperty(ref _updateManifestUrl, next)) return;
            IsDirty = true;
            OnPropertyChanged(nameof(UpdateManifestUrlError));
            OnPropertyChanged(nameof(IsValid));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string? UpdateManifestUrlError
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_updateManifestUrl))
                return null;

            return WebhookSecurity.ValidateUrlSyntax(_updateManifestUrl, allowLocalNetwork: false, out _, out var error)
                ? null
                : error;
        }
    }

    public string? UpdateStatusMessage
    {
        get => _updateStatusMessage;
        private set => SetProperty(ref _updateStatusMessage, value);
    }

    public string? FailureWebhookTestResult
    {
        get => _failureWebhookTestResult;
        private set => SetProperty(ref _failureWebhookTestResult, value);
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set => SetProperty(ref _isDirty, value);
    }

    public bool IsValid =>
        _defaultDelayMs is >= 0 and <= AppSettings.MaxDefaultDelayMs
        && HotkeyError is null
        && RulesError is null
        && FailureWebhookError is null
        && UpdateManifestUrlError is null;

    public string? DelayError => _defaultDelayMs is >= 0 and <= AppSettings.MaxDefaultDelayMs ? null : $"Enter a value from 0 to {AppSettings.MaxDefaultDelayMs:N0} ms.";

    public async Task LoadAsync()
    {
        var result = await _store.LoadAsync();
        if (result.Warning is not null)
        {
            _log.Warning(result.Warning);
            _setStatus(result.Warning, StatusKind.Warning);
        }

        _saved = result.Settings;
        LoadFrom(_saved);
        _apply(_saved.Clone());
    }

    public async Task ApplyRestoredSettingsAsync(AppSettings restored)
    {
        ArgumentNullException.ThrowIfNull(restored);
        restored.Sanitize();
        await _store.SaveAsync(restored);
        _saved = restored.Clone();
        LoadFrom(_saved);
        _apply(_saved.Clone());
    }

    private void ReloadProfileRules(IReadOnlyList<MacroProfileRule>? rules)
    {
        ProfileRules.Clear();
        if (rules is { Count: > 0 })
        {
            foreach (var rule in rules)
            {
                if (rule is not null)
                    ProfileRules.Add(new ProfileRuleItemViewModel(rule.Clone(), OnProfileRuleEdited));
            }
        }
        UpdateRulePositions();
        RefreshKnownProfiles();
        OnPropertyChanged(nameof(HasProfileRules));
        OnPropertyChanged(nameof(RulesError));
        OnPropertyChanged(nameof(HasRulesError));
        OnPropertyChanged(nameof(IsValid));
        NotifyLiveContextChanged();
    }

    private void OnProfileRuleEdited()
    {
        IsDirty = true;
        RefreshKnownProfiles();
        OnPropertyChanged(nameof(RulesError));
        OnPropertyChanged(nameof(HasRulesError));
        OnPropertyChanged(nameof(IsValid));
        NotifyLiveContextChanged();
        CommandManager.InvalidateRequerySuggested();
    }

    private void AddBlankProfileRule()
    {
        ProfileRules.Add(new ProfileRuleItemViewModel(new MacroProfileRule(), OnProfileRuleEdited));
        FinalizeRuleCollectionMutation();
    }

    private void AddRuleFromWindow(bool preferProcess)
    {
        if (_selectedOpenWindow is not { } win)
            return;

        var proc = win.ProcessName?.Trim() ?? string.Empty;
        var title = win.Title?.Trim() ?? string.Empty;
        if (preferProcess && proc.Length == 0)
            return;
        if (!preferProcess && title.Length == 0)
            return;

        var suggestedProfile = proc.Length > 0 ? proc : title;
        if (suggestedProfile.Length > MacroProfileRule.MaxProfileNameLength)
            suggestedProfile = suggestedProfile[..MacroProfileRule.MaxProfileNameLength];

        var rule = new MacroProfileRule
        {
            Enabled = true,
            ProfileName = suggestedProfile,
            WindowTitleMatch = preferProcess ? string.Empty : title,
            ProcessNameMatch = preferProcess ? proc : null,
        };
        rule.Sanitize();

        ProfileRules.Add(new ProfileRuleItemViewModel(rule, OnProfileRuleEdited));
        FinalizeRuleCollectionMutation();
    }

    private void MoveRule(ProfileRuleItemViewModel? rule, int delta)
    {
        if (rule is null) return;
        var idx = ProfileRules.IndexOf(rule);
        if (idx < 0) return;
        var next = idx + delta;
        if (next < 0 || next >= ProfileRules.Count) return;

        ProfileRules.Move(idx, next);
        FinalizeRuleCollectionMutation();
    }

    private void DeleteRule(ProfileRuleItemViewModel? rule)
    {
        if (rule is null || !ProfileRules.Remove(rule))
            return;
        FinalizeRuleCollectionMutation();
    }

    private void FinalizeRuleCollectionMutation()
    {
        UpdateRulePositions();
        RefreshKnownProfiles();
        IsDirty = true;
        OnPropertyChanged(nameof(HasProfileRules));
        OnPropertyChanged(nameof(RulesError));
        OnPropertyChanged(nameof(HasRulesError));
        OnPropertyChanged(nameof(IsValid));
        NotifyLiveContextChanged();
        CommandManager.InvalidateRequerySuggested();
    }

    private void UpdateRulePositions()
    {
        for (var i = 0; i < ProfileRules.Count; i++)
            ProfileRules[i].UpdatePosition(i, ProfileRules.Count);
    }

    private void RefreshKnownProfiles()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sorted = new List<string>();

        void AddIfNew(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            var trimmed = raw.Trim();
            if (seen.Add(trimmed))
                sorted.Add(trimmed);
        }

        foreach (var profile in _getLibraryProfiles())
            AddIfNew(profile);
        foreach (var rule in ProfileRules)
            AddIfNew(rule.ProfileName);

        sorted.Sort(StringComparer.OrdinalIgnoreCase);
        KnownProfiles.Clear();
        foreach (var p in sorted)
            KnownProfiles.Add(p);
        OnPropertyChanged(nameof(HasKnownProfiles));
    }

    public async Task RefreshWindowContextAsync()
    {
        RefreshKnownProfiles();
        IReadOnlyList<OpenWindowChoice> windows;
        try
        {
            windows = await Task.Run(() => _getOpenWindows());
        }
        catch (Exception ex)
        {
            _log.Warning("Could not list open windows for Profile Rules picker.", ex);
            windows = Array.Empty<OpenWindowChoice>();
        }

        var prevSelected = _selectedOpenWindow;
        OpenWindows.Clear();
        foreach (var win in windows)
            OpenWindows.Add(win);

        if (prevSelected is not null)
        {
            SelectedOpenWindow = OpenWindows.FirstOrDefault(w =>
                string.Equals(w.Title, prevSelected.Title, StringComparison.Ordinal)
                && string.Equals(w.ProcessName, prevSelected.ProcessName, StringComparison.OrdinalIgnoreCase));
        }

        OnPropertyChanged(nameof(HasOpenWindows));
        NotifyLiveContextChanged();
    }

    public void NotifyLiveContextChanged()
    {
        OnPropertyChanged(nameof(LiveForegroundDisplay));
        OnPropertyChanged(nameof(LiveResolvedProfileDisplay));
        OnPropertyChanged(nameof(SelectedWindowPreviewDisplay));
    }

    private string DescribeRoutingOutcome(string? title, string? processName)
    {
        if (!AutoSwitchProfileByWindow)
            return "All profiles armed (Auto-switch is OFF)";

        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(processName))
            return "Global macros only (waiting for target window)";

        foreach (var rule in ProfileRules)
        {
            if (!rule.Enabled || rule.ValidationError is not null)
                continue;
            var model = rule.ToModel();
            if (model.Matches(title, processName))
                return $"{model.ProfileName}  (matched Rule #{rule.PriorityNumber})";
        }

        var libraryStubs = KnownProfiles.Select(p => new Macro { Name = p, ProfileName = p }).ToList();
        var fallbackProfile = ProfileRouter.ResolveActiveProfile(
            rules: null,
            library: libraryStubs,
            foregroundWindowTitle: title,
            manualProfileFilter: null,
            autoSwitchByWindow: true,
            foregroundProcessName: processName);

        return string.IsNullOrWhiteSpace(fallbackProfile)
            ? "Global macros only (no rule or profile matched)"
            : $"{fallbackProfile}  (matched macro Profile tag)";
    }

    private List<MacroProfileRule>? BuildSavedProfileRules()
    {
        if (ProfileRules.Count == 0)
            return null;

        var list = new List<MacroProfileRule>(ProfileRules.Count);
        foreach (var rule in ProfileRules)
            list.Add(rule.ToModel());
        return list;
    }

    private void LoadFrom(AppSettings settings)
    {
        _confirmBeforeDelete = settings.ConfirmBeforeDelete;
        _defaultDelayMs = settings.DefaultDelayMs;
        _showLogPanel = settings.ShowLogPanel;
        _minimizeDuringRun = settings.MinimizeDuringRun;
        _showRunOverlay = settings.ShowRunOverlay;
        _minimizeToTrayOnClose = settings.MinimizeToTrayOnClose;
        _startWithWindows = settings.StartWithWindows;
        _autoSwitchProfileByWindow = settings.AutoSwitchProfileByWindow;
        _allowLocalWebhooks = settings.AllowLocalWebhooks;
        _failureWebhookUrl = settings.FailureWebhookUrl ?? string.Empty;
        _updateManifestUrl = settings.UpdateManifestUrl ?? string.Empty;
        _failureWebhookTestResult = null;
        ReloadProfileRules(settings.ProfileRules);
        _recordMouseMovement = settings.RecordMouseMovement;
        _recordingMode = settings.RecordingMode;
        _recordingFidelity = settings.RecordingFidelity;
        _computeEngineMode = settings.ComputeEngineMode;
        _visionMemoryBudgetMb = settings.VisionMemoryBudgetMb;
        _useRelativeMouseDeltas = settings.UseRelativeMouseDeltas;
        _keepTapHoldTime = settings.KeepTapHoldTime;
        _simplifyMousePaths = settings.SimplifyMousePaths;
        _captureClickSnapshots = settings.CaptureClickSnapshots;
        _captureClickIntent = settings.CaptureClickIntent;
        _smartTimingOnRecord = settings.SmartTimingOnRecord;
        _coalesceTypedTextOnRecord = settings.CoalesceTypedTextOnRecord;
        _smoothMouseMoves = settings.SmoothMouseMoves;
        _theme = settings.Theme;
        _accent = settings.Accent;
        _density = settings.Density;
        _experienceLevel = settings.ExperienceLevel;
        _reduceMotion = settings.ReduceMotion;
        _recordHotkey = settings.RecordHotkey;
        _playHotkey = settings.PlayHotkey;
        _pauseHotkey = settings.PauseHotkey;
        IsDirty = false;
        PreviewAppearance();
        OnPropertyChanged(string.Empty);
    }

    private async Task SaveAsync()
    {
        if (!IsValid) return;

        var updated = new AppSettings
        {
            ConfirmBeforeDelete = ConfirmBeforeDelete,
            DefaultDelayMs = DefaultDelayMs,
            ShowLogPanel = ShowLogPanel,
            MinimizeDuringRun = MinimizeDuringRun,
            ShowRunOverlay = ShowRunOverlay,
            MinimizeToTrayOnClose = MinimizeToTrayOnClose,
            StartWithWindows = StartWithWindows,
            AutoSwitchProfileByWindow = AutoSwitchProfileByWindow,
            AllowLocalWebhooks = AllowLocalWebhooks,
            FailureWebhookUrl = string.IsNullOrWhiteSpace(FailureWebhookUrl) ? null : FailureWebhookUrl.Trim(),
            UpdateManifestUrl = string.IsNullOrWhiteSpace(UpdateManifestUrl) ? null : UpdateManifestUrl.Trim(),
            ProfileRules = BuildSavedProfileRules(),
            RecordMouseMovement = RecordMouseMovement,
            RecordingMode = RecordingMode,
            RecordingFidelity = RecordingFidelity,
            ComputeEngineMode = ComputeEngineMode,
            VisionMemoryBudgetMb = VisionMemoryBudgetMb,
            UseRelativeMouseDeltas = UseRelativeMouseDeltas,
            KeepTapHoldTime = KeepTapHoldTime,
            SimplifyMousePaths = SimplifyMousePaths,
            CaptureClickSnapshots = CaptureClickSnapshots,
            CaptureClickIntent = CaptureClickIntent,
            SmartTimingOnRecord = SmartTimingOnRecord,
            CoalesceTypedTextOnRecord = CoalesceTypedTextOnRecord,
            SmoothMouseMoves = SmoothMouseMoves,
            ShowOverlaysInObsAndRecording = _saved.ShowOverlaysInObsAndRecording, // saved on its own when toggled
            Theme = Theme,
            Accent = Accent,
            Density = Density,
            ExperienceLevel = ExperienceLevel,
            ReduceMotion = ReduceMotion,
            SidebarPinned = _saved.SidebarPinned,
            RecordHotkey = RecordHotkey,
            PlayHotkey = PlayHotkey,
            PauseHotkey = PauseHotkey,
        };

        try
        {
            await _store.SaveAsync(updated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("Saving settings failed.", ex);
            _dialogs.ShowError("Could not save settings", ex.Message);
            return;
        }

        _saved = updated;
        IsDirty = false;
        _apply(updated.Clone());
        _setStatus("Settings saved.", StatusKind.Success);
    }

    /// <summary>
    /// Saves one quick change right away (the toolbar's "Show mini status overlay" / "Minimize while playing" / RecordingMode),
    /// without touching other unsaved edits on the Settings page.
    /// </summary>
    public async Task QuickSetAsync(Action<AppSettings> change)
    {
        var updated = _saved.Clone();
        change(updated);
        _saved = updated;
        _minimizeDuringRun = updated.MinimizeDuringRun;
        _showRunOverlay = updated.ShowRunOverlay;
        _recordingMode = updated.RecordingMode;
        _recordingFidelity = updated.RecordingFidelity;
        OnPropertyChanged(nameof(RecordingFidelity));
        OnPropertyChanged(nameof(RecordingFidelityHelp));
        OnPropertyChanged(nameof(MinimizeDuringRun));
        OnPropertyChanged(nameof(ShowRunOverlay));
        OnPropertyChanged(nameof(ShowOverlaysInObsAndRecording));
        OnPropertyChanged(nameof(RecordingMode));
        OnPropertyChanged(nameof(IsDesktopRecording));
        OnPropertyChanged(nameof(MakesEditableSteps));
        OnPropertyChanged(nameof(RecordingModeHelp));
        _apply(updated.Clone());

        try
        {
            await _store.SaveAsync(updated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("Saving settings failed.", ex);
            _setStatus($"The setting couldn't be saved: {ex.Message}", StatusKind.Warning);
        }
    }

    private void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{DataFolder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Error("Could not open the data folder.", ex);
            _dialogs.ShowError("Could not open folder", ex.Message);
        }
    }

    private Task ExportBackupAsync() =>
        ExportStudioBackupHandler is not null ? ExportStudioBackupHandler() : Task.CompletedTask;

    private Task RestoreBackupAsync() =>
        RestoreStudioBackupHandler is not null ? RestoreStudioBackupHandler() : Task.CompletedTask;

    private async Task CheckForUpdatesAsync()
    {
        if (_isCheckingUpdates)
            return;

        _isCheckingUpdates = true;
        CommandManager.InvalidateRequerySuggested();
        UpdateStatusMessage = "Checking release manifest…";
        _setStatus("Checking for SmartMacro updates…", StatusKind.Info);

        try
        {
            var result = await _updateChecker.CheckForUpdatesAsync(AppInfo.Version, UpdateManifestUrl)
                .ConfigureAwait(true);
            UpdateStatusMessage = result.Message;

            switch (result.Outcome)
            {
                case UpdateCheckOutcome.UpdateAvailable:
                {
                    _setStatus(result.Message, StatusKind.Success);
                    var lines = new List<string>
                    {
                        $"A new version of {AppInfo.Name} is available!",
                        string.Empty,
                        $"Installed: v{result.CurrentVersion}",
                        $"Latest: v{result.LatestVersion}",
                    };
                    if (!string.IsNullOrWhiteSpace(result.PublishedAt))
                        lines.Add($"Published: {result.PublishedAt}");
                    if (!string.IsNullOrWhiteSpace(result.Sha256))
                        lines.Add($"SHA-256: {result.Sha256}");
                    if (!string.IsNullOrWhiteSpace(result.ReleaseNotes))
                    {
                        lines.Add(string.Empty);
                        lines.Add(result.ReleaseNotes);
                    }

                    lines.Add(string.Empty);
                    lines.Add("Open the official download page in your browser?");
                    if (_dialogs.Confirm("Update Available", string.Join(Environment.NewLine, lines)))
                        TryOpenExternalUrl(result.DownloadUrl ?? UpdateCheckService.OfficialReleasesUrl);
                    break;
                }

                case UpdateCheckOutcome.UpToDate:
                    _setStatus(result.Message, StatusKind.Success);
                    _dialogs.ShowInfo(
                        "SmartMacro is up to date",
                        $"{result.Message}\n\nInstalled version: {AppInfo.Name} {AppInfo.Version}");
                    break;

                default:
                    _log.Warning($"Update check reported: {result.Message}");
                    _setStatus($"Update check unavailable: {result.Message}", StatusKind.Warning);
                    if (_dialogs.Confirm(
                            "Update Check Unavailable",
                            $"{result.Message}\n\nOpen the official GitHub releases page in your browser?"))
                    {
                        TryOpenExternalUrl(UpdateCheckService.OfficialReleasesUrl);
                    }
                    break;
            }
        }
        finally
        {
            _isCheckingUpdates = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task TestFailureWebhookAsync()
    {
        if (_isTestingFailureWebhook || string.IsNullOrWhiteSpace(FailureWebhookUrl))
            return;

        _isTestingFailureWebhook = true;
        CommandManager.InvalidateRequerySuggested();
        FailureWebhookTestResult = "Sending test failure alert…";

        try
        {
            var payload = new WebhookMessagePayload
            {
                Content = "❌ SmartMacro test failure alert — delivery verified.",
                Title = "🧪 Failure Alert Webhook Test",
                Details = "This test confirms fatal execution errors can reach this endpoint.",
                EmbedColor = 0xEF4444,
            };

            var result = await _webhookClient.SendAsync(
                FailureWebhookUrl,
                payload,
                AllowLocalWebhooks,
                bypassRateLimit: true).ConfigureAwait(true);

            FailureWebhookTestResult = result.Success
                ? $"✓ {result.Message}"
                : $"⚠ {result.Message}";

            _setStatus(
                result.Success ? "Test failure webhook delivered." : result.Message,
                result.Success ? StatusKind.Success : StatusKind.Warning);
        }
        finally
        {
            _isTestingFailureWebhook = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void TryOpenExternalUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Warning($"Could not open URL '{url}'.", ex);
            _setStatus($"Could not open browser link: {ex.Message}", StatusKind.Warning);
        }
    }
}
