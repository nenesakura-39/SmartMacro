using System.Text.Json;
using SmartMacro.Core.Models;

namespace SmartMacro.Core.Storage;

/// <summary>
/// Reading and writing a single macro file (the library's own files and exported/imported .smacro / .smartmacro files).
/// Only known step types are accepted, so an imported file can describe actions but never contain code.
/// </summary>
public static class MacroFileFormat
{
    /// <summary>Extension of exported / shared macro files (one self-contained file, pictures and sub-macros included).</summary>
    public const string ExportExtension = ".smacro";

    /// <summary>The extension older SmartMacro builds exported with (still imported).</summary>
    public const string LegacyExportExtension = ".smartmacro";

    /// <summary>Largest file we'll try to read (protects against huge or wrong files).</summary>
    public const long MaxFileSizeBytes = 20 * 1024 * 1024;

    public static string Serialize(Macro macro) => Serialize(macro, library: null);

    /// <summary>
    /// Serializes <paramref name="macro"/> and, when <paramref name="library"/> is supplied, automatically bundles
    /// every referenced sub-macro (called sub-macros, recovery sub-macros, and watcher handlers) recursively.
    /// </summary>
    public static string Serialize(Macro macro, IReadOnlyList<Macro>? library)
    {
        ArgumentNullException.ThrowIfNull(macro);
        var bundled = CollectReferencedSubMacros(macro, library);
        var version = MacroDocument.RequiredSchemaVersion(macro);
        if (bundled.Count > 0)
        {
            version = Math.Max(version, 16);
            foreach (var sub in bundled)
                version = Math.Max(version, MacroDocument.RequiredSchemaVersion(sub));
        }

        return MacroJson.Serialize(new MacroDocument
        {
            SchemaVersion = version,
            Macro = macro,
            SubMacros = bundled.Count > 0 ? bundled : null,
        });
    }

    /// <summary>Parses a macro file. Returns false with a plain-English error if it can't be used.</summary>
    public static bool TryParse(string json, out Macro? macro, out string? error) =>
        TryParseBundle(json, out macro, out _, out error);

    /// <summary>Parses a macro file along with any bundled sub-macros.</summary>
    public static bool TryParseBundle(string json, out Macro? macro, out List<Macro> subMacros, out string? error)
    {
        macro = null;
        subMacros = [];
        MacroDocument? document;
        try
        {
            document = MacroJson.Deserialize<MacroDocument>(json);
        }
        catch (JsonException ex)
        {
            error = $"The file is damaged or is not a SmartMacro macro ({ex.Message}).";
            return false;
        }
        catch (NotSupportedException ex)
        {
            error = $"The file contains unsupported data ({ex.Message}).";
            return false;
        }

        if (document is null)
        {
            error = "The file is empty.";
            return false;
        }

        if (document.SchemaVersion > MacroDocument.CurrentSchemaVersion)
        {
            error = $"This macro was made by a newer version of SmartMacro (format {document.SchemaVersion}). Update SmartMacro to open it.";
            return false;
        }

        if (document.SchemaVersion < 1)
        {
            error = "The file has no valid format version.";
            return false;
        }

        if (document.Macro is null)
        {
            error = "The file has no macro data.";
            return false;
        }

        macro = document.Macro;
        if (document.SubMacros is { Count: > 0 } list)
        {
            subMacros = list.Where(m => m is not null).ToList();
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Makes an imported macro safe to add to the library: fresh IDs (so it never overwrites an existing macro),
    /// a unique name, no empty steps, and new timestamps.
    /// </summary>
    public static Macro PrepareForImport(Macro macro, IEnumerable<string> existingNames)
    {
        RenewMacroIdsAndClean(macro);
        macro.Name = MacroFactory.MakeUniqueName(string.IsNullOrWhiteSpace(macro.Name) ? "Imported Macro" : macro.Name, existingNames);
        var now = DateTimeOffset.Now;
        macro.CreatedAt = now;
        macro.ModifiedAt = now;
        return macro;
    }

    /// <summary>
    /// Prepares an imported macro AND its bundled sub-macros for the library: gives every macro and step a fresh ID,
    /// ensures unique names across the library, and remaps all sub-macro links so the imported bundle works immediately.
    /// </summary>
    public static (Macro Main, List<Macro> SubMacros) PrepareBundleForImport(
        Macro macro,
        IReadOnlyList<Macro>? subMacros,
        IEnumerable<string> existingNames)
    {
        ArgumentNullException.ThrowIfNull(macro);
        var taken = new List<string>(existingNames.Where(n => !string.IsNullOrWhiteSpace(n)));

        var idMap = new Dictionary<Guid, (Guid NewId, string NewName)>();
        var nameMap = new Dictionary<string, (Guid NewId, string NewName)>(StringComparer.OrdinalIgnoreCase);

        var preparedSubs = new List<Macro>();
        if (subMacros is { Count: > 0 })
        {
            foreach (var rawSub in subMacros)
            {
                if (rawSub is null) continue;
                var oldId = rawSub.Id;
                var oldName = rawSub.Name?.Trim() ?? string.Empty;

                var prepared = PrepareForImport(rawSub, taken);
                taken.Add(prepared.Name);

                if (oldId != Guid.Empty)
                    idMap[oldId] = (prepared.Id, prepared.Name);
                if (oldName.Length > 0)
                    nameMap[oldName] = (prepared.Id, prepared.Name);

                preparedSubs.Add(prepared);
            }
        }

        var oldMainId = macro.Id;
        var oldMainName = macro.Name?.Trim() ?? string.Empty;
        var preparedMain = PrepareForImport(macro, taken);
        if (oldMainId != Guid.Empty)
            idMap[oldMainId] = (preparedMain.Id, preparedMain.Name);
        if (oldMainName.Length > 0)
            nameMap[oldMainName] = (preparedMain.Id, preparedMain.Name);

        RemapReferences(preparedMain, idMap, nameMap);
        foreach (var sub in preparedSubs)
            RemapReferences(sub, idMap, nameMap);

        return (preparedMain, preparedSubs);
    }

    /// <summary>Collects all sub-macros transitively referenced by <paramref name="root"/> from <paramref name="library"/>.</summary>
    public static List<Macro> CollectReferencedSubMacros(Macro root, IReadOnlyList<Macro>? library)
    {
        var result = new List<Macro>();
        if (library is null || library.Count == 0) return result;

        var visited = new HashSet<Guid>();
        if (root.Id != Guid.Empty) visited.Add(root.Id);

        var queue = new Queue<Macro>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var (targetId, targetName) in DirectSubMacroLinks(current))
            {
                var found = ResolveMacro(library, targetId, targetName);
                if (found is null || found.Id == Guid.Empty || !visited.Add(found.Id)) continue;
                var clone = MacroJson.Clone(found);
                result.Add(clone);
                queue.Enqueue(found);
            }
        }

        return result;
    }

    /// <summary>Resolves a sub-macro by ID first, falling back to case-insensitive name match.</summary>
    public static Macro? ResolveMacro(IReadOnlyList<Macro> library, Guid id, string? name)
    {
        if (id != Guid.Empty)
        {
            var byId = library.FirstOrDefault(m => m.Id == id);
            if (byId is not null) return byId;
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            var trimmed = name.Trim();
            return library.FirstOrDefault(m => string.Equals(m.Name?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    /// <summary>Enumerates all direct sub-macro references (CallMacroStep, FailurePolicy, Watchers) in a macro.</summary>
    public static IEnumerable<(Guid Id, string Name)> DirectSubMacroLinks(Macro macro)
    {
        if (macro.Steps is { } steps)
        {
            foreach (var step in steps)
            {
                switch (step)
                {
                    case CallMacroStep call when call.TargetMacroId != Guid.Empty || !string.IsNullOrWhiteSpace(call.TargetMacroName):
                        yield return (call.TargetMacroId, call.TargetMacroName);
                        break;
                    case ScreenSearchStep { FailurePolicy: { Action: StepFailureAction.RunRecoveryMacro } p }:
                        yield return (p.RecoveryMacroId ?? Guid.Empty, p.RecoveryMacroName ?? string.Empty);
                        break;
                    case WaitForChangeStep { FailurePolicy: { Action: StepFailureAction.RunRecoveryMacro } p }:
                        yield return (p.RecoveryMacroId ?? Guid.Empty, p.RecoveryMacroName ?? string.Empty);
                        break;
                    case FocusWindowStep { FailurePolicy: { Action: StepFailureAction.RunRecoveryMacro } p }:
                        yield return (p.RecoveryMacroId ?? Guid.Empty, p.RecoveryMacroName ?? string.Empty);
                        break;
                }
            }
        }

        if (macro.Watchers is { } watchers)
        {
            foreach (var watcher in watchers)
            {
                if (watcher is { Action: WatcherActionKind.RunSubMacro })
                    yield return (watcher.HandlerMacroId ?? Guid.Empty, watcher.HandlerMacroName ?? string.Empty);
            }
        }
    }

    private static void RenewMacroIdsAndClean(Macro macro)
    {
        macro.Steps ??= [];
        macro.Steps.RemoveAll(s => s is null);
        foreach (var step in macro.Steps)
            MacroFactory.RenewIds(step);

        if (macro.Variables is { } vars)
        {
            vars.RemoveAll(v => v is null);
            if (vars.Count == 0) macro.Variables = null;
        }

        if (macro.Watchers is { } watchers)
        {
            watchers.RemoveAll(w => w is null);
            foreach (var w in watchers)
            {
                w.Id = Guid.NewGuid();
                if (w.Condition?.Check is { } check) check.Id = Guid.NewGuid();
            }

            if (watchers.Count == 0) macro.Watchers = null;
        }

        if (macro.Triggers is { } triggers)
        {
            triggers.RemoveAll(t => t is null);
            foreach (var t in triggers)
                t.Id = Guid.NewGuid();
            if (triggers.Count == 0) macro.Triggers = null;
        }

        macro.Id = Guid.NewGuid();
    }

    private static void RemapReferences(
        Macro macro,
        Dictionary<Guid, (Guid NewId, string NewName)> idMap,
        Dictionary<string, (Guid NewId, string NewName)> nameMap)
    {
        static bool TryLookup(
            Guid id,
            string? name,
            Dictionary<Guid, (Guid NewId, string NewName)> ids,
            Dictionary<string, (Guid NewId, string NewName)> names,
            out (Guid NewId, string NewName) mapped)
        {
            if (id != Guid.Empty && ids.TryGetValue(id, out mapped)) return true;
            if (!string.IsNullOrWhiteSpace(name) && names.TryGetValue(name.Trim(), out mapped)) return true;
            mapped = default;
            return false;
        }

        foreach (var step in macro.Steps)
        {
            switch (step)
            {
                case CallMacroStep call:
                    if (TryLookup(call.TargetMacroId, call.TargetMacroName, idMap, nameMap, out var callTarget))
                    {
                        call.TargetMacroId = callTarget.NewId;
                        call.TargetMacroName = callTarget.NewName;
                    }
                    break;

                case ScreenSearchStep { FailurePolicy: { Action: StepFailureAction.RunRecoveryMacro } p }:
                    if (TryLookup(p.RecoveryMacroId ?? Guid.Empty, p.RecoveryMacroName, idMap, nameMap, out var rec1))
                    {
                        p.RecoveryMacroId = rec1.NewId;
                        p.RecoveryMacroName = rec1.NewName;
                    }
                    break;

                case WaitForChangeStep { FailurePolicy: { Action: StepFailureAction.RunRecoveryMacro } p }:
                    if (TryLookup(p.RecoveryMacroId ?? Guid.Empty, p.RecoveryMacroName, idMap, nameMap, out var rec2))
                    {
                        p.RecoveryMacroId = rec2.NewId;
                        p.RecoveryMacroName = rec2.NewName;
                    }
                    break;

                case FocusWindowStep { FailurePolicy: { Action: StepFailureAction.RunRecoveryMacro } p }:
                    if (TryLookup(p.RecoveryMacroId ?? Guid.Empty, p.RecoveryMacroName, idMap, nameMap, out var rec3))
                    {
                        p.RecoveryMacroId = rec3.NewId;
                        p.RecoveryMacroName = rec3.NewName;
                    }
                    break;
            }
        }

        if (macro.Watchers is { } watchers)
        {
            foreach (var watcher in watchers)
            {
                if (watcher.Action == WatcherActionKind.RunSubMacro
                    && TryLookup(watcher.HandlerMacroId ?? Guid.Empty, watcher.HandlerMacroName, idMap, nameMap, out var handler))
                {
                    watcher.HandlerMacroId = handler.NewId;
                    watcher.HandlerMacroName = handler.NewName;
                }
            }
        }
    }
}
