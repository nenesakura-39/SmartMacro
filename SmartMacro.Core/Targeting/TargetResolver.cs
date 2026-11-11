using System.Diagnostics;
using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Vision;
using SmartMacro.Core.Workflow;

namespace SmartMacro.Core.Targeting;

/// <summary>
/// Diagnostic outcome of testing a single locator tier (used by playback logs and the Inspector's "Locate Target" button).
/// </summary>
public sealed record StrategyDiagnostic(
    LocatorStrategyKind Strategy,
    bool Succeeded,
    int? X,
    int? Y,
    ScreenRect Bounds,
    double Confidence,
    double ElapsedMs,
    string Summary);

/// <summary>
/// Final result of resolving where a <see cref="MouseClickStep"/> should click, including the winning strategy and per-tier diagnostics.
/// </summary>
public sealed record TargetResolution(
    LocatorStrategyKind WinningStrategy,
    int X,
    int Y,
    ScreenRect Bounds,
    double Confidence,
    double TotalElapsedMs,
    string Summary,
    IReadOnlyList<StrategyDiagnostic> Diagnostics);

/// <summary>
/// Resolves a recorded <see cref="MouseClickStep"/> into live screen coordinates using a ranked 5-tier fallback chain:
/// <list type="number">
/// <item><b>UI Automation</b> — exact accessibility control by Name / AutomationId / ControlType.</item>
/// <item><b>OCR Text</b> — visible text near the click via Windows OCR + recorded offset.</item>
/// <item><b>Image Snapshot</b> — visual thumbnail match (1:1 fast path, then multi-scale 75%–150%).</item>
/// <item><b>Window-Relative</b> — proportional position inside the target app window.</item>
/// <item><b>Coordinates</b> — recorded physical (X, Y) fallback.</item>
/// </list>
/// </summary>
public sealed class TargetResolver
{
    private const int LocalOcrWidth = 640;
    private const int LocalOcrHeight = 360;
    private const int MinIntentSearchRadiusPx = 160;
    private const int MaxFullWindowPixelArea = 3840 * 2160;

    private readonly IUiElementLocator? _uiLocator;
    private readonly IOcrService? _ocr;
    private readonly IScreenCapture? _capture;
    private readonly IScreenLayout? _layout;

    public TargetResolver(
        IUiElementLocator? uiLocator = null,
        IOcrService? ocr = null,
        IScreenCapture? capture = null,
        IScreenLayout? layout = null)
    {
        _uiLocator = uiLocator;
        _ocr = ocr;
        _capture = capture;
        _layout = layout;
    }

    /// <summary>
    /// Resolves the best live click point for <paramref name="step"/>, stopping as soon as the highest-priority tier succeeds.
    /// </summary>
    public Task<TargetResolution> ResolveAsync(MouseClickStep step, CancellationToken cancellationToken = default) =>
        RunChainAsync(step, evaluateAllTiers: false, cancellationToken);

    /// <summary>
    /// Evaluates all 5 tiers without short-circuiting so the Inspector's "🧭 Locate Target" button can show ✓/✗ for every strategy.
    /// </summary>
    public Task<TargetResolution> DiagnoseAllAsync(MouseClickStep step, CancellationToken cancellationToken = default) =>
        RunChainAsync(step, evaluateAllTiers: true, cancellationToken);

    private async Task<TargetResolution> RunChainAsync(
        MouseClickStep step,
        bool evaluateAllTiers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(step);
        var totalWatch = Stopwatch.StartNew();
        var diagnostics = new List<StrategyDiagnostic>(5);
        var target = step.Target;

        var liveWindow = FindLiveWindow(target, _layout, step.X, step.Y);
        var (expectedX, expectedY, scaleX, scaleY) = ComputeExpectedPoint(step, target, liveWindow);
        StrategyDiagnostic? winner = null;

        // Tier 1: UI Automation
        var uiDiag = await TryUiAutomationAsync(target, liveWindow, (expectedX, expectedY), cancellationToken).ConfigureAwait(false);
        diagnostics.Add(uiDiag);
        if (uiDiag.Succeeded)
        {
            winner = uiDiag;
            if (!evaluateAllTiers) return BuildResolution(winner, totalWatch.Elapsed.TotalMilliseconds, diagnostics);
        }

        // Tier 2: OCR Text
        var ocrDiag = await TryOcrTextAsync(target, liveWindow, (expectedX, expectedY), scaleX, scaleY, cancellationToken).ConfigureAwait(false);
        diagnostics.Add(ocrDiag);
        if (ocrDiag.Succeeded && winner is null)
        {
            winner = ocrDiag;
            if (!evaluateAllTiers) return BuildResolution(winner, totalWatch.Elapsed.TotalMilliseconds, diagnostics);
        }

        // Tier 3: Image Snapshot (1:1 fast path + multi-scale fallback)
        var imageDiag = await TryImageSnapshotAsync(step, liveWindow, (expectedX, expectedY), cancellationToken).ConfigureAwait(false);
        diagnostics.Add(imageDiag);
        if (imageDiag.Succeeded && winner is null)
        {
            winner = imageDiag;
            if (!evaluateAllTiers) return BuildResolution(winner, totalWatch.Elapsed.TotalMilliseconds, diagnostics);
        }

        // Tier 4: Window-Relative
        var windowDiag = TryWindowRelative(target, liveWindow, expectedX, expectedY);
        diagnostics.Add(windowDiag);
        if (windowDiag.Succeeded && winner is null)
        {
            winner = windowDiag;
            if (!evaluateAllTiers) return BuildResolution(winner, totalWatch.Elapsed.TotalMilliseconds, diagnostics);
        }

        // Tier 5: Raw Coordinates fallback
        var coordBounds = new ScreenRect(step.X - 8, step.Y - 8, 17, 17);
        var coordDiag = new StrategyDiagnostic(
            LocatorStrategyKind.Coordinates,
            Succeeded: true,
            step.X,
            step.Y,
            coordBounds,
            Confidence: 1.0,
            ElapsedMs: 0,
            Summary: $"Coordinates ({step.X}, {step.Y})");
        diagnostics.Add(coordDiag);
        winner ??= coordDiag;

        return BuildResolution(winner, totalWatch.Elapsed.TotalMilliseconds, diagnostics);
    }

    private static TargetResolution BuildResolution(
        StrategyDiagnostic winner,
        double totalElapsedMs,
        IReadOnlyList<StrategyDiagnostic> diagnostics) =>
        new(
            winner.Strategy,
            winner.X ?? 0,
            winner.Y ?? 0,
            winner.Bounds,
            winner.Confidence,
            totalElapsedMs,
            winner.Summary,
            diagnostics);

    private async Task<StrategyDiagnostic> TryUiAutomationAsync(
        TargetFingerprint? target,
        WindowInfo? liveWindow,
        (int X, int Y) expectedPoint,
        CancellationToken cancellationToken)
    {
        if (target is not { HasUiElement: true })
        {
            return Miss(LocatorStrategyKind.UiAutomation, 0, "No UI element metadata captured for this click.");
        }

        if (_uiLocator is not { IsAvailable: true })
        {
            return Miss(LocatorStrategyKind.UiAutomation, 0, "UI Automation service is unavailable.");
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var match = await _uiLocator.FindAsync(target, liveWindow?.Bounds, expectedPoint, cancellationToken).ConfigureAwait(false);
            var ms = sw.Elapsed.TotalMilliseconds;
            if (match is null || match.Bounds.IsEmpty)
            {
                var label = !string.IsNullOrWhiteSpace(target.ElementName) ? $"\"{target.ElementName}\"" : $"#{target.AutomationId}";
                return Miss(LocatorStrategyKind.UiAutomation, ms, $"UI element {label} not found.");
            }

            var normX = Math.Clamp(target.ElementNormalizedX ?? 0.5, 0.05, 0.95);
            var normY = Math.Clamp(target.ElementNormalizedY ?? 0.5, 0.05, 0.95);
            var clickX = match.Bounds.X + (int)Math.Round(normX * Math.Max(0, match.Bounds.Width - 1));
            var clickY = match.Bounds.Y + (int)Math.Round(normY * Math.Max(0, match.Bounds.Height - 1));

            return new StrategyDiagnostic(
                LocatorStrategyKind.UiAutomation,
                Succeeded: true,
                clickX,
                clickY,
                match.Bounds,
                match.Confidence,
                ms,
                $"UI element {match.MatchedBy} at ({clickX}, {clickY})");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Miss(LocatorStrategyKind.UiAutomation, sw.Elapsed.TotalMilliseconds, $"UI Automation skipped ({ex.Message}).");
        }
    }

    private async Task<StrategyDiagnostic> TryOcrTextAsync(
        TargetFingerprint? target,
        WindowInfo? liveWindow,
        (int X, int Y) expectedPoint,
        double scaleX,
        double scaleY,
        CancellationToken cancellationToken)
    {
        if (target is not { HasOcrText: true } || string.IsNullOrWhiteSpace(target.NearbyText))
        {
            return Miss(LocatorStrategyKind.OcrText, 0, "No nearby visible text captured for this click.");
        }

        if (_ocr is not { IsAvailable: true })
        {
            return Miss(LocatorStrategyKind.OcrText, 0, "Windows OCR is unavailable.");
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var virtualScreen = _ocr.GetVirtualScreen();
            if (virtualScreen.IsEmpty)
                return Miss(LocatorStrategyKind.OcrText, sw.Elapsed.TotalMilliseconds, "Virtual screen is empty.");

            var query = target.NearbyText.Trim();
            var scaledOffsetX = (int)Math.Round(target.NearbyTextOffsetX * scaleX);
            var scaledOffsetY = (int)Math.Round(target.NearbyTextOffsetY * scaleY);
            var expectedTextCenter = (X: expectedPoint.X - scaledOffsetX, Y: expectedPoint.Y - scaledOffsetY);

            var localBox = new ScreenRect(
                expectedTextCenter.X - LocalOcrWidth / 2,
                expectedTextCenter.Y - LocalOcrHeight / 2,
                LocalOcrWidth,
                LocalOcrHeight);
            if (liveWindow is { Bounds.IsEmpty: false })
            {
                var inWin = localBox.Intersect(liveWindow.Bounds);
                if (!inWin.IsEmpty) localBox = inWin;
            }

            localBox = localBox.Intersect(virtualScreen);
            TextMatch? best = null;
            if (!localBox.IsEmpty)
            {
                best = await FindBestOcrMatchAsync(localBox, query, expectedTextCenter, cancellationToken).ConfigureAwait(false);
            }

            if (best is null && liveWindow is { Bounds.IsEmpty: false })
            {
                var winBox = liveWindow.Bounds.Intersect(virtualScreen);
                if (!winBox.IsEmpty && winBox != localBox && (long)winBox.Width * winBox.Height <= MaxFullWindowPixelArea)
                {
                    best = await FindBestOcrMatchAsync(winBox, query, expectedTextCenter, cancellationToken).ConfigureAwait(false);
                }
            }

            var ms = sw.Elapsed.TotalMilliseconds;
            if (best is null)
            {
                return Miss(LocatorStrategyKind.OcrText, ms, $"OCR text \"{query}\" not found.");
            }

            var clickX = best.Bounds.CenterX + scaledOffsetX;
            var clickY = best.Bounds.CenterY + scaledOffsetY;
            return new StrategyDiagnostic(
                LocatorStrategyKind.OcrText,
                Succeeded: true,
                clickX,
                clickY,
                best.Bounds,
                best.Similarity,
                ms,
                $"OCR text \"{best.Text}\" ({best.Similarity * 100:0}%) at ({clickX}, {clickY})");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Miss(LocatorStrategyKind.OcrText, sw.Elapsed.TotalMilliseconds, $"OCR search skipped ({ex.Message}).");
        }
    }

    private async Task<TextMatch?> FindBestOcrMatchAsync(
        ScreenRect region,
        string query,
        (int X, int Y) expectedTextCenter,
        CancellationToken cancellationToken)
    {
        var read = await _ocr!.ReadAsync(region, cancellationToken).ConfigureAwait(false);
        var matches = OcrTextMatcher.Find(
            read,
            query,
            TextMatchMode.Contains,
            caseSensitive: false,
            minSimilarity: 0.80,
            normalizeLookAlikes: true);
        if (matches.Count == 0) return null;

        return matches
            .OrderByDescending(m => Math.Round(m.Similarity, 2))
            .ThenBy(m => DistanceSquared(m.Bounds.CenterX, m.Bounds.CenterY, expectedTextCenter.X, expectedTextCenter.Y))
            .First();
    }

    private async Task<StrategyDiagnostic> TryImageSnapshotAsync(
        MouseClickStep step,
        WindowInfo? liveWindow,
        (int X, int Y) expectedPoint,
        CancellationToken cancellationToken)
    {
        if (!step.HasTargetThumbnail)
        {
            return Miss(LocatorStrategyKind.ImageSnapshot, 0, "No click thumbnail stored on this step.");
        }

        if (_capture is null)
        {
            return Miss(LocatorStrategyKind.ImageSnapshot, 0, "Screen capture is unavailable.");
        }

        var template = SmartClickConversion.TryDecodeThumbnail(step, out var decodeError);
        if (template is null)
        {
            return Miss(LocatorStrategyKind.ImageSnapshot, 0, decodeError ?? "Click thumbnail could not be decoded.");
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var virtualScreen = _capture.GetVirtualScreen();
            if (virtualScreen.IsEmpty)
                return Miss(LocatorStrategyKind.ImageSnapshot, sw.Elapsed.TotalMilliseconds, "Virtual screen is empty.");

            var (anchorX, anchorY) = SmartClickConversion.Anchor(step, template.Width, template.Height);
            var radius = Math.Max(SmartAim.RadiusOf(step), MinIntentSearchRadiusPx);
            var expectedTopLeftX = expectedPoint.X - anchorX;
            var expectedTopLeftY = expectedPoint.Y - anchorY;

            var localArea = new ScreenRect(
                expectedTopLeftX - radius,
                expectedTopLeftY - radius,
                template.Width + 2 * radius,
                template.Height + 2 * radius).Intersect(virtualScreen);

            var minScore = Math.Clamp(step.SmartAimAccuracy, 0.5, 1.0);
            double bestSeen = 0;

            if (!localArea.IsEmpty)
            {
                var frame = await _capture.CaptureAsync(localArea, cancellationToken).ConfigureAwait(false);
                var (hit, score) = MatchTemplateInFrame(frame, template, minScore, expectedTopLeftX, expectedTopLeftY, anchorX, anchorY, cancellationToken);
                bestSeen = Math.Max(bestSeen, score);
                if (hit is not null)
                {
                    return hit with { ElapsedMs = sw.Elapsed.TotalMilliseconds };
                }
            }

            if (liveWindow is { Bounds.IsEmpty: false })
            {
                var winArea = liveWindow.Bounds.Intersect(virtualScreen);
                if (!winArea.IsEmpty && winArea != localArea && (long)winArea.Width * winArea.Height <= MaxFullWindowPixelArea)
                {
                    var winFrame = await _capture.CaptureAsync(winArea, cancellationToken).ConfigureAwait(false);
                    var (hit, score) = MatchTemplateInFrame(winFrame, template, minScore, expectedTopLeftX, expectedTopLeftY, anchorX, anchorY, cancellationToken);
                    bestSeen = Math.Max(bestSeen, score);
                    if (hit is not null)
                    {
                        return hit with { ElapsedMs = sw.Elapsed.TotalMilliseconds };
                    }
                }
            }

            return Miss(
                LocatorStrategyKind.ImageSnapshot,
                sw.Elapsed.TotalMilliseconds,
                $"Image snapshot not found (best {bestSeen * 100:0}%, needed {minScore * 100:0}%).");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Miss(LocatorStrategyKind.ImageSnapshot, sw.Elapsed.TotalMilliseconds, $"Image snapshot skipped ({ex.Message}).");
        }
    }

    private static (StrategyDiagnostic? Hit, double BestScore) MatchTemplateInFrame(
        ScreenFrame frame,
        BgraImage template,
        double minScore,
        int expectedTopLeftX,
        int expectedTopLeftY,
        int anchorX,
        int anchorY,
        CancellationToken cancellationToken)
    {
        double bestScore = 0;

        // 1:1 fast check first.
        if (frame.Image.Width >= template.Width && frame.Image.Height >= template.Height)
        {
            var exact = TemplateMatcher.Find(
                frame.Image,
                template,
                new TemplateMatchOptions { MinScore = minScore, MaxResults = 10 },
                cancellationToken);
            bestScore = Math.Max(bestScore, exact.BestScore);
            if (exact.Matches.Count > 0)
            {
                return (PickNearestVisualMatch(frame, exact.Matches, expectedTopLeftX, expectedTopLeftY, anchorX, anchorY), bestScore);
            }
        }

        // Multi-scale fallback (75%–150%) on the already-captured frame (zero extra screen capture).
        var minScaledW = Math.Max(1, (int)Math.Round(template.Width * 0.75));
        var minScaledH = Math.Max(1, (int)Math.Round(template.Height * 0.75));
        if (frame.Image.Width >= minScaledW && frame.Image.Height >= minScaledH)
        {
            var scaled = TemplateMatcher.Find(
                frame.Image,
                template,
                new TemplateMatchOptions { MinScore = minScore, MaxResults = 10, AllowMultiScale = true },
                cancellationToken);
            bestScore = Math.Max(bestScore, scaled.BestScore);
            if (scaled.Matches.Count > 0)
            {
                return (PickNearestVisualMatch(frame, scaled.Matches, expectedTopLeftX, expectedTopLeftY, anchorX, anchorY), bestScore);
            }
        }

        return (null, bestScore);
    }

    private static StrategyDiagnostic PickNearestVisualMatch(
        ScreenFrame frame,
        IReadOnlyList<VisualMatch> matches,
        int expectedTopLeftX,
        int expectedTopLeftY,
        int anchorX,
        int anchorY)
    {
        var best = matches
            .OrderBy(m =>
            {
                var screen = frame.ToScreen(m.Bounds);
                return DistanceSquared(screen.X, screen.Y, expectedTopLeftX, expectedTopLeftY);
            })
            .First();

        var found = frame.ToScreen(best.Bounds);
        var scale = best.MatchedScale > 0 ? best.MatchedScale : 1.0;
        var clickX = found.X + (int)Math.Round(anchorX * scale);
        var clickY = found.Y + (int)Math.Round(anchorY * scale);
        var scaleSuffix = Math.Abs(scale - 1.0) >= 0.02 ? $" at {scale * 100:0}% scale" : string.Empty;

        return new StrategyDiagnostic(
            LocatorStrategyKind.ImageSnapshot,
            Succeeded: true,
            clickX,
            clickY,
            found,
            best.Score,
            ElapsedMs: 0,
            $"Image snapshot ({best.Score * 100:0}%{scaleSuffix}) at ({clickX}, {clickY})");
    }

    private static StrategyDiagnostic TryWindowRelative(
        TargetFingerprint? target,
        WindowInfo? liveWindow,
        int expectedX,
        int expectedY)
    {
        if (target is not { HasWindowAnchor: true })
        {
            return Miss(LocatorStrategyKind.WindowRelative, 0, "No target window anchor captured for this click.");
        }

        if (liveWindow is null || liveWindow.IsMinimized || liveWindow.Bounds.IsEmpty)
        {
            var name = !string.IsNullOrWhiteSpace(target.WindowTitle) ? $"\"{target.WindowTitle}\"" : target.WindowProcessName;
            return Miss(LocatorStrategyKind.WindowRelative, 0, $"Target window {name} isn't open or visible.");
        }

        var bounds = new ScreenRect(expectedX - 10, expectedY - 10, 21, 21);
        var winLabel = !string.IsNullOrWhiteSpace(liveWindow.Title) ? $"\"{liveWindow.Title}\"" : liveWindow.ProcessName;
        return new StrategyDiagnostic(
            LocatorStrategyKind.WindowRelative,
            Succeeded: true,
            expectedX,
            expectedY,
            bounds,
            Confidence: 0.75,
            ElapsedMs: 0,
            $"Window-relative in {winLabel} at ({expectedX}, {expectedY})");
    }

    /// <summary>
    /// Finds the best live top-level window matching <paramref name="target"/> by title and/or process name.
    /// When multiple windows share the same title/process (e.g. two Roblox windows side-by-side), prefers the window
    /// that contains or is closest to <paramref name="preferredX"/>, <paramref name="preferredY"/>.
    /// </summary>
    public static WindowInfo? FindLiveWindow(
        TargetFingerprint? target,
        IScreenLayout? layout,
        int? preferredX = null,
        int? preferredY = null)
    {
        if (target is null || layout is null) return null;
        var wantedTitle = target.WindowTitle?.Trim() ?? string.Empty;
        var wantedProc = NormalizeProcessName(target.WindowProcessName);
        if (wantedTitle.Length == 0 && wantedProc.Length == 0) return null;

        var windows = layout.GetWindows();
        WindowInfo? best = null;
        var bestRank = int.MaxValue;
        var bestContains = false;
        var bestDistanceSq = long.MaxValue;

        foreach (var w in windows)
        {
            if (w.IsMinimized || w.Bounds.IsEmpty) continue;
            var title = w.Title?.Trim() ?? string.Empty;
            var proc = NormalizeProcessName(w.ProcessName);

            var exactTitle = wantedTitle.Length > 0 && title.Length > 0 && string.Equals(title, wantedTitle, StringComparison.OrdinalIgnoreCase);
            var partialTitle = wantedTitle.Length > 0 && title.Length > 0
                && (title.Contains(wantedTitle, StringComparison.OrdinalIgnoreCase)
                    || wantedTitle.Contains(title, StringComparison.OrdinalIgnoreCase));
            var exactProc = wantedProc.Length > 0 && proc.Length > 0 && string.Equals(proc, wantedProc, StringComparison.OrdinalIgnoreCase);

            var rank = (exactProc, exactTitle, partialTitle) switch
            {
                (true, true, _) => 0,
                (true, false, true) => 1,
                (false, true, _) when wantedProc.Length == 0 => 2,
                (true, false, false) => 3,
                (false, true, _) => 4,
                (false, false, true) when wantedProc.Length == 0 => 5,
                _ => int.MaxValue,
            };

            if (rank == int.MaxValue || rank > bestRank) continue;

            var contains = false;
            var distSq = long.MaxValue;
            if (preferredX is int px && preferredY is int py)
            {
                contains = w.Bounds.Contains(px, py);
                distSq = DistanceSquared(w.Bounds.CenterX, w.Bounds.CenterY, px, py);
            }

            var isBetter = rank < bestRank
                || (preferredX is not null && preferredY is not null
                    && ((contains && !bestContains) || (contains == bestContains && distSq < bestDistanceSq)));

            if (isBetter)
            {
                bestRank = rank;
                best = w;
                bestContains = contains;
                bestDistanceSq = distSq;
                if (rank == 0 && (preferredX is null || preferredY is null)) break;
            }
        }

        return best;
    }

    private static (int X, int Y, double ScaleX, double ScaleY) ComputeExpectedPoint(
        MouseClickStep step,
        TargetFingerprint? target,
        WindowInfo? liveWindow)
    {
        if (target is not { HasWindowAnchor: true } || liveWindow is null || liveWindow.Bounds.IsEmpty)
        {
            return (step.X, step.Y, 1.0, 1.0);
        }

        var scaleX = target.WindowWidth > 0 ? (double)liveWindow.Bounds.Width / target.WindowWidth : 1.0;
        var scaleY = target.WindowHeight > 0 ? (double)liveWindow.Bounds.Height / target.WindowHeight : 1.0;
        var x = liveWindow.Bounds.X + (int)Math.Round(target.WindowRelativeX!.Value * scaleX);
        var y = liveWindow.Bounds.Y + (int)Math.Round(target.WindowRelativeY!.Value * scaleY);
        return (x, y, scaleX, scaleY);
    }

    private static string NormalizeProcessName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var trimmed = name.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? trimmed[..^4] : trimmed;
    }

    private static long DistanceSquared(int x1, int y1, int x2, int y2)
    {
        long dx = x1 - x2;
        long dy = y1 - y2;
        return dx * dx + dy * dy;
    }

    private static StrategyDiagnostic Miss(LocatorStrategyKind strategy, double elapsedMs, string summary) =>
        new(strategy, Succeeded: false, X: null, Y: null, Bounds: default, Confidence: 0, elapsedMs, summary);
}
