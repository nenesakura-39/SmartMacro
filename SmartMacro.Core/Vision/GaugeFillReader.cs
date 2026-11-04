using System.Globalization;
using SmartMacro.Core.Ocr;

namespace SmartMacro.Core.Vision;

/// <summary>Fill direction of a HUD bar or progress bar (v1.6 "Eyes").</summary>
public enum GaugeDirection
{
    LeftToRight,
    RightToLeft,
    BottomToTop,
    TopToBottom,
}

/// <summary>
/// Normalized 0..1 rectangle relative to a window or monitor, enabling resolution-independent regions
/// (e.g. a HUD bar captured at 1920×1080 scales automatically to 2560×1440 or 3840×2160).
/// </summary>
public readonly record struct RelativeRect(double X, double Y, double Width, double Height)
{
    public bool IsValid => Width > 0 && Height > 0 && X >= 0 && Y >= 0 && X + Width <= 1.0001 && Y + Height <= 1.0001;

    public static RelativeRect FromScreenRect(ScreenRect region, ScreenRect container)
    {
        if (container.IsEmpty || region.IsEmpty) return default;
        var rx = Math.Clamp((double)(region.X - container.X) / container.Width, 0.0, 1.0);
        var ry = Math.Clamp((double)(region.Y - container.Y) / container.Height, 0.0, 1.0);
        var rw = Math.Clamp((double)region.Width / container.Width, 0.0001, 1.0 - rx);
        var rh = Math.Clamp((double)region.Height / container.Height, 0.0001, 1.0 - ry);
        return new RelativeRect(Math.Round(rx, 6), Math.Round(ry, 6), Math.Round(rw, 6), Math.Round(rh, 6));
    }

    public ScreenRect ToScreenRect(ScreenRect container)
    {
        if (container.IsEmpty || Width <= 0 || Height <= 0) return default;
        var x = container.X + (int)Math.Round(X * container.Width, MidpointRounding.AwayFromZero);
        var y = container.Y + (int)Math.Round(Y * container.Height, MidpointRounding.AwayFromZero);
        var w = Math.Max(1, (int)Math.Round(Width * container.Width, MidpointRounding.AwayFromZero));
        var h = Math.Max(1, (int)Math.Round(Height * container.Height, MidpointRounding.AwayFromZero));
        return new ScreenRect(x, y, w, h);
    }
}

/// <summary>
/// Reads the fill percentage (0–100 %) of a game HUD bar (HP, Mana, Stamina, XP, Cast bar) or a work progress bar
/// (upload, render, build progress) by scanning slices along the bar's axis for pixels matching the bar's fill colour.
/// </summary>
public static class GaugeFillReader
{
    /// <summary>
    /// Minimum fraction of pixels in a perpendicular slice (column or row) that must match the fill colour
    /// for that slice to count as filled. 0.25 (25 %) handles rounded/beveled bars and text overlays cleanly.
    /// </summary>
    public const double SliceFillThreshold = 0.25;

    public static bool TryParseHexColor(string? hex, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var s = hex.Trim();
        if (s.StartsWith('#')) s = s[1..];
        if (s.Length != 6) return false;
        return byte.TryParse(s.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)
            && byte.TryParse(s.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)
            && byte.TryParse(s.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
    }

    /// <summary>
    /// Measures how full <paramref name="frame"/> is (0.0 to 100.0 %) with <paramref name="hexColor"/>
    /// within per-channel <paramref name="tolerance"/> along <paramref name="direction"/>.
    /// </summary>
    public static double MeasureFillPercent(
        ScreenFrame frame,
        string? hexColor,
        int tolerance = 32,
        GaugeDirection direction = GaugeDirection.LeftToRight)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var img = frame.Image;
        if (img.Width <= 0 || img.Height <= 0) return 0;

        if (!TryParseHexColor(hexColor, out var targetR, out var targetG, out var targetB))
            return 0;

        tolerance = Math.Clamp(tolerance, 0, 255);
        var pixels = img.Pixels;
        var width = img.Width;
        var height = img.Height;

        var isHorizontal = direction is GaugeDirection.LeftToRight or GaugeDirection.RightToLeft;
        var sliceCount = isHorizontal ? width : height;
        var crossLength = isHorizontal ? height : width;
        var minMatchingInSlice = Math.Max(1, (int)Math.Ceiling(crossLength * SliceFillThreshold));

        var filledSlices = 0;
        var highestFilledIndex = -1;

        for (var s = 0; s < sliceCount; s++)
        {
            // Map logical slice index s (0 = start of bar, sliceCount - 1 = end of bar) to pixel coordinate.
            var coord = direction switch
            {
                GaugeDirection.LeftToRight => s,
                GaugeDirection.RightToLeft => width - 1 - s,
                GaugeDirection.TopToBottom => s,
                GaugeDirection.BottomToTop => height - 1 - s,
                _ => s,
            };

            var matchedInSlice = 0;
            if (isHorizontal)
            {
                var x = coord;
                for (var y = 0; y < height; y++)
                {
                    var idx = (y * width + x) * 4;
                    if (Math.Abs(pixels[idx] - targetB) <= tolerance
                        && Math.Abs(pixels[idx + 1] - targetG) <= tolerance
                        && Math.Abs(pixels[idx + 2] - targetR) <= tolerance)
                    {
                        matchedInSlice++;
                    }
                }
            }
            else
            {
                var y = coord;
                var rowStart = y * width * 4;
                for (var x = 0; x < width; x++)
                {
                    var idx = rowStart + x * 4;
                    if (Math.Abs(pixels[idx] - targetB) <= tolerance
                        && Math.Abs(pixels[idx + 1] - targetG) <= tolerance
                        && Math.Abs(pixels[idx + 2] - targetR) <= tolerance)
                    {
                        matchedInSlice++;
                    }
                }
            }

            if (matchedInSlice >= minMatchingInSlice)
            {
                filledSlices++;
                highestFilledIndex = s;
            }
        }

        if (filledSlices == 0) return 0;

        // Use the span from start (0) to highestFilledIndex when the bar is contiguous (so numbers/text printed on top
        // of the middle of an HP/progress bar don't reduce the reported percentage), provided at least half the slices
        // up to highestFilledIndex matched; otherwise fall back to filledSlices / sliceCount.
        var spanSlices = highestFilledIndex + 1;
        var effectiveFilled = filledSlices >= spanSlices * 0.5 ? spanSlices : filledSlices;
        var pct = effectiveFilled * 100.0 / sliceCount;
        return Math.Round(Math.Clamp(pct, 0.0, 100.0), 1);
    }
}
