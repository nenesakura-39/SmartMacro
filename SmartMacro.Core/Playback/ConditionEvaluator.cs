using SmartMacro.Core.Models;
using SmartMacro.Core.Vision;

namespace SmartMacro.Core.Playback;

/// <summary>
/// Result of checking a condition once.
/// Seen: the text / image / colour was found (before "NOT" is applied). IsTrue: the condition's answer.
/// X / Y: where it was seen (screen pixels), only meaningful when Seen. Details: a sentence for the log.
/// </summary>
public sealed record ConditionCheck(bool Seen, bool IsTrue, int X, int Y, string Details);

/// <summary>
/// Answers a condition by looking at the screen once. Waiting, timeouts, pause and stop are handled by the
/// playback engine around it (the same way as for Find steps), so this stays a small, testable piece.
/// </summary>
public interface IConditionEvaluator
{
    /// <summary>
    /// Looks once. Throws when the condition can't be checked at all (no OCR / screen capture, damaged image),
    /// so the macro stops with a clear error instead of silently taking a branch.
    /// </summary>
    Task<ConditionCheck> CheckAsync(Condition condition, DetectionTimer? timer, CancellationToken cancellationToken);

    /// <summary>A timer for measuring a series of checks of this condition (null when it isn't timed, e.g. OCR).</summary>
    DetectionTimer? NewTimer(Condition condition);

    /// <summary>Short text for messages, e.g. "The image is on screen" / "\"OK\" is not on screen".</summary>
    string Describe(Condition condition);
}

/// <summary>
/// The real condition evaluator: uses <see cref="ScreenProbes"/>, the same detection code as the Find text /
/// image / colour steps. Prepared searches (decoded templates…) are cached per condition for one run, so a
/// condition checked in a loop doesn't decode its picture again every turn.
/// </summary>
public sealed class ScreenConditionEvaluator : IConditionEvaluator
{
    private readonly ScreenProbes _probes;
    private readonly Dictionary<Condition, PreparedSearch> _prepared = new(ReferenceEqualityComparer.Instance);

    public ScreenConditionEvaluator(ScreenProbes probes)
    {
        _probes = probes ?? throw new ArgumentNullException(nameof(probes));
    }

    public async Task<ConditionCheck> CheckAsync(Condition condition, DetectionTimer? timer, CancellationToken cancellationToken)
    {
        var search = Prepare(condition);
        var probe = await search.LookAsync(timer, cancellationToken).ConfigureAwait(false);
        var isTrue = probe.Found != condition.Negate;
        var details = probe.Found ? probe.FoundText : $"{search.What} is not on screen.{probe.MissText}";
        return new ConditionCheck(probe.Found, isTrue, probe.X, probe.Y, details);
    }

    public DetectionTimer? NewTimer(Condition condition) => Prepare(condition).NewTimer();

    public string Describe(Condition condition)
    {
        var what = condition.Kind switch
        {
            ConditionKind.TextVisible => $"\"{(condition.Check as FindTextStep)?.Text}\"",
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
