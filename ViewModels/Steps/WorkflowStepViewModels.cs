using SmartMacro.Core.Models;
using SmartMacro.Core.Storage;
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
public sealed record ScreenTools(
    IFindTextTools? Text,
    IVisionTools? Vision,
    IScreenLayout? Layout = null,
    Func<IReadOnlyList<Macro>>? MacroListProvider = null,
    Func<Guid?>? CurrentMacroIdProvider = null,
    Func<bool>? AllowLocalWebhooksProvider = null,
    Func<Guid, IReadOnlyList<SharedTemplateCandidate>>? SharedTemplatesProvider = null)
{
    public static ScreenTools None { get; } = new(null, null);

    /// <summary>Gives a step editor the tools it needs (does nothing for steps that don't use any).</summary>
    public void ApplyTo(StepViewModel step)
    {
        var excludeId = CurrentMacroIdProvider?.Invoke();
        if (step is ScreenSearchStepViewModel search)
        {
            search.Layout = Layout; // monitor / window lists
            search.FailurePolicy.ExcludeMacroId = excludeId;
            search.FailurePolicy.MacroListProvider = MacroListProvider;
        }
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
                findImage.SharedTemplatesProvider = SharedTemplatesProvider;
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
                waitForChange.FailurePolicy.ExcludeMacroId = excludeId;
                waitForChange.FailurePolicy.MacroListProvider = MacroListProvider;
                break;
            case FocusWindowStepViewModel focus:
                focus.Layout = Layout;
                focus.FailurePolicy.ExcludeMacroId = excludeId;
                focus.FailurePolicy.MacroListProvider = MacroListProvider;
                break;
            case ScreenshotStepViewModel screenshot:
                screenshot.Tools = Vision;
                screenshot.Layout = Layout;
                screenshot.AllowLocalWebhooks = AllowLocalWebhooksProvider;
                break;
            case SetVariableStepViewModel setVar:
                setVar.Tools = Vision;
                break;
            case CallMacroStepViewModel callMacro:
                callMacro.ExcludeMacroId = excludeId;
                callMacro.MacroListProvider = MacroListProvider;
                break;
        }
    }
}

/// <summary>
/// Editor for a condition: what to look for (text / image / color / variable value), "is" or "is NOT", and the search
/// or variable comparison settings.
/// </summary>
public sealed class ConditionViewModel : ObservableObject
{
    private readonly MacroCondition _condition;
    private readonly Action _changed;
    private ScreenTools _tools = ScreenTools.None;
    private ScreenSearchStepViewModel? _checkEditor;
    private ConditionViewModel? _secondCondition;

    public ConditionViewModel(MacroCondition condition, Action changed, bool allowSecondCondition = true)
    {
        _condition = condition;
        _changed = changed;
        AllowSecondCondition = allowSecondCondition;
        RebuildCheckEditor();
        RebuildSecondConditionEditor();
    }

    public bool AllowSecondCondition { get; }

    public static IReadOnlyList<Choice<ConditionCombinator>> CombinatorChoices { get; } = new[]
    {
        new Choice<ConditionCombinator>(ConditionCombinator.None, "Single condition only"),
        new Choice<ConditionCombinator>(ConditionCombinator.And, "AND — both conditions must be true"),
        new Choice<ConditionCombinator>(ConditionCombinator.Or, "OR — either condition can be true"),
    };

    public static IReadOnlyList<Choice<ConditionKind>> Kinds { get; } = new[]
    {
        new Choice<ConditionKind>(ConditionKind.TextVisible, "Text (read with OCR)"),
        new Choice<ConditionKind>(ConditionKind.ImageVisible, "Image (button, icon…)"),
        new Choice<ConditionKind>(ConditionKind.ColorVisible, "Color"),
        new Choice<ConditionKind>(ConditionKind.VariableCompare, "🧠 Variable value (memory / counter / OCR)"),
    };

    public static IReadOnlyList<Choice<bool>> NegateOptions { get; } = new[]
    {
        new Choice<bool>(false, "is on screen"),
        new Choice<bool>(true, "is NOT on screen"),
    };

    public static IReadOnlyList<Choice<bool>> VariableNegateOptions { get; } = new[]
    {
        new Choice<bool>(false, "is TRUE (matches)"),
        new Choice<bool>(true, "is FALSE (does not match)"),
    };

    public static IReadOnlyList<Choice<VariableCompareOperator>> CompareOperators { get; } = new[]
    {
        new Choice<VariableCompareOperator>(VariableCompareOperator.Equals, "=  Equals"),
        new Choice<VariableCompareOperator>(VariableCompareOperator.NotEquals, "≠  Does not equal"),
        new Choice<VariableCompareOperator>(VariableCompareOperator.GreaterThan, ">  Greater than"),
        new Choice<VariableCompareOperator>(VariableCompareOperator.GreaterOrEqual, "≥  Greater than or equal"),
        new Choice<VariableCompareOperator>(VariableCompareOperator.LessThan, "<  Less than"),
        new Choice<VariableCompareOperator>(VariableCompareOperator.LessOrEqual, "≤  Less than or equal"),
        new Choice<VariableCompareOperator>(VariableCompareOperator.Contains, "Contains text"),
        new Choice<VariableCompareOperator>(VariableCompareOperator.StartsWith, "Starts with text"),
    };

    public IReadOnlyList<Choice<bool>> EffectiveNegateOptions => IsVariableCompare ? VariableNegateOptions : NegateOptions;

    public ScreenTools Tools
    {
        get => _tools;
        set
        {
            _tools = value ?? ScreenTools.None;
            if (_checkEditor is not null) _tools.ApplyTo(_checkEditor);
            if (_secondCondition is not null) _secondCondition.Tools = _tools;
        }
    }

    public ConditionCombinator SecondCombinator
    {
        get => _condition.SecondCombinator;
        set
        {
            if (_condition.SecondCombinator == value) return;
            _condition.SecondCombinator = value;
            if (value == ConditionCombinator.None)
            {
                _condition.SecondCondition = null;
            }
            else if (_condition.SecondCondition is null)
            {
                _condition.SecondCondition = new MacroCondition
                {
                    Comparison = new VariableComparison { VariableName = "count", Operator = VariableCompareOperator.GreaterOrEqual, RightValue = "1" }
                };
            }
            RebuildSecondConditionEditor();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSecondCondition));
            _changed();
        }
    }

    public bool HasSecondCondition => AllowSecondCondition && _condition.HasSecondCondition;

    public ConditionViewModel? SecondCondition
    {
        get => _secondCondition;
        private set => SetProperty(ref _secondCondition, value);
    }

    /// <summary>What the condition looks for. Null when the stored condition is damaged (pick a kind to fix it).</summary>
    public ConditionKind? Kind
    {
        get => _condition.Kind;
        set
        {
            if (value is not { } kind || _condition.Kind == kind) return;

            if (kind == ConditionKind.VariableCompare)
            {
                _condition.Check = null;
                _condition.Comparison ??= new VariableComparison { VariableName = "count", Operator = VariableCompareOperator.GreaterOrEqual, RightValue = "10" };
            }
            else
            {
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

                _condition.Comparison = null;
                _condition.Check = check;
            }

            RebuildCheckEditor();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsVariableCompare));
            OnPropertyChanged(nameof(IsScreenCondition));
            OnPropertyChanged(nameof(EffectiveNegateOptions));
            OnPropertyChanged(nameof(VariableName));
            OnPropertyChanged(nameof(CompareOperator));
            OnPropertyChanged(nameof(RightValue));
            OnPropertyChanged(nameof(TestHint));
            _changed();
        }
    }

    public bool IsVariableCompare => _condition.Kind == ConditionKind.VariableCompare;
    public bool IsScreenCondition => !IsVariableCompare;

    public string VariableName
    {
        get => _condition.Comparison?.VariableName ?? string.Empty;
        set
        {
            var cmp = _condition.Comparison ??= new VariableComparison();
            var v = (value ?? string.Empty).Trim();
            if (cmp.VariableName == v) return;
            cmp.VariableName = v;
            OnPropertyChanged();
            _changed();
        }
    }

    public VariableCompareOperator CompareOperator
    {
        get => _condition.Comparison?.Operator ?? VariableCompareOperator.Equals;
        set
        {
            var cmp = _condition.Comparison ??= new VariableComparison();
            if (cmp.Operator == value) return;
            cmp.Operator = value;
            OnPropertyChanged();
            _changed();
        }
    }

    public string RightValue
    {
        get => _condition.Comparison?.RightValue ?? string.Empty;
        set
        {
            var cmp = _condition.Comparison ??= new VariableComparison();
            var v = value ?? string.Empty;
            if (cmp.RightValue == v) return;
            cmp.RightValue = v;
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

    /// <summary>The Find text / image / color editor for the search settings (null if variable compare or damaged).</summary>
    public ScreenSearchStepViewModel? CheckEditor
    {
        get => _checkEditor;
        private set => SetProperty(ref _checkEditor, value);
    }

    public bool IsDamaged => !IsVariableCompare && _checkEditor is null;

    public string TestHint => IsVariableCompare
        ? "Compares the live variable in memory (numbers are compared numerically; right-hand side supports {otherVar} placeholders)."
        : _condition.Negate
            ? "\"Test detection now\" shows whether it's found. With \"is NOT on screen\" the condition is true when it is NOT found."
            : "\"Test detection now\" shows whether it's found. The condition is true when it is found.";

    public void RefreshSharedTemplates()
    {
        _checkEditor?.RefreshSharedTemplates();
        _secondCondition?.RefreshSharedTemplates();
    }

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

    private void RebuildSecondConditionEditor()
    {
        if (!AllowSecondCondition || !_condition.HasSecondCondition || _condition.SecondCondition is null)
        {
            SecondCondition = null;
            return;
        }

        var vm = new ConditionViewModel(_condition.SecondCondition, _changed, allowSecondCondition: false)
        {
            Tools = _tools
        };
        SecondCondition = vm;
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
        set
        {
            if (_step.ThenOffsetX == value) return;
            _step.ThenOffsetX = value;
            OnPropertyChanged(nameof(OffsetX));
            NotifyChanged();
        }
    }

    public int ThenOffsetY
    {
        get => _step.ThenOffsetY;
        set
        {
            if (_step.ThenOffsetY == value) return;
            _step.ThenOffsetY = value;
            OnPropertyChanged(nameof(OffsetY));
            NotifyChanged();
        }
    }

    public int OffsetX
    {
        get => ThenOffsetX;
        set => ThenOffsetX = value;
    }

    public int OffsetY
    {
        get => ThenOffsetY;
        set => ThenOffsetY = value;
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

    public override void RefreshSharedTemplates() => Condition.RefreshSharedTemplates();
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

    public override void RefreshSharedTemplates() => Condition?.RefreshSharedTemplates();
}

/// <summary>Else / End If / End Repeat / End Group: nothing to set, just an explanation.</summary>
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
        EndGroupStep => "Marks the end of a Group section. Execution simply continues to the next step with zero overhead.",
        _ => string.Empty,
    };

    public string DisableHint => Model is EndGroupStep
        ? "Turning this marker off has no effect. To skip a whole Group section, turn off its Group header step instead."
        : "Turning this marker off has no effect. To skip a whole block, turn off its If or Repeat step instead.";
}

/// <summary>Editor for <see cref="BeginGroupStep"/>: collapsible section title, accent tag color, and group helpers.</summary>
public sealed class BeginGroupStepViewModel : StepViewModel
{
    private readonly BeginGroupStep _step;

    public BeginGroupStepViewModel(BeginGroupStep step) : base(step)
    {
        _step = step;
    }

    public static IReadOnlyList<Choice<string>> ColorChoices { get; } = new[]
    {
        new Choice<string>("#0EA5E9", "Sky Cyan (Default)"),
        new Choice<string>("#10B981", "Emerald"),
        new Choice<string>("#8B5CF6", "Violet"),
        new Choice<string>("#F59E0B", "Amber"),
        new Choice<string>("#EC4899", "Rose"),
        new Choice<string>("#64748B", "Slate"),
    };

    public string GroupTitle
    {
        get => _step.Title;
        set
        {
            var next = value ?? string.Empty;
            if (_step.Title == next) return;
            _step.Title = next;
            NotifyChanged();
        }
    }

    public string ColorHex
    {
        get => string.IsNullOrWhiteSpace(_step.ColorHex) ? BeginGroupStep.DefaultColorHex : _step.ColorHex;
        set
        {
            var next = string.IsNullOrWhiteSpace(value) ? BeginGroupStep.DefaultColorHex : value.Trim();
            if (_step.ColorHex == next) return;
            _step.ColorHex = next;
            NotifyChanged();
        }
    }
}

/// <summary>Editor for <see cref="FlowControlStep"/> (Break loop / Continue loop / Stop macro).</summary>
public sealed class FlowControlStepViewModel : StepViewModel
{
    private readonly FlowControlStep _step;

    public FlowControlStepViewModel(FlowControlStep step) : base(step)
    {
        _step = step;
    }

    public static IReadOnlyList<Choice<FlowControlAction>> Actions { get; } = new[]
    {
        new Choice<FlowControlAction>(FlowControlAction.ExitLoop, "⏹ Break loop — exit the innermost Repeat block immediately"),
        new Choice<FlowControlAction>(FlowControlAction.ContinueLoop, "⏭ Continue loop — skip to the next turn of the innermost Repeat"),
        new Choice<FlowControlAction>(FlowControlAction.StopMacro, "🛑 Stop macro — finish the macro run cleanly right now"),
    };

    public FlowControlAction Action
    {
        get => _step.Action;
        set
        {
            if (_step.Action == value) return;
            _step.Action = value;
            NotifyChanged();
            OnPropertyChanged(nameof(ActionHelp));
        }
    }

    public string Message
    {
        get => _step.Message ?? string.Empty;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (_step.Message == normalized) return;
            _step.Message = normalized;
            NotifyChanged();
        }
    }

    public string ActionHelp => _step.Action switch
    {
        FlowControlAction.ExitLoop => "Place inside an If block inside a Repeat loop to break out of the loop early as soon as your condition is met.",
        FlowControlAction.ContinueLoop => "Place inside an If block inside a Repeat loop to skip the rest of the current turn and start the next turn.",
        _ => "Cleanly stops the entire macro run immediately (without marking the run as an error).",
    };
}
