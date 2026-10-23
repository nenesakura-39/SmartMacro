using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using SmartMacro.Core.Models;
using SmartMacro.Core.Playback;
using SmartMacro.Services.Input;
using SmartMacro.Services.Vision;

namespace SmartMacro.Services;

public enum OverlayKind
{
    Recording,
    Playing,
    Paused,
}

/// <summary>
/// Everything the in-game mini-HUD shows (AAA upgrade): the state, "Run #3 / ∞", the live elapsed time,
/// "Step 4/12: Find image" with a progress bar (0..1), what the step is doing, and the pause / stop keys.
/// </summary>
public sealed record RunHud(
    OverlayKind Kind,
    string Text,
    string? Counter = null,
    string? StepText = null,
    double Progress = 0,
    TimeSpan Elapsed = default,
    string PauseKey = "F8",
    string StopKey = "Esc",
    string? Telemetry = null);

/// <summary>What the app does with its windows while recording or playing.</summary>
public interface IRunModeHost
{
    /// <param name="counter">Optional run counter shown in bold in front of the text, e.g. "Run #3 / ∞".</param>
    void Enter(OverlayKind kind, string text, string? counter = null);

    /// <param name="counter">Optional run counter shown in bold in front of the text, e.g. "Run #3 / ∞".</param>
    void Update(OverlayKind kind, string text, string? counter = null);

    /// <summary>The full mini-HUD update (state, run, timer, step + progress). The default just updates the text.</summary>
    void UpdateHud(RunHud hud) => Update(hud.Kind, hud.Text, hud.Counter);

    /// <summary>
    /// AAA live vision: draws a short "target lock" reticle where playback just found something. Only when the mini overlay
    /// is turned on. The default does nothing.
    /// </summary>
    void ShowTargetLock(TargetDetection target)
    {
    }

    /// <summary>The user clicked "⏸ F8" on the mini-HUD.</summary>
    event EventHandler? PauseRequested;

    /// <summary>The user clicked "⏹ Esc" on the mini-HUD.</summary>
    event EventHandler? StopRequested;

    void Exit();
}

/// <summary>
/// Minimizes the main window (optional) and shows a small always-on-top HUD so it's always obvious that SmartMacro is
/// recording or playing. While playing it also draws the target-lock reticle and takes clicks on the HUD's ⏸ / ⏹
/// buttons (physical clicks only; see <see cref="HudClickHook"/>).
/// </summary>
public sealed class RunModeHost : IRunModeHost, IDisposable
{
    private readonly Window _mainWindow;
    private readonly Func<AppSettings> _settings;
    private RunOverlayWindow? _overlay;
    private TargetLockOverlay? _targetLock;
    private HudClickHook? _clickHook;
    private bool _minimizedByUs;
    private WindowState _previousState = WindowState.Normal;

    public RunModeHost(Window mainWindow, Func<AppSettings> settings)
    {
        _mainWindow = mainWindow;
        _settings = settings;
    }

    public event EventHandler? PauseRequested;

    public event EventHandler? StopRequested;

    public void Enter(OverlayKind kind, string text, string? counter = null)
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
            _overlay.SetHud(new RunHud(kind, text, counter));
            _overlay.Show();

            // The ⏸ / ⏹ buttons only exist while playing (a recording must not lose clicks to them).
            if (kind != OverlayKind.Recording) StartClickHook();
        }
    }

    public void Update(OverlayKind kind, string text, string? counter = null) => UpdateHud(new RunHud(kind, text, counter));

    public void UpdateHud(RunHud hud)
    {
        if (_overlay is { IsVisible: true }) _overlay.SetHud(hud);
    }

    public void ShowTargetLock(TargetDetection target)
    {
        if (!_settings().ShowRunOverlay) return;
        try
        {
            _targetLock ??= new TargetLockOverlay();
            _targetLock.ShowTarget(target);
        }
        catch (Exception)
        {
            // Purely visual: a reticle that can't be drawn must never disturb playback.
        }
    }

    public void Exit()
    {
        StopClickHook();
        _targetLock?.HideNow();
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
        StopClickHook();
        _overlay?.Close();
        _overlay = null;
        _targetLock?.Close();
        _targetLock = null;
    }

    private void StartClickHook()
    {
        if (_clickHook is not null || _overlay is null) return;
        try
        {
            var overlay = _overlay;
            _clickHook = new HudClickHook(overlay.HitTestButton, button =>
            {
                if (button == HudButton.Pause) PauseRequested?.Invoke(this, EventArgs.Empty);
                else StopRequested?.Invoke(this, EventArgs.Empty);
            });
        }
        catch (Exception)
        {
            _clickHook = null; // no clickable buttons; F8 / Esc still work
        }
    }

    private void StopClickHook()
    {
        _clickHook?.Dispose();
        _clickHook = null;
    }
}

/// <summary>The two mini buttons of the HUD.</summary>
internal enum HudButton
{
    Pause,
    Stop,
}

/// <summary>
/// Makes the HUD's ⏸ / ⏹ buttons clickable although the HUD window is click-through: a low-level mouse hook (UI thread,
/// only while playing) looks at PHYSICAL left-button presses (injected ones, e.g. the macro's own clicks, are ignored) and
/// when one lands on a HUD button it is swallowed and turned into Pause / Stop. Everything else passes through untouched,
/// so a macro that clicks under the HUD (or the user clicking the game) is never blocked.
/// </summary>
internal sealed class HudClickHook : IDisposable
{
    private readonly Func<int, int, HudButton?> _hitTest;
    private readonly Action<HudButton> _clicked;
    private readonly NativeMethods.LowLevelHookProc _proc; // kept alive while the hook is installed
    private IntPtr _hook;
    private bool _swallowUp;

    public HudClickHook(Func<int, int, HudButton?> hitTest, Action<HudButton> clicked)
    {
        _hitTest = hitTest;
        _clicked = clicked;
        _proc = Callback;
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) throw new InvalidOperationException("Could not watch the mouse for the HUD buttons.");
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var message = wParam.ToInt32();
                if (message is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_LBUTTONUP)
                {
                    var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                    var physical = (data.flags & NativeMethods.LLMHF_INJECTED) == 0 && data.dwExtraInfo != NativeMethods.SmartMacroInputTag;
                    if (message == NativeMethods.WM_LBUTTONDOWN && physical && _hitTest(data.pt.X, data.pt.Y) is { } button)
                    {
                        _swallowUp = true;
                        // Act after the hook returns (never do real work inside a low-level hook).
                        Application.Current?.Dispatcher.InvokeAsync(() => _clicked(button));
                        return (IntPtr)1;
                    }

                    if (message == NativeMethods.WM_LBUTTONUP && physical && _swallowUp)
                    {
                        _swallowUp = false;
                        return (IntPtr)1;
                    }
                }
            }
            catch (Exception)
            {
                // A hook must never throw back into Windows.
            }
        }

        return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}

/// <summary>
/// The AAA mini-HUD: a dark-glass pill in the top-right corner of the main monitor.
///   ● Run #3 / ∞   00:24                 [⏸ F8] [⏹ Esc]
///   Step 4/12: Find image
///   Finding image "SHAKE" on the main monitor…
///   ▬▬▬▬▬▬▬▬▬▬▬▬▬────────────── (step progress)
/// The dot pulses (green = playing, amber = paused, red = recording). The window itself is click-through, never
/// activated and hidden from screen captures; the two mini buttons work through <see cref="HudClickHook"/>.
/// </summary>
internal sealed class RunOverlayWindow : Window
{
    private const double HudWidth = 372;

    private static readonly Color Green = Color.FromRgb(0x22, 0xC5, 0x5E);
    private static readonly Color Amber = Color.FromRgb(0xF5, 0x9E, 0x0B);
    private static readonly Color Red = Color.FromRgb(0xEF, 0x44, 0x44);
    private static readonly Color Cyan = Color.FromRgb(0x67, 0xE8, 0xF9);
    private static readonly Color TextColor = Color.FromRgb(0xE6, 0xEB, 0xF5);
    private static readonly Color Muted = Color.FromRgb(0x94, 0xA3, 0xB8);
    private static readonly Color Violet = Color.FromRgb(0xC0, 0x84, 0xFC);

    // Frozen brushes for the step line: SetHud runs up to ~30 times a second while playing, so it must not allocate
    // (and re-render) a new brush every time.
    private static readonly SolidColorBrush AmberTextBrush = Frozen(Amber);
    private static readonly SolidColorBrush NormalTextBrush = Frozen(TextColor);

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
    private static readonly FontFamily UiFont = new("Segoe UI Variable Text, Segoe UI");

    private readonly Ellipse _dot;
    private readonly System.Windows.Media.Effects.DropShadowEffect _dotGlow;
    private readonly Border _runBadge;
    private readonly TextBlock _runText;
    private readonly TextBlock _timer;
    private readonly TextBlock _telemetry; // "⚡ 0.6ms" live vision speed
    private readonly TextBlock _stepText;
    private readonly TextBlock _activity;
    private readonly Border _progressTrack;
    private readonly ScaleTransform _progressScale = new(0, 1);
    private readonly Border _pauseButton;
    private readonly TextBlock _pauseLabel;
    private readonly Border _stopButton;
    private readonly TextBlock _stopLabel;
    private readonly StackPanel _buttons;
    private OverlayKind? _kind;

    // Button rectangles in physical screen pixels (read by the click hook on the UI thread).
    private Rect _pauseRect = Rect.Empty;
    private Rect _stopRect = Rect.Empty;

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
        SizeToContent = SizeToContent.Height;
        Width = HudWidth;
        Title = "SmartMacro status";

        _dotGlow = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 10, ShadowDepth = 0, Opacity = 0.95, Color = Green };
        _dot = new Ellipse { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center, Fill = new SolidColorBrush(Green), Effect = _dotGlow };

        _runText = MakeText(12, FontWeights.SemiBold, Cyan);
        _runBadge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0x22, 0xD3, 0xEE)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x88, 0x22, 0xD3, 0xEE)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(7, 1, 7, 2),
            Margin = new Thickness(9, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = _runText,
        };
        _timer = MakeText(12, FontWeights.SemiBold, TextColor);
        _timer.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        _timer.Margin = new Thickness(9, 0, 0, 0);
        _telemetry = MakeText(11, FontWeights.SemiBold, Violet);
        _telemetry.Margin = new Thickness(9, 0, 0, 0);
        _telemetry.Visibility = Visibility.Collapsed;
        _telemetry.ToolTip = "How long the latest screen look took (capture + search)";

        (_pauseButton, _pauseLabel) = MakeMiniButton("⏸ F8", Amber);
        (_stopButton, _stopLabel) = MakeMiniButton("⏹ Esc", Red);
        _stopButton.Margin = new Thickness(6, 0, 0, 0);
        _buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { _pauseButton, _stopButton } };

        var top = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(_buttons, Dock.Right);
        top.Children.Add(_buttons);
        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new Views.BrandLogo { Width = 18, Height = 18, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center },
                _dot,
                _runBadge,
                _timer,
                _telemetry,
            },
        };
        DockPanel.SetDock(left, Dock.Left);
        top.Children.Add(left);

        _stepText = MakeText(12.5, FontWeights.SemiBold, TextColor);
        _stepText.Margin = new Thickness(0, 6, 0, 0);
        _stepText.TextTrimming = TextTrimming.CharacterEllipsis;
        _activity = MakeText(11, FontWeights.Normal, Muted);
        _activity.Margin = new Thickness(0, 1, 0, 0);
        _activity.TextTrimming = TextTrimming.CharacterEllipsis;

        var fill = new Border
        {
            CornerRadius = new CornerRadius(2),
            Background = new LinearGradientBrush(Color.FromRgb(0x22, 0xD3, 0xEE), Color.FromRgb(0xA8, 0x55, 0xF7), 0),
            RenderTransformOrigin = new Point(0, 0.5),
            RenderTransform = _progressScale,
        };
        _progressTrack = new Border
        {
            Height = 3,
            Margin = new Thickness(0, 7, 0, 0),
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(Color.FromArgb(0x40, 0x94, 0xA3, 0xB8)),
            Child = fill,
        };

        Content = new Border
        {
            Background = new LinearGradientBrush(Color.FromArgb(0xF0, 0x10, 0x18, 0x2A), Color.FromArgb(0xE8, 0x0A, 0x0F, 0x1C), 90),
            // The brand's glowing cyan → indigo edge (Themes/Brand.xaml), like the app icon.
            BorderBrush = Application.Current?.TryFindResource("BrandBorderBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(0x26, 0x32, 0x4A)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(12, 8, 12, 9),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.55, Color = Colors.Black },
            Child = new StackPanel { Children = { top, _stepText, _activity, _progressTrack } },
        };

        SizeChanged += (_, _) => PositionAtCorner();
        LayoutUpdated += (_, _) => UpdateButtonRects();
    }

    private static TextBlock MakeText(double size, FontWeight weight, Color color) => new()
    {
        Foreground = new SolidColorBrush(color),
        FontSize = size,
        FontWeight = weight,
        FontFamily = UiFont,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static (Border Button, TextBlock Label) MakeMiniButton(string text, Color color)
    {
        var label = MakeText(11, FontWeights.SemiBold, color);
        label.Text = text;
        var button = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x26, color.R, color.G, color.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, color.R, color.G, color.B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(7, 2, 7, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = label,
        };
        return (button, label);
    }

    /// <summary>Which mini button (if any) is at this physical screen point. Called by the click hook (UI thread).</summary>
    public HudButton? HitTestButton(int x, int y)
    {
        if (!IsVisible || _buttons.Visibility != Visibility.Visible) return null;
        var point = new Point(x, y);
        if (_pauseRect.Contains(point)) return HudButton.Pause;
        if (_stopRect.Contains(point)) return HudButton.Stop;
        return null;
    }

    public void SetHud(RunHud hud)
    {
        var recording = hud.Kind == OverlayKind.Recording;
        var color = hud.Kind switch
        {
            OverlayKind.Recording => Red,
            OverlayKind.Paused => Amber,
            _ => Green,
        };

        if (_kind != hud.Kind)
        {
            _kind = hud.Kind;
            _dot.Fill = new SolidColorBrush(color);
            _dotGlow.Color = color;
            // Pulse: slow while paused, a calm heartbeat while playing / recording.
            var pulse = new DoubleAnimation(1.0, 0.3, TimeSpan.FromMilliseconds(hud.Kind == OverlayKind.Paused ? 1100 : 700))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            _dot.BeginAnimation(OpacityProperty, pulse);
        }

        _runText.Text = !string.IsNullOrEmpty(hud.Counter) ? hud.Counter : recording ? "● REC" : "▶ PLAY";
        _timer.Text = FormatElapsed(hud.Elapsed);
        var telemetry = string.IsNullOrEmpty(hud.Telemetry) ? null : hud.Telemetry.Replace("Vision: ", "", StringComparison.Ordinal);
        _telemetry.Text = telemetry ?? string.Empty;
        _telemetry.Visibility = telemetry is null ? Visibility.Collapsed : Visibility.Visible;

        _buttons.Visibility = recording ? Visibility.Collapsed : Visibility.Visible;
        _pauseLabel.Text = hud.Kind == OverlayKind.Paused ? $"▶ {hud.PauseKey}" : $"⏸ {hud.PauseKey}";
        _stopLabel.Text = $"⏹ {hud.StopKey}";

        var step = hud.StepText;
        _stepText.Text = !string.IsNullOrEmpty(step) ? step : recording ? "Recording" : hud.Kind == OverlayKind.Paused ? "Paused" : "Starting…";
        _stepText.Foreground = hud.Kind == OverlayKind.Paused ? AmberTextBrush : NormalTextBrush;
        _activity.Text = hud.Text;
        _activity.Visibility = string.IsNullOrWhiteSpace(hud.Text) ? Visibility.Collapsed : Visibility.Visible;

        _progressTrack.Visibility = recording ? Visibility.Collapsed : Visibility.Visible;
        _progressScale.ScaleX = Math.Clamp(double.IsNaN(hud.Progress) ? 0 : hud.Progress, 0, 1);
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        return elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}" : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    private void UpdateButtonRects()
    {
        _pauseRect = ScreenRectOf(_pauseButton);
        _stopRect = ScreenRectOf(_stopButton);
    }

    /// <summary>An element's rectangle in physical screen pixels (empty when it isn't shown).</summary>
    private Rect ScreenRectOf(FrameworkElement element)
    {
        try
        {
            if (!element.IsVisible || PresentationSource.FromVisual(element) is null) return Rect.Empty;
            var topLeft = element.PointToScreen(new Point(0, 0));
            var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
            var rect = new Rect(topLeft, bottomRight);
            rect.Inflate(3, 3); // a little forgiving around the small buttons
            return rect;
        }
        catch (InvalidOperationException)
        {
            return Rect.Empty;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Click-through, never activated, hidden from Alt+Tab.
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLongValue(hwnd, NativeMethods.GWL_EXSTYLE);
        style |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLongValue(hwnd, NativeMethods.GWL_EXSTYLE, style);

        // Keep the HUD out of screen captures so OCR / Find image don't read it (Windows 10 2004+; best effort). On older
        // Windows it stays visible to captures: it's small and in a corner, and hiding it for every capture would flicker.
        CaptureShield.TryExclude(hwnd);
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
