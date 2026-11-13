using System.Text.Json;
using System.Text.Json.Serialization;
using SmartMacro.Core.Models;

namespace SmartMacro.Core.Storage;

/// <summary>Shared JSON settings so every part of the app reads/writes macros the same way.</summary>
public static class MacroJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            // Lets "type" appear anywhere in a step object, not only first (hand-edited files).
            AllowOutOfOrderMetadataProperties = true,
            MaxDepth = 32,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>Deep copy of a macro (keeps the same IDs) using structural cloning.</summary>
    public static Macro Clone(Macro macro)
    {
        ArgumentNullException.ThrowIfNull(macro);
        return macro.DeepClone();
    }

    /// <summary>Deep copy of one step (keeps the same ID) using structural cloning.</summary>
    public static MacroStep CloneStep(MacroStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return step.Clone();
    }

    /// <summary>Deep copy of one watcher (keeps the same ID) using structural cloning.</summary>
    public static MacroWatcher CloneWatcher(MacroWatcher watcher)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        return watcher.Clone();
    }
}
