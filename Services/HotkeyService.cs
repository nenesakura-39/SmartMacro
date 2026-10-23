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
/// Record and Play / Stop are always registered. Pause / Resume (F8 by default) is only registered while a macro plays
/// (<see cref="SetPauseActive"/>), so the key keeps working normally in other apps the rest of the time.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly ILogService _log;
    private readonly List<int> _registered = new();
    private HwndSource? _source;
    private IntPtr _hwnd;
    private HotkeyBinding _pauseBinding = HotkeyBinding.DefaultPause;
    private bool _pauseActive;
    private bool _pauseRegistered;

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
        _pauseBinding = settings.PauseHotkey;
        if (_pauseActive) RegisterPause(failed);
        return failed;
    }

    /// <summary>
    /// Turns the pause / resume shortcut on while a macro plays (or is paused) and off again afterwards. Call on the UI
    /// thread. A shortcut another program already uses is logged once per playback.
    /// </summary>
    public void SetPauseActive(bool active)
    {
        if (_pauseActive == active) return;
        _pauseActive = active;
        if (active)
        {
            RegisterPause(null);
        }
        else if (_pauseRegistered)
        {
            UnregisterHotKey(_hwnd, (int)HotkeyAction.TogglePause);
            _registered.Remove((int)HotkeyAction.TogglePause);
            _pauseRegistered = false;
        }
    }

    private void RegisterPause(List<string>? failed)
    {
        if (_pauseRegistered) return;
        var count = _registered.Count;
        Register(HotkeyAction.TogglePause, _pauseBinding, failed ?? new List<string>());
        _pauseRegistered = _registered.Count > count;
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
        _pauseRegistered = false;
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
