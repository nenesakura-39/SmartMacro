using System.Text.Json.Serialization;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Vision;

namespace SmartMacro.Core.Models;

// ---------------------------------------------------------------------------------------------------------------------
// Stage 4: continuous visual feedback.
//
// A Feedback loop step watches a bar-shaped area of the screen many times per second, finds a coloured marker
// (and a target zone) in it, and holds / releases / taps ONE key or mouse button depending on where the marker is
// compared with the zone. Everything here is plain data the user configures in the editor: no scripts, no
// expressions, nothing game-specific. The decisions are made by Core/Playback/FeedbackController (pure, unit tested).
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>Where the target zone comes from.</summary>
public enum FeedbackZoneMode
{
    /// <summary>A fixed part of the bar, e.g. 40 % to 60 % (set by the user).</summary>
    Fixed,

    /// <summary>Found in every frame by its colour (for zones that move or change size).</summary>
    DetectedByColor,
}

/// <summary>Where the marker is compared with the zone, along the bar.</summary>
public enum FeedbackRegion
{
    /// <summary>Before the zone: left of it (horizontal bar) or above it (vertical bar).</summary>
    Before,

    /// <summary>Inside the zone.</summary>
    Inside,

    /// <summary>After the zone: right of it (horizontal bar) or below it (vertical bar).</summary>
    After,
}

/// <summary>What to do with the key / button while the marker is in one region.</summary>
public enum FeedbackAction
{
    /// <summary>Hold it down (pressed until another rule releases it).</summary>
    Hold,

    /// <summary>Let it go (released).</summary>
    Release,

    /// <summary>Don't change anything: keep it held or released, whatever it was.</summary>
    Keep,

    /// <summary>One short press when the marker moves into this region (not again until it leaves and comes back).</summary>
    Tap,

    /// <summary>
    /// Inside the zone only: proportional pulsing. The key / button is held for part of every
    /// <see cref="FeedbackLoopStep.PulsePeriodMs"/>: almost always near the zone's far edge (after / below side),
    /// almost never near its near edge, half the time in the middle. Keeps a "follow the target" bar steady instead
    /// of bouncing between full hold and full release.
    /// </summary>
    Pulse,
}

/// <summary>A ready-made set of feedback loop settings, so beginners only pick the area and the colours.</summary>
public enum FeedbackPreset
{
    /// <summary>Every setting is shown and set by hand (also all loops made before presets existed).</summary>
    Custom,

    /// <summary>
    /// Follow the target (fishing / reeling minigames): the zone is the bar you control (found by its colour), the marker
    /// is the target it must stay on. Hold when the target is ahead of the bar (right / above), release when it's behind,
    /// pulse proportionally while it's inside the bar.
    /// </summary>
    FollowTargetBar,

    /// <summary>
    /// Click when the marker enters the zone (timing bars, skill checks, casting meters): wait for the moving marker to
    /// reach the zone, then click (or release the held button) once and end the loop.
    /// </summary>
    ClickWhenInZone,
}

/// <summary>Which kind of input the loop controls.</summary>
public enum FeedbackInputKind
{
    MouseButton,
    Key,
}

/// <summary>What happens when the marker (or a detected zone) has not been seen clearly for a while.</summary>
public enum FeedbackLostAction
{
    /// <summary>Release the key / button and keep looking (until it comes back, or the max runtime).</summary>
    ReleaseAndWait,

    /// <summary>Release and leave the loop: the macro continues with the next step (e.g. "the minigame closed").</summary>
    ExitLoop,

    /// <summary>Release and stop the macro with an error.</summary>
    StopMacro,
}

/// <summary>
/// "Keep the marker in the zone": a real-time loop that looks at a bar-shaped area of the screen, finds a coloured
/// marker (indicator, slider knob, fish icon…) and a target zone, and holds / releases / taps one key or mouse button
/// depending on whether the marker is before, inside or after the zone.
///
/// Safety: the loop always has a maximum runtime, is rate limited (captures per second and minimum time between input
/// changes), can stop on completion / failure conditions (the same conditions as If / Repeat), has a policy for when
/// the target can't be seen, and everything it holds is released whenever it ends, pauses, fails or is stopped.
/// It only reads the screen and sends normal input: no memory reading, no injection.
/// </summary>
public sealed class FeedbackLoopStep : MacroStep
{
    /// <summary>Which preset the editor shows (Custom = all settings). Only the editor uses it; playback reads the settings below.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public FeedbackPreset Preset { get; set; } = FeedbackPreset.Custom;

    // ---------------- Observe ----------------

    /// <summary>The bar area to watch (physical screen pixels). Keep it tight around the bar.</summary>
    public int RegionX { get; set; }
    public int RegionY { get; set; }
    public int RegionWidth { get; set; } = 300;
    public int RegionHeight { get; set; } = 30;

    /// <summary>Which way the marker moves. Horizontal: 0 % = left edge, 100 % = right edge. Vertical: 0 % = top.</summary>
    public BarOrientation Orientation { get; set; } = BarOrientation.Horizontal;

    /// <summary>Colour of the moving marker, "#RRGGBB".</summary>
    public string MarkerColor { get; set; } = "#FF0000";

    /// <summary>True when a 2nd target/marker colour should also be matched.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool UseSecondaryMarkerColor { get; set; }

    /// <summary>Optional 2nd colour of the moving marker, "#RRGGBB".</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SecondaryMarkerColor { get; set; }

    /// <summary>Allowed difference per red / green / blue channel for the marker colour (0-255).</summary>
    public int MarkerTolerance { get; set; } = 24;

    /// <summary>The marker patch must have at least this many pixels (ignores specks).</summary>
    public int MarkerMinPixels { get; set; } = 3;

    /// <summary>A one-frame jump bigger than this (% of the bar) is ignored once, in case it was a glitch.</summary>
    public int MaxJumpPercent { get; set; } = 35;

    // ---------------- Target zone ----------------

    public FeedbackZoneMode ZoneMode { get; set; } = FeedbackZoneMode.Fixed;

    /// <summary>Fixed zone: where it starts and ends along the bar (0-100 %).</summary>
    public int ZoneStartPercent { get; set; } = 40;
    public int ZoneEndPercent { get; set; } = 60;

    /// <summary>Detected zone: its colour, tolerance and minimum size.</summary>
    public string ZoneColor { get; set; } = "#FFFFFF";

    /// <summary>True when the control bar / zone has a 2nd colour (e.g. when overlapping / active).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool UseSecondaryZoneColor { get; set; }

    /// <summary>Optional 2nd colour of the control bar / target zone ("#RRGGBB", e.g. "#FFFFFF" when active/overlapping).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SecondaryZoneColor { get; set; }

    public int ZoneTolerance { get; set; } = 24;
    public int ZoneMinPixels { get; set; } = 20;

    // ---------------- Decide ----------------

    /// <summary>Rule while the marker is before the zone (left / above).</summary>
    public FeedbackAction WhenBeforeZone { get; set; } = FeedbackAction.Hold;

    /// <summary>Rule while the marker is inside the zone.</summary>
    public FeedbackAction WhenInsideZone { get; set; } = FeedbackAction.Keep;

    /// <summary>Rule while the marker is after the zone (right / below).</summary>
    public FeedbackAction WhenAfterZone { get; set; } = FeedbackAction.Release;

    /// <summary>
    /// Dead band (hysteresis), in % of the bar: the marker must be this far past a zone edge before the loop treats it
    /// as having crossed. Stops rapid on/off flicker when the marker sits right on an edge. 0 = off.
    /// </summary>
    public int DeadBandPercent { get; set; } = 2;

    /// <summary>Length of one pulse cycle for the <see cref="FeedbackAction.Pulse"/> rule, in ms.</summary>
    public int PulsePeriodMs { get; set; } = DefaultPulsePeriodMs;

    public const int DefaultPulsePeriodMs = 200;

    /// <summary>
    /// End the loop (successfully) as soon as the marker is inside the zone, right after doing the "inside" rule once
    /// (Tap = click, Release = let go of the held input). For timing bars and casting meters.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool FinishWhenInZone { get; set; }

    // ---------------- Act ----------------

    public FeedbackInputKind InputKind { get; set; } = FeedbackInputKind.MouseButton;

    /// <summary>The mouse button to control (when <see cref="InputKind"/> is MouseButton). Pressed where the cursor is.</summary>
    public MacroMouseButton MouseButton { get; set; } = MacroMouseButton.Left;

    /// <summary>The key to control (Windows virtual-key code, when <see cref="InputKind"/> is Key). Default: Space.</summary>
    public int VirtualKey { get; set; } = 0x20;

    /// <summary>How long a Tap keeps the key / button down, in ms.</summary>
    public int TapMs { get; set; } = 40;

    // ---------------- Limits ----------------

    /// <summary>At most this many screen captures per second (the loop's frame rate limit).</summary>
    public int MaxCapturesPerSecond { get; set; } = 30;

    /// <summary>At least this long between two input changes (press / release / tap), in ms.</summary>
    public int MinInputChangeMs { get; set; } = 50;

    /// <summary>Required: the loop ends after running this long (ms, paused time not counted).</summary>
    public int MaxRuntimeMs { get; set; } = 30_000;

    /// <summary>What happens when the max runtime is reached.</summary>
    public LoopLimitBehavior WhenTimeUp { get; set; } = LoopLimitBehavior.ExitLoop;

    /// <summary>The marker (or detected zone) must be missing / unclear this long (ms) before <see cref="WhenTargetLost"/> applies.</summary>
    public int LostTimeoutMs { get; set; } = 500;

    public FeedbackLostAction WhenTargetLost { get; set; } = FeedbackLostAction.ReleaseAndWait;

    // ---------------- Stop when ----------------

    /// <summary>Optional: the loop ends successfully as soon as this condition is true (checked every "check every").</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Condition? CompleteWhen { get; set; }

    /// <summary>Optional: the loop ends as failed as soon as this condition is true.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Condition? FailWhen { get; set; }

    /// <summary>What a failure (the fail condition became true) does.</summary>
    public LoopLimitBehavior WhenFailed { get; set; } = LoopLimitBehavior.StopMacro;

    /// <summary>The bar area as a rectangle.</summary>
    [JsonIgnore]
    public ScreenRect Region => new(RegionX, RegionY, RegionWidth, RegionHeight);

    /// <summary>The rule for one region.</summary>
    public FeedbackAction ActionFor(FeedbackRegion region) => region switch
    {
        FeedbackRegion.Before => WhenBeforeZone,
        FeedbackRegion.Inside => WhenInsideZone,
        _ => WhenAfterZone,
    };

    /// <summary>True if any rule taps.</summary>
    [JsonIgnore]
    public bool UsesTap => WhenBeforeZone == FeedbackAction.Tap || WhenInsideZone == FeedbackAction.Tap || WhenAfterZone == FeedbackAction.Tap;

    [JsonIgnore]
    public override StepKind Kind => StepKind.FeedbackLoop;
}
