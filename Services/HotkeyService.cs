using System.Windows;
using System.Windows.Interop;
using SmartMacro.Core.Logging;
using SmartMacro.Core.Models;
using static SmartMacro.Services.Input.NativeMethods;

namespace SmartMacro.Services;

public enum HotkeyAction
{
    ToggleRecording = 1,
    TogglePlayback = 2,
    TogglePause = 3,
}

/// <summary>
/// System-wide shortcuts (they work even when SmartMacro is minimized), registered with RegisterHotKey.
/// The keys come from Settings and can be changed while the app runs.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly ILogService _log;
    private readonly List<int> _registered = new();
    private HwndSource? _source;
    private IntPtr _hwnd;

    public HotkeyService(ILogService log)
    {
        _log = log;
    }

    public event EventHandler<HotkeyAction>? Pressed;

    /// <summary>Hooks into the window's messages. Call after the window has been shown.</summary>
    public void Attach(Window window)
    {
        _hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
    }

    /// <summary>(Re-)registers the shortcuts from settings. Returns the ones Windows refused (already used elsewhere).</summary>
    public IReadOnlyList<string> Apply(AppSettings settings)
    {
        UnregisterAll();
        var failed = new List<string>();
        Register(HotkeyAction.ToggleRecording, settings.RecordHotkey, failed);
        Register(HotkeyAction.TogglePlayback, settings.PlayHotkey, failed);
        Register(HotkeyAction.TogglePause, settings.PauseHotkey, failed);
        return failed;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source?.RemoveHook(WndProc);
        _source = null;
    }

    private void Register(HotkeyAction action, HotkeyBinding binding, List<string> failed)
    {
        if (_hwnd == IntPtr.Zero) return;

        var modifiers = MOD_NOREPEAT;
        if (binding.Modifiers.HasFlag(KeyModifiers.Control)) modifiers |= MOD_CONTROL;
        if (binding.Modifiers.HasFlag(KeyModifiers.Shift)) modifiers |= MOD_SHIFT;
        if (binding.Modifiers.HasFlag(KeyModifiers.Alt)) modifiers |= MOD_ALT;
        if (binding.Modifiers.HasFlag(KeyModifiers.Windows)) modifiers |= MOD_WIN;

        var id = (int)action;
        if (RegisterHotKey(_hwnd, id, modifiers, (uint)binding.VirtualKey))
        {
            _registered.Add(id);
        }
        else
        {
            failed.Add(binding.ToString());
            _log.Warning($"The shortcut {binding} is already used by Windows or another program, so it won't work in SmartMacro. Pick a different one in Settings.");
        }
    }

    private void UnregisterAll()
    {
        foreach (var id in _registered) UnregisterHotKey(_hwnd, id);
        _registered.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            var id = wParam.ToInt32();
            if (Enum.IsDefined(typeof(HotkeyAction), id))
            {
                handled = true;
                Pressed?.Invoke(this, (HotkeyAction)id);
            }
        }

        return IntPtr.Zero;
    }
}
