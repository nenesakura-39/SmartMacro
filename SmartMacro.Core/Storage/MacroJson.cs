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

    /// <summary>Deep copy by round-tripping through JSON (keeps the same IDs).</summary>
    public static Macro Clone(Macro macro) =>
        Deserialize<Macro>(Serialize(macro)) ?? throw new InvalidOperationException("Failed to copy the macro.");

    /// <summary>Deep copy of one step (keeps the same ID).</summary>
    public static MacroStep CloneStep(MacroStep step) =>
        Deserialize<MacroStep>(Serialize<MacroStep>(step)) ?? throw new InvalidOperationException("Failed to copy the step.");
}
