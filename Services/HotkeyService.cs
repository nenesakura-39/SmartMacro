using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
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
/// Also registers per-macro shortcuts (v1.4 "Reflex") and polls key release for Hold-to-run / Repeat-while-held modes.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int MacroHotkeyBaseId = 1_000;

    private readonly ILogService _log;
    private readonly List<int> _registered = new();
    private readonly Dictionary<int, HotkeyBinding> _macroHotkeysById = new();
    private readonly HashSet<HotkeyBinding> _heldMacroBindings = new();
    private readonly DispatcherTimer _holdPollTimer;
    private HwndSource? _source;
    private IntPtr _hwnd;
    private HotkeyBinding _pauseBinding = HotkeyBinding.DefaultPause;
    private bool _pauseActive;
    private bool _pauseRegistered;
    private List<HotkeyBinding> _desiredMacroHotkeys = new();

    public HotkeyService(ILogService log)
    {
        _log = log;
        _holdPollTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(25),
        };
        _holdPollTimer.Tick += OnHoldPollTick;
    }

    public event EventHandler<HotkeyAction>? Pressed;

    /// <summary>Raised when a registered per-macro shortcut is pressed down.</summary>
    public event EventHandler<HotkeyBinding>? MacroHotkeyPressed;

    /// <summary>Raised when a held per-macro shortcut is released.</summary>
    public event EventHandler<HotkeyBinding>? MacroHotkeyReleased;

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
        RegisterMacroHotkeysInternal();
        return failed;
    }

    /// <summary>(Re-)registers per-macro trigger shortcuts across all armed macros in the library.</summary>
    public void ApplyMacroHotkeys(IEnumerable<HotkeyBinding> bindings)
    {
        _desiredMacroHotkeys = bindings?.Distinct().ToList() ?? new List<HotkeyBinding>();
        UnregisterMacroHotkeys();
        RegisterMacroHotkeysInternal();
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
        _holdPollTimer.Stop();
        UnregisterAll();
        _source?.RemoveHook(WndProc);
        _source = null;
    }

    private void Register(HotkeyAction action, HotkeyBinding binding, List<string> failed)
    {
        if (_hwnd == IntPtr.Zero) return;

        var modifiers = ToNativeModifiers(binding.Modifiers);
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

    private void RegisterMacroHotkeysInternal()
    {
        if (_hwnd == IntPtr.Zero || _desiredMacroHotkeys.Count == 0) return;

        var nextId = MacroHotkeyBaseId;
        foreach (var binding in _desiredMacroHotkeys)
        {
            if (binding.Problem() is not null) continue;
            var id = nextId++;
            var modifiers = ToNativeModifiers(binding.Modifiers);
            if (RegisterHotKey(_hwnd, id, modifiers, (uint)binding.VirtualKey))
            {
                _registered.Add(id);
                _macroHotkeysById[id] = binding;
            }
        }
    }

    private void UnregisterMacroHotkeys()
    {
        foreach (var id in _macroHotkeysById.Keys.ToList())
        {
            if (_hwnd != IntPtr.Zero) UnregisterHotKey(_hwnd, id);
            _registered.Remove(id);
        }

        _macroHotkeysById.Clear();
    }

    private static uint ToNativeModifiers(KeyModifiers modifiers)
    {
        var flags = MOD_NOREPEAT;
        if (modifiers.HasFlag(KeyModifiers.Control)) flags |= MOD_CONTROL;
        if (modifiers.HasFlag(KeyModifiers.Shift)) flags |= MOD_SHIFT;
        if (modifiers.HasFlag(KeyModifiers.Alt)) flags |= MOD_ALT;
        if (modifiers.HasFlag(KeyModifiers.Windows)) flags |= MOD_WIN;
        return flags;
    }

    private void UnregisterAll()
    {
        foreach (var id in _registered) UnregisterHotKey(_hwnd, id);
        _registered.Clear();
        _macroHotkeysById.Clear();
        _heldMacroBindings.Clear();
        _holdPollTimer.Stop();
        _pauseRegistered = false;
    }

    private void OnHoldPollTick(object? sender, EventArgs e)
    {
        if (_heldMacroBindings.Count == 0)
        {
            _holdPollTimer.Stop();
            return;
        }

        foreach (var binding in _heldMacroBindings.ToList())
        {
            var isKeyDown = (GetAsyncKeyState(binding.VirtualKey) & 0x8000) != 0;
            if (!isKeyDown)
            {
                _heldMacroBindings.Remove(binding);
                MacroHotkeyReleased?.Invoke(this, binding);
            }
        }

        if (_heldMacroBindings.Count == 0)
            _holdPollTimer.Stop();
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
            else if (_macroHotkeysById.TryGetValue(id, out var binding))
            {
                handled = true;
                _heldMacroBindings.Add(binding);
                if (!_holdPollTimer.IsEnabled) _holdPollTimer.Start();
                MacroHotkeyPressed?.Invoke(this, binding);
            }
        }

        return IntPtr.Zero;
    }
}
