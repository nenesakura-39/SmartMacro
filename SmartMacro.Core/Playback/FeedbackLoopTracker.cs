using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Vision;

namespace SmartMacro.Core.Playback;

/// <summary>
/// Result of a 1D projection scan along the active axis (<c>X</c> for Horizontal, <c>Y</c> for Vertical)
/// inside a <see cref="FeedbackLoopStep"/> search region.
/// </summary>
public readonly record struct BarScanResult(
    bool Found,
    double CenterPos,
    double MinEdge,
    double MaxEdge,
    double SpanLength,
    int MatchCount)
{
    public static BarScanResult NotFound => default;
}

/// <summary>
/// 1D projection scanner for horizontal and vertical bar minigames with gap-bridging across target occlusion
/// and auto-calibrated bar width memory for edge-occlusion reconstruction.
/// </summary>
public sealed class FeedbackLoopTracker
{
    private double _estimatedBarWidth;
    private double _lastIndicatorCenter = double.NaN;
    private string? _cachedMarkerColorStr;
    private RgbColor _cachedMarkerColor;
    private string? _cachedSecMarkerColorStr;
    private RgbColor? _cachedSecMarkerColor;
    private string? _cachedZoneColorStr;
    private RgbColor _cachedZoneColor;
    private string? _cachedSecZoneColorStr;
    private RgbColor? _cachedSecZoneColor;

    /// <summary>
    /// Running exponential moving average of valid unoccluded bar widths (clamped between 8% and 45% of region length).
    /// </summary>
    public double EstimatedBarWidth
    {
        get => _estimatedBarWidth;
        set => _estimatedBarWidth = Math.Max(0, value);
    }

    /// <summary>
    /// True when the most recent <see cref="ScanIndicator"/> matched the active/overlapping 2nd bar color (<c>SecondaryZoneColor</c>).
    /// </summary>
    public bool LastBarWasActiveSecondary { get; private set; }

    public void Reset()
    {
        _estimatedBarWidth = 0;
        _lastIndicatorCenter = double.NaN;
        LastBarWasActiveSecondary = false;
    }

    /// <summary>
    /// Computes the cross-axis border margin (top/bottom rows for Horizontal, left/right columns for Vertical)
    /// to exclude track frames, bevels, outer border shadows, and loose background margins on 2D bar captures.
    /// </summary>
    public static int GetCrossMargin(int crossLength) =>
        crossLength >= 14 ? Math.Max(3, (int)Math.Round(crossLength * 0.22)) : (crossLength >= 10 ? 2 : 0);

    /// <summary>
    /// Scans a captured <see cref="BgraImage"/> of the search region for both the Target (fish / marker) and the
    /// Indicator (player control bar) along the active axis.
    /// </summary>
    public (BarScanResult Target, BarScanResult Indicator) Scan(
        BgraImage frame,
        FeedbackLoopStep step)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(step);

        if (_cachedMarkerColorStr != step.MarkerColor)
        {
            _cachedMarkerColorStr = step.MarkerColor;
            RgbColor.TryParse(step.MarkerColor, out _cachedMarkerColor);
        }

        if (!step.UseSecondaryMarkerColor)
        {
            _cachedSecMarkerColor = null;
            _cachedSecMarkerColorStr = null;
        }
        else
        {
            var secStr = string.IsNullOrWhiteSpace(step.SecondaryMarkerColor) ? "#FFFFFF" : step.SecondaryMarkerColor;
            if (_cachedSecMarkerColorStr != secStr)
            {
                _cachedSecMarkerColorStr = secStr;
                _cachedSecMarkerColor = RgbColor.TryParse(secStr, out var st) ? st : null;
            }
        }

        var hasZoneColors = step.ZoneMode == FeedbackZoneMode.DetectedByColor;
        if (hasZoneColors)
        {
            if (_cachedZoneColorStr != step.ZoneColor)
            {
                _cachedZoneColorStr = step.ZoneColor;
                RgbColor.TryParse(step.ZoneColor, out _cachedZoneColor);
            }

            if (!step.UseSecondaryZoneColor)
            {
                _cachedSecZoneColor = null;
                _cachedSecZoneColorStr = null;
            }
            else
            {
                var secZStr = string.IsNullOrWhiteSpace(step.SecondaryZoneColor) ? "#FFFFFF" : step.SecondaryZoneColor;
                if (_cachedSecZoneColorStr != secZStr)
                {
                    _cachedSecZoneColorStr = secZStr;
                    _cachedSecZoneColor = RgbColor.TryParse(secZStr, out var sz) ? sz : null;
                }
            }
        }

        var crossLength = step.Orientation == BarOrientation.Horizontal ? frame.Height : frame.Width;
        var crossMargin = GetCrossMargin(crossLength);
        var interiorCross = Math.Max(1, crossLength - (2 * crossMargin));
        var minTargetSliceMatches = interiorCross <= 4 ? 1 : Math.Max(2, (int)Math.Ceiling(interiorCross * 0.40));

        // 1. Project target counts while rejecting slices vertically sandwiched by the player bar (e.g. internal ← / → arrows).
        var targetCounts = hasZoneColors && crossLength >= 10
            ? ProjectTargetAxisCountsExcludingBarInteriorDecals(
                frame,
                _cachedMarkerColor,
                _cachedSecMarkerColor,
                step.MarkerTolerance,
                _cachedZoneColor,
                _cachedSecZoneColor,
                step.ZoneTolerance,
                step.Orientation,
                crossMargin)
            : ProjectAxisCounts(frame, _cachedMarkerColor, _cachedSecMarkerColor, step.MarkerTolerance, step.Orientation, crossMargin);

        var target = ScanTargetCounts(targetCounts, Math.Max(1, step.MarkerMinPixels), minTargetSliceMatches);

        // 2. Scan Indicator (Player Bar), evaluating active secondary color separately from inactive primary color.
        BarScanResult indicator;
        if (step.ZoneMode == FeedbackZoneMode.Fixed)
        {
            LastBarWasActiveSecondary = false;
            var length = step.Orientation == BarOrientation.Horizontal ? frame.Width : frame.Height;
            var minEdge = (step.ZoneStartPercent / 100.0) * Math.Max(1, length - 1);
            var maxEdge = (step.ZoneEndPercent / 100.0) * Math.Max(1, length - 1);
            var span = Math.Max(0, maxEdge - minEdge);
            indicator = new BarScanResult(true, (minEdge + maxEdge) * 0.5, minEdge, maxEdge, span, (int)Math.Round(span));
        }
        else
        {
            indicator = ScanIndicator(
                frame,
                _cachedZoneColor,
                step.ZoneTolerance,
                step.Orientation,
                target,
                _cachedSecZoneColor,
                Math.Max(1, step.ZoneMinPixels));

            // 3. Apply bar-state overlap context when a 2nd (active/overlapping) bar color is configured:
            if (indicator.Found && _cachedSecZoneColor is { } activeColor)
            {
                if (LastBarWasActiveSecondary)
                {
                    // The bar is currently in its Active/Overlapping color (e.g. White): the target MUST be overlapping the bar!
                    var minWindow = Math.Max(0, (int)Math.Floor(indicator.MinEdge) - 10);
                    var maxWindow = Math.Min(targetCounts.Length - 1, (int)Math.Ceiling(indicator.MaxEdge) + 10);

                    if (!target.Found || target.CenterPos < minWindow || target.CenterPos > maxWindow)
                    {
                        var windowedCounts = new int[targetCounts.Length];
                        for (var i = minWindow; i <= maxWindow; i++)
                            windowedCounts[i] = targetCounts[i];

                        var insideTarget = ScanTargetCounts(windowedCounts, Math.Max(1, step.MarkerMinPixels), minTargetSliceMatches);
                        if (insideTarget.Found)
                        {
                            target = insideTarget;
                        }
                        else
                        {
                            // If the active bar color completely occluded the target color, use the active bar's center
                            target = new BarScanResult(true, indicator.CenterPos, indicator.MinEdge, indicator.MaxEdge, indicator.SpanLength, indicator.MatchCount);
                        }
                    }
                }
                else if (target.Found)
                {
                    // The bar is in its Inactive primary color (e.g. Brown), so the target is NOT overlapping the bar's interior.
                    // Any match strictly inside the inactive bar is an internal bar decal/arrow (← / →) and must be masked out.
                    var innerMin = (int)Math.Ceiling(indicator.MinEdge) + 4;
                    var innerMax = (int)Math.Floor(indicator.MaxEdge) - 4;
                    if (innerMax > innerMin && target.CenterPos >= innerMin && target.CenterPos <= innerMax)
                    {
                        var maskedCounts = (int[])targetCounts.Clone();
                        for (var i = Math.Max(0, innerMin); i <= Math.Min(maskedCounts.Length - 1, innerMax); i++)
                            maskedCounts[i] = 0;

                        target = ScanTargetCounts(maskedCounts, Math.Max(1, step.MarkerMinPixels), minTargetSliceMatches);
                    }
                }
            }
        }

        return (target, indicator);
    }

    /// <summary>
    /// Scans for the Target (Fish / Marker) along the active axis and returns the densest contiguous cluster
    /// (peak density centroid) so background sparkles or water ripples are ignored.
    /// </summary>
    public BarScanResult ScanTarget(
        BgraImage frame,
        RgbColor targetColor,
        int tolerance,
        BarOrientation orientation = BarOrientation.Horizontal,
        RgbColor? secondaryColor = null,
        int minPixels = 1)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var crossLength = orientation == BarOrientation.Horizontal ? frame.Height : frame.Width;
        var crossMargin = GetCrossMargin(crossLength);
        var interiorCross = Math.Max(1, crossLength - (2 * crossMargin));
        var minSliceMatches = interiorCross <= 4 ? 1 : Math.Max(2, (int)Math.Ceiling(interiorCross * 0.40));
        var counts = ProjectAxisCounts(frame, targetColor, secondaryColor, tolerance, orientation, crossMargin);
        return ScanTargetCounts(counts, minPixels, minSliceMatches);
    }

    /// <summary>
    /// Scans a 1D array of per-column/per-row pixel match counts for the Target (Fish / Marker) by finding
    /// the highest-density compact cluster (peak slice density × total matches).
    /// </summary>
    public static BarScanResult ScanTargetCounts(ReadOnlySpan<int> counts, int minPixels = 1, int minSliceMatches = 1)
    {
        if (counts.IsEmpty) return BarScanResult.NotFound;

        var sliceThreshold = Math.Max(1, minSliceMatches);
        var maxTargetSpan = Math.Max(8, (int)Math.Ceiling(counts.Length * 0.18));

        int bestStart = -1, bestEnd = -1, bestMatches = 0;
        double bestScore = -1.0;
        double bestWeightedSum = 0;

        int curStart = -1, curEnd = -1, curMatches = 0, curPeak = 0;
        double curWeightedSum = 0;
        int gap = 0;
        const int maxTargetGap = 2;

        void CommitCluster(int start, int end, int matches, int peak, double weightedSum)
        {
            if (start < 0 || matches < Math.Max(1, minPixels)) return;
            var width = end - start + 1;
            // Score by total matches × peak column height, heavily penalizing wide background slabs (> 18% of track)
            var widthPenalty = width > maxTargetSpan ? (maxTargetSpan / (double)(width * width)) : 1.0;
            var score = matches * (double)peak * widthPenalty;
            if (score > bestScore)
            {
                bestScore = score;
                bestStart = start;
                bestEnd = end;
                bestMatches = matches;
                bestWeightedSum = weightedSum;
            }
        }

        for (var i = 0; i < counts.Length; i++)
        {
            var c = counts[i];
            if (c >= sliceThreshold)
            {
                if (curStart < 0) curStart = i;
                curEnd = i;
                curMatches += c;
                if (c > curPeak) curPeak = c;
                curWeightedSum += i * (double)c;
                gap = 0;
            }
            else if (curStart >= 0)
            {
                gap++;
                if (gap > maxTargetGap)
                {
                    CommitCluster(curStart, curEnd, curMatches, curPeak, curWeightedSum);
                    curStart = -1;
                    curMatches = 0;
                    curPeak = 0;
                    curWeightedSum = 0;
                    gap = 0;
                }
            }
        }

        if (curStart >= 0)
            CommitCluster(curStart, curEnd, curMatches, curPeak, curWeightedSum);

        if (bestStart < 0 || bestMatches < Math.Max(1, minPixels))
            return BarScanResult.NotFound;

        var center = bestWeightedSum / bestMatches;
        var span = (double)(bestEnd - bestStart);
        return new BarScanResult(true, center, bestStart, bestEnd, span, bestMatches);
    }

    /// <summary>
    /// Scans for the Indicator (Player Bar) along the active axis:
    /// 1. Ignores outer cross-axis track borders (<see cref="GetCrossMargin"/>) and requires 35% interior slice fill.
    /// 2. Evaluates <paramref name="secondaryColor"/> (active/overlapping bar) separately from <paramref name="indicatorColor"/>
    ///    so white active bars never merge with brown track borders.
    /// 3. Trims isolated noise specks and end-cap brackets, bridges internal marker/arrow gaps, and tracks <see cref="EstimatedBarWidth"/>.
    /// </summary>
    public BarScanResult ScanIndicator(
        BgraImage frame,
        RgbColor indicatorColor,
        int tolerance,
        BarOrientation orientation = BarOrientation.Horizontal,
        BarScanResult? targetScan = null,
        RgbColor? secondaryColor = null,
        int minPixels = 1)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var length = orientation == BarOrientation.Horizontal ? frame.Width : frame.Height;
        var crossLength = orientation == BarOrientation.Horizontal ? frame.Height : frame.Width;
        var crossMargin = GetCrossMargin(crossLength);
        var interiorCross = Math.Max(1, crossLength - (2 * crossMargin));
        var minSliceMatches = interiorCross <= 4 ? 1 : Math.Max(2, (int)Math.Ceiling(interiorCross * 0.35));

        // If a secondary (active/overlapping) bar color is configured, evaluate it separately first!
        if (secondaryColor is { } activeColor)
        {
            var secCounts = ProjectAxisCounts(frame, activeColor, null, tolerance, orientation, crossMargin);
            var secScan = ScanIndicatorCounts(secCounts, targetScan, minSliceMatches, minPixels);
            if (secScan.Found && secScan.SpanLength >= length * 0.05 && secScan.SpanLength <= length * 0.58)
            {
                LastBarWasActiveSecondary = true;
                return secScan;
            }
        }

        LastBarWasActiveSecondary = false;
        var priCounts = ProjectBarAxisCounts(frame, indicatorColor, tolerance, orientation, crossMargin);
        return ScanIndicatorCounts(priCounts, targetScan, minSliceMatches, minPixels);
    }

    /// <summary>
    /// Scans a 1D projection of per-column/per-row matching pixel counts for the Indicator (Player Bar).
    /// </summary>
    public BarScanResult ScanIndicatorCounts(
        ReadOnlySpan<int> counts,
        BarScanResult? targetScan = null,
        int minSliceMatches = 1,
        int minPixels = 1)
    {
        var length = counts.Length;
        if (length == 0) return BarScanResult.NotFound;

        // 1. Collect contiguous runs of active slices (bridging 1px internal dithering gaps).
        var rawClusters = new List<(int Start, int End, int Matches)>();
        int curStart = -1, curEnd = -1, curMatches = 0;

        for (var i = 0; i < length; i++)
        {
            if (counts[i] >= minSliceMatches)
            {
                if (curStart < 0) curStart = i;
                curEnd = i;
                curMatches += counts[i];
            }
            else if (curStart >= 0)
            {
                // Allow a 1px dither gap if the next slice is active
                if (i + 1 < length && counts[i + 1] >= minSliceMatches)
                    continue;

                rawClusters.Add((curStart, curEnd, curMatches));
                curStart = -1;
                curMatches = 0;
            }
        }

        if (curStart >= 0)
            rawClusters.Add((curStart, curEnd, curMatches));

        if (rawClusters.Count == 0)
            return BarScanResult.NotFound;

        // 2. Trim isolated 1–2px noise specks and narrow decorative end-cap brackets at the outer 3% edges of the track.
        var maxSubstantiveWidth = 0;
        for (var i = 0; i < rawClusters.Count; i++)
        {
            var w = rawClusters[i].End - rawClusters[i].Start + 1;
            if (w > maxSubstantiveWidth) maxSubstantiveWidth = w;
        }

        if (maxSubstantiveWidth >= 5 && rawClusters.Count > 1)
        {
            var edgeBracketMaxWidth = Math.Max(2, (int)Math.Ceiling(length * 0.04));
            var edgeZone = Math.Max(2, (int)Math.Ceiling(length * 0.03));
            var filtered = new List<(int Start, int End, int Matches)>(rawClusters.Count);

            for (var i = 0; i < rawClusters.Count; i++)
            {
                var c = rawClusters[i];
                var width = c.End - c.Start + 1;
                var distPrev = i > 0 ? c.Start - rawClusters[i - 1].End - 1 : int.MaxValue;
                var distNext = i + 1 < rawClusters.Count ? rawClusters[i + 1].Start - c.End - 1 : int.MaxValue;
                var minGap = Math.Min(distPrev, distNext);

                if (width <= 2 && minGap > 4)
                    continue; // isolated 1-2px noise speck

                // Decorative track end-cap bracket sitting at the extreme left/right edge separated from the real bar
                var isOuterEdgeBracket = (c.Start <= edgeZone || c.End >= length - 1 - edgeZone) &&
                                         width <= edgeBracketMaxWidth &&
                                         width < maxSubstantiveWidth * 0.35 &&
                                         minGap >= 3;
                if (isOuterEdgeBracket)
                    continue;

                filtered.Add(c);
            }

            if (filtered.Count > 0)
                rawClusters = filtered;
        }

        // 3. Bridge internal gaps caused by the target marker or internal arrow (up to 8% of track length, or target width + 6px),
        // without bridging across empty track or exceeding maximum valid bar span (55% of track length).
        var targetBridge = targetScan is { Found: true } ts ? (int)Math.Ceiling(ts.SpanLength) + 6 : 0;
        var maxBridgeGap = Math.Max(2, Math.Max(targetBridge, (int)Math.Ceiling(length * 0.08)));
        var maxCombinedBarSpan = Math.Max(12, (int)Math.Ceiling(length * 0.55));

        var merged = new List<(int Start, int End, int Matches)>(rawClusters.Count);
        var current = rawClusters[0];

        for (var i = 1; i < rawClusters.Count; i++)
        {
            var next = rawClusters[i];
            var gap = next.Start - current.End - 1;
            var combinedSpan = next.End - current.Start;
            var curWidth = current.End - current.Start + 1;
            var alreadyFullBar = _estimatedBarWidth > 0 && curWidth >= _estimatedBarWidth * 0.88 && gap >= 4;

            if (gap <= maxBridgeGap && combinedSpan <= maxCombinedBarSpan && !alreadyFullBar)
            {
                current = (current.Start, next.End, current.Matches + next.Matches);
            }
            else
            {
                merged.Add(current);
                current = next;
            }
        }
        merged.Add(current);

        // Pick the primary merged bar cluster (largest match count / span).
        var best = merged[0];
        for (var i = 1; i < merged.Count; i++)
        {
            var candidate = merged[i];
            if (candidate.Matches > best.Matches ||
                (candidate.Matches == best.Matches && (candidate.End - candidate.Start) > (best.End - best.Start)))
            {
                best = candidate;
            }
        }

        if (best.Matches < Math.Max(1, minPixels))
            return BarScanResult.NotFound;

        double minEdge = best.Start;
        double maxEdge = best.End;
        double spanLength = maxEdge - minEdge;

        // 4. Auto-Calibrated Bar Width Memory & Edge Occlusion Reconstruction.
        var minValidWidth = length * 0.08;
        var maxValidWidth = length * 0.45;
        var rawMidpoint = (minEdge + maxEdge) / 2.0;

        if (_estimatedBarWidth > 0 && spanLength < 0.85 * _estimatedBarWidth && targetScan is { Found: true } fish)
        {
            var distToMin = Math.Min(Math.Abs(fish.CenterPos - minEdge), Math.Abs(fish.MaxEdge - minEdge));
            var distToMax = Math.Min(Math.Abs(fish.CenterPos - maxEdge), Math.Abs(fish.MinEdge - maxEdge));
            var proximityLimit = Math.Max(12.0, _estimatedBarWidth * 0.65);

            // Full reconstruction when spanLength <= 0.70 * _estimatedBarWidth; smooth blend in [0.70, 0.85]
            var ratio = spanLength / _estimatedBarWidth;
            var reconWeight = ratio <= 0.70 ? 1.0 : Math.Clamp((0.85 - ratio) / 0.15, 0.0, 1.0);

            if (distToMin <= distToMax && distToMin <= proximityLimit)
            {
                // MinEdge (left/top) is occluded by the fish: reconstruct from unoccluded outer MaxEdge.
                var fullReconCenter = Math.Clamp(maxEdge - (_estimatedBarWidth / 2.0), 0.0, Math.Max(0.0, length - 1));
                var reconstructedCenter = (fullReconCenter * reconWeight) + (rawMidpoint * (1.0 - reconWeight));
                var reconstructedMin = Math.Max(0.0, maxEdge - _estimatedBarWidth);
                _lastIndicatorCenter = reconstructedCenter;
                return new BarScanResult(true, reconstructedCenter, reconstructedMin, maxEdge, spanLength, best.Matches);
            }

            if (distToMax < distToMin && distToMax <= proximityLimit)
            {
                // MaxEdge (right/bottom) is occluded by the fish: reconstruct from unoccluded outer MinEdge.
                var fullReconCenter = Math.Clamp(minEdge + (_estimatedBarWidth / 2.0), 0.0, Math.Max(0.0, length - 1));
                var reconstructedCenter = (fullReconCenter * reconWeight) + (rawMidpoint * (1.0 - reconWeight));
                var reconstructedMax = Math.Min(Math.Max(0.0, length - 1), minEdge + _estimatedBarWidth);
                _lastIndicatorCenter = reconstructedCenter;
                return new BarScanResult(true, reconstructedCenter, minEdge, reconstructedMax, spanLength, best.Matches);
            }
        }

        // Update auto-calibrated bar width memory when the span looks like a valid unoccluded bar width.
        if (spanLength >= minValidWidth && spanLength <= maxValidWidth)
        {
            if (_estimatedBarWidth <= 0)
                _estimatedBarWidth = Math.Clamp(spanLength, minValidWidth, maxValidWidth);
            else if (spanLength >= 0.70 * _estimatedBarWidth)
                _estimatedBarWidth = Math.Clamp(0.85 * _estimatedBarWidth + 0.15 * spanLength, minValidWidth, maxValidWidth);
        }

        // 5. Span midpoint (never a raw pixel average, which gets skewed when the fish sits on one side of the bar).
        _lastIndicatorCenter = rawMidpoint;
        return new BarScanResult(true, rawMidpoint, minEdge, maxEdge, spanLength, best.Matches);
    }

    /// <summary>
    /// Projects matching pixel counts onto the 1D active axis (<c>X</c> for Horizontal, <c>Y</c> for Vertical),
    /// optionally trimming <paramref name="crossMargin"/> border pixels from both sides of the cross-axis.
    /// </summary>
    public static int[] ProjectAxisCounts(
        BgraImage frame,
        RgbColor primaryColor,
        RgbColor? secondaryColor,
        int tolerance,
        BarOrientation orientation,
        int crossMargin = 0)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var tol = Math.Clamp(tolerance, 0, 255);
        var horizontal = orientation == BarOrientation.Horizontal;
        var length = horizontal ? frame.Width : frame.Height;
        var crossLength = horizontal ? frame.Height : frame.Width;
        var counts = new int[length];
        if (frame.Width <= 0 || frame.Height <= 0) return counts;

        var margin = Math.Clamp(crossMargin, 0, Math.Max(0, (crossLength - 1) / 2));
        var yStart = horizontal ? margin : 0;
        var yEnd = horizontal ? (frame.Height - margin) : frame.Height;
        var xStart = horizontal ? 0 : margin;
        var xEnd = horizontal ? frame.Width : (frame.Width - margin);

        var pixels = frame.Pixels;
        var stride = frame.Stride;
        var hasSecondary = secondaryColor.HasValue;
        var sec = secondaryColor.GetValueOrDefault();

        for (var y = yStart; y < yEnd; y++)
        {
            var rowOffset = y * stride;
            for (var x = xStart; x < xEnd; x++)
            {
                var idx = rowOffset + (x * 4);
                var b = pixels[idx];
                var g = pixels[idx + 1];
                var r = pixels[idx + 2];

                var matchesPrimary =
                    Math.Abs(r - primaryColor.R) <= tol &&
                    Math.Abs(g - primaryColor.G) <= tol &&
                    Math.Abs(b - primaryColor.B) <= tol;

                var matchesSecondary = hasSecondary &&
                    Math.Abs(r - sec.R) <= tol &&
                    Math.Abs(g - sec.G) <= tol &&
                    Math.Abs(b - sec.B) <= tol;

                if (matchesPrimary || matchesSecondary)
                    counts[horizontal ? x : y]++;
            }
        }

        return counts;
    }

    /// <summary>
    /// Projects player-bar matching pixel counts onto the 1D active axis with chromaticity-locked resilience
    /// so semi-transparent chromatic bars (e.g. a brown bar blended over bright 3D rocks vs. dark water) still match
    /// even when the user lowers <paramref name="tolerance"/>, while rejecting neutral grey background rocks/tracks.
    /// </summary>
    public static int[] ProjectBarAxisCounts(
        BgraImage frame,
        RgbColor barColor,
        int tolerance,
        BarOrientation orientation,
        int crossMargin = 0)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var tol = Math.Clamp(tolerance, 0, 255);
        var horizontal = orientation == BarOrientation.Horizontal;
        var length = horizontal ? frame.Width : frame.Height;
        var crossLength = horizontal ? frame.Height : frame.Width;
        var counts = new int[length];
        if (frame.Width <= 0 || frame.Height <= 0) return counts;

        var margin = Math.Clamp(crossMargin, 0, Math.Max(0, (crossLength - 1) / 2));
        var yStart = horizontal ? margin : 0;
        var yEnd = horizontal ? (frame.Height - margin) : frame.Height;
        var xStart = horizontal ? 0 : margin;
        var xEnd = horizontal ? frame.Width : (frame.Width - margin);

        var pixels = frame.Pixels;
        var stride = frame.Stride;

        for (var y = yStart; y < yEnd; y++)
        {
            var rowOffset = y * stride;
            for (var x = xStart; x < xEnd; x++)
            {
                var idx = rowOffset + (x * 4);
                var b = pixels[idx];
                var g = pixels[idx + 1];
                var r = pixels[idx + 2];

                if (MatchesBarColor(r, g, b, barColor, tol))
                    counts[horizontal ? x : y]++;
            }
        }

        return counts;
    }

    private static bool MatchesBarColor(byte r, byte g, byte b, RgbColor barColor, int tol)
    {
        if (Math.Abs(r - barColor.R) <= tol &&
            Math.Abs(g - barColor.G) <= tol &&
            Math.Abs(b - barColor.B) <= tol)
        {
            return true;
        }

        // Semi-transparent chromatic bar resilience: when a colored bar (chroma >= 14, e.g. #543630 brown)
        // moves across bright 3D rocks vs. dark water, its RGB shifts slightly in luminance while keeping its warm/cool hue ratios.
        var maxC = Math.Max(barColor.R, Math.Max(barColor.G, barColor.B));
        var minC = Math.Min(barColor.R, Math.Min(barColor.G, barColor.B));
        if (maxC - minC < 14)
            return false;

        var effTol = Math.Max(tol, 18);
        if (Math.Abs(r - barColor.R) > effTol ||
            Math.Abs(g - barColor.G) > effTol ||
            Math.Abs(b - barColor.B) > effTol)
        {
            return false;
        }

        var chromaTol = Math.Max(tol, 14);
        var rgDiff = Math.Abs((r - g) - (barColor.R - barColor.G));
        var rbDiff = Math.Abs((r - b) - (barColor.R - barColor.B));
        var gbDiff = Math.Abs((g - b) - (barColor.G - barColor.B));
        return rgDiff <= chromaTol && rbDiff <= chromaTol && gbDiff <= chromaTol;
    }

    /// <summary>
    /// Projects target pixel counts along the active axis while rejecting slices that are vertically sandwiched
    /// between player-bar pixels at the top and bottom of the interior cross-axis (eliminating internal bar decals
    /// such as <c>←</c> / <c>→</c> directional arrows).
    /// </summary>
    private static int[] ProjectTargetAxisCountsExcludingBarInteriorDecals(
        BgraImage frame,
        RgbColor markerColor,
        RgbColor? secondaryMarkerColor,
        int markerTolerance,
        RgbColor zoneColor,
        RgbColor? secondaryZoneColor,
        int zoneTolerance,
        BarOrientation orientation,
        int crossMargin)
    {
        var mTol = Math.Clamp(markerTolerance, 0, 255);
        var zTol = Math.Clamp(zoneTolerance, 0, 255);
        var horizontal = orientation == BarOrientation.Horizontal;
        var length = horizontal ? frame.Width : frame.Height;
        var crossLength = horizontal ? frame.Height : frame.Width;
        var counts = new int[length];
        if (frame.Width <= 0 || frame.Height <= 0) return counts;

        var margin = Math.Clamp(crossMargin, 0, Math.Max(0, (crossLength - 1) / 2));
        var cStart = margin;
        var cEnd = crossLength - margin;
        var interiorCross = cEnd - cStart;
        if (interiorCross <= 0) return counts;

        var edgeBand = Math.Max(1, Math.Min(3, interiorCross / 5));
        var pixels = frame.Pixels;
        var stride = frame.Stride;
        var hasSecMarker = secondaryMarkerColor.HasValue;
        var secM = secondaryMarkerColor.GetValueOrDefault();
        var hasSecZone = secondaryZoneColor.HasValue;
        var secZ = secondaryZoneColor.GetValueOrDefault();

        for (var i = 0; i < length; i++)
        {
            var targetCount = 0;
            var topBarCount = 0;
            var botBarCount = 0;

            for (var c = cStart; c < cEnd; c++)
            {
                var x = horizontal ? i : c;
                var y = horizontal ? c : i;
                var idx = (y * stride) + (x * 4);
                var b = pixels[idx];
                var g = pixels[idx + 1];
                var r = pixels[idx + 2];

                var isTarget =
                    (Math.Abs(r - markerColor.R) <= mTol && Math.Abs(g - markerColor.G) <= mTol && Math.Abs(b - markerColor.B) <= mTol) ||
                    (hasSecMarker && Math.Abs(r - secM.R) <= mTol && Math.Abs(g - secM.G) <= mTol && Math.Abs(b - secM.B) <= mTol);

                if (isTarget)
                    targetCount++;

                if (c < cStart + edgeBand || c >= cEnd - edgeBand)
                {
                    var isBar =
                        MatchesBarColor(r, g, b, zoneColor, zTol) ||
                        (hasSecZone && Math.Abs(r - secZ.R) <= zTol && Math.Abs(g - secZ.G) <= zTol && Math.Abs(b - secZ.B) <= zTol);

                    if (isBar && !isTarget)
                    {
                        if (c < cStart + edgeBand) topBarCount++;
                        else botBarCount++;
                    }
                }
            }

            // If both the top and bottom interior edges of this slice are filled with player-bar color while target pixels
            // only occupy part of the middle (< 75% of interiorCross), this slice is an internal bar arrow/decal (← / →)!
            var isSandwichedDecal = topBarCount > 0 && botBarCount > 0 && targetCount < (int)Math.Ceiling(interiorCross * 0.75);
            counts[i] = isSandwichedDecal ? 0 : targetCount;
        }

        return counts;
    }
}
