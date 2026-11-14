using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Vision;
using SmartMacro.Services.Input;

namespace SmartMacro.Services;

public interface IScreenInfoProvider
{
    /// <summary>The rectangle covering all monitors, in physical pixels.</summary>
    ScreenBounds GetVirtualScreen();

    /// <summary>The title of the currently focused foreground window, or null if none / SmartMacro itself.</summary>
    string? GetForegroundWindowTitle() => null;

    /// <summary>Title and executable process name (without ".exe") of the currently focused window.</summary>
    (string? Title, string ProcessName) GetForegroundWindowInfo() => (GetForegroundWindowTitle(), string.Empty);
}

/// <summary>
/// Reads the screen layout from Windows: the virtual screen, every connected monitor (numbered: 1 = main monitor, then
/// left to right) and the visible top-level app windows with their current rectangles. The app is per-monitor DPI aware
/// (see app.manifest) and every call runs in physical-pixel mode, so these are real pixels — the same coordinates mouse
/// automation and screen capture use. Safe to call from any thread (playback asks on its background thread).
/// </summary>
public sealed class ScreenInfoProvider : IScreenInfoProvider, IScreenLayout, IWindowFocuser
{
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private readonly uint _ownProcessId;
    private readonly IWindowApi _windowApi;

    public ScreenInfoProvider() : this(null, null) { }

    public ScreenInfoProvider(IWindowApi? windowApi, uint? ownProcessId = null)
    {
        _windowApi = windowApi ?? new Win32WindowApi();
        _ownProcessId = ownProcessId ?? (uint)Environment.ProcessId;
    }

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
        _windowApi.EnumWindows(hwnd =>
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
        });
        return windows;
    }

    public string? GetForegroundWindowTitle() => GetForegroundWindowInfo().Title;

    public (string? Title, string ProcessName) GetForegroundWindowInfo()
    {
        try
        {
            var fg = _windowApi.GetForegroundWindow();
            if (fg == IntPtr.Zero) return (null, string.Empty);
            var pid = _windowApi.GetWindowThreadProcessId(fg);
            if (pid == _ownProcessId) return (null, string.Empty);

            var title = _windowApi.GetWindowText(fg);
            var processName = ProcessNameOf(pid);
            return (title, processName);
        }
        catch
        {
            return (null, string.Empty);
        }
    }

    private WindowInfo? TryDescribe(IntPtr hwnd)
    {
        if (!_windowApi.IsWindowVisible(hwnd)) return null;

        // Tool windows (floating palettes, tray helpers) and owned pop-ups aren't "app windows".
        if (_windowApi.IsToolWindow(hwnd)) return null;
        if (_windowApi.HasOwner(hwnd)) return null;

        // Windows on another virtual desktop, suspended Store apps etc. are "cloaked": visible to Windows, not to you.
        if (_windowApi.IsCloaked(hwnd)) return null;

        var title = _windowApi.GetWindowText(hwnd);
        if (string.IsNullOrEmpty(title) || title == "Program Manager") return null;

        var processId = _windowApi.GetWindowThreadProcessId(hwnd);
        if (processId == _ownProcessId) return null; // never search inside SmartMacro itself

        var minimized = _windowApi.IsMinimized(hwnd);
        var bounds = minimized ? default : (_windowApi.GetWindowBounds(hwnd) ?? default);
        return new WindowInfo(hwnd.ToInt64(), title, bounds, ProcessNameOf(processId), IsMinimized: minimized);
    }

    // Process id → app name ("RobloxPlayerBeta"), for "Focus window" by app name. Cached: windows are listed often.
    private readonly Dictionary<uint, string> _processNames = new();
    private readonly object _processNamesGate = new();

    /// <summary>The app's file name without ".exe" (empty when Windows doesn't tell, e.g. for an elevated app's details).</summary>
    private string ProcessNameOf(uint processId)
    {
        lock (_processNamesGate)
        {
            if (_processNames.TryGetValue(processId, out var cached)) return cached;
        }

        var name = _windowApi.QueryProcessName(processId);

        lock (_processNamesGate)
        {
            if (_processNames.Count > 1024) _processNames.Clear(); // process ids get reused: never let the cache grow stale
            _processNames[processId] = name;
        }

        return name;
    }

    /// <summary>
    /// "Focus window" step: restore (if asked and minimized) and bring the window to the front. Windows only lets the app
    /// that owns the foreground change it, so for a moment our input queue is attached to the current foreground window's
    /// thread (the documented way for automation tools); it is always detached again.
    /// </summary>
    public bool TryFocus(WindowInfo window, bool restoreIfMinimized)
    {
        ArgumentNullException.ThrowIfNull(window);
        // Playback runs on a background thread without a message queue, and AttachThreadInput needs one: do it on the UI
        // thread (it has a queue, and it is the thread Windows associates with SmartMacro's own windows).
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess() && !dispatcher.HasShutdownStarted)
        {
            try
            {
                return dispatcher.Invoke(() => FocusNow(window, restoreIfMinimized), System.Windows.Threading.DispatcherPriority.Send,
                    CancellationToken.None, TimeSpan.FromSeconds(1));
            }
            catch (Exception ex) when (ex is TaskCanceledException or InvalidOperationException)
            {
                return false;
            }
        }

        return FocusNow(window, restoreIfMinimized);
    }

    public bool TrySnap(WindowInfo window, WindowSnapLayout snapLayout, ScreenRect primaryBounds)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (snapLayout == WindowSnapLayout.KeepCurrent) return true;

        var hwnd = new IntPtr(window.Handle);
        if (!WindowApi.IsWindow(hwnd)) return false;

        using var dpi = NativeMethods.PhysicalPixelsScope.Enter();
        if (snapLayout == WindowSnapLayout.Maximize)
        {
            return WindowApi.ShowWindow(hwnd, WindowApi.SW_MAXIMIZE);
        }

        WindowApi.ShowWindow(hwnd, WindowApi.SW_RESTORE);
        var b = primaryBounds.IsEmpty ? new ScreenRect(0, 0, 1920, 1080) : primaryBounds;
        var target = snapLayout switch
        {
            WindowSnapLayout.SnapLeftHalf => new ScreenRect(b.X, b.Y, Math.Max(200, b.Width / 2), b.Height),
            WindowSnapLayout.SnapRightHalf => new ScreenRect(b.X + b.Width / 2, b.Y, Math.Max(200, b.Width - b.Width / 2), b.Height),
            WindowSnapLayout.CenterOnPrimary => new ScreenRect(
                b.X + (int)(b.Width * 0.1),
                b.Y + (int)(b.Height * 0.1),
                Math.Max(320, (int)(b.Width * 0.8)),
                Math.Max(240, (int)(b.Height * 0.8))),
            _ => b,
        };

        return WindowApi.SetWindowPos(hwnd, IntPtr.Zero, target.X, target.Y, target.Width, target.Height, WindowApi.SWP_NOZORDER | WindowApi.SWP_NOACTIVATE);
    }

    internal static bool FocusWindowHandle(IntPtr hwnd, bool restoreIfMinimized = false)
    {
        if (hwnd == IntPtr.Zero || !WindowApi.IsWindow(hwnd)) return false;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess() && !dispatcher.HasShutdownStarted)
        {
            try
            {
                return dispatcher.Invoke(() => FocusWindowHandleCore(hwnd, restoreIfMinimized),
                    System.Windows.Threading.DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(1));
            }
            catch (Exception ex) when (ex is TaskCanceledException or InvalidOperationException)
            {
                return false;
            }
        }

        return FocusWindowHandleCore(hwnd, restoreIfMinimized);
    }

    private static bool FocusNow(WindowInfo window, bool restoreIfMinimized) =>
        FocusWindowHandleCore(new IntPtr(window.Handle), restoreIfMinimized);

    private static bool FocusWindowHandleCore(IntPtr hwnd, bool restoreIfMinimized)
    {
        if (!WindowApi.IsWindow(hwnd)) return false;

        if (restoreIfMinimized && WindowApi.IsIconic(hwnd))
            WindowApi.ShowWindow(hwnd, WindowApi.SW_RESTORE);
        if (NativeMethods.GetForegroundWindow() == hwnd) return true;

        var foreground = NativeMethods.GetForegroundWindow();
        var foregroundThread = foreground == IntPtr.Zero ? 0u : NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var ownThread = WindowApi.GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != ownThread
                       && WindowApi.AttachThreadInput(ownThread, foregroundThread, true);
        try
        {
            WindowApi.BringWindowToTop(hwnd);
            WindowApi.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) WindowApi.AttachThreadInput(ownThread, foregroundThread, false);
        }

        return NativeMethods.GetForegroundWindow() == hwnd;
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

    // "Focus window" step.
    public const int SW_MAXIMIZE = 3;
    public const int SW_RESTORE = 9;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    // App name of a window's process ("Focus window" by app name).
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    private static readonly uint OwnProcessId = (uint)Environment.ProcessId;

    public static string NormalizeProcessName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var trimmed = name.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? trimmed[..^4] : trimmed;
    }

    public static string? ReadWindowTitle(IntPtr hwnd, int maxLength = 512)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0) return null;
        var capacity = maxLength > 0 ? Math.Min(length + 1, maxLength + 1) : length + 1;
        var sb = new StringBuilder(capacity);
        GetWindowText(hwnd, sb, sb.Capacity);
        var trimmed = sb.ToString().Trim();
        if (trimmed.Length == 0) return null;
        return maxLength > 0 && trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }

    public static string? ReadProcessName(uint pid)
    {
        if (pid == 0) return null;
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            if (!QueryFullProcessImageName(handle, 0, buffer, ref size)) return null;
            var name = Path.GetFileNameWithoutExtension(buffer.ToString(0, size)).Trim();
            return name.Length == 0 ? null : name;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// Finds a visible top-level window matching <paramref name="query"/>, ranking exact title matches above
    /// substring title matches and process names (without arbitrary candidate limits), preferring non-minimized windows.
    /// </summary>
    public static IntPtr FindTopLevelWindow(string? query, bool allowOwnProcess = false)
    {
        if (string.IsNullOrWhiteSpace(query)) return IntPtr.Zero;
        var wanted = query.Trim();
        var wantedProc = NormalizeProcessName(wanted);

        IntPtr exactTitleMatch = IntPtr.Zero;
        IntPtr substringTitleMatch = IntPtr.Zero;
        IntPtr minimizedExactTitleMatch = IntPtr.Zero;
        IntPtr minimizedSubstringTitleMatch = IntPtr.Zero;
        var processCandidates = new List<(IntPtr Hwnd, uint Pid, bool Minimized)>(64);

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0 || (!allowOwnProcess && pid == OwnProcessId)) return true;

            var minimized = IsIconic(hwnd);
            var title = ReadWindowTitle(hwnd, maxLength: 0);
            if (!string.IsNullOrEmpty(title))
            {
                if (string.Equals(title, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    if (!minimized)
                    {
                        exactTitleMatch = hwnd;
                        return false; // best possible match found immediately
                    }

                    if (minimizedExactTitleMatch == IntPtr.Zero)
                        minimizedExactTitleMatch = hwnd;
                }
                else if (title.Contains(wanted, StringComparison.OrdinalIgnoreCase))
                {
                    if (!minimized && substringTitleMatch == IntPtr.Zero)
                        substringTitleMatch = hwnd;
                    else if (minimized && minimizedSubstringTitleMatch == IntPtr.Zero)
                        minimizedSubstringTitleMatch = hwnd;
                }
            }

            // Collect untitled visible top-level windows too so process-name matching still finds them.
            processCandidates.Add((hwnd, pid, minimized));
            return true;
        }, IntPtr.Zero);

        if (exactTitleMatch != IntPtr.Zero) return exactTitleMatch;
        if (substringTitleMatch != IntPtr.Zero) return substringTitleMatch;
        if (minimizedExactTitleMatch != IntPtr.Zero) return minimizedExactTitleMatch;
        if (minimizedSubstringTitleMatch != IntPtr.Zero) return minimizedSubstringTitleMatch;

        IntPtr exactProcMinimized = IntPtr.Zero;
        IntPtr substringProcActive = IntPtr.Zero;
        IntPtr substringProcMinimized = IntPtr.Zero;
        var pidCache = new Dictionary<uint, string?>();

        foreach (var (hwnd, pid, minimized) in processCandidates)
        {
            if (!pidCache.TryGetValue(pid, out var procName))
            {
                procName = NormalizeProcessName(ReadProcessName(pid));
                pidCache[pid] = procName;
            }

            if (string.IsNullOrEmpty(procName)) continue;
            if (string.Equals(procName, wantedProc, StringComparison.OrdinalIgnoreCase))
            {
                if (!minimized) return hwnd;
                if (exactProcMinimized == IntPtr.Zero) exactProcMinimized = hwnd;
            }
            else if (wantedProc.Length > 0 && procName.Contains(wantedProc, StringComparison.OrdinalIgnoreCase))
            {
                if (!minimized && substringProcActive == IntPtr.Zero) substringProcActive = hwnd;
                else if (minimized && substringProcMinimized == IntPtr.Zero) substringProcMinimized = hwnd;
            }
        }

        if (exactProcMinimized != IntPtr.Zero) return exactProcMinimized;
        if (substringProcActive != IntPtr.Zero) return substringProcActive;
        return substringProcMinimized;
    }

    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevationClass = 20;

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr TokenHandle,
        int TokenInformationClass,
        out int TokenInformation,
        int TokenInformationLength,
        out int ReturnLength);

    /// <summary>
    /// Checks whether the window at physical screen coordinates <c>(x, y)</c> belongs to an elevated (Administrator)
    /// process while SmartMacro itself is running un-elevated (which triggers Windows UIPI blocking during playback).
    /// Strictly read-only and never throws.
    /// </summary>
    public static bool IsPointInElevatedWindowWhileUnprivileged(int x, int y, out string? windowDescription)
    {
        windowDescription = null;
        if (Environment.IsPrivilegedProcess)
            return false;

        try
        {
            var child = NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = x, Y = y });
            if (child == IntPtr.Zero) return false;
            var root = NativeMethods.GetAncestor(child, NativeMethods.GA_ROOT);
            var hwnd = root != IntPtr.Zero ? root : child;

            _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0 || pid == OwnProcessId) return false;

            var hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProc == IntPtr.Zero)
            {
                // Access denied on PROCESS_QUERY_LIMITED_INFORMATION for another desktop window strongly indicates elevation.
                if (Marshal.GetLastWin32Error() == 5)
                {
                    windowDescription = ReadWindowTitle(hwnd, 80) ?? $"PID {pid}";
                    return true;
                }
                return false;
            }

            try
            {
                if (!OpenProcessToken(hProc, TOKEN_QUERY, out var hToken) || hToken == IntPtr.Zero)
                    return false;

                try
                {
                    if (GetTokenInformation(hToken, TokenElevationClass, out var isElevated, sizeof(int), out _)
                        && isElevated != 0)
                    {
                        var title = ReadWindowTitle(hwnd, 80);
                        var proc = ReadProcessName(pid);
                        windowDescription = !string.IsNullOrWhiteSpace(title)
                            ? title
                            : !string.IsNullOrWhiteSpace(proc) ? proc : $"PID {pid}";
                        return true;
                    }
                }
                finally
                {
                    CloseHandle(hToken);
                }
            }
            finally
            {
                CloseHandle(hProc);
            }
        }
        catch
        {
            // Diagnostic probe only; never disrupt recording.
        }

        return false;
    }
}
