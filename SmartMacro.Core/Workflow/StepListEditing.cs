using SmartMacro.Core.Models;

namespace SmartMacro.Core.Workflow;

/// <summary>Where the editor puts a new step.</summary>
public enum InsertPosition
{
    /// <summary>Right after the selected step (at the end when nothing is selected).</summary>
    AfterSelected,

    /// <summary>After the last step, wherever the selection is.</summary>
    AtEnd,
}

/// <summary>Where the selected steps should go.</summary>
public enum MoveDirection
{
    Up,
    Down,
    ToTop,
    ToBottom,
}

/// <summary>
/// The answer to "may these steps move there?". When <see cref="Allowed"/> is true, the steps from
/// <see cref="First"/> to <see cref="Last"/> (a contiguous range, maybe grown to whole blocks) end up starting at
/// <see cref="NewFirst"/>. When it's false, <see cref="Problem"/> says why in plain English (null = nothing to do,
/// e.g. already at the top).
/// </summary>
public sealed record MovePlan(bool Allowed, int First, int Last, int NewFirst, string? Problem = null)
{
    public int Count => Last - First + 1;

    /// <summary>True when the range was grown to take whole If / Repeat blocks along.</summary>
    public bool TookWholeBlocks { get; init; }

    public static MovePlan Nothing(int first, int last) => new(false, first, last, first);

    public static MovePlan Refused(int first, int last, string problem) => new(false, first, last, first, problem);
}

/// <summary>
/// Pure rules for adding and moving steps in the editor's step list, kept in Core so they can be unit tested.
/// The editor (WPF) only applies the result to its list.
///
/// Safety rule for If / Else / End If and Repeat / End Repeat: a move is refused when the blocks matched up before
/// and wouldn't afterwards, so the editor can never quietly break a working workflow. (When the blocks are already
/// broken, moves are allowed so the user can fix them.) "Move to top / bottom" and drag-and-drop take a whole block
/// along when an If / Repeat (or its End) is part of the selection; Move up / down move exactly the selected steps,
/// one place at a time, so a block can still be made bigger or smaller.
/// </summary>
public static class StepListEditing
{
    /// <summary>Index at which a new step is inserted.</summary>
    /// <param name="count">Number of steps in the list.</param>
    /// <param name="selectedIndex">Index of the selected step, or -1 / out of range when nothing is selected.</param>
    public static int InsertIndex(int count, int selectedIndex, InsertPosition position)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (position == InsertPosition.AtEnd || selectedIndex < 0 || selectedIndex >= count) return count;
        return selectedIndex + 1;
    }

    /// <summary>
    /// Index at which a new step is inserted, taking <c>If / Else / End If</c> and <c>Repeat / End Repeat</c> blocks
    /// into account so adding a nested <c>If</c>, <c>Repeat</c>, or <c>Else</c> while a block header or inner step is
    /// selected inserts inside the current block right after the selected step instead of at the bottom of the macro.
    /// </summary>
    public static int InsertIndex(IReadOnlyList<MacroStep> steps, int selectedIndex, InsertPosition position, StepKind? newKind = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (selectedIndex < 0 || selectedIndex >= steps.Count) return steps.Count;

        var blocks = BlockStructure.Analyze(steps);
        var selected = steps[selectedIndex];

        if (newKind == StepKind.Else && selected is EndIfStep &&
            (position == InsertPosition.AfterSelected || blocks.StartOf(selectedIndex) >= 0))
            return selectedIndex;

        var insideOrOpener = selected is IfStep or ElseStep or RepeatStep || blocks.Depth[selectedIndex] > 0;
        if (insideOrOpener && (position == InsertPosition.AfterSelected || newKind is StepKind.If or StepKind.Repeat or StepKind.Else))
            return selectedIndex + 1;

        return InsertIndex(steps.Count, selectedIndex, position);
    }

    /// <summary>Plans Move up / down / to top / to bottom for the contiguous range first..last.</summary>
    public static MovePlan PlanMove(IReadOnlyList<MacroStep> steps, int first, int last, MoveDirection direction)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (!IsRange(steps, first, last)) return MovePlan.Nothing(Math.Max(0, first), Math.Max(0, last));

        var tookBlocks = false;
        if (direction is MoveDirection.ToTop or MoveDirection.ToBottom)
            (first, last, tookBlocks) = GrowToWholeBlocks(steps, first, last);

        var count = last - first + 1;
        var newFirst = direction switch
        {
            MoveDirection.Up => first - 1,
            MoveDirection.Down => first + 1,
            MoveDirection.ToTop => 0,
            _ => steps.Count - count,
        };

        return Check(steps, first, last, newFirst, tookBlocks);
    }

    /// <summary>
    /// Plans a drag-and-drop: the range first..last is dropped in front of the step that is at
    /// <paramref name="dropIndex"/> now (<c>steps.Count</c> = after the last step).
    /// </summary>
    public static MovePlan PlanDrop(IReadOnlyList<MacroStep> steps, int first, int last, int dropIndex)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (!IsRange(steps, first, last) || dropIndex < 0 || dropIndex > steps.Count)
            return MovePlan.Nothing(Math.Max(0, first), Math.Max(0, last));

        var (start, end, tookBlocks) = GrowToWholeBlocks(steps, first, last);

        // Dropping inside the range itself (or right next to it) changes nothing.
        if (dropIndex >= start && dropIndex <= end + 1) return MovePlan.Nothing(start, end);

        var newFirst = dropIndex < start ? dropIndex : dropIndex - (end - start + 1);
        return Check(steps, start, end, newFirst, tookBlocks);
    }

    /// <summary>The list after a move (for checking, tests and anyone without an observable list).</summary>
    public static List<T> Apply<T>(IReadOnlyList<T> items, MovePlan plan)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(plan);
        var result = items.ToList();
        if (!plan.Allowed) return result;

        var moving = result.GetRange(plan.First, plan.Count);
        result.RemoveRange(plan.First, plan.Count);
        result.InsertRange(plan.NewFirst, moving);
        return result;
    }

    /// <summary>
    /// If the range contains an If / Repeat (or an Else / End) whose partner lies outside it, grows the range until it
    /// holds whole blocks. Only done when the blocks match up (otherwise the range is left as it is).
    /// </summary>
    public static (int First, int Last, bool Grew) GrowToWholeBlocks(IReadOnlyList<MacroStep> steps, int first, int last)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var blocks = BlockStructure.Analyze(steps);
        if (!blocks.IsValid) return (first, last, false);

        int start = first, end = last;
        var changed = true;
        while (changed)
        {
            changed = false;
            for (var i = start; i <= end; i++)
            {
                var blockStart = steps[i] is IfStep or RepeatStep ? i : blocks.StartOf(i);
                if (blockStart < 0) continue;
                var blockEnd = blocks.EndOf(blockStart);
                if (blockStart < start) { start = blockStart; changed = true; }
                if (blockEnd > end) { end = blockEnd; changed = true; }
            }
        }

        return (start, end, start != first || end != last);
    }

    private static MovePlan Check(IReadOnlyList<MacroStep> steps, int first, int last, int newFirst, bool tookBlocks)
    {
        var count = last - first + 1;
        if (newFirst < 0 || newFirst + count > steps.Count || newFirst == first) return MovePlan.Nothing(first, last);

        var plan = new MovePlan(true, first, last, newFirst) { TookWholeBlocks = tookBlocks };
        var before = BlockStructure.Analyze(steps);
        if (!before.IsValid) return plan; // already broken: let the user fix it

        var afterSteps = Apply(steps, plan);
        var after = BlockStructure.Analyze(afterSteps);
        if (after.IsValid && SameBlockPairs(steps, before, afterSteps, after)) return plan;

        var what = count == 1 ? "this step" : "these steps";
        var reason = after.Problems.Count > 0
            ? after.Problems[0].Message.TrimEnd('.')
            : "it would cross another If / Else / End If boundary";
        return MovePlan.Refused(first, last,
            $"Moving {what} there would break the If / Repeat blocks ({reason}). " +
            "To move a whole block, select it from its If / Repeat to its End (or use Move to top / bottom, or drag it), " +
            "or move the markers one place at a time.");
    }

    private static bool SameBlockPairs(
        IReadOnlyList<MacroStep> beforeSteps, BlockStructure before,
        IReadOnlyList<MacroStep> afterSteps, BlockStructure after)
    {
        for (var i = 0; i < beforeSteps.Count; i++)
        {
            if (beforeSteps[i] is not (IfStep or RepeatStep)) continue;
            var j = -1;
            for (var k = 0; k < afterSteps.Count; k++)
            {
                if (ReferenceEquals(afterSteps[k], beforeSteps[i])) { j = k; break; }
            }
            if (j < 0) return false;

            var beforeEnd = before.EndOf(i);
            var afterEnd = after.EndOf(j);
            if (beforeEnd < 0 || afterEnd < 0 || !ReferenceEquals(beforeSteps[beforeEnd], afterSteps[afterEnd]))
                return false;

            var beforeElse = before.ElseOf(i);
            var afterElse = after.ElseOf(j);
            if ((beforeElse < 0) != (afterElse < 0)) return false;
            if (beforeElse >= 0 && !ReferenceEquals(beforeSteps[beforeElse], afterSteps[afterElse]))
                return false;
        }
        return true;
    }

    private static bool IsRange(IReadOnlyList<MacroStep> steps, int first, int last) =>
        steps.Count > 0 && first >= 0 && last >= first && last < steps.Count;
}
