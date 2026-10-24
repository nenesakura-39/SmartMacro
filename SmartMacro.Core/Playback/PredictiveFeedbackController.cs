using SmartMacro.Core.Models;
using SmartMacro.Core.Vision;

namespace SmartMacro.Core.Playback;

/// <summary>The settings <see cref="PredictiveFeedbackController"/> needs (taken from a <see cref="FeedbackLoopStep"/>).</summary>
/// <param name="BarLengthPx">Length of the bar along its direction, in pixels (positions are converted from % to px).</param>
/// <param name="FollowMode">
/// True when the zone is found by its colour every frame (the zone is the bar the player moves, the marker is the target
/// it must stay on – fishing / reeling minigames). False with a fixed zone: the zone is the target, the marker is moved.
/// </param>
/// <param name="HoldDirection">
/// +1 when holding the input moves the controlled thing toward higher positions (right / down), -1 toward lower ones,
/// 0 when the Before / After rules don't say (then the loop never presses; the validator reports it).
/// </param>
public sealed record PredictiveFeedbackSettings(
    double BarLengthPx,
    bool FollowMode,
    int HoldDirection,
    double LeadSeconds,
    double DeadzonePx,
    bool UsePwm,
    TimeSpan PulsePeriod,
    TimeSpan MinInputChange,
    TimeSpan LostTimeout,
    FeedbackLostAction WhenLost,
    double DeadBandPercent)
{
    /// <summary>Weight of the newest speed sample in the exponential moving average (0..1).</summary>
    public const double VelocitySmoothing = 0.5;

    /// <summary>A direction change of the target only counts as a "reversal" (instant brake) above this speed.</summary>
    public const double ReversalMinSpeedPxPerSecond = 40;

    /// <summary>
    /// The hold direction from the step's rules: in follow mode the target is "ahead" of the bar when the marker is AFTER
    /// the zone; with a fixed zone the target (zone) is ahead of the marker when the marker is BEFORE the zone. Holding
    /// pushes toward the target where that side's rule is Hold.
    /// </summary>
    public static int HoldDirectionOf(FeedbackLoopStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        var follow = step.ZoneMode == FeedbackZoneMode.DetectedByColor;
        var ahead = follow ? step.WhenAfterZone : step.WhenBeforeZone;   // rule while the target is at a HIGHER position
        var behind = follow ? step.WhenBeforeZone : step.WhenAfterZone;  // rule while the target is at a LOWER position
        var aheadHolds = ahead == FeedbackAction.Hold;
        var behindHolds = behind == FeedbackAction.Hold;
        if (aheadHolds == behindHolds) return 0;
        return aheadHolds ? 1 : -1;
    }

    public static PredictiveFeedbackSettings From(FeedbackLoopStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        var length = step.Orientation == BarOrientation.Horizontal ? step.RegionWidth : step.RegionHeight;
        return new PredictiveFeedbackSettings(
            Math.Max(1, length - 1), // MarkerTracker / ZoneFinder: 0 % = first pixel, 100 % = last pixel
            step.ZoneMode == FeedbackZoneMode.DetectedByColor,
            HoldDirectionOf(step),
            Math.Clamp(step.LeadPredictionFactor, 0, 1),
            Math.Max(0, step.DeadzonePx),
            step.UsePwmMicroPulse,
            TimeSpan.FromMilliseconds(Math.Max(1, step.PulsePeriodMs)),
            TimeSpan.FromMilliseconds(Math.Max(0, step.MinInputChangeMs)),
            TimeSpan.FromMilliseconds(Math.Max(0, step.LostTimeoutMs)),
            step.WhenTargetLost,
            Math.Max(0, step.DeadBandPercent));
    }
}

/// <summary>
/// Feedback Loop 2.0 "Smart Predictive (PD + Brake)": for bars with momentum (Fisch's catch bar speeds up while the
/// button is held and falls back when it's released) and targets that dart around. A plain "hold while the target is
/// ahead" controller reacts too late: it holds until AFTER the bar crossed the target, overshoots and oscillates.
///
/// Every trusted frame (same input as the classic controller; positions converted to pixels):
/// 1. Target and bar positions (centroids from the sensor). Which one is which: <see cref="PredictiveFeedbackSettings.FollowMode"/>.
/// 2. Speeds (px/s) from the change since the last frame ÷ dt, smoothed with an exponential moving average.
/// 3. PD lead prediction: predictedError = (target + vTarget × lead) − (bar + vBar × lead). A bar racing toward the target
///    already "arrives" in the prediction, so the input is released (braking) BEFORE it crosses – and pressed again early
///    when the target runs away.
/// 4. Outside the dead zone (|predicted error| &gt; DeadzonePx): full hold toward the target, or full release.
///    Inside it: PWM micro-pulsing – held for a share of every pulse period (duty = hover duty + proportional part), so
///    the bar hovers against gravity. The hover duty adapts slowly: a bar sinking while hovering gets a bit more.
///    Without PWM the current state is kept inside the dead zone.
/// 5. Direction-reversal brake: when the target's speed changes sign above 40 px/s, the input flips at once toward the
///    target's new direction, even inside the minimum time between changes.
/// 6. Everything else respects the minimum time between input changes. Lost-target handling is the same as the classic
///    controller (short gaps change nothing; after the lost timeout: release and wait, or end the loop).
/// Pure logic (the caller passes the time), unit tested with scripted positions.
/// </summary>
public sealed class PredictiveFeedbackController : IFeedbackController
{
    /// <summary>Starting duty cycle for hovering (share of each pulse period held).</summary>
    public const double InitialHoverDuty = 0.5;

    // How fast the hover duty follows drift: 100 px/s of drift for 0.1 s moves it by 0.02.
    private const double HoverAdaptPerPx = 0.002;

    private readonly PredictiveFeedbackSettings _s;
    private FeedbackRegion? _region;
    private TimeSpan? _lastChangeAt;
    private TimeSpan _lastTrustedAt;
    private TimeSpan? _prevTime;
    private double _prevTarget;
    private double _prevPlayer;
    private bool _hasVelocity;

    public PredictiveFeedbackController(PredictiveFeedbackSettings settings, TimeSpan startedAt = default)
    {
        _s = settings ?? throw new ArgumentNullException(nameof(settings));
        _lastTrustedAt = startedAt;
    }

    public bool IsHeld { get; private set; }
    public bool IsWaiting { get; private set; }
    public FeedbackRegion? Region => _region;

    // ---- Telemetry (last trusted frame) ----

    /// <summary>Smoothed target speed, px/s (+ = toward higher positions: right / down).</summary>
    public double TargetVelocity { get; private set; }

    /// <summary>Smoothed bar (controlled thing) speed, px/s.</summary>
    public double PlayerVelocity { get; private set; }

    /// <summary>playerVelocity − targetVelocity, px/s.</summary>
    public double RelativeVelocity => PlayerVelocity - TargetVelocity;

    /// <summary>Target position, px along the bar.</summary>
    public double TargetPosition { get; private set; }

    /// <summary>Bar (controlled thing) position, px along the bar.</summary>
    public double PlayerPosition { get; private set; }

    /// <summary>The predicted gap "lead" seconds ahead, px (+ = the target will be at a higher position than the bar).</summary>
    public double PredictedError { get; private set; }

    /// <summary>The current PWM duty cycle while hovering (0..1), or null outside the dead zone / without PWM.</summary>
    public double? Duty { get; private set; }

    /// <summary>The adaptive hover duty (0.1..0.9).</summary>
    public double HoverDuty { get; private set; } = InitialHoverDuty;

    /// <summary>How many times a target reversal flipped the input at once.</summary>
    public int Brakes { get; private set; }

    public FeedbackDecision Update(FeedbackObservation observation, TimeSpan now)
    {
        if (!observation.IsTrusted) return Untrusted(now);

        _lastTrustedAt = now;
        IsWaiting = false;

        var markerPercent = observation.MarkerPercent!.Value;
        var pxPerPercent = _s.BarLengthPx / 100.0;
        var marker = markerPercent * pxPerPercent;
        var zoneCenter = (observation.ZoneStartPercent + observation.ZoneEndPercent) * 0.5 * pxPerPercent;
        var target = _s.FollowMode ? marker : zoneCenter;
        var player = _s.FollowMode ? zoneCenter : marker;
        _region = FeedbackController.Classify(markerPercent, observation.ZoneStartPercent, observation.ZoneEndPercent, _region, _s.DeadBandPercent);

        // 2. Speeds (EMA of the finite differences).
        var reversal = false;
        var dt = 0.0;
        if (_prevTime is { } previous && now > previous)
        {
            dt = (now - previous).TotalSeconds;
            var previousTargetVelocity = TargetVelocity;
            var a = PredictiveFeedbackSettings.VelocitySmoothing;
            TargetVelocity = a * ((target - _prevTarget) / dt) + (1 - a) * TargetVelocity;
            PlayerVelocity = a * ((player - _prevPlayer) / dt) + (1 - a) * PlayerVelocity;
            reversal = _hasVelocity
                       && previousTargetVelocity != 0
                       && Math.Sign(TargetVelocity) != Math.Sign(previousTargetVelocity)
                       && Math.Abs(TargetVelocity) > PredictiveFeedbackSettings.ReversalMinSpeedPxPerSecond;
            _hasVelocity = true;
        }

        _prevTime = now;
        _prevTarget = target;
        _prevPlayer = player;
        TargetPosition = target;
        PlayerPosition = player;

        // 3. PD lead prediction.
        PredictedError = target + TargetVelocity * _s.LeadSeconds - (player + PlayerVelocity * _s.LeadSeconds);
        var d = _s.HoldDirection;
        var push = d * PredictedError; // > 0: holding moves the bar toward where the target will be
        Duty = null;

        bool wantHeld;
        var urgent = false;
        if (d == 0)
        {
            wantHeld = false; // the rules don't say which way holding goes: never press (the validator reports it)
        }
        else if (reversal)
        {
            // 5. The target turned around: brake the bar's momentum right now, toward the target's new direction.
            wantHeld = d * TargetVelocity > 0;
            urgent = true;
        }
        else if (push > _s.DeadzonePx)
        {
            wantHeld = true;
        }
        else if (push < -_s.DeadzonePx)
        {
            wantHeld = false;
        }
        else if (_s.UsePwm)
        {
            // 4. Hover: proportional duty around the adaptive hover duty, applied by the phase of the pulse period.
            if (dt > 0)
                HoverDuty = Math.Clamp(HoverDuty - HoverAdaptPerPx * d * PlayerVelocity * dt, 0.1, 0.9);
            var proportional = _s.DeadzonePx > 0 ? 0.5 * push / _s.DeadzonePx : 0;
            var duty = Math.Clamp(HoverDuty + proportional, 0.05, 0.95);
            Duty = duty;
            var period = _s.PulsePeriod > TimeSpan.Zero ? _s.PulsePeriod : FeedbackRules.DefaultPulsePeriod;
            var phase = now.Ticks % period.Ticks / (double)period.Ticks;
            wantHeld = phase < duty;
        }
        else
        {
            wantHeld = IsHeld;
        }

        var command = wantHeld == IsHeld ? FeedbackCommand.None : wantHeld ? FeedbackCommand.Press : FeedbackCommand.Release;
        if (command == FeedbackCommand.None) return new FeedbackDecision(FeedbackCommand.None, _region);

        if (!urgent && _lastChangeAt is { } last && now - last < _s.MinInputChange)
            return new FeedbackDecision(FeedbackCommand.None, _region, Deferred: true);

        IsHeld = wantHeld;
        _lastChangeAt = now;
        if (urgent) Brakes++;
        return new FeedbackDecision(command, _region, Braked: urgent);
    }

    private FeedbackDecision Untrusted(TimeSpan now)
    {
        // Speeds across a gap would be nonsense: start measuring again with the next trusted frame.
        _prevTime = null;
        _hasVelocity = false;
        TargetVelocity = 0;
        PlayerVelocity = 0;

        if (now - _lastTrustedAt < _s.LostTimeout)
            return new FeedbackDecision(FeedbackCommand.None, null); // a short gap: change nothing

        _region = null;
        if (_s.WhenLost != FeedbackLostAction.ReleaseAndWait)
            return new FeedbackDecision(FeedbackCommand.None, null, TargetLost: true);

        IsWaiting = true;
        if (!IsHeld) return new FeedbackDecision(FeedbackCommand.None, null, Waiting: true);

        IsHeld = false; // safety release: not delayed by the minimum time between changes
        _lastChangeAt = now;
        return new FeedbackDecision(FeedbackCommand.Release, null, Waiting: true);
    }
}
