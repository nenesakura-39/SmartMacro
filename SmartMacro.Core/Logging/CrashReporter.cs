using System.Runtime.InteropServices;
using System.Text;

namespace SmartMacro.Core.Logging;

/// <summary>
/// Writes structured local crash reports under <c>Logs/Crashes</c> when an unhandled exception occurs,
/// retains the most recent <see cref="MaxRetainedReports"/> files, and provides a summary for "Copy diagnostics".
/// Never uploads anything over the network.
/// </summary>
public static class CrashReporter
{
    public const int MaxRetainedReports = 10;
    public const string CrashesSubfolder = "Crashes";

    public static string? WriteReport(
        string? logsDirectory,
        string source,
        Exception exception,
        string appVersion = "1.0.0",
        IReadOnlyList<LogEntry>? recentLogs = null)
    {
        if (string.IsNullOrWhiteSpace(logsDirectory) || exception is null)
            return null;

        try
        {
            var crashDir = Path.Combine(logsDirectory, CrashesSubfolder);
            Directory.CreateDirectory(crashDir);

            var now = DateTimeOffset.Now;
            var fileName = $"crash-{now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..6]}.log";
            var filePath = Path.Combine(crashDir, fileName);

            var sb = new StringBuilder();
            sb.AppendLine($"SmartMacro Crash Report ({appVersion})");
            sb.AppendLine($"Timestamp: {now:yyyy-MM-dd HH:mm:ss.fff zzz}");
            sb.AppendLine($"Source: {source}");
            sb.AppendLine($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
            sb.AppendLine($".NET: {Environment.Version} (64-bit: {Environment.Is64BitProcess})");
            sb.AppendLine($"WorkingSetMB: {Environment.WorkingSet / (1024 * 1024)}");
            sb.AppendLine();
            sb.AppendLine("=== Exception ===");
            sb.AppendLine(exception.ToString());

            if (recentLogs is { Count: > 0 })
            {
                sb.AppendLine();
                sb.AppendLine("=== Recent Log Tail ===");
                foreach (var entry in recentLogs.TakeLast(25))
                {
                    sb.Append(entry.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff"))
                      .Append(" [").Append(entry.Level).Append("] ")
                      .Append(entry.Message);
                    if (!string.IsNullOrEmpty(entry.Details))
                        sb.Append(" (").Append(entry.Details).Append(')');
                    sb.AppendLine();
                }
            }

            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
            PruneOldReports(crashDir);
            return filePath;
        }
        catch
        {
            // Never throw inside an unhandled-exception handler.
            return null;
        }
    }

    public static string? GetLatestCrashSummary(string? logsDirectory, int maxLines = 20)
    {
        if (string.IsNullOrWhiteSpace(logsDirectory)) return null;
        try
        {
            var crashDir = Path.Combine(logsDirectory, CrashesSubfolder);
            if (!Directory.Exists(crashDir)) return null;

            var latest = new DirectoryInfo(crashDir)
                .GetFiles("crash-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (latest is null) return null;

            var lines = File.ReadAllLines(latest.FullName, Encoding.UTF8).Take(maxLines);
            return $"Latest crash ({latest.Name}):{Environment.NewLine}" + string.Join(Environment.NewLine, lines);
        }
        catch
        {
            return null;
        }
    }

    private static void PruneOldReports(string crashDir)
    {
        try
        {
            var files = new DirectoryInfo(crashDir)
                .GetFiles("crash-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(MaxRetainedReports)
                .ToList();
            foreach (var old in files)
            {
                try { old.Delete(); }
                catch { /* ignore locked file */ }
            }
        }
        catch
        {
            // Ignore cleanup errors.
        }
    }
}
