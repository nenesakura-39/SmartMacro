using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using SmartMacro.Core.Logging;
using SmartMacro.Mvvm;

namespace SmartMacro.ViewModels;

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

        ClearCommand = new RelayCommand(() => { Entries.Clear(); OnPropertyChanged(nameof(IsEmpty)); });
        CopyCommand = new RelayCommand(CopyAll, () => Entries.Count > 0);
        OpenFolderCommand = new RelayCommand(OpenFolder, () => _log.LogDirectory is not null);
    }

    public ObservableCollection<LogEntry> Entries { get; } = new();

    public bool IsEmpty => Entries.Count == 0;

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
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void CopyAll()
    {
        var text = new StringBuilder();
        foreach (var e in Entries.Reverse())
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
