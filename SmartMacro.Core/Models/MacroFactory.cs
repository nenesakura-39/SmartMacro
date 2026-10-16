using SmartMacro.Core.Storage;
using SmartMacro.Core.Validation;

namespace SmartMacro.Core.Models;

/// <summary>Helpers for creating macros and steps with sensible defaults.</summary>
public static class MacroFactory
{
    public const string DefaultMacroName = "New Macro";

    public static Macro CreateNew(IEnumerable<string> existingNames, string baseName = DefaultMacroName)
    {
        var now = DateTimeOffset.Now;
        return new Macro
        {
            Id = Guid.NewGuid(),
            Name = MakeUniqueName(baseName, existingNames),
            CreatedAt = now,
            ModifiedAt = now,
        };
    }

    /// <summary>"Name", "Name (2)", "Name (3)"... compared case-insensitively. Respects the max name length.</summary>
    public static string MakeUniqueName(string baseName, IEnumerable<string> existingNames)
    {
        baseName = string.IsNullOrWhiteSpace(baseName) ? DefaultMacroName : baseName.Trim();
        var taken = new HashSet<string>(existingNames.Where(n => n is not null).Select(n => n.Trim()), StringComparer.CurrentCultureIgnoreCase);

        var candidate = Truncate(baseName, MacroValidator.MaxNameLength);
        for (var n = 2; taken.Contains(candidate); n++)
        {
            var suffix = $" ({n})";
            candidate = Truncate(baseName, MacroValidator.MaxNameLength - suffix.Length) + suffix;
        }

        return candidate;
    }

    /// <summary>Deep copy with new macro and step IDs.</summary>
    public static Macro Duplicate(Macro source, IEnumerable<string> existingNames)
    {
        var copy = MacroJson.Clone(source);
        copy.Id = Guid.NewGuid();
        copy.Name = MakeUniqueName($"{source.Name} (copy)", existingNames);
        var now = DateTimeOffset.Now;
        copy.CreatedAt = now;
        copy.ModifiedAt = now;
        foreach (var step in copy.Steps)
            step.Id = Guid.NewGuid();
        return copy;
    }

    public static MacroStep CreateStep(StepKind kind, int defaultDelayMs = 500) => kind switch
    {
        StepKind.Delay => new DelayStep { DurationMs = Math.Clamp(defaultDelayMs, 0, MacroValidator.MaxDelayMs) },
        StepKind.MouseMove => new MouseMoveStep(),
        StepKind.MouseClick => new MouseClickStep(),
        StepKind.MouseScroll => new MouseScrollStep(),
        StepKind.KeyPress => new KeyPressStep(),
        StepKind.TypeText => new TypeTextStep { Text = "Hello" },
        StepKind.FindText => new FindTextStep { Text = "OK" },
        StepKind.FindImage => new FindImageStep(),
        StepKind.FindColor => new FindColorStep(),
        StepKind.If => new IfStep(),
        StepKind.Else => new ElseStep(),
        StepKind.EndIf => new EndIfStep(),
        StepKind.Repeat => new RepeatStep(),
        StepKind.EndRepeat => new EndRepeatStep(),
        StepKind.FeedbackLoop => new FeedbackLoopStep(),
        StepKind.InputSequence => throw new ArgumentException("Recorded input steps are created by recording in game mode, not added by hand.", nameof(kind)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown step type."),
    };

    /// <summary>Copy of a step with a fresh ID.</summary>
    public static MacroStep DuplicateStep(MacroStep step)
    {
        var copy = MacroJson.CloneStep(step);
        copy.Id = Guid.NewGuid();
        return copy;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..Math.Max(1, maxLength)].TrimEnd();
}
