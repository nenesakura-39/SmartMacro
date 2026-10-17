using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using SmartMacro.Core.Models;
using SmartMacro.Services.Input;

namespace SmartMacro.Services;

public enum OverlayKind
{
    Recording,
    Playing,
    Paused,
}

/// <summary>What the app does with its windows while recording or playing.</summary>
public interface IRunModeHost
{
    void Enter(OverlayKind kind, string text);

    void Update(OverlayKind kind, string text);

    void Exit();
}

/// <summary>
/// Minimizes the main window (optional) and shows a small always-on-top banner so it's
/// always obvious that SmartMacro is recording or playing.
/// </summary>
public sealed class RunModeHost : IRunModeHost, IDisposable
{
    private readonly Window _mainWindow;
    private readonly Func<AppSettings> _settings;
    private RunOverlayWindow? _overlay;
    private bool _minimizedByUs;
    private WindowState _previousState = WindowState.Normal;

    public RunModeHost(Window mainWindow, Func<AppSettings> settings)
    {
        _mainWindow = mainWindow;
        _settings = settings;
    }

    public void Enter(OverlayKind kind, string text)
    {
        var settings = _settings();
        _minimizedByUs = false;

        // Even if it stays visible (or is still animating), OCR must never read SmartMacro's own window.
        NativeMethods.SetExcludedFromCapture(_mainWindow, true);
        if (settings.MinimizeDuringRun && _mainWindow.WindowState != WindowState.Minimized)
        {
            _previousState = _mainWindow.WindowState;
            _mainWindow.WindowState = WindowState.Minimized;
            _minimizedByUs = true;
        }

        if (settings.ShowRunOverlay)
        {
            _overlay ??= new RunOverlayWindow();
            _overlay.SetStatus(kind, text);
            _overlay.Show();
        }
    }

    public void Update(OverlayKind kind, string text)
    {
        if (_overlay is { IsVisible: true }) _overlay.SetStatus(kind, text);
    }

    public void Exit()
    {
        _overlay?.Hide();
        NativeMethods.SetExcludedFromCapture(_mainWindow, false);
        if (_minimizedByUs)
        {
            _mainWindow.WindowState = _previousState;
            _mainWindow.Activate();
            _minimizedByUs = false;
        }
    }

    public void Dispose()
    {
        _overlay?.Close();
        _overlay = null;
    }
}

/// <summary>
/// The compact status pill in the top-right corner of the main monitor ("● SmartMacro: Step 2/5 — Finding image…").
/// It can't be clicked or focused, so it never steals clicks or keystrokes from the macro (or the game), it's hidden
/// from screen captures, and it closes as soon as playback ends.
/// </summary>
internal sealed class RunOverlayWindow : Window
{
    private readonly TextBlock _text;
    private readonly Ellipse _dot;

    public RunOverlayWindow()
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
        Title = "SmartMacro status";

        _dot = new Ellipse { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center };
        _text = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xEB, 0xF5)),
            FontSize = 12,
            MaxWidth = 560,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x0E, 0x16, 0x26)),
            // The brand's glowing cyan → indigo edge (Themes/Brand.xaml), like the app icon.
            BorderBrush = Application.Current?.TryFindResource("BrandBorderBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(0x26, 0x32, 0x4A)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(8, 5, 14, 5),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { new Views.BrandLogo { Width = 20, Height = 20, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center }, _dot, _text } },
        };

        SizeChanged += (_, _) => PositionAtCorner();
    }

    public void SetStatus(OverlayKind kind, string text)
    {
        _text.Text = text;
        _dot.Fill = kind switch
        {
            OverlayKind.Recording => new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
            OverlayKind.Paused => new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),
            _ => new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)),
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Click-through, never activated, hidden from Alt+Tab.
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLongValue(hwnd, NativeMethods.GWL_EXSTYLE);
        style |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLongValue(hwnd, NativeMethods.GWL_EXSTYLE, style);

        // Keep the banner out of screen captures so OCR doesn't read it (Windows 10 2004+; best effort).
        NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
        PositionAtCorner();
    }

    /// <summary>Top-right corner of the main monitor's work area, out of the way of most game UI.</summary>
    private void PositionAtCorner()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 12;
        Top = area.Top + 12;
    }
}
