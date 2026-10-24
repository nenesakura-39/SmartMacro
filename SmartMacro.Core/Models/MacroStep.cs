using System.Text.Json.Serialization;

namespace SmartMacro.Core.Models;

/// <summary>Every kind of step the workflow editor knows about.</summary>
public enum StepKind
{
    Delay,
    MouseMove,
    MouseClick,
    MouseScroll,
    KeyPress,
    TypeText,
    FindText,
    InputSequence,
    FindImage,
    FindColor,

    // Stage 3: workflow blocks (see WorkflowSteps.cs).
    If,
    Else,
    EndIf,
    Repeat,
    EndRepeat,

    // Stage 4: continuous visual feedback (see FeedbackLoopStep.cs).
    FeedbackLoop,

    // Wait for screen change (VISION & AI) and the UTILITY & GAME steps (see UtilitySteps.cs).
    WaitForChange,
    FocusWindow,
    Screenshot,
}

public enum MacroMouseButton
{
    Left,
    Right,
    Middle,
}

public enum MouseClickAction
{
    Click,
    DoubleClick,
    Down,
    Up,

    /// <summary>Press, keep the button down for <see cref="MouseClickStep.HoldMs"/>, release (casting meters, hold-to-buy).</summary>
    Hold,

    /// <summary>
    /// Press at the start point, glide to (<see cref="MouseClickStep.EndX"/>, <see cref="MouseClickStep.EndY"/>) over
    /// <see cref="MouseClickStep.DragDurationMs"/> with the button held, release there (sliders, dragging items).
    /// </summary>
    Drag,
}

public enum KeyAction
{
    Press,
    Down,
    Up,
}

[Flags]
public enum KeyModifiers
{
    None = 0,
    Control = 1,
    Shift = 2,
    Alt = 4,
    Windows = 8,
}

/// <summary>
/// How the cursor travels to a step's target spot.
/// Null on a step (files made before this option, and recordings) = <see cref="Instant"/>, the old behaviour.
/// </summary>
public enum MouseMoveStyle
{
    /// <summary>Jump straight there (the old behaviour). Clicks still get a tiny hover nudge before the press.</summary>
    Instant,

    /// <summary>Glide in a straight line, about 120 cursor updates per second, with a gentle start and stop.</summary>
    SmoothGlide,

    /// <summary>
    /// Glide along a slight curve with an ease-in / ease-out speed, like a hand moving a mouse. Every point along
    /// the way is a real mouse-move event, so sliders, scroll bars and game buttons see the cursor arrive.
    /// </summary>
    HumanCurve,
}

/// <summary>
/// Where a Mouse click / Move mouse step acts (AAA upgrade). Stored on Move mouse steps; on Mouse click steps it is the
/// same setting as the older <see cref="MouseClickStep.UseCurrentPosition"/> / <see cref="MouseClickStep.UseLastFoundText"/>
/// switches (one value, so old files keep working unchanged).
/// </summary>
public enum ClickTargetMode
{
    /// <summary>At the step's own X / Y (the default and the old behaviour).</summary>
    Coordinates,

    /// <summary>
    /// Where the cursor already is. Mouse click: click right there (offsets are not used). Move mouse: move BY the
    /// offset from where the cursor is now.
    /// </summary>
    CurrentCursor,

    /// <summary>
    /// On what the last Find text / image / color step, If / Repeat condition found in this run: its center plus
    /// (<c>OffsetX</c>, <c>OffsetY</c>). E.g. "If the Tic-Tac-Toe board is on screen → click 100 px right of its center".
    /// </summary>
    LastFoundMatch,
}

/// <summary>Defaults shared by every step that moves the cursor or clicks for you.</summary>
public static class MouseMotionDefaults
{
    /// <summary>Default glide time for new Move / Click / Find steps.</summary>
    public const int MoveDurationMs = 120;

    /// <summary>
    /// Default "click hold": how long a click keeps the button down. Games read input once per frame; 35 ms spans two
    /// frames at 60 fps, so the press is never missed.
    /// </summary>
    public const int ClickHoldMs = 35;

    /// <summary>Default gap between the two clicks of a double-click (0 ms was too fast for Roblox).</summary>
    public const int DoubleClickIntervalMs = 65;

    /// <summary>Default "wait after click", so the clicked window / game UI can react before the next step looks.</summary>
    public const int PostActionDelayMs = 120;

    /// <summary>Longest allowed glide.</summary>
    public const int MaxMoveDurationMs = 5_000;

    /// <summary>Longest allowed double-click gap.</summary>
    public const int MaxDoubleClickIntervalMs = 5_000;

    /// <summary>Longest allowed "wait after click".</summary>
    public const int MaxPostActionDelayMs = 60_000;
}

/// <summary>
/// Base class for a single workflow step.
/// The "type" discriminator only maps to the sealed classes listed below, so a macro file
/// can never make the app create some other arbitrary .NET type. Files describe actions, not code.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type",
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(DelayStep), "delay")]
[JsonDerivedType(typeof(MouseMoveStep), "mouseMove")]
[JsonDerivedType(typeof(MouseClickStep), "mouseClick")]
[JsonDerivedType(typeof(MouseScrollStep), "mouseScroll")]
[JsonDerivedType(typeof(KeyPressStep), "keyPress")]
[JsonDerivedType(typeof(TypeTextStep), "typeText")]
[JsonDerivedType(typeof(FindTextStep), "findText")]
[JsonDerivedType(typeof(InputSequenceStep), "inputSequence")]
[JsonDerivedType(typeof(FindImageStep), "findImage")]
[JsonDerivedType(typeof(FindColorStep), "findColor")]
[JsonDerivedType(typeof(IfStep), "if")]
[JsonDerivedType(typeof(ElseStep), "else")]
[JsonDerivedType(typeof(EndIfStep), "endIf")]
[JsonDerivedType(typeof(RepeatStep), "repeat")]
[JsonDerivedType(typeof(EndRepeatStep), "endRepeat")]
[JsonDerivedType(typeof(FeedbackLoopStep), "feedbackLoop")]
[JsonDerivedType(typeof(WaitForChangeStep), "waitForChange")]
[JsonDerivedType(typeof(FocusWindowStep), "focusWindow")]
[JsonDerivedType(typeof(ScreenshotStep), "screenshot")]
public abstract class MacroStep
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Disabled steps stay in the macro but get skipped during playback.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Optional note the user can attach to a step.</summary>
    public string? Comment { get; set; }

    [JsonIgnore]
    public abstract StepKind Kind { get; }
}

/// <summary>Waits for a fixed amount of time.</summary>
public sealed class DelayStep : MacroStep
{
    public int DurationMs { get; set; } = 500;

    [JsonIgnore]
    public override StepKind Kind => StepKind.Delay;
}

/// <summary>Moves the cursor to an absolute screen position (physical pixels, virtual-screen coordinates).</summary>
public sealed class MouseMoveStep : MacroStep
{
    public int X { get; set; }
    public int Y { get; set; }

    /// <summary>How the cursor gets there. Null (older files, recordings) = jump straight there.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MouseMoveStyle? MoveStyle { get; set; }

    /// <summary>How long the glide takes, in ms (only for Smooth glide / Human curve).</summary>
    public int MoveDurationMs { get; set; } = MouseMotionDefaults.MoveDurationMs;

    /// <summary>
    /// Where to move: to X / Y (default, not written to files), to the last found spot + offset, or by the offset from
    /// where the cursor is now. Older files have no value and load as <see cref="ClickTargetMode.Coordinates"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ClickTargetMode TargetMode { get; set; } = ClickTargetMode.Coordinates;

    /// <summary>Offset (px) added to the last found spot, or the distance to move from the cursor (see <see cref="TargetMode"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int OffsetX { get; set; }

    /// <inheritdoc cref="OffsetX"/>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int OffsetY { get; set; }

    /// <summary>
    /// 3D game camera / cursor-locked games: play this move as RELATIVE mouse movement (SendInput MOUSEEVENTF_MOVE without
    /// ABSOLUTE) of <see cref="DeltaX"/>, <see cref="DeltaY"/> counts, the way a physical mouse turns a camera, instead of
    /// putting the cursor on X / Y (games that lock the cursor in the middle of the screen never see absolute moves).
    /// X / Y then only remember where the cursor was when it was recorded. Written only when on (file format 14).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool UseRelativeDelta { get; set; }

    /// <summary>Relative movement (mouse counts, + = right) when <see cref="UseRelativeDelta"/> is on.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int DeltaX { get; set; }

    /// <summary>Relative movement (mouse counts, + = down) when <see cref="UseRelativeDelta"/> is on.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int DeltaY { get; set; }

    [JsonIgnore]
    public override StepKind Kind => StepKind.MouseMove;
}

/// <summary>Clicks / presses / releases a mouse button, either at a position or wherever the cursor is.</summary>
public sealed class MouseClickStep : MacroStep
{
    public MacroMouseButton Button { get; set; } = MacroMouseButton.Left;
    public MouseClickAction Action { get; set; } = MouseClickAction.Click;
    public bool UseCurrentPosition { get; set; }
    public int X { get; set; }
    public int Y { get; set; }

    /// <summary>Click the center of the text found by the most recent "Find text" step (overrides X/Y).</summary>
    public bool UseLastFoundText { get; set; }

    /// <summary>
    /// How long the button stays down for a Click, in ms. 0 = instant (down and up back to back),
    /// which is what macros made before this option existed do. Recordings fill in the real hold time.
    /// </summary>
    public int HoldMs { get; set; }

    /// <summary>Drag only: where the drag ends (physical pixels, like X / Y).</summary>
    public int EndX { get; set; }
    public int EndY { get; set; }

    /// <summary>Drag only: how long the glide from the start to the end takes, in ms (0 = jump straight there).</summary>
    public int DragDurationMs { get; set; } = DefaultDragDurationMs;

    /// <summary>Default glide time for a new Drag.</summary>
    public const int DefaultDragDurationMs = 500;

    /// <summary>Default hold time when the user switches a click to "Hold for a duration".</summary>
    public const int DefaultHoldActionMs = 850;

    /// <summary>How the cursor gets to the click spot. Null (older files, recordings) = jump straight there.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MouseMoveStyle? MoveStyle { get; set; }

    /// <summary>How long the glide to the click spot takes, in ms (only for Smooth glide / Human curve).</summary>
    public int MoveDurationMs { get; set; } = MouseMotionDefaults.MoveDurationMs;

    /// <summary>
    /// "Click hold duration" — the same setting as <see cref="HoldMs"/> (one stored value), under the name the other
    /// clicking steps use. New Mouse click steps start at 35 ms; 0 = instant (older files, unless recorded).
    /// </summary>
    [JsonIgnore]
    public int ClickHoldMs
    {
        get => HoldMs;
        set => HoldMs = value;
    }

    /// <summary>
    /// "Hold duration (ms, 0 = instant click)": how long the button stays down for a Click — the same stored value as
    /// <see cref="HoldMs"/> (files keep using "holdMs"). Recordings fill in the exact hold (a 550 ms rod cast stays 550 ms,
    /// never less than 12 ms); playback keeps the button down that long (÷ speed) before releasing it.
    /// </summary>
    [JsonIgnore]
    public int HoldDurationMs
    {
        get => HoldMs;
        set => HoldMs = value;
    }

    /// <summary>
    /// Double click only: pause between the two clicks, in ms. 0 = back to back (older files). New steps: 65 ms.
    /// </summary>
    public int DoubleClickIntervalMs { get; set; }

    /// <summary>
    /// "Wait after click", in ms, after a Click / Double click / Hold (lets the app react before the next step).
    /// 0 = no extra wait (older files and recordings, whose timing is in their Delay steps). New steps: 120 ms.
    /// </summary>
    public int PostActionDelayMs { get; set; }

    // ---------------- AAA upgrade: where to click + Smart Click Snapshots (format 12) ----------------

    /// <summary>
    /// Where the click happens: at X / Y, where the cursor already is, or on the last found spot (+ offset). The same
    /// setting as <see cref="UseCurrentPosition"/> / <see cref="UseLastFoundText"/> (one stored value), so it isn't
    /// written separately.
    /// </summary>
    [JsonIgnore]
    public ClickTargetMode TargetMode
    {
        get => UseLastFoundText ? ClickTargetMode.LastFoundMatch : UseCurrentPosition ? ClickTargetMode.CurrentCursor : ClickTargetMode.Coordinates;
        set
        {
            UseLastFoundText = value == ClickTargetMode.LastFoundMatch;
            UseCurrentPosition = value == ClickTargetMode.CurrentCursor;
        }
    }

    /// <summary>"On the last found spot": offset (px) from the center of what was found. Not used by the other targets.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int OffsetX { get; set; }

    /// <inheritdoc cref="OffsetX"/>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int OffsetY { get; set; }

    /// <summary>
    /// Smart Click Snapshot: a small PNG picture (56×56 px, Base64) of what was under the cursor when this click was
    /// recorded. Null for clicks made by hand and older files. Shown in the editor; used by Smart-Aim and by
    /// "Convert to Find image".
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetThumbnailBase64 { get; set; }

    /// <summary>
    /// Where inside the thumbnail the click was (px from its left / top edge). The middle, unless the click was near a
    /// screen edge and the picture had to be shifted to stay on screen. Null = the middle.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TargetAnchorX { get; set; }

    /// <inheritdoc cref="TargetAnchorX"/>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TargetAnchorY { get; set; }

    /// <summary>True when the step has a recorded "what you clicked" picture.</summary>
    [JsonIgnore]
    public bool HasTargetThumbnail => !string.IsNullOrEmpty(TargetThumbnailBase64);

    /// <summary>
    /// "✨ Smart-Aim": before clicking at X / Y, look for the recorded thumbnail within <see cref="SmartAimRadiusPx"/> of
    /// where it was, and click the same spot on it if it moved. Not found = click X / Y as recorded. Off by default.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool SmartAimAtTarget { get; set; }

    /// <summary>Default Smart-Aim accuracy (0.85 = 85 % similar).</summary>
    public const double DefaultSmartAimAccuracy = 0.85;

    /// <summary>Default Smart-Aim search distance in pixels.</summary>
    public const int DefaultSmartAimRadiusPx = 120;

    private double? _smartAimAccuracy;
    private int? _smartAimRadiusPx;

    /// <summary>How similar the spot must be to the thumbnail (0.5–1.0). Default 0.85.</summary>
    [JsonIgnore]
    public double SmartAimAccuracy
    {
        get => _smartAimAccuracy ?? DefaultSmartAimAccuracy;
        set => _smartAimAccuracy = value.Equals(DefaultSmartAimAccuracy) ? null : value;
    }

    /// <summary>How far (px, each direction) the target may have moved. Default 120.</summary>
    [JsonIgnore]
    public int SmartAimRadiusPx
    {
        get => _smartAimRadiusPx ?? DefaultSmartAimRadiusPx;
        set => _smartAimRadiusPx = value == DefaultSmartAimRadiusPx ? null : value;
    }

    /// <summary>File form of <see cref="SmartAimAccuracy"/>: only written when it isn't the default.</summary>
    [JsonPropertyName("smartAimMinMatch")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? SmartAimAccuracyOverride
    {
        get => _smartAimAccuracy;
        set => _smartAimAccuracy = value;
    }

    /// <summary>File form of <see cref="SmartAimRadiusPx"/>: only written when it isn't the default.</summary>
    [JsonPropertyName("smartAimRadius")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SmartAimRadiusOverride
    {
        get => _smartAimRadiusPx;
        set => _smartAimRadiusPx = value;
    }

    [JsonIgnore]
    public override StepKind Kind => StepKind.MouseClick;
}

/// <summary>
/// Scrolls the mouse wheel. 120 = one notch. Positive = up (vertical) or right (horizontal).
/// </summary>
public sealed class MouseScrollStep : MacroStep
{
    public int Delta { get; set; } = -120;
    public bool Horizontal { get; set; }
    public bool UseCurrentPosition { get; set; } = true;
    public int X { get; set; }
    public int Y { get; set; }

    [JsonIgnore]
    public override StepKind Kind => StepKind.MouseScroll;
}

/// <summary>Presses, holds or releases one key (Windows virtual-key code). Modifiers only apply to Press.</summary>
public sealed class KeyPressStep : MacroStep
{
    public int VirtualKey { get; set; } = 0x0D; // Enter
    public KeyAction Action { get; set; } = KeyAction.Press;
    public KeyModifiers Modifiers { get; set; } = KeyModifiers.None;

    /// <summary>
    /// How long the key stays down for a Press, in ms. 0 = instant (down and up back to back),
    /// which is what macros made before this option existed do. Recordings fill in the real hold time,
    /// so a short W tap that moved a game character for 120 ms still lasts 120 ms when played back.
    /// </summary>
    public int HoldMs { get; set; }

    /// <summary>The exact recorded hold of a Press (same stored value as <see cref="HoldMs"/>).</summary>
    [JsonIgnore]
    public int HoldDurationMs
    {
        get => HoldMs;
        set => HoldMs = value;
    }

    [JsonIgnore]
    public override StepKind Kind => StepKind.KeyPress;
}

/// <summary>Types a piece of text as Unicode characters.</summary>
public sealed class TypeTextStep : MacroStep
{
    /// <summary>Default pause between characters. 0 made some apps and games drop characters (e.g. after a space).</summary>
    public const int DefaultCharacterDelayMs = 20;

    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// "Delay between characters (ms)". New steps start at 20 ms. Older files that saved 0 keep 0; the input
    /// simulator still keeps a few ms between key down / up and between characters so nothing gets dropped.
    /// </summary>
    public int CharacterDelayMs { get; set; } = DefaultCharacterDelayMs;

    [JsonIgnore]
    public override StepKind Kind => StepKind.TypeText;
}

public enum TextMatchMode
{
    /// <summary>The text appears anywhere, even inside a longer word ("Save" matches "Saved").</summary>
    Contains,

    /// <summary>The text matches complete word(s) on screen.</summary>
    WholeWords,
}

/// <summary>What to do with the thing a Find text / Find image / Find color step found.</summary>
public enum FoundTextAction
{
    None,
    Move,
    Click,
    DoubleClick,
    RightClick,

    /// <summary>Hold the left button on it for <see cref="ScreenSearchStep.ActionHoldMs"/>, then release.</summary>
    Hold,
}

/// <summary>What happens when a Find text / image / color step times out.</summary>
public enum TextNotFoundBehavior
{
    StopMacro,
    Continue,

    /// <summary>
    /// Skip the step right after this one (or the whole If / Repeat block that starts there), then continue.
    /// Lets a Find step "gate" the action below it without stopping the whole macro.
    /// </summary>
    SkipNextStep,
}

/// <summary>What a Find text / image / color step waits for.</summary>
public enum WaitGoal
{
    /// <summary>Wait until it is on screen (the original behaviour).</summary>
    Appear,

    /// <summary>Wait until it is no longer on screen (e.g. a "Loading…" message or a progress bar).</summary>
    Disappear,
}

/// <summary>
/// Shared settings of the steps that look at the screen (Find text, Find image, Find color): where to look,
/// how long to keep checking, what to wait for, and what to do with the result.
/// Coordinates are physical screen pixels (virtual-screen coordinates), the same as mouse steps.
/// </summary>
public abstract class ScreenSearchStep : MacroStep
{
    private bool _searchFullScreen = true;
    private Vision.SearchScope? _scope;

    /// <summary>
    /// The original "Search the whole screen (all monitors)" switch. Still written to files (older SmartMacro builds
    /// read it): true for every scope except <see cref="Vision.SearchScope.CustomArea"/>. Older files only have this, so
    /// true = all monitors and false = the custom area (see <see cref="EffectiveScope"/>).
    /// </summary>
    public bool SearchFullScreen
    {
        get => _searchFullScreen;
        set
        {
            _searchFullScreen = value;
            // Turning "whole screen" off always means "my own area"; turning it on doesn't change a chosen
            // monitor / window (files store both, and they always agree).
            if (!value && _scope is not null) _scope = Vision.SearchScope.CustomArea;
            else if (value && _scope == Vision.SearchScope.CustomArea) _scope = Vision.SearchScope.AllMonitors;
        }
    }

    /// <summary>
    /// "Where to look": main monitor, a specific monitor, an app window, a custom area or all monitors.
    /// Null in files made before this option (then <see cref="SearchFullScreen"/> decides). New steps use the main monitor.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Vision.SearchScope? Scope
    {
        get => _scope;
        set
        {
            _scope = value;
            if (value is { } scope) _searchFullScreen = scope != Vision.SearchScope.CustomArea;
        }
    }

    /// <summary>The scope that is actually used (old files: whole screen → all monitors, otherwise the custom area).</summary>
    [JsonIgnore]
    public Vision.SearchScope EffectiveScope =>
        _scope ?? (_searchFullScreen ? Vision.SearchScope.AllMonitors : Vision.SearchScope.CustomArea);

    /// <summary>"Specific monitor": which one (1 = the main monitor, then left to right).</summary>
    public int MonitorNumber { get; set; } = 1;

    /// <summary>"Specific app window": (part of) the window title, e.g. "Roblox". Matched ignoring case.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WindowTitle { get; set; }

    /// <summary>How the cursor gets to what was found. Null (older files) = jump straight there.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MouseMoveStyle? MoveStyle { get; set; }

    /// <summary>How long the glide to what was found takes, in ms.</summary>
    public int MoveDurationMs { get; set; } = MouseMotionDefaults.MoveDurationMs;

    /// <summary>How long a "when found" click keeps the button down, in ms.</summary>
    public int ClickHoldMs { get; set; } = MouseMotionDefaults.ClickHoldMs;

    /// <summary>Gap between the two clicks of a "when found" double-click, in ms.</summary>
    public int DoubleClickIntervalMs { get; set; } = MouseMotionDefaults.DoubleClickIntervalMs;

    /// <summary>"Wait after click": pause after a click / hold on what was found, in ms.</summary>
    public int PostActionDelayMs { get; set; } = MouseMotionDefaults.PostActionDelayMs;

    public int RegionX { get; set; }
    public int RegionY { get; set; }
    public int RegionWidth { get; set; } = 400;
    public int RegionHeight { get; set; } = 200;

    /// <summary>How long to keep looking. 0 = look once.</summary>
    public int TimeoutMs { get; set; } = 10_000;

    /// <summary>Pause between screen checks while waiting.</summary>
    public int CheckIntervalMs { get; set; } = 500;

    /// <summary>Wait for it to appear (default) or to disappear. Files made before this option load as Appear.</summary>
    public WaitGoal Goal { get; set; } = WaitGoal.Appear;

    /// <summary>What to do once it's found (only used when waiting for it to appear).</summary>
    public FoundTextAction Action { get; set; } = FoundTextAction.None;

    /// <summary>Offset from the center of what was found, for Move / Click.</summary>
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }

    /// <summary>For the "hold the left button on it" action: how long to hold, in ms.</summary>
    public int ActionHoldMs { get; set; } = DefaultActionHoldMs;

    public const int DefaultActionHoldMs = 500;

    /// <summary>
    /// After a click / hold on what was found: move the cursor out of the search area, so a hover effect or the cursor
    /// itself doesn't cover the next thing to find (a game button that re-appears in the same spot). Off by default.
    /// </summary>
    public bool MoveCursorAwayAfterAction { get; set; }

    public TextNotFoundBehavior WhenNotFound { get; set; } = TextNotFoundBehavior.StopMacro;

    /// <summary>The custom search area as a rectangle (only used with <see cref="Vision.SearchScope.CustomArea"/>).</summary>
    [JsonIgnore]
    public Ocr.ScreenRect Region => new(RegionX, RegionY, RegionWidth, RegionHeight);
}

/// <summary>
/// Uses OCR to look for text on the screen, waiting up to a timeout, and optionally
/// moves to / clicks on it. Later Mouse click steps can also click "the last found" spot.
/// Can also wait until the text disappears.
/// </summary>
public sealed class FindTextStep : ScreenSearchStep
{
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Whole words (new steps) or "contains" (the text may be part of a longer word). Saved as before; older files keep
    /// what they stored (they were made with "contains").
    /// </summary>
    public TextMatchMode MatchMode { get; set; } = TextMatchMode.WholeWords;

    /// <summary>Upper / lower case must match exactly (new steps: on). Older files keep what they stored.</summary>
    public bool CaseSensitive { get; set; } = true;

    /// <summary>
    /// "Match whole word only (e.g. 'art' won't match 'apart')": the text must be a complete word on screen – never a
    /// piece of a longer word – and leading / trailing punctuation of the screen word is ignored ("art," matches "art").
    /// The same setting as <see cref="MatchMode"/> = <see cref="TextMatchMode.WholeWords"/> (not saved separately).
    /// </summary>
    [JsonIgnore]
    public bool MatchWholeWord
    {
        get => MatchMode == TextMatchMode.WholeWords;
        set => MatchMode = value ? TextMatchMode.WholeWords : TextMatchMode.Contains;
    }

    /// <summary>
    /// "Match exact case (e.g. 'aRt' only matches 'aRt')": compares with exact upper / lower case (ordinal). The same
    /// setting as <see cref="CaseSensitive"/> (not saved separately).
    /// </summary>
    [JsonIgnore]
    public bool MatchCase
    {
        get => CaseSensitive;
        set => CaseSensitive = value;
    }

    /// <summary>How close the OCR text must be to the search text (50-100). Lower = more forgiving of OCR mistakes.</summary>
    public int MinSimilarityPercent { get; set; } = 85;

    /// <summary>
    /// Treat characters OCR often mixes up as equal (0 / O and 1 / I / l), so "OK" still matches a screen read as "0K".
    /// On by default; files made before this option load with it on too.
    /// </summary>
    public bool NormalizeLookAlikes { get; set; } = true;

    [JsonIgnore]
    public override StepKind Kind => StepKind.FindText;
}

/// <summary>
/// Looks for a small picture (a button, icon, symbol…) on the screen by comparing pixels, waiting up to a timeout,
/// and optionally moves to / clicks on it, or waits until it disappears. The picture is captured in the editor and
/// stored inside the macro (see <see cref="Vision.TemplateCodec"/>). It must look the same size on screen as when it
/// was captured (same display scaling and game resolution).
/// </summary>
public sealed class FindImageStep : ScreenSearchStep
{
    public FindImageStep()
    {
        CheckIntervalMs = 250;
    }

    /// <summary>Size of the stored picture in pixels.</summary>
    public int TemplateWidth { get; set; }
    public int TemplateHeight { get; set; }

    /// <summary>The stored picture: B, G, R bytes per pixel, zlib-compressed, Base64. Empty until captured.</summary>
    public string TemplateData { get; set; } = string.Empty;

    /// <summary>How similar a spot must be (50-100 %). 90 is a good start; lower it for slightly changing images.</summary>
    public int MinSimilarityPercent { get; set; } = 90;

    /// <summary>Match the shape only, even if the colours are different (e.g. any colour of the same icon).</summary>
    public bool IgnoreColor { get; set; }

    /// <summary>
    /// Ignore this much (0-40 %) of the picture on every side. Round buttons and game UI have background in the corners
    /// of the captured rectangle; when that background changes (a 3D scene) the match fails although the button is
    /// there. 15 % is a good value for round buttons. 0 = compare the whole picture (older files).
    /// </summary>
    public int IgnoreEdgesPercent { get; set; }

    /// <summary>Recommended value for round buttons / game UI.</summary>
    public const int RecommendedIgnoreEdgesPercent = 15;

    /// <summary>
    /// "Ignore background / match bright text and edges": also compare only the picture's foreground (the strokes
    /// that stand out, like the white text and ring of a semi-transparent game button), so it still matches when a
    /// different scene shows through it. Each spot gets the better of the normal and the foreground score.
    /// Off for older files.
    /// </summary>
    public bool MatchForeground { get; set; }

    /// <summary>
    /// True (default) to adapt to Day/Night lighting and brightness changes using zero-mean illumination-normalized
    /// matching.
    /// </summary>
    [JsonIgnore]
    public bool AdaptToLighting { get; set; } = true;

    /// <summary>
    /// Serialized only when turned off so existing macros keep their exact default JSON and schema version.
    /// </summary>
    [JsonPropertyName("disableLightingAdaptation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool DisableLightingAdaptation
    {
        get => !AdaptToLighting;
        set => AdaptToLighting = !value;
    }

    [JsonIgnore]
    public override StepKind Kind => StepKind.FindImage;
}

/// <summary>
/// Looks for pixels of a colour (within a tolerance) on the screen, waiting up to a timeout. The biggest patch of
/// that colour counts as "found" (its middle is used for Move / Click). Simple colour matching, not object
/// recognition: use a small search area and a distinctive colour.
/// </summary>
public sealed class FindColorStep : ScreenSearchStep
{
    public FindColorStep()
    {
        CheckIntervalMs = 250;
    }

    /// <summary>The colour to look for, written as "#RRGGBB".</summary>
    public string Color { get; set; } = "#FF0000";

    /// <summary>True when a 2nd colour should also be matched.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool UseSecondaryColor { get; set; }

    /// <summary>Optional 2nd colour to look for, written as "#RRGGBB".</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SecondaryColor { get; set; }

    /// <summary>Allowed difference per red / green / blue channel (0 = exact, 255 = anything).</summary>
    public int Tolerance { get; set; } = 24;

    /// <summary>The patch must have at least this many pixels (ignores stray single pixels).</summary>
    public int MinPixels { get; set; } = 10;

    /// <summary>
    /// "Shade variation": also match darker / lighter shades of the same colour (the shadowed and highlighted sides of a
    /// 3D ball, a glowing target), not only the colour ± <see cref="Tolerance"/>. A pixel then counts when, brought to the
    /// colour's brightness (50 %–200 % of it), it is within the tolerance. Off for older files; only saved when on.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool MatchShades { get; set; }

    /// <summary>The one-click tuning presets of the Find colour editor.</summary>
    public static readonly (string Name, int Tolerance, bool MatchShades) ExactUiPreset = ("Exact UI Color (Strict)", 10, false);

    /// <inheritdoc cref="ExactUiPreset"/>
    public static readonly (string Name, int Tolerance, bool MatchShades) GameTargetPreset = ("Game Target / Solid Object (Recommended)", 28, true);

    /// <inheritdoc cref="ExactUiPreset"/>
    public static readonly (string Name, int Tolerance, bool MatchShades) GlowingObjectPreset = ("Lighting / Glowing Object (Broad)", 50, true);

    [JsonIgnore]
    public override StepKind Kind => StepKind.FindColor;
}

/// <summary>
/// A recorded stream of input: mouse movement, key and button down/up and wheel events, each with its own
/// timestamp. Played back on one timeline so the original order and spacing of simultaneous keyboard and
/// mouse input is kept. Created by recording; not added by hand from the toolbox.
/// Two flavours:
/// - Game mode (no <see cref="MouseSegments"/>): mouse movement is always relative (like a real mouse),
///   which is what first-person games read for camera rotation. This is the original format.
/// - Smart recording (has <see cref="MouseSegments"/>): every mouse event also has the cursor position, and each
///   segment says whether that part plays cursor positions (menus, buttons) or relative movement (camera).
/// </summary>
public sealed class InputSequenceStep : MacroStep
{
    /// <summary>The recorded events, oldest first. Times are microseconds from the start of the step.</summary>
    [JsonConverter(typeof(InputEventListJsonConverter))]
    public List<InputEvent> Events { get; set; } = new();

    /// <summary>
    /// Smart recordings only: how each part of the timeline plays mouse movement. Null for game-mode recordings
    /// (and old files), which always play relative movement exactly like before.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<MouseSegment>? MouseSegments { get; set; }

    /// <summary>True for smart recordings (per-segment mouse playback).</summary>
    [JsonIgnore]
    public bool HasMouseSegments => MouseSegments is { Count: > 0 };

    /// <summary>Where the cursor was when recording started (physical pixels).</summary>
    public int StartX { get; set; }
    public int StartY { get; set; }

    /// <summary>
    /// Put the cursor back at StartX/StartY before playing. Useful when the game shows a free cursor
    /// (menus). Leave off when the game locks the cursor for camera control.
    /// </summary>
    public bool MoveCursorToStart { get; set; }

    /// <summary>
    /// Smart Click Snapshots of the clicks inside this recorded input (smart recordings with a visible cursor; format 12),
    /// one per button-down event that had a picture, by its index in <see cref="Events"/>. Kept so "✂ Break into steps"
    /// can give each resulting Mouse click its picture (and Smart-Aim). Null when there are none. Playback ignores it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SequenceClickTarget>? ClickTargets { get; set; }

    /// <summary>The picture recorded for the button-down event at <paramref name="eventIndex"/>, or null.</summary>
    public SequenceClickTarget? ClickTargetFor(int eventIndex) =>
        ClickTargets?.FirstOrDefault(t => t is not null && t.EventIndex == eventIndex);

    [JsonIgnore]
    public override StepKind Kind => StepKind.InputSequence;
}

/// <summary>
/// "What you clicked" for one click inside a recorded-input step: the 56×56 PNG (Base64) of the button-down event at
/// <see cref="EventIndex"/>, and where in it the click was (same meaning as on <see cref="MouseClickStep"/>).
/// </summary>
public sealed class SequenceClickTarget
{
    /// <summary>Index of the MouseDown event in <see cref="InputSequenceStep.Events"/>.</summary>
    public int EventIndex { get; set; }

    /// <summary>The picture as a Base64 PNG (like <see cref="MouseClickStep.TargetThumbnailBase64"/>).</summary>
    [JsonPropertyName("thumbnail")]
    public string TargetThumbnailBase64 { get; set; } = string.Empty;

    public int AnchorX { get; set; }

    public int AnchorY { get; set; }
}
