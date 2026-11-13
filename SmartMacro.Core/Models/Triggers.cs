using System.Globalization;
using System.Text.Json.Serialization;

namespace SmartMacro.Core.Models;

/// <summary>What kind of event starts a macro automatically (v1.4 "Reflex", format 17).</summary>
public enum TriggerKind
{
    /// <summary>A keyboard shortcut specific to this macro (Press once, Toggle, Hold to run, or Repeat while held).</summary>
    Hotkey,

    /// <summary>Runs on a time schedule (every N minutes, daily at HH:mm, or when SmartMacro starts).</summary>
    Schedule,

    /// <summary>Runs when a target application window opens or gains focus.</summary>
    WindowEvent,
}

/// <summary>How a per-macro <see cref="TriggerKind.Hotkey"/> behaves when pressed and released.</summary>
public enum HotkeyTriggerMode
{
    /// <summary>Pressing the shortcut starts the macro (or stops it if this macro is already playing).</summary>
    PressOnce,

    /// <summary>Pressing once starts the macro; pressing again stops it.</summary>
    Toggle,

    /// <summary>Holding the shortcut runs the macro; releasing the key immediately stops playback and releases all held keys.</summary>
    HoldToRun,

    /// <summary>Holding the shortcut repeats the macro continuously until the key is released.</summary>
    RepeatWhileHeld,
}

/// <summary>When a <see cref="TriggerKind.Schedule"/> trigger fires.</summary>
public enum ScheduleRecurrence
{
    /// <summary>Runs every <see cref="MacroTrigger.IntervalMinutes"/> minutes while triggers are armed.</summary>
    EveryNMinutes,

    /// <summary>Runs once per day when the local clock reaches <see cref="MacroTrigger.TimeOfDay"/> ("HH:mm").</summary>
    DailyAtTime,

    /// <summary>Runs once when SmartMacro starts up (if triggers are armed).</summary>
    OnAppStartup,
}

/// <summary>Which window transition fires a <see cref="TriggerKind.WindowEvent"/> trigger.</summary>
public enum WindowTriggerEvent
{
    /// <summary>Fires when a window whose title contains <see cref="MacroTrigger.WindowTitle"/> becomes the active foreground window.</summary>
    WindowFocused,

    /// <summary>Fires when a window whose title contains <see cref="MacroTrigger.WindowTitle"/> first appears.</summary>
    WindowOpened,
}

/// <summary>What the Run Governor does when a trigger fires while another macro is already playing.</summary>
public enum TriggerConflictPolicy
{
    /// <summary>Skip this trigger if any macro is already playing (safest default).</summary>
    IgnoreIfBusy,

    /// <summary>Stop the currently playing macro and start this triggered macro immediately.</summary>
    StopCurrentAndRun,

    /// <summary>Queue this macro to run right after the current macro finishes (at most 1 queued per macro).</summary>
    QueueNext,
}

/// <summary>
/// A declarative trigger attached to a <see cref="Macro"/> (v1.4 "Reflex", schema format 17).
/// Plain data only — never scripts or arbitrary code.
/// </summary>
public sealed class MacroTrigger
{
    public const int MaxTriggersPerMacro = 16;
    public const int MinIntervalMinutes = 1;
    public const int MaxIntervalMinutes = 1_440; // 24 hours
    public const int MaxCooldownMs = 3_600_000;  // 1 hour

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "Shortcut trigger";

    public bool Enabled { get; set; } = true;

    public TriggerKind Kind { get; set; } = TriggerKind.Hotkey;

    // ---- Hotkey trigger settings ----

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HotkeyBinding? Hotkey { get; set; } = new(KeyModifiers.Control | KeyModifiers.Alt, 0x70); // Ctrl+Alt+F1

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public HotkeyTriggerMode HotkeyMode { get; set; } = HotkeyTriggerMode.PressOnce;

    /// <summary>
    /// When true on a Hotkey trigger, observe key presses passively without consuming them via Win32 RegisterHotKey
    /// so the foreground game or app still receives the keystroke.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool PassThroughKey { get; set; }

    // ---- Schedule trigger settings ----

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ScheduleRecurrence Recurrence { get; set; } = ScheduleRecurrence.EveryNMinutes;

    /// <summary>Interval in minutes for <see cref="ScheduleRecurrence.EveryNMinutes"/> (1 to 1440).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int IntervalMinutes { get; set; } = 30;

    /// <summary>24-hour local time ("HH:mm", e.g. "09:00") for <see cref="ScheduleRecurrence.DailyAtTime"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TimeOfDay { get; set; }

    // ---- Window trigger settings & context filter ----

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public WindowTriggerEvent WindowEvent { get; set; } = WindowTriggerEvent.WindowFocused;

    /// <summary>
    /// Window title substring matched by <see cref="TriggerKind.WindowEvent"/>, or optional foreground-app filter
    /// for <see cref="TriggerKind.Hotkey"/> and <see cref="TriggerKind.Schedule"/> when <see cref="OnlyWhenWindowActive"/> is set.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WindowTitle { get; set; }

    /// <summary>
    /// Optional executable/process name substring (without ".exe", e.g. "Code", "chrome", "RobloxPlayerBeta") matched
    /// alongside <see cref="WindowTitle"/> when <see cref="Kind"/> is <see cref="TriggerKind.WindowEvent"/> or when
    /// <see cref="OnlyWhenWindowActive"/> is enabled.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProcessNameMatch { get; set; }

    /// <summary>
    /// When true on a Hotkey or Schedule trigger, the trigger only fires while a window matching <see cref="WindowTitle"/>
    /// and/or <see cref="ProcessNameMatch"/> is the active foreground window (context-aware per-app/per-game trigger).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool OnlyWhenWindowActive { get; set; }

    // ---- Run Governor settings ----

    /// <summary>Minimum wait in milliseconds before this trigger can fire again (0 to 3,600,000 ms).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int CooldownMs { get; set; }

    /// <summary>How to handle this trigger if another macro is already playing.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public TriggerConflictPolicy ConflictPolicy { get; set; } = TriggerConflictPolicy.IgnoreIfBusy;

    public static bool TryParseTimeOfDay(string? text, out int hour, out int minute)
    {
        hour = 0;
        minute = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Split(':');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hour)) return false;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minute)) return false;
        return hour is >= 0 and <= 23 && minute is >= 0 and <= 59;
    }

    public string DescribeWindowTarget()
    {
        var title = WindowTitle?.Trim();
        var proc = ProcessNameMatch?.Trim();
        if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(proc))
            return $"{title} [{proc}]";
        return !string.IsNullOrEmpty(title) ? title : proc ?? string.Empty;
    }

    /// <summary>Short human-readable badge text for library cards and the editor header.</summary>
    public string DescribeShort() => Kind switch
    {
        TriggerKind.Hotkey => DescribeHotkeyShort(),
        TriggerKind.Schedule => Recurrence switch
        {
            ScheduleRecurrence.EveryNMinutes => $"Every {IntervalMinutes}m",
            ScheduleRecurrence.DailyAtTime => $"Daily {TimeOfDay ?? "09:00"}",
            ScheduleRecurrence.OnAppStartup => "On startup",
            _ => "Schedule",
        },
        TriggerKind.WindowEvent => WindowEvent switch
        {
            WindowTriggerEvent.WindowOpened => $"When \"{DescribeWindowTarget()}\" opens",
            _ => $"When \"{DescribeWindowTarget()}\" focused",
        },
        _ => "Trigger",
    };

    private string DescribeHotkeyShort()
    {
        if (Hotkey is null)
            return PassThroughKey ? "Hotkey (Pass-through)" : "Hotkey";

        return (HotkeyMode, PassThroughKey) switch
        {
            (HotkeyTriggerMode.HoldToRun, true) => $"{Hotkey} (Hold, Pass-through)",
            (HotkeyTriggerMode.HoldToRun, false) => $"{Hotkey} (Hold)",
            (HotkeyTriggerMode.RepeatWhileHeld, true) => $"{Hotkey} (Hold ∞, Pass-through)",
            (HotkeyTriggerMode.RepeatWhileHeld, false) => $"{Hotkey} (Hold ∞)",
            (HotkeyTriggerMode.Toggle, true) => $"{Hotkey} (Toggle, Pass-through)",
            (HotkeyTriggerMode.Toggle, false) => $"{Hotkey} (Toggle)",
            (_, true) => $"{Hotkey} (Pass-through)",
            _ => Hotkey.ToString(),
        };
    }

    public MacroTrigger Clone() => new()
    {
        Id = Id,
        Name = Name,
        Enabled = Enabled,
        Kind = Kind,
        Hotkey = Hotkey,
        HotkeyMode = HotkeyMode,
        PassThroughKey = PassThroughKey,
        Recurrence = Recurrence,
        IntervalMinutes = IntervalMinutes,
        TimeOfDay = TimeOfDay,
        WindowEvent = WindowEvent,
        WindowTitle = WindowTitle,
        ProcessNameMatch = ProcessNameMatch,
        OnlyWhenWindowActive = OnlyWhenWindowActive,
        CooldownMs = CooldownMs,
        ConflictPolicy = ConflictPolicy,
    };
}

/// <summary>Effective hotkey registration entry resolved for the active window context.</summary>
public readonly record struct ScopedHotkeyBinding(HotkeyBinding Binding, bool PassThroughKey);
