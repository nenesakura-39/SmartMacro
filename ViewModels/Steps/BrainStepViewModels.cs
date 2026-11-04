using System.Collections.ObjectModel;
using SmartMacro.Core.Models;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Targeting;
using SmartMacro.Core.Vision;
using SmartMacro.Mvvm;
using SmartMacro.Services.Vision;
using MacroCondition = SmartMacro.Core.Models.Condition;

namespace SmartMacro.ViewModels.Steps;

/// <summary>One library macro offered in a sub-macro or recovery-macro picker.</summary>
public sealed record MacroChoice(Guid Id, string Name, string Label);

/// <summary>
/// Reusable editor for per-step failure policies (<see cref="StepFailurePolicy"/>) on Find text / image / color,
/// Wait for screen change, and Focus window steps. Legacy 3-choice actions (<see cref="StepFailureAction.Continue"/>,
/// <see cref="StepFailureAction.SkipNextStep"/>, <see cref="StepFailureAction.StopMacro"/>) stay stored in
/// <c>WhenNotFound</c> with <c>FailurePolicy = null</c> so Schema v1 compatibility is preserved.
/// </summary>
public sealed class FailurePolicyViewModel : ObservableObject
{
    private readonly Func<TextNotFoundBehavior> _getLegacy;
    private readonly Action<TextNotFoundBehavior> _setLegacy;
    private readonly Func<StepFailurePolicy?> _getPolicy;
    private readonly Action<StepFailurePolicy?> _setPolicy;
    private readonly Action _notifyChanged;
    private Func<IReadOnlyList<Macro>>? _macroListProvider;
    private Guid? _excludeMacroId;

    public FailurePolicyViewModel(
        Func<TextNotFoundBehavior> getLegacy,
        Action<TextNotFoundBehavior> setLegacy,
        Func<StepFailurePolicy?> getPolicy,
        Action<StepFailurePolicy?> setPolicy,
        Action notifyChanged)
    {
        _getLegacy = getLegacy;
        _setLegacy = setLegacy;
        _getPolicy = getPolicy;
        _setPolicy = setPolicy;
        _notifyChanged = notifyChanged;
        RefreshMacrosCommand = new RelayCommand(RefreshMacroChoices);
    }

    public static IReadOnlyList<Choice<StepFailureAction>> ActionChoices { get; } = new[]
    {
        new Choice<StepFailureAction>(StepFailureAction.Continue, "Continue to the next step"),
        new Choice<StepFailureAction>(StepFailureAction.SkipNextStep, "Skip the next step, then continue"),
        new Choice<StepFailureAction>(StepFailureAction.StopMacro, "Stop the macro (show an error)"),
        new Choice<StepFailureAction>(StepFailureAction.Retry, "🔁 Retry this step before giving up"),
        new Choice<StepFailureAction>(StepFailureAction.RunRecoveryMacro, "🛡 Run a recovery sub-macro, then continue"),
        new Choice<StepFailureAction>(StepFailureAction.RestartFromCheckpoint, "📍 Jump back to a Checkpoint"),
    };

    public RelayCommand RefreshMacrosCommand { get; }

    public ObservableCollection<MacroChoice> MacroChoices { get; } = new();

    public Func<IReadOnlyList<Macro>>? MacroListProvider
    {
        get => _macroListProvider;
        set
        {
            _macroListProvider = value;
            if (IsRecoveryMacro) RefreshMacroChoices();
        }
    }

    public Guid? ExcludeMacroId
    {
        get => _excludeMacroId;
        set
        {
            _excludeMacroId = value;
            if (IsRecoveryMacro) RefreshMacroChoices();
        }
    }

    public StepFailureAction Action
    {
        get => StepFailurePolicy.ResolveEffective(_getLegacy(), _getPolicy());
        set
        {
            if (Action == value) return;
            var (legacy, nextPolicy) = StepFailurePolicy.Apply(value, _getPolicy());
            _setLegacy(legacy);
            _setPolicy(nextPolicy);
            if (value == StepFailureAction.RunRecoveryMacro) RefreshMacroChoices();
            _notifyChanged();
            RaiseAll();
        }
    }

    public bool IsRetry => Action == StepFailureAction.Retry;
    public bool IsRecoveryMacro => Action == StepFailureAction.RunRecoveryMacro;
    public bool IsCheckpointJump => Action == StepFailureAction.RestartFromCheckpoint;

    public int RetryCount
    {
        get => _getPolicy()?.RetryCount ?? 2;
        set
        {
            var p = CloneOrCreate();
            if (p.RetryCount == value) return;
            p.RetryCount = value;
            Apply(p);
            OnPropertyChanged();
            OnPropertyChanged(nameof(HelpText));
        }
    }

    public int RetryDelayMs
    {
        get => _getPolicy()?.RetryDelayMs ?? 500;
        set
        {
            var p = CloneOrCreate();
            if (p.RetryDelayMs == value) return;
            p.RetryDelayMs = value;
            Apply(p);
            OnPropertyChanged();
            OnPropertyChanged(nameof(HelpText));
        }
    }

    public string RecoveryMacroName
    {
        get => _getPolicy()?.RecoveryMacroName ?? string.Empty;
        set
        {
            var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            var p = CloneOrCreate();
            if (p.RecoveryMacroName == trimmed) return;
            p.RecoveryMacroName = trimmed;
            var match = MacroChoices.FirstOrDefault(c => string.Equals(c.Name, trimmed, StringComparison.OrdinalIgnoreCase));
            if (match is not null) p.RecoveryMacroId = match.Id;
            Apply(p);
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedMacroChoice));
            OnPropertyChanged(nameof(HelpText));
        }
    }

    public MacroChoice? SelectedMacroChoice
    {
        get
        {
            var p = _getPolicy();
            if (p is null) return null;
            return (p.RecoveryMacroId is { } id && id != Guid.Empty
                       ? MacroChoices.FirstOrDefault(c => c.Id == id)
                       : null)
                   ?? MacroChoices.FirstOrDefault(c => string.Equals(c.Name, p.RecoveryMacroName, StringComparison.OrdinalIgnoreCase));
        }
        set
        {
            if (value is null) return;
            var p = CloneOrCreate();
            p.RecoveryMacroId = value.Id;
            p.RecoveryMacroName = value.Name;
            Apply(p);
            OnPropertyChanged();
            OnPropertyChanged(nameof(RecoveryMacroName));
            OnPropertyChanged(nameof(HelpText));
        }
    }

    public string CheckpointName
    {
        get => _getPolicy()?.CheckpointName ?? string.Empty;
        set
        {
            var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            var p = CloneOrCreate();
            if (p.CheckpointName == trimmed) return;
            p.CheckpointName = trimmed;
            Apply(p);
            OnPropertyChanged();
            OnPropertyChanged(nameof(HelpText));
        }
    }

    public string HelpText => Action switch
    {
        StepFailureAction.StopMacro => "Nothing below this step runs. Playback stops with a clear error message.",
        StepFailureAction.SkipNextStep => "Skips the step right below (or the whole If / Repeat block starting there), then continues.",
        StepFailureAction.Retry => $"Waits {RetryDelayMs} ms and tries this step again (up to {RetryCount} extra attempt{(RetryCount == 1 ? "" : "s")}) before stopping.",
        StepFailureAction.RunRecoveryMacro => string.IsNullOrWhiteSpace(RecoveryMacroName)
            ? "Pick a recovery sub-macro from your library (e.g. \"Close Popups\" or \"Reconnect\") to run when this step times out."
            : $"Runs sub-macro \"{RecoveryMacroName}\" to recover, then continues to the next step.",
        StepFailureAction.RestartFromCheckpoint => string.IsNullOrWhiteSpace(CheckpointName)
            ? "Enter the name of a top-level 📍 Checkpoint step in this macro to jump back to."
            : $"Jumps back to checkpoint \"{CheckpointName}\" and resumes from there (capped at 25 restarts per run).",
        _ => "Continues to the steps below even if it wasn't found.",
    };

    public void RefreshMacroChoices()
    {
        MacroLookupChoices.Fill(MacroChoices, _macroListProvider?.Invoke(), _excludeMacroId, _getPolicy()?.RecoveryMacroId, _getPolicy()?.RecoveryMacroName);
        OnPropertyChanged(nameof(SelectedMacroChoice));
    }

    public void RefreshFromModel() => RaiseAll();

    private StepFailurePolicy CloneOrCreate()
    {
        var cur = _getPolicy();
        return new StepFailurePolicy
        {
            Action = Action,
            RetryCount = cur?.RetryCount ?? 2,
            RetryDelayMs = cur?.RetryDelayMs ?? 500,
            RecoveryMacroId = cur?.RecoveryMacroId,
            RecoveryMacroName = cur?.RecoveryMacroName,
            CheckpointName = cur?.CheckpointName,
        };
    }

    private void Apply(StepFailurePolicy policy)
    {
        var (legacy, nextPolicy) = StepFailurePolicy.Apply(policy.Action, policy);
        _setLegacy(legacy);
        _setPolicy(nextPolicy);
        _notifyChanged();
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(Action));
        OnPropertyChanged(nameof(IsRetry));
        OnPropertyChanged(nameof(IsRecoveryMacro));
        OnPropertyChanged(nameof(IsCheckpointJump));
        OnPropertyChanged(nameof(RetryCount));
        OnPropertyChanged(nameof(RetryDelayMs));
        OnPropertyChanged(nameof(RecoveryMacroName));
        OnPropertyChanged(nameof(SelectedMacroChoice));
        OnPropertyChanged(nameof(CheckpointName));
        OnPropertyChanged(nameof(HelpText));
    }
}

internal static class MacroLookupChoices
{
    public static void Fill(
        ObservableCollection<MacroChoice> target,
        IReadOnlyList<Macro>? macros,
        Guid? excludeId,
        Guid? currentId,
        string? currentName)
    {
        target.Clear();
        if (macros is not null)
        {
            foreach (var m in macros.Where(m => m is not null && m.Id != excludeId).OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var name = string.IsNullOrWhiteSpace(m.Name) ? "Untitled" : m.Name.Trim();
                target.Add(new MacroChoice(m.Id, name, $"{name} ({m.Steps.Count} step{(m.Steps.Count == 1 ? "" : "s")})"));
            }
        }

        if (currentId is { } cid && cid != Guid.Empty && target.All(c => c.Id != cid))
        {
            var label = string.IsNullOrWhiteSpace(currentName) ? cid.ToString()[..8] : currentName.Trim();
            target.Add(new MacroChoice(cid, label, $"{label} (not in library)"));
        }
        else if (!string.IsNullOrWhiteSpace(currentName) && target.All(c => !string.Equals(c.Name, currentName.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            var trimmed = currentName.Trim();
            target.Add(new MacroChoice(Guid.Empty, trimmed, $"{trimmed} (by name)"));
        }
    }
}

/// <summary>Editor for <see cref="SetVariableStep"/> (Pillar 3: Memory &amp; Reading the Screen).</summary>
public sealed class SetVariableStepViewModel : StepViewModel
{
    private readonly SetVariableStep _step;
    private IVisionTools? _tools;
    private bool _isBusy;
    private string? _statusText;

    public SetVariableStepViewModel(SetVariableStep step) : base(step)
    {
        _step = step;
        PickRegionCommand = new AsyncRelayCommand(PickRegionAsync, ex => StatusText = ex.Message, () => _tools is not null && !_isBusy);
        PickGaugeColorCommand = new AsyncRelayCommand(PickGaugeColorAsync, ex => StatusText = ex.Message, () => _tools is not null && !_isBusy);
        ShowRegionCommand = new RelayCommand(() =>
            ScreenHighlightOverlay.ShowResult(_step.Region, null, found: true, $"Variable screen area · {_step.RegionWidth}×{_step.RegionHeight}",
                TimeSpan.FromMilliseconds(1600)), () => UsesScreenRegion && _step.RegionWidth > 0 && _step.RegionHeight > 0);
    }

    public static IReadOnlyList<Choice<VariableOperation>> Operations { get; } = new[]
    {
        new Choice<VariableOperation>(VariableOperation.Set, "Set (=) — store new value"),
        new Choice<VariableOperation>(VariableOperation.Add, "Add (+=) — add to number"),
        new Choice<VariableOperation>(VariableOperation.Subtract, "Subtract (-=) — subtract from number"),
        new Choice<VariableOperation>(VariableOperation.AppendText, "Append text — add to end of text"),
        new Choice<VariableOperation>(VariableOperation.TrimText, "✂ Trim & collapse spaces"),
        new Choice<VariableOperation>(VariableOperation.ExtractDigits, "🔢 Extract digits only (0–9)"),
        new Choice<VariableOperation>(VariableOperation.ToUpper, "🔠 Convert to UPPERCASE"),
        new Choice<VariableOperation>(VariableOperation.ToLower, "🔡 Convert to lowercase"),
    };

    public static IReadOnlyList<Choice<VariableSourceKind>> Sources { get; } = new[]
    {
        new Choice<VariableSourceKind>(VariableSourceKind.Constant, "Constant value or {variable} text"),
        new Choice<VariableSourceKind>(VariableSourceKind.ScreenOcrNumber, "🔢 Read number from screen area (OCR)"),
        new Choice<VariableSourceKind>(VariableSourceKind.ScreenOcrText, "🔤 Read text from screen area (OCR)"),
        new Choice<VariableSourceKind>(VariableSourceKind.ScreenGaugePercent, "📊 Read bar / gauge fill % (0–100)"),
        new Choice<VariableSourceKind>(VariableSourceKind.LineAtLoopIndex, "📄 N-th line or CSV item (by loop turn)"),
        new Choice<VariableSourceKind>(VariableSourceKind.ClipboardText, "📋 Clipboard text"),
        new Choice<VariableSourceKind>(VariableSourceKind.LastFoundX, "🎯 Last found X coordinate"),
        new Choice<VariableSourceKind>(VariableSourceKind.LastFoundY, "🎯 Last found Y coordinate"),
        new Choice<VariableSourceKind>(VariableSourceKind.LoopCounter, "🔁 Current loop / run counter"),
        new Choice<VariableSourceKind>(VariableSourceKind.ElapsedSeconds, "⏱ Elapsed run time (seconds)"),
    };

    public static IReadOnlyList<Choice<GaugeDirection>> GaugeDirections { get; } = new[]
    {
        new Choice<GaugeDirection>(GaugeDirection.LeftToRight, "➡ Left to Right (standard HP / progress bar)"),
        new Choice<GaugeDirection>(GaugeDirection.RightToLeft, "⬅ Right to Left (mirrored bar)"),
        new Choice<GaugeDirection>(GaugeDirection.BottomToTop, "⬆ Bottom to Top (vertical gauge)"),
        new Choice<GaugeDirection>(GaugeDirection.TopToBottom, "⬇ Top to Bottom (vertical drop)"),
    };

    public IVisionTools? Tools
    {
        get => _tools;
        set
        {
            _tools = value;
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    public AsyncRelayCommand PickRegionCommand { get; }
    public AsyncRelayCommand PickGaugeColorCommand { get; }
    public RelayCommand ShowRegionCommand { get; }

    public string? StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public override string? PlainSummary =>
        $"• Updates variable \"{(string.IsNullOrWhiteSpace(_step.VariableName) ? "?" : _step.VariableName)}\" ({_step.Operation}) from {DescribeSource()}.\n" +
        "• Use this variable in If / Repeat conditions (\"Variable value\") or type it anywhere with {" +
        (string.IsNullOrWhiteSpace(_step.VariableName) ? "count" : _step.VariableName) + "}.";

    private string DescribeSource() => _step.Source switch
    {
        VariableSourceKind.ScreenOcrNumber => $"a number read via OCR at ({_step.RegionX}, {_step.RegionY}) {_step.RegionWidth}×{_step.RegionHeight}",
        VariableSourceKind.ScreenOcrText => $"text read via OCR at ({_step.RegionX}, {_step.RegionY}) {_step.RegionWidth}×{_step.RegionHeight}",
        VariableSourceKind.ScreenGaugePercent => $"bar/gauge fill % ({_step.EffectiveGaugeColor}) at ({_step.RegionX}, {_step.RegionY}) {_step.RegionWidth}×{_step.RegionHeight}",
        VariableSourceKind.LineAtLoopIndex => $"item #turn from list \"{_step.Value}\"",
        VariableSourceKind.ClipboardText => "the Windows clipboard",
        VariableSourceKind.LastFoundX => "the X coordinate of the last found target",
        VariableSourceKind.LastFoundY => "the Y coordinate of the last found target",
        VariableSourceKind.LoopCounter => "the current Repeat / Run counter",
        VariableSourceKind.ElapsedSeconds => "elapsed seconds since the run started",
        _ => $"\"{_step.Value}\"",
    };

    public string VariableName
    {
        get => _step.VariableName ?? string.Empty;
        set
        {
            var v = (value ?? string.Empty).Trim();
            if (_step.VariableName == v) return;
            _step.VariableName = v;
            NotifyChanged();
        }
    }

    public VariableOperation Operation
    {
        get => _step.Operation;
        set
        {
            if (_step.Operation == value) return;
            _step.Operation = value;
            NotifyChanged();
        }
    }

    public VariableSourceKind Source
    {
        get => _step.Source;
        set
        {
            if (_step.Source == value) return;
            _step.Source = value;
            NotifyChanged();
            OnPropertyChanged(nameof(UsesConstantValue));
            OnPropertyChanged(nameof(UsesScreenRegion));
            OnPropertyChanged(nameof(IsScreenGauge));
            OnPropertyChanged(nameof(SourceHelp));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool UsesConstantValue => _step.Source is VariableSourceKind.Constant or VariableSourceKind.LineAtLoopIndex;
    public bool UsesScreenRegion => _step.UsesScreenRegion;
    public bool IsScreenGauge => _step.Source == VariableSourceKind.ScreenGaugePercent;

    public string GaugeColor
    {
        get => _step.EffectiveGaugeColor;
        set
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (_step.GaugeColor == trimmed) return;
            _step.GaugeColor = string.IsNullOrEmpty(trimmed) ? null : trimmed;
            NotifyChanged();
            OnPropertyChanged(nameof(GaugeColorBrush));
        }
    }

    public System.Windows.Media.Brush GaugeColorBrush
    {
        get
        {
            if (!RgbColor.TryParse(_step.EffectiveGaugeColor, out var c)) return System.Windows.Media.Brushes.Transparent;
            var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(c.R, c.G, c.B));
            brush.Freeze();
            return brush;
        }
    }

    public int GaugeTolerance
    {
        get => _step.GaugeTolerance;
        set
        {
            if (_step.GaugeTolerance == value) return;
            _step.GaugeTolerance = Math.Clamp(value, 0, 255);
            NotifyChanged();
        }
    }

    public GaugeDirection GaugeDirection
    {
        get => _step.GaugeDirection;
        set
        {
            if (_step.GaugeDirection == value) return;
            _step.GaugeDirection = value;
            NotifyChanged();
        }
    }

    public string ValueText
    {
        get => _step.Value ?? string.Empty;
        set
        {
            var v = value ?? string.Empty;
            if (_step.Value == v) return;
            _step.Value = v;
            NotifyChanged();
        }
    }

    public int RegionX
    {
        get => _step.RegionX;
        set { if (_step.RegionX == value) return; _step.RegionX = value; NotifyChanged(); System.Windows.Input.CommandManager.InvalidateRequerySuggested(); }
    }

    public int RegionY
    {
        get => _step.RegionY;
        set { if (_step.RegionY == value) return; _step.RegionY = value; NotifyChanged(); System.Windows.Input.CommandManager.InvalidateRequerySuggested(); }
    }

    public int RegionWidth
    {
        get => _step.RegionWidth;
        set { if (_step.RegionWidth == value) return; _step.RegionWidth = value; NotifyChanged(); System.Windows.Input.CommandManager.InvalidateRequerySuggested(); }
    }

    public int RegionHeight
    {
        get => _step.RegionHeight;
        set { if (_step.RegionHeight == value) return; _step.RegionHeight = value; NotifyChanged(); System.Windows.Input.CommandManager.InvalidateRequerySuggested(); }
    }

    public string SourceHelp => _step.Source switch
    {
        VariableSourceKind.ScreenOcrNumber => "Reads text inside the box with Windows OCR and extracts the number (e.g. \"Gold: 12,450\" → 12450, \"$9.50\" → 9.5). If no digits are visible yet, keeps 0 (or adds 0).",
        VariableSourceKind.ScreenOcrText => "Reads all visible words inside the box with Windows OCR and stores them as text.",
        VariableSourceKind.ScreenGaugePercent => "Measures how full a progress bar, HP/MP bar, or stamina gauge is (0 to 100%) by scanning the bar area for the fill color. Works even when text or icons sit in the middle of the bar.",
        VariableSourceKind.LineAtLoopIndex => "Extracts the N-th line or comma/semicolon-separated item from the text box above (or {listVar}), where N is the current Repeat loop turn (1, 2, 3…). Stores empty text once past the last item.",
        VariableSourceKind.ClipboardText => "Reads the current plain text on the Windows clipboard.",
        VariableSourceKind.LastFoundX => "Stores the X pixel coordinate of the latest Find step / condition match (0 if nothing has matched yet).",
        VariableSourceKind.LastFoundY => "Stores the Y pixel coordinate of the latest Find step / condition match (0 if nothing has matched yet).",
        VariableSourceKind.LoopCounter => "Stores the current Repeat turn (1, 2, 3…) or overall macro run number when outside a Repeat block.",
        VariableSourceKind.ElapsedSeconds => "Stores how many seconds this macro run has been playing (excluding paused time).",
        _ => "Enter a number (e.g. 0 or 1), true/false, or text. You can also include {otherVar} placeholders.",
    };

    private async Task PickRegionAsync()
    {
        if (_isBusy || _tools is not { } vision) return;
        _isBusy = true;
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        try
        {
            if (await vision.PickRegionAsync() is not { } rect)
            {
                StatusText = "Picking the area was cancelled.";
                return;
            }

            _step.RegionX = rect.X;
            _step.RegionY = rect.Y;
            _step.RegionWidth = rect.Width;
            _step.RegionHeight = rect.Height;
            OnPropertyChanged(nameof(RegionX));
            OnPropertyChanged(nameof(RegionY));
            OnPropertyChanged(nameof(RegionWidth));
            OnPropertyChanged(nameof(RegionHeight));
            NotifyChanged(nameof(RegionX));
            StatusText = $"Screen read area set to {rect}.";
        }
        finally
        {
            _isBusy = false;
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task PickGaugeColorAsync()
    {
        if (_isBusy || _tools is not { } vision) return;
        _isBusy = true;
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        try
        {
            var picked = await vision.PickColorAsync();
            if (picked is null)
            {
                StatusText = "Picking the fill color was cancelled.";
                return;
            }

            GaugeColor = picked.Color.ToString();
            StatusText = $"Bar fill color {picked.Color} picked at ({picked.X}, {picked.Y}).";
        }
        finally
        {
            _isBusy = false;
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }
}

/// <summary>Editor for <see cref="CheckpointStep"/> (Pillar 4: top-level safe restart point).</summary>
public sealed class CheckpointStepViewModel : StepViewModel
{
    private readonly CheckpointStep _step;

    public CheckpointStepViewModel(CheckpointStep step) : base(step)
    {
        _step = step;
    }

    public override string? PlainSummary =>
        $"• Marks a safe top-level restart point named \"{(string.IsNullOrWhiteSpace(_step.Name) ? "?" : _step.Name)}\".\n" +
        "• Any Find step, Wait step, Focus window step, or Watcher can jump back here when something goes wrong (must sit at the top level, outside If / Repeat blocks).";

    public string CheckpointName
    {
        get => _step.Name ?? string.Empty;
        set
        {
            var v = (value ?? string.Empty).Trim();
            if (_step.Name == v) return;
            _step.Name = v;
            NotifyChanged();
        }
    }
}

/// <summary>One input or output variable binding row on a <see cref="CallMacroStepViewModel"/>.</summary>
public sealed class VariableBindingItemViewModel : ObservableObject
{
    private readonly VariableBinding _binding;
    private readonly bool _isOutput;
    private readonly Action _changed;

    public VariableBindingItemViewModel(VariableBinding binding, bool isOutput, Action changed)
    {
        _binding = binding;
        _isOutput = isOutput;
        _changed = changed;
    }

    public VariableBinding Model => _binding;

    /// <summary>
    /// Left-hand box in the UI:
    /// - For Inputs (<c>subVar ← value</c>): sub-macro target variable (<see cref="VariableBinding.TargetVariable"/>).
    /// - For Outputs (<c>subVar → callerVar</c>): sub-macro source variable (<see cref="VariableBinding.SourceExpression"/>).
    /// </summary>
    public string Name
    {
        get => (_isOutput ? _binding.SourceExpression : _binding.TargetVariable) ?? string.Empty;
        set
        {
            var v = (value ?? string.Empty).Trim();
            if (_isOutput)
            {
                if (_binding.SourceExpression == v) return;
                _binding.SourceExpression = v;
            }
            else
            {
                if (_binding.TargetVariable == v) return;
                _binding.TargetVariable = v;
            }

            OnPropertyChanged();
            _changed();
        }
    }

    /// <summary>
    /// Right-hand box in the UI:
    /// - For Inputs (<c>subVar ← value</c>): caller expression or literal (<see cref="VariableBinding.SourceExpression"/>).
    /// - For Outputs (<c>subVar → callerVar</c>): caller target variable (<see cref="VariableBinding.TargetVariable"/>).
    /// </summary>
    public string Value
    {
        get => (_isOutput ? _binding.TargetVariable : _binding.SourceExpression) ?? string.Empty;
        set
        {
            var v = _isOutput ? (value ?? string.Empty).Trim() : (value ?? string.Empty);
            if (_isOutput)
            {
                if (_binding.TargetVariable == v) return;
                _binding.TargetVariable = v;
            }
            else
            {
                if (_binding.SourceExpression == v) return;
                _binding.SourceExpression = v;
            }

            OnPropertyChanged();
            _changed();
        }
    }
}

/// <summary>Editor for <see cref="CallMacroStep"/> (Pillar 5: reusable sub-macros with Inputs / Outputs).</summary>
public sealed class CallMacroStepViewModel : StepViewModel
{
    private readonly CallMacroStep _step;
    private Func<IReadOnlyList<Macro>>? _macroListProvider;
    private Guid? _excludeMacroId;

    public CallMacroStepViewModel(CallMacroStep step) : base(step)
    {
        _step = step;
        if (_step.Inputs is not null)
        {
            foreach (var b in _step.Inputs)
                Inputs.Add(new VariableBindingItemViewModel(b, isOutput: false, SyncBindings));
        }

        if (_step.Outputs is not null)
        {
            foreach (var b in _step.Outputs)
                Outputs.Add(new VariableBindingItemViewModel(b, isOutput: true, SyncBindings));
        }

        RefreshMacrosCommand = new RelayCommand(RefreshMacroChoices);
        AddInputCommand = new RelayCommand(AddInput);
        RemoveInputCommand = new RelayCommand(p => { if (p is VariableBindingItemViewModel item && Inputs.Remove(item)) SyncBindings(); });
        AddOutputCommand = new RelayCommand(AddOutput);
        RemoveOutputCommand = new RelayCommand(p => { if (p is VariableBindingItemViewModel item && Outputs.Remove(item)) SyncBindings(); });
    }

    public RelayCommand RefreshMacrosCommand { get; }
    public RelayCommand AddInputCommand { get; }
    public RelayCommand RemoveInputCommand { get; }
    public RelayCommand AddOutputCommand { get; }
    public RelayCommand RemoveOutputCommand { get; }

    public ObservableCollection<MacroChoice> MacroChoices { get; } = new();
    public ObservableCollection<VariableBindingItemViewModel> Inputs { get; } = new();
    public ObservableCollection<VariableBindingItemViewModel> Outputs { get; } = new();

    public bool HasInputs => Inputs.Count > 0;
    public bool HasOutputs => Outputs.Count > 0;

    public Func<IReadOnlyList<Macro>>? MacroListProvider
    {
        get => _macroListProvider;
        set
        {
            _macroListProvider = value;
            RefreshMacroChoices();
        }
    }

    public Guid? ExcludeMacroId
    {
        get => _excludeMacroId;
        set
        {
            _excludeMacroId = value;
            RefreshMacroChoices();
        }
    }

    public override string? PlainSummary =>
        string.IsNullOrWhiteSpace(_step.TargetMacroName) && _step.TargetMacroId == Guid.Empty
            ? "• Choose a macro from your library to run as a reusable sub-routine."
            : $"• Runs sub-macro \"{_step.TargetMacroName}\" with its own isolated memory{(Inputs.Count > 0 ? $", passing {Inputs.Count} input(s)" : "")}{(Outputs.Count > 0 ? $" and copying {Outputs.Count} output(s) back" : "")}.\n" +
              "• Exporting this macro automatically bundles its sub-macros into one self-contained .smacro file.";

    public string TargetMacroName
    {
        get => _step.TargetMacroName ?? string.Empty;
        set
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (_step.TargetMacroName == trimmed) return;
            _step.TargetMacroName = trimmed;
            var match = MacroChoices.FirstOrDefault(c => string.Equals(c.Name, trimmed, StringComparison.OrdinalIgnoreCase));
            if (match is not null && match.Id != Guid.Empty) _step.TargetMacroId = match.Id;
            NotifyChanged();
            OnPropertyChanged(nameof(SelectedMacroChoice));
        }
    }

    public MacroChoice? SelectedMacroChoice
    {
        get => (_step.TargetMacroId != Guid.Empty
                   ? MacroChoices.FirstOrDefault(c => c.Id == _step.TargetMacroId)
                   : null)
               ?? MacroChoices.FirstOrDefault(c => string.Equals(c.Name, _step.TargetMacroName, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is null) return;
            if (_step.TargetMacroId == value.Id && _step.TargetMacroName == value.Name) return;
            _step.TargetMacroId = value.Id;
            _step.TargetMacroName = value.Name;
            NotifyChanged();
            OnPropertyChanged(nameof(TargetMacroName));
        }
    }

    public void RefreshMacroChoices()
    {
        MacroLookupChoices.Fill(MacroChoices, _macroListProvider?.Invoke(), _excludeMacroId, _step.TargetMacroId, _step.TargetMacroName);
        OnPropertyChanged(nameof(SelectedMacroChoice));
    }

    private void AddInput()
    {
        var binding = new VariableBinding { TargetVariable = "param1", SourceExpression = "1" };
        Inputs.Add(new VariableBindingItemViewModel(binding, isOutput: false, SyncBindings));
        SyncBindings();
    }

    private void AddOutput()
    {
        var binding = new VariableBinding { SourceExpression = "result", TargetVariable = "callerVar" };
        Outputs.Add(new VariableBindingItemViewModel(binding, isOutput: true, SyncBindings));
        SyncBindings();
    }

    private void SyncBindings()
    {
        _step.Inputs = Inputs.Count == 0 ? null : Inputs.Select(i => i.Model).ToList();
        _step.Outputs = Outputs.Count == 0 ? null : Outputs.Select(o => o.Model).ToList();
        OnPropertyChanged(nameof(HasInputs));
        OnPropertyChanged(nameof(HasOutputs));
        NotifyChanged(nameof(Inputs));
    }
}

/// <summary>One declared variable in the Editor's 🧠 Variables popover (plus its live runtime value during/after playback).</summary>
public sealed class VariableItemViewModel : ObservableObject
{
    private readonly MacroVariableDefinition _model;
    private readonly Action _changed;
    private string? _liveValue;

    public VariableItemViewModel(MacroVariableDefinition model, Action changed)
    {
        _model = model;
        _changed = changed;
    }

    public static IReadOnlyList<Choice<MacroVariableType>> Types { get; } = new[]
    {
        new Choice<MacroVariableType>(MacroVariableType.Number, "Number"),
        new Choice<MacroVariableType>(MacroVariableType.Text, "Text"),
        new Choice<MacroVariableType>(MacroVariableType.Boolean, "True / False"),
    };

    public MacroVariableDefinition Model => _model;

    public string Name
    {
        get => _model.Name ?? string.Empty;
        set
        {
            var v = (value ?? string.Empty).Trim();
            if (_model.Name == v) return;
            _model.Name = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PlaceholderHint));
            _changed();
        }
    }

    public MacroVariableType Type
    {
        get => _model.Type;
        set
        {
            if (_model.Type == value) return;
            _model.Type = value;
            if (value == MacroVariableType.Number && string.IsNullOrWhiteSpace(_model.DefaultValue)) _model.DefaultValue = "0";
            else if (value == MacroVariableType.Boolean && _model.DefaultValue is not ("true" or "false")) _model.DefaultValue = "false";
            OnPropertyChanged();
            OnPropertyChanged(nameof(DefaultValue));
            _changed();
        }
    }

    public string DefaultValue
    {
        get => _model.DefaultValue ?? string.Empty;
        set
        {
            var v = value ?? string.Empty;
            if (_model.DefaultValue == v) return;
            _model.DefaultValue = v;
            OnPropertyChanged();
            _changed();
        }
    }

    public string PlaceholderHint => string.IsNullOrWhiteSpace(_model.Name) ? "{name}" : $"{{{_model.Name}}}";

    public string? LiveValue
    {
        get => _liveValue;
        set
        {
            if (SetProperty(ref _liveValue, value))
                OnPropertyChanged(nameof(HasLiveValue));
        }
    }

    public bool HasLiveValue => !string.IsNullOrEmpty(_liveValue);
}

/// <summary>One macro-level interrupt watcher in the Editor's 🛡 Watchers popover.</summary>
public sealed class WatcherItemViewModel : ObservableObject
{
    private readonly MacroWatcher _model;
    private readonly Action _changed;
    private Func<IReadOnlyList<Macro>>? _macroListProvider;
    private Guid? _excludeMacroId;

    public WatcherItemViewModel(MacroWatcher model, ScreenTools tools, Func<IReadOnlyList<Macro>>? macroListProvider, Guid? excludeMacroId, Action changed)
    {
        _model = model;
        _changed = changed;
        _macroListProvider = macroListProvider;
        _excludeMacroId = excludeMacroId;
        _model.Condition ??= new MacroCondition { Check = new FindTextStep { Text = "Close", Scope = SearchScope.PrimaryMonitor, TimeoutMs = 0 } };
        Condition = new ConditionViewModel(_model.Condition, () => _changed()) { Tools = tools };
        RefreshMacrosCommand = new RelayCommand(RefreshMacroChoices);
        RefreshMacroChoices();
    }

    public static IReadOnlyList<Choice<WatcherActionKind>> ActionChoices { get; } = new[]
    {
        new Choice<WatcherActionKind>(WatcherActionKind.ClickFoundSpot, "🖱 Click the found spot (dismiss popup)"),
        new Choice<WatcherActionKind>(WatcherActionKind.RunSubMacro, "🛡 Run a handler sub-macro"),
        new Choice<WatcherActionKind>(WatcherActionKind.RestartFromCheckpoint, "📍 Jump to a Checkpoint"),
        new Choice<WatcherActionKind>(WatcherActionKind.StopMacro, "⏹ Stop the macro"),
    };

    public MacroWatcher Model => _model;
    public ConditionViewModel Condition { get; }
    public RelayCommand RefreshMacrosCommand { get; }
    public ObservableCollection<MacroChoice> MacroChoices { get; } = new();

    public string Name
    {
        get => _model.Name ?? string.Empty;
        set
        {
            var v = value ?? string.Empty;
            if (_model.Name == v) return;
            _model.Name = v;
            OnPropertyChanged();
            _changed();
        }
    }

    public bool Enabled
    {
        get => _model.Enabled;
        set
        {
            if (_model.Enabled == value) return;
            _model.Enabled = value;
            OnPropertyChanged();
            _changed();
        }
    }

    public WatcherActionKind Action
    {
        get => _model.Action;
        set
        {
            if (_model.Action == value) return;
            _model.Action = value;
            if (value == WatcherActionKind.RunSubMacro) RefreshMacroChoices();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsClickSpot));
            OnPropertyChanged(nameof(IsSubMacro));
            OnPropertyChanged(nameof(IsCheckpointJump));
            _changed();
        }
    }

    public bool IsClickSpot => _model.Action == WatcherActionKind.ClickFoundSpot;
    public bool IsSubMacro => _model.Action == WatcherActionKind.RunSubMacro;
    public bool IsCheckpointJump => _model.Action == WatcherActionKind.RestartFromCheckpoint;

    public int CheckIntervalMs
    {
        get => _model.CheckIntervalMs;
        set
        {
            if (_model.CheckIntervalMs == value) return;
            _model.CheckIntervalMs = value;
            OnPropertyChanged();
            _changed();
        }
    }

    public int CooldownMs
    {
        get => _model.CooldownMs;
        set
        {
            if (_model.CooldownMs == value) return;
            _model.CooldownMs = value;
            OnPropertyChanged();
            _changed();
        }
    }

    public int ClickOffsetX
    {
        get => _model.ClickOffsetX;
        set { if (_model.ClickOffsetX == value) return; _model.ClickOffsetX = value; OnPropertyChanged(); _changed(); }
    }

    public int ClickOffsetY
    {
        get => _model.ClickOffsetY;
        set { if (_model.ClickOffsetY == value) return; _model.ClickOffsetY = value; OnPropertyChanged(); _changed(); }
    }

    public string HandlerMacroName
    {
        get => _model.HandlerMacroName ?? string.Empty;
        set
        {
            var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (_model.HandlerMacroName == trimmed) return;
            _model.HandlerMacroName = trimmed;
            var match = MacroChoices.FirstOrDefault(c => string.Equals(c.Name, trimmed, StringComparison.OrdinalIgnoreCase));
            if (match is not null && match.Id != Guid.Empty) _model.HandlerMacroId = match.Id;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedMacroChoice));
            _changed();
        }
    }

    public MacroChoice? SelectedMacroChoice
    {
        get => (_model.HandlerMacroId is { } id && id != Guid.Empty
                   ? MacroChoices.FirstOrDefault(c => c.Id == id)
                   : null)
               ?? MacroChoices.FirstOrDefault(c => string.Equals(c.Name, _model.HandlerMacroName, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is null) return;
            _model.HandlerMacroId = value.Id;
            _model.HandlerMacroName = value.Name;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HandlerMacroName));
            _changed();
        }
    }

    public string CheckpointName
    {
        get => _model.CheckpointName ?? string.Empty;
        set
        {
            var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (_model.CheckpointName == trimmed) return;
            _model.CheckpointName = trimmed;
            OnPropertyChanged();
            _changed();
        }
    }

    public void RefreshMacroChoices()
    {
        MacroLookupChoices.Fill(MacroChoices, _macroListProvider?.Invoke(), _excludeMacroId, _model.HandlerMacroId, _model.HandlerMacroName);
        OnPropertyChanged(nameof(SelectedMacroChoice));
    }
}

/// <summary>Opt-in Self-Healing suggestion item shown in the Editor after playback relocated a drifted click.</summary>
public sealed class SelfHealItemViewModel
{
    public SelfHealItemViewModel(SelfHealSuggestion suggestion, int? stepNumber)
    {
        Suggestion = suggestion;
        StepNumber = stepNumber;
    }

    public SelfHealSuggestion Suggestion { get; }
    public int? StepNumber { get; }

    public string SummaryText
    {
        get
        {
            var strategy = Suggestion.WinningStrategy switch
            {
                LocatorStrategyKind.UiAutomation => "UI Automation",
                LocatorStrategyKind.OcrText => "OCR Text",
                LocatorStrategyKind.ImageSnapshot => "Image Snapshot",
                LocatorStrategyKind.WindowRelative => "Window-Relative",
                _ => "Coordinates",
            };
            var num = StepNumber ?? Suggestion.StepNumber;
            return $"Step #{num}: ({Suggestion.OldX}, {Suggestion.OldY}) → ({Suggestion.NewX}, {Suggestion.NewY}) via {strategy}";
        }
    }
}

/// <summary>One declarative macro trigger row in the Editor's ⚡ Triggers popover / dock tab (v1.4 "Reflex").</summary>
public sealed class TriggerItemViewModel : ObservableObject
{
    private readonly MacroTrigger _model;
    private readonly Action _changed;

    public TriggerItemViewModel(MacroTrigger model, Action changed)
    {
        _model = model;
        _changed = changed;
    }

    public static IReadOnlyList<Choice<TriggerKind>> KindChoices { get; } = new[]
    {
        new Choice<TriggerKind>(TriggerKind.Hotkey, "⌨ Keyboard shortcut"),
        new Choice<TriggerKind>(TriggerKind.Schedule, "⏰ Schedule / Timer"),
        new Choice<TriggerKind>(TriggerKind.WindowEvent, "🪟 Window opened / focused"),
    };

    public static IReadOnlyList<Choice<HotkeyTriggerMode>> HotkeyModeChoices { get; } = new[]
    {
        new Choice<HotkeyTriggerMode>(HotkeyTriggerMode.PressOnce, "Press once (starts or stops)"),
        new Choice<HotkeyTriggerMode>(HotkeyTriggerMode.Toggle, "Toggle on / off"),
        new Choice<HotkeyTriggerMode>(HotkeyTriggerMode.HoldToRun, "Hold to run (stops on release)"),
        new Choice<HotkeyTriggerMode>(HotkeyTriggerMode.RepeatWhileHeld, "Repeat while held (loops until release)"),
    };

    public static IReadOnlyList<Choice<ScheduleRecurrence>> RecurrenceChoices { get; } = new[]
    {
        new Choice<ScheduleRecurrence>(ScheduleRecurrence.EveryNMinutes, "Every N minutes"),
        new Choice<ScheduleRecurrence>(ScheduleRecurrence.DailyAtTime, "Daily at HH:mm (24h)"),
        new Choice<ScheduleRecurrence>(ScheduleRecurrence.OnAppStartup, "Once when SmartMacro starts"),
    };

    public static IReadOnlyList<Choice<WindowTriggerEvent>> WindowEventChoices { get; } = new[]
    {
        new Choice<WindowTriggerEvent>(WindowTriggerEvent.WindowFocused, "When window gains focus"),
        new Choice<WindowTriggerEvent>(WindowTriggerEvent.WindowOpened, "When window first opens"),
    };

    public static IReadOnlyList<Choice<TriggerConflictPolicy>> ConflictPolicyChoices { get; } = new[]
    {
        new Choice<TriggerConflictPolicy>(TriggerConflictPolicy.IgnoreIfBusy, "Skip if another macro is playing"),
        new Choice<TriggerConflictPolicy>(TriggerConflictPolicy.StopCurrentAndRun, "Stop current macro & run this one"),
        new Choice<TriggerConflictPolicy>(TriggerConflictPolicy.QueueNext, "Queue to run right after current macro"),
    };

    public static IReadOnlyList<Choice<TriggerConflictPolicy>> ConflictChoices => ConflictPolicyChoices;

    public MacroTrigger Model => _model;

    public string Name
    {
        get => _model.Name ?? string.Empty;
        set
        {
            var v = value ?? string.Empty;
            if (_model.Name == v) return;
            _model.Name = v;
            OnPropertyChanged();
            _changed();
        }
    }

    public bool Enabled
    {
        get => _model.Enabled;
        set
        {
            if (_model.Enabled == value) return;
            _model.Enabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SummaryText));
            _changed();
        }
    }

    public TriggerKind Kind
    {
        get => _model.Kind;
        set
        {
            if (_model.Kind == value) return;
            _model.Kind = value;
            if (value == TriggerKind.Hotkey && _model.Hotkey is null)
                _model.Hotkey = new HotkeyBinding(KeyModifiers.Control | KeyModifiers.Alt, 0x70);
            if (value == TriggerKind.Schedule && _model.Recurrence == ScheduleRecurrence.DailyAtTime && string.IsNullOrWhiteSpace(_model.TimeOfDay))
                _model.TimeOfDay = "09:00";
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsHotkey));
            OnPropertyChanged(nameof(IsSchedule));
            OnPropertyChanged(nameof(IsWindowEvent));
            OnPropertyChanged(nameof(CanFilterByActiveWindow));
            OnPropertyChanged(nameof(SummaryText));
            _changed();
        }
    }

    public bool IsHotkey => _model.Kind == TriggerKind.Hotkey;
    public bool IsSchedule => _model.Kind == TriggerKind.Schedule;
    public bool IsWindowEvent => _model.Kind == TriggerKind.WindowEvent;
    public bool CanFilterByActiveWindow => _model.Kind is TriggerKind.Hotkey or TriggerKind.Schedule;

    public HotkeyTriggerMode HotkeyMode
    {
        get => _model.HotkeyMode;
        set
        {
            if (_model.HotkeyMode == value) return;
            _model.HotkeyMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SummaryText));
            _changed();
        }
    }

    public bool Control
    {
        get => (_model.Hotkey?.Modifiers ?? KeyModifiers.None).HasFlag(KeyModifiers.Control);
        set => SetModifier(KeyModifiers.Control, value);
    }

    public bool Shift
    {
        get => (_model.Hotkey?.Modifiers ?? KeyModifiers.None).HasFlag(KeyModifiers.Shift);
        set => SetModifier(KeyModifiers.Shift, value);
    }

    public bool Alt
    {
        get => (_model.Hotkey?.Modifiers ?? KeyModifiers.None).HasFlag(KeyModifiers.Alt);
        set => SetModifier(KeyModifiers.Alt, value);
    }

    public bool Windows
    {
        get => (_model.Hotkey?.Modifiers ?? KeyModifiers.None).HasFlag(KeyModifiers.Windows);
        set => SetModifier(KeyModifiers.Windows, value);
    }

    public int VirtualKey
    {
        get => _model.Hotkey?.VirtualKey ?? 0x70;
        set
        {
            var mods = _model.Hotkey?.Modifiers ?? (KeyModifiers.Control | KeyModifiers.Alt);
            if (_model.Hotkey?.VirtualKey == value) return;
            _model.Hotkey = new HotkeyBinding(mods, value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShortcutText));
            OnPropertyChanged(nameof(SummaryText));
            _changed();
        }
    }

    public HotkeyBinding? Hotkey
    {
        get => _model.Hotkey;
        set
        {
            if (Equals(_model.Hotkey, value)) return;
            _model.Hotkey = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Control));
            OnPropertyChanged(nameof(Shift));
            OnPropertyChanged(nameof(Alt));
            OnPropertyChanged(nameof(Windows));
            OnPropertyChanged(nameof(VirtualKey));
            OnPropertyChanged(nameof(ShortcutText));
            OnPropertyChanged(nameof(HotkeyText));
            OnPropertyChanged(nameof(SummaryText));
            _changed();
        }
    }

    public string ShortcutText => _model.Hotkey?.ToString() ?? "None";
    public string HotkeyText => ShortcutText;

    public ScheduleRecurrence Recurrence
    {
        get => _model.Recurrence;
        set
        {
            if (_model.Recurrence == value) return;
            _model.Recurrence = value;
            if (value == ScheduleRecurrence.DailyAtTime && string.IsNullOrWhiteSpace(_model.TimeOfDay))
                _model.TimeOfDay = "09:00";
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsEveryNMinutes));
            OnPropertyChanged(nameof(IsDailyAtTime));
            OnPropertyChanged(nameof(TimeOfDay));
            OnPropertyChanged(nameof(SummaryText));
            _changed();
        }
    }

    public bool IsEveryNMinutes => _model.Recurrence == ScheduleRecurrence.EveryNMinutes;
    public bool IsDailyAtTime => _model.Recurrence == ScheduleRecurrence.DailyAtTime;

    public int IntervalMinutes
    {
        get => _model.IntervalMinutes;
        set
        {
            if (_model.IntervalMinutes == value) return;
            _model.IntervalMinutes = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SummaryText));
            _changed();
        }
    }

    public string TimeOfDay
    {
        get => _model.TimeOfDay ?? "09:00";
        set
        {
            var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (_model.TimeOfDay == trimmed) return;
            _model.TimeOfDay = trimmed;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SummaryText));
            _changed();
        }
    }

    public WindowTriggerEvent WindowEvent
    {
        get => _model.WindowEvent;
        set
        {
            if (_model.WindowEvent == value) return;
            _model.WindowEvent = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SummaryText));
            _changed();
        }
    }

    public string WindowTitle
    {
        get => _model.WindowTitle ?? string.Empty;
        set
        {
            var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (_model.WindowTitle == trimmed) return;
            _model.WindowTitle = trimmed;
            OnPropertyChanged();
            OnPropertyChanged(nameof(WindowTitleMatch));
            OnPropertyChanged(nameof(SummaryText));
            _changed();
        }
    }

    public string WindowTitleMatch
    {
        get => WindowTitle;
        set => WindowTitle = value;
    }

    public bool OnlyWhenWindowActive
    {
        get => _model.OnlyWhenWindowActive;
        set
        {
            if (_model.OnlyWhenWindowActive == value) return;
            _model.OnlyWhenWindowActive = value;
            OnPropertyChanged();
            _changed();
        }
    }

    public int CooldownMs
    {
        get => _model.CooldownMs;
        set
        {
            if (_model.CooldownMs == value) return;
            _model.CooldownMs = value;
            OnPropertyChanged();
            _changed();
        }
    }

    public TriggerConflictPolicy ConflictPolicy
    {
        get => _model.ConflictPolicy;
        set
        {
            if (_model.ConflictPolicy == value) return;
            _model.ConflictPolicy = value;
            OnPropertyChanged();
            _changed();
        }
    }

    public string SummaryText => _model.DescribeShort();

    private void SetModifier(KeyModifiers flag, bool on)
    {
        var current = _model.Hotkey?.Modifiers ?? KeyModifiers.None;
        var next = on ? current | flag : current & ~flag;
        if (next == current) return;
        _model.Hotkey = new HotkeyBinding(next, VirtualKey);
        OnPropertyChanged(nameof(Control));
        OnPropertyChanged(nameof(Shift));
        OnPropertyChanged(nameof(Alt));
        OnPropertyChanged(nameof(Windows));
        OnPropertyChanged(nameof(ShortcutText));
        OnPropertyChanged(nameof(SummaryText));
        _changed();
    }
}
