using System.ComponentModel;
using System.Runtime.InteropServices;
using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Vision;
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

    // Extra margin captured around the search area, and when to enlarge areas before OCR so small/mixed fonts are crisp.
    private const int CapturePadding = 48;
    private const int SmallAreaWidth = 2200;
    private const int SmallAreaHeight = 1400;
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

    public bool SupportsPreprocessing => true;

    public string StatusText { get; }

    /// <summary>All monitors (physical pixels); shared with the image capture, re-read at most every 500 ms.</summary>
    public ScreenRect GetVirtualScreen() => ScreenCapture.VirtualScreen();

    public Task<OcrReadResult> ReadAsync(ScreenRect region, CancellationToken cancellationToken) =>
        ReadAsync(region, OcrPreprocessMode.Auto, cancellationToken);

    public Task<OcrReadResult> ReadAsync(ScreenRect region, OcrPreprocessMode preprocess, CancellationToken cancellationToken) =>
        ReadCoreAsync(windowHandle: 0, region, preprocess, cancellationToken);

    public Task<OcrReadResult> ReadWindowAsync(long windowHandle, ScreenRect region, CancellationToken cancellationToken) =>
        ReadCoreAsync(windowHandle, region, OcrPreprocessMode.Auto, cancellationToken);

    private async Task<OcrReadResult> ReadCoreAsync(
        long windowHandle,
        ScreenRect region,
        OcrPreprocessMode preprocess,
        CancellationToken cancellationToken)
    {
        var engine = _engine ?? throw new OcrUnavailableException(StatusText);
        var screen = GetVirtualScreen();

        var requested = region.Intersect(screen);
        if (requested.IsEmpty)
            throw new ArgumentException($"The search area {region} is outside the screen. Pick the area again.");

        // Windows OCR struggles with tightly cropped text, so capture a margin around the area so words touching the border don't get cut off.
        var area = new ScreenRect(requested.X - CapturePadding, requested.Y - CapturePadding,
            requested.Width + CapturePadding * 2, requested.Height + CapturePadding * 2).Intersect(screen);
        if (windowHandle != 0)
        {
            var frame = ScreenCapture.CaptureWindowOrScreen(new IntPtr(windowHandle), area);
            return await RecognizeBufferAsync(engine, frame.Image.Pixels, frame.Bounds, requested, preprocess, cancellationToken).ConfigureAwait(false);
        }

        var rawPixels = ScreenCapture.CaptureBgra(area);
        return await RecognizeBufferAsync(engine, rawPixels, area, requested, preprocess, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Multi-pass OCR read: runs the requested preprocess mode first, and if <paramref name="accept"/> returns false,
    /// retries on the same captured screenshot with HighContrast and Adaptive BinarizeOtsu (for mixed fonts, faint glyphs,
    /// or busy/dirty backgrounds) plus slight ±5° deskew for tilted words, merging any newly recognized words.
    /// </summary>
    public async Task<OcrReadResult> ReadMultiPassAsync(
        ScreenRect region,
        OcrPreprocessMode primaryPreprocess,
        Func<OcrReadResult, bool> accept,
        CancellationToken cancellationToken)
    {
        var engine = _engine ?? throw new OcrUnavailableException(StatusText);
        var screen = GetVirtualScreen();
        var requested = region.Intersect(screen);
        if (requested.IsEmpty)
            throw new ArgumentException($"The search area {region} is outside the screen. Pick the area again.");

        var area = new ScreenRect(requested.X - CapturePadding, requested.Y - CapturePadding,
            requested.Width + CapturePadding * 2, requested.Height + CapturePadding * 2).Intersect(screen);
        var rawPixels = ScreenCapture.CaptureBgra(area);

        var first = await RecognizeBufferAsync(engine, rawPixels, area, requested, primaryPreprocess, cancellationToken).ConfigureAwait(false);
        if (accept(first)) return first;

        var combinedLines = new List<OcrLineBox>(first.Lines);

        // Fallback Pass 2: HighContrast or BinarizeOtsu (strips noisy/busy background textures or boosts small mixed-font contrast)
        var fallbackModes = primaryPreprocess switch
        {
            OcrPreprocessMode.BinarizeOtsu => new[] { OcrPreprocessMode.HighContrast, OcrPreprocessMode.Auto },
            OcrPreprocessMode.HighContrast => new[] { OcrPreprocessMode.BinarizeOtsu, OcrPreprocessMode.InvertLightOnDark },
            _ => new[] { OcrPreprocessMode.HighContrast, OcrPreprocessMode.BinarizeOtsu },
        };

        foreach (var mode in fallbackModes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pass = await RecognizeBufferAsync(engine, rawPixels, area, requested, mode, cancellationToken).ConfigureAwait(false);
            if (pass.Lines.Count > 0)
            {
                combinedLines.AddRange(pass.Lines);
                var merged = new OcrReadResult(requested, OcrWordDeduplicator.DeduplicateLines(combinedLines));
                if (accept(merged)) return merged;
            }
        }

        // Fallback Pass 3: Slight deskew (-5° and +5°) for tilted/rotated words in mixed-font boards
        foreach (var angleDeg in new[] { -5, 5 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rotatedPass = await RecognizeRotatedBufferAsync(engine, rawPixels, area, requested, angleDeg, cancellationToken).ConfigureAwait(false);
            if (rotatedPass.Lines.Count > 0)
            {
                combinedLines.AddRange(rotatedPass.Lines);
                var merged = new OcrReadResult(requested, OcrWordDeduplicator.DeduplicateLines(combinedLines));
                if (accept(merged)) return merged;
            }
        }

        return new OcrReadResult(requested, OcrWordDeduplicator.DeduplicateLines(combinedLines));
    }

    private static async Task<OcrReadResult> RecognizeBufferAsync(
        OcrEngine engine,
        byte[] rawPixels,
        ScreenRect area,
        ScreenRect requested,
        OcrPreprocessMode preprocess,
        CancellationToken cancellationToken)
    {
        var img = new BgraImage(area.Width, area.Height, rawPixels);
        var scale = area.Width <= SmallAreaWidth && area.Height <= SmallAreaHeight ? UpscaleFactor : 1;
        if (scale > 1)
            img = OcrImagePreprocessor.UpscaleBilinear2x(img);
        if (preprocess != OcrPreprocessMode.Auto)
            img = OcrImagePreprocessor.Apply(img, preprocess);

        var pixels = img.Pixels;
        var width = img.Width;
        var height = img.Height;
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

        return new OcrReadResult(requested, OcrWordDeduplicator.DeduplicateLines(lines));
    }

    private static async Task<OcrReadResult> RecognizeRotatedBufferAsync(
        OcrEngine engine,
        byte[] rawPixels,
        ScreenRect area,
        ScreenRect requested,
        int angleDeg,
        CancellationToken cancellationToken)
    {
        var baseImg = new BgraImage(area.Width, area.Height, rawPixels);
        var rotated = EdgeShapeMatcher.Rotate(baseImg, angleDeg);
        var scale = rotated.Width <= SmallAreaWidth && rotated.Height <= SmallAreaHeight ? UpscaleFactor : 1;
        if (scale > 1)
            rotated = OcrImagePreprocessor.UpscaleBilinear2x(rotated);

        var pixels = rotated.Pixels;
        var width = rotated.Width;
        var height = rotated.Height;
        var maxTile = (int)Math.Min(OcrEngine.MaxImageDimension, 4000u);
        var lines = new List<OcrLineBox>();
        double rad = -angleDeg * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        double cx = area.Width * 0.5, cy = area.Height * 0.5;

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
                var words = new List<OcrWordBox>();
                foreach (var w in line.Words)
                {
                    double rx = (tile.X + w.BoundingRect.X + w.BoundingRect.Width * 0.5) / scale - cx;
                    double ry = (tile.Y + w.BoundingRect.Y + w.BoundingRect.Height * 0.5) / scale - cy;
                    double ux = rx * cos - ry * sin + cx;
                    double uy = rx * sin + ry * cos + cy;
                    int ww = Math.Max(1, (int)Math.Round(w.BoundingRect.Width / scale));
                    int wh = Math.Max(1, (int)Math.Round(w.BoundingRect.Height / scale));
                    var box = new ScreenRect(
                        area.X + (int)Math.Round(ux - ww * 0.5),
                        area.Y + (int)Math.Round(uy - wh * 0.5),
                        ww,
                        wh);
                    if (box.IntersectsWith(requested))
                        words.Add(new OcrWordBox(w.Text, box));
                }
                if (words.Count > 0) lines.Add(new OcrLineBox(words));
            }
        }

        return new OcrReadResult(requested, OcrWordDeduplicator.DeduplicateLines(lines));
    }

    /// <summary>
    /// Runs Windows OCR directly on an already-captured <see cref="ScreenFrame"/> in memory (no second screen capture),
    /// returning word boxes mapped to <paramref name="frame"/>'s screen coordinates. Used off-thread by Intent Recording
    /// to read button/menu labels as they looked right before the click landed.
    /// </summary>
    public async Task<OcrReadResult> ReadImageAsync(ScreenFrame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var engine = _engine ?? throw new OcrUnavailableException(StatusText);
        var area = frame.Bounds;
        if (area.IsEmpty || frame.Image.Width <= 0 || frame.Image.Height <= 0)
            return new OcrReadResult(area, Array.Empty<OcrLineBox>());

        var pixels = frame.Image.Pixels;
        var scale = frame.Image.Width < SmallAreaWidth || frame.Image.Height < SmallAreaHeight ? UpscaleFactor : 1;
        var width = frame.Image.Width * scale;
        var height = frame.Image.Height * scale;
        if (scale > 1) pixels = Upscale(pixels, frame.Image.Width, frame.Image.Height, scale);

        var maxTile = (int)Math.Min(OcrEngine.MaxImageDimension, 4000u);
        var lines = new List<OcrLineBox>();
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
                    .ToList();
                if (words.Count > 0) lines.Add(new OcrLineBox(words));
            }
        }

        return new OcrReadResult(area, OcrWordDeduplicator.DeduplicateLines(lines));
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
/// GDI handles: the whole process shares at most TWO reusable buffers (<see cref="Vision.ReusableGdiSurface"/>), each a memory
/// DC + DIB section guarded by its own lock – a "small" one for areas up to <see cref="MaxReusedPixels"/> (Hot-Spot
/// spots, Smart-Aim crops, feedback bars, click snapshots) and a "large" one for whole search areas / monitors up to
/// <see cref="MaxLargeReusedPixels"/>. A buffer is only re-created when its (width, height) changes; the BitBlt and the
/// copy into a new managed array happen inside the lock, so any thread can call this (async playback hops between
/// thread-pool threads) without one GDI bitmap per thread being left behind. Bigger areas use a one-shot surface that
/// is freed before returning. <see cref="ReleaseAllResources"/> (App.OnExit) frees both buffers.
/// </summary>
internal static class ScreenCapture
{
    /// <summary>Largest area (pixels) captured through the shared "small" buffer: 1024 × 1024 (4 MB).</summary>
    public const int MaxReusedPixels = 1024 * 1024;

    /// <summary>Largest area (pixels) captured through the shared "large" buffer: one 4K monitor (≈ 33 MB).</summary>
    public const int MaxLargeReusedPixels = 3840 * 2160;

    private static readonly Vision.ReusableGdiSurface SmallSurface = new("small");
    private static readonly Vision.ReusableGdiSurface LargeSurface = new("large");

    /// <summary>How long the virtual-screen rectangle (all monitors) is reused before it's asked from Windows again.</summary>
    public static readonly TimeSpan VirtualScreenCacheTime = TimeSpan.FromMilliseconds(500);

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

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
    /// <see cref="Vision.CaptureShield"/>). False only for the click snapshot on the mouse-hook thread, which must never
    /// wait: it then also never waits for the shared buffer (a one-shot surface is used if another capture is running).
    /// </param>
    public static byte[] CaptureBgra(ScreenRect area, bool shield = true)
    {
        if (area.IsEmpty) throw new ArgumentException("The capture area is empty.", nameof(area));
        if (shield) Vision.CaptureShield.BeforeCapture();

        // Physical pixels on every monitor, whatever thread this runs on (see PhysicalPixelsScope).
        using var dpi = NativeMethods.PhysicalPixelsScope.Enter();
        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) throw new Win32Exception("Could not access the screen.");

        try
        {
            var pixelCount = (long)area.Width * area.Height;
            var shared = pixelCount <= MaxReusedPixels ? SmallSurface
                : pixelCount <= MaxLargeReusedPixels ? LargeSurface
                : null;
            if (shared is not null && shared.TryCapture(screenDc, area, wait: shield, out var pixels))
                return pixels;

            // Huge area (several monitors at once), or the hook thread found the buffer busy: one-shot surface.
            return Vision.ReusableGdiSurface.CaptureOnce(screenDc, area);
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>
    /// Attempts to capture <paramref name="area"/> from <paramref name="hwnd"/> using DWM <c>PrintWindow(PW_RENDERFULLCONTENT)</c>
    /// when the target window is occluded behind other windows (or when a direct window surface read is requested),
    /// falling back to standard desktop <c>BitBlt</c> if the window doesn't support <c>PrintWindow</c>.
    /// </summary>
    public static ScreenFrame CaptureWindowOrScreen(IntPtr hwnd, ScreenRect area)
    {
        if (area.IsEmpty) throw new ArgumentException("The capture area is empty.", nameof(area));
        Vision.CaptureShield.BeforeCapture();

        using var dpi = NativeMethods.PhysicalPixelsScope.Enter();
        if (hwnd != IntPtr.Zero && NativeMethods.GetForegroundWindow() != hwnd)
        {
            var screenDc = NativeMethods.GetDC(IntPtr.Zero);
            if (screenDc != IntPtr.Zero)
            {
                try
                {
                    if (LargeSurface.TryCaptureWindow(screenDc, hwnd, area, out var capturedBounds, out var winPixels))
                        return new ScreenFrame(capturedBounds, new BgraImage(capturedBounds.Width, capturedBounds.Height, winPixels));
                }
                finally
                {
                    NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
                }
            }
        }

        var clipped = area.Intersect(VirtualScreen());
        if (clipped.IsEmpty)
            throw new ArgumentException($"The search area {area} is outside the screen. Pick the area again.");
        var pixels = CaptureBgra(clipped, shield: false);
        return new ScreenFrame(clipped, new BgraImage(clipped.Width, clipped.Height, pixels));
    }

    /// <summary>
    /// Kept for callers of the old per-thread cache: there is nothing per thread any more, so this does nothing.
    /// Use <see cref="ReleaseAllResources"/> to free the shared buffers.
    /// </summary>
    public static void ReleaseThreadResources()
    {
    }

    /// <summary>
    /// App exit / low memory: frees both shared GDI buffers (waiting for a capture in progress to finish first). A later
    /// capture simply creates them again.
    /// </summary>
    public static void ReleaseAllResources()
    {
        SmallSurface.Dispose();
        LargeSurface.Dispose();
    }

    /// <summary>How many GDI capture surfaces exist right now (shared + one-shot), for diagnostics.</summary>
    public static int LiveSurfaceCount => Vision.ReusableGdiSurface.LiveSurfaceCount;
}
