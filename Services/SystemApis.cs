using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using SmartMacro.Core.Logging;
using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Storage;
using SmartMacro.Services.Input;

namespace SmartMacro.Services;

/// <summary>
/// Thin testable wrapper around Win32 global hotkey registration and key-state polling APIs.
/// </summary>
public interface ISystemHotkeyApi
{
    bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    bool UnregisterHotKey(IntPtr hWnd, int id);

    short GetAsyncKeyState(int vKey);
}

internal sealed class Win32SystemHotkeyApi : ISystemHotkeyApi
{
    public bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk) =>
        NativeMethods.RegisterHotKey(hWnd, id, fsModifiers, vk);

    public bool UnregisterHotKey(IntPtr hWnd, int id) =>
        NativeMethods.UnregisterHotKey(hWnd, id);

    public short GetAsyncKeyState(int vKey) =>
        NativeMethods.GetAsyncKeyState(vKey);
}

/// <summary>
/// Thin testable wrapper around Win32 window enumeration, title/process inspection, and DWM bounds APIs.
/// </summary>
public interface IWindowApi
{
    void EnumWindows(Func<IntPtr, bool> callback);

    IntPtr GetForegroundWindow();

    uint GetWindowThreadProcessId(IntPtr hwnd);

    string? GetWindowText(IntPtr hwnd);

    bool IsWindowVisible(IntPtr hwnd);

    bool IsToolWindow(IntPtr hwnd);

    bool HasOwner(IntPtr hwnd);

    bool IsCloaked(IntPtr hwnd);

    bool IsMinimized(IntPtr hwnd);

    ScreenRect? GetWindowBounds(IntPtr hwnd);

    string QueryProcessName(uint processId);
}

internal sealed class Win32WindowApi : IWindowApi
{
    public void EnumWindows(Func<IntPtr, bool> callback)
    {
        WindowApi.EnumWindows((hwnd, _) => callback(hwnd), IntPtr.Zero);
    }

    public IntPtr GetForegroundWindow() => NativeMethods.GetForegroundWindow();

    public uint GetWindowThreadProcessId(IntPtr hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }

    public string? GetWindowText(IntPtr hwnd)
    {
        var length = WindowApi.GetWindowTextLength(hwnd);
        if (length <= 0) return null;
        var sb = new StringBuilder(length + 1);
        WindowApi.GetWindowText(hwnd, sb, sb.Capacity);
        var trimmed = sb.ToString().Trim();
        return trimmed.Length > 0 ? trimmed : null;
    }

    public bool IsWindowVisible(IntPtr hwnd) => WindowApi.IsWindowVisible(hwnd);

    public bool IsToolWindow(IntPtr hwnd) =>
        (NativeMethods.GetWindowLongValue(hwnd, NativeMethods.GWL_EXSTYLE) & NativeMethods.WS_EX_TOOLWINDOW) != 0;

    public bool HasOwner(IntPtr hwnd) => WindowApi.GetWindow(hwnd, WindowApi.GW_OWNER) != IntPtr.Zero;

    public bool IsCloaked(IntPtr hwnd) =>
        WindowApi.DwmGetWindowAttribute(hwnd, WindowApi.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    public bool IsMinimized(IntPtr hwnd) => WindowApi.IsIconic(hwnd);

    public ScreenRect? GetWindowBounds(IntPtr hwnd)
    {
        if (WindowApi.DwmGetWindowAttribute(hwnd, WindowApi.DWMWA_EXTENDED_FRAME_BOUNDS, out NativeMethods.RECT frame,
                Marshal.SizeOf<NativeMethods.RECT>()) == 0 && frame.Right > frame.Left && frame.Bottom > frame.Top)
            return new ScreenRect(frame.Left, frame.Top, frame.Right - frame.Left, frame.Bottom - frame.Top);

        return WindowApi.GetWindowRect(hwnd, out var rect) && rect.Right > rect.Left && rect.Bottom > rect.Top
            ? new ScreenRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)
            : null;
    }

    public string QueryProcessName(uint processId)
    {
        var handle = WindowApi.OpenProcess(WindowApi.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == IntPtr.Zero) return string.Empty;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            if (WindowApi.QueryFullProcessImageName(handle, 0, buffer, ref size))
                return Path.GetFileNameWithoutExtension(buffer.ToString(0, size));
        }
        finally
        {
            WindowApi.CloseHandle(handle);
        }

        return string.Empty;
    }
}

/// <summary>
/// Wraps settings JSON persistence together with Windows startup registration and runtime theme updates
/// so <see cref="ViewModels.SettingsViewModel"/> can be tested without touching the registry or global WPF resources.
/// </summary>
public interface ISettingsPersistence : ISettingsStore
{
    void ApplyAppearance(AppSettings settings);

    bool ApplyStartup(bool enabled, ILogService? log = null);
}

public sealed class WindowsSettingsPersistence : ISettingsPersistence
{
    private readonly ISettingsStore _inner;

    public WindowsSettingsPersistence(ISettingsStore inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
        _inner.LoadAsync(cancellationToken);

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
        _inner.SaveAsync(settings, cancellationToken);

    public void ApplyAppearance(AppSettings settings) => ThemeManager.Apply(settings);

    public bool ApplyStartup(bool enabled, ILogService? log = null) =>
        WindowsStartupService.Apply(enabled, log);
}
