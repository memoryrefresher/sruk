using System.Text.RegularExpressions;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Catalog;

/// <summary>
/// Recognises posts on game sites that aren't games: FitGirl's "Updates Digest for October 15, 2026" and "Upcoming
/// Repacks", donation calls, site announcements, FAQs, "beware of fake sites" warnings. The store hides them (Settings →
/// General → Store), and any card can also be hidden by hand with its × button.
///
/// <para>Deliberately title-based and narrow: a game called "News Tower" or "Digest Simulator" must stay. Every pattern
/// is anchored to how sites phrase these posts, not to single words.</para>
/// </summary>
public static partial class NonGameFilter
{
    [GeneratedRegex(
        @"^\s*(?:" +
        @"updates?\s+digest|upcoming\s+repacks?|repacks?\s+(?:list|index|digest)|updates?\s+list\b|" +
        @"(?:a\s+)?call\s+for\s+donations?|support\s+(?:us|the\s+site)\b|" +
        @"(?:site|website|important|short|small)\s+(?:news|announcement|update|notice|status)|announcements?\s*[:!\-–]|" +
        @"(?:important\s+)?notice\s*[:!\-–]|statement\s+(?:from|about|regarding)|" +
        @"faq\b|frequently\s+asked|how\s+to\s+(?:install|download|use|support)|q\s*&\s*a\b|" +
        @"beware\b|fake\s+(?:sites?|websites?|repacks?)|scam\s+(?:sites?|alert)|" +
        @"happy\s+new\s+year|merry\s+christmas|happy\s+holidays|" +
        @"(?:weekly|monthly)\s+(?:digest|recap|roundup)|giveaway\b|" +
        @"site\s+(?:is\s+)?(?:down|back|moving|maintenance)|maintenance\s*[:!\-–]|we\s+(?:are|have)\s+moved\b" +
        @")", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NotAGameTitleRegex();

    [GeneratedRegex(@"\b(?:digest|upcoming\s+repacks|donat\w+|announcement)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WeakSignalRegex();

    /// <summary>True when the post looks like site news rather than a game.</summary>
    public static bool IsNotAGame(string? title, string? description = null, bool hasGameFacts = false)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        if (NotAGameTitleRegex().IsMatch(title)) return true;

        // "October 2026 Updates Digest", "Repack news: upcoming repacks": a weaker word anywhere in the title, only when
        // the post has nothing a game page has (size, version, genre).
        return !hasGameFacts && WeakSignalRegex().IsMatch(title);
    }

    /// <inheritdoc cref="IsNotAGame(string?, string?, bool)"/>
    public static bool IsNotAGame(CatalogEntry entry) =>
        IsNotAGame(entry.Title, entry.ShortDescription,
            hasGameFacts: !string.IsNullOrWhiteSpace(entry.Size) || !string.IsNullOrWhiteSpace(entry.Version) || entry.Genres.Count > 0);
}
