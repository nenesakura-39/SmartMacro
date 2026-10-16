namespace SmartMacro.Core.Input;

/// <summary>
/// The three KEYBDINPUT fields SendInput needs for one key event: wVk, wScan and dwFlags.
/// Built here (no Windows calls) so the rules can be unit tested; the Windows simulator only copies them.
/// </summary>
public readonly record struct KeyboardInputFields(ushort VirtualKey, ushort ScanCode, uint Flags)
{
    public bool IsKeyUp => (Flags & KeyboardInputEncoder.KeyUpFlag) != 0;
    public bool IsExtended => (Flags & KeyboardInputEncoder.ExtendedKeyFlag) != 0;
    public bool UsesScanCode => (Flags & KeyboardInputEncoder.ScanCodeFlag) != 0;
}

/// <summary>
/// Builds keyboard input following the documented KEYBDINPUT contract:
/// <list type="bullet">
/// <item>Without KEYEVENTF_SCANCODE, Windows identifies the key by <c>wVk</c> (1-254). <c>wScan</c> is only
/// passed along as extra information, so it should match the key.</item>
/// <item>With KEYEVENTF_SCANCODE, <c>wScan</c> identifies the key and <c>wVk</c> is ignored (we send 0).
/// Windows works out the virtual key from the active keyboard layout, like it does for a real keyboard.</item>
/// <item>KEYEVENTF_EXTENDEDKEY means the scan code has the 0xE0 prefix (right Ctrl/Alt, arrows, Insert/Delete,
/// Home/End, Page Up/Down, numpad Enter and /, Windows keys...). It must be set on BOTH the press and the
/// release, or the release is reported as a different key.</item>
/// <item>KEYEVENTF_KEYUP marks the release; without it the event is a press.</item>
/// </list>
/// Desktop steps use virtual-key mode (same as before). Game-mode recordings use scan-code mode with the
/// recorded hardware scan code, so left/right modifiers and arrow-vs-numpad keys stay exactly the key that
/// was pressed, for apps that read scan codes (raw input, DirectInput) as well as ones that read messages.
/// </summary>
public static class KeyboardInputEncoder
{
    public const uint ExtendedKeyFlag = 0x0001; // KEYEVENTF_EXTENDEDKEY
    public const uint KeyUpFlag = 0x0002;       // KEYEVENTF_KEYUP
    public const uint ScanCodeFlag = 0x0008;    // KEYEVENTF_SCANCODE

    // Virtual keys whose scan code has the E0 prefix (they need KEYEVENTF_EXTENDEDKEY in virtual-key mode,
    // or apps can mix them up with the numpad / left-hand versions).
    private static readonly HashSet<int> ExtendedVirtualKeys = new()
    {
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, // Page Up, Page Down, End, Home, arrows
        0x2C, 0x2D, 0x2E,                               // Print Screen, Insert, Delete
        0x5B, 0x5C, 0x5D,                               // Left / right Windows, Apps
        0x6F, 0x90,                                     // Numpad /, Num Lock
        0xA3, 0xA5,                                     // Right Ctrl, Right Alt
        0xAD, 0xAE, 0xAF, 0xB0, 0xB1, 0xB2, 0xB3,       // Volume / media keys
    };

    public static bool IsExtendedVirtualKey(int virtualKey) => ExtendedVirtualKeys.Contains(virtualKey);

    /// <summary>
    /// A recorded scan code can be sent in scan-code mode when it's 1-0xFE (0 = unknown, 0xFF = overrun).
    /// Keys with the E1 prefix (Pause) are recorded with scan code 0, so they use virtual-key mode.
    /// </summary>
    public static bool CanUseScanCode(int scanCode) => scanCode is > 0 and < 0xFF;

    /// <summary>
    /// Virtual-key mode: the key is identified by <paramref name="virtualKey"/>. <paramref name="mappedScanCode"/>
    /// is what Windows reports for that key on the current layout (MapVirtualKey), sent along for apps that look at it.
    /// </summary>
    public static KeyboardInputFields ForVirtualKey(int virtualKey, int mappedScanCode, bool down)
    {
        if (virtualKey is < 1 or > 254) throw new ArgumentOutOfRangeException(nameof(virtualKey), "Virtual-key codes are 1 to 254.");

        var flags = down ? 0u : KeyUpFlag;
        if (IsExtendedVirtualKey(virtualKey)) flags |= ExtendedKeyFlag;
        return new KeyboardInputFields((ushort)virtualKey, (ushort)(mappedScanCode & 0xFF), flags);
    }

    /// <summary>
    /// Scan-code mode: the key is identified by <paramref name="scanCode"/> (+ <paramref name="extended"/> for the
    /// E0 prefix). wVk is 0 because Windows ignores it when KEYEVENTF_SCANCODE is set.
    /// </summary>
    public static KeyboardInputFields ForScanCode(int scanCode, bool extended, bool down)
    {
        if (!CanUseScanCode(scanCode)) throw new ArgumentOutOfRangeException(nameof(scanCode), "Scan codes are 1 to 254.");

        var flags = ScanCodeFlag | (down ? 0u : KeyUpFlag);
        if (extended) flags |= ExtendedKeyFlag;
        return new KeyboardInputFields(0, (ushort)scanCode, flags);
    }
}
