using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using SeaRaider.Core.Catalog;

namespace SeaRaider.Core.Scraping;

/// <summary>One guess at where a custom list starts and ends, with the game names that guess would read.</summary>
public sealed record FeedRegionCandidate(string StartHtml, string EndHtml, IReadOnlyList<string> Names);

/// <summary>
/// Works out a custom list's start and end HTML for the user. They type the name of one game that is on the page right now;
/// the finder looks for it in the page's source, proposes start/end markers around it (a tag plus the first words after it, like
/// <c>&lt;title&gt;Upcoming</c>, with anything that changes from post to post, such as a link address, left out), and runs each
/// proposal through the real reader (<see cref="CustomFeedParser"/>). Only proposals that really read that game are kept, best
/// first: the tightest region that reads at least two games. The user looks at each one's games and answers yes or no.
/// </summary>
public static partial class FeedRegionFinder
{
    private const int Window = 60_000;      // how far from the game to look for a marker
    private const int MaxSamples = 4;       // places the game's name is found on the page
    private const int MaxMarkers = 8;       // distinct start / end markers tried per place
    private const int MaxTries = 160;       // start+end pairs run through the reader
    private const int MaxResults = 6;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacesRegex();

    // Tags that begin a part of the page, looking backwards from the game for where the list begins.
    [GeneratedRegex(@"<(?:item|entry|article|section|main|ul|ol|dl|table|tbody|h[1-6]|title|content:encoded|div)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex OpenerRegex();

    // Where the list can end, looking forward from the game: the closing of a block, the start of the next post, or the
    // "The post … appeared first on …" line a WordPress feed puts after every post.
    [GeneratedRegex(
        @"</(?:ul|ol|dl|table|tbody|item|entry|article|section|main|aside|nav|footer|content:encoded|channel|div)\s*>" +
        @"|<(?:item|entry|article|section|footer|aside|nav|hr|h[1-6])\b[^>]*>" +
        @"|<p\b[^>]*>(?=\s*(?:the\s+post|related|share|posted|read\s+more|continue))",
        RegexOptions.IgnoreCase)]
    private static partial Regex CloserRegex();

    // The page as a run of tokens: text, one tag, a CDATA opener or a comment.
    [GeneratedRegex(@"<!\[CDATA\[|<!--.*?-->|<[^>]*>|[^<]+|<", RegexOptions.Singleline)]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"([A-Za-z_:][\w:.-]*)\s*=\s*(""[^""]*""|'[^']*')")]
    private static partial Regex AttributeRegex();

    [GeneratedRegex(@"\S+")]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"\d{3,}")]
    private static partial Regex LongNumberRegex();

    [GeneratedRegex(@"^<\s*([A-Za-z][\w:.-]*)")]
    private static partial Regex TagNameRegex();

    /// <summary>
    /// Start/end guesses for the list that contains <paramref name="gameName"/>, best first (empty if the page doesn't show
    /// that name, or no guess reads it).
    /// </summary>
    public static IReadOnlyList<FeedRegionCandidate> Find(string content, string gameName)
    {
        var typed = SpacesRegex().Replace(WebUtility.HtmlDecode(gameName ?? string.Empty).Trim(), " ");
        var wantedKey = TitleNormalizer.DedupKey(typed);
        if (typed.Length < 2 || wantedKey.Length < 2) return [];

        // A feed keeps its HTML escaped: the markers are also looked for in the unescaped page (the reader does the same).
        var documents = new List<string> { content };
        if (content.Contains("&lt;", StringComparison.Ordinal)) documents.Add(WebUtility.HtmlDecode(content));

        var tried = new HashSet<string>(StringComparer.Ordinal);
        var valid = new List<(string Start, string End, List<string> Names, int Length)>();
        var tries = 0;
        var nameRegex = NameRegex(typed);

        foreach (var doc in documents)
        {
            foreach (var hit in Occurrences(doc, nameRegex))
            {
                var starts = StartMarkers(doc, hit.Index);
                var ends = EndMarkers(doc, hit.Index + hit.Length);
                foreach (var start in starts)
                {
                    foreach (var end in ends)
                    {
                        if (tries >= MaxTries) goto ranked;
                        if (!tried.Add(start + "\u0001" + end)) continue;
                        tries++;

                        List<string> names;
                        try { names = CustomFeedParser.PreviewNames(content, start, end); }
                        catch (FeedMarkerException) { continue; }           // not on the page that way round
                        catch (RegexMatchTimeoutException) { continue; }
                        if (!names.Any(n => TitleNormalizer.DedupKey(n) == wantedKey)) continue; // doesn't read the game they typed

                        valid.Add((start, end, names, CustomFeedParser.RegionLength(content, start, end)));
                    }
                }
            }
        }

        ranked:
        if (valid.Count == 0) return [];

        // The cleanest guess first: the tightest region that reads at least two games (a wider one mostly adds menus, other
        // posts' titles and captions). Wider guesses follow, for lists that are split up or have no tidy wrapper; the user
        // says no until the right one comes. A guess that reads only the one game typed comes last.
        var ordered = valid.Where(v => v.Names.Count >= 2).OrderBy(v => v.Length)
            .Concat(valid.Where(v => v.Names.Count < 2).OrderBy(v => v.Length));

        var results = new List<FeedRegionCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in ordered)
        {
            if (!seen.Add(string.Join("\u0001", v.Names))) continue; // the same games again, only a different way of marking them
            results.Add(new FeedRegionCandidate(v.Start, v.End, v.Names));
            if (results.Count >= MaxResults) break;
        }
        return results;
    }

    /// <summary>Whether the name shows up anywhere in the page's text (also inside an escaped feed), to tell "not on the page" from "no guess works".</summary>
    public static bool ContainsName(string content, string gameName)
    {
        var typed = SpacesRegex().Replace(WebUtility.HtmlDecode(gameName ?? string.Empty).Trim(), " ");
        if (typed.Length < 2) return false;
        var regex = NameRegex(typed);
        try
        {
            return Occurrences(content, regex).Any() ||
                   (content.Contains("&lt;", StringComparison.Ordinal) && Occurrences(WebUtility.HtmlDecode(content), regex).Any());
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    // ---- finding the game on the page -------------------------------------------------------------

    /// <summary>The name as a pattern: any spacing, HTML spellings of "&amp;" and apostrophes, whole words only.</summary>
    private static Regex NameRegex(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(word => string.Concat(word.Select(ch => ch switch
        {
            '&' => "(?:&|&amp;|&#0*38;|&#x26;)",
            '\'' or '’' => "(?:'|’|&#0*39;|&#0*8217;|&apos;|&rsquo;)",
            _ => Regex.Escape(ch.ToString()),
        })));
        return new Regex(@"(?<![\p{L}\p{N}])" + string.Join(@"(?:\s|&nbsp;|&#160;)+", words) + @"(?![\p{L}\p{N}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);
    }

    /// <summary>Where the name shows as page text (not inside a tag's attributes), first few only.</summary>
    private static IEnumerable<Match> Occurrences(string doc, Regex nameRegex)
    {
        var found = 0;
        Match match;
        try { match = nameRegex.Match(doc); }
        catch (RegexMatchTimeoutException) { yield break; }
        while (match.Success && found < MaxSamples)
        {
            var insideTag = doc.LastIndexOf('<', match.Index) > doc.LastIndexOf('>', match.Index);
            if (!insideTag)
            {
                found++;
                yield return match;
            }
            try { match = match.NextMatch(); }
            catch (RegexMatchTimeoutException) { yield break; }
        }
    }

    // ---- proposing markers ------------------------------------------------------------------------

    /// <summary>Start markers: the tags that open a part of the page before the game, nearest first.</summary>
    private static List<string> StartMarkers(string doc, int gamePos)
    {
        var from = Math.Max(0, gamePos - Window);
        var before = doc.Substring(from, gamePos - from);
        var openers = OpenerRegex().Matches(before).Cast<Match>().Reverse().ToList(); // nearest first

        var markers = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var opener in openers)
        {
            var at = from + opener.Index;
            var name = TagNameRegex().Match(opener.Value).Groups[1].Value.ToLowerInvariant();
            var isTitle = name is "title" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6";

            // A title or heading is named by its first words ("<title>Upcoming"); a container by its tag(s) only.
            var variants = isTitle
                ? new[] { Marker(doc, at, gamePos, Mode.Words), Marker(doc, at, gamePos, Mode.Tag) }
                : new[] { Marker(doc, at, gamePos, Mode.Tag), Marker(doc, at, gamePos, Mode.Tags) };
            foreach (var marker in variants)
                if (marker is { Length: > 0 } && seen.Add(marker)) markers.Add(marker);
            if (seen.Count >= MaxMarkers * 2) break;
        }
        return markers;
    }

    /// <summary>End markers: what can close the list after the game, nearest first, and finally "the end of the page".</summary>
    private static List<string> EndMarkers(string doc, int gameEnd)
    {
        var to = Math.Min(doc.Length, gameEnd + Window);
        var after = doc.Substring(gameEnd, to - gameEnd);

        var markers = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match closer in CloserRegex().Matches(after))
        {
            var at = gameEnd + closer.Index;
            var isClosing = closer.Value.StartsWith("</", StringComparison.Ordinal);
            var marker = Marker(doc, at, doc.Length, isClosing ? Mode.Tag : Mode.Words);
            if (marker is { Length: > 0 } && seen.Add(marker)) markers.Add(marker);
            if (markers.Count >= MaxMarkers) break;
        }
        markers.Add(string.Empty); // nothing marks the end: read to the end of the page
        return markers;
    }

    private enum Mode
    {
        /// <summary>Just the tag.</summary>
        Tag,
        /// <summary>The tag and the tags right after it (no text).</summary>
        Tags,
        /// <summary>The tag and the first words after it (and the next tag if the words end first), like "&lt;p&gt;The post &lt;a".</summary>
        Words,
    }

    /// <summary>
    /// The piece of page source starting at <paramref name="tagAt"/> that makes a marker, never reaching <paramref name="limit"/>
    /// (a start marker must not swallow the game it is meant to read). Anything that changes between posts (a link address, an
    /// image, a numbered id) is cut off: "&lt;a href=…&gt;" becomes "&lt;a".
    /// </summary>
    private static string? Marker(string doc, int tagAt, int limit, Mode mode)
    {
        var token = TokenRegex().Match(doc, tagAt);
        if (!token.Success || token.Index != tagAt || !token.Value.StartsWith('<')) return null;

        var sb = new StringBuilder();
        if (!AppendTag(sb, token.Value)) return Finish(sb);
        if (mode == Mode.Tag) return Finish(sb);

        var tags = 0;
        var words = 0;
        for (token = token.NextMatch(); token.Success && token.Index < limit; token = token.NextMatch())
        {
            var value = token.Value;
            if (value.StartsWith("<![CDATA[", StringComparison.Ordinal))
            {
                sb.Append(value);
                continue;
            }

            if (value.StartsWith('<'))
            {
                if (mode == Mode.Tags && tags < 2)
                {
                    tags++;
                    if (!AppendTag(sb, value)) break;
                    continue;
                }
                if (mode == Mode.Words && words > 0) AppendTag(sb, value); // "The post " + "<a"
                break;
            }

            if (mode == Mode.Tags) break;

            // Words: the first two, stopping before anything with a digit in it (a date or number that changes), or at the limit.
            var text = value.Length > limit - token.Index ? value[..(limit - token.Index)] : value;
            var cut = text.Length;
            var stop = text.Length < value.Length;
            foreach (Match word in WordRegex().Matches(text))
            {
                if (words == 2 || word.Value.Any(char.IsDigit))
                {
                    cut = word.Index;
                    stop = true;
                    break;
                }
                words++;
            }
            sb.Append(text[..cut]);
            if (stop) break;
        }
        return Finish(sb);
    }

    private static string? Finish(StringBuilder sb)
    {
        var marker = sb.ToString().TrimEnd();
        return marker.Length >= 2 ? marker : null;
    }

    /// <summary>
    /// Adds the tag as it is on the page, or - when it carries something that changes (a link, a numbered id, any attribute but
    /// class / id / rel / type / role) - just its start ("&lt;a"). Returns false when it was cut short, so nothing may follow.
    /// </summary>
    private static bool AppendTag(StringBuilder sb, string tag)
    {
        var attributes = AttributeRegex().Matches(tag);
        var stable = attributes.Cast<Match>().All(a =>
        {
            var key = a.Groups[1].Value.ToLowerInvariant();
            var value = a.Groups[2].Value;
            return key is "class" or "id" or "rel" or "type" or "role" && !value.Contains("://", StringComparison.Ordinal) &&
                   !LongNumberRegex().IsMatch(value);
        });
        if (stable)
        {
            sb.Append(tag);
            return true;
        }
        sb.Append(TagNameRegex().Match(tag) is { Success: true } m ? "<" + m.Groups[1].Value : tag);
        return false;
    }
}
