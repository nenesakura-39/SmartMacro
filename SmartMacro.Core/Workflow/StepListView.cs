using SmartMacro.Core.Models;

namespace SmartMacro.Core.Workflow;

/// <summary>The filter pills above the step list.</summary>
public enum StepFilter
{
    /// <summary>Every step.</summary>
    All,

    /// <summary>Find text / image / color, feedback loops and the If / Else / End If / Repeat / End Repeat blocks.</summary>
    VisionAndLogic,

    /// <summary>Move mouse, Mouse click, Scroll wheel (and recorded input, which holds mouse input).</summary>
    Mouse,

    /// <summary>Key press, Type text (and recorded input, which holds key input).</summary>
    Keyboard,

    /// <summary>Steps that are turned off.</summary>
    Disabled,
}

/// <summary>
/// Which steps a collapsed If / Repeat hides. <see cref="Hidden"/>[i] = step i is folded away; <see cref="HiddenCount"/>[i]
/// = for a collapsed If / Repeat, how many steps it hides (the steps inside, not counting its own Else / End marker).
/// </summary>
public sealed record CollapseResult(IReadOnlyList<bool> Hidden, IReadOnlyList<int> HiddenCount)
{
    public int HiddenTotal => Hidden.Count(h => h);
}

/// <summary>
/// Pure rules for how the editor SHOWS the step list (Studio UI): folding If / Repeat blocks, the search box and the filter
/// pills. Nothing here changes the step list itself: every step keeps its index, block structure and indentation, and
/// playback / saving always use all steps.
/// </summary>
public static class StepListView
{
    /// <summary>True for steps that can be folded (If and Repeat).</summary>
    public static bool CanCollapse(MacroStep? step) => step is IfStep or RepeatStep;

    /// <summary>
    /// Which steps are folded away when the If / Repeat steps whose IDs are in <paramref name="collapsed"/> are collapsed:
    /// everything after the If / Repeat up to and including its End If / End Repeat (the header itself stays visible and
    /// shows "[N steps hidden]"). A block without a matching End (broken structure) doesn't fold.
    /// </summary>
    public static CollapseResult Collapse(IReadOnlyList<MacroStep> steps, IReadOnlySet<Guid> collapsed)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(collapsed);
        var hidden = new bool[steps.Count];
        var counts = new int[steps.Count];
        if (collapsed.Count == 0 || steps.Count == 0) return new CollapseResult(hidden, counts);

        var blocks = BlockStructure.Analyze(steps);
        for (var i = 0; i < steps.Count; i++)
        {
            if (!CanCollapse(steps[i]) || !collapsed.Contains(steps[i].Id)) continue;
            var end = blocks.EndOf(i);
            if (end <= i) continue;
            var elseIndex = steps[i] is IfStep ? blocks.ElseOf(i) : -1;

            var count = 0;
            for (var j = i + 1; j <= end; j++)
            {
                hidden[j] = true;
                if (j != end && j != elseIndex) count++;
            }

            counts[i] = count;
        }

        return new CollapseResult(hidden, counts);
    }

    /// <summary>"[4 steps hidden]" for a collapsed If / Repeat ("[empty]" when there is nothing inside).</summary>
    public static string HiddenBadge(int count) => count switch
    {
        0 => "[empty]",
        1 => "[1 step hidden]",
        _ => $"[{count:N0} steps hidden]",
    };

    /// <summary>True when the step belongs to the filter pill's group.</summary>
    public static bool MatchesFilter(MacroStep step, StepFilter filter)
    {
        ArgumentNullException.ThrowIfNull(step);
        return filter switch
        {
            StepFilter.VisionAndLogic => step.Kind is StepKind.FindText or StepKind.FindImage or StepKind.FindColor or StepKind.FeedbackLoop
                or StepKind.If or StepKind.Else or StepKind.EndIf or StepKind.Repeat or StepKind.EndRepeat,
            StepFilter.Mouse => step.Kind is StepKind.MouseMove or StepKind.MouseClick or StepKind.MouseScroll or StepKind.InputSequence,
            StepFilter.Keyboard => step.Kind is StepKind.KeyPress or StepKind.TypeText or StepKind.InputSequence,
            StepFilter.Disabled => !step.Enabled,
            _ => true,
        };
    }

    /// <summary>
    /// True when the search text is found in the step's name ("Find image"), its one-line description or its comment
    /// (ignoring case). Several words must all be found (in any order). Empty search = everything matches.
    /// </summary>
    public static bool MatchesText(MacroStep step, string? query)
    {
        ArgumentNullException.ThrowIfNull(step);
        var words = Words(query);
        if (words.Length == 0) return true;

        string description;
        try
        {
            description = StepDescriber.Describe(step);
        }
        catch (Exception)
        {
            description = string.Empty; // a half-edited step must never break the search
        }

        var haystack = string.Join('\n', StepDescriber.DisplayName(step.Kind), description, step.Comment ?? string.Empty);
        return words.All(w => haystack.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The indexes (ascending) of the steps that match the search text AND the filter. With no search text and the "All"
    /// filter nothing counts as a match (there is nothing to highlight).
    /// </summary>
    public static IReadOnlyList<int> FindMatches(IReadOnlyList<MacroStep> steps, string? query, StepFilter filter = StepFilter.All)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (Words(query).Length == 0 && filter == StepFilter.All) return Array.Empty<int>();
        var result = new List<int>();
        for (var i = 0; i < steps.Count; i++)
        {
            if (MatchesFilter(steps[i], filter) && MatchesText(steps[i], query)) result.Add(i);
        }

        return result;
    }

    /// <summary>
    /// The If / Repeat steps (outermost first) whose blocks contain the step at <paramref name="index"/>: the ones to
    /// unfold so that step can be shown (e.g. when jumping to a search match inside a collapsed block).
    /// </summary>
    public static IReadOnlyList<int> EnclosingBlocks(IReadOnlyList<MacroStep> steps, int index)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (index < 0 || index >= steps.Count) return Array.Empty<int>();
        var blocks = BlockStructure.Analyze(steps);
        var result = new List<int>();
        for (var i = 0; i < index; i++)
        {
            if (CanCollapse(steps[i]) && blocks.EndOf(i) >= index) result.Add(i);
        }

        return result;
    }

    /// <summary>"3 matches", "1 match", "No matches" (and "2 / 3" style position text via <see cref="MatchPosition"/>).</summary>
    public static string MatchCountText(int count) => count switch
    {
        0 => "No matches",
        1 => "1 match",
        _ => $"{count:N0} matches",
    };

    /// <summary>"2 / 3" for the current match (1-based); empty when there are no matches.</summary>
    public static string MatchPosition(int current, int count) => count <= 0 || current < 0 ? string.Empty : $"{Math.Min(current, count - 1) + 1} / {count}";

    private static string[] Words(string? query) =>
        string.IsNullOrWhiteSpace(query) ? Array.Empty<string>() : query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
}
