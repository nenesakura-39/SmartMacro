using System.Text;
using System.Text.RegularExpressions;
using SmartMacro.Core.Models;

namespace SmartMacro.Core.Ocr;

/// <summary>
/// Pre-indexed O(1) word lookup table for an <see cref="OcrReadResult"/>. Turns 1,000-word screen searches into instant
/// hash lookups for exact and look-alike matches before falling back to length-pruned fuzzy edit distance.
/// </summary>
public sealed class OcrWordIndex
{
    private readonly Dictionary<string, List<OcrWordBox>> _exactInsensitive = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<OcrWordBox>> _foldedInsensitive = new(StringComparer.OrdinalIgnoreCase);

    public OcrWordIndex(OcrReadResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        foreach (var line in result.Lines)
        {
            foreach (var word in line.Words)
            {
                if (string.IsNullOrWhiteSpace(word.Text)) continue;
                var trimmed = OcrTextMatcher.TrimPunctuation(word.Text);
                if (trimmed.Length == 0) continue;

                if (!_exactInsensitive.TryGetValue(trimmed, out var list))
                {
                    list = new List<OcrWordBox>(2);
                    _exactInsensitive[trimmed] = list;
                }
                list.Add(word);

                var folded = OcrTextMatcher.FoldLookAlikes(trimmed);
                if (!_foldedInsensitive.TryGetValue(folded, out var fList))
                {
                    fList = new List<OcrWordBox>(2);
                    _foldedInsensitive[folded] = fList;
                }
                fList.Add(word);
            }
        }
    }

    public int DistinctWordCount => _exactInsensitive.Count;

    /// <summary>Returns all indexed word occurrences matching <paramref name="wordQuery"/> in O(1) time.</summary>
    public IReadOnlyList<TextMatch> LookupSingleWord(string wordQuery, bool caseSensitive, bool normalizeLookAlikes)
    {
        var clean = OcrTextMatcher.TrimPunctuation(wordQuery.Trim());
        if (clean.Length == 0) return Array.Empty<TextMatch>();

        var matches = new List<TextMatch>();
        if (_exactInsensitive.TryGetValue(clean, out var exactHits))
        {
            foreach (var w in exactHits)
            {
                var wClean = OcrTextMatcher.TrimPunctuation(w.Text);
                if (caseSensitive && !string.Equals(wClean, clean, StringComparison.Ordinal))
                    continue;
                matches.Add(new TextMatch(w.Text, w.Bounds, 1.0));
            }
        }

        if (matches.Count == 0 && normalizeLookAlikes)
        {
            var foldedClean = OcrTextMatcher.FoldLookAlikes(clean);
            if (_foldedInsensitive.TryGetValue(foldedClean, out var foldedHits))
            {
                foreach (var w in foldedHits)
                {
                    var wFolded = OcrTextMatcher.FoldLookAlikes(OcrTextMatcher.TrimPunctuation(w.Text));
                    if (caseSensitive && !string.Equals(wFolded, foldedClean, StringComparison.Ordinal))
                        continue;
                    matches.Add(new TextMatch(w.Text, w.Bounds, 1.0));
                }
            }
        }

        return matches
            .OrderBy(m => m.Bounds.Y)
            .ThenBy(m => m.Bounds.X)
            .ToList();
    }
}

/// <summary>
/// Finds a piece of text in OCR results. Windows OCR doesn't give a confidence value, so instead
/// we measure how similar the screen text is to what the user typed (edit distance). That lets a
/// search for "Login" still match an OCR misread like "Log1n" when the similarity limit allows it.
/// On top of that, characters Windows OCR often confuses on short UI words (0 / O, 1 / I / l) can be treated as
/// equal, so a search for "OK" matches a screen read as "0K" even at 100 % accuracy.
/// </summary>
public static class OcrTextMatcher
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(150);

    /// <summary>Returns matches, best first (then top-to-bottom, left-to-right).</summary>
    /// <param name="normalizeLookAlikes">
    /// When the plain comparison isn't good enough, compare again with look-alike characters folded together
    /// (0 → O, 1 / l / | → I). The better of the two scores is used.
    /// </param>
    /// <param name="useRegexPattern">
    /// When true, interprets <paramref name="query"/> as a regular expression or pipe-separated pattern (e.g. <c>nice|great</c>
    /// or <c>\d+\s*gold</c>).
    /// </param>
    public static IReadOnlyList<TextMatch> Find(OcrReadResult result, string query, TextMatchMode mode, bool caseSensitive, double minSimilarity,
        bool normalizeLookAlikes = false, bool useRegexPattern = false)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (useRegexPattern)
            return FindByRegex(result, query, caseSensitive);

        var target = Normalize(query, caseSensitive);
        if (target.Length == 0) return Array.Empty<TextMatch>();
        var foldedTarget = normalizeLookAlikes ? Normalize(FoldLookAlikes(query), caseSensitive) : null;

        var targetWordCount = target.Split(' ').Length;
        var wholeWords = mode == TextMatchMode.WholeWords;

        // Vision 2.0 Fast Path: for single whole-word searches (e.g. finding "nice" among 1,000 shuffling words),
        // check the O(1) word index first. If exact / look-alike matches exist and minSimilarity > 0, return immediately
        // when minSimilarity >= 1.0, or seed candidates and skip exact scanning when all words are distinct.
        if (wholeWords && targetWordCount == 1 && !target.Contains(' '))
        {
            var index = new OcrWordIndex(result);
            var exactMatches = index.LookupSingleWord(target, caseSensitive, normalizeLookAlikes);
            if (exactMatches.Count > 0 && (minSimilarity >= 0.999 || result.Lines.Sum(l => l.Words.Count) > 60))
                return exactMatches;
        }

        var targetIgnoringCase = Normalize(query, caseSensitive: false);
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
                    // Whole words: punctuation stuck to the screen words doesn't count ("art," / "(art)" are the word "art").
                    var rawSpan = string.Join(" ", span.Select(w => wholeWords ? TrimPunctuation(w.Text) : w.Text));
                    var spanText = Normalize(rawSpan, caseSensitive);

                    // Fast length-bound pruning before running string allocations or Levenshtein edit distance:
                    // For WholeWords mode, a span whose length differs by more than (1 - minSimilarity) * maxLen can never reach minSimilarity.
                    if (wholeWords && spanText.Length != target.Length)
                    {
                        var maxLen = Math.Max(spanText.Length, target.Length);
                        var minPossibleSim = 1.0 - (double)Math.Abs(spanText.Length - target.Length) / Math.Max(1, maxLen);
                        if (minPossibleSim + 1e-6 < minSimilarity) continue;
                    }

                    // Whole words: the text inside a longer word ("art" in "apart" / "apartment") is never a match,
                    // however similar the rest is.
                    if (wholeWords && IsInsideLongerWord(spanText, target, Normalize(rawSpan, caseSensitive: false), targetIgnoringCase))
                        continue;

                    // Exact case: a difference in upper / lower case alone ("Art" / "art" for "aRt") is never an OCR
                    // mistake, so it never matches – not even through the "similar enough" tolerance.
                    if (caseSensitive && spanText != target && string.Equals(Normalize(rawSpan, caseSensitive: false), targetIgnoringCase, StringComparison.Ordinal))
                        continue;

                    var similarity = Score(spanText, target, mode, minSimilarity);
                    if (foldedTarget is not null && similarity < 1)
                        similarity = Math.Max(similarity, Score(Normalize(FoldLookAlikes(rawSpan), caseSensitive), foldedTarget, mode, minSimilarity));
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

    private static IReadOnlyList<TextMatch> FindByRegex(OcrReadResult result, string pattern, bool caseSensitive)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return Array.Empty<TextMatch>();
        Regex regex;
        try
        {
            var opts = RegexOptions.CultureInvariant | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
            regex = new Regex(pattern.Trim(), opts, RegexTimeout);
        }
        catch (ArgumentException)
        {
            return Array.Empty<TextMatch>();
        }

        var matches = new List<TextMatch>();
        foreach (var line in result.Lines)
        {
            var words = line.Words.Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
            if (words.Count == 0) continue;

            // Check individual words first for tightest bounding boxes, then full line spans.
            for (var start = 0; start < words.Count; start++)
            {
                for (var len = 1; len <= Math.Min(4, words.Count - start); len++)
                {
                    var span = words.GetRange(start, len);
                    var raw = string.Join(" ", span.Select(w => w.Text));
                    try
                    {
                        var m = regex.Match(raw);
                        if (m.Success)
                        {
                            var bounds = span.Select(w => w.Bounds).Aggregate((a, b) => a.Union(b));
                            matches.Add(new TextMatch(raw, bounds, m.Length == raw.Length ? 1.0 : 0.95));
                            break; // tightest span starting at 'start' won
                        }
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        return matches;
                    }
                }
            }
        }

        var accepted = new List<TextMatch>();
        foreach (var c in matches.OrderByDescending(m => m.Similarity).ThenBy(m => m.Bounds.Width).ThenBy(m => m.Bounds.Y).ThenBy(m => m.Bounds.X))
        {
            if (accepted.Any(a => a.Bounds.IntersectsWith(c.Bounds))) continue;
            accepted.Add(c);
        }

        return accepted;
    }

    /// <summary>
    /// The word without leading / trailing punctuation or symbols ("art," → "art", "(OK)" → "OK"); unchanged when it is
    /// nothing but punctuation.
    /// </summary>
    public static string TrimPunctuation(string word)
    {
        if (string.IsNullOrEmpty(word)) return word ?? string.Empty;
        int start = 0, end = word.Length;
        while (start < end && IsTrimmable(word[start])) start++;
        while (end > start && IsTrimmable(word[end - 1])) end--;
        return start == end ? word : word[start..end];

        // '|' is kept: OCR reads a capital I as a pipe ("|nfo"), and look-alike folding turns it back into I.
        static bool IsTrimmable(char c) => c != '|' && !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c);
    }

    /// <summary>
    /// True when the search text appears in the span only as a piece of a longer word (a letter or digit right before or
    /// after it), e.g. "ART" in "APART" or "APARTMENT". Checked without case so "Art" inside "Apartment" counts too.
    /// A span that also contains the text as a complete word is fine.
    /// </summary>
    internal static bool IsInsideLongerWord(string spanText, string target, string spanIgnoringCase, string targetIgnoringCase)
    {
        if (spanText == target || targetIgnoringCase.Length == 0) return false;
        var embedded = false;
        for (var at = spanIgnoringCase.IndexOf(targetIgnoringCase, StringComparison.Ordinal); at >= 0;
             at = spanIgnoringCase.IndexOf(targetIgnoringCase, at + 1, StringComparison.Ordinal))
        {
            var end = at + targetIgnoringCase.Length;
            var letterBefore = at > 0 && char.IsLetterOrDigit(spanIgnoringCase[at - 1]);
            var letterAfter = end < spanIgnoringCase.Length && char.IsLetterOrDigit(spanIgnoringCase[end]);
            if (!letterBefore && !letterAfter) return false; // also there as a whole word
            embedded = true;
        }

        return embedded;
    }

    /// <summary>0..1, where 1 means identical (after normalizing).</summary>
    public static double Similarity(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 1;
        var distance = Levenshtein(a, b);
        return 1.0 - (double)distance / Math.Max(a.Length, b.Length);
    }

    /// <summary>
    /// Maps characters OCR commonly mixes up onto one representative: zero → O, one / lower-case L / pipe → I.
    /// Runs on the raw text (before upper-casing), so a lower-case "l" read for a capital "I" is caught too.
    /// Both the search text and the screen text are folded the same way, so a real "l" still matches itself.
    /// </summary>
    public static string FoldLookAlikes(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = chars[i] switch
            {
                '0' => 'O',
                '1' or 'l' or '|' => 'I',
                _ => chars[i],
            };
        }

        return new string(chars);
    }

    private static double Score(string spanText, string target, TextMatchMode mode, double minSimilarity = 0)
    {
        if (spanText == target) return 1;
        if (mode == TextMatchMode.Contains && spanText.Contains(target, StringComparison.Ordinal)) return 1;

        // Fast character-mask upper-bound filter: if too few characters of 'target' exist anywhere in 'spanText',
        // Levenshtein distance is guaranteed to be too large.
        if (minSimilarity > 0.5 && target.Length >= 3)
        {
            ulong targetMask = CharMask(target);
            ulong spanMask = CharMask(spanText);
            int missingDistinct = System.Numerics.BitOperations.PopCount(targetMask & ~spanMask);
            int maxLen = mode == TextMatchMode.WholeWords ? Math.Max(spanText.Length, target.Length) : target.Length;
            if (1.0 - (double)missingDistinct / Math.Max(1, maxLen) + 1e-6 < minSimilarity)
                return 0;
        }

        var whole = Similarity(spanText, target);
        if (mode == TextMatchMode.WholeWords) return whole;

        // Contains: also allow a near-match of the query inside a longer span (best window of the same length).
        if (spanText.Length <= target.Length) return whole;
        var best = whole;
        for (var i = 0; i + target.Length <= spanText.Length; i++)
            best = Math.Max(best, Similarity(spanText.Substring(i, target.Length), target));
        return best;
    }

    private static ulong CharMask(string s)
    {
        ulong mask = 0;
        for (var i = 0; i < s.Length; i++)
            mask |= 1UL << (s[i] & 63);
        return mask;
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
