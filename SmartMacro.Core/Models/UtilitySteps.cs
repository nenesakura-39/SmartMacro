using System.Text.Json.Serialization;
using SmartMacro.Core.Ocr;

namespace SmartMacro.Core.Models;

/// <summary>
/// "Wait for screen change" (VISION &amp; AI): remembers what an area looks like when the step starts, then waits until
/// part of it changes (a fish bites, a dialog pops up, a loot window opens, a progress bar moves). The centre of the
/// changed pixels becomes the "last found spot", so a Mouse click step after it can click exactly where something
/// appeared, and the step can also click / move there itself.
/// Coordinates are physical screen pixels (virtual-screen coordinates), the same as mouse steps.
/// </summary>
public sealed class WaitForChangeStep : MacroStep
{
    public const int DefaultCheckIntervalMs = 100;
    public const int DefaultPixelTolerance = 30;
    public const double DefaultMinChangedPercent = 1.0;

    /// <summary>The area to watch.</summary>
    public int RegionX { get; set; }
    public int RegionY { get; set; }
    public int RegionWidth { get; set; } = 300;
    public int RegionHeight { get; set; } = 200;

    /// <summary>
    /// "How much must change": the share of the area's pixels (in %) that must look different from the starting picture.
    /// Small values react to tiny changes (a blinking cursor too); 1 % ignores most noise.
    /// </summary>
    public double MinChangedPercent { get; set; } = DefaultMinChangedPercent;

    /// <summary>"Ignore small colour shifts": a pixel only counts as changed when a red / green / blue channel moved more than this (0–255).</summary>
    public int PixelTolerance { get; set; } = DefaultPixelTolerance;

    /// <summary>How long to wait for a change. 0 = look once (after one check interval).</summary>
    public int TimeoutMs { get; set; } = 10_000;

    /// <summary>Pause between looks.</summary>
    public int CheckIntervalMs { get; set; } = DefaultCheckIntervalMs;

    /// <summary>What to do at the centre of the change once it happens (nothing by default: it only becomes the last found spot).</summary>
    public FoundTextAction Action { get; set; } = FoundTextAction.None;

    /// <summary>For the "hold the left button" action: how long to hold, in ms.</summary>
    public int ActionHoldMs { get; set; } = ScreenSearchStep.DefaultActionHoldMs;

    /// <summary>What happens when nothing changes in time.</summary>
    public TextNotFoundBehavior WhenNotFound { get; set; } = TextNotFoundBehavior.StopMacro;

    /// <summary>Extended v1.2 recovery settings (Retry, Recovery Sub-Macro, Restart from Checkpoint).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StepFailurePolicy? FailurePolicy { get; set; }

    [JsonIgnore]
    public StepFailureAction EffectiveFailureAction
    {
        get => StepFailurePolicy.ResolveEffective(WhenNotFound, FailurePolicy);
        set => (WhenNotFound, FailurePolicy) = StepFailurePolicy.Apply(value, FailurePolicy);
    }

    /// <summary>The watched area as a rectangle.</summary>
    [JsonIgnore]
    public ScreenRect Region => new(RegionX, RegionY, RegionWidth, RegionHeight);

    [JsonIgnore]
    public override StepKind Kind => StepKind.WaitForChange;
}

/// <summary>Optional window placement / tiling action for <see cref="FocusWindowStep"/> (v1.7 "Workbench").</summary>
public enum WindowSnapLayout
{
    /// <summary>Keep the window's current size and position (default).</summary>
    KeepCurrent,

    /// <summary>Snap the window to the left half of the primary monitor.</summary>
    SnapLeftHalf,

    /// <summary>Snap the window to the right half of the primary monitor.</summary>
    SnapRightHalf,

    /// <summary>Maximize the window on its current monitor.</summary>
    Maximize,

    /// <summary>Center the window (80% width × 80% height) on the primary monitor.</summary>
    CenterOnPrimary,
}

/// <summary>
/// "Focus window" (UTILITY &amp; GAME): brings an app window to the front (and restores it when it's minimized) so the
/// keys and clicks after it go to that app, e.g. the game after checking something in a browser. The window is found by
/// (part of) its title or its app (process) name, ignoring case, e.g. "Roblox" or "RobloxPlayerBeta".
/// </summary>
public sealed class FocusWindowStep : MacroStep
{
    /// <summary>(Part of) the window title or the app name, e.g. "Roblox" or "RobloxPlayerBeta.exe".</summary>
    public string WindowTitle { get; set; } = string.Empty;

    /// <summary>Restore the window first when it is minimized to the taskbar.</summary>
    public bool RestoreIfMinimized { get; set; } = true;

    /// <summary>Optional window snap / layout mode applied after focusing (v1.7 "Workbench").</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public WindowSnapLayout SnapLayout { get; set; } = WindowSnapLayout.KeepCurrent;

    /// <summary>How long to wait for the window to exist (an app that is still starting). 0 = look once.</summary>
    public int WaitForWindowMs { get; set; } = 5_000;

    /// <summary>Pause after the window came to the front, so the app is ready for the next input.</summary>
    public int SettleMs { get; set; } = 250;

    /// <summary>What happens when no such window is found in time.</summary>
    public TextNotFoundBehavior WhenNotFound { get; set; } = TextNotFoundBehavior.StopMacro;

    /// <summary>Extended v1.2 recovery settings (Retry, Recovery Sub-Macro, Restart from Checkpoint).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StepFailurePolicy? FailurePolicy { get; set; }

    [JsonIgnore]
    public StepFailureAction EffectiveFailureAction
    {
        get => StepFailurePolicy.ResolveEffective(WhenNotFound, FailurePolicy);
        set => (WhenNotFound, FailurePolicy) = StepFailurePolicy.Apply(value, FailurePolicy);
    }

    [JsonIgnore]
    public override StepKind Kind => StepKind.FocusWindow;
}

/// <summary>What a Screenshot step captures.</summary>
public enum ScreenshotArea
{
    /// <summary>The main monitor (default).</summary>
    PrimaryMonitor,

    /// <summary>Every monitor as one wide picture.</summary>
    AllMonitors,

    /// <summary>A picked rectangle (RegionX / RegionY / RegionWidth / RegionHeight).</summary>
    CustomArea,

    /// <summary>One app window, wherever it is right now (<see cref="ScreenshotStep.WindowTitle"/>).</summary>
    Window,
}

/// <summary>Optional built-in sound cue played when a <see cref="ScreenshotStep"/> runs.</summary>
public enum SoundCueKind
{
    None = 0,
    SuccessChime = 1,
    SoftBell = 2,
    DoubleBeep = 3,
}

/// <summary>
/// "Take screenshot" (UTILITY &amp; GAME): saves a PNG of the screen, an area or an app window into a folder (by default
/// Pictures\SmartMacro Screenshots), named with a prefix and the date and time, e.g. "Catch_20261024_180511_042.png".
/// Handy for logging rare catches / drops while a macro runs unattended. A screenshot that can't be taken or saved is
/// reported in the log and the macro carries on.
/// </summary>
public sealed class ScreenshotStep : MacroStep
{
    public ScreenshotArea Area { get; set; } = ScreenshotArea.PrimaryMonitor;

    public int RegionX { get; set; }
    public int RegionY { get; set; }
    public int RegionWidth { get; set; } = 400;
    public int RegionHeight { get; set; } = 300;

    /// <summary>"App window": (part of) the window title. Matched ignoring case.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WindowTitle { get; set; }

    /// <summary>Folder to save into; empty = Pictures\SmartMacro Screenshots.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FolderPath { get; set; }

    /// <summary>Start of the file name (the date and time are added).</summary>
    public string FileNamePrefix { get; set; } = Vision.ScreenshotFiles.DefaultPrefix;

    /// <summary>v1.8 "Evergreen": optional non-intrusive studio sound cue played when this step runs.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public SoundCueKind SoundCue { get; set; }

    /// <summary>v1.8 "Evergreen": optional notification message (supports <c>{var}</c> placeholders) logged and sent to webhook when set.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NotificationMessage { get; set; }

    /// <summary>v1.8 "Evergreen": optional HTTPS webhook URL (Discord/Slack compatible) that receives a JSON POST with <see cref="NotificationMessage"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WebhookUrl { get; set; }

    /// <summary>The custom area as a rectangle (only used with <see cref="ScreenshotArea.CustomArea"/>).</summary>
    [JsonIgnore]
    public ScreenRect Region => new(RegionX, RegionY, RegionWidth, RegionHeight);

    [JsonIgnore]
    public override StepKind Kind => StepKind.Screenshot;
}
