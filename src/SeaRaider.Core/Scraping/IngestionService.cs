using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Scraping;

/// <summary>
/// Manages sources and runs their crawls as background tasks (one per source at a time).
/// Games streamed by the crawler are written to the catalog in small batches (every ~12 games or
/// 1.5 s), and each batch raises <see cref="CatalogChanged"/> with the affected ids — so the store
/// fills in smoothly while a site with thousands of posts is still being crawled.
/// </summary>
public sealed class IngestionService(
    IEnumerable<IScraperService> scrapers,
    ILibraryRepository repository,
    ILogger<IngestionService> logger) : IIngestionService, IDisposable
{
    private const int BatchSize = 12;
    private static readonly TimeSpan BatchInterval = TimeSpan.FromSeconds(1.5);

    private readonly IReadOnlyList<IScraperService> _scrapers = scrapers.OrderByDescending(s => s.Priority).ToList();
    private readonly ConcurrentDictionary<Guid, (CancellationTokenSource Cts, Task Task)> _running = new();
    private readonly ConcurrentDictionary<Guid, CrawlProgress> _lastProgress = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _startLock = new();

    public event EventHandler<CatalogChangedEventArgs>? CatalogChanged;
    public event EventHandler<CrawlStatusEventArgs>? CrawlStatusChanged;

    // =============================================================================================
    // Sources
    // =============================================================================================

    public Task<GameSource> AddSourceAsync(string url, GameSource? template = null, CancellationToken cancellationToken = default)
    {
        var uri = NormalizeSourceUrl(url);

        if (repository.GetSources().Any(s => string.Equals(s.Url, uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"{uri.Host} is already in your sources.");

        ValidatePatterns(template);
        var known = SiteProfiles.Find(uri.Host); // FitGirl, SteamRIP …: a proper name and their non-game pages excluded
        var source = new GameSource
        {
            Url = uri.AbsoluteUri,
            DisplayName = known?.DisplayName ?? (uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host),
            Mode = template?.Mode ?? CrawlMode.Auto,
            MaxPages = template?.MaxPages ?? 1_000,
            MaxGames = template?.MaxGames ?? 20_000,
            UrlIncludePattern = template?.UrlIncludePattern,
            UrlExcludePattern = string.IsNullOrWhiteSpace(template?.UrlExcludePattern) ? known?.UrlExcludePattern : template!.UrlExcludePattern,
            RespectRobotsTxt = template?.RespectRobotsTxt ?? true,
            RequestDelayMs = template?.RequestDelayMs ?? 750,
            Profile = template?.Profile ?? new ScraperProfile(),
            SearchMode = template?.SearchMode ?? SearchMode.Auto,
            SearchUrlTemplate = template?.SearchUrlTemplate,
            IsWebGame = template?.IsWebGame ?? false,
        };
        repository.UpsertSource(source);

        // No upfront crawl: the store searches and browses the site live. A full offline index can still be
        // built from the Sources page (StartCrawl).
        return Task.FromResult(source);
    }

    public void UpdateSource(GameSource source)
    {
        ValidatePatterns(source);
        repository.UpsertSource(source);
    }

    public async Task RemoveSourceAsync(Guid sourceId, bool removeGames, CancellationToken cancellationToken = default)
    {
        if (_running.TryGetValue(sourceId, out var running))
        {
            try { running.Cts.Cancel(); } catch (ObjectDisposedException) { }
            try { await running.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }
        }

        repository.DeleteSource(sourceId);
        repository.DeleteCrawlRecordsForSource(sourceId);
        _lastProgress.TryRemove(sourceId, out _);

        if (removeGames)
        {
            var removed = repository.DeleteGamesForSource(sourceId);
            logger.LogInformation("Removed source {Source} and {Count} store entries", sourceId, removed);
            CatalogChanged?.Invoke(this, CatalogChangedEventArgs.Reset);
        }
    }

    // =============================================================================================
    // Background crawls
    // =============================================================================================

    public Task StartCrawl(Guid sourceId, bool fullRescan = false)
    {
        lock (_startLock)
        {
            if (_running.TryGetValue(sourceId, out var existing)) return existing.Task;

            var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var task = Task.Run(async () =>
            {
                await registered.Task.ConfigureAwait(false); // ensure the entry exists before we can remove it
                try
                {
                    await CrawlCoreAsync(sourceId, gamesSaved: null, fullRescan, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    // Recorded on the source (LastResult) and in the final progress event; nothing to rethrow to.
                    logger.LogWarning(ex, "Background crawl of {Source} failed", sourceId);
                }
                finally
                {
                    _running.TryRemove(sourceId, out _);
                    cts.Dispose();
                }
            });

            _running[sourceId] = (cts, task);
            registered.SetResult();
            return task;
        }
    }

    public Task StartCrawlAll(bool fullRescan = false) => Task.Run(async () =>
    {
        foreach (var source in repository.GetSources().Where(s => s.IsEnabled && !s.IsWebGame)) // a browser game has nothing to index
        {
            if (_shutdown.IsCancellationRequested) return;
            await StartCrawl(source.Id, fullRescan).ConfigureAwait(false); // sequential: be gentle with sites
        }
    });

    public void CancelCrawl(Guid sourceId)
    {
        if (!_running.TryGetValue(sourceId, out var running)) return;
        try { running.Cts.Cancel(); }
        catch (ObjectDisposedException) { /* finished meanwhile */ }
    }

    public bool IsCrawling(Guid sourceId) => _running.ContainsKey(sourceId);

    public CrawlProgress? GetLastProgress(Guid sourceId) => _lastProgress.TryGetValue(sourceId, out var p) ? p : null;

    public Task CrawlSourceAsync(Guid sourceId, IProgress<int>? gamesSaved, bool fullRescan = false,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => CrawlCoreAsync(sourceId, gamesSaved, fullRescan, cancellationToken), cancellationToken);

    /// <summary>Consumes the crawler's stream, saving games in batches and publishing progress.</summary>
    private async Task CrawlCoreAsync(Guid sourceId, IProgress<int>? gamesSaved, bool fullRescan, CancellationToken ct)
    {
        var source = repository.GetSource(sourceId) ?? throw new InvalidOperationException("Source not found.");
        var uri = new Uri(source.Url);
        var scraper = _scrapers.FirstOrDefault(s => s.CanHandle(uri))
                      ?? throw new NotSupportedException($"No crawler can handle {uri}.");

        logger.LogInformation("Crawling {Url} with {Scraper} (full rescan: {Full})", uri, scraper.Name, fullRescan);

        var progress = new SyncCallbackProgress<CrawlProgress>(p =>
        {
            _lastProgress[sourceId] = p;
            CrawlStatusChanged?.Invoke(this, new CrawlStatusEventArgs(sourceId, p, isRunning: p.Phase is CrawlPhase.Discovering or CrawlPhase.Fetching));
        });
        progress.Report(CrawlProgress.Starting);

        var batch = new List<GameMetadata>(BatchSize);
        var sinceFlush = Stopwatch.StartNew();
        var saved = 0;
        CrawlProgress? final = null;

        void Flush()
        {
            if (batch.Count == 0) return;
            repository.UpsertGames(batch);
            saved += batch.Count;
            var ids = batch.Select(g => g.Id).ToList();
            batch.Clear();
            sinceFlush.Restart();
            gamesSaved?.Report(saved);
            CatalogChanged?.Invoke(this, new CatalogChangedEventArgs(ids, isReset: false));
        }

        try
        {
            var crawlerProgress = new SyncCallbackProgress<CrawlProgress>(p =>
            {
                final = p;
                // The crawler's own "finished" report is held back until the last batch and the source are saved,
                // so listeners that react to "crawl ended" read up-to-date data.
                if (p.Phase is CrawlPhase.Discovering or CrawlPhase.Fetching)
                    progress.Report(p with { GamesFound = Math.Max(p.GamesFound, saved + batch.Count) });
            });

            await foreach (var game in scraper.CrawlAsync(source, new CrawlOptions(fullRescan), crawlerProgress, ct).ConfigureAwait(false))
            {
                game.SourceId = source.Id;
                batch.Add(game);
                if (batch.Count >= BatchSize || sinceFlush.Elapsed >= BatchInterval) Flush();
            }
            Flush();

            source = repository.GetSource(sourceId) ?? source; // may have been edited meanwhile
            source.LastScrapedAtUtc = DateTime.UtcNow;
            source.LastGameCount = repository.CountGamesForSource(sourceId);
            source.LastCrawlMethod = final?.Method;
            source.LastResult = final is { Errors: > 0 } f ? $"OK ({f.Errors} page(s) failed)" : "OK";
            repository.UpsertSource(source);

            progress.Report((final ?? CrawlProgress.Starting) with { Phase = CrawlPhase.Completed, GamesFound = saved });
        }
        catch (OperationCanceledException)
        {
            Flush();
            progress.Report((final ?? CrawlProgress.Starting) with { Phase = CrawlPhase.Cancelled, Message = "Stopped", GamesFound = saved });
            throw;
        }
        catch (Exception ex)
        {
            Flush();
            logger.LogError(ex, "Crawl failed for {Url}", uri);
            source.LastScrapedAtUtc = DateTime.UtcNow;
            source.LastResult = ex.Message;
            repository.UpsertSource(source);
            progress.Report((final ?? CrawlProgress.Starting) with { Phase = CrawlPhase.Failed, Message = ex.Message });
            throw;
        }
    }

    // =============================================================================================
    // Helpers
    // =============================================================================================

    /// <summary>Accepts "example.com/games" as well as full URLs; rejects non-HTTP schemes.</summary>
    internal static Uri NormalizeSourceUrl(string url)
    {
        var trimmed = (url ?? string.Empty).Trim();
        if (!trimmed.Contains("://", StringComparison.Ordinal)) trimmed = "https://" + trimmed;

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(uri.Host) || !uri.Host.Contains('.'))
        {
            throw new ArgumentException($"\"{url}\" is not a valid web address.", nameof(url));
        }
        return uri;
    }

    private static void ValidatePatterns(GameSource? source)
    {
        if (source is null) return;
        if (UrlRules.ValidatePattern(source.UrlIncludePattern) is { } includeError)
            throw new ArgumentException($"\"Only URLs matching\" isn't a valid pattern: {includeError}");
        if (UrlRules.ValidatePattern(source.UrlExcludePattern) is { } excludeError)
            throw new ArgumentException($"\"Skip URLs matching\" isn't a valid pattern: {excludeError}");
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    /// <summary>IProgress that runs its callback synchronously on the reporting thread.</summary>
    private sealed class SyncCallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
