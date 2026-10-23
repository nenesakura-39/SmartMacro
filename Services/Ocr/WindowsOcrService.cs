using System.ComponentModel;
using System.Runtime.InteropServices;
using SmartMacro.Core.Ocr;
using SmartMacro.Services.Input;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;

namespace SmartMacro.Services.Ocr;

/// <summary>
/// Reads text from the screen with the OCR engine built into Windows (Windows.Media.Ocr).
/// Everything runs locally; no screenshots leave the computer.
/// </summary>
public sealed class WindowsOcrService : IOcrService
{

    // Overlap between tiles when a big area must be split, so words on a tile edge aren't cut in half.
    private const int TileOverlap = 120;

    // Extra margin captured around the search area, and when to enlarge small areas before OCR.
    private const int CapturePadding = 48;
    private const int SmallAreaWidth = 600;
    private const int SmallAreaHeight = 200;
    private const int UpscaleFactor = 2;

    private readonly OcrEngine? _engine;

    public WindowsOcrService()
    {
        try
        {
            // Uses the first OCR-capable language from the user's Windows language list.
            _engine = OcrEngine.TryCreateFromUserProfileLanguages();
            StatusText = _engine is null
                ? "Text recognition isn't available: no OCR language is installed. In Windows Settings > Time & language > Language & region, open your language's options and add \"Optical character recognition\", then restart SmartMacro."
                : $"Text recognition is ready (language: {_engine.RecognizerLanguage.DisplayName}).";
        }
        catch (Exception ex)
        {
            _engine = null;
            StatusText = $"Text recognition couldn't start: {ex.Message}";
        }
    }

    public bool IsAvailable => _engine is not null;

    public string StatusText { get; }

    /// <summary>All monitors (physical pixels); shared with the image capture, re-read at most every 500 ms.</summary>
    public ScreenRect GetVirtualScreen() => ScreenCapture.VirtualScreen();

    public async Task<OcrReadResult> ReadAsync(ScreenRect region, CancellationToken cancellationToken)
    {
        var engine = _engine ?? throw new OcrUnavailableException(StatusText);
        var screen = GetVirtualScreen();

        var requested = region.Intersect(screen);
        if (requested.IsEmpty)
            throw new ArgumentException($"The search area {region} is outside the screen. Pick the area again.");

        // Windows OCR struggles with tightly cropped text, so capture a margin around the area...
        var area = new ScreenRect(requested.X - CapturePadding, requested.Y - CapturePadding,
            requested.Width + CapturePadding * 2, requested.Height + CapturePadding * 2).Intersect(screen);
        var pixels = ScreenCapture.CaptureBgra(area);

        // ...and enlarge small areas, since small text is read much more reliably when it's bigger.
        var scale = area.Width < SmallAreaWidth || area.Height < SmallAreaHeight ? UpscaleFactor : 1;
        var width = area.Width * scale;
        var height = area.Height * scale;
        if (scale > 1) pixels = Upscale(pixels, area.Width, area.Height, scale);

        var maxTile = (int)Math.Min(OcrEngine.MaxImageDimension, 4000u);
        var lines = new List<OcrLineBox>();

        // Split large areas into tiles the OCR engine accepts.
        foreach (var tile in Tiles(width, height, maxTile))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var tileBytes = CopyTile(pixels, width, tile);
            using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
                CryptographicBuffer.CreateFromByteArray(tileBytes),
                BitmapPixelFormat.Bgra8, tile.Width, tile.Height, BitmapAlphaMode.Ignore);

            var result = await engine.RecognizeAsync(bitmap);

            foreach (var line in result.Lines)
            {
                var words = line.Words
                    .Select(w => new OcrWordBox(w.Text, new ScreenRect(
                        area.X + (int)Math.Round((tile.X + w.BoundingRect.X) / scale),
                        area.Y + (int)Math.Round((tile.Y + w.BoundingRect.Y) / scale),
                        Math.Max(1, (int)Math.Round(w.BoundingRect.Width / scale)),
                        Math.Max(1, (int)Math.Round(w.BoundingRect.Height / scale)))))
                    // Only keep words that are (at least partly) inside the area the user asked for.
                    .Where(w => w.Bounds.IntersectsWith(requested))
                    .ToList();
                if (words.Count > 0) lines.Add(new OcrLineBox(words));
            }
        }

        return new OcrReadResult(requested, lines);
    }

    /// <summary>
    /// Saves a PNG of exactly what OCR would capture for this area (including the margin), for troubleshooting.
    /// Returns the file path, or null if it couldn't be saved. Stays on this PC; overwritten on each test.
    /// </summary>
    public string? TrySaveCapture(ScreenRect region, string path)
    {
        try
        {
            var screen = GetVirtualScreen();
            var requested = region.Intersect(screen);
            if (requested.IsEmpty) return null;
            var area = new ScreenRect(requested.X - CapturePadding, requested.Y - CapturePadding,
                requested.Width + CapturePadding * 2, requested.Height + CapturePadding * 2).Intersect(screen);

            var pixels = ScreenCapture.CaptureBgra(area);
            var image = System.Windows.Media.Imaging.BitmapSource.Create(area.Width, area.Height, 96, 96,
                System.Windows.Media.PixelFormats.Bgr32, null, pixels, area.Width * 4);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var file = File.Create(path);
            encoder.Save(file);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>Nearest-neighbour enlargement of a BGRA image (keeps text edges crisp for OCR).</summary>
    private static byte[] Upscale(byte[] source, int width, int height, int scale)
    {
        var sourceStride = width * 4;
        var targetStride = sourceStride * scale;
        var target = new byte[targetStride * height * scale];
        var row = new byte[targetStride];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var from = y * sourceStride + x * 4;
                for (var k = 0; k < scale; k++)
                    Buffer.BlockCopy(source, from, row, (x * scale + k) * 4, 4);
            }

            for (var k = 0; k < scale; k++)
                Buffer.BlockCopy(row, 0, target, (y * scale + k) * targetStride, targetStride);
        }

        return target;
    }

    private static IEnumerable<ScreenRect> Tiles(int width, int height, int maxTile)
    {
        var step = Math.Max(1, maxTile - TileOverlap);
        for (var y = 0; y < height; y += step)
        {
            var h = Math.Min(maxTile, height - y);
            for (var x = 0; x < width; x += step)
            {
                var w = Math.Min(maxTile, width - x);
                yield return new ScreenRect(x, y, w, h);
                if (x + w >= width) break;
            }

            if (y + h >= height) break;
        }
    }

    private static byte[] CopyTile(byte[] pixels, int fullWidth, ScreenRect tile)
    {
        var tileStride = tile.Width * 4;
        var fullStride = fullWidth * 4;
        if (tile.X == 0 && tile.Width == fullWidth && tile.Y == 0 && tile.Height * fullStride == pixels.Length)
            return pixels;

        var result = new byte[tileStride * tile.Height];
        for (var row = 0; row < tile.Height; row++)
            Buffer.BlockCopy(pixels, (tile.Y + row) * fullStride + tile.X * 4, result, row * tileStride, tileStride);
        return result;
    }
}

/// <summary>
/// Copies part of the screen into a 32-bit BGRA byte array (top-down rows) using GDI. BitBlt only copies the requested
/// rectangle, so a small area (a Smart-Aim crop, a Hot-Spot re-check, a feedback-loop bar) is cheap.
///
/// AAA speed-up: each thread keeps ONE reusable memory DC + bitmap (<see cref="CaptureSurface"/>) for the size it captured
/// last, so repeated captures of the same size (a fast loop looking at the same 80×80 spot) don't create and destroy GDI
/// handles every time. A different size replaces it; areas bigger than <see cref="MaxReusedPixels"/> (whole monitors) are
/// never kept, so no thread holds tens of MB of GDI memory. <see cref="ReleaseThreadResources"/> frees the current
/// thread's surface; <see cref="ReleaseAllResources"/> (app exit) frees it and makes every other thread drop its surface
/// on its next capture (GDI handles are only ever freed by the thread that uses them, so nothing is freed while in use).
/// </summary>
internal static class ScreenCapture
{
    /// <summary>Largest area (pixels) whose GDI bitmap is kept for reuse: 1024 × 1024 (4 MB).</summary>
    public const int MaxReusedPixels = 1024 * 1024;

    /// <summary>How long the virtual-screen rectangle (all monitors) is reused before it's asked from Windows again.</summary>
    public static readonly TimeSpan VirtualScreenCacheTime = TimeSpan.FromMilliseconds(500);

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    /// <summary>A memory DC with a bitmap of one size selected into it (kept per thread, see the class remarks).</summary>
    private sealed class CaptureSurface
    {
        public IntPtr MemoryDc;
        public IntPtr Bitmap;
        public IntPtr PreviousBitmap; // what the fresh DC had selected; put back before deleting
        public int Width;
        public int Height;
        public int Generation;
        public bool InUse;
        public bool Released;

        public void Free()
        {
            if (MemoryDc != IntPtr.Zero && PreviousBitmap != IntPtr.Zero) NativeMethods.SelectObject(MemoryDc, PreviousBitmap);
            if (Bitmap != IntPtr.Zero) NativeMethods.DeleteObject(Bitmap);
            if (MemoryDc != IntPtr.Zero) NativeMethods.DeleteDC(MemoryDc);
            MemoryDc = Bitmap = PreviousBitmap = IntPtr.Zero;
            Width = Height = 0;
        }
    }

    [ThreadStatic] private static CaptureSurface? t_surface;

    // Bumped by ReleaseAllResources: surfaces made before it are dropped by their thread on its next capture.
    private static int _generation;

    // Diagnostics: GDI capture surfaces that exist right now (kept + throw-away), all threads.
    private static int _liveSurfaces;

    private static readonly object VirtualScreenGate = new();
    private static ScreenRect _virtualScreen;
    private static long _virtualScreenAt;
    private static bool _virtualScreenValid;

    /// <summary>
    /// The rectangle covering all monitors (physical pixels), re-read from Windows at most every
    /// <see cref="VirtualScreenCacheTime"/> (enumerating monitors on every look of a fast loop costs more than the look).
    /// </summary>
    public static ScreenRect VirtualScreen()
    {
        var now = Environment.TickCount64;
        lock (VirtualScreenGate)
        {
            if (_virtualScreenValid && now - _virtualScreenAt < (long)VirtualScreenCacheTime.TotalMilliseconds) return _virtualScreen;
        }

        var rect = ReadVirtualScreen();
        lock (VirtualScreenGate)
        {
            _virtualScreen = rect;
            _virtualScreenAt = now;
            _virtualScreenValid = !rect.IsEmpty;
        }

        return rect;
    }

    /// <summary>Forgets the remembered monitor layout (e.g. after the display settings changed).</summary>
    public static void InvalidateVirtualScreen()
    {
        lock (VirtualScreenGate) _virtualScreenValid = false;
    }

    private static ScreenRect ReadVirtualScreen()
    {
        // The virtual-screen size must be in the same physical pixels as the capture and the mouse steps.
        using var dpi = NativeMethods.PhysicalPixelsScope.Enter();
        var rect = new ScreenRect(
            NativeMethods.GetSystemMetrics(SM_XVIRTUALSCREEN),
            NativeMethods.GetSystemMetrics(SM_YVIRTUALSCREEN),
            NativeMethods.GetSystemMetrics(SM_CXVIRTUALSCREEN),
            NativeMethods.GetSystemMetrics(SM_CYVIRTUALSCREEN));

        foreach (var m in RegionPicker.MonitorBounds())
        {
            if (rect.IsEmpty)
            {
                rect = m;
                continue;
            }

            var left = Math.Min(rect.X, m.X);
            var top = Math.Min(rect.Y, m.Y);
            var right = Math.Max(rect.Right, m.Right);
            var bottom = Math.Max(rect.Bottom, m.Bottom);
            rect = new ScreenRect(left, top, right - left, bottom - top);
        }

        return rect;
    }

    /// <param name="shield">
    /// Hide SmartMacro's own flashy overlays first where Windows can't leave them out of captures (see
    /// <see cref="Vision.CaptureShield"/>). False only for the click snapshot on the mouse-hook thread, which must never wait.
    /// </param>
    public static byte[] CaptureBgra(ScreenRect area, bool shield = true)
    {
        if (area.IsEmpty) throw new ArgumentException("The capture area is empty.", nameof(area));
        if (shield) Vision.CaptureShield.BeforeCapture();

        // Physical pixels on every monitor, whatever thread this runs on (see PhysicalPixelsScope).
        using var dpi = NativeMethods.PhysicalPixelsScope.Enter();
        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) throw new Win32Exception("Could not access the screen.");

        var reusable = (long)area.Width * area.Height <= MaxReusedPixels;
        CaptureSurface? surface = null;
        try
        {
            surface = Acquire(screenDc, area.Width, area.Height, reusable);

            var copied = NativeMethods.BitBlt(surface.MemoryDc, 0, 0, area.Width, area.Height, screenDc, area.X, area.Y,
                NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);
            var bltError = Marshal.GetLastWin32Error();
            if (!copied)
                throw new Win32Exception(bltError, "Windows didn't allow a screen capture (the screen may be locked).");

            var info = new NativeMethods.BITMAPINFO
            {
                bmiHeader = new NativeMethods.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                    biWidth = area.Width,
                    biHeight = -area.Height, // negative = rows top to bottom
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = NativeMethods.BI_RGB,
                },
            };

            // GetDIBits needs the bitmap deselected; it is selected again on the next use of this surface.
            NativeMethods.SelectObject(surface.MemoryDc, surface.PreviousBitmap);
            var pixels = new byte[area.Width * area.Height * 4];
            var lines = NativeMethods.GetDIBits(surface.MemoryDc, surface.Bitmap, 0, (uint)area.Height, pixels, ref info, NativeMethods.DIB_RGB_COLORS);
            if (lines == 0) throw new Win32Exception("Could not read the captured screen pixels.");
            return pixels;
        }
        catch
        {
            // Something went wrong with this surface: don't keep it (the next capture makes a fresh one).
            if (surface is not null) Discard(surface);
            surface = null;
            throw;
        }
        finally
        {
            if (surface is not null)
            {
                if (reusable) surface.InUse = false;
                else Release(surface);
            }

            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>
    /// This thread's surface when it has the right size (selected and ready for BitBlt), otherwise a new one. A surface
    /// for a big area (<paramref name="keep"/> false) is a throw-away one and never replaces the thread's kept surface.
    /// </summary>
    private static CaptureSurface Acquire(IntPtr screenDc, int width, int height, bool keep)
    {
        if (keep && t_surface is { } cached && !cached.InUse && cached.MemoryDc != IntPtr.Zero)
        {
            if (cached.Width == width && cached.Height == height && cached.Generation == Volatile.Read(ref _generation))
            {
                NativeMethods.SelectObject(cached.MemoryDc, cached.Bitmap);
                cached.InUse = true;
                return cached;
            }

            Discard(cached); // a different size (or released): replace it
        }

        var surface = new CaptureSurface { Width = width, Height = height, InUse = true, Generation = Volatile.Read(ref _generation) };
        Interlocked.Increment(ref _liveSurfaces);
        try
        {
            surface.MemoryDc = NativeMethods.CreateCompatibleDC(screenDc);
            surface.Bitmap = NativeMethods.CreateCompatibleBitmap(screenDc, width, height);
            if (surface.MemoryDc == IntPtr.Zero || surface.Bitmap == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not prepare a screen capture.");
            surface.PreviousBitmap = NativeMethods.SelectObject(surface.MemoryDc, surface.Bitmap);
        }
        catch
        {
            Release(surface);
            throw;
        }

        if (keep) t_surface = surface;
        return surface;
    }

    private static void Discard(CaptureSurface surface)
    {
        if (ReferenceEquals(t_surface, surface)) t_surface = null;
        Release(surface);
    }

    private static void Release(CaptureSurface surface)
    {
        if (surface.Released) return; // already freed
        surface.Released = true;
        surface.Free();
        Interlocked.Decrement(ref _liveSurfaces);
    }

    /// <summary>Frees the calling thread's reusable capture surface (if any). Safe to call any time.</summary>
    public static void ReleaseThreadResources()
    {
        if (t_surface is { InUse: false } surface) Discard(surface);
    }

    /// <summary>
    /// App exit / low memory: frees the calling thread's surface now and makes every other thread drop (free) its surface
    /// on its next capture, so no handle is ever freed while another thread is using it.
    /// </summary>
    public static void ReleaseAllResources()
    {
        Interlocked.Increment(ref _generation);
        ReleaseThreadResources();
    }

    /// <summary>How many GDI capture surfaces exist right now (all threads), for diagnostics.</summary>
    public static int LiveSurfaceCount => Volatile.Read(ref _liveSurfaces);
}
