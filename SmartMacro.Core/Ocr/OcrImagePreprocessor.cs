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
    /// Computes Otsu's optimal global threshold and binarizes the image into clean black text on a white background,
    /// stripping mid-tone particle/scene clutter.
    /// </summary>
    public static BgraImage BinarizeOtsu(BgraImage image)
    {
        int w = image.Width, h = image.Height, n = w * h;
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
        int threshold = 128;

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
                threshold = t;
            }
        }

        // Count how many pixels are above vs below threshold so the background becomes white (255) and text becomes black (0).
        int brightCount = 0;
        for (int i = 0; i < n; i++)
            if (lum[i] > threshold) brightCount++;
        bool invertPolarity = brightCount < n / 2; // dark background with bright text -> invert so text is black on white

        var dst = new byte[src.Length];
        for (int i = 0, k = 0; i < n; i++, k += 4)
        {
            bool isBright = lum[i] > threshold;
            byte v = (invertPolarity ? !isBright : isBright) ? (byte)255 : (byte)0;
            dst[k] = v;
            dst[k + 1] = v;
            dst[k + 2] = v;
            dst[k + 3] = 255;
        }

        return new BgraImage(w, h, dst);
    }
}
