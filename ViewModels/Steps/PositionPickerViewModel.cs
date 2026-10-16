using SmartMacro.Mvvm;
using SmartMacro.Services.Vision;

namespace SmartMacro.ViewModels.Steps;

/// <summary>
/// The "Pick position" and "Use cursor position" buttons shared by Move mouse, Mouse click and Scroll wheel.
/// It doesn't store anything itself: it hands the chosen point to the step through <c>setPosition</c>, which also
/// switches the step to "at a chosen position". All points are physical screen pixels, the same coordinates
/// recording and playback use.
/// </summary>
public sealed class PositionPickerViewModel : ObservableObject
{
    /// <summary>How long "Use cursor position" waits, so you can move the mouse away from SmartMacro first.</summary>
    public const int CursorCountdownSeconds = 3;

    private readonly Action<int, int> _setPosition;
    private IVisionTools? _tools;
    private string? _status;
    private bool _isBusy;

    /// <param name="setPosition">Writes the chosen X / Y into the step (and marks the macro as changed).</param>
    public PositionPickerViewModel(Action<int, int> setPosition)
    {
        _setPosition = setPosition ?? throw new ArgumentNullException(nameof(setPosition));
        PickCommand = new AsyncRelayCommand(PickAsync, ex => Status = ex.Message, () => _tools is not null && !_isBusy);
        UseCursorCommand = new AsyncRelayCommand(UseCursorAsync, ex => Status = ex.Message, () => _tools is not null && !_isBusy);
    }

    public AsyncRelayCommand PickCommand { get; }

    public AsyncRelayCommand UseCursorCommand { get; }

    /// <summary>Screen tools from the editor (set right after the step editor is created).</summary>
    public IVisionTools? Tools
    {
        get => _tools;
        set => _tools = value;
    }

    /// <summary>The last result ("Position set to (1250, 640)." / "Cancelled …"), or null.</summary>
    public string? Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    private async Task PickAsync()
    {
        if (_tools is not { } tools || _isBusy) return;
        SetBusy(true);
        try
        {
            var point = await tools.PickPositionAsync();
            if (point is not { } p)
            {
                Status = "Picking was cancelled. The position wasn't changed.";
                return;
            }

            _setPosition(p.X, p.Y);
            Status = $"Position set to ({p.X}, {p.Y}). You can still change X and Y by hand.";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task UseCursorAsync()
    {
        if (_tools is not { } tools || _isBusy) return;
        SetBusy(true);
        try
        {
            for (var left = CursorCountdownSeconds; left > 0; left--)
            {
                Status = $"Move the mouse to the spot now… taking the position in {left} s.";
                await Task.Delay(1000);
            }

            var point = tools.GetCursorPosition();
            if (point is not { } p)
            {
                Status = "Windows didn't report the cursor position. The position wasn't changed.";
                return;
            }

            _setPosition(p.X, p.Y);
            Status = $"Position set to the cursor: ({p.X}, {p.Y}).";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }
}
