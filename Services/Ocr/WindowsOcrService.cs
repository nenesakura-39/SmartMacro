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
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

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

    public ScreenRect GetVirtualScreen()
    {
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

/// <summary>Copies part of the screen into a 32-bit BGRA byte array (top-down rows) using GDI.</summary>
internal static class ScreenCapture
{
    public static byte[] CaptureBgra(ScreenRect area)
    {
        // Physical pixels on every monitor, whatever thread this runs on (see PhysicalPixelsScope).
        using var dpi = NativeMethods.PhysicalPixelsScope.Enter();
        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) throw new Win32Exception("Could not access the screen.");

        var memoryDc = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        try
        {
            memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
            bitmap = NativeMethods.CreateCompatibleBitmap(screenDc, area.Width, area.Height);
            if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not prepare a screen capture.");

            var previous = NativeMethods.SelectObject(memoryDc, bitmap);
            var copied = NativeMethods.BitBlt(memoryDc, 0, 0, area.Width, area.Height, screenDc, area.X, area.Y,
                NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);
            var bltError = Marshal.GetLastWin32Error();
            NativeMethods.SelectObject(memoryDc, previous); // GetDIBits needs the bitmap deselected
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

            var pixels = new byte[area.Width * area.Height * 4];
            var lines = NativeMethods.GetDIBits(memoryDc, bitmap, 0, (uint)area.Height, pixels, ref info, NativeMethods.DIB_RGB_COLORS);
            if (lines == 0) throw new Win32Exception("Could not read the captured screen pixels.");
            return pixels;
        }
        finally
        {
            if (bitmap != IntPtr.Zero) NativeMethods.DeleteObject(bitmap);
            if (memoryDc != IntPtr.Zero) NativeMethods.DeleteDC(memoryDc);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
