using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Vision;

namespace SmartMacro.Core.Playback;

/// <summary>
/// Result of one look at the screen. Found = the text / image / colour is there. X / Y is the spot to act on
/// (screen pixels). FoundText describes the find for the log; MissText (starting with a space) explains a miss,
/// e.g. what OCR read instead. Area is the screen rectangle that was searched (empty when it couldn't be searched).
/// Match is the rectangle of what was found (empty on a miss). Confidence is how sure the find is (0..1: the image's
/// similarity, the text's match; 1 for a colour patch).
/// </summary>
public readonly record struct ScreenProbe(bool Found, int X, int Y, string FoundText, string MissText, ScreenRect Area = default, ScreenRect Match = default,
    double Confidence = 1.0)
{
    public static ScreenProbe Miss(string missText, ScreenRect area = default) => new(false, 0, 0, string.Empty, missText, area);
}

/// <summary>
/// A Find text / image / colour search that is ready to look at the screen (the template is decoded, the area is
/// known). <see cref="LookAsync"/> looks exactly once. <see cref="What"/> names it for messages.
/// </summary>
public sealed class PreparedSearch
{
    private readonly Func<DetectionTimer?, CancellationToken, Task<ScreenProbe>> _look;

    internal PreparedSearch(string what, bool isTimed, Func<DetectionTimer?, CancellationToken, Task<ScreenProbe>> look)
    {
        What = what;
        IsTimed = isTimed;
        _look = look;
    }

    /// <summary>E.g. "\"OK\"", "The image", "Color #FF0000".</summary>
    public string What { get; }

    /// <summary>True for image / colour searches, whose capture and search times are measured.</summary>
    public bool IsTimed { get; }

    /// <summary>A timer for one step's worth of looks, or null for OCR (which isn't timed).</summary>
    public DetectionTimer? NewTimer() => IsTimed ? new DetectionTimer() : null;

    /// <summary>Looks at the screen once. Adds the capture / search time to <paramref name="timer"/> if given.</summary>
    public Task<ScreenProbe> LookAsync(DetectionTimer? timer, CancellationToken cancellationToken) => _look(timer, cancellationToken);
}

/// <summary>
/// The one place that knows how to look for text, an image or a colour on the screen. Used by the Find text /
/// image / colour steps (through the engine's wait loop) and by If / Repeat conditions, so both always detect
/// things in exactly the same way.
/// </summary>
public sealed class ScreenProbes
{
    private readonly IOcrService? _ocr;
    private readonly IScreenCapture? _capture;
    private readonly IPlaybackClock _clock;
    private readonly IScreenLayout? _layout;

    /// <param name="layout">Monitors and windows, for "where to look" = a monitor / an app window (null: main monitor = whole desktop).</param>
    public ScreenProbes(IOcrService? ocr, IScreenCapture? capture, IPlaybackClock? clock = null, IScreenLayout? layout = null)
    {
        _ocr = ocr;
        _capture = capture;
        _clock = clock ?? new SystemPlaybackClock();
        _layout = layout;
    }

    /// <summary>
    /// The area to search right now (monitors / windows are looked up on every look, because a window can move), or null
    /// with the reason when it can't be searched at the moment (the window isn't open, the monitor isn't connected).
    /// </summary>
    private (ScreenRect Area, string? Problem) ResolveArea(ScreenSearchStep step, ScreenRect virtualScreen)
    {
        try
        {
            return (SearchArea.Resolve(step, virtualScreen, _layout), null);
        }
        catch (SearchAreaUnavailableException ex)
        {
            return (default, " " + ex.Message);
        }
    }

    /// <summary>
    /// Gets a search ready. Throws <see cref="OcrUnavailableException"/> / <see cref="ScreenCaptureUnavailableException"/>
    /// when the needed service is missing, and <see cref="InvalidOperationException"/> for a damaged image or colour.
    /// </summary>
    public PreparedSearch Prepare(ScreenSearchStep step) => step switch
    {
        FindTextStep text => PrepareText(text),
        FindImageStep image => PrepareImage(image),
        FindColorStep color => PrepareColor(color),
        _ => throw new NotSupportedException($"{step.GetType().Name} can't look at the screen."),
    };

    private PreparedSearch PrepareText(FindTextStep step)
    {
        var ocr = _ocr;
        if (ocr is null || !ocr.IsAvailable)
            throw new OcrUnavailableException(ocr?.StatusText ?? "Text recognition (OCR) isn't available.");

        return new PreparedSearch($"\"{step.Text}\"", isTimed: false, async (_, t) =>
        {
            var (region, problem) = ResolveArea(step, ocr.GetVirtualScreen());
            if (problem is not null) return ScreenProbe.Miss(problem);
            var read = await ocr.ReadAsync(region, t).ConfigureAwait(false);
            var match = OcrTextMatcher.Find(read, step.Text, step.MatchMode, step.CaseSensitive, step.MinSimilarityPercent / 100.0,
                step.NormalizeLookAlikes)
                .FirstOrDefault();
            return match is null
                ? ScreenProbe.Miss(DescribeLastRead(read.FullText), region)
                : new ScreenProbe(true, match.Bounds.CenterX, match.Bounds.CenterY,
                    $"Found \"{match.Text}\" at ({match.Bounds.CenterX}, {match.Bounds.CenterY}), {match.Similarity:P0} match.", string.Empty, region, match.Bounds,
                    match.Similarity);
        });
    }

    private PreparedSearch PrepareImage(FindImageStep step)
    {
        var capture = RequireCapture();
        if (!TemplateCodec.TryDecode(step.TemplateData, step.TemplateWidth, step.TemplateHeight, out var decoded, out var error))
            throw new InvalidOperationException($"The image of this step can't be used: {error}");
        var template = decoded!;
        var options = ImageMatchOptions(step);

        return new PreparedSearch("The image", isTimed: true, async (timer, t) =>
        {
            var (region, problem) = ResolveArea(step, capture.GetVirtualScreen());
            if (problem is not null) return ScreenProbe.Miss(problem);
            var t0 = _clock.Elapsed;
            var frame = await capture.CaptureAsync(region, t).ConfigureAwait(false);
            var t1 = _clock.Elapsed;
            var result = TemplateMatcher.Find(frame.Image, template, options, t);
            timer?.Add(t1 - t0, _clock.Elapsed - t1);

            if (frame.Image.Width < template.Width || frame.Image.Height < template.Height)
                return ScreenProbe.Miss(" The search area (on screen) is smaller than the image.", region);

            if (result.Best is not { } best)
            {
                var miss = $" The best similarity was {result.BestScore:P0} (needs {step.MinSimilarityPercent}%).";
                if (result.RejectedForColor > 0)
                    miss += $" {result.RejectedForColor} spot(s) had the right shape but a different color (turn on \"Ignore color\" if the color changes).";
                if (result.BestScore >= 0.5 && !step.MatchForeground)
                    miss += " If you can see the game through the button (or the background around it changes), turn on \"Ignore background\""
                            + (step.IgnoreEdgesPercent == 0 ? $" or \"Ignore the edges\" ({FindImageStep.RecommendedIgnoreEdgesPercent} %)." : ".");
                return ScreenProbe.Miss(miss, region);
            }

            var bounds = frame.ToScreen(best.Bounds);
            var more = result.Matches.Count > 1 ? $" ({result.Matches.Count} matches; the best one is used)" : "";
            return new ScreenProbe(true, bounds.CenterX, bounds.CenterY,
                $"Found the image at ({bounds.CenterX}, {bounds.CenterY}), {best.Score:P0} match{more}.", string.Empty, region, bounds, best.Score);
        });
    }

    /// <summary>
    /// The matcher settings for a Find image step or an image condition (If / Repeat / feedback loop). The editor's
    /// "Test detection now" uses this too, so a test, a Find image step and an If condition with the same settings
    /// always search in exactly the same way (same accuracy, colour, edge, background and lighting options).
    /// </summary>
    public static TemplateMatchOptions ImageMatchOptions(FindImageStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return new TemplateMatchOptions
        {
            MinScore = step.MinSimilarityPercent / 100.0,
            IgnoreColor = step.IgnoreColor,
            IgnoreEdgeFraction = step.IgnoreEdgesPercent / 100.0,
            MatchForeground = step.MatchForeground,
            AdaptToLighting = step.AdaptToLighting,
            MaxResults = 10,
        };
    }

    private PreparedSearch PrepareColor(FindColorStep step)
    {
        var capture = RequireCapture();
        if (!RgbColor.TryParse(step.Color, out var color))
            throw new InvalidOperationException($"\"{step.Color}\" is not a valid color. Use #RRGGBB.");
        RgbColor? secondaryColor = null;
        if (step.UseSecondaryColor)
        {
            var secText = string.IsNullOrWhiteSpace(step.SecondaryColor) ? "#FFFFFF" : step.SecondaryColor;
            if (!RgbColor.TryParse(secText, out var parsedSecondary))
                throw new InvalidOperationException($"\"{step.SecondaryColor}\" is not a valid second color. Use #RRGGBB.");
            secondaryColor = parsedSecondary;
        }

        var what = secondaryColor is { } sec ? $"Color {color} or {sec}" : $"Color {color}";
        return new PreparedSearch(what, isTimed: true, async (timer, t) =>
        {
            var (region, problem) = ResolveArea(step, capture.GetVirtualScreen());
            if (problem is not null) return ScreenProbe.Miss(problem);
            var t0 = _clock.Elapsed;
            var frame = await capture.CaptureAsync(region, t).ConfigureAwait(false);
            var t1 = _clock.Elapsed;
            var result = ColorFinder.Find(frame.Image, color, secondaryColor, step.Tolerance, step.MinPixels, maxResults: 20, t);
            timer?.Add(t1 - t0, _clock.Elapsed - t1);

            if (result.Largest is not { } blob)
            {
                return ScreenProbe.Miss(result.MatchingPixels > 0
                    ? $" {result.MatchingPixels:N0} pixel(s) of that color were found, but no patch had at least {step.MinPixels} pixels."
                    : " No pixels of that color were found in the search area.", region);
            }

            var x = frame.Bounds.X + (int)Math.Round(blob.CenterX);
            var y = frame.Bounds.Y + (int)Math.Round(blob.CenterY);
            var more = result.Blobs.Count > 1 ? $", {result.Blobs.Count} patches; the biggest is used" : "";
            var patch = new ScreenRect(frame.Bounds.X + blob.Bounds.X, frame.Bounds.Y + blob.Bounds.Y, blob.Bounds.Width, blob.Bounds.Height);
            return new ScreenProbe(true, x, y, $"Found {what.ToLowerInvariant()} at ({x}, {y}): {blob.PixelCount:N0} pixels{more}.", string.Empty, region, patch);
        });
    }

    private IScreenCapture RequireCapture() =>
        _capture ?? throw new ScreenCaptureUnavailableException("Screen capture isn't available, so Find image / Find color steps and image / color conditions can't run.");

    /// <summary>Adds what OCR actually saw, which makes "not found" problems much easier to fix.</summary>
    private static string DescribeLastRead(string text)
    {
        var oneLine = string.Join("  |  ", text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        if (oneLine.Length == 0) return " No text could be read in the search area.";
        if (oneLine.Length > 160) oneLine = oneLine[..160] + "…";
        return $" Text read in the search area: \"{oneLine}\"";
    }
}
