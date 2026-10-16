using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SmartMacro.Core.Models;
using SmartMacro.Core.Recording;
using static SmartMacro.Services.Input.NativeMethods;

namespace SmartMacro.Services.Input;

/// <summary>
/// Game-mode recorder. Uses Windows Raw Input (WM_INPUT) to capture what the mouse and keyboard actually
/// report: relative mouse counts (not cursor positions, which games that lock the cursor keep resetting),
/// key down/up with hardware scan codes, buttons and wheel — every sample, in arrival order, with a
/// microsecond timestamp taken when the message is processed.
///
/// Each recording runs in its own <see cref="Session"/>: a dedicated thread with an invisible message-only
/// window and message loop, so the UI thread is never involved and the WM_INPUT handler stays fast (it only
/// copies the data into that session's list). RIDEV_INPUTSINK lets the window receive input while another
/// app (the game) is in front. Sessions never share buffers, window handles or event lists, so a session that
/// is slow to shut down can't corrupt the next recording.
///
/// With captureCursor (smart recording, see <see cref="SmartInputRecorder"/>) each mouse event also gets the cursor
/// position and state from GetCursorPos / GetCursorInfo / GetClipCursor, read in the same handler.
///
/// Limits: input to apps running as administrator may not be visible to a normal app (Windows UIPI), extra
/// mouse buttons (4/5) aren't recorded, and timestamps are "when SmartMacro processed the message", which is
/// normally well under a millisecond after the device reported it but can lag if the PC is very busy.
/// </summary>
public sealed class RawInputRecorder : IRelativeInputRecorder, IDisposable
{
    private readonly bool _captureCursor;

    /// <param name="captureCursor">
    /// Smart recording: also store the cursor position (and whether it's hidden / confined) with every mouse event,
    /// taken in the same WM_INPUT handler on the same clock, so the two representations can never disagree about
    /// order or timing. Off for game mode, which records exactly as before.
    /// </param>
    public RawInputRecorder(bool captureCursor = false)
    {
        _captureCursor = captureCursor;
    }

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    // Raw Input registration is per process (one target window per device type), so only the current
    // session may register or unregister it.
    private static readonly object RegistrationLock = new();
    private static Session? _registeredSession;

    private Session? _session;
    private Session? _lastSession;

    public bool IsRecording => _session is not null;

    public int EventCount => (_session ?? _lastSession)?.EventCount ?? 0;

    public bool LimitReached => (_session ?? _lastSession)?.LimitReached ?? false;

    public int StartX { get; private set; }

    public int StartY { get; private set; }

    /// <summary>Last error inside the WM_INPUT handler of the latest recording (the handler never throws into Windows).</summary>
    public Exception? LastHandlerError => (_session ?? _lastSession)?.LastHandlerError;

    public void Start()
    {
        if (IsRecording) throw new InvalidOperationException("Already recording.");

        if (GetCursorPos(out var cursor))
        {
            StartX = cursor.X;
            StartY = cursor.Y;
        }

        var session = new Session(_captureCursor);
        _lastSession = session;
        session.Start(); // throws with a readable message if Windows refuses
        _session = session;
    }

    public IReadOnlyList<InputEvent> Stop() => StopCaptured().Select(c => c.Input).ToList();

    /// <summary>Stops recording and returns every event with the cursor state captured alongside it.</summary>
    public IReadOnlyList<CapturedInputEvent> StopCaptured()
    {
        var session = _session;
        if (session is null) return _lastSession?.Snapshot() ?? new List<CapturedInputEvent>();

        _session = null;
        session.Stop();
        return session.Snapshot();
    }

    public void Dispose() => StopCaptured();

    private static bool TryRegister(Session session, IntPtr hwnd)
    {
        lock (RegistrationLock)
        {
            var devices = new[]
            {
                new RAWINPUTDEVICE { usUsagePage = HID_USAGE_PAGE_GENERIC, usUsage = HID_USAGE_GENERIC_MOUSE, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd },
                new RAWINPUTDEVICE { usUsagePage = HID_USAGE_PAGE_GENERIC, usUsage = HID_USAGE_GENERIC_KEYBOARD, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd },
            };
            if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
                return false;
            _registeredSession = session;
            return true;
        }
    }

    /// <summary>Removes the registration, but only if it still belongs to this session.</summary>
    private static void Unregister(Session session)
    {
        lock (RegistrationLock)
        {
            if (!ReferenceEquals(_registeredSession, session)) return;
            var devices = new[]
            {
                new RAWINPUTDEVICE { usUsagePage = HID_USAGE_PAGE_GENERIC, usUsage = HID_USAGE_GENERIC_MOUSE, dwFlags = RIDEV_REMOVE, hwndTarget = IntPtr.Zero },
                new RAWINPUTDEVICE { usUsagePage = HID_USAGE_PAGE_GENERIC, usUsage = HID_USAGE_GENERIC_KEYBOARD, dwFlags = RIDEV_REMOVE, hwndTarget = IntPtr.Zero },
            };
            RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
            _registeredSession = null;
        }
    }

    /// <summary>One recording: its own thread, window, buffer and event list.</summary>
    private sealed class Session
    {
        private static readonly uint OwnProcessId = (uint)Environment.ProcessId;
        private static readonly uint HeaderSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        private static readonly uint OwnTag = unchecked((uint)SmartMacroInputTag.ToInt64());

        private readonly object _lock = new();
        private readonly List<CapturedInputEvent> _events = new();
        private readonly bool _captureCursor;

        // Keys whose press was recorded and not released yet (only touched on the session thread).
        private readonly HashSet<int> _keysRecordedDown = new();
        private readonly ManualResetEventSlim _ready = new(false); // not disposed: a late thread may still signal it

        private Thread? _thread;
        private volatile IntPtr _hwnd;
        private volatile bool _stopRequested;
        private volatile bool _limitReached;
        private Exception? _startError;
        private long _startTimestamp;
        private IntPtr _buffer;
        private uint _bufferSize;
        private bool _hasAbsolute;
        private int _lastAbsX;
        private int _lastAbsY;

        // Smart recording: cursor state. Hidden / confined are re-read at most every CursorStateIntervalUs
        // (and on every button change), so the WM_INPUT handler stays fast at 1000+ Hz.
        private const long CursorStateIntervalUs = 8_000;
        private long _cursorStateUs = long.MinValue / 2; // "long ago" without overflowing the subtraction
        private bool _cursorHidden;
        private bool _cursorConfined;
        private RECT _clip;
        private bool _hasLastCursor;
        private int _lastCursorX;
        private int _lastCursorY;

        public Session(bool captureCursor)
        {
            _captureCursor = captureCursor;
        }

        public int EventCount
        {
            get { lock (_lock) return _events.Count; }
        }

        public bool LimitReached => _limitReached;

        public Exception? LastHandlerError { get; private set; }

        public List<CapturedInputEvent> Snapshot()
        {
            lock (_lock) return _events.ToList();
        }

        public void Start()
        {
            _startTimestamp = Stopwatch.GetTimestamp();
            var thread = new Thread(ThreadMain)
            {
                IsBackground = true,
                Name = "SmartMacro raw input",
                Priority = ThreadPriority.AboveNormal,
            };
            _thread = thread;
            thread.Start();

            if (!_ready.Wait(StartTimeout))
            {
                // The thread checks this flag after setting up, so it shuts itself down even if it's just late.
                Stop();
                throw new InvalidOperationException("The raw input recorder didn't start in time.");
            }

            if (_startError is { } error)
            {
                thread.Join(StopTimeout);
                throw new InvalidOperationException($"Windows didn't allow raw input recording: {error.Message}", error);
            }
        }

        public void Stop()
        {
            _stopRequested = true;
            var hwnd = _hwnd;
            if (hwnd != IntPtr.Zero) PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

            // The loop exits within a message or two. If Windows is wedged, don't hang the UI: the thread is a
            // background thread with its own state, so it can't disturb a later recording.
            _thread?.Join(StopTimeout);
        }

        private void ThreadMain()
        {
            var className = "SmartMacroRawInput_" + Guid.NewGuid().ToString("N");
            var hInstance = GetModuleHandle(null);
            var classRegistered = false;

            // Windows calls this delegate through a native pointer, so it must stay alive until the window is
            // gone (GC.KeepAlive at the end of this method).
            var proc = new WndProc(WindowProc);

            try
            {
                var wc = new WNDCLASSEX
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
                    hInstance = hInstance,
                    lpszClassName = className,
                };
                if (RegisterClassEx(ref wc) == 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not register the input window class.");
                classRegistered = true;

                var hwnd = CreateWindowEx(0, className, "SmartMacro raw input", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero);
                if (hwnd == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the input window.");
                _hwnd = hwnd;

                _bufferSize = 256;
                _buffer = Marshal.AllocHGlobal((int)_bufferSize);

                if (!TryRegister(this, hwnd))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not register for raw mouse and keyboard input.");
            }
            catch (Exception ex)
            {
                _startError = ex;
                Cleanup(className, hInstance, classRegistered);
                _ready.Set();
                GC.KeepAlive(proc);
                return;
            }

            _ready.Set();

            // Stop (or a start timeout) may have happened before the window handle was visible to it.
            if (_stopRequested) PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

            try
            {
                while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                    DispatchMessage(ref msg);
            }
            finally
            {
                Cleanup(className, hInstance, classRegistered);
                GC.KeepAlive(proc);
            }
        }

        private void Cleanup(string className, IntPtr hInstance, bool classRegistered)
        {
            Unregister(this);
            var hwnd = _hwnd;
            if (hwnd != IntPtr.Zero)
            {
                DestroyWindow(hwnd);
                _hwnd = IntPtr.Zero;
            }

            if (classRegistered) UnregisterClass(className, hInstance);

            if (_buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_buffer);
                _buffer = IntPtr.Zero;
                _bufferSize = 0;
            }
        }

        private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WM_INPUT:
                    try
                    {
                        if (!_stopRequested) HandleRawInput(lParam);
                    }
                    catch (Exception ex)
                    {
                        LastHandlerError = ex;
                    }

                    // DefWindowProc lets Windows clean up the raw input data.
                    return DefWindowProc(hWnd, msg, wParam, lParam);

                case WM_CLOSE:
                    Unregister(this);
                    DestroyWindow(hWnd);
                    _hwnd = IntPtr.Zero;
                    return IntPtr.Zero;

                case WM_DESTROY:
                    PostQuitMessage(0);
                    return IntPtr.Zero;
            }

            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private void HandleRawInput(IntPtr hRawInput)
        {
            // Timestamp first, before any other work.
            var now = Stopwatch.GetTimestamp();
            var timeUs = (long)((now - _startTimestamp) * (1_000_000.0 / Stopwatch.Frequency));

            uint size = 0;
            GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, HeaderSize);
            if (size == 0) return;
            if (size > _bufferSize)
            {
                Marshal.FreeHGlobal(_buffer);
                _buffer = Marshal.AllocHGlobal((int)size);
                _bufferSize = size;
            }

            if (GetRawInputData(hRawInput, RID_INPUT, _buffer, ref size, HeaderSize) != size) return;

            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(_buffer);
            var data = _buffer + (int)HeaderSize;

            if (header.dwType == RIM_TYPEMOUSE)
            {
                var mouse = Marshal.PtrToStructure<RAWMOUSE>(data);
                // Skip SmartMacro's own SendInput (tagged via dwExtraInfo) and anything done inside SmartMacro.
                if (mouse.ulExtraInformation == OwnTag || IsOwnAppInFront()) return;
                AddMouse(timeUs, mouse);
            }
            else if (header.dwType == RIM_TYPEKEYBOARD)
            {
                var keyboard = Marshal.PtrToStructure<RAWKEYBOARD>(data);
                if (keyboard.ExtraInformation == OwnTag) return;
                // Keys typed inside SmartMacro aren't recorded, EXCEPT the rest of a combo that started in the other
                // app: Alt+Tab (or Ctrl/Shift + key) that switches to SmartMacro must keep its Tab and the Alt release,
                // or the recording ends up with a lone "Press Left Alt".
                if (IsOwnAppInFront() && !ContinuesRecordedCombo(keyboard)) return;
                AddKeyboard(timeUs, keyboard);
            }
        }

        private void AddMouse(long timeUs, RAWMOUSE m)
        {
            int dx, dy;
            if ((m.usFlags & MOUSE_MOVE_ABSOLUTE) != 0)
            {
                // Tablets, remote desktop and some VMs report absolute positions (0..65535). Turn them into deltas.
                var virtualDesktop = (m.usFlags & MOUSE_VIRTUAL_DESKTOP) != 0;
                var left = virtualDesktop ? GetSystemMetrics(SM_XVIRTUALSCREEN) : 0;
                var top = virtualDesktop ? GetSystemMetrics(SM_YVIRTUALSCREEN) : 0;
                var width = virtualDesktop ? GetSystemMetrics(SM_CXVIRTUALSCREEN) : GetSystemMetrics(SM_CXSCREEN);
                var height = virtualDesktop ? GetSystemMetrics(SM_CYVIRTUALSCREEN) : GetSystemMetrics(SM_CYSCREEN);
                var x = left + (int)Math.Round(m.lLastX / 65535.0 * Math.Max(0, width - 1));
                var y = top + (int)Math.Round(m.lLastY / 65535.0 * Math.Max(0, height - 1));
                dx = _hasAbsolute ? x - _lastAbsX : 0;
                dy = _hasAbsolute ? y - _lastAbsY : 0;
                _lastAbsX = x;
                _lastAbsY = y;
                _hasAbsolute = true;
            }
            else
            {
                dx = m.lLastX;
                dy = m.lLastY;
            }

            var flags = m.usButtonFlags;
            if (_captureCursor)
            {
                AddMouseWithCursor(timeUs, dx, dy, flags, m.usButtonData);
                return;
            }

            // One raw packet can carry movement and button changes; movement is stored first.
            if (dx != 0 || dy != 0) Add(InputEvent.Move(timeUs, dx, dy));

            if ((flags & RI_MOUSE_LEFT_BUTTON_DOWN) != 0) Add(InputEvent.ButtonDown(timeUs, MacroMouseButton.Left));
            if ((flags & RI_MOUSE_LEFT_BUTTON_UP) != 0) Add(InputEvent.ButtonUp(timeUs, MacroMouseButton.Left));
            if ((flags & RI_MOUSE_RIGHT_BUTTON_DOWN) != 0) Add(InputEvent.ButtonDown(timeUs, MacroMouseButton.Right));
            if ((flags & RI_MOUSE_RIGHT_BUTTON_UP) != 0) Add(InputEvent.ButtonUp(timeUs, MacroMouseButton.Right));
            if ((flags & RI_MOUSE_MIDDLE_BUTTON_DOWN) != 0) Add(InputEvent.ButtonDown(timeUs, MacroMouseButton.Middle));
            if ((flags & RI_MOUSE_MIDDLE_BUTTON_UP) != 0) Add(InputEvent.ButtonUp(timeUs, MacroMouseButton.Middle));
            if ((flags & RI_MOUSE_WHEEL) != 0) Add(InputEvent.WheelEvent(timeUs, (short)m.usButtonData));
            if ((flags & RI_MOUSE_HWHEEL) != 0) Add(InputEvent.WheelEvent(timeUs, (short)m.usButtonData, horizontal: true));
        }

        /// <summary>
        /// Smart recording: the same events as game mode, each also carrying where the cursor is right now and
        /// whether it's hidden / confined / stuck on an edge. Movement first, then buttons, then wheel (same packet,
        /// same timestamp, original order).
        /// </summary>
        private void AddMouseWithCursor(long timeUs, int dx, int dy, ushort buttonFlags, ushort buttonData)
        {
            if (!GetCursorPos(out var cursor))
            {
                // No cursor information (e.g. a secure desktop): keep the relative data only.
                if (dx != 0 || dy != 0) Add(InputEvent.Move(timeUs, dx, dy));
                return;
            }

            var buttonsChanged = (buttonFlags & ButtonMask) != 0;
            if (buttonsChanged || timeUs - _cursorStateUs >= CursorStateIntervalUs) RefreshCursorState(timeUs);

            var cursorFlags = CursorFlags.None;
            if (_cursorHidden) cursorFlags |= CursorFlags.Hidden;
            if (_cursorConfined) cursorFlags |= CursorFlags.ConfinedToSpot;
            var stuck = _hasLastCursor && cursor.X == _lastCursorX && cursor.Y == _lastCursorY;
            if (stuck && (dx != 0 || dy != 0) && IsAgainstEdge(cursor, dx, dy)) cursorFlags |= CursorFlags.AtEdge;
            _hasLastCursor = true;
            _lastCursorX = cursor.X;
            _lastCursorY = cursor.Y;

            int x = cursor.X, y = cursor.Y;
            if (dx != 0 || dy != 0) Add(InputEvent.Move(timeUs, dx, dy, x, y), cursorFlags);

            // Clicks on SmartMacro's own windows (e.g. its Stop button) aren't part of the recording. The matching
            // release is then dropped by the converter as "released but never pressed".
            if ((buttonFlags & ButtonDownMask) != 0 && IsOwnWindowAt(cursor)) buttonFlags &= unchecked((ushort)~ButtonDownMask);

            if ((buttonFlags & RI_MOUSE_LEFT_BUTTON_DOWN) != 0) Add(InputEvent.ButtonDown(timeUs, MacroMouseButton.Left, x, y), cursorFlags);
            if ((buttonFlags & RI_MOUSE_LEFT_BUTTON_UP) != 0) Add(InputEvent.ButtonUp(timeUs, MacroMouseButton.Left, x, y), cursorFlags);
            if ((buttonFlags & RI_MOUSE_RIGHT_BUTTON_DOWN) != 0) Add(InputEvent.ButtonDown(timeUs, MacroMouseButton.Right, x, y), cursorFlags);
            if ((buttonFlags & RI_MOUSE_RIGHT_BUTTON_UP) != 0) Add(InputEvent.ButtonUp(timeUs, MacroMouseButton.Right, x, y), cursorFlags);
            if ((buttonFlags & RI_MOUSE_MIDDLE_BUTTON_DOWN) != 0) Add(InputEvent.ButtonDown(timeUs, MacroMouseButton.Middle, x, y), cursorFlags);
            if ((buttonFlags & RI_MOUSE_MIDDLE_BUTTON_UP) != 0) Add(InputEvent.ButtonUp(timeUs, MacroMouseButton.Middle, x, y), cursorFlags);
            if ((buttonFlags & RI_MOUSE_WHEEL) != 0) Add(InputEvent.WheelAt(timeUs, (short)buttonData, false, x, y), cursorFlags);
            if ((buttonFlags & RI_MOUSE_HWHEEL) != 0) Add(InputEvent.WheelAt(timeUs, (short)buttonData, true, x, y), cursorFlags);
        }

        private const ushort ButtonDownMask = RI_MOUSE_LEFT_BUTTON_DOWN | RI_MOUSE_RIGHT_BUTTON_DOWN | RI_MOUSE_MIDDLE_BUTTON_DOWN;

        private const ushort ButtonMask = ButtonDownMask | RI_MOUSE_LEFT_BUTTON_UP | RI_MOUSE_RIGHT_BUTTON_UP | RI_MOUSE_MIDDLE_BUTTON_UP;

        /// <summary>Re-reads whether the cursor is hidden and how far it may move (ClipCursor).</summary>
        private void RefreshCursorState(long timeUs)
        {
            _cursorStateUs = timeUs;

            var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            _cursorHidden = GetCursorInfo(ref info) && ((info.flags & CURSOR_SHOWING) == 0 || info.hCursor == IntPtr.Zero);

            if (GetClipCursor(out var clip))
            {
                _clip = clip;
                // A clip area of a few pixels means the game has pinned the cursor in place.
                _cursorConfined = clip.Right - clip.Left <= 8 && clip.Bottom - clip.Top <= 8;
            }
            else
            {
                _clip = default;
                _cursorConfined = false;
            }
        }

        /// <summary>
        /// True when the cursor can't follow the movement because it's on the edge of its allowed area or monitor
        /// in the direction the mouse moved (pushing against the screen edge isn't a "locked" cursor).
        /// </summary>
        private bool IsAgainstEdge(POINT p, int dx, int dy)
        {
            if (_clip.Right > _clip.Left && _clip.Bottom > _clip.Top && OnEdge(p, _clip, dx, dy)) return true;

            var monitor = MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) return false;
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            return GetMonitorInfo(monitor, ref info) && OnEdge(p, info.rcMonitor, dx, dy);
        }

        private static bool OnEdge(POINT p, RECT r, int dx, int dy) =>
            (dx < 0 && p.X <= r.Left) || (dx > 0 && p.X >= r.Right - 1) ||
            (dy < 0 && p.Y <= r.Top) || (dy > 0 && p.Y >= r.Bottom - 1);

        private static bool IsOwnWindowAt(POINT p)
        {
            var hwnd = WindowFromPoint(p);
            if (hwnd == IntPtr.Zero) return false;
            GetWindowThreadProcessId(hwnd, out var pid);
            return pid == OwnProcessId;
        }

        /// <summary>
        /// Key up: WM_KEYUP or WM_SYSKEYUP (Windows sends the SYS versions while Alt is held, e.g. Tab in Alt+Tab, and
        /// for F10 / Alt itself), or the break flag. Both are checked so no release is mistaken for a press.
        /// </summary>
        private static bool IsKeyUp(RAWKEYBOARD k) =>
            (k.Flags & RI_KEY_BREAK) != 0 || k.Message is (uint)WM_KEYUP or (uint)WM_SYSKEYUP;

        /// <summary>A key event that belongs to a combo whose first key was recorded in another app.</summary>
        private bool ContinuesRecordedCombo(RAWKEYBOARD k) =>
            IsKeyUp(k) ? _keysRecordedDown.Contains(k.VKey) : _keysRecordedDown.Count > 0;

        private void AddKeyboard(long timeUs, RAWKEYBOARD k)
        {
            // 0xFF marks the "fake" keys Windows inserts for some escaped sequences (e.g. Print Screen),
            // and a make code of 0xFF means the keyboard buffer overflowed.
            if (k.VKey is 0 or >= 0xFF || k.MakeCode == KEYBOARD_OVERRUN_MAKE_CODE) return;

            // WM_KEYDOWN / WM_SYSKEYDOWN (and the UP versions) are all keyboard input; nothing is filtered by message.
            var up = IsKeyUp(k);
            if (up) _keysRecordedDown.Remove(k.VKey);
            else _keysRecordedDown.Add(k.VKey);
            var extended = (k.Flags & RI_KEY_E0) != 0;

            // E1-prefixed keys (Pause/Break) can't be sent as a scan code with SendInput, so store scan 0 and
            // let playback send them by virtual-key code instead.
            var scan = (k.Flags & RI_KEY_E1) != 0 ? 0 : k.MakeCode & 0xFF;
            if (scan == 0) extended = false;

            Add(up
                ? InputEvent.KeyUp(timeUs, k.VKey, scan, extended)
                : InputEvent.KeyDown(timeUs, k.VKey, scan, extended));
        }

        private void Add(InputEvent e, CursorFlags cursor = CursorFlags.None)
        {
            lock (_lock)
            {
                if (_events.Count >= GameRecordingConverter.MaxRecordedEvents)
                {
                    _limitReached = true;
                    return;
                }

                _events.Add(new CapturedInputEvent(e, cursor));
            }
        }

        /// <summary>Input made while SmartMacro itself is in front (e.g. clicking Stop) isn't part of the recording.</summary>
        private static bool IsOwnAppInFront()
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            GetWindowThreadProcessId(hwnd, out var pid);
            return pid == OwnProcessId;
        }
    }
}

/// <summary>
/// Smart recorder (the default Record button). One Raw Input session — one source, one clock — so a physical
/// click or movement is captured exactly once: there's no second recorder whose events would have to be merged or
/// de-duplicated. Every mouse event carries the relative counts AND the cursor position read in the same handler;
/// see <see cref="SmartRecordingConverter"/> for how the recording is turned into steps.
/// </summary>
public sealed class SmartInputRecorder : ISmartInputRecorder, IDisposable
{
    private readonly RawInputRecorder _recorder = new(captureCursor: true);

    public bool IsRecording => _recorder.IsRecording;

    public int EventCount => _recorder.EventCount;

    public bool LimitReached => _recorder.LimitReached;

    public int StartX => _recorder.StartX;

    public int StartY => _recorder.StartY;

    /// <summary>Last error inside the WM_INPUT handler of the latest recording.</summary>
    public Exception? LastHandlerError => _recorder.LastHandlerError;

    public void Start() => _recorder.Start();

    public IReadOnlyList<CapturedInputEvent> Stop() => _recorder.StopCaptured();

    public void Dispose() => _recorder.Dispose();
}
