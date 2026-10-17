using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Vision;
using SmartMacro.Services.Input;
using SmartMacro.Services.Ocr;

namespace SmartMacro.Services.Vision;

/// <summary>A picture the user dragged a box around, and where it was on screen.</summary>
public sealed record CapturedTemplate(ScreenRect Bounds, BgraImage Image);

/// <summary>A colour the user clicked on, and where.</summary>
public sealed record PickedColor(int X, int Y, RgbColor Color);

/// <summary>A point on the screen in physical pixels (virtual-screen coordinates, the same ones mouse steps use).</summary>
public readonly record struct ScreenPoint(int X, int Y);

/// <summary>
/// Screen helpers for the Find image / Find color editors: picking areas, capturing the picture to find,
/// picking a colour, and testing a step without playing the macro. SmartMacro hides itself while it looks.
/// </summary>
public interface IVisionTools
{
    Task<ScreenRect?> PickRegionAsync();

    /// <summary>The user drags a box around a button / icon; returns its pixels (null if cancelled).</summary>
    Task<CapturedTemplate?> CaptureTemplateAsync();

    /// <summary>The user clicks a pixel; returns its colour (null if cancelled).</summary>
    Task<PickedColor?> PickColorAsync();

    /// <summary>
    /// SmartMacro hides and a crosshair overlay covers every monitor; the user clicks a spot and gets its exact
    /// position (null if cancelled with Esc / right-click). The click goes to the overlay, never to the app below.
    /// </summary>
    Task<ScreenPoint?> PickPositionAsync();

    /// <summary>Where the mouse cursor is right now (null if Windows doesn't say).</summary>
    ScreenPoint? GetCursorPosition();

    /// <summary>Looks for the step's image once, shows a preview with the matches, and describes the result.</summary>
    Task<string> TestImageAsync(FindImageStep step);

    /// <summary>Looks for the step's colour once, shows a preview with the patches, and describes the result.</summary>
    Task<string> TestColorAsync(FindColorStep step);

    /// <summary>
    /// Follows the colour as a moving marker inside the step's area for a few seconds and reports its positions
    /// (0–100 % along the area), the frame rate reached and the time per frame.
    /// </summary>
    Task<string> TrackColorAsync(FindColorStep step, TimeSpan duration);

    /// <summary>
    /// Feedback loop: captures the bar area and finds the marker / zone as fast as possible for a few seconds, sending
    /// no input, and reports the frame rate reached, capture and detection time, and a suggested captures-per-second.
    /// </summary>
    Task<string> MeasureFeedbackAsync(FeedbackLoopStep step, TimeSpan duration);

    /// <summary>
    /// Feedback loop dry run: runs the real loop (same sensor, same rules and limits) at the step's frame rate but
    /// sends NO input. A small always-on-top window shows the bar with the marker / zone marked and what the loop
    /// would do. Stop conditions aren't checked here.
    /// </summary>
    Task<string> PreviewFeedbackAsync(FeedbackLoopStep step, TimeSpan duration);

    /// <summary>
    /// Feedback loop "Test detection now": captures the bar once, finds the marker and the zone, and says where the
    /// marker is compared with the zone and what the loop would do right now (no input is sent).
    /// </summary>
    Task<string> TestFeedbackOnceAsync(FeedbackLoopStep step);

    /// <summary>Where the most recent test found its match (null when it found nothing).</summary>
    ScreenPoint? LastTestMatch { get; }

    /// <summary>Moves the mouse cursor to a spot so the user can see where a match is (nothing is clicked).</summary>
    void ShowSpot(ScreenPoint point);

    /// <summary>Turns a picture into something WPF can show (frozen, so any thread may use it).</summary>
    ImageSource ToImageSource(BgraImage image);
}

public sealed class VisionTools : IVisionTools
{
    // Time for the picker overlays to disappear from the screen before we capture it.
    private const int OverlayFadeMs = 150;

    // Tracking test: at most ~30 captures per second.
    private static readonly TimeSpan TrackFrameInterval = TimeSpan.FromMilliseconds(33);

    private static readonly RgbColor MatchColor = new(0x22, 0xC5, 0x5E);
    private static readonly RgbColor OtherColor = new(0xF5, 0x9E, 0x0B);
    private static readonly RgbColor ZoneOverlayColor = new(0x38, 0xBD, 0xF8);

    // Feedback preview: how often the live window's picture and text are refreshed.
    private static readonly TimeSpan LivePreviewRefresh = TimeSpan.FromMilliseconds(100);

    private readonly Window _mainWindow;
    private readonly GdiScreenCapture _capture;
    private readonly string _logsDirectory;
    private readonly IScreenLayout? _layout;
    private Window? _preview;
    private ScreenPoint? _lastMatch;

    public ScreenPoint? LastTestMatch => _lastMatch;

    public void ShowSpot(ScreenPoint point) => NativeMethods.SetCursorPos(point.X, point.Y);

    /// <param name="layout">Monitors and windows, so "Test detection now" searches the same place the step will.</param>
    public VisionTools(Window mainWindow, GdiScreenCapture capture, string logsDirectory, IScreenLayout? layout = null)
    {
        _mainWindow = mainWindow;
        _capture = capture;
        _logsDirectory = logsDirectory;
        _layout = layout;
    }

    /// <summary>
    /// The area a test searches, and how to name it ("Main Monitor", "the \"Roblox\" window"…), or an error to show
    /// when it can't be searched right now (the window isn't open).
    /// </summary>
    private (ScreenRect Area, string Where, string? Problem) TestArea(ScreenSearchStep step)
    {
        var where = step.EffectiveScope == SearchScope.CustomArea ? $"area {step.Region}" : SearchArea.ShortName(step);
        try
        {
            return (SearchArea.Resolve(step, _capture.GetVirtualScreen(), _layout), where, null);
        }
        catch (SearchAreaUnavailableException ex)
        {
            return (default, where, ex.Message);
        }
    }

    public Task<ScreenRect?> PickRegionAsync() => WhileHiddenAsync(() => RegionPicker.PickAsync(
        "Drag a box around the part of the screen to search. Smaller areas are faster and find fewer wrong things.   Esc or right-click to cancel."));

    public Task<CapturedTemplate?> CaptureTemplateAsync() => WhileHiddenAsync(async () =>
    {
        var picked = await RegionPicker.PickFrozenAsync(
            "Drag a box tightly around the button or icon to find. Include only the part that always looks the same.   Esc or right-click to cancel.");
        if (picked is null) return null;

        // Take the pixels from the frozen picture: exactly what you saw, even if the game has moved on since.
        if (FromFrozen(picked) is { } frozenPart) return (CapturedTemplate?)new CapturedTemplate(frozenPart.Bounds, frozenPart.Image);

        await Task.Delay(OverlayFadeMs);
        var frame = _capture.Capture(picked.Rect);
        return (CapturedTemplate?)new CapturedTemplate(frame.Bounds, frame.Image);
    });

    public Task<PickedColor?> PickColorAsync() => WhileHiddenAsync(async () =>
    {
        var picked = await RegionPicker.PickFrozenAsync(
            "Click the color to look for (for example the moving marker or a button). The magnifier shows the exact pixel.   Esc or right-click to cancel.", pickPoint: true);
        if (picked is null) return null;
        var point = picked.Rect;

        if (FromFrozen(picked) is { } frozenPixel)
            return (PickedColor?)new PickedColor(frozenPixel.Bounds.X, frozenPixel.Bounds.Y, frozenPixel.Image.GetPixel(0, 0));

        await Task.Delay(OverlayFadeMs);
        var frame = _capture.Capture(point);
        return (PickedColor?)new PickedColor(point.X, point.Y, frame.Image.GetPixel(0, 0));
    });

    /// <summary>The picked rectangle cut out of the frozen screenshot (clamped safely to the frozen image bounds).</summary>
    internal static ScreenFrame? FromFrozen(PickerResult picked) =>
        picked.Frozen?.CropFromScreen(picked.Rect);

    public Task<ScreenPoint?> PickPositionAsync() => WhileHiddenAsync(async () =>
    {
        var picked = await RegionPicker.PickAsync(
            "Click the spot for this step. The crosshair shows the exact X / Y.   Esc or right-click to cancel.", pickPoint: true);
        return picked is { } point ? new ScreenPoint(point.X, point.Y) : (ScreenPoint?)null;
    });

    public ScreenPoint? GetCursorPosition()
    {
        using var _ = NativeMethods.PhysicalPixelsScope.Enter();
        return NativeMethods.GetCursorPos(out var point) ? new ScreenPoint(point.X, point.Y) : null;
    }

    public async Task<string> TestImageAsync(FindImageStep step)
    {
        _lastMatch = null;
        if (!TemplateCodec.TryDecode(step.TemplateData, step.TemplateWidth, step.TemplateHeight, out var decoded, out var error))
            return error ?? "Capture the image first.";
        var template = decoded!;

        var (region, where, problem) = TestArea(step);
        if (problem is not null) return $"Can't test right now: {problem}";
        ClosePreview();
        _lastMatch = null;

        ScreenFrame frame;
        TimeSpan captureTime;
        try
        {
            (frame, captureTime) = await WhileInvisibleAsync(() => Task.FromResult(TimedCapture(region)));
        }
        catch (Exception ex)
        {
            return $"Test failed: {ex.Message}";
        }

        if (frame.Image.Width < template.Width || frame.Image.Height < template.Height)
            return $"The search area on screen ({frame.Bounds.Width}×{frame.Bounds.Height}) is smaller than the image ({template.Width}×{template.Height}). Make the area bigger.";

        var options = new TemplateMatchOptions { MinScore = step.MinSimilarityPercent / 100.0, IgnoreColor = step.IgnoreColor, IgnoreEdgeFraction = step.IgnoreEdgesPercent / 100.0, MatchForeground = step.MatchForeground, MaxResults = 10 };
        var watch = Stopwatch.StartNew();
        var result = await Task.Run(() => TemplateMatcher.Find(frame.Image, template, options));
        var searchTime = watch.Elapsed;

        var picture = frame.Image.Clone();
        for (var i = result.Matches.Count - 1; i >= 0; i--)
            picture.DrawRectangle(result.Matches[i].Bounds, i == 0 ? MatchColor : OtherColor, i == 0 ? 3 : 2);
        var saved = TrySave(picture, "last-image-test.png");

        var timing = $"Capture took {captureTime.TotalMilliseconds:0} ms, search {searchTime.TotalMilliseconds:0} ms{(result.Scale > 1 ? $" (coarse search at 1/{result.Scale} size, then exact)" : "")}.";
        string text;
        if (result.Best is { } best)
        {
            var bounds = frame.ToScreen(best.Bounds);
            var more = result.Matches.Count > 1
                ? $" {result.Matches.Count} matches (green = the one a macro uses, orange = others): " +
                  string.Join(", ", result.Matches.Skip(1).Select(m => $"{m.Score:P0} at ({frame.ToScreen(m.Bounds).CenterX}, {frame.ToScreen(m.Bounds).CenterY})")) + "."
                : "";
            _lastMatch = new ScreenPoint(bounds.CenterX, bounds.CenterY);
            text = $"Matched at ({bounds.CenterX}, {bounds.CenterY}) — {best.Score:P0} confidence (needs {step.MinSimilarityPercent}%). Searched {where}.{more}";
            ScreenHighlightOverlay.ShowResult(frame.Bounds, bounds, found: true, $"✓ {best.Score * 100:0}% Match");
        }
        else
        {
            ScreenHighlightOverlay.ShowResult(frame.Bounds, null, found: false, $"✗ Not found (best {result.BestScore * 100:0}%)");
            text = $"Not found in {where}. The best spot was only {result.BestScore:P0} similar (needs {step.MinSimilarityPercent}%).";
            if (result.RejectedForColor > 0)
                text += $" {result.RejectedForColor} spot(s) had the right shape but a different color; turn on \"Ignore color\" if the color changes.";
            if (result.BestScore >= 0.6 && result.RejectedForColor == 0)
                text += " If the right thing is on screen, lower the match accuracy a little (or capture the image again).";
            if (!step.MatchForeground && result.BestScore >= 0.5)
                text += " If you can see the game through the button (a different background shows inside it), turn on \"Ignore background\".";
        }

        text += " " + timing;
        if (saved is not null) text += $"\nPicture saved to: {saved}";
        ShowPreview("Find image – test result", picture, text);
        return text;
    }

    public async Task<string> TestColorAsync(FindColorStep step)
    {
        _lastMatch = null;
        if (!RgbColor.TryParse(step.Color, out var color)) return "Enter the color as #RRGGBB or pick it from the screen first.";

        var (region, where, problem) = TestArea(step);
        if (problem is not null) return $"Can't test right now: {problem}";
        ClosePreview();
        _lastMatch = null;

        ScreenFrame frame;
        TimeSpan captureTime;
        try
        {
            (frame, captureTime) = await WhileInvisibleAsync(() => Task.FromResult(TimedCapture(region)));
        }
        catch (Exception ex)
        {
            return $"Test failed: {ex.Message}";
        }

        var watch = Stopwatch.StartNew();
        var result = await Task.Run(() => ColorFinder.Find(frame.Image, color, step.Tolerance, step.MinPixels, maxResults: 10));
        var searchTime = watch.Elapsed;

        var picture = frame.Image.Clone();
        for (var i = result.Blobs.Count - 1; i >= 0; i--)
            picture.DrawRectangle(Grow(result.Blobs[i].Bounds, 2), i == 0 ? MatchColor : OtherColor, i == 0 ? 3 : 2);
        var saved = TrySave(picture, "last-color-test.png");

        string text;
        if (result.Largest is { } blob)
        {
            var x = frame.Bounds.X + (int)Math.Round(blob.CenterX);
            var y = frame.Bounds.Y + (int)Math.Round(blob.CenterY);
            _lastMatch = new ScreenPoint(x, y);
            var patch = new ScreenRect(frame.Bounds.X + blob.Bounds.X, frame.Bounds.Y + blob.Bounds.Y, blob.Bounds.Width, blob.Bounds.Height);
            ScreenHighlightOverlay.ShowResult(frame.Bounds, patch, found: true, $"✓ Color match · {blob.PixelCount:N0} px");
            text = $"Matched at ({x}, {y}) — color {color}, a patch of {blob.PixelCount:N0} pixels (green box). Searched {where}.";
            if (result.Blobs.Count > 1)
                text += $" There are {result.Blobs.Count} separate patches of this color (orange); a macro uses the biggest. If that's wrong, pick a smaller area, lower the tolerance or raise the minimum size.";
        }
        else
        {
            ScreenHighlightOverlay.ShowResult(frame.Bounds, null, found: false, "✗ Color not found");
            text = result.MatchingPixels > 0
                ? $"Not found: {result.MatchingPixels:N0} pixel(s) of that color are in {where}, but no patch has at least {step.MinPixels} pixels."
                : $"Not found: no pixels of {color} (± {step.Tolerance}) in {where}.";
        }

        text += $" Capture took {captureTime.TotalMilliseconds:0} ms, search {searchTime.TotalMilliseconds:0} ms.";
        if (saved is not null) text += $"\nPicture saved to: {saved}";
        ShowPreview("Find color – test result", picture, text);
        return text;
    }

    public async Task<string> TrackColorAsync(FindColorStep step, TimeSpan duration)
    {
        if (!RgbColor.TryParse(step.Color, out var color)) return "Enter the color as #RRGGBB or pick it from the screen first.";
        if (step.EffectiveScope != SearchScope.CustomArea)
            return "Tracking needs a search area: set \"Where to look\" to \"Custom Area on Screen\" and pick the bar or track the marker moves along.";

        var region = step.Region;
        var orientation = region.Width >= region.Height ? BarOrientation.Horizontal : BarOrientation.Vertical;
        ClosePreview();

        TrackSummary summary;
        try
        {
            summary = await WhileHiddenAsync(() => Task.Run(() => TrackAsync(region, color, step, orientation, duration)));
        }
        catch (Exception ex)
        {
            return $"Tracking failed: {ex.Message}";
        }

        var axis = orientation == BarOrientation.Horizontal ? "left (0 %) to right (100 %)" : "top (0 %) to bottom (100 %)";
        var text = $"Tracked for {summary.Elapsed.TotalSeconds:0.0} s: {summary.Frames} frames ({summary.Frames / Math.Max(0.001, summary.Elapsed.TotalSeconds):0.0} per second). " +
                   $"Marker found in {summary.Found}, unclear (two similar patches) in {summary.Ambiguous}, missing in {summary.Lost}, glitches ignored {summary.IgnoredJumps}. ";
        text += summary.Min is { } min && summary.Max is { } max
            ? $"Position along the area, {axis}: {min:0}% to {max:0}%. Samples: {string.Join(", ", summary.Samples.Select(p => p is { } v ? $"{v:0}" : "–"))}. "
            : "The marker was never found: pick its color again or raise the tolerance. ";
        text += $"Per frame: capture {summary.CaptureMs:0.0} ms, search {summary.SearchMs:0.0} ms on average.";
        text += " Real games add their own delay before the screen updates, so a feedback loop reacts a bit later than this.";

        if (summary.LastFrame is not null) ShowPreview("Find color – tracking (last frame)", summary.LastFrame, text);
        return text;
    }

    // ---------------- Feedback loop (Stage 4): Measure loop speed + Preview ----------------

    public async Task<string> MeasureFeedbackAsync(FeedbackLoopStep step, TimeSpan duration)
    {
        var copy = (FeedbackLoopStep)MacroJson.CloneStep(step); // the editor may change the step while this runs
        ClosePreview();

        MeasureSummary summary;
        try
        {
            summary = await WhileHiddenAsync(() => Task.Run(() => MeasureLoopAsync(copy, duration)));
        }
        catch (Exception ex)
        {
            return $"Measuring failed: {ex.Message}";
        }

        var stats = summary.Stats;
        var hz = stats.FramesPerSecond(summary.Elapsed);
        var suggested = SuggestCapturesPerSecond(hz);
        var text = $"Measured for {summary.Elapsed.TotalSeconds:0.0} s, sending no input: {stats.Frames:N0} frames, about {hz:0} frames per second at full speed for this {copy.RegionWidth}×{copy.RegionHeight} area. " +
                   $"Per frame: capture {stats.AverageCaptureMs:0.0} ms + detection {stats.AverageDetectMs:0.0} ms on average; 95% of frames took at most {summary.P95Ms:0.0} ms, the slowest {stats.SlowestFrame.TotalMilliseconds:0} ms. " +
                   $"Marker found in {Share(stats.MarkerFound, stats.Frames)} of frames, unclear (two similar patches) in {Share(stats.MarkerUnclear, stats.Frames)}, missing in {Share(stats.MarkerMissing, stats.Frames)}";
        if (copy.ZoneMode == FeedbackZoneMode.DetectedByColor) text += $"; zone missing in {Share(stats.ZoneMissing, stats.Frames)}";
        text += $". Suggested \"captures per second\": {suggested} (leaves time for the game itself). This step is set to {copy.MaxCapturesPerSecond}.";
        if (copy.MaxCapturesPerSecond > hz) text += " That's more than this PC reached, so the loop will simply run as fast as it can.";
        text += " Real reactions are slower than this: the game also needs time to draw the screen and to react to input.";

        if (summary.LastFrame is not null) ShowPreview("Feedback loop – measured (last frame)", DrawFeedbackOverlay(summary.LastFrame, copy), text);
        return text;
    }

    public async Task<string> PreviewFeedbackAsync(FeedbackLoopStep step, TimeSpan duration)
    {
        var copy = (FeedbackLoopStep)MacroJson.CloneStep(step);
        ClosePreview();

        PreviewSummary summary;
        try
        {
            summary = await WhileHiddenAsync(async () =>
            {
                using var stop = new CancellationTokenSource();
                var live = new LiveFeedbackWindow(stop.Cancel);
                live.Show();
                try
                {
                    return await Task.Run(() => PreviewLoopAsync(copy, duration, live, stop.Token));
                }
                finally
                {
                    live.Close();
                }
            });
        }
        catch (Exception ex)
        {
            return $"Preview failed: {ex.Message}";
        }

        var stats = summary.Stats;
        var input = copy.InputKind == FeedbackInputKind.MouseButton ? $"the {copy.MouseButton.ToString().ToLowerInvariant()} mouse button" : Core.Input.VirtualKeys.GetName(copy.VirtualKey);
        var text = $"Preview ({(summary.Stopped ? "stopped" : "finished")}) – NO input was sent. {stats.Describe(summary.Elapsed, summary.FrameInterval)} " +
                   $"With input on, it would have pressed {input} {stats.Presses} time(s), released it {stats.Releases} time(s) and tapped it {stats.Taps} time(s); it would have been held for {Share(summary.HeldFrames, stats.Frames)} of the frames.";
        if (summary.LoopEnds.Count > 0)
            text += $" The real loop would have ENDED at {summary.LoopEnds[0]} (target lost; {DescribeLostAction(copy.WhenTargetLost)}).";
        if (summary.Elapsed.TotalMilliseconds >= copy.MaxRuntimeMs)
            text += $" Its max runtime ({copy.MaxRuntimeMs / 1000.0:0.#} s) would have ended it at that point.";
        text += " \"Done when\" / \"failed when\" conditions aren't checked in the preview.";

        if (summary.LastFrame is not null) ShowPreview("Feedback loop – preview (last frame)", DrawFeedbackOverlay(summary.LastFrame, copy), text);
        return text;
    }

    public async Task<string> TestFeedbackOnceAsync(FeedbackLoopStep step)
    {
        var copy = (FeedbackLoopStep)MacroJson.CloneStep(step);
        ClosePreview();
        _lastMatch = null;

        FeedbackFrame frame;
        try
        {
            var sensor = new FeedbackSensor(copy, _capture);
            frame = await WhileInvisibleAsync(() => Task.Run(() => sensor.ReadAsync(CancellationToken.None)));
        }
        catch (Exception ex)
        {
            return $"Test failed: {ex.Message}";
        }

        var horizontal = copy.Orientation == BarOrientation.Horizontal;
        var marker = frame.Marker;
        string text;
        if (marker.Bounds is { } local)
        {
            var spot = frame.Frame.ToScreen(local);
            _lastMatch = new ScreenPoint(spot.CenterX, spot.CenterY);
            ScreenHighlightOverlay.ShowResult(frame.Frame.Bounds, spot, found: marker.Status == MarkerStatus.Found,
                marker.Status == MarkerStatus.Found ? $"✓ Marker at {marker.PositionPercent:0}%" : "? Marker unclear");
        }
        else
        {
            ScreenHighlightOverlay.ShowResult(frame.Frame.Bounds, null, found: false, "✗ Marker not found");
        }

        text = marker.Status switch
        {
            MarkerStatus.Found => $"Marker found at {marker.PositionPercent:0} % along the bar ({(horizontal ? "0 % = left" : "0 % = top")}).",
            MarkerStatus.Ambiguous => $"Marker unclear: {marker.Candidates} patches of the marker color. Lower the tolerance, raise the minimum size or pick a tighter area.",
            _ => "Marker NOT found: no patch of the marker color in the bar area. Pick its color again or raise the tolerance.",
        };
        text += frame.Zone.Found
            ? $" Zone: {frame.Zone.StartPercent:0}–{frame.Zone.EndPercent:0} %{(copy.ZoneMode == FeedbackZoneMode.DetectedByColor ? " (found by its color)" : " (fixed)")}."
            : " Zone NOT found: no patch of the zone color. Pick it again or raise its tolerance.";

        if (frame.Observation.IsTrusted)
        {
            var region = FeedbackController.Classify(frame.Observation.MarkerPercent!.Value, frame.Observation.ZoneStartPercent,
                frame.Observation.ZoneEndPercent, null, 0);
            var place = region switch
            {
                FeedbackRegion.Before => horizontal ? "LEFT of the zone" : "ABOVE the zone",
                FeedbackRegion.After => horizontal ? "RIGHT of the zone" : "BELOW the zone",
                _ => "INSIDE the zone",
            };
            var action = copy.ActionFor(region);
            var input = copy.InputKind == FeedbackInputKind.MouseButton ? $"the {copy.MouseButton.ToString().ToLowerInvariant()} button" : Core.Input.VirtualKeys.GetName(copy.VirtualKey);
            var would = action switch
            {
                FeedbackAction.Hold => $"HOLD {input}",
                FeedbackAction.Release => $"RELEASE {input}",
                FeedbackAction.Tap => $"TAP {input} when the marker moves in here",
                FeedbackAction.Pulse => $"PULSE {input}, holding about {FeedbackController.PulseDuty(frame.Observation.MarkerPercent!.Value, frame.Observation.ZoneStartPercent, frame.Observation.ZoneEndPercent, copy.WhenBeforeZone, copy.WhenAfterZone):P0} of the time",
                _ => "change nothing",
            };
            text += $" The marker is {place}, so the loop would {would}{(copy.FinishWhenInZone && region == FeedbackRegion.Inside ? " and then finish" : "")}. No input was sent.";
        }
        else
        {
            text += " This frame can't be trusted, so the loop wouldn't change anything.";
        }

        text += $" Capture {frame.CaptureTime.TotalMilliseconds:0.0} ms, detection {frame.DetectTime.TotalMilliseconds:0.0} ms.";
        ShowPreview("Feedback loop – detection test", DrawFeedbackOverlay(frame, copy), text);
        return text;
    }

    private sealed record MeasureSummary(TimeSpan Elapsed, FeedbackStats Stats, double P95Ms, FeedbackFrame? LastFrame);

    private sealed record PreviewSummary(TimeSpan Elapsed, TimeSpan FrameInterval, FeedbackStats Stats, int HeldFrames,
        IReadOnlyList<string> LoopEnds, bool Stopped, FeedbackFrame? LastFrame);

    /// <summary>Capture + detect back to back (no rate limit, no input) to see what this PC can do for this area.</summary>
    private async Task<MeasureSummary> MeasureLoopAsync(FeedbackLoopStep step, TimeSpan duration)
    {
        var sensor = new FeedbackSensor(step, _capture);
        var stats = new FeedbackStats();
        var times = new List<double>();
        FeedbackFrame? last = null;
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < duration)
        {
            var frame = await sensor.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            stats.Add(frame);
            times.Add((frame.CaptureTime + frame.DetectTime).TotalMilliseconds);
            last = frame;
        }

        times.Sort();
        var p95 = times.Count == 0 ? 0 : times[Math.Min(times.Count - 1, (int)Math.Ceiling(times.Count * 0.95) - 1)];
        return new MeasureSummary(watch.Elapsed, stats, p95, last);
    }

    /// <summary>
    /// The real loop logic (sensor, controller, rate limit) without input: counts what it would do and keeps the live
    /// window updated a few times per second.
    /// </summary>
    private async Task<PreviewSummary> PreviewLoopAsync(FeedbackLoopStep step, TimeSpan duration, LiveFeedbackWindow live, CancellationToken token)
    {
        using var timerResolution = new HighResolutionTimerScope(); // 1 ms timer, like during playback
        var sensor = new FeedbackSensor(step, _capture);
        var rules = FeedbackRules.From(step);
        var controller = new FeedbackController(rules);
        var stats = new FeedbackStats();
        var interval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / Math.Clamp(step.MaxCapturesPerSecond, 1, Core.Validation.MacroValidator.MaxCapturesPerSecond));
        var loopEnds = new List<string>();
        var heldFrames = 0;
        var waiting = false;
        var nextRefresh = TimeSpan.Zero;
        FeedbackFrame? last = null;
        var watch = Stopwatch.StartNew();

        while (watch.Elapsed < duration && !token.IsCancellationRequested)
        {
            var start = watch.Elapsed;
            var frame = await sensor.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            stats.Add(frame);
            last = frame;

            var decision = controller.Update(frame.Observation, start);
            if (decision.Deferred) stats.Deferred++;
            switch (decision.Command)
            {
                case FeedbackCommand.Press: stats.Presses++; break;
                case FeedbackCommand.Release: stats.Releases++; break;
                case FeedbackCommand.Tap: stats.Taps++; break;
            }

            if (controller.IsWaiting && !waiting) stats.LostEpisodes++;
            waiting = controller.IsWaiting;
            if (controller.IsHeld) heldFrames++;

            if (decision.TargetLost)
            {
                // The real loop would end here. Note it, and start over so the rest of the preview still shows something.
                loopEnds.Add($"{start.TotalSeconds:0.0} s");
                controller = new FeedbackController(rules, start);
            }

            if (start >= nextRefresh)
            {
                live.Post(ToImageSource(DrawFeedbackOverlay(frame, step)), DescribeLiveFrame(frame, decision, controller, stats, watch.Elapsed));
                nextRefresh = start + LivePreviewRefresh;
            }

            var rest = interval - (watch.Elapsed - start);
            if (rest > TimeSpan.Zero)
            {
                try { await Task.Delay(rest, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        return new PreviewSummary(watch.Elapsed, interval, stats, heldFrames, loopEnds, token.IsCancellationRequested, last);
    }

    /// <summary>One or two lines for the live window: where the marker and zone are and what the loop would do.</summary>
    private static string DescribeLiveFrame(FeedbackFrame frame, FeedbackDecision decision, FeedbackController controller, FeedbackStats stats, TimeSpan elapsed)
    {
        var marker = frame.Marker.Status switch
        {
            MarkerStatus.Found => $"marker {frame.Marker.PositionPercent:0} %",
            MarkerStatus.Ambiguous => "marker UNCLEAR (two similar patches)",
            MarkerStatus.IgnoredJump => "marker jump ignored",
            _ => "marker MISSING",
        };
        var zone = frame.Zone.Found ? $"zone {frame.Zone.StartPercent:0}–{frame.Zone.EndPercent:0} %" : "zone MISSING";
        var where = decision.Region switch
        {
            FeedbackRegion.Before => "before the zone",
            FeedbackRegion.Inside => "inside the zone",
            FeedbackRegion.After => "after the zone",
            _ => controller.IsWaiting ? "target lost, waiting" : "no change (frame not trusted)",
        };
        var action = decision.Command switch
        {
            FeedbackCommand.Press => "→ would PRESS",
            FeedbackCommand.Release => "→ would RELEASE",
            FeedbackCommand.Tap => "→ would TAP",
            _ => decision.Deferred ? "→ change held back (min time between changes)" : "",
        };
        var state = controller.IsHeld ? "HELD" : "released";
        return $"{marker} · {zone} · {where} {action}\nInput: {state} · {stats.FramesPerSecond(elapsed):0} fps · capture {stats.AverageCaptureMs:0.0} ms · detect {stats.AverageDetectMs:0.0} ms · NO input is sent";
    }

    private static string DescribeLostAction(FeedbackLostAction action) => action switch
    {
        FeedbackLostAction.ExitLoop => "set to continue with the next step",
        FeedbackLostAction.StopMacro => "set to stop the macro",
        _ => "set to release and wait",
    };

    /// <summary>About 60 % of what the PC reached at full speed, between 5 and 60 per second.</summary>
    private static int SuggestCapturesPerSecond(double fullSpeedHz) => Math.Clamp((int)Math.Floor(fullSpeedHz * 0.6), 5, 60);

    private static string Share(int count, int total) => total <= 0 ? "0%" : $"{count / (double)total:P0}";

    /// <summary>The captured bar with the zone (blue box) and the marker (green = found, orange = unclear) marked.</summary>
    private static BgraImage DrawFeedbackOverlay(FeedbackFrame frame, FeedbackLoopStep step)
    {
        var picture = frame.Frame.Image.Clone();
        var horizontal = step.Orientation == BarOrientation.Horizontal;
        var length = horizontal ? picture.Width : picture.Height;

        if (frame.Zone.Found && length > 1)
        {
            var a = (int)Math.Round(frame.Zone.StartPercent / 100.0 * (length - 1));
            var b = (int)Math.Round(frame.Zone.EndPercent / 100.0 * (length - 1));
            var zone = horizontal ? new ScreenRect(a, 0, b - a + 1, picture.Height) : new ScreenRect(0, a, picture.Width, b - a + 1);
            picture.DrawRectangle(zone, ZoneOverlayColor, 1);
        }

        if (frame.Marker.Bounds is { } marker)
            picture.DrawRectangle(Grow(marker, 2), frame.Marker.Status == MarkerStatus.Found ? MatchColor : OtherColor, 2);
        return picture;
    }

    /// <summary>
    /// A small always-on-top window for the feedback preview: the bar picture, what the loop sees / would do, and a
    /// Stop button. Not owned by the main window (that one is minimized while the preview runs), never takes the focus
    /// from the game, and is left out of screen captures so it can't confuse the detection.
    /// </summary>
    private sealed class LiveFeedbackWindow
    {
        private readonly Window _window;
        private readonly Image _image;
        private readonly TextBlock _text;
        private bool _closed;

        public LiveFeedbackWindow(Action stop)
        {
            _image = new Image { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both, Height = 90 };
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);
            _text = new TextBlock
            {
                Text = "Starting…",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xEB, 0xF5)),
                FontSize = 12,
                Margin = new Thickness(0, 8, 0, 8),
            };
            var stopButton = new Button { Content = "Stop preview", Padding = new Thickness(14, 4, 14, 4), HorizontalAlignment = HorizontalAlignment.Right };
            stopButton.Click += (_, _) => stop();

            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(new TextBlock
            {
                Text = "Feedback loop preview – no input is sent",
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xEB, 0xF5)),
                Margin = new Thickness(0, 0, 0, 8),
            });
            panel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x05, 0x0A, 0x14)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x32, 0x4A)),
                BorderThickness = new Thickness(1),
                Child = _image,
            });
            panel.Children.Add(_text);
            panel.Children.Add(stopButton);

            var area = SystemParameters.WorkArea;
            _window = new Window
            {
                Title = "SmartMacro – feedback preview",
                Width = 460,
                SizeToContent = SizeToContent.Height,
                WindowStyle = WindowStyle.ToolWindow,
                ResizeMode = ResizeMode.NoResize,
                Topmost = true,
                ShowActivated = false,
                ShowInTaskbar = false,
                Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x16, 0x26)),
                Content = panel,
                Left = area.Right - 480,
                Top = area.Bottom - 260,
            };
            _window.Closed += (_, _) =>
            {
                _closed = true;
                stop(); // closing the window also stops the preview
            };
        }

        public void Show()
        {
            _window.Show();
            NativeMethods.SetExcludedFromCapture(_window, true);
        }

        public void Close()
        {
            if (!_closed) _window.Close();
        }

        /// <summary>Called from the preview thread; the window is updated on its own (UI) thread.</summary>
        public void Post(ImageSource picture, string text)
        {
            if (_closed) return;
            _window.Dispatcher.InvokeAsync(() =>
            {
                if (_closed) return;
                _image.Source = picture;
                _text.Text = text;
            });
        }
    }

    public ImageSource ToImageSource(BgraImage image)
    {
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgr32, null, image.Pixels, image.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    private sealed record TrackSummary(TimeSpan Elapsed, int Frames, int Found, int Ambiguous, int Lost, int IgnoredJumps,
        double? Min, double? Max, IReadOnlyList<double?> Samples, double CaptureMs, double SearchMs, BgraImage? LastFrame);

    private async Task<TrackSummary> TrackAsync(ScreenRect region, RgbColor color, FindColorStep step, BarOrientation orientation, TimeSpan duration)
    {
        var tracker = new MarkerTracker(color, step.Tolerance, orientation, Math.Max(1, step.MinPixels));
        var watch = Stopwatch.StartNew();
        int frames = 0, found = 0, ambiguous = 0, lost = 0, ignored = 0;
        double? min = null, max = null;
        var samples = new List<double?>();
        var nextSample = TimeSpan.Zero;
        double captureMs = 0, searchMs = 0;
        BgraImage? last = null;
        MarkerReading? lastReading = null;

        while (watch.Elapsed < duration)
        {
            var t0 = watch.Elapsed;
            var frame = _capture.Capture(region);
            var t1 = watch.Elapsed;
            var reading = tracker.Update(frame.Image);
            var t2 = watch.Elapsed;

            frames++;
            captureMs += (t1 - t0).TotalMilliseconds;
            searchMs += (t2 - t1).TotalMilliseconds;
            switch (reading.Status)
            {
                case MarkerStatus.Found: found++; break;
                case MarkerStatus.Ambiguous: ambiguous++; break;
                case MarkerStatus.Lost: lost++; break;
                case MarkerStatus.IgnoredJump: ignored++; break;
            }

            if (reading.Status == MarkerStatus.Found && reading.PositionPercent is { } position)
            {
                min = min is null ? position : Math.Min(min.Value, position);
                max = max is null ? position : Math.Max(max.Value, position);
            }

            // One sample every 0.25 s for the summary (so the user can see the movement).
            if (t0 >= nextSample && samples.Count < 24)
            {
                samples.Add(reading.Status == MarkerStatus.Found ? reading.PositionPercent : null);
                nextSample += TimeSpan.FromMilliseconds(250);
            }

            last = frame.Image;
            lastReading = reading;

            var wait = TrackFrameInterval - (watch.Elapsed - t0);
            if (wait > TimeSpan.Zero) await Task.Delay(wait);
        }

        if (last is not null && lastReading is { Bounds: { } box } final)
        {
            last = last.Clone();
            last.DrawRectangle(Grow(box, 2), final.Status == MarkerStatus.Found ? MatchColor : OtherColor, 2);
        }

        var count = Math.Max(1, frames);
        return new TrackSummary(watch.Elapsed, frames, found, ambiguous, lost, ignored, min, max, samples,
            captureMs / count, searchMs / count, last);
    }

    private (ScreenFrame Frame, TimeSpan Time) TimedCapture(ScreenRect region)
    {
        var watch = Stopwatch.StartNew();
        var frame = _capture.Capture(region);
        return (frame, watch.Elapsed);
    }

    private static ScreenRect Grow(ScreenRect rect, int by) =>
        new(rect.X - by, rect.Y - by, rect.Width + by * 2, rect.Height + by * 2);

    /// <summary>Saves a PNG in the Logs folder (overwritten each test). Returns the path, or null if it couldn't be saved.</summary>
    private string? TrySave(BgraImage image, string fileName)
    {
        try
        {
            var path = Path.Combine(_logsDirectory, fileName);
            Directory.CreateDirectory(_logsDirectory);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create((BitmapSource)ToImageSource(image)));
            using var file = File.Create(path);
            encoder.Save(file);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private void ClosePreview()
    {
        _preview?.Close();
        _preview = null;
    }

    /// <summary>A simple window with the captured picture (matches marked) and the result text.</summary>
    private void ShowPreview(string title, BgraImage picture, string text)
    {
        ClosePreview();

        var close = new Button { Content = "Close", Padding = new Thickness(18, 6, 18, 6), HorizontalAlignment = HorizontalAlignment.Right, IsCancel = true };
        var info = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xEB, 0xF5)),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 10),
        };
        var image = new Image { Source = ToImageSource(picture), Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);

        var bottom = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        bottom.Children.Add(info);
        bottom.Children.Add(close);
        DockPanel.SetDock(bottom, Dock.Bottom);

        var panel = new DockPanel { Margin = new Thickness(14) };
        panel.Children.Add(bottom);
        panel.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x05, 0x0A, 0x14)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x32, 0x4A)),
            BorderThickness = new Thickness(1),
            Child = image,
        });

        var window = new Window
        {
            Title = title,
            Width = 900,
            Height = 640,
            MinWidth = 400,
            MinHeight = 300,
            Owner = _mainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x16, 0x26)),
            ShowInTaskbar = false,
            Content = panel,
        };
        close.Click += (_, _) => window.Close();
        window.Closed += (_, _) => { if (ReferenceEquals(_preview, window)) _preview = null; };
        _preview = window;
        window.Show();
    }

    /// <summary>How long SmartMacro stays invisible before a "Test detection now" screenshot.</summary>
    public const int TestHideMs = 150;

    /// <summary>
    /// For "Test detection now": SmartMacro (and its open tool windows) turn fully see-through and are excluded from
    /// capture for <see cref="TestHideMs"/>, the screenshot is taken, and everything is visible again before the
    /// result window opens. Unlike minimizing there is no animation and the app behind doesn't get focus (so a game's
    /// hover state isn't disturbed), and the editor's own picture of the image can never be what is found.
    /// </summary>
    private async Task<T> WhileInvisibleAsync<T>(Func<Task<T>> action)
    {
        var hidden = new List<(Window Window, long Style)>();
        foreach (Window window in Application.Current.Windows)
        {
            if (!window.IsVisible || (!ReferenceEquals(window, _mainWindow) && !ReferenceEquals(window.Owner, _mainWindow))) continue;
            NativeMethods.SetExcludedFromCapture(window, true);
            if (NativeMethods.MakeInvisible(window) is { } style) hidden.Add((window, style));
        }

        try
        {
            await Task.Delay(TestHideMs); // let the desktop compositor show what's behind
            return await action();
        }
        finally
        {
            foreach (var (window, style) in hidden)
            {
                NativeMethods.RestoreWindowStyle(window, style);
                NativeMethods.SetExcludedFromCapture(window, false);
            }
        }
    }

    private async Task<T> WhileHiddenAsync<T>(Func<Task<T>> action)
    {
        var previousState = _mainWindow.WindowState;
        NativeMethods.SetExcludedFromCapture(_mainWindow, true);
        _mainWindow.WindowState = WindowState.Minimized;
        try
        {
            await Task.Delay(350); // let the minimize animation finish
            return await action();
        }
        finally
        {
            NativeMethods.SetExcludedFromCapture(_mainWindow, false);
            _mainWindow.WindowState = previousState == WindowState.Minimized ? WindowState.Normal : previousState;
            _mainWindow.Activate();
        }
    }
}
