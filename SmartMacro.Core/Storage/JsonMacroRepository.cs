using SmartMacro.Core.Models;
using SmartMacro.Core.Validation;

namespace SmartMacro.Core.Storage;

/// <summary>
/// Stores one macro per JSON file in the Macros folder.
/// - Saves are atomic (temp file + replace) and keep a .bak of the previous version.
/// - Corrupted / unsupported files are reported and left alone, never deleted.
/// - Deleting moves the file into the Trash folder so it can be recovered by hand.
/// </summary>
public sealed class JsonMacroRepository : IMacroRepository
{
    private const string Extension = ".json";

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<Guid, string> _fileById = new();

    public JsonMacroRepository(string macrosDirectory, string trashDirectory)
    {
        MacrosDirectory = Path.GetFullPath(macrosDirectory);
        TrashDirectory = Path.GetFullPath(trashDirectory);
    }

    public string MacrosDirectory { get; }

    public string TrashDirectory { get; }

    public async Task<MacroLoadResult> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _fileById.Clear();
            var macros = new List<Macro>();
            var problems = new List<MacroLoadProblem>();
            var repairs = new List<string>();
            var needsRewrite = new List<Macro>();

            Directory.CreateDirectory(MacrosDirectory);
            PruneOldTrash(TrashDirectory, DefaultMaxRetainedTrashDays);

            var files = Directory.EnumerateFiles(MacrosDirectory)
                .Where(f => string.Equals(Path.GetExtension(f), Extension, StringComparison.OrdinalIgnoreCase))
                .Where(f => !Path.GetFileName(f).StartsWith('.'))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (macro, problem) = await TryReadFileAsync(file, cancellationToken).ConfigureAwait(false);
                if (problem is not null)
                {
                    problems.Add(problem);
                    continue;
                }

                var fileName = Path.GetFileName(file);
                var changed = Normalize(macro!, fileName, repairs);

                if (macro!.Id == Guid.Empty || _fileById.ContainsKey(macro.Id))
                {
                    var reason = macro.Id == Guid.Empty ? "had no ID" : "had the same ID as another macro";
                    macro.Id = Guid.NewGuid();
                    repairs.Add($"{fileName}: macro \"{macro.Name}\" {reason}, so it was given a new ID.");
                    changed = true;
                }

                _fileById[macro.Id] = file;
                macros.Add(macro);
                if (changed) needsRewrite.Add(macro);
            }

            // Write repaired macros back to their own files (previous version kept as .bak).
            foreach (var macro in needsRewrite)
            {
                try
                {
                    await WriteAsync(macro, _fileById[macro.Id], cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    problems.Add(new MacroLoadProblem(_fileById[macro.Id], $"Repairs could not be saved: {ex.Message}"));
                }
            }

            macros.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return new MacroLoadResult(macros, problems, repairs);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(Macro macro, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(macro);

        var issues = MacroValidator.Validate(macro);
        if (issues.HasErrors())
            throw new MacroValidationException(issues);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.Now;
            if (macro.CreatedAt == default) macro.CreatedAt = now;
            macro.ModifiedAt = now;
            macro.Name = macro.Name.Trim();

            if (!_fileById.TryGetValue(macro.Id, out var path))
                path = Path.Combine(MacrosDirectory, macro.Id.ToString("N") + Extension);

            await WriteAsync(macro, path, cancellationToken).ConfigureAwait(false);
            _fileById[macro.Id] = path;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_fileById.TryGetValue(id, out var path) || !File.Exists(path))
            {
                _fileById.Remove(id);
                return false;
            }

            Directory.CreateDirectory(TrashDirectory);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var baseName = Path.GetFileNameWithoutExtension(path);
            var target = Path.Combine(TrashDirectory, $"{baseName}.deleted-{stamp}{Extension}");
            for (var n = 2; File.Exists(target); n++)
                target = Path.Combine(TrashDirectory, $"{baseName}.deleted-{stamp}-{n}{Extension}");

            File.Move(path, target);

            var backup = AtomicFile.BackupPathFor(path);
            if (File.Exists(backup))
            {
                try { File.Move(backup, target + ".bak"); }
                catch (IOException) { /* The backup is optional; the macro itself is already in Trash. */ }
            }

            _fileById.Remove(id);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public const int DefaultMaxRetainedTrashDays = 30;

    /// <summary>
    /// Removes trashed macro files older than <paramref name="maxDays"/> days so the Trash folder stays clean over long-term use.
    /// </summary>
    public static int PruneOldTrash(string? trashDirectory, int maxDays = DefaultMaxRetainedTrashDays, DateTimeOffset? referenceUtcNow = null)
    {
        if (string.IsNullOrWhiteSpace(trashDirectory) || maxDays < 1 || !Directory.Exists(trashDirectory))
            return 0;

        var deleted = 0;
        var cutoffUtc = (referenceUtcNow ?? DateTimeOffset.UtcNow).UtcDateTime.AddDays(-maxDays);
        try
        {
            foreach (var file in new DirectoryInfo(trashDirectory).GetFiles("*"))
            {
                if (file.LastWriteTimeUtc < cutoffUtc)
                {
                    try
                    {
                        file.Delete();
                        deleted++;
                    }
                    catch
                    {
                        // Ignore locked files.
                    }
                }
            }
        }
        catch
        {
            // Ignore cleanup errors.
        }

        return deleted;
    }

    public async Task<IReadOnlyList<TrashedMacroItem>> LoadTrashAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(TrashDirectory))
                return Array.Empty<TrashedMacroItem>();

            var items = new List<TrashedMacroItem>();
            var files = Directory.EnumerateFiles(TrashDirectory)
                .Where(f => string.Equals(Path.GetExtension(f), Extension, StringComparison.OrdinalIgnoreCase))
                .Where(f => !Path.GetFileName(f).StartsWith('.'))
                .ToList();

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (macro, problem) = await TryReadFileAsync(file, cancellationToken).ConfigureAwait(false);
                if (problem is not null || macro is null) continue;

                var info = new FileInfo(file);
                var deletedAt = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                items.Add(new TrashedMacroItem(
                    Path.GetFileName(file),
                    string.IsNullOrWhiteSpace(macro.Name) ? "Untitled Macro" : macro.Name.Trim(),
                    macro.Steps?.Count ?? 0,
                    deletedAt,
                    macro.ProfileName));
            }

            return items
                .OrderByDescending(i => i.DeletedAt)
                .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Macro?> RestoreFromTrashAsync(string trashFileName, IEnumerable<string>? existingNames = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(trashFileName)) return null;
        var safeFileName = Path.GetFileName(trashFileName);

        Macro? restored;
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var trashPath = Path.Combine(TrashDirectory, safeFileName);
            if (!File.Exists(trashPath)) return null;

            var (macro, problem) = await TryReadFileAsync(trashPath, cancellationToken).ConfigureAwait(false);
            if (problem is not null || macro is null) return null;

            Normalize(macro, safeFileName, new List<string>());
            if (macro.Id == Guid.Empty || _fileById.ContainsKey(macro.Id))
                macro.Id = Guid.NewGuid();

            if (existingNames is not null)
            {
                var taken = new HashSet<string>(existingNames, StringComparer.CurrentCultureIgnoreCase);
                var baseName = string.IsNullOrWhiteSpace(macro.Name) ? "Restored Macro" : macro.Name.Trim();
                var candidate = baseName;
                for (var n = 2; taken.Contains(candidate); n++)
                    candidate = $"{baseName} ({n})";
                macro.Name = candidate;
            }

            Directory.CreateDirectory(MacrosDirectory);
            var targetPath = Path.Combine(MacrosDirectory, macro.Id.ToString("N") + Extension);
            macro.ModifiedAt = DateTimeOffset.Now;
            await WriteAsync(macro, targetPath, cancellationToken).ConfigureAwait(false);
            _fileById[macro.Id] = targetPath;

            try { File.Delete(trashPath); }
            catch (IOException) { /* Best-effort delete */ }

            var bakPath = trashPath + ".bak";
            if (File.Exists(bakPath))
            {
                try { File.Delete(bakPath); }
                catch (IOException) { }
            }

            restored = macro;
        }
        finally
        {
            _lock.Release();
        }

        return restored;
    }

    public async Task<bool> DeleteFromTrashAsync(string trashFileName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(trashFileName)) return false;
        var safeFileName = Path.GetFileName(trashFileName);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var trashPath = Path.Combine(TrashDirectory, safeFileName);
            if (!File.Exists(trashPath)) return false;

            File.Delete(trashPath);
            var bakPath = trashPath + ".bak";
            if (File.Exists(bakPath))
            {
                try { File.Delete(bakPath); }
                catch (IOException) { }
            }

            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<int> EmptyTrashAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(TrashDirectory)) return 0;
            var deleted = 0;
            foreach (var file in Directory.EnumerateFiles(TrashDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (string.Equals(Path.GetExtension(file), Extension, StringComparison.OrdinalIgnoreCase))
                        deleted++;
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // Ignore locked file.
                }
            }

            return deleted;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static async Task<(Macro? Macro, MacroLoadProblem? Problem)> TryReadFileAsync(string file, CancellationToken cancellationToken)
    {
        string json;
        try
        {
            var info = new FileInfo(file);
            if (info.Length > MacroFileFormat.MaxFileSizeBytes)
                return (null, new MacroLoadProblem(file, $"The file is too large ({info.Length / 1024 / 1024} MB) to be a macro."));
            json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, new MacroLoadProblem(file, $"The file could not be read: {ex.Message}"));
        }

        if (MacroFileFormat.TryParse(json, out var macro, out var error))
            return (macro, null);
        return (null, new MacroLoadProblem(file, error ?? "The file could not be read."));
    }

    /// <summary>Fixes harmless structural gaps in loaded data. Returns true if anything changed.</summary>
    private static bool Normalize(Macro macro, string fileName, List<string> repairs)
    {
        var changed = false;

        if (macro.Name is null)
        {
            macro.Name = string.Empty;
            changed = true;
        }

        if (macro.Steps is null)
        {
            macro.Steps = new List<MacroStep>();
            repairs.Add($"{fileName}: the step list was missing and was reset to empty.");
            changed = true;
        }

        var removed = macro.Steps.RemoveAll(s => s is null);
        if (removed > 0)
        {
            repairs.Add($"{fileName}: removed {removed} empty step entr{(removed == 1 ? "y" : "ies")}.");
            changed = true;
        }

        var seen = new HashSet<Guid>();
        foreach (var step in macro.Steps)
        {
            if (step.Id == Guid.Empty || !seen.Add(step.Id))
            {
                step.Id = Guid.NewGuid();
                seen.Add(step.Id);
                changed = true;
            }
        }

        if (macro.CreatedAt == default)
        {
            macro.CreatedAt = macro.ModifiedAt != default ? macro.ModifiedAt : DateTimeOffset.Now;
            changed = true;
        }

        return changed;
    }

    private static Task WriteAsync(Macro macro, string path, CancellationToken cancellationToken)
    {
        return AtomicFile.WriteAllTextAsync(path, MacroFileFormat.Serialize(macro), cancellationToken);
    }
}
