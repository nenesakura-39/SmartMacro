using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Vision;
using SmartMacro.Services.Input;

namespace SmartMacro.Services.Vision;

/// <summary>
/// AAA live vision "target lock": while a macro plays (with the mini overlay on), every time a Find step, an If / Repeat
/// condition or Smart-Aim finds its target, a neon box with corner brackets, a centre mark and a "🎯 98% Match" badge
/// flashes on it for <see cref="HoldTime"/>, then fades.
/// One reusable window (no window is created per detection): click-through (WS_EX_TRANSPARENT | WS_EX_LAYERED |
/// WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW), always on top, never activated, and left out of screen captures. If Windows
/// can't leave it out of captures (older than Windows 10 2004), <see cref="CaptureShield"/> hides it before every
/// GDI capture, so it never "photobombs" the next frame a Find step looks at.
/// Call <see cref="ShowTarget"/> on the UI thread.
/// </summary>
public sealed class TargetLockOverlay : Window
{
    /// <summary>How long the reticle stays fully visible.</summary>
    public static readonly TimeSpan HoldTime = TimeSpan.FromMilliseconds(350);

    /// <summary>How long it takes to fade out afterwards.</summary>
    public static readonly TimeSpan FadeTime = TimeSpan.FromMilliseconds(160);

    private const int Pad = 18;    // physical px around the target for the brackets and glow
    private const int BadgeRoom = 30; // physical px above the target for the badge

    private static readonly Color NeonCyan = Color.FromRgb(0x22, 0xD3, 0xEE);
    private static readonly Color NeonCyanBright = Color.FromRgb(0xA5, 0xF3, 0xFC);
    private static readonly Color LockGreen = Color.FromRgb(0x22, 0xFF, 0x7A);

    // "🧪 Test Step" near miss: the same reticle in amber, on the spot that ALMOST matched.
    private static readonly Color NearMissAmber = Color.FromRgb(0xF5, 0x9E, 0x0B);
    private static readonly Color NearMissAmberBright = Color.FromRgb(0xFD, 0xE6, 0x8A);
    private static readonly Color NearMissCenter = Color.FromRgb(0xFB, 0xBF, 0x24);

    // The colours of the reticle being drawn (cyan / green normally, amber for a near miss).
    private Color Neon = NeonCyan;
    private Color NeonBright = NeonCyanBright;
    private Color Lock = LockGreen;
    private string? _badgeOverride;

    private readonly Canvas _canvas = new() { IsHitTestVisible = false };
    private readonly DispatcherTimer _holdTimer;
    private ScreenRect _bounds;
    private TargetDetection? _target;
    private bool _streamSafe; // streamer mode reticle: drawn only around (never over) the target, short hold

    public TargetLockOverlay()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        Title = "SmartMacro target lock";
        Left = -32000; // placed in physical pixels once the handle exists
        Top = -32000;
        Width = 1;
        Height = 1;
        Content = _canvas;

        _holdTimer = new DispatcherTimer { Interval = HoldTime };
        _holdTimer.Tick += (_, _) =>
        {
            _holdTimer.Stop();
            var fade = new DoubleAnimation(1.0, 0.0, FadeTime);
            fade.Completed += (_, _) =>
            {
                if (_holdTimer.IsEnabled) return; // a new target arrived during the fade
                HideNow();
            };
            BeginAnimation(OpacityProperty, fade);
        };
    }

    /// <summary>
    /// Call from any thread right before playback captures the screen. Hides a visible reticle first, but only on PCs
    /// where it couldn't be excluded from captures (see <see cref="CaptureShield"/>).
    /// </summary>
    public static void BeforeCapture() => CaptureShield.BeforeCapture();

    /// <summary>True when Windows leaves the reticle out of captures (false until it was shown once, and in streamer mode).</summary>
    public bool IsExcludedFromCapture => OverlayCaptureService.IsExcluded(this);

    /// <summary>Flashes the reticle on a detected target (replaces the previous one right away). UI thread.</summary>
    public void ShowTarget(TargetDetection target) => ShowTarget(target, nearMiss: false, hold: null, badgeText: null);

    /// <summary>
    /// Flashes the reticle. <paramref name="nearMiss"/> = amber (the closest thing on screen that did NOT match well enough,
    /// for "🧪 Test Step"); <paramref name="hold"/> = how long it stays (default <see cref="HoldTime"/>);
    /// <paramref name="badgeText"/> replaces the "🎯 98% · Label" badge text. UI thread.
    /// </summary>
    public void ShowTarget(TargetDetection target, bool nearMiss, TimeSpan? hold, string? badgeText)
    {
        Neon = nearMiss ? NearMissAmber : NeonCyan;
        NeonBright = nearMiss ? NearMissAmberBright : NeonCyanBright;
        Lock = nearMiss ? NearMissCenter : LockGreen;
        _badgeOverride = badgeText;

        // "🎥 Show overlays in OBS / Discord": the playback reticle is in captures, so it must never cover what the next
        // Find step looks at: drawn only around the target and gone after 180 ms. (Editor "🧪 Test" reticles pass their
        // own hold time and keep the normal look; they're hidden before SmartMacro's next capture like before.)
        _streamSafe = hold is null && OverlayCapturePolicy.NeedsNonInterferingReticle(OverlayCaptureService.AllowObsCapture);
        _holdTimer.Interval = hold is { } h && h > TimeSpan.Zero
            ? h
            : OverlayCapturePolicy.ReticleHold(HoldTime, OverlayCaptureService.AllowObsCapture);

        var box = target.Bounds;
        var bounds = new ScreenRect(box.X - Pad, box.Y - Pad - BadgeRoom, box.Width + 2 * Pad, box.Height + 2 * Pad + BadgeRoom)
            .Intersect(VirtualScreen());
        if (bounds.IsEmpty) return;

        _bounds = bounds;
        _target = target;
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        if (!IsVisible) Show(); // first time: OnSourceInitialized sets the styles and places it
        ApplyBounds();
        Draw();
        if (!_streamSafe && !OverlayCaptureService.IsExcluded(this)) CaptureShield.MarkVisible(this, HideNow);

        _holdTimer.Stop();
        _holdTimer.Start();
    }

    /// <summary>Hides the reticle now (playback ended, or a capture is about to happen).</summary>
    public void HideNow()
    {
        _holdTimer.Stop();
        BeginAnimation(OpacityProperty, null);
        CaptureShield.MarkHidden(this);
        if (IsVisible) Hide();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLongValue(hwnd, NativeMethods.GWL_EXSTYLE);
        style |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLongValue(hwnd, NativeMethods.GWL_EXSTYLE, style);
        // The next Find step must never "see" the reticle (streamer mode: captures see it, see ShowTarget).
        CaptureShield.TryExclude(this);
        ApplyBounds();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        // Moved onto a monitor with other scaling: Windows resized the window, so put it back and redraw.
        Dispatcher.InvokeAsync(() =>
        {
            ApplyBounds();
            Draw();
        });
    }

    private void ApplyBounds()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || _bounds.IsEmpty) return;
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    /// <summary>Draws the reticle. Laid out in physical pixels, then scaled by the window's DPI.</summary>
    private void Draw()
    {
        if (_target is not { } target) return;
        _canvas.Children.Clear();
        var dpi = VisualTreeHelper.GetDpi(this);
        _canvas.LayoutTransform = new ScaleTransform(1.0 / dpi.DpiScaleX, 1.0 / dpi.DpiScaleY);
        _canvas.Width = _bounds.Width;
        _canvas.Height = _bounds.Height;

        var r = target.Bounds;
        double x = r.X - _bounds.X, y = r.Y - _bounds.Y, w = Math.Max(2, r.Width), h = Math.Max(2, r.Height);
        var glow = new DropShadowEffect { Color = Neon, BlurRadius = 14, ShadowDepth = 0, Opacity = 1 };

        // Streamer mode (captures see the reticle): nothing may land on the target itself – the box sits at least
        // OverlayCapturePolicy.StreamReticleClearancePx outside it, without the faint fill, the inward glow or the centre mark.
        var gap = _streamSafe ? OverlayCapturePolicy.StreamReticleClearancePx + 2 : 3;

        // Thin neon box with a faint fill. (A WPF Rectangle draws its stroke inside its own bounds.)
        var box = new Rectangle
        {
            Width = w + 2 * gap,
            Height = h + 2 * gap,
            Stroke = new SolidColorBrush(Color.FromArgb(0xCC, Neon.R, Neon.G, Neon.B)),
            StrokeThickness = 1.5,
            Fill = _streamSafe ? Brushes.Transparent : new SolidColorBrush(Color.FromArgb(0x16, Neon.R, Neon.G, Neon.B)),
            RadiusX = 3,
            RadiusY = 3,
            Effect = _streamSafe ? null : glow,
        };
        Canvas.SetLeft(box, x - gap);
        Canvas.SetTop(box, y - gap);
        _canvas.Children.Add(box);

        // Corner brackets just outside the box (the "lock").
        var arm = Math.Clamp(Math.Min(w, h) * 0.28, 8, 26);
        var out_ = gap + 6;
        double left = x - out_, top = y - out_, right = x + w + out_, bottom = y + h + out_;
        AddBracket(left, top, arm, arm);
        AddBracket(right, top, -arm, arm);
        AddBracket(left, bottom, arm, -arm);
        AddBracket(right, bottom, -arm, -arm);

        // Centre mark on the spot a click would use (not in streamer mode: it would sit on the target).
        if (!_streamSafe)
        {
            var cx = target.X - _bounds.X;
            var cy = target.Y - _bounds.Y;
            _canvas.Children.Add(MakeLine(cx - 7, cy, cx - 2, cy, Lock, 2));
            _canvas.Children.Add(MakeLine(cx + 2, cy, cx + 7, cy, Lock, 2));
            _canvas.Children.Add(MakeLine(cx, cy - 7, cx, cy - 2, Lock, 2));
            _canvas.Children.Add(MakeLine(cx, cy + 2, cx, cy + 7, Lock, 2));
        }

        // "🎯 98% · 0.6ms · Find image" badge above the target (below it when there's no room); ⚡ = found at its last spot.
        var badge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE8, 0x0B, 0x12, 0x20)),
            BorderBrush = new SolidColorBrush(Neon),
            BorderThickness = new Thickness(1.2 * dpi.DpiScaleX),
            CornerRadius = new CornerRadius(6 * dpi.DpiScaleX),
            Padding = new Thickness(7 * dpi.DpiScaleX, 2 * dpi.DpiScaleY, 8 * dpi.DpiScaleX, 3 * dpi.DpiScaleY),
            Effect = new DropShadowEffect { Color = Neon, BlurRadius = 10, ShadowDepth = 0, Opacity = 0.8 },
            Child = new TextBlock
            {
                Text = _badgeOverride ?? $"{target.Badge} · {(target.FromHotSpot ? "⚡ " : "")}{target.Label}",
                Foreground = new SolidColorBrush(NeonBright),
                FontSize = 12.5 * dpi.DpiScaleY, // the canvas is scaled back down, so this ends up as 12.5 px text
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            },
        };
        badge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var bx = Math.Clamp(left, 0, Math.Max(0, _bounds.Width - badge.DesiredSize.Width));
        var above = top - badge.DesiredSize.Height - 4;
        var by = above >= 0 ? above : Math.Min(bottom + 4, Math.Max(0, _bounds.Height - badge.DesiredSize.Height));
        Canvas.SetLeft(badge, bx);
        Canvas.SetTop(badge, by);
        _canvas.Children.Add(badge);
    }

    private void AddBracket(double x, double y, double dx, double dy)
    {
        var effect = new DropShadowEffect { Color = Neon, BlurRadius = 8, ShadowDepth = 0, Opacity = 1 };
        var horizontal = MakeLine(x, y, x + dx, y, NeonBright, 3);
        var vertical = MakeLine(x, y, x, y + dy, NeonBright, 3);
        horizontal.Effect = effect;
        vertical.Effect = effect;
        _canvas.Children.Add(horizontal);
        _canvas.Children.Add(vertical);
    }

    private static Line MakeLine(double x1, double y1, double x2, double y2, Color color, double thickness) => new()
    {
        X1 = x1,
        Y1 = y1,
        X2 = x2,
        Y2 = y2,
        Stroke = new SolidColorBrush(color),
        StrokeThickness = thickness,
        StrokeStartLineCap = PenLineCap.Square,
        StrokeEndLineCap = PenLineCap.Square,
    };

    /// <summary>The whole desktop (all monitors) in physical pixels.</summary>
    private static ScreenRect VirtualScreen()
    {
        using var dpi = NativeMethods.PhysicalPixelsScope.Enter();
        return new ScreenRect(
            NativeMethods.GetSystemMetrics(76), NativeMethods.GetSystemMetrics(77),
            NativeMethods.GetSystemMetrics(78), NativeMethods.GetSystemMetrics(79));
    }
}

/// <summary>
/// The screen capture playback uses: GDI capture, but the target-lock reticle is hidden first when Windows couldn't leave
/// it out of captures (see <see cref="TargetLockOverlay.BeforeCapture"/>).
/// </summary>
public sealed class OverlayAwareScreenCapture : IScreenCapture
{
    private readonly IScreenCapture _inner;

    public OverlayAwareScreenCapture(IScreenCapture inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public ScreenRect GetVirtualScreen() => _inner.GetVirtualScreen();

    /// <summary>Same as the wrapped capture (GDI: true), so the Hot-Spot fast path captures only the small spot.</summary>
    public bool PrefersSmallCaptures => _inner.PrefersSmallCaptures;

    public Task<ScreenFrame> CaptureAsync(ScreenRect region, CancellationToken cancellationToken)
    {
        TargetLockOverlay.BeforeCapture();
        return _inner.CaptureAsync(region, cancellationToken);
    }
}
