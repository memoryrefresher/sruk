using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SeaRaider.Core.Scraping;

namespace SeaRaider.Core.Catalog;

/// <summary>
/// Canonical forms of game titles, for matching titles written differently by different sites.
/// <list type="bullet">
///   <item><see cref="NormalizeForSearch"/>: release tags removed ("[FitGirl Repack]", "v1.0.4", "+ DLCs", "Free Download"),
///         lower case, accents folded, punctuation to spaces, "&amp;" → "and", Roman numerals → digits
///         ("Hades II" = "hades 2", "GTA V" = "gta 5").</item>
///   <item><see cref="DedupKey"/>: additionally ignores edition names ("Deluxe Edition", "GOTY"), a leading "The" and a
///         year in parentheses ("Resident Evil 2 (2019)"), so "ELDEN RING Deluxe Edition" from one site and "Elden Ring" from another become one game.</item>
/// </list>
/// </summary>
public static partial class TitleNormalizer
{
    private static readonly Dictionary<string, string> RomanNumerals = new(StringComparer.Ordinal)
    {
        ["i"] = "1", ["ii"] = "2", ["iii"] = "3", ["iv"] = "4", ["v"] = "5", ["vi"] = "6", ["vii"] = "7", ["viii"] = "8",
        ["ix"] = "9", ["x"] = "10", ["xi"] = "11", ["xii"] = "12", ["xiii"] = "13", ["xiv"] = "14", ["xv"] = "15", ["xvi"] = "16",
    };

    [GeneratedRegex(@"\b(?:(?:digital|super|ultimate|deluxe|complete|definitive|gold|premium|goty|game of the year|collectors?|special|standard|enhanced|legendary|royal|platinum|anniversary|launch|day one|bonus|extended)\s+)+edition\b")]
    private static partial Regex EditionRegex();

    [GeneratedRegex(@"\b(?:goty|game of the year|complete pack|bundle)\b")]
    private static partial Regex EditionWordsRegex();

    /// <summary>"(2019)" — a disambiguating year, not part of the name. Bare numbers ("Cyberpunk 2077") are kept.</summary>
    [GeneratedRegex(@"\(\s*(?:19|20)\d{2}\s*\)")]
    private static partial Regex ParenthesizedYearRegex();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonAlphanumericRegex();

    /// <summary>"The Witcher® 3: Wild Hunt – GOTY [v4.04]" → "the witcher 3 wild hunt goty".</summary>
    public static string NormalizeForSearch(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        var cleaned = MetadataParser.CleanTitle(title).Title;
        return Fold(string.IsNullOrWhiteSpace(cleaned) ? title : cleaned);
    }

    /// <summary>Lower-case, accent-free, punctuation-free text with Roman numerals as digits (no release-tag removal).</summary>
    public static string Fold(string text)
    {
        var decomposed = text.Replace("&", " and ", StringComparison.Ordinal)
            .Replace("'", string.Empty, StringComparison.Ordinal)
            .Replace("’", string.Empty, StringComparison.Ordinal)
            .Normalize(NormalizationForm.FormD);

        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(char.ToLowerInvariant(c));
        }

        var words = NonAlphanumericRegex().Replace(builder.ToString(), " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => RomanNumerals.TryGetValue(w, out var digits) ? digits : w);
        return string.Join(' ', words);
    }

    /// <summary>Key used to recognise the same game on different sites.</summary>
    public static string DedupKey(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        var cleaned = MetadataParser.CleanTitle(title).Title;
        if (string.IsNullOrWhiteSpace(cleaned)) cleaned = title;
        var text = Fold(ParenthesizedYearRegex().Replace(cleaned, " "));
        if (text.Length == 0) return string.Empty;

        text = EditionRegex().Replace(text, " ");
        text = EditionWordsRegex().Replace(text, " ");
        text = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (text.StartsWith("the ", StringComparison.Ordinal)) text = text[4..];
        return text.Trim();
    }

    [GeneratedRegex(@"\s*[-–—:(\[]*\s*(?:free\s+)?(?:demo version|demo|playtest|prologue demo)\s*[)\]]*\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex DemoSuffixRegex();

    [GeneratedRegex(@"\b(?:demo|playtest)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DemoWordRegex();

    /// <summary>
    /// True when the title names a demo or playtest ("Hollow Depths Demo", "Hollow Depths (Free Demo)", "Demo: Hollow Depths").
    /// Whole words only, so "Demolition Derby" is a game.
    /// </summary>
    public static bool IsDemo(string? title) => !string.IsNullOrWhiteSpace(title) && DemoWordRegex().IsMatch(title);

    /// <summary>"Hollow Depths Demo" / "Hollow Depths (Free Demo)" / "Hollow Depths Playtest" → "Hollow Depths".</summary>
    public static string WithoutDemo(string title)
    {
        var stripped = DemoSuffixRegex().Replace(title, string.Empty).Trim();
        return stripped.Length >= 2 ? stripped : title;
    }

    /// <summary>Words of a normalised string.</summary>
    public static string[] Tokens(string normalized) => normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
