using SmartMacro.Core.Models;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Vision;

namespace SmartMacro.Core.Storage;

/// <summary>One reusable visual template discovered inside the current macro.</summary>
public sealed record SharedTemplateCandidate(
    string Key,
    string Label,
    string SourceDetail,
    int Width,
    int Height,
    string TemplateData,
    string? PreviewPngBase64);

/// <summary>
/// Collects deduplicated captured templates and recorded click snapshots already present in a <see cref="Macro"/>
/// so users can reuse an existing image across multiple <see cref="FindImageStep"/> actions without re-capturing.
/// </summary>
public static class MacroTemplateGallery
{
    public static IReadOnlyList<SharedTemplateCandidate> Collect(
        Macro? macro,
        Guid? excludeStepId = null)
    {
        if (macro is null) return Array.Empty<SharedTemplateCandidate>();

        var result = new List<SharedTemplateCandidate>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var steps = macro.Steps ?? new List<MacroStep>();

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (step is null) continue;
            var stepNum = i + 1;

            switch (step)
            {
                case FindImageStep img:
                    TryCollectFindImage(macro, img, $"Step {stepNum} · Find image", excludeStepId, result, seenKeys);
                    break;

                case MouseClickStep { HasTargetThumbnail: true } click when click.Id != excludeStepId:
                    TryCollectPngThumbnail(
                        click.TargetThumbnailBase64,
                        $"Step {stepNum} · Click snapshot",
                        result,
                        seenKeys);
                    break;

                case InputSequenceStep { ClickTargets: { Count: > 0 } targets } when step.Id != excludeStepId:
                    for (var t = 0; t < targets.Count; t++)
                    {
                        var target = targets[t];
                        if (target is null || string.IsNullOrEmpty(target.TargetThumbnailBase64)) continue;
                        TryCollectPngThumbnail(
                            target.TargetThumbnailBase64,
                            $"Step {stepNum} · Recorded click #{t + 1}",
                            result,
                            seenKeys);
                    }
                    break;

                default:
                    foreach (var cond in WorkflowSteps.ConditionsOf(step))
                    {
                        if (cond.Check is FindImageStep condImg)
                            TryCollectFindImage(macro, condImg, $"Step {stepNum} · {StepDescriber.DisplayName(step.Kind)} condition", excludeStepId, result, seenKeys);
                    }
                    break;
            }
        }

        if (macro.Watchers is { Count: > 0 } watchers)
        {
            for (var w = 0; w < watchers.Count; w++)
            {
                var watcher = watchers[w];
                if (watcher?.Condition?.Check is FindImageStep watchImg)
                {
                    var name = string.IsNullOrWhiteSpace(watcher.Name) ? $"Watcher #{w + 1}" : $"Watcher \"{watcher.Name.Trim()}\"";
                    TryCollectFindImage(macro, watchImg, name, excludeStepId, result, seenKeys);
                }
            }
        }

        return result;
    }

    private static void TryCollectFindImage(
        Macro macro,
        FindImageStep img,
        string sourcePrefix,
        Guid? excludeStepId,
        List<SharedTemplateCandidate> result,
        HashSet<string> seenKeys)
    {
        if (img.Id != excludeStepId)
        {
            var primary = ResolvePayload(macro, img.TemplateData, img.TemplateAssetId);
            TryAddEncodedTemplate(primary, img.TemplateWidth, img.TemplateHeight, sourcePrefix, result, seenKeys);
        }

        if (img.AdditionalTemplates is { Count: > 0 } extras)
        {
            for (var i = 0; i < extras.Count; i++)
            {
                var variant = extras[i];
                if (variant is null) continue;
                var payload = ResolvePayload(macro, variant.Data, variant.TemplateAssetId);
                TryAddEncodedTemplate(payload, variant.Width, variant.Height, $"{sourcePrefix} (frame #{i + 2})", result, seenKeys);
            }
        }
    }

    private static string ResolvePayload(Macro macro, string? inlineData, string? assetId)
    {
        if (!string.IsNullOrEmpty(inlineData)) return inlineData;
        if (!string.IsNullOrWhiteSpace(assetId) && macro.Assets is not null && macro.Assets.TryGetValue(assetId, out var stored))
            return stored;
        return string.Empty;
    }

    private static void TryAddEncodedTemplate(
        string? templateData,
        int width,
        int height,
        string sourcePrefix,
        List<SharedTemplateCandidate> result,
        HashSet<string> seenKeys)
    {
        if (string.IsNullOrEmpty(templateData)) return;
        if (!TemplateCodec.TryDecode(templateData, width, height, out var bgra, out _) || bgra is null) return;
        if (bgra.Width < MacroValidator.MinTemplateSize || bgra.Height < MacroValidator.MinTemplateSize) return;

        var key = $"{MacroAssetStore.ComputeAssetId(templateData)}_{bgra.Width}x{bgra.Height}";
        if (!seenKeys.Add(key)) return;

        string? pngBase64 = null;
        try
        {
            pngBase64 = Convert.ToBase64String(PngCodec.Encode(bgra));
        }
        catch (Exception)
        {
            // Preview is optional; template payload remains usable.
        }

        var dims = $"{bgra.Width}×{bgra.Height}";
        result.Add(new SharedTemplateCandidate(
            key,
            $"{sourcePrefix} ({dims})",
            $"{sourcePrefix} ({dims})",
            bgra.Width,
            bgra.Height,
            templateData,
            pngBase64));
    }

    private static void TryCollectPngThumbnail(
        string? pngBase64,
        string sourcePrefix,
        List<SharedTemplateCandidate> result,
        HashSet<string> seenKeys)
    {
        if (string.IsNullOrEmpty(pngBase64)) return;
        if (!PngCodec.TryDecodeBase64(pngBase64, out var bgra, out _) || bgra is null) return;
        if (bgra.Width < MacroValidator.MinTemplateSize || bgra.Height < MacroValidator.MinTemplateSize) return;

        var templateData = TemplateCodec.Encode(bgra);
        var key = $"{MacroAssetStore.ComputeAssetId(templateData)}_{bgra.Width}x{bgra.Height}";
        if (!seenKeys.Add(key)) return;

        var dims = $"{bgra.Width}×{bgra.Height}";
        result.Add(new SharedTemplateCandidate(
            key,
            $"{sourcePrefix} ({dims})",
            $"{sourcePrefix} ({dims})",
            bgra.Width,
            bgra.Height,
            templateData,
            pngBase64));
    }
}
