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
        new StepTypeInfo(StepKind.MouseMove, StepDescriber.DisplayName(StepKind.MouseMove), "", "Moves the cursor straight to a screen position (it jumps there; it isn't animated). Use \"Pick position\" to choose the spot on your screen."),
        new StepTypeInfo(StepKind.MouseClick, StepDescriber.DisplayName(StepKind.MouseClick), "", "Clicks, double-clicks, holds for a set time or drags with a mouse button: at a chosen screen position, where the cursor already is, or on what a Find step found."),
        new StepTypeInfo(StepKind.MouseScroll, StepDescriber.DisplayName(StepKind.MouseScroll), "", "Turns the mouse wheel up, down, left or right by a number of notches, where the cursor already is or at a chosen screen position."),
        new StepTypeInfo(StepKind.KeyPress, StepDescriber.DisplayName(StepKind.KeyPress), "", "Press a key or shortcut, or hold/release a key."),
        new StepTypeInfo(StepKind.TypeText, StepDescriber.DisplayName(StepKind.TypeText), "", "Type a piece of text."),
        new StepTypeInfo(StepKind.FindText, StepDescriber.DisplayName(StepKind.FindText), "\uE721", "Wait for text to appear on screen (OCR), and optionally click it."),
        new StepTypeInfo(StepKind.FindImage, StepDescriber.DisplayName(StepKind.FindImage), "\uE8B9", "Wait for a picture (button, icon…) to appear on screen or disappear, and optionally click it."),
        new StepTypeInfo(StepKind.FindColor, StepDescriber.DisplayName(StepKind.FindColor), "\uE790", "Wait for a color to appear in an area (or disappear), and optionally click it. Simple color matching, not object recognition."),
        new StepTypeInfo(StepKind.FeedbackLoop, StepDescriber.DisplayName(StepKind.FeedbackLoop), "\uE895", "Keeps a moving marker in a target zone: watches a bar on screen many times per second and holds, releases or taps one key or mouse button depending on where the marker is. Always has a max runtime; Stop and Esc work at any time."),
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
    private bool _isRunning;

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

    /// <summary>
    /// The plain-English banner at the top of the step's properties ("Searches Main Monitor every 250 ms…"), or null
    /// for steps that don't need one. Refreshed whenever the step changes.
    /// </summary>
    public virtual string? PlainSummary => null;

    /// <summary>True while playback is running this step (the list shows a "▶ Running" badge and scrolls to it).</summary>
    public bool IsRunning
    {
        get => _isRunning;
        set => SetProperty(ref _isRunning, value);
    }

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
        OnPropertyChanged(nameof(PlainSummary));
        Modified?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>"Movement style" choices shared by Move mouse, Mouse click and the Find steps.</summary>
    public static IReadOnlyList<Choice<MouseMoveStyle>> MoveStyles { get; } = new[]
    {
        new Choice<MouseMoveStyle>(MouseMoveStyle.SmoothGlide, "Smooth Glide (straight line, recommended)"),
        new Choice<MouseMoveStyle>(MouseMoveStyle.HumanCurve, "Human Curve (natural arc, eases in and out)"),
        new Choice<MouseMoveStyle>(MouseMoveStyle.Instant, "Instant (jump straight there)"),
    };

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
        FeedbackLoopStep f => new FeedbackLoopStepViewModel(f),
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

    public MouseMoveStepViewModel(MouseMoveStep step) : base(step)
    {
        _step = step;
        PositionPicker = new PositionPickerViewModel(SetPosition);
    }

    /// <summary>"Pick position" / "Use cursor position" for the destination.</summary>
    public PositionPickerViewModel PositionPicker { get; }

    private void SetPosition(int x, int y)
    {
        _step.X = x;
        _step.Y = y;
        OnPropertyChanged(nameof(X));
        NotifyChanged(nameof(Y));
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

    /// <summary>How the cursor travels there (older steps: Instant).</summary>
    public MouseMoveStyle MoveStyle
    {
        get => _step.MoveStyle ?? MouseMoveStyle.Instant;
        set
        {
            if (_step.MoveStyle == value) return;
            _step.MoveStyle = value;
            NotifyChanged();
            OnPropertyChanged(nameof(Glides));
            OnPropertyChanged(nameof(MoveHelp));
        }
    }

    public bool Glides => MoveStyle != MouseMoveStyle.Instant;

    public int MoveDurationMs
    {
        get => _step.MoveDurationMs;
        set { if (_step.MoveDurationMs == value) return; _step.MoveDurationMs = value; NotifyChanged(); }
    }

    public string MoveHelp => Glides
        ? "The cursor glides to this spot (about 120 updates per second), so hover effects, sliders and game buttons see it arrive. Nothing is clicked."
        : "The cursor jumps straight to this spot and nothing is clicked. Add a Mouse click step to click, or use Mouse click on its own: it can move and click in one step.";
}

public sealed class MouseClickStepViewModel : StepViewModel
{
    private readonly MouseClickStep _step;

    public MouseClickStepViewModel(MouseClickStep step) : base(step)
    {
        _step = step;
        PositionPicker = new PositionPickerViewModel(SetPosition);
        EndPoint = new PointEditorViewModel(
            () => _step.EndX, v => _step.EndX = v,
            () => _step.EndY, v => _step.EndY = v,
            () => NotifyChanged(nameof(EndPoint)));
    }

    public static IReadOnlyList<MacroMouseButton> Buttons { get; } = Enum.GetValues<MacroMouseButton>();

    public static IReadOnlyList<Choice<MouseClickAction>> Actions { get; } = new[]
    {
        new Choice<MouseClickAction>(MouseClickAction.Click, "Single click"),
        new Choice<MouseClickAction>(MouseClickAction.DoubleClick, "Double click"),
        new Choice<MouseClickAction>(MouseClickAction.Hold, "Hold for a duration (ms)"),
        new Choice<MouseClickAction>(MouseClickAction.Drag, "Drag to a position"),
        new Choice<MouseClickAction>(MouseClickAction.Down, "Press down only (release in a later step)"),
        new Choice<MouseClickAction>(MouseClickAction.Up, "Release only"),
    };

    /// <summary>Drag: where the drag ends ("Pick position" works here too).</summary>
    public PointEditorViewModel EndPoint { get; }

    public static IReadOnlyList<Choice<ClickTarget>> Targets { get; } = new[]
    {
        new Choice<ClickTarget>(ClickTarget.ChosenPosition, "At a chosen position (X / Y below)"),
        new Choice<ClickTarget>(ClickTarget.CurrentCursor, "Where the cursor already is"),
        new Choice<ClickTarget>(ClickTarget.LastFound, "On what the last Find text / image / color step found"),
    };

    /// <summary>"Pick position" / "Use cursor position" for the click position.</summary>
    public PositionPickerViewModel PositionPicker { get; }

    /// <summary>Where the click happens: one choice instead of two check boxes (same two settings underneath).</summary>
    public ClickTarget Target
    {
        get => _step.UseLastFoundText ? ClickTarget.LastFound : _step.UseCurrentPosition ? ClickTarget.CurrentCursor : ClickTarget.ChosenPosition;
        set
        {
            if (Target == value) return;
            _step.UseLastFoundText = value == ClickTarget.LastFound;
            _step.UseCurrentPosition = value == ClickTarget.CurrentCursor;
            NotifyChanged();
            OnPropertyChanged(nameof(UseCurrentPosition));
            OnPropertyChanged(nameof(UseLastFoundText));
            OnPropertyChanged(nameof(UsesFixedPosition));
        }
    }

    private void SetPosition(int x, int y)
    {
        _step.X = x;
        _step.Y = y;
        Target = ClickTarget.ChosenPosition; // picking a spot means "click there"
        OnPropertyChanged(nameof(X));
        NotifyChanged(nameof(Y));
    }

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
            if (value == MouseClickAction.Hold && _step.HoldMs <= 0) _step.HoldMs = MouseClickStep.DefaultHoldActionMs;
            if (value == MouseClickAction.Drag && _step.EndX == 0 && _step.EndY == 0)
            {
                // Start the end point a little to the right of the start, so the drag does something visible.
                _step.EndX = _step.X + 100;
                _step.EndY = _step.Y;
                EndPoint.Refresh();
            }

            NotifyChanged();
            OnPropertyChanged(nameof(IsClick));
            OnPropertyChanged(nameof(UsesHoldTime));
            OnPropertyChanged(nameof(IsDrag));
            OnPropertyChanged(nameof(IsDoubleClick));
            OnPropertyChanged(nameof(UsesClickTiming));
            OnPropertyChanged(nameof(HoldMs));
            OnPropertyChanged(nameof(ActionHelp));
        }
    }

    public bool IsClick => _step.Action == MouseClickAction.Click;

    /// <summary>The hold-time box is used by Single click (recorded hold), Double click (each click) and Hold.</summary>
    public bool UsesHoldTime => _step.Action is MouseClickAction.Click or MouseClickAction.DoubleClick or MouseClickAction.Hold;

    public bool IsDrag => _step.Action == MouseClickAction.Drag;

    /// <summary>Drag: how long the glide from start to end takes.</summary>
    public int DragDurationMs
    {
        get => _step.DragDurationMs;
        set { if (_step.DragDurationMs == value) return; _step.DragDurationMs = value; NotifyChanged(); }
    }

    public string ActionHelp => _step.Action switch
    {
        MouseClickAction.Hold => "Presses the button, keeps it down for the time below, then releases it. Good for casting meters and hold-to-buy buttons.",
        MouseClickAction.Drag => "Presses the button at the start position, glides smoothly to the end position over the drag time while holding it, then releases. Good for sliders and dragging items.",
        MouseClickAction.Down => "Only presses the button. Add a \"Release only\" step later to let go (everything held is released when the macro ends).",
        MouseClickAction.Up => "Only releases the button (pressed by an earlier \"Press down only\" step).",
        MouseClickAction.DoubleClick => "Two quick clicks, each held for the click hold time, with the double-click gap in between.",
        _ => "One click. The click hold keeps the button down that long (35 ms is reliable in games; recordings fill in the real time).",
    };

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
            OnPropertyChanged(nameof(Target));
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
            OnPropertyChanged(nameof(Target));
        }
    }

    public bool UsesFixedPosition => !_step.UseCurrentPosition && !_step.UseLastFoundText;

    /// <summary>How long a Click keeps the button down (0 = instant).</summary>
    public int HoldMs
    {
        get => _step.HoldMs;
        set { if (_step.HoldMs == value) return; _step.HoldMs = value; NotifyChanged(); }
    }

    /// <summary>How the cursor travels to the click spot (older steps: Instant).</summary>
    public MouseMoveStyle MoveStyle
    {
        get => _step.MoveStyle ?? MouseMoveStyle.Instant;
        set
        {
            if (_step.MoveStyle == value) return;
            _step.MoveStyle = value;
            NotifyChanged();
            OnPropertyChanged(nameof(Glides));
        }
    }

    public bool Glides => MoveStyle != MouseMoveStyle.Instant;

    public int MoveDurationMs
    {
        get => _step.MoveDurationMs;
        set { if (_step.MoveDurationMs == value) return; _step.MoveDurationMs = value; NotifyChanged(); }
    }

    /// <summary>Double click: gap between the two clicks.</summary>
    public int DoubleClickIntervalMs
    {
        get => _step.DoubleClickIntervalMs;
        set { if (_step.DoubleClickIntervalMs == value) return; _step.DoubleClickIntervalMs = value; NotifyChanged(); }
    }

    public bool IsDoubleClick => _step.Action == MouseClickAction.DoubleClick;

    /// <summary>"Wait after click (ms)".</summary>
    public int PostActionDelayMs
    {
        get => _step.PostActionDelayMs;
        set { if (_step.PostActionDelayMs == value) return; _step.PostActionDelayMs = value; NotifyChanged(); }
    }

    /// <summary>Hold time also applies to a timed double click.</summary>
    public bool UsesClickTiming => _step.Action is MouseClickAction.Click or MouseClickAction.DoubleClick or MouseClickAction.Hold;

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

/// <summary>
/// An extra X / Y pair with its own "Pick position" buttons (e.g. where a Drag ends). Works with the shared
/// PositionPickerTemplate, which binds X, Y and PositionPicker.
/// </summary>
public sealed class PointEditorViewModel : Mvvm.ObservableObject
{
    private readonly Func<int> _getX;
    private readonly Action<int> _setX;
    private readonly Func<int> _getY;
    private readonly Action<int> _setY;
    private readonly Action _changed;

    public PointEditorViewModel(Func<int> getX, Action<int> setX, Func<int> getY, Action<int> setY, Action changed)
    {
        _getX = getX;
        _setX = setX;
        _getY = getY;
        _setY = setY;
        _changed = changed;
        PositionPicker = new PositionPickerViewModel((x, y) =>
        {
            _setX(x);
            _setY(y);
            Refresh();
            _changed();
        });
    }

    public PositionPickerViewModel PositionPicker { get; }

    public int X
    {
        get => _getX();
        set { if (_getX() == value) return; _setX(value); OnPropertyChanged(); _changed(); }
    }

    public int Y
    {
        get => _getY();
        set { if (_getY() == value) return; _setY(value); OnPropertyChanged(); _changed(); }
    }

    /// <summary>Re-reads X / Y after the step was changed elsewhere.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(X));
        OnPropertyChanged(nameof(Y));
    }
}

/// <summary>Where a Mouse click happens.</summary>
public enum ClickTarget
{
    ChosenPosition,
    CurrentCursor,
    LastFound,
}

/// <summary>Which way the wheel turns (a Scroll wheel step's sign + horizontal setting, as one choice).</summary>
public enum ScrollDirection
{
    Up,
    Down,
    Left,
    Right,
}

/// <summary>
/// Scroll wheel editor. The step stores Windows' wheel amount (120 = one notch, positive = up / right) and a
/// "horizontal" flag; the editor shows that as Direction + Number of notches. Stored values are only changed when
/// the user edits them, so old macros keep exactly what they had (including amounts that aren't whole notches).
/// </summary>
public sealed class MouseScrollStepViewModel : StepViewModel
{
    /// <summary>Windows' wheel amount for one notch (WHEEL_DELTA).</summary>
    public const int NotchDelta = 120;

    private readonly MouseScrollStep _step;

    public MouseScrollStepViewModel(MouseScrollStep step) : base(step)
    {
        _step = step;
        PositionPicker = new PositionPickerViewModel(SetPosition);
    }

    public static IReadOnlyList<Choice<ScrollDirection>> Directions { get; } = new[]
    {
        new Choice<ScrollDirection>(ScrollDirection.Up, "Up"),
        new Choice<ScrollDirection>(ScrollDirection.Down, "Down"),
        new Choice<ScrollDirection>(ScrollDirection.Left, "Left (sideways)"),
        new Choice<ScrollDirection>(ScrollDirection.Right, "Right (sideways)"),
    };

    public static IReadOnlyList<Choice<bool>> PositionModes { get; } = new[]
    {
        new Choice<bool>(true, "Where the cursor already is"),
        new Choice<bool>(false, "At a chosen position (X / Y below)"),
    };

    /// <summary>"Pick position" / "Use cursor position" for where to scroll.</summary>
    public PositionPickerViewModel PositionPicker { get; }

    /// <summary>Raw Windows amount (120 = one notch). Shown under "exact amount" for recorded / odd values.</summary>
    public int Delta
    {
        get => _step.Delta;
        set
        {
            if (_step.Delta == value) return;
            _step.Delta = value;
            NotifyChanged();
            OnPropertyChanged(nameof(Direction));
            OnPropertyChanged(nameof(Notches));
            OnPropertyChanged(nameof(AmountText));
        }
    }

    public ScrollDirection Direction
    {
        get => _step.Horizontal
            ? (_step.Delta >= 0 ? ScrollDirection.Right : ScrollDirection.Left)
            : (_step.Delta >= 0 ? ScrollDirection.Up : ScrollDirection.Down);
        set
        {
            if (Direction == value) return;
            var horizontal = value is ScrollDirection.Left or ScrollDirection.Right;
            var positive = value is ScrollDirection.Up or ScrollDirection.Right;
            var size = Math.Abs((long)_step.Delta);
            if (size == 0) size = NotchDelta;
            _step.Horizontal = horizontal;
            _step.Delta = (int)(positive ? size : -size);
            NotifyChanged();
            OnPropertyChanged(nameof(Horizontal));
            OnPropertyChanged(nameof(Delta));
            OnPropertyChanged(nameof(Notches));
            OnPropertyChanged(nameof(AmountText));
        }
    }

    /// <summary>Whole notches. Typing a number sets the amount to that many notches in the current direction.</summary>
    public int Notches
    {
        get => (int)(Math.Abs((long)_step.Delta) / NotchDelta);
        set
        {
            if (Notches == value && Math.Abs((long)_step.Delta) % NotchDelta == 0) return;
            var positive = Direction is ScrollDirection.Up or ScrollDirection.Right;
            var size = Math.Clamp((long)value, 0, int.MaxValue / NotchDelta) * NotchDelta;
            _step.Delta = (int)(positive ? size : -size);
            NotifyChanged();
            OnPropertyChanged(nameof(Delta));
            OnPropertyChanged(nameof(AmountText));
        }
    }

    /// <summary>Explains the amount in words, including amounts that aren't whole notches (some recordings / touchpads).</summary>
    public string AmountText
    {
        get
        {
            var size = Math.Abs((long)_step.Delta);
            var notches = size / (double)NotchDelta;
            var exact = size % NotchDelta == 0 ? "" : $" Exact amount: {size} (= {notches:0.##} notches; Windows counts 120 per notch).";
            return $"1 notch = one click of a normal mouse wheel (often about 3 lines of text). Several notches are sent one by one, 15 ms apart, so games and browsers count every one.{exact}";
        }
    }

    public bool Horizontal
    {
        get => _step.Horizontal;
        set
        {
            if (_step.Horizontal == value) return;
            _step.Horizontal = value;
            NotifyChanged();
            OnPropertyChanged(nameof(Direction));
        }
    }

    public bool UseCurrentPosition
    {
        get => _step.UseCurrentPosition;
        set
        {
            if (_step.UseCurrentPosition == value) return;
            _step.UseCurrentPosition = value;
            NotifyChanged();
            OnPropertyChanged(nameof(UsesFixedPosition));
        }
    }

    public bool UsesFixedPosition => !_step.UseCurrentPosition;

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

    private void SetPosition(int x, int y)
    {
        _step.X = x;
        _step.Y = y;
        UseCurrentPosition = false; // picking a spot means "scroll there"
        OnPropertyChanged(nameof(X));
        NotifyChanged(nameof(Y));
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
    private ScreenPoint? _testMatch;
    private bool? _testFound;
    private IScreenLayout? _layout;

    protected ScreenSearchStepViewModel(ScreenSearchStep step) : base(step)
    {
        _search = step;
        PickRegionCommand = new AsyncRelayCommand(PickRegionAsync, ex => TestResult = ex.Message, () => HasTools && !_isBusy);
        ShowMatchCommand = new RelayCommand(() =>
        {
            if (_testMatch is { } spot) ShowSpot(spot);
        }, () => _testMatch is not null && !_isBusy);
        RefreshMonitorsCommand = new RelayCommand(RefreshMonitors);
        RefreshWindowsCommand = new RelayCommand(RefreshWindows);
        RefreshMonitors();
    }

    // ---------------- "Where to look" ----------------

    /// <summary>The "Where to look" choices. New steps start on the main monitor.</summary>
    public static IReadOnlyList<Choice<SearchScope>> Scopes { get; } = new[]
    {
        new Choice<SearchScope>(SearchScope.PrimaryMonitor, "Main Monitor (Primary Screen)"),
        new Choice<SearchScope>(SearchScope.Monitor, "Specific Monitor…"),
        new Choice<SearchScope>(SearchScope.Window, "Specific App Window…"),
        new Choice<SearchScope>(SearchScope.CustomArea, "Custom Area on Screen"),
        new Choice<SearchScope>(SearchScope.AllMonitors, "All Monitors (Entire Desktop)"),
    };

    /// <summary>Monitors and windows from the editor (set right after creation), for the monitor / window lists.</summary>
    public IScreenLayout? Layout
    {
        get => _layout;
        set
        {
            _layout = value;
            RefreshMonitors();
            if (EffectiveScopeIsWindow) RefreshWindows();
        }
    }

    public RelayCommand RefreshMonitorsCommand { get; }
    public RelayCommand RefreshWindowsCommand { get; }

    /// <summary>"Monitor 1 — Primary (1920×1080)", "Monitor 2 (1920×1080)", …</summary>
    public System.Collections.ObjectModel.ObservableCollection<Choice<int>> Monitors { get; } = new();

    /// <summary>Titles of the app windows open right now (front to back).</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> WindowTitles { get; } = new();

    public SearchScope Scope
    {
        get => _search.EffectiveScope;
        set
        {
            if (_search.Scope == value) return;
            _search.Scope = value;
            if (value == SearchScope.Monitor) RefreshMonitors();
            if (value == SearchScope.Window) RefreshWindows();
            NotifyChanged();
            OnPropertyChanged(nameof(SearchFullScreen));
            NotifyScopeChanged();
        }
    }

    private bool EffectiveScopeIsWindow => _search.EffectiveScope == SearchScope.Window;

    public bool UsesMonitor => _search.EffectiveScope == SearchScope.Monitor;

    public bool UsesWindow => _search.EffectiveScope == SearchScope.Window;

    public int MonitorNumber
    {
        get => _search.MonitorNumber;
        set { if (_search.MonitorNumber == value || value < 1) return; _search.MonitorNumber = value; NotifyChanged(); OnPropertyChanged(nameof(ScopeHelp)); }
    }

    /// <summary>The window to search in: (part of) its title.</summary>
    public string WindowTitle
    {
        get => _search.WindowTitle ?? string.Empty;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (_search.WindowTitle == normalized) return;
            _search.WindowTitle = normalized;
            NotifyChanged();
            OnPropertyChanged(nameof(SelectedWindowTitle));
            OnPropertyChanged(nameof(ScopeHelp));
        }
    }

    /// <summary>The drop-down of open windows: choosing one fills in <see cref="WindowTitle"/>.</summary>
    public string? SelectedWindowTitle
    {
        get => WindowTitles.FirstOrDefault(t => string.Equals(t, _search.WindowTitle, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (!string.IsNullOrWhiteSpace(value)) WindowTitle = value;
        }
    }

    public string ScopeHelp => _search.EffectiveScope switch
    {
        SearchScope.PrimaryMonitor => "Only your main monitor is searched. Faster than all monitors, and it can't match something on a second screen by mistake.",
        SearchScope.Monitor => "Only the chosen monitor is searched. Click \"Refresh monitors\" after plugging a monitor in or out.",
        SearchScope.Window => string.IsNullOrWhiteSpace(_search.WindowTitle)
            ? "Choose an open window (e.g. Roblox). Only the inside of that window is searched, wherever it is on screen when the step runs."
            : $"Searches inside the window whose title contains \"{_search.WindowTitle}\", wherever it is when the step runs. If it isn't open, the step keeps checking until its time is up.",
        SearchScope.CustomArea => "Only the area below is searched. The smaller the area, the faster the search and the fewer wrong matches.",
        _ => "Every monitor is searched. Slowest, and it can match things on other screens (chat apps, browsers) by mistake.",
    };

    private void NotifyScopeChanged()
    {
        OnPropertyChanged(nameof(UsesRegion));
        OnPropertyChanged(nameof(UsesMonitor));
        OnPropertyChanged(nameof(UsesWindow));
        OnPropertyChanged(nameof(ScopeHelp));
    }

    private void RefreshMonitors()
    {
        Monitors.Clear();
        IReadOnlyList<MonitorInfo> monitors;
        try
        {
            monitors = _layout?.GetMonitors() ?? Array.Empty<MonitorInfo>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            monitors = Array.Empty<MonitorInfo>();
        }

        foreach (var monitor in monitors) Monitors.Add(new Choice<int>(monitor.Number, monitor.Label));
        // A monitor the macro was made for but that isn't connected now stays selectable (and the validator explains).
        if (Monitors.All(m => m.Value != _search.MonitorNumber))
            Monitors.Add(new Choice<int>(_search.MonitorNumber, $"Monitor {_search.MonitorNumber} (not connected right now)"));
        OnPropertyChanged(nameof(MonitorNumber));
    }

    private void RefreshWindows()
    {
        WindowTitles.Clear();
        IReadOnlyList<WindowInfo> windows;
        try
        {
            windows = _layout?.GetWindows() ?? Array.Empty<WindowInfo>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            windows = Array.Empty<WindowInfo>();
        }

        foreach (var title in windows.Select(w => w.Title).Distinct(StringComparer.OrdinalIgnoreCase)) WindowTitles.Add(title);
        OnPropertyChanged(nameof(SelectedWindowTitle));
    }

    // ---------------- Movement and click timing (Advanced) ----------------

    /// <summary>How the cursor gets to what was found (older steps: Instant).</summary>
    public MouseMoveStyle MoveStyle
    {
        get => _search.MoveStyle ?? MouseMoveStyle.Instant;
        set
        {
            if (_search.MoveStyle == value) return;
            _search.MoveStyle = value;
            NotifyChanged();
            OnPropertyChanged(nameof(Glides));
        }
    }

    public bool Glides => MoveStyle != MouseMoveStyle.Instant;

    public int MoveDurationMs
    {
        get => _search.MoveDurationMs;
        set { if (_search.MoveDurationMs == value) return; _search.MoveDurationMs = value; NotifyChanged(); }
    }

    public int ClickHoldMs
    {
        get => _search.ClickHoldMs;
        set { if (_search.ClickHoldMs == value) return; _search.ClickHoldMs = value; NotifyChanged(); }
    }

    public int DoubleClickIntervalMs
    {
        get => _search.DoubleClickIntervalMs;
        set { if (_search.DoubleClickIntervalMs == value) return; _search.DoubleClickIntervalMs = value; NotifyChanged(); }
    }

    /// <summary>"Wait after click (ms)".</summary>
    public int PostActionDelayMs
    {
        get => _search.PostActionDelayMs;
        set { if (_search.PostActionDelayMs == value) return; _search.PostActionDelayMs = value; NotifyChanged(); }
    }

    /// <summary>"4. Search timing" in a Find step; "3. …" inside a condition (which has no "when found" card).</summary>
    public string TimingCardTitle => IsStandalone ? "4. Search timing" : "3. Search timing";

    /// <summary>The live plain-English summary banner (only for a standalone Find step, not inside a condition).</summary>
    public override string? PlainSummary => IsStandalone ? StepDescriber.Explain(_search) : null;

    /// <summary>
    /// "When found" choices. Click / double / right-click / hold happen at the CENTER of what was found (plus the
    /// offset), so a button that moves around the screen (a "Shake" button, a shop item) is still hit.
    /// </summary>
    public static IReadOnlyList<Choice<FoundTextAction>> Actions { get; } = new[]
    {
        new Choice<FoundTextAction>(FoundTextAction.None, "Do nothing (just continue)"),
        new Choice<FoundTextAction>(FoundTextAction.Click, "Click its center (left click)"),
        new Choice<FoundTextAction>(FoundTextAction.DoubleClick, "Double-click its center"),
        new Choice<FoundTextAction>(FoundTextAction.RightClick, "Right-click its center"),
        new Choice<FoundTextAction>(FoundTextAction.Move, "Move the mouse to it (no click)"),
        new Choice<FoundTextAction>(FoundTextAction.Hold, "Hold the left button on it for…"),
    };

    public static IReadOnlyList<Choice<TextNotFoundBehavior>> NotFoundOptions { get; } = new[]
    {
        new Choice<TextNotFoundBehavior>(TextNotFoundBehavior.Continue, "Continue to the next step"),
        new Choice<TextNotFoundBehavior>(TextNotFoundBehavior.SkipNextStep, "Skip the next step, then continue"),
        new Choice<TextNotFoundBehavior>(TextNotFoundBehavior.StopMacro, "Stop the macro (show an error)"),
    };

    /// <summary>Moves the cursor to where the last "Test detection now" found the match, so you can see it.</summary>
    public RelayCommand ShowMatchCommand { get; }

    /// <summary>True when the last test found something (the "Show me" button is enabled).</summary>
    public bool HasTestMatch => _testMatch is not null;

    /// <summary>Whether the last test found it (null before any test). Conditions turn this into "true / false right now".</summary>
    public bool? TestFound
    {
        get => _testFound;
        private set => SetProperty(ref _testFound, value);
    }

    /// <summary>Moves the cursor with this step's own screen tools.</summary>
    protected abstract void ShowSpot(ScreenPoint point);

    /// <summary>Shows a test's text and remembers where it matched (null = not found).</summary>
    protected void SetTestResult(string text, ScreenPoint? match, bool found)
    {
        TestResult = text;
        _testMatch = match;
        TestFound = found;
        OnPropertyChanged(nameof(HasTestMatch));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

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

    /// <summary>The old "whole screen" switch (all monitors on / custom area off). The editor now uses <see cref="Scope"/>.</summary>
    public bool SearchFullScreen
    {
        get => _search.SearchFullScreen;
        set
        {
            if (_search.SearchFullScreen == value) return;
            _search.SearchFullScreen = value;
            NotifyChanged();
            OnPropertyChanged(nameof(Scope));
            NotifyScopeChanged();
        }
    }

    public bool UsesRegion => _search.EffectiveScope == SearchScope.CustomArea;

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
            OnPropertyChanged(nameof(IsHoldAction));
            OnPropertyChanged(nameof(NotFoundLabel));
        }
    }

    /// <summary>The "when found" options only matter while waiting for something to appear.</summary>
    public bool WaitsForAppear => _search.Goal == WaitGoal.Appear;

    public string NotFoundLabel => _search.Goal == WaitGoal.Appear ? "When not found (time is up)" : "If it's still there when the time is up";

    public FoundTextAction Action
    {
        get => _search.Action;
        set
        {
            if (_search.Action == value) return;
            _search.Action = value;
            NotifyChanged();
            OnPropertyChanged(nameof(HasPointerAction));
            OnPropertyChanged(nameof(IsHoldAction));
        }
    }

    public bool HasPointerAction => _search.Goal == WaitGoal.Appear && _search.Action != FoundTextAction.None;

    /// <summary>Shows the hold time box for "hold the left button on it".</summary>
    public bool IsHoldAction => _search.Goal == WaitGoal.Appear && _search.Action == FoundTextAction.Hold;

    /// <summary>How long "hold the left button on it" holds, in ms.</summary>
    public int ActionHoldMs
    {
        get => _search.ActionHoldMs;
        set { if (_search.ActionHoldMs == value) return; _search.ActionHoldMs = value; NotifyChanged(); }
    }

    /// <summary>"Move the cursor away after clicking" (so its hover effect doesn't hide the next thing to find).</summary>
    public bool MoveCursorAwayAfterAction
    {
        get => _search.MoveCursorAwayAfterAction;
        set { if (_search.MoveCursorAwayAfterAction == value) return; _search.MoveCursorAwayAfterAction = value; NotifyChanged(); }
    }

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
        set
        {
            if (_search.WhenNotFound == value) return;
            _search.WhenNotFound = value;
            NotifyChanged();
            OnPropertyChanged(nameof(NotFoundHelp));
        }
    }

    /// <summary>Plain-English note under the "when the time is up" choice, so it's clear what runs next.</summary>
    public string NotFoundHelp => _search.WhenNotFound switch
    {
        TextNotFoundBehavior.StopMacro => "Nothing below this step runs. The macro stops and the log says what was on screen.",
        TextNotFoundBehavior.SkipNextStep => "The step right below (or the whole If / Repeat block starting there) is skipped, then the macro goes on. Use this to make a Find step guard the action after it.",
        _ => "The steps below run as if it had been found. A later click on \"the last found spot\" stops with an error until something is found again.",
    };

    /// <summary>Lets the user drag the search area (each step type uses its own tool).</summary>
    protected abstract Task<ScreenRect?> PickRegionCoreAsync();

    /// <summary>Sets the search area (and turns off "whole screen"), marking the macro as changed.</summary>
    protected void SetRegion(ScreenRect rect)
    {
        _search.Scope = SearchScope.CustomArea; // picking an area means "search here"
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

    public bool NormalizeLookAlikes
    {
        get => _step.NormalizeLookAlikes;
        set { if (_step.NormalizeLookAlikes == value) return; _step.NormalizeLookAlikes = value; NotifyChanged(); }
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
        var text = await _tools.TestAsync(_step);
        SetTestResult(text, _tools.LastTestMatch, _tools.LastTestMatch is not null);
    });

    protected override void ShowSpot(ScreenPoint point) => _tools?.ShowSpot(point);
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

    /// <summary>"Ignore background / match bright text and edges" (see-through game UI).</summary>
    public bool MatchForeground
    {
        get => _step.MatchForeground;
        set { if (_step.MatchForeground == value) return; _step.MatchForeground = value; NotifyChanged(); }
    }

    /// <summary>"Ignore the edges (%)": 0 = compare the whole picture.</summary>
    public int IgnoreEdgesPercent
    {
        get => _step.IgnoreEdgesPercent;
        set
        {
            if (_step.IgnoreEdgesPercent == value) return;
            _step.IgnoreEdgesPercent = value;
            NotifyChanged();
            OnPropertyChanged(nameof(IgnoreEdges));
        }
    }

    /// <summary>Quick on/off for the recommended 15 % edge margin (round buttons, game UI).</summary>
    public bool IgnoreEdges
    {
        get => _step.IgnoreEdgesPercent > 0;
        set
        {
            if (IgnoreEdges == value) return;
            IgnoreEdgesPercent = value ? FindImageStep.RecommendedIgnoreEdgesPercent : 0;
            OnPropertyChanged(nameof(IgnoreEdgesPercent));
        }
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
        var text = await _tools.TestImageAsync(_step);
        SetTestResult(text, _tools.LastTestMatch, _tools.LastTestMatch is not null);
    });

    protected override void ShowSpot(ScreenPoint point) => _tools?.ShowSpot(point);

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
        var text = await _tools.TestColorAsync(_step);
        SetTestResult(text, _tools.LastTestMatch, _tools.LastTestMatch is not null);
    });

    protected override void ShowSpot(ScreenPoint point) => _tools?.ShowSpot(point);

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
