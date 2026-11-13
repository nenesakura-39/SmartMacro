using SmartMacro.Core.Models;

namespace SmartMacro.Core.Ocr;

/// <summary>A rectangle in physical screen pixels (virtual-screen coordinates).</summary>
public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public int CenterX => X + Width / 2;
    public int CenterY => Y + Height / 2;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public ScreenRect Union(ScreenRect other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        var x = Math.Min(X, other.X);
        var y = Math.Min(Y, other.Y);
        return new ScreenRect(x, y, Math.Max(Right, other.Right) - x, Math.Max(Bottom, other.Bottom) - y);
    }

    public ScreenRect Intersect(ScreenRect other)
    {
        var x = Math.Max(X, other.X);
        var y = Math.Max(Y, other.Y);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);
        return right <= x || bottom <= y ? default : new ScreenRect(x, y, right - x, bottom - y);
    }

    public bool IntersectsWith(ScreenRect other) => !Intersect(other).IsEmpty;

    public bool Contains(int x, int y) => !IsEmpty && x >= X && x < Right && y >= Y && y < Bottom;

    public override string ToString() => $"({X}, {Y}) {Width}×{Height}";
}

/// <summary>One word the OCR engine read, with its position on screen.</summary>
public sealed record OcrWordBox(string Text, ScreenRect Bounds);

/// <summary>One line of words, left to right.</summary>
public sealed record OcrLineBox(IReadOnlyList<OcrWordBox> Words)
{
    public string Text => string.Join(" ", Words.Select(w => w.Text));
}

/// <summary>Everything the OCR engine read from one screen area.</summary>
public sealed record OcrReadResult(ScreenRect Region, IReadOnlyList<OcrLineBox> Lines)
{
    private int? _totalWordCount;
    private OcrWordIndex? _wordIndex;

    public string FullText => string.Join(Environment.NewLine, Lines.Select(l => l.Text));

    /// <summary>Total number of non-empty words read across all lines on screen.</summary>
    public int TotalWordCount => _totalWordCount ??= Lines.Sum(l => l.Words.Count(w => !string.IsNullOrWhiteSpace(w.Text)));

    /// <summary>Cached O(1) word index for this OCR frame.</summary>
    internal OcrWordIndex WordIndex => _wordIndex ??= new OcrWordIndex(this);
}

/// <summary>
/// Removes duplicate OCR word boxes that arise when overlapping screen tiles or multi-pass reads recognize the same
/// physical word on screen more than once (e.g. along a 120 px tile seam on a 1,500-word screen).
/// </summary>
public static class OcrWordDeduplicator
{
    public static IReadOnlyList<OcrLineBox> DeduplicateLines(IReadOnlyList<OcrLineBox> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count <= 1) return lines;

        var keptWords = new List<OcrWordBox>();
        var resultLines = new List<OcrLineBox>(lines.Count);

        foreach (var line in lines)
        {
            if (line.Words.Count == 0) continue;
            var filteredLineWords = new List<OcrWordBox>(line.Words.Count);
            foreach (var word in line.Words)
            {
                if (string.IsNullOrWhiteSpace(word.Text)) continue;
                if (IsDuplicateWord(word, keptWords)) continue;
                keptWords.Add(word);
                filteredLineWords.Add(word);
            }

            if (filteredLineWords.Count > 0)
                resultLines.Add(new OcrLineBox(filteredLineWords));
        }

        return resultLines;
    }

    private static bool IsDuplicateWord(OcrWordBox candidate, List<OcrWordBox> existing)
    {
        var candClean = OcrTextMatcher.TrimPunctuation(candidate.Text.Trim());
        var candBounds = candidate.Bounds;
        if (candBounds.IsEmpty) return false;

        for (var i = existing.Count - 1; i >= 0; i--)
        {
            var other = existing[i];
            var otherBounds = other.Bounds;
            if (otherBounds.IsEmpty) continue;

            // Quick vertical and horizontal proximity check
            if (Math.Abs(candBounds.CenterY - otherBounds.CenterY) > Math.Max(candBounds.Height, otherBounds.Height))
                continue;
            if (Math.Abs(candBounds.CenterX - otherBounds.CenterX) > Math.Max(candBounds.Width, otherBounds.Width))
                continue;

            var inter = candBounds.Intersect(otherBounds);
            if (inter.IsEmpty) continue;

            var interArea = (double)inter.Width * inter.Height;
            var minArea = (double)Math.Min(candBounds.Width * candBounds.Height, otherBounds.Width * otherBounds.Height);
            var overlapRatio = minArea > 0 ? interArea / minArea : 0;
            var centerInside = otherBounds.Contains(candBounds.CenterX, candBounds.CenterY)
                               || candBounds.Contains(otherBounds.CenterX, otherBounds.CenterY);

            if (overlapRatio < 0.45 && !centerInside) continue;

            var otherClean = OcrTextMatcher.TrimPunctuation(other.Text.Trim());
            if (string.Equals(candClean, otherClean, StringComparison.OrdinalIgnoreCase)
                || string.Equals(OcrTextMatcher.FoldLookAlikes(candClean), OcrTextMatcher.FoldLookAlikes(otherClean), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Reads text from the screen. The real version uses Windows OCR (Windows.Media.Ocr).</summary>
public interface IOcrService
{
    bool IsAvailable { get; }

    /// <summary>Plain-English status, e.g. which language is used or why OCR isn't available.</summary>
    string StatusText { get; }

    /// <summary>The rectangle covering all monitors.</summary>
    ScreenRect GetVirtualScreen();

    /// <summary>Captures the given screen area and reads its text.</summary>
    Task<OcrReadResult> ReadAsync(ScreenRect region, CancellationToken cancellationToken);

    /// <summary>True when this OCR engine supports live image preprocessing modes (HighContrast, BinarizeOtsu, multi-pass).</summary>
    bool SupportsPreprocessing => false;

    /// <summary>Captures the given screen area, applies the requested preprocessing mode, and reads its text.</summary>
    Task<OcrReadResult> ReadAsync(ScreenRect region, OcrPreprocessMode preprocess, CancellationToken cancellationToken) =>
        ReadAsync(region, cancellationToken);

    /// <summary>Captures <paramref name="region"/> from a specific top-level window (<paramref name="windowHandle"/>) when supported and reads its text.</summary>
    Task<OcrReadResult> ReadWindowAsync(long windowHandle, ScreenRect region, CancellationToken cancellationToken) =>
        ReadAsync(region, cancellationToken);
}

public sealed class OcrUnavailableException : Exception
{
    public OcrUnavailableException(string message) : base(message)
    {
    }
}

public sealed class TextNotFoundException : Exception
{
    public TextNotFoundException(string message) : base(message)
    {
    }
}

/// <summary>A place on screen where the searched text was found. Similarity is 0..1.</summary>
public sealed record TextMatch(string Text, ScreenRect Bounds, double Similarity);
