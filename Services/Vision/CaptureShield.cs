using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Threading;
using SmartMacro.Services.Input;

namespace SmartMacro.Services.Vision;

/// <summary>
/// Keeps SmartMacro's own flashy overlays (target-lock reticle, test highlight) out of the screen captures that Find
/// image / Find color / Find text / feedback loops look at.
///
/// Normal case (Windows 10 2004+ / Windows 11): <see cref="TryExclude"/> sets WDA_EXCLUDEFROMCAPTURE, so Windows keeps
/// the overlay visible to the user but leaves it out of every BitBlt. Nothing else to do, no flicker.
///
/// Fallback (older Windows, or the call failed): the overlay calls <see cref="MarkVisible"/> while it shows, and every
/// GDI capture calls <see cref="BeforeCapture"/> first, which hides those overlays synchronously. They're short flashes
/// anyway, so hiding one a bit early is fine; a wrong match on the next frame is not.
/// </summary>
public static class CaptureShield
{
    // Windows 10 version 2004 = build 19041. Older builds treat WDA_EXCLUDEFROMCAPTURE like WDA_MONITOR, which makes the
    // window a black box in captures, so we don't even try there.
    private const int ExcludeFromCaptureBuild = 19041;

    // Visible overlays that captures can see, with how to hide each one.
    private static readonly ConcurrentDictionary<Window, Action> Unshielded = new(ReferenceEqualityComparer.Instance);

    /// <summary>True when this Windows version can leave a window out of captures.</summary>
    public static bool IsExclusionSupported { get; } = OperatingSystem.IsWindowsVersionAtLeast(10, 0, ExcludeFromCaptureBuild);

    /// <summary>
    /// Asks Windows to leave the window out of screen captures. False when it can't (old Windows, call failed): then the
    /// window should use <see cref="MarkVisible"/> / <see cref="MarkHidden"/>.
    /// </summary>
    public static bool TryExclude(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsExclusionSupported) return false;
        try
        {
            return NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
        }
        catch (Exception)
        {
            return false; // missing entry point etc.: treat as "not supported"
        }
    }

    /// <summary>An overlay that captures CAN see is showing now; <paramref name="hide"/> runs on its own UI thread.</summary>
    public static void MarkVisible(Window window, Action hide)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(hide);
        Unshielded[window] = hide;
    }

    /// <summary>The overlay is hidden / closed again.</summary>
    public static void MarkHidden(Window window)
    {
        if (window is not null) Unshielded.TryRemove(window, out _);
    }

    /// <summary>How many visible overlays a capture would see right now (0 on a normal PC). For logs and tests.</summary>
    public static int UnshieldedCount => Unshielded.Count;

    /// <summary>
    /// Call from any thread right before a GDI screen capture. Hides every visible overlay that isn't excluded from
    /// captures and waits (up to 200 ms each) until it's gone. Does nothing (and costs nothing) on a normal PC.
    /// </summary>
    public static void BeforeCapture()
    {
        if (Unshielded.IsEmpty) return;
        foreach (var (window, hide) in Unshielded.ToArray())
        {
            try
            {
                if (window.Dispatcher.CheckAccess()) hide();
                else window.Dispatcher.Invoke(hide, DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromMilliseconds(200));
            }
            catch (Exception)
            {
                // Cosmetic only: never block or break a capture.
            }
            finally
            {
                Unshielded.TryRemove(window, out _);
            }
        }
    }
}
