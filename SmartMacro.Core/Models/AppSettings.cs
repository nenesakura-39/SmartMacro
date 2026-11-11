using SmartMacro.Core.Recording;

namespace SmartMacro.Core.Models;

/// <summary>Dark studio surface palettes available in SmartMacro Studio X.</summary>
public enum StudioTheme
{
    /// <summary>Deep obsidian (#0B0F17) with slate cards and cyan-violet neon accents.</summary>
    ObsidianNeon = 0,

    /// <summary>Rich midnight sapphire (#070D1F) with cobalt-indigo glass surfaces.</summary>
    MidnightBlue = 1,
}

/// <summary>Primary interactive accent colour choice in SmartMacro Studio X.</summary>
public enum StudioAccent
{
    NeonBrand = 0,
    ElectricBlue = 1,
    CyberViolet = 2,
    EmeraldPulse = 3,
    RoseQuartz = 4,
}

/// <summary>Global UI spacing / row density.</summary>
public enum StudioDensity
{
    Comfortable = 0,
    Compact = 1,
}

/// <summary>
/// Controls whether step editors emphasize guided scenario cards and 1-click Auto-Tune (Beginner)
/// or also auto-expand the full Vision 2.0 &amp; engine parameter panels (Advanced).
/// </summary>
public enum ExperienceLevel
{
    Beginner = 0,
    Advanced = 1,
}

/// <summary>User preferences, stored in settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>
    /// 1 = original. 2 = adds the Smart recording mode (the new default). Version 1 files are switched to Smart once,
    /// because version 1 only offered Desktop / Game; both are still available as advanced options.
    /// 3 = the pause / resume shortcut defaults to F8 (AAA upgrade); files that still have the old default Ctrl+Shift+F11
    /// are moved to F8 once. A shortcut the user picked themselves is kept.
    /// 4 = adds the recording fidelity selector (<see cref="RecordingFidelity"/>, default 🎯 100% Exact 1:1 Replay). Files
    /// that had "record mouse movement" turned off start on 🖱️ Clicks &amp; Keys Only instead.
    /// </summary>
    public const int CurrentSchemaVersion = 4;
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

    /// <summary>
    /// Streamer mode, "🎥 Show overlays in OBS / Discord": the run HUD, status banner, target-lock reticle and region
    /// highlight boxes are left IN screen captures (Windows display affinity WDA_NONE), so OBS, Discord screen share, Xbox
    /// Game Bar and other recorders show them. Off (default) = they are left out of every capture (WDA_EXCLUDEFROMCAPTURE),
    /// which is also what keeps SmartMacro's own Find steps from ever seeing them. Changes apply at once to open overlays.
    /// </summary>
    public bool ShowOverlaysInObsAndRecording { get; set; }

    /// <summary>Record mouse movement (not just clicks) while recording.</summary>
    public bool RecordMouseMovement { get; set; } = true;

    /// <summary>
    /// Smart (default) = one recording that keeps cursor positions and relative mouse movement and picks the right
    /// one per part. Desktop / Game = the older single-purpose recorders, kept as advanced fallbacks.
    /// </summary>
    public RecordingMode RecordingMode { get; set; } = RecordingMode.Smart;

    /// <summary>
    /// The selector next to the Record button: 🎯 100% Exact 1:1 Replay (default), ✨ Smart Optimized or 🖱️ Clicks &amp; Keys
    /// Only. Decides mouse sampling / simplification and hold times of the editable steps of Smart and Desktop recordings
    /// (it replaces <see cref="RecordMouseMovement"/>, <see cref="SimplifyMousePaths"/> and <see cref="KeepTapHoldTime"/>,
    /// which are kept in the file only for older versions).
    /// </summary>
    public RecordingFidelity RecordingFidelity { get; set; } = RecordingFidelity.Exact1To1;

    /// <summary>
    /// "Use relative mouse deltas for 3D camera": every recorded cursor movement becomes a relative mouse move (for
    /// shift-lock / first-person / cursor-locked games). Off (default): every move plays as an absolute X / Y position
    /// (drags of items, sliders and maps never drift); the deltas are still recorded on every Move step, so one step can
    /// be switched to relative in the editor.
    /// </summary>
    public bool UseRelativeMouseDeltas { get; set; }

    /// <summary>The relative-mouse mode recordings use, from <see cref="UseRelativeMouseDeltas"/>.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public RelativeMouseMode RelativeMouseMode => UseRelativeMouseDeltas ? RelativeMouseMode.Always : RelativeMouseMode.Off;

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

    /// <summary>
    /// Smart Click Snapshots: while recording (Smart and Desktop modes), save a small 56×56 picture of what was under the
    /// cursor at every click, so the editor can show it and Smart-Aim / "Convert to Find image" can use it. The pictures
    /// are stored inside the macro file. On by default (also for settings files made before it existed).
    /// </summary>
    public bool CaptureClickSnapshots { get; set; } = true;

    /// <summary>
    /// Intent Recording (v1.1): while recording clicks (Smart and Desktop modes), inspect the UI control, nearby text and
    /// window anchor off the hook thread so Smart Intent can relocate buttons even when windows move, resize or rescale.
    /// On by default (also for settings files made before it existed).
    /// </summary>
    public bool CaptureClickIntent { get; set; } = true;

    /// <summary>
    /// Smart Timing (v1.1): when converting Smart / Desktop recordings, pauses of 150 ms+ right after a click become
    /// adaptive "Until screen settles" waits (fast when the UI is ready early, safe up to 3× when a page loads slowly).
    /// On by default (also for settings files made before it existed).
    /// </summary>
    public bool SmartTimingOnRecord { get; set; } = true;

    /// <summary>
    /// Smart Typing Coalescer: when converting Smart / Desktop recordings, runs of 2+ printable keystrokes separated by
    /// short pauses (&lt;= 600 ms) are grouped into a single editable "Type text" step (while single keys like WASD and
    /// shortcuts like Ctrl+C remain individual KeyPress steps). On by default.
    /// </summary>
    public bool CoalesceTypedTextOnRecord { get; set; } = true;

    /// <summary>
    /// Playback: a cursor jump right after a Delay glides there smoothly (~120 updates per second) during the end of
    /// that Delay instead of teleporting, so menus, sliders and games see real movement. Total timing is unchanged.
    /// On by default (also for settings files made before it existed).
    /// </summary>
    public bool SmoothMouseMoves { get; set; } = true;

    /// <summary>Global shortcut to start/stop recording.</summary>
    public HotkeyBinding RecordHotkey { get; set; } = HotkeyBinding.DefaultRecord;

    /// <summary>Global shortcut to play / stop playback.</summary>
    public HotkeyBinding PlayHotkey { get; set; } = HotkeyBinding.DefaultPlay;

    /// <summary>Shortcut to pause / resume playback (F8 by default; only active while a macro plays).</summary>
    public HotkeyBinding PauseHotkey { get; set; } = HotkeyBinding.DefaultPause;

    /// <summary>Studio X surface palette (Obsidian Neon default, or Midnight Blue).</summary>
    public StudioTheme Theme { get; set; } = StudioTheme.ObsidianNeon;

    /// <summary>Studio X primary accent colour.</summary>
    public StudioAccent Accent { get; set; } = StudioAccent.NeonBrand;

    /// <summary>Studio X global row/spacing density.</summary>
    public StudioDensity Density { get; set; } = StudioDensity.Comfortable;

    /// <summary>Beginner (guided scenario presets &amp; Auto-Tune) vs Advanced (all Vision 2.0 &amp; engine controls).</summary>
    public ExperienceLevel ExperienceLevel { get; set; } = ExperienceLevel.Beginner;

    /// <summary>Turn off UI transitions and decorative glow animations (also respects Windows Reduce Motion).</summary>
    public bool ReduceMotion { get; set; }

    /// <summary>Keep the left navigation rail pinned expanded (200 px) instead of collapsed to the 56 px icon rail.</summary>
    public bool SidebarPinned { get; set; }

    /// <summary>
    /// v1.5 "Context": when enabled, clicking the window Close (X) button hides SmartMacro to the Windows system tray
    /// instead of exiting, so per-macro hotkeys, schedules, and window triggers keep running in the background.
    /// </summary>
    public bool MinimizeToTrayOnClose { get; set; }

    /// <summary>
    /// Evergreen Upgrade: when enabled, registers SmartMacro in Windows Startup (<c>HKCU\...\Run</c>) with <c>--tray</c>
    /// so it starts quietly in the system tray when the user signs in to Windows.
    /// </summary>
    public bool StartWithWindows { get; set; }

    /// <summary>
    /// v1.5 "Context": when enabled (default), macros assigned to a Profile only arm their triggers when a matching
    /// window is active (or when that Profile is selected in the Library). Global macros (no Profile) stay armed everywhere.
    /// </summary>
    public bool AutoSwitchProfileByWindow { get; set; } = true;

    /// <summary>Optional explicit window-title → profile mapping rules.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<MacroProfileRule>? ProfileRules { get; set; }

    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        if (ProfileRules is { } rules)
            copy.ProfileRules = rules.Select(r => r.Clone()).ToList();
        return copy;
    }

    /// <summary>Clamps values that came from a hand-edited or old file into safe ranges.</summary>
    public void Sanitize()
    {
        if (DefaultDelayMs < 0) DefaultDelayMs = 0;
        if (DefaultDelayMs > MaxDefaultDelayMs) DefaultDelayMs = MaxDefaultDelayMs;
        if (!Enum.IsDefined(RecordingMode)) RecordingMode = RecordingMode.Smart;
        if (!Enum.IsDefined(RecordingFidelity)) RecordingFidelity = RecordingFidelity.Exact1To1;
        if (!Enum.IsDefined(Theme)) Theme = StudioTheme.ObsidianNeon;
        if (!Enum.IsDefined(Accent)) Accent = StudioAccent.NeonBrand;
        if (!Enum.IsDefined(Density)) Density = StudioDensity.Comfortable;
        if (!Enum.IsDefined(ExperienceLevel)) ExperienceLevel = ExperienceLevel.Beginner;

        // Settings saved before the fidelity selector: "don't record mouse movement" becomes Clicks & Keys Only once.
        if (SchemaVersion < 4 && !RecordMouseMovement) RecordingFidelity = RecordingFidelity.ClicksAndKeysOnly;

        // Settings saved before Smart recording existed: start using it (Desktop / Game stay in Settings → Advanced).
        if (SchemaVersion < 2) RecordingMode = RecordingMode.Smart;

        // Settings saved before the F8 pause key: the old default shortcut becomes F8 (unless F8 is taken).
        if (SchemaVersion < 3 && PauseHotkey == HotkeyBinding.LegacyDefaultPause
            && HotkeyBinding.ProblemWithSet(RecordHotkey ?? HotkeyBinding.DefaultRecord, PlayHotkey ?? HotkeyBinding.DefaultPlay, HotkeyBinding.DefaultPause) is null)
            PauseHotkey = HotkeyBinding.DefaultPause;

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
