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

/// <summary>How a feedback loop turns what it sees into hold / release.</summary>
public enum FeedbackControlMode
{
    /// <summary>
    /// "Classic Simple Hold": the region rules (before / inside / after the zone → Hold / Release / Keep / Tap / Pulse),
    /// exactly as before Feedback Loop 2.0. Older files and Click-when-in-zone loops use it.
    /// </summary>
    ClassicSimpleHold,

    /// <summary>
    /// "Smart Predictive (Adaptive PD + Brake)": tracks where the target and the bar are AND how fast they move,
    /// predicts their positions ahead by the measured loop latency, damps relative velocity, and brakes automatically
    /// using kinematic stopping distance before the bar can overshoot the target.
    /// </summary>
    SmartPredictive,
}

/// <summary>
/// Learned or calibrated physical profile of the player control bar along the active axis.
/// </summary>
public sealed record BarProfile(
    double RiseAccelPxPerSec2 = 750.0,
    double FallAccelPxPerSec2 = 650.0,
    double MaxSpeedPxPerSec = 520.0,
    double GlidePx = 18.0,
    double LatencyMs = 34.0,
    double HoverDuty = 0.45,
    bool IsCalibrated = false);

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

    // ---------------- Smart Predictive (Feedback Loop 2.0) ----------------

    /// <summary>
    /// Classic (default for older files and code) or Smart Predictive (new toolbox steps and the game presets). Only saved
    /// when it isn't Classic.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public FeedbackControlMode ControlMode { get; set; } = FeedbackControlMode.ClassicSimpleHold;

    /// <summary>
    /// Smart Predictive: how far ahead (seconds) the target and the bar are predicted from their speeds. 0.18 = 180 ms:
    /// predictedError = (target + targetVelocity × 0.18) − (bar + barVelocity × 0.18).
    /// </summary>
    public double LeadPredictionFactor { get; set; } = DefaultLeadPredictionFactor;

    public const double DefaultLeadPredictionFactor = 0.18;

    /// <summary>Smart Predictive: a predicted gap of at most this many pixels counts as "on target" (hover / pulse zone).</summary>
    public int DeadzonePx { get; set; } = DefaultDeadzonePx;

    public const int DefaultDeadzonePx = 6;

    /// <summary>
    /// Smart Predictive: near the target (inside the dead zone) micro-pulse the input at a proportional duty cycle
    /// (every <see cref="PulsePeriodMs"/>) so a bar with gravity hovers in place, instead of full hold / full release.
    /// Off = keep the current state inside the dead zone.
    /// </summary>
    public bool UsePwmMicroPulse { get; set; } = true;

    // ---------------- Closed-Loop PWM PD+FeedForward Servo (AAA Minigame Upgrade) ----------------

    /// <summary>
    /// Flips the default axis polarity when a game uses reversed controls.
    /// By default: Horizontal holding moves +X (Right); Vertical holding moves UP (-Y in screen coordinates).
    /// </summary>
    public bool InvertDirection { get; set; } = false;

    /// <summary>
    /// The baseline PWM hold ratio (0.0 to 1.0) required to counteract gravity and hover stationary when error == 0.
    /// </summary>
    public double NeutralHoverDutyCycle { get; set; } = 0.45;

    /// <summary>
    /// The micro-pulse period in milliseconds (10 to 60) used to convert continuous control output u in [0.0, 1.0]
    /// into high-frequency hold/release pulses (holdMs = u * PwmCycleMs, releaseMs = (1 - u) * PwmCycleMs).
    /// </summary>
    public int PwmCycleMs { get; set; } = 22;

    /// <summary>Proportional position error gain (Kp) for the PWM servo controller.</summary>
    public double Kp { get; set; } = 1.35;

    /// <summary>Derivative bar-velocity damping gain (Kd) for the PWM servo controller.</summary>
    public double Kd { get; set; } = 0.65;

    /// <summary>
    /// Target Velocity Feed-Forward Gain (Kv): tracks the target/fish velocity so the bar immediately moves with a moving fish.
    /// </summary>
    public double DerivativeTargetGain { get; set; } = 0.35;

    /// <summary>Convenience alias for <see cref="DerivativeTargetGain"/> (Kv).</summary>
    [JsonIgnore]
    public double Kv
    {
        get => DerivativeTargetGain;
        set => DerivativeTargetGain = value;
    }

    /// <summary>
    /// Extra counter-braking multiplier applied to Kd when the bar is rushing toward the target at high speed
    /// or when the target suddenly reverses direction ("ankle-break").
    /// </summary>
    public double AnkleBreakBrakingMultiplier { get; set; } = 1.65;

    /// <summary>
    /// Boundary / Wall Lock threshold (0 to 25 % of SearchRegion). When the target is within the release wall N%,
    /// clamps output to 0.0 (Full Release Lock). When within the hold wall N%, clamps output to Full Hold Lock.
    /// </summary>
    public double BoundaryLockThresholdPercent { get; set; } = 10.0;

    /// <summary>
    /// Automatically completes the FeedbackLoopStep early as soon as the minigame UI disappears (both Target and Indicator
    /// absent for <see cref="DisappearExitMs"/> after having been seen at least once).
    /// </summary>
    public bool AutoExitWhenBarDisappears { get; set; } = true;

    /// <summary>Duration in milliseconds that both Target and Indicator must be absent (after being seen) to auto-exit.</summary>
    public int DisappearExitMs { get; set; } = 650;

    /// <summary>Shows the live AAA FeedbackTelemetryOverlay HUD around the SearchRegion during execution.</summary>
    public bool ShowTelemetryHud { get; set; } = true;

    // ---------------- Feedback Loop 3.0: Auto-Calibrate, Comfort Zone & Frame-Safe Sigma-Delta ----------------

    /// <summary>
    /// Automatically measures the bar's rise/fall acceleration, neutral hover duty cycle, and response latency during play
    /// and adapts braking margin online whenever overshoot or lag is observed.
    /// </summary>
    public bool AutoCalibrate { get; set; } = true;

    /// <summary>
    /// Overall tracking responsiveness from 0 (Calm / Smooth) to 100 (Aggressive / Snappy). Default: 50.
    /// Scales the comfort zone tightness, proportional thrust, and stopping-distance safety margin.
    /// </summary>
    public int Responsiveness { get; set; } = 50;

    /// <summary>
    /// Safety margin inside each edge of the player bar (0 to 45 % of bar width per side; default 20 %).
    /// The controller keeps the target inside this inner "comfort zone" and brakes before the target crosses the comfort edge.
    /// </summary>
    public double ComfortZonePercent { get; set; } = 20.0;

    /// <summary>
    /// Minimum duration (in milliseconds, 8 to 50 ms; default 17 ms) for any hold or release state change in the
    /// frame-safe Sigma-Delta modulator so sub-frame pulses are never dropped by 60 FPS game loops.
    /// </summary>
    public int MinPulseMs { get; set; } = 17;

    /// <summary>
    /// Optional learned physical profile of the player bar (populated by Auto-Tune or online auto-calibration).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BarProfile? LearnedProfile { get; set; }

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

    /// <summary>Alias for <see cref="Region"/>.</summary>
    [JsonIgnore]
    public ScreenRect SearchRegion
    {
        get => Region;
        set
        {
            RegionX = value.X;
            RegionY = value.Y;
            RegionWidth = value.Width;
            RegionHeight = value.Height;
        }
    }

    /// <summary>Convenience alias for <see cref="MaxRuntimeMs"/> in seconds.</summary>
    [JsonIgnore]
    public double TimeoutSeconds
    {
        get => MaxRuntimeMs / 1000.0;
        set => MaxRuntimeMs = (int)Math.Round(value * 1000.0);
    }

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
