namespace SmartMacro.Core.Vision;

/// <summary>
/// Normalizes a custom screen ROI into bounded [0..1] radar preview fractions across single- or multi-monitor desktops,
/// guaranteeing min &lt;= max clamping for monitors at wide positive or negative coordinates.
/// </summary>
public static class SearchRegionLayout
{
    public static (double Rx, double Ry, double Rw, double Rh) ComputeNormalizedZone(
        int regionX,
        int regionY,
        int regionWidth,
        int regionHeight,
        double desktopLeft,
        double desktopTop,
        double desktopWidth,
        double desktopHeight)
    {
        var rightEdge = Math.Max(desktopLeft + Math.Max(1.0, desktopWidth), regionX + Math.Max(0, regionWidth));
        var bottomEdge = Math.Max(desktopTop + Math.Max(1.0, desktopHeight), regionY + Math.Max(0, regionHeight));
        var left = Math.Min(desktopLeft, regionX);
        var top = Math.Min(desktopTop, regionY);
        var spanW = Math.Max(1.0, rightEdge - left);
        var spanH = Math.Max(1.0, bottomEdge - top);

        var rx = Math.Clamp((regionX - left) / spanW, 0.0, 0.90);
        var ry = Math.Clamp((regionY - top) / spanH, 0.0, 0.90);
        var maxW = Math.Max(0.02, 1.0 - rx);
        var maxH = Math.Max(0.02, 1.0 - ry);
        var rw = regionWidth > 0
            ? Math.Clamp(regionWidth / spanW, Math.Min(0.08, maxW), maxW)
            : Math.Min(0.45, maxW);
        var rh = regionHeight > 0
            ? Math.Clamp(regionHeight / spanH, Math.Min(0.10, maxH), maxH)
            : Math.Min(0.45, maxH);
        return (rx, ry, rw, rh);
    }
}
