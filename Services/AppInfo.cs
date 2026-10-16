using System.Reflection;

namespace SmartMacro.Services;

/// <summary>Name, version and credits shown in the app.</summary>
public static class AppInfo
{
    public const string Name = "SmartMacro";
    public const string Owner = "Riku";
    public const string Tagline = "Desktop automation with recording, a workflow editor and text recognition (OCR).";

    /// <summary>e.g. "1.0.0" (from the project's Version setting).</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        return version is null ? "?" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
