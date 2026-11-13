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
        var preparedMacro = MacroAssetStore.PrepareForSerialization(macro);
        var bundled = CollectReferencedSubMacros(macro, library);
        var preparedBundled = new List<Macro>(bundled.Count);
        foreach (var sub in bundled)
            preparedBundled.Add(MacroAssetStore.PrepareForSerialization(sub));

        var version = MacroDocument.RequiredSchemaVersion(preparedMacro);
        if (preparedBundled.Count > 0)
        {
            version = Math.Max(version, 16);
            foreach (var sub in preparedBundled)
                version = Math.Max(version, MacroDocument.RequiredSchemaVersion(sub));
        }

        return MacroJson.Serialize(new MacroDocument
        {
            SchemaVersion = version,
            Macro = preparedMacro,
            SubMacros = preparedBundled.Count > 0 ? preparedBundled : null,
        });
    }

    /// <summary>Parses a macro file. Returns false with a plain-English error if it can't be used.</summary>
    public static bool TryParse(string json, out Macro? macro, out string? error) =>
        TryParseBundle(json, out macro, out _, out error);

    /// <summary>Parses a macro file along with any bundled sub-macros.</summary>
    public static bool TryParseBundle(string json, out Macro? macro, out List<Macro> subMacros, out string? error) =>
        TryParseBundle(json, out macro, out subMacros, out _, out error);

    /// <summary>
    /// Parses a macro file along with any bundled sub-macros and returns any automatic migration / legacy-upgrade notes.
    /// Pre-checks <c>schemaVersion</c> before step deserialization so files from newer or outdated incompatible versions
    /// report a clear version warning instead of a generic "file is damaged" error.
    /// </summary>
    public static bool TryParseBundle(
        string json,
        out Macro? macro,
        out List<Macro> subMacros,
        out List<string> migrationNotes,
        out string? error)
    {
        macro = null;
        subMacros = [];
        migrationNotes = [];

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "The file is empty.";
            return false;
        }

        int? rawSchemaVersion = null;
        string? rawAppVersion = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("schemaVersion", out var svProp) && svProp.TryGetInt32(out var sv))
                    rawSchemaVersion = sv;
                else if (doc.RootElement.TryGetProperty("SchemaVersion", out var svProp2) && svProp2.TryGetInt32(out var sv2))
                    rawSchemaVersion = sv2;

                if (doc.RootElement.TryGetProperty("appVersion", out var avProp) && avProp.ValueKind == JsonValueKind.String)
                    rawAppVersion = avProp.GetString();
                else if (doc.RootElement.TryGetProperty("AppVersion", out var avProp2) && avProp2.ValueKind == JsonValueKind.String)
                    rawAppVersion = avProp2.GetString();
            }
        }
        catch (JsonException ex)
        {
            error = $"The file is damaged or is not a SmartMacro macro ({ex.Message}).";
            return false;
        }

        if (rawSchemaVersion is int earlyVersion)
        {
            if (earlyVersion > MacroDocument.CurrentSchemaVersion)
            {
                var appNote = !string.IsNullOrWhiteSpace(rawAppVersion) ? $" (SmartMacro {rawAppVersion})" : string.Empty;
                error = $"This .smacro file was created by a newer version of SmartMacro{appNote} (format v{earlyVersion}, current supported is v{MacroDocument.CurrentSchemaVersion}). Please update SmartMacro to open it.";
                return false;
            }

            if (earlyVersion < 1)
            {
                error = "This .smacro file is outdated or has no valid format version.";
                return false;
            }
        }

        MacroDocument? document;
        try
        {
            document = MacroJson.Deserialize<MacroDocument>(json);
        }
        catch (JsonException ex)
        {
            if (rawSchemaVersion is int ver && ver < MacroDocument.CurrentSchemaVersion)
            {
                error = $"This .smacro file was made with an older, incompatible version of SmartMacro (format v{ver}) and could not be loaded ({ex.Message}).";
            }
            else
            {
                error = $"The file is damaged or is not a compatible SmartMacro macro ({ex.Message}).";
            }
            return false;
        }
        catch (NotSupportedException ex)
        {
            error = $"This .smacro file uses unsupported or outdated step data ({ex.Message}).";
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
        var internPool = new Dictionary<string, string>(StringComparer.Ordinal);
        MacroAssetStore.Hydrate(macro, internPool: internPool);
        MigrateLegacyMacro(macro, document.SchemaVersion, migrationNotes);
        if (document.SubMacros is { Count: > 0 } list)
        {
            subMacros = list.Where(m => m is not null).ToList();
            foreach (var sub in subMacros)
            {
                MacroAssetStore.Hydrate(sub, internPool: internPool);
                MigrateLegacyMacro(sub, document.SchemaVersion, migrationNotes);
            }
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Automatically upgrades older <c>.smacro</c> files so legacy fields or out-of-range check intervals remain playable
    /// in newer SmartMacro versions, recording human-readable notes when adjustments are made.
    /// </summary>
    public static void MigrateLegacyMacro(Macro macro, int fromSchemaVersion, List<string>? notes = null)
    {
        if (macro.Steps is null) return;

        var clampedIntervals = 0;
        foreach (var step in macro.Steps)
        {
            if (step is ScreenSearchStep search)
            {
                if (search.CheckIntervalMs < Validation.MacroValidator.MinCheckIntervalMs)
                {
                    search.CheckIntervalMs = Validation.MacroValidator.MinCheckIntervalMs;
                    clampedIntervals++;
                }
                else if (search.CheckIntervalMs > Validation.MacroValidator.MaxCheckIntervalMs)
                {
                    search.CheckIntervalMs = Validation.MacroValidator.MaxCheckIntervalMs;
                    clampedIntervals++;
                }
            }
        }

        if (clampedIntervals > 0)
        {
            notes?.Add($"Updated {clampedIntervals} legacy check interval(s) in \"{macro.Name}\" to match current engine limits.");
        }
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
