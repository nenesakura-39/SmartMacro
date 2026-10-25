using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Vision;
using SmartMacro.Services.Input;
using SmartMacro.Services.Vision;

namespace SmartMacro.Views;

/// <summary>
/// Helper for applying <c>WDA_EXCLUDEFROMCAPTURE</c> capture protection to overlay windows when OBS mode is off.
/// </summary>
public static class WindowCaptureProtection
{
    public static void Apply(Window window, bool? showInObs = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (showInObs.HasValue)
            OverlayCaptureService.AllowObsCapture = showInObs.Value;
        CaptureShield.TryExclude(window);
    }
}

/// <summary>
/// AAA Click-Through Cyber-Studio Telemetry HUD for <see cref="Core.Models.FeedbackLoopStep"/>.
/// Guarantees zero occlusion of the <c>SearchRegion</c> by framing it with an 8px outer margin and applying
/// <c>WDA_EXCLUDEFROMCAPTURE</c> via <see cref="WindowCaptureProtection"/> / <see cref="CaptureShield"/>.
/// </summary>
public partial class FeedbackTelemetryOverlay : Window
{
    // Zero-occlusion outer margin around SearchRegion (strictly >= 8px outside the ROI).
    public const int OuterMarginPx = 8;

    private const int PadLeft = 36;
    private const int PadRight = 36;
    private const int PadTop = 48;
    private const int PadBottom = 38;

    // Cyber-Studio Palette
    private static readonly Color DeepNavyColor = Color.FromRgb(0x0B, 0x11, 0x20);
    private static readonly Color ElectricCyanColor = Color.FromRgb(0x00, 0xD4, 0xFF);
    private static readonly Color VioletColor = Color.FromRgb(0x8B, 0x5C, 0xF6);
    private static readonly Color EmeraldColor = Color.FromRgb(0x10, 0xB9, 0x81);
    private static readonly Color CoralColor = Color.FromRgb(0xFF, 0x3B, 0x6B);
    private static readonly Color AmberColor = Color.FromRgb(0xF5, 0x9E, 0x0B);
    private static readonly Color RedColor = Color.FromRgb(0xEF, 0x44, 0x44);

    private static readonly SolidColorBrush CyanBrush = FrozenBrush(ElectricCyanColor);
    private static readonly SolidColorBrush CoralBrush = FrozenBrush(CoralColor);
    private static readonly SolidColorBrush EmeraldBrush = FrozenBrush(EmeraldColor);
    private static readonly SolidColorBrush AmberBrush = FrozenBrush(AmberColor);
    private static readonly SolidColorBrush VioletBrush = FrozenBrush(VioletColor);
    private static readonly SolidColorBrush RedBrush = FrozenBrush(RedColor);

    private static readonly DropShadowEffect CyanGlow = FrozenGlow(ElectricCyanColor, 8);
    private static readonly DropShadowEffect CoralGlow = FrozenGlow(CoralColor, 8);

    private ScreenRect _windowBounds;
    private FeedbackTelemetryData? _lastData;

    public FeedbackTelemetryOverlay()
    {
        InitializeComponent();
    }

    private static SolidColorBrush FrozenBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static DropShadowEffect FrozenGlow(Color c, double blur)
    {
        var g = new DropShadowEffect { Color = c, BlurRadius = blur, ShadowDepth = 0, Opacity = 0.9 };
        g.Freeze();
        return g;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLongValue(hwnd, NativeMethods.GWL_EXSTYLE);
        style |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_LAYERED;
        NativeMethods.SetWindowLongValue(hwnd, NativeMethods.GWL_EXSTYLE, style);

        WindowCaptureProtection.Apply(this);
        OverlayCaptureService.Changed += OnOverlayCaptureChanged;
        Closed += (_, _) => OverlayCaptureService.Changed -= OnOverlayCaptureChanged;
        ApplyWindowBounds();
    }

    private void OnOverlayCaptureChanged(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_lastData is { } d && IsVisible)
                RenderTelemetry(d);
        });
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Dispatcher.InvokeAsync(() =>
        {
            ApplyWindowBounds();
            if (_lastData is { } d) RenderTelemetry(d);
        });
    }

    /// <summary>Updates the overlay with the latest servo telemetry snapshot (call on UI thread).</summary>
    public void UpdateTelemetry(FeedbackTelemetryData? data)
    {
        if (data is null)
        {
            HideNow();
            return;
        }

        _lastData = data;
        var roi = data.SearchRegion;
        if (roi.Width <= 0 || roi.Height <= 0)
        {
            HideNow();
            return;
        }

        // Ensure window is wide enough to hold both the padded ROI and the telemetry pill banner (~380px).
        var minWidth = Math.Max(roi.Width + PadLeft + PadRight, 400);
        var extraHoriz = Math.Max(0, (minWidth - (roi.Width + PadLeft + PadRight)) / 2);
        var winX = roi.X - PadLeft - extraHoriz;
        var winY = roi.Y - PadTop;
        var winW = roi.Width + (PadLeft + extraHoriz) + (PadRight + extraHoriz);
        var winH = roi.Height + PadTop + PadBottom;

        var newBounds = new ScreenRect(winX, winY, winW, winH);
        var boundsChanged = _windowBounds != newBounds;
        _windowBounds = newBounds;

        if (!IsVisible)
            Show();

        if (boundsChanged)
            ApplyWindowBounds();

        RenderTelemetry(data);
    }

    public void HideNow()
    {
        _lastData = null;
        CaptureShield.MarkHidden(this);
        if (IsVisible)
            Hide();
    }

    private void ApplyWindowBounds()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || _windowBounds.IsEmpty) return;
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_TOPMOST,
            _windowBounds.X,
            _windowBounds.Y,
            _windowBounds.Width,
            _windowBounds.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    private void RenderTelemetry(FeedbackTelemetryData data)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        HudCanvas.LayoutTransform = new ScaleTransform(1.0 / dpi.DpiScaleX, 1.0 / dpi.DpiScaleY);
        HudCanvas.Width = _windowBounds.Width;
        HudCanvas.Height = _windowBounds.Height;

        var roi = data.SearchRegion;
        double roiX = roi.X - _windowBounds.X;
        double roiY = roi.Y - _windowBounds.Y;
        double roiW = Math.Max(4, roi.Width);
        double roiH = Math.Max(4, roi.Height);

        var (stateColor, stateBrush) = GetStatePalette(data.State);
        var allowObs = OverlayCaptureService.AllowObsCapture;

        // 1. Update Compact Telemetry Pill Banner docked above the SearchRegion
        StateDot.Fill = stateBrush;
        StateText.Text = $"SERVO [{data.StateLabel}]";
        StateText.Foreground = stateBrush;
        ErrorText.Text = string.Format(CultureInfo.InvariantCulture, "ERR: {0:+0;-0;0}px", Math.Round(data.ErrorPx));
        PwmText.Text = string.Format(CultureInfo.InvariantCulture, "PWM: {0:P0}", data.DutyCycle);
        OnTargetText.Text = string.Format(CultureInfo.InvariantCulture, "ON-TGT: {0:0}%", Math.Clamp(data.OnTargetPercent, 0.0, 100.0));
        ThrustBarFill.Width = Math.Clamp(data.DutyCycle * 52.0, 0.0, 52.0);
        TelemetryPill.BorderBrush = stateBrush;
        PillGlow.Color = stateColor;
        PillGlow.BlurRadius = allowObs ? 0.0 : 10.0;
        PillGlow.Opacity = allowObs ? 0.0 : 0.7;

        TelemetryPill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var pillW = TelemetryPill.DesiredSize.Width;
        var pillLeft = Math.Clamp(roiX + (roiW - pillW) * 0.5, 2.0, Math.Max(2.0, _windowBounds.Width - pillW - 2.0));
        var pillTop = Math.Max(2.0, roiY - 30.0 - OuterMarginPx);
        Canvas.SetLeft(TelemetryPill, pillLeft);
        Canvas.SetTop(TelemetryPill, pillTop);

        // 2. Redraw Dynamic Tactical Overlay Layer ( strictly outside [roiX, roiY, roiW, roiH] by >= 8px )
        DynamicLayer.Children.Clear();

        double frameLeft = roiX - OuterMarginPx;
        double frameTop = roiY - OuterMarginPx;
        double frameRight = roiX + roiW + OuterMarginPx;
        double frameBottom = roiY + roiH + OuterMarginPx;

        // Tactical Corner-Bracket Frame around outer border of SearchRegion
        var arm = Math.Clamp(Math.Min(roiW, roiH) * 0.32, 8.0, 22.0);
        AddCornerBracket(frameLeft, frameTop, arm, arm, stateBrush);
        AddCornerBracket(frameRight, frameTop, -arm, arm, stateBrush);
        AddCornerBracket(frameLeft, frameBottom, arm, -arm, stateBrush);
        AddCornerBracket(frameRight, frameBottom, -arm, -arm, stateBrush);

        var isHorizontal = data.Orientation == BarOrientation.Horizontal;

        // 3. Player Bar Span Track ([══════]) along the outside edge of the ROI in #00D4FF Cyan
        if (data.IndicatorFound)
        {
            if (isHorizontal)
            {
                var x1 = roiX + Math.Clamp(data.IndicatorMinEdge, 0, roiW);
                var x2 = roiX + Math.Clamp(data.IndicatorMaxEdge, 0, roiW);
                if (x2 < x1 + 2) x2 = x1 + 2;
                var trackY = frameBottom + 6.0;

                // Main cyan span bar (disable blur glow when AllowObsCapture is on so BitBlt never captures glow inside ROI)
                var spanLine = MakeLine(x1, trackY, x2, trackY, CyanBrush, 3.0);
                spanLine.Effect = allowObs ? null : CyanGlow;
                DynamicLayer.Children.Add(spanLine);

                // Left '[' and Right ']' end-cap brackets
                DynamicLayer.Children.Add(MakeLine(x1, trackY - 4, x1, trackY + 4, CyanBrush, 2.0));
                DynamicLayer.Children.Add(MakeLine(x1, trackY - 4, x1 + 3, trackY - 4, CyanBrush, 1.5));
                DynamicLayer.Children.Add(MakeLine(x1, trackY + 4, x1 + 3, trackY + 4, CyanBrush, 1.5));

                DynamicLayer.Children.Add(MakeLine(x2, trackY - 4, x2, trackY + 4, CyanBrush, 2.0));
                DynamicLayer.Children.Add(MakeLine(x2 - 3, trackY - 4, x2, trackY - 4, CyanBrush, 1.5));
                DynamicLayer.Children.Add(MakeLine(x2 - 3, trackY + 4, x2, trackY + 4, CyanBrush, 1.5));
            }
            else
            {
                var y1 = roiY + Math.Clamp(data.IndicatorMinEdge, 0, roiH);
                var y2 = roiY + Math.Clamp(data.IndicatorMaxEdge, 0, roiH);
                if (y2 < y1 + 2) y2 = y1 + 2;
                var trackX = frameRight + 6.0;

                var spanLine = MakeLine(trackX, y1, trackX, y2, CyanBrush, 3.0);
                spanLine.Effect = allowObs ? null : CyanGlow;
                DynamicLayer.Children.Add(spanLine);

                // Top and Bottom end-cap brackets
                DynamicLayer.Children.Add(MakeLine(trackX - 4, y1, trackX + 4, y1, CyanBrush, 2.0));
                DynamicLayer.Children.Add(MakeLine(trackX - 4, y2, trackX + 4, y2, CyanBrush, 2.0));
            }
        }

        // 4. Target Caret Marker (▼ / ▶) in #FF3B6B Neon Coral + Velocity Vector Line
        if (data.TargetFound)
        {
            if (isHorizontal)
            {
                var tx = roiX + Math.Clamp(data.TargetPos, 0, roiW);
                var caretTipY = frameBottom + 11.0;

                var caret = new Polygon
                {
                    Points = new PointCollection
                    {
                        new(tx, caretTipY),
                        new(tx - 5.5, caretTipY + 8.0),
                        new(tx + 5.5, caretTipY + 8.0),
                    },
                    Fill = CoralBrush,
                    Effect = allowObs ? null : CoralGlow,
                };
                DynamicLayer.Children.Add(caret);

                // Velocity vector line showing target motion
                var velPx = Math.Clamp(data.TargetVelocityPxPerSec * 0.12, -48.0, 48.0);
                if (Math.Abs(velPx) >= 2.0)
                {
                    var vecY = caretTipY + 12.0;
                    var endX = Math.Clamp(tx + velPx, roiX, roiX + roiW);
                    var vecLine = MakeLine(tx, vecY, endX, vecY, CoralBrush, 2.0);
                    DynamicLayer.Children.Add(vecLine);

                    var dir = Math.Sign(velPx);
                    DynamicLayer.Children.Add(MakeLine(endX, vecY, endX - dir * 4, vecY - 3, CoralBrush, 1.8));
                    DynamicLayer.Children.Add(MakeLine(endX, vecY, endX - dir * 4, vecY + 3, CoralBrush, 1.8));
                }
            }
            else
            {
                var ty = roiY + Math.Clamp(data.TargetPos, 0, roiH);
                var caretTipX = frameRight + 11.0;

                var caret = new Polygon
                {
                    Points = new PointCollection
                    {
                        new(caretTipX, ty),
                        new(caretTipX + 8.0, ty - 5.5),
                        new(caretTipX + 8.0, ty + 5.5),
                    },
                    Fill = CoralBrush,
                    Effect = allowObs ? null : CoralGlow,
                };
                DynamicLayer.Children.Add(caret);

                // Velocity vector line showing target motion along Y
                var velPx = Math.Clamp(data.TargetVelocityPxPerSec * 0.12, -48.0, 48.0);
                if (Math.Abs(velPx) >= 2.0)
                {
                    var vecX = caretTipX + 12.0;
                    var endY = Math.Clamp(ty + velPx, roiY, roiY + roiH);
                    var vecLine = MakeLine(vecX, ty, vecX, endY, CoralBrush, 2.0);
                    DynamicLayer.Children.Add(vecLine);

                    var dir = Math.Sign(velPx);
                    DynamicLayer.Children.Add(MakeLine(vecX, endY, vecX - 3, endY - dir * 4, CoralBrush, 1.8));
                    DynamicLayer.Children.Add(MakeLine(vecX, endY, vecX + 3, endY - dir * 4, CoralBrush, 1.8));
                }
            }
        }
    }

    private static (Color Color, SolidColorBrush Brush) GetStatePalette(ServoTelemetryState state) => state switch
    {
        ServoTelemetryState.Learning => (VioletColor, VioletBrush),
        ServoTelemetryState.Locked => (EmeraldColor, EmeraldBrush),
        ServoTelemetryState.Tracking => (ElectricCyanColor, CyanBrush),
        ServoTelemetryState.Braking => (AmberColor, AmberBrush),
        ServoTelemetryState.WallDrop or ServoTelemetryState.WallHold => (VioletColor, VioletBrush),
        _ => (RedColor, RedBrush),
    };

    private void AddCornerBracket(double x, double y, double dx, double dy, Brush brush)
    {
        DynamicLayer.Children.Add(MakeLine(x, y, x + dx, y, brush, 2.5));
        DynamicLayer.Children.Add(MakeLine(x, y, x, y + dy, brush, 2.5));
    }

    private static Line MakeLine(double x1, double y1, double x2, double y2, Brush stroke, double thickness) => new()
    {
        X1 = x1,
        Y1 = y1,
        X2 = x2,
        Y2 = y2,
        Stroke = stroke,
        StrokeThickness = thickness,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
    };
}
