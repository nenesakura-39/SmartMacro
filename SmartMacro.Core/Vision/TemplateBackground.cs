using System.Runtime.CompilerServices;
using SmartMacro.Core.Ocr;

namespace SmartMacro.Core.Vision;

/// <summary>
/// The plain background around the real target inside a Find image picture, e.g. the white square around a red
/// circle, or the see-through pixels of a picture with transparency.
///
/// Why: a rectangular capture of a round target always contains background pixels in its corners / margins. When the
/// target later sits next to a dark wall or in a corner of the game box, the wall shows up exactly where the picture
/// has its white corners, and a whole-picture comparison drops from ~95 % to 30–40 %. Knowing which pixels are
/// background lets the matcher score the target itself (<see cref="BackgroundMaskMatcher"/>) and lets
/// <see cref="TrimMargins"/> cut extra blank space off, so the found rectangle is centred on the target.
///
/// Detection (pure maths, no Windows code):
/// 1. Transparency: when most of the outer 1-pixel ring is see-through (alpha &lt; 128) and the picture also has
///    solid pixels, see-through = background.
/// 2. Otherwise the outer 1-pixel ring is sampled. Its typical colour (per-channel median, then the average of the ring
///    pixels close to it) is the background colour when at least <see cref="MinPerimeterShare"/> of the ring is within
///    <see cref="ColorTolerance"/> of it (biggest channel difference).
/// 3. Every pixel within <see cref="ColorTolerance"/> of that colour is background; the rest is the foreground (the target).
/// </summary>
public sealed class TemplateBackground
{
    /// <summary>A pixel counts as background when no channel differs from the background colour by more than this.</summary>
    public const int ColorTolerance = 24;

    /// <summary>Share of the outer ring that must show the background colour for it to count as "the" background.</summary>
    public const double MinPerimeterShare = 0.6;

    /// <summary>The background must cover at least this share of the picture before it is masked out…</summary>
    public const double MinBackgroundShare = 0.25;

    /// <summary>…and the target (foreground) must be between these shares, so there is a real shape to compare.</summary>
    public const double MinForegroundShare = 0.15;

    /// <inheritdoc cref="MinForegroundShare"/>
    public const double MaxForegroundShare = 0.85;

    /// <summary>
    /// Trimming blank margins changes where the found rectangle (and its centre) is, so it is only done for a clear
    /// "one object on a plain background" picture: a ring that is (almost) all background…
    /// </summary>
    public const double TrimPerimeterShare = 0.75;

    /// <summary>…and a foreground that is mostly one connected shape (not a scattered pattern).</summary>
    public const double TrimMinLargestShapeShare = 0.8;

    /// <summary>Margin (px) kept around the target on every trimmed side.</summary>
    public const int TrimKeepMarginPx = 1;

    /// <summary>A trimmed picture never gets smaller than this on either side.</summary>
    public const int MinTrimmedSize = 6;

    // Detection runs once per picture object (decoded pictures are shared through TemplateCache).
    private static readonly ConditionalWeakTable<BgraImage, Holder> Detected = new();
    private static readonly ConditionalWeakTable<BgraImage, TrimResult> Trimmed = new();

    private readonly bool[] _foreground;

    private TemplateBackground(int width, int height, RgbColor color, bool fromAlpha, bool[] foreground, int foregroundCount,
        double perimeterShare, double backgroundShare, double largestShapeShare)
    {
        Width = width;
        Height = height;
        Color = color;
        FromAlpha = fromAlpha;
        _foreground = foreground;
        ForegroundCount = foregroundCount;
        PerimeterShare = perimeterShare;
        BackgroundShare = backgroundShare;
        LargestShapeShare = largestShapeShare;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>The background colour (unused when <see cref="FromAlpha"/>).</summary>
    public RgbColor Color { get; }

    /// <summary>True when the background was found through transparency instead of a colour.</summary>
    public bool FromAlpha { get; }

    /// <summary>Number of foreground (target) pixels.</summary>
    public int ForegroundCount { get; }

    /// <summary>Share of the outer 1-pixel ring that is background (0..1).</summary>
    public double PerimeterShare { get; }

    /// <summary>Share of the whole picture that is background (0..1).</summary>
    public double BackgroundShare { get; }

    /// <summary>Share of the foreground pixels that belong to its biggest connected shape (0..1).</summary>
    public double LargestShapeShare { get; }

    /// <summary>Share of the whole picture that is foreground (0..1).</summary>
    public double ForegroundShare => 1 - BackgroundShare;

    /// <summary>True when matching may concentrate on the foreground (enough background, a real target shape).</summary>
    public bool CanMask =>
        BackgroundShare >= MinBackgroundShare && ForegroundShare >= MinForegroundShare && ForegroundShare <= MaxForegroundShare;

    /// <summary>True when blank margins may be trimmed (see <see cref="TrimPerimeterShare"/>).</summary>
    public bool CanTrim => CanMask && PerimeterShare >= TrimPerimeterShare && LargestShapeShare >= TrimMinLargestShapeShare;

    /// <summary>Is the pixel at (x, y) part of the target (not background)?</summary>
    public bool IsForeground(int x, int y) => _foreground[y * Width + x];

    /// <summary>
    /// The background of <paramref name="template"/>, or null when it has no clear one (a busy pattern, a photo, a flat
    /// colour, a picture smaller than 6×6). Cached per picture object.
    /// </summary>
    public static TemplateBackground? Detect(BgraImage template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return Detected.GetValue(template, t => new Holder(Compute(t))).Value;
    }

    /// <summary>
    /// Cuts extra blank rows / columns (background only) off every side of the picture, keeping
    /// <see cref="TrimKeepMarginPx"/> px of background, so the picture is tightly centred on the target. Returns the
    /// picture itself when nothing is trimmed (<see cref="CanTrim"/> false, or no extra margin). <paramref name="kept"/>
    /// is the part kept, in the original picture's pixels. The same input object always gives the same output object.
    /// The trimmed picture keeps the original's background (so <see cref="Detect"/> on it still allows masking, even
    /// when only a thin margin of background is left around a round target).
    /// </summary>
    public static BgraImage TrimMargins(BgraImage template, out ScreenRect kept)
    {
        ArgumentNullException.ThrowIfNull(template);
        var result = Trimmed.GetValue(template, ComputeTrim);
        kept = result.Kept;
        return result.Image;
    }

    private static TrimResult ComputeTrim(BgraImage template)
    {
        var full = new ScreenRect(0, 0, template.Width, template.Height);
        var background = Detect(template);
        if (background is null || !background.CanTrim) return new TrimResult(template, full);

        int w = template.Width, h = template.Height;
        int top = 0, bottom = 0, left = 0, right = 0;
        while (top < h && RowIsBackground(background, top)) top++;
        while (bottom < h - top && RowIsBackground(background, h - 1 - bottom)) bottom++;
        while (left < w && ColumnIsBackground(background, left)) left++;
        while (right < w - left && ColumnIsBackground(background, w - 1 - right)) right++;

        top = Math.Max(0, top - TrimKeepMarginPx);
        bottom = Math.Max(0, bottom - TrimKeepMarginPx);
        left = Math.Max(0, left - TrimKeepMarginPx);
        right = Math.Max(0, right - TrimKeepMarginPx);
        if (top + bottom + left + right == 0) return new TrimResult(template, full);

        var kept = new ScreenRect(left, top, w - left - right, h - top - bottom);
        if (kept.Width < MinTrimmedSize || kept.Height < MinTrimmedSize) return new TrimResult(template, full);

        var cropped = template.Crop(kept);
        var croppedBackground = background.CropTo(kept);
        Detected.AddOrUpdate(cropped, new Holder(croppedBackground));
        return new TrimResult(cropped, kept);
    }

    private static bool RowIsBackground(TemplateBackground b, int y)
    {
        for (var x = 0; x < b.Width; x++)
            if (b._foreground[y * b.Width + x]) return false;
        return true;
    }

    private static bool ColumnIsBackground(TemplateBackground b, int x)
    {
        for (var y = 0; y < b.Height; y++)
            if (b._foreground[y * b.Width + x]) return false;
        return true;
    }

    /// <summary>The same background for a part of the picture (shares are kept from the whole picture).</summary>
    private TemplateBackground CropTo(ScreenRect part)
    {
        var mask = new bool[part.Width * part.Height];
        var count = 0;
        for (var y = 0; y < part.Height; y++)
        {
            for (var x = 0; x < part.Width; x++)
            {
                var fg = _foreground[(part.Y + y) * Width + part.X + x];
                mask[y * part.Width + x] = fg;
                if (fg) count++;
            }
        }

        return new TemplateBackground(part.Width, part.Height, Color, FromAlpha, mask, count, PerimeterShare, BackgroundShare, LargestShapeShare);
    }

    private static TemplateBackground? Compute(BgraImage template)
    {
        int w = template.Width, h = template.Height;
        if (w < MinTrimmedSize || h < MinTrimmedSize) return null;
        var p = template.Pixels;
        var n = w * h;

        // The outer 1-pixel ring.
        var ring = new List<int>(2 * (w + h));
        for (var x = 0; x < w; x++)
        {
            ring.Add(x);
            ring.Add((h - 1) * w + x);
        }

        for (var y = 1; y < h - 1; y++)
        {
            ring.Add(y * w);
            ring.Add(y * w + w - 1);
        }

        // 1. Transparency. (Screen captures leave alpha 0 everywhere and decoded templates have 255 everywhere: a
        // picture that is see-through on its border but solid inside is a real transparent picture.)
        var ringClear = ring.Count(i => p[i * 4 + 3] < 128);
        var solid = 0;
        for (var i = 3; i < p.Length; i += 4)
            if (p[i] >= 128) solid++;

        var foreground = new bool[n];
        int foregroundCount = 0;
        double perimeterShare;
        RgbColor color;
        var fromAlpha = ringClear >= MinPerimeterShare * ring.Count && solid > 0 && solid < n;
        if (fromAlpha)
        {
            for (var i = 0; i < n; i++)
            {
                foreground[i] = p[i * 4 + 3] >= 128;
                if (foreground[i]) foregroundCount++;
            }

            perimeterShare = ringClear / (double)ring.Count;
            color = default;
        }
        else
        {
            // 2. The ring's typical colour.
            var median = new RgbColor(RingMedian(p, ring, 2), RingMedian(p, ring, 1), RingMedian(p, ring, 0));
            double sumR = 0, sumG = 0, sumB = 0;
            var close = 0;
            foreach (var i in ring)
            {
                if (!IsNear(p, i * 4, median)) continue;
                close++;
                sumR += p[i * 4 + 2];
                sumG += p[i * 4 + 1];
                sumB += p[i * 4];
            }

            perimeterShare = close / (double)ring.Count;
            if (perimeterShare < MinPerimeterShare || close == 0) return null;
            color = new RgbColor((byte)Math.Round(sumR / close), (byte)Math.Round(sumG / close), (byte)Math.Round(sumB / close));

            // 3. Everything not close to it is the target.
            for (var i = 0; i < n; i++)
            {
                foreground[i] = !IsNear(p, i * 4, color);
                if (foreground[i]) foregroundCount++;
            }
        }

        if (foregroundCount == 0) return null; // all background = one flat colour
        var largest = LargestShape(foreground, w, h) / (double)foregroundCount;
        return new TemplateBackground(w, h, color, fromAlpha, foreground, foregroundCount, perimeterShare, 1 - foregroundCount / (double)n, largest);
    }

    /// <summary>True when the pixel at byte index <paramref name="i"/> (BGRA) is within <see cref="ColorTolerance"/> of <paramref name="c"/>.</summary>
    internal static bool IsNear(byte[] p, int i, RgbColor c) =>
        Math.Abs(p[i] - c.B) <= ColorTolerance && Math.Abs(p[i + 1] - c.G) <= ColorTolerance && Math.Abs(p[i + 2] - c.R) <= ColorTolerance;

    private static byte RingMedian(byte[] p, List<int> ring, int channel)
    {
        var histogram = new int[256];
        foreach (var i in ring) histogram[p[i * 4 + channel]]++;
        var half = (ring.Count + 1) / 2;
        var seen = 0;
        for (var v = 0; v < 256; v++)
        {
            seen += histogram[v];
            if (seen >= half) return (byte)v;
        }

        return 255;
    }

    /// <summary>Pixel count of the biggest 4-connected group of true values.</summary>
    private static int LargestShape(bool[] mask, int w, int h)
    {
        var seen = new bool[mask.Length];
        var queue = new int[mask.Length];
        var best = 0;
        for (var start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || seen[start]) continue;
            int head = 0, tail = 0, size = 0;
            queue[tail++] = start;
            seen[start] = true;
            while (head < tail)
            {
                var i = queue[head++];
                size++;
                int x = i % w, y = i / w;
                if (x > 0) Visit(i - 1);
                if (x < w - 1) Visit(i + 1);
                if (y > 0) Visit(i - w);
                if (y < h - 1) Visit(i + w);
            }

            best = Math.Max(best, size);

            void Visit(int j)
            {
                if (!mask[j] || seen[j]) return;
                seen[j] = true;
                queue[tail++] = j;
            }
        }

        return best;
    }

    private sealed class Holder
    {
        public Holder(TemplateBackground? value) => Value = value;
        public TemplateBackground? Value { get; }
    }

    private sealed record TrimResult(BgraImage Image, ScreenRect Kept);
}
