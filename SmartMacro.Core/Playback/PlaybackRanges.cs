using SmartMacro.Core.Models;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Workflow;

namespace SmartMacro.Core.Playback;

/// <summary>
/// Which steps "▶ Run step" and "⏭ Play from here" play. Pure list logic (no input), used by the playback engine and
/// the editor, and unit tested on its own.
/// </summary>
public static class PlaybackRanges
{
    /// <summary>
    /// The steps "Run step" plays for the step at <paramref name="index"/>: [Start, End).
    /// - A normal step: just that step.
    /// - An If / Repeat: the whole block, through its End If / End Repeat.
    /// - An Else / End If / End Repeat: the whole block it belongs to.
    /// If the blocks don't match up, only the step itself (validation refuses broken blocks before anything runs).
    /// </summary>
    public static (int Start, int End) SingleStep(IReadOnlyList<MacroStep> steps, int index)
    {
        ArgumentNullException.ThrowIfNull(steps);
        CheckIndex(steps, index);

        var blocks = BlockStructure.Analyze(steps);
        if (!blocks.IsValid) return (index, index + 1);

        var start = steps[index] switch
        {
            ElseStep or EndIfStep or EndRepeatStep when blocks.StartOf(index) >= 0 => blocks.StartOf(index),
            _ => index,
        };

        return steps[start] is IfStep or RepeatStep && blocks.EndOf(start) >= 0
            ? (start, blocks.EndOf(start) + 1)
            : (start, start + 1);
    }

    /// <summary>
    /// Where "Play from here" starts for the step at <paramref name="index"/> (it then plays to the end of the macro):
    /// - A step inside a Repeat block starts at that Repeat (the innermost one), so the loop counts, checks its
    ///   condition and repeats normally. Outer loops it was nested in run their remaining part once.
    /// - An End Repeat starts at its Repeat; an Else starts right after it (its "otherwise" part).
    /// - Anything else (also a step inside an If) starts right there.
    /// </summary>
    public static int FromStep(IReadOnlyList<MacroStep> steps, int index)
    {
        ArgumentNullException.ThrowIfNull(steps);
        CheckIndex(steps, index);

        var blocks = BlockStructure.Analyze(steps);
        if (!blocks.IsValid) return index;

        switch (steps[index])
        {
            case RepeatStep:
                return index;
            case EndRepeatStep when blocks.StartOf(index) >= 0:
                return blocks.StartOf(index);
            case ElseStep:
                index++; // run the "otherwise" part (an Else directly followed by End If simply continues after it)
                if (index >= steps.Count) return steps.Count - 1;
                break;
        }

        return EnclosingRepeat(steps, blocks, index) ?? index;
    }

    /// <summary>
    /// "▶ From here" (Shift+F5): the source range [Start, End) that the play-from-here slice covers, i.e. from the first
    /// step <see cref="BuildPlayFromHereSlice"/> keeps to the end of the macro. (Markers the slice drops – an Else part
    /// that belongs to an If above the start, an End If / End Repeat whose opener is above the start – are inside this
    /// range but don't play.) Start = <c>steps.Count</c> when nothing is left to play.
    /// </summary>
    public static (int Start, int End) PlayFromHere(IReadOnlyList<MacroStep> steps, int index)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var slice = BuildPlayFromHereSlice(steps, index);
        return (slice.SourceIndexes.Count > 0 ? slice.SourceIndexes[0] : steps.Count, steps.Count);
    }

    /// <summary>
    /// Builds the steps "Play from here" (Shift+F5) plays when started at <paramref name="startIndex"/>: a self-contained
    /// list from that step to the end of the macro whose If / Repeat blocks always match up, even when the start is deep
    /// inside nested blocks. Walking from the start with a stack of the blocks opened AFTER the start:
    /// - If / Repeat: opened, kept.
    /// - Else whose If is on the stack: kept. An Else whose If is ABOVE the start means we started inside that If's
    ///   "then" part, so the Else, its whole "otherwise" part and its End If are left out (exactly what a normal run
    ///   does after a true branch).
    /// - End If / End Repeat whose opener is on the stack: closes it, kept. One whose opener is above the start is left
    ///   out: the rest of that block plays once and playback simply continues after it (no loop back to a Repeat that
    ///   isn't part of the run).
    /// - Everything else: kept.
    /// Selecting an Else line itself plays its "otherwise" part (the start moves to the step after it).
    /// The steps are the same objects as in <paramref name="steps"/> (nothing is copied).
    /// </summary>
    public static PlayFromHereSlice BuildPlayFromHereSlice(IReadOnlyList<MacroStep> steps, int startIndex)
    {
        ArgumentNullException.ThrowIfNull(steps);
        CheckIndex(steps, startIndex);

        var first = steps[startIndex] is ElseStep ? startIndex + 1 : startIndex;
        var kept = new List<MacroStep>(Math.Max(0, steps.Count - first));
        var source = new List<int>(kept.Capacity);
        var open = new List<MacroStep>(); // blocks opened at or after the start, innermost last
        var droppedMarkers = 0;
        var droppedSteps = 0;

        for (var i = first; i < steps.Count; i++)
        {
            var step = steps[i];
            switch (step)
            {
                case IfStep or RepeatStep:
                    open.Add(step);
                    Keep(i);
                    break;

                case ElseStep when open.Count > 0 && open[^1] is IfStep:
                    Keep(i);
                    break;

                case ElseStep:
                {
                    // We were inside the "then" part of an If above the start: skip its "otherwise" part + End If.
                    var endIf = MatchingEndIf(steps, i);
                    droppedMarkers += endIf < steps.Count ? 2 : 1;
                    for (var j = i + 1; j < Math.Min(endIf, steps.Count); j++)
                    {
                        if (WorkflowSteps.IsBlockStep(steps[j])) droppedMarkers++;
                        else droppedSteps++;
                    }

                    i = endIf; // the loop's i++ continues right after the End If
                    break;
                }

                case EndIfStep when open.Count > 0 && open[^1] is IfStep:
                case EndRepeatStep when open.Count > 0 && open[^1] is RepeatStep:
                    open.RemoveAt(open.Count - 1);
                    Keep(i);
                    break;

                case EndIfStep or EndRepeatStep:
                    droppedMarkers++; // its If / Repeat is above the start
                    break;

                default:
                    Keep(i);
                    break;
            }
        }

        return new PlayFromHereSlice(kept, source, startIndex, droppedMarkers, droppedSteps);

        void Keep(int index)
        {
            kept.Add(steps[index]);
            source.Add(index);
        }
    }

    /// <summary>
    /// The macro "Play from here" plays: a copy of <paramref name="macro"/> (same ID, speed, target window, Humanize …)
    /// whose steps are <see cref="BuildPlayFromHereSlice"/>, played once (no macro repeats, no wait between repeats).
    /// </summary>
    public static (Macro Macro, PlayFromHereSlice Slice) BuildPlayFromHereMacro(Macro macro, int startIndex)
    {
        ArgumentNullException.ThrowIfNull(macro);
        var slice = BuildPlayFromHereSlice(macro.Steps, startIndex);
        var copy = macro.CopyWithSteps(slice.Steps.ToList());
        copy.RepeatCount = 1;
        copy.RepeatDelayMs = 0;
        return (copy, slice);
    }

    /// <summary>
    /// Validation for "Play from here": the problems of the slice that will actually run (step numbers translated back
    /// to the macro's own numbering), plus block problems of the whole macro (with broken blocks there's no reliable
    /// slice), plus "nothing enabled to run".
    /// </summary>
    public static IReadOnlyList<ValidationIssue> ValidatePlayFromHere(Macro macro, int startIndex, ScreenBounds? screen = null)
    {
        ArgumentNullException.ThrowIfNull(macro);
        var (sliceMacro, slice) = BuildPlayFromHereMacro(macro, startIndex);

        var result = new List<ValidationIssue>();
        var blocks = BlockStructure.Analyze(macro.Steps);
        foreach (var problem in blocks.Problems)
            result.Add(new ValidationIssue(IssueSeverity.Error, problem.Message, problem.StepIndex, macro.Steps[problem.StepIndex]?.Id, "Block"));

        foreach (var issue in MacroValidator.Validate(sliceMacro, screen))
        {
            if (issue.Property == "Block" && result.Count > 0) continue; // already reported for the whole macro
            result.Add(issue.StepIndex is int k && k >= 0 && k < slice.SourceIndexes.Count
                ? issue with { StepIndex = slice.SourceIndexes[k] }
                : issue);
        }

        var hasEnabled = slice.Steps.Any(s => s is { Enabled: true } && (!WorkflowSteps.IsBlockStep(s) || s is IfStep or RepeatStep));
        if (!hasEnabled)
            result.Add(new ValidationIssue(IssueSeverity.Error, "There are no enabled steps to run from here."));
        return result;
    }

    /// <summary>The End If that closes the If owning the Else at <paramref name="elseIndex"/> (Count if there's none).</summary>
    private static int MatchingEndIf(IReadOnlyList<MacroStep> steps, int elseIndex)
    {
        var depth = 0;
        for (var j = elseIndex + 1; j < steps.Count; j++)
        {
            switch (steps[j])
            {
                case IfStep:
                    depth++;
                    break;
                case EndIfStep when depth == 0:
                    return j;
                case EndIfStep:
                    depth--;
                    break;
            }
        }

        return steps.Count;
    }

    /// <summary>The innermost Repeat whose block contains the step at <paramref name="index"/>, or null.</summary>
    public static int? EnclosingRepeat(IReadOnlyList<MacroStep> steps, BlockStructure blocks, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            if (steps[i] is RepeatStep && blocks.EndOf(i) > index) return i;
        }

        return null;
    }

    /// <summary>
    /// The validation that matters for playing only steps [start, end): macro-wide problems, block structure problems
    /// (the jumps must be right) and problems of the steps that will run. Problems in steps that don't run are ignored,
    /// so a half-finished step 12 doesn't stop you testing step 3. At least one of those steps must be enabled.
    /// </summary>
    public static IReadOnlyList<ValidationIssue> ValidateRange(Macro macro, int start, int end, ScreenBounds? screen = null)
    {
        ArgumentNullException.ThrowIfNull(macro);
        var all = MacroValidator.Validate(macro, screen);
        var result = all.Where(i => i.Severity != IssueSeverity.Error
                                    || i.StepIndex is null
                                    || i.Property == "Block"
                                    || (i.StepIndex >= start && i.StepIndex < end)).ToList();

        var steps = macro.Steps ?? new List<MacroStep>();
        var hasEnabled = false;
        for (var i = Math.Max(0, start); i < Math.Min(end, steps.Count); i++)
        {
            var step = steps[i];
            if (step is { Enabled: true } && (!WorkflowSteps.IsBlockStep(step) || step is IfStep or RepeatStep))
            {
                hasEnabled = true;
                break;
            }
        }

        if (!hasEnabled)
            result.Add(new ValidationIssue(IssueSeverity.Error,
                end - start == 1 ? "This step is turned off, so there is nothing to run. Turn it on first." : "There are no enabled steps to run from here."));
        return result;
    }

    private static void CheckIndex(IReadOnlyList<MacroStep> steps, int index)
    {
        if (index < 0 || index >= steps.Count)
            throw new ArgumentOutOfRangeException(nameof(index), index, $"There is no step {index + 1} in this macro ({steps.Count} steps).");
    }
}

/// <summary>
/// What "Play from here" plays: <see cref="Steps"/> (a valid, self-contained list; the same step objects as the macro)
/// and, for each of them, its index in the macro (<see cref="SourceIndexes"/>), so step numbers and highlights still
/// match the editor. <see cref="DroppedMarkers"/> / <see cref="DroppedSteps"/>: block markers and steps left out
/// (an Else part of an If that started above, orphan End If / End Repeat).
/// </summary>
public sealed record PlayFromHereSlice(
    IReadOnlyList<MacroStep> Steps,
    IReadOnlyList<int> SourceIndexes,
    int RequestedIndex,
    int DroppedMarkers,
    int DroppedSteps)
{
    public int Count => Steps.Count;

    /// <summary>The macro's index of the slice step at <paramref name="sliceIndex"/> (or the slice index itself if unknown).</summary>
    public int SourceIndexOf(int sliceIndex) =>
        sliceIndex >= 0 && sliceIndex < SourceIndexes.Count ? SourceIndexes[sliceIndex] : sliceIndex;
}
