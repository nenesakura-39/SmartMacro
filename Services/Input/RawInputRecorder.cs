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
/// Limits: input to apps running as administrator may not be visible to a normal app (Windows UIPI), extra
/// mouse buttons (4/5) aren't recorded, and timestamps are "when SmartMacro processed the message", which is
/// normally well under a millisecond after the device reported it but can lag if the PC is very busy.
/// </summary>
public sealed class RawInputRecorder : IRelativeInputRecorder, IDisposable
{
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

        var session = new Session();
        _lastSession = session;
        session.Start(); // throws with a readable message if Windows refuses
        _session = session;
    }

    public IReadOnlyList<InputEvent> Stop()
    {
        var session = _session;
        if (session is null) return _lastSession?.Snapshot() ?? new List<InputEvent>();

        _session = null;
        session.Stop();
        return session.Snapshot();
    }

    public void Dispose() => Stop();

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
        private readonly List<InputEvent> _events = new();
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

        public int EventCount
        {
            get { lock (_lock) return _events.Count; }
        }

        public bool LimitReached => _limitReached;

        public Exception? LastHandlerError { get; private set; }

        public List<InputEvent> Snapshot()
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
                if (keyboard.ExtraInformation == OwnTag || IsOwnAppInFront()) return;
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

            // One raw packet can carry movement and button changes; movement is stored first.
            if (dx != 0 || dy != 0) Add(InputEvent.Move(timeUs, dx, dy));

            var flags = m.usButtonFlags;
            if ((flags & RI_MOUSE_LEFT_BUTTON_DOWN) != 0) Add(InputEvent.ButtonDown(timeUs, MacroMouseButton.Left));
            if ((flags & RI_MOUSE_LEFT_BUTTON_UP) != 0) Add(InputEvent.ButtonUp(timeUs, MacroMouseButton.Left));
            if ((flags & RI_MOUSE_RIGHT_BUTTON_DOWN) != 0) Add(InputEvent.ButtonDown(timeUs, MacroMouseButton.Right));
            if ((flags & RI_MOUSE_RIGHT_BUTTON_UP) != 0) Add(InputEvent.ButtonUp(timeUs, MacroMouseButton.Right));
            if ((flags & RI_MOUSE_MIDDLE_BUTTON_DOWN) != 0) Add(InputEvent.ButtonDown(timeUs, MacroMouseButton.Middle));
            if ((flags & RI_MOUSE_MIDDLE_BUTTON_UP) != 0) Add(InputEvent.ButtonUp(timeUs, MacroMouseButton.Middle));
            if ((flags & RI_MOUSE_WHEEL) != 0) Add(InputEvent.WheelEvent(timeUs, (short)m.usButtonData));
            if ((flags & RI_MOUSE_HWHEEL) != 0) Add(InputEvent.WheelEvent(timeUs, (short)m.usButtonData, horizontal: true));
        }

        private void AddKeyboard(long timeUs, RAWKEYBOARD k)
        {
            // 0xFF marks the "fake" keys Windows inserts for some escaped sequences (e.g. Print Screen),
            // and a make code of 0xFF means the keyboard buffer overflowed.
            if (k.VKey is 0 or >= 0xFF || k.MakeCode == KEYBOARD_OVERRUN_MAKE_CODE) return;

            var up = (k.Flags & RI_KEY_BREAK) != 0;
            var extended = (k.Flags & RI_KEY_E0) != 0;

            // E1-prefixed keys (Pause/Break) can't be sent as a scan code with SendInput, so store scan 0 and
            // let playback send them by virtual-key code instead.
            var scan = (k.Flags & RI_KEY_E1) != 0 ? 0 : k.MakeCode & 0xFF;
            if (scan == 0) extended = false;

            Add(up
                ? InputEvent.KeyUp(timeUs, k.VKey, scan, extended)
                : InputEvent.KeyDown(timeUs, k.VKey, scan, extended));
        }

        private void Add(InputEvent e)
        {
            lock (_lock)
            {
                if (_events.Count >= GameRecordingConverter.MaxRecordedEvents)
                {
                    _limitReached = true;
                    return;
                }

                _events.Add(e);
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
