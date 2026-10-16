using System.Diagnostics;
using SmartMacro.Core.Logging;
using SmartMacro.Core.Models;
using SmartMacro.Core.Storage;
using SmartMacro.Mvvm;
using SmartMacro.Services;

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
