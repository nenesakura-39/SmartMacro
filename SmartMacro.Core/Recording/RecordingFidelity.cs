using SmartMacro.Core.Validation;

namespace SmartMacro.Core.Recording;

/// <summary>
/// How much of the recorded input becomes editable steps (the selector next to the Record button). Applies to the
/// editable steps of Smart and Desktop recordings; Game mode and "Exact 1:1 / rhythm" (one Recorded input step) always
/// keep every event anyway.
/// </summary>
public enum RecordingFidelity
{
    /// <summary>
    /// 🎯 100% Exact 1:1 Replay (default): mouse movement sampled every 8 ms (125 Hz) whenever the cursor moved at least
    /// 1 px, full drag paths, the exact hold of every key and click, explicit Down / Up for overlapping keys, and every
    /// gap of 1 ms or more kept as a Delay (shorter ones are carried into the next one, so nothing is lost).
    /// </summary>
    Exact1To1,

    /// <summary>
    /// ✨ Smart Optimized: straight stretches of mouse movement are simplified (Ramer–Douglas–Peucker, 3 px), but the spot
    /// before every press / release, every direction reversal, rest and drag path, and every hold time are kept.
    /// </summary>
    SmartOptimized,

    /// <summary>
    /// 🖱️ Clicks &amp; Keys Only: clicks (they carry their own position), drags, key presses, wheel turns and delays. Cursor
    /// wandering while no button is held isn't kept, except the one spot the cursor rested on before a key press.
    /// </summary>
    ClicksAndKeysOnly,
}

/// <summary>
/// 3D game camera / cursor-locked games: when cursor movement in the editable steps is recorded as RELATIVE mouse deltas
/// (Move mouse steps with <see cref="Models.MouseMoveStep.UseRelativeDelta"/>) instead of absolute positions.
/// </summary>
public enum RelativeMouseMode
{
    /// <summary>Always absolute positions (the behaviour before relative deltas existed).</summary>
    Off,

    /// <summary>
    /// While the right or middle button is held (the usual "hold right-click to turn the camera"): the movement is played
    /// as relative deltas and the release happens where the cursor is. Left-button drags (sliders, selections) stay
    /// absolute, because the exact spot matters there and Windows' pointer acceleration would bend relative moves.
    /// </summary>
    CameraDrags,

    /// <summary>
    /// "Use relative mouse deltas for 3D camera": every recorded movement is relative, and clicks / presses / releases /
    /// wheel happen where the cursor is (shift-lock, first-person and other cursor-locked games).
    /// </summary>
    Always,
}

/// <summary>The recording settings behind each <see cref="RecordingFidelity"/> choice.</summary>
public static class RecordingFidelityPresets
{
    /// <summary>Exact 1:1: one movement sample every 8 ms = 125 Hz.</summary>
    public const int ExactMoveIntervalMs = 8;

    /// <summary>Exact 1:1: gaps of 1 ms or more get their own Delay (shorter ones are carried into the next one).</summary>
    public const int ExactMinDelayMs = 1;

    /// <summary>Smart Optimized: how far (px) the simplified path may stray from the recorded one.</summary>
    public const int OptimizedPathTolerancePx = 3;

    /// <summary>
    /// <paramref name="options"/> with the preset's values (movement sampling, simplification, holds, delays). Everything
    /// else (shortcuts, start position, relative mode...) is kept. No preset = the options as they are.
    /// </summary>
    public static RecordingOptions Apply(RecordingOptions options, RecordingFidelity? fidelity)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (fidelity is not { } f) return options;

        return new RecordingOptions
        {
            // The preset decides these:
            RecordMouseMoves = true,
            KeepHoldTime = true,
            MaxTapDurationMs = MacroValidator.MaxHoldMs,
            SimplifyMousePath = f != RecordingFidelity.Exact1To1,
            CollapseMousePaths = f == RecordingFidelity.ClicksAndKeysOnly,
            MinMoveIntervalMs = f == RecordingFidelity.Exact1To1 ? ExactMoveIntervalMs : options.MinMoveIntervalMs,
            MinDelayMs = f == RecordingFidelity.Exact1To1 ? ExactMinDelayMs : options.MinDelayMs,
            PathTolerancePx = f == RecordingFidelity.Exact1To1 ? options.PathTolerancePx : OptimizedPathTolerancePx,

            // Kept from the caller:
            RestMs = options.RestMs,
            MaxClickDistance = options.MaxClickDistance,
            IgnoredHotkeys = options.IgnoredHotkeys,
            RelativeMouse = options.RelativeMouse,
            StartX = options.StartX,
            StartY = options.StartY,
            TrimTrailingKeys = options.TrimTrailingKeys,
        };
    }

    /// <summary>The label shown in the selectors.</summary>
    public static string Label(RecordingFidelity fidelity) => fidelity switch
    {
        RecordingFidelity.Exact1To1 => "🎯 100% Exact 1:1 Replay",
        RecordingFidelity.SmartOptimized => "✨ Smart Optimized",
        RecordingFidelity.ClicksAndKeysOnly => "🖱️ Clicks & Keys Only",
        _ => fidelity.ToString(),
    };

    /// <summary>One-line explanation for tooltips / Settings.</summary>
    public static string Help(RecordingFidelity fidelity) => fidelity switch
    {
        RecordingFidelity.Exact1To1 => "Every mouse movement (125 Hz, 1 px), full drags, the exact hold of every key and click, overlapping keys as separate Down / Up. Best for precise macros and long AFK loops.",
        RecordingFidelity.SmartOptimized => "Simplifies straight mouse paths (fewer Move steps) but keeps the spot before every click, drag paths and all hold times.",
        RecordingFidelity.ClicksAndKeysOnly => "Only clicks, drags, key presses, wheel turns and delays; cursor wandering between them isn't kept.",
        _ => string.Empty,
    };
}
