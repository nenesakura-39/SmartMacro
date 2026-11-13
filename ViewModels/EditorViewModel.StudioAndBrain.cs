using System.Collections.ObjectModel;
using System.Collections.Specialized;
using SmartMacro.Core.Logging;
using SmartMacro.Core.Models;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Targeting;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Workflow;
using SmartMacro.Mvvm;
using SmartMacro.Services;
using SmartMacro.Services.Ocr;
using SmartMacro.Services.Vision;
using SmartMacro.ViewModels.Steps;

namespace SmartMacro.ViewModels;


public sealed partial class EditorViewModel : ObservableObject
{
    // ---------------- v1.2 Brain: 🧠 Variables, 🛡 Watchers & 🩹 Self-Healing Suggestions ----------------

    /// <summary>Set by MainViewModel: returns the saved macros in the library (for Call Macro, Recovery Macro, and call-graph checks).</summary>
    public Func<IReadOnlyList<Macro>>? GetLibraryMacros { get; set; }

    /// <summary>Set by MainViewModel: opens a referenced sub-macro in the Workflow Editor ("Open ↗").</summary>
    public Func<Guid, string?, Task>? OpenSubMacroHandler { get; set; }

    private string _toolboxFilter = string.Empty;
    private bool _isBottomDockExpanded;
    private int _selectedBottomTab;
    private readonly Dictionary<Guid, BreakpointRule?> _breakpoints = new();
    private readonly Dictionary<Guid, (Guid MacroId, string MacroName, int StepNumber, string StepTitle)> _breakpointMeta = new();
    private int _runHistoryTotal;
    private int _runHistorySucceeded;
    private string _lastRunSummaryText = string.Empty;
    private bool _isPlaybackPaused;
    private string _selectedBreakpointExpression = string.Empty;
    private string? _breakpointRuleError;
    private string _newWatchpointVariableName = string.Empty;
    private string _newWatchpointExpression = string.Empty;
    private string? _watchpointRuleError;
    private readonly List<ExecutionTraceEntry> _traceAll = new();
    private string _traceFilterText = string.Empty;
    private TraceOutcomeFilter _traceOutcomeFilter = TraceOutcomeFilter.All;
    private string? _failureTracePath;

    /// <summary>v1.8 Studio X Debugger: set by MainViewModel to execute one step while paused (F10).</summary>
    public Func<bool>? StepOverHandler { get; set; }

    /// <summary>Debugger 2.0: set by MainViewModel to step into sub-macro calls while paused (F11).</summary>
    public Func<bool>? StepIntoHandler { get; set; }

    /// <summary>Debugger 2.0: set by MainViewModel to step out of the active sub-macro frame while paused (Shift+F11).</summary>
    public Func<bool>? StepOutHandler { get; set; }

    /// <summary>Debugger 2.0: set by MainViewModel to mutate a live runtime variable while paused.</summary>
    public Func<string, string?, (bool Success, string? Error)>? SetLiveVariableHandler { get; set; }

    public RelayCommand ClearToolboxFilterCommand { get; }
    public RelayCommand OpenSubMacroCommand { get; }
    public RelayCommand RunGivenStepCommand { get; }
    public RelayCommand TestGivenStepCommand { get; }
    public RelayCommand DeleteGivenStepCommand { get; }
    public RelayCommand OpenBottomTabCommand { get; }
    public RelayCommand ToggleBreakpointCommand { get; }
    public RelayCommand StepOverCommand { get; }
    public RelayCommand StepIntoCommand { get; }
    public RelayCommand StepOutCommand { get; }
    public RelayCommand ApplyBreakpointConditionCommand { get; }
    public RelayCommand ClearBreakpointConditionCommand { get; }
    public RelayCommand RemoveBreakpointItemCommand { get; }
    public RelayCommand AddWatchpointCommand { get; }
    public RelayCommand RemoveWatchpointCommand { get; }
    public RelayCommand ApplyLiveVariableCommand { get; }
    public RelayCommand OpenCallStackFrameCommand { get; }
    public RelayCommand ClearTraceCommand { get; }
    public RelayCommand SetTraceOutcomeFilterCommand { get; }
    public RelayCommand OpenFailureTraceCommand { get; }
    public RelayCommand DismissFailureTraceCommand { get; }
    public RelayCommand DismissFailureBannerCommand { get; }
    public RelayCommand CopyCliCommand { get; }

    /// <summary>True when a step ID has an active debugger breakpoint (F9).</summary>
    public bool HasStepBreakpoint(Guid stepId) => _breakpoints.ContainsKey(stepId);

    /// <summary>Optional conditional rule attached to <paramref name="stepId"/>.</summary>
    public BreakpointRule? GetBreakpointRule(Guid stepId) => _breakpoints.TryGetValue(stepId, out var rule) ? rule : null;

    /// <summary>Number of active breakpoints across opened workflows.</summary>
    public int BreakpointCount => _breakpoints.Count;

    public bool HasActiveBreakpoints => ActiveBreakpoints.Count > 0;
    public bool HasWatchpoints => VariableWatchpoints.Count > 0;
    public bool HasActiveCallStack => CallStackFrames.Count > 0;
    public bool HasActiveFrameVariables => ActiveFrameVariables.Count > 0;
    public bool HasTraceEntries => _traceAll.Count > 0;

    public bool IsPlaybackPaused
    {
        get => _isPlaybackPaused;
        set
        {
            if (SetProperty(ref _isPlaybackPaused, value))
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    public string DebuggerSummary
    {
        get
        {
            var count = _breakpoints.Count + VariableWatchpoints.Count;
            return count == 0 ? "Debug" : $"Debug ({count})";
        }
    }

    public string WatchpointsSummary => VariableWatchpoints.Count == 0
        ? "Watchpoints"
        : $"Watchpoints ({VariableWatchpoints.Count})";

    public string ActiveCallStackSummary => CallStackFrames.Count == 0
        ? "No active call stack"
        : CallStackFrames[^1].DetailText;

    public string TraceSummary => _traceAll.Count == 0 ? "Trace" : $"Trace ({_traceAll.Count})";

    public string SelectedBreakpointExpression
    {
        get => _selectedBreakpointExpression;
        set
        {
            if (SetProperty(ref _selectedBreakpointExpression, value ?? string.Empty))
            {
                BreakpointRuleError = null;
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string? BreakpointRuleError
    {
        get => _breakpointRuleError;
        private set
        {
            if (SetProperty(ref _breakpointRuleError, value))
                OnPropertyChanged(nameof(HasBreakpointRuleError));
        }
    }

    public bool HasBreakpointRuleError => !string.IsNullOrEmpty(_breakpointRuleError);

    public string NewWatchpointVariableName
    {
        get => _newWatchpointVariableName;
        set
        {
            if (SetProperty(ref _newWatchpointVariableName, value ?? string.Empty))
            {
                WatchpointRuleError = null;
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string NewWatchpointExpression
    {
        get => _newWatchpointExpression;
        set
        {
            if (SetProperty(ref _newWatchpointExpression, value ?? string.Empty))
                WatchpointRuleError = null;
        }
    }

    public string? WatchpointRuleError
    {
        get => _watchpointRuleError;
        private set
        {
            if (SetProperty(ref _watchpointRuleError, value))
                OnPropertyChanged(nameof(HasWatchpointRuleError));
        }
    }

    public bool HasWatchpointRuleError => !string.IsNullOrEmpty(_watchpointRuleError);

    public string TraceFilterText
    {
        get => _traceFilterText;
        set
        {
            if (SetProperty(ref _traceFilterText, value ?? string.Empty))
                RefreshFilteredTrace();
        }
    }

    public TraceOutcomeFilter TraceOutcomeFilter
    {
        get => _traceOutcomeFilter;
        set
        {
            if (SetProperty(ref _traceOutcomeFilter, value))
            {
                OnPropertyChanged(nameof(IsTraceFilterAll));
                OnPropertyChanged(nameof(IsTraceFilterFailed));
                OnPropertyChanged(nameof(IsTraceFilterVision));
                RefreshFilteredTrace();
            }
        }
    }

    public bool IsTraceFilterAll => _traceOutcomeFilter == TraceOutcomeFilter.All;
    public bool IsTraceFilterFailed => _traceOutcomeFilter == TraceOutcomeFilter.Failed;
    public bool IsTraceFilterVision => _traceOutcomeFilter == TraceOutcomeFilter.Vision;

    public string? FailureTracePath
    {
        get => _failureTracePath;
        set
        {
            if (SetProperty(ref _failureTracePath, value))
            {
                OnPropertyChanged(nameof(HasFailureTrace));
                OnPropertyChanged(nameof(HasFailureBanner));
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool HasFailureTrace => !string.IsNullOrWhiteSpace(_failureTracePath) && System.IO.File.Exists(_failureTracePath);
    public bool HasFailureBanner => HasFailureSnapshot || HasFailureTrace;

    /// <summary>Total runs recorded for this macro during the current editor session.</summary>
    public int RunHistoryTotal => _runHistoryTotal;

    /// <summary>Completed runs recorded for this macro during the current editor session.</summary>
    public int RunHistorySucceeded => _runHistorySucceeded;

    public bool HasRunHistory => _runHistoryTotal > 0;

    public string RunHistorySummaryText => _runHistoryTotal == 0
        ? string.Empty
        : $"📊 {_runHistorySucceeded}/{_runHistoryTotal} runs OK · {_lastRunSummaryText}";

    private void ToggleBreakpoint(StepViewModel? step)
    {
        if (!HasMacro || step is null) return;
        bool added;
        if (_breakpoints.ContainsKey(step.Id))
        {
            _breakpoints.Remove(step.Id);
            _breakpointMeta.Remove(step.Id);
            added = false;
        }
        else
        {
            _breakpoints[step.Id] = null;
            _breakpointMeta[step.Id] = (MacroId ?? Guid.Empty, Name, step.Number, step.Title);
            added = true;
        }
        step.HasBreakpoint = added;
        step.BreakpointCondition = null;
        if (ReferenceEquals(step, SelectedStep))
            SyncSelectedBreakpointEditor();
        RebuildActiveBreakpoints();
        _setStatus(added
            ? $"🔴 Breakpoint set on step #{step.Number} ({step.Title}). Playback pauses before this step (F10 over, F11 into, Shift+F11 out, F8 resume)."
            : $"⚪ Breakpoint removed from step #{step.Number}.", StatusKind.Info);
    }

    private void SyncSelectedBreakpointEditor()
    {
        BreakpointRuleError = null;
        if (SelectedStep is { } step && _breakpoints.TryGetValue(step.Id, out var rule))
            _selectedBreakpointExpression = rule?.Expression ?? string.Empty;
        else
            _selectedBreakpointExpression = string.Empty;
        OnPropertyChanged(nameof(SelectedBreakpointExpression));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private void ApplyBreakpointCondition()
    {
        if (!HasMacro || SelectedStep is not { } step) return;
        var raw = SelectedBreakpointExpression.Trim();
        BreakpointRule? rule = null;
        if (!string.IsNullOrEmpty(raw))
        {
            if (!DebugConditionEvaluator.TryParseExpression(raw, out _, out _, out var err))
            {
                BreakpointRuleError = err;
                _setStatus(err ?? "Invalid breakpoint condition.", StatusKind.Warning);
                return;
            }
            rule = new BreakpointRule { StepId = step.Id, Expression = raw };
        }

        BreakpointRuleError = null;
        _breakpoints[step.Id] = rule;
        _breakpointMeta[step.Id] = (MacroId ?? Guid.Empty, Name, step.Number, step.Title);
        step.HasBreakpoint = true;
        step.BreakpointCondition = rule?.Expression;
        RebuildActiveBreakpoints();
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        _setStatus(rule is null
            ? $"🔴 Unconditional breakpoint active on step #{step.Number}."
            : $"⚙ Conditional breakpoint set on step #{step.Number}: {rule.Expression}", StatusKind.Success);
    }

    private void ClearBreakpointCondition()
    {
        if (!HasMacro || SelectedStep is not { } step) return;
        BreakpointRuleError = null;
        _selectedBreakpointExpression = string.Empty;
        OnPropertyChanged(nameof(SelectedBreakpointExpression));
        if (_breakpoints.ContainsKey(step.Id))
        {
            _breakpoints[step.Id] = null;
            step.BreakpointCondition = null;
            RebuildActiveBreakpoints();
            _setStatus($"🔴 Condition cleared on step #{step.Number} breakpoint (now unconditional).", StatusKind.Info);
        }
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private void RemoveBreakpointItem(object? parameter)
    {
        if (parameter is not ConditionalBreakpointItemViewModel item) return;
        _breakpoints.Remove(item.StepId);
        _breakpointMeta.Remove(item.StepId);
        if (Steps.FirstOrDefault(s => s.Id == item.StepId) is { } match)
        {
            match.HasBreakpoint = false;
            match.BreakpointCondition = null;
        }
        if (SelectedStep?.Id == item.StepId)
            SyncSelectedBreakpointEditor();
        RebuildActiveBreakpoints();
    }

    private void RebuildActiveBreakpoints()
    {
        ActiveBreakpoints.Clear();
        foreach (var (stepId, rule) in _breakpoints)
        {
            _breakpointMeta.TryGetValue(stepId, out var meta);
            ActiveBreakpoints.Add(new ConditionalBreakpointItemViewModel
            {
                MacroId = meta.MacroId,
                MacroName = string.IsNullOrWhiteSpace(meta.MacroName) ? "Macro" : meta.MacroName,
                StepId = stepId,
                StepNumber = meta.StepNumber,
                StepTitle = string.IsNullOrWhiteSpace(meta.StepTitle) ? "Step" : meta.StepTitle,
                ConditionText = rule?.Expression ?? string.Empty,
            });
        }
        OnPropertyChanged(nameof(BreakpointCount));
        OnPropertyChanged(nameof(HasActiveBreakpoints));
        OnPropertyChanged(nameof(DebuggerSummary));
    }

    private void AddWatchpoint(object? parameter)
    {
        if (!HasMacro) return;
        var varName = (parameter as string ?? NewWatchpointVariableName).Trim();
        if (string.IsNullOrWhiteSpace(varName)) return;

        var expr = parameter is string ? string.Empty : NewWatchpointExpression.Trim();
        if (!string.IsNullOrEmpty(expr) && !DebugConditionEvaluator.TryParseExpression(expr, out _, out _, out var err))
        {
            WatchpointRuleError = err;
            _setStatus(err ?? "Invalid watchpoint filter.", StatusKind.Warning);
            return;
        }

        WatchpointRuleError = null;
        var vm = new WatchpointItemViewModel(varName, expr, OnWatchpointsModified);
        VariableWatchpoints.Add(vm);
        if (parameter is not string)
        {
            _newWatchpointVariableName = string.Empty;
            _newWatchpointExpression = string.Empty;
            OnPropertyChanged(nameof(NewWatchpointVariableName));
            OnPropertyChanged(nameof(NewWatchpointExpression));
        }
        OnWatchpointsModified();
        _setStatus(string.IsNullOrEmpty(expr)
            ? $"👁 Watchpoint added for {{{varName}}} (pauses on value change)."
            : $"👁 Watchpoint added for {{{varName}}} when {expr}.", StatusKind.Success);
    }

    private void RemoveWatchpoint(object? parameter)
    {
        if (parameter is not WatchpointItemViewModel wp) return;
        if (VariableWatchpoints.Remove(wp))
            OnWatchpointsModified();
    }

    private void OnWatchpointsModified()
    {
        OnPropertyChanged(nameof(HasWatchpoints));
        OnPropertyChanged(nameof(WatchpointsSummary));
        OnPropertyChanged(nameof(DebuggerSummary));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    public IReadOnlyList<VariableWatchpoint> GetCoreWatchpoints()
        => VariableWatchpoints.Select(w => w.ToCoreWatchpoint()).ToList();

    private void ExecuteStepOver()
    {
        if (StepOverHandler?.Invoke() == true)
            _setStatus("⏭ Stepping over current step (F10)…", StatusKind.Info);
        else
            _setStatus("Pause playback first (F8 or hit a 🔴 breakpoint), then press F10 to step over one step.", StatusKind.Info);
    }

    private void ExecuteStepInto()
    {
        if (StepIntoHandler?.Invoke() == true)
            _setStatus("⏬ Stepping into sub-macro / next step (F11)…", StatusKind.Info);
        else
            _setStatus("Pause playback first (F8 or hit a 🔴 breakpoint), then press F11 to step into calls.", StatusKind.Info);
    }

    private void ExecuteStepOut()
    {
        if (StepOutHandler?.Invoke() == true)
            _setStatus("⏫ Stepping out to caller macro (Shift+F11)…", StatusKind.Info);
        else
            _setStatus("Pause inside a sub-macro first (F11 / breakpoint), then press Shift+F11 to step out.", StatusKind.Info);
    }

    private void ApplyLiveVariable(object? parameter)
    {
        if (!IsPlaybackPaused)
        {
            _setStatus("Pause playback first (F8 / breakpoint) to mutate runtime variables.", StatusKind.Warning);
            return;
        }

        string name;
        string value;
        Action revert;
        switch (parameter)
        {
            case VariableItemViewModel declared:
                name = declared.Name;
                value = declared.EditableLiveValue;
                revert = () => declared.EditableLiveValue = declared.LiveValue ?? declared.DefaultValue;
                break;
            case LiveRuntimeVariableViewModel live:
                name = live.Name;
                value = live.EditableLiveValue;
                revert = () => live.EditableLiveValue = live.LiveValue;
                break;
            default:
                return;
        }

        if (SetLiveVariableHandler is null) return;
        var (ok, error) = SetLiveVariableHandler(name, value);
        if (!ok)
        {
            revert();
            _setStatus(error ?? $"Could not update live variable {{{name}}}.", StatusKind.Warning);
            return;
        }

        _setStatus($"✏ Live variable {{{name}}} set to \"{value}\" while paused.", StatusKind.Success);
    }

    private void OpenCallStackFrame(object? parameter)
    {
        if (parameter is not CallStackFrame frame) return;
        if (HasMacro && MacroId == frame.MacroId)
        {
            if (frame.StepId is { } stepId && Steps.FirstOrDefault(s => s.Id == stepId) is { } match)
            {
                Reveal(match);
                SelectedStep = match;
                RevealStepRequested?.Invoke(this, match);
            }
            return;
        }
        _ = OpenSubMacroHandler?.Invoke(frame.MacroId, frame.MacroName);
    }

    private void CopyCliRunCommand()
    {
        if (!HasMacro) return;
        var target = string.IsNullOrWhiteSpace(Name) ? MacroId.ToString() : Name.Trim();
        var cli = CliCommandLine.FormatRunCommand(Environment.ProcessPath, target, noFocus: true);
        try
        {
            System.Windows.Clipboard.SetText(cli);
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            _log.Warning("The clipboard is busy; could not copy CLI command.", ex);
            _setStatus("The clipboard is busy (another app is using it). Try copying again.", StatusKind.Warning);
            return;
        }

        _setStatus(IsDirty
            ? $"Copied CLI command (Save first so --run finds it in the Library): {cli}"
            : $"Copied CLI command for Stream Deck / Task Scheduler: {cli}",
            StatusKind.Success);
    }

    /// <summary>Search filter typed at the top of the Add Step toolbox.</summary>
    public string ToolboxFilter
    {
        get => _toolboxFilter;
        set
        {
            if (!SetProperty(ref _toolboxFilter, value ?? string.Empty)) return;
            OnPropertyChanged(nameof(HasToolboxFilter));
            OnPropertyChanged(nameof(FilteredInputSteps));
            OnPropertyChanged(nameof(FilteredVisionSteps));
            OnPropertyChanged(nameof(FilteredUtilitySteps));
            OnPropertyChanged(nameof(FilteredLogicSteps));
            OnPropertyChanged(nameof(FilteredBrainSteps));
            OnPropertyChanged(nameof(FilteredQuickPatterns));
            OnPropertyChanged(nameof(HasFilteredInputSteps));
            OnPropertyChanged(nameof(HasFilteredVisionSteps));
            OnPropertyChanged(nameof(HasFilteredUtilitySteps));
            OnPropertyChanged(nameof(HasFilteredLogicSteps));
            OnPropertyChanged(nameof(HasFilteredBrainSteps));
            OnPropertyChanged(nameof(HasFilteredQuickPatterns));
        }
    }

    public bool HasToolboxFilter => !string.IsNullOrWhiteSpace(_toolboxFilter);

    public IReadOnlyList<StepTypeInfo> FilteredInputSteps => FilterStepTypes(
        StepTypeCatalog.Addable.Where(t => Converters.StepCategories.Of(t.Kind) is Converters.StepCategories.Input or Converters.StepCategories.Timing), _toolboxFilter);

    public IReadOnlyList<StepTypeInfo> FilteredVisionSteps => FilterStepTypes(
        StepTypeCatalog.Addable.Where(t => Converters.StepCategories.Of(t.Kind) is Converters.StepCategories.Vision or Converters.StepCategories.Feedback), _toolboxFilter);

    public IReadOnlyList<StepTypeInfo> FilteredUtilitySteps => FilterStepTypes(
        StepTypeCatalog.Addable.Where(t => Converters.StepCategories.Of(t.Kind) == Converters.StepCategories.Utility), _toolboxFilter);

    public IReadOnlyList<StepTypeInfo> FilteredLogicSteps => FilterStepTypes(StepTypeCatalog.Logic, _toolboxFilter);

    public IReadOnlyList<StepTypeInfo> FilteredBrainSteps => FilterStepTypes(StepTypeCatalog.Brain, _toolboxFilter);

    public IReadOnlyList<QuickPatternInfo> FilteredQuickPatterns => string.IsNullOrWhiteSpace(_toolboxFilter)
        ? QuickPatterns.All
        : QuickPatterns.All.Where(p =>
            p.Title.Contains(_toolboxFilter.Trim(), StringComparison.OrdinalIgnoreCase) ||
            p.Description.Contains(_toolboxFilter.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

    public bool HasFilteredInputSteps => FilteredInputSteps.Count > 0;
    public bool HasFilteredVisionSteps => FilteredVisionSteps.Count > 0;
    public bool HasFilteredUtilitySteps => FilteredUtilitySteps.Count > 0;
    public bool HasFilteredLogicSteps => FilteredLogicSteps.Count > 0;
    public bool HasFilteredBrainSteps => FilteredBrainSteps.Count > 0;
    public bool HasFilteredQuickPatterns => FilteredQuickPatterns.Count > 0;

    /// <summary>Filters toolbox step items by display name or description.</summary>
    public static IReadOnlyList<StepTypeInfo> FilterStepTypes(IEnumerable<StepTypeInfo> source, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return source.ToList();
        var q = query.Trim();
        return source.Where(s =>
            s.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            s.Description.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public bool IsBottomDockExpanded
    {
        get => _isBottomDockExpanded;
        set => SetProperty(ref _isBottomDockExpanded, value);
    }

    public int SelectedBottomTab
    {
        get => _selectedBottomTab;
        set => SetProperty(ref _selectedBottomTab, Math.Clamp(value, 0, 6));
    }

    public ObservableCollection<VariableItemViewModel> Variables { get; } = new();
    public ObservableCollection<WatcherItemViewModel> Watchers { get; } = new();
    public ObservableCollection<TriggerItemViewModel> Triggers { get; } = new();
    public ObservableCollection<SelfHealItemViewModel> PendingSuggestions { get; } = new();
    public ObservableCollection<ConditionalBreakpointItemViewModel> ActiveBreakpoints { get; } = new();
    public ObservableCollection<WatchpointItemViewModel> VariableWatchpoints { get; } = new();
    public ObservableCollection<CallStackFrame> CallStackFrames { get; } = new();
    public ObservableCollection<LiveRuntimeVariableViewModel> ActiveFrameVariables { get; } = new();
    public ObservableCollection<ExecutionTraceEntry> FilteredTraceEntries { get; } = new();

    public RelayCommand AddVariableCommand { get; }
    public RelayCommand RemoveVariableCommand { get; }
    public RelayCommand AddWatcherCommand { get; }
    public RelayCommand RemoveWatcherCommand { get; }
    public RelayCommand AddTriggerCommand { get; }
    public RelayCommand RemoveTriggerCommand { get; }
    public RelayCommand ApplySuggestionCommand { get; }
    public RelayCommand ApplyAllSuggestionsCommand { get; }
    public RelayCommand DismissSuggestionsCommand { get; }
    public RelayCommand OpenFailureSnapshotCommand { get; }
    public RelayCommand DismissFailureSnapshotCommand { get; }

    public bool HasVariables => Variables.Count > 0;
    public bool HasWatchers => Watchers.Count > 0;
    public bool HasTriggers => Triggers.Count > 0;
    public bool HasPendingSuggestions => PendingSuggestions.Count > 0;

    public string VariablesSummary => Variables.Count == 0 ? "Variables" : $"Variables ({Variables.Count})";
    public string WatchersSummary => Watchers.Count == 0 ? "Watchers" : $"Watchers ({Watchers.Count})";
    public string TriggersSummary => Triggers.Count == 0 ? "Triggers" : $"Triggers ({Triggers.Count})";

    public string SelfHealBannerText => PendingSuggestions.Count == 0
        ? string.Empty
        : PendingSuggestions.Count == 1
            ? $"🔧 Smart Self-Heal Suggestion — {PendingSuggestions[0].SummaryText}"
            : $"🔧 {PendingSuggestions.Count} Smart Self-Heal Suggestions — {PendingSuggestions[0].SummaryText} (+{PendingSuggestions.Count - 1} more)";

    private void AddVariable()
    {
        if (!HasMacro) return;
        var index = Variables.Count + 1;
        var name = "count";
        while (Variables.Any(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)))
            name = $"var{index++}";

        Variables.Add(new VariableItemViewModel(new MacroVariableDefinition
        {
            Name = name,
            Type = MacroVariableType.Number,
            DefaultValue = "0",
        }, OnVariablesOrWatchersModified));
        OnVariablesOrWatchersModified();
    }

    private void AddWatcher()
    {
        if (!HasMacro) return;
        var index = Watchers.Count + 1;
        var model = new MacroWatcher
        {
            Name = $"Popup guard {index}",
            Enabled = true,
            Action = WatcherActionKind.ClickFoundSpot,
            CheckIntervalMs = 1000,
            CooldownMs = 3000,
            Condition = new Condition
            {
                Check = new FindTextStep { Text = "Close", Scope = Core.Vision.SearchScope.PrimaryMonitor, TimeoutMs = 0 },
            },
        };
        Watchers.Add(new WatcherItemViewModel(model, _screenTools, () => GetLibraryMacros?.Invoke() ?? Array.Empty<Macro>(), MacroId, OnVariablesOrWatchersModified));
        OnVariablesOrWatchersModified();
    }

    private void AddTrigger()
    {
        if (!HasMacro) return;
        var index = Triggers.Count + 1;
        var vk = 0x70; // F1
        for (var candidate = 0x70; candidate <= 0x7A; candidate++)
        {
            var binding = new HotkeyBinding(KeyModifiers.Control | KeyModifiers.Alt, candidate);
            if (!Triggers.Any(t => t.Model.Hotkey == binding))
            {
                vk = candidate;
                break;
            }
        }

        var model = new MacroTrigger
        {
            Name = $"Trigger {index}",
            Enabled = true,
            Kind = TriggerKind.Hotkey,
            Hotkey = new HotkeyBinding(KeyModifiers.Control | KeyModifiers.Alt, vk),
            HotkeyMode = HotkeyTriggerMode.PressOnce,
        };
        Triggers.Add(new TriggerItemViewModel(model, OnVariablesOrWatchersModified));
        OnVariablesOrWatchersModified();
    }

    private void OnVariablesOrWatchersModified()
    {
        OnPropertyChanged(nameof(HasVariables));
        OnPropertyChanged(nameof(HasWatchers));
        OnPropertyChanged(nameof(HasTriggers));
        OnPropertyChanged(nameof(VariablesSummary));
        OnPropertyChanged(nameof(WatchersSummary));
        OnPropertyChanged(nameof(TriggersSummary));
        Touch();
    }

    /// <summary>Resets runtime debugger telemetry at the start of a new playback run.</summary>
    public void BeginRuntimeDebugSession()
    {
        IsPlaybackPaused = false;
        CallStackFrames.Clear();
        OnPropertyChanged(nameof(HasActiveCallStack));
        OnPropertyChanged(nameof(ActiveCallStackSummary));
        _traceAll.Clear();
        FilteredTraceEntries.Clear();
        OnPropertyChanged(nameof(HasTraceEntries));
        OnPropertyChanged(nameof(TraceSummary));
        FailureSnapshotPath = null;
        FailureTracePath = null;
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Updates live runtime values shown next to variables and in the active Debugger frame table.</summary>
    public void UpdateLiveVariables(IReadOnlyList<VariableSnapshot>? snapshots)
    {
        if (snapshots is null || snapshots.Count == 0)
        {
            foreach (var v in Variables) v.LiveValue = null;
            if (ActiveFrameVariables.Count > 0)
            {
                ActiveFrameVariables.Clear();
                OnPropertyChanged(nameof(HasActiveFrameVariables));
            }
            return;
        }

        var map = snapshots.ToDictionary(s => s.Name, s => s, StringComparer.OrdinalIgnoreCase);
        foreach (var v in Variables)
            v.LiveValue = map.TryGetValue(v.Name, out var snap) ? snap.Value : null;

        // Sync active frame variables cleanly in-place when keys match so paused editing keeps focus.
        var sameOrder = ActiveFrameVariables.Count == snapshots.Count &&
                        !snapshots.Where((s, idx) => !string.Equals(ActiveFrameVariables[idx].Name, s.Name, StringComparison.OrdinalIgnoreCase)).Any();
        if (sameOrder)
        {
            for (var i = 0; i < snapshots.Count; i++)
            {
                var snap = snapshots[i];
                var row = ActiveFrameVariables[i];
                row.Type = snap.Type;
                row.LiveValue = snap.Value;
            }
        }
        else
        {
            ActiveFrameVariables.Clear();
            foreach (var snap in snapshots)
                ActiveFrameVariables.Add(new LiveRuntimeVariableViewModel(snap.Name, snap.Type, snap.Value));
            OnPropertyChanged(nameof(HasActiveFrameVariables));
        }
    }

    /// <summary>Updates the Debugger 2.0 nested sub-macro Call Stack displayed in the Debugger dock tab.</summary>
    public void UpdateCallStack(IReadOnlyList<CallStackFrame>? frames)
    {
        CallStackFrames.Clear();
        if (frames is not null)
        {
            foreach (var frame in frames)
                CallStackFrames.Add(frame);
        }
        OnPropertyChanged(nameof(HasActiveCallStack));
        OnPropertyChanged(nameof(ActiveCallStackSummary));
        if (frames is { Count: > 0 })
            UpdateLiveVariables(frames[^1].VariablesSnapshot);
    }

    /// <summary>Appends one structured step record into the Debugger 2.0 Trace viewer.</summary>
    public void AppendTraceEntry(ExecutionTraceEntry entry)
    {
        _traceAll.Add(entry);
        while (_traceAll.Count > PlaybackEngine.ExecutionTraceCapacity)
            _traceAll.RemoveAt(0);

        if (MatchesTraceFilter(entry))
        {
            FilteredTraceEntries.Add(entry);
            while (FilteredTraceEntries.Count > PlaybackEngine.ExecutionTraceCapacity)
                FilteredTraceEntries.RemoveAt(0);
        }

        OnPropertyChanged(nameof(HasTraceEntries));
        OnPropertyChanged(nameof(TraceSummary));
    }

    private void ClearTrace()
    {
        _traceAll.Clear();
        FilteredTraceEntries.Clear();
        OnPropertyChanged(nameof(HasTraceEntries));
        OnPropertyChanged(nameof(TraceSummary));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private void RefreshFilteredTrace()
    {
        FilteredTraceEntries.Clear();
        foreach (var entry in _traceAll)
        {
            if (MatchesTraceFilter(entry))
                FilteredTraceEntries.Add(entry);
        }
    }

    private bool MatchesTraceFilter(ExecutionTraceEntry entry)
    {
        switch (_traceOutcomeFilter)
        {
            case TraceOutcomeFilter.Failed when entry.Outcome != ExecutionTraceOutcome.Failed:
                return false;
            case TraceOutcomeFilter.Vision when !entry.VisionScore.HasValue:
                return false;
        }

        if (string.IsNullOrWhiteSpace(_traceFilterText))
            return true;

        var q = _traceFilterText.Trim();
        return entry.MacroName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               entry.StepTitle.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               entry.OutcomeText.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               (entry.Error?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (entry.Detail?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
               entry.StepNumber.ToString(System.Globalization.CultureInfo.InvariantCulture).Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Called when playback finishes: updates final variable readouts, run history telemetry, and opt-in Self-Heal coordinate updates.</summary>
    public void ShowPlaybackResult(Guid? playedMacroId, PlaybackResult? result)
    {
        IsPlaybackPaused = false;
        if (!HasMacro || playedMacroId != _id || result is null) return;

        _runHistoryTotal++;
        if (result.Outcome == PlaybackOutcome.Completed) _runHistorySucceeded++;
        _lastRunSummaryText = result.Stats?.SummaryText ?? result.Outcome switch
        {
            PlaybackOutcome.Completed => $"last run: {result.StepsExecuted} steps",
            PlaybackOutcome.Stopped => $"last run: stopped at {result.StepsExecuted} steps",
            _ => $"last run: failed ({result.Error ?? "error"})",
        };
        OnPropertyChanged(nameof(RunHistoryTotal));
        OnPropertyChanged(nameof(RunHistorySucceeded));
        OnPropertyChanged(nameof(HasRunHistory));
        OnPropertyChanged(nameof(RunHistorySummaryText));

        if (result.FinalVariables is { Count: > 0 } finalVars)
            UpdateLiveVariables(finalVars);

        if (result.Trace is { Count: > 0 } finalTrace)
        {
            _traceAll.Clear();
            _traceAll.AddRange(finalTrace);
            RefreshFilteredTrace();
            OnPropertyChanged(nameof(HasTraceEntries));
            OnPropertyChanged(nameof(TraceSummary));
        }

        if (result.Outcome == PlaybackOutcome.Failed && result.FailureCallStack is { Count: > 0 } failedFrames)
            UpdateCallStack(failedFrames);
        else
            UpdateCallStack(null);

        // v1.8 Evergreen Profiler: populate per-step execution count & average ms badges.
        var statMap = result.StepStats ?? new Dictionary<Guid, StepExecutionStat>();
        foreach (var stepVm in Steps)
        {
            if (statMap.TryGetValue(stepVm.Id, out var stat) && stat.ExecutionCount > 0)
            {
                var avgText = stat.AverageMs >= 1000
                    ? $"{stat.AverageMs / 1000.0:0.##} s"
                    : $"{stat.AverageMs:0.#} ms";
                stepVm.TelemetryBadgeText = $"×{stat.ExecutionCount} · {avgText}";
            }
            else
            {
                stepVm.TelemetryBadgeText = null;
            }
        }

        // v1.8 Evergreen Blackbox Flight Recorder + Debugger 2.0 Trace export.
        FailureSnapshotPath = result.Outcome == PlaybackOutcome.Failed ? result.FailureSnapshotPath : null;
        FailureTracePath = result.Outcome == PlaybackOutcome.Failed ? result.FailureTracePath : null;

        PendingSuggestions.Clear();
        if (result.Suggestions is { Count: > 0 } suggestions)
        {
            foreach (var s in suggestions)
            {
                var stepVm = Steps.FirstOrDefault(vm => vm.Id == s.StepId && vm.Model is MouseClickStep);
                if (stepVm is null) continue;
                PendingSuggestions.Add(new SelfHealItemViewModel(s, stepVm.Number));
            }
        }

        RaiseSelfHealChanged();
    }

    private void ApplySuggestion(object? parameter)
    {
        if (!HasMacro || parameter is not SelfHealItemViewModel item) return;
        var stepIndex = Steps.ToList().FindIndex(s => s.Id == item.Suggestion.StepId);
        if (stepIndex < 0 || Steps[stepIndex].Model is not MouseClickStep click) return;

        RecordUndo();
        item.Suggestion.ApplyTo(click);
        var vm = CreateStepViewModel(click);
        Steps[stepIndex] = vm;
        SelectedStep = vm;
        PendingSuggestions.Remove(item);
        RaiseSelfHealChanged();
        Touch();
        _setStatus($"🔧 Updated step #{stepIndex + 1} coordinates to ({click.X}, {click.Y}). Ctrl+Z undoes it.", StatusKind.Success);
    }

    private void ApplyAllSuggestions()
    {
        if (!HasMacro || PendingSuggestions.Count == 0) return;
        var applied = 0;
        RecordUndo();
        foreach (var item in PendingSuggestions.ToList())
        {
            var stepIndex = Steps.ToList().FindIndex(s => s.Id == item.Suggestion.StepId);
            if (stepIndex < 0 || Steps[stepIndex].Model is not MouseClickStep click) continue;
            item.Suggestion.ApplyTo(click);
            Steps[stepIndex] = CreateStepViewModel(click);
            applied++;
        }

        PendingSuggestions.Clear();
        RaiseSelfHealChanged();
        if (applied > 0)
        {
            Touch();
            _setStatus($"🔧 Applied {applied} self-heal coordinate update{(applied == 1 ? "" : "s")}. Click Save to keep them, or Ctrl+Z to undo.", StatusKind.Success);
        }
    }

    private void RaiseSelfHealChanged()
    {
        OnPropertyChanged(nameof(HasPendingSuggestions));
        OnPropertyChanged(nameof(SelfHealBannerText));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private string? _failureSnapshotPath;

    /// <summary>Path to the PNG captured automatically when a vision/wait step timed out and stopped the macro.</summary>
    public string? FailureSnapshotPath
    {
        get => _failureSnapshotPath;
        set
        {
            if (SetProperty(ref _failureSnapshotPath, value))
            {
                OnPropertyChanged(nameof(HasFailureSnapshot));
                OnPropertyChanged(nameof(HasFailureBanner));
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool HasFailureSnapshot => !string.IsNullOrWhiteSpace(_failureSnapshotPath) && System.IO.File.Exists(_failureSnapshotPath);

    private void OpenFailureSnapshot()
    {
        if (!HasFailureSnapshot || string.IsNullOrWhiteSpace(_failureSnapshotPath)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_failureSnapshotPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _setStatus($"Couldn't open failure snapshot: {ex.Message}", StatusKind.Error);
        }
    }

    private void OpenFailureTrace()
    {
        if (!HasFailureTrace || string.IsNullOrWhiteSpace(_failureTracePath)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_failureTracePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _setStatus($"Couldn't open failure trace JSON: {ex.Message}", StatusKind.Error);
        }
    }

    /// <summary>Refreshes sub-macro dropdown choices and call-graph validation when the library changes.</summary>
    public void RefreshSubMacroLinks()
    {
        if (!HasMacro) return;
        foreach (var w in Watchers) w.RefreshMacroChoices();
        foreach (var step in Steps)
        {
            switch (step)
            {
                case CallMacroStepViewModel call: call.RefreshMacroChoices(); break;
                case ScreenSearchStepViewModel s: s.FailurePolicy.RefreshMacroChoices(); break;
                case WaitForChangeStepViewModel wc: wc.FailurePolicy.RefreshMacroChoices(); break;
                case FocusWindowStepViewModel fw: fw.FailurePolicy.RefreshMacroChoices(); break;
            }
        }

        Revalidate();
    }

    // ---------------- 🧪 Test Step (Ctrl+T) ----------------

    private bool _isTestingStep;
    private string _lastTestResultText = string.Empty;
    private bool _lastTestPassed;

    /// <summary>
    /// Set by the main window: looks at the screen once for a vision step / If / Repeat condition (SmartMacro hides for a
    /// moment), flashes the reticle (green = found, amber = near miss) and returns the result. Null = no screen tools.
    /// </summary>
    public Func<MacroStep, Task<StepTestResult>>? TestVisionStepHandler { get; set; }

    /// <summary>Set by the main window: the result of the latest playback (to report how an executed test step went).</summary>
    public Func<PlaybackResult?>? LastPlaybackResult { get; set; }

    /// <summary>
    /// "🧪 Test Step" (Ctrl+T): a vision step (Find image / colour / text) or an If / Repeat with a screen condition looks at
    /// the screen ONCE, right now, without playing anything: "🧪 Test Passed: Found at (X, Y) · 96% confidence · 0.8ms" or
    /// "🧪 Test Failed: Not found (Closest match on screen: 79% at X, Y — step requires 85%)". Any other step is executed
    /// once (like ▶ Run step): "🧪 Executed step #N: …".
    /// </summary>
    public AsyncRelayCommand TestSelectedStepCommand { get; }

    /// <summary>The result pill of the latest test (empty = no pill).</summary>
    public string LastTestResultText
    {
        get => _lastTestResultText;
        private set
        {
            if (SetProperty(ref _lastTestResultText, value ?? string.Empty)) OnPropertyChanged(nameof(HasLastTestResult));
        }
    }

    /// <summary>Green pill when the test passed, amber when not.</summary>
    public bool LastTestPassed
    {
        get => _lastTestPassed;
        private set => SetProperty(ref _lastTestPassed, value);
    }

    public bool HasLastTestResult => _lastTestResultText.Length > 0;

    public RelayCommand DismissTestResultCommand { get; }

    private async Task TestSelectedStepAsync()
    {
        if (SelectedStep is not { } step) return;
        var index = Steps.IndexOf(step);
        if (index < 0) return;

        _isTestingStep = true;
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        try
        {
            if (StepTester.IsVisionTest(step.Model))
            {
                if (TestVisionStepHandler is null)
                {
                    ShowTestResult("🧪 Screen tests aren't available here.", passed: false);
                    return;
                }

                // Test exactly what is in the editor right now (unsaved edits included), on a copy.
                ShowTestResult($"🧪 Testing step #{index + 1}…", passed: true);
                var result = await TestVisionStepHandler(MacroJson.CloneStep(step.Model));
                ShowTestResult(result.Text, result.Passed);
                return;
            }

            // An action step (click, move, key, delay…): run it once, like ▶ Run step.
            if (RunSingleStepHandler is null) return;
            var macro = MacroJson.Clone(BuildMacro());
            macro.Steps[index].Enabled = true;
            await RunSingleStepHandler(macro, index);
            var outcome = LastPlaybackResult?.Invoke();
            ShowTestResult(outcome?.Outcome switch
            {
                PlaybackOutcome.Stopped => $"🧪 Step #{index + 1} was stopped.",
                PlaybackOutcome.Failed => $"🧪 Step #{index + 1} failed: {outcome!.Error}",
                _ => StepTester.ExecutedText(index + 1, step.Model),
            }, outcome is null or { Outcome: PlaybackOutcome.Completed });
        }
        finally
        {
            _isTestingStep = false;
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    private void ShowTestResult(string text, bool passed)
    {
        LastTestPassed = passed;
        LastTestResultText = text;
        _setStatus(text, passed ? StatusKind.Success : StatusKind.Warning);
    }

    // ---------------- 📚 Blueprints ----------------

    private bool _isBlueprintsMenuOpen;

    /// <summary>The four 1-click blueprints (toolbar menu, Ctrl+K palette, Manual recipe cards).</summary>
    public IReadOnlyList<MacroBlueprintInfo> Blueprints => MacroBlueprints.All;

    /// <summary>The "📚 Blueprints" toolbar popup is open.</summary>
    public bool IsBlueprintsMenuOpen
    {
        get => _isBlueprintsMenuOpen;
        set => SetProperty(ref _isBlueprintsMenuOpen, value);
    }

    public RelayCommand ToggleBlueprintsMenuCommand { get; }

    /// <summary>Inserts a blueprint (parameter: <see cref="MacroBlueprint"/>, its info, or its name) where new steps go.</summary>
    public RelayCommand AddBlueprintCommand { get; }

    private static MacroBlueprint? ToBlueprint(object? parameter) => parameter switch
    {
        MacroBlueprint b => b,
        MacroBlueprintInfo info => info.Blueprint,
        string name when Enum.TryParse<MacroBlueprint>(name, out var parsed) => parsed,
        _ => null,
    };

    /// <summary>
    /// Inserts a blueprint's steps after the selected step (or at the end), never inside a folded block, as one Undo step.
    /// Returns false when no macro is open. Also used by the Manual's "➕ Load Example into Editor".
    /// </summary>
    public bool AddBlueprint(object? parameter)
    {
        IsBlueprintsMenuOpen = false;
        if (!HasMacro || ToBlueprint(parameter) is not { } blueprint) return false;

        var info = MacroBlueprints.Info(blueprint);
        var models = MacroBlueprints.Build(blueprint);
        RecordUndo();
        var selectedIndex = SelectedStep is null ? -1 : Steps.IndexOf(SelectedStep);
        var index = InsertOutsideFolded(StepListEditing.InsertIndex(Steps.Count, selectedIndex, AddAtEnd ? InsertPosition.AtEnd : InsertPosition.AfterSelected));
        if (info.RequiresTopLevel)
            index = StepListEditing.InsertIndexAtTopLevel(Steps.Select(s => s.Model).ToList(), index);

        var added = new List<StepViewModel>();
        foreach (var model in models)
        {
            var vm = CreateStepViewModel(model);
            Steps.Insert(index + added.Count, vm);
            added.Add(vm);
        }

        var seededVars = 0;
        foreach (var def in MacroBlueprints.BuildVariables(blueprint))
        {
            if (Variables.Any(v => string.Equals(v.Name, def.Name, StringComparison.OrdinalIgnoreCase)))
                continue;
            Variables.Add(new VariableItemViewModel(def.Clone(), OnVariablesOrWatchersModified));
            seededVars++;
        }

        if (seededVars > 0)
        {
            OnPropertyChanged(nameof(HasVariables));
            OnPropertyChanged(nameof(VariablesSummary));
        }

        if (info.EnablesHumanize) HumanizePlayback = true;
        _selection = added.Count > 1 ? added : new List<StepViewModel>();
        SelectedStep = added[0];
        if (added.Count > 1) SelectStepsRequested?.Invoke(this, added);
        Touch();
        _setStatus($"📚 Added \"{info.Title}\" as step{(added.Count == 1 ? "" : "s")} {index + 1}{(added.Count == 1 ? "" : $"–{index + added.Count}")}" +
                   (seededVars > 0 ? $" (+{seededVars} variable{(seededVars == 1 ? "" : "s")})" : "") +
                   (info.EnablesHumanize ? " and turned Humanize on" : "") +
                   ". The Problems list shows what is left to set up. Ctrl+Z undoes it.", StatusKind.Success);
        return true;
    }

    // ---------------- AAA Studio UI: Ctrl+K Command Palette ----------------

    private bool _isCommandPaletteOpen;
    private string _paletteQuery = string.Empty;
    private int _selectedPaletteIndex = -1;

    /// <summary>Set by the main window: plays the open macro (same as the ▶ Play button).</summary>
    public Action? PlayHandler { get; set; }

    /// <summary>Set by the main window: the play shortcut shown next to "▶ Play Macro" (it can be customized).</summary>
    public Func<string>? PlayShortcutText { get; set; }

    /// <summary>Ctrl+K: the palette card at the top of the editor is open (its search box has the keyboard focus).</summary>
    public bool IsCommandPaletteOpen
    {
        get => _isCommandPaletteOpen;
        set
        {
            if (!SetProperty(ref _isCommandPaletteOpen, value)) return;
            if (value)
            {
                _paletteQuery = string.Empty;
                OnPropertyChanged(nameof(PaletteQuery));
                RefreshPaletteItems();
            }

            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>The palette's search text: filters <see cref="PaletteItems"/> as you type.</summary>
    public string PaletteQuery
    {
        get => _paletteQuery;
        set
        {
            if (SetProperty(ref _paletteQuery, value ?? string.Empty)) RefreshPaletteItems();
        }
    }

    /// <summary>The commands matching <see cref="PaletteQuery"/> (<see cref="StudioCommandCatalog.Filter(string?)"/>).</summary>
    public ObservableCollection<StudioCommand> PaletteItems { get; } = new();

    /// <summary>The highlighted result (↑ / ↓ move it, Enter runs it).</summary>
    public int SelectedPaletteIndex
    {
        get => _selectedPaletteIndex;
        set
        {
            var clamped = PaletteItems.Count == 0 ? -1 : Math.Clamp(value, 0, PaletteItems.Count - 1);
            if (SetProperty(ref _selectedPaletteIndex, clamped)) OnPropertyChanged(nameof(SelectedPaletteItem));
        }
    }

    public StudioCommand? SelectedPaletteItem =>
        _selectedPaletteIndex >= 0 && _selectedPaletteIndex < PaletteItems.Count ? PaletteItems[_selectedPaletteIndex] : null;

    /// <summary>"No commands match" while the filter leaves nothing.</summary>
    public bool HasNoPaletteResults => PaletteItems.Count == 0;

    public RelayCommand ToggleCommandPaletteCommand { get; }
    public RelayCommand CloseCommandPaletteCommand { get; }

    /// <summary>Runs a palette command (parameter = the <see cref="StudioCommand"/>, else the highlighted one) and closes the palette.</summary>
    public RelayCommand ExecutePaletteItemCommand { get; }

    /// <summary>↑ / ↓ in the palette (wraps around).</summary>
    public void MovePaletteSelection(int delta)
    {
        if (PaletteItems.Count == 0) return;
        var next = _selectedPaletteIndex < 0 ? 0 : ((_selectedPaletteIndex + delta) % PaletteItems.Count + PaletteItems.Count) % PaletteItems.Count;
        SelectedPaletteIndex = next;
    }

    private void RefreshPaletteItems()
    {
        var shortcut = PlayShortcutText?.Invoke() ?? string.Empty;
        PaletteItems.Clear();
        foreach (var command in StudioCommandCatalog.Filter(_paletteQuery))
            PaletteItems.Add(command.Kind == StudioCommandKind.Play && shortcut.Length > 0 ? command with { Shortcut = shortcut } : command);
        _selectedPaletteIndex = -1;
        SelectedPaletteIndex = 0;
        OnPropertyChanged(nameof(SelectedPaletteIndex));
        OnPropertyChanged(nameof(SelectedPaletteItem));
        OnPropertyChanged(nameof(HasNoPaletteResults));
    }

    private void ExecutePaletteItem(StudioCommand? command)
    {
        if (command is null) return;
        IsCommandPaletteOpen = false;
        if (!HasMacro)
        {
            _setStatus("Open a macro first.", StatusKind.Warning);
            return;
        }

        switch (command.Kind)
        {
            case StudioCommandKind.AddStep when command.Step is { } kind:
                Run(AddStepCommand, kind, "Can't add a step right now.");
                break;
            case StudioCommandKind.Play:
                if (PlayHandler is null || !(CanRunSteps?.Invoke() ?? false))
                    _setStatus("Can't play right now (something is already recording or playing).", StatusKind.Warning);
                else
                    PlayHandler();
                break;
            case StudioCommandKind.PlayFromSelected:
                Run(PlayFromHereCommand, null, "Select a step first (and make sure nothing is playing), then Play from here.");
                break;
            case StudioCommandKind.AutoFixBlocks:
                Run(AutoFixBlocksCommand, null, "The If / Repeat blocks are fine: nothing to fix.");
                break;
            case StudioCommandKind.CleanUp:
                Run(CleanUpMousePathsCommand, null, "Nothing to clean up: there are no Move mouse steps.");
                break;
            case StudioCommandKind.MergeKeysToText:
                Run(MergeKeysToTextCommand, null, "Nothing to merge: select or record at least 2 KeyPress steps.");
                break;
            case StudioCommandKind.ToggleCompactView:
                IsCompactView = !IsCompactView;
                _setStatus(IsCompactView ? "📐 Compact step view on." : "📐 Compact step view off.", StatusKind.Info);
                break;
            case StudioCommandKind.ToggleHumanize:
                HumanizePlayback = !HumanizePlayback;
                _setStatus(HumanizePlayback ? "🎲 Humanize on: small random click offsets and timing." : "🎲 Humanize off: exact positions and timing.", StatusKind.Info);
                break;
            case StudioCommandKind.TestStep:
                Run(TestSelectedStepCommand, null, "Select a step first (and make sure nothing is playing), then 🧪 Test Step.");
                break;
            case StudioCommandKind.AddBlueprint when command.Blueprint is { } blueprint:
                AddBlueprint(blueprint);
                break;
            case StudioCommandKind.SetSpeed when command.Speed > 0:
                Speed = command.Speed;
                _setStatus($"⚡ Playback speed {command.Speed.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}×.", StatusKind.Info);
                break;
            case StudioCommandKind.CopyCliCommand:
                Run(CopyCliCommand, null, "Open a macro first to copy its CLI run command.");
                break;
            case StudioCommandKind.StepOver:
                Run(StepOverCommand, null, "Open a macro and pause playback first to Step Over (F10).");
                break;
            case StudioCommandKind.StepInto:
                Run(StepIntoCommand, null, "Open a macro and pause playback first to Step Into (F11).");
                break;
            case StudioCommandKind.StepOut:
                Run(StepOutCommand, null, "Open a macro and pause inside a sub-macro first to Step Out (Shift+F11).");
                break;
            case StudioCommandKind.WrapInGroup:
                Run(WrapInGroupCommand, null, "Select one or more steps first to wrap them in a Group (Ctrl+Shift+G).");
                break;
            case StudioCommandKind.ExtractGroupToSubMacro:
                Run(ExtractGroupToSubMacroCommand, null, "Select a Group section (or step inside one) first to extract it to a sub-macro.");
                break;
        }

        void Run(System.Windows.Input.ICommand target, object? parameter, string unavailable)
        {
            if (target.CanExecute(parameter)) target.Execute(parameter);
            else _setStatus(unavailable, StatusKind.Warning);
        }
    }


    // ---------------- ▶ Run step / ⏭ Play from here ----------------

    /// <summary>Set by the main window: plays one step (or its block) of a macro copy.</summary>
    public Func<Macro, int, Task>? RunSingleStepHandler { get; set; }

    /// <summary>Set by the main window: plays a macro copy from a step to the end.</summary>
    public Func<Macro, int, Task>? PlayFromStepHandler { get; set; }

    /// <summary>Set by the main window: true while nothing is recording / playing.</summary>
    public Func<bool>? CanRunSteps { get; set; }

    private bool CanRunNow => HasMacro && (CanRunSteps?.Invoke() ?? false);

    /// <summary>"▶ Run step": tests only the selected step (an If / Repeat runs its whole block) once.</summary>
    public AsyncRelayCommand RunStepCommand { get; }

    /// <summary>"▶ From here" (Shift+F5): plays from the selected step to the end, once.</summary>
    public AsyncRelayCommand PlayFromHereCommand { get; }

    /// <summary>Same command as <see cref="PlayFromHereCommand"/> (the name used by the AAA upgrade: Shift+F5).</summary>
    public AsyncRelayCommand PlayFromSelectedCommand => PlayFromHereCommand;

    /// <summary>
    /// What "▶ From here" would play for the selected step: [Start, End) of the step list, from the first step of the
    /// block-safe slice (<see cref="PlaybackRanges.BuildPlayFromHereSlice"/>) to the end. Null when nothing is selected.
    /// </summary>
    public (int Start, int End)? PlayFromSelectedRange
    {
        get
        {
            var index = SelectedStep is null ? -1 : Steps.IndexOf(SelectedStep);
            if (index < 0) return null;
            return PlaybackRanges.PlayFromHere(Steps.Select(s => s.Model).ToList(), index);
        }
    }

    private Task RunSelectedStepAsync()
    {
        if (SelectedStep is not { } step || RunSingleStepHandler is null) return Task.CompletedTask;
        var index = Steps.IndexOf(step);
        var macro = MacroJson.Clone(BuildMacro());
        // Testing a step that is turned off: run it anyway (only in the copy that plays; the macro is unchanged).
        macro.Steps[index].Enabled = true;
        return RunSingleStepHandler(macro, index);
    }

    /// <summary>
    /// The steps "▶ From here" would actually play for the selected step (a valid, self-contained slice: Else parts and
    /// End markers of blocks that started above it are left out). Null when nothing is selected.
    /// </summary>
    public PlayFromHereSlice? PlayFromSelectedSlice
    {
        get
        {
            var index = SelectedStep is null ? -1 : Steps.IndexOf(SelectedStep);
            return index < 0 ? null : PlaybackRanges.BuildPlayFromHereSlice(Steps.Select(s => s.Model).ToList(), index);
        }
    }

    private Task PlayFromSelectedAsync()
    {
        if (SelectedStep is not { } step || PlayFromStepHandler is null) return Task.CompletedTask;
        var index = Steps.IndexOf(step);
        if (index < 0) return Task.CompletedTask;

        // The automation layer (and the engine) cut the copy down with PlaybackRanges.BuildPlayFromHereSlice, so a start
        // inside any nested If / Repeat always plays a valid workflow. Say up front when markers are left out.
        var slice = PlaybackRanges.BuildPlayFromHereSlice(Steps.Select(s => s.Model).ToList(), index);
        if (slice.DroppedMarkers > 0)
            _setStatus($"Playing from step {index + 1}: the rest of the blocks around it plays once" +
                       (slice.DroppedSteps > 0 ? $", the \"otherwise\" part of the If above is skipped ({slice.DroppedSteps} step(s))." : "."),
                StatusKind.Info);
        return PlayFromStepHandler(MacroJson.Clone(BuildMacro()), index);
    }

    // ---------------- Power-user QoL: Space toggles on / off ----------------

    /// <summary>
    /// Space: turns the selected steps on / off (all to the opposite of the focused step). A selected If / Repeat (or its
    /// Else / End) switches its whole block, inner steps included (<see cref="StepListEditing.ToggleEnabledTargets"/>).
    /// </summary>
    public RelayCommand ToggleEnabledCommand { get; }

    private void ToggleEnabledSelected()
    {
        if (SelectedStep is not { } focused) return;
        var models = Steps.Select(s => s.Model).ToList();
        var selected = SelectedInOrder().Select(s => Steps.IndexOf(s)).Where(i => i >= 0).ToList();
        if (selected.Count == 0) selected.Add(Steps.IndexOf(focused));
        // A folded block counts as selected whole.
        var targets = StepListEditing.ToggleEnabledTargets(models, selected);
        if (targets.Count == 0) return;

        var turnOn = !focused.Enabled;
        RecordUndo();
        _suspendTouch = true;
        try
        {
            foreach (var index in targets) Steps[index].Enabled = turnOn;
        }
        finally
        {
            _suspendTouch = false;
        }

        Touch();
        var what = targets.Count == 1 ? "the step" : $"{targets.Count} steps";
        var block = targets.Count > selected.Count ? " (the whole block)" : "";
        _setStatus($"Turned {(turnOn ? "on" : "off")} {what}{block}. Space switches it back.", StatusKind.Info);
    }

    // ---------------- Studio UI: folding If / Repeat blocks, compact view ----------------

    /// <summary>▼ / ▶ on an If / Repeat row (parameter = that row; the selected step when none): folds / unfolds its block.</summary>
    public RelayCommand ToggleCollapseCommand { get; }

    /// <summary>Folds every If / Repeat block.</summary>
    public RelayCommand CollapseAllCommand { get; }

    /// <summary>Unfolds every block.</summary>
    public RelayCommand ExpandAllCommand { get; }

    /// <summary>Dense single-line rows (about twice as many steps on screen).</summary>
    public bool IsCompactView
    {
        get => _isCompactView;
        set => SetProperty(ref _isCompactView, value);
    }

    /// <summary>IDs of the folded If / Repeat steps (view state only).</summary>
    public IReadOnlyCollection<Guid> CollapsedStepIds => _collapsed;

    /// <summary>How many steps are folded away right now.</summary>
    public int HiddenByCollapseCount => Steps.Count(s => s.IsHiddenByCollapse);

    private void ToggleCollapse(StepViewModel? step)
    {
        if (step is not { CanCollapse: true }) return;
        if (!_collapsed.Remove(step.Id)) _collapsed.Add(step.Id);
        RefreshListView();

        // Never leave the selection on a step that just disappeared: select the folded If / Repeat instead.
        if (SelectedStep is { IsHiddenByCollapse: true })
        {
            _selection.Clear();
            SelectedStep = step;
        }
    }

    private void SetAllCollapsed(bool collapsed)
    {
        _collapsed.Clear();
        if (collapsed)
        {
            foreach (var step in Steps.Where(s => s.CanCollapse)) _collapsed.Add(step.Id);
        }

        RefreshListView();
        if (SelectedStep is { IsHiddenByCollapse: true } hidden)
        {
            var models = Steps.Select(s => s.Model).ToList();
            var outer = StepListView.EnclosingBlocks(models, Steps.IndexOf(hidden));
            _selection.Clear();
            SelectedStep = outer.Count > 0 ? Steps[outer[0]] : null;
        }
    }

    /// <summary>Unfolds every block that hides the step (so it can be shown and selected).</summary>
    private void Reveal(StepViewModel step)
    {
        var index = Steps.IndexOf(step);
        if (index < 0) return;
        var changed = false;
        foreach (var outer in StepListView.EnclosingBlocks(Steps.Select(s => s.Model).ToList(), index))
            changed |= _collapsed.Remove(Steps[outer].Id);
        if (changed) RefreshListView();
    }

    // ---------------- Studio UI: search box + filter pills (Ctrl+F) ----------------

    /// <summary>The search box text: matching steps are highlighted, the others dimmed; "↑ ↓" jumps between them.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? string.Empty)) return;
            _currentMatch = -1;
            RefreshListView();
            OnPropertyChanged(nameof(IsSearchActive));
        }
    }

    /// <summary>The active filter pill (steps outside it are hidden; indexes and indentation stay the same).</summary>
    public StepFilter ActiveFilter
    {
        get => _filter;
        set
        {
            if (!SetProperty(ref _filter, value)) return;
            foreach (var pill in FilterPills) pill.IsActive = pill.Value == value;
            _currentMatch = -1;
            RefreshListView();
            OnPropertyChanged(nameof(IsSearchActive));
        }
    }

    /// <summary>All / Vision · If / Mouse / Keyboard / Disabled.</summary>
    public IReadOnlyList<StepFilterPill> FilterPills { get; }

    public RelayCommand SetFilterCommand { get; }
    public RelayCommand NextMatchCommand { get; }
    public RelayCommand PreviousMatchCommand { get; }
    public RelayCommand ClearSearchCommand { get; }

    /// <summary>True while a search text or a filter other than "All" is active.</summary>
    public bool IsSearchActive => !string.IsNullOrWhiteSpace(_searchText) || _filter != StepFilter.All;

    /// <summary>How many steps match the search / filter.</summary>
    public int SearchMatchCount => _matches.Count;

    /// <summary>"3 matches · 2 / 3 · ↑ ↓" (empty while no search is active).</summary>
    public string MatchNavigatorText
    {
        get
        {
            if (!IsSearchActive) return string.Empty;
            var count = StepListView.MatchCountText(_matches.Count);
            var position = StepListView.MatchPosition(_currentMatch, _matches.Count);
            return position.Length == 0 ? count : $"{count} · {position}";
        }
    }

    /// <summary>Raised when a step should be scrolled into view (search navigation); the view scrolls the list to it.</summary>
    public event EventHandler<StepViewModel>? RevealStepRequested;

    private void GoToMatch(int direction)
    {
        if (_matches.Count == 0) return;
        if (_currentMatch < 0)
        {
            // Start from the selected step: the next match after it (or the one before it for ↑).
            var selected = SelectedStep is null ? -1 : Steps.IndexOf(SelectedStep);
            var next = direction > 0 ? 0 : _matches.Count - 1; // wrap around when there is none after / before it
            if (direction > 0)
            {
                for (var k = 0; k < _matches.Count; k++)
                {
                    if (_matches[k] > selected) { next = k; break; }
                }
            }
            else
            {
                for (var k = _matches.Count - 1; k >= 0; k--)
                {
                    if (_matches[k] < selected) { next = k; break; }
                }
            }

            _currentMatch = next;
        }
        else
        {
            _currentMatch = ((_currentMatch + direction) % _matches.Count + _matches.Count) % _matches.Count;
        }

        var step = Steps[_matches[_currentMatch]];
        Reveal(step);
        _selection.Clear();
        SelectedStep = step;
        MarkCurrentMatch();
        OnPropertyChanged(nameof(MatchNavigatorText));
        RevealStepRequested?.Invoke(this, step);
    }

    private void ClearSearch()
    {
        SearchText = string.Empty;
        ActiveFilter = StepFilter.All;
    }

    private void ResetViewState()
    {
        _collapsed.Clear();
        // Preserve _breakpoints across macro switches so users can arm breakpoints inside sub-macros
        // before returning to the caller macro to run.
        _breakpointRuleError = null;
        _selectedBreakpointExpression = string.Empty;
        _runHistoryTotal = 0;
        _runHistorySucceeded = 0;
        _lastRunSummaryText = string.Empty;
        _searchText = string.Empty;
        _filter = StepFilter.All;
        foreach (var pill in FilterPills) pill.IsActive = pill.Value == StepFilter.All;
        _matches = Array.Empty<int>();
        _currentMatch = -1;
    }

    /// <summary>
    /// Recomputes what the list shows: folded blocks (<see cref="StepListView.Collapse"/>), the filter pill and the search
    /// highlight. Only view flags on the step view models change; the steps, their order and indexes never do.
    /// </summary>
    private void RefreshListView()
    {
        var models = Steps.Select(s => s.Model).ToList();
        _collapsed.RemoveWhere(id => !models.Any(m => m.Id == id && StepListView.CanCollapse(m)));
        var staleBreakpoints = _breakpoints.Keys
            .Where(id => _breakpointMeta.TryGetValue(id, out var meta) && meta.MacroId == MacroId && !models.Any(m => m.Id == id))
            .ToList();
        foreach (var id in staleBreakpoints)
        {
            _breakpoints.Remove(id);
            _breakpointMeta.Remove(id);
        }
        var fold = StepListView.Collapse(models, _collapsed);
        var searching = !string.IsNullOrWhiteSpace(_searchText);
        _matches = StepListView.FindMatches(models, _searchText, _filter);
        var matchSet = new HashSet<int>(_matches);

        for (var i = 0; i < Steps.Count; i++)
        {
            var vm = Steps[i];
            vm.IsCollapsed = _collapsed.Contains(vm.Id);
            vm.HasBreakpoint = _breakpoints.TryGetValue(vm.Id, out var bpRule);
            vm.BreakpointCondition = vm.HasBreakpoint ? bpRule?.Expression : null;
            if (vm.HasBreakpoint)
                _breakpointMeta[vm.Id] = (MacroId ?? Guid.Empty, Name, vm.Number, vm.Title);
            vm.HiddenStepCount = vm.IsCollapsed ? fold.HiddenCount[i] : 0;
            vm.IsHiddenByCollapse = fold.Hidden[i];
            vm.IsFilteredOut = _filter != StepFilter.All && !StepListView.MatchesFilter(vm.Model, _filter);
            vm.IsSearchMatch = searching && matchSet.Contains(i);
            vm.IsSearchDimmed = searching && !matchSet.Contains(i);
        }

        RebuildActiveBreakpoints();
        SyncSelectedBreakpointEditor();
        if (_currentMatch >= _matches.Count) _currentMatch = -1;
        MarkCurrentMatch();
        OnPropertyChanged(nameof(SearchMatchCount));
        OnPropertyChanged(nameof(MatchNavigatorText));
        OnPropertyChanged(nameof(HiddenByCollapseCount));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private void MarkCurrentMatch()
    {
        var current = _currentMatch >= 0 && _currentMatch < _matches.Count ? _matches[_currentMatch] : -1;
        for (var i = 0; i < Steps.Count; i++) Steps[i].IsCurrentSearchMatch = i == current;
    }

}

/// <summary>One filter pill above the step list ("All", "Vision / If", "Mouse", "Keyboard", "Disabled").</summary>
public sealed class StepFilterPill : ObservableObject
{
    private bool _isActive;

    public StepFilterPill(StepFilter value, string label, string tooltip)
    {
        Value = value;
        Label = label;
        Tooltip = tooltip;
    }

    public StepFilter Value { get; }
    public string Label { get; }
    public string Tooltip { get; }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }
}

/// <summary>Filter selector for the Debugger 2.0 Trace dock tab.</summary>
public enum TraceOutcomeFilter
{
    All = 0,
    Failed = 1,
    Vision = 2,
}
