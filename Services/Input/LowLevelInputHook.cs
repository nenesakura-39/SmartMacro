using System.ComponentModel;
using System.Runtime.InteropServices;
using SmartMacro.Core.Models;
using SmartMacro.Core.Recording;
using static SmartMacro.Services.Input.NativeMethods;

namespace SmartMacro.Services.Input;

/// <summary>One event seen by a low-level hook, plus where it came from.</summary>
internal readonly record struct HookEvent(
    RawInputKind Kind,
    uint Time,
    int X,
    int Y,
    MacroMouseButton Button,
    int Delta,
    int VirtualKey,
    bool Injected,
    bool FromSmartMacro,
    bool OverOwnWindow);

/// <summary>
/// Wraps Windows low-level mouse/keyboard hooks (WH_MOUSE_LL / WH_KEYBOARD_LL). These only observe
/// input; they never block it. Must be installed on a thread with a message loop (the UI thread),
/// and the callback must stay fast or Windows will skip it.
/// </summary>
internal sealed class LowLevelInputHook : IDisposable
{
    private static readonly uint OwnProcessId = (uint)Environment.ProcessId;

    private readonly bool _mouse;
    private readonly bool _keyboard;

    // Delegates must be kept alive while the hook is installed, or the GC frees them and Windows crashes.
    private LowLevelHookProc? _mouseProc;
    private LowLevelHookProc? _keyboardProc;
    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;

    public LowLevelInputHook(bool mouse, bool keyboard)
    {
        _mouse = mouse;
        _keyboard = keyboard;
    }

    public event Action<HookEvent>? Received;

    /// <summary>Last error thrown by a handler (handlers must never crash the hook callback).</summary>
    public Exception? LastHandlerError { get; private set; }

    public bool IsInstalled => _mouseHook != IntPtr.Zero || _keyboardHook != IntPtr.Zero;

    public void Install()
    {
        if (IsInstalled) return;

        var module = GetModuleHandle(null);
        try
        {
            if (_mouse)
            {
                _mouseProc = MouseCallback;
                _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
                if (_mouseHook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not start the mouse hook.");
            }

            if (_keyboard)
            {
                _keyboardProc = KeyboardCallback;
                _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, module, 0);
                if (_keyboardHook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not start the keyboard hook.");
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

        _mouseProc = null;
        _keyboardProc = null;
    }

    private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var message = wParam.ToInt32();
                RawInputKind? kind = null;
                var button = MacroMouseButton.Left;
                var delta = 0;

                switch (message)
                {
                    case WM_MOUSEMOVE: kind = RawInputKind.MouseMove; break;
                    case WM_LBUTTONDOWN: kind = RawInputKind.MouseDown; break;
                    case WM_LBUTTONUP: kind = RawInputKind.MouseUp; break;
                    case WM_RBUTTONDOWN: kind = RawInputKind.MouseDown; button = MacroMouseButton.Right; break;
                    case WM_RBUTTONUP: kind = RawInputKind.MouseUp; button = MacroMouseButton.Right; break;
                    case WM_MBUTTONDOWN: kind = RawInputKind.MouseDown; button = MacroMouseButton.Middle; break;
                    case WM_MBUTTONUP: kind = RawInputKind.MouseUp; button = MacroMouseButton.Middle; break;
                    case WM_MOUSEWHEEL: kind = RawInputKind.Wheel; delta = (short)((data.mouseData >> 16) & 0xFFFF); break;
                    case WM_MOUSEHWHEEL: kind = RawInputKind.HorizontalWheel; delta = (short)((data.mouseData >> 16) & 0xFFFF); break;
                }

                if (kind is RawInputKind k)
                {
                    // Checking the window under the cursor is cheap enough for clicks, but skip it for moves.
                    var overOwn = k != RawInputKind.MouseMove && IsOwnWindow(WindowFromPoint(data.pt));
                    Raise(new HookEvent(k, data.time, data.pt.X, data.pt.Y, button, delta, 0,
                        (data.flags & LLMHF_INJECTED) != 0, data.dwExtraInfo == SmartMacroInputTag, overOwn));
                }
            }
            catch (Exception ex)
            {
                LastHandlerError = ex;
            }
        }

        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var message = wParam.ToInt32();
                RawInputKind? kind = message switch
                {
                    WM_KEYDOWN or WM_SYSKEYDOWN => RawInputKind.KeyDown,
                    WM_KEYUP or WM_SYSKEYUP => RawInputKind.KeyUp,
                    _ => null,
                };

                if (kind is RawInputKind k)
                {
                    Raise(new HookEvent(k, data.time, 0, 0, MacroMouseButton.Left, 0, (int)data.vkCode,
                        (data.flags & LLKHF_INJECTED) != 0, data.dwExtraInfo == SmartMacroInputTag,
                        IsOwnWindow(GetForegroundWindow())));
                }
            }
            catch (Exception ex)
            {
                LastHandlerError = ex;
            }
        }

        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private void Raise(HookEvent e)
    {
        try
        {
            Received?.Invoke(e);
        }
        catch (Exception ex)
        {
            LastHandlerError = ex;
        }
    }

    private static bool IsOwnWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid == OwnProcessId;
    }
}

/// <summary>Records desktop mouse and keyboard input using low-level hooks.</summary>
public sealed class WindowsInputRecorder : IInputRecorder, IDisposable
{
    /// <summary>Safety cap (roughly an hour of busy input) so memory can't grow forever.</summary>
    public const int MaxEvents = 500_000;

    private readonly List<RawInputEvent> _events = new();
    private readonly HashSet<int> _keysRecordedDown = new(); // pressed in another app, not released yet
    private LowLevelInputHook? _hook;
    private uint _startTick;

    public bool IsRecording => _hook is not null;

    public int EventCount => _events.Count;

    public bool LimitReached { get; private set; }

    public int StartX { get; private set; }

    public int StartY { get; private set; }

    public void Start()
    {
        if (IsRecording) throw new InvalidOperationException("Already recording.");

        _events.Clear();
        _keysRecordedDown.Clear();
        LimitReached = false;
        _startTick = unchecked((uint)Environment.TickCount);
        if (GetCursorPos(out var pt))
        {
            StartX = pt.X;
            StartY = pt.Y;
        }
        else
        {
            StartX = 0;
            StartY = 0;
        }

        var hook = new LowLevelInputHook(mouse: true, keyboard: true);
        hook.Received += OnReceived;
        hook.Install(); // throws if Windows refuses the hook
        _hook = hook;
    }

    public IReadOnlyList<RawInputEvent> Stop()
    {
        if (_hook is not null)
        {
            _hook.Received -= OnReceived;
            _hook.Dispose();
            _hook = null;
        }

        return _events.ToList();
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Keys pressed while SmartMacro is in front aren't recorded, except the rest of a combo that started in the
    /// other app (Alt+Tab into SmartMacro keeps its Tab and the Alt release instead of leaving a lone "Press Left Alt").
    /// WM_SYSKEYDOWN / WM_SYSKEYUP (sent while Alt is held) arrive here as KeyDown / KeyUp like the normal messages.
    /// </summary>
    private bool ContinuesRecordedCombo(HookEvent e) =>
        e.Kind == RawInputKind.KeyUp ? _keysRecordedDown.Contains(e.VirtualKey) : _keysRecordedDown.Count > 0;

    private void OnReceived(HookEvent e)
    {
        // Skip our own playback and anything done inside SmartMacro's own windows (like the Stop button).
        if (e.FromSmartMacro) return;
        var isKey = e.Kind is RawInputKind.KeyDown or RawInputKind.KeyUp;
        if (e.OverOwnWindow && !(isKey && ContinuesRecordedCombo(e))) return;
        if (isKey)
        {
            if (e.Kind == RawInputKind.KeyDown) _keysRecordedDown.Add(e.VirtualKey);
            else _keysRecordedDown.Remove(e.VirtualKey);
        }

        if (_events.Count >= MaxEvents)
        {
            LimitReached = true;
            return;
        }

        // Hook times are milliseconds since boot (wrapping); make them relative to the start.
        var time = Math.Max(0, unchecked((int)(e.Time - _startTick)));
        _events.Add(new RawInputEvent(time, e.Kind, e.X, e.Y, e.Button, e.Delta, e.VirtualKey));
    }
}

/// <summary>
/// While a macro plays, watches for a real (physical) Esc key press and raises an emergency stop.
/// Esc keys sent by the macro itself are ignored.
/// </summary>
public sealed class EmergencyStopWatcher : IDisposable
{
    private const int VK_ESCAPE = 0x1B;
    private LowLevelInputHook? _hook;

    public event EventHandler? EmergencyStopRequested;

    public void Start()
    {
        if (_hook is not null) return;
        var hook = new LowLevelInputHook(mouse: false, keyboard: true);
        hook.Received += OnReceived;
        hook.Install();
        _hook = hook;
    }

    public void Stop()
    {
        if (_hook is null) return;
        _hook.Received -= OnReceived;
        _hook.Dispose();
        _hook = null;
    }

    public void Dispose() => Stop();

    private void OnReceived(HookEvent e)
    {
        if (e.Kind == RawInputKind.KeyDown && e.VirtualKey == VK_ESCAPE && !e.Injected)
            EmergencyStopRequested?.Invoke(this, EventArgs.Empty);
    }
}
