using SmartMacro.Core.Models;

namespace SmartMacro.Core.Vision;

/// <summary>
/// Result of <see cref="TemplateAutoTuner.AnalyzeSamples"/>: recommended settings learned from one or more captures of
/// the target, plus a plain-English summary of why those settings were chosen.
/// </summary>
public sealed record AutoTuneRecommendation(
    int MinSimilarityPercent,
    int MinVisiblePercent,
    int IgnoreEdgesPercent,
    bool MatchForeground,
    ImageDetectorKind Detector,
    VisionScenario SuggestedScenario,
    string Summary);

/// <summary>
/// Vision 2.0 "Teach Me" Sample Analyzer &amp; 1-Click Scenario Preset Engine.
/// Helps beginners get optimal complex-scene settings in one click while giving advanced users full control over the
/// underlying parameters.
/// </summary>
public static class TemplateAutoTuner
{
    /// <summary>
    /// Applies a 1-click <see cref="VisionScenario"/> preset to <paramref name="step"/>, tuning its polling speed,
    /// strategy, tracker, predictive click, and detector/matcher tolerances for that scenario.
    /// </summary>
    public static string ApplyScenario(ScreenSearchStep step, VisionScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(step);
        step.Scenario = scenario;

        switch (scenario)
        {
            case VisionScenario.StaticUi:
                step.CheckIntervalMs = 250;
                step.Strategy = VisionSearchStrategy.Balanced;
                step.TimeBudgetMs = 0;
                step.TrackTarget = false;
                step.PredictiveClick = false;
                step.PredictiveLeadMs = 0;
                step.ConfirmFrames = 0;
                step.OnlyWhereMoved = false;
                switch (step)
                {
                    case FindImageStep img:
                        img.MinSimilarityPercent = 90;
                        img.MinVisiblePercent = 0;
                        img.Detector = ImageDetectorKind.Template;
                        img.IgnoreEdgesPercent = 0;
                        img.MatchForeground = false;
                        break;
                    case FindColorStep col:
                        col.Tolerance = 10;
                        col.MatchShades = false;
                        col.ColorSpace = ColorSpaceKind.Rgb;
                        col.SelectRule = ColorSelectRule.Largest;
                        col.MinCircularityPercent = 0;
                        col.MinFillPercent = 0;
                        break;
                    case FindTextStep txt:
                        txt.MinSimilarityPercent = 90;
                        txt.Preprocess = OcrPreprocessMode.Auto;
                        txt.TrackTextAsImage = false;
                        break;
                }
                return "🖥️ Static App / Website: high accuracy (90%), standard 250 ms checks, lowest CPU usage.";

            case VisionScenario.GameHud:
                step.CheckIntervalMs = 150;
                step.Strategy = VisionSearchStrategy.Balanced;
                step.TimeBudgetMs = 0;
                step.TrackTarget = false;
                step.PredictiveClick = false;
                step.ConfirmFrames = 0;
                step.OnlyWhereMoved = false;
                switch (step)
                {
                    case FindImageStep img:
                        img.MinSimilarityPercent = 85;
                        img.MinVisiblePercent = 80;
                        img.IgnoreEdgesPercent = FindImageStep.RecommendedIgnoreEdgesPercent;
                        img.MatchForeground = true;
                        img.AdaptToLighting = true;
                        img.Detector = ImageDetectorKind.Template;
                        break;
                    case FindColorStep col:
                        col.Tolerance = 28;
                        col.MatchShades = true;
                        col.ColorSpace = ColorSpaceKind.Rgb;
                        break;
                    case FindTextStep txt:
                        txt.MinSimilarityPercent = 85;
                        txt.NormalizeLookAlikes = true;
                        txt.Preprocess = OcrPreprocessMode.HighContrast;
                        txt.TrackTextAsImage = false;
                        break;
                }
                return "🎮 Game HUD / Menu: ignores 15% outer corners and see-through backgrounds, adapts to Day/Night lighting.";

            case VisionScenario.MovingTarget:
                step.CheckIntervalMs = 30;
                step.Strategy = VisionSearchStrategy.Fastest;
                step.TimeBudgetMs = 0;
                step.TrackTarget = true;
                step.PredictiveClick = true;
                step.PredictiveLeadMs = 65;
                step.ConfirmFrames = 0;
                step.OnlyWhereMoved = false;
                switch (step)
                {
                    case FindImageStep img:
                        img.MinSimilarityPercent = 82;
                        img.MinVisiblePercent = 70;
                        img.IgnoreEdgesPercent = FindImageStep.RecommendedIgnoreEdgesPercent;
                        img.MatchForeground = true;
                        img.Detector = ImageDetectorKind.Combined;
                        break;
                    case FindColorStep col:
                        col.Tolerance = 32;
                        col.MatchShades = true;
                        col.ColorSpace = ColorSpaceKind.Hsv;
                        col.SelectRule = ColorSelectRule.NearestToLastSpot;
                        break;
                    case FindTextStep txt:
                        txt.MinSimilarityPercent = 80;
                        txt.TrackTextAsImage = true;
                        txt.Preprocess = OcrPreprocessMode.HighContrast;
                        break;
                }
                return "🏃 Moving Target: 2D velocity tracking ON, predictive lead click (65 ms ahead), 70% partial visibility, fast 30 ms checks.";

            case VisionScenario.BusyParticles:
                step.CheckIntervalMs = 40;
                step.Strategy = VisionSearchStrategy.MostAccurate;
                step.TimeBudgetMs = 0;
                step.TrackTarget = true;
                step.PredictiveClick = true;
                step.PredictiveLeadMs = 60;
                step.ConfirmFrames = 2;
                step.OnlyWhereMoved = false;
                switch (step)
                {
                    case FindImageStep img:
                        img.MinSimilarityPercent = 80;
                        img.MinVisiblePercent = 60;
                        img.IgnoreEdgesPercent = FindImageStep.RecommendedIgnoreEdgesPercent;
                        img.MatchForeground = true;
                        img.Detector = ImageDetectorKind.Combined;
                        break;
                    case FindColorStep col:
                        col.Tolerance = 28;
                        col.MatchShades = true;
                        col.ColorSpace = ColorSpaceKind.Hsv;
                        col.MinCircularityPercent = 35;
                        col.MinFillPercent = 30;
                        col.SelectRule = ColorSelectRule.NearestToLastSpot;
                        break;
                    case FindTextStep txt:
                        txt.MinSimilarityPercent = 80;
                        txt.Preprocess = OcrPreprocessMode.BinarizeOtsu;
                        txt.TrackTextAsImage = true;
                        break;
                }
                return "✨ Busy Scene / Particles: matches even when 40% is covered by particles (60% visible needed), Template+Edge fusion, 2-frame confirmation to ignore flashes.";

            case VisionScenario.ScrollingText:
                step.CheckIntervalMs = 30;
                step.Strategy = VisionSearchStrategy.Fastest;
                step.TimeBudgetMs = 0;
                step.TrackTarget = true;
                step.PredictiveClick = true;
                step.PredictiveLeadMs = 50;
                step.ConfirmFrames = 0;
                if (step is FindTextStep scrollTxt)
                {
                    scrollTxt.MinSimilarityPercent = 80;
                    scrollTxt.NormalizeLookAlikes = true;
                    scrollTxt.TrackTextAsImage = true;
                    scrollTxt.Preprocess = OcrPreprocessMode.HighContrast;
                }
                return "📜 Shuffling / Scrolling Text: O(1) word index + Find-Once-Then-Track (follows the found word as an image at frame rate).";

            case VisionScenario.FastReaction:
                step.CheckIntervalMs = 16;
                step.Strategy = VisionSearchStrategy.Fastest;
                step.TimeBudgetMs = 16;
                step.TrackTarget = false;
                step.PredictiveClick = false;
                step.PredictiveLeadMs = 0;
                step.MoveStyle = MouseMoveStyle.Instant;
                step.MoveDurationMs = 0;
                step.ClickHoldMs = 12;
                step.PostActionDelayMs = 0;
                step.ConfirmFrames = 0;
                return "⚡ Instant Reflex: 16 ms polling, instant snap, and zero-lag click for rapid-fire targets & QTEs.";

            default:
                return "Custom settings.";
        }
    }

    /// <summary>
    /// Analyzes one or more sample images of a target ("Teach Me") and computes the best tolerances, partial-visibility
    /// percentage, edge-ignore margin, and detector mode.
    /// </summary>
    public static AutoTuneRecommendation AnalyzeSamples(IReadOnlyList<BgraImage> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
            throw new ArgumentException("At least one sample image is required.", nameof(samples));

        var primary = samples[0];
        int w = primary.Width, h = primary.Height;

        // 1. Check whether the outer 15% border differs strongly from the inner 70% core (e.g., round button or world background in corners).
        double borderDiff = ComputeBorderVsCoreDifference(primary);
        bool ignoreEdges = borderDiff > 26.0 && Math.Min(w, h) >= 16;

        // 2. Check whether strong strokes/foreground stand out from the rest of the image.
        bool hasForeground = ForegroundModel.Build(primary) is not null;

        if (samples.Count == 1)
        {
            var scenario = ignoreEdges && hasForeground ? VisionScenario.GameHud : VisionScenario.StaticUi;
            return new AutoTuneRecommendation(
                MinSimilarityPercent: ignoreEdges ? 86 : 90,
                MinVisiblePercent: ignoreEdges ? 80 : 0,
                IgnoreEdgesPercent: ignoreEdges ? FindImageStep.RecommendedIgnoreEdgesPercent : 0,
                MatchForeground: hasForeground && ignoreEdges,
                Detector: ImageDetectorKind.Template,
                SuggestedScenario: scenario,
                Summary: ignoreEdges
                    ? "Auto-tuned from sample: detected background in corners → enabled 15% edge ignore, foreground matching, and 80% partial visibility."
                    : "Auto-tuned from sample: clean crisp target → 90% similarity with standard template matching.");
        }

        // Multi-sample analysis: compare later samples against the primary sample to measure how much of the target stays
        // consistent vs how much is occluded by particles or animation.
        double minPairSimilarity = 1.0;
        double minVisibleCells = 1.0;

        for (int i = 1; i < samples.Count; i++)
        {
            var other = samples[i].Width == w && samples[i].Height == h
                ? samples[i]
                : samples[i].Resize(w, h);

            var cellStats = CompareSampleCells(primary, other);
            if (cellStats.UnoccludedScore < minPairSimilarity) minPairSimilarity = cellStats.UnoccludedScore;
            if (cellStats.StableFraction < minVisibleCells) minVisibleCells = cellStats.StableFraction;
        }

        int recommendedVisible = minVisibleCells < 0.92
            ? Math.Clamp((int)Math.Floor(minVisibleCells * 100.0 / 5.0) * 5, 50, 85)
            : 0;
        int recommendedSim = Math.Clamp((int)Math.Floor(minPairSimilarity * 100.0) - 5, 72, 92);
        var detector = recommendedVisible > 0 ? ImageDetectorKind.Combined : ImageDetectorKind.Template;
        var suggestedScenario = recommendedVisible is > 0 and <= 65
            ? VisionScenario.BusyParticles
            : VisionScenario.MovingTarget;

        return new AutoTuneRecommendation(
            MinSimilarityPercent: recommendedSim,
            MinVisiblePercent: recommendedVisible,
            IgnoreEdgesPercent: ignoreEdges ? FindImageStep.RecommendedIgnoreEdgesPercent : 0,
            MatchForeground: true,
            Detector: detector,
            SuggestedScenario: suggestedScenario,
            Summary: $"Learned from {samples.Count} samples: ~{(int)Math.Round(minVisibleCells * 100)}% of the target stays consistent across frames → set accuracy to {recommendedSim}%{(recommendedVisible > 0 ? $", required visible to {recommendedVisible}%, and enabled Combined Template+Edge matching" : "")}.");
    }

    private static double ComputeBorderVsCoreDifference(BgraImage img)
    {
        int w = img.Width, h = img.Height;
        int bx = Math.Max(1, w * 15 / 100), by = Math.Max(1, h * 15 / 100);
        long borderR = 0, borderG = 0, borderB = 0, borderN = 0;
        long coreR = 0, coreG = 0, coreB = 0, coreN = 0;
        var p = img.Pixels;

        for (int y = 0; y < h; y++)
        {
            bool yBorder = y < by || y >= h - by;
            for (int x = 0; x < w; x++)
            {
                bool isBorder = yBorder || x < bx || x >= w - bx;
                int i = (y * w + x) * 4;
                if (isBorder)
                {
                    borderB += p[i]; borderG += p[i + 1]; borderR += p[i + 2]; borderN++;
                }
                else
                {
                    coreB += p[i]; coreG += p[i + 1]; coreR += p[i + 2]; coreN++;
                }
            }
        }

        if (borderN == 0 || coreN == 0) return 0;
        double db = Math.Abs((double)borderB / borderN - (double)coreB / coreN);
        double dg = Math.Abs((double)borderG / borderN - (double)coreG / coreN);
        double dr = Math.Abs((double)borderR / borderN - (double)coreR / coreN);
        return (db + dg + dr) / 3.0;
    }

    private static (double StableFraction, double UnoccludedScore) CompareSampleCells(BgraImage a, BgraImage b)
    {
        int cols = 4, rows = 4;
        int w = a.Width, h = a.Height;
        var scores = new List<double>(16);

        for (int r = 0; r < rows; r++)
        {
            int y0 = r * h / rows, y1 = (r + 1) * h / rows;
            if (y1 <= y0) continue;
            for (int c = 0; c < cols; c++)
            {
                int x0 = c * w / cols, x1 = (c + 1) * w / cols;
                if (x1 <= x0) continue;

                double diff = 0;
                int count = 0;
                for (int y = y0; y < y1; y++)
                {
                    for (int x = x0; x < x1; x++)
                    {
                        int i = (y * w + x) * 4;
                        diff += (Math.Abs(a.Pixels[i] - b.Pixels[i])
                               + Math.Abs(a.Pixels[i + 1] - b.Pixels[i + 1])
                               + Math.Abs(a.Pixels[i + 2] - b.Pixels[i + 2])) / (3.0 * 255.0);
                        count++;
                    }
                }

                scores.Add(1.0 - diff / Math.Max(1, count));
            }
        }

        if (scores.Count == 0) return (1.0, 1.0);
        scores.Sort((x, y) => y.CompareTo(x));
        int stableCount = scores.Count(s => s >= 0.78);
        double stableFrac = Math.Clamp((double)stableCount / scores.Count, 0.4, 1.0);
        int topN = Math.Max(1, stableCount);
        double topMean = scores.Take(topN).Average();
        return (stableFrac, topMean);
    }
}
