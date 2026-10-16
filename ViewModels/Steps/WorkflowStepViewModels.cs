using SmartMacro.Core.Models;
using SmartMacro.Core.Validation;
using SmartMacro.Mvvm;
using SmartMacro.Services.Ocr;
using SmartMacro.Services.Vision;
using MacroCondition = SmartMacro.Core.Models.Condition;

namespace SmartMacro.ViewModels.Steps;

/// <summary>
/// The screen tools (Pick area, Capture image, Pick color, Test now) the editor hands to steps that look at the
/// screen, including the search inside an If / Repeat condition and a Feedback loop (with its conditions).
/// </summary>
public sealed record ScreenTools(IFindTextTools? Text, IVisionTools? Vision)
{
    public static ScreenTools None { get; } = new(null, null);

    /// <summary>Gives a step editor the tools it needs (does nothing for steps that don't use any).</summary>
    public void ApplyTo(StepViewModel step)
    {
        switch (step)
        {
            case MouseMoveStepViewModel move:
                move.PositionPicker.Tools = Vision;
                break;
            case MouseClickStepViewModel click:
                click.PositionPicker.Tools = Vision;
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
                break;
            case RepeatStepViewModel repeat:
                repeat.Tools = this;
                break;
            case FeedbackLoopStepViewModel feedback:
                feedback.Tools = this;
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
            if (old is not null)
            {
                check.SearchFullScreen = old.SearchFullScreen;
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
            _changed();
        }
    }

    /// <summary>The Find text / image / color editor for the search settings (null if the condition is damaged).</summary>
    public ScreenSearchStepViewModel? CheckEditor
    {
        get => _checkEditor;
        private set => SetProperty(ref _checkEditor, value);
    }

    public bool IsDamaged => _checkEditor is null;

    public string TestHint => _condition.Negate
        ? "\"Test now\" shows whether it's found. With \"is NOT on screen\" the condition is true when it is NOT found."
        : "\"Test now\" shows whether it's found. The condition is true when it is found.";

    private void RebuildCheckEditor()
    {
        if (_checkEditor is not null) _checkEditor.Modified -= OnCheckModified;

        ScreenSearchStepViewModel? editor = null;
        if (_condition.Search is { } search && StepViewModel.Create(search) is ScreenSearchStepViewModel created)
        {
            editor = created;
            editor.IsStandalone = false;
            _tools.ApplyTo(editor);
            editor.Modified += OnCheckModified;
        }

        CheckEditor = editor;
        OnPropertyChanged(nameof(IsDamaged));
    }

    private void OnCheckModified(object? sender, EventArgs e) => _changed();
}

/// <summary>Editor for an If step: just its condition.</summary>
public sealed class IfStepViewModel : StepViewModel
{
    public IfStepViewModel(IfStep step) : base(step)
    {
        if (step.Condition is null) step.Condition = new MacroCondition(); // a damaged file may have no condition; the validator reports it
        Condition = new ConditionViewModel(step.Condition, () => NotifyChanged(nameof(Condition)));
    }

    public ConditionViewModel Condition { get; }

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
        new Choice<RepeatMode>(RepeatMode.Times, "A number of times"),
        new Choice<RepeatMode>(RepeatMode.While, "While a condition is true"),
        new Choice<RepeatMode>(RepeatMode.Until, "Until a condition becomes true"),
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
                _step.Condition = MacroCondition.Create(ConditionKind.TextVisible);
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
            ? new ConditionViewModel(condition, () => NotifyChanged(nameof(Condition))) { Tools = _tools }
            : null;
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
