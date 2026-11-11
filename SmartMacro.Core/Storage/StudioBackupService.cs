using System.IO.Compression;
using System.Text;
using System.Text.Json;
using SmartMacro.Core.Models;
using SmartMacro.Core.Validation;

namespace SmartMacro.Core.Storage;

/// <summary>Metadata stored inside a <c>.smbackup</c> archive so backups are self-describing.</summary>
public sealed class StudioBackupManifest
{
    public int FormatVersion { get; set; } = 1;
    public string AppVersion { get; set; } = "1.0.0";
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public int MacroCount { get; set; }
    public bool IncludesSettings { get; set; }
}

/// <summary>Outcome of restoring a <c>.smbackup</c> archive.</summary>
public sealed record StudioRestoreResult(
    IReadOnlyList<Macro> RestoredMacros,
    AppSettings? RestoredSettings,
    IReadOnlyList<string> SkippedFiles);

/// <summary>
/// Packages and restores complete SmartMacro Studio backups (<c>.smbackup</c> ZIP archives containing all library
/// macros, profiles, and optionally <c>settings.json</c>) using built-in <see cref="ZipArchive"/> with zero external dependencies.
/// </summary>
public static class StudioBackupService
{
    public const string Extension = ".smbackup";
    public const string ManifestEntryName = "backup-manifest.json";
    public const string SettingsEntryName = "settings.json";
    public const string MacrosFolderPrefix = "Macros/";
    public const long MaxArchiveSizeBytes = 100 * 1024 * 1024; // 100 MB safety ceiling

    public static string DefaultBackupFileName(DateTime? now = null) =>
        $"SmartMacro-Backup-{(now ?? DateTime.Now):yyyy-MM-dd}{Extension}";

    /// <summary>
    /// Creates a <c>.smbackup</c> ZIP archive at <paramref name="destinationFilePath"/> containing <paramref name="macros"/>
    /// and optionally <paramref name="settings"/>.
    /// </summary>
    public static async Task<StudioBackupManifest> CreateBackupAsync(
        string destinationFilePath,
        IReadOnlyList<Macro> macros,
        AppSettings? settings = null,
        string appVersion = "1.0.0",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFilePath);
        ArgumentNullException.ThrowIfNull(macros);

        var fullPath = Path.GetFullPath(destinationFilePath);
        DiskSpaceGuard.EnsureEnoughFreeSpace(fullPath, "creating backup archive");
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tempPath = fullPath + $".{Guid.NewGuid():N}.tmp";
        var manifest = new StudioBackupManifest
        {
            FormatVersion = 1,
            AppVersion = appVersion,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            MacroCount = macros.Count,
            IncludesSettings = settings is not null,
        };

        try
        {
            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
            {
                await WriteTextEntryAsync(archive, ManifestEntryName, MacroJson.Serialize(manifest), cancellationToken).ConfigureAwait(false);

                if (settings is not null)
                {
                    var sanitized = settings.Clone();
                    sanitized.Sanitize();
                    await WriteTextEntryAsync(archive, SettingsEntryName, MacroJson.Serialize(sanitized), cancellationToken).ConfigureAwait(false);
                }

                var usedEntryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var macro in macros)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (macro is null) continue;

                    var idPart = macro.Id == Guid.Empty ? Guid.NewGuid().ToString("N") : macro.Id.ToString("N");
                    var entryName = $"{MacrosFolderPrefix}{idPart}.json";
                    for (var n = 2; !usedEntryNames.Add(entryName); n++)
                        entryName = $"{MacrosFolderPrefix}{idPart}-{n}.json";

                    var serialized = MacroFileFormat.Serialize(macro);
                    await WriteTextEntryAsync(archive, entryName, serialized, cancellationToken).ConfigureAwait(false);
                }
            }

            File.Move(tempPath, fullPath, overwrite: true);
            return manifest;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch { /* ignore cleanup failure */ }
            }
        }
    }

    /// <summary>
    /// Reads and validates a <c>.smbackup</c> (or <c>.zip</c>) archive, returning all valid macros and optional settings.
    /// Does not overwrite existing library macros directly so the caller can save and merge safely.
    /// </summary>
    public static async Task<StudioRestoreResult> ReadBackupAsync(
        string archiveFilePath,
        IEnumerable<string>? existingLibraryNames = null,
        IEnumerable<Guid>? existingLibraryIds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveFilePath);
        var info = new FileInfo(archiveFilePath);
        if (!info.Exists)
            throw new FileNotFoundException("The backup file could not be found.", archiveFilePath);
        if (info.Length > MaxArchiveSizeBytes)
            throw new InvalidDataException("The backup file is too large (exceeds 100 MB).");

        var takenNames = new HashSet<string>(existingLibraryNames ?? Enumerable.Empty<string>(), StringComparer.CurrentCultureIgnoreCase);
        var takenIds = new HashSet<Guid>(existingLibraryIds ?? Enumerable.Empty<Guid>());
        var restoredMacros = new List<Macro>();
        var skipped = new List<string>();
        AppSettings? restoredSettings = null;

        await using var fs = new FileStream(archiveFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry

            if (string.Equals(entry.FullName, ManifestEntryName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(entry.FullName, SettingsEntryName, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var settingsJson = await ReadEntryTextAsync(entry, MacroFileFormat.MaxFileSizeBytes, cancellationToken).ConfigureAwait(false);
                    var parsedSettings = MacroJson.Deserialize<AppSettings>(settingsJson);
                    if (parsedSettings is not null)
                    {
                        parsedSettings.Sanitize();
                        restoredSettings = parsedSettings;
                    }
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException)
                {
                    skipped.Add($"settings.json: {ex.Message}");
                }
                continue;
            }

            var isMacroEntry = entry.FullName.StartsWith(MacrosFolderPrefix, StringComparison.OrdinalIgnoreCase)
                               || entry.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                               || entry.Name.EndsWith(".smacro", StringComparison.OrdinalIgnoreCase);
            if (!isMacroEntry) continue;

            try
            {
                var json = await ReadEntryTextAsync(entry, MacroFileFormat.MaxFileSizeBytes, cancellationToken).ConfigureAwait(false);
                if (!MacroFileFormat.TryParse(json, out var macro, out var error) || macro is null)
                {
                    skipped.Add($"{entry.Name}: {error ?? "Invalid macro format."}");
                    continue;
                }

                var issues = MacroValidator.Validate(macro);
                if (issues.HasErrors())
                {
                    var firstError = issues.First(i => i.Severity == IssueSeverity.Error).DisplayText;
                    skipped.Add($"{entry.Name}: {firstError}");
                    continue;
                }

                if (macro.Id == Guid.Empty || !takenIds.Add(macro.Id))
                {
                    macro.Id = Guid.NewGuid();
                    takenIds.Add(macro.Id);
                }

                var baseName = string.IsNullOrWhiteSpace(macro.Name) ? "Restored Macro" : macro.Name.Trim();
                var candidate = baseName;
                for (var n = 2; !takenNames.Add(candidate); n++)
                    candidate = $"{baseName} ({n})";
                macro.Name = candidate;

                restoredMacros.Add(macro);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
            {
                skipped.Add($"{entry.Name}: {ex.Message}");
            }
        }

        return new StudioRestoreResult(restoredMacros, restoredSettings, skipped);
    }

    private static async Task WriteTextEntryAsync(ZipArchive archive, string entryName, string content, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadEntryTextAsync(ZipArchiveEntry entry, long maxBytes, CancellationToken cancellationToken)
    {
        if (entry.Length > maxBytes)
            throw new InvalidDataException($"Entry \"{entry.Name}\" exceeds the maximum size limit.");

        await using var stream = entry.Open();
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read <= 0) break;
            total += read;
            if (total > maxBytes)
                throw new InvalidDataException($"Entry \"{entry.Name}\" exceeds the maximum size limit.");
            ms.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
