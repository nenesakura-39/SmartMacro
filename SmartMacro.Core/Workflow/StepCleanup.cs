using SmartMacro.Core.Models;
using SmartMacro.Core.Validation;

namespace SmartMacro.Core.Workflow;

/// <summary>What "Clean up mouse paths" did.</summary>
public sealed record MousePathCleanupResult(List<MacroStep> Steps, int RemovedSteps, int CollapsedRuns);

/// <summary>
/// What "✨ Auto-Fix block structure" changed. <see cref="Repairs"/> has one plain sentence per kind of fix (e.g.
/// "Removed 2 orphan 'End If' steps."). <see cref="FirstChangedIndex"/> is where the first change landed in the new list
/// (-1 when nothing changed), so the editor can scroll there.
/// </summary>
public sealed record BlockRepairResult(IReadOnlyList<string> Repairs, int RemovedSteps, int AddedSteps, int FirstChangedIndex)
{
    public const string NothingToFixText = "The If / Repeat blocks already match up. Nothing to fix.";

    public bool Changed => RemovedSteps > 0 || AddedSteps > 0;

    /// <summary>All the fixes in one line, or <see cref="NothingToFixText"/>.</summary>
    public string Summary => Changed ? string.Join(" ", Repairs) : NothingToFixText;
}

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

    // ---------------- ✨ Auto-Fix block structure ----------------

    /// <summary>
    /// Fixes If / Else / End If and Repeat / End Repeat markers that don't match up, right inside
    /// <paramref name="steps"/>. Normal steps and the If / Repeat steps themselves are never removed or reordered;
    /// only the Else / End markers are dropped or added. Rules, going top to bottom:
    /// - An Else / End If / End Repeat with no open If / Repeat of its kind above it is an orphan and gets removed
    ///   (e.g. the two stray End Ifs at the top of an old macro).
    /// - A second Else in the same If gets removed (an If can only have one).
    /// - If the marker's block is open but another block opened inside it was never closed (blocks overlap, like
    ///   Repeat, If, End Repeat), the missing End for the inner block is added right before the marker.
    /// - Blocks still open at the end get their End If / End Repeat added at the very end (innermost first).
    /// Afterwards <see cref="BlockStructure.Analyze"/> finds no problems, except "nested too deeply", which can't be
    /// guessed. Running it on a list that's already fine changes nothing. Disabled markers count like in the analyzer.
    /// </summary>
    public static BlockRepairResult RepairBlockStructure(IList<MacroStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        var output = new List<MacroStep>(steps.Count + 4);
        var open = new List<OpenBlock>(); // innermost last
        int orphanElse = 0, orphanEndIf = 0, orphanEndRepeat = 0, extraElse = 0;
        int earlyEndIf = 0, earlyEndRepeat = 0, finalEndIf = 0, finalEndRepeat = 0;
        var firstChanged = -1;

        void MarkChanged()
        {
            if (firstChanged < 0) firstChanged = output.Count;
        }

        // Closes every block opened inside open[target] (they overlap the marker we just reached).
        void CloseInnerBlocks(int target)
        {
            while (open.Count - 1 > target)
            {
                MarkChanged();
                var inner = open[^1];
                open.RemoveAt(open.Count - 1);
                output.Add(inner.IsIf ? new EndIfStep() : new EndRepeatStep());
                if (inner.IsIf) earlyEndIf++;
                else earlyEndRepeat++;
            }
        }

        foreach (var step in steps)
        {
            switch (step)
            {
                case IfStep or RepeatStep:
                    open.Add(new OpenBlock(step is IfStep));
                    output.Add(step);
                    break;

                case ElseStep:
                {
                    var target = FindOpen(open, isIf: true);
                    if (target < 0)
                    {
                        MarkChanged();
                        orphanElse++;
                        break;
                    }

                    CloseInnerBlocks(target);
                    if (open[target].HasElse)
                    {
                        MarkChanged();
                        extraElse++;
                        break;
                    }

                    open[target].HasElse = true;
                    output.Add(step);
                    break;
                }

                case EndIfStep or EndRepeatStep:
                {
                    var isIf = step is EndIfStep;
                    var target = FindOpen(open, isIf);
                    if (target < 0)
                    {
                        MarkChanged();
                        if (isIf) orphanEndIf++;
                        else orphanEndRepeat++;
                        break;
                    }

                    CloseInnerBlocks(target);
                    open.RemoveAt(target);
                    output.Add(step);
                    break;
                }

                default:
                    output.Add(step);
                    break;
            }
        }

        // Whatever is still open needs its End at the very end (innermost first).
        for (var k = open.Count - 1; k >= 0; k--)
        {
            MarkChanged();
            output.Add(open[k].IsIf ? new EndIfStep() : new EndRepeatStep());
            if (open[k].IsIf) finalEndIf++;
            else finalEndRepeat++;
        }

        var repairs = new List<string>();
        if (orphanEndIf > 0) repairs.Add($"Removed {StepCount(orphanEndIf, "orphan 'End If'")} (no If was open above).");
        if (orphanEndRepeat > 0) repairs.Add($"Removed {StepCount(orphanEndRepeat, "orphan 'End Repeat'")} (no Repeat was open above).");
        if (orphanElse > 0) repairs.Add($"Removed {StepCount(orphanElse, "orphan 'Else'")} (no If was open above).");
        if (extraElse > 0) repairs.Add($"Removed {StepCount(extraElse, "extra 'Else'")} (an If can only have one Else).");
        if (earlyEndIf > 0) repairs.Add($"Added {StepCount(earlyEndIf, "missing 'End If'")} where an inner If overlapped its outer block.");
        if (earlyEndRepeat > 0) repairs.Add($"Added {StepCount(earlyEndRepeat, "missing 'End Repeat'")} where an inner Repeat overlapped its outer block.");
        if (finalEndIf > 0) repairs.Add($"Added {StepCount(finalEndIf, "missing 'End If'")} at the end to close unclosed If blocks.");
        if (finalEndRepeat > 0) repairs.Add($"Added {StepCount(finalEndRepeat, "missing 'End Repeat'")} at the end to close unclosed Repeat blocks.");

        var removed = orphanElse + orphanEndIf + orphanEndRepeat + extraElse;
        var added = earlyEndIf + earlyEndRepeat + finalEndIf + finalEndRepeat;
        if (removed + added > 0)
        {
            steps.Clear();
            foreach (var step in output) steps.Add(step);
        }

        return new BlockRepairResult(repairs, removed, added, removed + added > 0 ? firstChanged : -1);
    }

    /// <summary>True when "✨ Auto-Fix block structure" would change something (the list itself isn't touched).</summary>
    public static bool NeedsBlockRepair(IReadOnlyList<MacroStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (!steps.Any(WorkflowSteps.IsBlockStep)) return false;
        return RepairBlockStructure(steps.ToList()).Changed;
    }

    private sealed class OpenBlock(bool isIf)
    {
        public bool IsIf { get; } = isIf;

        public bool HasElse { get; set; }
    }

    // The innermost open block of the wanted kind, or -1 (index into the open list).
    private static int FindOpen(List<OpenBlock> open, bool isIf)
    {
        for (var k = open.Count - 1; k >= 0; k--)
        {
            if (open[k].IsIf == isIf) return k;
        }

        return -1;
    }

    private static string StepCount(int count, string what) => $"{count} {what} step{(count == 1 ? "" : "s")}";

    // A Move to "the last found spot" or "by an offset" isn't a recorded cursor point, so it's never folded away.
    private static bool IsRunPart(MacroStep? step) =>
        step is (DelayStep or MouseMoveStep { TargetMode: ClickTargetMode.Coordinates }) and { Enabled: true, Comment: null or "" };

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
