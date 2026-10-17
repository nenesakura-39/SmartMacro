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

    // The last absolute cursor move that hasn't been followed by a button change yet (for the settle before a press / release).
    private bool _movedSinceButton;
    private long _lastMoveTimestamp;
    private int _lastMoveX;
    private int _lastMoveY;

    // Per button: when a "click at a jumped-to spot" was pressed / released (Stopwatch timestamps, 0 = none).
    private readonly Dictionary<MacroMouseButton, long> _jumpClickDown = new();
    private readonly Dictionary<MacroMouseButton, long> _jumpClickUp = new();

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
        _movedSinceButton = false;

        var x = _lastMoveX;
        var y = _lastMoveY;

        // Hover nudge: a tiny RELATIVE wiggle (+2,+2 then back) spread over ~15 ms, so the app gets real movement
        // events at the spot (MouseEnter / hover), even when the cursor arrived long ago or by a jump.
        // Not before a release: the button is held then (end of a drag) and the wiggle would move the slider.
        if (hoverNudge)
        {
            Send(RelativeMove(HoverNudgePx, HoverNudgePx));
            PreciseSleep(HoverNudgeMs / 2);
            Send(RelativeMove(-HoverNudgePx, -HoverNudgePx));
            PreciseSleep(HoverNudgeMs / 2);
        }
        else
        {
            SendAbsoluteMove(x + (x > int.MinValue ? -1 : 1), y);
        }

        // Pointer acceleration can make the way back differ by a pixel: land exactly on the target again.
        SendAbsoluteMove(x, y);
        SetCursorPos(x, y);
        _lastMoveTimestamp = Stopwatch.GetTimestamp() - (long)(HoverNudgeMs * Stopwatch.Frequency / 1000.0);
        PreciseSleepUntil(_lastMoveTimestamp, ButtonSettleMs);
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
        var flags = (button, down) switch
        {
            (MacroMouseButton.Left, true) => MOUSEEVENTF_LEFTDOWN,
            (MacroMouseButton.Left, false) => MOUSEEVENTF_LEFTUP,
            (MacroMouseButton.Right, true) => MOUSEEVENTF_RIGHTDOWN,
            (MacroMouseButton.Right, false) => MOUSEEVENTF_RIGHTUP,
            (MacroMouseButton.Middle, true) => MOUSEEVENTF_MIDDLEDOWN,
            (MacroMouseButton.Middle, false) => MOUSEEVENTF_MIDDLEUP,
            _ => throw new ArgumentOutOfRangeException(nameof(button)),
        };
        var jumped = SettleBeforeButton(hoverNudge: down);
        if (down)
        {
            // Second click of a double-click on a jumped-to spot: leave a real gap between the clicks.
            if (!jumped && _jumpClickUp.TryGetValue(button, out var lastUp) && lastUp != 0
                && (Stopwatch.GetTimestamp() - lastUp) * 1000.0 / Stopwatch.Frequency < MinDoubleClickGapMs)
            {
                PreciseSleepUntil(lastUp, MinDoubleClickGapMs);
                jumped = true; // this press belongs to the same "click on a spot", so it is held long enough too
            }

            DiagnoseClickTarget(button);
            Send(MouseInput(flags, 0));
            _jumpClickDown[button] = jumped ? Stopwatch.GetTimestamp() : 0;
            _jumpClickUp[button] = 0;
            return;
        }

        // Release of a click on a jumped-to spot: keep the button down at least MinClickHoldMs.
        if (_jumpClickDown.TryGetValue(button, out var pressedAt) && pressedAt != 0)
        {
            PreciseSleepUntil(pressedAt, MinClickHoldMs);
            _jumpClickDown[button] = 0;
            Send(MouseInput(flags, 0));
            _jumpClickUp[button] = Stopwatch.GetTimestamp();
            return;
        }

        Send(MouseInput(flags, 0));
        _jumpClickUp[button] = 0;
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

    public void Key(int virtualKey, bool down) => Send(KeyInput(virtualKey, down));

    public void KeyWithScanCode(int virtualKey, int scanCode, bool extended, bool down)
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

        // Give the target window's input queue a moment between characters, even at 0 ms "delay between characters".
        if (_lastTypedTimestamp != 0)
            PreciseSleepUntil(_lastTypedTimestamp, MinCharacterGapMs);

        try
        {
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
                PreciseSleep(MinKeyHoldMs);
                Send(KeyInput(realKey, false));
                return;
            }

            if (text.Length == 1)
            {
                Send(UnicodeInput(text[0], down: true));
                PreciseSleep(MinKeyHoldMs);
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
}

/// <summary>Raises the Windows timer resolution to 1 ms while playing, so short delays are accurate.</summary>
public sealed class HighResolutionTimerScope : IDisposable
{
    private bool _active;

    public HighResolutionTimerScope()
    {
        _active = timeBeginPeriod(1) == 0;
    }

    public void Dispose()
    {
        if (!_active) return;
        timeEndPeriod(1);
        _active = false;
    }
}
