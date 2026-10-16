using System.Text.Json;
using SmartMacro.Core.Models;

namespace SmartMacro.Core.Storage;

public sealed record SettingsLoadResult(AppSettings Settings, string? Warning);

public interface ISettingsStore
{
    Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads/writes settings.json. A missing file just means defaults.
/// A damaged file is renamed (kept for inspection) and defaults are used, with a warning.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly string _path;

    public JsonSettingsStore(string settingsFilePath)
    {
        _path = Path.GetFullPath(settingsFilePath);
    }

    public async Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
            return new SettingsLoadResult(new AppSettings(), null);

        try
        {
            var json = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
            var settings = MacroJson.Deserialize<AppSettings>(json);
            if (settings is null)
                return new SettingsLoadResult(new AppSettings(), "The settings file was empty, so default settings are used.");

            if (settings.SchemaVersion > AppSettings.CurrentSchemaVersion)
            {
                // Don't overwrite a newer file's unknown values: keep a copy before we save over it.
                var copy = KeepCopy("newer");
                settings.Sanitize();
                return new SettingsLoadResult(settings,
                    $"The settings file came from a newer SmartMacro version. A copy was kept as {Path.GetFileName(copy)}.");
            }

            settings.Sanitize();
            return new SettingsLoadResult(settings, null);
        }
        catch (JsonException ex)
        {
            var kept = KeepCopy("corrupt");
            return new SettingsLoadResult(new AppSettings(),
                $"The settings file was damaged ({ex.Message}). Default settings are used; the old file was kept as {Path.GetFileName(kept)}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SettingsLoadResult(new AppSettings(),
                $"The settings file could not be read ({ex.Message}). Default settings are used for now.");
        }
    }

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var copy = settings.Clone();
        copy.Sanitize();
        return AtomicFile.WriteAllTextAsync(_path, MacroJson.Serialize(copy), cancellationToken);
    }

    private string KeepCopy(string label)
    {
        var directory = Path.GetDirectoryName(_path)!;
        var target = Path.Combine(directory, $"settings.{label}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        for (var n = 2; File.Exists(target); n++)
            target = Path.Combine(directory, $"settings.{label}-{DateTime.Now:yyyyMMdd-HHmmss}-{n}.json");

        try
        {
            File.Copy(_path, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Couldn't keep a copy; the original is still on disk until the next save.
        }

        return target;
    }
}
