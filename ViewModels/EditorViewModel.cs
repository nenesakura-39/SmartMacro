using System.Collections.ObjectModel;
using System.Collections.Specialized;
using SmartMacro.Core.Logging;
using SmartMacro.Core.Models;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Storage;
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
public sealed class EditorViewModel : ObservableObject
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

    // Macro settings: target window + "auto-adjust coordinates", Humanize.
    private string? _targetWindowTitle;
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
        _screenTools = new ScreenTools(findTextTools, visionTools, screen as Core.Vision.IScreenLayout);
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
        AddToEndCommand = new RelayCommand(p => AddStep(p, InsertPosition.AtEnd, forcePosition: true), p => HasMacro && p is StepKind);
        InsertAfterSelectedCommand = new RelayCommand(p => AddStep(p, InsertPosition.AfterSelected, forcePosition: true), p => HasMacro && p is StepKind);
        SaveCommand = new AsyncRelayCommand(async () => await SaveAsync(), HandleUnexpectedError, () => HasMacro && !_isSaving);
        RevertCommand = new RelayCommand(Revert, () => HasMacro && IsDirty);
        GoToLibraryCommand = new RelayCommand(goToLibrary);
        WrapInIfCommand = new RelayCommand(() => WrapSelection(StepKind.If), () => HasMacro && SelectedStep is not null);
        WrapInRepeatCommand = new RelayCommand(() => WrapSelection(StepKind.Repeat), () => HasMacro && SelectedStep is not null);
        CleanUpMousePathsCommand = new RelayCommand(CleanUpMousePaths, () => HasMacro && Steps.Any(s => s.Model is MouseMoveStep));
        AddPatternCommand = new RelayCommand(p => AddPattern(p, AddAtEnd ? InsertPosition.AtEnd : InsertPosition.AfterSelected), p => HasMacro && p is QuickPattern);

        UndoCommand = new RelayCommand(Undo, () => HasMacro && _history.CanUndo);
        RedoCommand = new RelayCommand(Redo, () => HasMacro && _history.CanRedo);
        CopySelectedStepCommand = new RelayCommand(CopySelectedSteps, () => HasMacro && SelectedStep is not null);
        PasteStepCommand = new RelayCommand(PasteSteps, () => HasMacro);
        RunStepCommand = new AsyncRelayCommand(RunSelectedStepAsync, HandleUnexpectedError, () => CanRunNow && SelectedStep is not null);
        PlayFromHereCommand = new AsyncRelayCommand(PlayFromSelectedAsync, HandleUnexpectedError, () => CanRunNow && SelectedStep is not null);
        UseCurrentSizeCommand = new RelayCommand(CaptureReference, () => HasMacro);
        RefreshTargetWindowsCommand = new RelayCommand(RefreshTargetWindows);
    }

    // ---------------- Undo / Redo (Ctrl+Z / Ctrl+Y) ----------------

    /// <summary>↶ Undo the last structural edit (add, delete, move, duplicate, wrap, clean up, paste, quick pattern).</summary>
    public RelayCommand UndoCommand { get; }

    /// <summary>↷ Redo what Undo took back.</summary>
    public RelayCommand RedoCommand { get; }

    public bool CanUndo => _history.CanUndo;

    public bool CanRedo => _history.CanRedo;

    public string UndoTooltip => _history.CanUndo
        ? $"Undo (Ctrl+Z) — {_history.UndoCount} change{(_history.UndoCount == 1 ? "" : "s")} can be undone"
        : "Undo (Ctrl+Z) — nothing to undo yet";

    public string RedoTooltip => _history.CanRedo ? "Redo (Ctrl+Y)" : "Redo (Ctrl+Y) — nothing to redo";

    /// <summary>The step list as a snapshot string (for Undo / Redo).</summary>
    private string Snapshot() => StepSnapshots.Capture(Steps.Select(s => s.Model));

    /// <summary>Call right before a structural edit, so it can be undone.</summary>
    private void RecordUndo()
    {
        if (!HasMacro) return;
        _history.Record(Snapshot());
        RaiseHistoryChanged();
    }

    private void RaiseHistoryChanged()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoTooltip));
        OnPropertyChanged(nameof(RedoTooltip));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private void Undo()
    {
        if (!HasMacro || !_history.TryUndo(Snapshot(), out var snapshot)) return;
        RestoreSnapshot(snapshot);
        RaiseHistoryChanged();
        _setStatus($"Undone.{(_history.CanUndo ? $" {_history.UndoCount} more change(s) can be undone." : "")} Ctrl+Y redoes it.", StatusKind.Info);
    }

    private void Redo()
    {
        if (!HasMacro || !_history.TryRedo(Snapshot(), out var snapshot)) return;
        RestoreSnapshot(snapshot);
        RaiseHistoryChanged();
        _setStatus("Redone.", StatusKind.Info);
    }

    /// <summary>Puts a snapshot back into the list, keeping the selection on the same step (by ID) where possible.</summary>
    private void RestoreSnapshot(string snapshot)
    {
        var selectedId = SelectedStep?.Id;
        var selectedIndex = SelectedStep is null ? -1 : Steps.IndexOf(SelectedStep);
        List<MacroStep> restored;
        try
        {
            restored = StepSnapshots.Restore(snapshot);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException)
        {
            _log.Error("An undo snapshot couldn't be read.", ex);
            return;
        }

        ReplaceSteps(restored);
        _selection.Clear();
        SelectedStep = Steps.FirstOrDefault(s => s.Id == selectedId)
                       ?? (Steps.Count == 0 ? null : Steps[Math.Clamp(selectedIndex, 0, Steps.Count - 1)]);
        Touch();
    }

    // ---------------- Copy / Paste (Ctrl+C / Ctrl+V) ----------------

    /// <summary>Copies the selected steps (a single If / Repeat takes its whole block) to the clipboard.</summary>
    public RelayCommand CopySelectedStepCommand { get; }

    /// <summary>Pastes copied steps after the selected step (or at the end), also from another macro.</summary>
    public RelayCommand PasteStepCommand { get; }

    private void CopySelectedSteps()
    {
        var selected = SelectedInOrder();
        if (selected.Count == 0) return;
        var models = Steps.Select(s => s.Model).ToList();
        var indexes = StepClipboard.ExpandSelection(models, selected.Select(s => Steps.IndexOf(s)).ToList());
        if (indexes is null)
        {
            _setStatus("Copy whole blocks: select from the If to its End If (or Repeat to End Repeat), or just the If / Repeat step itself.", StatusKind.Warning);
            return;
        }

        var text = StepClipboard.Serialize(indexes.Select(i => models[i]).ToList());
        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            _log.Warning("The clipboard is busy; try copying again.", ex);
            _setStatus("The clipboard is busy (another app is using it). Try Ctrl+C again.", StatusKind.Warning);
            return;
        }

        _setStatus(indexes.Count == 1 ? $"Copied step {indexes[0] + 1}. Press Ctrl+V to paste it here or in another macro."
            : $"Copied {indexes.Count} steps ({indexes[0] + 1}–{indexes[^1] + 1}). Press Ctrl+V to paste them here or in another macro.", StatusKind.Info);
    }

    private void PasteSteps()
    {
        if (!HasMacro) return;
        string? text = null;
        try
        {
            if (System.Windows.Clipboard.ContainsText()) text = System.Windows.Clipboard.GetText();
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            _log.Warning("The clipboard is busy; try pasting again.", ex);
        }

        if (!StepClipboard.TryParse(text, out var pasted, out var error))
        {
            _setStatus(error ?? "Nothing to paste.", StatusKind.Warning);
            return;
        }

        RecordUndo();
        var selectedIndex = SelectedStep is null ? -1 : Steps.IndexOf(SelectedStep);
        var index = StepListEditing.InsertIndex(Steps.Count, selectedIndex, AddAtEnd ? InsertPosition.AtEnd : InsertPosition.AfterSelected);
        var added = new List<StepViewModel>();
        foreach (var model in pasted)
        {
            var vm = CreateStepViewModel(model);
            Steps.Insert(index + added.Count, vm);
            added.Add(vm);
        }

        _selection = added;
        SelectedStep = added[0];
        SelectStepsRequested?.Invoke(this, added);
        Touch();
        _setStatus(added.Count == 1 ? $"Pasted 1 step as step {index + 1}. Ctrl+Z undoes it." : $"Pasted {added.Count} steps as steps {index + 1}–{index + added.Count}. Ctrl+Z undoes it.", StatusKind.Success);
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

    /// <summary>"⏭ Play from here": plays from the selected step to the end, once.</summary>
    public AsyncRelayCommand PlayFromHereCommand { get; }

    private Task RunSelectedStepAsync()
    {
        if (SelectedStep is not { } step || RunSingleStepHandler is null) return Task.CompletedTask;
        var index = Steps.IndexOf(step);
        var macro = MacroJson.Clone(BuildMacro());
        // Testing a step that is turned off: run it anyway (only in the copy that plays; the macro is unchanged).
        macro.Steps[index].Enabled = true;
        return RunSingleStepHandler(macro, index);
    }

    private Task PlayFromSelectedAsync()
    {
        if (SelectedStep is not { } step || PlayFromStepHandler is null) return Task.CompletedTask;
        return PlayFromStepHandler(MacroJson.Clone(BuildMacro()), Steps.IndexOf(step));
    }

    // ---------------- Macro settings: target window, auto-adjust coordinates, Humanize ----------------

    /// <summary>Open app windows for the "Target window" list (first entry = any / main screen).</summary>
    public ObservableCollection<string> TargetWindowChoices { get; } = new();

    public RelayCommand RefreshTargetWindowsCommand { get; }

    /// <summary>"Use current size": remembers where the target window / main screen is now as the reference.</summary>
    public RelayCommand UseCurrentSizeCommand { get; }

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
        $"Humanize: adds a subtle ±{ClickJitterPx} px random offset around click spots (kept inside what a Find step found) and ±{DelayJitterPercent} % natural variation to delays and mouse glide times, so repeated game loops don't click the exact same pixel with robotic timing. Recorded input still plays exactly as recorded.";

    private void RefreshTargetWindows()
    {
        var current = _targetWindowTitle;
        TargetWindowChoices.Clear();
        TargetWindowChoices.Add(AnyWindowLabel);
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
        var index = StepListEditing.InsertIndex(Steps.Count, selectedIndex, position);
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
    private long _lastRunningStepTickMs;

    /// <summary>Raised when playback moves to another step of this macro (the view scrolls it into view).</summary>
    public event EventHandler<StepViewModel>? RunningStepChanged;

    /// <summary>
    /// Marks the step playback is running right now (by step ID, so it works on the copy that plays); null clears it.
    /// Steps of another macro (e.g. one played from the library) simply don't match. Throttled to at most ~20 Hz so
    /// rapid step transitions never stall the UI dispatcher or playback threads.
    /// </summary>
    public void SetRunningStep(Guid? stepId)
    {
        if (stepId is not null)
        {
            var now = Environment.TickCount64;
            if (_runningStep is not null && now - _lastRunningStepTickMs < 50) return;
            _lastRunningStepTickMs = now;
        }
        else
        {
            _lastRunningStepTickMs = 0;
        }

        var step = stepId is { } id ? Steps.FirstOrDefault(s => s.Id == id) : null;
        if (ReferenceEquals(step, _runningStep)) return;
        if (_runningStep is not null) _runningStep.IsRunning = false;
        _runningStep = step;
        if (step is null) return;
        step.IsRunning = true;
        RunningStepChanged?.Invoke(this, step);
    }

    public ObservableCollection<StepViewModel> Steps { get; } = new();

    public ObservableCollection<ValidationIssue> Issues { get; } = new();

    public IReadOnlyList<StepTypeInfo> StepTypes => StepTypeCatalog.Addable;

    /// <summary>If / Else / End If / Repeat / End Repeat, shown in the toolbox's "Logic" section.</summary>
    public IReadOnlyList<StepTypeInfo> LogicStepTypes => StepTypeCatalog.Logic;

    public RelayCommand AddStepCommand { get; }
    public RelayCommand DeleteStepCommand { get; }
    public RelayCommand DuplicateStepCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public RelayCommand MoveToTopCommand { get; }
    public RelayCommand MoveToBottomCommand { get; }

    /// <summary>Adds the step kind given as parameter after the last step (whatever the toolbox setting is).</summary>
    public RelayCommand AddToEndCommand { get; }

    /// <summary>Adds the step kind given as parameter right after the selected step (or at the end if none).</summary>
    public RelayCommand InsertAfterSelectedCommand { get; }

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
            if (SetProperty(ref _speed, value)) Touch();
        }
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
            if (SetProperty(ref _selectedStep, value))
            {
                if (value is not null && (value.Model is IfStep or ElseStep or RepeatStep || value.IndentLevel > 0))
                    AddAtEnd = false;
                UpdateBlockPartnerHighlights();
                OnPropertyChanged(nameof(HasSelection));
            }
        }
    }

    public bool HasSelection => _selectedStep is not null;

    /// <summary>Called by the view when the list's selection changes (several steps can be selected).</summary>
    public void SetSelection(IEnumerable<StepViewModel> selected)
    {
        _selection = selected.Where(s => Steps.Contains(s)).ToList();
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
                OnPropertyChanged(nameof(ValidationSummary));
            }
        }
    }

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
        _repeatDelayMs = copy.RepeatDelayMs;
        _targetWindowTitle = string.IsNullOrWhiteSpace(copy.TargetWindowTitle) ? null : copy.TargetWindowTitle.Trim();
        _scaleCoordinates = copy.ScaleCoordinatesToWindow;
        (_referenceLeft, _referenceTop, _referenceWidth, _referenceHeight) = (copy.ReferenceLeft, copy.ReferenceTop, copy.ReferenceWidth, copy.ReferenceHeight);
        _humanize = copy.HumanizePlayback;
        _clickJitterPx = copy.ClickJitterPx;
        _delayJitterPercent = copy.DelayJitterPercent;
        _history.Clear();
        RaiseHistoryChanged();
        RefreshTargetWindows();

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
        _history.Clear();
        RaiseHistoryChanged();
        ReplaceSteps(Array.Empty<MacroStep>());
        Issues.Clear();
        ErrorCount = 0;
        WarningCount = 0;
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

    /// <summary>Builds a macro from the editor's current state (steps are the live objects, not copies).</summary>
    public Macro BuildMacro() => new()
    {
        Id = _id,
        Name = Name.Trim(),
        Description = string.IsNullOrWhiteSpace(Description) ? null : Description,
        CreatedAt = _createdAt,
        ModifiedAt = _saved?.ModifiedAt ?? default,
        Steps = Steps.Select(s => s.Model).ToList(),
        RepeatCount = RepeatCount,
        Speed = Speed,
        RepeatDelayMs = RepeatDelayMs,
        TargetWindowTitle = _targetWindowTitle,
        ScaleCoordinatesToWindow = _scaleCoordinates,
        ReferenceLeft = _referenceLeft,
        ReferenceTop = _referenceTop,
        ReferenceWidth = _referenceWidth,
        ReferenceHeight = _referenceHeight,
        HumanizePlayback = _humanize,
        ClickJitterPx = _clickJitterPx,
        DelayJitterPercent = _delayJitterPercent,
    };

    public async Task<bool> SaveAsync()
    {
        if (!HasMacro || _isSaving) return false;

        Revalidate();
        if (HasErrors)
        {
            _setStatus($"Can't save yet: fix {ErrorCount} error{(ErrorCount == 1 ? "" : "s")} first (see the Problems list).", StatusKind.Error);
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

    private void AddStep(object? parameter, InsertPosition position, bool forcePosition = false)
    {
        if (!HasMacro || parameter is not StepKind kind) return;

        var model = MacroFactory.CreateStep(kind, _defaultDelayMs());
        var selectedIndex = SelectedStep is null ? -1 : Steps.IndexOf(SelectedStep);
        var models = Steps.Select(s => s.Model).ToList();
        var index = forcePosition
            ? StepListEditing.InsertIndex(Steps.Count, selectedIndex, position)
            : StepListEditing.InsertIndex(models, selectedIndex, position, kind);

        // A standalone Else / End If / End Repeat is only added where it closes (or splits) an open block, so the
        // toolbox can't create orphan markers (an End If at the top of the macro with no If before it).
        if (StepListEditing.CheckMarkerInsert(models, index, model) is { } markerProblem)
        {
            _setStatus(markerProblem, StatusKind.Warning);
            return;
        }

        RecordUndo();
        var step = CreateStepViewModel(model);
        Steps.Insert(index, step);

        // If and Repeat come with their End, so the new block is complete straight away.
        var closer = kind switch
        {
            StepKind.If => MacroFactory.CreateStep(StepKind.EndIf),
            StepKind.Repeat => MacroFactory.CreateStep(StepKind.EndRepeat),
            _ => null,
        };
        if (closer is not null) Steps.Insert(index + 1, CreateStepViewModel(closer));

        _selection.Clear();
        SelectedStep = step; // the list scrolls the new step into view
        Touch();
        if (closer is not null)
            _setStatus($"Added {StepDescriber.DisplayName(kind)} and {StepDescriber.DisplayName(closer.Kind)} as steps {index + 1}–{index + 2}. Add steps between them, or select steps and use \"Wrap in\".", StatusKind.Info);
        else
            _setStatus($"Added {StepDescriber.DisplayName(kind)} as step {index + 1}{(index == Steps.Count - 1 ? " (at the end)" : "")}.", StatusKind.Info);
    }

    /// <summary>
    /// Puts an If … End If or Repeat … End Repeat around the selected steps. The selection must be steps next to
    /// each other and must contain whole blocks (not just the If of a block).
    /// </summary>
    private void WrapSelection(StepKind kind)
    {
        if (!HasMacro) return;
        var selected = SelectedInOrder();
        if (selected.Count == 0) return;

        var first = Steps.IndexOf(selected[0]);
        var last = Steps.IndexOf(selected[^1]);
        if (last - first + 1 != selected.Count)
        {
            _setStatus("Select steps that are next to each other (click the first, then Shift+click the last), then wrap them.", StatusKind.Warning);
            return;
        }

        var inner = BlockStructure.Analyze(Steps.Skip(first).Take(selected.Count).Select(s => s.Model).ToList());
        if (!inner.IsValid)
        {
            _setStatus("The selection contains only part of a block. Select whole blocks (from If to End If, or Repeat to End Repeat) to wrap them.", StatusKind.Warning);
            return;
        }

        RecordUndo();
        var opener = CreateStepViewModel(MacroFactory.CreateStep(kind));
        var closer = CreateStepViewModel(MacroFactory.CreateStep(kind == StepKind.If ? StepKind.EndIf : StepKind.EndRepeat));
        Steps.Insert(last + 1, closer);
        Steps.Insert(first, opener);
        SelectedStep = opener;
        Touch();
        _setStatus($"Wrapped {selected.Count} step{(selected.Count == 1 ? "" : "s")} in {StepDescriber.DisplayName(kind)}. Set it up in Properties.", StatusKind.Success);
    }

    /// <summary>
    /// "Convert recording to clean steps": collapses runs of recorded Delay + Move mouse steps. With several steps
    /// selected (next to each other) only those are cleaned; otherwise the whole macro. Drags (moves while a button is
    /// held) and steps with a comment or turned off are kept. Total waiting time stays the same.
    /// </summary>
    private void CleanUpMousePaths()
    {
        if (!HasMacro) return;
        var selected = SelectedInOrder();
        var models = Steps.Select(s => s.Model).ToList();
        var first = 0;
        var last = models.Count - 1;
        var scope = "the macro";
        if (selected.Count > 1)
        {
            first = Steps.IndexOf(selected[0]);
            last = Steps.IndexOf(selected[^1]);
            scope = "the selected steps";
        }

        var before = Snapshot();
        var result = StepCleanup.CollapseMouseMoves(models, first, last);
        if (result.RemovedSteps <= 0)
        {
            _setStatus($"Nothing to clean up in {scope}: no runs of Delay + Move mouse steps outside drags.", StatusKind.Info);
            return;
        }

        _history.Record(before);
        RaiseHistoryChanged();

        var existing = new Dictionary<MacroStep, StepViewModel>(ReferenceEqualityComparer.Instance);
        foreach (var vm in Steps) existing[vm.Model] = vm;
        var keepSelected = SelectedStep is { } current && result.Steps.Contains(current.Model) ? current : null;
        foreach (var old in Steps) old.Modified -= OnStepModified;
        Steps.Clear();
        foreach (var step in result.Steps)
            Steps.Add(existing.TryGetValue(step, out var kept) ? kept : CreateStepViewModel(step));

        _selection.Clear();
        SelectedStep = keepSelected ?? Steps.ElementAtOrDefault(Math.Min(first, Steps.Count - 1));
        Touch();
        _setStatus($"Cleaned up {result.CollapsedRuns} mouse path(s) in {scope}: {result.RemovedSteps:N0} step(s) fewer, same total timing. Ctrl+Z undoes it.", StatusKind.Success);
    }

    /// <summary>
    /// Deletes the selected steps. Deleting an If / Repeat (or its End If / End Repeat) also removes the other markers
    /// of that block (If, Else, End If / Repeat, End Repeat) but keeps the steps that were inside, so a delete can
    /// never leave an orphan End If / Else / End Repeat behind (see <see cref="StepListEditing.PlanDelete"/>).
    /// </summary>
    private void DeleteSelectedSteps()
    {
        var selected = SelectedInOrder();
        if (selected.Count == 0) return;

        var plan = StepListEditing.PlanDelete(Steps.Select(s => s.Model).ToList(), selected.Select(s => Steps.IndexOf(s)));
        if (plan.Indexes.Count == 0) return;
        var toRemove = plan.Indexes.Select(i => Steps[i]).ToList();

        RecordUndo();
        var firstIndex = plan.Indexes[0];
        foreach (var step in toRemove.OrderByDescending(s => Steps.IndexOf(s)).ToList())
            Steps.Remove(step);

        _selection.Clear();
        SelectedStep = Steps.Count == 0 ? null : Steps[Math.Clamp(firstIndex, 0, Steps.Count - 1)];
        Touch();
        if (plan.Note is not null) _setStatus(plan.Note, StatusKind.Info);
    }

    private void DuplicateSelectedStep()
    {
        if (SelectedStep is not { } step) return;
        var index = Steps.IndexOf(step);
        if (index < 0) return;

        if (step.Model is IfStep or RepeatStep or EndIfStep or EndRepeatStep or ElseStep)
        {
            var models = Steps.Select(s => s.Model).ToList();
            var blocks = BlockStructure.Analyze(models);
            if (blocks.IsValid)
            {
                if (step.Model is ElseStep)
                {
                    _setStatus("An If block can have only one Else. To duplicate the whole If block, select its If step and press Ctrl+D.", StatusKind.Warning);
                    return;
                }

                var start = step.Model is IfStep or RepeatStep ? index : blocks.StartOf(index);
                var end = start >= 0 ? blocks.EndOf(start) : -1;
                if (start >= 0 && end >= 0)
                {
                    RecordUndo();
                    var added = new List<StepViewModel>();
                    for (var i = start; i <= end; i++)
                    {
                        var vm = CreateStepViewModel(MacroFactory.DuplicateStep(models[i]));
                        Steps.Insert(end + 1 + added.Count, vm);
                        added.Add(vm);
                    }

                    _selection = added;
                    SelectedStep = added[0];
                    SelectStepsRequested?.Invoke(this, added);
                    Touch();
                    return;
                }
            }
        }

        RecordUndo();
        var copy = CreateStepViewModel(MacroFactory.DuplicateStep(step.Model));
        Steps.Insert(index + 1, copy);
        SelectedStep = copy;
        Touch();
    }

    private bool CanMove(bool up)
    {
        var selected = SelectedInOrder();
        if (selected.Count == 0) return false;
        return up ? Steps.IndexOf(selected[0]) > 0 : Steps.IndexOf(selected[^1]) < Steps.Count - 1;
    }

    /// <summary>Move up / down / to top / to bottom for the selected steps (they must be next to each other).</summary>
    private void MoveSelected(MoveDirection direction)
    {
        if (!TryGetSelectedRange(out var first, out var last)) return;
        Apply(StepListEditing.PlanMove(Steps.Select(s => s.Model).ToList(), first, last, direction));
    }

    /// <summary>
    /// Drag and drop: moves the selected steps (whole blocks when an If / Repeat is part of them) in front of the
    /// step that is now at <paramref name="dropIndex"/> (Steps.Count = after the last step).
    /// </summary>
    public void MoveSelectionTo(int dropIndex)
    {
        if (!HasMacro || !TryGetSelectedRange(out var first, out var last)) return;
        Apply(StepListEditing.PlanDrop(Steps.Select(s => s.Model).ToList(), first, last, dropIndex));
    }

    private bool TryGetSelectedRange(out int first, out int last)
    {
        first = last = -1;
        var selected = SelectedInOrder();
        if (selected.Count == 0) return false;

        first = Steps.IndexOf(selected[0]);
        last = Steps.IndexOf(selected[^1]);
        if (last - first + 1 == selected.Count) return true;

        _setStatus("To move several steps at once, select steps that are next to each other (click the first, then Shift+click the last).", StatusKind.Warning);
        return false;
    }

    /// <summary>Carries out a planned move (or explains why it isn't possible) and keeps the moved steps selected.</summary>
    private void Apply(MovePlan plan)
    {
        if (!plan.Allowed)
        {
            if (plan.Problem is not null) _setStatus(plan.Problem, StatusKind.Warning);
            return;
        }

        RecordUndo();
        var moved = Steps.Skip(plan.First).Take(plan.Count).ToList();
        if (plan.NewFirst < plan.First)
        {
            for (var i = 0; i < plan.Count; i++) Steps.Move(plan.First + i, plan.NewFirst + i);
        }
        else
        {
            for (var i = plan.Count - 1; i >= 0; i--) Steps.Move(plan.First + i, plan.NewFirst + i);
        }

        _selection = moved;
        SelectedStep = moved[0];
        SelectStepsRequested?.Invoke(this, moved);
        Touch();

        var where = plan.NewFirst == 0 ? "to the top" : plan.NewFirst + plan.Count == Steps.Count ? "to the bottom" : $"to step {plan.NewFirst + 1}";
        var what = plan.Count == 1 ? "Moved the step" : $"Moved {plan.Count} steps";
        var blocks = plan.TookWholeBlocks ? " (whole If / Repeat blocks were moved together)" : "";
        if (plan.TookWholeBlocks || Math.Abs(plan.NewFirst - plan.First) > 1)
            _setStatus($"{what} {where}{blocks}.", StatusKind.Info);
    }

    private StepViewModel CreateStepViewModel(MacroStep step)
    {
        var vm = StepViewModel.Create(step);
        _screenTools.ApplyTo(vm);
        return vm;
    }

    private void ReplaceSteps(IEnumerable<MacroStep> steps)
    {
        foreach (var old in Steps) old.Modified -= OnStepModified;
        Steps.Clear();
        foreach (var step in steps)
            Steps.Add(CreateStepViewModel(step));
        SelectedStep = Steps.FirstOrDefault();
    }

    private void OnStepsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (StepViewModel old in e.OldItems) old.Modified -= OnStepModified;
        if (e.NewItems is not null)
            foreach (StepViewModel added in e.NewItems) added.Modified += OnStepModified;

        for (var i = 0; i < Steps.Count; i++) Steps[i].Number = i + 1;

        var blocks = BlockStructure.Analyze(Steps.Select(s => s.Model).ToList());
        UpdateBlockPairing(blocks);

        OnPropertyChanged(nameof(HasSteps));
        OnPropertyChanged(nameof(StepCountText)); // also re-checks Move up/down availability
    }

    private void OnStepModified(object? sender, EventArgs e) => Touch();

    private void Touch()
    {
        if (!HasMacro) return;
        IsDirty = true;
        Revalidate();
    }

    private void Revalidate()
    {
        if (!HasMacro) return;

        var issues = MacroValidator.Validate(BuildMacro(), _screenBounds);

        Issues.Clear();
        foreach (var issue in issues.OrderByDescending(i => i.Severity).ThenBy(i => i.StepIndex ?? -1))
            Issues.Add(issue);

        var byStep = issues.Where(i => i.StepId is not null).ToLookup(i => i.StepId!.Value);
        foreach (var step in Steps) step.SetIssues(byStep[step.Id]);

        // Indent the steps inside If / Repeat blocks (also when the blocks don't match up yet).
        var blocks = BlockStructure.Analyze(Steps.Select(s => s.Model).ToList());
        UpdateBlockPairing(blocks);

        ErrorCount = issues.Count(i => i.Severity == IssueSeverity.Error);
        WarningCount = issues.Count(i => i.Severity == IssueSeverity.Warning);
        OnPropertyChanged(nameof(HasIssues));
    }

    private void UpdateBlockPairing(BlockStructure blocks)
    {
        for (var i = 0; i < Steps.Count; i++)
        {
            var depth = blocks.Depth[i];
            Steps[i].IndentLevel = depth;
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
