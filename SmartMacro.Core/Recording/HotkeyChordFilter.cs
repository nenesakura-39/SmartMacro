using SmartMacro.Core.Models;

namespace SmartMacro.Core.Recording;

/// <summary>
/// Removes SmartMacro's own global shortcuts from a recording (Record Ctrl+Shift+F9, Play / Stop Ctrl+Shift+F10, ...).
/// Windows hands those key presses to SmartMacro (RegisterHotKey), never to the app being recorded, so they must never
/// be played back into it. A shortcut is recognised the way Windows does it: its key goes down while EXACTLY its
/// modifiers are held. That key press (with its auto-repeat and release) is dropped, and so are the presses of the
/// modifiers that formed the chord (from their key-down to their key-up). The same keys used on their own (a plain
/// F9, Shift+A, Ctrl held for a sprint) are kept.
/// </summary>
public static class HotkeyChordFilter
{
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    /// <summary>Which shortcut modifier a key is (generic or left / right version), or None.</summary>
    public static KeyModifiers ModifierOf(int vk) => vk switch
    {
        VK_CONTROL or 0xA2 or 0xA3 => KeyModifiers.Control,
        VK_SHIFT or 0xA0 or 0xA1 => KeyModifiers.Shift,
        VK_MENU or 0xA4 or 0xA5 => KeyModifiers.Alt,
        VK_LWIN or VK_RWIN => KeyModifiers.Windows,
        _ => KeyModifiers.None,
    };

    /// <summary>
    /// Indexes of the events that belong to a shortcut chord. <paramref name="keyOf"/> describes event i: whether it's
    /// a key event, whether it's a press, and its virtual key; non-key events are ignored.
    /// </summary>
    public static HashSet<int> FindChordEvents(int count, Func<int, (bool IsKey, bool Down, int Vk)> keyOf,
        IReadOnlyCollection<HotkeyBinding>? hotkeys)
    {
        var drop = new HashSet<int>();
        if (hotkeys is null || hotkeys.Count == 0 || count == 0) return drop;

        // Every event index of each key's current press (press + auto-repeats), so a whole press can be dropped.
        var pressEvents = new Dictionary<int, List<int>>();
        var dropping = new HashSet<int>(); // keys whose current press is being dropped (auto-repeat + release go too)

        for (var i = 0; i < count; i++)
        {
            var (isKey, down, vk) = keyOf(i);
            if (!isKey) continue;

            if (!down)
            {
                if (dropping.Remove(vk)) drop.Add(i);
                pressEvents.Remove(vk);
                continue;
            }

            if (dropping.Contains(vk))
            {
                drop.Add(i); // auto-repeat of a dropped press
                continue;
            }

            if (pressEvents.TryGetValue(vk, out var events))
            {
                events.Add(i); // OS auto-repeat of a press that is still kept (for now)
                continue;
            }

            pressEvents[vk] = new List<int> { i };
            if (ModifierOf(vk) != KeyModifiers.None) continue;

            // A normal key went down: is it a shortcut with exactly the modifiers that are held right now?
            var held = KeyModifiers.None;
            foreach (var key in pressEvents.Keys) held |= ModifierOf(key);
            if (!hotkeys.Any(h => h is not null && h.VirtualKey == vk && h.Modifiers == held)) continue;

            foreach (var (key, indexes) in pressEvents)
            {
                if (key != vk && ModifierOf(key) == KeyModifiers.None) continue; // another normal key held (W while pressing the chord) stays
                foreach (var index in indexes) drop.Add(index);
                dropping.Add(key);
            }

            foreach (var key in dropping) pressEvents.Remove(key);
        }

        return drop;
    }

    /// <summary>Desktop recordings: the events without SmartMacro's shortcut chords (same list if there are none).</summary>
    public static List<RawInputEvent> Remove(IReadOnlyList<RawInputEvent> events, IReadOnlyCollection<HotkeyBinding>? hotkeys)
    {
        ArgumentNullException.ThrowIfNull(events);
        var drop = FindChordEvents(events.Count,
            i => (events[i].Kind is RawInputKind.KeyDown or RawInputKind.KeyUp, events[i].Kind == RawInputKind.KeyDown, events[i].VirtualKey),
            hotkeys);
        return Without(events, drop);
    }

    /// <summary>Smart recordings (Raw Input reports Ctrl / Shift / Alt as the generic keys; both are handled).</summary>
    public static List<CapturedInputEvent> Remove(IReadOnlyList<CapturedInputEvent> events, IReadOnlyCollection<HotkeyBinding>? hotkeys)
    {
        ArgumentNullException.ThrowIfNull(events);
        var drop = FindChordEvents(events.Count,
            i => (events[i].Input.IsKey, events[i].Input.Kind == InputEventKind.KeyDown, events[i].Input.VirtualKey),
            hotkeys);
        return Without(events, drop);
    }

    /// <summary>Game recordings.</summary>
    public static List<InputEvent> Remove(IReadOnlyList<InputEvent> events, IReadOnlyCollection<HotkeyBinding>? hotkeys)
    {
        ArgumentNullException.ThrowIfNull(events);
        var drop = FindChordEvents(events.Count,
            i => (events[i].IsKey, events[i].Kind == InputEventKind.KeyDown, events[i].VirtualKey),
            hotkeys);
        return Without(events, drop);
    }

    private static List<T> Without<T>(IReadOnlyList<T> events, HashSet<int> drop)
    {
        var result = new List<T>(events.Count - drop.Count);
        for (var i = 0; i < events.Count; i++)
        {
            if (!drop.Contains(i)) result.Add(events[i]);
        }

        return result;
    }
}
