using System.Collections.Concurrent;
using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;

namespace SmartMacro.Core.Playback;

/// <summary>
/// Vision Engine Turbo – "Hot-Spot" memory: where each image search last found its picture (screen pixels), so the
/// next look can first check that small spot instead of scanning the whole search area. One instance per playback
/// session (kept across poll iterations, loop turns and macro repeats; cleared when a new playback starts).
///
/// The key is the search step's ID plus a fingerprint of its picture, so two steps (or a step whose picture was
/// re-captured) never share a spot. Thread-safe.
/// </summary>
public sealed class HotSpotMemory
{
    /// <summary>Identifies one image search: its step ID and its picture (size + text hash).</summary>
    public readonly record struct Key(Guid StepId, int Width, int Height, int DataLength, int DataHash);

    private readonly ConcurrentDictionary<Key, ScreenRect> _spots = new();

    /// <summary>The key of a Find image step / image condition.</summary>
    public static Key KeyOf(FindImageStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        var data = step.TemplateData ?? string.Empty;
        return new Key(step.Id, step.TemplateWidth, step.TemplateHeight, data.Length, data.GetHashCode());
    }

    /// <summary>How many searches have a remembered spot.</summary>
    public int Count => _spots.Count;

    /// <summary>Where this search last found its picture.</summary>
    public bool TryGet(Key key, out ScreenRect lastMatch) => _spots.TryGetValue(key, out lastMatch);

    /// <summary>Remembers where this search just found its picture (empty rectangles are ignored).</summary>
    public void Remember(Key key, ScreenRect match)
    {
        if (match.IsEmpty) return;
        _spots[key] = match;
    }

    /// <summary>Forgets one search's spot.</summary>
    public void Forget(Key key) => _spots.TryRemove(key, out _);

    /// <summary>Forgets everything (a new playback session).</summary>
    public void Clear() => _spots.Clear();
}
