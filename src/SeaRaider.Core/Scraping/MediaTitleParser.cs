using System.Net;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Scraping;

/// <summary>
/// Reads a media site the way the store reads a game site: the list of titles on its pages (a grid of posters, each linking
/// to the title's own page), not the videos embedded on one page. Each title becomes a box on the Media tab's Browse grid;
/// the title's page is only opened when it is played (<see cref="FindPlayer"/>), where its player is looked for.
///
/// <para>How the titles are found, on any site, without knowing it:</para>
/// <list type="number">
///   <item>Every link to another page of the same site is a candidate. A card often has two links to the same page (the poster
///         and the name): they are merged, taking the name from one and the picture from the other.</item>
///   <item>Links are grouped by where they sit in the page (the tag and class of the link and its three parents). A grid of
///         titles is a group of many links in the same place, most of them with a picture.</item>
///   <item>Menus, footers, genre / category / page links and "Read more" style labels are left out.</item>
/// </list>
/// </summary>
public static partial class MediaTitleParser
{
    /// <summary>Titles kept per source (all pages together).</summary>
    public const int MaxTitles = 2000;

    /// <summary>Sections of a site followed (its categories, "Movies", "TV Shows" …), each with its own list pages.</summary>
    public const int MaxSections = 30;

    /// <summary>List pages read per section (page 1, 2, 3 …).</summary>
    public const int MaxSectionPages = 5;

    /// <summary>Pages of a site's list followed (page 1, 2, 3 …).</summary>
    public const int MaxPages = 10;

    /// <summary>A group of links needs at least this many members to be a list of titles.</summary>
    private const int MinGroup = 4;

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacesRegex();

    // Paths that are lists or site pages, not one title.
    [GeneratedRegex(@"/(?:genres?|category|categories|tags?|country|countries|years?|release-year|page|search|login|log-in|signin|sign-in|register|signup|sign-up|account|user|profile|contact|dmca|faq|about|privacy|terms|request|az-list|a-z|letter|filter|browse|sort|cast|actors?|directors?|studios?|networks?|top-imdb|feed|wp-login\.php|wp-admin)(?:/|$|\?)", RegexOptions.IgnoreCase)]
    private static partial Regex ListPathRegex();

    [GeneratedRegex(@"[?&](?:page|paged|p|sort|order|orderby|genre|cat|tag|s|q|search|keyword)=", RegexOptions.IgnoreCase)]
    private static partial Regex ListQueryRegex();

    // Labels that are buttons, not names.
    [GeneratedRegex(@"^(?:watch(?:\s+(?:now|online|free|trailer|movie|it|here))?|play(?:\s+now)?|more|more\s+info|info|details|view(?:\s+(?:more|all|details))?|(?:match\s+)?poster|thumbnail|image|cover|read\s+more|see\s+(?:more|all)|trailer|download|home|next|prev(?:ious)?|older|newer|login|log\s+in|sign\s+(?:in|up)|register|hd|cam|sd|4k|\d{1,4}|\d+\s*(?:min|mins|m)|tv|movies?|series|shows?|episodes?|new|latest|trending|popular|top|all|continue|\W*)$", RegexOptions.IgnoreCase)]
    private static partial Regex JunkLabelRegex();

    // "Movie Name (2024) Full Movie Online Free" → "Movie Name (2024)"
    [GeneratedRegex(@"\s*[-|:–—]?\s*(?:watch\s+)?(?:full\s+movie|online\s+free|free\s+online|in\s+hd|hd\s+online|online)(?:\s+(?:free|hd|online))*\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TitleTailRegex();

    [GeneratedRegex(@"^\s*watch\s+", RegexOptions.IgnoreCase)]
    private static partial Regex WatchPrefixRegex();

    // What a card says before the name: "Match Poster", "LIVE 57729" (viewers), "Oct 22", "Today 18:00".
    [GeneratedRegex(@"^(?:(?:match\s+poster|poster|thumbnail|image)\s+)?(?<live>live(?:\s+now)?\s+)?(?:(?:(?:jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?\s+\d{1,2}(?:st|nd|rd|th)?|today|tomorrow|tonight|upcoming)\s+)?(?<n>\d{1,7}\s+)?", RegexOptions.IgnoreCase)]
    private static partial Regex CardPrefixRegex();

    // What a card says after the name: "| 7:00 PM", "· 18:30".
    [GeneratedRegex(@"\s*[|•·\-–]?\s*\d{1,2}:\d{2}\s*(?:am|pm)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TimeSuffixRegex();

    // The sport after a match's name: "Bayern Munich vs Arsenal Football".
    [GeneratedRegex(@"\s+(?<sport>american\s+football|ice\s+hockey|motor\s*sports?|football|soccer|basketball|baseball|hockey|tennis|fight(?:ing)?|mma|ufc|boxing|wrestling|cricket|rugby|golf|darts|billiards|snooker|afl|volleyball|handball|f1|formula\s*1|nfl|nba|nhl|mlb|other)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SportSuffixRegex();

    // A card's own type label, on its own: "Football", "Movie", "TV Series" …
    [GeneratedRegex(@"^(?:american\s+football|ice\s+hockey|motor\s*sports?|football|soccer|basketball|baseball|hockey|tennis|fight(?:ing)?|mma|ufc|boxing|wrestling|cricket|rugby|golf|darts|billiards|snooker|afl|volleyball|handball|f1|formula\s*1|nfl|nba|nhl|mlb|movies?|films?|tv|tv\s*(?:show|series)s?|series|shows?|anime|documentar(?:y|ies)|cartoons?|kdrama)$", RegexOptions.IgnoreCase)]
    private static partial Regex TypeLabelRegex();

    // Two sides of a match: "A vs B", "A v B", "A @ B".
    [GeneratedRegex(@"^(?<a>.+?)\s+(?:vs\.?|v\.?|@)\s+(?<b>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex MatchupRegex();

    /// <summary>The two sides of a match title ("Bayern Munich vs Arsenal" → both teams), or null for anything else.</summary>
    public static (string Home, string Away)? Matchup(string title) =>
        MatchupRegex().Match(title) is { Success: true } m && m.Groups["a"].Value.Trim().Length > 1 && m.Groups["b"].Value.Trim().Length > 1
            ? (m.Groups["a"].Value.Trim(), m.Groups["b"].Value.Trim())
            : null;

    [GeneratedRegex(@"url\(\s*['""]?(?<u>[^'"")]+)['""]?\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex CssUrlRegex();

    // Players inside a title page: frames whose address or element says it's the video.
    [GeneratedRegex(@"(?:/embed|/e/|/v/|/player|/play/|/watch|/stream|/video|/movie|/tv/|[?&](?:id|v|tmdb|imdb|vid)=|player|embed|stream|video)", RegexOptions.IgnoreCase)]
    private static partial Regex PlayerHintRegex();

    [GeneratedRegex(@"(?:player|video|embed|stream|watch|iframe|server)", RegexOptions.IgnoreCase)]
    private static partial Regex PlayerContainerRegex();

    /// <summary>One link found on a page, merged with other links to the same address.</summary>
    private sealed class Candidate(string url, string group, int order)
    {
        public string Url { get; } = url;
        public string Group { get; set; } = group;
        public int Order { get; } = order;
        public string? Title { get; set; }
        public int TitleRank { get; set; } = int.MaxValue;
        public string? Image { get; set; }
        public string? Category { get; set; }
        public bool IsLive { get; set; }
    }

    /// <summary>Opens <paramref name="html"/> as a document at <paramref name="pageUri"/>.</summary>
    public static async Task<IDocument> OpenAsync(string html, Uri pageUri, CancellationToken ct)
    {
        var context = BrowsingContext.New(Configuration.Default);
        return await context.OpenAsync(req => req.Content(html).Address(pageUri.AbsoluteUri), ct).ConfigureAwait(false);
    }

    /// <summary>The titles listed on one page, in page order, each to be resolved when played.</summary>
    /// <param name="startOrder">Order of the first title (pages after the first continue the numbering).</param>
    public static List<MediaItem> ParseTitles(IDocument doc, Uri pageUri, Guid sourceId, int startOrder = 0)
    {
        var candidates = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        var order = 0;
        var pageKey = Key(pageUri);

        foreach (var anchor in doc.QuerySelectorAll("a[href]"))
        {
            if (anchor.Closest("nav, header, footer, aside, [role='navigation'], .menu, .navbar, .pagination, .breadcrumb, .breadcrumbs") is not null) continue;
            var url = Resolve(anchor.GetAttribute("href"), pageUri);
            if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var target)) continue;
            if (!SameSite(target, pageUri) || Key(target) == pageKey) continue;
            if (target.AbsolutePath.Length <= 1 || ListPathRegex().IsMatch(target.AbsolutePath) || ListQueryRegex().IsMatch(target.Query)) continue;

            var link = target.GetLeftPart(UriPartial.Query);
            var image = ImageOf(anchor, pageUri);
            var (title, rank, category, live) = TitleOf(anchor);

            if (!candidates.TryGetValue(link, out var candidate))
            {
                candidate = new Candidate(link, GroupOf(anchor), order++);
                candidates[link] = candidate;
            }
            // The poster link decides where the card sits (titles below posters are often in a different spot).
            if (candidate.Image is null && image is not null)
            {
                candidate.Image = image;
                candidate.Group = GroupOf(anchor);
            }
            if (title is not null && rank < candidate.TitleRank)
            {
                candidate.Title = title;
                candidate.TitleRank = rank;
                candidate.Category = category ?? candidate.Category;
            }
            candidate.IsLive |= live;
        }

        // A list of titles: a group with several members, most of them with a picture and a name.
        var groups = candidates.Values
            .Where(c => c.Title is not null)
            .GroupBy(c => c.Group)
            .Where(g => g.Count() >= MinGroup && g.Count(c => c.Image is not null) >= g.Count() * 0.6)
            .ToList();

        var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<MediaItem>();
        foreach (var candidate in groups.SelectMany(g => g).OrderBy(c => c.Order))
        {
            if (!seenTitles.Add(candidate.Url)) continue;
            items.Add(new MediaItem
            {
                SourceId = sourceId,
                Key = $"{sourceId:N}|title:{Key(new Uri(candidate.Url))}",
                Title = candidate.Title!,
                PlayUrl = candidate.Url,
                PageUrl = candidate.Url,
                ThumbnailUrl = candidate.Image,
                Channel = pageUri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? pageUri.Host[4..] : pageUri.Host,
                Order = startOrder + items.Count,
                NeedsResolve = true,
                IsLive = candidate.IsLive,
                Category = candidate.Category,
            });
        }
        return items;
    }

    // Section links: a site's categories, kinds and genres.
    [GeneratedRegex(@"/(?:category|categories|cat|genre|genres|sport|sports|type|types|tag|tags|movies?|films?|tv|tv-shows|tvshows|shows?|series|anime|cartoons?|kdrama|drama|documentar(?:y|ies)|live|channels?|leagues?|competitions?|collections?)(?:/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex SectionPathRegex();

    // Sections that say what kind of thing a title is (followed first: a title keeps the first section it's found in).
    [GeneratedRegex(@"/(?:movies?|films?|tv|tv-shows|tvshows|shows?|series|anime|cartoons?|kdrama|documentar(?:y|ies)|sport|sports|category|categories|cat|leagues?|competitions?|live|channels?)(?:/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex KindPathRegex();

    // Site pages that aren't lists of titles.
    [GeneratedRegex(@"^(?:home|login|log\s*in|sign\s*(?:in|up)|register|account|profile|search|dmca|contact(?:\s+us)?|faq|about(?:\s+us)?|privacy(?:\s+policy)?|terms.*|request.*|donate|discord|telegram|twitter|reddit|app|download\s+app|settings|more|menu|next|prev(?:ious)?|\d+|«|»|‹|›)$", RegexOptions.IgnoreCase)]
    private static partial Regex NotSectionRegex();

    /// <summary>
    /// The sections of a site, from its home page: its menus' links to its categories and kinds ("Football", "Basketball",
    /// "Movies", "TV Shows", "Action" …), which the crawl reads like the home page's list. Kinds come first, then genres and
    /// the rest, at most <see cref="MaxSections"/>. Each comes with the label it's shown under on the Browse grid.
    /// </summary>
    public static List<(Uri Url, string Label)> FindSections(IDocument doc, Uri pageUri)
    {
        var found = new List<(Uri Url, string Label, int Priority, int Order)>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { Key(pageUri) };
        var order = 0;
        foreach (var anchor in doc.QuerySelectorAll("a[href]"))
        {
            if (Resolve(anchor.GetAttribute("href"), pageUri) is not { } url || !Uri.TryCreate(url, UriKind.Absolute, out var target)) continue;
            if (!SameSite(target, pageUri) || target.AbsolutePath.Length <= 1) continue;
            var sectionPath = SectionPathRegex().IsMatch(target.AbsolutePath) || SectionPathRegex().IsMatch(target.Query.Replace('=', '/'));
            // A link with a picture is a title's card, unless its address says it's a section (sport links with an icon).
            if (anchor.QuerySelector("img, svg") is not null && !sectionPath) continue;
            if (ListQueryRegex().IsMatch(target.Query) && !target.Query.Contains("cat", StringComparison.OrdinalIgnoreCase) &&
                !target.Query.Contains("genre", StringComparison.OrdinalIgnoreCase) && !target.Query.Contains("type", StringComparison.OrdinalIgnoreCase))
                continue;

            // "Fight (UFC, Boxing)" → "Fight": the same name the cards show, so both land under one heading.
            var label = Undouble(Regex.Replace(Clean(SpacedText(anchor)), @"\s*\([^)]*\)", string.Empty).Trim());
            if (label.Length is < 2 or > 30 || label.Split(' ').Length > 4 || NotSectionRegex().IsMatch(label)) continue;
            var inMenu = anchor.Closest("nav, header, aside, [role='navigation'], .menu, .navbar, .nav, .sidebar, .categories, .genres, .sports, ul.dropdown-menu") is not null;
            if (!inMenu && !sectionPath) continue;
            // A menu link to a site page ("/about", "/request") isn't a section.
            if (!sectionPath && ListPathRegex().IsMatch(target.AbsolutePath)) continue;
            if (!seen.Add(Key(target))) continue;

            var priority = KindPathRegex().IsMatch(target.AbsolutePath) ? 0 : sectionPath ? 1 : 2;
            found.Add((new Uri(target.GetLeftPart(UriPartial.Query)), NiceType(SectionLabel(label, target)), priority, order++));
        }
        return found.OrderBy(f => f.Priority).ThenBy(f => f.Order).Take(MaxSections).Select(f => (f.Url, f.Label)).ToList();
    }

    /// <summary>"Basketball Basketball" (an icon's label plus the text) → "Basketball".</summary>
    internal static string Undouble(string label)
    {
        var words = label.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length % 2 == 0 && words.Length > 0)
        {
            var half = words.Length / 2;
            if (words.Take(half).SequenceEqual(words.Skip(half), StringComparer.OrdinalIgnoreCase)) return string.Join(' ', words.Take(half));
        }
        return label;
    }

    /// <summary>"football" / "tv-shows" → "Football" / "TV Shows" (the link's text when it reads well, else its address).</summary>
    private static string SectionLabel(string text, Uri url)
    {
        if (text.Length >= 2 && !text.All(c => char.IsLower(c) || c == ' ')) return text;
        var last = url.AbsolutePath.TrimEnd('/').Split('/').LastOrDefault() ?? text;
        var words = (last.Length > 1 ? last : text).Replace('-', ' ').Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select(w => w.Length <= 2 ? w.ToUpperInvariant() : char.ToUpperInvariant(w[0]) + w[1..]));
    }

    /// <summary>The page after this one in the site's list (rel="next", or a "Next" link), or null.</summary>
    public static Uri? FindNextPage(IDocument doc, Uri pageUri)
    {
        var next = doc.QuerySelector("link[rel='next'][href], a[rel='next'][href]")?.GetAttribute("href");
        if (next is null)
        {
            foreach (var a in doc.QuerySelectorAll("a[href]"))
            {
                var text = Clean(a.TextContent).ToLowerInvariant();
                var cls = (a.ClassName ?? string.Empty).ToLowerInvariant();
                var label = (a.GetAttribute("aria-label") ?? string.Empty).ToLowerInvariant();
                if (text is "next" or "next »" or "next ›" or "»" or "›" or ">" or "next page" or "older posts" or "older entries" or "load more" ||
                    label.StartsWith("next", StringComparison.Ordinal) ||
                    (cls.Contains("next", StringComparison.Ordinal) && !cls.Contains("disabled", StringComparison.Ordinal)))
                {
                    next = a.GetAttribute("href");
                    break;
                }
            }
        }
        if (Resolve(next, pageUri) is not { } url || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        return SameSite(uri, pageUri) && Key(uri) != Key(pageUri) ? uri : null;
    }

    /// <summary>
    /// What to play for a title's page: a stream written in it (.m3u8 / .mpd), else the page's video player frame, else a video
    /// file, else a trailer (YouTube / Vimeo), else the page itself shown in the player. The result is a play address for
    /// <c>TrailerPlayerService.Play</c> (embed pages and streams carry their format hint).
    /// </summary>
    public static string FindPlayer(IDocument doc, string html, Uri pageUri)
    {
        // 1. A stream address written in the page or its scripts.
        foreach (var stream in MediaStreams.FindStreamUrls(html.Replace("\\/", "/", StringComparison.Ordinal))) // "https:\/\/…" in scripts
        {
            if (Uri.TryCreate(pageUri, stream, out var abs) && abs.Scheme is "http" or "https")
                return MediaStreams.WithHint(abs.AbsoluteUri, MediaStreams.FormatOfPath(abs) ?? StreamFormat.Hls);
        }

        // 2. The player frame: the best-looking frame that isn't a trailer, an ad or a widget.
        string? trailer = null;
        (string Url, int Score)? best = null;
        foreach (var frame in doc.QuerySelectorAll("iframe"))
        {
            var src = Resolve(frame.GetAttribute("src") ?? frame.GetAttribute("data-src") ?? frame.GetAttribute("data-lazy-src"), pageUri);
            if (src is null || !Uri.TryCreate(src, UriKind.Absolute, out var frameUri)) continue;
            if (MediaUrlHelper.TryGetYouTubeId(src) is not null || frameUri.Host.EndsWith("vimeo.com", StringComparison.OrdinalIgnoreCase))
            {
                trailer ??= src;
                continue;
            }
            if (AdHosts.IsAd(frameUri) || IsWidget(frameUri)) continue;

            var score = 1;
            if (PlayerHintRegex().IsMatch(frameUri.PathAndQuery)) score += 3;
            var context = string.Join(' ', frame.Id, frame.ClassName, frame.ParentElement?.Id, frame.ParentElement?.ClassName,
                frame.ParentElement?.ParentElement?.Id, frame.ParentElement?.ParentElement?.ClassName);
            if (PlayerContainerRegex().IsMatch(context)) score += 2;
            if (frame.HasAttribute("allowfullscreen") || (frame.GetAttribute("allow") ?? string.Empty).Contains("fullscreen", StringComparison.OrdinalIgnoreCase)) score += 2;
            if (int.TryParse(frame.GetAttribute("width"), out var w) && w is > 0 and < 200) score -= 3; // a badge or counter
            if (best is null || score > best.Value.Score) best = (frameUri.AbsoluteUri, score);
        }
        if (best is { Score: >= 3 } player) return MediaStreams.WithHint(player.Url, StreamFormat.Embed);

        // 3. A video file in a <video> tag.
        foreach (var video in doc.QuerySelectorAll("video[src], video source[src]"))
        {
            if (Resolve(video.GetAttribute("src"), pageUri) is { } file && Uri.TryCreate(file, UriKind.Absolute, out var fileUri) && fileUri.Scheme is "http" or "https")
                return MediaStreams.FormatOfPath(fileUri) is not null ? fileUri.AbsoluteUri : MediaStreams.WithHint(fileUri.AbsoluteUri, StreamFormat.File);
        }

        // 4. Any other frame, then a trailer, then the page itself.
        if (best is { } anyFrame) return MediaStreams.WithHint(anyFrame.Url, StreamFormat.Embed);
        if (trailer is not null) return MediaUrlHelper.NormalizeVideoUrl(trailer) ?? trailer;
        return MediaStreams.WithHint(pageUri.AbsoluteUri, StreamFormat.Embed);
    }

    // A stream / server button's label: "Stream 1", "Server #2", "Source 3", "Link 1", "S1" …
    [GeneratedRegex(@"^\s*(?:stream|server|source|link|mirror|player|option|host|sv|s)\s*[#:\-]?\s*(?<n>\d{1,2})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex StreamLabelRegex();

    [GeneratedRegex(@"(?:https?:)?//[^\s""'<>\\)]+", RegexOptions.IgnoreCase)]
    private static partial Regex AnyUrlRegex();

    /// <summary>Attributes a stream button keeps its player's address in.</summary>
    private static readonly string[] PlayerAttributes =
        ["data-src", "data-url", "data-embed", "data-link", "data-iframe", "data-video", "data-href", "data-server", "data-player", "data-stream", "value", "href", "onclick"];

    /// <summary>
    /// The player the user's preference picks on a title's page (Settings › Media player), from the page's HTML alone:
    /// <list type="number">
    ///   <item>The chosen stream button ("Stream 1"), when the address it loads is written on it and is on a preferred player
    ///         site (or no sites are set).</item>
    ///   <item>Otherwise the addresses on the preferred player sites written anywhere in the page (frames, buttons, scripts),
    ///         best site first, then in page order; the chosen stream number picks among them (Stream 2: the second one).</item>
    /// </list>
    /// Null when nothing on the page matches: the caller may click the button in a browser (<c>IBrowserPlayerFinder</c>), or
    /// fall back to <see cref="FindPlayer"/>.
    /// </summary>
    public static string? FindPreferred(IDocument doc, string html, Uri pageUri, PlayerPreference preference)
    {
        if (!preference.IsActive) return null;

        if (preference.StreamNumber > 0 && StreamButtonUrl(doc, pageUri, preference.StreamNumber) is { } chosen &&
            (preference.Hosts.Count > 0 ? preference.Matches(chosen) : !SameSite(new Uri(chosen), pageUri))) // not just this page again with ?server=1
            return AsPlay(chosen);
        if (preference.Hosts.Count == 0) return null;

        var text = WebUtility.HtmlDecode(html.Replace("\\/", "/", StringComparison.Ordinal));
        var found = new List<string>();
        foreach (Match m in AnyUrlRegex().Matches(text))
        {
            var value = m.Value.TrimEnd('.', ',', ';');
            if (value.StartsWith("//", StringComparison.Ordinal)) value = pageUri.Scheme + ":" + value;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !preference.Matches(uri)) continue;
            if (uri.AbsolutePath.Length <= 1 || MediaUrlHelper.IsImageUrl(uri.AbsoluteUri) || uri.AbsolutePath.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
                uri.AbsolutePath.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
                continue; // the site's logo or scripts, not a player
            if (!found.Contains(uri.AbsoluteUri)) found.Add(uri.AbsoluteUri);
        }
        if (found.Count == 0) return null;

        var bestRank = found.Min(u => preference.RankOf(u));
        var onBest = found.Where(u => preference.RankOf(u) == bestRank).ToList();
        var pick = preference.StreamNumber > 1 && preference.StreamNumber <= onBest.Count ? onBest[preference.StreamNumber - 1] : onBest[0];
        return AsPlay(pick);
    }

    /// <summary>The address the "Stream N" / "Server N" button on the page loads, when it's written on the button (or around it).</summary>
    internal static string? StreamButtonUrl(IDocument doc, Uri pageUri, int number)
    {
        var buttons = doc.QuerySelectorAll("a, button, li, option, span, div, label, [role='button'], [data-src], [data-url], [data-embed], [data-link]")
            .Where(e =>
            {
                var label = Clean(e.TextContent);
                return label.Length is > 0 and < 40 && StreamLabelRegex().Match(label) is { Success: true } m &&
                       int.TryParse(m.Groups["n"].Value, out var n) && n == number;
            })
            .ToList();
        // The innermost: "<li data-src=…><a>Stream 1</a></li>" is one button, not two.
        foreach (var button in buttons.Where(b => !buttons.Any(o => o != b && b.Contains(o))))
        {
            for (IElement? e = button; e is not null && e != doc.Body; e = e.ParentElement)
            {
                foreach (var attr in PlayerAttributes)
                {
                    var value = e.GetAttribute(attr);
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    if (attr == "onclick") value = AnyUrlRegex().Match(value) is { Success: true } inClick ? inClick.Value : null;
                    if (Resolve(value, pageUri) is { } url && Key(new Uri(url)) != Key(pageUri)) return url;
                }
                if (e != button) break; // the button and the element right around it only
            }
        }
        return null;
    }

    /// <summary>A stream or video file plays as it is; anything else is a player page shown in a frame.</summary>
    private static string AsPlay(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && MediaStreams.FormatOfPath(uri) is not null ? uri.AbsoluteUri : MediaStreams.WithHint(url, StreamFormat.Embed);

    /// <summary>
    /// True when <see cref="FindPlayer"/> found nothing better than a fallback: the page itself in a frame, or a trailer. Worth
    /// clicking the stream button in a browser then.
    /// </summary>
    public static bool IsFallback(string play, Uri pageUri) =>
        MediaUrlHelper.TryGetYouTubeId(play) is not null || play.Contains("vimeo.com", StringComparison.OrdinalIgnoreCase) ||
        (MediaStreams.FormatOf(play) == StreamFormat.Embed && Uri.TryCreate(MediaStreams.WithoutHint(play), UriKind.Absolute, out var uri) && Key(uri) == Key(pageUri));

    /// <summary>Social / comment / chat frames that sit on title pages but are never the video.</summary>
    private static bool IsWidget(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        return host.Contains("facebook.") || host.Contains("disqus") || host.Contains("twitter.") || host.EndsWith("x.com", StringComparison.Ordinal) ||
               host.Contains("google.com") || host.Contains("recaptcha") || host.Contains("hcaptcha") || host.Contains("cloudflare") ||
               host.Contains("discord") || host.Contains("telegram") || host.Contains("addthis") || host.Contains("sharethis");
    }

    /// <summary>The links "group": where it sits in the page (the link and three parents, by tag and first class).</summary>
    private static string GroupOf(IElement anchor)
    {
        var parts = new List<string>(4);
        var element = anchor;
        for (var i = 0; i < 4 && element is not null; i++)
        {
            var cls = element.ClassList.FirstOrDefault(c => !c.Any(char.IsDigit)) ?? string.Empty; // "post-123" changes per card
            parts.Add(element.LocalName + (cls.Length > 0 ? "." + cls : string.Empty));
            element = element.ParentElement;
        }
        parts.Reverse();
        return string.Join('>', parts);
    }

    /// <summary>The title's poster: an image in the link, or in the card around it (lazy-loading attributes first).</summary>
    private static string? ImageOf(IElement anchor, Uri pageUri)
    {
        var image = ImageIn(anchor, pageUri);
        if (image is not null) return image;
        // A card with the poster next to the link (only when the card holds one link's worth of content).
        var card = anchor.ParentElement;
        for (var i = 0; i < 2 && card is not null; i++, card = card.ParentElement)
        {
            if (card.QuerySelectorAll("a[href]").Length > 3) break;
            if (ImageIn(card, pageUri) is { } near) return near;
        }
        return null;
    }

    private static string? ImageIn(IElement container, Uri pageUri)
    {
        foreach (var img in container.QuerySelectorAll("img"))
        {
            foreach (var attr in new[] { "data-src", "data-original", "data-lazy-src", "data-lazy", "data-echo", "data-img", "src" })
            {
                var value = img.GetAttribute(attr);
                if (string.IsNullOrWhiteSpace(value) || value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                if (Resolve(value, pageUri) is { } url && !LooksLikeIcon(url, img)) return url;
            }
            var srcset = img.GetAttribute("data-srcset") ?? img.GetAttribute("srcset");
            if (!string.IsNullOrWhiteSpace(srcset) && Resolve(srcset.Split(',')[0].Trim().Split(' ')[0], pageUri) is { } fromSet && !LooksLikeIcon(fromSet, img))
                return fromSet;
        }
        foreach (var styled in new[] { container }.Concat(container.QuerySelectorAll("[style*='url'], [data-bg], [data-background]")))
        {
            var value = styled.GetAttribute("data-bg") ?? styled.GetAttribute("data-background");
            if (value is null && CssUrlRegex().Match(styled.GetAttribute("style") ?? string.Empty) is { Success: true } css) value = css.Groups["u"].Value;
            if (value is not null && Resolve(value, pageUri) is { } url) return url;
        }
        return null;
    }

    private static bool LooksLikeIcon(string url, IElement img)
    {
        var lower = url.ToLowerInvariant();
        if (lower.Contains(".svg") || lower.Contains("logo") || lower.Contains("icon") || lower.Contains("avatar") || lower.Contains("flag") || lower.Contains("spinner") || lower.Contains("loading"))
            return true;
        return int.TryParse(img.GetAttribute("width"), out var w) && w is > 0 and < 60;
    }

    /// <summary>The title's name and how sure that is (lower is better): the link's title, the poster's alt, a heading, the text.</summary>
    private static (string? Title, int Rank, string? Category, bool IsLive) TitleOf(IElement anchor)
    {
        var options = new (string? Text, int Rank)[]
        {
            (anchor.GetAttribute("title"), 0),
            (SpacedText(anchor.QuerySelector("h1, h2, h3, h4, h5, h6")), 1),
            (IsNameLink(anchor) ? SpacedText(anchor) : null, 1), // <h3><a>Name (2024)</a></h3>: the card's name
            (SpacedText(anchor.QuerySelector("[class*='title'], [class*='name']")), 2),
            (anchor.QuerySelector("img[alt]")?.GetAttribute("alt"), 3),
            (anchor.GetAttribute("aria-label"), 4),
            (SpacedText(anchor), 5),
            (anchor.QuerySelector("img[title]")?.GetAttribute("title"), 6),
        };
        string? category = null;
        var live = false;
        foreach (var (text, rank) in options)
        {
            var title = Tidy(text, out var sport, out var isLive);
            category ??= sport;
            live |= isLive;
            if (title is not null) return (title, rank, category ?? TypeInCard(anchor), live || IsLiveCard(anchor));
        }
        return (null, int.MaxValue, null, false);
    }

    /// <summary>
    /// The type a card shows on its own ("Football" under the match's name, a "Movie" / "TV" badge), or the sport after the
    /// name in the card's whole text. Null when the card doesn't say.
    /// </summary>
    private static string? TypeInCard(IElement anchor)
    {
        // The card may be the link itself or the box around it (when the box holds just this one title).
        var card = anchor;
        if (anchor.ParentElement is { } parent && parent.QuerySelectorAll("a[href]").Length <= 2) card = parent;
        foreach (var element in card.QuerySelectorAll("*"))
        {
            if (element.Children.Length > 0) continue; // the label itself, not a box around it
            var text = Clean(element.TextContent);
            if (text.Length is > 1 and < 25 && TypeLabelRegex().IsMatch(text)) return NiceType(text);
        }
        Tidy(SpacedText(card), out var sport, out _);
        return sport is null ? null : NiceType(sport);
    }

    /// <summary>"FOOTBALL" / "tv" → "Football" / "TV": the same type always reads the same, whatever the card's case.</summary>
    private static string NiceType(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length <= 3 && w.All(char.IsLetter) && w.ToUpperInvariant() is "TV" or "AFL" or "NFL" or "NBA" or "NHL" or "MLB" or "MMA" or "UFC" or "F1"
                ? w.ToUpperInvariant()
                : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant());
        return string.Join(' ', words);
    }

    /// <summary>The card says it's live ("LIVE" on its own, a live badge).</summary>
    private static bool IsLiveCard(IElement anchor) =>
        anchor.QuerySelector("[class*='live']") is not null ||
        Clean(SpacedText(anchor)).StartsWith("LIVE ", StringComparison.Ordinal) ||
        Clean(SpacedText(anchor)).Contains(" LIVE ", StringComparison.Ordinal);

    /// <summary>
    /// An element's text with a space between its parts ("&lt;b&gt;LIVE&lt;/b&gt;&lt;span&gt;Arsenal&lt;/span&gt;" → "LIVE Arsenal",
    /// where TextContent would give "LIVEArsenal"). Scripts and styles left out.
    /// </summary>
    private static string? SpacedText(INode? node)
    {
        if (node is null) return null;
        var parts = new List<string>();
        void Walk(INode n)
        {
            foreach (var child in n.ChildNodes)
            {
                if (child is IText text) parts.Add(text.Data);
                else if (child is IElement e && e.LocalName is not ("script" or "style" or "noscript" or "svg")) Walk(e);
            }
        }
        Walk(node);
        return string.Join(' ', parts);
    }

    /// <summary>The link is the card's name: it sits in a heading or a "title" / "name" element.</summary>
    private static bool IsNameLink(IElement anchor)
    {
        var parent = anchor.ParentElement;
        if (parent is null) return false;
        if (parent.LocalName is "h1" or "h2" or "h3" or "h4" or "h5" or "h6") return true;
        var cls = parent.ClassName ?? string.Empty;
        return cls.Contains("title", StringComparison.OrdinalIgnoreCase) || cls.Contains("name", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A usable name, tidied ("Watch X Full Movie Online Free" → "X"), or null for labels and junk.</summary>
    internal static string? Tidy(string? text) => Tidy(text, out _, out _);

    /// <summary>
    /// <see cref="Tidy(string?)"/>, also taking off what a sports card says around a match's name ("Match Poster LIVE 57729
    /// Bayern Munich vs Arsenal Football | 7:00 PM" → "Bayern Munich vs Arsenal"): whether it's live, and its sport.
    /// </summary>
    internal static string? Tidy(string? text, out string? category, out bool isLive)
    {
        category = null;
        isLive = false;
        var clean = Clean(text);
        if (clean.Length == 0 || JunkLabelRegex().IsMatch(clean)) return null; // "Watch now" before "Watch " is taken off

        var sportsCard = false; // "LIVE …", "Oct 22 …": a sports listing, where the sport follows the name
        var prefix = CardPrefixRegex().Match(clean);
        // A leading number is only the viewer count after "LIVE" or a date; "300" or "1917" alone is a name.
        if (prefix.Success && prefix.Length > 0 && prefix.Length < clean.Length &&
            (!prefix.Groups["n"].Success || prefix.Groups["live"].Success || prefix.Length > prefix.Groups["n"].Length))
        {
            isLive = prefix.Groups["live"].Success;
            sportsCard = isLive || prefix.Groups["n"].Success;
            clean = clean[prefix.Length..];
        }
        clean = TimeSuffixRegex().Replace(clean, string.Empty);
        if ((sportsCard || Matchup(clean) is not null) && SportSuffixRegex().Match(clean) is { Success: true } sport && sport.Index > 1)
        {
            category = NiceType(Clean(sport.Groups["sport"].Value));
            clean = clean[..sport.Index];
        }
        clean = WatchPrefixRegex().Replace(clean, string.Empty);
        clean = TitleTailRegex().Replace(clean, string.Empty).Trim(' ', '-', '|', ':', '–', '—');
        if (clean.Length is < 2 or > 120 || JunkLabelRegex().IsMatch(clean)) return null;
        if (clean.StartsWith("http", StringComparison.OrdinalIgnoreCase) || clean.Contains("://", StringComparison.Ordinal)) return null;
        return clean;
    }

    private static bool SameSite(Uri a, Uri b) => BaseHost(a.Host) == BaseHost(b.Host);

    private static string BaseHost(string host)
    {
        host = host.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    private static string Key(Uri uri) => BaseHost(uri.Host) + uri.AbsolutePath.TrimEnd('/').ToLowerInvariant() + uri.Query.ToLowerInvariant();

    private static string Clean(string? text) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty : SpacesRegex().Replace(WebUtility.HtmlDecode(text), " ").Trim();

    private static string? Resolve(string? href, Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        var value = WebUtility.HtmlDecode(href.Trim());
        if (value.StartsWith('#') || value.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            return null;
        return Uri.TryCreate(baseUri, value, out var uri) && uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;
    }
}
