using SmartMacro.Core.Playback;

namespace SmartMacro.Core.Storage;

/// <summary>Primary action parsed from SmartMacro command-line arguments.</summary>
public enum CliActionKind
{
    Activate,
    RunMacro,
    Stop,
    ArmTriggers,
    DisarmTriggers,
    Help,
}

/// <summary>Structured process exit codes returned by SmartMacro CLI invocations.</summary>
public static class CliExitCodes
{
    public const int Success = 0;
    public const int InvalidOrNotFound = 1;
    public const int PlaybackFailedOrStopped = 2;
    public const int BusyOrIpcError = 3;
}

/// <summary>Serializable CLI command forwarded from a secondary launch to the running primary instance.</summary>
public sealed record CliCommandRequest
{
    public CliActionKind Action { get; init; } = CliActionKind.Activate;
    public string MacroTarget { get; init; } = string.Empty;
    public bool StartInTray { get; init; }
    public bool NoFocus { get; init; }
    public bool ActivateWindow { get; init; }
    public bool WaitForCompletion { get; init; }
    public bool ExitAfterRun { get; init; }
    public string? ParseError { get; init; }

    /// <summary>True when the caller wants the secondary process to wait for the macro run to finish.</summary>
    public bool EffectiveWait => WaitForCompletion || ExitAfterRun;

    /// <summary>True when launching primary should display and focus the Studio window.</summary>
    public bool ShouldShowMainWindowOnStartup =>
        ActivateWindow || (!StartInTray && !(NoFocus && Action == CliActionKind.RunMacro));
}

/// <summary>Response returned over single-instance IPC after handling a <see cref="CliCommandRequest"/>.</summary>
public sealed record CliCommandResponse(
    int ExitCode,
    string Status,
    string Message,
    PlaybackOutcome? PlaybackOutcome = null);

/// <summary>
/// Parses command-line invocations (<c>--run</c>, <c>--stop</c>, <c>--arm</c>, <c>--disarm</c>,
/// <c>--tray</c>, <c>--no-focus</c>, <c>--wait</c>, <c>--activate</c>, <c>--exit-after-run</c>, <c>--help</c>)
/// and generates copy-ready CLI commands for Task Scheduler, Stream Deck, and shortcuts.
/// </summary>
public static class CliCommandLine
{
    private static readonly HashSet<string> KnownSwitches = new(StringComparer.OrdinalIgnoreCase)
    {
        "--run", "--stop", "--arm", "--disarm",
        "--tray", "--minimized", "--no-focus", "--wait",
        "--activate", "--show", "--exit-after-run",
        "--help", "-h", "/?",
    };

    public static CliCommandRequest Parse(IReadOnlyList<string>? args)
    {
        var action = CliActionKind.Activate;
        var hasExplicitAction = false;
        var macroTarget = string.Empty;
        var startInTray = false;
        var noFocus = false;
        var activateWindow = false;
        var waitForCompletion = false;
        var exitAfterRun = false;
        string? parseError = null;

        if (args is not { Count: > 0 })
            return new CliCommandRequest();

        for (var i = 0; i < args.Count; i++)
        {
            var raw = args[i]?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(raw)) continue;

            if (EqualsSwitch(raw, "--help") || EqualsSwitch(raw, "-h") || EqualsSwitch(raw, "/?"))
            {
                return new CliCommandRequest { Action = CliActionKind.Help };
            }

            if (EqualsSwitch(raw, "--tray") || EqualsSwitch(raw, "--minimized"))
            {
                startInTray = true;
                continue;
            }

            if (EqualsSwitch(raw, "--no-focus"))
            {
                noFocus = true;
                continue;
            }

            if (EqualsSwitch(raw, "--activate") || EqualsSwitch(raw, "--show"))
            {
                activateWindow = true;
                continue;
            }

            if (EqualsSwitch(raw, "--wait"))
            {
                waitForCompletion = true;
                continue;
            }

            if (EqualsSwitch(raw, "--exit-after-run"))
            {
                exitAfterRun = true;
                continue;
            }

            if (EqualsSwitch(raw, "--stop"))
            {
                TrySetAction(CliActionKind.Stop);
                continue;
            }

            if (EqualsSwitch(raw, "--arm"))
            {
                TrySetAction(CliActionKind.ArmTriggers);
                continue;
            }

            if (EqualsSwitch(raw, "--disarm"))
            {
                TrySetAction(CliActionKind.DisarmTriggers);
                continue;
            }

            if (EqualsSwitch(raw, "--run"))
            {
                TrySetAction(CliActionKind.RunMacro);
                if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1]) || IsKnownSwitch(args[i + 1]))
                {
                    parseError ??= "Missing macro name, ID, or .smacro file path after --run.";
                    continue;
                }

                macroTarget = Unquote(args[++i]);
                if (string.IsNullOrWhiteSpace(macroTarget))
                    parseError ??= "Missing macro name, ID, or .smacro file path after --run.";
                continue;
            }

            if (raw.StartsWith("--run=", StringComparison.OrdinalIgnoreCase))
            {
                TrySetAction(CliActionKind.RunMacro);
                macroTarget = Unquote(raw["--run=".Length..]);
                if (string.IsNullOrWhiteSpace(macroTarget))
                    parseError ??= "Missing macro name, ID, or .smacro file path after --run=.";
                continue;
            }

            if (raw.StartsWith("--", StringComparison.Ordinal))
            {
                parseError ??= $"Unknown CLI option \"{raw}\". Use --help to list supported options.";
            }
        }

        if (parseError is null && action == CliActionKind.RunMacro && string.IsNullOrWhiteSpace(macroTarget))
            parseError = "Missing macro name, ID, or .smacro file path after --run.";

        return new CliCommandRequest
        {
            Action = action,
            MacroTarget = macroTarget,
            StartInTray = startInTray,
            NoFocus = noFocus,
            ActivateWindow = activateWindow,
            WaitForCompletion = waitForCompletion,
            ExitAfterRun = exitAfterRun,
            ParseError = parseError,
        };

        void TrySetAction(CliActionKind next)
        {
            if (hasExplicitAction && action != next)
            {
                parseError ??= "Specify only one CLI action (--run, --stop, --arm, or --disarm) per invocation.";
                return;
            }

            action = next;
            hasExplicitAction = true;
        }
    }

    /// <summary>
    /// Formats a ready-to-paste command line for Task Scheduler, Stream Deck, or desktop shortcuts.
    /// </summary>
    public static string FormatRunCommand(
        string? executablePath,
        string? macroTarget,
        bool noFocus = true,
        bool wait = false,
        bool exitAfterRun = false)
    {
        var exe = string.IsNullOrWhiteSpace(executablePath) ? "SmartMacro.exe" : executablePath.Trim();
        var target = string.IsNullOrWhiteSpace(macroTarget) ? "My Macro" : macroTarget.Trim();

        var parts = new List<string>
        {
            QuoteForCommandLine(exe),
            "--run",
            QuoteForCommandLine(target),
        };

        if (noFocus) parts.Add("--no-focus");
        if (wait) parts.Add("--wait");
        if (exitAfterRun) parts.Add("--exit-after-run");

        return string.Join(" ", parts);
    }

    public static string BuildHelpText(string? executableName = null)
    {
        var exe = string.IsNullOrWhiteSpace(executableName) ? "SmartMacro.exe" : executableName.Trim();
        return string.Join(Environment.NewLine, new[]
        {
            "SmartMacro Studio CLI Options",
            string.Empty,
            "Usage:",
            $"  {exe} [--tray] [--run <Name|Id|Path>] [options]",
            string.Empty,
            "Actions:",
            "  --run <Name|Id|Path>   Run a saved macro by name, GUID, or .smacro file path.",
            "  --stop                 Stop active recording or playback in the running instance.",
            "  --arm                  Arm per-macro triggers (hotkeys, schedules, window triggers).",
            "  --disarm               Disarm per-macro triggers in the running instance.",
            "  --activate, --show     Bring the running SmartMacro Studio window to the front.",
            "  --help, -h, /?         Show this help summary.",
            string.Empty,
            "Modifiers:",
            "  --tray, --minimized    Start SmartMacro minimized in the system tray.",
            "  --no-focus             Run headless/in-tray without stealing focus or showing dialogs.",
            "  --wait                 Wait for --run completion before exiting with playback status.",
            "  --exit-after-run       Exit SmartMacro after --run completes.",
            string.Empty,
            "Exit Codes:",
            $"  {CliExitCodes.Success}  Completed / Dispatched",
            $"  {CliExitCodes.InvalidOrNotFound}  Invalid arguments, macro not found, or validation failed",
            $"  {CliExitCodes.PlaybackFailedOrStopped}  Playback stopped or failed",
            $"  {CliExitCodes.BusyOrIpcError}  Busy (another run active) or IPC connection failed",
        });
    }

    private static bool EqualsSwitch(string candidate, string flag) =>
        string.Equals(candidate, flag, StringComparison.OrdinalIgnoreCase);

    private static bool IsKnownSwitch(string candidate)
    {
        var trimmed = candidate?.Trim() ?? string.Empty;
        return KnownSwitches.Contains(trimmed)
               || trimmed.StartsWith("--run=", StringComparison.OrdinalIgnoreCase);
    }

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
            trimmed = trimmed[1..^1];
        return trimmed.Trim();
    }

    private static string QuoteForCommandLine(string value)
    {
        var escaped = value.Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }
}
