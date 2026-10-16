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
    public string Text { get; set; } = string.Empty;
    public int CharacterDelayMs { get; set; }

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

public enum FoundTextAction
{
    None,
    Move,
    Click,
    DoubleClick,
    RightClick,
}

public enum TextNotFoundBehavior
{
    StopMacro,
    Continue,
}

/// <summary>
/// Uses OCR to look for text on the screen, waiting up to a timeout, and optionally
/// moves to / clicks on it. Later Mouse click steps can also click "the last found text".
/// </summary>
public sealed class FindTextStep : MacroStep
{
    public string Text { get; set; } = string.Empty;

    /// <summary>Search the whole (virtual) screen instead of a specific area.</summary>
    public bool SearchFullScreen { get; set; } = true;

    public int RegionX { get; set; }
    public int RegionY { get; set; }
    public int RegionWidth { get; set; } = 400;
    public int RegionHeight { get; set; } = 200;

    public TextMatchMode MatchMode { get; set; } = TextMatchMode.Contains;
    public bool CaseSensitive { get; set; }

    /// <summary>How close the OCR text must be to the search text (50-100). Lower = more forgiving of OCR mistakes.</summary>
    public int MinSimilarityPercent { get; set; } = 85;

    /// <summary>How long to keep looking. 0 = look once.</summary>
    public int TimeoutMs { get; set; } = 10_000;

    /// <summary>Pause between screen checks while waiting.</summary>
    public int CheckIntervalMs { get; set; } = 500;

    public FoundTextAction Action { get; set; } = FoundTextAction.None;

    /// <summary>Offset from the center of the found text for Move / Click.</summary>
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }

    public TextNotFoundBehavior WhenNotFound { get; set; } = TextNotFoundBehavior.StopMacro;

    [JsonIgnore]
    public override StepKind Kind => StepKind.FindText;
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
