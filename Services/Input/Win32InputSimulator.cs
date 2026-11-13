using System.Diagnostics;
using System.Runtime.InteropServices;
using SmartMacro.Core.Input;
using SmartMacro.Core.Logging;
using SmartMacro.Core.Models;
using SmartMacro.Core.Playback;
using static SmartMacro.Services.Input.NativeMethods;

namespace SmartMacro.Services.Input;

/// <summary>
/// Sends real mouse and keyboard input with the Windows SendInput API.
/// Note: Windows silently blocks input to apps running as administrator when SmartMacro isn't
/// (this is called UIPI), and nothing works while the screen is locked. SendInput's return value only says
/// Windows put the events in its input queue; it can't tell whether the target app actually used them.
/// </summary>
public sealed class Win32InputSimulator : IInputSimulator
{
    private const int VK_RETURN = 0x0D;
    private const int VK_TAB = 0x09;
    private const int VK_SPACE = 0x20;

    /// <summary>
    /// Minimum time a typed key stays down, and the minimum gap between two typed characters. Sending down + up
    /// (and the next character) in the same instant made Notepad and games drop characters after a space, even
    /// with "delay between characters" at 0. These floors apply on top of the step's own delay.
    /// </summary>
    internal const double MinKeyHoldMs = 6;
    internal const double MinCharacterGapMs = 8;

    /// <summary>
    /// A button press / release right after a cursor jump waits until the cursor has been at the target this long.
    /// Games (Roblox UI sliders, DirectInput) read the pointer once per frame; a release sent in the same instant as
    /// the last move was handled at the old position, so a slider dragged back to 50 % stayed at 80 %.
    /// </summary>
    internal const double ButtonSettleMs = 20;

    /// <summary>
    /// Relative "hover nudge" before a click at a spot the cursor jumped to: +2,+2 then back, spread over this long.
    /// Roblox (and other Raw Input UIs) only update hover / MouseEnter from real relative movement; without it the 3rd
    /// click on a re-spawned button was ignored until the user wiggled the mouse.
    /// </summary>
    internal const double HoverNudgeMs = 15;
    internal const int HoverNudgePx = 2;

    /// <summary>
    /// A click at a spot the cursor jumped to keeps the button down at least this long (games read input once per frame).
    /// Only the missing part is waited for, and only for such clicks (feedback loops pressing in place aren't touched).
    /// </summary>
    internal const double MinClickHoldMs = 35;

    /// <summary>Minimum gap between the two clicks of a double-click on such a spot (0 ms was too fast for Roblox).</summary>
    internal const double MinDoubleClickGapMs = 65;
    private const uint MAPVK_VK_TO_VSC = 0;
    private const int DiagnosticIntervalMs = 2000;
    private static readonly int InputSize = Marshal.SizeOf<INPUT>();
    private static readonly uint OwnProcessId = (uint)Environment.ProcessId;

    private readonly ILogService? _log;
    private long _lastDiagnosticTick = long.MinValue / 2;
    private long _lastTypedTimestamp; // Stopwatch timestamp of the last typed key release (0 = none yet)
    private double _speedScale = 1.0;
    private volatile bool _rapidActionMode;

    public void SetPlaybackSpeed(double speed) =>
        Volatile.Write(ref _speedScale, double.IsFinite(speed) && speed > 0 ? Math.Clamp(speed, 0.25, 20.0) : 1.0);

    public void SetRapidActionMode(bool enabled) => _rapidActionMode = enabled;

    private double ScaleInterval(double baseMs, double minFloorMs)
    {
        var speed = Volatile.Read(ref _speedScale);
        return speed <= 1.0 ? baseMs : Math.Max(minFloorMs, baseMs / speed);
    }

    // The last absolute cursor move that hasn't been followed by a button change yet (for the settle before a press / release).
    private bool _movedSinceButton;
    private int _movesSinceButtonCount;
    private double _lastMoveStepDistPx;
    private long _lastMoveTimestamp;
    private long _lastButtonTimestamp;
    private int _lastMoveX;
    private int _lastMoveY;

    // Per button: when a "click at a jumped-to spot" was pressed / released (Stopwatch timestamps, 0 = none).
    private readonly Dictionary<MacroMouseButton, long> _jumpClickDown = new();
    private readonly Dictionary<MacroMouseButton, long> _jumpClickUp = new();

    // Failsafe: every key / button this simulator pressed and hasn't released yet (see ReleaseAllHeldInputs).
    private readonly HeldInputTracker _held = new();

    /// <summary>What SmartMacro's simulated input is holding down right now.</summary>
    public HeldInputTracker Held => _held;

    /// <param name="log">If given, unusual click conditions (window not active, cursor confined or moved by
    /// another program) are written to the log, at most once every 2 seconds.</param>
    public Win32InputSimulator(ILogService? log = null)
    {
        _log = log;
    }

    /// <summary>Where the cursor is now (physical pixels; the app is per-monitor DPI aware). Used by Drag "from the cursor".</summary>
    public bool TryGetCursorPosition(out int x, out int y)
    {
        if (GetCursorPos(out var point))
        {
            x = point.X;
            y = point.Y;
            return true;
        }

        x = 0;
        y = 0;
        return false;
    }

    /// <summary>
    /// Moves the cursor to exact screen pixels. Two parts:
    /// 1. A SendInput absolute move, so the move arrives as a real mouse-input event. SetCursorPos alone only
    ///    teleports the cursor: Windows asks the window under it to update, but apps that track the mouse from
    ///    the input stream (Raw Input, as many games and game UIs do) never hear about it, so their UI still
    ///    thinks the mouse is where it was and the next click lands on the old spot.
    /// 2. SetCursorPos to the same point, to land on the exact pixel (normalized coordinates can be 1 px off).
    /// </summary>
    public void MoveMouse(int x, int y)
    {
        SendAbsoluteMove(x, y);

        if (!SetCursorPos(x, y))
            throw new InputSimulationException(
                $"Windows refused to move the mouse (error {Marshal.GetLastWin32Error()}). This can happen when the screen is locked or a secure window is open.");

        var stepDistPx = Math.Sqrt((double)(x - _lastMoveX) * (x - _lastMoveX) + (double)(y - _lastMoveY) * (y - _lastMoveY));
        if (_movedSinceButton)
        {
            _movesSinceButtonCount++;
            _lastMoveStepDistPx = stepDistPx;
        }
        else if (stepDistPx <= 4.0 && _lastButtonTimestamp != 0
                 && (Stopwatch.GetTimestamp() - _lastButtonTimestamp) * 1000.0 / Stopwatch.Frequency <= 350.0)
        {
            // Stationary repeat click at the same spot (autoclick / spam click): cursor is already hovering here,
            // so don't flag a fresh cursor jump that would add a 35 ms hover-nudge + settle before the next click.
            _lastMoveTimestamp = Stopwatch.GetTimestamp();
            _lastMoveX = x;
            _lastMoveY = y;
            return;
        }
        else
        {
            _movesSinceButtonCount = 1;
            _lastMoveStepDistPx = double.MaxValue;
        }

        _movedSinceButton = true;
        _lastMoveTimestamp = Stopwatch.GetTimestamp();
        _lastMoveX = x;
        _lastMoveY = y;

        if (_log is not null && GetCursorPos(out var actual) && (actual.X != x || actual.Y != y))
            Diagnose($"Mouse moved to ({x}, {y}) but the cursor is at ({actual.X}, {actual.Y}). Another program (for example a game that locks the mouse) moved or confined it.");
    }

    /// <summary>One SendInput absolute move (a real mouse-input event, so WM_MOUSEMOVE / Raw Input see it).</summary>
    private static void SendAbsoluteMove(int x, int y)
    {
        var left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        var height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (width > 1 && height > 1)
        {
            Send(new INPUT
            {
                type = INPUT_MOUSE,
                U = new InputUnion
                {
                    mi = new MOUSEINPUT
                    {
                        dx = AbsoluteMouseMath.Normalize(x, left, width),
                        dy = AbsoluteMouseMath.Normalize(y, top, height),
                        dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                        dwExtraInfo = SmartMacroInputTag,
                    },
                },
            });
        }
    }

    /// <summary>
    /// Before a button press / release that follows a cursor jump: a relative hover nudge (+2,+2 and back over
    /// <see cref="HoverNudgeMs"/>; real movement events, so Roblox / DirectInput / Raw Input UIs register hover at the
    /// final spot), an exact absolute move back onto the target, and a rest of <see cref="ButtonSettleMs"/>.
    /// Returns true when it did this (the click is a "click on a jumped-to spot").
    /// </summary>
    private bool SettleBeforeButton(bool hoverNudge = true)
    {
        if (!_movedSinceButton) return false;
        var smoothPathArrival = _movesSinceButtonCount >= 2 && _lastMoveStepDistPx <= 28;
        _movedSinceButton = false;
        _movesSinceButtonCount = 0;

        var x = _lastMoveX;
        var y = _lastMoveY;

        if (_rapidActionMode)
        {
            SendAbsoluteMove(x, y);
            SetCursorPos(x, y);
            _lastMoveTimestamp = Stopwatch.GetTimestamp();
            PreciseSleepUntil(_lastMoveTimestamp, 1.5);
            return false;
        }

        if (smoothPathArrival)
        {
            // Cursor arrived via a multi-step glide or recorded path: intermediate WM_MOUSEMOVE events already
            // updated hover state, so skip the artificial 15 ms hover wiggle and 1 px release nudge.
            SendAbsoluteMove(x, y);
            SetCursorPos(x, y);
            PreciseSleepUntil(_lastMoveTimestamp, hoverNudge ? 2 : 4);
            return false;
        }

        // Hover nudge: a tiny RELATIVE wiggle (+2,+2 then back) spread over ~15 ms, so the app gets real movement
        // events at the spot (MouseEnter / hover), even when the cursor arrived long ago or by a jump.
        // Not before a release: the button is held then (end of a drag) and the wiggle would move the slider.
        if (hoverNudge)
        {
            var halfHoverMs = ScaleInterval(HoverNudgeMs, 2.0) / 2.0;
            Send(RelativeMove(HoverNudgePx, HoverNudgePx));
            PreciseSleep(halfHoverMs);
            Send(RelativeMove(-HoverNudgePx, -HoverNudgePx));
            PreciseSleep(halfHoverMs);
        }
        else
        {
            SendAbsoluteMove(x + (x > int.MinValue ? -1 : 1), y);
        }

        // Pointer acceleration can make the way back differ by a pixel: land exactly on the target again.
        SendAbsoluteMove(x, y);
        SetCursorPos(x, y);
        _lastMoveTimestamp = Stopwatch.GetTimestamp();
        PreciseSleepUntil(_lastMoveTimestamp, ScaleInterval(ButtonSettleMs, 3.0));
        return true;
    }

    private static INPUT RelativeMove(int dx, int dy) => new()
    {
        type = INPUT_MOUSE,
        U = new InputUnion
        {
            mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = MOUSEEVENTF_MOVE, dwExtraInfo = SmartMacroInputTag },
        },
    };

    /// <summary>
    /// Relative movement through SendInput (MOUSEEVENTF_MOVE without ABSOLUTE). Games that read raw mouse input
    /// receive these counts unchanged. Note: Windows applies the pointer speed / "Enhance pointer precision"
    /// settings to the visible cursor, exactly like it does for a physical mouse.
    /// </summary>
    public void MoveMouseRelative(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;
        _movedSinceButton = false; // relative (camera) movement: never nudge the cursor, that would turn the camera
        Send(RelativeMove(dx, dy));
    }

    public void MouseButton(MacroMouseButton button, bool down)
    {
        // Remember a press before sending it (so a press that half-arrived is still released), forget a release after.
        if (down) _held.NoteButton(button, true);
        MouseButtonCore(button, down);
        if (!down) _held.NoteButton(button, false);
    }

    private void MouseButtonCore(MacroMouseButton button, bool down)
    {
        var (flags, mouseData) = (button, down) switch
        {
            (MacroMouseButton.Left, true) => (MOUSEEVENTF_LEFTDOWN, 0u),
            (MacroMouseButton.Left, false) => (MOUSEEVENTF_LEFTUP, 0u),
            (MacroMouseButton.Right, true) => (MOUSEEVENTF_RIGHTDOWN, 0u),
            (MacroMouseButton.Right, false) => (MOUSEEVENTF_RIGHTUP, 0u),
            (MacroMouseButton.Middle, true) => (MOUSEEVENTF_MIDDLEDOWN, 0u),
            (MacroMouseButton.Middle, false) => (MOUSEEVENTF_MIDDLEUP, 0u),
            (MacroMouseButton.XButton1, true) => (MOUSEEVENTF_XDOWN, XBUTTON1),
            (MacroMouseButton.XButton1, false) => (MOUSEEVENTF_XUP, XBUTTON1),
            (MacroMouseButton.XButton2, true) => (MOUSEEVENTF_XDOWN, XBUTTON2),
            (MacroMouseButton.XButton2, false) => (MOUSEEVENTF_XUP, XBUTTON2),
            _ => throw new ArgumentOutOfRangeException(nameof(button)),
        };
        var jumped = SettleBeforeButton(hoverNudge: down);
        _lastButtonTimestamp = Stopwatch.GetTimestamp();
        if (down)
        {
            // Second click of a double-click on a jumped-to spot: leave a real gap between the clicks.
            var minDoubleGap = _rapidActionMode ? 8.0 : ScaleInterval(MinDoubleClickGapMs, 10.0);
            if (!jumped && _jumpClickUp.TryGetValue(button, out var lastUp) && lastUp != 0
                && (Stopwatch.GetTimestamp() - lastUp) * 1000.0 / Stopwatch.Frequency < minDoubleGap)
            {
                PreciseSleepUntil(lastUp, minDoubleGap);
                jumped = true; // this press belongs to the same "click on a spot", so it is held long enough too
            }

            EnsureTargetWindowActivated(button);
            DiagnoseClickTarget(button);
            Send(MouseInput(flags, mouseData));
            _jumpClickDown[button] = jumped ? Stopwatch.GetTimestamp() : 0;
            _jumpClickUp[button] = 0;
            return;
        }

        // Release of a click on a jumped-to spot: keep the button down at least minClickHold.
        if (_jumpClickDown.TryGetValue(button, out var pressedAt) && pressedAt != 0)
        {
            var minClickHold = _rapidActionMode ? 2.0 : ScaleInterval(MinClickHoldMs, 4.0);
            PreciseSleepUntil(pressedAt, minClickHold);
            _jumpClickDown[button] = 0;
            Send(MouseInput(flags, mouseData));
            _jumpClickUp[button] = Stopwatch.GetTimestamp();
            return;
        }

        Send(MouseInput(flags, mouseData));
        _jumpClickUp[button] = 0;
    }

    /// <summary>
    /// When clicking into an unfocused top-level window (e.g. switching between two side-by-side Roblox windows),
    /// activates that window first so the click itself and any subsequent key presses (like Space to jump) are
    /// received by the newly focused game window instead of being swallowed as a focus-only click.
    /// </summary>
    private static void EnsureTargetWindowActivated(MacroMouseButton button)
    {
        if (button is not (MacroMouseButton.Left or MacroMouseButton.Right)) return;
        try
        {
            if (!GetCursorPos(out var point)) return;
            var root = GetAncestor(WindowFromPoint(point), GA_ROOT);
            if (root == IntPtr.Zero) return;
            var foreground = GetForegroundWindow();
            if (root == foreground) return;

            GetWindowThreadProcessId(root, out var pid);
            if (pid == 0 || pid == OwnProcessId) return;

            if (ScreenInfoProvider.FocusWindowHandle(root, restoreIfMinimized: false))
            {
                PreciseSleep(18);
            }
        }
        catch
        {
            // Activation is best-effort; never block the click itself.
        }
    }

    /// <summary>
    /// Notes conditions that commonly make a click "not count" in the target app. Only for the log; it never
    /// changes what is sent.
    /// </summary>
    private void DiagnoseClickTarget(MacroMouseButton button)
    {
        if (_log is null) return;
        try
        {
            if (!GetCursorPos(out var point)) return;
            var problems = new List<string>();

            var root = GetAncestor(WindowFromPoint(point), GA_ROOT);
            var foreground = GetForegroundWindow();
            if (root != IntPtr.Zero && root != foreground)
                problems.Add($"the window under the cursor ({ProcessName(root)}) isn't the active window ({ProcessName(foreground)}), so this click may only activate it");

            if (GetClipCursor(out var clip))
            {
                var left = GetSystemMetrics(SM_XVIRTUALSCREEN);
                var top = GetSystemMetrics(SM_YVIRTUALSCREEN);
                var right = left + GetSystemMetrics(SM_CXVIRTUALSCREEN);
                var bottom = top + GetSystemMetrics(SM_CYVIRTUALSCREEN);
                if (clip.Left > left || clip.Top > top || clip.Right < right || clip.Bottom < bottom)
                    problems.Add($"the cursor is confined to ({clip.Left}, {clip.Top})-({clip.Right}, {clip.Bottom}), which games do while they lock the mouse");
            }

            if (problems.Count > 0)
                Diagnose($"{button} button press at ({point.X}, {point.Y}): {string.Join("; ", problems)}.");
        }
        catch (Exception ex)
        {
            Diagnose($"Click diagnostics failed: {ex.Message}");
        }
    }

    private void Diagnose(string message)
    {
        if (_log is null) return;
        var now = Environment.TickCount64;
        if (now - _lastDiagnosticTick < DiagnosticIntervalMs) return; // don't flood the log during long macros
        _lastDiagnosticTick = now;
        _log.Info("Input check: " + message);
    }

    private static string ProcessName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "no window";
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == OwnProcessId) return "SmartMacro";
        return ProcessNameById(pid);
    }

    private static string ProcessNameById(uint pid)
    {
        if (pid == 0) return string.Empty;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception)
        {
            return $"process {pid}";
        }
    }

    /// <summary>
    /// One wheel event. Multi-notch Scroll steps are split into one event per notch (120) by the playback engine,
    /// 15 ms apart; recorded wheel events are sent exactly as recorded. A scroll right after a cursor jump settles
    /// first (like a click), so the wheel reaches the list under the new position.
    /// </summary>
    public void Scroll(int delta, bool horizontal)
    {
        SettleBeforeButton();
        Send(MouseInput(horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL, unchecked((uint)delta)));
    }

    public void Key(int virtualKey, bool down)
    {
        if (down) _held.NoteKey(virtualKey, true);
        Send(KeyInput(virtualKey, down));
        if (!down) _held.NoteKey(virtualKey, false);
    }

    public void KeyWithScanCode(int virtualKey, int scanCode, bool extended, bool down)
    {
        if (down) _held.NoteScanKey(virtualKey, scanCode, extended, true);
        KeyWithScanCodeCore(virtualKey, scanCode, extended, down);
        if (!down) _held.NoteScanKey(virtualKey, scanCode, extended, false);
    }

    /// <summary>
    /// Failsafe for Stop / Esc / timeouts / errors: sends a release for every key and mouse button that is still marked
    /// as held (mouse buttons first, then keys in reverse order), so nothing stays stuck down in Windows or a game.
    /// Every release is tried even if one fails; returns the first error, or null.
    /// </summary>
    public string? ReleaseAllHeldInputs()
    {
        if (_held.IsEmpty) return null;
        var buttons = _held.HeldButtons.Count;
        var keys = _held.HeldKeys.Count;
        var error = _held.ReleaseAll(
            button => MouseButtonCore(button, down: false),
            key =>
            {
                if (key.ScanMode) KeyWithScanCodeCore(key.VirtualKey, key.ScanCode, key.Extended, down: false);
                else Send(KeyInput(key.VirtualKey, false));
            });
        _log?.Info($"Input safety: released {buttons} mouse button(s) and {keys} key(s) that were still held when playback ended.");
        return error;
    }

    private void KeyWithScanCodeCore(int virtualKey, int scanCode, bool extended, bool down)
    {
        // Unknown / unusable scan code (0, E1-prefixed keys like Pause): fall back to the virtual key.
        if (!KeyboardInputEncoder.CanUseScanCode(scanCode))
        {
            Send(KeyInput(virtualKey, down));
            return;
        }

        // Scan-code mode (KEYEVENTF_SCANCODE): the recorded hardware key + E0 flag identify the key and
        // wVk is ignored, so right Shift stays right Shift and arrows don't turn into numpad keys.
        // The release uses the same scan code and extended flag as the press.
        Send(ToInput(KeyboardInputEncoder.ForScanCode(scanCode, extended, down)));
    }

    public void TypeCharacters(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        // Give the target window's input queue a moment between characters, scaled by live playback speed.
        if (_lastTypedTimestamp != 0)
            PreciseSleepUntil(_lastTypedTimestamp, ScaleInterval(MinCharacterGapMs, 1.0));

        try
        {
            var keyHoldMs = ScaleInterval(MinKeyHoldMs, 1.0);
            // Enter, Tab and Space are sent as real keys: many apps and games ignore them (or what follows them)
            // when they arrive as Unicode characters.
            var realKey = text switch
            {
                "\n" => VK_RETURN,
                "\t" => VK_TAB,
                " " => VK_SPACE,
                _ => 0,
            };
            if (realKey != 0)
            {
                Send(KeyInput(realKey, true));
                PreciseSleep(keyHoldMs);
                Send(KeyInput(realKey, false));
                return;
            }

            if (text.Length == 1)
            {
                Send(UnicodeInput(text[0], down: true));
                PreciseSleep(keyHoldMs);
                Send(UnicodeInput(text[0], down: false));
                return;
            }

            // A surrogate pair (emoji etc.) has to arrive as one batch so the app can join the halves.
            var inputs = new INPUT[text.Length * 2];
            for (var i = 0; i < text.Length; i++)
            {
                inputs[i * 2] = UnicodeInput(text[i], down: true);
                inputs[i * 2 + 1] = UnicodeInput(text[i], down: false);
            }

            Send(inputs);
        }
        finally
        {
            _lastTypedTimestamp = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>
    /// Blocks the playback thread for a few ms with ~0.1 ms accuracy (sleep most of it, spin the rest).
    /// Only used for the tiny gaps that keep apps from dropping input; normal delays go through the engine's timeline.
    /// </summary>
    internal static void PreciseSleep(double milliseconds) => PreciseSleepUntil(Stopwatch.GetTimestamp(), milliseconds);

    private static void PreciseSleepUntil(long startTimestamp, double milliseconds)
    {
        if (milliseconds <= 0) return;
        var target = startTimestamp + (long)(milliseconds * Stopwatch.Frequency / 1000.0);
        while (true)
        {
            var remainingMs = (target - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency;
            if (remainingMs <= 0) return;
            if (remainingMs > 2) Thread.Sleep(1);
            else Thread.SpinWait(50);
        }
    }

    private static INPUT MouseInput(uint flags, uint data) => new()
    {
        type = INPUT_MOUSE,
        U = new InputUnion
        {
            mi = new MOUSEINPUT { dwFlags = flags, mouseData = data, dwExtraInfo = SmartMacroInputTag },
        },
    };

    /// <summary>Virtual-key mode (desktop steps): wVk identifies the key; wScan is the layout's matching scan code.</summary>
    private static INPUT KeyInput(int virtualKey, bool down) =>
        ToInput(KeyboardInputEncoder.ForVirtualKey(virtualKey, (int)MapVirtualKey((uint)virtualKey, MAPVK_VK_TO_VSC), down));

    private static INPUT ToInput(KeyboardInputFields fields) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = fields.VirtualKey,
                wScan = fields.ScanCode,
                dwFlags = fields.Flags,
                dwExtraInfo = SmartMacroInputTag,
            },
        },
    };

    private static INPUT UnicodeInput(char c, bool down) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = 0,
                wScan = c,
                dwFlags = KEYEVENTF_UNICODE | (down ? 0u : KEYEVENTF_KEYUP),
                dwExtraInfo = SmartMacroInputTag,
            },
        },
    };

    private static void Send(params INPUT[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, InputSize);
        if (sent != inputs.Length)
            throw new InputSimulationException(
                $"Windows blocked the simulated input (error {Marshal.GetLastWin32Error()}). Another program may be blocking input, or the screen is locked.");
    }

    // ---------------- Background / Inactive Window Input (PostMessage) ----------------

    /// <summary>
    /// Finds a visible top-level window matching <paramref name="windowTitle"/>, ranking exact title matches above
    /// substring title matches, and only querying process names if no title matched. Non-minimized windows are preferred.
    /// </summary>
    private static IntPtr FindBackgroundTargetWindow(string? windowTitle)
    {
        if (string.IsNullOrWhiteSpace(windowTitle)) return IntPtr.Zero;
        var wanted = windowTitle.Trim();
        IntPtr exactTitleMatch = IntPtr.Zero;
        IntPtr substringTitleMatch = IntPtr.Zero;
        IntPtr minimizedTitleMatch = IntPtr.Zero;
        var candidatesForProcess = new List<(IntPtr Hwnd, uint Pid, bool Minimized)>(16);

        WindowApi.EnumWindows((hwnd, _) =>
        {
            if (!WindowApi.IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0 || pid == OwnProcessId) return true;

            var minimized = WindowApi.IsIconic(hwnd);
            var len = WindowApi.GetWindowTextLength(hwnd);
            if (len > 0)
            {
                var sb = new System.Text.StringBuilder(len + 1);
                WindowApi.GetWindowText(hwnd, sb, sb.Capacity);
                var title = sb.ToString();
                if (string.Equals(title, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    if (!minimized)
                    {
                        exactTitleMatch = hwnd;
                        return false; // best possible match found immediately
                    }
                    if (minimizedTitleMatch == IntPtr.Zero)
                        minimizedTitleMatch = hwnd;
                }
                else if (title.Contains(wanted, StringComparison.OrdinalIgnoreCase))
                {
                    if (!minimized && substringTitleMatch == IntPtr.Zero)
                        substringTitleMatch = hwnd;
                    else if (minimized && minimizedTitleMatch == IntPtr.Zero)
                        minimizedTitleMatch = hwnd;
                }

                if (candidatesForProcess.Count < 64)
                    candidatesForProcess.Add((hwnd, pid, minimized));
            }

            return true;
        }, IntPtr.Zero);

        if (exactTitleMatch != IntPtr.Zero) return exactTitleMatch;
        if (substringTitleMatch != IntPtr.Zero) return substringTitleMatch;
        if (minimizedTitleMatch != IntPtr.Zero) return minimizedTitleMatch;

        IntPtr substringProcessMatch = IntPtr.Zero;
        var pidCache = new Dictionary<uint, string>();
        foreach (var (hwnd, pid, _) in candidatesForProcess)
        {
            if (!pidCache.TryGetValue(pid, out var procName))
            {
                procName = ProcessNameById(pid);
                pidCache[pid] = procName;
            }

            if (string.IsNullOrEmpty(procName)) continue;
            if (string.Equals(procName, wanted, StringComparison.OrdinalIgnoreCase))
                return hwnd;
            if (substringProcessMatch == IntPtr.Zero && procName.Contains(wanted, StringComparison.OrdinalIgnoreCase))
                substringProcessMatch = hwnd;
        }

        return substringProcessMatch;
    }

    /// <summary>
    /// Resolves the inner child window at the center of <paramref name="rootHwnd"/> so background keyboard and text
    /// messages reach the active canvas or edit control (e.g. Notepad's inner editor) while falling back to <paramref name="rootHwnd"/>
    /// for single-HWND game windows.
    /// </summary>
    private static IntPtr ResolveKeyboardTargetWindow(IntPtr rootHwnd)
    {
        if (rootHwnd == IntPtr.Zero || WindowApi.IsIconic(rootHwnd)) return rootHwnd;
        if (!GetWindowRect(rootHwnd, out var wr)) return rootHwnd;
        var w = wr.Right - wr.Left;
        var h = wr.Bottom - wr.Top;
        if (w <= 0 || h <= 0) return rootHwnd;
        var centerX = wr.Left + w / 2;
        var centerY = wr.Top + h / 2;
        return ResolveChildClientPoint(rootHwnd, centerX, centerY, out _);
    }

    /// <summary>
    /// Resolves the deepest visible child window at screen coordinates (<paramref name="screenX"/>, <paramref name="screenY"/>)
    /// inside <paramref name="rootHwnd"/> and converts the point into that window's client coordinates.
    /// </summary>
    private static IntPtr ResolveChildClientPoint(IntPtr rootHwnd, int screenX, int screenY, out POINT clientPt)
    {
        var current = rootHwnd;
        var pt = new POINT { X = screenX, Y = screenY };
        if (!ScreenToClient(current, ref pt))
        {
            clientPt = new POINT { X = screenX, Y = screenY };
            return rootHwnd;
        }

        const uint skipFlags = CWP_SKIPINVISIBLE | CWP_SKIPDISABLED | CWP_SKIPTRANSPARENT;
        for (var depth = 0; depth < 16; depth++)
        {
            var child = ChildWindowFromPointEx(current, pt, skipFlags);
            if (child == IntPtr.Zero || child == current)
                break;

            var childPt = new POINT { X = screenX, Y = screenY };
            if (!ScreenToClient(child, ref childPt))
                break;

            current = child;
            pt = childPt;
        }

        clientPt = pt;
        return current;
    }

    public bool SendBackgroundMouseClick(string windowTitle, MacroMouseButton button, MouseClickAction action, int screenX, int screenY, int holdMs)
    {
        var root = FindBackgroundTargetWindow(windowTitle);
        if (root == IntPtr.Zero) return false;

        var target = ResolveChildClientPoint(root, screenX, screenY, out var clientPt);
        var lParam = MakeLParam(clientPt.X, clientPt.Y);

        var (downMsg, upMsg, dblMsg, wParamDown) = button switch
        {
            MacroMouseButton.Right => ((uint)WM_RBUTTONDOWN, (uint)WM_RBUTTONUP, (uint)WM_RBUTTONDBLCLK, (IntPtr)MK_RBUTTON),
            MacroMouseButton.Middle => ((uint)WM_MBUTTONDOWN, (uint)WM_MBUTTONUP, (uint)WM_MBUTTONDBLCLK, (IntPtr)MK_MBUTTON),
            _ => ((uint)WM_LBUTTONDOWN, (uint)WM_LBUTTONUP, (uint)WM_LBUTTONDBLCLK, (IntPtr)MK_LBUTTON),
        };

        PostMessage(target, WM_MOUSEMOVE, IntPtr.Zero, lParam);

        var clampHold = Math.Clamp(holdMs, 0, 60_000);
        switch (action)
        {
            case MouseClickAction.Down:
                PostMessage(target, downMsg, wParamDown, lParam);
                break;

            case MouseClickAction.Up:
                PostMessage(target, upMsg, IntPtr.Zero, lParam);
                break;

            case MouseClickAction.DoubleClick:
                PostMessage(target, downMsg, wParamDown, lParam);
                if (clampHold > 0) PreciseSleep(Math.Min(clampHold, 150));
                PostMessage(target, upMsg, IntPtr.Zero, lParam);
                PreciseSleep(35);
                PostMessage(target, dblMsg, wParamDown, lParam);
                if (clampHold > 0) PreciseSleep(Math.Min(clampHold, 150));
                PostMessage(target, upMsg, IntPtr.Zero, lParam);
                break;

            default:
                PostMessage(target, downMsg, wParamDown, lParam);
                if (clampHold > 0) PreciseSleep(clampHold);
                PostMessage(target, upMsg, IntPtr.Zero, lParam);
                break;
        }

        return true;
    }

    public bool SendBackgroundKey(string windowTitle, int virtualKey, KeyAction action, int holdMs, KeyModifiers modifiers)
    {
        var root = FindBackgroundTargetWindow(windowTitle);
        if (root == IntPtr.Zero) return false;
        var target = ResolveKeyboardTargetWindow(root);

        var scan = MapVirtualKey((uint)virtualKey, MAPVK_VK_TO_VSC);
        var downLParam = new IntPtr(1 | ((int)scan << 16));
        var upLParam = new IntPtr(1 | ((int)scan << 16) | (unchecked((int)0xC0000000)));
        var vkPtr = new IntPtr(virtualKey);

        static void PostMod(IntPtr hwnd, KeyModifiers mods, bool down)
        {
            var msg = down ? (uint)WM_KEYDOWN : (uint)WM_KEYUP;
            if (mods.HasFlag(KeyModifiers.Control)) PostMessage(hwnd, msg, new IntPtr(0x11), IntPtr.Zero);
            if (mods.HasFlag(KeyModifiers.Shift)) PostMessage(hwnd, msg, new IntPtr(0x10), IntPtr.Zero);
            if (mods.HasFlag(KeyModifiers.Alt)) PostMessage(hwnd, msg, new IntPtr(0x12), IntPtr.Zero);
        }

        switch (action)
        {
            case KeyAction.Down:
                PostMessage(target, WM_KEYDOWN, vkPtr, downLParam);
                break;

            case KeyAction.Up:
                PostMessage(target, WM_KEYUP, vkPtr, upLParam);
                break;

            default:
                if (modifiers != KeyModifiers.None) PostMod(target, modifiers, down: true);
                PostMessage(target, WM_KEYDOWN, vkPtr, downLParam);
                if (holdMs > 0) PreciseSleep(Math.Clamp(holdMs, 0, 60_000));
                PostMessage(target, WM_KEYUP, vkPtr, upLParam);
                if (modifiers != KeyModifiers.None) PostMod(target, modifiers, down: false);
                break;
        }

        return true;
    }

    public bool SendBackgroundText(string windowTitle, string text, int characterDelayMs)
    {
        var root = FindBackgroundTargetWindow(windowTitle);
        if (root == IntPtr.Zero) return false;
        if (string.IsNullOrEmpty(text)) return true;
        var target = ResolveKeyboardTargetWindow(root);

        var delay = Math.Clamp(characterDelayMs, 0, 10_000);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                continue; // handle \r\n on the \n

            if (ch is '\n' or '\r')
            {
                PostMessage(target, WM_KEYDOWN, new IntPtr(VK_RETURN), IntPtr.Zero);
                PostMessage(target, WM_KEYUP, new IntPtr(VK_RETURN), IntPtr.Zero);
            }
            else
            {
                PostMessage(target, WM_CHAR, new IntPtr(ch), IntPtr.Zero);
            }

            if (delay > 0 && i < text.Length - 1)
                PreciseSleep(delay);
        }

        return true;
    }
}

/// <summary>
/// Raises the Windows timer resolution to 1 ms while recording or playing, so short waits are accurate.
/// timeBeginPeriod / timeEndPeriod are process-wide, so all scopes share ONE reference count: the 1 ms period starts with
/// the first scope and ends with the last one (recording and playback, or nested scopes, can overlap safely). Disposing a
/// scope twice (or from another thread) is a no-op.
/// </summary>
public sealed class HighResolutionTimerScope : IDisposable
{
    private static readonly object Gate = new();
    private static int _activeCount;
    private static bool _periodActive;
    private int _disposed;

    public HighResolutionTimerScope()
    {
        lock (Gate)
        {
            if (++_activeCount == 1) _periodActive = timeBeginPeriod(1) == 0;
        }
    }

    /// <summary>How many scopes are open right now.</summary>
    public static int ActiveCount
    {
        get { lock (Gate) return _activeCount; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return; // already disposed
        lock (Gate)
        {
            if (--_activeCount > 0) return;
            _activeCount = 0;
            if (_periodActive) timeEndPeriod(1);
            _periodActive = false;
        }
    }
}
