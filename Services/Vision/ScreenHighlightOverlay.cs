using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Vision;
using SmartMacro.Services.Input;

namespace SmartMacro.Services.Vision;

/// <summary>
/// A short on-screen highlight for "Test detection now" and "Show search area on screen": a lightweight, click-through,
/// always-on-top, see-through window drawn right over the real screen.
/// - Cyan dashed rectangle = where the step looks (its search area).
/// - Bright green rectangle + crosshair = what was found, with a label such as "✓ 94% Match".
/// - Red rectangle = nothing was found (drawn around the search area), with a "✗ Not found" label.
/// It never takes focus, can't be clicked, is left out of screenshots (so a test can't find it), holds still for a
/// moment and then fades out and closes by itself.
/// </summary>
public sealed class ScreenHighlightOverlay : Window
{
    /// <summary>How long the highlight is fully visible before it fades out.</summary>
    public static readonly TimeSpan HoldTime = TimeSpan.FromMilliseconds(800);

    /// <summary>How long the fade-out takes.</summary>
    public static readonly TimeSpan FadeTime = TimeSpan.FromMilliseconds(350);

    private const int LabelRoom = 34; // physical px kept above / below the boxes for the label

    private static readonly Color Cyan = Color.FromRgb(0x22, 0xD3, 0xEE);
    private static readonly Color Green = Color.FromRgb(0x22, 0xFF, 0x7A);
    private static readonly Color Red = Color.FromRgb(0xFF, 0x4D, 0x4F);

    private static ScreenHighlightOverlay? _current;

    private readonly ScreenRect _bounds;        // the window, physical screen pixels
    private readonly ScreenRect? _searchArea;
    private readonly ScreenRect? _match;
    private readonly bool _found;
    private readonly string? _label;
    private readonly Canvas _canvas = new() { IsHitTestVisible = false };
    private readonly TimeSpan _hold;

    private ScreenHighlightOverlay(ScreenRect bounds, ScreenRect? searchArea, ScreenRect? match, bool found, string? label, TimeSpan hold)
    {
        _bounds = bounds;
        _searchArea = searchArea;
        _match = match;
        _found = found;
        _label = label;
        _hold = hold;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        Title = "SmartMacro highlight";
        Left = -32000; // placed exactly in physical pixels once the window handle exists
        Top = -32000;
        Width = 1;
        Height = 1;
        Content = _canvas;
    }

    /// <summary>
    /// Shows the highlight for a test result. <paramref name="match"/> is what was found (null = not found);
    /// <paramref name="label"/> e.g. "✓ 94% Match". Replaces a highlight that is still showing. Never throws (a highlight
    /// is only a visual aid).
    /// </summary>
    public static void ShowResult(ScreenRect? searchArea, ScreenRect? match, bool found, string? label, TimeSpan? hold = null)
    {
        try
        {
            var boxes = new List<ScreenRect>();
            if (searchArea is { IsEmpty: false } area) boxes.Add(area);
            if (match is { IsEmpty: false } hit) boxes.Add(hit);
            if (boxes.Count == 0) return;

            var union = boxes.Aggregate((a, b) => a.Union(b));
            // Room for the label and the glow, but never bigger than the desktop.
            var bounds = new ScreenRect(union.X - 12, union.Y - LabelRoom, union.Width + 24, union.Height + 2 * LabelRoom)
                .Intersect(VirtualScreen());
            if (bounds.IsEmpty) return;

            _current?.CloseNow();
            var overlay = new ScreenHighlightOverlay(bounds, searchArea, match, found, label, hold ?? HoldTime);
            _current = overlay;
            overlay.Show();
        }
        catch (Exception)
        {
            // Purely cosmetic: a failed highlight must never break a test.
        }
    }

    /// <summary>"🎯 Show search area on screen": outlines where a Find step / condition looks (cyan, dashed).</summary>
    public static string ShowSearchArea(ScreenSearchStep step, IScreenLayout? layout)
    {
        ArgumentNullException.ThrowIfNull(step);
        ScreenRect area;
        try
        {
            area = SearchArea.Resolve(step, VirtualScreen(), layout);
        }
        catch (SearchAreaUnavailableException ex)
        {
            return $"Can't show the search area right now: {ex.Message}";
        }

        if (area.IsEmpty) return "The search area is empty. Pick the area on screen again.";
        ShowResult(area, null, found: true, $"Search area · {area.Width}×{area.Height}", TimeSpan.FromMilliseconds(1600));
        return $"Showing the search area on screen: {area.Width}×{area.Height} at ({area.X}, {area.Y}).";
    }

    /// <summary>The whole desktop (all monitors) in physical pixels.</summary>
    private static ScreenRect VirtualScreen()
    {
        using var dpi = NativeMethods.PhysicalPixelsScope.Enter();
        return new ScreenRect(
            NativeMethods.GetSystemMetrics(76), NativeMethods.GetSystemMetrics(77),
            NativeMethods.GetSystemMetrics(78), NativeMethods.GetSystemMetrics(79));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLongValue(hwnd, NativeMethods.GWL_EXSTYLE);
        style |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLongValue(hwnd, NativeMethods.GWL_EXSTYLE, style);
        // A test (or the next playback capture) must never "find" the highlight. Old Windows, or "🎥 Show overlays in OBS /
        // Discord" on (streams / recordings see it): it's closed right before SmartMacro's own next capture instead.
        if (!CaptureShield.TryExclude(this)) CaptureShield.MarkVisible(this, CloseNow);
        ApplyBounds();
        Draw();
        StartTimer();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        // Windows resizes the window when it lands on a monitor with other scaling: put it back and redraw.
        Dispatcher.InvokeAsync(() =>
        {
            ApplyBounds();
            Draw();
        });
    }

    private void ApplyBounds()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    /// <summary>Draws the boxes. Everything is laid out in physical pixels, then scaled by the window's DPI.</summary>
    private void Draw()
    {
        _canvas.Children.Clear();
        var dpi = VisualTreeHelper.GetDpi(this);
        _canvas.LayoutTransform = new ScaleTransform(1.0 / dpi.DpiScaleX, 1.0 / dpi.DpiScaleY);
        _canvas.Width = _bounds.Width;
        _canvas.Height = _bounds.Height;

        if (_searchArea is { IsEmpty: false } area)
        {
            var color = _match is null && !_found ? Red : Cyan;
            var box = Box(area, color, thickness: 2, dashed: _found || _match is not null);
            _canvas.Children.Add(box);
        }

        if (_match is { IsEmpty: false } hit)
        {
            var color = _found ? Green : Red;
            _canvas.Children.Add(Box(Inflate(hit, 3), color, thickness: 3, dashed: false));

            // Crosshair through the centre of the match.
            var cx = hit.CenterX - _bounds.X;
            var cy = hit.CenterY - _bounds.Y;
            var arm = Math.Clamp(Math.Max(hit.Width, hit.Height) / 2 + 10, 12, 60);
            _canvas.Children.Add(MakeLine(cx - arm, cy, cx + arm, cy, color));
            _canvas.Children.Add(MakeLine(cx, cy - arm, cx, cy + arm, color));
        }

        if (!string.IsNullOrWhiteSpace(_label))
        {
            var anchor = _match is { IsEmpty: false } m ? m : _searchArea ?? _bounds;
            var color = _match is not null ? (_found ? Green : Red) : _found ? Cyan : Red;
            var label = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x0B, 0x0F, 0x17)),
                BorderBrush = new SolidColorBrush(color),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 3, 8, 3),
                Child = new TextBlock
                {
                    Text = _label,
                    Foreground = new SolidColorBrush(color),
                    FontSize = 14 * dpi.DpiScaleY, // the canvas is scaled back down, so this ends up as 14 px text
                    FontWeight = FontWeights.SemiBold,
                },
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var x = Math.Clamp(anchor.X - _bounds.X, 0, Math.Max(0, _bounds.Width - label.DesiredSize.Width));
            var above = anchor.Y - _bounds.Y - label.DesiredSize.Height - 6;
            var y = above >= 0 ? above : Math.Min(anchor.Bottom - _bounds.Y + 6, Math.Max(0, _bounds.Height - label.DesiredSize.Height));
            Canvas.SetLeft(label, x);
            Canvas.SetTop(label, y);
            _canvas.Children.Add(label);
        }
    }

    private Rectangle Box(ScreenRect rect, Color color, double thickness, bool dashed)
    {
        var box = new Rectangle
        {
            Width = Math.Max(1, rect.Width),
            Height = Math.Max(1, rect.Height),
            Stroke = new SolidColorBrush(color),
            StrokeThickness = thickness,
            RadiusX = 3,
            RadiusY = 3,
            Fill = new SolidColorBrush(Color.FromArgb(0x18, color.R, color.G, color.B)),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = color, BlurRadius = 10, ShadowDepth = 0, Opacity = 0.9 },
        };
        if (dashed) box.StrokeDashArray = new DoubleCollection { 4, 3 };
        Canvas.SetLeft(box, rect.X - _bounds.X);
        Canvas.SetTop(box, rect.Y - _bounds.Y);
        return box;
    }

    private static Line MakeLine(double x1, double y1, double x2, double y2, Color color) => new()
    {
        X1 = x1,
        Y1 = y1,
        X2 = x2,
        Y2 = y2,
        Stroke = new SolidColorBrush(color),
        StrokeThickness = 1.5,
    };

    private static ScreenRect Inflate(ScreenRect r, int by) => new(r.X - by, r.Y - by, r.Width + 2 * by, r.Height + 2 * by);

    private void StartTimer()
    {
        var timer = new DispatcherTimer { Interval = _hold };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var fade = new DoubleAnimation(1.0, 0.0, FadeTime);
            fade.Completed += (_, _) => CloseNow();
            BeginAnimation(OpacityProperty, fade);
        };
        timer.Start();
    }

    private bool _closed;

    private void CloseNow()
    {
        if (_closed) return;
        _closed = true;
        if (ReferenceEquals(_current, this)) _current = null;
        CaptureShield.MarkHidden(this);
        try { Close(); }
        catch (InvalidOperationException) { /* already closing */ }
    }
}
