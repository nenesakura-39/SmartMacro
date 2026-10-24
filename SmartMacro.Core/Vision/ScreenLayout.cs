using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;

namespace SmartMacro.Core.Vision;

/// <summary>
/// "Where to look" for Find text / image / color steps and conditions. Searching less of the desktop is faster and
/// can't match something on another monitor (a chat app, a browser) by mistake.
/// </summary>
public enum SearchScope
{
    /// <summary>Every monitor (the whole virtual desktop). What "Search the whole screen" meant in older files.</summary>
    AllMonitors,

    /// <summary>Only the main (primary) monitor. The default for new steps.</summary>
    PrimaryMonitor,

    /// <summary>One chosen monitor (<see cref="ScreenSearchStep.MonitorNumber"/>).</summary>
    Monitor,

    /// <summary>Inside one app's window, wherever it is right now (<see cref="ScreenSearchStep.WindowTitle"/>).</summary>
    Window,

    /// <summary>A rectangle picked on screen (RegionX / RegionY / RegionWidth / RegionHeight).</summary>
    CustomArea,
}

/// <summary>
/// One connected display. Number 1 is the main monitor, the others follow left to right (then top to bottom).
/// Bounds are physical pixels in virtual-screen coordinates (the same ones mouse steps use).
/// </summary>
public sealed record MonitorInfo(int Number, ScreenRect Bounds, bool IsPrimary, string DeviceName = "")
{
    /// <summary>E.g. "Monitor 1 — Primary (1920×1080)" or "Monitor 2 (2560×1440)".</summary>
    public string Label => IsPrimary
        ? $"Monitor {Number} — Primary ({Bounds.Width}×{Bounds.Height})"
        : $"Monitor {Number} ({Bounds.Width}×{Bounds.Height})";
}

/// <summary>A visible top-level app window. Bounds are physical pixels; minimized windows have no usable bounds.</summary>
public sealed record WindowInfo(long Handle, string Title, ScreenRect Bounds, string ProcessName = "", bool IsMinimized = false)
{
    /// <summary>E.g. "Window: Roblox".</summary>
    public string Label => $"Window: {Title}";
}

/// <summary>
/// Tells the engine and the editor which monitors and windows exist right now. The real one (WPF project) asks
/// Windows; tests use a fake. Every call returns fresh values (windows move, monitors get plugged in).
/// </summary>
public interface IScreenLayout
{
    /// <summary>All connected monitors, numbered (1 = main monitor).</summary>
    IReadOnlyList<MonitorInfo> GetMonitors();

    /// <summary>Visible top-level windows with a title (not SmartMacro's own), front to back.</summary>
    IReadOnlyList<WindowInfo> GetWindows();
}

/// <summary>
/// Brings an app window to the front (the "Focus window" step). The real screen layout (WPF project) implements it next to
/// <see cref="IScreenLayout"/>; without it the step can find the window but not focus it.
/// </summary>
public interface IWindowFocuser
{
    /// <summary>
    /// Restores the window when it's minimized (if asked) and makes it the foreground window. Returns true when it is the
    /// foreground window afterwards (Windows can refuse, e.g. while another app is in a full-screen exclusive mode).
    /// </summary>
    bool TryFocus(WindowInfo window, bool restoreIfMinimized);
}

/// <summary>Thrown when a step's "where to look" can't be used right now (the window isn't open, no such monitor…).</summary>
public sealed class SearchAreaUnavailableException : Exception
{
    public SearchAreaUnavailableException(string message) : base(message)
    {
    }
}

/// <summary>Works out the rectangle a search step looks at, and describes it in plain English.</summary>
public static class SearchArea
{
    /// <summary>Longest window title that can be stored.</summary>
    public const int MaxWindowTitleLength = 260;

    /// <summary>Highest monitor number a step can name.</summary>
    public const int MaxMonitorNumber = 16;

    /// <summary>
    /// The screen rectangle to search (always inside <paramref name="virtualScreen"/>).
    /// Without a layout (tests, or the layout isn't available) the main monitor falls back to the whole desktop.
    /// Throws <see cref="SearchAreaUnavailableException"/> when the chosen monitor / window isn't there right now.
    /// </summary>
    public static ScreenRect Resolve(ScreenSearchStep step, ScreenRect virtualScreen, IScreenLayout? layout)
    {
        ArgumentNullException.ThrowIfNull(step);
        switch (step.EffectiveScope)
        {
            case SearchScope.CustomArea:
                return step.Region;

            case SearchScope.PrimaryMonitor:
            {
                if (layout is null) return virtualScreen;
                var monitors = layout.GetMonitors();
                var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
                return primary is null ? virtualScreen : Clip(primary.Bounds, virtualScreen, "The main monitor");
            }

            case SearchScope.Monitor:
            {
                if (layout is null)
                    throw new SearchAreaUnavailableException($"Monitor {step.MonitorNumber} can't be found (the monitor list isn't available).");
                var monitors = layout.GetMonitors();
                var monitor = FindMonitor(monitors, step.MonitorNumber)
                              ?? throw new SearchAreaUnavailableException(
                                  $"Monitor {step.MonitorNumber} isn't connected ({monitors.Count} monitor{(monitors.Count == 1 ? " is" : "s are")} connected). Choose another monitor.");
                return Clip(monitor.Bounds, virtualScreen, $"Monitor {step.MonitorNumber}");
            }

            case SearchScope.Window:
            {
                var title = step.WindowTitle?.Trim();
                if (string.IsNullOrEmpty(title))
                    throw new SearchAreaUnavailableException("No app window is chosen. Pick the window to search in.");
                if (layout is null)
                    throw new SearchAreaUnavailableException($"The window \"{title}\" can't be found (the window list isn't available).");
                var window = FindWindow(layout.GetWindows(), title)
                             ?? throw new SearchAreaUnavailableException($"The window \"{title}\" isn't open.");
                if (window.IsMinimized || window.Bounds.IsEmpty)
                    throw new SearchAreaUnavailableException($"The window \"{window.Title}\" is minimized.");
                return Clip(window.Bounds, virtualScreen, $"The window \"{window.Title}\"");
            }

            default:
                return virtualScreen;
        }
    }

    /// <summary>The monitor with this number, or null.</summary>
    public static MonitorInfo? FindMonitor(IEnumerable<MonitorInfo> monitors, int number) =>
        monitors.FirstOrDefault(m => m.Number == number);

    /// <summary>
    /// The window whose title matches: an exact match (ignoring case) wins, otherwise the first (front-most) window whose
    /// title contains the text. Minimized windows are only used when nothing else matches.
    /// </summary>
    public static WindowInfo? FindWindow(IEnumerable<WindowInfo> windows, string title)
    {
        ArgumentNullException.ThrowIfNull(windows);
        if (string.IsNullOrWhiteSpace(title)) return null;
        var wanted = title.Trim();
        var list = windows.Where(w => !string.IsNullOrEmpty(w.Title)).ToList();

        static int Rank(WindowInfo w, string wanted) =>
            string.Equals(w.Title.Trim(), wanted, StringComparison.OrdinalIgnoreCase) ? 0
            : w.Title.Contains(wanted, StringComparison.OrdinalIgnoreCase) ? 1
            : 2;

        return list
            .Select((w, i) => (Window: w, Rank: Rank(w, wanted), Order: i))
            .Where(x => x.Rank < 2)
            .OrderBy(x => x.Window.IsMinimized ? 1 : 0)
            .ThenBy(x => x.Rank)
            .ThenBy(x => x.Order)
            .Select(x => x.Window)
            .FirstOrDefault();
    }

    /// <summary>
    /// Numbers monitors the way the editor shows them: the main monitor is 1, the others follow left to right, then
    /// top to bottom. Used by the real layout provider (and tests).
    /// </summary>
    public static IReadOnlyList<MonitorInfo> NumberMonitors(IEnumerable<(ScreenRect Bounds, bool IsPrimary, string DeviceName)> monitors) =>
        monitors
            .OrderBy(m => m.IsPrimary ? 0 : 1)
            .ThenBy(m => m.Bounds.X)
            .ThenBy(m => m.Bounds.Y)
            .Select((m, i) => new MonitorInfo(i + 1, m.Bounds, m.IsPrimary, m.DeviceName))
            .ToList();

    /// <summary>E.g. "on screen", "on the main monitor", "on monitor 2", "in window \"Roblox\"", "in area (…)".</summary>
    public static string Describe(ScreenSearchStep step) => step.EffectiveScope switch
    {
        SearchScope.PrimaryMonitor => "on the main monitor",
        SearchScope.Monitor => $"on monitor {step.MonitorNumber}",
        SearchScope.Window => string.IsNullOrWhiteSpace(step.WindowTitle) ? "in an app window (not chosen yet)" : $"in window \"{step.WindowTitle!.Trim()}\"",
        SearchScope.CustomArea => $"in area ({step.RegionX}, {step.RegionY}, {step.RegionWidth}×{step.RegionHeight})",
        _ => "on screen",
    };

    /// <summary>Short name for sentences, e.g. "Main Monitor", "Monitor 2", "the Roblox window", "all monitors".</summary>
    public static string ShortName(ScreenSearchStep step) => step.EffectiveScope switch
    {
        SearchScope.PrimaryMonitor => "Main Monitor",
        SearchScope.Monitor => $"Monitor {step.MonitorNumber}",
        SearchScope.Window => string.IsNullOrWhiteSpace(step.WindowTitle) ? "an app window (not chosen yet)" : $"the \"{step.WindowTitle!.Trim()}\" window",
        SearchScope.CustomArea => $"the area ({step.RegionX}, {step.RegionY}, {step.RegionWidth}×{step.RegionHeight})",
        _ => "all monitors",
    };

    /// <summary>True when the scope covers a whole monitor or more (big areas: slower, and colours match more by mistake).</summary>
    public static bool IsWholeMonitorOrMore(ScreenSearchStep step) =>
        step.EffectiveScope is SearchScope.AllMonitors or SearchScope.PrimaryMonitor or SearchScope.Monitor;

    private static ScreenRect Clip(ScreenRect area, ScreenRect virtualScreen, string what)
    {
        if (virtualScreen.IsEmpty) return area;
        var clipped = area.Intersect(virtualScreen);
        if (clipped.IsEmpty) throw new SearchAreaUnavailableException($"{what} is outside the visible screen.");
        return clipped;
    }
}
