using System.Text;
using SmartMacro.Core.Models;

namespace SmartMacro.Core.Ocr;

/// <summary>
/// Finds a piece of text in OCR results. Windows OCR doesn't give a confidence value, so instead
/// we measure how similar the screen text is to what the user typed (edit distance). That lets a
/// search for "Login" still match an OCR misread like "Log1n" when the similarity limit allows it.
/// </summary>
public static class OcrTextMatcher
{
    /// <summary>Returns matches, best first (then top-to-bottom, left-to-right).</summary>
    public static IReadOnlyList<TextMatch> Find(OcrReadResult result, string query, TextMatchMode mode, bool caseSensitive, double minSimilarity)
    {
        ArgumentNullException.ThrowIfNull(result);
        var target = Normalize(query, caseSensitive);
        if (target.Length == 0) return Array.Empty<TextMatch>();

        var targetWordCount = target.Split(' ').Length;
        var candidates = new List<TextMatch>();

        foreach (var line in result.Lines)
        {
            var words = line.Words.Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
            for (var start = 0; start < words.Count; start++)
            {
                // Try spans with one word fewer/more than the query, since OCR sometimes splits or joins words.
                for (var length = Math.Max(1, targetWordCount - 1); length <= targetWordCount + 1 && start + length <= words.Count; length++)
                {
                    var span = words.GetRange(start, length);
                    var spanText = Normalize(string.Join(" ", span.Select(w => w.Text)), caseSensitive);
                    var similarity = Score(spanText, target, mode);
                    if (similarity < minSimilarity) continue;

                    var bounds = span.Select(w => w.Bounds).Aggregate((a, b) => a.Union(b));
                    candidates.Add(new TextMatch(string.Join(" ", span.Select(w => w.Text)), bounds, similarity));
                }
            }
        }

        // Keep the best match for each spot on screen.
        var ordered = candidates
            .OrderByDescending(c => c.Similarity)
            .ThenBy(c => c.Bounds.Width)        // tighter span wins a tie
            .ThenBy(c => c.Bounds.Y)
            .ThenBy(c => c.Bounds.X);

        var accepted = new List<TextMatch>();
        foreach (var candidate in ordered)
        {
            if (accepted.Any(a => a.Bounds.IntersectsWith(candidate.Bounds))) continue;
            accepted.Add(candidate);
        }

        return accepted
            .OrderByDescending(a => a.Similarity)
            .ThenBy(a => a.Bounds.Y)
            .ThenBy(a => a.Bounds.X)
            .ToList();
    }

    /// <summary>0..1, where 1 means identical (after normalizing).</summary>
    public static double Similarity(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 1;
        var distance = Levenshtein(a, b);
        return 1.0 - (double)distance / Math.Max(a.Length, b.Length);
    }

    private static double Score(string spanText, string target, TextMatchMode mode)
    {
        if (spanText == target) return 1;
        if (mode == TextMatchMode.Contains && spanText.Contains(target, StringComparison.Ordinal)) return 1;

        var whole = Similarity(spanText, target);
        if (mode == TextMatchMode.WholeWords) return whole;

        // Contains: also allow a near-match of the query inside a longer span (best window of the same length).
        if (spanText.Length <= target.Length) return whole;
        var best = whole;
        for (var i = 0; i + target.Length <= spanText.Length; i++)
            best = Math.Max(best, Similarity(spanText.Substring(i, target.Length), target));
        return best;
    }

    private static string Normalize(string? text, bool caseSensitive)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var builder = new StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var c in text.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace) builder.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                builder.Append(caseSensitive ? c : char.ToUpperInvariant(c));
                lastWasSpace = false;
            }
        }

        return builder.ToString();
    }

    private static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
