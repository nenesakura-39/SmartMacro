using System.Collections.ObjectModel;
using System.Collections.Specialized;
using SmartMacro.Core.Logging;
using SmartMacro.Core.Models;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Validation;
using SmartMacro.Mvvm;
using SmartMacro.Services;
using SmartMacro.Services.Ocr;
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
    private readonly IFindTextTools? _findTextTools;

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

    public EditorViewModel(
        IMacroRepository repository,
        ILogService log,
        IDialogService dialogs,
        IScreenInfoProvider screen,
        Func<int> defaultDelayMs,
        Action<Macro> onSaved,
        Action<string, StatusKind> setStatus,
        Action goToLibrary,
        IFindTextTools? findTextTools = null)
    {
        _findTextTools = findTextTools;
        _repository = repository;
        _log = log;
        _dialogs = dialogs;
        _screen = screen;
        _defaultDelayMs = defaultDelayMs;
        _onSaved = onSaved;
        _setStatus = setStatus;

        Steps.CollectionChanged += OnStepsChanged;

        AddStepCommand = new RelayCommand(p => AddStep(p), p => HasMacro && p is StepKind);
        DeleteStepCommand = new RelayCommand(DeleteSelectedStep, () => SelectedStep is not null);
        DuplicateStepCommand = new RelayCommand(DuplicateSelectedStep, () => SelectedStep is not null);
        MoveUpCommand = new RelayCommand(() => MoveSelected(-1), () => SelectedStep is not null && Steps.IndexOf(SelectedStep) > 0);
        MoveDownCommand = new RelayCommand(() => MoveSelected(+1),
            () => SelectedStep is not null && Steps.IndexOf(SelectedStep) < Steps.Count - 1);
        SaveCommand = new AsyncRelayCommand(async () => await SaveAsync(), HandleUnexpectedError, () => HasMacro && !_isSaving);
        RevertCommand = new RelayCommand(Revert, () => HasMacro && IsDirty);
        GoToLibraryCommand = new RelayCommand(goToLibrary);
    }

    public ObservableCollection<StepViewModel> Steps { get; } = new();

    public ObservableCollection<ValidationIssue> Issues { get; } = new();

    public IReadOnlyList<StepTypeInfo> StepTypes => StepTypeCatalog.All;

    public RelayCommand AddStepCommand { get; }
    public RelayCommand DeleteStepCommand { get; }
    public RelayCommand DuplicateStepCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public RelayCommand RevertCommand { get; }
    public RelayCommand GoToLibraryCommand { get; }

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

    private void AddStep(object? parameter)
    {
        if (!HasMacro || parameter is not StepKind kind) return;

        var step = CreateStepViewModel(MacroFactory.CreateStep(kind, _defaultDelayMs()));
        var index = SelectedStep is null ? Steps.Count : Steps.IndexOf(SelectedStep) + 1;
        Steps.Insert(index, step);
        SelectedStep = step;
        Touch();
    }

    private void DeleteSelectedStep()
    {
        if (SelectedStep is not { } step) return;

        var index = Steps.IndexOf(step);
        Steps.Remove(step);
        SelectedStep = Steps.Count == 0 ? null : Steps[Math.Min(index, Steps.Count - 1)];
        Touch();
    }

    private void DuplicateSelectedStep()
    {
        if (SelectedStep is not { } step) return;

        var copy = CreateStepViewModel(MacroFactory.DuplicateStep(step.Model));
        Steps.Insert(Steps.IndexOf(step) + 1, copy);
        SelectedStep = copy;
        Touch();
    }

    private void MoveSelected(int offset)
    {
        if (SelectedStep is not { } step) return;

        var from = Steps.IndexOf(step);
        var to = from + offset;
        if (to < 0 || to >= Steps.Count) return;

        Steps.Move(from, to);
        SelectedStep = step;
        Touch();
    }

    private StepViewModel CreateStepViewModel(MacroStep step)
    {
        var vm = StepViewModel.Create(step);
        if (vm is FindTextStepViewModel findText) findText.Tools = _findTextTools;
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
