using System.Runtime.CompilerServices;
using SmartMacro.Core.Input;
using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Vision;
using SmartMacro.Mvvm;
using SmartMacro.Services.Ocr;
using SmartMacro.Services.Vision;

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
        new StepTypeInfo(StepKind.FindImage, StepDescriber.DisplayName(StepKind.FindImage), "\uE8B9", "Wait for a picture (button, icon…) to appear on screen or disappear, and optionally click it."),
        new StepTypeInfo(StepKind.FindColor, StepDescriber.DisplayName(StepKind.FindColor), "\uE790", "Wait for a color to appear in an area (or disappear), and optionally click it. Simple color matching, not object recognition."),
        new StepTypeInfo(StepKind.InputSequence, StepDescriber.DisplayName(StepKind.InputSequence), "\uE7FC", "Mouse and keyboard input recorded on one timeline and played back with its original timing: exact key and button down/up, plus cursor positions or relative (camera) mouse movement."),
        new StepTypeInfo(StepKind.If, StepDescriber.DisplayName(StepKind.If), "\uE8AB", "Runs the steps inside only when a condition is true: some text, an image or a color is (or is not) on screen. Adding it from the toolbox adds the If and its End If."),
        new StepTypeInfo(StepKind.Else, StepDescriber.DisplayName(StepKind.Else), "\uE76C", "Optional, inside an If block: the steps after Else run when the If's condition is false."),
        new StepTypeInfo(StepKind.EndIf, StepDescriber.DisplayName(StepKind.EndIf), "\uE70E", "Closes an If block."),
        new StepTypeInfo(StepKind.Repeat, StepDescriber.DisplayName(StepKind.Repeat), "\uE8EE", "Runs the steps inside several times, or while / until a condition is true. Loops always have a safety limit. Adding it from the toolbox adds the Repeat and its End Repeat."),
        new StepTypeInfo(StepKind.EndRepeat, StepDescriber.DisplayName(StepKind.EndRepeat), "\uE70E", "Closes a Repeat block: playback goes back to the Repeat for the next turn."),
    };

    private static readonly HashSet<StepKind> LogicKinds = new() { StepKind.If, StepKind.Else, StepKind.EndIf, StepKind.Repeat, StepKind.EndRepeat };

    /// <summary>Action step types offered in the editor's "Add step" toolbox (recorded input only comes from recording).</summary>
    public static IReadOnlyList<StepTypeInfo> Addable { get; } = All.Where(t => t.Kind != StepKind.InputSequence && !LogicKinds.Contains(t.Kind)).ToList();

    /// <summary>Block steps offered in the toolbox's "Logic" section.</summary>
    public static IReadOnlyList<StepTypeInfo> Logic { get; } = All.Where(t => LogicKinds.Contains(t.Kind)).ToList();

    public static StepTypeInfo Get(StepKind kind) => All.First(t => t.Kind == kind);
}

/// <summary>
/// Wraps one MacroStep for the editor. Edits go straight into the wrapped model;
/// the Modified event tells the editor to mark the macro dirty and re-validate.
/// </summary>
public abstract class StepViewModel : ObservableObject
{
    private int _number;
    private int _indentLevel;
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

    /// <summary>How many If / Repeat blocks this step is inside (set by the editor; used for indentation).</summary>
    public int IndentLevel
    {
        get => _indentLevel;
        set
        {
            if (SetProperty(ref _indentLevel, value)) OnPropertyChanged(nameof(IndentMargin));
        }
    }

    /// <summary>Left indentation of the row in the step list.</summary>
    public System.Windows.Thickness IndentMargin => new(_indentLevel * 22, 0, 0, 0);

    /// <summary>True for If / Else / End If / Repeat / End Repeat.</summary>
    public bool IsBlockStep => WorkflowSteps.IsBlockStep(Model);

    /// <summary>
    /// False when this editor is shown inside an If / Repeat condition. Then the parts that conditions don't use
    /// (title, enabled switch, "when found" action, comment) are hidden.
    /// </summary>
    public bool IsStandalone { get; internal set; } = true;

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
        FindImageStep i => new FindImageStepViewModel(i),
        FindColorStep c => new FindColorStepViewModel(c),
        InputSequenceStep q => new InputSequenceStepViewModel(q),
        IfStep i => new IfStepViewModel(i),
        RepeatStep r => new RepeatStepViewModel(r),
        ElseStep or EndIfStep or EndRepeatStep => new BlockMarkerStepViewModel(step),
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
        set
        {
            if (_step.Action == value) return;
            _step.Action = value;
            NotifyChanged();
            OnPropertyChanged(nameof(IsClick));
        }
    }

    public bool IsClick => _step.Action == MouseClickAction.Click;

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

    /// <summary>Click where the last Find text / image / color step found something.</summary>
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

    /// <summary>How long a Click keeps the button down (0 = instant).</summary>
    public int HoldMs
    {
        get => _step.HoldMs;
        set { if (_step.HoldMs == value) return; _step.HoldMs = value; NotifyChanged(); }
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

    /// <summary>How long a Press keeps the key down (0 = instant).</summary>
    public int HoldMs
    {
        get => _step.HoldMs;
        set { if (_step.HoldMs == value) return; _step.HoldMs = value; NotifyChanged(); }
    }

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

/// <summary>
/// Shared editor part of Find text / Find image / Find color: search area, timing, what to wait for, what to do
/// with the result, plus "busy" handling for the screen tools (only one tool runs at a time).
/// </summary>
public abstract class ScreenSearchStepViewModel : StepViewModel
{
    private readonly ScreenSearchStep _search;
    private string? _testResult;
    private bool _isBusy;

    protected ScreenSearchStepViewModel(ScreenSearchStep step) : base(step)
    {
        _search = step;
        PickRegionCommand = new AsyncRelayCommand(PickRegionAsync, ex => TestResult = ex.Message, () => HasTools && !_isBusy);
    }

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

    public static IReadOnlyList<Choice<WaitGoal>> Goals { get; } = new[]
    {
        new Choice<WaitGoal>(WaitGoal.Appear, "Wait until it appears"),
        new Choice<WaitGoal>(WaitGoal.Disappear, "Wait until it disappears"),
    };

    public AsyncRelayCommand PickRegionCommand { get; }

    /// <summary>True once the editor has given this step its screen tools.</summary>
    protected abstract bool HasTools { get; }

    protected bool IsBusy => _isBusy;

    public string? TestResult
    {
        get => _testResult;
        protected set => SetProperty(ref _testResult, value);
    }

    public bool SearchFullScreen
    {
        get => _search.SearchFullScreen;
        set
        {
            if (_search.SearchFullScreen == value) return;
            _search.SearchFullScreen = value;
            NotifyChanged();
            OnPropertyChanged(nameof(UsesRegion));
        }
    }

    public bool UsesRegion => !_search.SearchFullScreen;

    public int RegionX
    {
        get => _search.RegionX;
        set { if (_search.RegionX == value) return; _search.RegionX = value; NotifyChanged(); }
    }

    public int RegionY
    {
        get => _search.RegionY;
        set { if (_search.RegionY == value) return; _search.RegionY = value; NotifyChanged(); }
    }

    public int RegionWidth
    {
        get => _search.RegionWidth;
        set { if (_search.RegionWidth == value) return; _search.RegionWidth = value; NotifyChanged(); }
    }

    public int RegionHeight
    {
        get => _search.RegionHeight;
        set { if (_search.RegionHeight == value) return; _search.RegionHeight = value; NotifyChanged(); }
    }

    public int TimeoutMs
    {
        get => _search.TimeoutMs;
        set { if (_search.TimeoutMs == value) return; _search.TimeoutMs = value; NotifyChanged(); }
    }

    public int CheckIntervalMs
    {
        get => _search.CheckIntervalMs;
        set { if (_search.CheckIntervalMs == value) return; _search.CheckIntervalMs = value; NotifyChanged(); }
    }

    public WaitGoal Goal
    {
        get => _search.Goal;
        set
        {
            if (_search.Goal == value) return;
            _search.Goal = value;
            NotifyChanged();
            OnPropertyChanged(nameof(WaitsForAppear));
            OnPropertyChanged(nameof(HasPointerAction));
            OnPropertyChanged(nameof(NotFoundLabel));
        }
    }

    /// <summary>The "when found" options only matter while waiting for something to appear.</summary>
    public bool WaitsForAppear => _search.Goal == WaitGoal.Appear;

    public string NotFoundLabel => _search.Goal == WaitGoal.Appear ? "If it's not found in time" : "If it's still there when the time is up";

    public FoundTextAction Action
    {
        get => _search.Action;
        set
        {
            if (_search.Action == value) return;
            _search.Action = value;
            NotifyChanged();
            OnPropertyChanged(nameof(HasPointerAction));
        }
    }

    public bool HasPointerAction => _search.Goal == WaitGoal.Appear && _search.Action != FoundTextAction.None;

    public int OffsetX
    {
        get => _search.OffsetX;
        set { if (_search.OffsetX == value) return; _search.OffsetX = value; NotifyChanged(); }
    }

    public int OffsetY
    {
        get => _search.OffsetY;
        set { if (_search.OffsetY == value) return; _search.OffsetY = value; NotifyChanged(); }
    }

    public TextNotFoundBehavior WhenNotFound
    {
        get => _search.WhenNotFound;
        set { if (_search.WhenNotFound == value) return; _search.WhenNotFound = value; NotifyChanged(); }
    }

    /// <summary>Lets the user drag the search area (each step type uses its own tool).</summary>
    protected abstract Task<ScreenRect?> PickRegionCoreAsync();

    /// <summary>Sets the search area (and turns off "whole screen"), marking the macro as changed.</summary>
    protected void SetRegion(ScreenRect rect)
    {
        _search.SearchFullScreen = false;
        _search.RegionX = rect.X;
        _search.RegionY = rect.Y;
        _search.RegionWidth = rect.Width;
        _search.RegionHeight = rect.Height;
        OnPropertyChanged(string.Empty); // refresh all fields
        NotifyChanged(nameof(RegionX));  // mark the macro as changed + re-validate
    }

    /// <summary>Runs a screen tool with the buttons disabled; errors end up in the result box.</summary>
    protected async Task RunBusyAsync(Func<Task> action)
    {
        if (_isBusy) return;
        SetBusy(true);
        try
        {
            await action();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private Task PickRegionAsync() => RunBusyAsync(async () =>
    {
        var picked = await PickRegionCoreAsync();
        if (picked is not { } rect)
        {
            TestResult = "Picking the area was cancelled.";
            return;
        }

        SetRegion(rect);
        TestResult = $"Search area set to {rect}.";
    });

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }
}

public sealed class FindTextStepViewModel : ScreenSearchStepViewModel
{
    private readonly FindTextStep _step;
    private IFindTextTools? _tools;

    public FindTextStepViewModel(FindTextStep step) : base(step)
    {
        _step = step;
        TestCommand = new AsyncRelayCommand(TestAsync, ex => TestResult = ex.Message, () => _tools is not null && !IsBusy);
    }

    public static IReadOnlyList<Choice<TextMatchMode>> MatchModes { get; } = new[]
    {
        new Choice<TextMatchMode>(TextMatchMode.Contains, "Contains the text (can be part of a word)"),
        new Choice<TextMatchMode>(TextMatchMode.WholeWords, "Whole word(s) only"),
    };

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

    protected override bool HasTools => _tools is not null;

    /// <summary>Shown when OCR isn't available on this PC.</summary>
    public string? OcrWarning => _tools is { IsOcrAvailable: false } tools ? tools.OcrStatus : null;

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

    protected override Task<ScreenRect?> PickRegionCoreAsync() =>
        _tools?.PickRegionAsync() ?? Task.FromResult<ScreenRect?>(null);

    private Task TestAsync() => RunBusyAsync(async () =>
    {
        if (_tools is null) return;
        TestResult = "Reading the screen…";
        TestResult = await _tools.TestAsync(_step);
    });
}

/// <summary>Editor for "Find image": capture the picture, choose where and how strictly to search, test it.</summary>
public sealed class FindImageStepViewModel : ScreenSearchStepViewModel
{
    private readonly FindImageStep _step;
    private IVisionTools? _tools;
    private System.Windows.Media.ImageSource? _preview;

    public FindImageStepViewModel(FindImageStep step) : base(step)
    {
        _step = step;
        CaptureTemplateCommand = new AsyncRelayCommand(CaptureTemplateAsync, ex => TestResult = ex.Message, () => _tools is not null && !IsBusy);
        TestCommand = new AsyncRelayCommand(TestAsync, ex => TestResult = ex.Message, () => _tools is not null && !IsBusy);
    }

    public AsyncRelayCommand CaptureTemplateCommand { get; }
    public AsyncRelayCommand TestCommand { get; }

    /// <summary>Screen tools from the editor (set right after creation).</summary>
    public IVisionTools? Tools
    {
        get => _tools;
        set
        {
            _tools = value;
            RefreshPreview();
        }
    }

    protected override bool HasTools => _tools is not null;

    /// <summary>The stored picture, for the editor to show. Null if none (or damaged).</summary>
    public System.Windows.Media.ImageSource? TemplatePreview
    {
        get => _preview;
        private set => SetProperty(ref _preview, value);
    }

    public bool HasTemplate => !string.IsNullOrEmpty(_step.TemplateData);

    public string TemplateInfo => HasTemplate
        ? $"{_step.TemplateWidth} × {_step.TemplateHeight} pixels"
        : "No image yet. Click \"Capture image\" and drag a box around the button or icon to find.";

    public int MinSimilarityPercent
    {
        get => _step.MinSimilarityPercent;
        set { if (_step.MinSimilarityPercent == value) return; _step.MinSimilarityPercent = value; NotifyChanged(); }
    }

    public bool IgnoreColor
    {
        get => _step.IgnoreColor;
        set { if (_step.IgnoreColor == value) return; _step.IgnoreColor = value; NotifyChanged(); }
    }

    protected override Task<ScreenRect?> PickRegionCoreAsync() =>
        _tools?.PickRegionAsync() ?? Task.FromResult<ScreenRect?>(null);

    private Task CaptureTemplateAsync() => RunBusyAsync(async () =>
    {
        if (_tools is null) return;
        var captured = await _tools.CaptureTemplateAsync();
        if (captured is null)
        {
            TestResult = "Capturing the image was cancelled.";
            return;
        }

        var image = captured.Image;
        if (image.Width < MacroValidator.MinTemplateSize || image.Height < MacroValidator.MinTemplateSize
            || image.Width > MacroValidator.MaxTemplateSize || image.Height > MacroValidator.MaxTemplateSize)
        {
            TestResult = $"The box was {image.Width} × {image.Height} pixels. The image must be {MacroValidator.MinTemplateSize} to {MacroValidator.MaxTemplateSize} pixels on each side; please drag again.";
            return;
        }

        if (TemplateMatcher.IsFlat(image))
        {
            TestResult = "That area is one flat color, so its shape can't be matched. Capture an area with some detail (text, an icon edge), or use a \"Find color\" step instead.";
            return;
        }

        _step.TemplateWidth = image.Width;
        _step.TemplateHeight = image.Height;
        _step.TemplateData = TemplateCodec.Encode(image);
        RefreshPreview();
        OnPropertyChanged(nameof(HasTemplate));
        OnPropertyChanged(nameof(TemplateInfo));
        NotifyChanged(nameof(TemplatePreview));

        var b = captured.Bounds;
        TestResult = $"Image captured ({image.Width} × {image.Height}) at ({b.X}, {b.Y}). Tip: a search area around where it appears makes the search faster. Click \"Test now\" to check it.";
    });

    private Task TestAsync() => RunBusyAsync(async () =>
    {
        if (_tools is null) return;
        TestResult = "Looking at the screen…";
        TestResult = await _tools.TestImageAsync(_step);
    });

    private void RefreshPreview()
    {
        TemplatePreview = _tools is not null
                          && TemplateCodec.TryDecode(_step.TemplateData, _step.TemplateWidth, _step.TemplateHeight, out var image, out _)
            ? _tools.ToImageSource(image!)
            : null;
    }
}

/// <summary>Editor for "Find color": pick the color, tolerance and area, test it, or watch it move.</summary>
public sealed class FindColorStepViewModel : ScreenSearchStepViewModel
{
    private static readonly TimeSpan TrackDuration = TimeSpan.FromSeconds(3);

    private readonly FindColorStep _step;
    private IVisionTools? _tools;

    public FindColorStepViewModel(FindColorStep step) : base(step)
    {
        _step = step;
        PickColorCommand = new AsyncRelayCommand(PickColorAsync, ex => TestResult = ex.Message, () => _tools is not null && !IsBusy);
        TestCommand = new AsyncRelayCommand(TestAsync, ex => TestResult = ex.Message, () => _tools is not null && !IsBusy);
        TrackCommand = new AsyncRelayCommand(TrackAsync, ex => TestResult = ex.Message, () => _tools is not null && !IsBusy);
    }

    public AsyncRelayCommand PickColorCommand { get; }
    public AsyncRelayCommand TestCommand { get; }
    public AsyncRelayCommand TrackCommand { get; }

    /// <summary>Screen tools from the editor (set right after creation).</summary>
    public IVisionTools? Tools
    {
        get => _tools;
        set => _tools = value;
    }

    protected override bool HasTools => _tools is not null;

    public string Color
    {
        get => _step.Color ?? string.Empty;
        set
        {
            value = (value ?? string.Empty).Trim();
            if (_step.Color == value) return;
            _step.Color = value;
            NotifyChanged();
            OnPropertyChanged(nameof(ColorBrush));
        }
    }

    /// <summary>A swatch of the colour (transparent while the text isn't a valid #RRGGBB).</summary>
    public System.Windows.Media.Brush ColorBrush
    {
        get
        {
            if (!RgbColor.TryParse(_step.Color, out var c)) return System.Windows.Media.Brushes.Transparent;
            var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(c.R, c.G, c.B));
            brush.Freeze();
            return brush;
        }
    }

    public int Tolerance
    {
        get => _step.Tolerance;
        set { if (_step.Tolerance == value) return; _step.Tolerance = value; NotifyChanged(); }
    }

    public int MinPixels
    {
        get => _step.MinPixels;
        set { if (_step.MinPixels == value) return; _step.MinPixels = value; NotifyChanged(); }
    }

    protected override Task<ScreenRect?> PickRegionCoreAsync() =>
        _tools?.PickRegionAsync() ?? Task.FromResult<ScreenRect?>(null);

    private Task PickColorAsync() => RunBusyAsync(async () =>
    {
        if (_tools is null) return;
        var picked = await _tools.PickColorAsync();
        if (picked is null)
        {
            TestResult = "Picking the color was cancelled.";
            return;
        }

        Color = picked.Color.ToString();
        TestResult = $"Color {picked.Color} picked at ({picked.X}, {picked.Y}). Click \"Test now\" to see what matches.";
    });

    private Task TestAsync() => RunBusyAsync(async () =>
    {
        if (_tools is null) return;
        TestResult = "Looking at the screen…";
        TestResult = await _tools.TestColorAsync(_step);
    });

    private Task TrackAsync() => RunBusyAsync(async () =>
    {
        if (_tools is null) return;
        TestResult = $"Tracking for {TrackDuration.TotalSeconds:0} seconds…";
        TestResult = await _tools.TrackColorAsync(_step, TrackDuration);
    });
}

/// <summary>
/// Read-only overview of a recording (thousands of events aren't edited one by one), the
/// "start from the recorded cursor position" option, and — for smart recordings — how each part plays the mouse.
/// </summary>
public sealed class InputSequenceStepViewModel : StepViewModel
{
    private readonly InputSequenceStep _step;
    private InputSequenceSummary _summary;

    public InputSequenceStepViewModel(InputSequenceStep step) : base(step)
    {
        _step = step;
        _summary = StepDescriber.Summarize(step);

        // A damaged file could contain empty entries; the validator reports those, the editor just skips them.
        var segments = (step.MouseSegments ?? new List<MouseSegment>()).Where(segment => segment is not null).ToList();
        var lastUs = step.Events is { Count: > 0 } events ? events[^1].TimeUs : 0;
        MouseParts = segments
            .Select((segment, index) => new MouseSegmentViewModel(
                segment, index + 1,
                index + 1 < segments.Count ? segments[index + 1].StartUs : Math.Max(lastUs, segment.StartUs),
                OnMousePartChanged))
            .ToList();

        UseCursorEverywhereCommand = new RelayCommand(() => SetAll(MouseModeChoice.CursorPosition), () => HasMouseParts);
        UseRelativeEverywhereCommand = new RelayCommand(() => SetAll(MouseModeChoice.Relative), () => HasMouseParts);
        UseAutomaticEverywhereCommand = new RelayCommand(() => SetAll(MouseModeChoice.Automatic), () => HasMouseParts);
    }

    /// <summary>Smart recordings: one entry per mouse part. Empty for game-mode recordings.</summary>
    public IReadOnlyList<MouseSegmentViewModel> MouseParts { get; }

    public bool HasMouseParts => MouseParts.Count > 0;

    /// <summary>Game-mode recordings (always relative) show the older explanation.</summary>
    public bool IsGameModeRecording => !HasMouseParts;

    public RelayCommand UseCursorEverywhereCommand { get; }
    public RelayCommand UseRelativeEverywhereCommand { get; }
    public RelayCommand UseAutomaticEverywhereCommand { get; }

    public string PlaybackExplanation => HasMouseParts
        ? "Played back with the original timing. Each part below plays the mouse one way only: \"Cursor position\" puts the cursor on the recorded screen spot (menus, buttons); \"Relative\" sends the recorded mouse movement (camera turning while a game locks the cursor). SmartMacro guessed each part while recording; change a part if it guessed wrong. The macro's Speed setting also applies here."
        : "Recorded in game mode: played back with the original timing, as relative mouse movement and exact key down/up. Start from the same spot and camera direction you recorded from. The macro's Speed setting also applies here.";

    public string DurationText => $"{_summary.DurationMs / 1000.0:0.00} s";

    public string EventCountText => $"{_summary.EventCount:N0}";

    public string MouseText => HasMouseParts
        ? $"{_summary.MouseMoves:N0} moves, each with a cursor position and relative counts ({_summary.CursorParts} cursor-position part(s), {_summary.RelativeParts} relative part(s))"
        : $"{_summary.MouseMoves:N0} moves (total {_summary.TotalAbsDx:N0} horizontal, {_summary.TotalAbsDy:N0} vertical counts)";

    private void SetAll(MouseModeChoice choice)
    {
        var changed = false;
        foreach (var part in MouseParts) changed |= part.SetChoiceSilently(choice);
        if (changed) OnMousePartChanged();
    }

    private void OnMousePartChanged()
    {
        _summary = StepDescriber.Summarize(_step);
        OnPropertyChanged(nameof(MouseText));
        NotifyChanged(nameof(MouseParts));
    }

    public string KeysText => _summary.KeyPresses == 0
        ? "None"
        : $"{_summary.KeyPresses:N0} presses: {string.Join(", ", _summary.KeysUsed)}{(_summary.KeysUsed.Count >= 12 ? ", …" : "")}";

    public string ButtonsText => $"{_summary.ButtonPresses:N0} button presses, {_summary.WheelEvents:N0} wheel events";

    public bool MoveCursorToStart
    {
        get => _step.MoveCursorToStart;
        set { if (_step.MoveCursorToStart == value) return; _step.MoveCursorToStart = value; NotifyChanged(); }
    }

    public int StartX
    {
        get => _step.StartX;
        set { if (_step.StartX == value) return; _step.StartX = value; NotifyChanged(); }
    }

    public int StartY
    {
        get => _step.StartY;
        set { if (_step.StartY == value) return; _step.StartY = value; NotifyChanged(); }
    }
}

/// <summary>One mouse part of a smart recording in the editor: what was detected, and the user's choice.</summary>
public sealed class MouseSegmentViewModel : ObservableObject
{
    private readonly MouseSegment _segment;
    private readonly Action _changed;

    public MouseSegmentViewModel(MouseSegment segment, int number, long endUs, Action changed)
    {
        _segment = segment;
        _changed = changed;
        Number = number;
        RangeText = $"{segment.StartUs / 1_000_000.0:0.00} s – {endUs / 1_000_000.0:0.00} s";
    }

    public static IReadOnlyList<Choice<MouseModeChoice>> Choices { get; } = new[]
    {
        new Choice<MouseModeChoice>(MouseModeChoice.Automatic, "Automatic (use what was detected)"),
        new Choice<MouseModeChoice>(MouseModeChoice.CursorPosition, "Cursor position (menus, buttons)"),
        new Choice<MouseModeChoice>(MouseModeChoice.Relative, "Relative movement (camera)"),
    };

    public int Number { get; }

    public string Title => $"Part {Number}";

    public string RangeText { get; }

    public string DetectedText => $"Detected: {ModeName(_segment.Detected)} — {EvidenceText(_segment.Evidence)}";

    public string EffectiveText => $"Plays as: {ModeName(_segment.Effective)}";

    public MouseModeChoice SelectedMode
    {
        get => _segment.Choice;
        set
        {
            if (!SetChoiceSilently(value)) return;
            _changed();
        }
    }

    /// <summary>Changes the choice without telling the step (used by the "all parts" buttons, which tell it once).</summary>
    internal bool SetChoiceSilently(MouseModeChoice value)
    {
        if (_segment.Choice == value || !Enum.IsDefined(value)) return false;
        _segment.Choice = value;
        OnPropertyChanged(nameof(SelectedMode));
        OnPropertyChanged(nameof(EffectiveText));
        return true;
    }

    private static string ModeName(MouseMovementMode mode) => mode == MouseMovementMode.Relative ? "relative movement" : "cursor position";

    private static string EvidenceText(MouseContextEvidence evidence) => evidence switch
    {
        MouseContextEvidence.FreeCursor => "the cursor followed the mouse",
        MouseContextEvidence.HiddenCursor => "the cursor was hidden",
        MouseContextEvidence.ConfinedCursor => "the cursor was confined to one spot",
        MouseContextEvidence.LockedCursor => "the mouse moved but the cursor stayed in place",
        _ => "not enough movement to tell, so it follows the part before",
    };
}
