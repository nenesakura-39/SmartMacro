using SmartMacro.Core.Input;
using SmartMacro.Core.Models;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Vision;

namespace SmartMacro.Core.Validation;

public enum LibraryAuditSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

public sealed record LibraryAuditEntry(
    LibraryAuditSeverity Severity,
    string Category,
    Guid? MacroId,
    string MacroName,
    int? StepNumber,
    string Message)
{
    public string SeverityIcon => Severity switch
    {
        LibraryAuditSeverity.Error => "⛔",
        LibraryAuditSeverity.Warning => "⚠",
        _ => "ℹ",
    };

    public string LocationText => StepNumber is int n ? $"{MacroName} · Step {n}" : MacroName;
}

public sealed record LibraryAuditReport(
    DateTimeOffset GeneratedAt,
    int MacrosAudited,
    int ErrorCount,
    int WarningCount,
    int InfoCount,
    IReadOnlyList<LibraryAuditEntry> Entries)
{
    public int IssueCount => Entries.Count;

    public bool IsHealthy => ErrorCount == 0 && WarningCount == 0;

    public string SummaryText
    {
        get
        {
            if (MacrosAudited == 0)
                return "Library is empty — create or import a macro first.";
            if (Entries.Count == 0)
                return $"✓ Audited {MacrosAudited} macro{(MacrosAudited == 1 ? "" : "s")} — no issues, collisions, or portability warnings found.";

            var parts = new List<string>(3);
            if (ErrorCount > 0) parts.Add($"{ErrorCount} error{(ErrorCount == 1 ? "" : "s")}");
            if (WarningCount > 0) parts.Add($"{WarningCount} warning{(WarningCount == 1 ? "" : "s")}");
            if (InfoCount > 0) parts.Add($"{InfoCount} portability note{(InfoCount == 1 ? "" : "s")}");
            return $"Audited {MacrosAudited} macro{(MacrosAudited == 1 ? "" : "s")}: {string.Join(", ", parts)}.";
        }
    }
}

/// <summary>
/// 1-click whole-library auditor: aggregates per-macro validation, sub-macro call-graph checks,
/// unresolved template asset references, global &amp; cross-macro hotkey collisions,
/// OS-dependent path portability hints, and OCR environment readiness.
/// </summary>
public static class LibraryAuditService
{
    private sealed record TriggerRecord(
        Macro Macro,
        MacroTrigger Trigger,
        HotkeyBinding Hotkey,
        bool IsWindowScoped,
        string WindowTitle,
        string ProcessName);

    public static LibraryAuditReport Audit(
        IReadOnlyList<Macro> macros,
        AppSettings? settings = null,
        bool isOcrAvailable = true,
        ScreenBounds? screenBounds = null)
    {
        ArgumentNullException.ThrowIfNull(macros);

        var entries = new List<LibraryAuditEntry>();
        var list = macros.Where(m => m is not null).ToList();

        CheckDuplicateNames(list, entries);
        CheckPerMacroRules(list, list, isOcrAvailable, screenBounds, entries);
        CheckHotkeyCollisions(list, settings, entries);

        var ordered = entries
            .OrderByDescending(e => e.Severity)
            .ThenBy(e => e.MacroName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(e => e.StepNumber ?? 0)
            .ThenBy(e => e.Category, StringComparer.Ordinal)
            .ToList();

        return new LibraryAuditReport(
            DateTimeOffset.UtcNow,
            list.Count,
            ordered.Count(e => e.Severity == LibraryAuditSeverity.Error),
            ordered.Count(e => e.Severity == LibraryAuditSeverity.Warning),
            ordered.Count(e => e.Severity == LibraryAuditSeverity.Info),
            ordered);
    }

    private static void CheckDuplicateNames(IReadOnlyList<Macro> macros, List<LibraryAuditEntry> entries)
    {
        foreach (var group in macros
            .Where(m => !string.IsNullOrWhiteSpace(m.Name))
            .GroupBy(m => m.Name.Trim(), StringComparer.CurrentCultureIgnoreCase))
        {
            var members = group.ToList();
            if (members.Count < 2) continue;

            foreach (var macro in members)
            {
                entries.Add(new LibraryAuditEntry(
                    LibraryAuditSeverity.Warning,
                    "Naming",
                    macro.Id,
                    macro.Name,
                    null,
                    $"Duplicate macro name \"{group.Key}\" appears {members.Count} times. Sub-macro calls or CLI --run by name can become ambiguous."));
            }
        }
    }

    private static void CheckPerMacroRules(
        IReadOnlyList<Macro> targetMacros,
        IReadOnlyList<Macro> libraryMacros,
        bool isOcrAvailable,
        ScreenBounds? screenBounds,
        List<LibraryAuditEntry> entries)
    {
        foreach (var macro in targetMacros)
        {
            var macroName = string.IsNullOrWhiteSpace(macro.Name) ? "(Unnamed macro)" : macro.Name;

            foreach (var issue in MacroValidator.Validate(macro, screenBounds))
            {
                var sev = issue.Severity == IssueSeverity.Error
                    ? LibraryAuditSeverity.Error
                    : LibraryAuditSeverity.Warning;
                var category = issue.Property == "Block" ? "Blocks" : "Validation";
                entries.Add(new LibraryAuditEntry(
                    sev,
                    category,
                    macro.Id,
                    macroName,
                    issue.StepIndex is int idx ? idx + 1 : null,
                    issue.Message));
            }

            foreach (var issue in MacroValidator.ValidateCallGraph(macro, libraryMacros))
            {
                var sev = issue.Severity == IssueSeverity.Error
                    ? LibraryAuditSeverity.Error
                    : LibraryAuditSeverity.Warning;
                entries.Add(new LibraryAuditEntry(
                    sev,
                    "Sub-Macro",
                    macro.Id,
                    macroName,
                    issue.StepIndex is int idx ? idx + 1 : null,
                    issue.Message));
            }

            foreach (var issue in MacroValidator.ValidateInputHygiene(macro))
            {
                entries.Add(new LibraryAuditEntry(
                    LibraryAuditSeverity.Warning,
                    "Input Hygiene",
                    macro.Id,
                    macroName,
                    issue.StepIndex is int idx ? idx + 1 : null,
                    issue.Message));
            }

            if (!isOcrAvailable && WorkflowSteps.UsesTextRecognition(macro))
            {
                entries.Add(new LibraryAuditEntry(
                    LibraryAuditSeverity.Warning,
                    "Environment",
                    macro.Id,
                    macroName,
                    null,
                    "Uses Windows OCR text recognition, which is currently unavailable on this system."));
            }

            var steps = macro.Steps ?? new List<MacroStep>();
            for (var i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                if (step is null) continue;
                var stepNum = i + 1;

                CheckAssetReferences(macro, macroName, step, stepNum, entries);

                if (step is ScreenshotStep shot && !string.IsNullOrWhiteSpace(shot.FolderPath) && IsHardcodedPath(shot.FolderPath))
                {
                    entries.Add(new LibraryAuditEntry(
                        LibraryAuditSeverity.Info,
                        "Portability",
                        macro.Id,
                        macroName,
                        stepNum,
                        $"Screenshot folder \"{shot.FolderPath.Trim()}\" is machine-specific. Leaving it empty (to use each PC's Pictures\\SmartMacro Screenshots) is easier to share across PCs."));
                }
            }
        }
    }

    private static void CheckAssetReferences(
        Macro macro,
        string macroName,
        MacroStep step,
        int stepNum,
        List<LibraryAuditEntry> entries)
    {
        foreach (var search in WorkflowSteps.ScreenSearches(step))
        {
            if (search is not FindImageStep img) continue;

            if (!string.IsNullOrWhiteSpace(img.TemplateAssetId)
                && string.IsNullOrEmpty(img.TemplateData)
                && (macro.Assets is null || !macro.Assets.TryGetValue(img.TemplateAssetId, out var stored) || string.IsNullOrEmpty(stored)))
            {
                entries.Add(new LibraryAuditEntry(
                    LibraryAuditSeverity.Error,
                    "Assets",
                    macro.Id,
                    macroName,
                    stepNum,
                    $"References missing template asset '{img.TemplateAssetId}' (inline data is empty and asset entry is missing)."));
            }

            if (img.AdditionalTemplates is { Count: > 0 } extras)
            {
                foreach (var variant in extras)
                {
                    if (variant is null) continue;
                    if (!string.IsNullOrWhiteSpace(variant.TemplateAssetId)
                        && string.IsNullOrEmpty(variant.Data)
                        && (macro.Assets is null || !macro.Assets.TryGetValue(variant.TemplateAssetId, out var vStored) || string.IsNullOrEmpty(vStored)))
                    {
                        entries.Add(new LibraryAuditEntry(
                            LibraryAuditSeverity.Error,
                            "Assets",
                            macro.Id,
                            macroName,
                            stepNum,
                            $"Additional template frame references missing asset '{variant.TemplateAssetId}'."));
                    }
                }
            }
        }
    }

    private static void CheckHotkeyCollisions(
        IReadOnlyList<Macro> macros,
        AppSettings? settings,
        List<LibraryAuditEntry> entries)
    {
        var active = new List<TriggerRecord>();
        foreach (var macro in macros)
        {
            if (macro.Triggers is null) continue;
            foreach (var trigger in macro.Triggers)
            {
                if (trigger is null || !trigger.Enabled || trigger.Kind != TriggerKind.Hotkey) continue;
                if (trigger.Hotkey is null || trigger.Hotkey.VirtualKey <= 0) continue;
                var title = trigger.WindowTitle?.Trim() ?? string.Empty;
                var proc = trigger.ProcessNameMatch?.Trim() ?? string.Empty;
                var scoped = trigger.OnlyWhenWindowActive && (title.Length > 0 || proc.Length > 0);
                active.Add(new TriggerRecord(macro, trigger, trigger.Hotkey, scoped, title, proc));
            }
        }

        if (settings is not null)
        {
            var appHotkeys = new[]
            {
                (Label: "Record", Binding: settings.RecordHotkey),
                (Label: "Play", Binding: settings.PlayHotkey),
                (Label: "Pause / Resume", Binding: settings.PauseHotkey),
            };

            foreach (var rec in active)
            {
                var macroName = string.IsNullOrWhiteSpace(rec.Macro.Name) ? "(Unnamed macro)" : rec.Macro.Name;
                foreach (var (label, appBinding) in appHotkeys)
                {
                    if (appBinding is null || appBinding.VirtualKey <= 0) continue;
                    if (rec.Hotkey == appBinding)
                    {
                        entries.Add(new LibraryAuditEntry(
                            LibraryAuditSeverity.Warning,
                            "Hotkey",
                            rec.Macro.Id,
                            macroName,
                            null,
                            $"Trigger \"{rec.Trigger.Name}\" ({rec.Hotkey}) collides with the global {label} hotkey."));
                    }
                }
            }
        }

        for (var i = 0; i < active.Count; i++)
        {
            for (var j = i + 1; j < active.Count; j++)
            {
                var a = active[i];
                var b = active[j];
                if (a.Hotkey != b.Hotkey) continue;
                if (!ScopesOverlap(a, b)) continue;

                var aName = string.IsNullOrWhiteSpace(a.Macro.Name) ? "(Unnamed macro)" : a.Macro.Name;
                var bName = string.IsNullOrWhiteSpace(b.Macro.Name) ? "(Unnamed macro)" : b.Macro.Name;
                var scopeNote = !a.IsWindowScoped && !b.IsWindowScoped
                    ? "both are global"
                    : "their active-window filters can overlap";

                entries.Add(new LibraryAuditEntry(
                    LibraryAuditSeverity.Warning,
                    "Hotkey",
                    a.Macro.Id,
                    aName,
                    null,
                    $"Trigger hotkey {a.Hotkey} in \"{a.Trigger.Name}\" collides with \"{bName}\" (trigger \"{b.Trigger.Name}\"; {scopeNote})."));
            }
        }
    }

    private static bool ScopesOverlap(TriggerRecord a, TriggerRecord b)
    {
        if (!a.IsWindowScoped || !b.IsWindowScoped) return true;
        return FiltersOverlap(a.WindowTitle, b.WindowTitle) && FiltersOverlap(a.ProcessName, b.ProcessName);
    }

    private static bool FiltersOverlap(string filterA, string filterB)
    {
        if (string.IsNullOrEmpty(filterA) || string.IsNullOrEmpty(filterB)) return true;
        return filterA.Contains(filterB, StringComparison.OrdinalIgnoreCase)
               || filterB.Contains(filterA, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHardcodedPath(string rawPath)
    {
        var path = rawPath.Trim();
        if (path.Length == 0) return false;
        return Path.IsPathRooted(path)
               || (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':')
               || path.StartsWith(@"\\", StringComparison.Ordinal);
    }
}
