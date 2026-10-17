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
    private readonly RgbColor _zoneColor;
    private readonly RgbColor? _secondaryZoneColor;

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

        _tracker = new MarkerTracker(markerColor, step.MarkerTolerance, step.Orientation, step.MarkerMinPixels, step.MaxJumpPercent, secondaryMarkerColor);

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

        var marker = _tracker.Update(frame.Image, cancellationToken);
        ZoneReading zone;
        if (_step.ZoneMode == FeedbackZoneMode.Fixed)
        {
            zone = ZoneReading.Fixed(_step.ZoneStartPercent, _step.ZoneEndPercent);
        }
        else
        {
            // The marker can sit on top of the zone and cut it in two: bridge gaps up to the marker's size.
            var markerSize = marker.Bounds is { } b ? (_step.Orientation == BarOrientation.Horizontal ? b.Width : b.Height) : 0;
            zone = ZoneFinder.Find(frame.Image, _zoneColor, _secondaryZoneColor, _step.ZoneTolerance, _step.ZoneMinPixels, _step.Orientation,
                markerSize + 2, cancellationToken);
            if (_secondaryZoneColor is { } secZone)
            {
                marker = ApplySmartOverlap(frame.Image, _step.Orientation, marker, zone, secZone, _step.ZoneTolerance, _step.ZoneMinPixels, cancellationToken);
            }
        }

        var t2 = _clock.Elapsed;
        var observation = new FeedbackObservation(marker.Status, marker.PositionPercent, zone.Found, zone.StartPercent, zone.EndPercent);
        return new FeedbackFrame(frame, marker, zone, observation, t1 - t0, t2 - t1);
    }

    /// <summary>
    /// Smart Overlap Handling: when the control bar has an active/overlapping 2nd colour (e.g. White in Roblox Fisch)
    /// and that active colour is present on screen while the primary target is occluded or partially occluded by the bar,
    /// use the centroid of the active bar region so the controller knows the bar is aligned with the target instead of
    /// declaring "Target missing".
    /// </summary>
    public static MarkerReading ApplySmartOverlap(BgraImage barImage, BarOrientation orientation, MarkerReading marker,
        ZoneReading zone, RgbColor secondaryZoneColor, int zoneTolerance, int zoneMinPixels, CancellationToken cancellationToken = default)
    {
        if (!zone.Found) return marker;
        var activeZone = ZoneFinder.Find(barImage, secondaryZoneColor, null, zoneTolerance, zoneMinPixels, orientation, 2, cancellationToken);
        if (!activeZone.Found) return marker;

        var occluded = marker.Status != MarkerStatus.Found
                       || (marker.Bounds is { } mb && activeZone.Bounds is { } zb && TouchingOrOverlapping(mb, zb));
        if (!occluded) return marker;

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

    /// <summary>How many times the target went missing long enough for "release and wait".</summary>
    public int LostEpisodes { get; set; }

    public int ConditionChecks { get; set; }

    public int InputChanges => Presses + Releases + Taps;

    public double AverageCaptureMs => Frames == 0 ? 0 : CaptureTime.TotalMilliseconds / Frames;
    public double AverageDetectMs => Frames == 0 ? 0 : DetectTime.TotalMilliseconds / Frames;

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
        text += ")";
        text += $"; marker found in {Share(MarkerFound)} of frames, unclear in {Share(MarkerUnclear)}, missing in {Share(MarkerMissing)}";
        if (IgnoredJumps > 0) text += $", {IgnoredJumps} jump{(IgnoredJumps == 1 ? "" : "s")} ignored";
        if (ZoneMissing > 0) text += $", zone missing in {Share(ZoneMissing)}";
        if (LostEpisodes > 0) text += $"; target lost {LostEpisodes} time{(LostEpisodes == 1 ? "" : "s")}";
        if (ConditionChecks > 0) text += $"; {ConditionChecks} condition check{(ConditionChecks == 1 ? "" : "s")}";
        return text + ".";
    }

    private string Share(int count) => (count / (double)Frames).ToString("P0", System.Globalization.CultureInfo.InvariantCulture);
}
