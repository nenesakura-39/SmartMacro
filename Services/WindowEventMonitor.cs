using SmartMacro.Services.Input;

namespace SmartMacro.Services;

/// <summary>
/// Observes OS foreground focus, window creation/destruction, and active-window title changes asynchronously
/// without polling <c>EnumWindows</c> on the WPF UI thread.
/// </summary>
public interface IWindowEventMonitor : IDisposable
{
    bool IsRunning { get; }
    event EventHandler? WindowStateChanged;
    void Start();
    void RequestRefresh();
}

/// <summary>
/// Dedicated background STA message-pump thread hosting Win32 <c>SetWinEventHook</c> listeners
/// (<c>EVENT_SYSTEM_FOREGROUND</c>, <c>EVENT_OBJECT_CREATE</c> / <c>SHOW</c> / <c>DESTROY</c> / <c>NAMECHANGE</c>)
/// so SmartMacro reacts within ~15–60 ms to window switches and spawns zero UI-thread polling overhead.
/// </summary>
public sealed class WindowEventMonitor : IWindowEventMonitor
{
    private const int ForegroundDebounceMs = 15;
    private const int ObjectDebounceMs = 60;

    private readonly object _gate = new();
    private readonly System.Threading.Timer _debounceTimer;
    private Thread? _pumpThread;
    private uint _pumpThreadId;
    private bool _started;
    private bool _disposed;
    private volatile bool _isRunning;

    public WindowEventMonitor()
    {
        _debounceTimer = new System.Threading.Timer(
            static state => ((WindowEventMonitor)state!).RaiseWindowStateChanged(),
            this,
            Timeout.Infinite,
            Timeout.Infinite);
    }

    public bool IsRunning => _isRunning && !_disposed;

    public event EventHandler? WindowStateChanged;

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _started)
                return;

            _started = true;
            using var ready = new ManualResetEventSlim(false);
            _pumpThread = new Thread(() => RunMessagePump(ready))
            {
                IsBackground = true,
                Name = "SmartMacro WinEvent Monitor",
            };
            _pumpThread.SetApartmentState(ApartmentState.STA);
            _pumpThread.Start();
            ready.Wait(millisecondsTimeout: 1000);
        }
    }

    public void RequestRefresh()
    {
        if (_disposed)
            return;
        ScheduleNotification(ForegroundDebounceMs);
    }

    public void Dispose()
    {
        Thread? thread;
        uint threadId;

        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _isRunning = false;
            thread = _pumpThread;
            threadId = _pumpThreadId;
            _pumpThread = null;
            _pumpThreadId = 0;
        }

        _debounceTimer.Dispose();

        if (threadId != 0)
            NativeMethods.PostThreadMessage(threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);

        if (thread is not null && thread.IsAlive && Thread.CurrentThread != thread)
            thread.Join(500);
    }

    private void RunMessagePump(ManualResetEventSlim ready)
    {
        var hookForeground = IntPtr.Zero;
        var hookObjects = IntPtr.Zero;
        NativeMethods.WinEventDelegate? callback = null;

        try
        {
            var threadId = NativeMethods.GetCurrentThreadId();
            lock (_gate)
            {
                if (_disposed)
                {
                    ready.Set();
                    return;
                }
                _pumpThreadId = threadId;
            }
            // Ensure this thread's Win32 message queue exists before SetWinEventHook / PostThreadMessage.
            NativeMethods.PeekMessage(out _, IntPtr.Zero, 0, 0, NativeMethods.PM_NOREMOVE);

            callback = OnWinEvent;
            const uint flags = NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS;

            hookForeground = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_SYSTEM_FOREGROUND,
                NativeMethods.EVENT_SYSTEM_MINIMIZEEND,
                IntPtr.Zero,
                callback,
                0,
                0,
                flags);

            hookObjects = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_OBJECT_CREATE,
                NativeMethods.EVENT_OBJECT_NAMECHANGE,
                IntPtr.Zero,
                callback,
                0,
                0,
                flags);

            _isRunning = hookForeground != IntPtr.Zero && hookObjects != IntPtr.Zero;
            ready.Set();

            if (!_isRunning)
                return;

            while (!_disposed && NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                NativeMethods.DispatchMessage(ref msg);
        }
        finally
        {
            _isRunning = false;
            if (hookForeground != IntPtr.Zero)
                NativeMethods.UnhookWinEvent(hookForeground);
            if (hookObjects != IntPtr.Zero)
                NativeMethods.UnhookWinEvent(hookObjects);
            GC.KeepAlive(callback);
            ready.Set();
        }
    }

    private void OnWinEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (_disposed)
            return;

        if (idObject != NativeMethods.OBJID_WINDOW || idChild != NativeMethods.CHILDID_SELF || hwnd == IntPtr.Zero)
            return;

        if (eventType == NativeMethods.EVENT_OBJECT_NAMECHANGE)
        {
            // Ignore title noise from background tabs/windows; only the active window title affects profiles/hotkeys.
            if (hwnd != NativeMethods.GetForegroundWindow())
                return;
        }
        else if (eventType is not (
            NativeMethods.EVENT_SYSTEM_FOREGROUND
            or NativeMethods.EVENT_SYSTEM_MINIMIZEEND
            or NativeMethods.EVENT_OBJECT_CREATE
            or NativeMethods.EVENT_OBJECT_DESTROY
            or NativeMethods.EVENT_OBJECT_SHOW
            or NativeMethods.EVENT_OBJECT_HIDE))
        {
            return;
        }

        var delayMs = eventType is NativeMethods.EVENT_SYSTEM_FOREGROUND or NativeMethods.EVENT_SYSTEM_MINIMIZEEND
            ? ForegroundDebounceMs
            : ObjectDebounceMs;
        ScheduleNotification(delayMs);
    }

    private void ScheduleNotification(int delayMs)
    {
        if (_disposed)
            return;

        try
        {
            _debounceTimer.Change(delayMs, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void RaiseWindowStateChanged()
    {
        if (_disposed)
            return;

        WindowStateChanged?.Invoke(this, EventArgs.Empty);
    }
}
