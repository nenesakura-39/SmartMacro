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

    // v1.2 "Brain": Variables, Resilience & Sub-Macros (see Variables.cs & ResilienceAndSubMacros.cs).
    SetVariable,
    Checkpoint,
    CallMacro,

    // Pro Flow Control (see WorkflowSteps.cs).
    FlowControl,
}

public enum MacroMouseButton
{
    Left,
    Right,
    Middle,
    XButton1,
    XButton2,
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
[JsonDerivedType(typeof(SetVariableStep), "setVariable")]
[JsonDerivedType(typeof(CheckpointStep), "checkpoint")]
[JsonDerivedType(typeof(CallMacroStep), "callMacro")]
[JsonDerivedType(typeof(FlowControlStep), "flowControl")]
public abstract class MacroStep
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Disabled steps stay in the macro but get skipped during playback.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Optional note the user can attach to a step.</summary>
    public string? Comment { get; set; }

    [JsonIgnore]
    public abstract StepKind Kind { get; }

    /// <summary>Creates an independent structural copy of this step (retaining the same <see cref="Id"/>).</summary>
    public virtual MacroStep Clone() => (MacroStep)MemberwiseClone();
}

/// <summary>How a <see cref="DelayStep"/> decides when to move on (v1.1 Adaptive Timing).</summary>
public enum DelayMode
{
    /// <summary>Wait for the exact <see cref="DelayStep.DurationMs"/> (the classic behavior).</summary>
    Fixed,

    /// <summary>
    /// Wait at least <see cref="DelayStep.MinDurationMs"/>, then continue as soon as screen motion around the last
    /// action settles for <see cref="DelayStep.EffectiveQuietWindowMs"/> (or when <see cref="DelayStep.EffectiveMaxDurationMs"/> expires).
    /// </summary>
    UntilScreenSettles,

    /// <summary>
    /// Wait at least <see cref="DelayStep.MinDurationMs"/>, then continue as soon as the screen around the last action
    /// visibly changes from how it looked when the wait began (or when <see cref="DelayStep.EffectiveMaxDurationMs"/> expires).
    /// </summary>
    UntilScreenChanges,
}

/// <summary>Waits for a fixed amount of time, or adaptively until the screen settles / changes.</summary>
public sealed class DelayStep : MacroStep
{
    /// <summary>Default calm period required for <see cref="DelayMode.UntilScreenSettles"/> (ms).</summary>
    public const int DefaultQuietWindowMs = 120;

    public int DurationMs { get; set; } = 500;

    /// <summary>Wait strategy. Omitted from JSON when <see cref="DelayMode.Fixed"/> so existing files stay unchanged.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public DelayMode Mode { get; set; } = DelayMode.Fixed;

    /// <summary>Adaptive modes: minimum wait before checking the screen (ms).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int MinDurationMs { get; set; }

    /// <summary>Adaptive modes: safety ceiling after which playback continues even if the screen hasn't settled/changed (ms).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int MaxDurationMs { get; set; }

    /// <summary>Optional custom calm window (ms) for <see cref="DelayMode.UntilScreenSettles"/>. 0 = <see cref="DefaultQuietWindowMs"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int QuietWindowMs { get; set; }

    [JsonIgnore]
    public int EffectiveQuietWindowMs => QuietWindowMs > 0 ? QuietWindowMs : DefaultQuietWindowMs;

    [JsonIgnore]
    public int EffectiveMaxDurationMs => Math.Max(MinDurationMs, MaxDurationMs > 0 ? MaxDurationMs : DurationMs);

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

    /// <summary>Optional key into <see cref="Macro.Assets"/> when serialized with deduplicated snapshots.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SnapshotAssetId { get; set; }

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

    // ---------------- v1.1 Intent Recording (format 15) ----------------

    /// <summary>
    /// Multi-signal "Intent" fingerprint captured when this click was recorded (UI element, nearby OCR text, and owning
    /// window). Null for older files and hand-created coordinate clicks.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Targeting.TargetFingerprint? Target { get; set; }

    /// <summary>
    /// "🧭 Smart Intent": resolve where to click using the 5-tier fallback chain (UI Automation → OCR text → Multi-scale
    /// snapshot → Window-relative → X/Y) so moved, resized, or DPI-scaled windows don't break the click.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool UseSmartIntent { get; set; }

    /// <summary>True when an Intent fingerprint with at least one non-coordinate signal is stored on this step.</summary>
    [JsonIgnore]
    public bool HasTargetIntent => Target is { HasAnySignal: true };

    /// <summary>
    /// Optional target window title or process name for background / inactive window clicks via <c>PostMessage</c>.
    /// When set, the click is dispatched directly to the matching window without moving the physical cursor or stealing focus.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BackgroundWindowTitle { get; set; }

    /// <summary>True when this click should be sent directly to a background window without moving the physical cursor.</summary>
    [JsonIgnore]
    public bool SendInBackground => !string.IsNullOrWhiteSpace(BackgroundWindowTitle);

    /// <summary>
    /// Selects how background clicks targeting <see cref="BackgroundWindowTitle"/> execute
    /// (<see cref="BackgroundInputBackend.Auto"/> tries UI Automation first on normal clicks then falls back to Win32 PostMessage).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public BackgroundInputBackend BackgroundBackend { get; set; } = BackgroundInputBackend.Auto;

    [JsonIgnore]
    public override StepKind Kind => StepKind.MouseClick;

    /// <inheritdoc />
    public override MacroStep Clone()
    {
        var copy = (MouseClickStep)MemberwiseClone();
        copy.Target = Target?.Clone();
        return copy;
    }
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
    /// Optional hardware scan code (1..255) captured during recording or live key binding.
    /// When non-zero, playback sends the exact hardware scan code alongside the virtual-key code.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ScanCode { get; set; }

    /// <summary>Whether the key uses the extended scan-code prefix (0xE0), e.g. arrow keys or right-hand modifiers.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Extended { get; set; }

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

    /// <summary>
    /// Optional target window title or process name for background / inactive window key presses via <c>PostMessage</c>.
    /// When set, the key is posted directly to the matching window without stealing focus.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BackgroundWindowTitle { get; set; }

    /// <summary>True when this key press should be sent directly to a background window without stealing focus.</summary>
    [JsonIgnore]
    public bool SendInBackground => !string.IsNullOrWhiteSpace(BackgroundWindowTitle);

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

    /// <summary>
    /// Optional target window title or process name for background / inactive window text typing via <c>PostMessage(WM_CHAR)</c>.
    /// When set, characters are posted directly to the matching window without stealing focus.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BackgroundWindowTitle { get; set; }

    /// <summary>True when this text should be typed directly into a background window without stealing focus.</summary>
    [JsonIgnore]
    public bool SendInBackground => !string.IsNullOrWhiteSpace(BackgroundWindowTitle);

    /// <summary>
    /// Selects how background text targeting <see cref="BackgroundWindowTitle"/> is delivered
    /// (<see cref="BackgroundInputBackend.Auto"/> tries UI Automation <c>ValuePattern.SetValue</c> then falls back to Win32 <c>PostMessage</c>).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public BackgroundInputBackend BackgroundBackend { get; set; } = BackgroundInputBackend.Auto;

    [JsonIgnore]
    public override StepKind Kind => StepKind.TypeText;
}

/// <summary>Selects how background steps deliver input to inactive windows without stealing focus.</summary>
public enum BackgroundInputBackend
{
    /// <summary>Try UI Automation control patterns first when supported, falling back to Win32 PostMessage.</summary>
    Auto = 0,

    /// <summary>Send direct Win32 <c>PostMessage</c> mouse/keyboard messages to the target window handle.</summary>
    Win32PostMessage = 1,

    /// <summary>Execute via UI Automation control patterns (<c>Invoke</c>, <c>Toggle</c>, <c>SelectionItem</c>, <c>Value.SetValue</c>) only.</summary>
    UiAutomationInvoke = 2,
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

    /// <summary>Middle-click its center.</summary>
    MiddleClick,

    /// <summary>Move to it, pause briefly so game/UI hover state registers, then left-click.</summary>
    HoverThenClick,

    /// <summary>Press a keyboard key (e.g. Space / E / Enter) without clicking.</summary>
    PressKey,

    /// <summary>Left-click its center, then press a keyboard key.</summary>
    ClickAndPressKey,

    /// <summary>Left-click its center, then type text.</summary>
    ClickAndTypeText,

    /// <summary>Scroll the mouse wheel up at its center.</summary>
    ScrollUp,

    /// <summary>Scroll the mouse wheel down at its center.</summary>
    ScrollDown,
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
/// Vision 2.0 1-Click Scenario preset for Find Image / Color / Text steps. Configures polling rate, partial visibility,
/// motion tracking, predictive click, and detector strategy in one choice for beginners.
/// </summary>
public enum VisionScenario
{
    /// <summary>Manual / standard settings (default).</summary>
    Custom = 0,

    /// <summary>🖥️ Static App / Website button (clean 90% match, standard 250 ms checks).</summary>
    StaticUi = 1,

    /// <summary>🎮 Game HUD / Menu (ignores 15% outer corners + see-through backgrounds, Day/Night adaptation).</summary>
    GameHud = 2,

    /// <summary>🏃 Moving Object / Target (Kalman velocity tracking + predictive lead click + 70% partial visibility).</summary>
    MovingTarget = 3,

    /// <summary>✨ Busy Scene / Particles (matches with 40% occlusion, Template+Edge fusion, 2-frame flash confirmation).</summary>
    BusyParticles = 4,

    /// <summary>📜 Shuffling / Scrolling Text (O(1) word index + Find-Once-Then-Track as image).</summary>
    ScrollingText = 5,

    /// <summary>⚡ Instant Reflex (fastest 20 ms checks, 16 ms budget, predictive click).</summary>
    FastReaction = 6,
}

/// <summary>Vision 2.0 time / accuracy search strategy per step.</summary>
public enum VisionSearchStrategy
{
    /// <summary>Default: Hot-Spot fast path + coarse-to-fine pyramid.</summary>
    Balanced = 0,

    /// <summary>Fastest reaction: prioritizes tracked/motion ROIs and tighter candidate budgets.</summary>
    Fastest = 1,

    /// <summary>Highest accuracy: runs full multi-detector / partial-visibility checks.</summary>
    MostAccurate = 2,

    /// <summary>Time-budgeted: adapts candidate refinement to complete each look within <see cref="ScreenSearchStep.TimeBudgetMs"/>.</summary>
    Deadline = 3,
}

/// <summary>Vision 2.0 image detector algorithm.</summary>
public enum ImageDetectorKind
{
    /// <summary>Standard ZNCC + Direct Color template matching (default).</summary>
    Template = 0,

    /// <summary>Sobel gradient-orientation shape matching (invariant to severe colour/lighting shifts and flat fills).</summary>
    EdgeShape = 1,

    /// <summary>Fuses Template + EdgeShape matching (keeps the higher confidence at each candidate).</summary>
    Combined = 2,
}

/// <summary>Vision 2.0 colour matching space.</summary>
public enum ColorSpaceKind
{
    /// <summary>Per-channel RGB tolerance (default).</summary>
    Rgb = 0,

    /// <summary>Hue / Saturation / Value range (resilient to lighting and particle bloom).</summary>
    Hsv = 1,

    /// <summary>Perceptual CIE L*a*b* Delta-E distance.</summary>
    LabDeltaE = 2,
}

/// <summary>Vision 2.0 rule for choosing which matching colour object to act on.</summary>
public enum ColorSelectRule
{
    /// <summary>Largest object by pixel count (default).</summary>
    Largest = 0,

    /// <summary>Closest to the center of the search area.</summary>
    NearestToCenter = 1,

    /// <summary>Closest to where the target was last found / tracked.</summary>
    NearestToLastSpot = 2,

    /// <summary>Highest on screen (smallest Y, then smallest X).</summary>
    TopMost = 3,
}

/// <summary>Vision 2.0 OCR image preprocessing mode for stylized or low-contrast game fonts.</summary>
public enum OcrPreprocessMode
{
    /// <summary>Standard padding and small-region 2× upscale (default).</summary>
    Auto = 0,

    /// <summary>Stretches 2nd–98th percentile luminance for faint or low-contrast text.</summary>
    HighContrast = 1,

    /// <summary>Inverts bright HUD text on dark backgrounds before OCR.</summary>
    InvertLightOnDark = 2,

    /// <summary>Adaptive Otsu binarization to strip noisy backgrounds/particles behind text.</summary>
    BinarizeOtsu = 3,
}

/// <summary>
/// How a Find text / image / color step chooses among multiple matches on screen (e.g. finding "nice" on a 1,500-word screen).
/// </summary>
public enum SearchMatchMode
{
    /// <summary>Find One: pick the highest-confidence / first match (default).</summary>
    FirstOrBest = 0,

    /// <summary>Find All: detect and count every occurrence on screen, highlight all of them, and optionally act on all matches in sequence.</summary>
    AllMatches = 1,

    /// <summary>Find Specific #: pick the N-th match in reading order (top-to-bottom, left-to-right), set by <see cref="ScreenSearchStep.TargetMatchIndex"/>.</summary>
    ReadingOrderNth = 2,

    /// <summary>Find One (Random): pick a random match among all occurrences found on screen.</summary>
    RandomMatch = 3,

    /// <summary>Find One (Nearest to Mouse): pick the match closest to the current cursor position.</summary>
    NearestToCursor = 4,
}

/// <summary>
/// Order in which <see cref="SearchMatchMode.AllMatches"/> acts on multiple matches when an action (Click, Move, etc.) is enabled.
/// </summary>
public enum MultiMatchOrder
{
    /// <summary>Top-to-bottom, left-to-right reading order (default).</summary>
    ReadingOrder = 0,

    /// <summary>Bottom-to-top, right-to-left (ideal when clicking items removes or collapses rows in a list).</summary>
    BottomToTop = 1,

    /// <summary>Shortest path (nearest-neighbor chain starting from the first match or cursor).</summary>
    NearestNeighbor = 2,
}

/// <summary>An additional stored template frame (e.g. animation frame or hover/glow variant) on a <see cref="FindImageStep"/>.</summary>
public sealed class TemplateVariant
{
    public int Width { get; set; }
    public int Height { get; set; }
    public string Data { get; set; } = string.Empty;

    /// <summary>Optional key into <see cref="Macro.Assets"/> when serialized with deduplicated templates.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TemplateAssetId { get; set; }

    /// <summary>Creates a structural copy of this variant.</summary>
    public TemplateVariant Clone() => (TemplateVariant)MemberwiseClone();
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

    /// <summary>For PressKey / ClickAndPressKey actions: the Windows virtual-key code to press (default: Space 0x20).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int FoundVirtualKey { get; set; } = 0x20;

    /// <summary>For ClickAndTypeText action: the text to type after clicking the found target.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FoundTextToType { get; set; }

    /// <summary>For ScrollUp / ScrollDown actions: how many wheel notches to scroll (default: 3).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int FoundScrollClicks { get; set; } = 3;

    /// <summary>
    /// After a click / hold on what was found: move the cursor out of the search area, so a hover effect or the cursor
    /// itself doesn't cover the next thing to find (a game button that re-appears in the same spot). Off by default.
    /// </summary>
    public bool MoveCursorAwayAfterAction { get; set; }

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

    /// <summary>The custom search area as a rectangle (only used with <see cref="Vision.SearchScope.CustomArea"/>).</summary>
    [JsonIgnore]
    public Ocr.ScreenRect Region => new(RegionX, RegionY, RegionWidth, RegionHeight);

    // ---------------- Vision 2.0: Scenarios, Tracking, Predictive Click & Budget ----------------

    /// <summary>1-click beginner scenario preset (Custom = default).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public VisionScenario Scenario { get; set; }

    /// <summary>Speed vs accuracy search strategy (Balanced = default).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public VisionSearchStrategy Strategy { get; set; }

    /// <summary>Optional per-look time budget in ms (0 = unlimited; used with <see cref="VisionSearchStrategy.Deadline"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int TimeBudgetMs { get; set; }

    /// <summary>When true, tracks the target's 2D velocity across checks and searches a predicted ROI along its path first.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool TrackTarget { get; set; }

    /// <summary>When true, leads clicks on moving targets to where the target will be (<c>pos + velocity × leadMs</c>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool PredictiveClick { get; set; }

    /// <summary>How far ahead (ms) <see cref="PredictiveClick"/> leads a moving target (0 = default 60 ms when PredictiveClick is on).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int PredictiveLeadMs { get; set; }

    /// <summary>Effective predictive lead time in ms (0 when <see cref="PredictiveClick"/> is off).</summary>
    [JsonIgnore]
    public int EffectivePredictiveLeadMs => !PredictiveClick ? 0 : PredictiveLeadMs > 0 ? PredictiveLeadMs : Vision.TargetTracker.DefaultLeadMs;

    /// <summary>
    /// Temporal confirmation: requires the target to be found in at least this many consecutive checks before acting
    /// (0 or 1 = act on first hit; 2..5 = ignore 1-frame particle flashes).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ConfirmFrames { get; set; }

    /// <summary>Motion-aware search: when true, prioritizes regions that changed since the previous frame.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool OnlyWhereMoved { get; set; }

    // ---------------- Multi-Match Detection, Counting & Batch Actions ("Find One vs. Find All") ----------------

    /// <summary>Default pause (ms) between acting on each match when <see cref="TargetSelection"/> is <see cref="SearchMatchMode.AllMatches"/>.</summary>
    public const int DefaultBetweenMatchesDelayMs = 80;

    private int? _betweenMatchesDelayMs;

    /// <summary>
    /// Which match(es) to select when multiple occurrences exist on screen: Find One (First/Best), Find All (count &amp; act on all),
    /// Find N-th in reading order, Random match, or Nearest to cursor.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public SearchMatchMode TargetSelection { get; set; }

    /// <summary>
    /// Sweep order when <see cref="TargetSelection"/> is <see cref="SearchMatchMode.AllMatches"/> and an action is performed on each match.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public MultiMatchOrder MatchOrder { get; set; }

    /// <summary>
    /// 1-based match number to pick when <see cref="TargetSelection"/> is <see cref="SearchMatchMode.ReadingOrderNth"/>
    /// (e.g. 2 = pick the 2nd "nice" on screen). 0 or 1 = 1st match.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int TargetMatchIndex { get; set; }

    /// <summary>Effective 1-based match index for <see cref="SearchMatchMode.ReadingOrderNth"/> (at least 1).</summary>
    [JsonIgnore]
    public int EffectiveTargetMatchIndex => Math.Max(1, TargetMatchIndex);

    /// <summary>
    /// Minimum number of occurrences that must be present on screen at the same time for the step to count as "Found"
    /// (0 or 1 = at least 1 match; e.g. 5 = wait until at least 5 occurrences of "nice" are on screen).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int MinMatchCount { get; set; }

    /// <summary>Effective minimum match count required for detection (at least 1).</summary>
    [JsonIgnore]
    public int EffectiveMinMatchCount => Math.Max(1, MinMatchCount);

    /// <summary>
    /// Optional cap on how many matches <see cref="SearchMatchMode.AllMatches"/> acts on in one step (0 = act on all found matches).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int MaxMatchesToActOn { get; set; }

    /// <summary>
    /// Delay (ms) between acting on each match when <see cref="TargetSelection"/> is <see cref="SearchMatchMode.AllMatches"/>.
    /// Default is <see cref="DefaultBetweenMatchesDelayMs"/> (80 ms).
    /// </summary>
    [JsonIgnore]
    public int BetweenMatchesDelayMs
    {
        get => _betweenMatchesDelayMs ?? DefaultBetweenMatchesDelayMs;
        set => _betweenMatchesDelayMs = value == DefaultBetweenMatchesDelayMs ? null : Math.Max(0, value);
    }

    /// <summary>Serialized form of <see cref="BetweenMatchesDelayMs"/>; omitted when using the 80 ms default.</summary>
    [JsonPropertyName("betweenMatchesDelayMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BetweenMatchesDelayOverride
    {
        get => _betweenMatchesDelayMs;
        set => _betweenMatchesDelayMs = value;
    }

    /// <summary>
    /// Optional macro variable name (e.g. <c>nice_count</c>) that receives the exact number of matches found on screen
    /// every time this step checks the screen.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CountVariableName { get; set; }

    /// <summary>True when any Vision 2.0 property on this base search step is set to a non-default value (schema 18).</summary>
    [JsonIgnore]
    public bool UsesVision2BaseFeatures =>
        Scenario != VisionScenario.Custom
        || Strategy != VisionSearchStrategy.Balanced
        || TimeBudgetMs > 0
        || TrackTarget
        || PredictiveClick
        || PredictiveLeadMs > 0
        || ConfirmFrames > 1
        || OnlyWhereMoved
        || TargetSelection != SearchMatchMode.FirstOrBest
        || MatchOrder != MultiMatchOrder.ReadingOrder
        || TargetMatchIndex > 1
        || MinMatchCount > 1
        || MaxMatchesToActOn > 0
        || _betweenMatchesDelayMs is not null
        || !string.IsNullOrWhiteSpace(CountVariableName);

    /// <inheritdoc />
    public override MacroStep Clone()
    {
        var copy = (ScreenSearchStep)MemberwiseClone();
        copy.FailurePolicy = FailurePolicy?.Clone();
        return copy;
    }
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

    /// <summary>
    /// When true, rejects words that have punctuation attached (e.g. searching "nice" will reject "nice!" or "nice,")
    /// and disables look-alike character substitution so only exact characters match.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool StrictPunctuation { get; set; }

    /// <summary>Vision 2.0: when true, interprets <see cref="Text"/> as a regular expression or pipe pattern (e.g. <c>nice|great</c>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool UseRegexPattern { get; set; }

    /// <summary>Vision 2.0: image preprocessing applied before OCR (HighContrast, InvertLightOnDark, BinarizeOtsu).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public OcrPreprocessMode Preprocess { get; set; }

    /// <summary>
    /// Vision 2.0 "Find-Once-Then-Track": once OCR finds the word, crops its pixels into a visual template and tracks it at
    /// frame rate (1–3 ms) as it moves or shuffles on screen.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool TrackTextAsImage { get; set; }

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

    /// <summary>Optional key into <see cref="Macro.Assets"/> when serialized with deduplicated templates.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TemplateAssetId { get; set; }

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

    /// <summary>
    /// "Match across display scales &amp; sizes (75%–150%)": also checks resized copies of the image when 1:1 doesn't
    /// match, so a macro recorded at 100 % DPI still finds buttons on a 125 % or 150 % monitor (or resized game UI).
    /// Off by default; written only when enabled (schema 15).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AllowMultiScale { get; set; }

    /// <summary>Vision 2.0: detector algorithm (Template = default, EdgeShape, or Combined).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ImageDetectorKind Detector { get; set; }

    /// <summary>
    /// Vision 2.0 Partial Visibility ("Occlusion tolerance"): minimum percentage of the template's cells that must be
    /// visible (40–100%, or 0 = compare whole picture). Setting 65% lets the target match even when 35% of it is covered by
    /// game particles, floating damage numbers, or effects.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int MinVisiblePercent { get; set; }

    /// <summary>Vision 2.0: optional rotation range in degrees (0 = upright only; 1..45 = checks ±RotationRangeDeg).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int RotationRangeDeg { get; set; }

    /// <summary>Vision 2.0: optional extra template appearances (animation frames, glow states, learned samples).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<TemplateVariant>? AdditionalTemplates { get; set; }

    [JsonIgnore]
    public override StepKind Kind => StepKind.FindImage;

    /// <inheritdoc />
    public override MacroStep Clone()
    {
        var copy = (FindImageStep)base.Clone();
        if (AdditionalTemplates is not null)
        {
            var variants = new List<TemplateVariant>(AdditionalTemplates.Count);
            foreach (var variant in AdditionalTemplates)
            {
                if (variant is not null)
                {
                    variants.Add(variant.Clone());
                }
            }
            copy.AdditionalTemplates = variants;
        }
        return copy;
    }
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

    /// <summary>Vision 2.0: colour space used for comparison (Rgb = default, Hsv, or LabDeltaE).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ColorSpaceKind ColorSpace { get; set; }

    /// <summary>Vision 2.0: rule for choosing which matching colour object to return (Largest = default).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ColorSelectRule SelectRule { get; set; }

    /// <summary>Vision 2.0: optional maximum blob size in pixels (0 = unlimited; filters out large background walls).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int MaxPixels { get; set; }

    /// <summary>
    /// Vision 2.0: minimum shape compactness / circularity (0–100%, 0 = any shape). E.g. 35% keeps round/compact targets
    /// and rejects thin particle streaks or bars.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int MinCircularityPercent { get; set; }

    /// <summary>
    /// Vision 2.0: minimum bounding-box fill density (0–100%, 0 = any density). Rejects sparse scattered particle clouds.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int MinFillPercent { get; set; }

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

    /// <inheritdoc />
    public override MacroStep Clone()
    {
        var copy = (InputSequenceStep)MemberwiseClone();
        copy.Events = Events is null ? new List<InputEvent>() : new List<InputEvent>(Events);
        if (MouseSegments is not null)
        {
            var segments = new List<MouseSegment>(MouseSegments.Count);
            foreach (var segment in MouseSegments)
            {
                if (segment is not null)
                {
                    segments.Add(segment.Clone());
                }
            }
            copy.MouseSegments = segments;
        }
        if (ClickTargets is not null)
        {
            var targets = new List<SequenceClickTarget>(ClickTargets.Count);
            foreach (var target in ClickTargets)
            {
                if (target is not null)
                {
                    targets.Add(target.Clone());
                }
            }
            copy.ClickTargets = targets;
        }
        return copy;
    }
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

    /// <summary>Optional key into <see cref="Macro.Assets"/> when serialized with deduplicated snapshots.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SnapshotAssetId { get; set; }

    public int AnchorX { get; set; }

    public int AnchorY { get; set; }

    /// <summary>Optional Intent fingerprint for this click, preserved when breaking a recorded sequence into steps.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Targeting.TargetFingerprint? Target { get; set; }

    /// <summary>Creates a structural copy of this sequence click target.</summary>
    public SequenceClickTarget Clone()
    {
        var copy = (SequenceClickTarget)MemberwiseClone();
        copy.Target = Target?.Clone();
        return copy;
    }
}
