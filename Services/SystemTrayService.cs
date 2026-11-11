using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SmartMacro.Services;

/// <summary>
/// Lightweight Win32 <c>Shell_NotifyIconW</c> system tray service (v1.5 "Context").
/// Lets SmartMacro stay resident in the Windows notification area when closed-to-tray so per-macro hotkeys,
/// schedules, and window triggers continue running silently in the background.
/// </summary>
public sealed class SystemTrayService : IDisposable
{
    private const int WM_USER = 0x0400;
    private const int WM_TRAYICON = WM_USER + 0x145;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;

    private const uint MF_STRING = 0x00000000;
    private const uint MF_CHECKED = 0x00000008;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_NONOTIFY = 0x0080;

    private const int IDI_APPLICATION = 32512;
    private const int GCLP_HICONSM = -34;
    private const int GCLP_HICON = -14;

    private const int CmdOpenStudio = 1001;
    private const int CmdToggleTriggers = 1002;
    private const int CmdStopPlayback = 1003;
    private const int CmdExitApp = 1004;

    private IntPtr _hwnd;
    private HwndSource? _source;
    private IntPtr _ownedIcon;
    private bool _iconAdded;
    private bool _disposed;

    private readonly Action _openStudio;
    private readonly Func<bool> _areTriggersArmed;
    private readonly Action _toggleTriggers;
    private readonly Action _stopPlayback;
    private readonly Action _exitApp;

    public SystemTrayService(
        Action openStudio,
        Func<bool> areTriggersArmed,
        Action toggleTriggers,
        Action stopPlayback,
        Action exitApp)
    {
        _openStudio = openStudio;
        _areTriggersArmed = areTriggersArmed;
        _toggleTriggers = toggleTriggers;
        _stopPlayback = stopPlayback;
        _exitApp = exitApp;
    }

    public void Attach(Window window)
    {
        _hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
        EnsureIconAdded("SmartMacro Studio — Triggers Armed");
    }

    public void UpdateTooltip(string tooltip)
    {
        if (_disposed || !_iconAdded || _hwnd == IntPtr.Zero) return;
        var data = CreateNotifyIconData(NIF_TIP, tooltip);
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    private void EnsureIconAdded(string tooltip)
    {
        if (_disposed || _iconAdded || _hwnd == IntPtr.Zero) return;
        var data = CreateNotifyIconData(NIF_MESSAGE | NIF_ICON | NIF_TIP, tooltip);
        _iconAdded = Shell_NotifyIconW(NIM_ADD, ref data);
    }

    private NOTIFYICONDATAW CreateNotifyIconData(uint flags, string tooltip)
    {
        var icon = EnsureTrayIcon();

        var tip = tooltip ?? "SmartMacro Studio";
        if (tip.Length > 120) tip = tip[..120];

        return new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = flags,
            uCallbackMessage = WM_TRAYICON,
            hIcon = icon,
            szTip = tip,
        };
    }

    private IntPtr EnsureTrayIcon()
    {
        if (_ownedIcon != IntPtr.Zero) return _ownedIcon;

        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            {
                ushort iconIndex = 0;
                var extracted = ExtractAssociatedIconW(IntPtr.Zero, exePath, ref iconIndex);
                if (extracted != IntPtr.Zero)
                {
                    _ownedIcon = extracted;
                    return _ownedIcon;
                }
            }
        }
        catch
        {
            // Fall back to window/class/default icon below.
        }

        var icon = GetClassLongPtr(_hwnd, GCLP_HICONSM);
        if (icon == IntPtr.Zero) icon = GetClassLongPtr(_hwnd, GCLP_HICON);
        if (icon == IntPtr.Zero) icon = LoadIconW(IntPtr.Zero, (IntPtr)IDI_APPLICATION);
        return icon;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAYICON)
        {
            var eventId = (int)(lParam.ToInt64() & 0xFFFF);
            if (eventId is WM_LBUTTONUP or WM_LBUTTONDBLCLK)
            {
                _openStudio();
                handled = true;
            }
            else if (eventId is WM_RBUTTONUP or WM_CONTEXTMENU)
            {
                ShowContextMenu();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    private void ShowContextMenu()
    {
        if (_hwnd == IntPtr.Zero) return;
        var hMenu = CreatePopupMenu();
        if (hMenu == IntPtr.Zero) return;

        try
        {
            var armed = _areTriggersArmed();
            AppendMenuW(hMenu, MF_STRING, CmdOpenStudio, "Open SmartMacro Studio");
            AppendMenuW(hMenu, MF_STRING | (armed ? MF_CHECKED : 0), CmdToggleTriggers, "Macro Triggers Armed");
            AppendMenuW(hMenu, MF_STRING, CmdStopPlayback, "■ Stop Playback");
            AppendMenuW(hMenu, MF_SEPARATOR, 0, null);
            AppendMenuW(hMenu, MF_STRING, CmdExitApp, "Exit SmartMacro");

            GetCursorPos(out var pt);
            SetForegroundWindow(_hwnd);
            var cmd = TrackPopupMenuEx(hMenu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY, pt.X, pt.Y, _hwnd, IntPtr.Zero);
            PostMessageW(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            switch (cmd)
            {
                case CmdOpenStudio:
                    _openStudio();
                    break;
                case CmdToggleTriggers:
                    _toggleTriggers();
                    break;
                case CmdStopPlayback:
                    _stopPlayback();
                    break;
                case CmdExitApp:
                    _exitApp();
                    break;
            }
        }
        finally
        {
            DestroyMenu(hMenu);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _source?.RemoveHook(WndProc);
        if (_iconAdded && _hwnd != IntPtr.Zero)
        {
            var data = new NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = _hwnd,
                uID = 1,
            };
            Shell_NotifyIconW(NIM_DELETE, ref data);
            _iconAdded = false;
        }
        if (_ownedIcon != IntPtr.Zero)
        {
            DestroyIcon(_ownedIcon);
            _ownedIcon = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractAssociatedIconW(IntPtr hInst, string lpIconPath, ref ushort lpiIcon);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    private static extern IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, uint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    private const uint WM_NULL = 0x0000;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
}
