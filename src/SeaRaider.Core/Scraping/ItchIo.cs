using System.Net;
using System.Runtime.CompilerServices;
using AngleSharp.Dom;
using Microsoft.Extensions.Logging;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Scraping;

/// <summary>
/// itch.io as a source (for example <c>https://itch.io/games/platform-windows</c>): the home of community demos and indie
/// games. It isn't a WordPress site, so the generic parser doesn't understand its pages; this reads them.
/// <list type="bullet">
///   <item><b>Browse</b>: the listing page and <c>?page=N</c>; each <c>.game_cell</c> becomes a store card (title, cover,
///         teaser, author, genre). Only games that offer a Windows build are kept when the card says which platforms it has.</item>
///   <item><b>Search</b>: itch.io's own search (<c>/search?type=games&amp;q=…</c>).</item>
///   <item><b>Game page</b>: description, screenshots, author, genre, tags, update date and the file list. itch.io hands out
///         files through a per-file click-through (a button that asks the site for a short-lived link), so the download
///         option opens the game's page in your browser (<see cref="FileHosts"/>: "itch.io", browser only).</item>
/// </list>
/// </summary>
public static partial class ItchIo
{
    public static bool IsItchHost(string? host) =>
        host is not null && (host.Equals("itch.io", StringComparison.OrdinalIgnoreCase) ||
                             host.EndsWith(".itch.io", StringComparison.OrdinalIgnoreCase));

    public static bool IsItch(GameSource source) =>
        Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) && IsItchHost(uri.Host);

    /// <summary>Browse page <paramref name="page"/> of the source's listing, or itch.io's search for <paramref name="query"/>.</summary>
    public static Uri BuildUrl(GameSource source, string? query, int page)
    {
        page = Math.Max(1, page);
        if (!string.IsNullOrWhiteSpace(query))
        {
            // A creator's site or one game: search within it (its games are scored against the query), not all of itch.io.
            if (Uri.TryCreate(source.Url, UriKind.Absolute, out var own) && !own.Host.Equals("itch.io", StringComparison.OrdinalIgnoreCase))
                return own;
            return new Uri($"https://itch.io/search?type=games&q={Uri.EscapeDataString(query)}" + (page > 1 ? $"&page={page}" : string.Empty));
        }

        var start = new Uri(source.Url);
        if (page == 1) return start;
        // A creator's own site (someone.itch.io, or one of their games) is a single page: no paging.
        if (!start.Host.Equals("itch.io", StringComparison.OrdinalIgnoreCase)) return start;

        var kept = start.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.StartsWith("page=", StringComparison.OrdinalIgnoreCase));
        var query2 = string.Join('&', kept.Append($"page={page}"));
        return new UriBuilder(start) { Query = query2 }.Uri;
    }

    // =============================================================================================
    // Listing / search pages
    // =============================================================================================

    /// <summary>Store entries (stubs; the game page is read when one is opened) of a listing or search page.</summary>
    public static (List<GameMetadata> Games, bool HasMore) ParseListing(IDocument doc, Uri pageUri, GameSource source)
    {
        var games = new List<GameMetadata>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cells = doc.QuerySelectorAll(".game_cell").ToList();

        foreach (var cell in cells)
        {
            var anchor = cell.QuerySelector("a.title[href], .game_title a[href], a.game_link[href]");
            if (anchor is null || Resolve(anchor.GetAttribute("href"), pageUri) is not { } link) continue;
            if (!IsItchHost(link.Host) || !seen.Add(link.AbsoluteUri)) continue;

            var title = Collapse(anchor.TextContent);
            if (title.Length < 2) title = Collapse(cell.QuerySelector(".game_title")?.TextContent);
            if (title.Length < 2) continue;

            // itch.io lists the platforms of every game; skip the ones that have no Windows build, unless they play in a
            // browser (those run in SeaRaider's game window).
            var platforms = cell.QuerySelectorAll(".game_platform [class*=icon-]").Select(p => p.ClassName ?? string.Empty).ToList();
            var playsInBrowser = cell.QuerySelector(".web_flag, [class*=web_flag]") is not null;
            if (platforms.Count > 0 && !playsInBrowser && !platforms.Any(c => c.Contains("windows", StringComparison.OrdinalIgnoreCase))) continue;

            var thumb = cell.QuerySelector(".game_thumb img, img");
            var cover = Resolve(thumb?.GetAttribute("data-lazy_src") ?? thumb?.GetAttribute("src"), pageUri)?.AbsoluteUri;
            var teaser = Collapse(cell.QuerySelector(".game_text")?.TextContent);
            var author = Collapse(cell.QuerySelector(".game_author")?.TextContent);
            var genre = Collapse(cell.QuerySelector(".game_genre")?.TextContent);

            games.Add(new GameMetadata
            {
                Id = GameMetadata.CreateId(link.AbsoluteUri),
                SourceId = source.Id,
                PageUrl = link.AbsoluteUri,
                Title = title,
                RawTitle = title,
                CoverImageUrl = cover,
                ShortDescription = teaser.Length > 220 ? teaser[..220].TrimEnd() + "…" : teaser,
                Developer = author.Length > 0 ? author : null,
                Genres = genre.Length > 0 ? [genre] : [],
                DetailsLoaded = false,
                MetadataVersion = GameMetadata.CurrentMetadataVersion,
                FetchedAtUtc = DateTime.UtcNow,
            });
        }

        // The source is one game's page (someone.itch.io/their-game): that game.
        if (cells.Count == 0 && IsGamePage(doc) && ItemFromGamePage(doc, pageUri, source) is { } single)
            return ([single], false);

        var hasMore = doc.QuerySelector(".pager a.next_page[href], a.next_page[href]") is not null ||
                      (cells.Count >= 30 && doc.QuerySelector(".pager") is not null);
        return (games, hasMore);
    }

    // =============================================================================================
    // Game pages
    // =============================================================================================

    /// <summary>Fills a game from its itch.io page on top of what the generic parser found.</summary>
    public static void EnrichFromGamePage(IDocument doc, Uri pageUri, GameMetadata game)
    {
        // Description: the author's own formatted text.
        var description = Collapse(doc.QuerySelector(".formatted_description")?.TextContent, keepParagraphs: true);
        if (description.Length > 0) game.Description = description;
        if (game.ShortDescription.Length == 0)
        {
            var teaser = Collapse(doc.QuerySelector("meta[property='og:description']")?.GetAttribute("content"));
            game.ShortDescription = teaser.Length > 220 ? teaser[..220].TrimEnd() + "…" : teaser;
        }

        // Screenshots are linked at full size from the page's screenshot strip.
        var shots = doc.QuerySelectorAll(".screenshot_list a[href], .screenshot_list img")
            .Select(e => e.TagName.Equals("A", StringComparison.OrdinalIgnoreCase)
                ? e.GetAttribute("href")
                : e.GetAttribute("data-lazy_src") ?? e.GetAttribute("src"))
            .Select(u => Resolve(u, pageUri)?.AbsoluteUri)
            .OfType<string>()
            .Where(u => IsImage(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
        if (shots.Count > 0) game.ScreenshotUrls = shots;

        // The info table on the side: Updated, Status, Platforms, Author, Genre, Tags …
        foreach (var row in doc.QuerySelectorAll(".game_info_panel_widget tr"))
        {
            var cells = row.QuerySelectorAll("td").ToList();
            if (cells.Count < 2) continue;
            var label = Collapse(cells[0].TextContent).TrimEnd(':').ToLowerInvariant();
            var value = Collapse(cells[1].TextContent);
            var links = cells[1].QuerySelectorAll("a").Select(a => Collapse(a.TextContent)).Where(t => t.Length > 0).ToList();
            switch (label)
            {
                case "author":
                    if (value.Length > 0) game.Developer = value;
                    break;
                case "genre":
                    if (links.Count > 0) game.Genres = links;
                    break;
                case "tags":
                    if (links.Count > 0) game.Tags = links.Take(8).ToList();
                    break;
                case "updated" or "published" or "release date":
                    var stamp = cells[1].QuerySelector("abbr[title]")?.GetAttribute("title") ?? value;
                    if (MetadataParser.ParseDate(stamp) is { } date)
                    {
                        if (label == "updated") game.UploadedAtUtc = date;
                        else game.ReleaseDateUtc ??= date;
                        game.ReleaseDate ??= label == "updated" ? null : stamp;
                    }
                    break;
            }
        }

        // The file list: the one download option opens the page, with the Windows file's size next to it.
        var uploads = doc.QuerySelectorAll(".upload_list_widget .upload").ToList();
        var windows = uploads.FirstOrDefault(u => u.QuerySelector("[class*=icon-windows]") is not null) ?? uploads.FirstOrDefault();
        var size = Collapse(windows?.QuerySelector(".file_size")?.TextContent);
        game.DownloadLinks =
        [
            new DownloadLink
            {
                Url = pageUri.AbsoluteUri,
                Kind = DownloadLinkKind.FileHost,
                Label = "Download on itch.io",
                Host = pageUri.Host,
                Mirror = "itch.io",
                SizeText = size.Length > 0 ? size : null,
            },
        ];
        if (size.Length > 0) game.OriginalSize ??= size;

        // Played in the browser (HTML5): the game's own address, so SeaRaider can run it in its game window.
        if (WebGameUrlOf(doc) is { } web)
        {
            game.WebGameUrl = web;
            if (uploads.Count == 0) game.DownloadLinks[0].Label = "Play in SeaRaider";
        }

        // These are the author's own pictures: no Steam / IGDB lookup (a demo can share a name with an unrelated Steam game).
        game.MediaChecked = true;
        game.MediaVersion = GameMetadata.CurrentMediaVersion;
        game.DetailsLoaded = true;
        game.MetadataVersion = GameMetadata.CurrentMetadataVersion;
    }

    // =============================================================================================
    // Browser games
    // =============================================================================================

    [System.Text.RegularExpressions.GeneratedRegex(@"https?://[^""'\s<>]+?/html/\d+/[^""'\s<>]*?\.html?", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex HtmlBuildRegex();

    /// <summary>
    /// The HTML5 build of an itch.io game page: the frame "Run game" opens (<c>data-iframe</c>), an embedded frame, or the
    /// build address anywhere in the page (html.itch.zone/html/&lt;id&gt;/index.html). Null when the game doesn't play in a browser.
    /// </summary>
    public static string? WebGameUrlOf(IDocument doc)
    {
        foreach (var holder in doc.QuerySelectorAll("[data-iframe]"))
        {
            if (HtmlBuildRegex().Match(WebUtility.HtmlDecode(holder.GetAttribute("data-iframe") ?? string.Empty)) is { Success: true } m)
                return m.Value;
        }
        foreach (var frame in doc.QuerySelectorAll(".html_embed_widget iframe[src], #html_embed_widget iframe[src], iframe[src*='/html/']"))
        {
            if (HtmlBuildRegex().Match(frame.GetAttribute("src") ?? string.Empty) is { Success: true } m) return m.Value;
        }
        var html = WebUtility.HtmlDecode(doc.DocumentElement?.OuterHtml ?? string.Empty);
        var any = HtmlBuildRegex().Match(html);
        return any.Success && any.Value.Contains("itch.zone", StringComparison.OrdinalIgnoreCase) ? any.Value : null;
    }

    private static bool IsGamePage(IDocument doc) =>
        doc.QuerySelector(".view_game_page, .game_info_panel_widget, h1.game_title") is not null;

    private static GameMetadata? ItemFromGamePage(IDocument doc, Uri pageUri, GameSource source)
    {
        var title = Collapse(doc.QuerySelector("h1.game_title")?.TextContent);
        if (title.Length < 2) title = Collapse(doc.QuerySelector("meta[property='og:title']")?.GetAttribute("content"));
        if (title.Length < 2) return null;
        var cover = Resolve(doc.QuerySelector("meta[property='og:image']")?.GetAttribute("content"), pageUri)?.AbsoluteUri;
        var teaser = Collapse(doc.QuerySelector("meta[property='og:description']")?.GetAttribute("content"));
        var page = new Uri(pageUri.GetLeftPart(UriPartial.Path));
        return new GameMetadata
        {
            Id = GameMetadata.CreateId(page.AbsoluteUri),
            SourceId = source.Id,
            PageUrl = page.AbsoluteUri,
            Title = title,
            RawTitle = title,
            CoverImageUrl = cover,
            ShortDescription = teaser.Length > 220 ? teaser[..220].TrimEnd() + "…" : teaser,
            DetailsLoaded = false,
            MetadataVersion = GameMetadata.CurrentMetadataVersion,
            FetchedAtUtc = DateTime.UtcNow,
        };
    }

    // =============================================================================================

    private static bool IsImage(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        (u.AbsolutePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || u.AbsolutePath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
         u.AbsolutePath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) || u.AbsolutePath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ||
         u.AbsolutePath.EndsWith(".webp", StringComparison.OrdinalIgnoreCase));

    private static Uri? Resolve(string? href, Uri baseUri) =>
        !string.IsNullOrWhiteSpace(href) && Uri.TryCreate(baseUri, WebUtility.HtmlDecode(href.Trim()), out var uri) &&
        uri.Scheme is "http" or "https"
            ? uri
            : null;

    private static string Collapse(string? text, bool keepParagraphs = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var lines = text.Replace("\r", string.Empty).Split('\n')
            .Select(l => string.Join(' ', l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            .Where(l => l.Length > 0);
        return keepParagraphs ? string.Join("\n\n", lines) : string.Join(' ', lines);
    }
}

/// <summary>
/// Crawls an itch.io listing page by page (Sources → build an offline index). The store itself browses and searches the
/// site live without this; the crawl only fills the offline catalog.
/// </summary>
public sealed class ItchIoScraper(IHttpClientFactory httpClientFactory, ILibraryRepository repository, ILogger<ItchIoScraper> logger) : IScraperService
{
    public string Name => "itch.io listing crawler";

    public int Priority => 10;

    public bool CanHandle(Uri sourceUrl) => sourceUrl.Scheme is "http" or "https" && ItchIo.IsItchHost(sourceUrl.Host);

    public async IAsyncEnumerable<GameMetadata> CrawlAsync(GameSource source, CrawlOptions options, IProgress<CrawlProgress>? progress,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientNames.Scraper);
        var userAgent = repository.GetSettings().UserAgent;
        var found = 0;
        var emptyPages = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var page = 1; page <= Math.Max(1, source.MaxPages) && found < source.MaxGames; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = ItchIo.BuildUrl(source, null, page);
            progress?.Report(new CrawlProgress(CrawlPhase.Fetching, "listing pages", page, 0, found, 0, found, 0, $"Reading page {page} of itch.io…"));

            string html;
            Uri finalUri;
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    if (page > 1) break; // ran past the last page
                    throw new HttpRequestException($"itch.io answered {(int)response.StatusCode}.", null, response.StatusCode);
                }
                html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                finalUri = response.RequestMessage?.RequestUri ?? url;
            }

            using var doc = await GamePageParser.ParseHtmlAsync(html, finalUri, cancellationToken).ConfigureAwait(false);
            var (games, hasMore) = ItchIo.ParseListing(doc, finalUri, source);
            var fresh = games.Where(g => seen.Add(g.Id)).ToList();
            emptyPages = fresh.Count == 0 ? emptyPages + 1 : 0;

            foreach (var game in fresh)
            {
                found++;
                yield return game;
            }

            if (!hasMore || emptyPages >= 2) break;
            await Task.Delay(Math.Max(0, source.RequestDelayMs), cancellationToken).ConfigureAwait(false);
        }

        logger.LogInformation("itch.io crawl finished: {Count} games", found);
        progress?.Report(new CrawlProgress(CrawlPhase.Completed, "listing pages", 0, found, found, 0, found, 0, $"{found} games found"));
    }
}
