using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace SmartMacro.Controls;

/// <summary>
/// Interactive 2D target reticle and image thumbnail preview.
/// Lets the user click or drag directly on a captured template/thumbnail (or radar grid)
/// to set <see cref="OffsetX"/> and <see cref="OffsetY"/> visually, with a 1-click center reset.
/// </summary>
public sealed class OffsetCrosshairPreview : FrameworkElement
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source),
        typeof(ImageSource),
        typeof(OffsetCrosshairPreview),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OffsetXProperty = DependencyProperty.Register(
        nameof(OffsetX),
        typeof(int),
        typeof(OffsetCrosshairPreview),
        new FrameworkPropertyMetadata(
            0,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OffsetYProperty = DependencyProperty.Register(
        nameof(OffsetY),
        typeof(int),
        typeof(OffsetCrosshairPreview),
        new FrameworkPropertyMetadata(
            0,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush SurfaceBrush = Frozen(Color.FromRgb(0x09, 0x0D, 0x16));
    private static readonly Brush CheckerBrushA = Frozen(Color.FromRgb(0x0E, 0x14, 0x22));
    private static readonly Brush CheckerBrushB = Frozen(Color.FromRgb(0x13, 0x1B, 0x2E));
    private static readonly Pen BorderPen = FrozenPen(Color.FromArgb(0x55, 0x38, 0xBD, 0xF8), 1.0);
    private static readonly Pen GridPen = FrozenPen(Color.FromArgb(0x22, 0x64, 0x74, 0x8B), 0.8);
    private static readonly Pen OriginAxisPen = FrozenPen(Color.FromArgb(0x50, 0x94, 0xA3, 0xB8), 1.0);
    private static readonly Pen VectorLinePen = FrozenPen(Color.FromArgb(0xAA, 0x22, 0xD3, 0xEE), 1.4);
    private static readonly Pen CrosshairGlowPen = FrozenPen(Color.FromArgb(0x50, 0x22, 0xD3, 0xEE), 4.0);
    private static readonly Pen CrosshairPen = FrozenPen(Color.FromRgb(0x22, 0xD3, 0xEE), 1.6);
    private static readonly Brush CenterDotBrush = Frozen(Color.FromArgb(0xAA, 0x94, 0xA3, 0xB8));
    private static readonly Brush TargetDotBrush = Frozen(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly Brush BadgeBgBrush = Frozen(Color.FromArgb(0xCC, 0x0B, 0x0F, 0x1A));
    private static readonly Pen BadgeBorderPen = FrozenPen(Color.FromArgb(0x55, 0x33, 0x41, 0x55), 1.0);
    private static readonly Brush ResetBadgeBgBrush = Frozen(Color.FromArgb(0xDD, 0x1E, 0x29, 0x3B));
    private static readonly Pen ResetBadgeBorderPen = FrozenPen(Color.FromArgb(0x88, 0x38, 0xBD, 0xF8), 1.0);
    private static readonly Brush BadgeTextBrush = Frozen(Color.FromRgb(0xE2, 0xE8, 0xF0));
    private static readonly Brush AccentTextBrush = Frozen(Color.FromRgb(0x38, 0xBD, 0xF8));
    private static readonly Typeface MonoTypeface = new(new FontFamily("Cascadia Mono, Consolas, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private bool _isDragging;
    private Rect _resetButtonRect;

    public OffsetCrosshairPreview()
    {
        Height = 132;
        MinWidth = 140;
        Cursor = Cursors.Cross;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        ToolTip = "Click or drag to aim target offset (Shift = snap 5px, Double-click = reset 0,0)";
    }

    public ImageSource? Source
    {
        get => (ImageSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public int OffsetX
    {
        get => (int)GetValue(OffsetXProperty);
        set => SetValue(OffsetXProperty, value);
    }

    public int OffsetY
    {
        get => (int)GetValue(OffsetYProperty);
        set => SetValue(OffsetYProperty, value);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var pos = e.GetPosition(this);

        if ((OffsetX != 0 || OffsetY != 0) && _resetButtonRect.Contains(pos))
        {
            OffsetX = 0;
            OffsetY = 0;
            e.Handled = true;
            return;
        }

        if (e.ClickCount >= 2)
        {
            OffsetX = 0;
            OffsetY = 0;
            e.Handled = true;
            return;
        }

        _isDragging = true;
        CaptureMouse();
        UpdateOffsetFromPoint(pos);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var pos = e.GetPosition(this);
        if (_isDragging)
        {
            UpdateOffsetFromPoint(pos);
            e.Handled = true;
        }
        else
        {
            Cursor = (OffsetX != 0 || OffsetY != 0) && _resetButtonRect.Contains(pos)
                ? Cursors.Hand
                : Cursors.Cross;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_isDragging)
        {
            _isDragging = false;
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _isDragging = false;
    }

    private void UpdateOffsetFromPoint(Point pt)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 20 || h <= 20) return;

        var center = new Point(w / 2.0, h / 2.0);
        var scale = GetPixelsPerUnit(w, h);
        if (scale <= 0.001) scale = 1.0;

        var rawDx = (int)Math.Round((pt.X - center.X) / scale);
        var rawDy = (int)Math.Round((pt.Y - center.Y) / scale);

        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            rawDx = (int)Math.Round(rawDx / 5.0) * 5;
            rawDy = (int)Math.Round(rawDy / 5.0) * 5;
        }

        OffsetX = rawDx;
        OffsetY = rawDy;
    }

    private double GetPixelsPerUnit(double w, double h)
    {
        var img = Source;
        if (img is not null && img.Width > 1 && img.Height > 1)
        {
            var maxDrawW = Math.Max(40, w - 28);
            var maxDrawH = Math.Max(40, h - 36);
            var scaleX = maxDrawW / img.Width;
            var scaleY = maxDrawH / img.Height;
            return Math.Clamp(Math.Min(scaleX, scaleY), 0.25, 4.0);
        }

        var maxExtent = Math.Max(100, Math.Max(Math.Abs(OffsetX), Math.Abs(OffsetY)) + 20);
        var radius = Math.Max(30, Math.Min(w, h) * 0.40);
        return radius / maxExtent;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 20 || h <= 20) return;

        var bounds = new Rect(0.5, 0.5, w - 1, h - 1);
        dc.DrawRoundedRectangle(SurfaceBrush, BorderPen, bounds, 8, 8);

        dc.PushClip(new RectangleGeometry(bounds, 8, 8));

        // Subtle radar grid
        var center = new Point(Math.Round(w / 2.0), Math.Round(h / 2.0));
        const double step = 20.0;
        for (var x = center.X % step; x < w; x += step)
            dc.DrawLine(GridPen, new Point(x, 0), new Point(x, h));
        for (var y = center.Y % step; y < h; y += step)
            dc.DrawLine(GridPen, new Point(0, y), new Point(w, y));

        var scale = GetPixelsPerUnit(w, h);

        // Draw image thumbnail if available
        var img = Source;
        if (img is not null && img.Width > 1 && img.Height > 1)
        {
            var drawW = img.Width * scale;
            var drawH = img.Height * scale;
            var imgRect = new Rect(center.X - drawW / 2.0, center.Y - drawH / 2.0, drawW, drawH);

            // Subtle checker behind image
            dc.DrawRectangle(CheckerBrushA, null, imgRect);
            dc.DrawRectangle(CheckerBrushB, BorderPen, new Rect(imgRect.X - 1, imgRect.Y - 1, imgRect.Width + 2, imgRect.Height + 2));
            dc.DrawImage(img, imgRect);
        }

        // Origin axes (0, 0)
        dc.DrawLine(OriginAxisPen, new Point(center.X - 10, center.Y), new Point(center.X + 10, center.Y));
        dc.DrawLine(OriginAxisPen, new Point(center.X, center.Y - 10), new Point(center.X, center.Y + 10));
        dc.DrawEllipse(CenterDotBrush, null, center, 2.5, 2.5);

        // Target crosshair point
        var targetX = Math.Clamp(center.X + OffsetX * scale, 8, w - 8);
        var targetY = Math.Clamp(center.Y + OffsetY * scale, 8, h - 8);
        var targetPt = new Point(targetX, targetY);

        if (OffsetX != 0 || OffsetY != 0)
        {
            dc.DrawLine(VectorLinePen, center, targetPt);
        }

        // Outer glow + crisp reticle ring + crosshairs
        dc.DrawEllipse(null, CrosshairGlowPen, targetPt, 9, 9);
        dc.DrawEllipse(null, CrosshairPen, targetPt, 8, 8);
        dc.DrawLine(CrosshairPen, new Point(targetPt.X - 13, targetPt.Y), new Point(targetPt.X - 4, targetPt.Y));
        dc.DrawLine(CrosshairPen, new Point(targetPt.X + 4, targetPt.Y), new Point(targetPt.X + 13, targetPt.Y));
        dc.DrawLine(CrosshairPen, new Point(targetPt.X, targetPt.Y - 13), new Point(targetPt.X, targetPt.Y - 4));
        dc.DrawLine(CrosshairPen, new Point(targetPt.X, targetPt.Y + 4), new Point(targetPt.X, targetPt.Y + 13));
        dc.DrawEllipse(TargetDotBrush, null, targetPt, 2.4, 2.4);

        // Top-left coordinate telemetry pill
        var dpu = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var label = OffsetX == 0 && OffsetY == 0
            ? "Aim: Center (0, 0)"
            : $"Aim: {OffsetX:+#;-#;0}, {OffsetY:+#;-#;0} px";
        var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, MonoTypeface, 10.5, BadgeTextBrush, dpu);
        var pillRect = new Rect(8, 7, ft.Width + 14, ft.Height + 6);
        dc.DrawRoundedRectangle(BadgeBgBrush, BadgeBorderPen, pillRect, 5, 5);
        dc.DrawText(ft, new Point(pillRect.X + 7, pillRect.Y + 3));

        // Top-right Reset (0,0) pill when non-zero
        if (OffsetX != 0 || OffsetY != 0)
        {
            var resetFt = new FormattedText("Reset (0,0)", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, MonoTypeface, 10.0, AccentTextBrush, dpu);
            _resetButtonRect = new Rect(w - resetFt.Width - 22, 7, resetFt.Width + 14, resetFt.Height + 6);
            dc.DrawRoundedRectangle(ResetBadgeBgBrush, ResetBadgeBorderPen, _resetButtonRect, 5, 5);
            dc.DrawText(resetFt, new Point(_resetButtonRect.X + 7, _resetButtonRect.Y + 3));
        }
        else
        {
            _resetButtonRect = Rect.Empty;
        }

        dc.Pop();
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
