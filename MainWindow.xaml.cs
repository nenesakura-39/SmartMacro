using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SmartMacro.Services;
using SmartMacro.ViewModels;
using SmartMacro.Views;

namespace SmartMacro;

public partial class MainWindow : Window
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWCP_ROUND = 2;
    private const int DWMSBT_TABBEDWINDOW = 4; // Windows 11 Mica-Alt

    private const int WM_NCHITTEST = 0x0084;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int WM_NCLBUTTONUP = 0x00A2;
    private const int WM_NCMOUSELEAVE = 0x02A2;
    private const int HTMAXBUTTON = 9;

    private bool _closeConfirmed;
    private bool _closePending;
    private bool _forceExit;
    private bool _maxButtonPressed;
    private MissionControlWindow? _missionControl;
    private MainViewModel? _wiredViewModel;

    public void ForceClose()
    {
        _forceExit = true;
        Close();
    }

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closed += OnWindowClosed;
    }

    /// <summary>Smallest / largest height of the Activity list when dragging its grip.</summary>
    private const double MinActivityHeight = 40;
    private const double MaxActivityHeight = 420;

    private void ActivityGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var height = double.IsNaN(ActivityList.Height) ? ActivityList.ActualHeight : ActivityList.Height;
        ActivityList.Height = Math.Clamp(height - e.VerticalChange, MinActivityHeight, MaxActivityHeight);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_wiredViewModel is not null)
            _wiredViewModel.MissionControlRequested -= OnMissionControlRequested;

        _wiredViewModel = e.NewValue as MainViewModel;
        if (_wiredViewModel is not null)
            _wiredViewModel.MissionControlRequested += OnMissionControlRequested;
    }

    private void OnMissionControlRequested(object? sender, EventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (_missionControl is { IsLoaded: true })
        {
            if (_missionControl.WindowState == WindowState.Minimized)
                _missionControl.WindowState = WindowState.Normal;
            _missionControl.Activate();
            return;
        }

        _missionControl = new MissionControlWindow { DataContext = vm };
        _missionControl.Closed += (_, _) => _missionControl = null;
        _missionControl.Show();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (_wiredViewModel is not null)
            _wiredViewModel.MissionControlRequested -= OnMissionControlRequested;
        if (_missionControl is { IsLoaded: true })
            _missionControl.Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero && HwndSource.FromHwnd(hwnd) is { } source)
        {
            source.AddHook(WndProc);
        }

        ApplyStudioWindowBackdrop(hwnd);
        UpdateMaximizedPadding();
    }

    private void MainWindow_StateChanged(object sender, EventArgs e) => UpdateMaximizedPadding();

    private void UpdateMaximizedPadding()
    {
        if (WindowState == WindowState.Maximized)
        {
            var frame = SystemParameters.WindowResizeBorderThickness;
            RootBorder.Padding = new Thickness(frame.Left + 4, frame.Top + 4, frame.Right + 4, frame.Bottom + 4);
        }
        else
        {
            RootBorder.Padding = new Thickness(0);
        }
    }

    // ---------------- Caption Buttons & Win11 Snap Layouts ----------------

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs e) => ToggleMaximizeRestore();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximizeRestore()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
            {
                if (MaximizeRestoreButton is not null && IsCursorOverElement(MaximizeRestoreButton, lParam))
                {
                    MaximizeRestoreButton.Background = TryFindResource("HoverBrush") as Brush ?? Brushes.Transparent;
                    handled = true;
                    return new IntPtr(HTMAXBUTTON);
                }

                MaximizeRestoreButton?.ClearValue(BackgroundProperty);
                break;
            }
            case WM_NCLBUTTONDOWN when wParam.ToInt32() == HTMAXBUTTON:
                _maxButtonPressed = true;
                if (MaximizeRestoreButton is not null)
                    MaximizeRestoreButton.Background = TryFindResource("PressedBrush") as Brush ?? Brushes.Transparent;
                handled = true;
                return IntPtr.Zero;

            case WM_NCLBUTTONUP when wParam.ToInt32() == HTMAXBUTTON:
                var wasPressed = _maxButtonPressed;
                _maxButtonPressed = false;
                MaximizeRestoreButton?.ClearValue(BackgroundProperty);
                if (wasPressed && MaximizeRestoreButton is not null && IsCursorOverElement(MaximizeRestoreButton, lParam))
                    ToggleMaximizeRestore();
                handled = true;
                return IntPtr.Zero;

            case WM_NCMOUSELEAVE:
                _maxButtonPressed = false;
                MaximizeRestoreButton?.ClearValue(BackgroundProperty);
                break;
        }

        return IntPtr.Zero;
    }

    private static bool IsCursorOverElement(FrameworkElement? element, IntPtr lParam)
    {
        if (element is null || !element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
            return false;

        try
        {
            var xy = lParam.ToInt64();
            var screenX = (short)(xy & 0xFFFF);
            var screenY = (short)((xy >> 16) & 0xFFFF);
            var pt = element.PointFromScreen(new Point(screenX, screenY));
            return pt.X >= 0 && pt.X <= element.ActualWidth && pt.Y >= 0 && pt.Y <= element.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    // ---------------- Unpinned Sidebar Hover Flyout ----------------

    private void SidebarRail_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (DataContext is MainViewModel { IsSidebarPinned: true }) return;
        SetSidebarOverlayExpanded(true);
    }

    private void SidebarRail_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (DataContext is MainViewModel { IsSidebarPinned: true }) return;
        SetSidebarOverlayExpanded(false);
    }

    private void SidebarToggle_Click(object sender, RoutedEventArgs e)
    {
        var pinned = DataContext is MainViewModel { IsSidebarPinned: true };
        SidebarRail.BeginAnimation(WidthProperty, null);
        SidebarRail.ClearValue(WidthProperty);
        SidebarRail.Effect = null;
        var vis = pinned ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceHeaderLabel.SetCurrentValue(VisibilityProperty, vis);
        StudioHeaderLabel.SetCurrentValue(VisibilityProperty, vis);
        PinLabelText.SetCurrentValue(VisibilityProperty, vis);
        PinLabelText.SetCurrentValue(System.Windows.Controls.TextBlock.TextProperty, pinned ? "Pinned" : "Pin sidebar");
        SidebarFooterCard.SetCurrentValue(VisibilityProperty, vis);
    }

    private void SetSidebarOverlayExpanded(bool expanded)
    {
        double targetWidth = expanded ? 192 : 56;
        var vis = expanded ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceHeaderLabel.SetCurrentValue(VisibilityProperty, vis);
        StudioHeaderLabel.SetCurrentValue(VisibilityProperty, vis);
        PinLabelText.SetCurrentValue(VisibilityProperty, vis);
        SidebarFooterCard.SetCurrentValue(VisibilityProperty, vis);
        SidebarRail.Effect = expanded ? TryFindResource("ShadowPopover") as System.Windows.Media.Effects.Effect : null;

        if (!ThemeManager.AreAnimationsEnabled)
        {
            SidebarRail.BeginAnimation(WidthProperty, null);
            SidebarRail.SetCurrentValue(WidthProperty, targetWidth);
            return;
        }

        var anim = new DoubleAnimation(targetWidth, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        SidebarRail.BeginAnimation(WidthProperty, anim);
    }

    // ---------------- Close Confirmation ----------------

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_forceExit && DataContext is MainViewModel trayVm && trayVm.CurrentSettings.MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            Hide();
            trayVm.SetStatus("SmartMacro is running in the system tray (macro triggers stay active).", StatusKind.Info);
            return;
        }

        if (_closeConfirmed || DataContext is not MainViewModel vm || (!vm.Editor.IsDirty && !vm.Automation.IsBusy))
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        if (_closePending) return;
        _closePending = true;

        Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                if (await vm.ConfirmCloseAsync())
                {
                    _closeConfirmed = true;
                    Close();
                }
            }
            finally
            {
                _closePending = false;
            }
        });
    }

    /// <summary>
    /// Applies Windows 11 dark frame, rounded corners, and Mica-Alt backdrop (falling back cleanly to solid Obsidian on Windows 10).
    /// </summary>
    private void ApplyStudioWindowBackdrop(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            var enabled = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int));

            var round = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

            if (Environment.OSVersion.Version.Build >= 22621)
            {
                if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target)
                    target.BackgroundColor = Colors.Transparent;

                var backdrop = DWMSBT_TABBEDWINDOW;
                var hr = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
                if (hr == 0)
                {
                    Background = Brushes.Transparent;
                    return;
                }
            }

            var caption = 0x00110A08;
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
