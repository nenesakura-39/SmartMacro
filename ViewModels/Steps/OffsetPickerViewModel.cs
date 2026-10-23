using SmartMacro.Core.Models;
using SmartMacro.Mvvm;
using SmartMacro.Services.Vision;

namespace SmartMacro.ViewModels.Steps;

/// <summary>
/// The "🎯 Pick offset from screen" button for steps that act on "the last found spot" (Mouse click, Move mouse) and
/// for an If step's "When found, also:" action. SmartMacro finds the reference target once (the Find step or If
/// condition that sets the last found spot), then you click the spot you want; the difference becomes Offset X / Y.
/// If the target isn't on screen right now, you click its center yourself first.
/// </summary>
public sealed class OffsetPickerViewModel : ObservableObject
{
    private readonly Action<int, int> _setOffset;
    private readonly Func<ScreenSearchStep?> _reference;
    private IVisionTools? _tools;
    private string? _status;
    private bool _isBusy;

    /// <param name="setOffset">Writes the picked offset into the step (and marks the macro as changed).</param>
    /// <param name="reference">The search whose "found spot" the offset is measured from (null = ask the user to click it).</param>
    public OffsetPickerViewModel(Action<int, int> setOffset, Func<ScreenSearchStep?> reference)
    {
        _setOffset = setOffset ?? throw new ArgumentNullException(nameof(setOffset));
        _reference = reference ?? (() => null);
        PickCommand = new AsyncRelayCommand(PickAsync, ex => Status = ex.Message, () => _tools is not null && !_isBusy);
    }

    public AsyncRelayCommand PickCommand { get; }

    /// <summary>Screen tools from the editor (set right after the step editor is created).</summary>
    public IVisionTools? Tools
    {
        get => _tools;
        set
        {
            _tools = value;
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>The last result ("Offset set to (+120, -40)…" / "Cancelled…"), or null.</summary>
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
            var reference = _reference();
            var pick = await tools.PickOffsetAsync(reference);
            if (pick is null)
            {
                Status = "Picking was cancelled. The offset wasn't changed.";
                return;
            }

            _setOffset(pick.OffsetX, pick.OffsetY);
            var how = pick.AnchorDetected
                ? $"measured from the target SmartMacro found at ({pick.Anchor.X}, {pick.Anchor.Y})"
                : $"measured from the center you clicked at ({pick.Anchor.X}, {pick.Anchor.Y})"
                  + (reference is null ? " (no Find step or If condition before this step)" : pick.AnchorNote is { } note ? $"; {note.TrimEnd('.')}" : "");
            Status = $"Offset set to ({pick.OffsetX:+0;-0;0}, {pick.OffsetY:+0;-0;0}), {how}. You can still change Offset X / Y by hand.";
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
