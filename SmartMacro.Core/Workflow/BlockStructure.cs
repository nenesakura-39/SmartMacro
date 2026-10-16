using SmartMacro.Core.Models;

namespace SmartMacro.Core.Workflow;

/// <summary>One problem with the block markers, e.g. an If without End If. StepIndex is 0-based.</summary>
public sealed record BlockProblem(int StepIndex, string Message);

/// <summary>
/// Works out how the If / Else / End If and Repeat / End Repeat markers in a flat step list belong together.
///
/// Result:
/// - <see cref="Depth"/>: how far each step is indented (0 = top level). Markers sit at the level of their block,
///   the steps inside are one level deeper. Used by the editor.
/// - A jump table for the engine: for each If its Else (if any) and End If; for each Else its End If; for each
///   Repeat its End Repeat, and back again.
/// - <see cref="Problems"/>: unbalanced or wrongly nested markers, too-deep nesting. The engine only runs a list
///   without problems; the validator shows them in the Problems list.
///
/// Disabled steps count too (the structure must not change when a step is switched off).
/// </summary>
public sealed class BlockStructure
{
    /// <summary>Most blocks that may be open inside each other (an If inside a Repeat inside … ).</summary>
    public const int MaxDepth = 8;

    private readonly int[] _depth;
    private readonly int[] _else;    // If → its Else, or -1
    private readonly int[] _end;     // If / Else → End If; Repeat → End Repeat; otherwise -1
    private readonly int[] _start;   // Else / End If → its If; End Repeat → its Repeat; otherwise -1

    private BlockStructure(int count)
    {
        _depth = new int[count];
        _else = Enumerable.Repeat(-1, count).ToArray();
        _end = Enumerable.Repeat(-1, count).ToArray();
        _start = Enumerable.Repeat(-1, count).ToArray();
    }

    public IReadOnlyList<BlockProblem> Problems { get; private set; } = Array.Empty<BlockProblem>();

    public bool IsValid => Problems.Count == 0;

    public int Count => _depth.Length;

    /// <summary>Indentation level of each step (0 = not inside any block).</summary>
    public IReadOnlyList<int> Depth => _depth;

    /// <summary>The deepest nesting used (0 = no blocks).</summary>
    public int MaxDepthUsed { get; private set; }

    /// <summary>For an If: the index of its Else, or -1 if it has none (or the index isn't an If).</summary>
    public int ElseOf(int index) => _else[index];

    /// <summary>For an If or Else: its End If. For a Repeat: its End Repeat. Otherwise -1.</summary>
    public int EndOf(int index) => _end[index];

    /// <summary>For an Else / End If: its If. For an End Repeat: its Repeat. Otherwise -1.</summary>
    public int StartOf(int index) => _start[index];

    /// <summary>Analyses the step list. Never throws for bad structure; problems are listed instead.</summary>
    public static BlockStructure Analyze(IReadOnlyList<MacroStep?> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var result = new BlockStructure(steps.Count);
        var problems = new List<BlockProblem>();
        var open = new List<int>(); // indexes of the If / Repeat steps currently open, innermost last

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            switch (step)
            {
                case IfStep:
                case RepeatStep:
                    result._depth[i] = open.Count;
                    if (open.Count >= MaxDepth)
                        problems.Add(new BlockProblem(i,
                            $"Blocks are nested too deeply here (more than {MaxDepth} levels). Move some steps out of the inner blocks."));
                    open.Add(i);
                    result.MaxDepthUsed = Math.Max(result.MaxDepthUsed, open.Count);
                    break;

                case ElseStep:
                {
                    var top = open.Count > 0 ? open[^1] : -1;
                    if (top >= 0 && steps[top] is IfStep)
                    {
                        result._depth[i] = open.Count - 1;
                        if (result._else[top] >= 0)
                        {
                            problems.Add(new BlockProblem(i,
                                $"This If (step {top + 1}) already has an Else (step {result._else[top] + 1}). An If can have only one Else."));
                        }
                        else
                        {
                            result._else[top] = i;
                            result._start[i] = top;
                        }
                    }
                    else
                    {
                        result._depth[i] = open.Count;
                        problems.Add(new BlockProblem(i, top >= 0
                            ? $"This Else is inside the Repeat at step {top + 1}, not directly inside an If. Add an End Repeat before it, or move it."
                            : "This Else has no If before it. Add an If above it, or delete the Else."));
                    }

                    break;
                }

                case EndIfStep:
                    Close<IfStep>(steps, result, open, problems, i, "End If", "If");
                    break;

                case EndRepeatStep:
                    Close<RepeatStep>(steps, result, open, problems, i, "End Repeat", "Repeat");
                    break;

                default:
                    result._depth[i] = open.Count;
                    break;
            }
        }

        // Anything still open at the end is missing its End.
        foreach (var index in open)
        {
            problems.Add(steps[index] is IfStep
                ? new BlockProblem(index, "This If has no End If. Add an End If after the steps that belong to it.")
                : new BlockProblem(index, "This Repeat has no End Repeat. Add an End Repeat after the steps that belong to it."));
        }

        result.Problems = problems.OrderBy(p => p.StepIndex).ToList();
        return result;
    }

    private static void Close<TOpen>(IReadOnlyList<MacroStep?> steps, BlockStructure result, List<int> open,
        List<BlockProblem> problems, int index, string name, string opener) where TOpen : MacroStep
    {
        var top = open.Count > 0 ? open[^1] : -1;
        if (top >= 0 && steps[top] is TOpen)
        {
            open.RemoveAt(open.Count - 1);
            result._depth[index] = open.Count;
            result._end[top] = index;
            result._start[index] = top;
            if (result._else[top] >= 0) result._end[result._else[top]] = index;
            return;
        }

        result._depth[index] = open.Count;
        if (top < 0)
        {
            problems.Add(new BlockProblem(index, $"This {name} has no {opener} before it. Add an {opener} above it, or delete the {name}."));
            return;
        }

        // The innermost open block is the other kind: blocks overlap instead of nesting.
        var other = steps[top] is IfStep ? "If" : "Repeat";
        problems.Add(new BlockProblem(index,
            $"This {name} doesn't match: the {other} at step {top + 1} is still open. Add an {(other == "If" ? "End If" : "End Repeat")} before it (blocks must be closed in the reverse order they were opened)."));
    }
}
