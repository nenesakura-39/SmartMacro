using SmartMacro.Core.Models;

namespace SmartMacro.Core.Storage;

/// <summary>A file that couldn't be loaded. The file is left untouched on disk.</summary>
public sealed record MacroLoadProblem(string FilePath, string Message)
{
    public string FileName => Path.GetFileName(FilePath);
}

public sealed class MacroLoadResult
{
    public MacroLoadResult(IReadOnlyList<Macro> macros, IReadOnlyList<MacroLoadProblem> problems, IReadOnlyList<string> repairs)
    {
        Macros = macros;
        Problems = problems;
        Repairs = repairs;
    }

    public IReadOnlyList<Macro> Macros { get; }

    /// <summary>Files that were skipped (corrupted, newer version, unreadable...).</summary>
    public IReadOnlyList<MacroLoadProblem> Problems { get; }

    /// <summary>Automatic fixes that were applied (e.g. duplicate IDs replaced).</summary>
    public IReadOnlyList<string> Repairs { get; }
}

/// <summary>Metadata for a deleted macro sitting in the recoverable Trash folder.</summary>
public sealed record TrashedMacroItem(
    string TrashFileName,
    string Name,
    int StepCount,
    DateTimeOffset DeletedAt,
    string? ProfileName = null)
{
    public string DeletedAtText => DeletedAt.ToLocalTime().ToString("MMM d, yyyy  h:mm tt");
    public string StepCountText => StepCount == 1 ? "1 step" : $"{StepCount:N0} steps";
}

public interface IMacroRepository
{
    string MacrosDirectory { get; }

    string TrashDirectory { get; }

    /// <summary>Loads every macro file. Bad files are reported, never deleted.</summary>
    Task<MacroLoadResult> LoadAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates and saves a macro. Updates ModifiedAt (and CreatedAt if unset) on the passed object.
    /// Throws MacroValidationException if the macro has errors.
    /// </summary>
    Task SaveAsync(Macro macro, CancellationToken cancellationToken = default);

    /// <summary>Moves the macro file to the Trash folder (recoverable). Returns false if it wasn't found.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Lists all recoverable macros currently in the Trash folder (newest first).</summary>
    Task<IReadOnlyList<TrashedMacroItem>> LoadTrashAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TrashedMacroItem>>(Array.Empty<TrashedMacroItem>());

    /// <summary>Restores a deleted macro from Trash back into the active library, resolving any ID or name conflict.</summary>
    Task<Macro?> RestoreFromTrashAsync(string trashFileName, IEnumerable<string>? existingNames = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<Macro?>(null);

    /// <summary>Permanently deletes one file from the Trash folder.</summary>
    Task<bool> DeleteFromTrashAsync(string trashFileName, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    /// <summary>Permanently deletes all files in the Trash folder. Returns the number of deleted macro files.</summary>
    Task<int> EmptyTrashAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(0);
}
