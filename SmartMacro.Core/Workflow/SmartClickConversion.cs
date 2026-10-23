using SmartMacro.Core.Models;
using SmartMacro.Core.Vision;

namespace SmartMacro.Core.Workflow;

/// <summary>
/// Smart Click Snapshots in the editor and the engine: reading a click step's recorded thumbnail, and the 1-click
/// "🔍 Convert step to full 'Find image &amp; click'".
/// </summary>
public static class SmartClickConversion
{
    /// <summary>
    /// The search whose "found spot" a step at <paramref name="index"/> would use for "the last found spot": the nearest
    /// enabled Find text / image / color step (waiting for it to appear) or If / Repeat condition with "is on screen"
    /// above it. Null when there is none. Used by "Pick offset from screen" to find the reference target.
    /// </summary>
    public static ScreenSearchStep? LastFoundReference(IReadOnlyList<MacroStep> steps, int index)
    {
        ArgumentNullException.ThrowIfNull(steps);
        for (var i = Math.Min(index, steps.Count) - 1; i >= 0; i--)
        {
            switch (steps[i])
            {
                case ScreenSearchStep { Enabled: true, Goal: WaitGoal.Appear } search:
                    return search;
                case IfStep { Enabled: true, Condition: { Negate: false, Search: { } check } }:
                    return check;
                case RepeatStep { Enabled: true, Mode: not RepeatMode.Times, Condition: { Negate: false, Search: { } check } }:
                    return check;
            }
        }

        return null;
    }

    /// <summary>How long the converted Find image step waits for its picture.</summary>
    public const int ConvertedWaitMs = 5_000;

    /// <summary>The click step's thumbnail as pixels, or null with the reason (no thumbnail / damaged).</summary>
    public static BgraImage? TryDecodeThumbnail(MouseClickStep step, out string? error)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (!step.HasTargetThumbnail)
        {
            error = "This click has no recorded picture.";
            return null;
        }

        return PngCodec.TryDecodeBase64(step.TargetThumbnailBase64, out var image, out error) ? image : null;
    }

    /// <summary>
    /// Where inside the thumbnail the click was (px from its top-left corner). The middle when the step doesn't say, and
    /// always inside the picture.
    /// </summary>
    public static (int X, int Y) Anchor(MouseClickStep step, int width, int height) =>
        (Math.Clamp(step.TargetAnchorX ?? width / 2, 0, Math.Max(0, width - 1)),
         Math.Clamp(step.TargetAnchorY ?? height / 2, 0, Math.Max(0, height - 1)));

    /// <summary>True for the click actions a Find image step can do on what it finds (Click / Double click / Hold).</summary>
    public static bool HasConvertibleAction(MouseClickStep step) =>
        step.Action is MouseClickAction.Click or MouseClickAction.DoubleClick or MouseClickAction.Hold;

    /// <summary>
    /// Null when the step can be converted, otherwise a plain sentence why not (no picture, a damaged picture, a
    /// press / release / drag, which a Find image step can't do).
    /// </summary>
    public static string? WhyNotConvertible(MouseClickStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (!step.HasTargetThumbnail) return "This click has no recorded picture (only recorded clicks have one).";
        if (!HasConvertibleAction(step)) return "Only a single click, double click or hold can become a \"Find image & click\" step.";
        if (TryDecodeThumbnail(step, out var error) is null) return $"The recorded picture can't be used: {error}";
        return null;
    }

    /// <summary>
    /// Builds the "Find image &amp; click" step that replaces <paramref name="step"/>: its recorded picture is the image to
    /// find (anywhere on any monitor, waiting up to 5 s), and when found it does the same click on the same spot of the
    /// picture (the offset keeps an off-centre click off-centre). Right button → right-click; Hold → hold for the same
    /// time. Comment, Enabled, movement style and glide time carry over; the new step gets a new ID.
    /// Throws <see cref="InvalidOperationException"/> with the reason when it can't be converted.
    /// </summary>
    public static FindImageStep ToFindImageStep(MouseClickStep step)
    {
        if (WhyNotConvertible(step) is { } problem) throw new InvalidOperationException(problem);
        var image = TryDecodeThumbnail(step, out _)!;

        var (anchorX, anchorY) = Anchor(step, image.Width, image.Height);
        var action = step.Action switch
        {
            MouseClickAction.DoubleClick => FoundTextAction.DoubleClick,
            MouseClickAction.Hold => FoundTextAction.Hold,
            _ when step.Button == MacroMouseButton.Right => FoundTextAction.RightClick,
            _ => FoundTextAction.Click,
        };

        var accuracy = (int)Math.Round(Math.Clamp(step.SmartAimAccuracy, 0.5, 1.0) * 100);
        var find = new FindImageStep
        {
            Enabled = step.Enabled,
            Comment = step.Comment,
            TemplateWidth = image.Width,
            TemplateHeight = image.Height,
            TemplateData = TemplateCodec.Encode(image),
            MinSimilarityPercent = accuracy,
            Scope = SearchScope.AllMonitors,
            TimeoutMs = ConvertedWaitMs,
            Goal = WaitGoal.Appear,
            Action = action,
            // A Find step clicks the match's center (X + W/2, Y + H/2); the offset moves that onto the recorded spot.
            OffsetX = anchorX - image.Width / 2,
            OffsetY = anchorY - image.Height / 2,
            WhenNotFound = TextNotFoundBehavior.StopMacro,
            MoveStyle = step.MoveStyle,
            MoveDurationMs = step.MoveDurationMs,
        };

        if (step.HoldMs > 0 && step.Action != MouseClickAction.Hold) find.ClickHoldMs = Math.Max(step.HoldMs, MouseMotionDefaults.ClickHoldMs);
        if (step.Action == MouseClickAction.Hold) find.ActionHoldMs = Math.Max(1, step.HoldMs);
        if (step.Action == MouseClickAction.DoubleClick && step.DoubleClickIntervalMs > 0) find.DoubleClickIntervalMs = step.DoubleClickIntervalMs;
        if (step.PostActionDelayMs > 0) find.PostActionDelayMs = step.PostActionDelayMs;
        return find;
    }
}
