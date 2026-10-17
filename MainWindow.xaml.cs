using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using SmartMacro.ViewModels;

namespace SmartMacro;

public partial class MainWindow : Window
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR = 35;

    private bool _closeConfirmed;
    private bool _closePending;

    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>Smallest / largest height of the Activity list when dragging its grip.</summary>
    private const double MinActivityHeight = 40;
    private const double MaxActivityHeight = 420;

    // Dragging the grip on top of the Activity panel: up = taller, down = shorter.
    private void ActivityGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var height = double.IsNaN(ActivityList.Height) ? ActivityList.ActualHeight : ActivityList.Height;
        ActivityList.Height = Math.Clamp(height - e.VerticalChange, MinActivityHeight, MaxActivityHeight);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyDarkTitleBar();
    }

    /// <summary>
    /// Asks about unsaved changes before closing. The question may involve an async save,
    /// so we cancel this close, ask, and then close again if the user agreed.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
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
    /// Makes the Windows title bar dark to match the app (Windows 10 20H1+ / Windows 11).
    /// Purely cosmetic: on older Windows versions the calls just fail and nothing changes.
    /// </summary>
    private void ApplyDarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            var enabled = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int));

            // COLORREF is 0x00BBGGRR; this is the studio canvas colour #0B0F17 (Windows 11 only).
            var caption = 0x00170F0B;
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Not available on this Windows version; keep the default title bar.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
