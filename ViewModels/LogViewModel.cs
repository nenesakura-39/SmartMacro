using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using SmartMacro.Core.Logging;
using SmartMacro.Mvvm;

namespace SmartMacro.ViewModels;

/// <summary>The colour-coded pill a log line gets in the Logs view.</summary>
public enum LogCategory
{
    Info,
    Match,
    Done,
    Warn,
    Error,
}

/// <summary>Shows log entries (newest first). Entries may arrive from any thread.</summary>
public sealed class LogViewModel : ObservableObject, IDisposable
{
    private const int MaxEntries = 1_000;

    private readonly ILogService _log;
    private readonly Dispatcher _dispatcher;

    public LogViewModel(ILogService log, Dispatcher dispatcher)
    {
        _log = log;
        _dispatcher = dispatcher;

        foreach (var entry in log.GetRecent().Reverse().Take(MaxEntries))
            Entries.Add(entry);

        _log.EntryAdded += OnEntryAdded;

        EntriesView = new System.Windows.Data.ListCollectionView(Entries) { Filter = Matches };

        ClearCommand = new RelayCommand(() => { Entries.Clear(); RaiseCounts(); });
        CopyCommand = new RelayCommand(CopyAll, () => Entries.Count > 0);
        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);
        OpenFolderCommand = new RelayCommand(OpenFolder, () => _log.LogDirectory is not null);
    }

    public ObservableCollection<LogEntry> Entries { get; } = new();

    public bool IsEmpty => Entries.Count == 0;

    /// <summary>The entries the Logs page shows (search text + level filter applied).</summary>
    public ICollectionView EntriesView { get; }

    public RelayCommand ClearSearchCommand { get; }

    private string _searchText = string.Empty;
    private string _levelFilter = "All";

    /// <summary>Only lines containing this text (case doesn't matter).</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? string.Empty)) return;
            EntriesView.Refresh();
            RaiseCounts();
        }
    }

    /// <summary>"All", "Info", "Match", "Warn" or "Error" (the filter pills).</summary>
    public string LevelFilter
    {
        get => _levelFilter;
        set
        {
            if (!SetProperty(ref _levelFilter, string.IsNullOrEmpty(value) ? "All" : value)) return;
            EntriesView.Refresh();
            RaiseCounts();
        }
    }

    /// <summary>The level filter choices, for the segmented pills.</summary>
    public IReadOnlyList<string> LevelFilters { get; } = new[] { "All", "Info", "Match", "Warn", "Error" };

    public bool IsFilteredEmpty => Entries.Count > 0 && EntriesView.IsEmpty;

    public string CountText => string.IsNullOrWhiteSpace(_searchText) && _levelFilter == "All"
        ? $"{Entries.Count:N0} entries"
        : $"{EntriesView.Cast<object>().Count():N0} of {Entries.Count:N0} entries";

    /// <summary>
    /// Which pill a line gets: errors and warnings by level; successes (saved, finished) as DONE; info lines about
    /// something being found / matched / a condition answer as MATCH; everything else INFO.
    /// </summary>
    public static LogCategory CategoryOf(LogEntry entry) => entry.Level switch
    {
        AppLogLevel.Error => LogCategory.Error,
        AppLogLevel.Warning => LogCategory.Warn,
        AppLogLevel.Success => LogCategory.Done,
        _ when IsMatchMessage(entry.Message) => LogCategory.Match,
        _ => LogCategory.Info,
    };

    private static bool IsMatchMessage(string message) =>
        message.StartsWith("Found", StringComparison.OrdinalIgnoreCase)
        || message.StartsWith("Matched", StringComparison.OrdinalIgnoreCase)
        || message.StartsWith("If (step", StringComparison.Ordinal)
        || message.Contains(" is no longer on screen", StringComparison.Ordinal)
        || message.Contains("% match", StringComparison.OrdinalIgnoreCase);

    private bool Matches(object item)
    {
        if (item is not LogEntry entry) return false;
        if (_levelFilter != "All")
        {
            var category = CategoryOf(entry);
            var wanted = _levelFilter switch
            {
                "Info" => category is LogCategory.Info or LogCategory.Done,
                "Match" => category == LogCategory.Match,
                "Warn" => category == LogCategory.Warn,
                "Error" => category == LogCategory.Error,
                _ => true,
            };
            if (!wanted) return false;
        }

        var term = _searchText.Trim();
        return term.Length == 0
               || entry.Message.Contains(term, StringComparison.CurrentCultureIgnoreCase)
               || (entry.Details?.Contains(term, StringComparison.CurrentCultureIgnoreCase) ?? false);
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsFilteredEmpty));
        OnPropertyChanged(nameof(CountText));
    }

    public RelayCommand ClearCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand OpenFolderCommand { get; }

    public void Dispose() => _log.EntryAdded -= OnEntryAdded;

    private void OnEntryAdded(object? sender, LogEntry entry)
    {
        if (entry.Level == AppLogLevel.Debug) return;

        if (_dispatcher.CheckAccess()) Add(entry);
        else _dispatcher.InvokeAsync(() => Add(entry));
    }

    private void Add(LogEntry entry)
    {
        Entries.Insert(0, entry);
        while (Entries.Count > MaxEntries) Entries.RemoveAt(Entries.Count - 1);
        RaiseCounts();
    }

    private void CopyAll()
    {
        var text = new StringBuilder();
        foreach (var e in EntriesView.Cast<LogEntry>().Reverse())
        {
            text.Append(e.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")).Append(" [").Append(e.Level).Append("] ").Append(e.Message);
            if (e.Details is not null) text.Append(" (").Append(e.Details).Append(')');
            text.AppendLine();
        }

        try
        {
            Clipboard.SetText(text.ToString());
            _log.Info("Log copied to the clipboard.");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException)
        {
            _log.Warning("The clipboard is busy; try copying again.", ex);
        }
    }

    private void OpenFolder()
    {
        if (_log.LogDirectory is null) return;
        try
        {
            Directory.CreateDirectory(_log.LogDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_log.LogDirectory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Error("Could not open the log folder.", ex);
        }
    }
}
