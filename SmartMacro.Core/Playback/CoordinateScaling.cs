using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Vision;
using SmartMacro.Core.Workflow;

namespace SmartMacro.Core.Playback;

/// <summary>
/// Maps a position from the rectangle the macro was made in (<see cref="From"/>: the target window or the main monitor
/// when the positions were picked) to where that rectangle is now (<see cref="To"/>). Moving the window shifts every
/// point; a different window / screen size scales it proportionally (1920×1080 → 2560×1440 = ×1.333).
/// </summary>
public readonly record struct CoordinateMapping(ScreenRect From, ScreenRect To)
{
    /// <summary>Nothing moves (same place, same size).</summary>
    public bool IsIdentity => From == To;

    public double ScaleX => From.Width > 0 ? (double)To.Width / From.Width : 1.0;

    public double ScaleY => From.Height > 0 ? (double)To.Height / From.Height : 1.0;

    /// <summary>Maps one point (rounded to the nearest pixel).</summary>
    public (int X, int Y) Map(int x, int y) =>
        ((int)Math.Round(To.X + (x - (double)From.X) * ScaleX), (int)Math.Round(To.Y + (y - (double)From.Y) * ScaleY));

    /// <summary>Maps a rectangle (its corners), keeping it at least 1×1.</summary>
    public ScreenRect MapRect(ScreenRect rect)
    {
        var (left, top) = Map(rect.X, rect.Y);
        var (right, bottom) = Map(rect.Right, rect.Bottom);
        return new ScreenRect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    public override string ToString() =>
        $"{From.Width}×{From.Height} at ({From.X}, {From.Y}) → {To.Width}×{To.Height} at ({To.X}, {To.Y})";
}

/// <summary>
/// "Auto-adjust coordinates if the window moves or the screen resolution changes" (<see cref="Macro.ScaleCoordinatesToWindow"/>).
/// The playback engine works out where the target is right now and plays a scaled copy of the macro, so the stored
/// steps never change. What gets adjusted: Move mouse / Mouse click / Scroll positions (and drag ends), custom search
/// areas of Find steps and conditions, and feedback loop bar areas. Not adjusted: recorded input (it is played exactly
/// as recorded) and the pictures of Find image steps (a different resolution may need a new capture).
/// </summary>
public static class CoordinateScaling
{
    /// <summary>The rectangle the positions were made in, from the macro's reference settings.</summary>
    public static ScreenRect ReferenceOf(Macro macro) =>
        new(macro.ReferenceLeft, macro.ReferenceTop, macro.ReferenceWidth, macro.ReferenceHeight);

    /// <summary>
    /// Where the target is right now: the window whose title matches <see cref="Macro.TargetWindowTitle"/> (exact title
    /// first, then "contains"), or the main monitor when no window is set. Throws
    /// <see cref="SearchAreaUnavailableException"/> with a plain-English reason when it can't be found.
    /// </summary>
    public static ScreenRect CurrentTarget(Macro macro, IScreenLayout? layout)
    {
        ArgumentNullException.ThrowIfNull(macro);
        var title = macro.TargetWindowTitle?.Trim();
        if (layout is null)
            throw new SearchAreaUnavailableException("The window / monitor list isn't available, so the macro's coordinates can't be adjusted.");

        if (!string.IsNullOrEmpty(title))
        {
            var window = SearchArea.FindWindow(layout.GetWindows(), title)
                         ?? throw new SearchAreaUnavailableException(
                             $"The target window \"{title}\" isn't open, so the macro's positions can't be adjusted to it. Open it (or turn off \"Auto-adjust coordinates\").");
            if (window.IsMinimized || window.Bounds.IsEmpty)
                throw new SearchAreaUnavailableException($"The target window \"{window.Title}\" is minimized. Restore it before playing.");
            return window.Bounds;
        }

        var monitors = layout.GetMonitors();
        var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault()
                      ?? throw new SearchAreaUnavailableException("No monitor was found, so the macro's coordinates can't be adjusted.");
        return primary.Bounds;
    }

    /// <summary>The mapping for this macro right now, or null when scaling is off (or the reference isn't set).</summary>
    public static CoordinateMapping? MappingFor(Macro macro, IScreenLayout? layout)
    {
        ArgumentNullException.ThrowIfNull(macro);
        if (!macro.ScaleCoordinatesToWindow) return null;
        var reference = ReferenceOf(macro);
        if (reference.IsEmpty) return null;
        return new CoordinateMapping(reference, CurrentTarget(macro, layout));
    }

    /// <summary>A copy of the steps with every stored position mapped (same step IDs, so the editor can follow along).</summary>
    public static List<MacroStep> ApplyToCopy(IReadOnlyList<MacroStep> steps, CoordinateMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var copy = steps.Select(MacroJson.CloneStep).ToList();
        ApplyInPlace(copy, mapping);
        return copy;
    }

    /// <summary>Maps every stored position of these steps (changes them).</summary>
    public static void ApplyInPlace(IEnumerable<MacroStep> steps, CoordinateMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (mapping.IsIdentity) return;

        foreach (var step in steps)
        {
            switch (step)
            {
                case MouseMoveStep m:
                    (m.X, m.Y) = mapping.Map(m.X, m.Y);
                    break;
                case MouseClickStep c:
                    (c.X, c.Y) = mapping.Map(c.X, c.Y);
                    (c.EndX, c.EndY) = mapping.Map(c.EndX, c.EndY);
                    break;
                case MouseScrollStep s:
                    (s.X, s.Y) = mapping.Map(s.X, s.Y);
                    break;
                case FeedbackLoopStep f:
                {
                    var bar = mapping.MapRect(new ScreenRect(f.RegionX, f.RegionY, f.RegionWidth, f.RegionHeight));
                    (f.RegionX, f.RegionY, f.RegionWidth, f.RegionHeight) = (bar.X, bar.Y, bar.Width, bar.Height);
                    break;
                }
                case WaitForChangeStep w:
                {
                    var watched = mapping.MapRect(w.Region);
                    (w.RegionX, w.RegionY, w.RegionWidth, w.RegionHeight) = (watched.X, watched.Y, watched.Width, watched.Height);
                    break;
                }
                case ScreenshotStep { Area: ScreenshotArea.CustomArea } s:
                {
                    var shot = mapping.MapRect(s.Region);
                    (s.RegionX, s.RegionY, s.RegionWidth, s.RegionHeight) = (shot.X, shot.Y, shot.Width, shot.Height);
                    break;
                }
            }

            // Find steps and the searches of If / Repeat / feedback conditions: only a custom area is a stored position
            // (monitors and windows are found again at playback anyway).
            foreach (var search in WorkflowSteps.ScreenSearches(step))
            {
                if (search.EffectiveScope != SearchScope.CustomArea) continue;
                var area = mapping.MapRect(search.Region);
                (search.RegionX, search.RegionY, search.RegionWidth, search.RegionHeight) = (area.X, area.Y, area.Width, area.Height);
            }
        }
    }
}
