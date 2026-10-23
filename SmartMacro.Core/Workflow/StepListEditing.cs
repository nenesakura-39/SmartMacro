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
/// The answer to "what goes when these steps are deleted?": <see cref="Indexes"/> (ascending) are the steps to remove,
/// <see cref="AddedMarkers"/> of them were added to keep If / Repeat blocks matched, and <see cref="Note"/> tells the
/// user about those (null when nothing was added).
/// </summary>
public sealed record DeletePlan(IReadOnlyList<int> Indexes, int AddedMarkers, string? Note);

/// <summary>
/// The answer to "what does Duplicate (Ctrl+D) copy?": the steps <see cref="First"/>..<see cref="Last"/> (grown to whole
/// If / Repeat blocks, so a copied If always comes with its Else / End If), inserted as copies at <see cref="InsertAt"/>
/// (right after the original block). <see cref="Allowed"/> false = nothing is copied; <see cref="Problem"/> says why.
/// </summary>
public sealed record DuplicatePlan(bool Allowed, int First, int Last, int InsertAt, bool TookWholeBlocks = false, string? Problem = null)
{
    public int Count => Allowed ? Last - First + 1 : 0;

    public static DuplicatePlan Refused(string problem) => new(false, -1, -1, -1, false, problem);
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

    /// <summary>
    /// Keeps an insert position out of folded blocks: <paramref name="index"/> is where new steps would go (in front of the
    /// step now at that index, <c>steps.Count</c> = at the end). When that spot is inside the hidden part of a folded If /
    /// Repeat (e.g. "after the selected step" while the selected step is a folded header), the steps go right after the
    /// folded block's End instead, so a new / pasted / duplicated step can never land invisible inside a block you can't
    /// see. Blocks that aren't folded are unaffected (adding after an open If still adds inside it).
    /// </summary>
    public static int InsertIndexOutsideCollapsed(IReadOnlyList<MacroStep> steps, int index, IReadOnlySet<Guid>? collapsed)
    {
        ArgumentNullException.ThrowIfNull(steps);
        index = Math.Clamp(index, 0, steps.Count);
        if (collapsed is null || collapsed.Count == 0) return index;

        var fold = StepListView.Collapse(steps, collapsed);
        while (index < steps.Count && fold.Hidden[index]) index++;
        return index;
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
    /// Move up / down / to top / to bottom with folded blocks (Alt+↑ / Alt+↓ in the editor). What the user sees is what
    /// moves, and where it lands:
    /// - A folded (collapsed) If / Repeat in the range is ONE row on screen, so its whole block (If … Else … End If,
    ///   Repeat … End Repeat, every hidden step inside) moves as a single unit. Moving only the header would tear it
    ///   away from its hidden steps and its End.
    /// - Moving up / down over a folded block jumps past the WHOLE folded block (header to End) in one press, instead of
    ///   dropping the moved steps inside a block you can't see.
    /// Blocks that aren't folded behave exactly like <see cref="PlanMove(IReadOnlyList{MacroStep}, int, int, MoveDirection)"/>
    /// (one place at a time, so a block can still be made bigger or smaller). The usual block-safety check still applies.
    /// </summary>
    public static MovePlan PlanMove(IReadOnlyList<MacroStep> steps, int first, int last, MoveDirection direction,
        IReadOnlySet<Guid>? collapsed)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (collapsed is null || collapsed.Count == 0) return PlanMove(steps, first, last, direction);
        if (!IsRange(steps, first, last)) return MovePlan.Nothing(Math.Max(0, first), Math.Max(0, last));

        var (start, end, grew) = GrowToCollapsedBlocks(steps, first, last, collapsed);
        if (direction is MoveDirection.ToTop or MoveDirection.ToBottom)
        {
            var plan = PlanMove(steps, start, end, direction);
            return grew && plan.Allowed ? plan with { TookWholeBlocks = true } : plan;
        }

        var fold = StepListView.Collapse(steps, collapsed);
        var count = end - start + 1;
        int newFirst;
        if (direction == MoveDirection.Up)
        {
            var above = start - 1;
            if (above < 0) return MovePlan.Nothing(start, end);

            // The row above is the hidden End of a folded block: jump above that block's (visible) header.
            while (above > 0 && fold.Hidden[above]) above--;
            newFirst = above;
        }
        else
        {
            var below = end + 1;
            if (below >= steps.Count) return MovePlan.Nothing(start, end);

            // The row below is a folded header: the whole folded block (header + everything it hides) is one row.
            var rowEnd = below;
            while (rowEnd + 1 < steps.Count && fold.Hidden[rowEnd + 1]) rowEnd++;
            newFirst = rowEnd - count + 1;
        }

        return Check(steps, start, end, newFirst, grew);
    }

    /// <summary>
    /// Grows first..last so every FOLDED If / Repeat in it brings its whole block along (header through End, nested
    /// blocks included). Unfolded blocks are left as they are. Only with matching blocks.
    /// </summary>
    public static (int First, int Last, bool Grew) GrowToCollapsedBlocks(IReadOnlyList<MacroStep> steps, int first, int last,
        IReadOnlySet<Guid> collapsed)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(collapsed);
        if (!IsRange(steps, first, last) || collapsed.Count == 0) return (first, last, false);
        var blocks = BlockStructure.Analyze(steps);
        if (!blocks.IsValid) return (first, last, false);

        int start = first, end = last;
        for (var i = start; i <= end; i++) // end can grow while looping: nested folded blocks are covered too
        {
            if (steps[i] is not (IfStep or RepeatStep) || !collapsed.Contains(steps[i].Id)) continue;
            var blockEnd = blocks.EndOf(i);
            if (blockEnd > end) end = blockEnd;
        }

        // A folded block's header pulls in its block; if that block now overlaps a partly selected outer block, take
        // the outer block whole too (the normal rule), so the moved range always holds complete blocks.
        if (end != last)
        {
            var (s2, e2, _) = GrowToWholeBlocks(steps, start, end);
            (start, end) = (s2, e2);
        }

        return (start, end, start != first || end != last);
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

    /// <summary>
    /// Which steps to delete when the user deletes <paramref name="selected"/>: the selection itself, plus the other
    /// markers of every If / Repeat block whose If, End If, Repeat or End Repeat is selected (If + its Else + End If;
    /// Repeat + End Repeat). The steps inside those blocks are kept. That way deleting an If can never leave an
    /// orphan End If (or Else) behind, and an End If is only ever deleted together with its If.
    /// A marker that has no partner (already orphaned) is simply deleted. Selecting just an Else deletes just the Else.
    /// </summary>
    public static DeletePlan PlanDelete(IReadOnlyList<MacroStep> steps, IEnumerable<int> selected)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(selected);
        var remove = new SortedSet<int>(selected.Where(i => i >= 0 && i < steps.Count));
        if (remove.Count == 0) return new DeletePlan(Array.Empty<int>(), 0, null);

        var blocks = BlockStructure.Analyze(steps);
        var added = 0;
        bool addedIf = false, addedRepeat = false;
        foreach (var index in remove.ToList())
        {
            if (steps[index] is not (IfStep or EndIfStep or RepeatStep or EndRepeatStep)) continue;
            var start = steps[index] is IfStep or RepeatStep ? index : blocks.StartOf(index);
            if (start < 0) continue; // an orphan End: deleting it alone fixes the list

            var isIf = steps[start] is IfStep;
            var partners = new[] { start, blocks.EndOf(start), isIf ? blocks.ElseOf(start) : -1 };
            foreach (var partner in partners)
            {
                if (partner < 0 || !remove.Add(partner)) continue;
                added++;
                if (isIf) addedIf = true;
                else addedRepeat = true;
            }
        }

        string? note = null;
        if (added > 0)
        {
            var what = addedIf && addedRepeat
                ? "the matching If / Else / End If and Repeat / End Repeat"
                : addedIf ? "the matching If / Else / End If" : "the matching Repeat / End Repeat";
            note = $"Also removed {what} marker{(added == 1 ? "" : "s")}, so no block is left half open." +
                   (remove.Count < steps.Count ? " The steps inside were kept." : "");
        }

        return new DeletePlan(remove.ToList(), added, note);
    }

    /// <summary>
    /// Delete with folded blocks: a selected FOLDED If / Repeat is one row on screen, so deleting it deletes the whole
    /// block with every hidden step inside (Ctrl+Z brings it back). Everything else follows
    /// <see cref="PlanDelete(IReadOnlyList{MacroStep}, IEnumerable{int})"/> (an unfolded If loses only its markers).
    /// </summary>
    public static DeletePlan PlanDelete(IReadOnlyList<MacroStep> steps, IEnumerable<int> selected, IReadOnlySet<Guid>? collapsed)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(selected);
        var picked = selected.Where(i => i >= 0 && i < steps.Count).Distinct().ToList();
        if (collapsed is null || collapsed.Count == 0) return PlanDelete(steps, picked);

        var blocks = BlockStructure.Analyze(steps);
        if (!blocks.IsValid) return PlanDelete(steps, picked);

        var whole = new SortedSet<int>();
        var foldedBlocks = 0;
        foreach (var index in picked)
        {
            if (steps[index] is not (IfStep or RepeatStep) || !collapsed.Contains(steps[index].Id)) continue;
            var end = blocks.EndOf(index);
            if (end <= index) continue;
            foldedBlocks++;
            for (var i = index; i <= end; i++) whole.Add(i);
        }

        if (foldedBlocks == 0) return PlanDelete(steps, picked);

        var rest = PlanDelete(steps, picked.Where(i => !whole.Contains(i)));
        foreach (var i in rest.Indexes) whole.Add(i);
        var hiddenSteps = whole.Count(i => !WorkflowSteps.IsBlockStep(steps[i]));
        var note = (foldedBlocks == 1 ? "Deleted the folded block" : $"Deleted {foldedBlocks} folded blocks") +
                   $" with {hiddenSteps} step{(hiddenSteps == 1 ? "" : "s")} inside. Ctrl+Z brings {(foldedBlocks == 1 ? "it" : "them")} back.";
        if (rest.Note is not null) note += " " + rest.Note;
        return new DeletePlan(whole.ToList(), rest.AddedMarkers, note);
    }

    /// <summary>
    /// Plans Duplicate (Ctrl+D) for the contiguous range first..last. Block-aware: when the range holds an If / Repeat (or
    /// one of its Else / End markers), the copy takes the WHOLE block (If … Else … End If, Repeat … End Repeat, with every
    /// step inside), so the copy is a valid block of its own and goes right after the original. A lone Else can't be
    /// copied (an If can only have one Else). When the blocks are already broken, markers aren't copied at all (that
    /// would only add more broken markers): Auto-Fix first.
    /// </summary>
    public static DuplicatePlan PlanDuplicate(IReadOnlyList<MacroStep> steps, int first, int last)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (!IsRange(steps, first, last)) return DuplicatePlan.Refused("Select the step(s) to duplicate first.");

        var hasMarker = false;
        var onlyElse = true;
        for (var i = first; i <= last; i++)
        {
            if (WorkflowSteps.IsBlockStep(steps[i])) hasMarker = true;
            if (steps[i] is not ElseStep) onlyElse = false;
        }

        if (!hasMarker) return new DuplicatePlan(true, first, last, last + 1);
        if (onlyElse)
            return DuplicatePlan.Refused("An If block can have only one Else. To duplicate the whole If block, select its If step and press Ctrl+D.");

        var blocks = BlockStructure.Analyze(steps);
        if (!blocks.IsValid)
            return DuplicatePlan.Refused("The If / Repeat blocks don't match up yet, so block steps can't be duplicated. Use \"✨ Auto-Fix Block Structure\" first.");

        var (start, end, grew) = GrowToWholeBlocks(steps, first, last);
        return new DuplicatePlan(true, start, end, end + 1, grew);
    }

    /// <summary>
    /// Fresh copies (new IDs) of the steps a <see cref="DuplicatePlan"/> copies, in order. The block structure of the
    /// copied range is the same as the original's, so it can be inserted at <see cref="DuplicatePlan.InsertAt"/> as is.
    /// </summary>
    public static List<MacroStep> DuplicateSteps(IReadOnlyList<MacroStep> steps, DuplicatePlan plan)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(plan);
        var copies = new List<MacroStep>();
        if (!plan.Allowed) return copies;
        for (var i = plan.First; i <= plan.Last; i++) copies.Add(MacroFactory.DuplicateStep(steps[i]));
        return copies;
    }

    /// <summary>
    /// The list after a duplicate (for tests and anyone without an observable list): the copies inserted right after
    /// the original range.
    /// </summary>
    public static List<MacroStep> ApplyDuplicate(IReadOnlyList<MacroStep> steps, DuplicatePlan plan)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var result = steps.ToList();
        if (plan.Allowed) result.InsertRange(plan.InsertAt, DuplicateSteps(steps, plan));
        return result;
    }

    /// <summary>
    /// Which steps Space (turn on / off) changes for the selected steps: each selected step, and for a selected If /
    /// Repeat (or one of its Else / End markers) every step of its block, from the If / Repeat to its End. Ascending,
    /// no duplicates. With broken blocks only the selected steps themselves.
    /// </summary>
    public static IReadOnlyList<int> ToggleEnabledTargets(IReadOnlyList<MacroStep> steps, IEnumerable<int> selected)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(selected);
        var targets = new SortedSet<int>(selected.Where(i => i >= 0 && i < steps.Count));
        var blocks = BlockStructure.Analyze(steps);
        if (!blocks.IsValid) return targets.ToList();

        foreach (var index in targets.ToList())
        {
            if (!WorkflowSteps.IsBlockStep(steps[index])) continue;
            var start = steps[index] is IfStep or RepeatStep ? index : blocks.StartOf(index);
            var end = start >= 0 ? blocks.EndOf(start) : -1;
            if (start < 0 || end < 0) continue;
            for (var i = start; i <= end; i++) targets.Add(i);
        }

        return targets.ToList();
    }

    /// <summary>
    /// Checks whether a standalone Else, End If or End Repeat may be inserted at <paramref name="index"/>: it must
    /// close (or split) an If / Repeat that is still open there, and it must not leave another marker without its
    /// partner. Returns null when it's fine, otherwise a plain-English reason (the step is then not added).
    /// Other step kinds always return null.
    /// </summary>
    public static string? CheckMarkerInsert(IReadOnlyList<MacroStep> steps, int index, MacroStep marker)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(marker);
        if (marker is not (ElseStep or EndIfStep or EndRepeatStep)) return null;
        index = Math.Clamp(index, 0, steps.Count);

        var before = BlockStructure.Analyze(steps);
        var afterSteps = steps.ToList();
        afterSteps.Insert(index, marker);
        var after = BlockStructure.Analyze(afterSteps);

        var markerProblem = after.Problems.Any(p => p.StepIndex == index);
        if (!markerProblem && after.Problems.Count <= before.Problems.Count) return null;

        var position = index >= steps.Count ? "at the end" : $"before step {index + 1}";
        return marker switch
        {
            ElseStep => $"An Else can only go inside an If block that has no Else yet, and there isn't one open {position}. " +
                        "Select a step inside an If block first, or add an If (it comes with its End If).",
            EndIfStep => $"An End If closes an If block, but no If is waiting for its End If {position}. " +
                         "Add an If instead (it comes with its End If), or select a step inside the If you want to close.",
            _ => $"An End Repeat closes a Repeat block, but no Repeat is waiting for its End Repeat {position}. " +
                 "Add a Repeat instead (it comes with its End Repeat), or select a step inside the Repeat you want to close.",
        };
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
