using System.Text.RegularExpressions;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Catalog;

/// <summary>
/// Spots games with nudity or sexual content from what a store card carries (title, genres / tags, teaser, page address),
/// for the store's "Hide adult games" option. Words only, whole words, so ordinary titles aren't caught ("Sextant", "Nudge").
/// It is a keyword check, not a rating: a game that never says what it is can still get through, and a news-style teaser that
/// mentions one of these words can be caught. To tune it, edit the two patterns below.
/// </summary>
public static partial class AdultContentFilter
{
    [GeneratedRegex(
        @"(?<![\p{L}\p{N}])(?:hentai|nsfw|porn|porno|pornographic|erotic|erotica|eroge|ecchi|lewd|futanari|xxx|uncensored|nudity|nude|nudes|adults?[\s-]+only|adult[\s-]+(?:game|content|visual novel)s?|sex(?:ual)?[\s-]+(?:content|scenes?|games?|themes?|simulator)|explicit[\s-]+(?:sex|content)|r-?18|18[\s-]*plus)(?![\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AdultWordsRegex();

    /// <summary>"18+" / "+18", which a word boundary can't express.</summary>
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:18\s*\+|\+\s*18)(?![\p{L}\p{N}])", RegexOptions.CultureInvariant)]
    private static partial Regex EighteenPlusRegex();

    public static bool IsAdult(string? text) =>
        !string.IsNullOrWhiteSpace(text) && (AdultWordsRegex().IsMatch(text) || EighteenPlusRegex().IsMatch(text));

    public static bool IsAdult(CatalogEntry entry) =>
        IsAdult($"{entry.Title} {string.Join(' ', entry.Genres)} {entry.ShortDescription} {entry.PageUrl}");
}
