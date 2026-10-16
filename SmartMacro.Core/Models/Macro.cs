namespace SmartMacro.Core.Models;

/// <summary>A saved macro: a name plus an ordered list of steps.</summary>
public sealed class Macro
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ModifiedAt { get; set; }
    public List<MacroStep> Steps { get; set; } = new();

    /// <summary>How many times playback runs the steps. 0 = repeat until stopped.</summary>
    public int RepeatCount { get; set; } = 1;

    /// <summary>Playback speed multiplier for delays (2 = twice as fast). 0.1 to 10.</summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>Pause between repeats, in ms (not after the last one). Also scaled by Speed.</summary>
    public int RepeatDelayMs { get; set; }
}

/// <summary>
/// What actually gets written to disk. The schema version lets future versions
/// migrate old files and lets old versions refuse files they don't understand.
/// </summary>
public sealed class MacroDocument
{
    /// <summary>
    /// Newest format this version understands.
    /// 1 = original format. 2 = adds hold times on Press / Click steps and "Recorded input (game mode)" steps.
    /// 3 = adds smart recordings: recorded input with cursor positions ("u1" event text) and mouse segments.
    /// </summary>
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; set; }
    public Macro? Macro { get; set; }

    /// <summary>
    /// The oldest format that can hold this macro without losing anything. Macros that don't use the
    /// version 2 / 3 features are still written as the older version, so older copies of SmartMacro can keep
    /// opening them. Older files need no migration: the new fields simply default to 0 / off / null, which is
    /// exactly the old behavior (game-mode recordings stay relative-only).
    /// </summary>
    public static int RequiredSchemaVersion(Macro macro)
    {
        ArgumentNullException.ThrowIfNull(macro);
        if (macro.Steps is null) return 1;

        var required = 1;
        foreach (var step in macro.Steps)
        {
            switch (step)
            {
                case InputSequenceStep q when UsesSmartRecordingFeatures(q):
                    return 3;
                case InputSequenceStep:
                case KeyPressStep { HoldMs: not 0 }:
                case MouseClickStep { HoldMs: not 0 }:
                    required = 2;
                    break;
            }
        }

        return required;
    }

    private static bool UsesSmartRecordingFeatures(InputSequenceStep step)
    {
        if (step.MouseSegments is not null) return true;
        if (step.Events is null) return false;
        foreach (var e in step.Events)
        {
            if (e.HasPosition && e.IsMouse) return true;
        }

        return false;
    }
}
