using SmartMacro.Core.Models;
using SmartMacro.Core.Vision;

namespace SmartMacro.Core.Playback;

/// <summary>
/// Everything one frame of a feedback loop saw: the captured picture, the marker and zone readings, the observation
/// for the controller, and how long capturing and detecting took.
/// </summary>
public sealed record FeedbackFrame(
    ScreenFrame Frame,
    MarkerReading Marker,
    ZoneReading Zone,
    FeedbackObservation Observation,
    TimeSpan CaptureTime,
    TimeSpan DetectTime);

/// <summary>Thrown when a feedback loop ends as a failure (fail condition, lost target, max runtime) and is set to stop the macro.</summary>
public sealed class FeedbackLoopException : Exception
{
    public FeedbackLoopException(string message) : base(message)
    {
    }
}

/// <summary>
/// The "eyes" of a feedback loop: captures the bar area once, finds the marker (with <see cref="MarkerTracker"/>, which
/// remembers the previous frame to ignore one-frame glitches) and the zone (fixed, or with <see cref="ZoneFinder"/>).
/// Shared by the playback engine and the editor's Measure / Preview tools, so both see exactly the same thing.
/// It never sends input.
/// </summary>
public sealed class FeedbackSensor
{
    private readonly FeedbackLoopStep _step;
    private readonly IScreenCapture _capture;
    private readonly IPlaybackClock _clock;
    private readonly MarkerTracker _tracker;
    private readonly FeedbackLoopTracker _loopTracker = new();
    private readonly RgbColor _zoneColor;
    private readonly RgbColor? _secondaryZoneColor;
    private readonly int _markerMinPixels;
    private readonly int _zoneMinPixels;

    /// <summary>Throws <see cref="InvalidOperationException"/> when a colour can't be read (the validator reports it first).</summary>
    public FeedbackSensor(FeedbackLoopStep step, IScreenCapture capture, IPlaybackClock? clock = null)
    {
        _step = step ?? throw new ArgumentNullException(nameof(step));
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _clock = clock ?? new SystemPlaybackClock();

        if (!RgbColor.TryParse(step.MarkerColor, out var markerColor))
            throw new InvalidOperationException($"The marker color \"{step.MarkerColor}\" is not valid. Use #RRGGBB.");
        RgbColor? secondaryMarkerColor = null;
        if (step.UseSecondaryMarkerColor)
        {
            var secText = string.IsNullOrWhiteSpace(step.SecondaryMarkerColor) ? "#FFFFFF" : step.SecondaryMarkerColor;
            if (!RgbColor.TryParse(secText, out var parsedSecMarker))
                throw new InvalidOperationException($"The second marker color \"{step.SecondaryMarkerColor}\" is not valid. Use #RRGGBB.");
            secondaryMarkerColor = parsedSecMarker;
        }

        // Smart Predictive measures speeds from the centroids, so a 1–2 px stray match must never count as the target
        // or your bar (it would make the speed spike): at least MinCentroidPixels matching pixels each.
        var smart = step.ControlMode == FeedbackControlMode.SmartPredictive;
        _markerMinPixels = smart ? Math.Max(PredictiveFeedbackSettings.MinCentroidPixels, step.MarkerMinPixels) : step.MarkerMinPixels;
        _zoneMinPixels = smart ? Math.Max(PredictiveFeedbackSettings.MinCentroidPixels, step.ZoneMinPixels) : step.ZoneMinPixels;
        _tracker = new MarkerTracker(markerColor, step.MarkerTolerance, step.Orientation, _markerMinPixels, step.MaxJumpPercent, secondaryMarkerColor);

        if (step.ZoneMode == FeedbackZoneMode.DetectedByColor)
        {
            if (!RgbColor.TryParse(step.ZoneColor, out _zoneColor))
                throw new InvalidOperationException($"The zone color \"{step.ZoneColor}\" is not valid. Use #RRGGBB.");
            if (step.UseSecondaryZoneColor)
            {
                var secZoneText = string.IsNullOrWhiteSpace(step.SecondaryZoneColor) ? "#FFFFFF" : step.SecondaryZoneColor;
                if (!RgbColor.TryParse(secZoneText, out var parsedSecZone))
                    throw new InvalidOperationException($"The second zone color \"{step.SecondaryZoneColor}\" is not valid. Use #RRGGBB.");
                _secondaryZoneColor = parsedSecZone;
            }
        }
    }

    /// <summary>Captures and reads one frame. Throws if the bar area is not (completely) on screen.</summary>
    public async Task<FeedbackFrame> ReadAsync(CancellationToken cancellationToken)
    {
        var region = _step.Region;
        var t0 = _clock.Elapsed;
        var frame = await _capture.CaptureAsync(region, cancellationToken).ConfigureAwait(false);
        var t1 = _clock.Elapsed;

        // A partly visible bar would silently shift every percentage, so refuse it with a clear message.
        if (frame.Bounds != region)
            throw new InvalidOperationException($"The bar area {region} is partly outside the screen (only {frame.Bounds} is visible). Pick the area again.");

        var horizontal = _step.Orientation == BarOrientation.Horizontal;
        var crossLength = horizontal ? frame.Image.Height : frame.Image.Width;
        var length = horizontal ? frame.Image.Width : frame.Image.Height;

        if (_step.ControlMode == FeedbackControlMode.SmartPredictive &&
            _step.ZoneMode == FeedbackZoneMode.DetectedByColor &&
            crossLength >= 10 &&
            length > 1)
        {
            var (targetScan, indicatorScan) = _loopTracker.Scan(frame.Image, _step);
            MarkerReading smartMarker;
            if (targetScan.Found && targetScan.MatchCount >= _markerMinPixels)
            {
                var posPct = ZoneFinder.ToPercent(targetScan.CenterPos, length);
                var mMin = (int)Math.Clamp(Math.Floor(targetScan.MinEdge), 0, length - 1);
                var mMax = (int)Math.Clamp(Math.Ceiling(targetScan.MaxEdge), mMin, length - 1);
                var mBounds = horizontal
                    ? new Ocr.ScreenRect(mMin, 0, Math.Max(1, mMax - mMin + 1), frame.Image.Height)
                    : new Ocr.ScreenRect(0, mMin, frame.Image.Width, Math.Max(1, mMax - mMin + 1));
                smartMarker = new MarkerReading(MarkerStatus.Found, posPct, 1.0, 1, mBounds);
            }
            else
            {
                smartMarker = new MarkerReading(MarkerStatus.Lost, null, 0, 0, null);
            }

            ZoneReading smartZone;
            if (indicatorScan.Found && indicatorScan.MatchCount >= _zoneMinPixels)
            {
                var startPct = ZoneFinder.ToPercent(indicatorScan.MinEdge, length);
                var endPct = ZoneFinder.ToPercent(indicatorScan.MaxEdge, length);
                var zMin = (int)Math.Clamp(Math.Floor(indicatorScan.MinEdge), 0, length - 1);
                var zMax = (int)Math.Clamp(Math.Ceiling(indicatorScan.MaxEdge), zMin, length - 1);
                var zBounds = horizontal
                    ? new Ocr.ScreenRect(zMin, 0, Math.Max(1, zMax - zMin + 1), frame.Image.Height)
                    : new Ocr.ScreenRect(0, zMin, frame.Image.Width, Math.Max(1, zMax - zMin + 1));
                smartZone = new ZoneReading(true, startPct, endPct, zBounds);
            }
            else
            {
                smartZone = ZoneReading.Missing;
            }

            var t2Smart = _clock.Elapsed;
            var smartObs = new FeedbackObservation(smartMarker.Status, smartMarker.PositionPercent, smartZone.Found, smartZone.StartPercent, smartZone.EndPercent, _loopTracker.LastBarWasActiveSecondary);
            return new FeedbackFrame(frame, smartMarker, smartZone, smartObs, t1 - t0, t2Smart - t1);
        }

        var marker = _tracker.Update(frame.Image, cancellationToken);
        var occluded = false;
        ZoneReading zone;
        if (_step.ZoneMode == FeedbackZoneMode.Fixed)
        {
            zone = ZoneReading.Fixed(_step.ZoneStartPercent, _step.ZoneEndPercent);
        }
        else
        {
            // The marker can sit on top of the zone and cut it in two: bridge gaps up to the marker's size.
            var markerSize = marker.Bounds is { } b ? (_step.Orientation == BarOrientation.Horizontal ? b.Width : b.Height) : 0;
            zone = ZoneFinder.Find(frame.Image, _zoneColor, _secondaryZoneColor, _step.ZoneTolerance, _zoneMinPixels, _step.Orientation,
                markerSize + 2, cancellationToken);
            if (_secondaryZoneColor is { } secZone)
            {
                var seen = marker;
                marker = ApplySmartOverlap(frame.Image, _step.Orientation, marker, zone, secZone, _step.ZoneTolerance, _zoneMinPixels, cancellationToken);
                occluded = !ReferenceEquals(seen, marker);
            }
        }

        var t2 = _clock.Elapsed;
        var observation = new FeedbackObservation(marker.Status, marker.PositionPercent, zone.Found, zone.StartPercent, zone.EndPercent, occluded);
        return new FeedbackFrame(frame, marker, zone, observation, t1 - t0, t2 - t1);
    }

    /// <summary>
    /// Smart Overlap Handling: when the control bar has an active/overlapping 2nd colour (e.g. White when active)
    /// and that active colour is present on screen while the primary target is occluded or partially occluded by the bar,
    /// use the centroid of the active bar region so the controller knows the bar is aligned with the target instead of
    /// declaring "Target missing".
    /// </summary>
    public static MarkerReading ApplySmartOverlap(BgraImage barImage, BarOrientation orientation, MarkerReading marker,
        ZoneReading zone, RgbColor secondaryZoneColor, int zoneTolerance, int zoneMinPixels, CancellationToken cancellationToken = default)
    {
        if (!zone.Found || marker.Status == MarkerStatus.Found) return marker;
        var activeZone = ZoneFinder.Find(barImage, secondaryZoneColor, null, zoneTolerance, zoneMinPixels, orientation, 2, cancellationToken);
        if (!activeZone.Found) return marker;

        var centroid = (activeZone.StartPercent + activeZone.EndPercent) * 0.5;
        return new MarkerReading(MarkerStatus.Found, centroid, 1.0, 1, activeZone.Bounds);
    }

    private static bool TouchingOrOverlapping(Ocr.ScreenRect a, Ocr.ScreenRect b)
    {
        var expanded = new Ocr.ScreenRect(b.X - 2, b.Y - 2, b.Width + 4, b.Height + 4);
        return a.IntersectsWith(expanded);
    }
}

/// <summary>
/// Counters for one feedback loop run (or one preview / measurement), for the single summary line in the log and the
/// editor's measured numbers. Times are capture and detection time per frame; nothing is stored per frame.
/// </summary>
public sealed class FeedbackStats
{
    public int Frames { get; private set; }
    public int MarkerFound { get; private set; }
    public int MarkerUnclear { get; private set; }
    public int MarkerMissing { get; private set; }
    public int IgnoredJumps { get; private set; }
    public int ZoneMissing { get; private set; }
    public TimeSpan CaptureTime { get; private set; }
    public TimeSpan DetectTime { get; private set; }

    /// <summary>The slowest frame (capture + detection), to show how uneven the timing is.</summary>
    public TimeSpan SlowestFrame { get; private set; }

    public int Presses { get; set; }
    public int Releases { get; set; }
    public int Taps { get; set; }

    /// <summary>Changes held back by "minimum time between input changes" (tried again on a later frame).</summary>
    public int Deferred { get; set; }

    /// <summary>Smart Predictive: how often a target reversal flipped the input at once (instant brake).</summary>
    public int Brakes { get; set; }

    /// <summary>Feedback Loop 3.0: share of tracked frames where the target stayed inside the player bar (0–100%).</summary>
    public double? OnTargetPercent { get; set; }

    /// <summary>How many times the target went missing long enough for "release and wait".</summary>
    public int LostEpisodes { get; set; }

    public int ConditionChecks { get; set; }

    public int InputChanges => Presses + Releases + Taps;

    public double AverageCaptureMs => Frames == 0 ? 0 : CaptureTime.TotalMilliseconds / Frames;
    public double AverageDetectMs => Frames == 0 ? 0 : DetectTime.TotalMilliseconds / Frames;

    public void AddServoFrame(BarScanResult target, BarScanResult indicator, TimeSpan captureTime, TimeSpan detectTime)
    {
        Frames++;
        if (captureTime > TimeSpan.Zero) CaptureTime += captureTime;
        if (detectTime > TimeSpan.Zero) DetectTime += detectTime;
        var work = captureTime + detectTime;
        if (work > SlowestFrame) SlowestFrame = work;

        if (target.Found) MarkerFound++;
        else MarkerMissing++;

        if (!indicator.Found) ZoneMissing++;
    }

    public void Add(FeedbackFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Frames++;
        if (frame.CaptureTime > TimeSpan.Zero) CaptureTime += frame.CaptureTime;
        if (frame.DetectTime > TimeSpan.Zero) DetectTime += frame.DetectTime;
        var work = frame.CaptureTime + frame.DetectTime;
        if (work > SlowestFrame) SlowestFrame = work;

        switch (frame.Marker.Status)
        {
            case MarkerStatus.Found: MarkerFound++; break;
            case MarkerStatus.Ambiguous: MarkerUnclear++; break;
            case MarkerStatus.Lost: MarkerMissing++; break;
            case MarkerStatus.IgnoredJump: IgnoredJumps++; break;
        }

        if (!frame.Zone.Found) ZoneMissing++;
    }

    /// <summary>Frames per second over <paramref name="total"/>.</summary>
    public double FramesPerSecond(TimeSpan total) => total <= TimeSpan.Zero ? 0 : Frames / total.TotalSeconds;

    /// <summary>
    /// One sentence for the log: frames, frame rate, time per frame and the resulting reaction time, input changes,
    /// and how often the marker was found / unclear / missing.
    /// </summary>
    public string Describe(TimeSpan total, TimeSpan frameInterval)
    {
        var text = $"{Frames:N0} frame{(Frames == 1 ? "" : "s")} in {total.TotalSeconds:0.0} s ({FramesPerSecond(total):0.0} per second)";
        if (Frames == 0) return text + ".";

        var work = AverageCaptureMs + AverageDetectMs;
        var interval = Math.Max(frameInterval.TotalMilliseconds, work);
        text += $"; capture {AverageCaptureMs:0.0} ms + detection {AverageDetectMs:0.0} ms per frame (slowest {SlowestFrame.TotalMilliseconds:0} ms), " +
                $"so it reacts about {work:0}-{work + interval:0} ms after the screen changes, plus the game's own delay";
        text += $"; {InputChanges} input change{(InputChanges == 1 ? "" : "s")} ({Presses} press, {Releases} release, {Taps} tap";
        if (Deferred > 0) text += $", {Deferred} delayed by the minimum time between changes";
        if (Brakes > 0) text += $", {Brakes} instant brake{(Brakes == 1 ? "" : "s")} on a target reversal";
        text += ")";
        text += $"; marker found in {Share(MarkerFound)} of frames, unclear in {Share(MarkerUnclear)}, missing in {Share(MarkerMissing)}";
        if (IgnoredJumps > 0) text += $", {IgnoredJumps} jump{(IgnoredJumps == 1 ? "" : "s")} ignored";
        if (ZoneMissing > 0) text += $", zone missing in {Share(ZoneMissing)}";
        if (OnTargetPercent is { } onTarget) text += $"; on-target {onTarget:0}%";
        if (LostEpisodes > 0) text += $"; target lost {LostEpisodes} time{(LostEpisodes == 1 ? "" : "s")}";
        if (ConditionChecks > 0) text += $"; {ConditionChecks} condition check{(ConditionChecks == 1 ? "" : "s")}";
        return text + ".";
    }

    private string Share(int count) => (count / (double)Frames).ToString("P0", System.Globalization.CultureInfo.InvariantCulture);
}
