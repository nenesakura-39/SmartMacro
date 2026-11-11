using SmartMacro.Core.Models;
using SmartMacro.Core.Vision;

namespace SmartMacro.Core.Ocr;

/// <summary>
/// Vision 2.0 OCR Image Preprocessor: enhances contrast, inverts light-on-dark game HUD text, or applies Otsu adaptive
/// binarization to strip noisy backgrounds/particles behind text before running OCR.
/// </summary>
public static class OcrImagePreprocessor
{
    public static BgraImage Apply(BgraImage image, OcrPreprocessMode mode)
    {
        ArgumentNullException.ThrowIfNull(image);
        return mode switch
        {
            OcrPreprocessMode.HighContrast => StretchContrast(image),
            OcrPreprocessMode.InvertLightOnDark => Invert(image),
            OcrPreprocessMode.BinarizeOtsu => BinarizeOtsu(image),
            _ => image,
        };
    }

    /// <summary>Stretches the 2nd–98th percentile luminance range to full 0..255 contrast.</summary>
    public static BgraImage StretchContrast(BgraImage image)
    {
        int w = image.Width, h = image.Height, n = w * h;
        var src = image.Pixels;
        var hist = new int[256];
        for (int i = 0; i < src.Length; i += 4)
        {
            int lum = (src[i] * 29 + src[i + 1] * 150 + src[i + 2] * 77) >> 8;
            hist[Math.Clamp(lum, 0, 255)]++;
        }

        int lowCut = Math.Max(1, n * 2 / 100);
        int highCut = Math.Max(1, n * 98 / 100);
        int minL = 0, maxL = 255, acc = 0;
        for (int i = 0; i < 256; i++)
        {
            acc += hist[i];
            if (acc >= lowCut) { minL = i; break; }
        }

        acc = 0;
        for (int i = 0; i < 256; i++)
        {
            acc += hist[i];
            if (acc >= highCut) { maxL = i; break; }
        }

        if (maxL - minL < 12) return image.Clone();
        float scale = 255f / (maxL - minL);
        var dst = new byte[src.Length];
        for (int i = 0; i < src.Length; i += 4)
        {
            dst[i] = (byte)Math.Clamp((int)((src[i] - minL) * scale), 0, 255);
            dst[i + 1] = (byte)Math.Clamp((int)((src[i + 1] - minL) * scale), 0, 255);
            dst[i + 2] = (byte)Math.Clamp((int)((src[i + 2] - minL) * scale), 0, 255);
            dst[i + 3] = 255;
        }

        return new BgraImage(w, h, dst);
    }

    /// <summary>Inverts RGB channels so bright game HUD text on a dark background becomes dark text on a white page.</summary>
    public static BgraImage Invert(BgraImage image)
    {
        var src = image.Pixels;
        var dst = new byte[src.Length];
        for (int i = 0; i < src.Length; i += 4)
        {
            dst[i] = (byte)(255 - src[i]);
            dst[i + 1] = (byte)(255 - src[i + 1]);
            dst[i + 2] = (byte)(255 - src[i + 2]);
            dst[i + 3] = 255;
        }

        return new BgraImage(image.Width, image.Height, dst);
    }

    /// <summary>
    /// Computes adaptive block thresholding combined with Otsu's global threshold and 3x3 noise filtering to binarize
    /// the image into clean black text on a white background, stripping busy background textures and mid-tone particle clutter.
    /// </summary>
    public static BgraImage BinarizeOtsu(BgraImage image)
    {
        int w = image.Width, h = image.Height, n = w * h;
        if (n == 0) return image;
        var src = image.Pixels;
        var lum = new byte[n];
        var hist = new int[256];

        for (int i = 0, k = 0; i < n; i++, k += 4)
        {
            byte l = (byte)Math.Clamp((src[k] * 29 + src[k + 1] * 150 + src[k + 2] * 77) >> 8, 0, 255);
            lum[i] = l;
            hist[l]++;
        }

        double totalSum = 0;
        for (int t = 0; t < 256; t++) totalSum += t * hist[t];

        double sumB = 0;
        int wB = 0;
        double maxVar = -1;
        int globalThreshold = 128;

        for (int t = 0; t < 256; t++)
        {
            wB += hist[t];
            if (wB == 0) continue;
            int wF = n - wB;
            if (wF == 0) break;

            sumB += (double)t * hist[t];
            double mB = sumB / wB;
            double mF = (totalSum - sumB) / wF;
            double betweenVar = (double)wB * wF * (mB - mF) * (mB - mF);
            if (betweenVar > maxVar)
            {
                maxVar = betweenVar;
                globalThreshold = t;
            }
        }

        // Count how many pixels are above vs below threshold so the background becomes white (255) and text becomes black (0).
        int brightCount = 0;
        for (int i = 0; i < n; i++)
            if (lum[i] > globalThreshold) brightCount++;
        bool invertPolarity = brightCount < n / 2; // dark background with bright text -> invert so text is black on white

        // Build an integral image for fast local 25x25 window mean to handle uneven/busy background gradients.
        var integral = new long[(w + 1) * (h + 1)];
        int strideI = w + 1;
        for (int y = 0; y < h; y++)
        {
            long rowSum = 0;
            int srcRow = y * w;
            int dstRow = (y + 1) * strideI;
            int prevRow = y * strideI;
            for (int x = 0; x < w; x++)
            {
                rowSum += lum[srcRow + x];
                integral[dstRow + x + 1] = integral[prevRow + x + 1] + rowSum;
            }
        }

        int radius = Math.Clamp(Math.Min(w, h) / 8, 6, 18);
        var bin = new byte[n];
        for (int y = 0; y < h; y++)
        {
            int y0 = Math.Max(0, y - radius);
            int y1 = Math.Min(h, y + radius + 1);
            int rowOffset = y * w;
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Max(0, x - radius);
                int x1 = Math.Min(w, x + radius + 1);
                int area = (x1 - x0) * (y1 - y0);
                long sum = integral[y1 * strideI + x1] - integral[y0 * strideI + x1] - integral[y1 * strideI + x0] + integral[y0 * strideI + x0];
                int localMean = (int)(sum / Math.Max(1, area));

                // Blend Otsu global threshold with local adaptive threshold so both uniform and busy backgrounds segment cleanly.
                int blendedThreshold = (globalThreshold + localMean) / 2;
                int bias = invertPolarity ? 8 : -8;
                bool isForeground = invertPolarity
                    ? lum[rowOffset + x] > Math.Max(globalThreshold, localMean + bias)
                    : lum[rowOffset + x] < Math.Min(globalThreshold, localMean + bias);

                bin[rowOffset + x] = isForeground ? (byte)0 : (byte)255;
            }
        }

        // Remove isolated 1-pixel background speckle noise while preserving character strokes.
        var dst = new byte[src.Length];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int idx = y * w + x;
                byte v = bin[idx];
                if (v == 0 && x > 0 && x < w - 1 && y > 0 && y < h - 1)
                {
                    // Count black neighbors in 3x3 window; if completely isolated (0 neighbors), clear speckle.
                    int blackNeighbors = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if ((dx != 0 || dy != 0) && bin[(y + dy) * w + (x + dx)] == 0)
                                blackNeighbors++;
                        }
                    }
                    if (blackNeighbors == 0) v = 255;
                }

                int k = idx * 4;
                dst[k] = v;
                dst[k + 1] = v;
                dst[k + 2] = v;
                dst[k + 3] = 255;
            }
        }

        return new BgraImage(w, h, dst);
    }

    /// <summary>Smooth 2x bilinear upscaling with mild unsharp mask for small or stylized fonts.</summary>
    public static BgraImage UpscaleBilinear2x(BgraImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        int sw = image.Width, sh = image.Height;
        if (sw <= 0 || sh <= 0) return image;
        int dw = sw * 2, dh = sh * 2;
        var src = image.Pixels;
        var dst = new byte[dw * dh * 4];

        for (int dy = 0; dy < dh; dy++)
        {
            float fy = (dy + 0.5f) * 0.5f - 0.5f;
            int y0 = Math.Clamp((int)MathF.Floor(fy), 0, sh - 1);
            int y1 = Math.Clamp(y0 + 1, 0, sh - 1);
            float wy = fy - MathF.Floor(fy);
            if (fy < 0) wy = 0;

            for (int dx = 0; dx < dw; dx++)
            {
                float fx = (dx + 0.5f) * 0.5f - 0.5f;
                int x0 = Math.Clamp((int)MathF.Floor(fx), 0, sw - 1);
                int x1 = Math.Clamp(x0 + 1, 0, sw - 1);
                float wx = fx - MathF.Floor(fx);
                if (fx < 0) wx = 0;

                int i00 = (y0 * sw + x0) * 4;
                int i10 = (y0 * sw + x1) * 4;
                int i01 = (y1 * sw + x0) * 4;
                int i11 = (y1 * sw + x1) * 4;

                float w00 = (1 - wx) * (1 - wy);
                float w10 = wx * (1 - wy);
                float w01 = (1 - wx) * wy;
                float w11 = wx * wy;

                int di = (dy * dw + dx) * 4;
                dst[di] = (byte)Math.Clamp((int)(src[i00] * w00 + src[i10] * w10 + src[i01] * w01 + src[i11] * w11), 0, 255);
                dst[di + 1] = (byte)Math.Clamp((int)(src[i00 + 1] * w00 + src[i10 + 1] * w10 + src[i01 + 1] * w01 + src[i11 + 1] * w11), 0, 255);
                dst[di + 2] = (byte)Math.Clamp((int)(src[i00 + 2] * w00 + src[i10 + 2] * w10 + src[i01 + 2] * w01 + src[i11 + 2] * w11), 0, 255);
                dst[di + 3] = 255;
            }
        }

        return new BgraImage(dw, dh, dst);
    }
}
