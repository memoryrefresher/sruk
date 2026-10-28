using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AngleSharp;
using AngleSharp.Dom;
using Microsoft.Extensions.Logging;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Scraping;

/// <summary>
/// Turns HTML / feed documents into store data. Pure parsing — no network I/O — so it is shared by
/// the crawler (<see cref="ScraperService"/>) and easy to unit-test.
///
/// <para><b>Field extraction order</b> (first hit wins): per-site profile selector → schema.org JSON-LD
/// (VideoGame / SoftwareApplication / Product) → OpenGraph / Twitter meta → DOM heuristics.</para>
/// </summary>
public sealed partial class GamePageParser(ILogger<GamePageParser> logger)
{
    private const int MaxScreenshots = 12;
    private const int MaxTrailers = 6;
    internal const int ShortDescriptionLength = 220;

    private static readonly string[] ContentRootSelectors =
        ["article", "main", ".entry-content", ".post-content", ".game-details", "#content", ".content"];

    private static readonly string[] JunkImageHints =
        ["logo", "avatar", "icon", "emoji", "gravatar", "sprite", "pixel", "spinner", "loader", "badge", "/ads/", "placeholder"];

    private static readonly HashSet<string> GameSchemaTypes = new(StringComparer.OrdinalIgnoreCase)
        { "VideoGame", "Game", "SoftwareApplication", "VideoGameSeries", "Product" };

    // =============================================================================================
    // Fetching & parsing
    // =============================================================================================

    public static Task<IDocument> ParseHtmlAsync(string html, Uri address, CancellationToken ct)
    {
        // Parsing only — no script execution, no resource loading.
        var context = BrowsingContext.New(Configuration.Default);
        return context.OpenAsync(req => req.Content(html).Address(address.AbsoluteUri), ct);
    }

    // =============================================================================================
    // Listing pages
    // =============================================================================================

    public bool IsListingPage(IDocument doc, Uri pageUri, ScraperProfile profile, IReadOnlyList<Uri> itemLinks, List<string> warnings)
    {
        if (!string.IsNullOrWhiteSpace(profile.ItemLinkSelector)) return itemLinks.Count > 0;

        var articleCount = doc.QuerySelectorAll("article").Length;
        if (articleCount >= 3 && itemLinks.Count >= 3) return true;

        // A page that carries download links or game schema is a detail page.
        if (FindDownloadLinks(doc, pageUri, profile, warnings).Count > 0 || FindJsonLdGame(doc) is not null) return false;

        return itemLinks.Count >= 2;
    }

    /// <summary>Links from a listing page to individual game pages (same host, de-duplicated).</summary>
    public List<Uri> FindItemLinks(IDocument doc, Uri pageUri, ScraperProfile profile, List<string> warnings)
    {
        IEnumerable<IElement> anchors;
        if (!string.IsNullOrWhiteSpace(profile.ItemLinkSelector))
        {
            anchors = SafeQueryAll(doc, profile.ItemLinkSelector!, warnings);
        }
        else
        {
            var cards = doc.QuerySelectorAll("article");
            if (cards.Length == 0) cards = doc.QuerySelectorAll(".post, .card, .game, .game-card, .item, li.product");

            anchors = cards
                .Select(card => card.QuerySelector("h1 a[href], h2 a[href], h3 a[href], a[rel~=bookmark], a.title[href]")
                                ?? card.QuerySelector("a[href]"))
                .OfType<IElement>();
        }

        // A section listing whose entries live under it (".../files/gba" → ".../files/gba/lost-marbles"): those links are the
        // entries, whatever the theme's markup (no <article> cards needed).
        if (string.IsNullOrWhiteSpace(profile.ItemLinkSelector) && ChildLinks(doc, pageUri) is { Count: >= 3 } children)
            return children;

        var result = new List<Uri>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { pageUri.AbsoluteUri };
        foreach (var anchor in anchors)
        {
            if (Resolve(anchor.GetAttribute("href"), pageUri) is not { } uri) continue;
            if (!UrlRules.SameSite(uri, pageUri) || UrlRules.IsNonContentPath(uri)) continue;

            var normalized = UrlRules.Normalize(uri);
            if (seen.Add(normalized.AbsoluteUri)) result.Add(normalized);
        }
        return result;
    }

    /// <summary>Same-site links exactly one level below the listing's own address (its /page/N part ignored).</summary>
    internal static List<Uri> ChildLinks(IDocument doc, Uri pageUri)
    {
        var result = new List<Uri>();
        if (SectionOf(pageUri) is not { } section) return result;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var anchor in doc.QuerySelectorAll("a[href]"))
        {
            if (Resolve(anchor.GetAttribute("href"), pageUri) is not { } uri) continue;
            if (!UrlRules.SameSite(uri, pageUri) || UrlRules.IsNonContentPath(uri) || !IsChildPath(uri, section)) continue;
            var normalized = UrlRules.Normalize(uri);
            if (seen.Add(normalized.AbsoluteUri)) result.Add(normalized);
        }
        return result;
    }

    /// <summary>True when <paramref name="link"/> is one level below the listing <paramref name="listing"/> (".../files/gba/x").</summary>
    public static bool IsChildOf(Uri link, Uri listing) =>
        UrlRules.SameSite(link, listing) && SectionOf(listing) is { } section && IsChildPath(link, section);

    private static string? SectionOf(Uri listing)
    {
        var path = Regex.Replace(listing.AbsolutePath, @"/page/\d+/?$", string.Empty, RegexOptions.IgnoreCase).Trim('/');
        return path.Length == 0 ? null : path.ToLowerInvariant(); // the home page has no section
    }

    private static bool IsChildPath(Uri link, string section)
    {
        var path = link.AbsolutePath.Trim('/').ToLowerInvariant();
        if (!path.StartsWith(section + "/", StringComparison.Ordinal)) return false;
        var rest = path[(section.Length + 1)..];
        return rest.Length > 0 && !rest.Contains('/') && rest != "page" && rest != "feed";
    }

    /// <summary>
    /// Turns a listing / search-results page into store entries without visiting each game page:
    /// a card whose theme shows the whole post inline (download links, info lines) becomes a full record;
    /// otherwise a lightweight stub (title, link, cover, excerpt, post date) whose details load on demand.
    /// </summary>
    public async Task<List<GameMetadata>> ExtractListingResultsAsync(IDocument doc, Uri pageUri, GameSource source,
        List<string> warnings, CancellationToken ct)
    {
        var profile = source.Profile ?? new ScraperProfile();
        var results = new List<GameMetadata>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        IEnumerable<IElement> cards = doc.QuerySelectorAll("article");
        if (!cards.Any()) cards = doc.QuerySelectorAll(".post, .card, .game, .game-card, .item, li.product");
        if (!string.IsNullOrWhiteSpace(profile.ItemLinkSelector))
        {
            cards = SafeQueryAll(doc, profile.ItemLinkSelector!, warnings)
                .Select(a => a.Closest("article, li, .post, .card, .item") ?? a)
                .Distinct();
        }

        foreach (var card in cards)
        {
            ct.ThrowIfCancellationRequested();
            var anchor = (string.IsNullOrWhiteSpace(profile.ItemLinkSelector) ? null : SafeQueryAll(card, profile.ItemLinkSelector!, warnings).FirstOrDefault())
                         ?? card.QuerySelector("h1 a[href], h2 a[href], h3 a[href], a[rel~=bookmark], a.title[href]")
                         ?? card.QuerySelector("a[href]");
            if (anchor is null || Resolve(anchor.GetAttribute("href"), pageUri) is not { } link) continue;
            if (!UrlRules.SameSite(link, pageUri) || UrlRules.IsNonContentPath(link)) continue;
            if (!UrlRules.PassesFilters(link, source.UrlIncludePattern, source.UrlExcludePattern)) continue;
            link = UrlRules.Normalize(link);
            if (!seen.Add(link.AbsoluteUri)) continue;

            var rawTitle = Collapse(anchor.TextContent);
            if (rawTitle.Length < 2) rawTitle = Collapse(card.QuerySelector("h1, h2, h3")?.TextContent ?? anchor.GetAttribute("title"));
            if (rawTitle.Length < 2) continue;

            // Full post inline? Parse the card as its own small document.
            var cardHtml = $"<html><head><title>{WebUtility.HtmlEncode(rawTitle)}</title></head><body>{card.OuterHtml}</body></html>";
            using var cardDoc = await ParseHtmlAsync(cardHtml, link, ct).ConfigureAwait(false);
            var full = ExtractGame(cardDoc, link, source, warnings);
            if (IsGamePage(cardDoc, full) && (full.DownloadLinks.Count > 0 || full.RepackSize is not null || full.Genres.Count > 0))
            {
                full.UploadedAtUtc ??= CardDate(card);
                results.Add(full);
                continue;
            }

            var titleParts = MetadataParser.CleanTitle(rawTitle, Meta(doc, "og:site_name"));
            var excerpt = Collapse(card.QuerySelector(".entry-summary, .excerpt, .post-excerpt, .entry-content p, p")?.TextContent);
            results.Add(new GameMetadata
            {
                Id = GameMetadata.CreateId(link.AbsoluteUri),
                SourceId = source.Id,
                PageUrl = link.AbsoluteUri,
                Title = titleParts.Title,
                RawTitle = rawTitle,
                Version = titleParts.Version,
                DlcInfo = titleParts.DlcInfo,
                CoverImageUrl = card.QuerySelectorAll("img").Where(img => !IsJunkCover(img)).Select(img => ImageSource(img, pageUri)).FirstOrDefault(u => u is not null),
                ShortDescription = CleanTeaser(excerpt) is { } teaser ? Truncate(teaser, ShortDescriptionLength) : string.Empty,
                UploadedAtUtc = CardDate(card),
                DetailsLoaded = false,
                MetadataVersion = GameMetadata.CurrentMetadataVersion,
                FetchedAtUtc = DateTime.UtcNow,
            });
        }
        return results;
    }

    private static DateTime? CardDate(IElement card)
    {
        var stamp = card.QuerySelector("time[datetime]")?.GetAttribute("datetime");
        if (DateTimeOffset.TryParse(stamp, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
            return parsed.UtcDateTime;
        return MetadataParser.ParseDate(card.QuerySelector(".entry-date, .posted-on, .post-date, .date")?.TextContent);
    }

    private IEnumerable<IElement> SafeQueryAll(IElement scope, string selector, List<string> warnings)
    {
        try { return scope.QuerySelectorAll(selector).ToList(); }
        catch (DomException)
        {
            warnings.Add($"Invalid selector \"{selector}\" ignored");
            return [];
        }
    }

    /// <summary>
    /// The "next page" link of a listing page: profile selector, then &lt;link rel="next"&gt;,
    /// then common theme markup (WordPress "Older posts", .pagination .next …). Null on the last page.
    /// </summary>
    public Uri? FindNextPageLink(IDocument doc, Uri pageUri, ScraperProfile profile, List<string> warnings)
    {
        string? href = null;
        if (!string.IsNullOrWhiteSpace(profile.NextPageSelector))
            href = SafeQueryAll(doc, profile.NextPageSelector!, warnings).FirstOrDefault()?.GetAttribute("href");

        href ??= doc.QuerySelector("link[rel~=next]")?.GetAttribute("href")
                 ?? doc.QuerySelector("a[rel~=next]")?.GetAttribute("href")
                 ?? doc.QuerySelector(".pagination a.next, a.next.page-numbers, .nav-links a.next, li.next a, .nav-previous a, a.next")
                     ?.GetAttribute("href");

        return Resolve(href, pageUri) is { } next && UrlRules.SameSite(next, pageUri) ? next : null;
    }

    /// <summary>A page counts as a game page when it offers downloads or declares game structured data.</summary>
    public static bool IsGamePage(IDocument doc, GameMetadata game) =>
        game.DownloadLinks.Count > 0 || FindJsonLdGame(doc) is not null;

    // =============================================================================================
    // Feeds
    // =============================================================================================

    public sealed record FeedItem(string Title, Uri? Link, string Description, string? ImageUrl, List<string> Enclosures);

    public static bool LooksLikeFeed(string? mediaType, string body)
    {
        if (mediaType is not null && (mediaType.Contains("rss") || mediaType.Contains("atom"))) return true;
        var head = body.AsSpan(0, Math.Min(body.Length, 512)).TrimStart();
        if (head.StartsWith("<rss", StringComparison.OrdinalIgnoreCase) || head.StartsWith("<feed", StringComparison.OrdinalIgnoreCase))
            return true;
        return head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) &&
               (body.Contains("<rss", StringComparison.OrdinalIgnoreCase) || body.Contains("<feed", StringComparison.OrdinalIgnoreCase));
    }

    public static IEnumerable<FeedItem> ParseFeed(string xml, Uri feedUri)
    {
        var doc = TryParseXml(xml);
        if (doc is null) yield break;

        // Match on local names so RSS 2.0, RSS 1.0 (RDF) and Atom all work without namespace juggling.
        var items = doc.Descendants().Where(e => e.Name.LocalName is "item" or "entry");
        foreach (var item in items)
        {
            string Child(string name) => item.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() ?? string.Empty;

            var linkElement = item.Elements().FirstOrDefault(e => e.Name.LocalName == "link" &&
                                  (string?)e.Attribute("rel") is null or "alternate");
            var linkText = (string?)linkElement?.Attribute("href") ?? linkElement?.Value;

            var enclosures = item.Elements()
                .Where(e => e.Name.LocalName is "enclosure" || (e.Name.LocalName == "link" && (string?)e.Attribute("rel") == "enclosure"))
                .Select(e => (string?)e.Attribute("url") ?? (string?)e.Attribute("href"))
                .OfType<string>()
                .ToList();
            // Torrent feeds often put the magnet in a custom element (e.g. <torrent:magnetURI>).
            enclosures.AddRange(item.Descendants()
                .Where(e => e.Name.LocalName.Contains("magnet", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Value.Trim())
                .Where(v => v.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)));

            var image = item.Descendants()
                .Where(e => e.Name.LocalName is "thumbnail" or "content" && ((string?)e.Attribute("url"))?.Length > 0)
                .Select(e => (string?)e.Attribute("url"))
                .FirstOrDefault(u => u is not null && MediaUrlHelper.IsImageUrl(u));

            yield return new FeedItem(
                Title: HtmlToText(Child("title")),
                Link: Resolve(linkText, feedUri),
                Description: HtmlToText(Child("description") is { Length: > 0 } d ? d : Child("summary") is { Length: > 0 } s ? s : Child("content")),
                ImageUrl: image,
                Enclosures: enclosures);
        }
    }

    /// <summary>Download links carried by the feed item itself (enclosures / magnet elements).</summary>
    public static List<DownloadLink> EnclosureLinks(FeedItem item) =>
        LinkClassifier.NormalizeGroups(item.Enclosures
            .Select(e => Uri.TryCreate(e, UriKind.Absolute, out var u) ? LinkClassifier.Classify(u, null) : null)
            .OfType<DownloadLink>());

    /// <summary>Builds a store entry from a feed item alone (pure file/torrent feeds without detail pages).</summary>
    public static GameMetadata? FromFeedItem(FeedItem item, Guid sourceId)
    {
        var links = EnclosureLinks(item);
        if (links.Count == 0 || string.IsNullOrWhiteSpace(item.Title)) return null;

        var pageUrl = item.Link?.AbsoluteUri ?? links[0].Url;
        return new GameMetadata
        {
            Id = GameMetadata.CreateId(pageUrl),
            SourceId = sourceId,
            PageUrl = pageUrl,
            Title = item.Title,
            Description = item.Description,
            ShortDescription = Truncate(item.Description, ShortDescriptionLength),
            CoverImageUrl = item.ImageUrl,
            DownloadLinks = links,
        };
    }

    private static XDocument? TryParseXml(string xml)
    {
        try { return XDocument.Parse(xml); }
        catch (System.Xml.XmlException) { return null; }
    }

    // =============================================================================================
    // Detail page → GameMetadata
    // =============================================================================================

    /// <summary>Extracts one game from a parsed detail page.</summary>
    public GameMetadata ExtractGame(IDocument doc, Uri pageUri, GameSource source, List<string> warnings)
    {
        var profile = source.Profile ?? new ScraperProfile();
        var ld = FindJsonLdGame(doc);
        var root = FindContentRoot(doc);

        var siteName = Meta(doc, "og:site_name");
        var rawTitle = StripSiteSuffix(Collapse(FirstNonEmpty(
            SelectText(doc, profile.TitleSelector, warnings),
            ld?.Name,
            Meta(doc, "og:title"),
            Meta(doc, "twitter:title"),
            doc.QuerySelector("h1")?.TextContent,
            doc.Title)), siteName);

        // Structured fields + clean title + scrubbed description.
        var meta = MetadataParser.Parse(root, rawTitle, siteName, doc);

        var description = FirstNonEmpty(
            SelectText(doc, profile.DescriptionSelector, warnings),
            meta.Description,
            Longest(HtmlToText(ld?.Description), ExtractParagraphs(root)),
            CleanTeaser(Meta(doc, "og:description")),
            CleanTeaser(Meta(doc, "description")));

        // Repack sites often put the info dump ("Genres/Tags: … Companies: …") in og:description; skip it then.
        var teaser = FirstNonEmpty(meta.Description, CleanTeaser(Meta(doc, "og:description")), CleanTeaser(Meta(doc, "description")), description);

        var cover = FirstNonEmpty(
            SelectImage(doc, profile.CoverImageSelector, pageUri, warnings),
            ld?.Images.FirstOrDefault(),
            Resolve(Meta(doc, "og:image"), pageUri)?.AbsoluteUri,
            Resolve(Meta(doc, "twitter:image"), pageUri)?.AbsoluteUri,
            FindCoverHeuristically(root, pageUri));

        var screenshots = FindScreenshots(doc, root, pageUri, profile, warnings)
            .Concat(ld?.Images.Skip(1) ?? Enumerable.Empty<string>())
            .Where(u => !string.Equals(u, cover, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxScreenshots)
            .ToList();

        var trailers = FindTrailers(doc, root, pageUri)
            .Concat(ld?.Videos.Select(MediaUrlHelper.NormalizeVideoUrl).OfType<string>() ?? Enumerable.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxTrailers)
            .ToList();

        var tags = doc.QuerySelectorAll("a[rel~=tag]")
            .Select(a => Collapse(a.TextContent))
            .Where(t => t.Length is > 1 and < 32)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        var genres = meta.Genres.Count > 0 ? meta.Genres : (ld?.Genres ?? new List<string>()).Take(8).ToList();
        var bodyText = root.TextContent;
        var releaseText = FirstNonEmpty(meta.ReleaseDateText, ld?.DatePublished, MatchLabeled(ReleaseDateRegex(), bodyText)).NullIfEmpty();
        var releaseUtc = meta.ReleaseDateUtc ?? MetadataParser.ParseDate(releaseText);
        // Only a "Release date:" line on the page is trusted as is. A guess (the page's datePublished, "released" in the text)
        // that falls on the day the post went up is the post's own date, not the game's.
        if (meta.ReleaseDateText is null && !ReleaseDates.IsReal(releaseUtc, meta.UploadedAtUtc, fromSteam: false))
        {
            releaseText = null;
            releaseUtc = null;
        }
        var repackSize = meta.RepackSize;
        var originalSize = meta.OriginalSize ?? MetadataParser.NormalizeSize(MatchLabeled(SizeRegex(), bodyText));

        return new GameMetadata
        {
            Id = GameMetadata.CreateId(pageUri.AbsoluteUri),
            SourceId = source.Id,
            PageUrl = pageUri.AbsoluteUri,
            Title = meta.Title,
            RawTitle = rawTitle,
            Description = description,
            ShortDescription = Truncate(teaser, ShortDescriptionLength),
            CoverImageUrl = cover.NullIfEmpty(),
            ScreenshotUrls = screenshots,
            TrailerUrls = trailers,
            DownloadLinks = FindDownloadLinks(doc, pageUri, profile, warnings),
            Tags = tags,
            Genres = genres,
            Developer = FirstNonEmpty(meta.Developer, ld?.Creator, MatchLabeled(DeveloperRegex(), bodyText)).NullIfEmpty(),
            Publisher = meta.Publisher,
            ReleaseDate = releaseText,
            ReleaseDateUtc = releaseUtc,
            UploadedAtUtc = meta.UploadedAtUtc,
            Version = meta.Version,
            DlcInfo = meta.DlcInfo,
            Languages = meta.Languages,
            RepackSize = repackSize,
            OriginalSize = originalSize,
            SizeText = repackSize ?? originalSize,
            DetailsLoaded = true,
            MetadataVersion = GameMetadata.CurrentMetadataVersion,
            FetchedAtUtc = DateTime.UtcNow,
        };
    }

    /// <summary>Meta descriptions that are really an info dump ("Genres/Tags: … Companies: …") are not teasers.</summary>
    private static string? CleanTeaser(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return MetadataParser.ExtractLabeledFields([text]).Count >= 2 ? null : text;
    }

    private static IElement FindContentRoot(IDocument doc)
    {
        foreach (var selector in ContentRootSelectors)
        {
            if (doc.QuerySelector(selector) is { } el && el.TextContent.Length > 200) return el;
        }
        return (IElement?)doc.Body ?? doc.DocumentElement;
    }

    private static string ExtractParagraphs(IElement root) =>
        string.Join("\n\n", root.QuerySelectorAll("p")
            .Select(p => Collapse(p.TextContent))
            .Where(t => t.Length >= 60 && !t.Contains("magnet:", StringComparison.OrdinalIgnoreCase))
            .Take(5));

    // ---- downloads ------------------------------------------------------------------------------

    public List<DownloadLink> FindDownloadLinks(IDocument doc, Uri pageUri, ScraperProfile profile, List<string> warnings)
    {
        var useSelector = !string.IsNullOrWhiteSpace(profile.DownloadLinkSelector);
        var anchors = useSelector ? SafeQueryAll(doc, profile.DownloadLinkSelector!, warnings) : doc.QuerySelectorAll("a[href]");

        var links = new List<DownloadLink>();
        var labels = MirrorLabels.Parse(profile.MirrorLabels);
        var shortened = new List<DownloadLink>();

        // Provider lines ("Link Gofile: Part 1 – Part 2 – Part 3"): one set per provider, all parts or none. Their links are
        // taken from here, never from the general scan below, so a set with a dead part can't come back as loose files.
        var sets = PartSetParser.Parse(doc, pageUri, profile.PartSetLabelSelector);
        warnings.AddRange(sets.Rejections);
        links.AddRange(sets.ToDownloadLinks());

        foreach (var anchor in anchors)
        {
            if (sets.Claims(anchor)) continue;
            var href = anchor.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href)) continue;

            var uri = href.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
                ? Uri.TryCreate(href.Trim(), UriKind.Absolute, out var m) ? m : null
                : Resolve(href, pageUri);
            if (uri is null) continue;

            if (LinkClassifier.Classify(uri, anchor.TextContent, forceDownload: useSelector) is { } link)
            {
                if (HostPolicy.IsBlocked(link)) continue; // MixDrop & co.
                link.SizeText ??= NearbySize(anchor); // "Buzzheavier (5.2 GB)" style mirror buttons
                if (FileHosts.IsLinkShortener(uri.Host) || FileHosts.IsGeneratorUrl(uri))
                {
                    // "Torrent: Click Here", "Gofile – Click Here": the text next to the button names the mirror.
                    MirrorLabels.Apply(link, MirrorLabels.ContextOf(anchor), labels);
                    shortened.Add(link);
                }
                links.Add(link);
            }
        }
        MirrorLabels.MakeDistinct(shortened);

        // Magnets printed as plain text (inside <code>, <pre>, <textarea>…), and magnets kept anywhere else in the page: in a
        // button's data-* / onclick / value, or in the page's script (a "Torrent link" button that copies or opens it).
        var haystack = (doc.Body?.TextContent ?? string.Empty) + "\n" + (doc.DocumentElement?.OuterHtml ?? string.Empty);
        foreach (Match match in MagnetAnywhereRegex().Matches(haystack))
        {
            var text = WebUtility.HtmlDecode(match.Value).Replace("\\u0026", "&", StringComparison.OrdinalIgnoreCase).Replace("\\/", "/", StringComparison.Ordinal)
                .TrimEnd('\\', ')', ';', ',');
            if (!text.Contains("xt=urn:bt", StringComparison.OrdinalIgnoreCase)) continue;
            if (Uri.TryCreate(text, UriKind.Absolute, out var magnet) && LinkClassifier.Classify(magnet, null) is { } link)
            {
                links.Add(link);
            }
        }

        return DownloadLinkDeduplicator.Deduplicate(LinkClassifier.NormalizeGroups(links));
    }

    /// <summary>A size printed in the link itself or in its short parent line.</summary>
    private static string? NearbySize(IElement anchor)
    {
        if (MetadataParser.NormalizeSize(anchor.TextContent) is { } own) return own;
        var parent = anchor.ParentElement;
        return parent is not null && parent.TextContent.Length < 160 ? MetadataParser.NormalizeSize(parent.TextContent) : null;
    }

    // ---- images ---------------------------------------------------------------------------------

    private static string? FindCoverHeuristically(IElement root, Uri pageUri)
    {
        var images = root.QuerySelectorAll("img").Where(img => !IsJunkCover(img)).ToList();

        var hinted = images.FirstOrDefault(img =>
            Regex.IsMatch($"{img.ClassName} {img.GetAttribute("alt")} {img.GetAttribute("src")}",
                "cover|poster|boxart|capsule|header|key-?art", RegexOptions.IgnoreCase));

        return ImageSource(hinted ?? images.FirstOrDefault(), pageUri);
    }

    private IEnumerable<string> FindScreenshots(IDocument doc, IElement root, Uri pageUri, ScraperProfile profile, List<string> warnings)
    {
        if (!string.IsNullOrWhiteSpace(profile.ScreenshotSelector))
        {
            foreach (var el in SafeQueryAll(doc, profile.ScreenshotSelector, warnings))
            {
                var url = el.LocalName == "a" ? Resolve(el.GetAttribute("href"), pageUri)?.AbsoluteUri : ImageSource(el, pageUri);
                if (url is not null) yield return url;
            }
            yield break;
        }

        // Thumbnails that link to full-size images are the most reliable screenshot signal — unless the thumbnail
        // itself is a stats banner, button or logo (FitGirl links its seeders/peers banner like a screenshot).
        foreach (var anchor in root.QuerySelectorAll("a[href]"))
        {
            if (Resolve(anchor.GetAttribute("href"), pageUri) is not { } uri || !MediaUrlHelper.IsImageUrl(uri.AbsoluteUri)) continue;
            if (anchor.QuerySelector("img") is { } thumb && IsJunkScreenshot(thumb, pageUri)) continue;
            if (MediaScraperService.IsJunk(uri.AbsoluteUri, anchor.GetAttribute("title"))) continue;
            yield return uri.AbsoluteUri;
        }

        foreach (var img in root.QuerySelectorAll("img"))
        {
            if (IsJunkScreenshot(img, pageUri) || img.Closest("a[href]") is { } a && MediaUrlHelper.IsImageUrl(a.GetAttribute("href") ?? "")) continue;
            if (ImageSource(img, pageUri) is { } src) yield return src;
        }
    }

    /// <summary>Screenshot check: the general junk rules plus shape (thin banners, tiny images) when dimensions are given.</summary>
    private static bool IsJunkScreenshot(IElement img, Uri pageUri)
    {
        if (IsJunkImage(img)) return true;
        static int? Dim(string? v) => int.TryParse(v, out var n) ? n : null;
        var src = ImageSource(img, pageUri) ?? string.Empty;
        return MediaScraperService.IsJunk(src, img.GetAttribute("alt"), img.ClassName, Dim(img.GetAttribute("width")), Dim(img.GetAttribute("height")));
    }

    /// <summary>
    /// Cover check: the image's address and shape only. Alt text and classes are left out on purpose: a cover is often
    /// described as "… logo" or carries a theme class, and losing the cover left games like College Football 27 blank.
    /// </summary>
    private static bool IsJunkCover(IElement img)
    {
        var source = img.GetAttribute("data-src") ?? img.GetAttribute("data-lazy-src") ?? img.GetAttribute("src") ?? string.Empty;
        if (source.Length == 0 || source.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return true;
        var lower = source.ToLowerInvariant();
        if (lower.Contains(".svg") || lower.Contains(".gif")) return true;
        // Word rules (stats banners, buttons, avatars …) — except "logo": cover art is often named "…-logo-art.jpg".
        if (MediaScraperService.IsJunk(System.Text.RegularExpressions.Regex.Replace(source, "logos?", "art",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            return true;

        static int? Dim(string? v) => int.TryParse(v, out var n) ? n : null;
        return Dim(img.GetAttribute("width")) is < 120 || Dim(img.GetAttribute("height")) is < 90;
    }

    private static bool IsJunkImage(IElement img)
    {
        var haystack = $"{img.ClassName} {img.GetAttribute("src")} {img.GetAttribute("alt")}".ToLowerInvariant();
        if (JunkImageHints.Any(haystack.Contains)) return true;
        // Stats banners, file-host buttons, donation badges … (word rules shared with the screenshot filter; no shape rule,
        // since covers may be portrait).
        var source = img.GetAttribute("data-src") ?? img.GetAttribute("data-lazy-src") ?? img.GetAttribute("src") ?? "x.jpg";
        if (!source.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && MediaScraperService.IsJunk(source, img.GetAttribute("alt"), img.ClassName))
            return true;
        if (haystack.Contains(".svg") || haystack.Contains(".gif")) return true;

        static int? Dim(string? v) => int.TryParse(v, out var n) ? n : null;
        return Dim(img.GetAttribute("width")) is < 120 || Dim(img.GetAttribute("height")) is < 90;
    }

    /// <summary>Best source URL of an &lt;img&gt;, honouring lazy-loading attributes and srcset.</summary>
    private static string? ImageSource(IElement? img, Uri pageUri)
    {
        if (img is null) return null;
        foreach (var attr in new[] { "data-src", "data-lazy-src", "data-original", "data-full-url", "src", "content" })
        {
            var value = img.GetAttribute(attr);
            if (!string.IsNullOrWhiteSpace(value) && !value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return Resolve(value, pageUri)?.AbsoluteUri;
        }

        var srcset = img.GetAttribute("srcset") ?? img.GetAttribute("data-srcset");
        var largest = srcset?.Split(',', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim().Split(' ')[0];
        return Resolve(largest, pageUri)?.AbsoluteUri;
    }

    // ---- trailers -------------------------------------------------------------------------------

    private static IEnumerable<string> FindTrailers(IDocument doc, IElement root, Uri pageUri)
    {
        var candidates = new List<string?>
        {
            Meta(doc, "og:video:url"), Meta(doc, "og:video:secure_url"), Meta(doc, "og:video"),
        };
        // Only videos embedded in the post itself. The whole page also has the site's sidebar and footer widgets (a music
        // player on some repack sites), and plain links in a post point anywhere ("music by …"): neither is a trailer.
        candidates.AddRange(root.QuerySelectorAll("iframe[src], iframe[data-src]")
            .Select(f => f.GetAttribute("src") ?? f.GetAttribute("data-src")));
        candidates.AddRange(root.QuerySelectorAll("video[src], video source[src]")
            .Select(v => Resolve(v.GetAttribute("src"), pageUri)?.AbsoluteUri));

        return candidates.Select(MediaUrlHelper.NormalizeVideoUrl).OfType<string>();
    }

    // ---- JSON-LD --------------------------------------------------------------------------------

    internal sealed record LdGame(
        string? Name, string? Description, List<string> Images, string? Creator, string? DatePublished,
        List<string> Genres, List<string> Videos);

    internal static LdGame? FindJsonLdGame(IDocument doc)
    {
        foreach (var script in doc.QuerySelectorAll("script[type='application/ld+json']"))
        {
            try
            {
                using var json = JsonDocument.Parse(script.TextContent, new JsonDocumentOptions { AllowTrailingCommas = true });
                if (FindGameNode(json.RootElement) is { } node) return ToLdGame(node);
            }
            catch (JsonException)
            {
                // Broken JSON-LD is common; ignore and fall back to other signals.
            }
        }
        return null;
    }

    private static JsonElement? FindGameNode(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                    if (FindGameNode(child) is { } hit) return hit;
                break;

            case JsonValueKind.Object:
                if (element.TryGetProperty("@type", out var type))
                {
                    IEnumerable<string?> types = type.ValueKind == JsonValueKind.Array
                        ? type.EnumerateArray().Select(t => t.GetString())
                        : new[] { type.GetString() };
                    if (types.Any(t => t is not null && GameSchemaTypes.Contains(t))) return element;
                }
                if (element.TryGetProperty("@graph", out var graph)) return FindGameNode(graph);
                break;
        }
        return null;
    }

    private static LdGame ToLdGame(JsonElement node)
    {
        static IEnumerable<string> Strings(JsonElement el, params string[] objectKeys) => el.ValueKind switch
        {
            JsonValueKind.String => new[] { el.GetString()! },
            JsonValueKind.Array => el.EnumerateArray().SelectMany(e => Strings(e, objectKeys)),
            JsonValueKind.Object => objectKeys
                .Select(k => el.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null)
                .OfType<string>()
                .Take(1),
            _ => Enumerable.Empty<string>(),
        };

        IEnumerable<string> Prop(string name, params string[] objectKeys) =>
            node.TryGetProperty(name, out var v) ? Strings(v, objectKeys) : Enumerable.Empty<string>();

        return new LdGame(
            Name: Prop("name").FirstOrDefault(),
            Description: Prop("description").FirstOrDefault(),
            Images: Prop("image", "url", "contentUrl").Where(u => u.StartsWith("http", StringComparison.OrdinalIgnoreCase)).ToList(),
            Creator: Prop("author", "name").Concat(Prop("creator", "name")).Concat(Prop("publisher", "name")).FirstOrDefault(),
            DatePublished: Prop("datePublished").FirstOrDefault(),
            Genres: Prop("genre").ToList(),
            Videos: Prop("trailer", "embedUrl", "contentUrl", "url").Concat(Prop("video", "embedUrl", "contentUrl", "url")).ToList());
    }

    // =============================================================================================
    // Small helpers
    // =============================================================================================

    [GeneratedRegex(@"magnet:\?xt=urn:bt[a-z]{1,4}:[^\s""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex MagnetTextRegex();

    /// <summary>A magnet address wherever it sits in the page (any parameter order; stops at a quote, space or tag).</summary>
    [GeneratedRegex(@"magnet:\?[^\s""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex MagnetAnywhereRegex();

    [GeneratedRegex(@"(?:developer|developed by)\s*[:\-]\s*(?<v>[^\n\r|•]{2,60})", RegexOptions.IgnoreCase)]
    private static partial Regex DeveloperRegex();

    [GeneratedRegex(@"release(?:d| date)\s*[:\-]\s*(?<v>[^\n\r|•]{4,40})", RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseDateRegex();

    [GeneratedRegex(@"(?:game |file |download |install |repack )?size\s*[:\-]?\s*(?<v>\d+(?:[.,]\d+)?\s*(?:TB|GB|MB))", RegexOptions.IgnoreCase)]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    private static string MatchLabeled(Regex regex, string text) =>
        regex.Match(text) is { Success: true } m ? Collapse(m.Groups["v"].Value) : string.Empty;

    internal static Uri? Resolve(string? raw, Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();
        if (raw.StartsWith('#') || raw.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            return null;

        return Uri.TryCreate(baseUri, raw, out var abs) && (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps)
            ? abs
            : null;
    }

    private static string? Meta(IDocument doc, string key) =>
        doc.QuerySelector($"meta[property='{key}'], meta[name='{key}']")?.GetAttribute("content")?.Trim();

    private IEnumerable<IElement> SafeQueryAll(IDocument doc, string selector, List<string> warnings)
    {
        try { return doc.QuerySelectorAll(selector).ToList(); }
        catch (DomException)
        {
            logger.LogWarning("Invalid CSS selector in scraper profile: {Selector}", selector);
            warnings.Add($"Invalid selector \"{selector}\" ignored");
            return [];
        }
    }

    private string? SelectText(IDocument doc, string? selector, List<string> warnings) =>
        string.IsNullOrWhiteSpace(selector) ? null : SafeQueryAll(doc, selector, warnings).FirstOrDefault()?.TextContent;

    private string? SelectImage(IDocument doc, string? selector, Uri pageUri, List<string> warnings) =>
        string.IsNullOrWhiteSpace(selector) ? null : ImageSource(SafeQueryAll(doc, selector, warnings).FirstOrDefault(), pageUri);

    private static string FirstNonEmpty(params string?[] values) =>
        Collapse(values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty, keepParagraphs: true);

    private static string Longest(params string?[] values) =>
        values.Where(v => !string.IsNullOrWhiteSpace(v)).OrderByDescending(v => v!.Length).FirstOrDefault() ?? string.Empty;

    private static string HtmlToText(string? html) =>
        string.IsNullOrWhiteSpace(html) ? string.Empty : WebUtility.HtmlDecode(TagRegex().Replace(html.Replace("<br", "\n<br"), " ")).Trim();

    /// <summary>Collapses runs of whitespace; optionally keeps blank-line paragraph breaks.</summary>
    private static string Collapse(string? text, bool keepParagraphs = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        if (!keepParagraphs) return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        var paragraphs = Regex.Split(text, @"\n\s*\n")
            .Select(p => string.Join(' ', p.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            .Where(p => p.Length > 0);
        return string.Join("\n\n", paragraphs);
    }

    internal static string Truncate(string text, int max)
    {
        text = Collapse(text);
        if (text.Length <= max) return text;
        var cut = text.LastIndexOf(' ', max - 1);
        return text[..(cut > max / 2 ? cut : max - 1)].TrimEnd(',', '.', ';', ':') + "…";
    }

    private static string StripSiteSuffix(string title, string? siteName)
    {
        if (string.IsNullOrWhiteSpace(siteName)) return title;
        var pattern = $@"\s*[-|–—:]\s*{Regex.Escape(siteName.Trim())}\s*$";
        var stripped = Regex.Replace(title, pattern, string.Empty, RegexOptions.IgnoreCase);
        return stripped.Length > 0 ? stripped : title;
    }
}

internal static class StringExtensions
{
    public static string? NullIfEmpty(this string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
