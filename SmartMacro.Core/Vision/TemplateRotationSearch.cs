using SmartMacro.Core.Ocr;

namespace SmartMacro.Core.Vision;

/// <summary>
/// Upright and optional ±angle rotation search for a single template appearance, delegating scale sweeps to
/// <see cref="TemplateScaleSearch"/>.
/// </summary>
internal static class TemplateRotationSearch
{
    internal static TemplateMatchResult FindWithRotation(BgraImage image, BgraImage template, TemplateMatchOptions options,
        CancellationToken cancellationToken)
    {
        var baseOpts = options.RotationRangeDeg > 0 ? options with { RotationRangeDeg = 0 } : options;
        var upright = baseOpts.AllowMultiScale
            ? TemplateScaleSearch.FindMultiScale(image, template, baseOpts, cancellationToken)
            : TemplateScaleSearch.FindSingleScale(image, template, baseOpts, cancellationToken);

        if (options.RotationRangeDeg <= 0 || (upright.Matches.Count > 0 && upright.BestScore >= Math.Max(options.MinScore, 0.94)))
            return upright;

        int maxDeg = Math.Clamp(options.RotationRangeDeg, 1, 45);
        int stepDeg = maxDeg <= 10 ? 5 : 8;
        var allMatches = new List<VisualMatch>(upright.Matches);
        double bestScore = upright.BestScore;
        ScreenRect bestBounds = upright.BestBounds;

        for (int deg = stepDeg; deg <= maxDeg; deg += stepDeg)
        {
            foreach (int sign in new[] { -1, 1 })
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rotated = EdgeShapeMatcher.Rotate(template, sign * deg);
                var res = TemplateScaleSearch.FindSingleScale(image, rotated, baseOpts with { AllowMultiScale = false }, cancellationToken);
                allMatches.AddRange(res.Matches);
                if (res.BestScore > bestScore)
                {
                    bestScore = res.BestScore;
                    bestBounds = res.BestBounds;
                }
            }
        }

        var kept = TemplatePyramidMatcher.KeepBest(allMatches, options.MaxResults);
        return new TemplateMatchResult(kept, Math.Min(1.0, bestScore), kept.Count > 0 ? 0 : upright.RejectedForColor, upright.Scale)
        {
            BestBounds = kept.Count > 0 ? kept[0].Bounds : bestBounds,
            BestScale = upright.BestScale,
        };
    }
}
