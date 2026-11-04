using System.IO.Compression;
using SmartMacro.Core.Ocr;

namespace SmartMacro.Core.Vision;

/// <summary>
/// A plain 32-bit picture: 4 bytes per pixel in Blue, Green, Red, Alpha order, rows top to bottom, no padding.
/// That's the layout Windows screen capture (GDI) gives us, so no conversion is needed.
/// Alpha is ignored everywhere (screen captures don't have a meaningful one).
/// </summary>
public sealed class BgraImage
{
    public BgraImage(int width, int height, byte[] pixels)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "The image must be at least 1×1.");
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length != (long)width * height * 4)
            throw new ArgumentException($"Expected {(long)width * height * 4} bytes for a {width}×{height} image, got {pixels.Length}.", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>The raw BGRA bytes (Width × Height × 4).</summary>
    public byte[] Pixels { get; }

    public int Stride => Width * 4;

    /// <summary>Creates a blank (black) image.</summary>
    public static BgraImage Create(int width, int height) => new(width, height, new byte[checked(width * height * 4)]);

    public RgbColor GetPixel(int x, int y)
    {
        var i = (y * Width + x) * 4;
        return new RgbColor(Pixels[i + 2], Pixels[i + 1], Pixels[i]);
    }

    public void SetPixel(int x, int y, RgbColor color)
    {
        var i = (y * Width + x) * 4;
        Pixels[i] = color.B;
        Pixels[i + 1] = color.G;
        Pixels[i + 2] = color.R;
        Pixels[i + 3] = 255;
    }

    /// <summary>Fills a rectangle (clipped to the image). Handy for tests and previews.</summary>
    public void FillRectangle(ScreenRect rect, RgbColor color)
    {
        var area = rect.Intersect(new ScreenRect(0, 0, Width, Height));
        for (var y = area.Y; y < area.Bottom; y++)
            for (var x = area.X; x < area.Right; x++)
                SetPixel(x, y, color);
    }

    /// <summary>Draws a rectangle outline (clipped to the image), e.g. to mark a match in a preview picture.</summary>
    public void DrawRectangle(ScreenRect rect, RgbColor color, int thickness = 2)
    {
        for (var t = 0; t < thickness; t++)
        {
            var r = new ScreenRect(rect.X - t, rect.Y - t, rect.Width + t * 2, rect.Height + t * 2);
            for (var x = r.X; x < r.Right; x++)
            {
                SetPixelClipped(x, r.Y, color);
                SetPixelClipped(x, r.Bottom - 1, color);
            }

            for (var y = r.Y; y < r.Bottom; y++)
            {
                SetPixelClipped(r.X, y, color);
                SetPixelClipped(r.Right - 1, y, color);
            }
        }
    }

    /// <summary>Copies part of the image. The rectangle is in this image's own pixel coordinates.</summary>
    public BgraImage Crop(ScreenRect rect)
    {
        var area = rect.Intersect(new ScreenRect(0, 0, Width, Height));
        if (area.IsEmpty) throw new ArgumentException("The crop area is outside the image.", nameof(rect));
        var result = Create(area.Width, area.Height);
        for (var row = 0; row < area.Height; row++)
            Buffer.BlockCopy(Pixels, ((area.Y + row) * Width + area.X) * 4, result.Pixels, row * result.Stride, result.Stride);
        return result;
    }

    public BgraImage Clone() => new(Width, Height, (byte[])Pixels.Clone());

    /// <summary>
    /// Resizes this image to <paramref name="newWidth"/> × <paramref name="newHeight"/> using separable resampling:
    /// exact continuous area averaging when shrinking (so thin 1-pixel strokes never vanish between samples) and
    /// bilinear interpolation when enlarging. Alpha is written as 255.
    /// </summary>
    public BgraImage Resize(int newWidth, int newHeight)
    {
        if (newWidth <= 0) throw new ArgumentOutOfRangeException(nameof(newWidth), "The resized width must be at least 1.");
        if (newHeight <= 0) throw new ArgumentOutOfRangeException(nameof(newHeight), "The resized height must be at least 1.");
        if (newWidth == Width && newHeight == Height) return Clone();

        var xKernel = BuildResampleKernel(Width, newWidth);
        var yKernel = BuildResampleKernel(Height, newHeight);

        // Pass 1: horizontal resample (Width -> newWidth) for every source row.
        var tempB = new float[newWidth * Height];
        var tempG = new float[newWidth * Height];
        var tempR = new float[newWidth * Height];
        var src = Pixels;
        var srcStride = Width * 4;

        for (var y = 0; y < Height; y++)
        {
            var srcRow = y * srcStride;
            var dstRow = y * newWidth;
            for (var x = 0; x < newWidth; x++)
            {
                var (start, weights) = xKernel[x];
                float b = 0, g = 0, r = 0;
                var idx = srcRow + start * 4;
                for (var k = 0; k < weights.Length; k++, idx += 4)
                {
                    var w = weights[k];
                    b += src[idx] * w;
                    g += src[idx + 1] * w;
                    r += src[idx + 2] * w;
                }

                tempB[dstRow + x] = b;
                tempG[dstRow + x] = g;
                tempR[dstRow + x] = r;
            }
        }

        // Pass 2: vertical resample (Height -> newHeight) into the destination BGRA buffer.
        var dst = new byte[checked(newWidth * newHeight * 4)];
        for (var y = 0; y < newHeight; y++)
        {
            var (start, weights) = yKernel[y];
            var dstIdx = y * newWidth * 4;
            for (var x = 0; x < newWidth; x++, dstIdx += 4)
            {
                float b = 0, g = 0, r = 0;
                for (var k = 0; k < weights.Length; k++)
                {
                    var w = weights[k];
                    var tIdx = (start + k) * newWidth + x;
                    b += tempB[tIdx] * w;
                    g += tempG[tIdx] * w;
                    r += tempR[tIdx] * w;
                }

                dst[dstIdx] = ClampToByte(b);
                dst[dstIdx + 1] = ClampToByte(g);
                dst[dstIdx + 2] = ClampToByte(r);
                dst[dstIdx + 3] = 255;
            }
        }

        return new BgraImage(newWidth, newHeight, dst);
    }

    private static byte ClampToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v), 0, 255);

    private static (int Start, float[] Weights)[] BuildResampleKernel(int srcLength, int dstLength)
    {
        var kernel = new (int Start, float[] Weights)[dstLength];
        if (dstLength < srcLength)
        {
            // Shrinking: integrate continuously over [i * scale, (i + 1) * scale] so every source pixel contributes.
            var scale = (double)srcLength / dstLength;
            for (var i = 0; i < dstLength; i++)
            {
                var u0 = i * scale;
                var u1 = (i + 1) * scale;
                var first = Math.Clamp((int)Math.Floor(u0), 0, srcLength - 1);
                var last = Math.Clamp((int)Math.Ceiling(u1) - 1, first, srcLength - 1);
                var count = last - first + 1;
                var weights = new float[count];
                double total = 0;
                for (var k = 0; k < count; k++)
                {
                    var srcIdx = first + k;
                    var overlap = Math.Min(u1, srcIdx + 1.0) - Math.Max(u0, srcIdx);
                    if (overlap > 0)
                    {
                        weights[k] = (float)overlap;
                        total += overlap;
                    }
                }

                if (total > 0)
                {
                    var inv = (float)(1.0 / total);
                    for (var k = 0; k < count; k++) weights[k] *= inv;
                }
                else
                {
                    weights[0] = 1f;
                }

                kernel[i] = (first, weights);
            }
        }
        else
        {
            // Enlarging (or 1:1): pixel-center aligned linear interpolation.
            var scale = (double)srcLength / dstLength;
            for (var i = 0; i < dstLength; i++)
            {
                var u = Math.Clamp((i + 0.5) * scale - 0.5, 0.0, srcLength - 1.0);
                var k0 = (int)Math.Floor(u);
                var k1 = Math.Min(k0 + 1, srcLength - 1);
                var frac = (float)(u - k0);
                if (k0 == k1 || frac <= 1e-6f)
                    kernel[i] = (k0, new[] { 1f });
                else
                    kernel[i] = (k0, new[] { 1f - frac, frac });
            }
        }

        return kernel;
    }

    private void SetPixelClipped(int x, int y, RgbColor color)
    {
        if (x >= 0 && y >= 0 && x < Width && y < Height) SetPixel(x, y, color);
    }
}

/// <summary>A screen capture: the picture plus where on the screen it came from (physical pixels).</summary>
public sealed record ScreenFrame(ScreenRect Bounds, BgraImage Image)
{
    /// <summary>Turns a rectangle inside the picture into screen coordinates.</summary>
    public ScreenRect ToScreen(ScreenRect local) => new(Bounds.X + local.X, Bounds.Y + local.Y, local.Width, local.Height);

    /// <summary>
    /// Crops a physical screen rectangle out of this capture, clamping safely to <c>[0, Image.Width] × [0, Image.Height]</c>
    /// across multi-monitor and negative secondary-monitor coordinates.
    /// </summary>
    public ScreenFrame? CropFromScreen(ScreenRect screenRect)
    {
        var local = new ScreenRect(
            screenRect.X - Bounds.X,
            screenRect.Y - Bounds.Y,
            screenRect.Width,
            screenRect.Height).Intersect(new ScreenRect(0, 0, Image.Width, Image.Height));
        if (local.IsEmpty) return null;
        return new ScreenFrame(ToScreen(local), Image.Crop(local));
    }
}

/// <summary>An RGB colour. Written as "#RRGGBB" in macro files.</summary>
public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public static RgbColor Black => new(0, 0, 0);
    public static RgbColor White => new(255, 255, 255);

    /// <summary>Accepts "#RRGGBB" or "RRGGBB" (upper or lower case).</summary>
    public static bool TryParse(string? text, out RgbColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s.StartsWith('#')) s = s[1..];
        if (s.Length != 6) return false;
        if (!int.TryParse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var value))
            return false;
        color = new RgbColor((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    /// <summary>Largest difference of the three channels (0 = identical, 255 = opposite).</summary>
    public int MaxChannelDifference(RgbColor other) =>
        Math.Max(Math.Abs(R - other.R), Math.Max(Math.Abs(G - other.G), Math.Abs(B - other.B)));

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";

    public static implicit operator string(RgbColor color) => color.ToString();
    public static implicit operator RgbColor(string? text) => TryParse(text, out var color) ? color : default;
}

/// <summary>
/// Stores a template picture inside the macro file as text: the pixels without alpha (B, G, R per pixel),
/// zlib-compressed, then Base64. Keeping it inside the macro means export / import (one .smartmacro file)
/// keeps working, and the zlib checksum detects a damaged picture.
/// </summary>
public static class TemplateCodec
{
    public static string Encode(BgraImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var bgr = new byte[image.Width * image.Height * 3];
        for (int i = 0, j = 0; i < image.Pixels.Length; i += 4, j += 3)
        {
            bgr[j] = image.Pixels[i];
            bgr[j + 1] = image.Pixels[i + 1];
            bgr[j + 2] = image.Pixels[i + 2];
        }

        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(bgr, 0, bgr.Length);
        return Convert.ToBase64String(output.ToArray());
    }

    /// <summary>Decodes a stored template. Returns false (with a plain-English reason) if it's missing or damaged.</summary>
    public static bool TryDecode(string? data, int width, int height, out BgraImage? image, out string? error)
    {
        image = null;
        if (string.IsNullOrEmpty(data))
        {
            error = "No image has been captured yet.";
            return false;
        }

        if (width <= 0 || height <= 0 || (long)width * height > 40_000_000)
        {
            error = "The image size is invalid.";
            return false;
        }

        byte[] compressed;
        try
        {
            compressed = Convert.FromBase64String(data);
        }
        catch (FormatException)
        {
            error = "The stored image is damaged (not valid Base64).";
            return false;
        }

        var expected = width * height * 3;
        var bgr = new byte[expected];
        try
        {
            using var input = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress);
            var read = 0;
            while (read < expected)
            {
                var n = input.Read(bgr, read, expected - read);
                if (n == 0) break;
                read += n;
            }

            // Too short, or extra bytes after the expected size = wrong size or damaged.
            if (read != expected || input.ReadByte() != -1)
            {
                error = "The stored image doesn't match its width and height. The macro may be damaged; capture the image again.";
                return false;
            }
        }
        catch (InvalidDataException)
        {
            error = "The stored image is damaged. Capture the image again.";
            return false;
        }

        var pixels = new byte[width * height * 4];
        for (int i = 0, j = 0; j < bgr.Length; i += 4, j += 3)
        {
            pixels[i] = bgr[j];
            pixels[i + 1] = bgr[j + 1];
            pixels[i + 2] = bgr[j + 2];
            pixels[i + 3] = 255;
        }

        image = new BgraImage(width, height, pixels);
        error = null;
        return true;
    }
}
