using SmartMacro.Core.Recording;

namespace SmartMacro.Core.Models;

/// <summary>User preferences, stored in settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>
    /// 1 = original. 2 = adds the Smart recording mode (the new default). Version 1 files are switched to Smart once,
    /// because version 1 only offered Desktop / Game; both are still available as advanced options.
    /// </summary>
    public const int CurrentSchemaVersion = 2;
    public const int MaxDefaultDelayMs = 60_000;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Ask before deleting a macro from the library.</summary>
    public bool ConfirmBeforeDelete { get; set; } = true;

    /// <summary>Duration used for new Delay steps.</summary>
    public int DefaultDelayMs { get; set; } = 500;

    /// <summary>Show the small activity log under the main content.</summary>
    public bool ShowLogPanel { get; set; } = true;

    /// <summary>Minimize SmartMacro while recording or playing, so it doesn't get in the way.</summary>
    public bool MinimizeDuringRun { get; set; } = true;

    /// <summary>Show the small always-on-top status banner while recording or playing.</summary>
    public bool ShowRunOverlay { get; set; } = true;

    /// <summary>Record mouse movement (not just clicks) while recording.</summary>
    public bool RecordMouseMovement { get; set; } = true;

    /// <summary>
    /// Smart (default) = one recording that keeps cursor positions and relative mouse movement and picks the right
    /// one per part. Desktop / Game = the older single-purpose recorders, kept as advanced fallbacks.
    /// </summary>
    public RecordingMode RecordingMode { get; set; } = RecordingMode.Smart;

    /// <summary>
    /// Desktop recordings: keep how long each key / click was held (a short W tap stays as long as it was).
    /// Turn off for the older "instant tap" behavior.
    /// </summary>
    public bool KeepTapHoldTime { get; set; } = true;

    /// <summary>
    /// "Simplify mouse paths": in editable recorded steps, cursor travel between clicks isn't kept as Move steps (each
    /// click already stores its position), so 5 shop clicks become 5 Click + Delay steps instead of hundreds of Moves.
    /// Drags (moving with a button held) are always kept. On by default (also for settings files made before it existed).
    /// </summary>
    public bool SimplifyMousePaths { get; set; } = true;

    /// <summary>Global shortcut to start/stop recording.</summary>
    public HotkeyBinding RecordHotkey { get; set; } = HotkeyBinding.DefaultRecord;

    /// <summary>Global shortcut to play / stop playback.</summary>
    public HotkeyBinding PlayHotkey { get; set; } = HotkeyBinding.DefaultPlay;

    /// <summary>Global shortcut to pause / resume playback.</summary>
    public HotkeyBinding PauseHotkey { get; set; } = HotkeyBinding.DefaultPause;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    /// <summary>Clamps values that came from a hand-edited or old file into safe ranges.</summary>
    public void Sanitize()
    {
        if (DefaultDelayMs < 0) DefaultDelayMs = 0;
        if (DefaultDelayMs > MaxDefaultDelayMs) DefaultDelayMs = MaxDefaultDelayMs;
        if (!Enum.IsDefined(RecordingMode)) RecordingMode = RecordingMode.Smart;

        // Settings saved before Smart recording existed: start using it (Desktop / Game stay in Settings → Advanced).
        if (SchemaVersion < 2) RecordingMode = RecordingMode.Smart;

        // Broken or clashing shortcuts (e.g. from a hand-edited file) fall back to the defaults.
        if (RecordHotkey is null || PlayHotkey is null || PauseHotkey is null
            || HotkeyBinding.ProblemWithSet(RecordHotkey, PlayHotkey, PauseHotkey) is not null)
        {
            RecordHotkey = HotkeyBinding.DefaultRecord;
            PlayHotkey = HotkeyBinding.DefaultPlay;
            PauseHotkey = HotkeyBinding.DefaultPause;
        }
        SchemaVersion = CurrentSchemaVersion;
    }
}
