using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AngleSharp.Dom;
using Microsoft.Extensions.Logging;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Scraping;

/// <summary>
/// Full-site crawler (the default <see cref="IScraperService"/>).
///
/// <para><b>Discovery</b> — how game page URLs are found (<see cref="CrawlMode.Auto"/> tries these in order):</para>
/// <list type="number">
///   <item><b>Feed</b>: the source URL is RSS/Atom → every item is a game candidate.</item>
///   <item><b>Single page</b>: the source URL itself is a game page (downloads or game schema, not a listing).</item>
///   <item><b>XML sitemap</b> (primary): sitemaps declared in robots.txt, else /sitemap_index.xml, /wp-sitemap.xml,
///         /sitemap.xml… Indexes are followed recursively; taxonomy/author/image sitemaps are skipped. This
///         yields every historical post in a few requests.</item>
///   <item><b>Pagination</b> (fallback): walk the listing page by page — rel="next", theme "Older posts" links,
///         or a /page/N/ (?page=N) template — until a page brings no new posts, 404s, or MaxPages is reached.</item>
/// </list>
///
/// <para><b>Fetching</b> — a producer/consumer pipeline: discovery writes URLs into a bounded channel; three
/// workers fetch and parse pages and stream games out through <see cref="CrawlAsync"/> as they're parsed. All
/// requests go through one <see cref="CrawlSession"/> (rate limit, robots.txt, retries).</para>
///
/// <para><b>Incremental re-crawls</b> — every URL looked at is remembered (<see cref="CrawlUrlRecord"/>). A URL is
/// re-fetched only if its sitemap &lt;lastmod&gt; is newer than the last visit, or (without a date) after
/// <see cref="AppSettings.RecrawlAfterDays"/>. Pagination stops after two pages of already-known posts.</para>
/// </summary>
public sealed class ScraperService(
    IHttpClientFactory httpClientFactory,
    ILibraryRepository repository,
    GamePageParser parser,
    ILogger<ScraperService> logger) : IScraperService
{
    public string Name => "Site crawler (sitemap, pagination, feed)";

    public int Priority => 0;

    public bool CanHandle(Uri sourceUrl) => sourceUrl.Scheme is "http" or "https";

    public async IAsyncEnumerable<GameMetadata> CrawlAsync(GameSource source, CrawlOptions options,
        IProgress<CrawlProgress>? progress, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var settings = repository.GetSettings();
        var known = options.FullRescan
            ? new Dictionary<string, CrawlUrlRecord>()
            : repository.GetCrawlRecords(source.Id);

        using var session = new CrawlSession(
            httpClientFactory.CreateClient(HttpClientNames.Scraper),
            settings.UserAgent,
            TimeSpan.FromMilliseconds(Math.Max(0, source.RequestDelayMs)),
            source.RespectRobotsTxt,
            logger);

        var run = new CrawlRun(source, options, session, parser, repository, known,
            TimeSpan.FromDays(Math.Max(1, settings.RecrawlAfterDays)), logger);

        await foreach (var game in run.ExecuteAsync(progress, cancellationToken).ConfigureAwait(false))
            yield return game;
    }

    // =============================================================================================
    // One crawl
    // =============================================================================================

    /// <summary>All state of a single crawl, so the service itself stays stateless and thread-safe.</summary>
    private sealed class CrawlRun
    {
        private readonly GameSource source;
        private readonly CrawlOptions options;
        private readonly CrawlSession session;
        private readonly GamePageParser parser;
        private readonly ILibraryRepository repository;
        private readonly IReadOnlyDictionary<string, CrawlUrlRecord> known;
        private readonly TimeSpan recrawlAfter;
        private readonly ILogger logger;

        public CrawlRun(GameSource source, CrawlOptions options, CrawlSession session, GamePageParser parser,
            ILibraryRepository repository, IReadOnlyDictionary<string, CrawlUrlRecord> known, TimeSpan recrawlAfter, ILogger logger)
        {
            this.source = source;
            this.options = options;
            this.session = session;
            this.parser = parser;
            this.repository = repository;
            this.known = known;
            this.recrawlAfter = recrawlAfter;
            this.logger = logger;
            _sourceUri = new Uri(source.Url);
        }

        private const int Workers = 3;
        private const int MaxSitemapFiles = 2_000;
        private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(300);

        private sealed record WorkItem(Uri Url, DateTime? LastModifiedUtc, GamePageParser.FeedItem? FeedItem = null, bool ForceGame = false);

        private readonly Uri _sourceUri;
        private readonly Channel<WorkItem> _work = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(500) { SingleWriter = true });
        private readonly Channel<GameMetadata> _results = Channel.CreateUnbounded<GameMetadata>();
        private readonly ConcurrentQueue<CrawlUrlRecord> _visited = new();
        private readonly HashSet<string> _emitted = new(StringComparer.OrdinalIgnoreCase); // producer-only
        private readonly List<string> _warnings = [];                                          // producer-only

        private volatile CrawlPhase _phase = CrawlPhase.Discovering;
        private string _method = string.Empty;
        private string _message = "Starting…";
        private int _pages, _discovered, _processed, _skipped, _games, _errors;

        public async IAsyncEnumerable<GameMetadata> ExecuteAsync(IProgress<CrawlProgress>? progress,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var ct = linked.Token;

            await session.InitializeAsync(_sourceUri, ct).ConfigureAwait(false);

            var producer = Task.Run(async () =>
            {
                try { await DiscoverAsync(ct).ConfigureAwait(false); }
                finally
                {
                    _work.Writer.TryComplete();
                    if (_phase == CrawlPhase.Discovering) _phase = CrawlPhase.Fetching;
                }
            }, CancellationToken.None);

            var consumers = Enumerable.Range(0, Workers)
                .Select(_ => Task.Run(() => ConsumeAsync(ct), CancellationToken.None))
                .ToArray();

            var everything = Task.WhenAll(consumers.Append(producer));
            _ = everything.ContinueWith(_ => _results.Writer.TryComplete(), TaskScheduler.Default);
            var reporter = Task.Run(() => ReportLoopAsync(progress, everything, ct), CancellationToken.None);

            var failed = false;
            try
            {
                await foreach (var game in _results.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                    yield return game;

                await everything.ConfigureAwait(false); // surfaces discovery errors (e.g. site unreachable)
            }
            finally
            {
                var cancelled = cancellationToken.IsCancellationRequested;
                failed = everything.IsFaulted;
                linked.Cancel(); // stops workers if the caller stopped enumerating early
                try { await Task.WhenAll(everything, reporter).ConfigureAwait(false); }
                catch { /* already surfaced above, or cancellation */ }

                FlushVisited();
                _phase = cancelled ? CrawlPhase.Cancelled : failed ? CrawlPhase.Failed : CrawlPhase.Completed;
                if (_phase == CrawlPhase.Completed)
                    _message = $"Done: {_games} game(s) from {_processed} page(s), {_skipped} unchanged";
                else if (_phase == CrawlPhase.Failed)
                    _message = everything.Exception?.GetBaseException().Message ?? "Crawl failed";
                else
                    _message = "Cancelled";
                progress?.Report(Snapshot());
                logger.LogInformation("Crawl of {Source} finished: {Message}", source.DisplayName, _message);
            }
        }

        // ---- discovery (single producer) ------------------------------------------------------

        private async Task DiscoverAsync(CancellationToken ct)
        {
            switch (source.Mode)
            {
                case CrawlMode.SinglePage:
                    _method = "single page";
                    await EmitAsync(_sourceUri, null, ct, forceGame: true).ConfigureAwait(false);
                    return;

                case CrawlMode.Sitemap:
                    if (!await CrawlSitemapsAsync(ct).ConfigureAwait(false))
                        throw new InvalidOperationException("No usable XML sitemap was found for this site.");
                    return;

                case CrawlMode.Pagination:
                    await PaginateAsync(_sourceUri, firstPage: null, ct).ConfigureAwait(false);
                    return;
            }

            // Auto: look at the source URL first.
            _message = "Reading the source page…";
            var first = await session.GetAsync(_sourceUri, ct).ConfigureAwait(false);
            if (first.Disallowed) throw new InvalidOperationException("robots.txt does not allow crawling this address.");
            if (!first.IsSuccess) throw new HttpRequestException($"{_sourceUri} returned {(int?)first.Status ?? 0}.");

            var text = first.Text;
            if (GamePageParser.LooksLikeFeed(first.MediaType, text))
            {
                _method = "feed";
                foreach (var item in GamePageParser.ParseFeed(text, first.FinalUri).Take(source.MaxGames))
                {
                    var url = item.Link ?? (GamePageParser.EnclosureLinks(item).FirstOrDefault()?.Url is { } u ? new Uri(u) : null);
                    if (url is not null) await EmitAsync(url, null, ct, feedItem: item).ConfigureAwait(false);
                }
                return;
            }

            using var doc = await GamePageParser.ParseHtmlAsync(text, first.FinalUri, ct).ConfigureAwait(false);
            var itemLinks = parser.FindItemLinks(doc, first.FinalUri, source.Profile, _warnings);
            var isListing = parser.IsListingPage(doc, first.FinalUri, source.Profile, itemLinks, _warnings);

            if (!isListing && !UrlRules.IsNonContentPath(first.FinalUri) &&
                GamePageParser.IsGamePage(doc, parser.ExtractGame(doc, first.FinalUri, source, _warnings)))
            {
                _method = "single page";
                await EmitAsync(first.FinalUri, null, ct, forceGame: true).ConfigureAwait(false);
                return;
            }

            if (await CrawlSitemapsAsync(ct).ConfigureAwait(false)) return;

            await PaginateAsync(first.FinalUri, doc, ct).ConfigureAwait(false);
        }

        /// <summary>Primary method. Returns false when the site has no usable sitemap.</summary>
        private async Task<bool> CrawlSitemapsAsync(CancellationToken ct)
        {
            _message = "Looking for XML sitemaps…";
            var declared = session.Robots.Sitemaps.Where(s => UrlRules.SameSite(s, _sourceUri)).ToList();

            // robots.txt-declared sitemaps are processed together; otherwise try well-known locations one by one.
            IEnumerable<IReadOnlyList<Uri>> batches = declared.Count > 0
                ? new[] { (IReadOnlyList<Uri>)declared }
                : SitemapReader.CandidateLocations(_sourceUri).Select(u => (IReadOnlyList<Uri>)new[] { u });

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var emittedBefore = _discovered;

            foreach (var batch in batches)
            {
                var queue = new Queue<Uri>(batch);
                while (queue.Count > 0 && visited.Count < MaxSitemapFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    var sitemapUri = queue.Dequeue();
                    if (!visited.Add(sitemapUri.AbsoluteUri)) continue;

                    var response = await session.GetAsync(sitemapUri, ct).ConfigureAwait(false);
                    if (!response.IsSuccess) continue;

                    var parsed = SitemapReader.Parse(response.Text, response.FinalUri);
                    if (parsed is null) continue;

                    Interlocked.Increment(ref _pages);
                    _method = "sitemap";
                    _message = $"Reading {Path.GetFileName(sitemapUri.AbsolutePath)}";

                    if (parsed.IsIndex)
                    {
                        foreach (var child in parsed.Entries)
                        {
                            if (UrlRules.SameSite(child.Location, _sourceUri) && SitemapReader.IsLikelyContentSitemap(child.Location))
                                queue.Enqueue(child.Location);
                        }
                        continue;
                    }

                    foreach (var entry in parsed.Entries)
                    {
                        if (_discovered >= source.MaxGames) return true;
                        if (Accept(entry.Location))
                            await EmitAsync(entry.Location, entry.LastModifiedUtc, ct).ConfigureAwait(false);
                    }
                }

                if (_discovered > emittedBefore) return true; // this batch worked; don't try the next location
            }

            return _discovered > emittedBefore;
        }

        /// <summary>Fallback method: walk listing pages until they stop producing new posts.</summary>
        private async Task PaginateAsync(Uri startUri, IDocument? firstPage, CancellationToken ct)
        {
            _method = "pagination";
            var visitedPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pageNumber = UrlRules.PageNumberOf(startUri);
            Uri? pageUri = startUri;
            var knownStreak = 0;

            while (pageUri is not null && _pages < source.MaxPages && _discovered < source.MaxGames)
            {
                ct.ThrowIfCancellationRequested();
                if (!visitedPages.Add(UrlRules.Normalize(pageUri).AbsoluteUri)) break;

                IDocument page;
                var ownsPage = firstPage is null;
                if (firstPage is not null)
                {
                    page = firstPage;
                    firstPage = null;
                }
                else
                {
                    var response = await session.GetAsync(pageUri, ct).ConfigureAwait(false);
                    if (!response.IsSuccess)
                    {
                        _message = $"Stopped at page {pageNumber}: {(response.Disallowed ? "blocked by robots.txt" : $"HTTP {(int?)response.Status ?? 0}")}";
                        break;
                    }
                    page = await GamePageParser.ParseHtmlAsync(response.Text, response.FinalUri, ct).ConfigureAwait(false);
                }

                try
                {
                    Interlocked.Increment(ref _pages);
                    _message = $"Listing page {pageNumber}";

                    var fresh = parser.FindItemLinks(page, pageUri, source.Profile, _warnings)
                        .Where(Accept)
                        .ToList();
                    if (fresh.Count == 0)
                    {
                        _message = $"Reached the end at page {pageNumber}";
                        break;
                    }

                    var queued = 0;
                    foreach (var link in fresh)
                        if (await EmitAsync(link, null, ct).ConfigureAwait(false)) queued++;

                    // Listings are newest-first: two pages of nothing-new means we've caught up.
                    if (!options.FullRescan && known.Count > 0)
                    {
                        knownStreak = queued == 0 ? knownStreak + 1 : 0;
                        if (knownStreak >= 2)
                        {
                            _message = $"Up to date (stopped at page {pageNumber})";
                            break;
                        }
                    }

                    var next = parser.FindNextPageLink(page, pageUri, source.Profile, _warnings);
                    pageNumber++;
                    pageUri = next ?? UrlRules.BuildPageUrl(startUri, pageNumber); // absolute page number
                }
                finally
                {
                    if (ownsPage) page.Dispose();
                }
            }
        }

        /// <summary>Filters a discovered URL (same site, not an archive/taxonomy page, user patterns, not seen).</summary>
        private bool Accept(Uri url)
        {
            var normalized = UrlRules.Normalize(url);
            return UrlRules.SameSite(normalized, _sourceUri)
                   && !UrlRules.IsNonContentPath(normalized)
                   && UrlRules.PassesFilters(normalized, source.UrlIncludePattern, source.UrlExcludePattern)
                   && !_emitted.Contains(normalized.AbsoluteUri);
        }

        /// <summary>Queues a URL for fetching unless it's already known and unchanged. Returns true if queued.</summary>
        private async ValueTask<bool> EmitAsync(Uri url, DateTime? lastModifiedUtc, CancellationToken ct,
            GamePageParser.FeedItem? feedItem = null, bool forceGame = false)
        {
            var normalized = UrlRules.Normalize(url);
            if (!_emitted.Add(normalized.AbsoluteUri)) return false;
            Interlocked.Increment(ref _discovered);

            if (!forceGame && IsUpToDate(normalized, lastModifiedUtc))
            {
                Interlocked.Increment(ref _skipped);
                return false;
            }

            await _work.Writer.WriteAsync(new WorkItem(normalized, lastModifiedUtc, feedItem, forceGame), ct).ConfigureAwait(false);
            return true;
        }

        private bool IsUpToDate(Uri url, DateTime? lastModifiedUtc)
        {
            if (!known.TryGetValue(url.AbsoluteUri, out var record)) return false;
            return lastModifiedUtc is { } modified
                ? record.LastCrawledUtc >= modified
                : DateTime.UtcNow - record.LastCrawledUtc < recrawlAfter;
        }

        // ---- fetching (several consumers) ----------------------------------------------------

        private async Task ConsumeAsync(CancellationToken ct)
        {
            await foreach (var item in _work.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    var game = await ProcessAsync(item, ct).ConfigureAwait(false);
                    Interlocked.Increment(ref _processed);
                    if (game is not null)
                    {
                        Interlocked.Increment(ref _games);
                        await _results.Writer.WriteAsync(game, ct).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    Interlocked.Increment(ref _errors);
                    logger.LogDebug(ex, "Failed to process {Url}", item.Url);
                }
            }
        }

        private async Task<GameMetadata?> ProcessAsync(WorkItem item, CancellationToken ct)
        {
            GameMetadata? game = null;

            if (item.FeedItem is { Link: null } feedOnly)
            {
                game = GamePageParser.FromFeedItem(feedOnly, source.Id);
            }
            else
            {
                var response = await session.GetAsync(item.Url, ct).ConfigureAwait(false);
                if (response.Disallowed)
                {
                    Interlocked.Increment(ref _skipped);
                    return null;
                }
                if (!response.IsSuccess)
                {
                    Interlocked.Increment(ref _errors);
                    return null; // not remembered, so it's retried next crawl
                }

                using var doc = await GamePageParser.ParseHtmlAsync(response.Text, response.FinalUri, ct).ConfigureAwait(false);
                var warnings = new List<string>();
                var parsed = parser.ExtractGame(doc, response.FinalUri, source, warnings);

                if (item.FeedItem is { } fromFeed)
                {
                    if (parsed.DownloadLinks.Count == 0) parsed.DownloadLinks = GamePageParser.EnclosureLinks(fromFeed);
                    parsed.CoverImageUrl ??= fromFeed.ImageUrl;
                }

                var isGame = item.ForceGame || GamePageParser.IsGamePage(doc, parsed);
                if (isGame && !string.IsNullOrWhiteSpace(parsed.Title)) game = parsed;
            }

            _visited.Enqueue(new CrawlUrlRecord
            {
                Url = item.Url.AbsoluteUri,
                SourceId = source.Id,
                IsGame = game is not null,
                LastCrawledUtc = DateTime.UtcNow,
                LastModifiedUtc = item.LastModifiedUtc,
            });
            return game;
        }

        // ---- progress & persistence -----------------------------------------------------------

        private async Task ReportLoopAsync(IProgress<CrawlProgress>? progress, Task completion, CancellationToken ct)
        {
            using var timer = new PeriodicTimer(ProgressInterval);
            try
            {
                while (!completion.IsCompleted && await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    progress?.Report(Snapshot());
                    if (_visited.Count >= 100) FlushVisited();
                }
            }
            catch (OperationCanceledException)
            {
                // crawl stopped
            }
        }

        /// <summary>Persists visited-URL records in batches (cheap, and lets a cancelled crawl resume where it was).</summary>
        private void FlushVisited()
        {
            var batch = new List<CrawlUrlRecord>();
            while (_visited.TryDequeue(out var record)) batch.Add(record);
            if (batch.Count == 0) return;

            try { repository.UpsertCrawlRecords(batch); }
            catch (Exception ex) { logger.LogWarning(ex, "Could not save crawl state"); }
        }

        private CrawlProgress Snapshot() => new(
            _phase, _method, Volatile.Read(ref _pages), Volatile.Read(ref _discovered), Volatile.Read(ref _processed),
            Volatile.Read(ref _skipped), Volatile.Read(ref _games), Volatile.Read(ref _errors), _message);
    }
}
