using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Models;
using SeaRaider.Core.Scraping;

namespace SeaRaider.Core.Catalog;

/// <summary>
/// The Media tab's sources (YouTube channels and playlists, Twitch channels, video feeds, pages with videos) and their
/// latest videos. Sources are read when added, then in the background: Twitch every few minutes (live or not), the rest
/// every half hour. Events are raised on background threads.
/// </summary>
public sealed class MediaService(IHttpClientFactory httpClientFactory, ILibraryRepository repository, ILogger<MediaService> logger,
    IEnumerable<IBrowserPlayerFinder> browserFinders, MediaArtworkService artwork, WebChallenge.IChallengeResolver browser) : IDisposable
{
    /// <summary>
    /// The page as a browser shows it, for sites whose list of events is drawn by script (Streamed and other live-sports
    /// sites) or that block SeaRaider's own requests: read in SeaRaider's invisible browser. Null when that isn't possible.
    /// </summary>
    private async Task<string?> RenderedAsync(Uri page, CancellationToken ct)
    {
        if (!browser.IsAvailable) return null;
        try
        {
            var rendered = await browser.FetchPageAsync(page, ct).ConfigureAwait(false);
            return rendered is { Content.Length: > 200 } ? rendered.Content : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation(ex, "Browser read of {Page} failed", page);
            return null;
        }
    }

    /// <summary>The page's HTML: SeaRaider's own request, or the browser's when the site refuses it (403, 503, a bot check).</summary>
    private async Task<string> PageHtmlAsync(Uri page, CancellationToken ct)
    {
        try
        {
            return await Pages.GetStringAsync(page, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.ServiceUnavailable
                                                  or System.Net.HttpStatusCode.TooManyRequests)
        {
            return await RenderedAsync(page, ct).ConfigureAwait(false) ?? throw ex;
        }
    }

    /// <summary>Sources whose titles are having their pictures looked up right now (one lookup run per source).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _artworkRunning = new();

    /// <summary>Clicks "Stream 1" in a hidden browser (desktop app only; null in tests and without WebView2).</summary>
    private readonly IBrowserPlayerFinder? _browserFinder = browserFinders.FirstOrDefault();

    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan TwitchRefreshEvery = TimeSpan.FromMinutes(4);
    // A site's list is read again every hour (and when the Media tab opens after a while): what's live now comes from the
    // events' own times, so it needn't be read often, and every read redraws the tab.
    private static readonly TimeSpan CatalogRefreshEvery = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan PageDelay = TimeSpan.FromMilliseconds(500); // between a site's list pages

    /// <summary>A page with at least this many titles (and more titles than videos) is read as a site's list of titles.</summary>
    private const int MinCatalogTitles = 6;

    /// <summary>Title pages already read for their player (title page → play address), for this run of SeaRaider.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _resolved = new(StringComparer.Ordinal);
    private static readonly TimeSpan LoopInterval = TimeSpan.FromMinutes(1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _refreshing = new();

    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _gate = new(2, 2);
    private int _started;

    /// <summary>Sources or their videos changed (reload the tab).</summary>
    public event EventHandler? Changed;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        _ = Task.Run(() => LoopAsync(_lifetime.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                var due = repository.GetMediaSources()
                    .Where(s => s.LastFetchedUtc is null ||
                                DateTime.UtcNow - s.LastFetchedUtc > s.Kind switch
                                {
                                    MediaSourceKind.Twitch => TwitchRefreshEvery,
                                    MediaSourceKind.Catalog => CatalogRefreshEvery,
                                    _ => RefreshEvery,
                                })
                    .ToList();
                await Task.WhenAll(due.Select(s => RefreshAsync(s, ct))).ConfigureAwait(false);
                await Task.Delay(LoopInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Media background refresh stopped");
        }
    }

    public IReadOnlyList<MediaSource> GetSources() => repository.GetMediaSources();

    public IReadOnlyList<MediaItem> GetItems(Guid sourceId)
    {
        var items = repository.GetMediaItems(sourceId);
        // Games only, also for what an earlier read stored (sport links such as "NHL Streams" are not games).
        return items.Any(i => i.EventName is not null) ? MediaTitleParser.OnlyGames(items) : items;
    }

    /// <summary>Adds a channel, playlist, feed, page, stream or single video and reads it at once.</summary>
    /// <param name="name">A name to show instead of the one the address gives (optional).</param>
    /// <exception cref="ArgumentException">Not a web address, can't be played, or already added.</exception>
    public async Task<MediaSource> AddSourceAsync(string url, CancellationToken ct = default, string? name = null)
    {
        url = url.Trim();
        if (Uri.TryCreate(url, UriKind.Absolute, out var typed) && MediaStreams.IsUnplayableScheme(typed.Scheme))
            throw new ArgumentException($"{typed.Scheme}:// streams can't be played in the app. Use the stream's HLS address instead (it ends in .m3u8), or ask the stream's provider for it.");
        if (!url.Contains("://", StringComparison.Ordinal)) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("That doesn't look like a web address. Paste a stream or video address, a YouTube or Twitch channel, a video feed or a page with videos.");
        if (repository.GetMediaSources().Any(s => SourcesFile.Key(s.Url) == SourcesFile.Key(uri.AbsoluteUri)))
            throw new ArgumentException("That's already on your Media tab.");

        var source = NewSource(uri);
        if (source.Kind == MediaSourceKind.Page && await MediaStreams.DetectAsync(Pages, uri, ct).ConfigureAwait(false) is { } format)
        {
            // An address that doesn't end in .m3u8 / .mp4 but is one: remembered with its format.
            source.Kind = MediaSourceKind.Stream;
            source.FeedUrl = MediaStreams.WithHint(uri.AbsoluteUri, format);
        }
        if (!string.IsNullOrWhiteSpace(name))
        {
            source.Name = name.Trim();
            source.NameChosen = true;
        }
        repository.UpsertMediaSource(source);
        Changed?.Invoke(this, EventArgs.Empty);
        await RefreshAsync(source, ct).ConfigureAwait(false);
        return source;
    }

    /// <summary>A source record for an address (not read yet; the background loop or <see cref="RefreshAsync"/> reads it).</summary>
    public static MediaSource NewSource(Uri uri)
    {
        var (kind, feed, name) = MediaListParser.Classify(uri);
        return new MediaSource
        {
            Url = uri.AbsoluteUri,
            Kind = kind,
            FeedUrl = feed,
            Name = name ?? (uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host),
        };
    }

    public void RemoveSource(Guid sourceId)
    {
        repository.DeleteMediaSource(sourceId);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task RefreshAllAsync(CancellationToken ct = default) =>
        await Task.WhenAll(repository.GetMediaSources().Select(s => RefreshAsync(s, ct))).ConfigureAwait(false);

    /// <summary>
    /// Reads again every source not read in the last <paramref name="olderThan"/> (the Media tab opening: what's live now,
    /// without waiting for the background round).
    /// </summary>
    public Task RefreshStaleAsync(TimeSpan olderThan, CancellationToken ct = default) =>
        Task.WhenAll(repository.GetMediaSources()
            .Where(s => s.LastFetchedUtc is null || DateTime.UtcNow - s.LastFetchedUtc > olderThan)
            .Select(s => RefreshAsync(s, ct)));

    /// <summary>Reads one source again and replaces its videos (once at a time per source: a second ask while it's read is skipped).</summary>
    public async Task RefreshAsync(MediaSource source, CancellationToken ct = default)
    {
        if (!_refreshing.TryAdd(source.Id, 0)) return;
        try
        {
            await RefreshOnceAsync(source, ct).ConfigureAwait(false);
        }
        finally
        {
            _refreshing.TryRemove(source.Id, out _);
        }
    }

    private async Task RefreshOnceAsync(MediaSource source, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var items = source.Kind switch
            {
                MediaSourceKind.Twitch => new List<MediaItem> { await ReadTwitchAsync(source, ct).ConfigureAwait(false) },
                MediaSourceKind.Stream => new List<MediaItem> { await ReadStreamAsync(source, ct).ConfigureAwait(false) },
                MediaSourceKind.YouTubeChannel or MediaSourceKind.YouTubePlaylist => await ReadYouTubeAsync(source, ct).ConfigureAwait(false),
                MediaSourceKind.Catalog => await ReadCatalogAsync(source, ct).ConfigureAwait(false),
                _ => await ReadFeedOrPageAsync(source, ct).ConfigureAwait(false),
            };
            await FillMissingTitlesAsync(items, ct).ConfigureAwait(false);
            items = items.Where(i => i.Title.Length > 0).ToList();
            foreach (var item in items) EsportsMatches.Apply(item); // Counter-Strike matches: their own section, named after the match

            if (!StillThere(source)) return; // removed while it was being read

            // Titles from a site's list: real pictures of what they are, not the site's own picture.
            List<MediaItem> needArtwork = [];
            if (source.Kind == MediaSourceKind.Catalog)
            {
                DropPlaceholderPictures(items);
                needArtwork = items.Where(i => !artwork.ApplyCached(i)).ToList();
            }
            else if (source.Kind is MediaSourceKind.Stream or MediaSourceKind.Page)
            {
                // A player or embed link: a picture of what's on (team crests for a match, a poster), unless the page has its
                // own. Only titles that say something (not "Stream" or the site's name).
                needArtwork = items.Where(i => i.ThumbnailUrl is null && !IsVagueTitle(i.Title, i.PageUrl) && !artwork.ApplyCached(i)).ToList();
            }
            repository.ReplaceMediaItems(source.Id, items);
            if (needArtwork.Count > 0 && _artworkRunning.TryAdd(source.Id, 0))
                _ = Task.Run(() => FillArtworkAsync(source.Id, needArtwork, _lifetime.Token));
            source.LastCount = items.Count;
            source.LastError = items.Count > 0 ? null
                : source.Kind == MediaSourceKind.Catalog ? "No titles found on this site right now. Try the page that lists its movies or shows."
                : "No videos found here. Try the channel's or site's video page, or its RSS feed.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            source.LastError = $"Couldn't read it: {ex.Message}";
            logger.LogWarning(ex, "Media source {Source} failed", source.Url);
        }
        finally
        {
            source.LastFetchedUtc = DateTime.UtcNow;
            if (StillThere(source)) repository.UpsertMediaSource(source);
            _gate.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private bool StillThere(MediaSource source) => repository.GetMediaSources().Any(s => s.Id == source.Id);

    /// <summary>
    /// The same picture on three cards or more is the site's own (its logo, a "no poster" image), not the title's: dropped,
    /// so the card shows the picture found for the title, or its initials.
    /// </summary>
    private static void DropPlaceholderPictures(List<MediaItem> items)
    {
        var shared = items.Where(i => i.ThumbnailUrl is not null)
            .GroupBy(i => i.ThumbnailUrl!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 3)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.Where(i => i.ThumbnailUrl is not null && shared.Contains(i.ThumbnailUrl)))
            item.ThumbnailUrl = null;
    }

    /// <summary>
    /// Looks up pictures for a source's titles in the background (see <see cref="MediaArtworkService"/>), saving and showing
    /// them a few at a time as they're found.
    /// </summary>
    private async Task FillArtworkAsync(Guid sourceId, List<MediaItem> items, CancellationToken ct)
    {
        var found = new List<MediaItem>();
        void Flush()
        {
            if (found.Count == 0) return;
            if (repository.GetMediaSources().Any(s => s.Id == sourceId))
            {
                // Only titles the source still lists (it may have been read again meanwhile).
                var current = repository.GetMediaItems(sourceId).Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
                repository.UpsertMediaItems(found.Where(i => current.Contains(i.Key)).ToList());
                Changed?.Invoke(this, EventArgs.Empty);
            }
            found.Clear();
        }
        try
        {
            foreach (var item in items)
            {
                if (await artwork.FindAsync(item, ct).ConfigureAwait(false)) found.Add(item);
                if (found.Count >= 6) Flush();
            }
            Flush();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Looking up pictures for media titles stopped");
        }
        finally
        {
            _artworkRunning.TryRemove(sourceId, out _);
        }
    }

    private HttpClient Pages => httpClientFactory.CreateClient(HttpClientNames.Scraper);
    private HttpClient Api => httpClientFactory.CreateClient(HttpClientNames.Metadata);

    private async Task<List<MediaItem>> ReadYouTubeAsync(MediaSource source, CancellationToken ct)
    {
        if (source.FeedUrl is null)
        {
            // @handle, /c/…, /user/…: the channel page names its id once; the feed is kept from then on.
            var html = await Pages.GetStringAsync(source.Url, ct).ConfigureAwait(false);
            var id = MediaListParser.FindChannelId(html) ?? throw new InvalidOperationException("couldn't find the channel on that page");
            source.FeedUrl = MediaListParser.ChannelFeed(id);
            source.AvatarUrl ??= MediaListParser.MetaContent(html, "og:image");
        }
        var (items, title) = MediaListParser.ParseFeed(await Api.GetStringAsync(source.FeedUrl, ct).ConfigureAwait(false), source.Id);
        if (title is not null) source.Name = title;
        return items;
    }

    private async Task<List<MediaItem>> ReadFeedOrPageAsync(MediaSource source, CancellationToken ct)
    {
        var url = source.FeedUrl ?? source.Url;
        var content = await PageHtmlAsync(new Uri(url), ct).ConfigureAwait(false);
        if (MediaListParser.LooksLikeFeed(content))
        {
            source.Kind = MediaSourceKind.Feed;
            var (items, title) = MediaListParser.ParseFeed(content, source.Id);
            if (title is not null) source.Name = title;
            return items;
        }

        source.Kind = MediaSourceKind.Page;
        var pageUri = new Uri(url);
        var (found, pageTitle) = await MediaListParser.ParsePageAsync(content, pageUri, source.Id, ct).ConfigureAwait(false);
        if (pageTitle is not null && source.LastFetchedUtc is null && !source.NameChosen) source.Name = pageTitle;

        // A site that lists titles (a grid of posters): read like the store reads a game site, page after page.
        using (var doc = await MediaTitleParser.OpenAsync(content, pageUri, ct).ConfigureAwait(false))
        {
            var titles = MediaTitleParser.ParseTitles(doc, pageUri, source.Id);
            if (LooksLikeTitleList(titles, doc, pageUri, found.Count))
            {
                source.Kind = MediaSourceKind.Catalog;
                return await CrawlCatalogAsync(source, doc, pageUri, titles, ct).ConfigureAwait(false);
            }
            if (titles.Count == 0 && MediaTitleParser.FindScheduleSections(doc, pageUri).Count >= 2)
            {
                // A "Schedule" area with a link per sport, and the events on those pages: read like a site's sections.
                source.Kind = MediaSourceKind.Catalog;
                return await CrawlCatalogAsync(source, doc, pageUri, titles, ct).ConfigureAwait(false);
            }
        }

        // Nothing in the page as sent: its list may be drawn by script. Read it as the browser shows it.
        if (found.Count == 0 && await RenderedAsync(pageUri, ct).ConfigureAwait(false) is { } rendered)
        {
            using var doc = await MediaTitleParser.OpenAsync(rendered, pageUri, ct).ConfigureAwait(false);
            var titles = MediaTitleParser.ParseTitles(doc, pageUri, source.Id);
            if (LooksLikeTitleList(titles, doc, pageUri, 0) ||
                (titles.Count == 0 && MediaTitleParser.FindScheduleSections(doc, pageUri).Count >= 2))
            {
                source.Kind = MediaSourceKind.Catalog;
                source.NeedsBrowser = true;
                return await CrawlCatalogAsync(source, doc, pageUri, titles, ct).ConfigureAwait(false);
            }
            (found, _) = await MediaListParser.ParsePageAsync(rendered, pageUri, source.Id, ct).ConfigureAwait(false);
            if (found.Count > 0) content = rendered;
        }

        if (found.Count == 0) found = PageAsPlayer(source, content, pageUri, pageTitle);
        return found;
    }

    // =============================================================================================
    // Sites that list titles (Browse grid)
    // =============================================================================================

    /// <summary>
    /// The page lists titles to browse: plenty of them, or a few that are clearly events (live, "A vs B") or that sit in a
    /// site with sections ("Football", "Basketball"): a live-sports site can have only three or four events on at a time.
    /// </summary>
    private static bool LooksLikeTitleList(List<MediaItem> titles, AngleSharp.Dom.IDocument doc, Uri pageUri, int videos)
    {
        if (titles.Count <= videos * 2) return false;
        if (titles.Count >= MinCatalogTitles) return true;
        return titles.Count >= 2 &&
               (titles.Any(t => t.IsLive || t.EventName is not null || MediaTitleParser.Matchup(t.Title) is not null) || MediaTitleParser.FindSections(doc, pageUri).Count > 0);
    }

    /// <summary>A site's list of titles, read again: its first page and the pages after it.</summary>
    private async Task<List<MediaItem>> ReadCatalogAsync(MediaSource source, CancellationToken ct)
    {
        var pageUri = new Uri(source.FeedUrl ?? source.Url);
        var content = source.NeedsBrowser
            ? await RenderedAsync(pageUri, ct).ConfigureAwait(false) ?? await PageHtmlAsync(pageUri, ct).ConfigureAwait(false)
            : await PageHtmlAsync(pageUri, ct).ConfigureAwait(false);
        var doc = await MediaTitleParser.OpenAsync(content, pageUri, ct).ConfigureAwait(false);
        var titles = MediaTitleParser.ParseTitles(doc, pageUri, source.Id);
        var scheduleHome = titles.Count == 0 && MediaTitleParser.FindScheduleSections(doc, pageUri).Count >= 2; // sports to open, no events on this page
        if (titles.Count == 0 && !scheduleHome && !source.NeedsBrowser && await RenderedAsync(pageUri, ct).ConfigureAwait(false) is { } rendered)
        {
            // The list is drawn by script now: read it in the browser from here on.
            doc.Dispose();
            content = rendered;
            doc = await MediaTitleParser.OpenAsync(content, pageUri, ct).ConfigureAwait(false);
            titles = MediaTitleParser.ParseTitles(doc, pageUri, source.Id);
            scheduleHome = titles.Count == 0 && MediaTitleParser.FindScheduleSections(doc, pageUri).Count >= 2;
            if (titles.Count > 0 || scheduleHome) source.NeedsBrowser = true;
        }
        using var docScope = doc;
        if (titles.Count == 0 && !scheduleHome)
        {
            // Nothing listed right now (between events, or the site hiccuped): it stays a site, and the next check reads it
            // again. Turning it into a plain page made it a single "player" box, named after the site, for good.
            return [];
        }
        if (source.LastFetchedUtc is null && !source.NameChosen &&
            (MediaListParser.MetaContent(content, "og:site_name") ?? HtmlTitle(content)) is { Length: > 0 } siteName)
            source.Name = Tidy(siteName, 80) ?? source.Name;
        return await CrawlCatalogAsync(source, doc, pageUri, titles, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the whole site, the way the store reads a game site:
    /// <list type="number">
    ///   <item>The list it starts on, page after page ("Next", page 2, 3 …), up to <see cref="MediaTitleParser.MaxPages"/> pages.</item>
    ///   <item>Then each of its sections (<see cref="MediaTitleParser.FindSections"/>: "Football", "Basketball", "Movies",
    ///         "TV Shows" …), up to <see cref="MediaTitleParser.MaxSectionPages"/> pages each. Every title found there is
    ///         tagged with the section, which is how Browse sorts titles by type; a title keeps the first section it's in.</item>
    /// </list>
    /// At most <see cref="MediaTitleParser.MaxTitles"/> titles, pages a moment apart. What's read so far shows on the Media tab
    /// as the crawl goes. A page that fails ends that list; what was read is kept.
    /// </summary>
    private async Task<List<MediaItem>> CrawlCatalogAsync(MediaSource source, AngleSharp.Dom.IDocument firstDoc, Uri firstPage,
        List<MediaItem> firstTitles, CancellationToken ct)
    {
        var all = new List<MediaItem>();
        var byKey = new Dictionary<string, MediaItem>(StringComparer.Ordinal);
        int AddAll(IEnumerable<MediaItem> titles, string? section)
        {
            var added = 0;
            foreach (var t in titles)
            {
                if (byKey.TryGetValue(t.Key, out var known))
                {
                    known.Category ??= section; // on the home page first, its section says what it is
                    continue;
                }
                if (all.Count >= MediaTitleParser.MaxTitles) break;
                t.Category ??= section;
                t.Order = all.Count;
                byKey[t.Key] = t;
                all.Add(t);
                added++;
            }
            return added;
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // One list, page after page: the first page given (already read) or fetched.
        async Task ReadListAsync(Uri start, AngleSharp.Dom.IDocument? startDoc, string? section, int maxPages)
        {
            var url = start;
            var doc = startDoc;
            var dayPages = new Queue<Uri>(); // day tabs ("Sep 8", "Today") found on the way: each is a page of that day's events
            var limit = maxPages;
            for (var page = 1; page <= limit && url is not null && all.Count < MediaTitleParser.MaxTitles; page++)
            {
                if (!visited.Add(url.AbsoluteUri)) break;
                AngleSharp.Dom.IDocument? owned = null;
                try
                {
                    if (doc is null)
                    {
                        await Task.Delay(PageDelay, ct).ConfigureAwait(false);
                        var content = source.NeedsBrowser
                            ? await RenderedAsync(url, ct).ConfigureAwait(false) ?? await Pages.GetStringAsync(url, ct).ConfigureAwait(false)
                            : await Pages.GetStringAsync(url, ct).ConfigureAwait(false);
                        doc = owned = await MediaTitleParser.OpenAsync(content, url, ct).ConfigureAwait(false);
                    }
                    var titles = MediaTitleParser.ParseTitles(doc, url, source.Id, all.Count);
                    if (AddAll(titles, section) == 0 && page > 1 && dayPages.Count == 0) break; // nothing new: the end of the list (or a page that repeats)
                    if (titles.Count == 0) // day tabs with no events on this page: each day has its own page
                    {
                        foreach (var day in MediaTitleParser.FindDatePages(doc, url))
                        {
                            if (visited.Contains(day.AbsoluteUri) || dayPages.Contains(day)) continue;
                            dayPages.Enqueue(day);
                            limit++;
                        }
                    }
                    url = MediaTitleParser.FindNextPage(doc, url) ?? (dayPages.Count > 0 ? dayPages.Dequeue() : null);
                }
                catch (HttpRequestException ex)
                {
                    logger.LogInformation(ex, "Media site {Source}: stopped at {Page}", source.Url, url);
                    break;
                }
                finally
                {
                    owned?.Dispose();
                    doc = null;
                }
            }
        }

        AddAll(firstTitles, null);
        visited.Add(firstPage.AbsoluteUri);
        // The list it starts on: page 1 is read already, so start from page 2.
        if (MediaTitleParser.FindNextPage(firstDoc, firstPage) is { } second)
            await ReadListAsync(second, null, null, MediaTitleParser.MaxPages - 1).ConfigureAwait(false);
        if (all.Count == 0)
        {
            foreach (var day in MediaTitleParser.FindDatePages(firstDoc, firstPage))
            {
                if (all.Count >= MediaTitleParser.MaxTitles || ct.IsCancellationRequested) break;
                await ReadListAsync(day, null, null, 1).ConfigureAwait(false);
            }
        }
        ShowProgress(source, all);

        // Then every section of the site.
        foreach (var (url, label) in MediaTitleParser.FindSections(firstDoc, firstPage))
        {
            if (all.Count >= MediaTitleParser.MaxTitles || ct.IsCancellationRequested) break;
            var before = all.Count;
            await ReadListAsync(url, null, label, MediaTitleParser.MaxSectionPages).ConfigureAwait(false);
            if (all.Count > before) ShowProgress(source, all);
        }

        // Games only (see OnlyGames): sport links like "NHL Streams" aren't games.
        var games = MediaTitleParser.OnlyGames(all);
        for (var i = 0; i < games.Count; i++) games[i].Order = i;
        return games;
    }

    /// <summary>A long crawl shows what it has so far (the final list replaces it when the crawl ends).</summary>
    private void ShowProgress(MediaSource source, List<MediaItem> items)
    {
        // Only the first read shows as it goes; a re-read of a site already on the tab replaces it once, when it's done
        // (redrawing the tab all through the crawl made the menus hitch).
        if (source.LastFetchedUtc is not null || !StillThere(source)) return;
        var snapshot = items.ToList();
        foreach (var item in snapshot) artwork.ApplyCached(item);
        repository.ReplaceMediaItems(source.Id, snapshot);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A source's own setup for finding its video: where it is, and what to click first.</summary>
    private sealed record PlayerSetup(string? Hint, string? Click, bool PageMode, int Link);

    /// <summary>The player setup of the source this page belongs to (same site), if the user set one.</summary>
    private PlayerSetup? PlayerSetupFor(Uri page)
    {
        static string BaseHost(string host) => host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..].ToLowerInvariant() : host.ToLowerInvariant();
        return repository.GetMediaSources()
            .Where(s => (!string.IsNullOrWhiteSpace(s.PlayerHint) || s.PlayerPageMode) && Uri.TryCreate(s.Url, UriKind.Absolute, out var u) && BaseHost(u.Host) == BaseHost(page.Host))
            .Select(s => new PlayerSetup(string.IsNullOrWhiteSpace(s.PlayerHint) ? null : s.PlayerHint.Trim(),
                string.IsNullOrWhiteSpace(s.PlayerClick) ? null : s.PlayerClick.Trim(), s.PlayerPageMode, s.PlayerLink))
            .FirstOrDefault();
    }

    /// <summary>
    /// The video's address by the user's hint, or null when it isn't found. An address template is filled in. Otherwise, with
    /// nothing to click, the page is read as sent, then as a browser shows it; with something to click (or when the video
    /// shows up late), the page is opened in a hidden browser, the button clicked, and the hint looked for until it matches.
    /// <paramref name="link"/> picks the page's numbered link ("Link-2" = 2); 0 tries the page's links in turn and takes the first
    /// that really starts playing.
    /// </summary>
    private async Task<string?> FindHintedPlayerAsync(PlayerSetup setup, string hint, Uri page, int link, CancellationToken ct)
    {
        if (PlayerHints.IsTemplate(hint)) return PlayerHints.Find(hint, null, null, page);

        if (setup.Click is null && link == 0)
        {
            try
            {
                var html = await PageHtmlAsync(page, ct).ConfigureAwait(false);
                using var doc = await MediaTitleParser.OpenAsync(html, page, ct).ConfigureAwait(false);
                if (PlayerHints.Find(hint, doc, html, page) is { } fromPage) return fromPage;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                logger.LogInformation(ex, "Couldn't read {Page} for its player hint", page);
            }
            if (await RenderedAsync(page, ct).ConfigureAwait(false) is { } shown)
            {
                using var shownDoc = await MediaTitleParser.OpenAsync(shown, page, ct).ConfigureAwait(false);
                if (PlayerHints.Find(hint, shownDoc, shown, page) is { } fromShown) return fromShown;
            }
        }

        if (_browserFinder is null) return null;
        try
        {
            var lookFor = new PlayerPreference([], 0, true) { Hint = hint, ClickSelector = setup.Click, Link = link };
            var found = await _browserFinder.FindPlayerAsync(page, lookFor, ct).ConfigureAwait(false);
            return found is not null && Uri.TryCreate(found, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogInformation(ex, "Looking for the player by hint on {Page} failed", page);
            return null;
        }
    }

    /// <summary>
    /// Sets where the video is on a source's pages (<see cref="PlayerHints"/>) and what to click first to make it appear (a CSS
    /// selector, optional); empty goes back to finding it automatically.
    /// </summary>
    /// <exception cref="ArgumentException">The hint isn't a usable selector, address template or pattern.</exception>
    public void SetPlayerHint(Guid sourceId, string? hint, string? click = null, bool pageMode = false, int link = 0)
    {
        if (PlayerHints.Validate(hint) is { } problem) throw new ArgumentException(problem);
        if (!string.IsNullOrWhiteSpace(click) && PlayerHints.ValidateSelector(click) is { } clickProblem) throw new ArgumentException(clickProblem);
        if (repository.GetMediaSources().FirstOrDefault(s => s.Id == sourceId) is not { } source) return;
        source.PlayerHint = string.IsNullOrWhiteSpace(hint) ? null : hint.Trim();
        source.PlayerClick = string.IsNullOrWhiteSpace(click) ? null : click.Trim();
        source.PlayerPageMode = pageMode;
        source.PlayerLink = Math.Clamp(link, 0, 9);
        repository.UpsertMediaSource(source);
        _resolved.Clear(); // players found under the old hint
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The Media player settings: preferred player sites ("embed.st") and which stream button to pick.</summary>
    public PlayerPreference CurrentPlayerPreference
    {
        get
        {
            var s = repository.GetSettings();
            return PlayerPreference.From(s.MediaPreferredPlayers, s.MediaStreamNumber, s.MediaClickStreams);
        }
    }

    /// <summary>
    /// What to play for a title from a site's list. In order:
    /// <list type="number">
    ///   <item>The chosen stream on the page ("Stream 1") or a player on a preferred site ("embed.st"), when the page's HTML
    ///         names it (<see cref="MediaTitleParser.FindPreferred"/>).</item>
    ///   <item>When it doesn't (the page loads its streams on a click) and clicking is on: the page opened in a hidden browser
    ///         and the stream button clicked; the player that loads is played.</item>
    ///   <item>The page's own player (a stream, the player frame, a video file, a trailer), else the page itself.</item>
    /// </list>
    /// Remembered for the rest of the session (per setting).
    /// </summary>
    public async Task<string> ResolvePlayUrlAsync(string titlePageUrl, CancellationToken ct = default, int link = 0)
    {
        var preference = CurrentPlayerPreference;
        var page = new Uri(titlePageUrl);
        var setup = PlayerSetupFor(page);

        // The site's own page, played whole: the player picks the link and presses play for the user (see PageAssist).
        if (setup is { PageMode: true })
        {
            PageAssist.Set(page.AbsoluteUri, new PageAssistPlan(link > 0 ? link : setup.Link, setup.Click));
            return MediaStreams.WithHint(page.AbsoluteUri, StreamFormat.Embed);
        }

        var cacheKey = titlePageUrl + "\n" + preference.Signature + "\n" + setup?.Hint + "\n" + setup?.Click + "\n" + link;
        if (_resolved.TryGetValue(cacheKey, out var known)) return known;

        // The user said where this site's video is (Media tab › Player…).
        if (setup is { Hint: { } hintText })
        {
            if (await FindHintedPlayerAsync(setup, hintText, page, link, ct).ConfigureAwait(false) is { } hinted)
            {
                var hintedPlay = MediaTitleParser.AsPlay(hinted);
                _resolved[cacheKey] = hintedPlay;
                return hintedPlay;
            }
            logger.LogInformation("Player hint {Hint} found nothing on {Page}; looking the usual way", setup.Hint, titlePageUrl);
        }

        string? play = null;
        var readable = true;
        try
        {
            var html = await Pages.GetStringAsync(page, ct).ConfigureAwait(false);
            using var doc = await MediaTitleParser.OpenAsync(html, page, ct).ConfigureAwait(false);
            var preferred = MediaTitleParser.FindPreferred(doc, html, page, preference);
            if (preferred is not null)
            {
                _resolved[cacheKey] = preferred;
                return preferred;
            }
            play = MediaTitleParser.FindPlayer(doc, html, page);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogInformation(ex, "Couldn't read {Page} for its player", titlePageUrl);
            readable = false;
        }

        // The page shows its streams only after a click (or the preferred site wasn't on it): click "Stream N" in a browser.
        var worthClicking = preference.Hosts.Count > 0 || play is null || MediaTitleParser.IsFallback(play, page);
        if (_browserFinder is not null && preference.ClickInBrowser && preference.IsActive && worthClicking)
        {
            try
            {
                if (await _browserFinder.FindPlayerAsync(page, preference, ct).ConfigureAwait(false) is { } clicked &&
                    Uri.TryCreate(clicked, UriKind.Absolute, out var clickedUri) && clickedUri.Scheme is "http" or "https")
                {
                    var found = MediaStreams.FormatOfPath(clickedUri) is not null ? clickedUri.AbsoluteUri : MediaStreams.WithHint(clickedUri.AbsoluteUri, StreamFormat.Embed);
                    _resolved[cacheKey] = found;
                    return found;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogInformation(ex, "Clicking the stream on {Page} failed", titlePageUrl);
            }
        }

        if (play is null) return MediaStreams.WithHint(page.AbsoluteUri, StreamFormat.Embed); // not remembered: may work next time
        if (readable) _resolved[cacheKey] = play;
        return play;
    }

    /// <summary>One box for a stream or video: live or not (HLS is looked at), with the video's own title and picture when the site gives them.</summary>
    private async Task<MediaItem> ReadStreamAsync(MediaSource source, CancellationToken ct)
    {
        var play = source.FeedUrl ?? source.Url;
        var format = MediaStreams.FormatOf(play);
        var bare = MediaStreams.WithoutHint(play);
        var isLive = false;
        string? title = null, thumbnail = null, channel = null;

        if (format == StreamFormat.Hls && Uri.TryCreate(bare, UriKind.Absolute, out var playlistUri))
        {
            var text = await Pages.GetStringAsync(playlistUri, ct).ConfigureAwait(false);
            isLive = await MediaStreams.IsLiveAsync(text, playlistUri, next => Pages.GetStringAsync(next, ct)).ConfigureAwait(false);
        }
        else if (format is null)
        {
            // A video on a site the player embeds: its own title and picture, when the site tells.
            var link = await DescribeLinkAsync(bare, ct).ConfigureAwait(false);
            title = link.Title;
            thumbnail = link.ThumbnailUrl;
            channel = link.Channel;
            isLive = bare.Contains("kick.com/", StringComparison.OrdinalIgnoreCase) && !bare.Contains("/video/", StringComparison.OrdinalIgnoreCase);
            if (isLive && title == (new Uri(bare)).Host.Replace("www.", "")) title = null;
        }

        var host = new Uri(bare).Host;
        host = host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
        // Embed pages are titled "Stream", "Player" or the site's name: the address usually names the event
        // (".../embed/bayern-munich-vs-arsenal/1").
        if (IsVagueTitle(title, bare) && TitleFromAddress(bare) is { } named) title = named;
        var name = source.NameChosen && source.Name.Length > 0 ? source.Name : title ?? $"{host} stream";
        if (!source.NameChosen && title is not null) source.Name = title;

        return new MediaItem
        {
            Key = $"{source.Id:N}|stream",
            SourceId = source.Id,
            Title = name,
            PlayUrl = play,
            PageUrl = source.Url,
            ThumbnailUrl = thumbnail,
            Channel = channel ?? host,
            IsLive = isLive,
        };
    }

    /// <summary>
    /// A page with no videos of a kind we know: it may itself be a player (embed pages of streaming sites). Stream addresses written
    /// in it are played directly; otherwise the page is shown in a frame in the player.
    /// </summary>
    private static List<MediaItem> PageAsPlayer(MediaSource source, string html, Uri page, string? pageTitle)
    {
        if (IsVagueTitle(pageTitle, page.AbsoluteUri) && TitleFromAddress(page.AbsoluteUri) is { } named) pageTitle = named;
        var title = source.NameChosen && source.Name.Length > 0 ? source.Name : string.IsNullOrWhiteSpace(pageTitle) ? page.Host : pageTitle.Trim();
        var streams = MediaStreams.FindStreamUrls(html)
            .Select(u => Uri.TryCreate(page, u, out var abs) ? abs.AbsoluteUri : null).OfType<string>().ToList();
        var plays = streams.Count > 0
            ? streams.Select(u => MediaStreams.WithHint(u, MediaStreams.FormatOfPath(new Uri(u)) ?? StreamFormat.Hls)).ToList()
            : [MediaStreams.WithHint(page.AbsoluteUri, StreamFormat.Embed)];
        return plays.Select((play, i) => new MediaItem
        {
            Key = $"{source.Id:N}|page{i}",
            SourceId = source.Id,
            Title = plays.Count > 1 ? $"{title} ({i + 1})" : title,
            PlayUrl = play,
            PageUrl = page.AbsoluteUri,
            Channel = page.Host,
            Order = i,
        }).ToList();
    }

    private async Task<MediaItem> ReadTwitchAsync(MediaSource source, CancellationToken ct)
    {
        var channel = new Uri(source.Url).AbsolutePath.Trim('/').ToLowerInvariant();
        source.Name = channel;

        // Live or not: Twitch's live picture redirects to a "404_preview" image while the channel is offline.
        var live = false;
        try
        {
            using var preview = await Api.GetAsync(MediaListParser.TwitchPreview(channel), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var final = preview.RequestMessage?.RequestUri?.AbsoluteUri ?? string.Empty;
            live = preview.IsSuccessStatusCode && !final.Contains("404_preview", StringComparison.OrdinalIgnoreCase);
        }
        catch (HttpRequestException)
        {
        }

        string? title = null, image = null;
        try
        {
            var html = await Pages.GetStringAsync(source.Url, ct).ConfigureAwait(false);
            title = MediaListParser.MetaContent(html, "og:description");
            image = MediaListParser.MetaContent(html, "og:image");
            source.AvatarUrl = image ?? source.AvatarUrl;
        }
        catch (HttpRequestException)
        {
        }
        return MediaListParser.TwitchItem(source.Id, channel, live, title, image ?? source.AvatarUrl);
    }

    /// <summary>YouTube videos found on a page without a usable name: YouTube's public oEmbed has it (and the channel).</summary>
    private async Task FillMissingTitlesAsync(List<MediaItem> items, CancellationToken ct)
    {
        foreach (var item in items.Where(i => i.Title.Length == 0 && MediaUrlHelper.TryGetYouTubeId(i.PlayUrl) is not null).Take(MediaListParser.MaxItems))
        {
            try
            {
                using var json = await Api.GetFromJsonAsync<JsonDocument>(
                    $"https://www.youtube.com/oembed?format=json&url={Uri.EscapeDataString(item.PlayUrl)}", ct).ConfigureAwait(false);
                if (json?.RootElement.TryGetProperty("title", out var title) == true) item.Title = title.GetString() ?? string.Empty;
                if (json?.RootElement.TryGetProperty("author_name", out var author) == true) item.Channel = author.GetString();
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // private or removed video: dropped (no title)
            }
        }
    }

    // =============================================================================================
    // One link (the "Shared" feed)
    // =============================================================================================

    /// <summary>
    /// What a pasted link is: its title, picture and channel (YouTube and Vimeo through their public oEmbed, Twitch and
    /// any other page through the page's own preview tags), plus the start time written into it (?t=…). Never fails for
    /// a valid web address: without details the link is still shared, named after its site.
    /// </summary>
    /// <exception cref="ArgumentException">Not a web address.</exception>
    public async Task<MediaLink> DescribeLinkAsync(string url, CancellationToken ct = default)
    {
        url = (url ?? string.Empty).Trim();
        if (!url.Contains("://", StringComparison.Ordinal)) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || url.Length > 2048)
            throw new ArgumentException("That doesn't look like a link. Paste a YouTube, Twitch or Vimeo address, a video file or a page.");

        var start = Social.MediaTime.FromUrl(url);
        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        var link = new MediaLink(uri.AbsoluteUri, host, null, host, start);

        try
        {
            if (MediaUrlHelper.TryGetYouTubeId(url) is { } youTubeId)
            {
                var watch = MediaListParser.Watch(youTubeId);
                link = link with { Url = watch, ThumbnailUrl = MediaListParser.YouTubeThumbnail(youTubeId), Channel = "YouTube" };
                return await FromOEmbedAsync(link, $"https://www.youtube.com/oembed?format=json&url={Uri.EscapeDataString(watch)}", ct).ConfigureAwait(false);
            }
            if (uri.Host.EndsWith("vimeo.com", StringComparison.OrdinalIgnoreCase))
                return await FromOEmbedAsync(link with { Channel = "Vimeo" },
                    $"https://vimeo.com/api/oembed.json?url={Uri.EscapeDataString(uri.AbsoluteUri)}", ct).ConfigureAwait(false);
            if (TrailerPlayerUrls.IsVideoFile(uri))
                return link with { Title = Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(uri.AbsolutePath)) is { Length: > 0 } file ? file : host };

            // Twitch and any other page: its preview tags.
            var html = await Pages.GetStringAsync(uri, ct).ConfigureAwait(false);
            var title = MediaListParser.MetaContent(html, "og:title") ?? MediaListParser.MetaContent(html, "twitter:title") ?? HtmlTitle(html);
            var image = MediaListParser.MetaContent(html, "og:image") ?? MediaListParser.MetaContent(html, "twitter:image");
            var site = MediaListParser.MetaContent(html, "og:site_name");
            if (uri.Host.EndsWith("twitch.tv", StringComparison.OrdinalIgnoreCase))
            {
                site = "Twitch";
                // A channel: its live picture while live (Twitch's own og:image is just the profile picture).
                var channel = uri.AbsolutePath.Trim('/');
                if (channel.Length is >= 3 and <= 25 && !channel.Contains('/') && await IsTwitchLiveAsync(channel.ToLowerInvariant(), ct).ConfigureAwait(false))
                {
                    image = MediaListParser.TwitchPreview(channel.ToLowerInvariant());
                    title = MediaListParser.MetaContent(html, "og:description") is { Length: > 0 } stream ? stream : title;
                }
            }
            return link with
            {
                Title = Tidy(title, 200) ?? link.Title,
                ThumbnailUrl = Absolute(image, uri),
                Channel = Tidy(site, 60) ?? link.Channel,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogInformation(ex, "Couldn't read details for {Url}", url);
            return link;
        }
    }

    private async Task<MediaLink> FromOEmbedAsync(MediaLink link, string oEmbed, CancellationToken ct)
    {
        using var json = await Api.GetFromJsonAsync<JsonDocument>(oEmbed, ct).ConfigureAwait(false);
        string? Read(string name) => json?.RootElement.TryGetProperty(name, out var v) == true && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return link with
        {
            Title = Tidy(Read("title"), 200) ?? link.Title,
            Channel = Tidy(Read("author_name"), 60) ?? link.Channel,
            ThumbnailUrl = link.ThumbnailUrl ?? Absolute(Read("thumbnail_url"), new Uri(link.Url)),
        };
    }

    private async Task<bool> IsTwitchLiveAsync(string channel, CancellationToken ct)
    {
        try
        {
            using var preview = await Api.GetAsync(MediaListParser.TwitchPreview(channel), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var final = preview.RequestMessage?.RequestUri?.AbsoluteUri ?? string.Empty;
            return preview.IsSuccessStatusCode && !final.Contains("404_preview", StringComparison.OrdinalIgnoreCase);
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private static readonly HashSet<string> PlayerWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "stream", "streams", "streaming", "embed", "embeds", "player", "players", "watch", "live", "video", "videos", "tv", "hd",
        "online", "free", "server", "source", "channel", "home", "iframe", "e", "v", "p", "play", "link", "links", "event", "events",
        "match", "matches", "sports", "sport", "alpha", "bravo", "charlie", "delta", "echo", "golf", "admin", "main", "en",
    };

    /// <summary>
    /// A title that doesn't say what's on: empty, the site's own name ("Streamed", "embedsports.top"), or only player words
    /// ("Stream 1", "Live Player", "Embed").
    /// </summary>
    internal static bool IsVagueTitle(string? title, string? address)
    {
        if (string.IsNullOrWhiteSpace(title)) return true;
        var words = System.Text.RegularExpressions.Regex.Split(title.ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(w => w.Length > 0).ToList();
        if (words.Count == 0) return true;
        var siteWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Uri.TryCreate(MediaStreams.WithoutHint(address ?? string.Empty), UriKind.Absolute, out var uri))
            foreach (var part in uri.Host.Split('.')) siteWords.Add(part);
        return words.All(w => PlayerWords.Contains(w) || siteWords.Contains(w) || w.All(char.IsDigit) || w is "com" or "net" or "org" or "to" or "su" or "top" or "io");
    }

    /// <summary>
    /// What an embed address names: the most wordy part of its path, made readable
    /// (".../embed/admin/bayern-munich-vs-arsenal/1" → "Bayern Munich vs Arsenal"). Null when no part reads as a name.
    /// </summary>
    internal static string? TitleFromAddress(string? address)
    {
        if (!Uri.TryCreate(MediaStreams.WithoutHint(address ?? string.Empty), UriKind.Absolute, out var uri)) return null;
        string? best = null;
        var bestWords = 0;
        foreach (var segment in uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString))
        {
            var bare = System.Text.RegularExpressions.Regex.Replace(segment, @"\.(html?|php|aspx?)$", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var words = bare.Split(['-', '_', '+', ' ', '.'], StringSplitOptions.RemoveEmptyEntries)
                .Where(w => !w.All(char.IsDigit))                                         // "1", "20261022"
                .Where(w => !(w.Length >= 8 && w.Any(char.IsDigit) && w.Any(char.IsLetter))) // ids: "a8f3k2l9x"
                .ToList();
            var meaningful = words.Count(w => w.Any(char.IsLetter) && !PlayerWords.Contains(w));
            if (meaningful >= 2 && meaningful > bestWords)
            {
                bestWords = meaningful;
                best = string.Join(' ', words.Select(w => w.Equals("vs", StringComparison.OrdinalIgnoreCase) || w.Equals("v", StringComparison.OrdinalIgnoreCase)
                    ? "vs"
                    : w.Length <= 3 && w.All(char.IsLetter) && w.ToUpperInvariant() == w ? w
                    : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
            }
        }
        return best;
    }

    private static string? HtmlTitle(string html) =>
        System.Text.RegularExpressions.Regex.Match(html, @"<title[^>]*>(?<t>[^<]{1,300})</title>", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            is { Success: true } m ? System.Net.WebUtility.HtmlDecode(m.Groups["t"].Value) : null;

    private static string? Tidy(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var clean = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return clean.Length > max ? clean[..max] : clean;
    }

    private static string? Absolute(string? url, Uri page) =>
        !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(page, url.Trim(), out var uri) && uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}

/// <summary>A link described for sharing (see <see cref="MediaService.DescribeLinkAsync"/>).</summary>
/// <param name="Url">The link, tidied (a YouTube video becomes its watch page, without the start time).</param>
/// <param name="StartSeconds">The start time written into the link, if any.</param>
public sealed record MediaLink(string Url, string Title, string? ThumbnailUrl, string? Channel, int? StartSeconds);

/// <summary>Video file addresses (played directly by the in-app player).</summary>
public static class TrailerPlayerUrls
{
    private static readonly string[] Extensions = [".mp4", ".webm", ".m4v", ".mov", ".m3u8", ".mpd"];

    public static bool IsVideoFile(Uri uri) => Extensions.Any(e => uri.AbsolutePath.EndsWith(e, StringComparison.OrdinalIgnoreCase));
}
