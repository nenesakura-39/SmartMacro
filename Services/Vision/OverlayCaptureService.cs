using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using SmartMacro.Core.Vision;
using SmartMacro.Services.Input;

namespace SmartMacro.Services.Vision;

/// <summary>
/// "🎥 Show overlays in OBS / Discord" (streamer mode, <see cref="Core.Models.AppSettings.ShowOverlaysInObsAndRecording"/>).
///
/// Every SmartMacro overlay window (run HUD / status banner, target-lock reticle, region highlight boxes) registers here
/// when its handle is created (<see cref="Track"/>, also through <see cref="CaptureShield.TryExclude(Window)"/>). The
/// service sets its Windows display affinity from <see cref="AllowObsCapture"/> (rules: <see cref="OverlayCapturePolicy"/>):
/// - off (default): WDA_EXCLUDEFROMCAPTURE – visible on the monitor, invisible to OBS, Discord, Game Bar AND to SmartMacro's
///   own Find-step captures;
/// - on: WDA_NONE – OBS, Discord streams and screen recordings capture the overlays in full quality.
/// Changing <see cref="AllowObsCapture"/> re-applies the affinity right away to every overlay that is open (on each
/// window's own UI thread). Closed windows are forgotten automatically.
///
/// Windows that must never be in a capture whatever the setting (the area / colour pickers and their countdown banner,
/// which would end up inside the very picture being picked, and SmartMacro's main window during playback, whose template
/// previews a Find image step could match) don't register here.
/// </summary>
public static class OverlayCaptureService
{
    private static readonly ConcurrentDictionary<IntPtr, Dispatcher> Tracked = new();
    private static readonly ConcurrentDictionary<IntPtr, bool> Excluded = new();
    private static volatile bool _allowObsCapture;

    /// <summary>Raised (on the thread that changed it) after <see cref="AllowObsCapture"/> changed and was re-applied.</summary>
    public static event EventHandler? Changed;

    /// <summary>
    /// True = overlays are visible to OBS / Discord / recorders (WDA_NONE). False (default) = left out of every capture.
    /// Setting it updates every open overlay at once.
    /// </summary>
    public static bool AllowObsCapture
    {
        get => _allowObsCapture;
        set
        {
            if (_allowObsCapture == value) return;
            _allowObsCapture = value;
            ReapplyAll();
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>How many overlay windows are registered right now (for logs and diagnostics).</summary>
    public static int TrackedCount => Tracked.Count;

    /// <summary>
    /// Registers an overlay window (call from <c>OnSourceInitialized</c>, on its UI thread) and applies the current setting.
    /// Returns true when the window is now left out of captures, false when captures can see it (streamer mode, Windows
    /// older than 10 2004, or the call failed) – then <see cref="CaptureShield"/> may hide it before SmartMacro's own captures.
    /// </summary>
    public static bool Track(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;

        if (Tracked.TryAdd(hwnd, window.Dispatcher))
            window.Closed += (_, _) => Forget(hwnd);
        return Apply(hwnd);
    }

    /// <summary>Registers a raw window handle owned by the calling thread (prefer <see cref="Track"/>).</summary>
    public static bool Track(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        Tracked[hwnd] = Dispatcher.CurrentDispatcher;
        return Apply(hwnd);
    }

    /// <summary>Stops updating a window (it was closed).</summary>
    public static void Forget(IntPtr hwnd)
    {
        Tracked.TryRemove(hwnd, out _);
        Excluded.TryRemove(hwnd, out _);
    }

    /// <summary>True when the window is currently left out of captures (false for unknown windows).</summary>
    public static bool IsExcluded(IntPtr hwnd) => hwnd != IntPtr.Zero && Excluded.TryGetValue(hwnd, out var excluded) && excluded;

    /// <inheritdoc cref="IsExcluded(IntPtr)"/>
    public static bool IsExcluded(Window window) => IsExcluded(new WindowInteropHelper(window).Handle);

    /// <summary>Sets the window's display affinity from the current setting. Returns true when it is excluded from captures.</summary>
    private static bool Apply(IntPtr hwnd)
    {
        var allow = _allowObsCapture;
        var wanted = OverlayCapturePolicy.AffinityFor(allow);
        bool excluded;
        try
        {
            if (wanted == OverlayCapturePolicy.WdaExcludeFromCapture)
            {
                // Older Windows treats 0x11 like WDA_MONITOR (a black box in captures): don't even try there.
                excluded = CaptureShield.IsExclusionSupported && NativeMethods.SetWindowDisplayAffinity(hwnd, OverlayCapturePolicy.WdaExcludeFromCapture);
            }
            else
            {
                NativeMethods.SetWindowDisplayAffinity(hwnd, OverlayCapturePolicy.WdaNone);
                excluded = false;
            }
        }
        catch (Exception)
        {
            excluded = false; // missing entry point / dead window: captures can see it
        }

        Excluded[hwnd] = excluded;
        return excluded;
    }

    private static void ReapplyAll()
    {
        foreach (var (hwnd, dispatcher) in Tracked.ToArray())
        {
            try
            {
                if (dispatcher.HasShutdownStarted)
                {
                    Forget(hwnd);
                    continue;
                }

                if (dispatcher.CheckAccess()) Apply(hwnd);
                else dispatcher.InvokeAsync(() => Apply(hwnd), DispatcherPriority.Send);
            }
            catch (Exception)
            {
                Forget(hwnd); // purely visual: never let one broken window stop the others
            }
        }
    }
}
