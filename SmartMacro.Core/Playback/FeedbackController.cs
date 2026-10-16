using SmartMacro.Core.Models;
using SmartMacro.Core.Vision;

namespace SmartMacro.Core.Playback;

/// <summary>
/// What the screen showed in one frame, ready for the controller: the marker's status and position along the bar
/// (0-100 %) and the zone (fixed, or detected this frame). Only a frame with a clearly found marker AND a zone is
/// trusted; anything else (lost, two similar patches, an ignored jump, no zone) means "don't act on this frame".
/// </summary>
public readonly record struct FeedbackObservation(
    MarkerStatus Marker,
    double? MarkerPercent,
    bool ZoneFound,
    double ZoneStartPercent,
    double ZoneEndPercent)
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
/// </summary>
public readonly record struct FeedbackDecision(
    FeedbackCommand Command,
    FeedbackRegion? Region,
    bool Deferred = false,
    bool Waiting = false,
    bool TargetLost = false);

/// <summary>The rule settings the controller needs (taken from a <see cref="FeedbackLoopStep"/>).</summary>
public sealed record FeedbackRules(
    FeedbackAction WhenBefore,
    FeedbackAction WhenInside,
    FeedbackAction WhenAfter,
    double DeadBandPercent,
    TimeSpan MinInputChange,
    TimeSpan LostTimeout,
    FeedbackLostAction WhenLost)
{
    public static FeedbackRules From(FeedbackLoopStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return new FeedbackRules(step.WhenBeforeZone, step.WhenInsideZone, step.WhenAfterZone,
            Math.Max(0, step.DeadBandPercent),
            TimeSpan.FromMilliseconds(Math.Max(0, step.MinInputChangeMs)),
            TimeSpan.FromMilliseconds(Math.Max(0, step.LostTimeoutMs)),
            step.WhenTargetLost);
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
public sealed class FeedbackController
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
