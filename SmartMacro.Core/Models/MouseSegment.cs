using System.Text.Json.Serialization;

namespace SmartMacro.Core.Models;

/// <summary>How mouse movement in one part of a smart recording is played back.</summary>
public enum MouseMovementMode
{
    /// <summary>Put the cursor on the recorded screen position (for menus, buttons, normal apps).</summary>
    CursorPosition,

    /// <summary>Send the recorded relative mouse counts (for camera turning while a game locks the cursor).</summary>
    Relative,
}

/// <summary>What the user picked for a part of the recording. Automatic = use what SmartMacro detected.</summary>
public enum MouseModeChoice
{
    Automatic,
    CursorPosition,
    Relative,
}

/// <summary>Why SmartMacro picked the mode it detected (shown in the editor).</summary>
public enum MouseContextEvidence
{
    /// <summary>Not enough movement to tell; the mode was carried over from nearby movement.</summary>
    Unclear,

    /// <summary>The cursor followed the mouse around the screen.</summary>
    FreeCursor,

    /// <summary>The cursor was hidden (games hide it while the mouse turns the camera).</summary>
    HiddenCursor,

    /// <summary>The cursor was confined to a tiny area (a game locking the mouse with ClipCursor).</summary>
    ConfinedCursor,

    /// <summary>The mouse moved but the cursor stayed in (or kept snapping back to) the same spot.</summary>
    LockedCursor,
}

/// <summary>
/// One part of a smart recording's timeline that plays mouse movement the same way. Segments are sorted by
/// StartUs; each one lasts until the next one starts (the first one starts at 0). Recorded events always keep
/// both the relative counts and the cursor position, so the user can switch a segment's mode without re-recording.
/// Exactly one representation is played per event: never a position and a relative move for the same movement.
/// </summary>
public sealed class MouseSegment
{
    /// <summary>Microseconds from the start of the step.</summary>
    public long StartUs { get; set; }

    /// <summary>What SmartMacro detected while recording.</summary>
    public MouseMovementMode Detected { get; set; }

    public MouseContextEvidence Evidence { get; set; } = MouseContextEvidence.Unclear;

    /// <summary>The user's override (Automatic = use <see cref="Detected"/>).</summary>
    public MouseModeChoice Choice { get; set; } = MouseModeChoice.Automatic;

    /// <summary>The mode playback actually uses.</summary>
    [JsonIgnore]
    public MouseMovementMode Effective => Choice switch
    {
        MouseModeChoice.CursorPosition => MouseMovementMode.CursorPosition,
        MouseModeChoice.Relative => MouseMovementMode.Relative,
        _ => Detected,
    };

    public MouseSegment Clone() => (MouseSegment)MemberwiseClone();
}
