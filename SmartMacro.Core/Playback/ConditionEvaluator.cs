using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Vision;

namespace SmartMacro.Core.Playback;

/// <summary>
/// Result of checking a condition once.
/// Seen: the text / image / colour was found (before "NOT" is applied). IsTrue: the condition's answer.
/// X / Y: where it was seen (screen pixels), only meaningful when Seen. Details: a sentence for the log.
/// Match: the rectangle of what was seen (empty when unknown). ElapsedMs: how long the look took (real time, capture +
/// search), for the live vision telemetry.
/// </summary>
public sealed record ConditionCheck(bool Seen, bool IsTrue, int X, int Y, string Details, ScreenRect Match = default, double Confidence = 0,
    double ElapsedMs = 0);

/// <summary>
/// Answers a condition by looking at the screen or variable store once. Waiting, timeouts, pause and stop are handled by the
/// playback engine around it (the same way as for Find steps), so this stays a small, testable piece.
/// </summary>
public interface IConditionEvaluator
{
    /// <summary>
    /// Looks once. Throws when the condition can't be checked at all (no OCR / screen capture, damaged image),
    /// so the macro stops with a clear error instead of silently taking a branch.
    /// </summary>
    Task<ConditionCheck> CheckAsync(Condition condition, DetectionTimer? timer, CancellationToken cancellationToken);

    /// <summary>A timer for measuring a series of checks of this condition (null when it isn't timed, e.g. OCR or variable).</summary>
    DetectionTimer? NewTimer(Condition condition);

    /// <summary>Short text for messages, e.g. "The image is on screen" / "\"OK\" is not on screen" / "{gold} >= 500".</summary>
    string Describe(Condition condition);
}

/// <summary>
/// The real condition evaluator: uses <see cref="ScreenProbes"/> (for text / image / colour) and <see cref="VariableStore"/>
/// (for variable comparisons). Prepared searches (decoded templates…) are cached per condition for one run, so a
/// condition checked in a loop doesn't decode its picture again every turn.
/// </summary>
public sealed class ScreenConditionEvaluator : IConditionEvaluator
{
    private readonly ScreenProbes _probes;
    private readonly VariableStore? _variables;
    private readonly Dictionary<Condition, PreparedSearch> _prepared = new(ReferenceEqualityComparer.Instance);

    public ScreenConditionEvaluator(ScreenProbes probes, VariableStore? variables = null)
    {
        _probes = probes ?? throw new ArgumentNullException(nameof(probes));
        _variables = variables;
    }

    public async Task<ConditionCheck> CheckAsync(Condition condition, DetectionTimer? timer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(condition);
        if (condition.Kind == ConditionKind.VariableCompare && condition.Comparison is { } comp)
        {
            var store = _variables ?? new VariableStore();
            var matched = store.EvaluateComparison(comp, condition.Negate, out var varDetails);
            return new ConditionCheck(Seen: false, IsTrue: matched, X: 0, Y: 0, Details: varDetails);
        }

        var search = Prepare(condition);
        var probe = await search.LookAsync(timer, cancellationToken).ConfigureAwait(false);
        var isTrue = probe.Found != condition.Negate;
        var details = probe.Found ? probe.FoundText : $"{search.What} is not on screen.{probe.MissText}";
        return new ConditionCheck(probe.Found, isTrue, probe.X, probe.Y, details, probe.Match, probe.Found ? probe.Confidence : 0, probe.ElapsedMs);
    }

    public DetectionTimer? NewTimer(Condition condition) =>
        condition.Kind == ConditionKind.VariableCompare ? null : Prepare(condition).NewTimer();

    public string Describe(Condition condition)
    {
        if (condition.Kind == ConditionKind.VariableCompare && condition.Comparison is { } comp)
            return StepDescriber.DescribeCondition(condition);

        var what = condition.Kind switch
        {
            ConditionKind.TextVisible => $"\"{(_variables is not null && condition.Check is FindTextStep ft ? _variables.Interpolate(ft.Text) : (condition.Check as FindTextStep)?.Text)}\"",
            ConditionKind.ImageVisible => "the image",
            ConditionKind.ColorVisible => $"color {(condition.Check as FindColorStep)?.Color}",
            _ => "(missing condition)",
        };
        return condition.Negate ? $"{what} is not on screen" : $"{what} is on screen";
    }

    private PreparedSearch Prepare(Condition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        if (_prepared.TryGetValue(condition, out var cached)) return cached;

        var search = condition.Search
                     ?? throw new InvalidOperationException("This condition has no valid search settings. The macro may be damaged.");
        var prepared = _probes.Prepare(search);
        _prepared[condition] = prepared;
        return prepared;
    }
}
