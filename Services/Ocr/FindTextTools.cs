using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Services.Input;
using SmartMacro.Services.Vision;

namespace SmartMacro.Services.Ocr;

/// <summary>Screen helpers the "Find text" editor uses: picking an area and test-reading it.</summary>
public interface IFindTextTools
{
    bool IsOcrAvailable { get; }

    string OcrStatus { get; }

    /// <summary>Hides SmartMacro, lets the user drag a rectangle on screen, returns it (null if cancelled).</summary>
    Task<ScreenRect?> PickRegionAsync();

    /// <summary>Hides SmartMacro, reads the step's search area once, and describes what was found.</summary>
    Task<string> TestAsync(FindTextStep step);

    /// <summary>Where the most recent test found the text (null when it wasn't found).</summary>
    ScreenPoint? LastTestMatch { get; }

    /// <summary>Moves the mouse cursor to a spot so the user can see where a match is (nothing is clicked).</summary>
    void ShowSpot(ScreenPoint point);
}

public sealed class FindTextTools : IFindTextTools
{
    private readonly Window _mainWindow;
    private readonly IOcrService _ocr;
    private readonly string? _debugImagePath;

    /// <param name="debugImagePath">Where "Test now" saves a picture of what was captured (helps diagnose misses).</param>
    public FindTextTools(Window mainWindow, IOcrService ocr, string? debugImagePath = null)
    {
        _mainWindow = mainWindow;
        _ocr = ocr;
        _debugImagePath = debugImagePath;
    }

    public bool IsOcrAvailable => _ocr.IsAvailable;

    public ScreenPoint? LastTestMatch { get; private set; }

    public void ShowSpot(ScreenPoint point) => NativeMethods.SetCursorPos(point.X, point.Y);

    public string OcrStatus => _ocr.StatusText;

    public Task<ScreenRect?> PickRegionAsync() => WhileHiddenAsync(() => RegionPicker.PickAsync());

    public async Task<string> TestAsync(FindTextStep step)
    {
        LastTestMatch = null;
        if (!_ocr.IsAvailable) return _ocr.StatusText;
        if (string.IsNullOrWhiteSpace(step.Text)) return "Enter the text to look for first.";

        var region = step.SearchFullScreen
            ? _ocr.GetVirtualScreen()
            : new ScreenRect(step.RegionX, step.RegionY, step.RegionWidth, step.RegionHeight);

        OcrReadResult read;
        string? savedImage = null;
        try
        {
            // Hide SmartMacro so it doesn't read its own window (which shows the search text).
            read = await WhileHiddenAsync(async () =>
            {
                var result = await _ocr.ReadAsync(region, CancellationToken.None);
                if (_debugImagePath is not null && _ocr is WindowsOcrService windowsOcr)
                    savedImage = windowsOcr.TrySaveCapture(region, _debugImagePath);
                return result;
            });
        }
        catch (Exception ex)
        {
            return $"Test failed: {ex.Message}";
        }

        var where = step.SearchFullScreen ? "the whole screen" : $"area {region}";
        var picture = savedImage is null ? "" : $"\nA picture of what was captured was saved to: {savedImage}";

        var matches = OcrTextMatcher.Find(read, step.Text, step.MatchMode, step.CaseSensitive, step.MinSimilarityPercent / 100.0,
                step.NormalizeLookAlikes);
        if (matches.Count > 0)
        {
            var best = matches[0];
            var more = matches.Count > 1 ? $" ({matches.Count} matches; the first one is used)" : "";
            LastTestMatch = new ScreenPoint(best.Bounds.CenterX, best.Bounds.CenterY);
            return $"Matched! Read text in area: '{best.Text}' at ({best.Bounds.CenterX}, {best.Bounds.CenterY}) — {best.Similarity:P0} match{more}. Searched {where}.{picture}";
        }

        var text = read.FullText.Replace(Environment.NewLine, "  |  ");
        if (text.Length > 400) text = text[..400] + "…";
        return string.IsNullOrWhiteSpace(text)
            ? $"Not found. No text could be read in {where}.{picture}"
            : $"Not found. Text read in area: '{text}' (searched {where}).{picture}";
    }

    private async Task<T> WhileHiddenAsync<T>(Func<Task<T>> action)
    {
        var previousState = _mainWindow.WindowState;
        NativeMethods.SetExcludedFromCapture(_mainWindow, true);
        _mainWindow.WindowState = WindowState.Minimized;
        try
        {
            await Task.Delay(350); // let the minimize animation finish
            return await action();
        }
        finally
        {
            NativeMethods.SetExcludedFromCapture(_mainWindow, false);
            _mainWindow.WindowState = previousState == WindowState.Minimized ? WindowState.Normal : previousState;
            _mainWindow.Activate();
        }
    }
}

/// <summary>
/// Shows one dimmed overlay per monitor (like Snipping Tool). Each overlay only covers a single monitor,
/// so it always has that monitor's display scaling. Positions come straight from the cursor in physical
/// pixels, the same coordinates screen capture uses.
/// </summary>
internal static class RegionPicker
{
    public const string DefaultHint = "Drag a box around the text to search for. Leave a little space around it.   Esc or right-click to cancel.";

    /// <param name="hint">Instruction shown at the top of each monitor.</param>
    /// <param name="pickPoint">True = a single click picks one pixel (returned as a 1×1 rectangle) instead of dragging a box.</param>
    public static Task<ScreenRect?> PickAsync(string? hint = null, bool pickPoint = false)
    {
        var result = new TaskCompletionSource<ScreenRect?>();
        var windows = new List<RegionPickerWindow>();

        void Finish(ScreenRect? picked)
        {
            if (!result.TrySetResult(picked)) return;
            foreach (var window in windows) window.CloseQuietly();
        }

        foreach (var monitor in MonitorBounds())
            windows.Add(new RegionPickerWindow(monitor, Finish, hint ?? DefaultHint, pickPoint));

        if (windows.Count == 0)
        {
            result.TrySetResult(null);
            return result.Task;
        }

        foreach (var window in windows) window.Show();

        // Give keyboard focus (for Esc) to the overlay under the mouse.
        NativeMethods.GetCursorPos(out var cursor);
        (windows.FirstOrDefault(w => w.Contains(cursor.X, cursor.Y)) ?? windows[0]).Activate();

        return result.Task;
    }

    private static List<ScreenRect> MonitorBounds()
    {
        var monitors = new List<ScreenRect>();
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
/// A dimmed overlay over one monitor. Drag to draw the search area; Esc or right-click cancels.
/// In point mode (Pick position / Pick color) it shows a crosshair and the exact X / Y under the cursor, and the
/// pick happens when the button is released: both the press and the release land on this overlay, so nothing
/// underneath is clicked.
/// </summary>
internal sealed class RegionPickerWindow : Window
{
    private readonly ScreenRect _monitor;
    private readonly Action<ScreenRect?> _done;
    private readonly bool _pickPoint;
    private readonly Canvas _canvas = new();
    private readonly Rectangle _selection;
    private readonly TextBlock _sizeLabel;
    private readonly Line _crossX;
    private readonly Line _crossY;
    private NativeMethods.POINT? _start;
    private bool _pointPressed;
    private bool _closed;

    public RegionPickerWindow(ScreenRect monitor, Action<ScreenRect?> done, string hintText, bool pickPoint)
    {
        _monitor = monitor;
        _done = done;
        _pickPoint = pickPoint;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(0x55, 0x05, 0x0A, 0x14));
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Cursor = Cursors.Cross;
        Title = "Pick the search area";

        var accent = Color.FromRgb(0x3B, 0x82, 0xF6);
        _selection = new Rectangle
        {
            Stroke = new SolidColorBrush(accent),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(0x33, accent.R, accent.G, accent.B)),
            Visibility = Visibility.Collapsed,
        };
        _sizeLabel = new TextBlock
        {
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x0E, 0x16, 0x26)),
            Padding = new Thickness(6, 2, 6, 2),
            FontSize = 12,
            Visibility = Visibility.Collapsed,
        };

        // Point mode: a full-width / full-height crosshair through the cursor (positioned in OnMove).
        var crossBrush = new SolidColorBrush(Color.FromArgb(0xDD, 0xF5, 0x9E, 0x0B));
        _crossX = new Line { Stroke = crossBrush, StrokeThickness = 1, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        _crossY = new Line { Stroke = crossBrush, StrokeThickness = 1, Visibility = Visibility.Collapsed, IsHitTestVisible = false };

        var hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x0E, 0x16, 0x26)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 8, 14, 8),
            Child = new TextBlock
            {
                Text = hintText,
                Foreground = Brushes.White,
                FontSize = 14,
            },
        };
        Canvas.SetLeft(hint, 24);
        Canvas.SetTop(hint, 24);

        _canvas.Children.Add(_crossX);
        _canvas.Children.Add(_crossY);
        _canvas.Children.Add(_selection);
        _canvas.Children.Add(_sizeLabel);
        _canvas.Children.Add(hint);
        Content = _canvas;

        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseRightButtonUp += (_, _) => _done(null);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) _done(null); };
        Closed += (_, _) =>
        {
            _closed = true;
            _done(null); // closed some other way (e.g. Alt+F4) = cancel; ignored if already finished
        };
    }

    public bool Contains(int x, int y) =>
        x >= _monitor.X && y >= _monitor.Y && x < _monitor.Right && y < _monitor.Bottom;

    public void CloseQuietly()
    {
        if (_closed) return;
        _closed = true;
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyBounds();
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

        _sizeLabel.Text = $"X {point.X}, Y {point.Y}";
        _sizeLabel.Visibility = Visibility.Visible;
        // Keep the label on screen: to the lower right of the cursor, or flipped near the right / bottom edge.
        Canvas.SetLeft(_sizeLabel, p.X + 150 > width ? Math.Max(0, p.X - 150) : p.X + 14);
        Canvas.SetTop(_sizeLabel, p.Y + 40 > height ? Math.Max(0, p.Y - 30) : p.Y + 14);
    }

    private static ScreenRect ToRect(NativeMethods.POINT a, NativeMethods.POINT b)
    {
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        return new ScreenRect(x, y, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }
}
