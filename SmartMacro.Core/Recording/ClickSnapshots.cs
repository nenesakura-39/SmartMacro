using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Vision;

namespace SmartMacro.Core.Recording;

/// <summary>
/// Smart Click Snapshot: a small picture of what was under the cursor when a button went down during recording, plus
/// where in that picture the click was. <see cref="Png"/> is a normal PNG file (see <see cref="PngCodec"/>).
/// </summary>
public sealed record ClickTargetSnapshot(byte[] Png, int AnchorX, int AnchorY);

/// <summary>
/// Helpers for Smart Click Snapshots: which screen rectangle to capture around a click, how to turn the captured pixels
/// into a <see cref="ClickTargetSnapshot"/>, and how to put one on a recorded <see cref="MouseClickStep"/>.
/// The capturing itself is done by the recorders in the app (GDI, Windows only).
/// </summary>
public static class ClickSnapshots
{
    /// <summary>Thumbnail size: 56×56 px around the click.</summary>
    public const int Size = 56;

    /// <summary>
    /// A recorder keeps at most this many snapshots per recording (each one is a few KB in the macro file). Clicks after
    /// that are recorded normally, just without a picture.
    /// </summary>
    public const int MaxPerRecording = 500;

    /// <summary>
    /// The <see cref="Size"/>×<see cref="Size"/> rectangle centred on (x, y), moved (not shrunk) so it stays inside the
    /// virtual screen. Empty when the virtual screen is unknown or smaller than the thumbnail.
    /// </summary>
    public static ScreenRect RectAround(int x, int y, ScreenRect virtualScreen, int size = Size)
    {
        if (virtualScreen.IsEmpty || size <= 0) return default;
        var width = Math.Min(size, virtualScreen.Width);
        var height = Math.Min(size, virtualScreen.Height);
        var left = Math.Clamp(x - width / 2, virtualScreen.X, virtualScreen.Right - width);
        var top = Math.Clamp(y - height / 2, virtualScreen.Y, virtualScreen.Bottom - height);
        return new ScreenRect(left, top, width, height);
    }

    /// <summary>
    /// Builds a snapshot from captured pixels: <paramref name="captured"/> is the picture of <paramref name="rect"/>,
    /// and (x, y) is the click on screen. Null when the picture is empty.
    /// </summary>
    public static ClickTargetSnapshot? Create(BgraImage captured, ScreenRect rect, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(captured);
        if (captured.Width <= 0 || captured.Height <= 0) return null;
        var anchorX = Math.Clamp(x - rect.X, 0, captured.Width - 1);
        var anchorY = Math.Clamp(y - rect.Y, 0, captured.Height - 1);
        return new ClickTargetSnapshot(PngCodec.Encode(captured), anchorX, anchorY);
    }

    /// <summary>Puts a recorded snapshot on a click step (nothing happens when there is none).</summary>
    public static void Attach(MouseClickStep step, ClickTargetSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (snapshot is null || snapshot.Png.Length == 0) return;
        step.TargetThumbnailBase64 = Convert.ToBase64String(snapshot.Png);
        step.TargetAnchorX = snapshot.AnchorX;
        step.TargetAnchorY = snapshot.AnchorY;
    }
}
