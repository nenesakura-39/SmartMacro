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
    private const uint MAPVK_VK_TO_VSC = 0;
    private const int DiagnosticIntervalMs = 2000;
    private static readonly int InputSize = Marshal.SizeOf<INPUT>();
    private static readonly uint OwnProcessId = (uint)Environment.ProcessId;

    private readonly ILogService? _log;
    private long _lastDiagnosticTick = long.MinValue / 2;

    /// <param name="log">If given, unusual click conditions (window not active, cursor confined or moved by
    /// another program) are written to the log, at most once every 2 seconds.</param>
    public Win32InputSimulator(ILogService? log = null)
    {
        _log = log;
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

        if (!SetCursorPos(x, y))
            throw new InputSimulationException(
                $"Windows refused to move the mouse (error {Marshal.GetLastWin32Error()}). This can happen when the screen is locked or a secure window is open.");

        if (_log is not null && GetCursorPos(out var actual) && (actual.X != x || actual.Y != y))
            Diagnose($"Mouse moved to ({x}, {y}) but the cursor is at ({actual.X}, {actual.Y}). Another program (for example a game that locks the mouse) moved or confined it.");
    }

    /// <summary>
    /// Relative movement through SendInput (MOUSEEVENTF_MOVE without ABSOLUTE). Games that read raw mouse input
    /// receive these counts unchanged. Note: Windows applies the pointer speed / "Enhance pointer precision"
    /// settings to the visible cursor, exactly like it does for a physical mouse.
    /// </summary>
    public void MoveMouseRelative(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;
        Send(new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = MOUSEEVENTF_MOVE, dwExtraInfo = SmartMacroInputTag },
            },
        });
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
        if (down) DiagnoseClickTarget(button);
        Send(MouseInput(flags, 0));
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

    public void Scroll(int delta, bool horizontal) =>
        Send(MouseInput(horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL, unchecked((uint)delta)));

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

        // Enter and Tab are sent as real keys; many apps ignore them as Unicode characters.
        if (text == "\n")
        {
            Send(KeyInput(VK_RETURN, true), KeyInput(VK_RETURN, false));
            return;
        }

        if (text == "\t")
        {
            Send(KeyInput(VK_TAB, true), KeyInput(VK_TAB, false));
            return;
        }

        var inputs = new INPUT[text.Length * 2];
        for (var i = 0; i < text.Length; i++)
        {
            inputs[i * 2] = UnicodeInput(text[i], down: true);
            inputs[i * 2 + 1] = UnicodeInput(text[i], down: false);
        }

        Send(inputs);
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
