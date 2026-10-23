using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Vision;

namespace SmartMacro.Core.Workflow;

/// <summary>
/// What Smart-Aim found: the spot to click (the recorded click moved by the same shift as the picture), the matched
/// rectangle on screen, how similar it was, and how far it moved from where it was recorded.
/// </summary>
public sealed record SmartAimHit(int X, int Y, ScreenRect Bounds, double Score, int ShiftX, int ShiftY)
{
    public bool Moved => ShiftX != 0 || ShiftY != 0;
}

/// <summary>
/// The looking part of Smart-Aim (Smart Click Snapshots), shared by playback and the editor's "🎯 Test Smart-Aim" button,
/// so the test finds exactly what a run would. Pure: works on a captured <see cref="ScreenFrame"/>.
/// </summary>
public static class SmartAim
{
    /// <summary>Largest Smart-Aim search distance (px each way).</summary>
    public const int MaxRadiusPx = 2_000;

    /// <summary>Where the thumbnail's top-left corner was when the click was recorded.</summary>
    public static (int X, int Y) ExpectedTopLeft(MouseClickStep c, BgraImage template)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(template);
        var (anchorX, anchorY) = SmartClickConversion.Anchor(c, template.Width, template.Height);
        return (c.X - anchorX, c.Y - anchorY);
    }

    /// <summary>The search radius actually used (the step's, clamped to 0…<see cref="MaxRadiusPx"/>).</summary>
    public static int RadiusOf(MouseClickStep c) => Math.Clamp(c.SmartAimRadiusPx, 0, MaxRadiusPx);

    /// <summary>The screen area Smart-Aim looks at: the thumbnail's recorded rectangle grown by the radius on every side.</summary>
    public static ScreenRect SearchArea(MouseClickStep c, BgraImage template)
    {
        var (x, y) = ExpectedTopLeft(c, template);
        var radius = RadiusOf(c);
        return new ScreenRect(x - radius, y - radius, template.Width + 2 * radius, template.Height + 2 * radius);
    }

    /// <summary>Matcher settings: the step's accuracy (50–100 %), up to 10 candidates (look-alikes).</summary>
    public static TemplateMatchOptions OptionsFor(MouseClickStep c) => new()
    {
        MinScore = Math.Clamp(c.SmartAimAccuracy, 0.5, 1.0),
        MaxResults = 10,
    };

    /// <summary>
    /// Looks for the click's picture in <paramref name="frame"/>. When several copies match (identical grid cells), the
    /// one nearest the recorded spot wins. Returns the hit (null = not found) and the best score seen (for "best 62 %").
    /// </summary>
    public static (SmartAimHit? Hit, double BestScore) Locate(MouseClickStep c, BgraImage template, ScreenFrame frame,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Image.Width < template.Width || frame.Image.Height < template.Height) return (null, 0);

        var result = TemplateMatcher.Find(frame.Image, template, OptionsFor(c), token);
        if (result.Matches.Count == 0) return (null, result.BestScore);

        var (expectedX, expectedY) = ExpectedTopLeft(c, template);
        VisualMatch? best = null;
        var bestDistance = long.MaxValue;
        foreach (var match in result.Matches)
        {
            var bounds = frame.ToScreen(match.Bounds);
            long dx = bounds.X - expectedX, dy = bounds.Y - expectedY;
            var distance = dx * dx + dy * dy;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = match;
            }
        }

        var found = frame.ToScreen(best!.Bounds);
        var shiftX = found.X - expectedX;
        var shiftY = found.Y - expectedY;
        return (new SmartAimHit(c.X + shiftX, c.Y + shiftY, found, best.Score, shiftX, shiftY), result.BestScore);
    }
}
