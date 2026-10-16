namespace SmartMacro.Core.Storage;

/// <summary>
/// Where SmartMacro keeps its data. By default: %LOCALAPPDATA%\SmartMacro (user-writable, no admin needed).
/// Tests pass their own temp folder instead.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new ArgumentException("A data folder is required.", nameof(rootDirectory));

        RootDirectory = Path.GetFullPath(rootDirectory);
        MacrosDirectory = Path.Combine(RootDirectory, "Macros");
        TrashDirectory = Path.Combine(RootDirectory, "Trash");
        LogsDirectory = Path.Combine(RootDirectory, "Logs");
        SettingsFile = Path.Combine(RootDirectory, "settings.json");
    }

    public string RootDirectory { get; }
    public string MacrosDirectory { get; }
    public string TrashDirectory { get; }
    public string LogsDirectory { get; }
    public string SettingsFile { get; }

    public static AppPaths CreateDefault()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localAppData))
            localAppData = Path.GetTempPath();
        return new AppPaths(Path.Combine(localAppData, "SmartMacro"));
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(MacrosDirectory);
        Directory.CreateDirectory(TrashDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
