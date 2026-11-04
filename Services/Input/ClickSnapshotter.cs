using System.Collections.Concurrent;
using System.Diagnostics;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Recording;
using SmartMacro.Core.Targeting;
using SmartMacro.Core.Vision;
using SmartMacro.Services.Ocr;
using SmartMacro.Services.Vision;

namespace SmartMacro.Services.Input;

/// <summary>
/// Smart Click Snapshots + Intent Recording while recording: when a mouse button goes down (with a visible, free cursor),
/// grabs a tiny GDI pixel region (well under a millisecond) on the input thread, then performs PNG encoding, Windows UI
/// Automation inspection, and pre-click OCR strictly off-thread on a background worker queue.
/// Never blocks the mouse hook and never throws: a failed capture or lookup simply leaves that signal empty.
/// </summary>
internal sealed class ClickSnapshotter
{
    /// <summary>A press this soon after the last snapshot …</summary>
    public static readonly TimeSpan ReuseWithin = TimeSpan.FromMilliseconds(60);

    /// <summary>… and at most this far away (px each way) reuses that snapshot.</summary>
    public const int ReuseDistancePx = 8;

    private const int OcrCropWidth = 160;
    private const int OcrCropHeight = 64;
    private static readonly TimeSpan OcrEnrichTimeout = TimeSpan.FromMilliseconds(180);

    private readonly record struct EnrichJob(
        ClickTargetSnapshot Snapshot,
        int X,
        int Y,
        ScreenFrame? OcrFrame,
        bool EncodePng);

    private readonly ConcurrentQueue<EnrichJob> _jobs = new();
    private readonly object _workerGate = new();
    private Task _workerTask = Task.CompletedTask;

    private ScreenRect _virtualScreen;
    private int _count;
    private ClickTargetSnapshot? _last;
    private int _lastX;
    private int _lastY;
    private long _lastTimestamp;

    /// <summary>Turned off by Settings → "Smart Click Snapshots".</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Turned off by Settings → "Intent Recording".</summary>
    public bool CaptureIntent { get; set; } = true;

    public IUiElementLocator? UiLocator { get; set; }

    public WindowsOcrService? OcrService { get; set; }

    /// <summary>How many click targets this recording kept so far.</summary>
    public int Count => _count;

    /// <summary>Call when a recording starts (on the UI thread): reads the screen layout and resets the count.</summary>
    public void Reset()
    {
        _count = 0;
        _last = null;
        while (_jobs.TryDequeue(out _)) { }
        try
        {
            _virtualScreen = new GdiScreenCapture().GetVirtualScreen();
        }
        catch (Exception)
        {
            _virtualScreen = default;
        }
    }

    /// <summary>The snapshot + async Intent placeholder around (x, y), or null (turned off, limit reached, capture failed).</summary>
    public ClickTargetSnapshot? TryCapture(int x, int y)
    {
        var wantThumb = Enabled;
        var wantIntent = CaptureIntent && (UiLocator is not null || OcrService is not null);
        if ((!wantThumb && !wantIntent) || _count >= ClickSnapshots.MaxPerRecording || _virtualScreen.IsEmpty)
            return null;

        var now = Stopwatch.GetTimestamp();
        if (_last is { } recent && Stopwatch.GetElapsedTime(_lastTimestamp, now) < ReuseWithin
            && Math.Abs(x - _lastX) <= ReuseDistancePx && Math.Abs(y - _lastY) <= ReuseDistancePx)
        {
            _count++;
            return recent;
        }

        try
        {
            // Privacy mode (thumbnails disabled, Intent enabled): zero pixel capture, inspect UIA + window off-thread only.
            if (!wantThumb)
            {
                var intentOnly = ClickTargetSnapshot.FromIntentOnly();
                EnqueueEnrichment(new EnrichJob(intentOnly, x, y, OcrFrame: null, EncodePng: false));
                _count++;
                (_last, _lastX, _lastY, _lastTimestamp) = (intentOnly, x, y, now);
                return intentOnly;
            }

            // Thumbnails only (Intent disabled): exact 56×56 fast path.
            if (!wantIntent)
            {
                var rect = ClickSnapshots.RectAround(x, y, _virtualScreen);
                if (rect.IsEmpty) return null;
                var pixels = ScreenCapture.CaptureBgra(rect, shield: false);
                var snapshot = ClickSnapshots.Create(new BgraImage(rect.Width, rect.Height, pixels), rect, x, y);
                if (snapshot is null) return null;
                snapshot.EncodeInBackground();
                _count++;
                (_last, _lastX, _lastY, _lastTimestamp) = (snapshot, x, y, now);
                return snapshot;
            }

            // Both Thumbnails and Intent enabled: one single 160×64 GDI BitBlt, crop 56×56 in RAM, enrich off-thread.
            var thumbRect = ClickSnapshots.RectAround(x, y, _virtualScreen);
            if (thumbRect.IsEmpty) return null;

            var wideRect = RectAroundBox(x, y, _virtualScreen, OcrCropWidth, OcrCropHeight).Union(thumbRect).Intersect(_virtualScreen);
            var widePixels = ScreenCapture.CaptureBgra(wideRect, shield: false);
            var wideImage = new BgraImage(wideRect.Width, wideRect.Height, widePixels);
            var thumbImage = CropSubImage(wideImage, wideRect, thumbRect);
            var fullSnapshot = ClickSnapshots.Create(thumbImage, thumbRect, x, y);
            if (fullSnapshot is null) return null;

            var ocrFrame = OcrService is { IsAvailable: true } ? new ScreenFrame(wideRect, wideImage) : null;
            EnqueueEnrichment(new EnrichJob(fullSnapshot, x, y, ocrFrame, EncodePng: true));
            _count++;
            (_last, _lastX, _lastY, _lastTimestamp) = (fullSnapshot, x, y, now);
            return fullSnapshot;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Waits briefly after recording stops (once input hooks are already uninstalled) so the final click's background
    /// UIA/OCR enrichment finishes before steps are built.
    /// </summary>
    public void WaitForPendingEnrichment(TimeSpan timeout)
    {
        Task pending;
        lock (_workerGate) pending = _workerTask;
        if (pending.IsCompleted) return;
        try
        {
            pending.Wait(timeout);
        }
        catch
        {
            // Timed out or faulted: keep whatever signals already finished.
        }
    }

    private void EnqueueEnrichment(EnrichJob job)
    {
        _jobs.Enqueue(job);
        lock (_workerGate)
        {
            if (_workerTask.IsCompleted)
            {
                _workerTask = Task.Run(DrainJobsAsync);
            }
        }
    }

    private async Task DrainJobsAsync()
    {
        while (_jobs.TryDequeue(out var job))
        {
            try
            {
                if (job.EncodePng)
                {
                    _ = job.Snapshot.Png;
                }

                await EnrichFingerprintAsync(job).ConfigureAwait(false);
            }
            catch
            {
                // Background enrichment is strictly best-effort.
            }
        }
    }

    private async Task EnrichFingerprintAsync(EnrichJob job)
    {
        var fp = new TargetFingerprint();

        if (UiLocator is { IsAvailable: true } ui && ui.InspectPoint(job.X, job.Y) is { } snap)
        {
            if (!string.IsNullOrWhiteSpace(snap.ElementName) || !string.IsNullOrWhiteSpace(snap.AutomationId))
            {
                fp.ElementName = snap.ElementName;
                fp.AutomationId = snap.AutomationId;
                fp.ControlType = snap.ControlType;
                fp.ClassName = snap.ClassName;

                if (!snap.ElementBounds.IsEmpty)
                {
                    fp.ElementNormalizedX = snap.ElementBounds.Width > 1
                        ? Math.Clamp((double)(job.X - snap.ElementBounds.X) / (snap.ElementBounds.Width - 1), 0.05, 0.95)
                        : 0.5;
                    fp.ElementNormalizedY = snap.ElementBounds.Height > 1
                        ? Math.Clamp((double)(job.Y - snap.ElementBounds.Y) / (snap.ElementBounds.Height - 1), 0.05, 0.95)
                        : 0.5;
                }
            }

            if (!snap.WindowBounds.IsEmpty
                && (!string.IsNullOrWhiteSpace(snap.WindowTitle) || !string.IsNullOrWhiteSpace(snap.WindowProcessName)))
            {
                fp.WindowTitle = snap.WindowTitle;
                fp.WindowProcessName = snap.WindowProcessName;
                fp.WindowRelativeX = job.X - snap.WindowBounds.X;
                fp.WindowRelativeY = job.Y - snap.WindowBounds.Y;
                fp.WindowWidth = snap.WindowBounds.Width;
                fp.WindowHeight = snap.WindowBounds.Height;
            }
        }

        // Only run pre-click OCR when UIA didn't already give us an accessible ElementName (saves CPU on standard apps,
        // while catching custom game/canvas/image buttons!).
        if (string.IsNullOrWhiteSpace(fp.ElementName)
            && job.OcrFrame is { } ocrFrame
            && OcrService is { IsAvailable: true } ocr)
        {
            try
            {
                var read = await ocr.ReadImageAsync(ocrFrame).WaitAsync(OcrEnrichTimeout).ConfigureAwait(false);
                if (PickNearestOcrLabel(read, job.X, job.Y) is { } match)
                {
                    fp.NearbyText = match.Text;
                    fp.NearbyTextOffsetX = job.X - match.Bounds.CenterX;
                    fp.NearbyTextOffsetY = job.Y - match.Bounds.CenterY;
                }
            }
            catch
            {
                // OCR timed out or failed: keep UIA / window signals.
            }
        }

        if (fp.HasAnySignal)
        {
            job.Snapshot.Fingerprint = fp;
        }
    }

    private static (string Text, ScreenRect Bounds)? PickNearestOcrLabel(OcrReadResult read, int clickX, int clickY)
    {
        (string Text, ScreenRect Bounds)? best = null;
        var bestScore = double.MaxValue;

        foreach (var line in read.Lines)
        {
            // Check both individual meaningful words and short 2–3 word button phrases on the same line.
            var words = line.Words.Where(w => HasMeaningfulCharacters(w.Text)).ToList();
            if (words.Count == 0) continue;

            if (words.Count <= 3)
            {
                var phraseBounds = words.Select(w => w.Bounds).Aggregate((a, b) => a.Union(b));
                var phraseText = string.Join(" ", words.Select(w => w.Text.Trim()));
                if (phraseText.Length <= 40)
                {
                    Consider(phraseText, phraseBounds);
                }
            }

            foreach (var word in words)
            {
                Consider(word.Text.Trim(), word.Bounds);
            }
        }

        return best;

        void Consider(string text, ScreenRect bounds)
        {
            if (bounds.IsEmpty || text.Length == 0) return;
            var dx = clickX < bounds.X ? bounds.X - clickX : clickX > bounds.Right ? clickX - bounds.Right : 0;
            var dy = clickY < bounds.Y ? bounds.Y - clickY : clickY > bounds.Bottom ? clickY - bounds.Bottom : 0;
            if (dx > 64 || dy > 24) return;

            // Strongly prefer labels whose bounding box directly contains the click, and prefer fuller button phrases over single words on ties.
            var distancePenalty = dx * dx + dy * dy * 2.0;
            var centerDist = Math.Sqrt(Math.Pow(clickX - bounds.CenterX, 2) + Math.Pow(clickY - bounds.CenterY, 2)) * 0.25;
            var lengthBonus = Math.Min(text.Length, 16) * 0.4;
            var score = distancePenalty + centerDist - lengthBonus;
            if (score < bestScore)
            {
                bestScore = score;
                best = (text, bounds);
            }
        }
    }

    private static bool HasMeaningfulCharacters(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var alnum = 0;
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c)) alnum++;
        }

        return alnum >= 2;
    }

    private static ScreenRect RectAroundBox(int x, int y, ScreenRect virtualScreen, int width, int height)
    {
        var w = Math.Min(width, virtualScreen.Width);
        var h = Math.Min(height, virtualScreen.Height);
        var left = Math.Clamp(x - w / 2, virtualScreen.X, virtualScreen.Right - w);
        var top = Math.Clamp(y - h / 2, virtualScreen.Y, virtualScreen.Bottom - h);
        return new ScreenRect(left, top, w, h);
    }

    private static BgraImage CropSubImage(BgraImage source, ScreenRect sourceRect, ScreenRect subRect)
    {
        var clipped = subRect.Intersect(sourceRect);
        if (clipped == sourceRect) return source;
        var dst = new byte[clipped.Width * clipped.Height * 4];
        var srcStride = source.Width * 4;
        var dstStride = clipped.Width * 4;
        var offsetX = clipped.X - sourceRect.X;
        var offsetY = clipped.Y - sourceRect.Y;
        for (var row = 0; row < clipped.Height; row++)
        {
            var srcOffset = (offsetY + row) * srcStride + offsetX * 4;
            Buffer.BlockCopy(source.Pixels, srcOffset, dst, row * dstStride, dstStride);
        }

        return new BgraImage(clipped.Width, clipped.Height, dst);
    }
}
