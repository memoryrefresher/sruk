using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using SeaRaider.Core.Catalog;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Scraping;

/// <summary>The start or end HTML the user gave wasn't found on the page (the message says which).</summary>
public sealed class FeedMarkerException(string message) : Exception(message);

/// <summary>
/// Reads a list of game names off a page or feed: only the part of the page between a "start" and an "end" piece
/// of HTML that the user pasted. What counts as a name depends on what that part looks like:
/// <list type="number">
///   <item>RSS / Atom items: each item's &lt;title&gt; (not its link, date, author or excerpt).</item>
///   <item>Blog-style HTML with two or more headings: the headings (post titles), not "Continue reading", dates or excerpts.</item>
///   <item>Anything else (a plain list): each line of text (list item, paragraph, table row …).</item>
/// </list>
/// Whatever the route, <see cref="LooksLikeGameName"/> throws out page furniture ("Continue reading", dates, links, excerpts).
/// The games' pictures, descriptions and details come later, from Steam (see <see cref="UpcomingService"/>).
/// </summary>
public static partial class CustomFeedParser
{
    public const int MaxItems = 150;

    [GeneratedRegex(@"<!--.*?-->|<(script|style)\b.*?</\1\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex NoiseRegex();

    // Where a new line of text starts: block elements and line breaks.
    [GeneratedRegex(@"<\s*/?\s*(?:br|hr|p|div|li|ul|ol|dl|dt|dd|tr|th|table|h[1-6]|article|section|header|footer|blockquote|pre)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacesRegex();

    // "1. ", "12) ", "- ", "• " in front of a name
    [GeneratedRegex(@"^(?:\d{1,3}[\.\)]\s+|[\-\*•·–—▪►▶●○]+\s*)")]
    private static partial Regex BulletRegex();

    // <title>…</title> of a feed item
    [GeneratedRegex(@"<title\b[^>]*>(.*?)</title\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ItemTitleRegex();

    // Something only a feed has (so a stray <title> in an ordinary page isn't mistaken for a feed item).
    [GeneratedRegex(@"<\s*(?:item|entry|pubDate|guid|updated|published)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FeedHintRegex();

    // A feed item's bookkeeping elements, with their content
    [GeneratedRegex(@"<(link|guid|pubDate|dc:creator|author|category|comments|wfw:commentRss|slash:comments|updated|published|enclosure)\b[^>]*?(?:/>|>.*?</\1\s*>)",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex FeedMetaRegex();

    // A feed item's post text (what the list would otherwise be read from line by line)
    [GeneratedRegex(@"<\s*(?:content:encoded|description|summary|content)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FeedBodyRegex();

    [GeneratedRegex(@"<h([1-6])\b[^>]*>(.*?)</h\1\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex HeadingRegex();

    // Page furniture that is never a game name: "Continue reading »", "Read more", "3 Comments", "Posted in …", "Share" …
    // (Kept narrow on purpose: "Next Level", "Home" or "Like" could be real games.)
    [GeneratedRegex(
        @"^(?:continue(?:\s+reading)?|read(?:\s+(?:more|on|the\s+rest|full\s+(?:story|article|post)|article|post))?|more\s+info|view\s+(?:all|more|post|article|details)|learn\s+more|click\s+here|see\s+(?:more|all)|" +
        @"leave\s+a\s+(?:comment|reply)|(?:\d+|no|one)\s+comments?|comments?|share(?:\s+this)?|tweet|pin\s+it|permalink|tags?|categories|category|uncategori[sz]ed|" +
        @"(?:posted|written|submitted|published|updated|filed)\s+(?:in|on|by|under|at)\b.*|author|admin|administrator|(?:previous|next)(?:\s+(?:post|posts|page|article|entry))?|older\s+posts?|newer\s+posts?|subscribe\b.*|" +
        @"related\s+posts?|recent\s+posts?|archives|skip\s+to\s+content|coming\s+soon|tba|tbd|tbc|(?:add\s+to\s+)?wishlist|release\s+date\b.*|" +
        @"the\s+post\s+.*\s+appeared\s+first\s+on\s+.*|.*\s+appeared\s+first\s+on\s+.*|\[?(?:…|\.{3})\]?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FurnitureRegex();

    // "October 12, 2026", "12 Oct 2026", "Mon, 12 Oct 2026 14:03:00 +0000" (must contain a digit, and months are matched whole,
    // so a game called "March" or "Marble 2" survives)
    [GeneratedRegex(
        @"^(?=.*\d)(?:(?:mon|tues?|wed(?:nes)?|thu(?:rs?)?|fri|sat(?:ur)?|sun)(?:day)?\.?,?\s+)?(?:\d{1,2}(?:st|nd|rd|th)?\s+)?" +
        @"(?:jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|june?|july?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)\.?" +
        @"(?:\s+\d{1,2}(?:st|nd|rd|th)?)?,?(?:\s+\d{2,4})?" +
        @"(?:[\s,]+\d{1,2}:\d{2}(?::\d{2})?(?:\s*[ap]m)?(?:\s+(?:[+\-]\d{4}|[a-z]{2,4}))?)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WrittenDateRegex();

    // "2026-10-12", "10/12/2026", "3 days ago", "Q1 2027", "2027"
    [GeneratedRegex(@"^(?:\d{4}-\d{2}-\d{2}.*|\d{1,2}[/.]\d{1,2}[/.]\d{2,4}|\d+\s+(?:second|minute|hour|day|week|month|year)s?\s+ago|(?:q[1-4]|h[12])\s+\d{4}|(?:19|20)\d{2})$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NumericDateRegex();

    // The inside of a game's post, which is never a game name: "Company: Oxymoron Games", "Genres/Tags: Action", "Repack Size: 4 GB",
    // "Download Mirrors (Direct Links)", "Click to show direct links". A page or feed that carries whole posts has these lines
    // between the names. Labels only when followed by a colon or bracket, so "Tag: The Power of Paint" (singular) still passes.
    [GeneratedRegex(
        @"^(?:compan(?:y|ies)|developers?|publishers?|genres?(?:\s*/\s*tags)?|tags|languages?|original\s+size|repack\s+size|file\s*size|release\s+date|platforms?|" +
        @"version|downloads?|download\s+mirrors?|mirrors?|file\s*hosters?|system\s+requirements|screenshots?|repack\s+features|game\s+description|description|" +
        @"installation|password|torrent|magnet)\s*[:：(\[]|" +
        @"^(?:click|tap|press)\s+(?:to|here|on)\b|^(?:show|hide|expand|collapse)\s+(?:all|more|less|direct|download|links?|spoilers?|mirrors?)\b|" +
        @"\bdirect\s+links?\b|\bdownload\s+mirrors?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PostContentRegex();

    /// <summary>The names found between <paramref name="startHtml"/> and <paramref name="endHtml"/>, as upcoming releases.</summary>
    /// <exception cref="FeedMarkerException">A marker isn't on the page, or nothing that looks like a game is between them.</exception>
    public static List<UpcomingRelease> Parse(string content, string? startHtml, string? endHtml, Guid sourceId) =>
        PreviewNames(content, startHtml, endHtml)
            .Select(name => new UpcomingRelease { Key = TitleNormalizer.DedupKey(name), SourceId = sourceId, Title = name, FindOnSteam = true })
            .ToList();

    /// <summary>
    /// The game names <see cref="Parse"/> would read for these markers (the same reader, so what the user is shown is exactly
    /// what the shelf will get): duplicates and demos dropped, at most <see cref="MaxItems"/>.
    /// </summary>
    /// <exception cref="FeedMarkerException">A marker isn't on the page, or nothing that looks like a game is between them.</exception>
    public static List<string> PreviewNames(string content, string? startHtml, string? endHtml)
    {
        var region = Region(content, startHtml, endHtml, out var error)
                     ?? throw new FeedMarkerException(error ?? "Couldn't find the start HTML on the page.");

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in Names(region, StartsMidText(startHtml)))
        {
            var key = TitleNormalizer.DedupKey(name);
            if (key.Length < 2 || TitleNormalizer.IsDemo(name) || !seen.Add(key)) continue;
            names.Add(name);
            if (names.Count >= MaxItems) break;
        }

        if (names.Count == 0)
            throw new FeedMarkerException("Found the start and end HTML, but no game names between them. Pick a start and end that wrap just the list.");
        return names;
    }

    /// <summary>How many characters of the page lie between the two markers (a tighter region has fewer); int.MaxValue if they aren't there.</summary>
    public static int RegionLength(string content, string? startHtml, string? endHtml) =>
        Region(content, startHtml, endHtml, out _)?.Length ?? int.MaxValue;

    /// <summary>The text between the markers; a feed keeps its HTML escaped (&amp;lt;li&amp;gt;…), so look again with the escapes undone.</summary>
    private static string? Region(string content, string? startHtml, string? endHtml, out string? error)
    {
        var region = Slice(content, startHtml, endHtml, out error);
        if (region is not null || !content.Contains("&lt;", StringComparison.Ordinal)) return region;
        var decoded = Slice(WebUtility.HtmlDecode(content), startHtml, endHtml, out var decodedError);
        if (decoded is null) error ??= decodedError;
        return decoded;
    }

    /// <summary>
    /// The start marker stops in the middle of an element's text ("&lt;title&gt;Upcoming"): what follows up to the next tag is the
    /// rest of that title ("Games - Example"), not a game.
    /// </summary>
    private static bool StartsMidText(string? startHtml)
    {
        var start = startHtml?.TrimEnd();
        return !string.IsNullOrEmpty(start) && start[0] == '<' && start[^1] != '>';
    }

    /// <summary>The text between the two markers (case and spacing don't matter), or null with the reason in <paramref name="error"/>.</summary>
    private static string? Slice(string content, string? startHtml, string? endHtml, out string? error)
    {
        error = null;
        var begin = 0;
        if (!string.IsNullOrWhiteSpace(startHtml))
        {
            var start = Marker(startHtml).Match(content);
            if (!start.Success)
            {
                error = "Couldn't find your start HTML on the page. Copy it exactly as it appears in the page's source (right-click, View page source).";
                return null;
            }
            begin = start.Index + start.Length;
        }

        var stop = content.Length;
        if (!string.IsNullOrWhiteSpace(endHtml))
        {
            var end = Marker(endHtml).Match(content, begin);
            if (!end.Success)
            {
                error = "Found your start HTML, but not the end HTML after it. Copy the end exactly as it appears in the page's source.";
                return null;
            }
            stop = end.Index;
        }
        return content[begin..stop];
    }

    /// <summary>
    /// A pasted bit of HTML as a pattern: whitespace of any amount matches, case doesn't matter, and an empty attribute
    /// (<c>href=""</c>) stands for "whatever address is there", since a link address changes from post to post.
    /// </summary>
    private static Regex Marker(string html)
    {
        var parts = html.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => Regex.Escape(part).Replace("=\"\"", "=\"[^\"]*\"", StringComparison.Ordinal).Replace("=''", "='[^']*'", StringComparison.Ordinal));
        return new Regex(string.Join(@"\s*", parts), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    }

    /// <summary>The game names in the region: feed item titles, else post headings, else one per line of text.</summary>
    private static IEnumerable<string> Names(string region, bool startsMidText = false)
    {
        // Inside a feed the HTML is often escaped (&lt;h2&gt;…), and titles may be wrapped in CDATA.
        var work = region.Replace("<![CDATA[", " ", StringComparison.Ordinal).Replace("]]>", " ", StringComparison.Ordinal);
        if (work.Contains("&lt;", StringComparison.Ordinal)) work = WebUtility.HtmlDecode(work);
        if (startsMidText && work.IndexOf('<') is var firstTag and > 0) work = work[firstTag..]; // the rest of the marker's own title

        // 1. A feed: the title of each item.
        if (FeedHintRegex().IsMatch(work))
        {
            var titles = ItemTitleRegex().Matches(work).Select(m => Clean(m.Groups[1].Value)).Where(LooksLikeGameName).ToList();
            if (titles.Count > 0) return titles;
            // A feed's items carry whole posts (content / description). Reading those line by line gives "Company: …" and
            // "Click to show direct links" as games, so a feed with no usable titles gives no names at all.
            if (FeedBodyRegex().IsMatch(work)) return [];
        }

        // Past this point a feed's own bookkeeping (link, date, author, category) isn't a list of games.
        if (FeedHintRegex().IsMatch(work)) work = FeedMetaRegex().Replace(work, " ");

        // 2. Blog-style posts (each with a heading, then "Continue reading", a date, an excerpt …): the headings only.
        // Two or more, so a single "Upcoming games" heading above a plain list isn't taken for the list.
        var headings = HeadingRegex().Matches(work).Select(m => Clean(m.Groups[2].Value)).Where(LooksLikeGameName).ToList();
        if (headings.Count >= 2) return headings;

        // 3. A plain list: one name per line of text.
        return Lines(work).Where(LooksLikeGameName).ToList();
    }

    private static IEnumerable<string> Lines(string html)
    {
        var text = NoiseRegex().Replace(html, " ");
        text = LineBreakRegex().Replace(text, "\n");
        text = TagRegex().Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        foreach (var raw in text.Split('\n')) yield return CleanLine(raw);
    }

    /// <summary>The visible text of a piece of HTML, on one line.</summary>
    private static string Clean(string html)
    {
        var text = NoiseRegex().Replace(html, " ");
        text = TagRegex().Replace(text, " ");
        return CleanLine(WebUtility.HtmlDecode(text));
    }

    private static string CleanLine(string raw)
    {
        var line = SpacesRegex().Replace(raw.Replace('\u00A0', ' '), " ").Trim();
        return TidyTitle(BulletRegex().Replace(line, string.Empty));
    }

    /// <summary>
    /// A game's name without the decoration a page puts around it: arrows ("→ Alice", "Alice »"), bullets, dashes, other
    /// symbols and emoji at either end, and ™ / ®. Punctuation that can belong to a name ("#Hashtag", "[Redacted]", "Hades!") stays.
    /// </summary>
    public static string TidyTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        var text = title.Trim();
        int start = 0, end = text.Length;
        while (start < end && IsDecoration(text[start])) start++;
        while (end > start && IsDecoration(text[end - 1])) end--;
        var tidy = text[start..end].Trim();
        return tidy.Length >= 2 ? tidy : text;
    }

    private static bool IsDecoration(char c)
    {
        if (char.IsWhiteSpace(c) || char.IsSurrogate(c)) return true; // emoji come as surrogate pairs
        if (c is '+' or '~') return false; // can start a name ("+Ultra")
        return CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.MathSymbol or UnicodeCategory.OtherSymbol
            or UnicodeCategory.ModifierSymbol or UnicodeCategory.DashPunctuation or UnicodeCategory.InitialQuotePunctuation
            or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.Format
            || c is '*' or '•' or '·' or '|' or '\u200B';
    }

    /// <summary>
    /// False for text that can't be a game's name: too short or long, a web address, a date, a sentence (an excerpt),
    /// or page furniture such as "Continue reading", "Read more", "3 Comments" or "Posted in News".
    /// </summary>
    internal static bool LooksLikeGameName(string line)
    {
        if (line.Length is < 2 or > 80 || !line.Any(char.IsLetterOrDigit)) return false;
        if (line.Contains("://", StringComparison.Ordinal) || line.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) return false;
        if (line.Contains('@') && !line.Contains(' ')) return false; // an e-mail address

        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (words > 10) return false; // an excerpt, not a name
        if (words >= 5 && line.EndsWith('.')) return false; // a sentence

        // Arrows and ellipses that come with "Continue reading »" / "Read more →"
        var probe = line.Trim('«', '‹', '»', '›', '→', '>', '<', '…', ':', ' ').Trim();
        if (probe.Length < 2) return false;
        return !FurnitureRegex().IsMatch(probe) && !PostContentRegex().IsMatch(probe) && !WrittenDateRegex().IsMatch(probe) && !NumericDateRegex().IsMatch(probe);
    }
}
