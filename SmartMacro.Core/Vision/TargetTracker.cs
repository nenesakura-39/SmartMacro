using System.Collections.Concurrent;
using SmartMacro.Core.Ocr;

namespace SmartMacro.Core.Vision;

/// <summary>
/// Kinematic state of a tracked visual target (image, colour blob, or tracked word) in virtual-screen coordinates.
/// </summary>
public sealed record TargetTrackState(
    ScreenRect LastBounds,
    double FilteredX,
    double FilteredY,
    double VelocityX,
    double VelocityY,
    double LastSeenMs,
    int HitStreak,
    int MissedFrames,
    BgraImage? LearnedTemplate = null)
{
    /// <summary>Speed in pixels per second.</summary>
    public double SpeedPxPerSec => Math.Sqrt(VelocityX * VelocityX + VelocityY * VelocityY);

    /// <summary>Predicts the target's bounding box at <paramref name="nowMs"/> using constant-velocity kinematics.</summary>
    public ScreenRect PredictBounds(double nowMs)
    {
        var dtSec = Math.Clamp((nowMs - LastSeenMs) / 1000.0, 0.0, 0.5);
        var predCenterX = (int)Math.Round(FilteredX + VelocityX * dtSec);
        var predCenterY = (int)Math.Round(FilteredY + VelocityY * dtSec);
        return new ScreenRect(
            predCenterX - LastBounds.Width / 2,
            predCenterY - LastBounds.Height / 2,
            LastBounds.Width,
            LastBounds.Height);
    }

    /// <summary>
    /// Computes the predictive click coordinates ("lead the target") at <paramref name="leadMs"/> ahead of the current
    /// observation, clamped safely inside <paramref name="searchArea"/>.
    /// </summary>
    public (int X, int Y) PredictClickPoint(int observedX, int observedY, int leadMs, ScreenRect searchArea)
    {
        if (leadMs <= 0 || HitStreak < 2 || SpeedPxPerSec < 8.0)
            return (observedX, observedY);

        var leadSec = Math.Clamp(leadMs / 1000.0, 0.0, 0.35);
        var ledX = (int)Math.Round(observedX + VelocityX * leadSec);
        var ledY = (int)Math.Round(observedY + VelocityY * leadSec);

        if (!searchArea.IsEmpty)
        {
            ledX = Math.Clamp(ledX, searchArea.X, Math.Max(searchArea.X, searchArea.Right - 1));
            ledY = Math.Clamp(ledY, searchArea.Y, Math.Max(searchArea.Y, searchArea.Bottom - 1));
        }

        return (ledX, ledY);
    }
}

/// <summary>
/// Vision 2.0 2D Kalman / Alpha-Beta Target Tracker &amp; Temporal Confirmation memory.
/// Tracks moving objects across checks, estimates their velocity vector (px/s), predicts dynamic search ROIs along the
/// motion path, computes predictive lead click points, and stores cropped visual templates for "Find-once-then-track" OCR.
/// Thread-safe.
/// </summary>
public sealed class TargetTracker
{
    /// <summary>Maximum plausible target speed on screen (px/s) before treating a jump as a newly spawned target.</summary>
    public const double MaxSpeedPxPerSec = 4500.0;

    /// <summary>Maximum time gap (ms) over which velocity is integrated; longer pauses reset velocity to zero.</summary>
    public const double MaxTrackGapMs = 1500.0;

    /// <summary>Default lead time (ms) when <c>PredictiveClick</c> is enabled with default lead.</summary>
    public const int DefaultLeadMs = 60;

    private const double AlphaPosition = 0.78;
    private const double BetaVelocity = 0.45;

    private readonly ConcurrentDictionary<Guid, TargetTrackState> _tracks = new();

    public int Count => _tracks.Count;

    public bool TryGet(Guid stepId, out TargetTrackState state) => _tracks.TryGetValue(stepId, out state!);

    /// <summary>
    /// Updates the tracker for <paramref name="stepId"/> with a newly observed target rectangle at <paramref name="timestampMs"/>.
    /// </summary>
    public TargetTrackState RecordHit(Guid stepId, ScreenRect bounds, double timestampMs, BgraImage? learnedTemplate = null)
    {
        double cx = bounds.CenterX;
        double cy = bounds.CenterY;

        return _tracks.AddOrUpdate(
            stepId,
            _ => new TargetTrackState(bounds, cx, cy, 0, 0, timestampMs, HitStreak: 1, MissedFrames: 0, learnedTemplate),
            (_, prev) =>
            {
                var dtMs = timestampMs - prev.LastSeenMs;
                var templateToKeep = learnedTemplate ?? prev.LearnedTemplate;
                if (dtMs <= 0.5 || dtMs > MaxTrackGapMs)
                {
                    return new TargetTrackState(bounds, cx, cy, 0, 0, timestampMs, prev.HitStreak + 1, 0, templateToKeep);
                }

                var dtSec = dtMs / 1000.0;
                var rawVx = (cx - prev.FilteredX) / dtSec;
                var rawVy = (cy - prev.FilteredY) / dtSec;
                var rawSpeed = Math.Sqrt(rawVx * rawVx + rawVy * rawVy);

                // If the target teleported across the screen (> MaxSpeedPxPerSec), treat it as a fresh position lock.
                if (rawSpeed > MaxSpeedPxPerSec)
                {
                    return new TargetTrackState(bounds, cx, cy, 0, 0, timestampMs, HitStreak: 1, MissedFrames: 0, templateToKeep);
                }

                var predX = prev.FilteredX + prev.VelocityX * dtSec;
                var predY = prev.FilteredY + prev.VelocityY * dtSec;
                var resX = cx - predX;
                var resY = cy - predY;

                var filtX = predX + AlphaPosition * resX;
                var filtY = predY + AlphaPosition * resY;
                var vx = prev.HitStreak == 1
                    ? rawVx * 0.75
                    : (1.0 - BetaVelocity) * prev.VelocityX + BetaVelocity * rawVx;
                var vy = prev.HitStreak == 1
                    ? rawVy * 0.75
                    : (1.0 - BetaVelocity) * prev.VelocityY + BetaVelocity * rawVy;

                return new TargetTrackState(bounds, filtX, filtY, vx, vy, timestampMs, prev.HitStreak + 1, 0, templateToKeep);
            });
    }

    /// <summary>Records a miss for <paramref name="stepId"/>, decaying its confirmation streak if too many frames miss.</summary>
    public void RecordMiss(Guid stepId)
    {
        if (!_tracks.TryGetValue(stepId, out var prev)) return;
        var missed = prev.MissedFrames + 1;
        if (missed > 4)
        {
            _tracks[stepId] = prev with { HitStreak = 0, MissedFrames = missed, VelocityX = 0, VelocityY = 0 };
        }
        else
        {
            _tracks[stepId] = prev with { HitStreak = Math.Max(0, prev.HitStreak - 1), MissedFrames = missed };
        }
    }

    /// <summary>
    /// Computes the velocity-predicted ROI around the tracked target, expanding the margin along the direction of motion.
    /// </summary>
    public ScreenRect PredictSearchArea(Guid stepId, double nowMs, ScreenRect searchArea, int baseMarginPx = 64)
    {
        if (!_tracks.TryGetValue(stepId, out var state) || state.MissedFrames > 3 || nowMs - state.LastSeenMs > MaxTrackGapMs)
            return default;

        var predicted = state.PredictBounds(nowMs);
        var speedMargin = (int)Math.Min(180, Math.Round(state.SpeedPxPerSec * 0.08));
        var margin = Math.Max(baseMarginPx, baseMarginPx + speedMargin + state.MissedFrames * 24);
        var roi = new ScreenRect(
            predicted.X - margin,
            predicted.Y - margin,
            predicted.Width + margin * 2,
            predicted.Height + margin * 2);
        return roi.Intersect(searchArea);
    }

    public void Forget(Guid stepId) => _tracks.TryRemove(stepId, out _);

    public void Clear() => _tracks.Clear();
}
