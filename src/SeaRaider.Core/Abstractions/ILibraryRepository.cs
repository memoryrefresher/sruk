using SeaRaider.Core.Models;

namespace SeaRaider.Core.Abstractions;

/// <summary>
/// Local persistence for the catalog, sources, installs, download jobs and settings.
/// The API is synchronous on purpose: the embedded database is in-process and fast, and
/// callers already run it from background tasks. Implementations must be thread-safe.
/// </summary>
public interface ILibraryRepository
{
    // Catalog -------------------------------------------------------------------------------

    /// <summary>Raised after catalog entries were written or removed (any thread).</summary>
    event EventHandler<CatalogEntriesChangedEventArgs>? CatalogEntriesChanged;

    /// <summary>Full records of every game. Expensive on big catalogs — prefer <see cref="GetCatalogEntries"/>.</summary>
    IReadOnlyList<GameMetadata> GetAllGames();
    GameMetadata? GetGame(string gameId);

    /// <summary>Writes full records (and their catalog entries).</summary>
    void UpsertGames(IEnumerable<GameMetadata> games);

    /// <summary>
    /// Writes results from live search / browsing: full records replace anything; stubs (DetailsLoaded = false)
    /// never overwrite a fully loaded game, they only fill missing upload dates / covers. Returns what changed.
    /// </summary>
    IReadOnlyList<GameMetadata> MergeGames(IEnumerable<GameMetadata> games);

    /// <summary>Lightweight summaries of every stored game, for the in-memory store index.</summary>
    IReadOnlyList<CatalogEntry> GetCatalogEntries();
    int DeleteGamesForSource(Guid sourceId);
    int CountGamesForSource(Guid sourceId);

    // Sources -------------------------------------------------------------------------------

    /// <summary>A download source or an upcoming site was added, changed or removed (any thread).</summary>
    event EventHandler? SourcesChanged;

    IReadOnlyList<GameSource> GetSources();
    GameSource? GetSource(Guid sourceId);
    void UpsertSource(GameSource source);
    void DeleteSource(Guid sourceId);

    // Crawl state (incremental re-crawls) -----------------------------------------------------
    /// <summary>Every URL a crawl of this source has seen, keyed by normalised URL.</summary>
    IReadOnlyDictionary<string, CrawlUrlRecord> GetCrawlRecords(Guid sourceId);
    void UpsertCrawlRecords(IEnumerable<CrawlUrlRecord> records);
    int DeleteCrawlRecordsForSource(Guid sourceId);

    // Installs ------------------------------------------------------------------------------
    IReadOnlyList<InstalledGame> GetInstalls();
    InstalledGame? GetInstall(string gameId);
    void UpsertInstall(InstalledGame install);

    // Download jobs -------------------------------------------------------------------------
    IReadOnlyList<DownloadJob> GetJobs();
    void UpsertJob(DownloadJob job);
    void DeleteJob(Guid jobId);

    // Settings ------------------------------------------------------------------------------
    /// <summary>Pictures found for a game outside its page (Steam), by <see cref="Catalog.TitleNormalizer.DedupKey"/>.</summary>
    ArtworkRecord? GetArtwork(string key);
    void UpsertArtwork(ArtworkRecord record);

    // Media tab (videos and streams) ------------------------------------------------------------
    IReadOnlyList<MediaSource> GetMediaSources();
    void UpsertMediaSource(MediaSource source);
    void DeleteMediaSource(Guid sourceId);
    IReadOnlyList<MediaItem> GetMediaItems(Guid sourceId);
    void ReplaceMediaItems(Guid sourceId, IReadOnlyCollection<MediaItem> items);

    /// <summary>Saves changes to some of a source's items (pictures found later), leaving the rest as they are.</summary>
    void UpsertMediaItems(IReadOnlyCollection<MediaItem> items);

    // Upcoming releases & wishlist ---------------------------------------------------------------
    IReadOnlyList<UpcomingSource> GetUpcomingSources();
    void UpsertUpcomingSource(UpcomingSource source);
    void DeleteUpcomingSource(Guid sourceId);

    IReadOnlyList<UpcomingRelease> GetUpcomingReleases();

    /// <summary>Replaces what <paramref name="sourceId"/> listed with <paramref name="releases"/>.</summary>
    void ReplaceUpcomingReleases(Guid sourceId, IReadOnlyCollection<UpcomingRelease> releases);
    void UpsertUpcomingRelease(UpcomingRelease release);

    IReadOnlyList<WishlistItem> GetWishlist();
    WishlistItem? GetWishlistItem(string key);
    void UpsertWishlistItem(WishlistItem item);
    void DeleteWishlistItem(string key);

    AppSettings GetSettings();
    void SaveSettings(AppSettings settings);

    /// <summary>Raised after <see cref="SaveSettings"/> (on the caller's thread) so running services apply changes live.</summary>
    event EventHandler<AppSettings>? SettingsChanged;
}

/// <param name="Upserted">Entries added or updated.</param>
/// <param name="RemovedIds">Entries removed.</param>
/// <param name="IsReset">Many entries changed (e.g. a source was removed): reload everything.</param>
public sealed record CatalogEntriesChangedEventArgs(IReadOnlyList<CatalogEntry> Upserted, IReadOnlyList<string> RemovedIds, bool IsReset);
