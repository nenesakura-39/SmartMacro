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
    public string FullText => string.Join(Environment.NewLine, Lines.Select(l => l.Text));
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
