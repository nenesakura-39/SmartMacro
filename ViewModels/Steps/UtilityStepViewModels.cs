using System.Collections.ObjectModel;
using SmartMacro.Core.Models;
using SmartMacro.Core.Vision;
using SmartMacro.Mvvm;
using SmartMacro.Services.Vision;

namespace SmartMacro.ViewModels.Steps;

/// <summary>The open app windows for a "pick a window" drop-down (front to back, one entry per title).</summary>
internal static class OpenWindowList
{
    public static void Fill(ObservableCollection<string> titles, IScreenLayout? layout)
    {
        titles.Clear();
        IReadOnlyList<WindowInfo> windows;
        try
        {
            windows = layout?.GetWindows() ?? Array.Empty<WindowInfo>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            windows = Array.Empty<WindowInfo>();
        }

        foreach (var title in windows.Select(w => w.Title).Distinct(StringComparer.OrdinalIgnoreCase)) titles.Add(title);
    }
}

/// <summary>Editor for "Wait for screen change": the watched area, how much must change, timing and what to do then.</summary>
public sealed class WaitForChangeStepViewModel : StepViewModel
{
    private readonly WaitForChangeStep _step;
    private IVisionTools? _tools;
    private string? _testResult;
    private bool _isBusy;

    public WaitForChangeStepViewModel(WaitForChangeStep step) : base(step)
    {
        _step = step;
        PickAreaCommand = new AsyncRelayCommand(PickAreaAsync, ex => TestResult = ex.Message, () => _tools is not null && !_isBusy);
        ShowAreaCommand = new RelayCommand(() =>
            ScreenHighlightOverlay.ShowResult(_step.Region, null, found: true, $"Watched area · {_step.RegionWidth}×{_step.RegionHeight}",
                TimeSpan.FromMilliseconds(1600)));
    }

    /// <summary>The editor's screen tools (set right after creation): the area picker.</summary>
    public IVisionTools? Tools
    {
        get => _tools;
        set
        {
            _tools = value;
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    public AsyncRelayCommand PickAreaCommand { get; }
    public RelayCommand ShowAreaCommand { get; }

    public override string? PlainSummary
    {
        get
        {
            var percent = _step.MinChangedPercent.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            var wait = _step.TimeoutMs == 0 ? "checks once" : $"keeps looking every {_step.CheckIntervalMs} ms for up to {_step.TimeoutMs / 1000.0:0.#} s";
            var then = _step.Action == FoundTextAction.None
                ? "The middle of the change becomes the last found spot, so a Mouse click \"on the last found spot\" can click it."
                : "Then it acts on the middle of the change (it also becomes the last found spot).";
            return $"• Takes a picture of the area when the step starts, then {wait} until at least {percent} % of it looks different.\n• {then}";
        }
    }

    public int RegionX
    {
        get => _step.RegionX;
        set { if (_step.RegionX == value) return; _step.RegionX = value; NotifyChanged(); }
    }

    public int RegionY
    {
        get => _step.RegionY;
        set { if (_step.RegionY == value) return; _step.RegionY = value; NotifyChanged(); }
    }

    public int RegionWidth
    {
        get => _step.RegionWidth;
        set { if (_step.RegionWidth == value) return; _step.RegionWidth = value; NotifyChanged(); }
    }

    public int RegionHeight
    {
        get => _step.RegionHeight;
        set { if (_step.RegionHeight == value) return; _step.RegionHeight = value; NotifyChanged(); }
    }

    public double MinChangedPercent
    {
        get => _step.MinChangedPercent;
        set { if (_step.MinChangedPercent.Equals(value)) return; _step.MinChangedPercent = value; NotifyChanged(); }
    }

    public int PixelTolerance
    {
        get => _step.PixelTolerance;
        set { if (_step.PixelTolerance == value) return; _step.PixelTolerance = value; NotifyChanged(); }
    }

    public int TimeoutMs
    {
        get => _step.TimeoutMs;
        set { if (_step.TimeoutMs == value) return; _step.TimeoutMs = value; NotifyChanged(); }
    }

    public int CheckIntervalMs
    {
        get => _step.CheckIntervalMs;
        set { if (_step.CheckIntervalMs == value) return; _step.CheckIntervalMs = value; NotifyChanged(); }
    }

    public static IReadOnlyList<Choice<FoundTextAction>> Actions { get; } = new[]
    {
        new Choice<FoundTextAction>(FoundTextAction.None, "Do nothing (just remember the spot)"),
        new Choice<FoundTextAction>(FoundTextAction.Click, "Click the middle of the change"),
        new Choice<FoundTextAction>(FoundTextAction.DoubleClick, "Double-click the middle of the change"),
        new Choice<FoundTextAction>(FoundTextAction.RightClick, "Right-click the middle of the change"),
        new Choice<FoundTextAction>(FoundTextAction.Move, "Move the mouse there (no click)"),
        new Choice<FoundTextAction>(FoundTextAction.Hold, "Hold the left button there for…"),
    };

    public static IReadOnlyList<Choice<TextNotFoundBehavior>> NotFoundOptions => ScreenSearchStepViewModel.NotFoundOptions;

    public FoundTextAction Action
    {
        get => _step.Action;
        set
        {
            if (_step.Action == value) return;
            _step.Action = value;
            NotifyChanged();
            OnPropertyChanged(nameof(UsesHold));
        }
    }

    public bool UsesHold => _step.Action == FoundTextAction.Hold;

    public int ActionHoldMs
    {
        get => _step.ActionHoldMs;
        set { if (_step.ActionHoldMs == value) return; _step.ActionHoldMs = value; NotifyChanged(); }
    }

    public TextNotFoundBehavior WhenNotFound
    {
        get => _step.WhenNotFound;
        set { if (_step.WhenNotFound == value) return; _step.WhenNotFound = value; NotifyChanged(); }
    }

    public string? TestResult
    {
        get => _testResult;
        private set => SetProperty(ref _testResult, value);
    }

    private async Task PickAreaAsync()
    {
        if (_isBusy || _tools is not { } vision) return;
        _isBusy = true;
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        try
        {
            if (await vision.PickRegionAsync() is not { } rect)
            {
                TestResult = "Picking the area was cancelled.";
                return;
            }

            _step.RegionX = rect.X;
            _step.RegionY = rect.Y;
            _step.RegionWidth = rect.Width;
            _step.RegionHeight = rect.Height;
            OnPropertyChanged(string.Empty);
            NotifyChanged(nameof(RegionX));
            TestResult = $"Watched area set to {rect}.";
        }
        finally
        {
            _isBusy = false;
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }
}

/// <summary>Editor for "Focus window": which window (from the open ones or typed), restore, wait and pause.</summary>
public sealed class FocusWindowStepViewModel : StepViewModel
{
    private readonly FocusWindowStep _step;
    private IScreenLayout? _layout;

    public FocusWindowStepViewModel(FocusWindowStep step) : base(step)
    {
        _step = step;
        RefreshWindowsCommand = new RelayCommand(RefreshWindows);
    }

    /// <summary>Monitors and windows from the editor (set right after creation), for the window list.</summary>
    public IScreenLayout? Layout
    {
        get => _layout;
        set
        {
            _layout = value;
            RefreshWindows();
        }
    }

    public RelayCommand RefreshWindowsCommand { get; }

    public ObservableCollection<string> WindowTitles { get; } = new();

    public override string? PlainSummary =>
        string.IsNullOrWhiteSpace(_step.WindowTitle)
            ? "• Choose the window to bring to the front (open it first, then click Refresh windows)."
            : $"• Brings the window whose title or app name contains \"{_step.WindowTitle.Trim()}\" to the front{(_step.RestoreIfMinimized ? ", restoring it if it's minimized" : "")}, so the keys and clicks after this step go to it."
              + (_step.WaitForWindowMs > 0 ? $"\n• If it isn't open yet, waits up to {_step.WaitForWindowMs / 1000.0:0.#} s for it." : "");

    public string WindowTitle
    {
        get => _step.WindowTitle ?? string.Empty;
        set
        {
            var text = value ?? string.Empty;
            if (_step.WindowTitle == text) return;
            _step.WindowTitle = text;
            NotifyChanged();
            OnPropertyChanged(nameof(SelectedWindowTitle));
        }
    }

    /// <summary>The drop-down of open windows: choosing one fills in <see cref="WindowTitle"/>.</summary>
    public string? SelectedWindowTitle
    {
        get => WindowTitles.FirstOrDefault(t => string.Equals(t, _step.WindowTitle?.Trim(), StringComparison.OrdinalIgnoreCase));
        set
        {
            if (!string.IsNullOrWhiteSpace(value)) WindowTitle = value.Trim();
        }
    }

    public bool RestoreIfMinimized
    {
        get => _step.RestoreIfMinimized;
        set { if (_step.RestoreIfMinimized == value) return; _step.RestoreIfMinimized = value; NotifyChanged(); }
    }

    public int WaitForWindowMs
    {
        get => _step.WaitForWindowMs;
        set { if (_step.WaitForWindowMs == value) return; _step.WaitForWindowMs = value; NotifyChanged(); }
    }

    public int SettleMs
    {
        get => _step.SettleMs;
        set { if (_step.SettleMs == value) return; _step.SettleMs = value; NotifyChanged(); }
    }

    public static IReadOnlyList<Choice<TextNotFoundBehavior>> NotFoundOptions => ScreenSearchStepViewModel.NotFoundOptions;

    public TextNotFoundBehavior WhenNotFound
    {
        get => _step.WhenNotFound;
        set { if (_step.WhenNotFound == value) return; _step.WhenNotFound = value; NotifyChanged(); }
    }

    private void RefreshWindows()
    {
        OpenWindowList.Fill(WindowTitles, _layout);
        OnPropertyChanged(nameof(SelectedWindowTitle));
    }
}

/// <summary>Editor for "Take screenshot": what to capture, the folder and the file name prefix.</summary>
public sealed class ScreenshotStepViewModel : StepViewModel
{
    private readonly ScreenshotStep _step;
    private IVisionTools? _tools;
    private IScreenLayout? _layout;
    private string? _testResult;
    private bool _isBusy;

    public ScreenshotStepViewModel(ScreenshotStep step) : base(step)
    {
        _step = step;
        PickAreaCommand = new AsyncRelayCommand(PickAreaAsync, ex => TestResult = ex.Message, () => _tools is not null && !_isBusy);
        RefreshWindowsCommand = new RelayCommand(RefreshWindows);
        ChooseFolderCommand = new RelayCommand(ChooseFolder);
        UseDefaultFolderCommand = new RelayCommand(() => FolderPath = string.Empty);
        OpenFolderCommand = new RelayCommand(OpenFolder);
    }

    public IVisionTools? Tools
    {
        get => _tools;
        set
        {
            _tools = value;
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    public IScreenLayout? Layout
    {
        get => _layout;
        set
        {
            _layout = value;
            if (UsesWindow) RefreshWindows();
        }
    }

    public AsyncRelayCommand PickAreaCommand { get; }
    public RelayCommand RefreshWindowsCommand { get; }
    public RelayCommand ChooseFolderCommand { get; }
    public RelayCommand UseDefaultFolderCommand { get; }
    public RelayCommand OpenFolderCommand { get; }

    public ObservableCollection<string> WindowTitles { get; } = new();

    public override string? PlainSummary =>
        $"• Saves a PNG as {ExampleFileName} in {EffectiveFolder}.\n• If it can't be taken or saved (window closed, folder not allowed), the log says why and the macro carries on.";

    public static IReadOnlyList<Choice<ScreenshotArea>> Areas { get; } = new[]
    {
        new Choice<ScreenshotArea>(ScreenshotArea.PrimaryMonitor, "Main monitor"),
        new Choice<ScreenshotArea>(ScreenshotArea.AllMonitors, "All monitors (one wide picture)"),
        new Choice<ScreenshotArea>(ScreenshotArea.Window, "An app window"),
        new Choice<ScreenshotArea>(ScreenshotArea.CustomArea, "Custom area on screen"),
    };

    public ScreenshotArea Area
    {
        get => _step.Area;
        set
        {
            if (_step.Area == value) return;
            _step.Area = value;
            if (value == ScreenshotArea.Window) RefreshWindows();
            NotifyChanged();
            OnPropertyChanged(nameof(UsesRegion));
            OnPropertyChanged(nameof(UsesWindow));
        }
    }

    public bool UsesRegion => _step.Area == ScreenshotArea.CustomArea;

    public bool UsesWindow => _step.Area == ScreenshotArea.Window;

    public int RegionX
    {
        get => _step.RegionX;
        set { if (_step.RegionX == value) return; _step.RegionX = value; NotifyChanged(); }
    }

    public int RegionY
    {
        get => _step.RegionY;
        set { if (_step.RegionY == value) return; _step.RegionY = value; NotifyChanged(); }
    }

    public int RegionWidth
    {
        get => _step.RegionWidth;
        set { if (_step.RegionWidth == value) return; _step.RegionWidth = value; NotifyChanged(); }
    }

    public int RegionHeight
    {
        get => _step.RegionHeight;
        set { if (_step.RegionHeight == value) return; _step.RegionHeight = value; NotifyChanged(); }
    }

    public string WindowTitle
    {
        get => _step.WindowTitle ?? string.Empty;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (_step.WindowTitle == normalized) return;
            _step.WindowTitle = normalized;
            NotifyChanged();
            OnPropertyChanged(nameof(SelectedWindowTitle));
        }
    }

    public string? SelectedWindowTitle
    {
        get => WindowTitles.FirstOrDefault(t => string.Equals(t, _step.WindowTitle, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (!string.IsNullOrWhiteSpace(value)) WindowTitle = value;
        }
    }

    /// <summary>The chosen folder ("" = the default Pictures\SmartMacro Screenshots).</summary>
    public string FolderPath
    {
        get => _step.FolderPath ?? string.Empty;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (_step.FolderPath == normalized) return;
            _step.FolderPath = normalized;
            NotifyChanged();
            OnPropertyChanged(nameof(EffectiveFolder));
        }
    }

    public string EffectiveFolder => ScreenshotFiles.FolderOf(_step);

    public string FileNamePrefix
    {
        get => _step.FileNamePrefix ?? string.Empty;
        set
        {
            var text = value ?? string.Empty;
            if (_step.FileNamePrefix == text) return;
            _step.FileNamePrefix = text;
            NotifyChanged();
            OnPropertyChanged(nameof(ExampleFileName));
        }
    }

    public string ExampleFileName => ScreenshotFiles.FileName(_step.FileNamePrefix, DateTime.Now);

    public string? TestResult
    {
        get => _testResult;
        private set => SetProperty(ref _testResult, value);
    }

    private void RefreshWindows()
    {
        OpenWindowList.Fill(WindowTitles, _layout);
        OnPropertyChanged(nameof(SelectedWindowTitle));
    }

    private void ChooseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Folder for the screenshots",
            Multiselect = false,
        };
        var start = EffectiveFolder;
        if (Directory.Exists(start)) dialog.InitialDirectory = start;
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            FolderPath = dialog.FolderName;
    }

    private void OpenFolder()
    {
        var folder = EffectiveFolder;
        try
        {
            Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
            TestResult = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            TestResult = $"Couldn't open the folder: {ex.Message}";
        }
    }

    private async Task PickAreaAsync()
    {
        if (_isBusy || _tools is not { } vision) return;
        _isBusy = true;
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        try
        {
            if (await vision.PickRegionAsync() is not { } rect)
            {
                TestResult = "Picking the area was cancelled.";
                return;
            }

            _step.RegionX = rect.X;
            _step.RegionY = rect.Y;
            _step.RegionWidth = rect.Width;
            _step.RegionHeight = rect.Height;
            OnPropertyChanged(string.Empty);
            NotifyChanged(nameof(RegionX));
            TestResult = $"Screenshot area set to {rect}.";
        }
        finally
        {
            _isBusy = false;
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }
}
