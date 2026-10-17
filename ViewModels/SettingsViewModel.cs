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
    private bool _recordMouseMovement;
    private RecordingMode _recordingMode;
    private bool _keepTapHoldTime;
    private bool _simplifyMousePaths;
    private bool _smoothMouseMoves;
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
            case "Record": RecordHotkey = binding; break;
            case "Play": PlayHotkey = binding; break;
            case "Pause": PauseHotkey = binding; break;
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

    private void LoadFrom(AppSettings settings)
    {
        _confirmBeforeDelete = settings.ConfirmBeforeDelete;
        _defaultDelayMs = settings.DefaultDelayMs;
        _showLogPanel = settings.ShowLogPanel;
        _minimizeDuringRun = settings.MinimizeDuringRun;
        _showRunOverlay = settings.ShowRunOverlay;
        _recordMouseMovement = settings.RecordMouseMovement;
        _recordingMode = settings.RecordingMode;
        _keepTapHoldTime = settings.KeepTapHoldTime;
        _simplifyMousePaths = settings.SimplifyMousePaths;
        _smoothMouseMoves = settings.SmoothMouseMoves;
        _recordHotkey = settings.RecordHotkey;
        _playHotkey = settings.PlayHotkey;
        _pauseHotkey = settings.PauseHotkey;
        IsDirty = false;
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
            RecordMouseMovement = RecordMouseMovement,
            RecordingMode = RecordingMode,
            KeepTapHoldTime = KeepTapHoldTime,
            SimplifyMousePaths = SimplifyMousePaths,
            SmoothMouseMoves = SmoothMouseMoves,
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
        OnPropertyChanged(nameof(MinimizeDuringRun));
        OnPropertyChanged(nameof(ShowRunOverlay));
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
}
