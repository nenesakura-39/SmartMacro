using System.Text.Json.Serialization;
using SmartMacro.Core.Input;

namespace SmartMacro.Core.Models;

/// <summary>A global keyboard shortcut, e.g. Ctrl+Shift+F9.</summary>
public sealed record HotkeyBinding(KeyModifiers Modifiers, int VirtualKey)
{
    private const int VK_ESCAPE = 0x1B;

    public static HotkeyBinding DefaultRecord { get; } = new(KeyModifiers.Control | KeyModifiers.Shift, 0x78); // F9
    public static HotkeyBinding DefaultPlay { get; } = new(KeyModifiers.Control | KeyModifiers.Shift, 0x79);   // F10
    public static HotkeyBinding DefaultPause { get; } = new(KeyModifiers.Control | KeyModifiers.Shift, 0x7A);  // F11

    [JsonIgnore]
    public bool IsFunctionKey => VirtualKey is >= 0x70 and <= 0x87; // F1..F24

    /// <summary>Keys pressed for this shortcut (used to trim the stop shortcut from recordings).</summary>
    public IReadOnlyCollection<int> AllKeys()
    {
        var keys = new List<int> { VirtualKey };
        if (Modifiers.HasFlag(KeyModifiers.Control)) keys.AddRange(new[] { 0x11, 0xA2, 0xA3 });
        if (Modifiers.HasFlag(KeyModifiers.Shift)) keys.AddRange(new[] { 0x10, 0xA0, 0xA1 });
        if (Modifiers.HasFlag(KeyModifiers.Alt)) keys.AddRange(new[] { 0x12, 0xA4, 0xA5 });
        if (Modifiers.HasFlag(KeyModifiers.Windows)) keys.AddRange(new[] { 0x5B, 0x5C });
        return keys;
    }

    /// <summary>Plain-English problem with this shortcut, or null if it's fine.</summary>
    public string? Problem()
    {
        if (!VirtualKeys.IsValid(VirtualKey)) return "Choose a key.";
        if (IsModifierKey(VirtualKey)) return "Add a normal key (not only Ctrl/Shift/Alt/Win).";
        if (VirtualKey == VK_ESCAPE) return "Esc is reserved for the emergency stop.";
        if (VirtualKey == 0x7B) return "F12 is reserved by Windows for debuggers.";
        const KeyModifiers all = KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Windows;
        if ((Modifiers & ~all) != 0) return "Unknown modifier key.";
        if (Modifiers == KeyModifiers.None && !IsFunctionKey)
            return "Use Ctrl, Alt, Shift or Win with this key, so normal typing doesn't trigger it.";
        return null;
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(KeyModifiers.Windows)) parts.Add("Win");
        parts.Add(VirtualKeys.GetName(VirtualKey));
        return string.Join("+", parts);
    }

    public static bool IsModifierKey(int vk) =>
        vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5;

    /// <summary>Checks the three app shortcuts together (each valid, no duplicates).</summary>
    public static string? ProblemWithSet(HotkeyBinding record, HotkeyBinding play, HotkeyBinding pause)
    {
        var named = new[] { ("Record", record), ("Play / stop", play), ("Pause / resume", pause) };
        foreach (var (name, binding) in named)
            if (binding.Problem() is { } problem)
                return $"{name}: {problem}";

        if (record == play || record == pause || play == pause)
            return "Each shortcut must be different.";
        return null;
    }
}
