using System.Text.Json.Serialization;

namespace SmartMacro.Core.Models;

/// <summary>
/// Unified per-step failure / timeout behavior (v1.2 Resilience). The first three choices match
/// <see cref="TextNotFoundBehavior"/> losslessly; the last three add retries, recovery sub-macros, and checkpoints.
/// </summary>
public enum StepFailureAction
{
    StopMacro,
    Continue,
    SkipNextStep,
    Retry,
    RunRecoveryMacro,
    RestartFromCheckpoint,
}

/// <summary>
/// Extended failure recovery settings stored on a search / wait / focus step when <see cref="Action"/> uses a v1.2
/// resilience policy (<see cref="StepFailureAction.Retry"/>, <see cref="StepFailureAction.RunRecoveryMacro"/>, or
/// <see cref="StepFailureAction.RestartFromCheckpoint"/>).
/// </summary>
public sealed class StepFailurePolicy
{
    public const int MaxRetryCount = 20;
    public const int MaxRetryDelayMs = 60_000;

    public StepFailureAction Action { get; set; } = StepFailureAction.Retry;

    /// <summary>How many extra attempts to make when <see cref="Action"/> is <see cref="StepFailureAction.Retry"/> (1–20).</summary>
    public int RetryCount { get; set; } = 2;

    /// <summary>Wait before each retry attempt in milliseconds (0–60,000).</summary>
    public int RetryDelayMs { get; set; } = 500;

    /// <summary>Sub-macro to run when <see cref="Action"/> is <see cref="StepFailureAction.RunRecoveryMacro"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? RecoveryMacroId { get; set; }

    /// <summary>Fallback sub-macro name if <see cref="RecoveryMacroId"/> was re-created.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RecoveryMacroName { get; set; }

    /// <summary>Checkpoint name to jump to when <see cref="Action"/> is <see cref="StepFailureAction.RestartFromCheckpoint"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CheckpointName { get; set; }

    [JsonIgnore]
    public bool IsAdvanced => Action is StepFailureAction.Retry
        or StepFailureAction.RunRecoveryMacro
        or StepFailureAction.RestartFromCheckpoint;

    public StepFailurePolicy Clone() => (StepFailurePolicy)MemberwiseClone();

    public static StepFailureAction ResolveEffective(TextNotFoundBehavior legacy, StepFailurePolicy? policy) =>
        policy is { IsAdvanced: true }
            ? policy.Action
            : legacy switch
            {
                TextNotFoundBehavior.Continue => StepFailureAction.Continue,
                TextNotFoundBehavior.SkipNextStep => StepFailureAction.SkipNextStep,
                _ => StepFailureAction.StopMacro,
            };

    public static (TextNotFoundBehavior Legacy, StepFailurePolicy? Policy) Apply(
        StepFailureAction action,
        StepFailurePolicy? current)
    {
        switch (action)
        {
            case StepFailureAction.StopMacro:
                return (TextNotFoundBehavior.StopMacro, null);
            case StepFailureAction.Continue:
                return (TextNotFoundBehavior.Continue, null);
            case StepFailureAction.SkipNextStep:
                return (TextNotFoundBehavior.SkipNextStep, null);
            default:
            {
                var next = current?.Clone() ?? new StepFailurePolicy();
                next.Action = action;
                if (action == StepFailureAction.Retry && next.RetryCount <= 0) next.RetryCount = 2;
                return (TextNotFoundBehavior.StopMacro, next);
            }
        }
    }
}

/// <summary>
/// Named top-level restart marker ("Checkpoint"). Only allowed outside If / Repeat blocks (depth 0) so jumping back to a
/// checkpoint always leaves block and loop state completely clean.
/// </summary>
public sealed class CheckpointStep : MacroStep
{
    public const int MaxNameLength = 64;

    public string Name { get; set; } = "Main";

    [JsonIgnore]
    public override StepKind Kind => StepKind.Checkpoint;

    public static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Trim().Length <= MaxNameLength
        && !name.Any(char.IsControl);
}

/// <summary>
/// Maps a variable value across a <see cref="CallMacroStep"/> boundary.
/// - For <see cref="CallMacroStep.Inputs"/>: <see cref="TargetVariable"/> is the sub-macro's variable name, and
///   <see cref="SourceExpression"/> is a literal or <c>{callerVar}</c> placeholder evaluated in the caller.
/// - For <see cref="CallMacroStep.Outputs"/>: <see cref="TargetVariable"/> is the caller's variable name, and
///   <see cref="SourceExpression"/> is the sub-macro's variable name to read back when it finishes.
/// </summary>
public sealed class VariableBinding
{
    public string TargetVariable { get; set; } = string.Empty;

    public string SourceExpression { get; set; } = string.Empty;

    public VariableBinding Clone() => new()
    {
        TargetVariable = TargetVariable,
        SourceExpression = SourceExpression,
    };
}

/// <summary>
/// Calls another macro in the library as a reusable building block ("Login", "Sell inventory", "Return to town"), passing
/// optional input variables and copying optional output variables back when finished.
/// </summary>
public sealed class CallMacroStep : MacroStep
{
    public const int MaxCallDepth = 8;

    public Guid TargetMacroId { get; set; }

    public string TargetMacroName { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<VariableBinding>? Inputs { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<VariableBinding>? Outputs { get; set; }

    [JsonIgnore]
    public override StepKind Kind => StepKind.CallMacro;
}

/// <summary>What a macro-level <see cref="MacroWatcher"/> does when its condition triggers between steps.</summary>
public enum WatcherActionKind
{
    /// <summary>Click the spot where the watched image / text / color appeared, then resume the macro.</summary>
    ClickFoundSpot,

    /// <summary>Run a recovery sub-macro (e.g. "Reconnect"), then resume the macro.</summary>
    RunSubMacro,

    /// <summary>Jump back to a named top-level <see cref="CheckpointStep"/>.</summary>
    RestartFromCheckpoint,

    /// <summary>Stop the macro cleanly with a log message.</summary>
    StopMacro,
}

/// <summary>
/// Macro-level interrupt handler ("Watcher"): checked periodically between steps to handle surprise pop-ups, disconnect
/// banners, ads, or level-up dialogs automatically.
/// </summary>
public sealed class MacroWatcher
{
    public const int MinCheckIntervalMs = 200;
    public const int MaxCheckIntervalMs = 60_000;
    public const int MaxCooldownMs = 300_000;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "Popup handler";

    public bool Enabled { get; set; } = true;

    public Condition Condition { get; set; } = Condition.Create(ConditionKind.TextVisible);

    public WatcherActionKind Action { get; set; } = WatcherActionKind.ClickFoundSpot;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? HandlerMacroId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HandlerMacroName { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CheckpointName { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ClickOffsetX { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ClickOffsetY { get; set; }

    /// <summary>Minimum interval between checks of this watcher (ms). Default 1000 ms.</summary>
    public int CheckIntervalMs { get; set; } = 1_000;

    /// <summary>Minimum pause before this same watcher may fire again (ms). Default 2000 ms.</summary>
    public int CooldownMs { get; set; } = 2_000;
}
