namespace SmartMacro.Core.Storage;

/// <summary>
/// Where SmartMacro keeps its data. By default: %LOCALAPPDATA%\SmartMacro (user-writable, no admin needed).
/// Tests pass their own temp folder instead.
/// </summary>
public sealed class AppPaths
{
    public const string PortableMarkerFileName = "portable.txt";
    public const string PortableDataFolderName = "PortableData";

    public string RootDirectory { get; }
    public string MacrosDirectory { get; }
    public string TrashDirectory { get; }
    public string LogsDirectory { get; }
    public string SettingsFile { get; }
    public bool IsPortable { get; }

    public AppPaths(string rootDirectory, bool isPortable = false)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new ArgumentException("A data folder is required.", nameof(rootDirectory));

        RootDirectory = Path.GetFullPath(rootDirectory);
        MacrosDirectory = Path.Combine(RootDirectory, "Macros");
        TrashDirectory = Path.Combine(RootDirectory, "Trash");
        LogsDirectory = Path.Combine(RootDirectory, "Logs");
        SettingsFile = Path.Combine(RootDirectory, "settings.json");
        IsPortable = isPortable;
    }

    /// <summary>
    /// Resolves the active data folder. When <c>portable.txt</c> or a <c>PortableData</c> folder exists beside the
    /// executable (or inside <paramref name="baseDirectoryOverride"/>), SmartMacro runs in self-contained Portable Mode
    /// and stores all macros, settings, and logs inside <c>PortableData</c>. Otherwise uses <c>%LOCALAPPDATA%\SmartMacro</c>.
    /// </summary>
    public static AppPaths CreateDefault(string? baseDirectoryOverride = null)
    {
        var appDir = string.IsNullOrWhiteSpace(baseDirectoryOverride)
            ? AppContext.BaseDirectory
            : Path.GetFullPath(baseDirectoryOverride);

        if (!string.IsNullOrWhiteSpace(appDir))
        {
            var markerPath = Path.Combine(appDir, PortableMarkerFileName);
            var portableFolder = Path.Combine(appDir, PortableDataFolderName);
            if (File.Exists(markerPath) || Directory.Exists(portableFolder))
            {
                return new AppPaths(portableFolder, isPortable: true);
            }
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localAppData))
            localAppData = Path.GetTempPath();
        return new AppPaths(Path.Combine(localAppData, "SmartMacro"), isPortable: false);
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(MacrosDirectory);
        Directory.CreateDirectory(TrashDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }

    /// <summary>
    /// Parses <c>--run &lt;MacroNameOrPath&gt;</c> (or <c>--run=&lt;MacroNameOrPath&gt;</c>) and optional
    /// <c>--exit-after-run</c> command-line arguments for Task Scheduler, Stream Deck, and desktop shortcuts.
    /// </summary>
    public static bool TryParseCliRunRequest(
        IReadOnlyList<string>? args,
        out string macroTarget,
        out bool exitAfterRun)
    {
        var parsed = CliCommandLine.Parse(args);
        if (parsed.Action == CliActionKind.RunMacro
            && string.IsNullOrEmpty(parsed.ParseError)
            && !string.IsNullOrWhiteSpace(parsed.MacroTarget))
        {
            macroTarget = parsed.MacroTarget;
            exitAfterRun = parsed.ExitAfterRun;
            return true;
        }

        macroTarget = string.Empty;
        exitAfterRun = false;
        return false;
    }
}
