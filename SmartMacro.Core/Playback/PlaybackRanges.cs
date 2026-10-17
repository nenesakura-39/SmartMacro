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
