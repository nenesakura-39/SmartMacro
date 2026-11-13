using System.Globalization;
using System.Text.Json.Serialization;
using SmartMacro.Core.Models;

namespace SmartMacro.Core.Playback;

/// <summary>Stepping mode requested by the interactive debugger while paused.</summary>
public enum DebugStepMode
{
    None = 0,
    StepOver = 1,
    StepInto = 2,
    StepOut = 3,
}

/// <summary>Outcome recorded for a single step in the Execution Trace ring buffer.</summary>
public enum ExecutionTraceOutcome
{
    Executed = 0,
    ConditionTrue = 1,
    ConditionFalse = 2,
    Skipped = 3,
    Paused = 4,
    Failed = 5,
    Stopped = 6,
}

/// <summary>Snapshot of one active call-stack frame (root macro or called sub-macro).</summary>
public sealed record CallStackFrame(
    Guid MacroId,
    string MacroName,
    int StepNumber,
    int StepCount,
    int StepIndex,
    Guid? StepId,
    string StepTitle,
    IReadOnlyList<VariableSnapshot> VariablesSnapshot,
    int Depth,
    bool IsSubRoutine)
{
    [JsonIgnore]
    public string FrameLabel => IsSubRoutine
        ? $"#{Depth} {MacroName} · Step {StepNumber}/{Math.Max(1, StepCount)}"
        : $"Root {MacroName} · Step {StepNumber}/{Math.Max(1, StepCount)}";

    [JsonIgnore]
    public string DetailText => string.IsNullOrWhiteSpace(StepTitle)
        ? FrameLabel
        : $"{FrameLabel} — {StepTitle}";
}

/// <summary>One structured step record captured in the Debugger 2.0 Execution Trace ring buffer.</summary>
public sealed record ExecutionTraceEntry(
    long SequenceNumber,
    DateTimeOffset Timestamp,
    TimeSpan ActiveElapsed,
    Guid MacroId,
    string MacroName,
    int Depth,
    Guid StepId,
    int StepNumber,
    string StepTitle,
    double DurationMs,
    ExecutionTraceOutcome Outcome,
    double? VisionScore = null,
    string? Error = null,
    string? Detail = null)
{
    [JsonIgnore]
    public string OutcomeText => Outcome switch
    {
        ExecutionTraceOutcome.Executed => "OK",
        ExecutionTraceOutcome.ConditionTrue => "TRUE",
        ExecutionTraceOutcome.ConditionFalse => "FALSE",
        ExecutionTraceOutcome.Skipped => "SKIP",
        ExecutionTraceOutcome.Paused => "PAUSED",
        ExecutionTraceOutcome.Failed => "FAIL",
        ExecutionTraceOutcome.Stopped => "STOP",
        _ => Outcome.ToString(),
    };

    [JsonIgnore]
    public string ScopePrefix => Depth > 0 ? $"[d{Depth} {MacroName}] " : string.Empty;

    [JsonIgnore]
    public string SummaryLine
    {
        get
        {
            var score = VisionScore is { } vs ? $" · {vs * 100:0.#}% vision" : string.Empty;
            var dur = DurationMs >= 1000 ? $"{DurationMs / 1000.0:0.##}s" : $"{DurationMs:0.#}ms";
            var extra = !string.IsNullOrWhiteSpace(Error)
                ? $" · {Error}"
                : (!string.IsNullOrWhiteSpace(Detail) ? $" · {Detail}" : string.Empty);
            return $"#{SequenceNumber} {Timestamp.ToLocalTime():HH:mm:ss.fff} [{OutcomeText}] {ScopePrefix}Step {StepNumber}: {StepTitle} ({dur}{score}){extra}";
        }
    }
}

/// <summary>
/// Runtime breakpoint rule attached to a step ID. When <see cref="Condition"/>, <see cref="Comparison"/>, and
/// <see cref="Expression"/> are all unset, playback pauses unconditionally whenever the step is reached.
/// </summary>
public sealed class BreakpointRule
{
    public Guid StepId { get; init; }
    public Condition? Condition { get; init; }
    public VariableComparison? Comparison { get; init; }
    public string? Expression { get; init; }

    [JsonIgnore]
    public bool HasCondition => Condition is not null || Comparison is not null || !string.IsNullOrWhiteSpace(Expression);

    public string Describe()
    {
        if (!string.IsNullOrWhiteSpace(Expression)) return Expression.Trim();
        if (Comparison is { } cmp)
            return $"{{{cmp.VariableName}}} {VariableStore.DescribeOperator(cmp.Operator)} {cmp.RightValue}";
        if (Condition is not null)
            return Condition.Kind == ConditionKind.VariableCompare && Condition.Comparison is { } c
                ? $"{{{c.VariableName}}} {VariableStore.DescribeOperator(c.Operator)} {c.RightValue}"
                : Condition.Kind?.ToString() ?? nameof(Condition);
        return string.Empty;
    }
}

/// <summary>
/// Watches a named macro variable and pauses playback when its value changes (optionally filtered by a condition).
/// </summary>
public sealed class VariableWatchpoint
{
    public string VariableName { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public VariableComparison? Condition { get; set; }
    public string? Expression { get; set; }

    public string Describe()
    {
        var name = string.IsNullOrWhiteSpace(VariableName) ? "(unset)" : $"{{{VariableName.Trim()}}}";
        if (!string.IsNullOrWhiteSpace(Expression)) return $"{name} when {Expression.Trim()}";
        if (Condition is { } c)
            return $"{name} when {{{c.VariableName}}} {VariableStore.DescribeOperator(c.Operator)} {c.RightValue}";
        return $"{name} (on change)";
    }
}

/// <summary>JSON payload saved alongside <c>last-failure-snapshot.png</c> when playback stops on failure.</summary>
public sealed record ExecutionTraceDocument(
    string MacroName,
    PlaybackOutcome Outcome,
    string? Error,
    DateTimeOffset SavedAtUtc,
    string? FailureSnapshotPath,
    IReadOnlyList<CallStackFrame> CallStack,
    IReadOnlyList<ExecutionTraceEntry> Entries);

/// <summary>
/// Parses and evaluates Debugger 2.0 conditional breakpoint and watchpoint expressions such as
/// <c>{hp} &lt; 20</c> or <c>VisionScore &lt; 0.85</c>.
/// </summary>
public static class DebugConditionEvaluator
{
    private static readonly (string Token, VariableCompareOperator Op)[] Operators =
    {
        (">=", VariableCompareOperator.GreaterOrEqual),
        ("<=", VariableCompareOperator.LessOrEqual),
        ("!=", VariableCompareOperator.NotEquals),
        ("==", VariableCompareOperator.Equals),
        ("=", VariableCompareOperator.Equals),
        (">", VariableCompareOperator.GreaterThan),
        ("<", VariableCompareOperator.LessThan),
        (" contains ", VariableCompareOperator.Contains),
        (" starts with ", VariableCompareOperator.StartsWith),
        (" startswith ", VariableCompareOperator.StartsWith),
    };

    /// <summary>
    /// Parses compact expressions like <c>{hp} &lt; 20</c>, <c>VisionScore &lt; 0.85</c>, or <c>!{status} == OK</c>.
    /// </summary>
    public static bool TryParseExpression(
        string? expression,
        out VariableComparison? comparison,
        out bool negate,
        out string? error)
    {
        comparison = null;
        negate = false;
        error = null;

        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "Expression cannot be empty.";
            return false;
        }

        var text = expression.Trim();
        if (text.StartsWith('!'))
        {
            negate = true;
            text = text[1..].TrimStart();
        }
        else if (text.StartsWith("not ", StringComparison.OrdinalIgnoreCase))
        {
            negate = true;
            text = text[4..].TrimStart();
        }

        var bestIndex = -1;
        var matchedToken = string.Empty;
        var matchedOp = VariableCompareOperator.Equals;

        foreach (var (token, op) in Operators)
        {
            var idx = text.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (idx > 0 && (bestIndex < 0 || idx < bestIndex))
            {
                bestIndex = idx;
                matchedToken = token;
                matchedOp = op;
            }
        }

        if (bestIndex < 0)
        {
            error = "Use a comparison like '{hp} < 20', 'VisionScore < 0.85', or 'status == Ready'.";
            return false;
        }

        var left = text[..bestIndex].Trim();
        if (left.StartsWith('{') && left.EndsWith('}') && left.Length >= 3)
            left = left[1..^1].Trim();

        if (!IsValidTargetName(left))
        {
            error = "Variable name before the operator must be a valid variable or 'VisionScore'.";
            return false;
        }

        var right = text[(bestIndex + matchedToken.Length)..].Trim();
        if (right.Length >= 2 && ((right[0] == '"' && right[^1] == '"') || (right[0] == '\'' && right[^1] == '\'')))
            right = right[1..^1];

        comparison = new VariableComparison
        {
            VariableName = left,
            Operator = matchedOp,
            RightValue = right,
        };
        return true;
    }

    public static bool EvaluateComparisonWithVision(
        VariableComparison comparison,
        bool negate,
        VariableStore variables,
        double? lastVisionScore)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(variables);

        var name = (comparison.VariableName ?? string.Empty).Trim();
        if (string.Equals(name, "VisionScore", StringComparison.OrdinalIgnoreCase))
        {
            if (lastVisionScore is not { } score) return false;
            var rightResolved = variables.Interpolate(comparison.RightValue ?? string.Empty);
            if (!VariableStore.TryParseScreenNumber(rightResolved, out var target))
                return false;

            var matched = comparison.Operator switch
            {
                VariableCompareOperator.Equals => Math.Abs(score - target) < 1e-6,
                VariableCompareOperator.NotEquals => Math.Abs(score - target) >= 1e-6,
                VariableCompareOperator.GreaterThan => score > target,
                VariableCompareOperator.GreaterOrEqual => score >= target,
                VariableCompareOperator.LessThan => score < target,
                VariableCompareOperator.LessOrEqual => score <= target,
                VariableCompareOperator.Contains => score.ToString("0.####", CultureInfo.InvariantCulture)
                    .Contains(rightResolved, StringComparison.OrdinalIgnoreCase),
                VariableCompareOperator.StartsWith => score.ToString("0.####", CultureInfo.InvariantCulture)
                    .StartsWith(rightResolved, StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
            return negate ? !matched : matched;
        }

        return variables.EvaluateComparison(comparison, negate, out _);
    }

    public static async Task<(bool Hit, string? Reason)> EvaluateBreakpointRuleAsync(
        BreakpointRule rule,
        VariableStore variables,
        IConditionEvaluator conditions,
        double? lastVisionScore,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (!rule.HasCondition)
            return (true, null);

        if (rule.Condition is { } cond)
        {
            if (cond.Kind == ConditionKind.VariableCompare && !cond.HasSecondCondition && cond.Comparison is { } condCmp
                && string.Equals(condCmp.VariableName?.Trim(), "VisionScore", StringComparison.OrdinalIgnoreCase))
            {
                var visionMatch = EvaluateComparisonWithVision(condCmp, cond.Negate, variables, lastVisionScore);
                return (visionMatch, visionMatch ? rule.Describe() : null);
            }

            var check = await conditions.CheckAsync(cond, null, token).ConfigureAwait(false);
            return (check.IsTrue, check.IsTrue ? rule.Describe() : null);
        }

        if (rule.Comparison is { } comparison)
        {
            var matched = EvaluateComparisonWithVision(comparison, false, variables, lastVisionScore);
            return (matched, matched ? rule.Describe() : null);
        }

        if (!string.IsNullOrWhiteSpace(rule.Expression)
            && TryParseExpression(rule.Expression, out var parsed, out var negate, out _)
            && parsed is not null)
        {
            var matched = EvaluateComparisonWithVision(parsed, negate, variables, lastVisionScore);
            return (matched, matched ? rule.Expression.Trim() : null);
        }

        return (false, null);
    }

    public static bool EvaluateWatchpointCondition(
        VariableWatchpoint watchpoint,
        VariableStore variables,
        double? lastVisionScore)
    {
        if (watchpoint.Condition is { } cmp)
            return EvaluateComparisonWithVision(cmp, false, variables, lastVisionScore);

        if (!string.IsNullOrWhiteSpace(watchpoint.Expression))
        {
            if (!TryParseExpression(watchpoint.Expression, out var parsed, out var negate, out _) || parsed is null)
                return false;
            return EvaluateComparisonWithVision(parsed, negate, variables, lastVisionScore);
        }

        return true;
    }

    private static bool IsValidTargetName(string name) =>
        string.Equals(name, "VisionScore", StringComparison.OrdinalIgnoreCase)
        || MacroVariableDefinition.IsValidName(name);
}
