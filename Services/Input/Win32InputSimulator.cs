using System.Runtime.InteropServices;
using SmartMacro.Core.Input;
using SmartMacro.Core.Models;
using SmartMacro.Core.Playback;
using static SmartMacro.Services.Input.NativeMethods;

namespace SmartMacro.Services.Input;

/// <summary>
/// Sends real mouse and keyboard input with the Windows SendInput API.
/// Note: Windows silently blocks input to apps running as administrator when SmartMacro isn't
/// (this is called UIPI), and nothing works while the screen is locked.
/// </summary>
public sealed class Win32InputSimulator : IInputSimulator
{
    private const int VK_RETURN = 0x0D;
    private const int VK_TAB = 0x09;
    private const uint MAPVK_VK_TO_VSC = 0;
    private static readonly int InputSize = Marshal.SizeOf<INPUT>();

    public void MoveMouse(int x, int y)
    {
        if (!SetCursorPos(x, y))
            throw new InputSimulationException(
                $"Windows refused to move the mouse (error {Marshal.GetLastWin32Error()}). This can happen when the screen is locked or a secure window is open.");
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
        Send(MouseInput(flags, 0));
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
