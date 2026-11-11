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
    private readonly OcrReadResult _result;
    private readonly Dictionary<string, List<OcrWordBox>> _exactInsensitive = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<OcrWordBox>>? _foldedInsensitive;

    public OcrWordIndex(OcrReadResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _result = result;
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
            }
        }
    }

    private Dictionary<string, List<OcrWordBox>> EnsureFoldedIndex()
    {
        if (_foldedInsensitive is not null) return _foldedInsensitive;
        var foldedMap = new Dictionary<string, List<OcrWordBox>>(_exactInsensitive.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var line in _result.Lines)
        {
            foreach (var word in line.Words)
            {
                if (string.IsNullOrWhiteSpace(word.Text)) continue;
                var trimmed = OcrTextMatcher.TrimPunctuation(word.Text);
                if (trimmed.Length == 0) continue;
                var folded = OcrTextMatcher.FoldLookAlikes(trimmed);
                if (!foldedMap.TryGetValue(folded, out var fList))
                {
                    fList = new List<OcrWordBox>(2);
                    foldedMap[folded] = fList;
                }
                fList.Add(word);
            }
        }
        _foldedInsensitive = foldedMap;
        return foldedMap;
    }

    public int DistinctWordCount => _exactInsensitive.Count;

    /// <summary>Returns all indexed word occurrences matching <paramref name="wordQuery"/> in O(1) time.</summary>
    public IReadOnlyList<TextMatch> LookupSingleWord(string wordQuery, bool caseSensitive, bool normalizeLookAlikes, bool strictPunctuation = false)
    {
        var rawQuery = wordQuery.Trim();
        var clean = strictPunctuation ? rawQuery : OcrTextMatcher.TrimPunctuation(rawQuery);
        if (clean.Length == 0) return Array.Empty<TextMatch>();

        var matches = new List<TextMatch>();
        if (_exactInsensitive.TryGetValue(OcrTextMatcher.TrimPunctuation(rawQuery), out var exactHits))
        {
            foreach (var w in exactHits)
            {
                var wRaw = w.Text.Trim();
                var wClean = OcrTextMatcher.TrimPunctuation(wRaw);
                if (strictPunctuation && !string.Equals(wRaw, wClean, StringComparison.Ordinal))
                    continue;
                if (caseSensitive && !string.Equals(wClean, clean, StringComparison.Ordinal))
                    continue;

                // Exact raw token ("nice" == "nice") ranks strictly higher than punctuation-trimmed ("nice!") or case-differing ("Nice").
                double score = 1.0;
                if (!string.Equals(wRaw, rawQuery, StringComparison.Ordinal))
                {
                    if (!string.Equals(wRaw, wClean, StringComparison.Ordinal))
                        score -= 0.008; // trimmed punctuation penalty (e.g. "nice!" -> 0.992)
                    if (!string.Equals(wClean, clean, StringComparison.Ordinal))
                        score -= 0.004; // case difference when caseSensitive=false ("Nice" -> 0.996)
                }

                matches.Add(new TextMatch(w.Text, w.Bounds, score));
            }
        }

        if (normalizeLookAlikes && !strictPunctuation)
        {
            var foldedClean = OcrTextMatcher.FoldLookAlikes(clean);
            if (EnsureFoldedIndex().TryGetValue(foldedClean, out var foldedHits))
            {
                foreach (var w in foldedHits)
                {
                    var wClean = OcrTextMatcher.TrimPunctuation(w.Text.Trim());
                    if (string.Equals(wClean, clean, caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                        continue; // already added via exactHits

                    // Exact case: if folding doesn't change the case-only difference (e.g. "0k" vs "OK"), reject when caseSensitive is true.
                    var wFolded = OcrTextMatcher.FoldLookAlikes(wClean);
                    if (caseSensitive && !string.Equals(wFolded, foldedClean, StringComparison.Ordinal))
                        continue;

                    int foldedDiffs = OcrTextMatcher.CountFoldedDifferences(wClean, clean);
                    // Only penalize when there are competing exact matches or multi-character leet substitutions ("c01n" / "n1ce" on a word board).
                    double penalty = (matches.Count > 0 || (foldedDiffs >= 2 && clean.Length >= 4))
                        ? Math.Min(0.12, foldedDiffs * 0.015)
                        : 0.0;
                    if (matches.Count > 0 && !string.Equals(w.Text.Trim(), wClean, StringComparison.Ordinal))
                        penalty += 0.008;

                    matches.Add(new TextMatch(w.Text, w.Bounds, Math.Max(0.85, 1.0 - penalty)));
                }
            }
        }

        // If only a single match was found, restore 1.0 score when it matched via punctuation trim or look-alike folding without penalty
        if (matches.Count == 1 && matches[0].Similarity is >= 0.991 and < 1.0 && !strictPunctuation)
        {
            matches[0] = matches[0] with { Similarity = 1.0 };
        }

        var ordered = matches
            .OrderByDescending(m => m.Similarity)
            .ThenBy(m => m.Bounds.Y)
            .ThenBy(m => m.Bounds.X);

        var accepted = new List<TextMatch>(matches.Count);
        foreach (var candidate in ordered)
        {
            if (!candidate.Bounds.IsEmpty && accepted.Any(a => a.Bounds.IntersectsWith(candidate.Bounds)))
                continue;
            accepted.Add(candidate);
        }

        return accepted;
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
        bool normalizeLookAlikes = false, bool useRegexPattern = false, bool strictPunctuation = false)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (useRegexPattern)
            return FindByRegex(result, query, caseSensitive);

        // Multi-term pipe list ("nice | great | awesome") without requiring full regex mode
        if (!string.IsNullOrWhiteSpace(query) && query.Contains(" | ", StringComparison.Ordinal))
        {
            var terms = query.Split(" | ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (terms.Length > 1)
            {
                var allCandidates = new List<TextMatch>();
                foreach (var term in terms)
                {
                    allCandidates.AddRange(Find(result, term, mode, caseSensitive, minSimilarity, normalizeLookAlikes, false, strictPunctuation));
                }

                var merged = new List<TextMatch>();
                foreach (var cand in allCandidates.OrderByDescending(m => m.Similarity).ThenBy(m => m.Bounds.Y).ThenBy(m => m.Bounds.X))
                {
                    if (!cand.Bounds.IsEmpty && merged.Any(a => a.Bounds.IntersectsWith(cand.Bounds))) continue;
                    merged.Add(cand);
                }

                return merged;
            }
        }

        var rawTarget = Normalize(query, caseSensitive);
        var target = strictPunctuation ? rawTarget : Normalize(TrimPunctuation(query.Trim()), caseSensitive);
        if (target.Length == 0) target = rawTarget;
        if (target.Length == 0) return Array.Empty<TextMatch>();
        var foldedTarget = (normalizeLookAlikes && !strictPunctuation) ? Normalize(FoldLookAlikes(query), caseSensitive) : null;

        var targetWordCount = target.Split(' ').Length;
        var wholeWords = mode == TextMatchMode.WholeWords;

        // Vision 2.0 Fast Path: for single whole-word searches (e.g. finding "nice" among 1,000 shuffling words),
        // check the O(1) word index first. If exact / look-alike matches exist and meet minSimilarity, return immediately
        // when minSimilarity >= 1.0 or when > 60 words are on screen.
        if (wholeWords && targetWordCount == 1 && !target.Contains(' '))
        {
            var index = result.WordIndex;
            var exactMatches = index.LookupSingleWord(query, caseSensitive, normalizeLookAlikes, strictPunctuation)
                .Where(m => m.Similarity + 1e-6 >= minSimilarity)
                .ToList();
            if (exactMatches.Count > 0 && (minSimilarity >= 0.99 || result.TotalWordCount > 60))
                return exactMatches;
        }

        var targetIgnoringCase = Normalize(strictPunctuation ? query : TrimPunctuation(query.Trim()), caseSensitive: false);
        if (targetIgnoringCase.Length == 0) targetIgnoringCase = Normalize(query, caseSensitive: false);
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
                    var rawUntrimmed = string.Join(" ", span.Select(w => w.Text.Trim()));
                    // Whole words: punctuation stuck to the screen words doesn't count ("art," / "(art)" are the word "art") unless strictPunctuation is enabled.
                    var rawSpan = string.Join(" ", span.Select(w => (wholeWords && !strictPunctuation) ? TrimPunctuation(w.Text) : w.Text.Trim()));
                    if (strictPunctuation && wholeWords && !string.Equals(rawUntrimmed, string.Join(" ", span.Select(w => TrimPunctuation(w.Text.Trim()))), StringComparison.Ordinal))
                        continue;

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
                    bool usedLookAlike = false;
                    if (foldedTarget is not null && similarity < 1)
                    {
                        var foldedSpan = Normalize(FoldLookAlikes(rawSpan), caseSensitive);
                        var foldedSpanIgnCase = Normalize(FoldLookAlikes(rawSpan), caseSensitive: false);
                        var foldedTargetIgnCase = Normalize(FoldLookAlikes(strictPunctuation ? query : TrimPunctuation(query.Trim())), caseSensitive: false);

                        // Exact case: if even after folding look-alikes the only difference is upper/lower case ("0k" vs "OK"), reject when caseSensitive is true.
                        if (!(caseSensitive && foldedSpan != foldedTarget && string.Equals(foldedSpanIgnCase, foldedTargetIgnCase, StringComparison.Ordinal)))
                        {
                            var foldedSim = Score(foldedSpan, foldedTarget, mode, minSimilarity);
                            if (foldedSim > similarity)
                            {
                                int diffs = CountFoldedDifferences(rawSpan, query.Trim());
                                // Penalize multi-character leet substitutions ("c01n" vs "coin" or "n1ce" vs "nice") so real words always rank higher.
                                double penalty = diffs >= 2 && target.Length >= 4 ? Math.Min(0.12, diffs * 0.015) : (diffs >= 1 && target.Length >= 4 ? 0.008 : 0.0);
                                similarity = Math.Max(similarity, foldedSim - penalty);
                                usedLookAlike = true;
                            }
                        }
                    }

                    // Apply a tiny tie-breaker penalty when punctuation had to be stripped ("nice!" vs "nice") so exact "nice" always wins when both are on screen.
                    if (wholeWords && !usedLookAlike && similarity >= 0.999 && !string.Equals(rawUntrimmed, rawSpan, StringComparison.Ordinal))
                    {
                        similarity = 0.992;
                    }

                    if (similarity + 0.01 < minSimilarity && !(similarity >= 0.985 && minSimilarity <= 1.0 && !strictPunctuation)) continue;

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

        // If only a single punctuation-trimmed whole-word match was found (e.g. "art," in "Draw art, then (OK)") and no unpunctuated match exists,
        // restore its 1.0 display score for backward compatibility.
        if (accepted.Count == 1 && accepted[0].Similarity is >= 0.991 and < 1.0 && wholeWords && !strictPunctuation)
        {
            var only = accepted[0];
            if (string.Equals(Normalize(TrimPunctuation(only.Text), caseSensitive), target, StringComparison.Ordinal))
                accepted[0] = only with { Similarity = 1.0 };
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
            var cleanPattern = pattern.Contains(" | ", StringComparison.Ordinal)
                ? Regex.Replace(pattern.Trim(), @"\s+\|\s+", "|")
                : pattern.Trim();
            var opts = RegexOptions.CultureInvariant | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
            regex = new Regex(cleanPattern, opts, RegexTimeout);
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
        var needsFold = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is '0' or '1' or 'l' or '|')
            {
                needsFold = true;
                break;
            }
        }
        if (!needsFold) return text;

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

    internal static int CountFoldedDifferences(string a, string b)
    {
        int len = Math.Min(a.Length, b.Length);
        int diffs = Math.Abs(a.Length - b.Length);
        for (int i = 0; i < len; i++)
        {
            if (char.ToUpperInvariant(a[i]) != char.ToUpperInvariant(b[i]))
                diffs++;
        }
        return diffs;
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
