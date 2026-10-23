using System.Text.RegularExpressions;

namespace SeaRaider.Core.Catalog;

/// <summary>A parsed search box query.</summary>
/// <param name="Raw">As typed.</param>
/// <param name="Normalized">Whole query, normalised like titles (quotes removed).</param>
/// <param name="Phrases">"Quoted phrases" (normalised) that must appear in the title exactly, as whole words.</param>
/// <param name="Tokens">Normalised words of the whole query.</param>
/// <param name="SiteText">What to send to the sites' own search: the words without quotes or release tags.</param>
public sealed partial record SearchQuery(string Raw, string Normalized, IReadOnlyList<string> Phrases, IReadOnlyList<string> Tokens,
    string SiteText)
{
    [GeneratedRegex("\"(?<p>[^\"]+)\"")]
    private static partial Regex QuotedRegex();

    public bool IsEmpty => Tokens.Count == 0;

    public static SearchQuery Parse(string? raw)
    {
        raw ??= string.Empty;
        var phrases = QuotedRegex().Matches(raw)
            .Select(m => TitleNormalizer.Fold(m.Groups["p"].Value))
            .Where(p => p.Length > 0)
            .ToList();

        var unquoted = raw.Replace("\"", " ", StringComparison.Ordinal).Trim();
        // Release tags typed into the box ("elden ring v1.2 fitgirl") would only narrow the sites' searches.
        var normalized = TitleNormalizer.NormalizeForSearch(unquoted);
        var siteText = unquoted.Length == 0 ? string.Empty : Scraping.MetadataParser.CleanTitle(unquoted).Title.Trim();
        return new SearchQuery(raw, normalized, phrases, TitleNormalizer.Tokens(normalized), siteText.Length > 0 ? siteText : unquoted);
    }
}

/// <summary>
/// Relevance of a title to a search, 0–1. Exact title matches score 1 and always sort first; then titles that start
/// with the query ("hades" → "Hades II"), contain it as a phrase, contain every word in any order, and finally fuzzy
/// matches that survive typos (Levenshtein per word, Dice coefficient on the whole string). Numbers must match exactly,
/// so "witcher 3" doesn't pull in "The Witcher 2". Results under <see cref="MinimumScore"/> are noise (a site's search
/// matched the word somewhere in a post body) and are dropped.
/// </summary>
public static class SearchScorer
{
    /// <summary>Below this a result is hidden.</summary>
    public const double MinimumScore = 0.45;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal) { "the", "a", "an", "of", "and", "to", "in" };

    public static double Score(SearchQuery query, string title, IReadOnlyCollection<string>? genres = null, string? developer = null) =>
        ScoreNormalized(query, TitleNormalizer.NormalizeForSearch(title), genres, developer);

    /// <summary><see cref="Score"/> for a title already passed through <see cref="TitleNormalizer.NormalizeForSearch"/> (cached by the index).</summary>
    public static double ScoreNormalized(SearchQuery query, string normalizedTitle, IReadOnlyCollection<string>? genres = null,
        string? developer = null)
    {
        if (query.IsEmpty) return 1;
        var titleScore = ScoreTitle(query, normalizedTitle);

        // Genre / developer searches ("roguelike", "remedy") still find games, below title hits.
        if (titleScore < 0.5 && MatchesAllTokens(query.Tokens, genres, developer)) titleScore = Math.Max(titleScore, 0.5);
        return titleScore;
    }

    public static double ScoreTitle(SearchQuery query, string normalizedTitle)
    {
        if (normalizedTitle.Length == 0) return 0;
        var padded = $" {normalizedTitle} ";

        foreach (var phrase in query.Phrases)
        {
            if (!padded.Contains($" {phrase} ", StringComparison.Ordinal)) return 0; // quoted phrase is mandatory
        }

        var q = query.Normalized;
        if (normalizedTitle == q) return 1.0;

        var titleTokens = TitleNormalizer.Tokens(normalizedTitle);
        var extra = Math.Max(0, titleTokens.Length - query.Tokens.Count);

        if (normalizedTitle.StartsWith(q + " ", StringComparison.Ordinal))
            return 0.92 - 0.1 * Math.Min(1, extra / 6.0);

        var phraseAt = padded.IndexOf($" {q} ", StringComparison.Ordinal);
        if (phraseAt >= 0)
            return 0.82 - 0.02 * Math.Min(4, normalizedTitle[..phraseAt].Count(c => c == ' ') + 1) - 0.04 * Math.Min(1, extra / 8.0);

        // Word by word, any order, with typo tolerance.
        var significant = query.Tokens.Count > 1 ? query.Tokens.Where(t => !StopWords.Contains(t)).ToList() : query.Tokens.ToList();
        if (significant.Count == 0) significant = query.Tokens.ToList();

        var initials = string.Concat(titleTokens.Where(t => char.IsLetter(t[0])).Select(t => t[0]));
        var similarities = significant
            .Select((token, i) => Math.Max(
                titleTokens.Select(t => TokenSimilarity(token, t, isLast: i == significant.Count - 1)).DefaultIfEmpty(0).Max(),
                IsAcronymOf(token, initials) ? 0.85 : 0)) // "gta" → Grand Theft Auto, "dmc" → Devil May Cry
            .ToList();
        var average = similarities.Average();

        if (similarities.All(s => s >= 0.75))
            return 0.55 + 0.25 * average - 0.05 * Math.Min(1, extra / 8.0);

        // A number in the query that the title doesn't have means another game ("resident evil 2" vs "… 3").
        if (significant.Any(t => char.IsDigit(t[0]) && !titleTokens.Contains(t))) return Math.Min(0.3, 0.35 * average);

        var dice = DiceCoefficient(q, normalizedTitle);
        return dice >= 0.6 ? 0.6 * dice : 0.35 * average;
    }

    /// <summary>1 exact, 0.9 prefix (for the word being typed), Levenshtein similarity for longer words, 0 otherwise.</summary>
    public static double TokenSimilarity(string queryToken, string titleToken, bool isLast)
    {
        if (queryToken == titleToken) return 1;

        var numeric = char.IsDigit(queryToken[0]) || char.IsDigit(titleToken[0]);
        if (numeric) return 0; // "3" never matches "2"; "2077" never matches "2049"

        if (titleToken.StartsWith(queryToken, StringComparison.Ordinal) && (queryToken.Length >= 3 || isLast)) return 0.9;

        var longest = Math.Max(queryToken.Length, titleToken.Length);
        if (Math.Min(queryToken.Length, titleToken.Length) < 4) return 0;

        var allowed = longest >= 8 ? 2 : 1;
        var distance = Levenshtein(queryToken, titleToken, allowed);
        return distance <= allowed ? 1.0 - (double)distance / longest : 0;
    }

    private static bool IsAcronymOf(string token, string initials) =>
        token.Length is >= 2 and <= 6 && token.All(char.IsLetter) && initials.Contains(token, StringComparison.Ordinal);

    /// <summary>Edit distance, giving up (returning max+1) once it exceeds <paramref name="max"/>.</summary>
    public static int Levenshtein(string a, string b, int max = int.MaxValue)
    {
        if (Math.Abs(a.Length - b.Length) > max) return max == int.MaxValue ? Math.Abs(a.Length - b.Length) : max + 1;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var rowMin = current[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                rowMin = Math.Min(rowMin, current[j]);
            }
            if (rowMin > max) return max + 1;
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    /// <summary>Sørensen–Dice coefficient on character bigrams (spaces removed).</summary>
    public static double DiceCoefficient(string a, string b)
    {
        a = a.Replace(" ", string.Empty, StringComparison.Ordinal);
        b = b.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (a.Length < 2 || b.Length < 2) return a == b ? 1 : 0;

        var bigrams = new Dictionary<int, int>();
        for (var i = 0; i < a.Length - 1; i++)
        {
            var key = (a[i] << 16) | a[i + 1];
            bigrams[key] = bigrams.TryGetValue(key, out var n) ? n + 1 : 1;
        }

        var shared = 0;
        for (var i = 0; i < b.Length - 1; i++)
        {
            var key = (b[i] << 16) | b[i + 1];
            if (bigrams.TryGetValue(key, out var n) && n > 0)
            {
                shared++;
                bigrams[key] = n - 1;
            }
        }
        return 2.0 * shared / (a.Length - 1 + b.Length - 1);
    }

    private static bool MatchesAllTokens(IReadOnlyList<string> tokens, IReadOnlyCollection<string>? genres, string? developer)
    {
        var haystack = string.Join(' ', (genres ?? Array.Empty<string>()).Append(developer ?? string.Empty).Select(TitleNormalizer.Fold));
        if (haystack.Trim().Length == 0) return false;
        var words = TitleNormalizer.Tokens(haystack);
        return tokens.All(t => words.Any(w => w.StartsWith(t, StringComparison.Ordinal)));
    }
}
