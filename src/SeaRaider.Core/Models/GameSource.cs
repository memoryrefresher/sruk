using LiteDB;

namespace SeaRaider.Core.Models;

/// <summary>How a source is turned into a list of game pages.</summary>
public enum CrawlMode
{
    /// <summary>Feed if it's a feed; else sitemap; else pagination; else treat the URL as one game page.</summary>
    Auto,

    /// <summary>Only use the site's XML sitemaps.</summary>
    Sitemap,

    /// <summary>Only walk /page/1/, /page/2/ … (or rel="next" links) from the source URL.</summary>
    Pagination,

    /// <summary>The URL is a single game page (no crawling).</summary>
    SinglePage,
}

/// <summary>How live search and browsing query a source.</summary>
public enum SearchMode
{
    /// <summary>Detect once: WordPress REST API if available, else the site's own search page.</summary>
    Auto,

    /// <summary>/wp-json/wp/v2/posts?search=… — fast JSON with full post content (one request per page of results).</summary>
    WordPressApi,

    /// <summary>The site's HTML search page (/?s=query, /page/2/?s=query).</summary>
    SiteSearchPage,

    /// <summary><see cref="GameSource.SearchUrlTemplate"/> with {query} and {page} placeholders.</summary>
    Custom,

    /// <summary>Only the cached / crawled games are searched.</summary>
    Disabled,
}

/// <summary>A website, listing page or RSS/Atom feed the storefront pulls games from.</summary>
public sealed class GameSource
{
    [BsonId]
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Url { get; set; } = string.Empty;

    /// <summary>Friendly name; defaults to the host name.</summary>
    public string DisplayName { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// The address is a game you play in a browser (astrastrike.fun …), not a site that lists downloads: it becomes one
    /// store entry, and Play opens it in SeaRaider's full-screen game window.
    /// </summary>
    public bool IsWebGame { get; set; }

    /// <summary>
    /// A torrent of backups as a source (Sources › Add: a .torrent file, its address, or a magnet): each game folder in it is a
    /// store entry that downloads only that folder. Where it was added from.
    /// </summary>
    public string? TorrentOrigin { get; set; }

    /// <summary>SeaRaider's own copy of that torrent file (so the source works without the original).</summary>
    public string? TorrentFile { get; set; }

    [BsonIgnore]
    public bool IsTorrent => !string.IsNullOrEmpty(TorrentFile);

    /// <summary>The console a backups torrent's games are for, when chosen in Sources (else worked out from its names).</summary>
    public string? TorrentSystemId { get; set; }

    public CrawlMode Mode { get; set; } = CrawlMode.Auto;

    /// <summary>Safety cap on listing pages walked by the pagination crawler.</summary>
    public int MaxPages { get; set; } = 1_000;

    /// <summary>Safety cap on game pages processed per crawl.</summary>
    public int MaxGames { get; set; } = 20_000;

    /// <summary>
    /// Optional regular expression; only URLs that match are treated as game pages
    /// (e.g. <c>/games/</c> or <c>/\d{4}/\d{2}/</c>). Applied to sitemap and pagination results.
    /// </summary>
    public string? UrlIncludePattern { get; set; }

    /// <summary>Optional regular expression for URLs to skip (news posts, guides…).</summary>
    public string? UrlExcludePattern { get; set; }

    /// <summary>Honour robots.txt Disallow rules and Crawl-delay.</summary>
    public bool RespectRobotsTxt { get; set; } = true;

    /// <summary>Minimum pause between two requests to this site, in milliseconds.</summary>
    public int RequestDelayMs { get; set; } = 750;

    /// <summary>Optional per-site CSS selector overrides for sites the heuristics don't nail.</summary>
    public ScraperProfile Profile { get; set; } = new();

    /// <summary>How live search reaches this site.</summary>
    public SearchMode SearchMode { get; set; } = SearchMode.Auto;

    /// <summary>For <see cref="SearchMode.Custom"/>: e.g. "https://example.com/search/{query}/page/{page}".</summary>
    public string? SearchUrlTemplate { get; set; }

    /// <summary>Result of Auto detection (cached so detection runs once per source).</summary>
    public SearchMode? DetectedSearchMode { get; set; }

    public DateTime? LastScrapedAtUtc { get; set; }

    public int LastGameCount { get; set; }

    /// <summary>"OK", or the last error message.</summary>
    public string? LastResult { get; set; }

    /// <summary>How the last crawl found its pages ("sitemap", "pagination", "feed", "single page").</summary>
    public string? LastCrawlMethod { get; set; }
}

/// <summary>
/// CSS selectors that override the generic heuristics in the page parser.
/// Every property is optional — leave a selector empty to use auto-detection for that field.
/// </summary>
public sealed class ScraperProfile
{
    /// <summary>On a listing page: anchors that lead to individual game pages (e.g. "article h2 a").</summary>
    public string? ItemLinkSelector { get; set; }

    /// <summary>On a listing page: the "next page" link, when it isn't rel="next" (e.g. "a.next").</summary>
    public string? NextPageSelector { get; set; }

    public string? TitleSelector { get; set; }
    public string? DescriptionSelector { get; set; }
    public string? CoverImageSelector { get; set; }
    public string? ScreenshotSelector { get; set; }

    /// <summary>Anchors whose href is a download (e.g. ".downloads a, a.btn-download").</summary>
    public string? DownloadLinkSelector { get; set; }

    /// <summary>
    /// The mirror names the site prints next to its "Click Here" buttons, comma-separated ("Torrent, Gofile, Pixeldrain").
    /// Each link-shortener button is named after the one next to it; names with "torrent" or "magnet" are torrents.
    /// </summary>
    public string? MirrorLabels { get; set; }

    /// <summary>
    /// CSS for the label that starts each provider's line of parts ("b.uk-heading-bullet": <c>&lt;b&gt;Link Gofile:&lt;/b&gt; Part 1 – Part 2 …</c>).
    /// Everything after the label up to the next one is that provider's set, taken whole or not at all (see PartSetParser).
    /// Empty: the default label (<c>b.uk-heading-bullet</c>).
    /// </summary>
    public string? PartSetLabelSelector { get; set; }

    [BsonIgnore]
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(ItemLinkSelector) && string.IsNullOrWhiteSpace(NextPageSelector) &&
        string.IsNullOrWhiteSpace(TitleSelector) && string.IsNullOrWhiteSpace(DescriptionSelector) &&
        string.IsNullOrWhiteSpace(CoverImageSelector) && string.IsNullOrWhiteSpace(ScreenshotSelector) &&
        string.IsNullOrWhiteSpace(DownloadLinkSelector) && string.IsNullOrWhiteSpace(MirrorLabels) &&
        string.IsNullOrWhiteSpace(PartSetLabelSelector);
}

/// <summary>
/// Remembers every URL a crawl has looked at, so re-crawls only fetch new or changed pages
/// (thousands of historical posts stay cheap to refresh).
/// </summary>
public sealed class CrawlUrlRecord
{
    /// <summary>Normalised absolute URL.</summary>
    [BsonId]
    public string Url { get; set; } = string.Empty;

    public Guid SourceId { get; set; }

    /// <summary>True if the page produced a store entry; false for non-game pages (news, guides…).</summary>
    public bool IsGame { get; set; }

    public DateTime LastCrawledUtc { get; set; }

    /// <summary>&lt;lastmod&gt; from the sitemap at the time of the crawl, if any.</summary>
    public DateTime? LastModifiedUtc { get; set; }
}

/// <summary>Live crawl statistics for progress UI.</summary>
public sealed record CrawlProgress(
    CrawlPhase Phase,
    string Method,
    int PagesScanned,
    int UrlsDiscovered,
    int UrlsProcessed,
    int UrlsSkipped,
    int GamesFound,
    int Errors,
    string Message)
{
    /// <summary>0–100 once discovery is finished; null while the total is still unknown.</summary>
    public int? Percent => Phase == CrawlPhase.Fetching && UrlsDiscovered > 0
        ? (int?)(int)Math.Clamp((UrlsProcessed + UrlsSkipped) * 100L / UrlsDiscovered, 0, 100)
        : Phase == CrawlPhase.Completed ? 100 : null;

    public static CrawlProgress Starting { get; } = new(CrawlPhase.Discovering, string.Empty, 0, 0, 0, 0, 0, 0, "Starting…");
}

public enum CrawlPhase
{
    /// <summary>Reading sitemaps / walking listing pages. New URLs keep arriving.</summary>
    Discovering,

    /// <summary>Discovery finished; remaining game pages are being fetched.</summary>
    Fetching,

    Completed,
    Cancelled,
    Failed,
}
