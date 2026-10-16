using SmartMacro.Core.Recording;

namespace SmartMacro.Core.Models;

/// <summary>User preferences, stored in settings.json.</summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;
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
    /// Desktop = editable steps with screen positions (default). Game = raw relative mouse movement and exact
    /// key timing, for games that use the mouse for camera control.
    /// </summary>
    public RecordingMode RecordingMode { get; set; } = RecordingMode.Desktop;

    /// <summary>
    /// Desktop recordings: keep how long each key / click was held (a short W tap stays as long as it was).
    /// Turn off for the older "instant tap" behavior.
    /// </summary>
    public bool KeepTapHoldTime { get; set; } = true;

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
        if (!Enum.IsDefined(RecordingMode)) RecordingMode = RecordingMode.Desktop;

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
