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
            RenewIds(step);
        return copy;
    }

    /// <summary>
    /// Gives a step a brand-new ID, and also every search step nested inside it (the Find image / colour / text "Check"
    /// of an If / Repeat condition and of a Feedback loop's Done / Failed conditions). Those nested searches have IDs of
    /// their own, and the playback engine's Hot-Spot memory remembers "where did this search find its picture" by that
    /// ID: a copied If that kept its condition's ID would share (and overwrite) the original If's remembered spot.
    /// </summary>
    public static void RenewIds(MacroStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        step.Id = Guid.NewGuid();
        foreach (var condition in WorkflowSteps.ConditionsOf(step))
        {
            if (condition.Check is { } check) check.Id = Guid.NewGuid();
        }
    }

    /// <summary>
    /// A new step as the editor's toolbox adds it. New steps get the beginner-friendly defaults: Find steps and conditions
    /// look at the main monitor only, and steps that move the mouse glide there smoothly (120 ms) and click with a 35 ms
    /// hold, a 65 ms double-click gap and a 120 ms wait afterwards. (Steps made with <c>new</c>, older files and
    /// recordings keep the old behaviour: whole screen, instant jumps, recorded timing.)
    /// </summary>
    public static MacroStep CreateStep(StepKind kind, int defaultDelayMs = 500) => kind switch
    {
        StepKind.Delay => new DelayStep { DurationMs = Math.Clamp(defaultDelayMs, 0, MacroValidator.MaxDelayMs) },
        StepKind.MouseMove => new MouseMoveStep { MoveStyle = MouseMoveStyle.SmoothGlide },
        StepKind.MouseClick => new MouseClickStep
        {
            MoveStyle = MouseMoveStyle.SmoothGlide,
            HoldMs = MouseMotionDefaults.ClickHoldMs,
            DoubleClickIntervalMs = MouseMotionDefaults.DoubleClickIntervalMs,
            PostActionDelayMs = MouseMotionDefaults.PostActionDelayMs,
        },
        StepKind.MouseScroll => new MouseScrollStep(),
        StepKind.KeyPress => new KeyPressStep(),
        StepKind.TypeText => new TypeTextStep { Text = "Hello", CharacterDelayMs = TypeTextStep.DefaultCharacterDelayMs },
        StepKind.FindText => WithNewSearchDefaults(new FindTextStep { Text = "OK" }),
        StepKind.FindImage => WithNewSearchDefaults(new FindImageStep()),
        StepKind.FindColor => WithNewSearchDefaults(new FindColorStep()),
        StepKind.If => new IfStep { Condition = NewCondition(ConditionKind.TextVisible) },
        StepKind.Else => new ElseStep(),
        StepKind.EndIf => new EndIfStep(),
        StepKind.Repeat => new RepeatStep(),
        StepKind.EndRepeat => new EndRepeatStep(),
        StepKind.FeedbackLoop => new FeedbackLoopStep(),
        StepKind.InputSequence => throw new ArgumentException("Recorded input steps are created by recording in game mode, not added by hand.", nameof(kind)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown step type."),
    };

    /// <summary>
    /// A new condition for the editor (If / Repeat / Feedback loop): like <see cref="Condition.Create"/>, but it looks at
    /// the main monitor only (new steps' default).
    /// </summary>
    public static Condition NewCondition(ConditionKind kind)
    {
        var condition = Condition.Create(kind);
        if (condition.Search is { } search) search.Scope = Vision.SearchScope.PrimaryMonitor;
        return condition;
    }

    /// <summary>New Find steps: main monitor, smooth glide to what was found.</summary>
    public static T WithNewSearchDefaults<T>(T step) where T : ScreenSearchStep
    {
        step.Scope = Vision.SearchScope.PrimaryMonitor;
        step.MoveStyle = MouseMoveStyle.SmoothGlide;
        step.MoveDurationMs = MouseMotionDefaults.MoveDurationMs;
        return step;
    }

    /// <summary>Copy of a step with a fresh ID (its nested condition searches get fresh IDs too, see <see cref="RenewIds"/>).</summary>
    public static MacroStep DuplicateStep(MacroStep step)
    {
        var copy = MacroJson.CloneStep(step);
        RenewIds(copy);
        return copy;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..Math.Max(1, maxLength)].TrimEnd();
}
