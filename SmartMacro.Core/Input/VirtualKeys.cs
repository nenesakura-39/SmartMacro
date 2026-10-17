namespace SmartMacro.Core.Input;

public sealed record KeyInfo(int Code, string Name);

/// <summary>
/// Friendly names for common Windows virtual-key codes.
/// Kept in Core (no WPF dependency) so validation and summaries work in tests too.
/// </summary>
public static class VirtualKeys
{
    public static IReadOnlyList<KeyInfo> All { get; } = Build();

    private static readonly Dictionary<int, string> NameByCode = All.ToDictionary(k => k.Code, k => k.Name);

    /// <summary>Windows virtual-key codes are 1..254.</summary>
    public static bool IsValid(int code) => code is >= 1 and <= 254;

    /// <summary>True when the virtual-key code is Shift, Ctrl, Alt, or Windows.</summary>
    public static bool IsModifier(int code) => code is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5);

    public static string GetName(int code) =>
        NameByCode.TryGetValue(code, out var name) ? name : $"VK 0x{code:X2}";

    private static List<KeyInfo> Build()
    {
        var keys = new List<KeyInfo>
        {
            new(0x0D, "Enter"),
            new(0x1B, "Escape"),
            new(0x09, "Tab"),
            new(0x20, "Space"),
            new(0x08, "Backspace"),
            new(0x2E, "Delete"),
            new(0x2D, "Insert"),
            new(0x24, "Home"),
            new(0x23, "End"),
            new(0x21, "Page Up"),
            new(0x22, "Page Down"),
            new(0x25, "Left Arrow"),
            new(0x26, "Up Arrow"),
            new(0x27, "Right Arrow"),
            new(0x28, "Down Arrow"),
            new(0x10, "Shift"),
            new(0x11, "Ctrl"),
            new(0x12, "Alt"),
            new(0xA0, "Left Shift"),
            new(0xA1, "Right Shift"),
            new(0xA2, "Left Ctrl"),
            new(0xA3, "Right Ctrl"),
            new(0xA4, "Left Alt"),
            new(0xA5, "Right Alt"),
            new(0x5B, "Windows (Left)"),
            new(0x5C, "Windows (Right)"),
            new(0x5D, "Menu / Apps"),
            new(0x14, "Caps Lock"),
            new(0x90, "Num Lock"),
            new(0x91, "Scroll Lock"),
            new(0x2C, "Print Screen"),
            new(0x13, "Pause"),
        };

        for (var c = 'A'; c <= 'Z'; c++) keys.Add(new(c, c.ToString()));
        for (var d = 0; d <= 9; d++) keys.Add(new(0x30 + d, d.ToString()));
        for (var f = 1; f <= 24; f++) keys.Add(new(0x70 + f - 1, $"F{f}"));
        for (var n = 0; n <= 9; n++) keys.Add(new(0x60 + n, $"Numpad {n}"));

        keys.AddRange(new KeyInfo[]
        {
            new(0x6A, "Numpad *"),
            new(0x6B, "Numpad +"),
            new(0x6D, "Numpad -"),
            new(0x6E, "Numpad ."),
            new(0x6F, "Numpad /"),
            new(0xBA, "; :"),
            new(0xBB, "= +"),
            new(0xBC, ", <"),
            new(0xBD, "- _"),
            new(0xBE, ". >"),
            new(0xBF, "/ ?"),
            new(0xC0, "` ~"),
            new(0xDB, "[ {"),
            new(0xDC, "\\ |"),
            new(0xDD, "] }"),
            new(0xDE, "' \""),
            new(0xAD, "Volume Mute"),
            new(0xAE, "Volume Down"),
            new(0xAF, "Volume Up"),
            new(0xB0, "Media Next"),
            new(0xB1, "Media Previous"),
            new(0xB2, "Media Stop"),
            new(0xB3, "Media Play/Pause"),
        });

        return keys;
    }
}
