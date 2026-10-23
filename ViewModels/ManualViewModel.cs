using System.Collections.ObjectModel;
using SmartMacro.Core.Help;
using SmartMacro.Core.Workflow;
using SmartMacro.Mvvm;

namespace SmartMacro.ViewModels;

/// <summary>One category pill above the Manual ("All Guides", "🚀 Quick Start", …).</summary>
public sealed class ManualCategoryPill : ObservableObject
{
    private bool _isActive;

    public ManualCategoryPill(ManualCategory? value, string label)
    {
        Value = value;
        Label = label;
    }

    /// <summary>The category (null = All Guides).</summary>
    public ManualCategory? Value { get; }

    public string Label { get; }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }
}

/// <summary>
/// The In-App Manual page: AAA Studio Knowledge Base & Recipe Center. Instant search over every guide and the shortcut
/// cheat-sheet, category pills, and Macro Recipe cards whose "➕ Load Example into Editor" inserts the blueprint into
/// the open macro (or a new one) and switches to the Editor. Content: <see cref="ManualCatalog"/> (Core).
/// </summary>
public sealed class ManualViewModel : ObservableObject
{
    private readonly Func<MacroBlueprint, Task> _loadRecipe;
    private readonly Func<(string Record, string Play, string Pause)> _globalKeys;
    private string _searchText = string.Empty;
    private ManualCategory? _category;

    /// <param name="loadRecipe">Inserts a blueprint into the editor (and switches to it).</param>
    /// <param name="globalKeys">The global Record / Play / Pause shortcuts as set in Settings (for the cheat-sheet).</param>
    public ManualViewModel(Func<MacroBlueprint, Task> loadRecipe, Func<(string Record, string Play, string Pause)> globalKeys,
        Action<Exception> onError)
    {
        _loadRecipe = loadRecipe;
        _globalKeys = globalKeys;

        Pills = new[] { new ManualCategoryPill(null, "All Guides") }
            .Concat(Enum.GetValues<ManualCategory>().Select(c => new ManualCategoryPill(c, ManualCatalog.Label(c))))
            .ToList();
        Pills[0].IsActive = true;

        SetCategoryCommand = new RelayCommand(p => Category = (p as ManualCategoryPill)?.Value ?? (p as ManualCategory?));
        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty, () => _searchText.Length > 0);
        LoadRecipeCommand = new AsyncRelayCommand(
            p => p is ManualArticle { Recipe: { } recipe } ? _loadRecipe(recipe) : p is MacroBlueprint blueprint ? _loadRecipe(blueprint) : Task.CompletedTask,
            onError);
        Refresh();
    }

    /// <summary>"🔍 Search guides, steps, hotkeys, or troubleshooting..." – every word must match (any order).</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty)) Refresh();
        }
    }

    /// <summary>The active category pill (null = All Guides).</summary>
    public ManualCategory? Category
    {
        get => _category;
        set
        {
            if (!SetProperty(ref _category, value)) return;
            foreach (var pill in Pills) pill.IsActive = pill.Value == value;
            Refresh();
        }
    }

    public IReadOnlyList<ManualCategoryPill> Pills { get; }

    public ObservableCollection<ManualArticle> Articles { get; } = new();

    public ObservableCollection<ManualShortcut> Shortcuts { get; } = new();

    /// <summary>The cheat-sheet table is shown under "All Guides" / "⌨️ Keyboard Shortcuts" while rows match.</summary>
    public bool ShowShortcutTable => Shortcuts.Count > 0;

    public bool HasNoResults => Articles.Count == 0 && Shortcuts.Count == 0;

    /// <summary>"23 guides · 19 shortcuts" / "2 guides match \"black\"".</summary>
    public string ResultText
    {
        get
        {
            var guides = $"{Articles.Count} guide{(Articles.Count == 1 ? "" : "s")}";
            var keys = Shortcuts.Count > 0 ? $" · {Shortcuts.Count} shortcut{(Shortcuts.Count == 1 ? "" : "s")}" : "";
            return _searchText.Trim().Length > 0 ? $"{guides}{keys} match \"{_searchText.Trim()}\"" : guides + keys;
        }
    }

    public RelayCommand SetCategoryCommand { get; }
    public RelayCommand ClearSearchCommand { get; }

    /// <summary>"➕ Load Example into Editor" (parameter: the recipe article or a <see cref="MacroBlueprint"/>).</summary>
    public AsyncRelayCommand LoadRecipeCommand { get; }

    /// <summary>Re-reads the filter (also called when the page opens, so changed shortcut keys show up).</summary>
    public void Refresh()
    {
        Articles.Clear();
        foreach (var article in ManualCatalog.Filter(_searchText, _category)) Articles.Add(article);

        Shortcuts.Clear();
        if (_category is null or ManualCategory.KeyboardShortcuts)
        {
            var (record, play, pause) = _globalKeys();
            foreach (var row in ManualCatalog.FilterShortcuts(ManualCatalog.Shortcuts(record, play, pause), _searchText)) Shortcuts.Add(row);
        }

        OnPropertyChanged(nameof(ShowShortcutTable));
        OnPropertyChanged(nameof(HasNoResults));
        OnPropertyChanged(nameof(ResultText));
    }
}
