using System.Collections.ObjectModel;
using System.Collections.Specialized;
using SmartMacro.Core.Logging;
using SmartMacro.Core.Models;
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
        _screenTools = new ScreenTools(findTextTools, visionTools);
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
        AddToEndCommand = new RelayCommand(p => AddStep(p, InsertPosition.AtEnd), p => HasMacro && p is StepKind);
        InsertAfterSelectedCommand = new RelayCommand(p => AddStep(p, InsertPosition.AfterSelected), p => HasMacro && p is StepKind);
        SaveCommand = new AsyncRelayCommand(async () => await SaveAsync(), HandleUnexpectedError, () => HasMacro && !_isSaving);
        RevertCommand = new RelayCommand(Revert, () => HasMacro && IsDirty);
        GoToLibraryCommand = new RelayCommand(goToLibrary);
        WrapInIfCommand = new RelayCommand(() => WrapSelection(StepKind.If), () => HasMacro && SelectedStep is not null);
        WrapInRepeatCommand = new RelayCommand(() => WrapSelection(StepKind.Repeat), () => HasMacro && SelectedStep is not null);
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
                OnPropertyChanged(nameof(HasSelection));
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

    private void AddStep(object? parameter, InsertPosition position)
    {
        if (!HasMacro || parameter is not StepKind kind) return;

        var step = CreateStepViewModel(MacroFactory.CreateStep(kind, _defaultDelayMs()));
        var selectedIndex = SelectedStep is null ? -1 : Steps.IndexOf(SelectedStep);
        var index = StepListEditing.InsertIndex(Steps.Count, selectedIndex, position);
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

        var opener = CreateStepViewModel(MacroFactory.CreateStep(kind));
        var closer = CreateStepViewModel(MacroFactory.CreateStep(kind == StepKind.If ? StepKind.EndIf : StepKind.EndRepeat));
        Steps.Insert(last + 1, closer);
        Steps.Insert(first, opener);
        SelectedStep = opener;
        Touch();
        _setStatus($"Wrapped {selected.Count} step{(selected.Count == 1 ? "" : "s")} in {StepDescriber.DisplayName(kind)}. Set it up in Properties.", StatusKind.Success);
    }

    /// <summary>
    /// Deletes the selected steps. Deleting just the If / Repeat (or its End) of a complete block removes the whole
    /// set of markers (If, Else, End If / Repeat, End Repeat) but keeps the steps that were inside.
    /// </summary>
    private void DeleteSelectedSteps()
    {
        var selected = SelectedInOrder();
        if (selected.Count == 0) return;

        var toRemove = new HashSet<StepViewModel>(selected);
        string? note = null;
        if (selected.Count == 1 && selected[0].Model is IfStep or EndIfStep or RepeatStep or EndRepeatStep)
        {
            var blocks = BlockStructure.Analyze(Steps.Select(s => s.Model).ToList());
            var index = Steps.IndexOf(selected[0]);
            var start = selected[0].Model is IfStep or RepeatStep ? index : blocks.StartOf(index);
            var end = start >= 0 ? blocks.EndOf(start) : -1;
            if (start >= 0 && end >= 0)
            {
                toRemove.Add(Steps[start]);
                toRemove.Add(Steps[end]);
                var elseIndex = blocks.ElseOf(start);
                if (elseIndex >= 0) toRemove.Add(Steps[elseIndex]);
                note = $"Removed the {StepDescriber.DisplayName(Steps[start].Kind)} block markers. The steps inside were kept.";
            }
        }

        var firstIndex = Steps.IndexOf(selected[0]);
        foreach (var step in toRemove.OrderByDescending(s => Steps.IndexOf(s)).ToList())
            Steps.Remove(step);

        _selection.Clear();
        SelectedStep = Steps.Count == 0 ? null : Steps[Math.Clamp(firstIndex, 0, Steps.Count - 1)];
        Touch();
        if (note is not null) _setStatus(note, StatusKind.Info);
    }

    private void DuplicateSelectedStep()
    {
        if (SelectedStep is not { } step) return;

        var copy = CreateStepViewModel(MacroFactory.DuplicateStep(step.Model));
        Steps.Insert(Steps.IndexOf(step) + 1, copy);
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
        for (var i = 0; i < Steps.Count; i++) Steps[i].IndentLevel = blocks.Depth[i];

        ErrorCount = issues.Count(i => i.Severity == IssueSeverity.Error);
        WarningCount = issues.Count(i => i.Severity == IssueSeverity.Warning);
        OnPropertyChanged(nameof(HasIssues));
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
