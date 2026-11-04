using System.Windows;
using System.Windows.Media;
using SmartMacro.Core.Models;

namespace SmartMacro.Controls;

/// <summary>
/// Live vector visualizer for the Inspector's Cursor Movement section.
/// Renders the easing / trajectory profile (Instant jump, Smooth Glide S-curve, or Human Curve arc)
/// with glowing start/end nodes and sample ticks.
/// </summary>
public sealed class MotionCurvePreview : FrameworkElement
{
    public static readonly DependencyProperty MoveStyleProperty = DependencyProperty.Register(
        nameof(MoveStyle),
        typeof(MouseMoveStyle),
        typeof(MotionCurvePreview),
        new FrameworkPropertyMetadata(MouseMoveStyle.SmoothGlide, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DurationMsProperty = DependencyProperty.Register(
        nameof(DurationMs),
        typeof(int),
        typeof(MotionCurvePreview),
        new FrameworkPropertyMetadata(180, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush SurfaceBrush = Frozen(Color.FromArgb(0x55, 0x0B, 0x0F, 0x1A));
    private static readonly Pen BorderPen = FrozenPen(Color.FromArgb(0x40, 0x38, 0xBD, 0xF8), 1.0);
    private static readonly Pen GridPen = FrozenPen(Color.FromArgb(0x1E, 0x94, 0xA3, 0xB8), 0.8);
    private static readonly Pen GlowPen = FrozenPen(Color.FromArgb(0x45, 0x22, 0xD3, 0xEE), 4.0);
    private static readonly Pen CurvePen = FrozenPen(Color.FromRgb(0x38, 0xBD, 0xF8), 2.0);
    private static readonly Brush DotBrush = Frozen(Color.FromRgb(0x22, 0xD3, 0xEE));
    private static readonly Brush EndDotBrush = Frozen(Color.FromRgb(0xA8, 0x55, 0xF7));
    private static readonly Brush TickBrush = Frozen(Color.FromArgb(0xAA, 0xE2, 0xE8, 0xF0));

    public MotionCurvePreview()
    {
        Height = 52;
        Margin = new Thickness(0, 8, 0, 2);
        SnapsToDevicePixels = true;
    }

    public MouseMoveStyle MoveStyle
    {
        get => (MouseMoveStyle)GetValue(MoveStyleProperty);
        set => SetValue(MoveStyleProperty, value);
    }

    public int DurationMs
    {
        get => (int)GetValue(DurationMsProperty);
        set => SetValue(DurationMsProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 20 || h <= 16) return;

        dc.DrawRoundedRectangle(SurfaceBrush, BorderPen, new Rect(0.5, 0.5, w - 1, h - 1), 8, 8);

        var padX = 16.0;
        var padY = 12.0;
        var plotW = Math.Max(10, w - padX * 2);
        var plotH = Math.Max(8, h - padY * 2);

        // Baseline & midline
        dc.DrawLine(GridPen, new Point(padX, padY + plotH / 2), new Point(padX + plotW, padY + plotH / 2));

        var startPt = new Point(padX, padY + plotH - 2);
        var endPt = new Point(padX + plotW, padY + 2);

        var geom = new StreamGeometry();
        using (var ctx = geom.Open())
        {
            ctx.BeginFigure(startPt, isFilled: false, isClosed: false);
            if (MoveStyle == MouseMoveStyle.Instant)
            {
                ctx.LineTo(new Point(padX + plotW * 0.5, startPt.Y), isStroked: true, isSmoothJoin: false);
                ctx.LineTo(new Point(padX + plotW * 0.5, endPt.Y), isStroked: true, isSmoothJoin: false);
                ctx.LineTo(endPt, isStroked: true, isSmoothJoin: false);
            }
            else
            {
                const int samples = 28;
                for (var i = 1; i <= samples; i++)
                {
                    var t = i / (double)samples;
                    var eased = t * t * (3 - 2 * t);
                    var arc = MoveStyle == MouseMoveStyle.HumanCurve
                        ? Math.Sin(t * Math.PI) * (plotH * 0.28)
                        : 0.0;
                    var x = startPt.X + (endPt.X - startPt.X) * t;
                    var y = startPt.Y + (endPt.Y - startPt.Y) * eased - arc;
                    ctx.LineTo(new Point(x, Math.Clamp(y, 5, h - 5)), isStroked: true, isSmoothJoin: true);
                }
            }
        }
        geom.Freeze();

        dc.DrawGeometry(null, GlowPen, geom);
        dc.DrawGeometry(null, CurvePen, geom);

        // Sample cadence dots along the curve for SmoothGlide / HumanCurve
        if (MoveStyle != MouseMoveStyle.Instant)
        {
            var dots = Math.Clamp(DurationMs / 30, 4, 14);
            for (var i = 1; i < dots; i++)
            {
                var t = i / (double)dots;
                var eased = t * t * (3 - 2 * t);
                var arc = MoveStyle == MouseMoveStyle.HumanCurve
                    ? Math.Sin(t * Math.PI) * (plotH * 0.28)
                    : 0.0;
                var x = startPt.X + (endPt.X - startPt.X) * t;
                var y = Math.Clamp(startPt.Y + (endPt.Y - startPt.Y) * eased - arc, 5, h - 5);
                dc.DrawEllipse(TickBrush, null, new Point(x, y), 1.6, 1.6);
            }
        }

        dc.DrawEllipse(DotBrush, null, startPt, 3.8, 3.8);
        dc.DrawEllipse(EndDotBrush, null, endPt, 4.2, 4.2);
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
