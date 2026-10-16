using System.Runtime.CompilerServices;
using SmartMacro.Core.Input;
using SmartMacro.Core.Models;
using SmartMacro.Core.Validation;
using SmartMacro.Mvvm;
using SmartMacro.Services.Ocr;

namespace SmartMacro.ViewModels.Steps;

/// <summary>Info shown in the "Add step" toolbox.</summary>
public sealed record StepTypeInfo(StepKind Kind, string DisplayName, string Glyph, string Description);

public static class StepTypeCatalog
{
    public static IReadOnlyList<StepTypeInfo> All { get; } = new[]
    {
        new StepTypeInfo(StepKind.Delay, StepDescriber.DisplayName(StepKind.Delay), "", "Wait for a set amount of time."),
        new StepTypeInfo(StepKind.MouseMove, StepDescriber.DisplayName(StepKind.MouseMove), "", "Move the cursor to a screen position."),
        new StepTypeInfo(StepKind.MouseClick, StepDescriber.DisplayName(StepKind.MouseClick), "", "Click, double-click, press or release a mouse button."),
        new StepTypeInfo(StepKind.MouseScroll, StepDescriber.DisplayName(StepKind.MouseScroll), "", "Scroll the mouse wheel up/down or left/right."),
        new StepTypeInfo(StepKind.KeyPress, StepDescriber.DisplayName(StepKind.KeyPress), "", "Press a key or shortcut, or hold/release a key."),
        new StepTypeInfo(StepKind.TypeText, StepDescriber.DisplayName(StepKind.TypeText), "", "Type a piece of text."),
        new StepTypeInfo(StepKind.FindText, StepDescriber.DisplayName(StepKind.FindText), "\uE721", "Wait for text to appear on screen (OCR), and optionally click it."),
    };

    public static StepTypeInfo Get(StepKind kind) => All.First(t => t.Kind == kind);
}

/// <summary>
/// Wraps one MacroStep for the editor. Edits go straight into the wrapped model;
/// the Modified event tells the editor to mark the macro dirty and re-validate.
/// </summary>
public abstract class StepViewModel : ObservableObject
{
    private int _number;
    private string? _issueText;
    private bool _hasError;
    private bool _hasWarning;

    protected StepViewModel(MacroStep model)
    {
        Model = model;
    }

    public event EventHandler? Modified;

    public MacroStep Model { get; }

    public Guid Id => Model.Id;

    public StepKind Kind => Model.Kind;

    public StepTypeInfo TypeInfo => StepTypeCatalog.Get(Kind);

    public string Title => TypeInfo.DisplayName;

    public string Glyph => TypeInfo.Glyph;

    public string Summary => StepDescriber.Describe(Model);

    /// <summary>1-based position shown in the list.</summary>
    public int Number
    {
        get => _number;
        set => SetProperty(ref _number, value);
    }

    public bool Enabled
    {
        get => Model.Enabled;
        set
        {
            if (Model.Enabled == value) return;
            Model.Enabled = value;
            NotifyChanged();
        }
    }

    public string? Comment
    {
        get => Model.Comment;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value;
            if (Model.Comment == normalized) return;
            Model.Comment = normalized;
            NotifyChanged();
            OnPropertyChanged(nameof(HasComment));
        }
    }

    public bool HasComment => !string.IsNullOrEmpty(Model.Comment);

    public string? IssueText
    {
        get => _issueText;
        private set => SetProperty(ref _issueText, value);
    }

    public bool HasError
    {
        get => _hasError;
        private set => SetProperty(ref _hasError, value);
    }

    public bool HasWarning
    {
        get => _hasWarning;
        private set => SetProperty(ref _hasWarning, value);
    }

    public void SetIssues(IEnumerable<ValidationIssue> issues)
    {
        var list = issues.ToList();
        HasError = list.Any(i => i.Severity == IssueSeverity.Error);
        HasWarning = !HasError && list.Any(i => i.Severity == IssueSeverity.Warning);
        IssueText = list.Count == 0 ? null : string.Join(Environment.NewLine, list.Select(i => i.Message));
    }

    /// <summary>Call after changing the model: updates the UI and tells the editor.</summary>
    protected void NotifyChanged([CallerMemberName] string? propertyName = null)
    {
        OnPropertyChanged(propertyName);
        OnPropertyChanged(nameof(Summary));
        Modified?.Invoke(this, EventArgs.Empty);
    }

    public static StepViewModel Create(MacroStep step) => step switch
    {
        DelayStep d => new DelayStepViewModel(d),
        MouseMoveStep m => new MouseMoveStepViewModel(m),
        MouseClickStep c => new MouseClickStepViewModel(c),
        MouseScrollStep s => new MouseScrollStepViewModel(s),
        KeyPressStep k => new KeyPressStepViewModel(k),
        TypeTextStep t => new TypeTextStepViewModel(t),
        FindTextStep f => new FindTextStepViewModel(f),
        _ => throw new NotSupportedException($"No editor for step type {step.GetType().Name}."),
    };
}

public sealed class DelayStepViewModel : StepViewModel
{
    private readonly DelayStep _step;

    public DelayStepViewModel(DelayStep step) : base(step)
    {
        _step = step;
        SetDurationCommand = new RelayCommand(p =>
        {
            if (p is not null && int.TryParse(p.ToString(), out var ms)) DurationMs = ms;
        });
    }

    public RelayCommand SetDurationCommand { get; }

    public int DurationMs
    {
        get => _step.DurationMs;
        set
        {
            if (_step.DurationMs == value) return;
            _step.DurationMs = value;
            NotifyChanged();
            OnPropertyChanged(nameof(SecondsText));
        }
    }

    public string SecondsText => $"= {_step.DurationMs / 1000.0:0.###} seconds";
}

public sealed class MouseMoveStepViewModel : StepViewModel
{
    private readonly MouseMoveStep _step;

    public MouseMoveStepViewModel(MouseMoveStep step) : base(step) => _step = step;

    public int X
    {
        get => _step.X;
        set { if (_step.X == value) return; _step.X = value; NotifyChanged(); }
    }

    public int Y
    {
        get => _step.Y;
        set { if (_step.Y == value) return; _step.Y = value; NotifyChanged(); }
    }
}

public sealed class MouseClickStepViewModel : StepViewModel
{
    private readonly MouseClickStep _step;

    public MouseClickStepViewModel(MouseClickStep step) : base(step) => _step = step;

    public static IReadOnlyList<MacroMouseButton> Buttons { get; } = Enum.GetValues<MacroMouseButton>();

    public static IReadOnlyList<MouseClickAction> Actions { get; } = Enum.GetValues<MouseClickAction>();

    public MacroMouseButton Button
    {
        get => _step.Button;
        set { if (_step.Button == value) return; _step.Button = value; NotifyChanged(); }
    }

    public MouseClickAction Action
    {
        get => _step.Action;
        set { if (_step.Action == value) return; _step.Action = value; NotifyChanged(); }
    }

    public bool UseCurrentPosition
    {
        get => _step.UseCurrentPosition;
        set
        {
            if (_step.UseCurrentPosition == value) return;
            _step.UseCurrentPosition = value;
            if (value) _step.UseLastFoundText = false;
            NotifyChanged();
            OnPropertyChanged(nameof(UseLastFoundText));
            OnPropertyChanged(nameof(UsesFixedPosition));
        }
    }

    /// <summary>Click where the last "Find text" step found its text.</summary>
    public bool UseLastFoundText
    {
        get => _step.UseLastFoundText;
        set
        {
            if (_step.UseLastFoundText == value) return;
            _step.UseLastFoundText = value;
            if (value) _step.UseCurrentPosition = false;
            NotifyChanged();
            OnPropertyChanged(nameof(UseCurrentPosition));
            OnPropertyChanged(nameof(UsesFixedPosition));
        }
    }

    public bool UsesFixedPosition => !_step.UseCurrentPosition && !_step.UseLastFoundText;

    public int X
    {
        get => _step.X;
        set { if (_step.X == value) return; _step.X = value; NotifyChanged(); }
    }

    public int Y
    {
        get => _step.Y;
        set { if (_step.Y == value) return; _step.Y = value; NotifyChanged(); }
    }
}

public sealed class MouseScrollStepViewModel : StepViewModel
{
    private readonly MouseScrollStep _step;

    public MouseScrollStepViewModel(MouseScrollStep step) : base(step) => _step = step;

    public int Delta
    {
        get => _step.Delta;
        set { if (_step.Delta == value) return; _step.Delta = value; NotifyChanged(); }
    }

    public bool Horizontal
    {
        get => _step.Horizontal;
        set { if (_step.Horizontal == value) return; _step.Horizontal = value; NotifyChanged(); }
    }

    public bool UseCurrentPosition
    {
        get => _step.UseCurrentPosition;
        set { if (_step.UseCurrentPosition == value) return; _step.UseCurrentPosition = value; NotifyChanged(); }
    }

    public int X
    {
        get => _step.X;
        set { if (_step.X == value) return; _step.X = value; NotifyChanged(); }
    }

    public int Y
    {
        get => _step.Y;
        set { if (_step.Y == value) return; _step.Y = value; NotifyChanged(); }
    }
}

public sealed class KeyPressStepViewModel : StepViewModel
{
    private readonly KeyPressStep _step;

    public KeyPressStepViewModel(KeyPressStep step) : base(step) => _step = step;

    public static IReadOnlyList<KeyInfo> Keys => VirtualKeys.All;

    public static IReadOnlyList<KeyAction> Actions { get; } = Enum.GetValues<KeyAction>();

    public int VirtualKey
    {
        get => _step.VirtualKey;
        set { if (_step.VirtualKey == value) return; _step.VirtualKey = value; NotifyChanged(); }
    }

    public KeyAction Action
    {
        get => _step.Action;
        set
        {
            if (_step.Action == value) return;
            _step.Action = value;
            NotifyChanged();
            OnPropertyChanged(nameof(IsPress));
        }
    }

    public bool IsPress => _step.Action == KeyAction.Press;

    public bool Ctrl
    {
        get => Has(KeyModifiers.Control);
        set => SetModifier(KeyModifiers.Control, value);
    }

    public bool Shift
    {
        get => Has(KeyModifiers.Shift);
        set => SetModifier(KeyModifiers.Shift, value);
    }

    public bool Alt
    {
        get => Has(KeyModifiers.Alt);
        set => SetModifier(KeyModifiers.Alt, value);
    }

    public bool Win
    {
        get => Has(KeyModifiers.Windows);
        set => SetModifier(KeyModifiers.Windows, value);
    }

    private bool Has(KeyModifiers modifier) => (_step.Modifiers & modifier) == modifier;

    private void SetModifier(KeyModifiers modifier, bool on, [CallerMemberName] string? propertyName = null)
    {
        var updated = on ? _step.Modifiers | modifier : _step.Modifiers & ~modifier;
        if (updated == _step.Modifiers) return;
        _step.Modifiers = updated;
        NotifyChanged(propertyName);
    }
}

public sealed class TypeTextStepViewModel : StepViewModel
{
    private readonly TypeTextStep _step;

    public TypeTextStepViewModel(TypeTextStep step) : base(step) => _step = step;

    public string Text
    {
        get => _step.Text ?? string.Empty;
        set
        {
            value ??= string.Empty;
            if (_step.Text == value) return;
            _step.Text = value;
            NotifyChanged();
            OnPropertyChanged(nameof(CharacterCountText));
        }
    }

    public string CharacterCountText => $"{(_step.Text ?? string.Empty).Length:N0} / {MacroValidator.MaxTextLength:N0} characters";

    public int CharacterDelayMs
    {
        get => _step.CharacterDelayMs;
        set { if (_step.CharacterDelayMs == value) return; _step.CharacterDelayMs = value; NotifyChanged(); }
    }
}

/// <summary>A choice in a drop-down: the stored value plus a friendly label.</summary>
public sealed record Choice<T>(T Value, string Label);

public sealed class FindTextStepViewModel : StepViewModel
{
    private readonly FindTextStep _step;
    private IFindTextTools? _tools;
    private string? _testResult;
    private bool _isBusy;

    public FindTextStepViewModel(FindTextStep step) : base(step)
    {
        _step = step;
        PickRegionCommand = new AsyncRelayCommand(PickRegionAsync, ex => TestResult = ex.Message, () => _tools is not null && !_isBusy);
        TestCommand = new AsyncRelayCommand(TestAsync, ex => TestResult = ex.Message, () => _tools is not null && !_isBusy);
    }

    public static IReadOnlyList<Choice<TextMatchMode>> MatchModes { get; } = new[]
    {
        new Choice<TextMatchMode>(TextMatchMode.Contains, "Contains the text (can be part of a word)"),
        new Choice<TextMatchMode>(TextMatchMode.WholeWords, "Whole word(s) only"),
    };

    public static IReadOnlyList<Choice<FoundTextAction>> Actions { get; } = new[]
    {
        new Choice<FoundTextAction>(FoundTextAction.None, "Just continue"),
        new Choice<FoundTextAction>(FoundTextAction.Move, "Move the mouse to it"),
        new Choice<FoundTextAction>(FoundTextAction.Click, "Click it"),
        new Choice<FoundTextAction>(FoundTextAction.DoubleClick, "Double-click it"),
        new Choice<FoundTextAction>(FoundTextAction.RightClick, "Right-click it"),
    };

    public static IReadOnlyList<Choice<TextNotFoundBehavior>> NotFoundOptions { get; } = new[]
    {
        new Choice<TextNotFoundBehavior>(TextNotFoundBehavior.StopMacro, "Stop the macro (show an error)"),
        new Choice<TextNotFoundBehavior>(TextNotFoundBehavior.Continue, "Skip and continue with the next step"),
    };

    public AsyncRelayCommand PickRegionCommand { get; }
    public AsyncRelayCommand TestCommand { get; }

    /// <summary>Screen tools from the editor (set right after creation).</summary>
    public IFindTextTools? Tools
    {
        get => _tools;
        set
        {
            _tools = value;
            OnPropertyChanged(nameof(OcrWarning));
        }
    }

    /// <summary>Shown when OCR isn't available on this PC.</summary>
    public string? OcrWarning => _tools is { IsOcrAvailable: false } tools ? tools.OcrStatus : null;

    public string? TestResult
    {
        get => _testResult;
        private set => SetProperty(ref _testResult, value);
    }

    public string Text
    {
        get => _step.Text ?? string.Empty;
        set
        {
            value ??= string.Empty;
            if (_step.Text == value) return;
            _step.Text = value;
            NotifyChanged();
        }
    }

    public bool SearchFullScreen
    {
        get => _step.SearchFullScreen;
        set
        {
            if (_step.SearchFullScreen == value) return;
            _step.SearchFullScreen = value;
            NotifyChanged();
            OnPropertyChanged(nameof(UsesRegion));
        }
    }

    public bool UsesRegion => !_step.SearchFullScreen;

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

    public TextMatchMode MatchMode
    {
        get => _step.MatchMode;
        set { if (_step.MatchMode == value) return; _step.MatchMode = value; NotifyChanged(); }
    }

    public bool CaseSensitive
    {
        get => _step.CaseSensitive;
        set { if (_step.CaseSensitive == value) return; _step.CaseSensitive = value; NotifyChanged(); }
    }

    public int MinSimilarityPercent
    {
        get => _step.MinSimilarityPercent;
        set { if (_step.MinSimilarityPercent == value) return; _step.MinSimilarityPercent = value; NotifyChanged(); }
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

    public FoundTextAction Action
    {
        get => _step.Action;
        set
        {
            if (_step.Action == value) return;
            _step.Action = value;
            NotifyChanged();
            OnPropertyChanged(nameof(HasPointerAction));
        }
    }

    public bool HasPointerAction => _step.Action != FoundTextAction.None;

    public int OffsetX
    {
        get => _step.OffsetX;
        set { if (_step.OffsetX == value) return; _step.OffsetX = value; NotifyChanged(); }
    }

    public int OffsetY
    {
        get => _step.OffsetY;
        set { if (_step.OffsetY == value) return; _step.OffsetY = value; NotifyChanged(); }
    }

    public TextNotFoundBehavior WhenNotFound
    {
        get => _step.WhenNotFound;
        set { if (_step.WhenNotFound == value) return; _step.WhenNotFound = value; NotifyChanged(); }
    }

    private async Task PickRegionAsync()
    {
        if (_tools is null) return;
        SetBusy(true);
        try
        {
            var picked = await _tools.PickRegionAsync();
            if (picked is not { } rect)
            {
                TestResult = "Picking the area was cancelled.";
                return;
            }

            _step.SearchFullScreen = false;
            _step.RegionX = rect.X;
            _step.RegionY = rect.Y;
            _step.RegionWidth = rect.Width;
            _step.RegionHeight = rect.Height;
            OnPropertyChanged(string.Empty); // refresh all fields
            NotifyChanged(nameof(RegionX));  // mark the macro as changed + re-validate
            TestResult = $"Search area set to {rect}.";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task TestAsync()
    {
        if (_tools is null) return;
        SetBusy(true);
        TestResult = "Reading the screen…";
        try
        {
            TestResult = await _tools.TestAsync(_step);
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
