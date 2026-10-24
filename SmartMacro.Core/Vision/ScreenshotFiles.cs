using System.Globalization;
using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;

namespace SmartMacro.Core.Vision;

/// <summary>
/// Where a Screenshot step's picture comes from and where it is saved: the capture rectangle, the default folder
/// (Pictures\SmartMacro Screenshots) and a safe, unique, sortable file name ("SmartMacro_20261024_180511_042.png").
/// </summary>
public static class ScreenshotFiles
{
    public const string DefaultPrefix = "SmartMacro";
    public const string DefaultFolderName = "SmartMacro Screenshots";
    public const int MaxPrefixLength = 60;

    // Characters Windows doesn't allow in a file name (fixed list, so it's the same on every OS the tests run on).
    private static readonly char[] InvalidFileNameChars = { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

    /// <summary>Pictures\SmartMacro Screenshots (the user folder when there is no Pictures folder).</summary>
    public static string DefaultFolder
    {
        get
        {
            var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (string.IsNullOrEmpty(pictures)) pictures = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(pictures, DefaultFolderName);
        }
    }

    /// <summary>The folder a step saves into.</summary>
    public static string FolderOf(ScreenshotStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return string.IsNullOrWhiteSpace(step.FolderPath) ? DefaultFolder : step.FolderPath.Trim();
    }

    /// <summary>True when the text has a character a file name can't contain (or a control character).</summary>
    public static bool HasInvalidFileNameChars(string? text) =>
        text is not null && text.Any(c => c < ' ' || Array.IndexOf(InvalidFileNameChars, c) >= 0);

    /// <summary>The prefix with characters a file name can't contain replaced by "_" ("SmartMacro" when empty).</summary>
    public static string CleanPrefix(string? prefix)
    {
        var text = (prefix ?? string.Empty).Trim();
        if (text.Length > MaxPrefixLength) text = text[..MaxPrefixLength];
        var chars = text.Select(c => c < ' ' || Array.IndexOf(InvalidFileNameChars, c) >= 0 ? '_' : c).ToArray();
        var clean = new string(chars).Trim().TrimEnd('.');
        return clean.Length == 0 ? DefaultPrefix : clean;
    }

    /// <summary>"SmartMacro_20261024_180511_042.png" (sorts by time in Explorer).</summary>
    public static string FileName(string? prefix, DateTime time) =>
        $"{CleanPrefix(prefix)}_{time.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture)}.png";

    /// <summary>
    /// Saves the picture as a PNG in the folder (created when missing) and returns the full path. Never overwrites a
    /// file: "_2", "_3"… is added when two screenshots land in the same millisecond.
    /// </summary>
    public static string Save(BgraImage image, string folder, string? prefix, DateTime time)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        Directory.CreateDirectory(folder);
        var name = FileName(prefix, time);
        var path = Path.Combine(folder, name);
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)}_{n}.png");

        var png = PngCodec.Encode(image);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.Write(png, 0, png.Length);
        return path;
    }

    /// <summary>
    /// The screen rectangle to capture (always inside <paramref name="virtualScreen"/>). Throws
    /// <see cref="SearchAreaUnavailableException"/> when the window isn't open / is minimized or the area is off screen.
    /// </summary>
    public static ScreenRect ResolveArea(ScreenshotStep step, ScreenRect virtualScreen, IScreenLayout? layout)
    {
        ArgumentNullException.ThrowIfNull(step);
        switch (step.Area)
        {
            case ScreenshotArea.AllMonitors:
                return virtualScreen;

            case ScreenshotArea.CustomArea:
                return Clip(step.Region, virtualScreen, "The screenshot area");

            case ScreenshotArea.Window:
            {
                var title = step.WindowTitle?.Trim();
                if (string.IsNullOrEmpty(title))
                    throw new SearchAreaUnavailableException("No app window is chosen for the screenshot.");
                if (layout is null)
                    throw new SearchAreaUnavailableException($"The window \"{title}\" can't be found (the window list isn't available).");
                var window = SearchArea.FindWindow(layout.GetWindows(), title)
                             ?? throw new SearchAreaUnavailableException($"The window \"{title}\" isn't open.");
                if (window.IsMinimized || window.Bounds.IsEmpty)
                    throw new SearchAreaUnavailableException($"The window \"{window.Title}\" is minimized.");
                return Clip(window.Bounds, virtualScreen, $"The window \"{window.Title}\"");
            }

            default:
            {
                if (layout is null) return virtualScreen;
                var monitors = layout.GetMonitors();
                var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
                return primary is null ? virtualScreen : Clip(primary.Bounds, virtualScreen, "The main monitor");
            }
        }
    }

    private static ScreenRect Clip(ScreenRect area, ScreenRect virtualScreen, string what)
    {
        if (virtualScreen.IsEmpty) return area;
        var clipped = area.Intersect(virtualScreen);
        if (clipped.IsEmpty) throw new SearchAreaUnavailableException($"{what} is outside the visible screen.");
        return clipped;
    }
}
