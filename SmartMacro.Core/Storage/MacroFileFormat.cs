using System.Text.Json;
using SmartMacro.Core.Models;

namespace SmartMacro.Core.Storage;

/// <summary>
/// Reading and writing a single macro file (the library's own files and exported/imported .smacro / .smartmacro files).
/// Only known step types are accepted, so an imported file can describe actions but never contain code.
/// </summary>
public static class MacroFileFormat
{
    /// <summary>Extension of exported / shared macro files (one self-contained file, pictures included).</summary>
    public const string ExportExtension = ".smacro";

    /// <summary>The extension older SmartMacro builds exported with (still imported).</summary>
    public const string LegacyExportExtension = ".smartmacro";

    /// <summary>Largest file we'll try to read (protects against huge or wrong files).</summary>
    public const long MaxFileSizeBytes = 20 * 1024 * 1024;

    public static string Serialize(Macro macro) =>
        MacroJson.Serialize(new MacroDocument { SchemaVersion = MacroDocument.RequiredSchemaVersion(macro), Macro = macro });

    /// <summary>Parses a macro file. Returns false with a plain-English error if it can't be used.</summary>
    public static bool TryParse(string json, out Macro? macro, out string? error)
    {
        macro = null;
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
        error = null;
        return true;
    }

    /// <summary>
    /// Makes an imported macro safe to add to the library: fresh IDs (so it never overwrites an existing macro),
    /// a unique name, no empty steps, and new timestamps.
    /// </summary>
    public static Macro PrepareForImport(Macro macro, IEnumerable<string> existingNames)
    {
        macro.Steps ??= new List<MacroStep>();
        macro.Steps.RemoveAll(s => s is null);
        foreach (var step in macro.Steps) step.Id = Guid.NewGuid();

        macro.Id = Guid.NewGuid();
        macro.Name = MacroFactory.MakeUniqueName(string.IsNullOrWhiteSpace(macro.Name) ? "Imported Macro" : macro.Name, existingNames);
        var now = DateTimeOffset.Now;
        macro.CreatedAt = now;
        macro.ModifiedAt = now;
        return macro;
    }
}
