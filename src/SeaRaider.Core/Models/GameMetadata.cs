using System.Security.Cryptography;
using System.Text;
using LiteDB;

namespace SeaRaider.Core.Models;

/// <summary>
/// Everything the storefront knows about a game, as scraped from a source page.
/// Persisted in LiteDB so the store keeps working offline.
/// </summary>
public sealed class GameMetadata
{
    /// <summary>Stable id derived from the detail-page URL, so re-scraping upserts instead of duplicating.</summary>
    [BsonId]
    public string Id { get; set; } = string.Empty;

    /// <summary>The <see cref="GameSource"/> this entry was found through.</summary>
    public Guid SourceId { get; set; }

    /// <summary>Canonical URL of the game's detail page.</summary>
    public string PageUrl { get; set; } = string.Empty;

    /// <summary>Clean display title ("Portal Dungeon: Goblin Escape"), without version / DLC / release noise.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Title exactly as the source published it ("Portal Dungeon: Goblin Escape - v1.1 + 2 DLCs").</summary>
    public string? RawTitle { get; set; }

    /// <summary>One- or two-sentence teaser used on store cards (≤ ~220 chars).</summary>
    public string ShortDescription { get; set; } = string.Empty;

    /// <summary>Full plain-text description shown on the store page.</summary>
    public string Description { get; set; } = string.Empty;

    public string? CoverImageUrl { get; set; }

    public List<string> ScreenshotUrls { get; set; } = [];

    /// <summary>Trailer / video URLs (YouTube, Vimeo, direct .mp4/.webm).</summary>
    public List<string> TrailerUrls { get; set; } = [];

    /// <summary>Thumbnail per trailer when the provider gave one (same order as <see cref="TrailerUrls"/>).</summary>
    public List<string?> TrailerThumbnailUrls { get; set; } = [];

    /// <summary>Where screenshots / trailers came from when the page had none ("Steam", "IGDB"); null = the page.</summary>
    public string? MediaSource { get; set; }

    /// <summary>Media was cleaned and, if needed, filled from Steam / IGDB.</summary>
    public bool MediaChecked { get; set; }

    /// <summary>Version of the media rules that filled this record (see <see cref="CurrentMediaVersion"/>).</summary>
    public int MediaVersion { get; set; }

    /// <summary>The game's Steam store page, when the media came from Steam (trailer fallback).</summary>
    public string? StorePageUrl { get; set; }

    /// <summary>
    /// 2: full-length Steam trailers (HLS) in trailer order. 3: Steam trailers for every game (not only pages without
    /// screenshots), and no YouTube links picked up from a site's sidebar.
    /// </summary>
    public const int CurrentMediaVersion = 3;

    /// <summary>Media is checked with the current rules (older records are looked at again once).</summary>
    [LiteDB.BsonIgnore]
    public bool MediaUpToDate => MediaChecked && MediaVersion >= CurrentMediaVersion;

    /// <summary>Every downloadable thing found on the page, already classified.</summary>
    public List<DownloadLink> DownloadLinks { get; set; } = [];

    /// <summary>Free-form tags from the page (taxonomy links). Genres are kept separately in <see cref="Genres"/>.</summary>
    public List<string> Tags { get; set; } = [];

    // ---- structured metadata (MetadataParser) ------------------------------------------------

    public List<string> Genres { get; set; } = [];

    public string? Developer { get; set; }

    public string? Publisher { get; set; }

    /// <summary>Release date as written on the page ("12 March 2024").</summary>
    public string? ReleaseDate { get; set; }

    /// <summary>Parsed release date, for sorting (null when unknown or unparseable).</summary>
    public DateTime? ReleaseDateUtc { get; set; }

    /// <summary>When the source posted / uploaded this entry (post date, sitemap or API date).</summary>
    public DateTime? UploadedAtUtc { get; set; }

    /// <summary>Game version or build ("1.1", "Build 18923104").</summary>
    public string? Version { get; set; }

    /// <summary>DLC note ("5 DLCs", "All DLCs", "DLC included"); null when none is mentioned.</summary>
    public string? DlcInfo { get; set; }

    public List<string> Languages { get; set; } = [];

    /// <summary>Download (repacked / compressed) size, e.g. "from 5.2 GB".</summary>
    public string? RepackSize { get; set; }

    /// <summary>Original / installed size, e.g. "12.4 GB".</summary>
    public string? OriginalSize { get; set; }

    /// <summary>Human-readable size (kept for older records): repack size, else original size.</summary>
    public string? SizeText { get; set; }

    // ---- lazy loading ------------------------------------------------------------------------

    /// <summary>
    /// False for lightweight search / listing results (title, link, cover only). Full metadata and download
    /// links are fetched the first time the game is opened or installed.
    /// </summary>
    public bool DetailsLoaded { get; set; } = true;

    /// <summary>Parser version that produced this record; older records are re-parsed lazily when opened.</summary>
    public int MetadataVersion { get; set; }

    /// <summary>Bump when MetadataParser output changes, so stored games get re-cleaned on next view.</summary>
    public const int CurrentMetadataVersion = 5; // 4: stat banners filtered from screenshots, media fallback; 5: shortener mirrors named by their labels

    [BsonIgnore]
    public bool NeedsDetails => !DetailsLoaded || MetadataVersion < CurrentMetadataVersion;

    [BsonIgnore]
    public string? DisplaySize => RepackSize ?? OriginalSize ?? SizeText;

    /// <summary>
    /// A game that runs in a browser (itch.io's HTML5 games): the address of the game itself. "Download &amp; Install" adds
    /// it to the library straight away and Play opens it in SeaRaider's own full-screen game window.
    /// </summary>
    public string? WebGameUrl { get; set; }

    /// <summary>When this record was last refreshed from the web (UTC).</summary>
    public DateTime FetchedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Builds the deterministic id for a detail page.</summary>
    /// <summary>
    /// Fills in whatever a site's page (or an older database record) left empty, so nothing downstream meets a null: lists
    /// become empty lists, text becomes "", and download links without an address are dropped (the rest get their host).
    /// Sites outside the usual layouts are where these come from ("Object reference not set to an instance of an object").
    /// </summary>
    public GameMetadata Normalize()
    {
        Id ??= string.Empty;
        PageUrl ??= string.Empty;
        Title ??= string.Empty;
        ShortDescription ??= string.Empty;
        Description ??= string.Empty;
        ScreenshotUrls = (ScreenshotUrls ?? []).Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
        TrailerUrls = (TrailerUrls ?? []).Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
        TrailerThumbnailUrls ??= [];
        Tags = (Tags ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        Genres = (Genres ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        Languages = (Languages ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();

        var links = new List<DownloadLink>();
        foreach (var link in DownloadLinks ?? [])
        {
            if (link is null || string.IsNullOrWhiteSpace(link.Url)) continue;
            link.Url = link.Url.Trim();
            link.Label ??= string.Empty;
            link.FileName ??= string.Empty;
            link.Mirror ??= string.Empty;
            if (string.IsNullOrEmpty(link.Host))
                link.Host = Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
            links.Add(link);
        }
        DownloadLinks = links;
        return this;
    }

    public static string CreateId(string pageUrl)
    {
        var normalized = pageUrl.Trim().TrimEnd('/').ToLowerInvariant();
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash, 0, 10).ToLowerInvariant();
    }
}

/// <summary>How a <see cref="DownloadLink"/> must be fetched.</summary>
public enum DownloadLinkKind
{
    /// <summary>A single file over HTTP(S) (archive, installer, ISO…).</summary>
    DirectHttp,

    /// <summary>One volume of a split archive (.z01/.zip, .part1.rar, .r00/.rar, .7z.001 …).</summary>
    MultipartArchivePart,

    /// <summary>A link to a .torrent metadata file.</summary>
    TorrentFile,

    /// <summary>A magnet: URI.</summary>
    Magnet,

    /// <summary>
    /// A file on a hosting service (Buzzheavier, Gofile, DataNodes …): a web page that leads to the file.
    /// Resolved to the real file link by SeaRaider where possible, otherwise opened in the verification window.
    /// </summary>
    FileHost,
}

/// <summary>A single classified download link.</summary>
public sealed class DownloadLink
{
    public string Url { get; set; } = string.Empty;

    public DownloadLinkKind Kind { get; set; }

    /// <summary>Anchor text or a generated label ("Part 3", "Magnet").</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>File name inferred from the URL path (may be empty for magnets / redirectors).</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// For multipart volumes: the archive base name shared by all parts of one set
    /// (e.g. "mygame" for mygame.part01.rar … mygame.part09.rar). Null otherwise.
    /// </summary>
    public string? GroupKey { get; set; }

    /// <summary>1-based volume order inside <see cref="GroupKey"/>. 0 when not multipart.</summary>
    public int PartNumber { get; set; }

    /// <summary>Host name, handy for showing "via example.com" in the UI.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// Mirror this link belongs to ("Torrent", "Buzzheavier", "DataNodes", "example.com"). Links of one multipart set
    /// on one mirror share it, so the store page can list each mirror with its own parts.
    /// </summary>
    public string Mirror { get; set; } = string.Empty;

    /// <summary>Size printed next to the link on the page ("5.2 GB"), when there was one.</summary>
    public string? SizeText { get; set; }

    /// <summary>
    /// A page (a link shortener's button labelled "Torrent") that leads to a magnet or .torrent rather than to the files:
    /// listed with the torrents, opened in the download browser to collect the torrent, then downloaded by the torrent client.
    /// </summary>
    public bool LeadsToTorrent { get; set; }

    public override string ToString() => $"{Kind}: {Label} ({Url})";
}
