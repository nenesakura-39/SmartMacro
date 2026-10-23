using SmartMacro.Core.Ocr;
using SmartMacro.Core.Recording;
using SmartMacro.Core.Vision;
using SmartMacro.Services.Ocr;
using SmartMacro.Services.Vision;

namespace SmartMacro.Services.Input;

/// <summary>
/// Smart Click Snapshots while recording: when a mouse button goes down (with a visible, free cursor), grabs the
/// 56×56 px around the click with one tiny GDI BitBlt (well under a millisecond) and keeps it as a PNG with the click.
/// The picture is taken right in the input callback, before the clicked app has reacted, so it shows the button as it
/// looked when you clicked it. Never throws: a failed capture just means that click has no picture.
/// One instance per recorder; not shared between threads at the same time.
/// </summary>
internal sealed class ClickSnapshotter
{
    private ScreenRect _virtualScreen;
    private int _count;

    /// <summary>Turned off by Settings → "Smart Click Snapshots".</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How many pictures this recording kept so far.</summary>
    public int Count => _count;

    /// <summary>Call when a recording starts (on the UI thread): reads the screen layout and resets the count.</summary>
    public void Reset()
    {
        _count = 0;
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
        try
        {
            var rect = ClickSnapshots.RectAround(x, y, _virtualScreen);
            if (rect.IsEmpty) return null;
            var pixels = ScreenCapture.CaptureBgra(rect);
            var snapshot = ClickSnapshots.Create(new BgraImage(rect.Width, rect.Height, pixels), rect, x, y);
            if (snapshot is not null) _count++;
            return snapshot;
        }
        catch (Exception)
        {
            return null; // e.g. the secure desktop / a locked screen: the click is still recorded, just without a picture
        }
    }
}
