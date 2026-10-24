using SmartMacro.Core.Models;
using SmartMacro.Core.Vision;

namespace SmartMacro.Core.Playback;

/// <summary>
/// What the screen showed in one frame, ready for the controller: the marker's status and position along the bar
/// (0-100 %) and the zone (fixed, or detected this frame). Only a frame with a clearly found marker AND a zone is
/// trusted; anything else (lost, two similar patches, an ignored jump, no zone) means "don't act on this frame".
/// MarkerOccluded: the marker was hidden behind your bar and the sensor used the bar's centre instead (Smart overlap
/// handling); Smart Predictive coasts through such frames for a moment instead of treating that as a jump.
/// </summary>
public readonly record struct FeedbackObservation(
    MarkerStatus Marker,
    double? MarkerPercent,
    bool ZoneFound,
    double ZoneStartPercent,
    double ZoneEndPercent,
    bool MarkerOccluded = false)
{
    public bool IsTrusted => Marker == MarkerStatus.Found && MarkerPercent is not null && ZoneFound;
}

/// <summary>What the runner has to do after a frame.</summary>
public enum FeedbackCommand
{
    /// <summary>Nothing to change.</summary>
    None,

    /// <summary>Press the key / button and keep it down.</summary>
    Press,

    /// <summary>Release the key / button.</summary>
    Release,

    /// <summary>Release it if it's down, then press it briefly once.</summary>
    Tap,
}

/// <summary>
/// The controller's answer for one frame.
/// Region: where the marker counts as being (after the dead band), or null when the frame wasn't trusted.
/// Deferred: a change was wanted but held back by the "minimum time between input changes" (it's retried next frame).
/// Waiting: the target has been missing longer than the lost timeout and the policy is "release and wait".
/// TargetLost: the target has been missing longer than the lost timeout and the policy says the loop must end.
/// Braked: (Smart Predictive) the target reversed direction and the input was flipped at once to brake the bar.
/// </summary>
public readonly record struct FeedbackDecision(
    FeedbackCommand Command,
    FeedbackRegion? Region,
    bool Deferred = false,
    bool Waiting = false,
    bool TargetLost = false,
    bool Braked = false);

/// <summary>A feedback loop "brain": the classic region rules (<see cref="FeedbackController"/>) or Smart Predictive (<see cref="PredictiveFeedbackController"/>).</summary>
public interface IFeedbackController
{
    /// <summary>True while the controller wants the key / button held down.</summary>
    bool IsHeld { get; }

    /// <summary>True while the target is missing and the controller released and waits for it.</summary>
    bool IsWaiting { get; }

    /// <summary>The marker's current region (null before the first trusted frame and while waiting).</summary>
    FeedbackRegion? Region { get; }

    /// <param name="now">Time since any fixed point (the same one for every call; paused time should not count).</param>
    FeedbackDecision Update(FeedbackObservation observation, TimeSpan now);
}

/// <summary>Picks the controller a Feedback loop step asks for.</summary>
public static class FeedbackControllers
{
    public static IFeedbackController Create(FeedbackLoopStep step, TimeSpan startedAt = default)
    {
        ArgumentNullException.ThrowIfNull(step);
        return step.ControlMode == FeedbackControlMode.SmartPredictive
            ? new PredictiveFeedbackController(PredictiveFeedbackSettings.From(step), startedAt)
            : new FeedbackController(FeedbackRules.From(step), startedAt);
    }
}

/// <summary>The rule settings the controller needs (taken from a <see cref="FeedbackLoopStep"/>).</summary>
public sealed record FeedbackRules(
    FeedbackAction WhenBefore,
    FeedbackAction WhenInside,
    FeedbackAction WhenAfter,
    double DeadBandPercent,
    TimeSpan MinInputChange,
    TimeSpan LostTimeout,
    FeedbackLostAction WhenLost,
    TimeSpan PulsePeriod = default)
{
    /// <summary>Pulse cycle used when none is set.</summary>
    public static readonly TimeSpan DefaultPulsePeriod = TimeSpan.FromMilliseconds(FeedbackLoopStep.DefaultPulsePeriodMs);

    public static FeedbackRules From(FeedbackLoopStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return new FeedbackRules(step.WhenBeforeZone, step.WhenInsideZone, step.WhenAfterZone,
            Math.Max(0, step.DeadBandPercent),
            TimeSpan.FromMilliseconds(Math.Max(0, step.MinInputChangeMs)),
            TimeSpan.FromMilliseconds(Math.Max(0, step.LostTimeoutMs)),
            step.WhenTargetLost,
            TimeSpan.FromMilliseconds(Math.Max(0, step.PulsePeriodMs)));
    }

    public FeedbackAction ActionFor(FeedbackRegion region) => region switch
    {
        FeedbackRegion.Before => WhenBefore,
        FeedbackRegion.Inside => WhenInside,
        _ => WhenAfter,
    };
}

/// <summary>
/// The "brain" of a feedback loop: turns each frame's reading into "press / release / tap / nothing".
/// Pure logic with no screen, input or clock of its own (the caller passes the time), so every rule can be unit tested
/// with scripted marker positions.
///
/// Rules, in order:
/// 1. A frame that isn't trusted (marker lost, two similar patches, ignored jump, zone missing) changes nothing.
///    Once nothing trusted has been seen for the lost timeout, the lost policy applies: release and wait for the
///    target, or end the loop (the runner releases and leaves / stops). Releasing for safety ignores the minimum
///    time between input changes.
/// 2. The marker is placed before / inside / after the zone. Dead band: it only counts as having crossed a zone edge
///    once it is the dead band (% of the bar) past that edge, so it can't flicker on/off at an edge.
/// 3. The region's rule gives the wanted state: Hold → pressed, Release → released, Keep → no change,
///    Tap → one tap when the marker moves into that region from another one (not when the loop starts or the target
///    comes back inside it, so a tap is never a guess).
/// 4. A wanted change is only made if the last change was at least the minimum time ago; otherwise it waits for a
///    later frame (Deferred).
/// </summary>
public sealed class FeedbackController : IFeedbackController
{
    private readonly FeedbackRules _rules;
    private FeedbackRegion? _region;
    private TimeSpan? _lastChangeAt;
    private TimeSpan _lastTrustedAt;
    private bool _tapPending;

    /// <param name="startedAt">The time the loop started (the lost timer counts from here until the first trusted frame).</param>
    public FeedbackController(FeedbackRules rules, TimeSpan startedAt = default)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _lastTrustedAt = startedAt;
    }

    /// <summary>True while the controller wants the key / button held down.</summary>
    public bool IsHeld { get; private set; }

    /// <summary>True while the target is missing and the controller released and waits for it ("release and wait").</summary>
    public bool IsWaiting { get; private set; }

    /// <summary>The marker's current region (null before the first trusted frame and while waiting).</summary>
    public FeedbackRegion? Region => _region;

    /// <param name="now">Time since any fixed point (the same one for every call; paused time should not count).</param>
    public FeedbackDecision Update(FeedbackObservation observation, TimeSpan now)
    {
        if (!observation.IsTrusted) return Untrusted(now);

        _lastTrustedAt = now;
        IsWaiting = false;

        var previous = _region;
        var region = Classify(observation.MarkerPercent!.Value, observation.ZoneStartPercent, observation.ZoneEndPercent, previous, _rules.DeadBandPercent);
        var action = _rules.ActionFor(region);
        if (region != previous)
        {
            // A tap needs a real move into the region (from a known other region), see rule 3.
            _tapPending = action == FeedbackAction.Tap && previous is not null;
        }

        _region = region;

        var wanted = action switch
        {
            FeedbackAction.Hold when !IsHeld => FeedbackCommand.Press,
            FeedbackAction.Release when IsHeld => FeedbackCommand.Release,
            FeedbackAction.Tap when _tapPending => FeedbackCommand.Tap,
            FeedbackAction.Pulse => PulseCommand(observation, now),
            _ => FeedbackCommand.None,
        };

        if (wanted == FeedbackCommand.None) return new FeedbackDecision(FeedbackCommand.None, region);

        if (_lastChangeAt is { } last && now - last < _rules.MinInputChange)
            return new FeedbackDecision(FeedbackCommand.None, region, Deferred: true);

        switch (wanted)
        {
            case FeedbackCommand.Press:
                IsHeld = true;
                break;
            case FeedbackCommand.Release:
                IsHeld = false;
                break;
            case FeedbackCommand.Tap:
                IsHeld = false; // a tap ends released
                _tapPending = false;
                break;
        }

        _lastChangeAt = now;
        return new FeedbackDecision(wanted, region);
    }

    /// <summary>
    /// Proportional pulsing inside the zone: the share of each pulse cycle the input is held follows how far the marker
    /// is toward the zone edge whose side holds (see <see cref="PulseDuty"/>).
    /// </summary>
    private FeedbackCommand PulseCommand(FeedbackObservation observation, TimeSpan now)
    {
        var duty = PulseDuty(observation.MarkerPercent!.Value, observation.ZoneStartPercent, observation.ZoneEndPercent,
            _rules.WhenBefore, _rules.WhenAfter);
        var period = _rules.PulsePeriod > TimeSpan.Zero ? _rules.PulsePeriod : FeedbackRules.DefaultPulsePeriod;
        var phase = (now.Ticks % period.Ticks) / (double)period.Ticks;
        var wantHeld = phase < duty;
        if (wantHeld && !IsHeld) return FeedbackCommand.Press;
        if (!wantHeld && IsHeld) return FeedbackCommand.Release;
        return FeedbackCommand.None;
    }

    /// <summary>
    /// How much of a pulse cycle to hold (0-1) for a marker inside the zone. 0 at the edge facing the "release" side,
    /// 1 at the edge facing the "hold" side, in between proportionally; values close to 0 / 1 snap to 0 / 1 so the
    /// input isn't flicked for nothing. When neither or both sides hold, it's 0.5.
    /// </summary>
    public static double PulseDuty(double position, double zoneStart, double zoneEnd, FeedbackAction whenBefore, FeedbackAction whenAfter)
    {
        var width = zoneEnd - zoneStart;
        var towardAfter = width <= 0 ? 0.5 : Math.Clamp((position - zoneStart) / width, 0, 1);
        var afterHolds = whenAfter == FeedbackAction.Hold;
        var beforeHolds = whenBefore == FeedbackAction.Hold;
        var duty = afterHolds == beforeHolds ? 0.5 : afterHolds ? towardAfter : 1 - towardAfter;
        if (duty < 0.15) return 0;
        if (duty > 0.85) return 1;
        return duty;
    }

    private FeedbackDecision Untrusted(TimeSpan now)
    {
        if (now - _lastTrustedAt < _rules.LostTimeout)
            return new FeedbackDecision(FeedbackCommand.None, null); // a short gap: hold still, change nothing

        // Lost for too long. Whatever happens next, the old region and any pending tap are stale.
        _region = null;
        _tapPending = false;

        if (_rules.WhenLost != FeedbackLostAction.ReleaseAndWait)
            return new FeedbackDecision(FeedbackCommand.None, null, TargetLost: true); // the runner releases and ends

        IsWaiting = true;
        if (!IsHeld) return new FeedbackDecision(FeedbackCommand.None, null, Waiting: true);

        // Safety release: not delayed by the minimum time between changes.
        IsHeld = false;
        _lastChangeAt = now;
        return new FeedbackDecision(FeedbackCommand.Release, null, Waiting: true);
    }

    /// <summary>
    /// Before / inside / after the zone, with a dead band: when the raw answer differs from the previous region, the
    /// marker has to be at least <paramref name="deadBand"/> % past the edge it crossed, otherwise the previous region
    /// is kept. With no previous region (first frame) the raw answer is used.
    /// </summary>
    public static FeedbackRegion Classify(double position, double zoneStart, double zoneEnd, FeedbackRegion? previous, double deadBand)
    {
        var raw = position < zoneStart ? FeedbackRegion.Before
            : position > zoneEnd ? FeedbackRegion.After
            : FeedbackRegion.Inside;
        if (previous is not { } before || raw == before || deadBand <= 0) return raw;

        var clearlyCrossed = (before, raw) switch
        {
            (FeedbackRegion.Inside, FeedbackRegion.Before) => position <= zoneStart - deadBand,
            (FeedbackRegion.Inside, FeedbackRegion.After) => position >= zoneEnd + deadBand,
            (FeedbackRegion.Before, _) => position >= zoneStart + deadBand,  // into the zone, or right through it
            (FeedbackRegion.After, _) => position <= zoneEnd - deadBand,
            _ => true,
        };
        if (!clearlyCrossed) return before;

        // Jumped right across the zone but not clearly past its far edge: count it as inside for now.
        if (before == FeedbackRegion.Before && raw == FeedbackRegion.After && position < zoneEnd + deadBand) return FeedbackRegion.Inside;
        if (before == FeedbackRegion.After && raw == FeedbackRegion.Before && position > zoneStart - deadBand) return FeedbackRegion.Inside;
        return raw;
    }
}
