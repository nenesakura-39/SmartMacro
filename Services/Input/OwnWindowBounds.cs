using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using static SmartMacro.Services.Input.NativeMethods;

namespace SmartMacro.Services.Input;

/// <summary>
/// Where SmartMacro's own visible windows are (physical screen pixels), as a snapshot the low-level mouse hook can check
/// in well under a microsecond (a few rectangle compares, no Windows calls). Refreshed on the UI thread before recording
/// starts and a few times per second while it runs (windows can be moved). Click-through overlays (HUD, reticle,
/// highlight boxes) and minimized windows aren't in it: clicks there go to the app below and are real input.
/// </summary>
internal static class OwnWindowBounds
{
    private static RECT[] _rects = Array.Empty<RECT>();

    /// <summary>How many SmartMacro windows the last refresh found (for tests / diagnostics).</summary>
    public static int Count => Volatile.Read(ref _rects).Length;

    /// <summary>Re-reads the rectangles. UI thread only (it walks WPF's window list).</summary>
    public static void Refresh()
    {
        var rects = new List<RECT>();
        try
        {
            if (Application.Current is { } app)
            {
                foreach (Window window in app.Windows)
                {
                    var hwnd = new WindowInteropHelper(window).Handle;
                    if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd) || IsIconic(hwnd)) continue;
                    if ((GetWindowLongValue(hwnd, GWL_EXSTYLE) & WS_EX_TRANSPARENT) != 0) continue; // click-through overlay
                    if (GetWindowRect(hwnd, out var rect) && rect.Right > rect.Left && rect.Bottom > rect.Top) rects.Add(rect);
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Called from the wrong thread or while the app shuts down: keep the previous snapshot.
            return;
        }

        Volatile.Write(ref _rects, rects.ToArray());
    }

    public static void Clear() => Volatile.Write(ref _rects, Array.Empty<RECT>());

    /// <summary>True if the point lies inside one of SmartMacro's own visible windows. Safe from any thread.</summary>
    public static bool Contains(int x, int y)
    {
        foreach (var r in Volatile.Read(ref _rects))
        {
            if (x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom) return true;
        }

        return false;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
}
