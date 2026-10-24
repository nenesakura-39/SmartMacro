using SmartMacro.Core.Models;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Vision;
using SmartMacro.Mvvm;
using SmartMacro.Services.Ocr;
using SmartMacro.Services.Vision;
using MacroCondition = SmartMacro.Core.Models.Condition;

namespace SmartMacro.ViewModels.Steps;

/// <summary>
/// The screen tools (Pick area, Capture image, Pick color, Test now) the editor hands to steps that look at the
/// screen, including the search inside an If / Repeat condition and a Feedback loop (with its conditions).
/// </summary>
public sealed record ScreenTools(IFindTextTools? Text, IVisionTools? Vision, IScreenLayout? Layout = null)
{
    public static ScreenTools None { get; } = new(null, null);

    /// <summary>Gives a step editor the tools it needs (does nothing for steps that don't use any).</summary>
    public void ApplyTo(StepViewModel step)
    {
        if (step is ScreenSearchStepViewModel search) search.Layout = Layout; // monitor / window lists
        switch (step)
        {
            case MouseMoveStepViewModel move:
                move.PositionPicker.Tools = Vision;
                move.OffsetPicker.Tools = Vision;
                break;
            case MouseClickStepViewModel click:
                click.PositionPicker.Tools = Vision;
                click.EndPoint.PositionPicker.Tools = Vision;
                click.OffsetPicker.Tools = Vision;
                click.VisionTools = Vision; // 🎯 Test Smart-Aim
                break;
            case MouseScrollStepViewModel scroll:
                scroll.PositionPicker.Tools = Vision;
                break;
            case FindTextStepViewModel findText:
                findText.Tools = Text;
                break;
            case FindImageStepViewModel findImage:
                findImage.Tools = Vision;
                break;
            case FindColorStepViewModel findColor:
                findColor.Tools = Vision;
                break;
            case IfStepViewModel ifStep:
                ifStep.Tools = this;
                ifStep.OffsetPicker.Tools = Vision;
                break;
            case RepeatStepViewModel repeat:
                repeat.Tools = this;
                break;
            case FeedbackLoopStepViewModel feedback:
                feedback.Tools = this;
                break;
            case WaitForChangeStepViewModel waitForChange:
                waitForChange.Tools = Vision;
                break;
            case FocusWindowStepViewModel focus:
                focus.Layout = Layout;
                break;
            case ScreenshotStepViewModel screenshot:
                screenshot.Tools = Vision;
                screenshot.Layout = Layout;
                break;
        }
    }
}

/// <summary>
/// Editor for a condition: what to look for (text / image / color), "is" or "is NOT" on screen, and the search
/// settings, which are shown with the normal Find text / Find image / Find color editor (so Pick area, Capture
/// image, Pick color and Test now all work here too). The parts conditions don't use are hidden.
/// </summary>
public sealed class ConditionViewModel : ObservableObject
{
    private readonly MacroCondition _condition;
    private readonly Action _changed;
    private ScreenTools _tools = ScreenTools.None;
    private ScreenSearchStepViewModel? _checkEditor;

    public ConditionViewModel(MacroCondition condition, Action changed)
    {
        _condition = condition;
        _changed = changed;
        RebuildCheckEditor();
    }

    public static IReadOnlyList<Choice<ConditionKind>> Kinds { get; } = new[]
    {
        new Choice<ConditionKind>(ConditionKind.TextVisible, "Text (read with OCR)"),
        new Choice<ConditionKind>(ConditionKind.ImageVisible, "Image (button, icon…)"),
        new Choice<ConditionKind>(ConditionKind.ColorVisible, "Color"),
    };

    public static IReadOnlyList<Choice<bool>> NegateOptions { get; } = new[]
    {
        new Choice<bool>(false, "is on screen"),
        new Choice<bool>(true, "is NOT on screen"),
    };

    public ScreenTools Tools
    {
        get => _tools;
        set
        {
            _tools = value ?? ScreenTools.None;
            if (_checkEditor is not null) _tools.ApplyTo(_checkEditor);
        }
    }

    /// <summary>What the condition looks for. Null when the stored condition is damaged (pick a kind to fix it).</summary>
    public ConditionKind? Kind
    {
        get => _condition.Kind;
        set
        {
            if (value is not { } kind || _condition.Kind == kind) return;

            // Keep the area and the wait time when switching between text / image / color.
            var old = _condition.Search;
            var check = MacroCondition.CreateCheck(kind);
            check.Scope = SearchScope.PrimaryMonitor;
            if (old is not null)
            {
                check.SearchFullScreen = old.SearchFullScreen;
                check.Scope = old.EffectiveScope;
                check.MonitorNumber = old.MonitorNumber;
                check.WindowTitle = old.WindowTitle;
                check.RegionX = old.RegionX;
                check.RegionY = old.RegionY;
                check.RegionWidth = old.RegionWidth;
                check.RegionHeight = old.RegionHeight;
                check.TimeoutMs = old.TimeoutMs;
            }

            _condition.Check = check;
            RebuildCheckEditor();
            OnPropertyChanged();
            _changed();
        }
    }

    public bool Negate
    {
        get => _condition.Negate;
        set
        {
            if (_condition.Negate == value) return;
            _condition.Negate = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TestHint));
            OnPropertyChanged(nameof(TestVerdict));
            _changed();
        }
    }

    /// <summary>After "Test detection now": what the whole condition (with "is" / "is NOT") answers right now.</summary>
    public string? TestVerdict => _checkEditor?.TestFound is not { } found
        ? null
        : (found != _condition.Negate)
            ? $"Right now this condition is TRUE ({(found ? "it was found" : "it was not found")})."
            : $"Right now this condition is FALSE ({(found ? "it was found" : "it was not found")}).";

    /// <summary>The Find text / image / color editor for the search settings (null if the condition is damaged).</summary>
    public ScreenSearchStepViewModel? CheckEditor
    {
        get => _checkEditor;
        private set => SetProperty(ref _checkEditor, value);
    }

    public bool IsDamaged => _checkEditor is null;

    public string TestHint => _condition.Negate
        ? "\"Test detection now\" shows whether it's found. With \"is NOT on screen\" the condition is true when it is NOT found."
        : "\"Test detection now\" shows whether it's found. The condition is true when it is found.";

    private void RebuildCheckEditor()
    {
        if (_checkEditor is not null)
        {
            _checkEditor.Modified -= OnCheckModified;
            _checkEditor.PropertyChanged -= OnCheckPropertyChanged;
        }

        ScreenSearchStepViewModel? editor = null;
        if (_condition.Search is { } search && StepViewModel.Create(search) is ScreenSearchStepViewModel created)
        {
            editor = created;
            editor.IsStandalone = false;
            _tools.ApplyTo(editor);
            editor.Modified += OnCheckModified;
            editor.PropertyChanged += OnCheckPropertyChanged;
        }

        CheckEditor = editor;
        OnPropertyChanged(nameof(IsDamaged));
        OnPropertyChanged(nameof(TestVerdict));
    }

    private void OnCheckModified(object? sender, EventArgs e) => _changed();

    private void OnCheckPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScreenSearchStepViewModel.TestFound)) OnPropertyChanged(nameof(TestVerdict));
    }
}

/// <summary>Editor for an If step: its condition and the optional "when true" action on what it saw.</summary>
public sealed class IfStepViewModel : StepViewModel
{
    private readonly IfStep _step;

    public IfStepViewModel(IfStep step) : base(step)
    {
        _step = step;
        if (step.Condition is null) step.Condition = new MacroCondition(); // a damaged file may have no condition; the validator reports it
        Condition = new ConditionViewModel(step.Condition, () =>
        {
            NotifyChanged(nameof(Condition));
            OnPropertyChanged(nameof(ThenActionHint));
            OnPropertyChanged(nameof(ShowsWhenFound));
        });

        // "Pick offset from screen": measured from what this If's own condition finds.
        OffsetPicker = new OffsetPickerViewModel(SetFoundOffset, () => _step.Condition is { Negate: false } c ? c.Search : null);
    }

    public ConditionViewModel Condition { get; }

    // ---------------- AAA: "When found, also:" ----------------

    /// <summary>"When found, also:" choices (shown when the condition is an "is on screen" check).</summary>
    public static IReadOnlyList<Choice<IfFoundAction>> WhenFoundActions { get; } = new[]
    {
        new Choice<IfFoundAction>(IfFoundAction.None, "Just run the steps inside the If"),
        new Choice<IfFoundAction>(IfFoundAction.ClickFoundSpot, "Click the found spot"),
        new Choice<IfFoundAction>(IfFoundAction.DoubleClickFoundSpot, "Double-click the found spot"),
        new Choice<IfFoundAction>(IfFoundAction.RightClickFoundSpot, "Right-click the found spot"),
        new Choice<IfFoundAction>(IfFoundAction.MoveToFoundSpot, "Move the mouse to the found spot"),
        new Choice<IfFoundAction>(IfFoundAction.HoldOnFoundSpot, "Hold the left button on the found spot"),
    };

    /// <summary>"When found, also:" (the same stored setting as <see cref="ThenAction"/>).</summary>
    public IfFoundAction WhenFoundAction
    {
        get => _step.WhenFoundAction;
        set
        {
            if (_step.WhenFoundAction == value) return;
            _step.WhenFoundAction = value;
            NotifyChanged();
            OnPropertyChanged(nameof(ThenAction));
            OnPropertyChanged(nameof(ShowsWhenFound));
            OnPropertyChanged(nameof(HasThenAction));
            OnPropertyChanged(nameof(IsThenHold));
            OnPropertyChanged(nameof(ThenActionHint));
        }
    }

    /// <summary>
    /// The "When found, also:" section only makes sense for "… is on screen" (something is found). It stays visible while
    /// an action is still set, so it can be turned off again after switching to "is NOT on screen".
    /// </summary>
    public bool ShowsWhenFound => _step.Condition is not { Negate: true } || _step.ThenAction != FoundTextAction.None;

    /// <summary>"🎯 Pick offset from screen" for the found-spot action.</summary>
    public OffsetPickerViewModel OffsetPicker { get; }

    private void SetFoundOffset(int x, int y)
    {
        _step.FoundOffsetX = x;
        _step.FoundOffsetY = y;
        OnPropertyChanged(nameof(ThenOffsetX));
        NotifyChanged(nameof(ThenOffsetY));
    }

    /// <summary>The same choices as a Find step's "when found".</summary>
    public static IReadOnlyList<Choice<FoundTextAction>> ThenActions => ScreenSearchStepViewModel.Actions;

    public FoundTextAction ThenAction
    {
        get => _step.ThenAction;
        set
        {
            if (_step.ThenAction == value) return;
            _step.ThenAction = value;
            NotifyChanged();
            OnPropertyChanged(nameof(WhenFoundAction));
            OnPropertyChanged(nameof(ShowsWhenFound));
            OnPropertyChanged(nameof(HasThenAction));
            OnPropertyChanged(nameof(IsThenHold));
            OnPropertyChanged(nameof(ThenActionHint));
        }
    }

    public bool HasThenAction => _step.ThenAction != FoundTextAction.None;

    public bool IsThenHold => _step.ThenAction == FoundTextAction.Hold;

    public int ThenOffsetX
    {
        get => _step.ThenOffsetX;
        set { if (_step.ThenOffsetX == value) return; _step.ThenOffsetX = value; NotifyChanged(); }
    }

    public int ThenOffsetY
    {
        get => _step.ThenOffsetY;
        set { if (_step.ThenOffsetY == value) return; _step.ThenOffsetY = value; NotifyChanged(); }
    }

    public int ThenHoldMs
    {
        get => _step.ThenHoldMs;
        set { if (_step.ThenHoldMs == value) return; _step.ThenHoldMs = value; NotifyChanged(); }
    }

    public string ThenActionHint => _step.Condition is { Negate: true } && HasThenAction
        ? "This only works with \"is on screen\": with \"is NOT on screen\" nothing is found, so there's nothing to click."
        : "When the condition sees its image / color / text, this happens at its center (plus the offset) before the steps inside the If run, even when it shows up at a different spot every time. Mouse click / Move mouse steps inside the If can also use \"the last found spot\" + their own offset (e.g. one cell of a game board).";

    public ScreenTools Tools
    {
        get => Condition.Tools;
        set => Condition.Tools = value;
    }
}

/// <summary>Editor for a Repeat step: how many times / while / until, and the safety limits.</summary>
public sealed class RepeatStepViewModel : StepViewModel
{
    private readonly RepeatStep _step;
    private ScreenTools _tools = ScreenTools.None;
    private ConditionViewModel? _condition;

    public RepeatStepViewModel(RepeatStep step) : base(step)
    {
        _step = step;
        RebuildCondition();
    }

    public static IReadOnlyList<Choice<RepeatMode>> Modes { get; } = new[]
    {
        new Choice<RepeatMode>(RepeatMode.Times, "A fixed number of times"),
        new Choice<RepeatMode>(RepeatMode.While, "While a condition is true (e.g. while a button is on screen)"),
        new Choice<RepeatMode>(RepeatMode.Until, "Until a condition becomes true (e.g. until the next screen appears)"),
    };

    public static IReadOnlyList<Choice<LoopLimitBehavior>> LimitOptions { get; } = new[]
    {
        new Choice<LoopLimitBehavior>(LoopLimitBehavior.StopMacro, "Stop the macro (show an error)"),
        new Choice<LoopLimitBehavior>(LoopLimitBehavior.ExitLoop, "Leave the loop and continue after End Repeat"),
    };

    public ScreenTools Tools
    {
        get => _tools;
        set
        {
            _tools = value ?? ScreenTools.None;
            if (_condition is not null) _condition.Tools = _tools;
        }
    }

    public RepeatMode Mode
    {
        get => _step.Mode;
        set
        {
            if (_step.Mode == value) return;
            _step.Mode = value;
            if (value != RepeatMode.Times && _step.Condition is null)
                _step.Condition = MacroFactory.NewCondition(ConditionKind.TextVisible);
            RebuildCondition();
            NotifyChanged();
            OnPropertyChanged(nameof(IsTimes));
            OnPropertyChanged(nameof(UsesCondition));
            OnPropertyChanged(nameof(ConditionLabel));
            OnPropertyChanged(nameof(LimitHint));
        }
    }

    public bool IsTimes => _step.Mode == RepeatMode.Times;

    public bool UsesCondition => _step.Mode != RepeatMode.Times;

    public string ConditionLabel => _step.Mode == RepeatMode.Until
        ? "Before each turn, stop when this is true"
        : "Before each turn, keep going while this is true";

    /// <summary>The condition editor for While / Until (null for Times).</summary>
    public ConditionViewModel? Condition
    {
        get => _condition;
        private set => SetProperty(ref _condition, value);
    }

    public int Count
    {
        get => _step.Count;
        set { if (_step.Count == value) return; _step.Count = value; NotifyChanged(); }
    }

    /// <summary>
    /// "Keep checking before leaving (ms)": the grace window before the loop ends because its target is not seen.
    /// Shows the automatic value until edited (800 ms for image / colour conditions, 0 for text).
    /// </summary>
    public int KeepCheckingForMs
    {
        get => _step.EffectiveKeepCheckingForMs;
        set
        {
            if (_step.KeepCheckingForMs == value) return;
            _step.KeepCheckingForMs = value;
            NotifyChanged();
        }
    }

    public string KeepCheckingHint =>
        "If the target is missing for a moment (e.g. a game button that comes back 0.1–0.3 s after a click), keep looking this long before leaving the loop. 0 = leave on the first miss.";

    public int MaxIterations
    {
        get => _step.MaxIterations;
        set { if (_step.MaxIterations == value) return; _step.MaxIterations = value; NotifyChanged(); }
    }

    public int MaxDurationMs
    {
        get => _step.MaxDurationMs;
        set
        {
            if (_step.MaxDurationMs == value) return;
            _step.MaxDurationMs = value;
            NotifyChanged();
            OnPropertyChanged(nameof(MaxDurationText));
        }
    }

    public string MaxDurationText => _step.MaxDurationMs <= 0
        ? "0 = no time limit"
        : $"= {_step.MaxDurationMs / 1000.0:0.###} seconds (paused time doesn't count)";

    public LoopLimitBehavior WhenLimitReached
    {
        get => _step.WhenLimitReached;
        set { if (_step.WhenLimitReached == value) return; _step.WhenLimitReached = value; NotifyChanged(); }
    }

    public string LimitHint => _step.Mode == RepeatMode.Times
        ? "Optional: a time limit for the whole loop."
        : $"Required: set Max turns and/or Max time, so the loop can't run forever (0 = no limit of that kind, but not both 0). Max turns can be up to {MacroValidator.MaxLoopCount:N0}.";

    private void RebuildCondition()
    {
        Condition = _step.Mode != RepeatMode.Times && _step.Condition is { } condition
            ? new ConditionViewModel(condition, () =>
            {
                NotifyChanged(nameof(Condition));
                OnPropertyChanged(nameof(KeepCheckingForMs));
            }) { Tools = _tools }
            : null;
        OnPropertyChanged(nameof(KeepCheckingForMs));
    }
}

/// <summary>Else / End If / End Repeat: nothing to set, just an explanation.</summary>
public sealed class BlockMarkerStepViewModel : StepViewModel
{
    public BlockMarkerStepViewModel(MacroStep step) : base(step)
    {
    }

    public string Explanation => Model switch
    {
        ElseStep => "The steps between Else and End If run when the If's condition is false. The steps between the If and Else run when it is true. An If doesn't need an Else.",
        EndIfStep => "Marks the end of an If block. Playback continues here after either part of the If.",
        EndRepeatStep => "Marks the end of a Repeat block. Playback goes back to the Repeat step, which decides whether to run another turn.",
        _ => string.Empty,
    };

    public string DisableHint => "Turning this marker off has no effect. To skip a whole block, turn off its If or Repeat step instead.";
}
