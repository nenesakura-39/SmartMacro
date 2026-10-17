using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using SmartMacro.Core.Logging;
using SmartMacro.Core.Models;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Validation;
using SmartMacro.Mvvm;
using SmartMacro.Services;

namespace SmartMacro.ViewModels;

/// <summary>One row in the macro library.</summary>
public sealed class MacroListItemViewModel : ObservableObject
{
    private Macro _macro;

    public MacroListItemViewModel(Macro macro)
    {
        _macro = macro;
    }

    /// <summary>The saved version of the macro (treat as read-only).</summary>
    public Macro Macro => _macro;

    public Guid Id => _macro.Id;
    public string Name => _macro.Name;
    public string Description => string.IsNullOrWhiteSpace(_macro.Description) ? "No description" : _macro.Description!;
    public bool HasDescription => !string.IsNullOrWhiteSpace(_macro.Description);
    public int StepCount => _macro.Steps.Count;
    public string StepCountText => StepCount == 1 ? "1 step" : $"{StepCount:N0} steps";
    public string ModifiedText => _macro.ModifiedAt == default ? "-" : _macro.ModifiedAt.ToLocalTime().ToString("MMM d, yyyy  h:mm tt");
    public string CreatedText => _macro.CreatedAt == default ? "-" : _macro.CreatedAt.ToLocalTime().ToString("MMM d, yyyy  h:mm tt");
    public string IdText => _macro.Id.ToString();
    public bool HasErrors { get; private set; }

    // Category pills on the Studio Macro Card: what kinds of steps the macro uses.
    public bool UsesInput { get; private set; }
    public bool UsesVision { get; private set; }
    public bool UsesOcr { get; private set; }
    public bool UsesFeedback { get; private set; }
    public bool UsesLogic { get; private set; }
    public bool UsesHumanize => _macro.HumanizePlayback;
    public bool UsesTargetWindow => _macro.ScaleCoordinatesToWindow || !string.IsNullOrWhiteSpace(_macro.TargetWindowTitle);

    /// <summary>E.g. "just now", "5 min ago", "yesterday", or the date.</summary>
    public string ModifiedAgoText
    {
        get
        {
            if (_macro.ModifiedAt == default) return "never saved";
            var age = DateTimeOffset.Now - _macro.ModifiedAt;
            if (age < TimeSpan.FromMinutes(1)) return "edited just now";
            if (age < TimeSpan.FromHours(1)) return $"edited {(int)age.TotalMinutes} min ago";
            if (age < TimeSpan.FromDays(1)) return $"edited {(int)age.TotalHours} h ago";
            if (age < TimeSpan.FromDays(2)) return "edited yesterday";
            if (age < TimeSpan.FromDays(7)) return $"edited {(int)age.TotalDays} days ago";
            return "edited " + _macro.ModifiedAt.ToLocalTime().ToString("MMM d, yyyy");
        }
    }

    public string RepeatText => _macro.RepeatCount switch
    {
        0 => "loops until stopped",
        1 => "runs once",
        var n => $"runs {n:N0}×",
    };

    public void Update(Macro macro)
    {
        _macro = macro;
        HasErrors = MacroValidator.Validate(macro).HasErrors();

        var steps = macro.Steps ?? new List<MacroStep>();
        var searches = steps.SelectMany(WorkflowSteps.ScreenSearches).ToList();
        UsesInput = steps.Any(s => s is MouseMoveStep or MouseClickStep or MouseScrollStep or KeyPressStep or TypeTextStep or InputSequenceStep);
        UsesVision = searches.Any(s => s is FindImageStep or FindColorStep);
        UsesOcr = searches.Any(s => s is FindTextStep);
        UsesFeedback = steps.Any(s => s is FeedbackLoopStep);
        UsesLogic = steps.Any(WorkflowSteps.IsBlockStep);
        OnPropertyChanged(string.Empty);
    }

    /// <summary>True when the card matches a search (name, description or a category word like "ocr").</summary>
    public bool Matches(string term)
    {
        if (Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)
            || (_macro.Description?.Contains(term, StringComparison.CurrentCultureIgnoreCase) ?? false))
            return true;

        return term.ToLowerInvariant() switch
        {
            "vision" or "image" or "color" or "colour" => UsesVision,
            "ocr" or "text" => UsesOcr,
            "feedback" or "loop" or "minigame" => UsesFeedback,
            "input" or "click" or "mouse" or "key" => UsesInput,
            "logic" or "if" or "repeat" => UsesLogic,
            "error" or "errors" or "problem" or "problems" => HasErrors,
            _ => false,
        };
    }

    public static MacroListItemViewModel From(Macro macro)
    {
        var item = new MacroListItemViewModel(macro);
        item.Update(macro);
        return item;
    }
}

public sealed class LibraryViewModel : ObservableObject
{
    private readonly IMacroRepository _repository;
    private readonly ILogService _log;
    private readonly IDialogService _dialogs;
    private readonly Func<bool> _confirmBeforeDelete;
    private readonly Func<Guid, Task> _openInEditor;
    private readonly Action<Guid> _afterDelete;
    private readonly Action<string, StatusKind> _setStatus;
    private readonly Func<Macro, Task>? _playMacro;
    private readonly Func<bool> _canPlay;

    private const string ImportFileFilter = "SmartMacro macro (*.smacro;*.smartmacro;*.json)|*.smacro;*.smartmacro;*.json|All files (*.*)|*.*";
    private const string ExportFileFilter = "SmartMacro macro (*.smacro)|*.smacro|All files (*.*)|*.*";

    private MacroListItemViewModel? _selectedItem;
    private string _searchText = string.Empty;
    private bool _isLoading;
    private string? _loadProblemText;

    public LibraryViewModel(
        IMacroRepository repository,
        ILogService log,
        IDialogService dialogs,
        Func<bool> confirmBeforeDelete,
        Func<Guid, Task> openInEditor,
        Action<Guid> afterDelete,
        Action<string, StatusKind> setStatus,
        Func<Macro, Task>? playMacro = null,
        Func<bool>? canPlay = null)
    {
        _playMacro = playMacro;
        _canPlay = canPlay ?? (() => true);
        _repository = repository;
        _log = log;
        _dialogs = dialogs;
        _confirmBeforeDelete = confirmBeforeDelete;
        _openInEditor = openInEditor;
        _afterDelete = afterDelete;
        _setStatus = setStatus;

        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = FilterItem;
        ItemsView.SortDescriptions.Add(new SortDescription(nameof(MacroListItemViewModel.Name), ListSortDirection.Ascending));

        NewCommand = new AsyncRelayCommand(CreateNewAsync, HandleError, () => !IsLoading);
        // Card buttons pass their own macro as the parameter; toolbar / keyboard use the selection.
        OpenCommand = new AsyncRelayCommand(p => OpenSelectedAsync(Target(p)), HandleError, HasTarget);
        DuplicateCommand = new AsyncRelayCommand(p => DuplicateSelectedAsync(Target(p)), HandleError, HasTarget);
        DeleteCommand = new AsyncRelayCommand(p => DeleteSelectedAsync(Target(p)), HandleError, HasTarget);
        ReloadCommand = new AsyncRelayCommand(LoadAsync, HandleError, () => !IsLoading);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);
        ImportCommand = new AsyncRelayCommand(ImportAsync, HandleError, () => !IsLoading);
        ExportCommand = new AsyncRelayCommand(p => ExportSelectedAsync(Target(p)), HandleError, HasTarget);
        PlayItemCommand = new AsyncRelayCommand(
            p => PlayItemAsync(p as MacroListItemViewModel ?? SelectedItem),
            HandleError,
            p => _playMacro is not null && _canPlay() && (p is MacroListItemViewModel || SelectedItem is not null));
    }

    /// <summary>The card a button belongs to (its parameter), or the selected macro. Selects it too.</summary>
    private MacroListItemViewModel? Target(object? parameter)
    {
        if (parameter is MacroListItemViewModel item)
        {
            SelectedItem = item;
            return item;
        }

        return SelectedItem;
    }

    private bool HasTarget(object? parameter) => parameter is MacroListItemViewModel || SelectedItem is not null;

    public AsyncRelayCommand ImportCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }

    /// <summary>Plays a macro straight from the library (parameter: the row; defaults to the selection).</summary>
    public AsyncRelayCommand PlayItemCommand { get; }

    public ObservableCollection<MacroListItemViewModel> Items { get; } = new();

    public ICollectionView ItemsView { get; }

    public AsyncRelayCommand NewCommand { get; }
    public AsyncRelayCommand OpenCommand { get; }
    public AsyncRelayCommand DuplicateCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public AsyncRelayCommand ReloadCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand ClearSearchCommand { get; }

    public MacroListItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value))
                OnPropertyChanged(nameof(HasSelection));
        }
    }

    public bool HasSelection => _selectedItem is not null;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                ItemsView.Refresh();
                RaiseStateChanged();
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value)) RaiseStateChanged();
        }
    }

    /// <summary>Shown as a banner when some files couldn't be loaded.</summary>
    public string? LoadProblemText
    {
        get => _loadProblemText;
        private set
        {
            if (SetProperty(ref _loadProblemText, value)) OnPropertyChanged(nameof(HasLoadProblems));
        }
    }

    public bool HasLoadProblems => !string.IsNullOrEmpty(_loadProblemText);

    public bool IsEmpty => !IsLoading && Items.Count == 0;

    public bool IsFilteredEmpty => !IsLoading && Items.Count > 0 && ItemsView.IsEmpty;

    public bool HasVisibleItems => !IsLoading && !ItemsView.IsEmpty;

    public string CountText => Items.Count == 1 ? "1 macro" : $"{Items.Count:N0} macros";

    public IEnumerable<string> Names => Items.Select(i => i.Name);

    public string MacrosDirectory => _repository.MacrosDirectory;

    public Macro? GetSaved(Guid id) => Items.FirstOrDefault(i => i.Id == id)?.Macro;

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var selectedId = SelectedItem?.Id;
            var result = await _repository.LoadAllAsync();

            Items.Clear();
            foreach (var macro in result.Macros) Items.Add(MacroListItemViewModel.From(macro));
            SelectedItem = Items.FirstOrDefault(i => i.Id == selectedId) ?? ItemsView.Cast<MacroListItemViewModel>().FirstOrDefault();

            foreach (var repair in result.Repairs) _log.Warning($"Repaired: {repair}");
            foreach (var problem in result.Problems) _log.Warning($"Skipped {problem.FileName}: {problem.Message}");

            LoadProblemText = result.Problems.Count == 0
                ? null
                : $"{result.Problems.Count} file{(result.Problems.Count == 1 ? "" : "s")} in the macros folder could not be loaded and {(result.Problems.Count == 1 ? "was" : "were")} left untouched: "
                  + string.Join(", ", result.Problems.Take(3).Select(p => p.FileName))
                  + (result.Problems.Count > 3 ? "…" : "") + ". See Logs for details.";

            _log.Info($"Loaded {result.Macros.Count} macro(s) from {_repository.MacrosDirectory}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("Could not read the macros folder.", ex);
            LoadProblemText = $"The macros folder could not be read: {ex.Message}";
            _setStatus("Could not load macros. See Logs for details.", StatusKind.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Called by the editor after a successful save.</summary>
    public void Upsert(Macro saved)
    {
        var existing = Items.FirstOrDefault(i => i.Id == saved.Id);
        if (existing is null)
        {
            existing = MacroListItemViewModel.From(saved);
            Items.Add(existing);
        }
        else
        {
            existing.Update(saved);
        }

        ItemsView.Refresh();
        SelectedItem = existing;
        RaiseStateChanged();
    }

    private async Task CreateNewAsync()
    {
        var macro = MacroFactory.CreateNew(Names);
        try
        {
            await _repository.SaveAsync(macro);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("Creating a macro failed.", ex);
            _dialogs.ShowError("Could not create macro", ex.Message);
            return;
        }

        // Make sure the new macro is visible even if a search is active.
        SearchText = string.Empty;
        Upsert(MacroJson.Clone(macro));
        _log.Success($"Created macro \"{macro.Name}\".");
        await _openInEditor(macro.Id);
    }

    private Task OpenSelectedAsync(MacroListItemViewModel? target) =>
        target is { } item ? _openInEditor(item.Id) : Task.CompletedTask;

    private async Task DuplicateSelectedAsync(MacroListItemViewModel? target)
    {
        if (target is not { } item) return;

        var copy = MacroFactory.Duplicate(item.Macro, Names);
        try
        {
            await _repository.SaveAsync(copy);
        }
        catch (MacroValidationException ex)
        {
            _dialogs.ShowWarning("Can't duplicate", $"\"{item.Name}\" has problems that must be fixed first.\n\n{ex.Message}");
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("Duplicating a macro failed.", ex);
            _dialogs.ShowError("Could not duplicate macro", ex.Message);
            return;
        }

        SearchText = string.Empty;
        Upsert(MacroJson.Clone(copy));
        _setStatus($"Created \"{copy.Name}\".", StatusKind.Success);
    }

    private async Task DeleteSelectedAsync(MacroListItemViewModel? target)
    {
        if (target is not { } item) return;

        if (_confirmBeforeDelete() &&
            !_dialogs.Confirm("Delete macro",
                $"Delete \"{item.Name}\"?\n\nThe file is moved to SmartMacro's Trash folder, so it can still be recovered manually."))
            return;

        try
        {
            await _repository.DeleteAsync(item.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error($"Deleting \"{item.Name}\" failed.", ex);
            _dialogs.ShowError("Could not delete macro", ex.Message);
            return;
        }

        var index = ItemsView.Cast<MacroListItemViewModel>().ToList().IndexOf(item);
        Items.Remove(item);
        var visible = ItemsView.Cast<MacroListItemViewModel>().ToList();
        SelectedItem = visible.Count == 0 ? null : visible[Math.Clamp(index, 0, visible.Count - 1)];
        RaiseStateChanged();

        _afterDelete(item.Id);
        _setStatus($"Deleted \"{item.Name}\" (moved to Trash).", StatusKind.Success);
    }

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(_repository.MacrosDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_repository.MacrosDirectory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Error("Could not open the macros folder.", ex);
            _dialogs.ShowError("Could not open folder", ex.Message);
        }
    }

    private Task PlayItemAsync(MacroListItemViewModel? item)
    {
        if (item is null || _playMacro is null) return Task.CompletedTask;
        SelectedItem = item;
        return _playMacro(MacroJson.Clone(item.Macro));
    }

    /// <summary>Adds macros from .smartmacro / .json files. Each file is checked; bad files are skipped with a reason.</summary>
    private async Task ImportAsync()
    {
        var files = _dialogs.PickFilesToOpen("Import .smacro files", ImportFileFilter);
        if (files is null || files.Length == 0) return;

        var imported = new List<string>();
        var skipped = new List<string>();
        MacroListItemViewModel? last = null;

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            try
            {
                var info = new FileInfo(file);
                if (info.Length > MacroFileFormat.MaxFileSizeBytes)
                {
                    skipped.Add($"{name}: the file is too large to be a macro.");
                    continue;
                }

                var json = await File.ReadAllTextAsync(file);
                if (!MacroFileFormat.TryParse(json, out var macro, out var error))
                {
                    skipped.Add($"{name}: {error}");
                    continue;
                }

                var prepared = MacroFileFormat.PrepareForImport(macro!, Names);
                var issues = MacroValidator.Validate(prepared);
                if (issues.HasErrors())
                {
                    var first = issues.First(i => i.Severity == IssueSeverity.Error).DisplayText;
                    skipped.Add($"{name}: the macro has problems ({first}).");
                    continue;
                }

                await _repository.SaveAsync(prepared);
                SearchText = string.Empty;
                Upsert(MacroJson.Clone(prepared));
                last = Items.FirstOrDefault(i => i.Id == prepared.Id);
                imported.Add(prepared.Name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped.Add($"{name}: {ex.Message}");
            }
        }

        if (last is not null) SelectedItem = last;
        foreach (var s in skipped) _log.Warning($"Import skipped {s}");

        var summary = imported.Count == 1 ? $"Imported \"{imported[0]}\"." : $"Imported {imported.Count} macros.";
        if (skipped.Count == 0)
        {
            _setStatus(summary, StatusKind.Success);
        }
        else
        {
            _setStatus($"{summary} {skipped.Count} file(s) skipped (see Logs).", imported.Count > 0 ? StatusKind.Warning : StatusKind.Error);
            _dialogs.ShowWarning("Import", $"{summary}\n\nThese files were skipped:\n\n{string.Join("\n", skipped.Take(10).Select(s => "• " + s))}");
        }
    }

    /// <summary>Saves a macro as a self-contained .smacro file (to share or back up).</summary>
    private async Task ExportSelectedAsync(MacroListItemViewModel? target)
    {
        if (target is not { } item) return;

        var safeName = string.Concat(item.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        if (safeName.Length == 0) safeName = "macro";

        var path = _dialogs.PickSaveFile("Export macro", ExportFileFilter, safeName + MacroFileFormat.ExportExtension);
        if (path is null) return;

        try
        {
            await AtomicFile.WriteAllTextAsync(path, MacroFileFormat.Serialize(MacroJson.Clone(item.Macro)));
            _setStatus($"Exported \"{item.Name}\" to {path}. It's one self-contained file (pictures included): send it to a friend and they can import it.", StatusKind.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error($"Exporting \"{item.Name}\" failed.", ex);
            _dialogs.ShowError("Export failed", ex.Message);
        }
    }

    private bool FilterItem(object obj)
    {
        if (obj is not MacroListItemViewModel item) return false;
        if (string.IsNullOrWhiteSpace(_searchText)) return true;
        return item.Matches(_searchText.Trim());
    }

    private void RaiseStateChanged()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsFilteredEmpty));
        OnPropertyChanged(nameof(HasVisibleItems));
        OnPropertyChanged(nameof(CountText));
    }

    private void HandleError(Exception ex)
    {
        _log.Error("Unexpected error in the library.", ex);
        _dialogs.ShowError("Unexpected error", ex.Message);
    }
}
