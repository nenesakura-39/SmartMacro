using System.Text.Json;
using SmartMacro.Core.Models;
using SmartMacro.Core.Storage;

namespace SmartMacro.Core.Workflow;

/// <summary>What is put on the Windows clipboard by "Copy steps" (plain JSON text, so it also survives an app restart).</summary>
public sealed class StepClipboardData
{
    /// <summary>Always <see cref="StepClipboard.FormatName"/>; anything else on the clipboard isn't pasted.</summary>
    public string Format { get; set; } = StepClipboard.FormatName;

    /// <summary>The macro file format these steps need (pasting into an older SmartMacro refuses newer steps).</summary>
    public int SchemaVersion { get; set; }

    public List<MacroStep>? Steps { get; set; }
}

/// <summary>
/// Copy / paste of workflow steps (Ctrl+C / Ctrl+V), within one macro or into another one. The text is the same safe
/// JSON as macro files (only known step types load). Pasted steps get fresh IDs, and a paste must contain whole
/// If / Repeat blocks so it can never break the macro's structure.
/// </summary>
public static class StepClipboard
{
    public const string FormatName = "SmartMacro.Steps";

    /// <summary>Largest clipboard text that is looked at (a few big Find image steps fit easily).</summary>
    public const int MaxTextLength = 20 * 1024 * 1024;

    /// <summary>The clipboard text for these steps (copies, same IDs; IDs are renewed when pasting).</summary>
    public static string Serialize(IReadOnlyList<MacroStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var list = steps.ToList();
        return MacroJson.Serialize(new StepClipboardData
        {
            SchemaVersion = MacroDocument.RequiredSchemaVersion(new Macro { Steps = list }),
            Steps = list,
        });
    }

    /// <summary>Cheap check whether a clipboard text looks like copied steps (for enabling Paste).</summary>
    public static bool LooksLikeSteps(string? text) =>
        !string.IsNullOrEmpty(text) && text.Length <= MaxTextLength && text.Contains(FormatName, StringComparison.Ordinal);

    /// <summary>
    /// Reads copied steps. Returns false with a plain-English reason when the text isn't SmartMacro steps, comes from a
    /// newer version, or holds only part of an If / Repeat block. The steps get new IDs so they never clash.
    /// </summary>
    public static bool TryParse(string? text, out List<MacroStep> steps, out string? error)
    {
        steps = new List<MacroStep>();
        if (!LooksLikeSteps(text))
        {
            error = "The clipboard doesn't contain SmartMacro steps. Copy steps first (select them and press Ctrl+C).";
            return false;
        }

        StepClipboardData? data;
        try
        {
            data = MacroJson.Deserialize<StepClipboardData>(text!);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            error = $"The copied steps are damaged ({ex.Message}).";
            return false;
        }

        if (data is null || data.Format != FormatName || data.Steps is null)
        {
            error = "The clipboard doesn't contain SmartMacro steps.";
            return false;
        }

        if (data.SchemaVersion > MacroDocument.CurrentSchemaVersion)
        {
            error = "These steps were copied from a newer version of SmartMacro. Update SmartMacro to paste them.";
            return false;
        }

        var list = data.Steps.Where(s => s is not null).ToList();
        if (list.Count == 0)
        {
            error = "There are no steps on the clipboard.";
            return false;
        }

        if (!BlockStructure.Analyze(list).IsValid)
        {
            error = "The copied steps contain only part of an If / Repeat block, so they can't be pasted. Copy whole blocks.";
            return false;
        }

        // Fresh IDs for every pasted step AND its nested condition searches, so a pasted If / Find image never shares
        // an ID (and with it the engine's Hot-Spot memory or the editor's running-step highlight) with the original.
        foreach (var step in list) MacroFactory.RenewIds(step);
        steps = list;
        error = null;
        return true;
    }

    /// <summary>
    /// What Copy should take for a selection (indexes in list order): a single If / Repeat (or one of its markers) is
    /// widened to its whole block. Returns null when the selection holds only part of a block.
    /// </summary>
    public static IReadOnlyList<int>? ExpandSelection(IReadOnlyList<MacroStep> steps, IReadOnlyList<int> selected)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(selected);
        var indexes = selected.Where(i => i >= 0 && i < steps.Count).Distinct().OrderBy(i => i).ToList();
        if (indexes.Count == 0) return null;

        if (indexes.Count == 1 && WorkflowSteps.IsBlockStep(steps[indexes[0]]))
        {
            var (start, end) = Playback.PlaybackRanges.SingleStep(steps, indexes[0]);
            indexes = Enumerable.Range(start, end - start).ToList();
        }

        return BlockStructure.Analyze(indexes.Select(i => steps[i]).ToList()).IsValid ? indexes : null;
    }
}
