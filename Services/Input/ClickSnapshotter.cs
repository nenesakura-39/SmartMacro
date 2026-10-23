using System.Diagnostics;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Recording;
using SmartMacro.Core.Vision;
using SmartMacro.Services.Ocr;
using SmartMacro.Services.Vision;

namespace SmartMacro.Services.Input;

/// <summary>
/// Smart Click Snapshots while recording: when a mouse button goes down (with a visible, free cursor), grabs the
/// 56×56 px around the click with one tiny GDI BitBlt (well under a millisecond). The picture is taken right in the
/// input callback, before the clicked app has reacted, so it shows the button as it looked when you clicked it.
/// Only the raw pixel copy happens on the hook thread: the PNG + Base64 encoding runs on a thread-pool thread (or at the
/// latest when the recording is turned into steps), so fast clicking never slows the low-level mouse hook down (Windows
/// silently removes a hook that answers too slowly). A second press within 60 ms and 8 px of the last snapshot (a fast
/// double click, a button mashed at 10+ CPS) reuses that picture instead of capturing again.
/// Never throws: a failed capture just means that click has no picture.
/// One instance per recorder; not shared between threads at the same time.
/// </summary>
internal sealed class ClickSnapshotter
{
    /// <summary>A press this soon after the last snapshot …</summary>
    public static readonly TimeSpan ReuseWithin = TimeSpan.FromMilliseconds(60);

    /// <summary>… and at most this far away (px each way) reuses that snapshot.</summary>
    public const int ReuseDistancePx = 8;

    private ScreenRect _virtualScreen;
    private int _count;
    private ClickTargetSnapshot? _last;
    private int _lastX;
    private int _lastY;
    private long _lastTimestamp;

    /// <summary>Turned off by Settings → "Smart Click Snapshots".</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How many pictures this recording kept so far.</summary>
    public int Count => _count;

    /// <summary>Call when a recording starts (on the UI thread): reads the screen layout and resets the count.</summary>
    public void Reset()
    {
        _count = 0;
        _last = null;
        try
        {
            _virtualScreen = new GdiScreenCapture().GetVirtualScreen();
        }
        catch (Exception)
        {
            _virtualScreen = default; // no pictures this time
        }
    }

    /// <summary>The picture around (x, y), or null (turned off, limit reached, capture failed).</summary>
    public ClickTargetSnapshot? TryCapture(int x, int y)
    {
        if (!Enabled || _count >= ClickSnapshots.MaxPerRecording || _virtualScreen.IsEmpty) return null;
        var now = Stopwatch.GetTimestamp();
        if (_last is { } recent && Stopwatch.GetElapsedTime(_lastTimestamp, now) < ReuseWithin
            && Math.Abs(x - _lastX) <= ReuseDistancePx && Math.Abs(y - _lastY) <= ReuseDistancePx)
        {
            _count++;
            return recent; // same button pressed again right away: same picture, no second capture
        }

        try
        {
            var rect = ClickSnapshots.RectAround(x, y, _virtualScreen);
            if (rect.IsEmpty) return null;
            // shield: false = never wait for SmartMacro's own overlays here (we're on the mouse-hook thread).
            var pixels = ScreenCapture.CaptureBgra(rect, shield: false);
            var snapshot = ClickSnapshots.Create(new BgraImage(rect.Width, rect.Height, pixels), rect, x, y);
            if (snapshot is null) return null;
            snapshot.EncodeInBackground(); // PNG + Base64 off the hook thread
            _count++;
            (_last, _lastX, _lastY, _lastTimestamp) = (snapshot, x, y, now);
            return snapshot;
        }
        catch (Exception)
        {
            return null; // e.g. the secure desktop / a locked screen: the click is still recorded, just without a picture
        }
    }
}
