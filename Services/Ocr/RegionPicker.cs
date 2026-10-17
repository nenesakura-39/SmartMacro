using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Vision;
using SmartMacro.Services.Input;
using SmartMacro.Services.Vision;

namespace SmartMacro.Services.Ocr;

/// <summary>
/// What a picker returned: the rectangle (1×1 for a point) and the frozen picture of the whole desktop the user picked
/// on (null when the screen couldn't be frozen). Capture image / Pick color take their pixels from the frozen picture,
/// so a fast-moving game element is captured exactly as it looked when the screen froze.
/// </summary>
internal sealed record PickerResult(ScreenRect Rect, ScreenFrame? Frozen);

/// <summary>
/// The screen picker (like Win + Shift + S): optionally counts down 3 / 5 s first (<see cref="ScreenFreezeOptions"/>), then
/// takes one screenshot of every monitor and shows it on one overlay per monitor, so the screen is "frozen" while you
/// drag a box or click a pixel. Space takes a fresh screenshot (after the same countdown), Esc / right-click cancels.
/// A live 8× magnifier shows the exact pixel under the crosshair with its colour and position.
/// Each overlay only covers a single monitor, so it always has that monitor's display scaling. Positions come straight
/// from the cursor in physical pixels, the same coordinates screen capture and mouse steps use.
/// </summary>
internal static class RegionPicker
{
    public const string DefaultHint = "Drag a box around the text to search for. Leave a little space around it.   Esc or right-click to cancel.";

    public const string FrozenBanner = "Screen Frozen — Drag to select or click a pixel  |  Press [Space] to unfreeze & capture a new frame  |  [Esc] Cancel";

    /// <summary>Picks an area (or a point) and returns only the rectangle.</summary>
    public static async Task<ScreenRect?> PickAsync(string? hint = null, bool pickPoint = false) =>
        (await PickFrozenAsync(hint, pickPoint))?.Rect;

    /// <param name="hint">Instruction shown at the top of each monitor.</param>
    /// <param name="pickPoint">True = a single click picks one pixel (returned as a 1×1 rectangle) instead of dragging a box.</param>
    public static async Task<PickerResult?> PickFrozenAsync(string? hint = null, bool pickPoint = false)
    {
        var delay = ScreenFreezeOptions.Instance.DelaySeconds;
        if (delay > 0 && !await FreezeCountdown.RunAsync(delay)) return null;

        var monitors = MonitorBounds();
        if (monitors.Count == 0) return null;

        var frozen = TryFreeze();
        var result = new TaskCompletionSource<PickerResult?>();
        var windows = new List<RegionPickerWindow>();
        var refreezing = false;

        void Finish(ScreenRect? picked)
        {
            if (!result.TrySetResult(picked is { } rect ? new PickerResult(rect, frozen) : null)) return;
            foreach (var window in windows) window.CloseQuietly();
        }

        async void Refreeze()
        {
            if (refreezing || result.Task.IsCompleted) return;
            refreezing = true;
            try
            {
                foreach (var window in windows) window.Hide();
                var wait = ScreenFreezeOptions.Instance.DelaySeconds;
                if (wait > 0)
                {
                    if (!await FreezeCountdown.RunAsync(wait))
                    {
                        Finish(null);
                        return;
                    }
                }
                else
                {
                    await Task.Delay(250); // let the overlays disappear from the screen first
                }

                if (result.Task.IsCompleted) return;
                frozen = TryFreeze();
                foreach (var window in windows)
                {
                    window.SetFrozen(CropFor(frozen, window.Monitor));
                    window.Show();
                }

                ActivateUnderCursor(windows);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                Finish(null); // a window was closed meanwhile
            }
            finally
            {
                refreezing = false;
            }
        }

        foreach (var monitor in monitors)
            windows.Add(new RegionPickerWindow(monitor, Finish, hint ?? DefaultHint, pickPoint, CropFor(frozen, monitor), Refreeze));

        foreach (var window in windows) window.Show();
        ActivateUnderCursor(windows);

        return await result.Task;
    }

    /// <summary>Gives keyboard focus (for Esc / Space) to the overlay under the mouse.</summary>
    private static void ActivateUnderCursor(List<RegionPickerWindow> windows)
    {
        if (windows.Count == 0) return;
        NativeMethods.GetCursorPos(out var cursor);
        (windows.FirstOrDefault(w => w.Contains(cursor.X, cursor.Y)) ?? windows[0]).Activate();
    }

    /// <summary>One screenshot of the whole desktop (SmartMacro is already hidden), or null if Windows refused.</summary>
    private static ScreenFrame? TryFreeze()
    {
        try
        {
            var capture = new GdiScreenCapture();
            return capture.Capture(capture.GetVirtualScreen());
        }
        catch (Exception ex) when (ex is Win32Exception or ArgumentException or InvalidOperationException)
        {
            return null; // fall back to the live (see-through) picker
        }
    }

    /// <summary>The part of the frozen desktop that one monitor shows (exactly the monitor's size), or null.</summary>
    private static BgraImage? CropFor(ScreenFrame? frozen, ScreenRect monitor)
    {
        if (frozen is null) return null;
        var local = new ScreenRect(monitor.X - frozen.Bounds.X, monitor.Y - frozen.Bounds.Y, monitor.Width, monitor.Height);
        if (local.X < 0 || local.Y < 0 || local.Right > frozen.Image.Width || local.Bottom > frozen.Image.Height) return null;
        return frozen.Image.Crop(local);
    }

    private static List<ScreenRect> MonitorBounds()
    {
        var monitors = new List<ScreenRect>();
        using var dpi = NativeMethods.PhysicalPixelsScope.Enter();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                var r = info.rcMonitor;
                monitors.Add(new ScreenRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));
            }

            return true;
        }, IntPtr.Zero);
        return monitors;
    }
}

/// <summary>
/// "Freezing the screen in 3… 2… 1…": a small click-through banner (it never takes focus, so the game stays in front)
/// shown before the picker freezes the screen. Esc cancels.
/// </summary>
internal static class FreezeCountdown
{
    private const int VK_ESCAPE = 0x1B;

    /// <summary>Counts down; false when Esc was pressed. Returns after the banner has left the screen.</summary>
    public static async Task<bool> RunAsync(int seconds)
    {
        var window = new CountdownWindow();
        window.Show();
        try
        {
            var end = DateTime.UtcNow.AddSeconds(seconds);
            while (true)
            {
                var left = end - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) break;
                window.SetText($"Freezing the screen in {Math.Ceiling(left.TotalSeconds):0}…   Switch to your game and open the menu / minigame now.   (Esc cancels)");
                if ((WindowApi.GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0) return false;
                await Task.Delay(50);
            }
        }
        finally
        {
            window.Close();
        }

        await Task.Delay(120); // let the banner disappear before the screenshot
        return true;
    }

    private sealed class CountdownWindow : Window
    {
        private readonly TextBlock _text;

        public CountdownWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            Focusable = false;
            IsHitTestVisible = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            Title = "SmartMacro countdown";

            _text = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            };
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x0E, 0x16, 0x26)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(20, 12, 20, 12),
                Child = _text,
            };
            SizeChanged += (_, _) => PositionAtTop();
        }

        public void SetText(string text) => _text.Text = text;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;
            var style = NativeMethods.GetWindowLongValue(hwnd, NativeMethods.GWL_EXSTYLE);
            style |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
            NativeMethods.SetWindowLongValue(hwnd, NativeMethods.GWL_EXSTYLE, style);
            NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE); // never in the frozen picture
            PositionAtTop();
        }

        private void PositionAtTop()
        {
            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - ActualWidth) / 2;
            Top = area.Top + 40;
        }
    }
}

/// <summary>
/// An overlay over one monitor. With a frozen picture it shows that picture (the screen "stands still"); without one it
/// is a see-through dimmed layer. Drag to draw an area; in point mode (Pick position / Pick color) it shows a crosshair
/// and the pick happens when the button is released: both the press and the release land on this overlay, so nothing
/// underneath is clicked. A magnifier near the cursor shows the pixels around it 8× larger, the exact pixel's colour
/// (hex and RGB) and its X / Y. Space = new screenshot, Esc / right-click = cancel.
/// </summary>
internal sealed class RegionPickerWindow : Window
{
    private const int LoupeRadius = 7;              // 15 × 15 pixels around the cursor
    private const int LoupeZoom = 8;                // shown 8× larger
    private const int LoupePixels = LoupeRadius * 2 + 1;
    private const double LoupeSize = LoupePixels * LoupeZoom;

    private static readonly Brush LiveBackground = new SolidColorBrush(Color.FromArgb(0x55, 0x05, 0x0A, 0x14));

    private readonly ScreenRect _monitor;
    private readonly Action<ScreenRect?> _done;
    private readonly Action? _refreeze;
    private readonly bool _pickPoint;
    private readonly string _hintText;
    private readonly Canvas _canvas = new();
    private readonly Image _frozenImage;
    private readonly Rectangle _dim;
    private readonly Rectangle _selection;
    private readonly TextBlock _sizeLabel;
    private readonly TextBlock _banner;
    private readonly Line _crossX;
    private readonly Line _crossY;
    private readonly Border _loupe;
    private readonly Canvas _loupeCanvas;
    private readonly Image _loupeImage;
    private readonly Border _loupeSwatch;
    private readonly TextBlock _loupeText;
    private BgraImage? _frozen;
    private BitmapSource? _frozenSource;
    private NativeMethods.POINT? _start;
    private bool _pointPressed;
    private bool _closed;

    public RegionPickerWindow(ScreenRect monitor, Action<ScreenRect?> done, string hintText, bool pickPoint, BgraImage? frozen = null,
        Action? refreeze = null)
    {
        _monitor = monitor;
        _done = done;
        _pickPoint = pickPoint;
        _hintText = hintText;
        _refreeze = refreeze;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = LiveBackground;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Cursor = Cursors.Cross;
        Title = "Pick the search area";

        var accent = Color.FromRgb(0x3B, 0x82, 0xF6);
        _frozenImage = new Image { Stretch = Stretch.Fill, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        RenderOptions.SetBitmapScalingMode(_frozenImage, BitmapScalingMode.NearestNeighbor);
        _dim = new Rectangle
        {
            Fill = new SolidColorBrush(Color.FromArgb(0x30, 0x05, 0x0A, 0x14)),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        _selection = new Rectangle
        {
            Stroke = new SolidColorBrush(accent),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(0x33, accent.R, accent.G, accent.B)),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        _sizeLabel = new TextBlock
        {
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x0E, 0x16, 0x26)),
            Padding = new Thickness(6, 2, 6, 2),
            FontSize = 12,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };

        // Point mode: a full-width / full-height crosshair through the cursor (positioned in OnMove).
        var crossBrush = new SolidColorBrush(Color.FromArgb(0xDD, 0xF5, 0x9E, 0x0B));
        _crossX = new Line { Stroke = crossBrush, StrokeThickness = 1, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        _crossY = new Line { Stroke = crossBrush, StrokeThickness = 1, Visibility = Visibility.Collapsed, IsHitTestVisible = false };

        _banner = new TextBlock { Foreground = Brushes.White, FontSize = 14, TextWrapping = TextWrapping.Wrap, MaxWidth = 1100 };
        var hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x0E, 0x16, 0x26)),
            BorderBrush = new SolidColorBrush(accent),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 8, 14, 8),
            Child = _banner,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(hint, 24);
        Canvas.SetTop(hint, 24);

        // The 8× magnifier: pixels around the cursor, a grid, the exact pixel marked, its colour and position below.
        _loupeImage = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
        RenderOptions.SetBitmapScalingMode(_loupeImage, BitmapScalingMode.NearestNeighbor);
        _loupeCanvas = new Canvas { Width = LoupeSize, Height = LoupeSize, ClipToBounds = true, Background = Brushes.Black };
        _loupeCanvas.Children.Add(_loupeImage);
        var gridBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x00, 0x00));
        for (var i = 1; i < LoupePixels; i++)
        {
            var at = i * LoupeZoom;
            _loupeCanvas.Children.Add(new Line { X1 = at, X2 = at, Y1 = 0, Y2 = LoupeSize, Stroke = gridBrush, StrokeThickness = 1 });
            _loupeCanvas.Children.Add(new Line { X1 = 0, X2 = LoupeSize, Y1 = at, Y2 = at, Stroke = gridBrush, StrokeThickness = 1 });
        }

        var center = new Rectangle { Width = LoupeZoom + 2, Height = LoupeZoom + 2, Stroke = Brushes.White, StrokeThickness = 1.5 };
        Canvas.SetLeft(center, LoupeRadius * LoupeZoom - 1);
        Canvas.SetTop(center, LoupeRadius * LoupeZoom - 1);
        _loupeCanvas.Children.Add(center);
        var centerOuter = new Rectangle { Width = LoupeZoom + 4, Height = LoupeZoom + 4, Stroke = Brushes.Black, StrokeThickness = 1 };
        Canvas.SetLeft(centerOuter, LoupeRadius * LoupeZoom - 2);
        Canvas.SetTop(centerOuter, LoupeRadius * LoupeZoom - 2);
        _loupeCanvas.Children.Add(centerOuter);

        _loupeSwatch = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(3), BorderBrush = Brushes.White, BorderThickness = new Thickness(1) };
        _loupeText = new TextBlock { Foreground = Brushes.White, FontSize = 11, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var info = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        info.Children.Add(_loupeSwatch);
        info.Children.Add(_loupeText);
        var loupePanel = new StackPanel();
        loupePanel.Children.Add(new Border { BorderBrush = new SolidColorBrush(accent), BorderThickness = new Thickness(2), Child = _loupeCanvas });
        loupePanel.Children.Add(info);
        _loupe = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x0E, 0x16, 0x26)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            Child = loupePanel,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };

        _canvas.Children.Add(_crossX);
        _canvas.Children.Add(_crossY);
        _canvas.Children.Add(_selection);
        _canvas.Children.Add(_sizeLabel);
        _canvas.Children.Add(_loupe);
        _canvas.Children.Add(hint);

        var root = new Grid();
        root.Children.Add(_frozenImage);
        root.Children.Add(_dim);
        root.Children.Add(_canvas);
        Content = root;

        SetFrozen(frozen);

        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseRightButtonUp += (_, _) => _done(null);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) _done(null);
            else if (e.Key == Key.Space && _refreeze is not null && !_pointPressed && _start is null)
            {
                e.Handled = true;
                _refreeze();
            }
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _done(null); // closed some other way (e.g. Alt+F4) = cancel; ignored if already finished
        };
    }

    /// <summary>The monitor this overlay covers (physical pixels).</summary>
    public ScreenRect Monitor => _monitor;

    public bool Contains(int x, int y) =>
        x >= _monitor.X && y >= _monitor.Y && x < _monitor.Right && y < _monitor.Bottom;

    public void CloseQuietly()
    {
        if (_closed) return;
        _closed = true;
        Close();
    }

    /// <summary>Shows a (new) frozen picture of this monitor, or null to go back to the see-through overlay.</summary>
    public void SetFrozen(BgraImage? frozen)
    {
        _frozen = frozen is not null && frozen.Width == _monitor.Width && frozen.Height == _monitor.Height ? frozen : null;
        if (_frozen is { } image)
        {
            var source = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgr32, null, image.Pixels, image.Stride);
            source.Freeze();
            _frozenSource = source;
            _frozenImage.Source = source;
            _frozenImage.Visibility = Visibility.Visible;
            _dim.Visibility = Visibility.Visible;
            Background = Brushes.Black;
            _banner.Text = RegionPicker.FrozenBanner + Environment.NewLine + _hintText;
        }
        else
        {
            _frozenSource = null;
            _frozenImage.Source = null;
            _frozenImage.Visibility = Visibility.Collapsed;
            _dim.Visibility = Visibility.Collapsed;
            _loupe.Visibility = Visibility.Collapsed;
            Background = LiveBackground;
            _banner.Text = _hintText;
        }

        _selection.Visibility = Visibility.Collapsed;
        _sizeLabel.Visibility = Visibility.Collapsed;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyBounds();
        // Never part of a screenshot (also not of the next frozen frame).
        NativeMethods.SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        // Moving onto a monitor with different scaling makes WPF resize the window; put it back exactly.
        Dispatcher.InvokeAsync(ApplyBounds);
    }

    /// <summary>Covers exactly this monitor, in physical pixels.</summary>
    private void ApplyBounds()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, _monitor.X, _monitor.Y,
            _monitor.Width, _monitor.Height, NativeMethods.SWP_SHOWWINDOW);
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        NativeMethods.GetCursorPos(out var point);
        if (_pickPoint)
        {
            // One click = one pixel. Picked on release (see OnUp), so the release doesn't reach the app below.
            _pointPressed = true;
            CaptureMouse();
            ShowCrosshair(point);
            return;
        }

        _start = point;
        CaptureMouse();
        UpdateSelection(point);
        _selection.Visibility = Visibility.Visible;
        _sizeLabel.Visibility = Visibility.Visible;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        NativeMethods.GetCursorPos(out var point);
        UpdateLoupe(point);
        if (_pickPoint)
        {
            ShowCrosshair(point);
            return;
        }

        if (_start is null) return;
        UpdateSelection(point);
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_pickPoint)
        {
            if (!_pointPressed) return;
            _pointPressed = false;
            ReleaseMouseCapture();
            NativeMethods.GetCursorPos(out var picked);
            if (Contains(picked.X, picked.Y)) _done(new ScreenRect(picked.X, picked.Y, 1, 1));
            return;
        }

        if (_start is not { } start) return;
        ReleaseMouseCapture();
        NativeMethods.GetCursorPos(out var end);
        _start = null;

        // Keep the box on this monitor.
        var rect = ToRect(start, end).Intersect(_monitor);
        if (rect.Width >= 5 && rect.Height >= 5)
        {
            _done(rect);
        }
        else
        {
            // Too small (probably a stray click): let the user try again.
            _selection.Visibility = Visibility.Collapsed;
            _sizeLabel.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateSelection(NativeMethods.POINT current)
    {
        if (_start is not { } start) return;
        var rect = ToRect(start, current).Intersect(_monitor);
        if (rect.IsEmpty) return;

        // Convert physical screen pixels to this window's layout units (this window has one DPI).
        var topLeft = PointFromScreen(new Point(rect.X, rect.Y));
        var bottomRight = PointFromScreen(new Point(rect.Right, rect.Bottom));

        Canvas.SetLeft(_selection, topLeft.X);
        Canvas.SetTop(_selection, topLeft.Y);
        _selection.Width = Math.Max(0, bottomRight.X - topLeft.X);
        _selection.Height = Math.Max(0, bottomRight.Y - topLeft.Y);

        _sizeLabel.Text = $"{rect.Width} × {rect.Height}  at ({rect.X}, {rect.Y})";
        Canvas.SetLeft(_sizeLabel, topLeft.X);
        Canvas.SetTop(_sizeLabel, Math.Max(0, topLeft.Y - 24));
    }

    /// <summary>Point mode: crosshair lines through the cursor and its exact screen position next to it.</summary>
    private void ShowCrosshair(NativeMethods.POINT point)
    {
        if (!Contains(point.X, point.Y))
        {
            _crossX.Visibility = _crossY.Visibility = _sizeLabel.Visibility = Visibility.Collapsed;
            return;
        }

        // Physical screen pixels → this window's layout units (the window has one DPI: its monitor's).
        var p = PointFromScreen(new Point(point.X, point.Y));
        var width = ActualWidth;
        var height = ActualHeight;
        _crossX.X1 = 0; _crossX.X2 = width; _crossX.Y1 = _crossX.Y2 = p.Y;
        _crossY.Y1 = 0; _crossY.Y2 = height; _crossY.X1 = _crossY.X2 = p.X;
        _crossX.Visibility = _crossY.Visibility = Visibility.Visible;

        if (_frozen is not null)
        {
            _sizeLabel.Visibility = Visibility.Collapsed; // the magnifier shows the position
            return;
        }

        _sizeLabel.Text = $"X {point.X}, Y {point.Y}";
        _sizeLabel.Visibility = Visibility.Visible;
        // Keep the label on screen: to the lower right of the cursor, or flipped near the right / bottom edge.
        Canvas.SetLeft(_sizeLabel, p.X + 150 > width ? Math.Max(0, p.X - 150) : p.X + 14);
        Canvas.SetTop(_sizeLabel, p.Y + 40 > height ? Math.Max(0, p.Y - 30) : p.Y + 14);
    }

    /// <summary>The 8× magnifier: the 15 × 15 pixels around the cursor from the frozen picture, plus colour and X / Y.</summary>
    private void UpdateLoupe(NativeMethods.POINT point)
    {
        if (_frozen is not { } image || _frozenSource is not { } source || !Contains(point.X, point.Y))
        {
            _loupe.Visibility = Visibility.Collapsed;
            return;
        }

        var px = point.X - _monitor.X;
        var py = point.Y - _monitor.Y;
        var cropWidth = Math.Min(LoupePixels, image.Width);
        var cropHeight = Math.Min(LoupePixels, image.Height);
        var x0 = Math.Clamp(px - LoupeRadius, 0, image.Width - cropWidth);
        var y0 = Math.Clamp(py - LoupeRadius, 0, image.Height - cropHeight);
        var crop = new CroppedBitmap(source, new Int32Rect(x0, y0, cropWidth, cropHeight));
        crop.Freeze();
        _loupeImage.Source = crop;
        _loupeImage.Width = cropWidth * LoupeZoom;
        _loupeImage.Height = cropHeight * LoupeZoom;
        // Near an edge the crop can't be centred on the cursor: shift it so the cursor's pixel stays in the middle box.
        Canvas.SetLeft(_loupeImage, (x0 - (px - LoupeRadius)) * LoupeZoom);
        Canvas.SetTop(_loupeImage, (y0 - (py - LoupeRadius)) * LoupeZoom);

        var color = image.GetPixel(Math.Clamp(px, 0, image.Width - 1), Math.Clamp(py, 0, image.Height - 1));
        var swatch = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
        swatch.Freeze();
        _loupeSwatch.Background = swatch;
        _loupeText.Text = $"{color}   RGB({color.R}, {color.G}, {color.B}){Environment.NewLine}X {point.X}, Y {point.Y}";

        var p = PointFromScreen(new Point(point.X, point.Y));
        const double offset = 28;
        const double panelWidth = LoupeSize + 24;
        const double panelHeight = LoupeSize + 60;
        Canvas.SetLeft(_loupe, p.X + offset + panelWidth > ActualWidth ? Math.Max(0, p.X - offset - panelWidth) : p.X + offset);
        Canvas.SetTop(_loupe, p.Y + offset + panelHeight > ActualHeight ? Math.Max(0, p.Y - offset - panelHeight) : p.Y + offset);
        _loupe.Visibility = Visibility.Visible;
    }

    private static ScreenRect ToRect(NativeMethods.POINT a, NativeMethods.POINT b)
    {
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        return new ScreenRect(x, y, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }
}
