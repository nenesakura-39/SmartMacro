using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;

namespace SmartMacro.Core.Vision;

/// <summary>
/// Single-scale and multi-scale search dispatch (plus edge-ignored inner cropping and Vision 2.0 detector fusion)
/// for <see cref="TemplateMatcher"/>.
/// </summary>
internal static class TemplateScaleSearch
{
    // Common Windows DPI / resolution scale steps ordered so standard desktop scales (125%, 150%, 80%, 75%) hit first.
    private static readonly double[] CandidateScales =
    {
        1.25, 1.50, 0.80, 0.75, 0.90, 1.10, 1.3333333, 1.20, 0.85, 1.15, 1.40, 0.95, 1.05, 0.6666667, 1.75, 2.00,
    };

    internal static TemplateMatchResult FindSingleScale(BgraImage image, BgraImage template, TemplateMatchOptions options,
        CancellationToken cancellationToken)
    {
        int width = image.Width, height = image.Height, tw = template.Width, th = template.Height;

        if (options.IgnoreEdgeFraction > 0)
        {
            var inner = TemplatePyramidMatcher.InnerArea(tw, th, options.IgnoreEdgeFraction);
            if (inner is { } crop && !TemplatePyramidMatcher.IsFlat(template.Crop(crop)))
                return FindInner(image, template.Crop(crop), crop.X, crop.Y, tw, th, options with { IgnoreEdgeFraction = 0 }, cancellationToken);
        }

        if (tw > width || th > height) return TemplateMatchResult.None;

        if (options.Detector == ImageDetectorKind.EdgeShape)
            return EdgeShapeMatcher.FindByEdgeShape(image, template, options, cancellationToken);

        var fine = new TemplatePyramidMatcher.FineTemplate(template);
        if (fine.Norm < 1e-6) return TemplateMatchResult.None; // flat template

        var result = options.MatchForeground
            ? TemplatePyramidMatcher.FindWithForeground(image, template, fine, options, cancellationToken)
            : TemplatePyramidMatcher.FindStandard(image, template, fine, options, cancellationToken);
        result = TemplatePyramidMatcher.WithBackgroundMask(image, template, result, options, cancellationToken);

        // Vision 2.0 Partial Visibility (occlusion tolerance): when particles or overlays cover part of the target so the
        // whole-template score falls below MinScore, run the cell-grid partial-visibility matcher.
        if (options.MinVisibleFraction is > 0.35 and < 0.995
            && (result.Matches.Count == 0 || result.BestScore < Math.Max(options.MinScore, 0.92)))
        {
            var partial = EdgeShapeMatcher.FindWithPartialVisibility(image, template, options, options.MinVisibleFraction, cancellationToken);
            result = MergeMatchResults(result, partial, options.MaxResults);
        }

        // Vision 2.0 Combined (Template + EdgeShape fusion): when Template alone didn't find a strong match, fuse with EdgeShape.
        if (options.Detector == ImageDetectorKind.Combined
            && (result.Matches.Count == 0 || result.BestScore < Math.Max(options.MinScore, 0.92)))
        {
            var edgeRes = EdgeShapeMatcher.FindByEdgeShape(image, template, options, cancellationToken);
            result = MergeMatchResults(result, edgeRes, options.MaxResults);
        }

        return result;
    }

    internal static TemplateMatchResult FindMultiScale(BgraImage image, BgraImage template, TemplateMatchOptions options,
        CancellationToken cancellationToken)
    {
        var minScale = Math.Clamp(Math.Min(options.MinScale, options.MaxScale), 0.4, 2.5);
        var maxScale = Math.Clamp(Math.Max(options.MinScale, options.MaxScale), minScale, 2.5);
        var singleOptions = options with { AllowMultiScale = false };

        var seenSizes = new HashSet<(int W, int H)>();
        var allMatches = new List<VisualMatch>();
        var bestScore = 0.0;
        var bestBounds = default(ScreenRect);
        var bestScale = 1.0;
        var totalRejectedForColor = 0;
        var maxCoarseScale = 1;

        // Fast path: try 1.0x first when within [minScale, maxScale]. If it already gives a strong match, return at once
        // with zero multi-scale overhead.
        if (1.0 >= minScale - 1e-6 && 1.0 <= maxScale + 1e-6
            && template.Width <= image.Width && template.Height <= image.Height)
        {
            seenSizes.Add((template.Width, template.Height));
            var exact = FindSingleScale(image, template, singleOptions, cancellationToken);
            if (exact.Matches.Count > 0 && exact.BestScore >= Math.Max(options.MinScore, 0.93))
                return exact;

            allMatches.AddRange(exact.Matches);
            bestScore = exact.BestScore;
            bestBounds = exact.BestBounds;
            totalRejectedForColor = exact.RejectedForColor;
            maxCoarseScale = exact.Scale;
        }

        var scales = new List<double>(CandidateScales.Length + 2);
        foreach (var candidate in CandidateScales)
        {
            if (candidate >= minScale - 1e-6 && candidate <= maxScale + 1e-6)
                scales.Add(candidate);
        }

        AddScaleIfDistinct(scales, minScale);
        AddScaleIfDistinct(scales, maxScale);

        foreach (var scale in scales)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Math.Abs(scale - 1.0) < 1e-4) continue;

            var sw = (int)Math.Round(template.Width * scale);
            var sh = (int)Math.Round(template.Height * scale);
            if (sw < TemplateMatcher.MinInnerSize || sh < TemplateMatcher.MinInnerSize || sw > image.Width || sh > image.Height) continue;
            if (!seenSizes.Add((sw, sh))) continue;

            var scaledTemplate = template.Resize(sw, sh);
            var res = FindSingleScale(image, scaledTemplate, singleOptions, cancellationToken);

            foreach (var match in res.Matches)
                allMatches.Add(match with { MatchedScale = scale });

            if (res.BestScore > bestScore)
            {
                bestScore = res.BestScore;
                bestBounds = res.BestBounds;
                bestScale = scale;
            }

            totalRejectedForColor += res.RejectedForColor;
            if (res.Scale > maxCoarseScale) maxCoarseScale = res.Scale;

            // Stop scanning distant scales once a near-exact scaled copy is already confirmed.
            if (res.Matches.Count > 0 && res.BestScore >= Math.Max(options.MinScore, 0.97))
                break;
        }

        if (allMatches.Count == 0 && bestScore <= 0 && totalRejectedForColor == 0)
            return TemplateMatchResult.None;

        var kept = TemplatePyramidMatcher.KeepBest(allMatches, options.MaxResults);
        return new TemplateMatchResult(kept, Math.Min(1.0, bestScore), kept.Count > 0 ? 0 : totalRejectedForColor, maxCoarseScale)
        {
            BestBounds = kept.Count > 0 ? kept[0].Bounds : bestBounds,
            BestScale = kept.Count > 0 ? kept[0].MatchedScale : bestScale,
        };
    }

    private static TemplateMatchResult FindInner(BgraImage image, BgraImage inner, int offsetX, int offsetY, int fullWidth, int fullHeight,
        TemplateMatchOptions options, CancellationToken cancellationToken)
    {
        var result = FindSingleScale(image, inner, options, cancellationToken);
        var bestBounds = result.BestBounds.IsEmpty
            ? default
            : new ScreenRect(result.BestBounds.X - offsetX, result.BestBounds.Y - offsetY, fullWidth, fullHeight);
        if (result.Matches.Count == 0) return result with { BestBounds = bestBounds };
        var matches = result.Matches
            .Select(m => m with { Bounds = new ScreenRect(m.Bounds.X - offsetX, m.Bounds.Y - offsetY, fullWidth, fullHeight) })
            .ToList();
        return result with { Matches = matches, BestBounds = bestBounds };
    }

    private static TemplateMatchResult MergeMatchResults(TemplateMatchResult a, TemplateMatchResult b, int maxResults)
    {
        var merged = TemplatePyramidMatcher.KeepBest(a.Matches.Concat(b.Matches), maxResults);
        var bestScore = Math.Max(a.BestScore, b.BestScore);
        var bestBounds = merged.Count > 0 ? merged[0].Bounds
            : b.BestScore > a.BestScore && !b.BestBounds.IsEmpty ? b.BestBounds
            : a.BestBounds;
        return new TemplateMatchResult(merged, Math.Min(1.0, bestScore), merged.Count > 0 ? 0 : a.RejectedForColor, Math.Max(a.Scale, b.Scale))
        {
            BestBounds = bestBounds,
            BestScale = merged.Count > 0 ? merged[0].MatchedScale : a.BestScale,
        };
    }

    private static void AddScaleIfDistinct(List<double> scales, double value)
    {
        foreach (var existing in scales)
        {
            if (Math.Abs(existing - value) < 0.01) return;
        }

        scales.Add(value);
    }
}
