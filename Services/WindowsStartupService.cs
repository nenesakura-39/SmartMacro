using Microsoft.Win32;
using SmartMacro.Core.Logging;

namespace SmartMacro.Services;

/// <summary>
/// Manages the per-user Windows Startup registry entry (<c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>)
/// so SmartMacro can launch minimized to the system tray (<c>--tray</c>) when the user signs into Windows.
/// Requires zero administrator privileges.
/// </summary>
public static class WindowsStartupService
{
    public const string RunRegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "SmartMacroStudio";
    public const string TrayArgument = "--tray";

    public static bool IsStartInTrayCommandLine(IReadOnlyList<string>? args) =>
        args is { Count: > 0 } && args.Any(a =>
            string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase)
            || string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Parses <c>--run &lt;MacroNameOrPath&gt;</c> (or <c>--run=&lt;MacroNameOrPath&gt;</c>) and optional
    /// <c>--exit-after-run</c> command-line arguments for Task Scheduler, Stream Deck, and desktop shortcuts.
    /// </summary>
    public static bool TryParseCliRunRequest(
        IReadOnlyList<string>? args,
        out string macroTarget,
        out bool exitAfterRun) =>
        Core.Storage.AppPaths.TryParseCliRunRequest(args, out macroTarget, out exitAfterRun);

    public static string BuildStartupCommand(string? executablePath = null)
    {
        var exe = string.IsNullOrWhiteSpace(executablePath)
            ? Environment.ProcessPath
            : executablePath.Trim();
        if (string.IsNullOrWhiteSpace(exe)) return string.Empty;
        return $"\"{exe}\" {TrayArgument}";
    }

    public static bool Apply(bool enabled, ILogService? log = null)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunRegistryKeyPath, writable: true);
            if (key is null) return false;

            if (enabled)
            {
                var command = BuildStartupCommand();
                if (string.IsNullOrEmpty(command)) return false;
                key.SetValue(ValueName, command, RegistryValueKind.String);
            }
            else
            {
                if (key.GetValue(ValueName) is not null)
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex)
        {
            log?.Warning("Could not update the Windows Startup setting.", ex);
            return false;
        }
    }
}
