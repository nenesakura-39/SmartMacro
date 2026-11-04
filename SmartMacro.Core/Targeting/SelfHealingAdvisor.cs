using SmartMacro.Core.Models;

namespace SmartMacro.Core.Targeting;

/// <summary>
/// A non-destructive post-run recommendation generated when Smart Intent had to relocate a click significantly or fall
/// back past a failed higher-priority strategy. Never applied silently — only when the user approves it.
/// </summary>
public sealed record SelfHealSuggestion(
    Guid StepId,
    int StepNumber,
    LocatorStrategyKind WinningStrategy,
    int OldX,
    int OldY,
    int NewX,
    int NewY,
    string Description)
{
    /// <summary>Minimum pixel shift before suggesting a coordinate update on a successful relocation.</summary>
    public const int MinShiftPixels = 25;

    /// <summary>
    /// Applies this suggestion to the matching <see cref="MouseClickStep"/> in <paramref name="macro"/>.
    /// Returns true if the step was found and updated.
    /// </summary>
    public bool ApplyTo(Macro macro)
    {
        ArgumentNullException.ThrowIfNull(macro);
        var step = macro.Steps.OfType<MouseClickStep>().FirstOrDefault(s => s.Id == StepId);
        return step is not null && ApplyTo(step);
    }

    /// <summary>
    /// Applies this suggestion directly to <paramref name="step"/> when its ID matches <see cref="StepId"/>.
    /// </summary>
    public bool ApplyTo(MouseClickStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (step.Id != StepId) return false;

        var dx = NewX - step.X;
        var dy = NewY - step.Y;
        step.X = NewX;
        step.Y = NewY;

        if (step.Target is { WindowRelativeX: { } wx, WindowRelativeY: { } wy, WindowWidth: > 0, WindowHeight: > 0 } target
            && WinningStrategy is LocatorStrategyKind.UiAutomation or LocatorStrategyKind.OcrText or LocatorStrategyKind.ImageSnapshot)
        {
            var nextWx = wx + dx;
            var nextWy = wy + dy;
            if (nextWx >= 0 && nextWx <= target.WindowWidth && nextWy >= 0 && nextWy <= target.WindowHeight)
            {
                target.WindowRelativeX = nextWx;
                target.WindowRelativeY = nextWy;
            }
        }

        return true;
    }

    public static SelfHealSuggestion? TryCreate(
        MouseClickStep step,
        int stepNumber,
        TargetResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(resolution);
        if (resolution.WinningStrategy == LocatorStrategyKind.Coordinates) return null;

        var shiftX = resolution.X - step.X;
        var shiftY = resolution.Y - step.Y;
        var distance = Math.Sqrt((double)shiftX * shiftX + (double)shiftY * shiftY);
        var higherTierFailed = resolution.Diagnostics.Any(d =>
            !d.Succeeded && d.Strategy < resolution.WinningStrategy
            && ((d.Strategy == LocatorStrategyKind.UiAutomation && step.Target is { HasUiElement: true })
                || (d.Strategy == LocatorStrategyKind.OcrText && step.Target is { HasOcrText: true })));

        if (distance < MinShiftPixels && !higherTierFailed) return null;

        var label = step.Target?.ShortLabel is { Length: > 0 } s ? $" ({s})" : "";
        var strategyName = resolution.WinningStrategy switch
        {
            LocatorStrategyKind.UiAutomation => "UI Automation",
            LocatorStrategyKind.OcrText => "OCR Text",
            LocatorStrategyKind.ImageSnapshot => "Image Snapshot",
            LocatorStrategyKind.WindowRelative => "Window-Relative",
            _ => "Coordinates",
        };

        var desc = distance >= MinShiftPixels
            ? $"Step {stepNumber}{label} moved by ({shiftX:+0;-0;0}, {shiftY:+0;-0;0}) px → relocated to ({resolution.X}, {resolution.Y}) via {strategyName}."
            : $"Step {stepNumber}{label} fell back to {strategyName} at ({resolution.X}, {resolution.Y}).";

        return new SelfHealSuggestion(
            step.Id,
            stepNumber,
            resolution.WinningStrategy,
            step.X,
            step.Y,
            resolution.X,
            resolution.Y,
            desc);
    }
}
