using SmartMacro.Core.Models;
using SmartMacro.Core.Validation;

namespace SmartMacro.Core.Workflow;

/// <summary>What "Clean up mouse paths" did.</summary>
public sealed record MousePathCleanupResult(List<MacroStep> Steps, int RemovedSteps, int CollapsedRuns);

/// <summary>
/// "Convert recording to clean steps": turns long runs of recorded "Delay + Move mouse" steps (one every few ms while
/// the mouse travelled) into at most Delay + Move + Delay. Pure list editing, unit tested.
///
/// Rules:
/// - A run is a block of enabled Delay / Move mouse steps without comments, with at least one Move.
///   Steps with a comment or turned off are left alone (the user marked them on purpose).
/// - While a mouse button is held down by an earlier "Press" click step (a drag), nothing is changed: the path
///   matters there (sliders, dragging items).
/// - Only the run's LAST position is kept. If the step after the run clicks / scrolls at its own fixed position (or
///   on the last found spot), the Move isn't needed at all and goes too.
/// - The total waiting time is kept: the delays before the kept Move become one Delay, the delays after it another.
/// </summary>
public static class StepCleanup
{
    public static MousePathCleanupResult CollapseMouseMoves(IReadOnlyList<MacroStep> steps) =>
        CollapseMouseMoves(steps, 0, steps.Count - 1);

    /// <summary>Same, but only for runs that lie completely between <paramref name="first"/> and <paramref name="last"/> (indexes).</summary>
    public static MousePathCleanupResult CollapseMouseMoves(IReadOnlyList<MacroStep> steps, int first, int last)
    {
        ArgumentNullException.ThrowIfNull(steps);
        first = Math.Max(0, first);
        last = Math.Min(steps.Count - 1, last);

        var result = new List<MacroStep>(steps.Count);
        var held = new HashSet<MacroMouseButton>();
        var removed = 0;
        var runs = 0;
        var i = 0;
        while (i < steps.Count)
        {
            var step = steps[i];
            if (i < first || i > last || held.Count > 0 || !IsRunPart(step))
            {
                TrackButtons(step, held);
                result.Add(step);
                i++;
                continue;
            }

            var end = i;
            while (end + 1 <= last && IsRunPart(steps[end + 1])) end++;

            var lastMove = -1;
            for (var k = i; k <= end; k++)
            {
                if (steps[k] is MouseMoveStep) lastMove = k;
            }

            var moves = 0;
            for (var k = i; k <= end; k++)
            {
                if (steps[k] is MouseMoveStep) moves++;
            }

            var nextStep = end + 1 < steps.Count ? steps[end + 1] : null;
            var keepMove = lastMove >= 0 && !PositionsItself(nextStep);

            // Nothing to gain: a single Move that stays and at most one delay on each side.
            if (lastMove < 0 || (moves == 1 && keepMove && CountDelays(steps, i, lastMove) <= 1 && CountDelays(steps, lastMove, end + 1) <= 1))
            {
                for (var k = i; k <= end; k++) result.Add(steps[k]);
                i = end + 1;
                continue;
            }

            var before = SumDelays(steps, i, keepMove ? lastMove : end + 1);
            var after = keepMove ? SumDelays(steps, lastMove, end + 1) : 0;
            var added = 0;
            added += AddDelay(result, before);
            if (keepMove)
            {
                result.Add(steps[lastMove]);
                added++;
            }

            added += AddDelay(result, after);
            removed += end - i + 1 - added;
            runs++;
            i = end + 1;
        }

        return new MousePathCleanupResult(result, removed, runs);
    }

    private static bool IsRunPart(MacroStep? step) =>
        step is (DelayStep or MouseMoveStep) and { Enabled: true, Comment: null or "" };

    /// <summary>True if the step moves the cursor to its own spot first, so a Move right before it is not needed.</summary>
    private static bool PositionsItself(MacroStep? step) => step switch
    {
        MouseClickStep { Enabled: true } c => c.UseLastFoundText || !c.UseCurrentPosition,
        MouseScrollStep { Enabled: true } s => !s.UseCurrentPosition,
        _ => false,
    };

    private static void TrackButtons(MacroStep step, HashSet<MacroMouseButton> held)
    {
        if (step is not MouseClickStep { Enabled: true } c) return;
        if (c.Action == MouseClickAction.Down) held.Add(c.Button);
        else if (c.Action == MouseClickAction.Up) held.Remove(c.Button);
    }

    private static int CountDelays(IReadOnlyList<MacroStep> steps, int from, int toExclusive)
    {
        var count = 0;
        for (var k = from; k < toExclusive; k++)
        {
            if (steps[k] is DelayStep) count++;
        }

        return count;
    }

    private static long SumDelays(IReadOnlyList<MacroStep> steps, int from, int toExclusive)
    {
        long total = 0;
        for (var k = from; k < toExclusive; k++)
        {
            if (steps[k] is DelayStep d) total += Math.Max(0, d.DurationMs);
        }

        return total;
    }

    /// <summary>Adds the time as Delay steps (split if longer than the 1-hour limit). Returns how many were added.</summary>
    private static int AddDelay(List<MacroStep> result, long totalMs)
    {
        var added = 0;
        while (totalMs > 0)
        {
            var part = (int)Math.Min(totalMs, MacroValidator.MaxDelayMs);
            result.Add(new DelayStep { DurationMs = part });
            totalMs -= part;
            added++;
        }

        return added;
    }
}
