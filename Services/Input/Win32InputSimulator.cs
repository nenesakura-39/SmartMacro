using System.Runtime.InteropServices;
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
    private static readonly int InputSize = Marshal.SizeOf<INPUT>();

    // Keys that need the "extended key" flag, or apps can mix them up with the numpad versions.
    private static readonly HashSet<int> ExtendedKeys = new()
    {
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, // PgUp, PgDn, End, Home, arrows
        0x2C, 0x2D, 0x2E,                               // Print Screen, Insert, Delete
        0x5B, 0x5C, 0x5D,                               // Windows keys, Apps
        0x6F, 0x90,                                     // Numpad /, Num Lock
        0xA3, 0xA5,                                     // Right Ctrl, Right Alt
        0xAD, 0xAE, 0xAF, 0xB0, 0xB1, 0xB2, 0xB3,       // Volume / media keys
    };

    public void MoveMouse(int x, int y)
    {
        if (!SetCursorPos(x, y))
            throw new InputSimulationException(
                $"Windows refused to move the mouse (error {Marshal.GetLastWin32Error()}). This can happen when the screen is locked or a secure window is open.");
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

    private static INPUT KeyInput(int virtualKey, bool down)
    {
        var flags = down ? 0u : KEYEVENTF_KEYUP;
        if (ExtendedKeys.Contains(virtualKey)) flags |= KEYEVENTF_EXTENDEDKEY;

        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)virtualKey,
                    wScan = (ushort)MapVirtualKey((uint)virtualKey, 0), // MAPVK_VK_TO_VSC
                    dwFlags = flags,
                    dwExtraInfo = SmartMacroInputTag,
                },
            },
        };
    }

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
