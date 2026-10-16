using System.Text;

namespace SmartMacro.Core.Logging;

public enum AppLogLevel
{
    Debug,
    Info,
    Success,
    Warning,
    Error,
}

public sealed record LogEntry(DateTimeOffset Timestamp, AppLogLevel Level, string Message, string? Details = null)
{
    public string TimeText => Timestamp.ToLocalTime().ToString("HH:mm:ss");
}

public interface ILogService
{
    /// <summary>Raised on whatever thread logged the entry. UI code must marshal to the dispatcher.</summary>
    event EventHandler<LogEntry>? EntryAdded;

    void Log(AppLogLevel level, string message, Exception? exception = null);

    IReadOnlyList<LogEntry> GetRecent();

    string? LogDirectory { get; }
}

public static class LogServiceExtensions
{
    public static void Info(this ILogService log, string message) => log.Log(AppLogLevel.Info, message);
    public static void Success(this ILogService log, string message) => log.Log(AppLogLevel.Success, message);
    public static void Warning(this ILogService log, string message, Exception? ex = null) => log.Log(AppLogLevel.Warning, message, ex);
    public static void Error(this ILogService log, string message, Exception? ex = null) => log.Log(AppLogLevel.Error, message, ex);
}

/// <summary>
/// Keeps recent entries in memory (for the UI) and appends them to a daily log file.
/// Logging must never crash the app, so file errors are reported once and then ignored.
/// </summary>
public sealed class LogService : ILogService
{
    public const int MaxInMemoryEntries = 2_000;

    private readonly object _gate = new();
    private readonly LinkedList<LogEntry> _recent = new();
    private bool _fileWriteFailed;

    public LogService(string? logDirectory)
    {
        LogDirectory = logDirectory;
    }

    public event EventHandler<LogEntry>? EntryAdded;

    public string? LogDirectory { get; }

    public void Log(AppLogLevel level, string message, Exception? exception = null)
    {
        var details = exception is null ? null : $"{exception.GetType().Name}: {exception.Message}";
        var entry = new LogEntry(DateTimeOffset.Now, level, message, details);

        lock (_gate)
        {
            _recent.AddLast(entry);
            while (_recent.Count > MaxInMemoryEntries) _recent.RemoveFirst();
            WriteToFile(entry, exception);
        }

        EntryAdded?.Invoke(this, entry);
    }

    public IReadOnlyList<LogEntry> GetRecent()
    {
        lock (_gate) return _recent.ToList();
    }

    private void WriteToFile(LogEntry entry, Exception? exception)
    {
        if (LogDirectory is null || _fileWriteFailed) return;

        try
        {
            Directory.CreateDirectory(LogDirectory);
            var file = Path.Combine(LogDirectory, $"smartmacro-{entry.Timestamp:yyyyMMdd}.log");
            var line = new StringBuilder()
                .Append(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                .Append(" [").Append(entry.Level.ToString().ToUpperInvariant()).Append("] ")
                .Append(entry.Message);
            if (exception is not null)
                line.AppendLine().Append("    ").Append(exception.ToString().Replace(Environment.NewLine, Environment.NewLine + "    "));
            line.AppendLine();
            File.AppendAllText(file, line.ToString(), Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Don't recurse into Log(); just remember and surface it in memory once.
            _fileWriteFailed = true;
            _recent.AddLast(new LogEntry(DateTimeOffset.Now, AppLogLevel.Warning,
                "Could not write to the log file. Logging continues in this window only.", ex.Message));
        }
    }
}
