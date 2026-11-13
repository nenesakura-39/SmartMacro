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

/// <summary>
/// The workflow editor. It edits a private copy of the macro, so nothing changes
/// in the library or on disk until the user clicks Save.
/// </summary>
public sealed partial class EditorViewModel : ObservableObject
{
    private readonly IMacroRepository _repository;
    private readonly ILogService _log;
    private readonly IDialogService _dialogs;
    private readonly IScreenInfoProvider _screen;
    private readonly Func<int> _defaultDelayMs;
    private readonly Action<Macro> _onSaved;
    private readonly Action<string, StatusKind> _setStatus;
    private readonly ScreenTools _screenTools;

    private Macro? _saved;          // last saved version (copy)
    private Guid _id;
    private DateTimeOffset _createdAt;
    private string _name = string.Empty;
    private string _description = string.Empty;
    private int _repeatCount = 1;
    private double _speed = 1.0;
    private int _repeatDelayMs;
    private StepViewModel? _selectedStep;
    private bool _isDirty;
    private bool _isSaving;
    private int _errorCount;
    private int _warningCount;
    private ScreenBounds? _screenBounds;
    private List<StepViewModel> _selection = new(); // all selected steps (the list allows Ctrl / Shift selection)
    private bool _addAtEnd;

    // Undo / Redo of structural edits (snapshots of the step list taken before each edit).
    private readonly UndoHistory _history = new();

    // Studio UI view state (never saved, never played): folded If / Repeat blocks (by step ID, so it survives Undo /
    // Redo, moves and reloads of the list), the search box, the filter pill and the compact list mode.
    private readonly HashSet<Guid> _collapsed = new();
    private string _searchText = string.Empty;
    private StepFilter _filter = StepFilter.All;
    private IReadOnlyList<int> _matches = Array.Empty<int>();
    private int _currentMatch = -1;
    private bool _isCompactView;
    private bool _suspendTouch;
    private bool _suspendStepsChanged;
    private int _preferredInspectorTab;

    // Macro settings: target window + "auto-adjust coordinates", Humanize, Profile.
    private string? _targetWindowTitle;
    private string? _profileName;
    private bool _scaleCoordinates;
    private int _referenceLeft;
    private int _referenceTop;
    private int _referenceWidth;
    private int _referenceHeight;
    private bool _humanize;
    private int? _clickJitterPx;
    private int? _delayJitterPercent;

    public EditorViewModel(
        IMacroRepository repository,
        ILogService log,
        IDialogService dialogs,
        IScreenInfoProvider screen,
        Func<int> defaultDelayMs,
        Action<Macro> onSaved,
        Action<string, StatusKind> setStatus,
        Action goToLibrary,
        IFindTextTools? findTextTools = null,
        IVisionTools? visionTools = null)
    {
        _screenTools = new ScreenTools(
            findTextTools,
            visionTools,
            screen as Core.Vision.IScreenLayout,
            MacroListProvider: () => GetLibraryMacros?.Invoke() ?? Array.Empty<Macro>(),
            CurrentMacroIdProvider: () => MacroId);
        _repository = repository;
        _log = log;
        _dialogs = dialogs;
        _screen = screen;
        _defaultDelayMs = defaultDelayMs;
        _onSaved = onSaved;
        _setStatus = setStatus;

        Steps.CollectionChanged += OnStepsChanged;

        AddStepCommand = new RelayCommand(p => AddStep(p, AddAtEnd ? InsertPosition.AtEnd : InsertPosition.AfterSelected), p => HasMacro && p is StepKind);
        DeleteStepCommand = new RelayCommand(DeleteSelectedSteps, () => SelectedStep is not null);
        DuplicateStepCommand = new RelayCommand(DuplicateSelectedStep, () => SelectedStep is not null);
        MoveUpCommand = new RelayCommand(() => MoveSelected(MoveDirection.Up), () => CanMove(up: true));
        MoveDownCommand = new RelayCommand(() => MoveSelected(MoveDirection.Down), () => CanMove(up: false));
        MoveToTopCommand = new RelayCommand(() => MoveSelected(MoveDirection.ToTop), () => CanMove(up: true));
        MoveToBottomCommand = new RelayCommand(() => MoveSelected(MoveDirection.ToBottom), () => CanMove(up: false));
        SelectPreviousStepCommand = new RelayCommand(() => SelectAdjacentStep(-1), () => GetAdjacentVisibleStep(-1) is not null);
        SelectNextStepCommand = new RelayCommand(() => SelectAdjacentStep(+1), () => GetAdjacentVisibleStep(+1) is not null);
        FocusFirstErrorCommand = new RelayCommand(FocusFirstErrorStepAndOpenProblems, () => HasMacro && HasErrors);
        AddToEndCommand = new RelayCommand(p => AddStep(p, InsertPosition.AtEnd, forcePosition: true), p => HasMacro && p is StepKind);
        InsertAfterSelectedCommand = new RelayCommand(p =>
        {
            foreach (var s in Steps) s.IsInsertMenuOpen = false;
            AddStep(p, InsertPosition.AfterSelected, forcePosition: true);
        }, p => HasMacro && p is StepKind);
        InsertQuickAfterRowCommand = new RelayCommand(InsertQuickAfterRow, p => HasMacro && p is StepViewModel);
        OpenInsertMenuAfterRowCommand = new RelayCommand(p =>
        {
            if (!HasMacro || p is not StepViewModel row) return;
            foreach (var s in Steps) s.IsInsertMenuOpen = false;
            SelectedStep = row;
            row.IsInsertMenuOpen = true;
        }, p => HasMacro && p is StepViewModel);
        SaveCommand = new AsyncRelayCommand(async () => await SaveAsync(), HandleUnexpectedError, () => HasMacro && !_isSaving);
        RevertCommand = new RelayCommand(Revert, () => HasMacro && IsDirty);
        GoToLibraryCommand = new RelayCommand(goToLibrary);
        WrapInIfCommand = new RelayCommand(() => WrapSelection(StepKind.If), () => HasMacro && SelectedStep is not null);
        WrapInRepeatCommand = new RelayCommand(() => WrapSelection(StepKind.Repeat), () => HasMacro && SelectedStep is not null);
        CleanUpMousePathsCommand = new RelayCommand(CleanUpMousePaths, () => HasMacro && Steps.Any(s => s.Model is MouseMoveStep));
        MergeKeysToTextCommand = new RelayCommand(MergeKeystrokesToText, () => HasMacro && Steps.Count(s => s.Model is KeyPressStep) >= 2);
        AddPatternCommand = new RelayCommand(p => AddPattern(p, AddAtEnd ? InsertPosition.AtEnd : InsertPosition.AfterSelected), p => HasMacro && p is QuickPattern);

        UndoCommand = new RelayCommand(Undo, () => HasMacro && _history.CanUndo);
        RedoCommand = new RelayCommand(Redo, () => HasMacro && _history.CanRedo);
        CopySelectedStepCommand = new RelayCommand(CopySelectedSteps, () => HasMacro && SelectedStep is not null);
        PasteStepCommand = new RelayCommand(PasteSteps, () => HasMacro);
        RunStepCommand = new AsyncRelayCommand(RunSelectedStepAsync, HandleUnexpectedError, () => CanRunNow && SelectedStep is not null);
        PlayFromHereCommand = new AsyncRelayCommand(PlayFromSelectedAsync, HandleUnexpectedError, () => CanRunNow && SelectedStep is not null);
        ToggleEnabledCommand = new RelayCommand(ToggleEnabledSelected, () => HasMacro && SelectedStep is not null);
        ToggleCollapseCommand = new RelayCommand(p => ToggleCollapse(p as StepViewModel ?? SelectedStep), p => HasMacro && (p as StepViewModel ?? SelectedStep) is { CanCollapse: true });
        CollapseAllCommand = new RelayCommand(() => SetAllCollapsed(true), () => HasMacro && Steps.Any(s => s.CanCollapse));
        ExpandAllCommand = new RelayCommand(() => SetAllCollapsed(false), () => HasMacro && _collapsed.Count > 0);
        NextMatchCommand = new RelayCommand(() => GoToMatch(+1), () => _matches.Count > 0);
        PreviousMatchCommand = new RelayCommand(() => GoToMatch(-1), () => _matches.Count > 0);
        ClearSearchCommand = new RelayCommand(ClearSearch, () => IsSearchActive);
        SetFilterCommand = new RelayCommand(p => { if (p is StepFilterPill pill) ActiveFilter = pill.Value; else if (p is StepFilter f) ActiveFilter = f; });
        FilterPills = new[]
        {
            new StepFilterPill(StepFilter.All, "All", "Show every step"),
            new StepFilterPill(StepFilter.VisionAndLogic, "Vision / If", "Find text / image / color, Wait for screen change, feedback loops, If and Repeat blocks"),
            new StepFilterPill(StepFilter.Mouse, "Mouse", "Move mouse, Mouse click, Scroll wheel and recorded input"),
            new StepFilterPill(StepFilter.Keyboard, "Keyboard", "Key press, Type text and recorded input"),
            new StepFilterPill(StepFilter.Disabled, "Disabled", "Steps that are turned off"),
        };
        FilterPills[0].IsActive = true;
        UseCurrentSizeCommand = new RelayCommand(CaptureReference, () => HasMacro);
        RefreshTargetWindowsCommand = new RelayCommand(RefreshTargetWindows);
        ConvertClickToFindImageCommand = new RelayCommand(ConvertClickToFindImage,
            p => HasMacro && (p as MouseClickStepViewModel ?? SelectedStep as MouseClickStepViewModel) is { CanConvertToFindImage: true });
        AutoFixBlocksCommand = new RelayCommand(AutoFixBlocks, () => HasMacro && CanAutoFixBlocks);
        BreakIntoStepsCommand = new RelayCommand(BreakIntoSteps,
            p => HasMacro && (p as InputSequenceStepViewModel ?? SelectedStep as InputSequenceStepViewModel) is { CanBreakIntoSteps: true });

        if (visionTools is not null) TestVisionStepHandler = visionTools.TestStepAsync;
        TestSelectedStepCommand = new AsyncRelayCommand(TestSelectedStepAsync, HandleUnexpectedError,
            () => HasMacro && SelectedStep is not null && !_isTestingStep && (CanRunSteps?.Invoke() ?? false));
        DismissTestResultCommand = new RelayCommand(() => LastTestResultText = string.Empty, () => HasLastTestResult);
        AddBlueprintCommand = new RelayCommand(p => AddBlueprint(p), p => HasMacro && ToBlueprint(p) is not null);
        ToggleBlueprintsMenuCommand = new RelayCommand(() => IsBlueprintsMenuOpen = !IsBlueprintsMenuOpen, () => HasMacro);

        ToggleCommandPaletteCommand = new RelayCommand(() => IsCommandPaletteOpen = !IsCommandPaletteOpen, () => HasMacro || IsCommandPaletteOpen);
        CloseCommandPaletteCommand = new RelayCommand(() => IsCommandPaletteOpen = false);
        ExecutePaletteItemCommand = new RelayCommand(p => ExecutePaletteItem(p as StudioCommand ?? SelectedPaletteItem));

        AddVariableCommand = new RelayCommand(AddVariable, () => HasMacro);
        RemoveVariableCommand = new RelayCommand(p => { if (p is VariableItemViewModel v && Variables.Remove(v)) OnVariablesOrWatchersModified(); }, _ => HasMacro);
        AddWatcherCommand = new RelayCommand(AddWatcher, () => HasMacro);
        RemoveWatcherCommand = new RelayCommand(p => { if (p is WatcherItemViewModel w && Watchers.Remove(w)) OnVariablesOrWatchersModified(); }, _ => HasMacro);
        AddTriggerCommand = new RelayCommand(AddTrigger, () => HasMacro);
        RemoveTriggerCommand = new RelayCommand(p => { if (p is TriggerItemViewModel t && Triggers.Remove(t)) OnVariablesOrWatchersModified(); }, _ => HasMacro);
        ApplySuggestionCommand = new RelayCommand(ApplySuggestion, p => HasMacro && p is SelfHealItemViewModel);
        ApplyAllSuggestionsCommand = new RelayCommand(ApplyAllSuggestions, () => HasMacro && PendingSuggestions.Count > 0);
        DismissSuggestionsCommand = new RelayCommand(() => { PendingSuggestions.Clear(); RaiseSelfHealChanged(); }, () => PendingSuggestions.Count > 0);
        OpenFailureSnapshotCommand = new RelayCommand(OpenFailureSnapshot, () => HasFailureSnapshot);
        DismissFailureSnapshotCommand = new RelayCommand(() => FailureSnapshotPath = null, () => HasFailureSnapshot);

        ClearToolboxFilterCommand = new RelayCommand(() => ToolboxFilter = string.Empty, () => HasToolboxFilter);
        OpenSubMacroCommand = new RelayCommand(p =>
        {
            if (p is CallMacroStepViewModel call && call.Model is CallMacroStep step)
                _ = OpenSubMacroHandler?.Invoke(step.TargetMacroId, call.TargetMacroName);
        }, p => p is CallMacroStepViewModel);
        RunGivenStepCommand = new RelayCommand(p =>
        {
            if (p is StepViewModel s)
            {
                SelectedStep = s;
                if (RunStepCommand.CanExecute(null)) RunStepCommand.Execute(null);
            }
        }, _ => HasMacro && (CanRunSteps?.Invoke() ?? false));
        TestGivenStepCommand = new RelayCommand(p =>
        {
            if (p is StepViewModel s)
            {
                SelectedStep = s;
                if (TestSelectedStepCommand.CanExecute(null)) TestSelectedStepCommand.Execute(null);
            }
        }, _ => HasMacro && !_isTestingStep && (CanRunSteps?.Invoke() ?? false));
        DeleteGivenStepCommand = new RelayCommand(p =>
        {
            if (p is StepViewModel s)
            {
                _selection = new List<StepViewModel> { s };
                SelectedStep = s;
                if (DeleteStepCommand.CanExecute(null)) DeleteStepCommand.Execute(null);
            }
        }, _ => HasMacro);
        OpenBottomTabCommand = new RelayCommand(p =>
        {
            if (p is string tabStr && int.TryParse(tabStr, out var idx))
            {
                if (IsBottomDockExpanded && SelectedBottomTab == idx)
                    IsBottomDockExpanded = false;
                else
                {
                    SelectedBottomTab = idx;
                    IsBottomDockExpanded = true;
                }
            }
        });
        ToggleBreakpointCommand = new RelayCommand(p => ToggleBreakpoint(p as StepViewModel ?? SelectedStep), p => HasMacro && (p as StepViewModel ?? SelectedStep) is not null);
        StepOverCommand = new RelayCommand(ExecuteStepOver, () => HasMacro);
        CopyCliCommand = new RelayCommand(CopyCliRunCommand, () => HasMacro);

        RefreshPaletteItems();
    }


    // ---------------- Macro settings: target window, auto-adjust coordinates, Humanize ----------------

    /// <summary>Open app windows for the "Target window" list (first entry = any / main screen).</summary>
    public ObservableCollection<string> TargetWindowChoices { get; } = new();

    public RelayCommand RefreshTargetWindowsCommand { get; }

    /// <summary>"Use current size": remembers where the target window / main screen is now as the reference.</summary>
    public RelayCommand UseCurrentSizeCommand { get; }

    /// <summary>Optional app / game profile tag (e.g. "Excel", "Work", "Elden Ring"). Empty = global.</summary>
    public string ProfileName
    {
        get => _profileName ?? string.Empty;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (_profileName == normalized) return;
            _profileName = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TargetSummary));
            Touch();
        }
    }

    /// <summary>The window the macro is made for (part of its title); empty = any / main screen.</summary>
    public string TargetWindowTitle
    {
        get => _targetWindowTitle ?? AnyWindowLabel;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) || value == AnyWindowLabel ? null : value.Trim();
            if (_targetWindowTitle == normalized) return;
            _targetWindowTitle = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TargetSummary));
            Touch();
        }
    }

    /// <summary>The first entry of the target list.</summary>
    public const string AnyWindowLabel = "Any / Main screen";

    /// <summary>"Auto-adjust coordinates if window moves or screen resolution changes".</summary>
    public bool ScaleCoordinatesToWindow
    {
        get => _scaleCoordinates;
        set
        {
            if (!SetProperty(ref _scaleCoordinates, value)) return;
            // Turning it on without a reference: use where the target is right now.
            if (value && (_referenceWidth <= 0 || _referenceHeight <= 0)) CaptureReference();
            OnPropertyChanged(nameof(TargetSummary));
            Touch();
        }
    }

    public string ReferenceText => _referenceWidth > 0 && _referenceHeight > 0
        ? $"Positions were picked on {_referenceWidth}×{_referenceHeight} at ({_referenceLeft}, {_referenceTop})."
        : "Reference size: not set yet.";

    /// <summary>Short text for the header button, e.g. "Roblox · auto-adjust".</summary>
    public string TargetSummary =>
        (string.IsNullOrEmpty(_targetWindowTitle) ? "Any screen" : _targetWindowTitle) + (_scaleCoordinates ? " · auto-adjust" : "");

    /// <summary>"Humanize": small random click offsets and timing variation.</summary>
    public bool HumanizePlayback
    {
        get => _humanize;
        set
        {
            if (SetProperty(ref _humanize, value)) Touch();
        }
    }

    /// <summary>Humanize: largest random click offset (px).</summary>
    public int ClickJitterPx
    {
        get => _clickJitterPx ?? Macro.DefaultClickJitterPx;
        set
        {
            if (_clickJitterPx == value || (_clickJitterPx is null && value == Macro.DefaultClickJitterPx)) return;
            _clickJitterPx = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HumanizeTooltip));
            Touch();
        }
    }

    /// <summary>Humanize: largest random change of delays / glide times (%).</summary>
    public int DelayJitterPercent
    {
        get => _delayJitterPercent ?? Macro.DefaultDelayJitterPercent;
        set
        {
            if (_delayJitterPercent == value || (_delayJitterPercent is null && value == Macro.DefaultDelayJitterPercent)) return;
            _delayJitterPercent = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HumanizeTooltip));
            Touch();
        }
    }

    public string HumanizeTooltip =>
        $"🎲 Humanize: adds a subtle ±{ClickJitterPx} px random offset around click spots (kept inside what a Find step found), ±{DelayJitterPercent} % natural variation to delays of {PlaybackEngine.HumanizeMinDelayMs} ms or more and to glide times, and a slight natural arc to Smooth glides, so repeated game loops don't click the exact same pixel with robotic timing. Recorded input still plays exactly as recorded.";

    private void RefreshTargetWindows() => RefreshTargetWindowsCore(enumerateOpenWindows: true);

    private void RefreshTargetWindowsCore(bool enumerateOpenWindows)
    {
        var current = _targetWindowTitle;
        TargetWindowChoices.Clear();
        TargetWindowChoices.Add(AnyWindowLabel);
        if (enumerateOpenWindows)
        {
            try
            {
                if (_screen is Core.Vision.IScreenLayout layout)
                    foreach (var title in layout.GetWindows().Select(w => w.Title).Distinct(StringComparer.OrdinalIgnoreCase))
                        TargetWindowChoices.Add(title);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                _log.Warning("Could not list the open windows.", ex);
            }
        }

        if (!string.IsNullOrEmpty(current) && !TargetWindowChoices.Contains(current)) TargetWindowChoices.Add(current);
        OnPropertyChanged(nameof(TargetWindowTitle));
    }

    /// <summary>Stores where the target window (or the main screen) is right now as the reference rectangle.</summary>
    private void CaptureReference()
    {
        if (!HasMacro) return;
        try
        {
            var probe = new Macro { TargetWindowTitle = _targetWindowTitle };
            var bounds = CoordinateScaling.CurrentTarget(probe, _screen as Core.Vision.IScreenLayout);
            (_referenceLeft, _referenceTop, _referenceWidth, _referenceHeight) = (bounds.X, bounds.Y, bounds.Width, bounds.Height);
            OnPropertyChanged(nameof(ReferenceText));
            Touch();
            _setStatus($"Reference set: {bounds.Width}×{bounds.Height} at ({bounds.X}, {bounds.Y}). Pick positions with the window where it is now; playback adjusts them if it moves or changes size.", StatusKind.Info);
        }
        catch (Core.Vision.SearchAreaUnavailableException ex)
        {
            _setStatus(ex.Message, StatusKind.Warning);
        }
    }

    // ---------------- Quick patterns (1-click starter templates) ----------------

    /// <summary>The "Quick patterns" at the bottom of the Add step panel.</summary>
    public IReadOnlyList<QuickPatternInfo> QuickPatternTypes => QuickPatterns.All;

    /// <summary>Inserts the steps of the <see cref="QuickPattern"/> given as parameter (where new steps go).</summary>
    public RelayCommand AddPatternCommand { get; }

    private void AddPattern(object? parameter, InsertPosition position)
    {
        if (!HasMacro || parameter is not QuickPattern pattern) return;

        var models = QuickPatterns.Build(pattern);
        RecordUndo();
        var selectedIndex = SelectedStep is null ? -1 : Steps.IndexOf(SelectedStep);
        var index = InsertOutsideFolded(StepListEditing.InsertIndex(Steps.Count, selectedIndex, position));
        var added = new List<StepViewModel>();
        foreach (var model in models)
        {
            var vm = CreateStepViewModel(model);
            Steps.Insert(index + added.Count, vm);
            added.Add(vm);
        }

        _selection.Clear();
        SelectedStep = added[0];
        Touch();
        var info = QuickPatterns.Info(pattern);
        _setStatus($"Added \"{info.Title}\" as steps {index + 1}–{index + added.Count}. Next: capture the images / pick the spots named in the step comments (the Problems list shows what's left).", StatusKind.Success);
    }

    // ---------------- Live step highlight while playing ----------------

    private StepViewModel? _runningStep;
    private StepViewModel? _progressStep;

    /// <summary>Raised when playback moves to another step of this macro (the view scrolls it into view).</summary>
    public event EventHandler<StepViewModel>? RunningStepChanged;

    /// <summary>
    /// Marks the step playback is running right now (by step ID, so it works on the copy that plays); null clears it.
    /// Steps of another macro (e.g. one played from the library) simply don't match. The automation layer already
    /// coalesces step changes to ~30 FPS, so every call is applied (the old extra 50 ms drop here could leave the
    /// highlight on a step that had already finished, because the same ID is never sent twice).
    /// </summary>
    public void SetRunningStep(Guid? stepId)
    {
        var step = stepId is { } id ? Steps.FirstOrDefault(s => s.Id == id) : null;
        if (ReferenceEquals(step, _runningStep)) return;
        if (_runningStep is not null) _runningStep.IsRunning = false;
        _runningStep = step;
        if (step is null)
        {
            SetRunningStepProgress(null, 0, false);
            return;
        }

        step.IsRunning = true;
        RunningStepChanged?.Invoke(this, step);
    }

    /// <summary>
    /// The live 2 px progress bar on the playing step's card (~30 FPS from the automation layer): <paramref name="progress"/>
    /// 0..1 of step <paramref name="stepId"/>; <paramref name="show"/> false (or a null ID) hides it.
    /// </summary>
    public void SetRunningStepProgress(Guid? stepId, double progress, bool show)
    {
        StepViewModel? step = null;
        if (stepId is { } id && show)
            step = _progressStep?.Id == id ? _progressStep : _runningStep?.Id == id ? _runningStep : Steps.FirstOrDefault(s => s.Id == id);

        if (!ReferenceEquals(step, _progressStep) && _progressStep is not null)
        {
            _progressStep.ShowExecutionProgress = false;
            _progressStep.ExecutionProgress = 0;
        }

        _progressStep = step;
        if (step is null) return;
        step.ExecutionProgress = progress;
        step.ShowExecutionProgress = true;
    }

    public ObservableCollection<StepViewModel> Steps { get; } = new();

    public ObservableCollection<ValidationIssue> Issues { get; } = new();

    public IReadOnlyList<StepTypeInfo> StepTypes => StepTypeCatalog.Addable;

    /// <summary>If / Else / End If / Repeat / End Repeat, shown in the toolbox's "Logic" section.</summary>
    public IReadOnlyList<StepTypeInfo> LogicStepTypes => StepTypeCatalog.Logic;

    /// <summary>Set variable / Checkpoint / Call macro, shown in the toolbox's "Memory &amp; Sub-Macros" section.</summary>
    public IReadOnlyList<StepTypeInfo> BrainStepTypes => StepTypeCatalog.Brain;

    public RelayCommand AddStepCommand { get; }
    public RelayCommand DeleteStepCommand { get; }
    public RelayCommand DuplicateStepCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public RelayCommand MoveToTopCommand { get; }
    public RelayCommand MoveToBottomCommand { get; }
    public RelayCommand SelectPreviousStepCommand { get; }
    public RelayCommand SelectNextStepCommand { get; }
    public RelayCommand FocusFirstErrorCommand { get; }

    /// <summary>Adds the step kind given as parameter after the last step (whatever the toolbox setting is).</summary>
    public RelayCommand AddToEndCommand { get; }

    /// <summary>Adds the step kind given as parameter right after the selected step (or at the end if none).</summary>
    public RelayCommand InsertAfterSelectedCommand { get; }

    /// <summary>Inserts a default step right after the hovered row when the user clicks the between-step '+' rail.</summary>
    public RelayCommand InsertQuickAfterRowCommand { get; }

    /// <summary>Opens the categorized quick-insert step popover right below the hovered row.</summary>
    public RelayCommand OpenInsertMenuAfterRowCommand { get; }

    private void InsertQuickAfterRow(object? parameter)
    {
        if (!HasMacro || parameter is not StepViewModel row) return;
        SelectedStep = row;
        var kind = row.Model is DelayStep ? StepKind.MouseClick : StepKind.Delay;
        AddStep(kind, InsertPosition.AfterSelected, forcePosition: true);
    }

    /// <summary>
    /// Raised after a move with the steps that should be selected afterwards (the view selects them in the list,
    /// so a multi-step selection survives the move).
    /// </summary>
    public event EventHandler<IReadOnlyList<StepViewModel>>? SelectStepsRequested;

    /// <summary>
    /// Toolbox setting: true = new steps go to the end of the workflow, false = right after the selected step
    /// (also the end when nothing is selected).
    /// </summary>
    public bool AddAtEnd
    {
        get => _addAtEnd;
        set
        {
            if (SetProperty(ref _addAtEnd, value)) OnPropertyChanged(nameof(AddAfterSelected));
        }
    }

    /// <summary>The other radio button of <see cref="AddAtEnd"/>.</summary>
    public bool AddAfterSelected
    {
        get => !_addAtEnd;
        set => AddAtEnd = !value;
    }
    public AsyncRelayCommand SaveCommand { get; }
    public RelayCommand RevertCommand { get; }
    public RelayCommand GoToLibraryCommand { get; }
    public RelayCommand WrapInIfCommand { get; }
    public RelayCommand WrapInRepeatCommand { get; }

    /// <summary>"Clean up mouse paths": recorded Delay + Move runs become at most Delay + Move + Delay (see StepCleanup).</summary>
    public RelayCommand CleanUpMousePathsCommand { get; }

    /// <summary>"⌨ Merge keys → Type Text": recorded printable KeyPress runs become TypeTextStep (see StepCleanup).</summary>
    public RelayCommand MergeKeysToTextCommand { get; }

    public bool HasMacro => _saved is not null;

    public Guid? MacroId => HasMacro ? _id : null;

    public string Name
    {
        get => _name;
        set
        {
            value ??= string.Empty;
            if (SetProperty(ref _name, value)) Touch();
        }
    }

    public string Description
    {
        get => _description;
        set
        {
            value ??= string.Empty;
            if (SetProperty(ref _description, value)) Touch();
        }
    }

    /// <summary>How many times Play runs the steps (0 = until stopped).</summary>
    public int RepeatCount
    {
        get => _repeatCount;
        set
        {
            if (SetProperty(ref _repeatCount, value)) Touch();
        }
    }

    /// <summary>Playback speed for delays (1 = recorded speed).</summary>
    public double Speed
    {
        get => _speed;
        set
        {
            if (!SetProperty(ref _speed, value)) return;
            RefreshSpeedChoices();
            Touch();
        }
    }

    /// <summary>The speed selector: 0.5× / 1× / 1.5× / 2× / 5× (plus the macro's own speed if it is something else).</summary>
    public ObservableCollection<Choice<double>> SpeedChoices { get; } = new();

    /// <param name="rebuild">
    /// True when a macro is loaded: start from the presets again. Otherwise the list only grows (an extra entry for an unusual
    /// speed is added, nothing is removed), so the selector is never cleared while the user is picking from it.
    /// </param>
    private void RefreshSpeedChoices(bool rebuild = false)
    {
        var known = rebuild ? Macro.SpeedPresets.ToList() : SpeedChoices.Select(c => c.Value).Union(Macro.SpeedPresets).ToList();
        if (!known.Any(v => Math.Abs(v - _speed) < 1e-9) && !double.IsNaN(_speed) && _speed > 0) known.Add(_speed);
        known.Sort();
        if (SpeedChoices.Select(c => c.Value).SequenceEqual(known)) return;

        if (rebuild) SpeedChoices.Clear();
        for (var i = 0; i < known.Count; i++)
        {
            if (i < SpeedChoices.Count && SpeedChoices[i].Value.Equals(known[i])) continue;
            SpeedChoices.Insert(i, new Choice<double>(known[i], $"{known[i]:0.##}×"));
        }

        OnPropertyChanged(nameof(Speed)); // re-select after the list changed
    }

    /// <summary>Wait between repeats in ms.</summary>
    public int RepeatDelayMs
    {
        get => _repeatDelayMs;
        set
        {
            if (SetProperty(ref _repeatDelayMs, value)) Touch();
        }
    }

    public StepViewModel? SelectedStep
    {
        get => _selectedStep;
        set
        {
            var previous = _selectedStep;
            if (SetProperty(ref _selectedStep, value))
            {
                if (previous is not null)
                {
                    previous.PropertyChanged -= OnSelectedStepPropertyChanged;
                    previous.IsQuickEditOpen = false;
                    previous.IsInsertMenuOpen = false;
                }
                if (previous is KeyPressStepViewModel { IsRecordingKey: true } prevKey)
                    prevKey.IsRecordingKey = false;
                if (value is not null)
                {
                    if (value.HasInspectorTabs)
                        value.ApplyPreferredInspectorTab(_preferredInspectorTab);
                    value.PropertyChanged += OnSelectedStepPropertyChanged;
                    if (value.Model is IfStep or ElseStep or RepeatStep || value.IndentLevel > 0)
                        AddAtEnd = false;
                }
                UpdateBlockPartnerHighlights();
                OnPropertyChanged(nameof(HasSelection));
            }
        }
    }

    private void OnSelectedStepPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StepViewModel.InspectorTab) && sender is StepViewModel step)
            _preferredInspectorTab = step.InspectorTab;
    }

    private StepViewModel? GetAdjacentVisibleStep(int delta)
    {
        if (!HasMacro || Steps.Count == 0 || SelectedStep is null) return null;
        var index = Steps.IndexOf(SelectedStep);
        if (index < 0) return null;
        for (var i = index + delta; i >= 0 && i < Steps.Count; i += delta)
        {
            if (Steps[i].IsVisibleInList) return Steps[i];
        }
        return null;
    }

    private void SelectAdjacentStep(int delta)
    {
        if (GetAdjacentVisibleStep(delta) is not { } target) return;
        _selection = new List<StepViewModel> { target };
        SelectedStep = target;
        OnPropertyChanged(nameof(HasMultiSelection));
        OnPropertyChanged(nameof(MultiSelectCountText));
        SelectStepsRequested?.Invoke(this, _selection);
        RevealStepRequested?.Invoke(this, target);
    }

    public bool HasSelection => _selectedStep is not null;

    public bool HasMultiSelection => _selection.Count > 1;

    public string MultiSelectCountText => $"{_selection.Count} steps selected";

    /// <summary>Called by the view when the list's selection changes (several steps can be selected).</summary>
    public void SetSelection(IEnumerable<StepViewModel> selected)
    {
        _selection = selected.Where(s => Steps.Contains(s)).ToList();
        OnPropertyChanged(nameof(HasMultiSelection));
        OnPropertyChanged(nameof(MultiSelectCountText));
    }

    /// <summary>The selected steps in list order (falls back to the single selected step).</summary>
    private List<StepViewModel> SelectedInOrder()
    {
        var selected = _selection.Count > 0 ? _selection : (SelectedStep is { } one ? new List<StepViewModel> { one } : new List<StepViewModel>());
        return selected.Where(s => Steps.Contains(s)).Distinct().OrderBy(s => Steps.IndexOf(s)).ToList();
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set => SetProperty(ref _isDirty, value);
    }

    public bool HasSteps => Steps.Count > 0;

    public string StepCountText => Steps.Count == 1 ? "1 step" : $"{Steps.Count:N0} steps";

    public int ErrorCount
    {
        get => _errorCount;
        private set
        {
            if (SetProperty(ref _errorCount, value))
            {
                OnPropertyChanged(nameof(HasErrors));
                OnPropertyChanged(nameof(ErrorBadgeText));
                OnPropertyChanged(nameof(ValidationSummary));
            }
        }
    }

    public string ErrorBadgeText => ErrorCount == 1 ? "1 error · Fix to save" : $"{ErrorCount} errors · Fix to save";

    public int WarningCount
    {
        get => _warningCount;
        private set
        {
            if (SetProperty(ref _warningCount, value))
                OnPropertyChanged(nameof(ValidationSummary));
        }
    }

    public bool HasErrors => ErrorCount > 0;

    public bool HasIssues => Issues.Count > 0;

    public string ValidationSummary =>
        ErrorCount == 0 && WarningCount == 0
            ? "No problems found"
            : $"{ErrorCount} error{(ErrorCount == 1 ? "" : "s")}, {WarningCount} warning{(WarningCount == 1 ? "" : "s")}";

    public string ScreenBoundsText => _screenBounds is null ? "" : $"Your screen area: {_screenBounds}";

    /// <summary>Opens a macro for editing (works on a copy).</summary>
    public void Load(Macro macro)
    {
        var copy = MacroJson.Clone(macro);
        _saved = MacroJson.Clone(macro);
        _screenBounds = TryGetScreen();

        _id = copy.Id;
        _createdAt = copy.CreatedAt;
        _name = copy.Name ?? string.Empty;
        _description = copy.Description ?? string.Empty;
        _repeatCount = copy.RepeatCount;
        _speed = copy.Speed;
        RefreshSpeedChoices(rebuild: true);
        _repeatDelayMs = copy.RepeatDelayMs;
        _profileName = string.IsNullOrWhiteSpace(copy.ProfileName) ? null : copy.ProfileName.Trim();
        _targetWindowTitle = string.IsNullOrWhiteSpace(copy.TargetWindowTitle) ? null : copy.TargetWindowTitle.Trim();
        _scaleCoordinates = copy.ScaleCoordinatesToWindow;
        (_referenceLeft, _referenceTop, _referenceWidth, _referenceHeight) = (copy.ReferenceLeft, copy.ReferenceTop, copy.ReferenceWidth, copy.ReferenceHeight);
        _humanize = copy.HumanizePlayback;
        _clickJitterPx = copy.ClickJitterPx;
        _delayJitterPercent = copy.DelayJitterPercent;
        _history.Clear();
        _preferredInspectorTab = 0;
        RaiseHistoryChanged();
        RefreshTargetWindowsCore(enumerateOpenWindows: false);
        ResetViewState();

        Variables.Clear();
        if (copy.Variables is not null)
        {
            foreach (var v in copy.Variables)
                Variables.Add(new VariableItemViewModel(v, OnVariablesOrWatchersModified));
        }

        Watchers.Clear();
        if (copy.Watchers is not null)
        {
            foreach (var w in copy.Watchers)
                Watchers.Add(new WatcherItemViewModel(w, _screenTools, () => GetLibraryMacros?.Invoke() ?? Array.Empty<Macro>(), _id, OnVariablesOrWatchersModified));
        }

        Triggers.Clear();
        if (copy.Triggers is not null)
        {
            foreach (var t in copy.Triggers)
                Triggers.Add(new TriggerItemViewModel(t, OnVariablesOrWatchersModified));
        }

        PendingSuggestions.Clear();
        RaiseSelfHealChanged();
        FailureSnapshotPath = null;

        ReplaceSteps(copy.Steps);

        IsDirty = false;
        OnPropertyChanged(string.Empty); // refresh every binding
        Revalidate();
    }

    /// <summary>Closes the editor without asking (caller must have checked IsDirty).</summary>
    public void Unload()
    {
        _saved = null;
        _name = string.Empty;
        _description = string.Empty;
        _profileName = null;
        _history.Clear();
        _preferredInspectorTab = 0;
        RaiseHistoryChanged();
        ResetViewState();
        Variables.Clear();
        Watchers.Clear();
        Triggers.Clear();
        PendingSuggestions.Clear();
        RaiseSelfHealChanged();
        FailureSnapshotPath = null;
        ReplaceSteps(Array.Empty<MacroStep>());
        Issues.Clear();
        ErrorCount = 0;
        WarningCount = 0;
        CanAutoFixBlocks = false;
        BlockProblemText = "";
        IsDirty = false;
        OnPropertyChanged(string.Empty);
    }

    /// <summary>
    /// If there are unsaved changes, asks Save / Discard / Cancel.
    /// Returns true when it's OK to continue (saved, discarded, or nothing to save).
    /// </summary>
    public async Task<bool> ConfirmLeaveAsync()
    {
        if (!HasMacro || !IsDirty) return true;

        switch (_dialogs.AskSaveChanges(string.IsNullOrWhiteSpace(Name) ? "This macro" : Name.Trim()))
        {
            case SaveChoice.Save:
                return await SaveAsync();
            case SaveChoice.Discard:
                Revert();
                return true;
            default:
                return false;
        }
    }

    /// <summary>The macro in the library was deleted: close it here too.</summary>
    public void OnMacroDeleted(Guid id)
    {
        if (HasMacro && _id == id) Unload();
    }

    /// <summary>Syncs the macro name if it was renamed from the Library while open in the Editor.</summary>
    public void SyncExternalRename(Guid id, string newName)
    {
        if (!HasMacro || _id != id || string.IsNullOrWhiteSpace(newName)) return;
        var trimmed = newName.Trim();
        if (_saved is not null) _saved.Name = trimmed;
        if (!IsDirty)
        {
            _name = trimmed;
            OnPropertyChanged(nameof(Name));
        }
    }

    /// <summary>Builds a macro from the editor's current state (steps are the live objects, not copies).</summary>
    public Macro BuildMacro() => new()
    {
        Id = _id,
        Name = Name.Trim(),
        Description = string.IsNullOrWhiteSpace(Description) ? null : Description,
        IsPinned = GetLibraryMacros?.Invoke().FirstOrDefault(m => m.Id == _id)?.IsPinned ?? (_saved?.IsPinned ?? false),
        CreatedAt = _createdAt,
        ModifiedAt = _saved?.ModifiedAt ?? default,
        Steps = Steps.Select(s => s.Model).ToList(),
        RepeatCount = RepeatCount,
        Speed = Speed,
        RepeatDelayMs = RepeatDelayMs,
        ProfileName = _profileName,
        TargetWindowTitle = _targetWindowTitle,
        ScaleCoordinatesToWindow = _scaleCoordinates,
        ReferenceLeft = _referenceLeft,
        ReferenceTop = _referenceTop,
        ReferenceWidth = _referenceWidth,
        ReferenceHeight = _referenceHeight,
        HumanizePlayback = _humanize,
        ClickJitterPx = _clickJitterPx,
        DelayJitterPercent = _delayJitterPercent,
        Variables = Variables.Count == 0 ? null : Variables.Select(v => v.Model).ToList(),
        Watchers = Watchers.Count == 0 ? null : Watchers.Select(w => w.Model).ToList(),
        Triggers = Triggers.Count == 0 ? null : Triggers.Select(t => t.Model).ToList(),
    };

    /// <summary>Opens the Problems dock and selects/scrolls to the first step with a validation error.</summary>
    public void FocusFirstErrorStepAndOpenProblems()
    {
        SelectedBottomTab = 1;
        IsBottomDockExpanded = true;

        var errorStep = Steps.FirstOrDefault(s => s.HasError);
        if (errorStep is not null)
        {
            Reveal(errorStep);
            _selection = new List<StepViewModel> { errorStep };
            SelectedStep = errorStep;
            errorStep.InspectorTab = 0;
            OnPropertyChanged(nameof(HasMultiSelection));
            OnPropertyChanged(nameof(MultiSelectCountText));
            SelectStepsRequested?.Invoke(this, _selection);
            RevealStepRequested?.Invoke(this, errorStep);
        }
    }

    /// <summary>
    /// Raised when a new recording finishes loading so the view can focus and select the default "Recording …" name
    /// for immediate 1-keypress renaming.
    /// </summary>
    public event EventHandler? FocusMacroNameRequested;

    /// <summary>Requests the view to focus and select the Macro Name box (e.g. right after recording or pressing F2).</summary>
    public void RequestFocusMacroName() => FocusMacroNameRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Commits a header field edit (Macro Name, Description, Repeat Count, Profile) when the user presses Enter:
    /// if the macro has unsaved changes and no validation errors, saves immediately to disk and updates the Library.
    /// </summary>
    public async Task<bool> CommitHeaderEditAsync()
    {
        if (!HasMacro) return false;
        if (string.IsNullOrWhiteSpace(Name) && _saved is not null && !string.IsNullOrWhiteSpace(_saved.Name))
        {
            // Never allow an empty Enter press to wipe out the macro's name: restore the last saved name cleanly.
            Name = _saved.Name;
        }

        if (!IsDirty) return true;
        Revalidate();
        if (HasErrors) return false;
        return await SaveAsync();
    }

    public async Task<bool> SaveAsync()
    {
        if (!HasMacro || _isSaving) return false;

        Revalidate();
        if (HasErrors)
        {
            FocusFirstErrorStepAndOpenProblems();
            var errorLines = Issues
                .Where(i => i.Severity == IssueSeverity.Error)
                .Take(5)
                .Select(i => $"• {i.Message}")
                .ToList();
            if (ErrorCount > errorLines.Count)
                errorLines.Add($"• …and {ErrorCount - errorLines.Count} more error(s).");

            var macroTitle = string.IsNullOrWhiteSpace(Name) ? "This macro" : Name.Trim();
            _setStatus($"Can't save yet: fix {ErrorCount} error{(ErrorCount == 1 ? "" : "s")} first (see the Problems list).", StatusKind.Error);
            _dialogs.ShowWarning(
                "Fix errors before saving",
                $"\"{macroTitle}\" has {ErrorCount} error{(ErrorCount == 1 ? "" : "s")} that must be fixed before it can be saved:\n\n" +
                string.Join("\n", errorLines) +
                "\n\nWe've highlighted the problem step and opened the Problems panel below so you can fix or delete it (or choose \"Don't save\" when closing to discard unsaved changes).");
            return false;
        }

        _isSaving = true;
        try
        {
            // Save a copy so the editor's live objects are never shared with the library.
            var toSave = MacroJson.Clone(BuildMacro());
            await _repository.SaveAsync(toSave);

            _saved = MacroJson.Clone(toSave);
            _createdAt = toSave.CreatedAt;
            _name = toSave.Name;
            OnPropertyChanged(nameof(Name));
            IsDirty = false;
            _onSaved(MacroJson.Clone(toSave));
            _setStatus($"Saved \"{toSave.Name}\".", StatusKind.Success);
            return true;
        }
        catch (MacroValidationException ex)
        {
            _setStatus(ex.Message, StatusKind.Error);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error($"Saving \"{Name}\" failed.", ex);
            _dialogs.ShowError("Save failed",
                $"The macro could not be saved.\n\n{ex.Message}\n\nYour changes are still in the editor. Try again, or check that the SmartMacro data folder is writable.");
            _setStatus("Save failed. Your changes are still in the editor.", StatusKind.Error);
            return false;
        }
        finally
        {
            _isSaving = false;
        }
    }

    private void Revert()
    {
        if (_saved is null) return;
        var name = _saved.Name;
        Load(_saved);
        _setStatus($"Changes to \"{name}\" were discarded.", StatusKind.Info);
    }


    private StepViewModel CreateStepViewModel(MacroStep step)
    {
        var vm = StepViewModel.Create(step);
        _screenTools.ApplyTo(vm);

        // "Pick offset from screen" measures from the Find step / condition above the step (looked up when clicked,
        // so moving the step around keeps it right).
        switch (vm)
        {
            case MouseClickStepViewModel click:
                click.ReferenceFinder = () => ReferenceFor(click);
                break;
            case MouseMoveStepViewModel move:
                move.ReferenceFinder = () => ReferenceFor(move);
                break;
        }

        return vm;
    }

    private void ReplaceSteps(IEnumerable<MacroStep> steps)
    {
        _suspendStepsChanged = true;
        try
        {
            foreach (var old in Steps) old.Modified -= OnStepModified;
            Steps.Clear();
            _selection.Clear();
            OnPropertyChanged(nameof(HasMultiSelection));
            OnPropertyChanged(nameof(MultiSelectCountText));
            var index = 0;
            foreach (var step in steps)
            {
                var vm = CreateStepViewModel(step);
                vm.Number = ++index;
                vm.Modified += OnStepModified;
                Steps.Add(vm);
            }
        }
        finally
        {
            _suspendStepsChanged = false;
        }

        var blocks = BlockStructure.Analyze(Steps.Select(s => s.Model).ToList());
        UpdateBlockPairing(blocks);
        RefreshListView();
        OnPropertyChanged(nameof(HasSteps));
        OnPropertyChanged(nameof(StepCountText));
        SelectedStep = Steps.FirstOrDefault();
    }

    private void OnStepsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_suspendStepsChanged) return;

        if (e.OldItems is not null)
            foreach (StepViewModel old in e.OldItems) old.Modified -= OnStepModified;
        if (e.NewItems is not null)
            foreach (StepViewModel added in e.NewItems) added.Modified += OnStepModified;

        for (var i = 0; i < Steps.Count; i++) Steps[i].Number = i + 1;

        var blocks = BlockStructure.Analyze(Steps.Select(s => s.Model).ToList());
        UpdateBlockPairing(blocks);
        RefreshListView();

        OnPropertyChanged(nameof(HasSteps));
        OnPropertyChanged(nameof(StepCountText)); // also re-checks Move up/down availability
    }

    private void OnStepModified(object? sender, EventArgs e)
    {
        if (!_suspendTouch) Touch();
    }

    private void Touch()
    {
        if (!HasMacro) return;
        IsDirty = true;
        Revalidate();
    }

    private void Revalidate()
    {
        if (!HasMacro) return;

        var built = BuildMacro();
        var issues = MacroValidator.Validate(built, _screenBounds);
        if (GetLibraryMacros?.Invoke() is { } libraryMacros)
        {
            var graphIssues = MacroValidator.ValidateCallGraph(built, libraryMacros);
            if (graphIssues.Count > 0)
                issues = issues.Concat(graphIssues).ToList();
        }

        Issues.Clear();
        foreach (var issue in issues.OrderByDescending(i => i.Severity).ThenBy(i => i.StepIndex ?? -1))
            Issues.Add(issue);

        var byStep = issues.Where(i => i.StepId is not null).ToLookup(i => i.StepId!.Value);
        foreach (var step in Steps) step.SetIssues(byStep[step.Id]);

        // Indent the steps inside If / Repeat blocks (also when the blocks don't match up yet).
        var blocks = BlockStructure.Analyze(Steps.Select(s => s.Model).ToList());
        UpdateBlockPairing(blocks);
        UpdateBlockFixBanner(blocks);
        RefreshListView(); // an edit can change what the search matches

        ErrorCount = issues.Count(i => i.Severity == IssueSeverity.Error);
        WarningCount = issues.Count(i => i.Severity == IssueSeverity.Warning);
        OnPropertyChanged(nameof(HasIssues));
    }

    private void UpdateBlockPairing(BlockStructure blocks)
    {
        // Guide rails: amber inside an If / Else, violet inside a Repeat (the innermost block a row is in).
        var rails = StepListView.EnclosingBlockKinds(Steps.Select(s => s.Model).ToList());
        for (var i = 0; i < Steps.Count; i++)
        {
            var depth = blocks.Depth[i];
            Steps[i].IndentLevel = depth;
            Steps[i].BlockRailKind = depth > 0 ? rails[i] : BlockRailKind.None;
            Steps[i].SetBlockInfo(null, 0, Math.Max(0, depth - 1), null, null);
        }

        var ifCounter = 0;
        var repeatCounter = 0;
        var groupCounter = 0;

        for (var i = 0; i < Steps.Count; i++)
        {
            var depth = blocks.Depth[i];
            switch (Steps[i].Model)
            {
                case IfStep:
                {
                    ifCounter++;
                    groupCounter++;
                    var groupKey = groupCounter;
                    Steps[i].SetBlockInfo(ifCounter, groupKey, depth, $"#{ifCounter}", $"If #{ifCounter}");
                    var elseIdx = blocks.ElseOf(i);
                    if (elseIdx >= 0 && elseIdx < Steps.Count)
                        Steps[elseIdx].SetBlockInfo(ifCounter, groupKey, depth, $"#{ifCounter}", $"Else #{ifCounter}");
                    var endIdx = blocks.EndOf(i);
                    if (endIdx >= 0 && endIdx < Steps.Count)
                        Steps[endIdx].SetBlockInfo(ifCounter, groupKey, depth, $"#{ifCounter}", $"End If #{ifCounter}");
                    break;
                }
                case RepeatStep:
                {
                    repeatCounter++;
                    groupCounter++;
                    var groupKey = groupCounter;
                    Steps[i].SetBlockInfo(repeatCounter, groupKey, depth, $"#{repeatCounter}", $"Repeat #{repeatCounter}");
                    var endIdx = blocks.EndOf(i);
                    if (endIdx >= 0 && endIdx < Steps.Count)
                        Steps[endIdx].SetBlockInfo(repeatCounter, groupKey, depth, $"#{repeatCounter}", $"End Repeat #{repeatCounter}");
                    break;
                }
            }
        }

        UpdateBlockPartnerHighlights();
        UpdateTimelineOffsets();
    }

    private void UpdateTimelineOffsets()
    {
        var cumulativeMs = 0.0;
        var speed = _speed > 0 && double.IsFinite(_speed) ? _speed : 1.0;

        for (var i = 0; i < Steps.Count; i++)
        {
            var vm = Steps[i];
            var step = vm.Model;
            vm.TimelineOffsetText = FormatTimelineOffset(cumulativeMs);

            if (!step.Enabled) continue;
            var stepMs = step switch
            {
                DelayStep d => d.DurationMs / speed,
                MouseClickStep { Action: MouseClickAction.Drag } c => c.DragDurationMs / speed,
                MouseClickStep { HoldMs: > 0 } c => c.HoldMs / speed,
                MouseMoveStep { UseRelativeDelta: false, MoveStyle: MouseMoveStyle.SmoothGlide or MouseMoveStyle.HumanCurve } m => m.MoveDurationMs / speed,
                KeyPressStep { Action: KeyAction.Press, HoldMs: > 0 } k => k.HoldMs / speed,
                InputSequenceStep q => (q.Events.Count == 0 ? 0.0 : Math.Max(0L, q.Events[^1].TimeUs) / 1000.0) / speed,
                TypeTextStep t => (t.Text?.Length ?? 0) * Math.Max(8, t.CharacterDelayMs) / speed,
                _ => 0.0,
            };
            if (stepMs > 0 && double.IsFinite(stepMs))
                cumulativeMs += stepMs;
        }
    }

    private static string FormatTimelineOffset(double ms)
    {
        if (ms < 1000) return $"+{ms / 1000.0:0.00}s";
        if (ms < 60_000) return $"+{ms / 1000.0:0.0}s";
        var mins = (int)(ms / 60_000);
        var secs = (ms % 60_000) / 1000.0;
        return $"+{mins}m{secs:00}s";
    }

    private void UpdateBlockPartnerHighlights()
    {
        var key = _selectedStep?.BlockGroupKey ?? 0;
        var hasKey = key > 0;
        foreach (var step in Steps)
        {
            step.IsBlockPartnerHighlighted = hasKey
                && !ReferenceEquals(step, _selectedStep)
                && step.BlockGroupKey == key;
        }
    }

    private ScreenBounds? TryGetScreen()
    {
        try
        {
            var bounds = _screen.GetVirtualScreen();
            return bounds.Width > 0 && bounds.Height > 0 ? bounds : null;
        }
        catch (Exception ex)
        {
            _log.Warning("Could not read the screen size; position checks are skipped.", ex);
            return null;
        }
    }

    private void HandleUnexpectedError(Exception ex)
    {
        _log.Error("Unexpected error in the editor.", ex);
        _dialogs.ShowError("Unexpected error", ex.Message);
    }
}
