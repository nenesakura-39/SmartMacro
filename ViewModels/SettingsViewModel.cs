using System.Diagnostics;
using SmartMacro.Core.Logging;
using SmartMacro.Core.Models;
using SmartMacro.Core.Recording;
using SmartMacro.Core.Storage;
using SmartMacro.Mvvm;
using SmartMacro.Services;
using SmartMacro.ViewModels.Steps;

namespace SmartMacro.ViewModels;

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
    private bool _recordMouseMovement;
    private RecordingMode _recordingMode;
    private RecordingFidelity _recordingFidelity = RecordingFidelity.Exact1To1;
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

    public SettingsViewModel(ISettingsStore store, ILogService log, IDialogService dialogs, AppPaths paths,
        Action<AppSettings> apply, Action<string, StatusKind> setStatus, string ocrStatus = "", bool ocrAvailable = false,
        Func<string>? buildDiagnostics = null)
    {
        _buildDiagnostics = buildDiagnostics ?? (() => $"{AppInfo.Name} {AppInfo.Version}");
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
        CheckForUpdatesCommand = new RelayCommand(CheckForUpdates);
        SetAccentCommand = new RelayCommand(p =>
        {
            if (p is StudioAccent accent) Accent = accent;
            else if (p is string text && Enum.TryParse<StudioAccent>(text, out var parsed)) Accent = parsed;
        });
    }

    public RelayCommand SetAccentCommand { get; }
    public AsyncRelayCommand ExportBackupCommand { get; }
    public AsyncRelayCommand RestoreBackupCommand { get; }
    public RelayCommand CheckForUpdatesCommand { get; }

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
        ThemeManager.Apply(new AppSettings
        {
            Theme = _theme,
            Accent = _accent,
            Density = _density,
            ReduceMotion = _reduceMotion,
        });
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

    /// <summary>Hardware SIMD &amp; CPU worker telemetry shown on the Settings page.</summary>
    public string HardwareAccelerationStatus => SmartMacro.Core.Vision.HardwareAccelerationInfo.StatusText;

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
        set { if (SetProperty(ref _autoSwitchProfileByWindow, value)) IsDirty = true; }
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

    public bool IsDirty
    {
        get => _isDirty;
        private set => SetProperty(ref _isDirty, value);
    }

    public bool IsValid => _defaultDelayMs is >= 0 and <= AppSettings.MaxDefaultDelayMs && HotkeyError is null;

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
        _recordMouseMovement = settings.RecordMouseMovement;
        _recordingMode = settings.RecordingMode;
        _recordingFidelity = settings.RecordingFidelity;
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
            ProfileRules = _saved.ProfileRules is null ? null : new List<MacroProfileRule>(_saved.ProfileRules),
            RecordMouseMovement = RecordMouseMovement,
            RecordingMode = RecordingMode,
            RecordingFidelity = RecordingFidelity,
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

    private void CheckForUpdates()
    {
        try
        {
            _setStatus($"SmartMacro Studio {AppInfo.Version} — opening official release channel…", StatusKind.Info);
            Process.Start(new ProcessStartInfo("https://discord.gg/CKBk5KnGmk") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Warning("Could not open release page.", ex);
            _setStatus($"You are running {AppInfo.Name} {AppInfo.Version}.", StatusKind.Info);
        }
    }
}
