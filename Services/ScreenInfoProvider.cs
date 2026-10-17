using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Vision;
using SmartMacro.Services.Input;

namespace SmartMacro.Services;

public interface IScreenInfoProvider
{
    /// <summary>The rectangle covering all monitors, in physical pixels.</summary>
    ScreenBounds GetVirtualScreen();
}

/// <summary>
/// Reads the screen layout from Windows: the virtual screen, every connected monitor (numbered: 1 = main monitor, then
/// left to right) and the visible top-level app windows with their current rectangles. The app is per-monitor DPI aware
/// (see app.manifest) and every call runs in physical-pixel mode, so these are real pixels — the same coordinates mouse
/// automation and screen capture use. Safe to call from any thread (playback asks on its background thread).
/// </summary>
public sealed class ScreenInfoProvider : IScreenInfoProvider, IScreenLayout
{
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private readonly uint _ownProcessId = (uint)Environment.ProcessId;

    public ScreenBounds GetVirtualScreen()
    {
        using var dpi = NativeMethods.PhysicalPixelsScope.Enter();
        return new ScreenBounds(
            NativeMethods.GetSystemMetrics(SM_XVIRTUALSCREEN),
            NativeMethods.GetSystemMetrics(SM_YVIRTUALSCREEN),
            NativeMethods.GetSystemMetrics(SM_CXVIRTUALSCREEN),
            NativeMethods.GetSystemMetrics(SM_CYVIRTUALSCREEN));
    }

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        using var dpi = NativeMethods.PhysicalPixelsScope.Enter();
        var found = new List<(ScreenRect Bounds, bool IsPrimary, string DeviceName)>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new WindowApi.MONITORINFOEX { cbSize = Marshal.SizeOf<WindowApi.MONITORINFOEX>() };
            if (WindowApi.GetMonitorInfo(monitor, ref info))
            {
                var r = info.rcMonitor;
                found.Add((new ScreenRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top),
                    (info.dwFlags & WindowApi.MONITORINFOF_PRIMARY) != 0, info.szDevice ?? string.Empty));
            }

            return true;
        }, IntPtr.Zero);
        return SearchArea.NumberMonitors(found);
    }

    public IReadOnlyList<WindowInfo> GetWindows()
    {
        using var dpi = NativeMethods.PhysicalPixelsScope.Enter();
        var windows = new List<WindowInfo>();
        WindowApi.EnumWindows((hwnd, _) =>
        {
            try
            {
                if (TryDescribe(hwnd) is { } window) windows.Add(window);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
            {
                // A window that closes while we look at it: skip it.
            }

            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private WindowInfo? TryDescribe(IntPtr hwnd)
    {
        if (!WindowApi.IsWindowVisible(hwnd)) return null;

        // Tool windows (floating palettes, tray helpers) and owned pop-ups aren't "app windows".
        var exStyle = NativeMethods.GetWindowLongValue(hwnd, NativeMethods.GWL_EXSTYLE);
        if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0) return null;
        if (WindowApi.GetWindow(hwnd, WindowApi.GW_OWNER) != IntPtr.Zero) return null;

        // Windows on another virtual desktop, suspended Store apps etc. are "cloaked": visible to Windows, not to you.
        if (WindowApi.DwmGetWindowAttribute(hwnd, WindowApi.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return null;

        var length = WindowApi.GetWindowTextLength(hwnd);
        if (length <= 0) return null;
        var text = new StringBuilder(length + 1);
        WindowApi.GetWindowText(hwnd, text, text.Capacity);
        var title = text.ToString().Trim();
        if (title.Length == 0 || title == "Program Manager") return null;

        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == _ownProcessId) return null; // never search inside SmartMacro itself

        var minimized = WindowApi.IsIconic(hwnd);
        var bounds = minimized ? default : GetBounds(hwnd);
        return new WindowInfo(hwnd.ToInt64(), title, bounds, IsMinimized: minimized);
    }

    /// <summary>
    /// The window's visible rectangle. DWM's "extended frame bounds" leave out the invisible resize borders that
    /// GetWindowRect includes on Windows 10 / 11, so the search area matches what you see.
    /// </summary>
    private static ScreenRect GetBounds(IntPtr hwnd)
    {
        if (WindowApi.DwmGetWindowAttribute(hwnd, WindowApi.DWMWA_EXTENDED_FRAME_BOUNDS, out NativeMethods.RECT frame,
                Marshal.SizeOf<NativeMethods.RECT>()) == 0 && frame.Right > frame.Left && frame.Bottom > frame.Top)
            return new ScreenRect(frame.Left, frame.Top, frame.Right - frame.Left, frame.Bottom - frame.Top);

        return WindowApi.GetWindowRect(hwnd, out var rect) && rect.Right > rect.Left && rect.Bottom > rect.Top
            ? new ScreenRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)
            : default;
    }
}

/// <summary>Window-listing Windows APIs used only by <see cref="ScreenInfoProvider"/> (documented user32 / dwmapi calls).</summary>
internal static class WindowApi
{
    public const uint MONITORINFOF_PRIMARY = 0x00000001;
    public const uint GW_OWNER = 4;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const int DWMWA_CLOAKED = 14;

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public NativeMethods.RECT rcMonitor;
        public NativeMethods.RECT rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string? szDevice;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out NativeMethods.RECT lpRect);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out NativeMethods.RECT pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);
}
