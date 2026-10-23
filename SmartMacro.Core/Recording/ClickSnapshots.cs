using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Vision;

namespace SmartMacro.Core.Recording;

/// <summary>
/// Smart Click Snapshot: a small picture of what was under the cursor when a button went down during recording, plus
/// where in that picture the click was. <see cref="Png"/> is a normal PNG file (see <see cref="PngCodec"/>).
/// A snapshot made from raw pixels (<see cref="FromPixels"/>) encodes its PNG only when it's first needed (or in the
/// background via <see cref="EncodeInBackground"/>), so the recorder's mouse hook only pays for the tiny screen copy.
/// Thread-safe: the PNG is encoded exactly once.
/// </summary>
public sealed class ClickTargetSnapshot
{
    private readonly object _gate = new();
    private byte[]? _png;
    private BgraImage? _pixels;

    /// <summary>A snapshot from an already encoded PNG.</summary>
    public ClickTargetSnapshot(byte[] png, int anchorX, int anchorY)
    {
        _png = png ?? throw new ArgumentNullException(nameof(png));
        AnchorX = anchorX;
        AnchorY = anchorY;
    }

    private ClickTargetSnapshot(BgraImage pixels, int anchorX, int anchorY)
    {
        _pixels = pixels;
        AnchorX = anchorX;
        AnchorY = anchorY;
    }

    /// <summary>A snapshot from raw captured pixels; the PNG is made later (first use, or <see cref="EncodeInBackground"/>).</summary>
    public static ClickTargetSnapshot FromPixels(BgraImage pixels, int anchorX, int anchorY) =>
        new(pixels ?? throw new ArgumentNullException(nameof(pixels)), anchorX, anchorY);

    /// <summary>Where in the picture the click was (px from its top-left corner).</summary>
    public int AnchorX { get; }

    public int AnchorY { get; }

    /// <summary>True once the PNG exists (always for a snapshot made from a PNG).</summary>
    public bool IsEncoded
    {
        get { lock (_gate) return _png is not null; }
    }

    /// <summary>The picture as a PNG file (encoded now if that hasn't happened yet).</summary>
    public byte[] Png
    {
        get
        {
            lock (_gate)
            {
                if (_png is null)
                {
                    _png = PngCodec.Encode(_pixels!);
                    _pixels = null; // the raw copy isn't needed any more
                }

                return _png;
            }
        }
    }

    /// <summary>Encodes the PNG on a thread-pool thread (never on the caller's thread). Errors are ignored.</summary>
    public void EncodeInBackground()
    {
        if (IsEncoded) return;
        ThreadPool.QueueUserWorkItem(static state =>
        {
            try
            {
                _ = ((ClickTargetSnapshot)state!).Png;
            }
            catch (Exception)
            {
                // A broken picture just isn't kept (Attach skips it).
            }
        }, this);
    }
}

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
        return ClickTargetSnapshot.FromPixels(captured, anchorX, anchorY); // PNG encoded later, off the hook thread
    }

    /// <summary>Puts a recorded snapshot on a click step (nothing happens when there is none or it can't be encoded).</summary>
    public static void Attach(MouseClickStep step, ClickTargetSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (TryBase64(snapshot) is not { } base64) return;
        step.TargetThumbnailBase64 = base64;
        step.TargetAnchorX = snapshot!.AnchorX;
        step.TargetAnchorY = snapshot.AnchorY;
    }

    /// <summary>The snapshot as Base64 PNG text, or null (none / empty / couldn't be encoded).</summary>
    public static string? TryBase64(ClickTargetSnapshot? snapshot)
    {
        if (snapshot is null) return null;
        try
        {
            var png = snapshot.Png;
            return png.Length == 0 ? null : Convert.ToBase64String(png);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NullReferenceException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>A snapshot back from a stored Base64 thumbnail (recorded-input clicks), or null when it's damaged.</summary>
    public static ClickTargetSnapshot? FromBase64(string? base64, int anchorX, int anchorY)
    {
        if (string.IsNullOrEmpty(base64)) return null;
        try
        {
            return new ClickTargetSnapshot(Convert.FromBase64String(base64), anchorX, anchorY);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
