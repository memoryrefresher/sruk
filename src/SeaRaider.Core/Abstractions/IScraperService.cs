using SeaRaider.Core.Models;

namespace SeaRaider.Core.Abstractions;

/// <summary>
/// A pluggable game source crawler. Register additional implementations in DI to support sites that
/// need bespoke logic (JSON APIs, JS-rendered pages, auth…); <see cref="IIngestionService"/> picks the
/// highest-<see cref="Priority"/> crawler whose <see cref="CanHandle"/> returns true. The built-in
/// <c>ScraperService</c> (sitemap → pagination → feed → single page) has priority 0.
/// </summary>
public interface IScraperService
{
    /// <summary>Display name, shown in source status messages.</summary>
    string Name { get; }

    /// <summary>Higher wins when several crawlers can handle the same URL.</summary>
    int Priority { get; }

    /// <summary>Cheap, synchronous check based on the URL only (host, path, scheme).</summary>
    bool CanHandle(Uri sourceUrl);

    /// <summary>
    /// Crawls the whole source and streams each game as soon as its page is parsed, so callers can
    /// store and display results while the crawl is still running. Runs entirely off the caller's
    /// thread; cancel the token to stop.
    /// </summary>
    IAsyncEnumerable<GameMetadata> CrawlAsync(GameSource source, CrawlOptions options, IProgress<CrawlProgress>? progress,
        CancellationToken cancellationToken);
}

/// <param name="FullRescan">Ignore remembered URLs and re-fetch every page.</param>
public sealed record CrawlOptions(bool FullRescan = false);

/// <summary>Catalog entries were added or updated.</summary>
/// <param name="GameIds">Ids that changed; empty together with <paramref name="IsReset"/> means "reload everything".</param>
public sealed class CatalogChangedEventArgs(IReadOnlyList<string> gameIds, bool isReset) : EventArgs
{
    public IReadOnlyList<string> GameIds { get; } = gameIds;
    public bool IsReset { get; } = isReset;

    public static CatalogChangedEventArgs Reset { get; } = new([], true);
}

/// <summary>Progress of one source's background crawl.</summary>
public sealed class CrawlStatusEventArgs(Guid sourceId, CrawlProgress progress, bool isRunning) : EventArgs
{
    public Guid SourceId { get; } = sourceId;
    public CrawlProgress Progress { get; } = progress;
    public bool IsRunning { get; } = isRunning;
}

/// <summary>Owns the list of sources and runs crawls in the background, saving games as they arrive.</summary>
public interface IIngestionService
{
    /// <summary>Raised (on a background thread) after store entries were added, updated or removed.</summary>
    event EventHandler<CatalogChangedEventArgs>? CatalogChanged;

    /// <summary>Raised (on a background thread, ~3×/s) while a crawl runs, and once when it ends.</summary>
    event EventHandler<CrawlStatusEventArgs>? CrawlStatusChanged;

    /// <summary>Validates and stores a new source. Nothing is crawled up front; the store searches it live.</summary>
    Task<GameSource> AddSourceAsync(string url, GameSource? template = null, CancellationToken cancellationToken = default);

    /// <summary>Saves edited crawl settings for an existing source.</summary>
    void UpdateSource(GameSource source);

    Task RemoveSourceAsync(Guid sourceId, bool removeGames, CancellationToken cancellationToken = default);

    /// <summary>Starts (or returns the already-running) background crawl of one source.</summary>
    Task StartCrawl(Guid sourceId, bool fullRescan = false);

    /// <summary>Starts crawls of every enabled source, one after another, in the background.</summary>
    Task StartCrawlAll(bool fullRescan = false);

    void CancelCrawl(Guid sourceId);

    bool IsCrawling(Guid sourceId);

    /// <summary>Latest progress of the current or last crawl of a source.</summary>
    CrawlProgress? GetLastProgress(Guid sourceId);

    /// <summary>
    /// Crawls one source and awaits completion. Reports the running number of games saved to the store
    /// through <paramref name="gamesSaved"/>, which is all most UIs need.
    /// </summary>
    Task CrawlSourceAsync(Guid sourceId, IProgress<int>? gamesSaved, bool fullRescan = false,
        CancellationToken cancellationToken = default);
}
