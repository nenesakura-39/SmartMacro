using System.Diagnostics;
using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Vision;

namespace SmartMacro.Core.Playback;

/// <summary>
/// Result of one look at the screen. Found = the text / image / colour is there. X / Y is the spot to act on
/// (screen pixels). FoundText describes the find for the log; MissText (starting with a space) explains a miss,
/// e.g. what OCR read instead. Area is the screen rectangle that was searched (empty when it couldn't be searched).
/// Match is the rectangle of what was found (empty on a miss). Confidence is how sure the find is (0..1: the image's
/// similarity, the text's match; 1 for a colour patch). ElapsedMs = how long the look took in real time (capture + search,
/// for the live "⚡ Vision: 0.6 ms" telemetry). FromHotSpot = an image found by the Hot-Spot fast path (at / near the
/// spot where it was found last time) without scanning the whole search area.
///
/// Near-miss diagnostics ("🧪 Test Step"): <see cref="BestConfidence"/> is the best score seen anywhere in the area, also
/// when nothing was good enough (e.g. 0.79 when the step needs 0.85), <see cref="BestMatch"/> / <see cref="BestX"/> /
/// <see cref="BestY"/> where that was (empty / 0 when unknown, e.g. OCR), and <see cref="RequiredConfidence"/> what the step
/// needs (0 = no such threshold). For a found target they describe the match itself.
/// </summary>
public readonly record struct ScreenProbe(bool Found, int X, int Y, string FoundText, string MissText, ScreenRect Area = default, ScreenRect Match = default,
    double Confidence = 1.0, double ElapsedMs = 0, bool FromHotSpot = false)
{
    public static ScreenProbe Miss(string missText, ScreenRect area = default) => new(false, 0, 0, string.Empty, missText, area);

    /// <summary>Best score seen (0..1), found or not.</summary>
    public double BestConfidence { get; init; }

    /// <summary>Where the best-scoring spot was (screen pixels; empty when unknown).</summary>
    public ScreenRect BestMatch { get; init; }

    /// <summary>Centre of <see cref="BestMatch"/> (0 when unknown).</summary>
    public int BestX => BestMatch.IsEmpty ? 0 : BestMatch.CenterX;

    /// <summary>Centre of <see cref="BestMatch"/> (0 when unknown).</summary>
    public int BestY => BestMatch.IsEmpty ? 0 : BestMatch.CenterY;

    /// <summary>The score the step needs (0..1; 0 when the search has no such threshold).</summary>
    public double RequiredConfidence { get; init; }
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
    private readonly HotSpotMemory? _hotSpots;
    private readonly VariableStore? _variables;
    private readonly FrameBus _frameBus = new();
    private readonly TargetTracker _tracker = new();

    /// <summary>
    /// Hot-Spot fast path: how far (px, every side) around the spot where an image was found last time the quick
    /// re-check looks. A target that moved further than this is found by the normal search of the whole area.
    /// </summary>
    public const int HotSpotMarginPx = 24;

    /// <summary>The first, tiny re-check: the last spot ± this many px (a target that didn't move costs almost nothing).</summary>
    public const int HotSpotExactSlackPx = 2;

    /// <summary>
    /// The quick re-check only counts when the picture matches at least this well (or the step's own accuracy, if that's
    /// higher). Anything less goes to the normal full search, so a borderline look gets exactly the old answer.
    /// </summary>
    public const double HotSpotMinScore = 0.88;

    /// <param name="layout">Monitors and windows, for "where to look" = a monitor / an app window (null: main monitor = whole desktop).</param>
    /// <param name="hotSpots">
    /// Where each image search found its picture last time (one per playback session), for the Hot-Spot fast path.
    /// Null = no fast path: every look searches the whole area (the editor's "Test detection now").
    /// </param>
    public ScreenProbes(IOcrService? ocr, IScreenCapture? capture, IPlaybackClock? clock = null, IScreenLayout? layout = null,
        HotSpotMemory? hotSpots = null, VariableStore? variables = null)
    {
        _ocr = ocr;
        _capture = capture;
        _clock = clock ?? new SystemPlaybackClock();
        _layout = layout;
        _hotSpots = hotSpots;
        _variables = variables;
    }

    /// <summary>The Hot-Spot memory these probes use (null when the fast path is off).</summary>
    public HotSpotMemory? HotSpots => _hotSpots;

    /// <summary>Vision 2.0 shared Frame Bus ring buffer.</summary>
    public FrameBus FrameBus => _frameBus;

    /// <summary>Vision 2.0 2D Kalman/Alpha-Beta target tracker.</summary>
    public TargetTracker Tracker => _tracker;

    /// <summary>Looks for a Find image step's picture once (a shortcut for <c>Prepare(step).LookAsync(...)</c>).</summary>
    public Task<ScreenProbe> ProbeImageAsync(FindImageStep step, DetectionTimer? timer = null, CancellationToken cancellationToken = default) =>
        PrepareImage(step).LookAsync(timer, cancellationToken);

    /// <summary>Real time (ms) since a <see cref="Stopwatch.GetTimestamp"/> value.</summary>
    private static double MsSince(long started) => Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    /// <summary>
    /// The area to search right now (monitors / windows are looked up on every look, because a window can move), or null
    /// with the reason when it can't be searched at the moment (the window isn't open, the monitor isn't connected).
    /// </summary>
    private (ScreenRect Area, string? Problem) ResolveArea(ScreenSearchStep step, ScreenRect virtualScreen)
    {
        try
        {
            var titleOverride = _variables is not null && step.EffectiveScope == SearchScope.Window && !string.IsNullOrEmpty(step.WindowTitle)
                ? _variables.Interpolate(step.WindowTitle)
                : null;
            return (SearchArea.Resolve(step, virtualScreen, _layout, titleOverride), null);
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

        var whatText = _variables is not null ? _variables.Interpolate(step.Text) : step.Text;
        return new PreparedSearch($"\"{whatText}\"", isTimed: false, async (_, t) =>
        {
            var started = Stopwatch.GetTimestamp();
            var nowMs = _clock.Elapsed.TotalMilliseconds;
            var (region, problem) = ResolveArea(step, ocr.GetVirtualScreen());
            if (problem is not null) return ScreenProbe.Miss(problem);

            // Vision 2.0 "Find-Once-Then-Track" fast path: if OCR already found this word and cropped its visual template,
            // follow the moving word at image-matcher speed (~1–3 ms) before falling back to a full OCR read.
            if (step.TrackTextAsImage && _capture is not null
                && _tracker.TryGet(step.Id, out var prevTrack)
                && prevTrack.LearnedTemplate is { } wordTemplate
                && prevTrack.MissedFrames <= 2)
            {
                var roi = _tracker.PredictSearchArea(step.Id, nowMs, region, baseMarginPx: 96);
                var searchBox = roi.Width >= wordTemplate.Width && roi.Height >= wordTemplate.Height ? roi : region;
                if (searchBox.Width >= wordTemplate.Width && searchBox.Height >= wordTemplate.Height)
                {
                    var frame = await _capture.CaptureAsync(searchBox, t).ConfigureAwait(false);
                    var tplOpts = new TemplateMatchOptions
                    {
                        MinScore = Math.Max(0.78, step.MinSimilarityPercent / 100.0 - 0.05),
                        AdaptToLighting = true,
                        MaxResults = 1,
                    };
                    if (TemplateMatcher.Find(frame.Image, wordTemplate, tplOpts, t).Best is { } trackedHit)
                    {
                        var screenBounds = frame.ToScreen(trackedHit.Bounds);
                        var updated = _tracker.RecordHit(step.Id, screenBounds, nowMs, wordTemplate);
                        var (tx, ty) = step.EffectivePredictiveLeadMs > 0
                            ? updated.PredictClickPoint(screenBounds.CenterX, screenBounds.CenterY, step.EffectivePredictiveLeadMs, region)
                            : (screenBounds.CenterX, screenBounds.CenterY);
                        var ms = MsSince(started);
                        return new ScreenProbe(true, tx, ty,
                            $"Found \"{whatText}\" at ({tx}, {ty}), {trackedHit.Score:P0} match (⚡ tracked as image, {ms:0.0} ms).",
                            string.Empty, region, screenBounds, trackedHit.Score, ms, FromHotSpot: true)
                        {
                            BestConfidence = trackedHit.Score,
                            BestMatch = screenBounds,
                            RequiredConfidence = step.MinSimilarityPercent / 100.0,
                        };
                    }
                }
            }

            var read = await ocr.ReadAsync(region, t).ConfigureAwait(false);
            var query = _variables is not null ? _variables.Interpolate(step.Text) : step.Text;
            var match = ProbeText(read, step, query);
            if (match is null)
            {
                _tracker.RecordMiss(step.Id);
                return ScreenProbe.Miss(DescribeLastRead(read.FullText), region);
            }

            BgraImage? learnedWordTemplate = null;
            if (step.TrackTextAsImage && _capture is not null
                && match.Bounds.Width >= TemplateMatcher.MinInnerSize
                && match.Bounds.Height >= TemplateMatcher.MinInnerSize)
            {
                try
                {
                    var wordCap = await _capture.CaptureAsync(match.Bounds, t).ConfigureAwait(false);
                    if (wordCap.Image.Width >= TemplateMatcher.MinInnerSize
                        && wordCap.Image.Height >= TemplateMatcher.MinInnerSize
                        && !TemplateMatcher.IsFlat(wordCap.Image))
                    {
                        learnedWordTemplate = wordCap.Image;
                    }
                }
                catch
                {
                    // Non-fatal if the word sits partly off-screen.
                }
            }

            var trackState = _tracker.RecordHit(step.Id, match.Bounds, nowMs, learnedWordTemplate);
            var (clickX, clickY) = step.EffectivePredictiveLeadMs > 0
                ? trackState.PredictClickPoint(match.Bounds.CenterX, match.Bounds.CenterY, step.EffectivePredictiveLeadMs, region)
                : (match.Bounds.CenterX, match.Bounds.CenterY);

            return new ScreenProbe(true, clickX, clickY,
                $"Found \"{match.Text}\" at ({clickX}, {clickY}), {match.Similarity:P0} match.", string.Empty, region, match.Bounds,
                match.Similarity, MsSince(started))
            {
                BestConfidence = match.Similarity,
                BestMatch = match.Bounds,
                RequiredConfidence = step.MinSimilarityPercent / 100.0,
            };
        });
    }

    /// <summary>
    /// The best match of a Find text step (or text condition) in what OCR read, or null. Uses the step's
    /// <see cref="FindTextStep.MatchWholeWord"/> ("art" never matches "apart" / "apartment"; punctuation around a screen word
    /// is ignored) and <see cref="FindTextStep.MatchCase"/> (ordinal: "aRt" only matches "aRt", never "art" / "Art").
    /// The match's Bounds are the matched word(s) only – the OCR word boxes – never the whole line, so a click on found
    /// text lands on that exact word.
    /// </summary>
    public static TextMatch? ProbeText(OcrReadResult read, FindTextStep step, string? queryOverride = null)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(step);
        var mode = step.MatchWholeWord ? TextMatchMode.WholeWords : TextMatchMode.Contains;
        return OcrTextMatcher.Find(
                read,
                queryOverride ?? step.Text,
                mode,
                step.MatchCase,
                step.MinSimilarityPercent / 100.0,
                step.NormalizeLookAlikes,
                step.UseRegexPattern)
            .FirstOrDefault();
    }

    private PreparedSearch PrepareImage(FindImageStep step)
    {
        var capture = RequireCapture();
        // Vision Engine Turbo: the picture is decoded once and shared (not again for every run / repeat / poll).
        if (!TemplateCache.TryGetOrDecode(step.TemplateData, step.TemplateWidth, step.TemplateHeight, out var decoded, out var error))
            throw new InvalidOperationException($"The image of this step can't be used: {error}");
        // Extra blank margin around a target on a plain background is trimmed (1 px kept), so the match – and the click
        // at its centre – is centred on the target itself, not on the rectangle that happened to be captured.
        var template = SearchTemplate(decoded!);
        var options = ImageMatchOptions(step);
        var hotSpots = _hotSpots;
        var hotKey = hotSpots is null ? default : HotSpotMemory.KeyOf(step);
        var hotOptions = options with { MinScore = HotSpotScore(options.MinScore) };

        return new PreparedSearch("The image", isTimed: true, async (timer, t) =>
        {
            var started = Stopwatch.GetTimestamp();
            var nowMs = _clock.Elapsed.TotalMilliseconds;
            var (region, problem) = ResolveArea(step, capture.GetVirtualScreen());
            if (problem is not null) return ScreenProbe.Miss(problem);

            var captureTime = TimeSpan.Zero;
            var searchTime = TimeSpan.Zero;
            ScreenFrame? frame = null;

            // 1. Hot-Spot & Kalman Velocity-Predicted ROI fast path:
            ScreenRect? candidateSpot = null;
            if ((step.TrackTarget || step.PredictiveClick) && _tracker.PredictSearchArea(step.Id, nowMs, region) is { IsEmpty: false } trackedRoi)
                candidateSpot = trackedRoi;
            else if (hotSpots is not null && hotSpots.TryGet(hotKey, out var lastSpot))
                candidateSpot = HotSpotArea(lastSpot, region);

            if (candidateSpot is { } spotArea)
            {
                var last = hotSpots is not null && hotSpots.TryGet(hotKey, out var hLast) ? hLast : spotArea;
                var hotTemplate = options.AllowMultiScale
                    && (last.Width != template.Width || last.Height != template.Height)
                    && last.Width >= TemplateMatcher.MinInnerSize
                    && last.Height >= TemplateMatcher.MinInnerSize
                    && last.Width <= spotArea.Width && last.Height <= spotArea.Height
                    ? template.Resize(last.Width, last.Height)
                    : template;
                var hotSpotOptions = (step.TrackTarget ? options : hotOptions) with { AllowMultiScale = false };
                if (spotArea.Width >= hotTemplate.Width && spotArea.Height >= hotTemplate.Height)
                {
                    ScreenFrame? spot;
                    var c0 = _clock.Elapsed;
                    if (capture.PrefersSmallCaptures)
                    {
                        spot = await capture.CaptureAsync(spotArea, t).ConfigureAwait(false);
                    }
                    else
                    {
                        frame = await capture.CaptureAsync(region, t).ConfigureAwait(false);
                        spot = frame.CropFromScreen(spotArea);
                    }

                    var s0 = _clock.Elapsed;
                    captureTime += s0 - c0;
                    var hit = spot is null ? null : FindNearLastSpot(spot, last, hotTemplate, hotSpotOptions, t);
                    searchTime += _clock.Elapsed - s0;

                    if (hit is not null && !Contains(spotArea, hit.Bounds)) hit = null;

                    if (hit is not null)
                    {
                        timer?.Add(captureTime, searchTime);
                        hotSpots?.Remember(hotKey, hit.Bounds);
                        var track = _tracker.RecordHit(step.Id, hit.Bounds, nowMs);
                        var (hx, hy) = step.EffectivePredictiveLeadMs > 0
                            ? track.PredictClickPoint(hit.Bounds.CenterX, hit.Bounds.CenterY, step.EffectivePredictiveLeadMs, region)
                            : (hit.Bounds.CenterX, hit.Bounds.CenterY);
                        var ms = MsSince(started);
                        return new ScreenProbe(true, hx, hy,
                            $"Found the image at ({hx}, {hy}), {hit.Score:P0} match (⚡ at its last spot, {ms:0.0} ms).",
                            string.Empty, region, hit.Bounds, hit.Score, ms, FromHotSpot: true)
                        {
                            BestConfidence = hit.Score,
                            BestMatch = hit.Bounds,
                            RequiredConfidence = options.MinScore,
                        };
                    }
                }
            }

            // 2. Normal search of the whole area (first look, or the picture moved / disappeared).
            if (frame is null)
            {
                var c1 = _clock.Elapsed;
                frame = await capture.CaptureAsync(region, t).ConfigureAwait(false);
                captureTime += _clock.Elapsed - c1;
            }

            _frameBus.Publish(frame, nowMs);

            var s1 = _clock.Elapsed;
            TemplateMatchResult? result = null;

            // Motion-aware priority: if OnlyWhereMoved is enabled and recent frames show motion inside a sub-rectangle,
            // search the moving sub-rectangle first before scanning static background.
            if (step.OnlyWhereMoved)
            {
                var motion = _frameBus.AnalyzeRecentMotion(region);
                if (motion.HasMotion)
                {
                    var motionScreenRect = Grow(frame.ToScreen(motion.MotionBounds), Math.Max(template.Width, template.Height)).Intersect(region);
                    if (frame.CropFromScreen(motionScreenRect) is { } motionCrop
                        && motionCrop.Image.Width >= template.Width && motionCrop.Image.Height >= template.Height)
                    {
                        var motionRes = TemplateMatcher.Find(motionCrop.Image, template, options, t);
                        if (motionRes.Best is { } mBest)
                        {
                            var translatedMatches = motionRes.Matches.Select(m => m with { Bounds = motionCrop.ToScreen(m.Bounds) }).ToList();
                            // Convert back to frame-local coordinates so downstream frame.ToScreen works uniformly.
                            var localMatches = translatedMatches.Select(m => m with
                            {
                                Bounds = new ScreenRect(m.Bounds.X - frame.Bounds.X, m.Bounds.Y - frame.Bounds.Y, m.Bounds.Width, m.Bounds.Height),
                            }).ToList();
                            result = motionRes with { Matches = localMatches, BestBounds = localMatches[0].Bounds };
                        }
                    }
                }
            }

            result ??= TemplateMatcher.Find(frame.Image, template, options, t);
            searchTime += _clock.Elapsed - s1;
            timer?.Add(captureTime, searchTime);

            var minRequiredWidth = options.AllowMultiScale
                ? Math.Max(TemplateMatcher.MinInnerSize, (int)Math.Round(template.Width * options.MinScale))
                : template.Width;
            var minRequiredHeight = options.AllowMultiScale
                ? Math.Max(TemplateMatcher.MinInnerSize, (int)Math.Round(template.Height * options.MinScale))
                : template.Height;
            if (frame.Image.Width < minRequiredWidth || frame.Image.Height < minRequiredHeight)
                return ScreenProbe.Miss(" The search area (on screen) is smaller than the image.", region);

            if (result.Best is not { } best)
            {
                _tracker.RecordMiss(step.Id);
                var miss = $" The best similarity was {result.BestScore:P0} (needs {step.MinSimilarityPercent}%).";
                if (result.RejectedForColor > 0)
                    miss += $" {result.RejectedForColor} spot(s) had the right shape but a different color (turn on \"Ignore color\" if the color changes).";
                if (result.BestScore >= 0.5 && !step.MatchForeground)
                    miss += " If you can see the game through the button (or the background around it changes), turn on \"Ignore background\""
                            + (step.IgnoreEdgesPercent == 0 ? $" or \"Ignore the edges\" ({FindImageStep.RecommendedIgnoreEdgesPercent} %)." : ".");
                if (result.BestScore >= 0.45 && step.MinVisiblePercent == 0)
                    miss += " If particles or effects cover part of the target, choose the \"Busy Scene / Particles\" scenario or lower \"Required visible (%)\" to 65 %.";
                // Near miss: the best spot anywhere in the area, even though it wasn't good enough.
                return ScreenProbe.Miss(miss, region) with
                {
                    ElapsedMs = MsSince(started),
                    BestConfidence = Math.Clamp(result.BestScore, 0, 1),
                    BestMatch = result.BestBounds.IsEmpty ? default : frame.ToScreen(result.BestBounds),
                    RequiredConfidence = options.MinScore,
                };
            }

            var bounds = frame.ToScreen(best.Bounds);
            hotSpots?.Remember(hotKey, bounds);
            var fullTrack = _tracker.RecordHit(step.Id, bounds, nowMs);
            var (cx, cy) = step.EffectivePredictiveLeadMs > 0
                ? fullTrack.PredictClickPoint(bounds.CenterX, bounds.CenterY, step.EffectivePredictiveLeadMs, region)
                : (bounds.CenterX, bounds.CenterY);
            var scaleNote = Math.Abs(best.MatchedScale - 1.0) >= 0.02 ? $" at {best.MatchedScale * 100:0}% scale" : "";
            var visNote = best.VisibleFraction < 0.99 ? $" ({best.VisibleFraction:P0} visible)" : "";
            var leadNote = (cx != bounds.CenterX || cy != bounds.CenterY) ? $" (⚡ lead click)" : "";
            var more = result.Matches.Count > 1 ? $" ({result.Matches.Count} matches; the best one is used)" : "";
            return new ScreenProbe(true, cx, cy,
                $"Found the image at ({cx}, {cy}), {best.Score:P0} match{scaleNote}{visNote}{leadNote}{more}.", string.Empty, region, bounds, best.Score,
                MsSince(started))
            {
                BestConfidence = best.Score,
                BestMatch = bounds,
                RequiredConfidence = options.MinScore,
            };
        });
    }

    /// <summary>The accuracy the Hot-Spot re-check needs: the step's own, but at least <see cref="HotSpotMinScore"/>.</summary>
    public static double HotSpotScore(double requiredScore) => Math.Clamp(Math.Max(requiredScore, HotSpotMinScore), 0, 1);

    /// <summary>
    /// The part of the screen the Hot-Spot re-check looks at: the last match grown by <see cref="HotSpotMarginPx"/> on
    /// every side, kept inside the step's search area (so it never finds something the normal search couldn't).
    /// </summary>
    public static ScreenRect HotSpotArea(ScreenRect lastMatch, ScreenRect searchArea) =>
        Grow(lastMatch, HotSpotMarginPx).Intersect(searchArea);

    /// <summary>True when <paramref name="inner"/> lies completely inside <paramref name="outer"/>.</summary>
    internal static bool Contains(ScreenRect outer, ScreenRect inner) =>
        !inner.IsEmpty && inner.X >= outer.X && inner.Y >= outer.Y && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;

    private static ScreenRect Grow(ScreenRect r, int by) => new(r.X - by, r.Y - by, r.Width + 2 * by, r.Height + 2 * by);

    /// <summary>
    /// The Hot-Spot re-check inside <paramref name="spot"/> (a capture of <see cref="HotSpotArea"/>), at full resolution:
    /// first the last spot ± <see cref="HotSpotExactSlackPx"/> px (a target that didn't move: a few thousand pixel
    /// compares), then the whole ± <see cref="HotSpotMarginPx"/> px area. Null when it isn't there well enough.
    /// Uses the matcher exactly like the normal search (same shape, colour, edge and background rules).
    /// </summary>
    internal static VisualMatch? FindNearLastSpot(ScreenFrame spot, ScreenRect lastMatch, BgraImage template, TemplateMatchOptions options,
        CancellationToken token)
    {
        if (spot.CropFromScreen(Grow(lastMatch, HotSpotExactSlackPx)) is { } exact
            && exact.Image.Width >= template.Width && exact.Image.Height >= template.Height
            && TemplateMatcher.Find(exact.Image, template, options, token).Best is { } still)
            return still with { Bounds = exact.ToScreen(still.Bounds) };

        // The last spot ± 2 px covers the whole capture already when it was clipped that small: nothing more to check.
        if (spot.Image.Width < template.Width || spot.Image.Height < template.Height) return null;
        if (spot.Image.Width <= template.Width + 2 * HotSpotExactSlackPx && spot.Image.Height <= template.Height + 2 * HotSpotExactSlackPx)
            return null;

        return TemplateMatcher.Find(spot.Image, template, options, token).Best is { } near
            ? near with { Bounds = spot.ToScreen(near.Bounds) }
            : null;
    }

    /// <summary>
    /// The picture a Find image step / image condition actually searches for: the decoded template with extra blank
    /// margin trimmed (<see cref="TemplateBackground.TrimMargins"/>; the same picture when there's nothing to trim). Used by
    /// playback and by the editor's "Test detection now", so both search for exactly the same thing.
    /// </summary>
    public static BgraImage SearchTemplate(BgraImage decoded) => TemplateBackground.TrimMargins(decoded, out _);

    /// <summary>
    /// The matcher settings for a Find image step or an image condition (If / Repeat / feedback loop). The editor's
    /// "Test detection now" uses this too, so a test, a Find image step and an If condition with the same settings
    /// always search in exactly the same way (same accuracy, colour, edge, background and lighting options).
    /// </summary>
    public static TemplateMatchOptions ImageMatchOptions(FindImageStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        List<BgraImage>? extras = null;
        if (step.AdditionalTemplates is { Count: > 0 } variants)
        {
            extras = new List<BgraImage>(variants.Count);
            foreach (var v in variants)
            {
                if (v is not null && TemplateCache.TryGetOrDecode(v.Data, v.Width, v.Height, out var img, out _) && img is not null)
                    extras.Add(SearchTemplate(img));
            }
        }

        return new TemplateMatchOptions
        {
            MinScore = step.MinSimilarityPercent / 100.0,
            IgnoreColor = step.IgnoreColor,
            IgnoreEdgeFraction = step.IgnoreEdgesPercent / 100.0,
            MatchForeground = step.MatchForeground,
            AdaptToLighting = step.AdaptToLighting,
            AllowMultiScale = step.AllowMultiScale,
            Detector = step.Detector,
            MinVisibleFraction = step.MinVisiblePercent > 0 ? Math.Clamp(step.MinVisiblePercent / 100.0, 0.35, 1.0) : 0,
            RotationRangeDeg = step.RotationRangeDeg,
            AdditionalTemplates = extras,
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
            var started = Stopwatch.GetTimestamp();
            var nowMs = _clock.Elapsed.TotalMilliseconds;
            var (region, problem) = ResolveArea(step, capture.GetVirtualScreen());
            if (problem is not null) return ScreenProbe.Miss(problem);
            var t0 = _clock.Elapsed;
            var frame = await capture.CaptureAsync(region, t).ConfigureAwait(false);
            var t1 = _clock.Elapsed;

            (int X, int Y)? refPoint = null;
            if (step.SelectRule == ColorSelectRule.NearestToLastSpot && _tracker.TryGet(step.Id, out var prevTrack))
            {
                var pred = prevTrack.PredictBounds(nowMs);
                refPoint = (pred.CenterX - frame.Bounds.X, pred.CenterY - frame.Bounds.Y);
            }
            else if (step.SelectRule == ColorSelectRule.NearestToCenter)
            {
                refPoint = (frame.Image.Width / 2, frame.Image.Height / 2);
            }

            var colorOpts = new ColorSearchOptions
            {
                MinPixels = step.MinPixels,
                MaxPixels = step.MaxPixels,
                MaxResults = 20,
                MatchShades = step.MatchShades,
                ColorSpace = step.ColorSpace,
                SelectRule = step.SelectRule,
                MinCircularityPercent = step.MinCircularityPercent,
                MinFillPercent = step.MinFillPercent,
                ReferencePoint = refPoint,
            };

            // The selected colour OBJECT: nearby matching pixels are grouped (8×8 px cells), so a shaded ball is one object and
            // the click goes to its centre of mass, never to a stray pixel or the top edge.
            var result = ColorFinder.FindObjects(frame.Image, color, secondaryColor, step.Tolerance, colorOpts, t);
            timer?.Add(t1 - t0, _clock.Elapsed - t1);

            if (result.Largest is not { } blob)
            {
                _tracker.RecordMiss(step.Id);
                var filterReason = (step.MaxPixels > 0 || step.MinCircularityPercent > 0 || step.MinFillPercent > 0)
                    ? $"no patch had at least {step.MinPixels} pixels and matched the shape/size filter"
                    : $"no patch had at least {step.MinPixels} pixels";
                var missProbe = ScreenProbe.Miss(result.MatchingPixels > 0
                    ? $" {result.MatchingPixels:N0} pixel(s) of that color were found, but {filterReason}."
                    : " No pixels of that color were found in the search area.", region) with { ElapsedMs = MsSince(started), RequiredConfidence = 1.0 };

                // Near miss: the biggest patch that was too small ("4 px of the 10 needed" = 40 %).
                if (result.MatchingPixels > 0 && step.MinPixels > 1
                    && ColorFinder.FindObjects(frame.Image, color, secondaryColor, step.Tolerance, 1, maxResults: 1, t, step.MatchShades).Largest is { } small)
                {
                    var smallRect = new ScreenRect(frame.Bounds.X + small.Bounds.X, frame.Bounds.Y + small.Bounds.Y, small.Bounds.Width, small.Bounds.Height);
                    missProbe = missProbe with
                    {
                        BestConfidence = Math.Clamp((double)small.PixelCount / step.MinPixels, 0, 0.99),
                        BestMatch = smallRect,
                    };
                }

                return missProbe;
            }

            var rawX = frame.Bounds.X + (int)Math.Round(blob.CenterX);
            var rawY = frame.Bounds.Y + (int)Math.Round(blob.CenterY);
            var patch = new ScreenRect(frame.Bounds.X + blob.Bounds.X, frame.Bounds.Y + blob.Bounds.Y, blob.Bounds.Width, blob.Bounds.Height);
            var track = _tracker.RecordHit(step.Id, patch, nowMs);
            var (x, y) = step.EffectivePredictiveLeadMs > 0
                ? track.PredictClickPoint(rawX, rawY, step.EffectivePredictiveLeadMs, region)
                : (rawX, rawY);
            var more = result.Blobs.Count > 1 ? $", {result.Blobs.Count} objects; the best is used" : "";
            return new ScreenProbe(true, x, y, $"Found {what.ToLowerInvariant()} at ({x}, {y}): {blob.PixelCount:N0} pixels{more}.", string.Empty, region, patch,
                1.0, MsSince(started))
            {
                BestConfidence = 1.0,
                BestMatch = patch,
                RequiredConfidence = 1.0,
            };
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
