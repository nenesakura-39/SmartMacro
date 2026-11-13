using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using SmartMacro.Core.Models;

namespace SmartMacro.Core.Storage;

/// <summary>
/// Content-addressed deduplication store for repeated template and snapshot payloads inside <see cref="Macro.Assets"/>.
/// Dehydrates repeated inline Base64 payloads on save to keep <c>.smacro</c> files compact, and rehydrates/interns them
/// on load while maintaining full backward compatibility with legacy inline macros.
/// </summary>
public static class MacroAssetStore
{
    /// <summary>Minimum payload character length eligible for deduplication into <see cref="Macro.Assets"/>.</summary>
    public const int MinDeduplicatePayloadLength = 128;

    /// <summary>Computes a deterministic short SHA-256 content identifier (<c>sha256-&lt;24-hex&gt;</c>) for an inline payload.</summary>
    public static string ComputeAssetId(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var hash = ComputeSha256(payload);
        return FormatAssetId(hash, 12);
    }

    /// <summary>
    /// Returns an independent clone of <paramref name="source"/> whose repeated image payloads (appearing at least
    /// <paramref name="minOccurrences"/> times and at least <see cref="MinDeduplicatePayloadLength"/> characters long) are
    /// moved into <see cref="Macro.Assets"/> and replaced with <c>*AssetId</c> references. Single-occurrence payloads keep
    /// their inline fields unchanged.
    /// </summary>
    public static Macro PrepareForSerialization(Macro source, int minOccurrences = 2)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (minOccurrences < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minOccurrences), "Minimum occurrences must be at least 1.");
        }

        var copy = source.DeepClone();
        // Normalize any pre-existing asset references back onto inline properties before counting occurrences.
        Hydrate(copy, releaseAssetTable: false);
        copy.Assets = null;

        var slots = new List<ImageAssetSlot>();
        CollectImageSlots(copy, slots);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var slot in slots)
        {
            var payload = slot.GetInline();
            if (!string.IsNullOrEmpty(payload) && payload.Length >= MinDeduplicatePayloadLength)
            {
                counts.TryGetValue(payload, out var count);
                counts[payload] = count + 1;
            }
        }

        Dictionary<string, string>? assets = null;
        Dictionary<string, string>? payloadToId = null;

        foreach (var (payload, count) in counts)
        {
            if (count < minOccurrences)
            {
                continue;
            }

            assets ??= new Dictionary<string, string>(StringComparer.Ordinal);
            payloadToId ??= new Dictionary<string, string>(StringComparer.Ordinal);
            var assetId = AllocateAssetId(assets, payload);
            assets[assetId] = payload;
            payloadToId[payload] = assetId;
        }

        foreach (var slot in slots)
        {
            var payload = slot.GetInline();
            if (!string.IsNullOrEmpty(payload)
                && payloadToId is not null
                && payloadToId.TryGetValue(payload, out var assetId))
            {
                slot.SetAssetId(assetId);
                slot.ClearInline();
            }
            else
            {
                slot.SetAssetId(null);
            }
        }

        copy.Assets = assets is { Count: > 0 } ? assets : null;
        return copy;
    }

    /// <summary>
    /// Resolves <c>*AssetId</c> references from <see cref="Macro.Assets"/> back onto inline step/target properties and
    /// interns duplicate payloads onto shared managed <see cref="string"/> instances in RAM.
    /// </summary>
    public static void Hydrate(
        Macro macro,
        bool releaseAssetTable = true,
        Dictionary<string, string>? internPool = null)
    {
        ArgumentNullException.ThrowIfNull(macro);

        internPool ??= new Dictionary<string, string>(StringComparer.Ordinal);
        var slots = new List<ImageAssetSlot>();
        CollectImageSlots(macro, slots);

        var assets = macro.Assets;
        foreach (var slot in slots)
        {
            var inline = slot.GetInline();
            if (string.IsNullOrEmpty(inline))
            {
                var assetId = slot.GetAssetId();
                if (!string.IsNullOrWhiteSpace(assetId)
                    && assets is not null
                    && assets.TryGetValue(assetId, out var resolved)
                    && !string.IsNullOrEmpty(resolved))
                {
                    slot.SetInline(InternPayload(internPool, resolved));
                }
            }
            else
            {
                slot.SetInline(InternPayload(internPool, inline));
            }

            if (releaseAssetTable)
            {
                slot.SetAssetId(null);
            }
        }

        if (releaseAssetTable)
        {
            macro.Assets = null;
        }
    }

    private static string InternPayload(Dictionary<string, string> pool, string payload)
    {
        if (payload.Length == 0)
        {
            return string.Empty;
        }

        if (pool.TryGetValue(payload, out var canonical))
        {
            return canonical;
        }

        pool[payload] = payload;
        return payload;
    }

    private static byte[] ComputeSha256(string payload) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(payload));

    private static string FormatAssetId(byte[] hash, int prefixByteCount) =>
        "sha256-" + Convert.ToHexStringLower(hash.AsSpan(0, prefixByteCount));

    private static string AllocateAssetId(Dictionary<string, string> assets, string payload)
    {
        var hash = ComputeSha256(payload);
        var shortId = FormatAssetId(hash, 12);
        if (!assets.TryGetValue(shortId, out var existing) || string.Equals(existing, payload, StringComparison.Ordinal))
        {
            return shortId;
        }

        var fullId = FormatAssetId(hash, hash.Length);
        if (!assets.TryGetValue(fullId, out existing) || string.Equals(existing, payload, StringComparison.Ordinal))
        {
            return fullId;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{fullId}-{suffix}";
            if (!assets.TryGetValue(candidate, out existing) || string.Equals(existing, payload, StringComparison.Ordinal))
            {
                return candidate;
            }
        }
    }

    private static void CollectImageSlots(Macro macro, List<ImageAssetSlot> slots)
    {
        if (macro.Steps is not null)
        {
            foreach (var step in macro.Steps)
            {
                VisitStep(step, slots);
            }
        }

        if (macro.Watchers is not null)
        {
            foreach (var watcher in macro.Watchers)
            {
                if (watcher is not null)
                {
                    VisitCondition(watcher.Condition, slots);
                }
            }
        }
    }

    private static void VisitStep(MacroStep? step, List<ImageAssetSlot> slots)
    {
        switch (step)
        {
            case null:
                return;

            case FindImageStep findImage:
                slots.Add(new ImageAssetSlot(
                    () => findImage.TemplateData,
                    value => findImage.TemplateData = value,
                    () => findImage.TemplateData = string.Empty,
                    () => findImage.TemplateAssetId,
                    value => findImage.TemplateAssetId = value));
                if (findImage.AdditionalTemplates is not null)
                {
                    foreach (var variant in findImage.AdditionalTemplates)
                    {
                        if (variant is null)
                        {
                            continue;
                        }

                        var capturedVariant = variant;
                        slots.Add(new ImageAssetSlot(
                            () => capturedVariant.Data,
                            value => capturedVariant.Data = value,
                            () => capturedVariant.Data = string.Empty,
                            () => capturedVariant.TemplateAssetId,
                            value => capturedVariant.TemplateAssetId = value));
                    }
                }
                break;

            case MouseClickStep click:
                slots.Add(new ImageAssetSlot(
                    () => click.TargetThumbnailBase64,
                    value => click.TargetThumbnailBase64 = value,
                    () => click.TargetThumbnailBase64 = null,
                    () => click.SnapshotAssetId,
                    value => click.SnapshotAssetId = value));
                break;

            case InputSequenceStep sequence:
                if (sequence.ClickTargets is not null)
                {
                    foreach (var target in sequence.ClickTargets)
                    {
                        if (target is null)
                        {
                            continue;
                        }

                        var capturedTarget = target;
                        slots.Add(new ImageAssetSlot(
                            () => capturedTarget.TargetThumbnailBase64,
                            value => capturedTarget.TargetThumbnailBase64 = value,
                            () => capturedTarget.TargetThumbnailBase64 = string.Empty,
                            () => capturedTarget.SnapshotAssetId,
                            value => capturedTarget.SnapshotAssetId = value));
                    }
                }
                break;

            case IfStep ifStep:
                VisitCondition(ifStep.Condition, slots);
                break;

            case RepeatStep repeatStep:
                VisitCondition(repeatStep.Condition, slots);
                break;

            case FeedbackLoopStep feedback:
                VisitCondition(feedback.CompleteWhen, slots);
                VisitCondition(feedback.FailWhen, slots);
                break;
        }
    }

    private static void VisitCondition(Condition? condition, List<ImageAssetSlot> slots)
    {
        if (condition is null)
        {
            return;
        }

        VisitStep(condition.Check, slots);
        VisitCondition(condition.SecondCondition, slots);
    }

    private readonly record struct ImageAssetSlot(
        Func<string?> GetInline,
        Action<string> SetInline,
        Action ClearInline,
        Func<string?> GetAssetId,
        Action<string?> SetAssetId);
}
