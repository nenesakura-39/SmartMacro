using System.Globalization;
using System.Windows;
using System.Windows.Media;
using SmartMacro.Core.Models;
using SmartMacro.Core.Vision;

namespace SmartMacro.Controls;

/// <summary>
/// Compact 16:9 mini-monitor radar control for Vision steps.
/// Visually plots the active <see cref="SearchScope"/> and custom region rectangle
/// <c>(RegionX, RegionY, RegionWidth, RegionHeight)</c> against a 1920×1080 reference screen.
/// </summary>
public sealed class SearchRegionRadar : FrameworkElement
{
    public static readonly DependencyProperty ScopeProperty = DependencyProperty.Register(
        nameof(Scope),
        typeof(SearchScope),
        typeof(SearchRegionRadar),
        new FrameworkPropertyMetadata(SearchScope.PrimaryMonitor, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RegionXProperty = DependencyProperty.Register(
        nameof(RegionX),
        typeof(int),
        typeof(SearchRegionRadar),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RegionYProperty = DependencyProperty.Register(
        nameof(RegionY),
        typeof(int),
        typeof(SearchRegionRadar),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RegionWidthProperty = DependencyProperty.Register(
        nameof(RegionWidth),
        typeof(int),
        typeof(SearchRegionRadar),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RegionHeightProperty = DependencyProperty.Register(
        nameof(RegionHeight),
        typeof(int),
        typeof(SearchRegionRadar),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush OuterBgBrush = Frozen(Color.FromRgb(0x09, 0x0D, 0x16));
    private static readonly Pen OuterBorderPen = FrozenPen(Color.FromArgb(0x44, 0x33, 0x41, 0x55), 1.0);
    private static readonly Brush MonitorScreenBrush = Frozen(Color.FromRgb(0x0D, 0x14, 0x24));
    private static readonly Pen MonitorFramePen = FrozenPen(Color.FromRgb(0x33, 0x41, 0x55), 1.4);
    private static readonly Pen GridPen = FrozenPen(Color.FromArgb(0x20, 0x64, 0x74, 0x8B), 0.8);
    private static readonly Brush ActiveZoneFill = Frozen(Color.FromArgb(0x30, 0x22, 0xD3, 0xEE));
    private static readonly Pen ActiveZoneGlowPen = FrozenPen(Color.FromArgb(0x50, 0x22, 0xD3, 0xEE), 3.0);
    private static readonly Pen ActiveZonePen = FrozenPen(Color.FromRgb(0x22, 0xD3, 0xEE), 1.5);
    private static readonly Brush CornerHandleBrush = Frozen(Color.FromRgb(0x38, 0xBD, 0xF8));
    private static readonly Brush TextBrush = Frozen(Color.FromRgb(0x94, 0xA3, 0xB8));
    private static readonly Brush AccentTextBrush = Frozen(Color.FromRgb(0x22, 0xD3, 0xEE));
    private static readonly Typeface MonoTypeface = new(new FontFamily("Cascadia Mono, Consolas, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public SearchRegionRadar()
    {
        Height = 64;
        Margin = new Thickness(0, 6, 0, 2);
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    public SearchScope Scope
    {
        get => (SearchScope)GetValue(ScopeProperty);
        set => SetValue(ScopeProperty, value);
    }

    public int RegionX
    {
        get => (int)GetValue(RegionXProperty);
        set => SetValue(RegionXProperty, value);
    }

    public int RegionY
    {
        get => (int)GetValue(RegionYProperty);
        set => SetValue(RegionYProperty, value);
    }

    public int RegionWidth
    {
        get => (int)GetValue(RegionWidthProperty);
        set => SetValue(RegionWidthProperty, value);
    }

    public int RegionHeight
    {
        get => (int)GetValue(RegionHeightProperty);
        set => SetValue(RegionHeightProperty, value);
    }

    internal static (double Rx, double Ry, double Rw, double Rh) ComputeNormalizedZone(
        int regionX, int regionY, int regionWidth, int regionHeight,
        double desktopLeft, double desktopTop, double desktopWidth, double desktopHeight) =>
        SmartMacro.Core.Vision.SearchRegionLayout.ComputeNormalizedZone(
            regionX, regionY, regionWidth, regionHeight,
            desktopLeft, desktopTop, desktopWidth, desktopHeight);

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 40 || h <= 24) return;

        dc.DrawRoundedRectangle(OuterBgBrush, OuterBorderPen, new Rect(0.5, 0.5, w - 1, h - 1), 8, 8);

        // Mini 16:9 monitor frame on the left
        var monH = Math.Max(28, h - 14);
        var monW = Math.Round(monH * (16.0 / 9.0));
        var monRect = new Rect(10, (h - monH) / 2.0, monW, monH);

        dc.DrawRoundedRectangle(MonitorScreenBrush, MonitorFramePen, monRect, 4, 4);

        // Rule-of-thirds grid inside mini monitor
        dc.DrawLine(GridPen, new Point(monRect.X + monRect.Width / 3, monRect.Y), new Point(monRect.X + monRect.Width / 3, monRect.Bottom));
        dc.DrawLine(GridPen, new Point(monRect.X + monRect.Width * 2 / 3, monRect.Y), new Point(monRect.X + monRect.Width * 2 / 3, monRect.Bottom));
        dc.DrawLine(GridPen, new Point(monRect.X, monRect.Y + monRect.Height / 3), new Point(monRect.Right, monRect.Y + monRect.Height / 3));
        dc.DrawLine(GridPen, new Point(monRect.X, monRect.Y + monRect.Height * 2 / 3), new Point(monRect.Right, monRect.Y + monRect.Height * 2 / 3));

        Rect zoneRect;
        string title;
        string subtitle;

        if (Scope == SearchScope.CustomArea)
        {
            var deskLeft = SystemParameters.VirtualScreenLeft;
            var deskTop = SystemParameters.VirtualScreenTop;
            var deskW = Math.Max(1.0, SystemParameters.VirtualScreenWidth);
            var deskH = Math.Max(1.0, SystemParameters.VirtualScreenHeight);

            var (rx, ry, rw, rh) = ComputeNormalizedZone(
                RegionX, RegionY, RegionWidth, RegionHeight,
                deskLeft, deskTop, deskW, deskH);

            zoneRect = new Rect(
                monRect.X + rx * monRect.Width,
                monRect.Y + ry * monRect.Height,
                rw * monRect.Width,
                rh * monRect.Height);

            title = "ACTIVE ROI SCAN BOX";
            subtitle = RegionWidth > 0 && RegionHeight > 0
                ? $"({RegionX}, {RegionY}) · {RegionWidth}×{RegionHeight} px"
                : "Click 'Pick area on screen' to set bounds";
        }
        else if (Scope == SearchScope.Window)
        {
            zoneRect = new Rect(
                monRect.X + monRect.Width * 0.14,
                monRect.Y + monRect.Height * 0.14,
                monRect.Width * 0.72,
                monRect.Height * 0.72);
            title = "WINDOW CLIENT BOUNDS";
            subtitle = "Tracks target window wherever it moves";
        }
        else if (Scope == SearchScope.AllMonitors)
        {
            zoneRect = new Rect(monRect.X + 1.5, monRect.Y + 1.5, monRect.Width - 3, monRect.Height - 3);
            title = "ALL MONITORS (VIRTUAL DESKTOP)";
            subtitle = "Full multi-display scan area";
        }
        else if (Scope == SearchScope.Monitor)
        {
            zoneRect = new Rect(monRect.X + 2, monRect.Y + 2, monRect.Width - 4, monRect.Height - 4);
            title = "DEDICATED MONITOR SCAN";
            subtitle = "Locked to selected display";
        }
        else
        {
            zoneRect = new Rect(monRect.X + 2, monRect.Y + 2, monRect.Width - 4, monRect.Height - 4);
            title = "PRIMARY DISPLAY (100% COVERAGE)";
            subtitle = "Fast single-screen capture pipeline";
        }

        dc.DrawRoundedRectangle(ActiveZoneFill, ActiveZoneGlowPen, zoneRect, 2, 2);
        dc.DrawRoundedRectangle(null, ActiveZonePen, zoneRect, 2, 2);

        // Corner ticks on custom / window ROI
        dc.DrawEllipse(CornerHandleBrush, null, zoneRect.TopLeft, 2.0, 2.0);
        dc.DrawEllipse(CornerHandleBrush, null, zoneRect.TopRight, 2.0, 2.0);
        dc.DrawEllipse(CornerHandleBrush, null, zoneRect.BottomLeft, 2.0, 2.0);
        dc.DrawEllipse(CornerHandleBrush, null, zoneRect.BottomRight, 2.0, 2.0);

        // Telemetry labels on the right of the mini monitor
        var textX = monRect.Right + 12;
        if (textX < w - 30)
        {
            var dpu = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var titleFt = new FormattedText(title, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, MonoTypeface, 10.5, AccentTextBrush, dpu)
            {
                MaxTextWidth = Math.Max(20, w - textX - 10),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            var subFt = new FormattedText(subtitle, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, MonoTypeface, 10.0, TextBrush, dpu)
            {
                MaxTextWidth = Math.Max(20, w - textX - 10),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };

            dc.DrawText(titleFt, new Point(textX, h / 2.0 - titleFt.Height - 1));
            dc.DrawText(subFt, new Point(textX, h / 2.0 + 1));
        }
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Pen FrozenPen(Color c, double thickness)
    {
        var p = new Pen(Frozen(c), thickness);
        p.Freeze();
        return p;
    }
}
