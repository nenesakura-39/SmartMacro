using SmartMacro.Core.Models;
using SmartMacro.Core.Playback;
using SmartMacro.Mvvm;

namespace SmartMacro.ViewModels;

/// <summary>
/// Live variable row displayed for the currently paused call-stack frame (including sub-macro locals or runtime-created variables).
/// Editable in place while playback is paused.
/// </summary>
public sealed class LiveRuntimeVariableViewModel : ObservableObject
{
    private MacroVariableType _type;
    private string _liveValue = string.Empty;
    private string _editableLiveValue = string.Empty;

    public LiveRuntimeVariableViewModel(string name, MacroVariableType type, string liveValue)
    {
        Name = name;
        _type = type;
        _liveValue = liveValue;
        _editableLiveValue = liveValue;
    }

    public string Name { get; }

    public MacroVariableType Type
    {
        get => _type;
        set
        {
            if (SetProperty(ref _type, value))
                OnPropertyChanged(nameof(TypeLabel));
        }
    }

    public string TypeLabel => Type switch
    {
        MacroVariableType.Number => "Number",
        MacroVariableType.Boolean => "Bool",
        _ => "Text",
    };

    public string LiveValue
    {
        get => _liveValue;
        set
        {
            if (SetProperty(ref _liveValue, value ?? string.Empty))
                EditableLiveValue = _liveValue;
        }
    }

    public string EditableLiveValue
    {
        get => _editableLiveValue;
        set => SetProperty(ref _editableLiveValue, value ?? string.Empty);
    }
}

/// <summary>Editable Debugger 2.0 variable watchpoint entry (pause on variable mutation or condition).</summary>
public sealed class WatchpointItemViewModel : ObservableObject
{
    private bool _enabled = true;
    private string _variableName = string.Empty;
    private string _expression = string.Empty;
    private readonly Action _changed;

    public WatchpointItemViewModel(string variableName, string expression, Action changed)
    {
        _variableName = variableName;
        _expression = expression;
        _changed = changed;
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value)) _changed();
        }
    }

    public string VariableName
    {
        get => _variableName;
        set
        {
            if (SetProperty(ref _variableName, (value ?? string.Empty).Trim())) _changed();
        }
    }

    /// <summary>Optional filter expression (e.g. "{hp} &lt; 20"); leave blank to break on any change.</summary>
    public string Expression
    {
        get => _expression;
        set
        {
            if (SetProperty(ref _expression, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasValidationError));
                OnPropertyChanged(nameof(ValidationError));
                OnPropertyChanged(nameof(DisplayCondition));
                _changed();
            }
        }
    }

    public string DisplayCondition => string.IsNullOrWhiteSpace(_expression) ? "on any change" : $"when {_expression.Trim()}";

    public string? ValidationError
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_expression)) return null;
            return DebugConditionEvaluator.TryParseExpression(_expression, out _, out _, out var err) ? null : err;
        }
    }

    public bool HasValidationError => !string.IsNullOrEmpty(ValidationError);

    public VariableWatchpoint ToCoreWatchpoint() => new()
    {
        Enabled = Enabled,
        VariableName = VariableName,
        Expression = string.IsNullOrWhiteSpace(Expression) ? null : Expression.Trim(),
    };
}

/// <summary>Summary item listing an active (or conditional) breakpoint across the root or sub-macros.</summary>
public sealed class ConditionalBreakpointItemViewModel : ObservableObject
{
    private string _conditionText = string.Empty;

    public Guid MacroId { get; init; }
    public string MacroName { get; init; } = string.Empty;
    public Guid StepId { get; init; }
    public int StepNumber { get; init; }
    public string StepTitle { get; init; } = string.Empty;

    public string ConditionText
    {
        get => _conditionText;
        set
        {
            if (SetProperty(ref _conditionText, value ?? string.Empty))
                OnPropertyChanged(nameof(DisplayLabel));
        }
    }

    public string DisplayLabel => string.IsNullOrWhiteSpace(_conditionText)
        ? $"{MacroName} · Step #{StepNumber} ({StepTitle})"
        : $"{MacroName} · Step #{StepNumber} ({StepTitle}) [{_conditionText}]";
}
