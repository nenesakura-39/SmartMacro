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
    /// <summary>Search the whole (virtual) screen instead of a specific area.</summary>
    public bool SearchFullScreen { get; set; } = true;

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

    public TextNotFoundBehavior WhenNotFound { get; set; } = TextNotFoundBehavior.StopMacro;

    /// <summary>The search area as a rectangle (ignored when <see cref="SearchFullScreen"/> is on).</summary>
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

    public TextMatchMode MatchMode { get; set; } = TextMatchMode.Contains;
    public bool CaseSensitive { get; set; }

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

    /// <summary>Allowed difference per red / green / blue channel (0 = exact, 255 = anything).</summary>
    public int Tolerance { get; set; } = 24;

    /// <summary>The patch must have at least this many pixels (ignores stray single pixels).</summary>
    public int MinPixels { get; set; } = 10;

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

    [JsonIgnore]
    public override StepKind Kind => StepKind.InputSequence;
}
