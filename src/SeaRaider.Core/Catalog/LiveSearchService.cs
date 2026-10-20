using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Models;
using SeaRaider.Core.Scraping;

namespace SeaRaider.Core.Catalog;

/// <summary>One source's answer to a live search / browse request.</summary>
/// <param name="Results">Entries in the order the site returned them.</param>
/// <param name="HasMore">The site has another page of results.</param>
/// <param name="Error">User-facing reason the source couldn't be searched (null on success).</param>
public sealed record LiveSearchBatch(Guid SourceId, string SourceName, IReadOnlyList<CatalogEntry> Results, bool HasMore, string? Error);

/// <summary>Asks the source sites directly instead of syncing whole catalogs up front.</summary>
public interface ILiveSearchService
{
    /// <summary>
    /// Searches every enabled source in parallel and yields each source's results as soon as they arrive.
    /// An empty <paramref name="query"/> browses the sites' latest posts instead. Results are cached locally
    /// (as stubs or full records), so they also show up in offline searches later.
    /// </summary>
    IAsyncEnumerable<LiveSearchBatch> SearchAsync(string? query, int page, CancellationToken cancellationToken);

    /// <summary>
    /// Like <see cref="SearchAsync(string?, int, CancellationToken)"/>, but only the given sources (that are switched on);
    /// null means every source.
    /// </summary>
    IAsyncEnumerable<LiveSearchBatch> SearchAsync(string? query, int page, IReadOnlyCollection<Guid>? sourceIds,
        CancellationToken cancellationToken);

    /// <summary>Searches (or browses, with an empty query) one source.</summary>
    Task<LiveSearchBatch> SearchSourceAsync(Guid sourceId, string? query, int page, CancellationToken cancellationToken);

    /// <summary>Forgets cached answers so the next request asks the sites again ("Refresh").</summary>
    void InvalidateCache();
}

/// <summary>
/// On-demand search and browsing.
/// <list type="bullet">
///   <item><b>WordPress REST API</b> (<c>/wp-json/wp/v2/posts?search=</c>): one JSON request returns 20 posts with
///         full content, so results arrive complete (download links, info pills) with no extra page loads.</item>
///   <item><b>Site search page</b> (<c>/?s=</c>, <c>/page/N/?s=</c>) or a custom URL template: the results page is
///         parsed into cards — full records when the theme shows posts inline, stubs otherwise.</item>
///   <item><b>Auto</b> mode detects the API once per source and remembers the answer.</item>
/// </list>
/// Identical requests within a few minutes are answered from memory, so paging back and forth or re-typing a
/// query doesn't hit the site again.
/// <para><b>Precision.</b> Sites' own searches match words anywhere in a post (a news post mentioning "hades", a
/// "Hades-like" in another game's description). So the query sent to the site has release tags removed
/// (<see cref="SearchQuery"/>), the WordPress API is asked to search titles only (<c>search_columns=post_title</c>,
/// falling back to full-text when that finds nothing), and every result is scored with <see cref="SearchScorer"/>:
/// weak matches are dropped and the rest come back best first, exact titles on top.</para>
/// </summary>
public sealed class LiveSearchService(
    ILibraryRepository repository,
    IHttpClientFactory httpClientFactory,
    GamePageParser parser,
    CatalogIndexService index,
    SiteReaderCatalog readers,
    ILogger<LiveSearchService> logger) : ILiveSearchService
{
    private const int PageSize = 20;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, (DateTime At, LiveSearchBatch Batch)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _fullTextQueries = new(StringComparer.OrdinalIgnoreCase);

    public IAsyncEnumerable<LiveSearchBatch> SearchAsync(string? query, int page, CancellationToken cancellationToken) =>
        SearchAsync(query, page, null, cancellationToken);

    public async IAsyncEnumerable<LiveSearchBatch> SearchAsync(string? query, int page, IReadOnlyCollection<Guid>? sourceIds,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var sources = repository.GetSources()
            .Where(s => s.IsEnabled && s.SearchMode != SearchMode.Disabled && (sourceIds is null || sourceIds.Contains(s.Id)))
            .ToList();
        if (sources.Count == 0) yield break;

        var channel = Channel.CreateUnbounded<LiveSearchBatch>();
        var searches = sources.Select(async source =>
        {
            var batch = await SearchSourceAsync(source.Id, query, page, cancellationToken).ConfigureAwait(false);
            await channel.Writer.WriteAsync(batch, cancellationToken).ConfigureAwait(false);
        }).ToList();
        _ = Task.WhenAll(searches).ContinueWith(t => channel.Writer.TryComplete(t.Exception), TaskScheduler.Default);

        await foreach (var batch in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return batch;
    }

    public async Task<LiveSearchBatch> SearchSourceAsync(Guid sourceId, string? query, int page, CancellationToken cancellationToken)
    {
        var source = repository.GetSource(sourceId);
        if (source is null) return new LiveSearchBatch(sourceId, "?", [], false, "Source not found.");
        var sourceName = SiteProfiles.NameFor(source);

        var search = SearchQuery.Parse(query);
        query = search.IsEmpty ? null : search.SiteText;
        page = Math.Max(1, page);
        var cacheKey = $"{sourceId:N}|{query}|{page}";
        if (_cache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.At < CacheLifetime) return cached.Batch;

        try
        {
            // A dedicated reader (readers\*.json) with a list for this kind of page wins over everything else.
            var reader = readers.Find(source);
            var readerUrl = reader?.Listing is not null ? SiteReaderParser.BuildUrl(reader, query, page) : null;
            var mode = ItchIo.IsItch(source) || readerUrl is not null || source.IsWebGame
                ? SearchMode.Custom
                : await ResolveModeAsync(source, cancellationToken).ConfigureAwait(false);
            var (games, hasMore) = mode switch
            {
                _ when source.IsWebGame => await QueryWebGameAsync(source, cancellationToken).ConfigureAwait(false),
                _ when readerUrl is not null => await QueryReaderAsync(reader!, source, readerUrl, page, cancellationToken).ConfigureAwait(false),
                _ when ItchIo.IsItch(source) => await QueryItchAsync(source, query, page, cancellationToken).ConfigureAwait(false),
                SearchMode.WordPressApi => await QueryWordPressPreferTitlesAsync(source, query, page, cancellationToken).ConfigureAwait(false),
                SearchMode.Custom when !string.IsNullOrWhiteSpace(source.SearchUrlTemplate) && (query is not null || !source.SearchUrlTemplate.Contains("{query}")) =>
                    await QueryHtmlAsync(source, BuildCustomUrl(source, query, page), page, cancellationToken).ConfigureAwait(false),
                _ => await QueryHtmlAsync(source, BuildSearchPageUrl(source, query, page), page, cancellationToken).ConfigureAwait(false),
            };

            // Save (stubs never overwrite fully loaded games), then read back what the store now shows.
            games = games
                .Where(g => !Uri.TryCreate(g.PageUrl, UriKind.Absolute, out var u) ||
                            UrlRules.PassesFilters(u, source.UrlIncludePattern, source.UrlExcludePattern)) // FAQ, digests …
                .GroupBy(g => g.Id).Select(g => g.First()).ToList(); // sites sometimes list a post twice
            var stored = repository.MergeGames(games).ToDictionary(g => g.Id);
            var entries = games
                .Select(g => index.Get(g.Id) ?? CatalogEntry.From(stored.TryGetValue(g.Id, out var saved) ? saved : g))
                .ToList();

            if (!search.IsEmpty)
            {
                // Drop noise, best first; the site's own order breaks ties.
                entries = entries
                    .Select((e, i) => (Entry: e, Score: index.Score(search, e), Index: i))
                    .Where(x => x.Score >= SearchScorer.MinimumScore)
                    .OrderByDescending(x => x.Score).ThenBy(x => x.Index)
                    .Select(x => x.Entry)
                    .ToList();
            }

            var batch = new LiveSearchBatch(source.Id, sourceName, entries, hasMore, null);
            _cache[cacheKey] = (DateTime.UtcNow, batch);
            if (_cache.Count > 300) TrimCache();
            return batch;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Live search on {Source} failed", sourceName);
            return new LiveSearchBatch(source.Id, sourceName, [], false, Describe(ex));
        }
    }

    // =============================================================================================
    // Mode detection
    // =============================================================================================

    private async Task<SearchMode> ResolveModeAsync(GameSource source, CancellationToken ct)
    {
        if (source.SearchMode != SearchMode.Auto) return source.SearchMode;
        if (source.DetectedSearchMode is { } known) return known;

        var detected = SearchMode.SiteSearchPage;
        try
        {
            var probe = new Uri(Origin(source), "/wp-json/wp/v2/posts?per_page=1&_fields=id");
            var (status, body, _, _) = await GetAsync(probe, ct).ConfigureAwait(false);
            if (status == HttpStatusCode.OK && body.TrimStart().StartsWith('[')) detected = SearchMode.WordPressApi;
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or JsonException)
        {
            // No API: use the site's search page.
        }

        var fresh = repository.GetSource(source.Id) ?? source;
        fresh.DetectedSearchMode = detected;
        repository.UpsertSource(fresh);
        logger.LogInformation("Live search for {Source}: {Mode}", source.DisplayName, detected);
        return detected;
    }

    // =============================================================================================
    // WordPress REST API
    // =============================================================================================

    /// <summary>Title search first (precise); full-text only when no title matched.</summary>
    private async Task<(List<GameMetadata> Games, bool HasMore)> QueryWordPressPreferTitlesAsync(GameSource source, string? query, int page,
        CancellationToken ct)
    {
        if (query is null) return await QueryWordPressApiAsync(source, null, page, titlesOnly: false, ct).ConfigureAwait(false);

        var key = $"{source.Id:N}|{query}";
        if (page > 1 && _fullTextQueries.ContainsKey(key)) // page 1 already fell back: keep paging the same search
            return await QueryWordPressApiAsync(source, query, page, titlesOnly: false, ct).ConfigureAwait(false);

        var byTitle = await QueryWordPressApiAsync(source, query, page, titlesOnly: true, ct).ConfigureAwait(false);
        if (byTitle.Games.Count > 0 || page > 1) return byTitle;

        _fullTextQueries[key] = 0;
        return await QueryWordPressApiAsync(source, query, page, titlesOnly: false, ct).ConfigureAwait(false);
    }

    /// <param name="titlesOnly">Search post titles only (WordPress 6.2+; older versions ignore the parameter).</param>
    private async Task<(List<GameMetadata> Games, bool HasMore)> QueryWordPressApiAsync(GameSource source, string? query, int page,
        bool titlesOnly, CancellationToken ct)
    {
        var url = $"/wp-json/wp/v2/posts?per_page={PageSize}&page={page}&_embed=wp:featuredmedia" +
                  (query is null ? string.Empty : "&search=" + Uri.EscapeDataString(query)) +
                  (query is not null && titlesOnly ? "&search_columns=post_title" : string.Empty);
        var (status, body, _, headers) = await GetAsync(new Uri(Origin(source), url), ct).ConfigureAwait(false);

        // WordPress answers 400 "rest_post_invalid_page_number" past the last page.
        if (status == HttpStatusCode.BadRequest && body.Contains("invalid_page_number", StringComparison.Ordinal)) return (new List<GameMetadata>(), false);
        if (status != HttpStatusCode.OK) throw new HttpRequestException($"The site's search API answered {(int)status}.", null, status);

        var totalPages = headers.TryGetValues("X-WP-TotalPages", out var values) && int.TryParse(values.FirstOrDefault(), out var tp) ? tp : 0;
        var games = new List<GameMetadata>();

        using var json = JsonDocument.Parse(body);
        foreach (var post in json.RootElement.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            var link = Str(post, "link");
            if (link is null || !Uri.TryCreate(link, UriKind.Absolute, out var linkUri)) continue;
            if (!UrlRules.PassesFilters(linkUri, source.UrlIncludePattern, source.UrlExcludePattern)) continue;

            var title = WebUtility.HtmlDecode(Rendered(post, "title") ?? string.Empty);
            var content = Rendered(post, "content") ?? string.Empty;
            var excerpt = Rendered(post, "excerpt") ?? string.Empty;
            var posted = ParseWpDate(Str(post, "date_gmt"));
            var featured = FeaturedImage(post);

            var html = $"<html><head><title>{WebUtility.HtmlEncode(title)}</title></head>" +
                       $"<body><article><h1>{WebUtility.HtmlEncode(title)}</h1>{(content.Length > 0 ? content : excerpt)}</article></body></html>";
            using var doc = await GamePageParser.ParseHtmlAsync(html, linkUri, ct).ConfigureAwait(false);
            var game = parser.ExtractGame(doc, linkUri, source, []);

            game.UploadedAtUtc = posted ?? game.UploadedAtUtc;
            game.CoverImageUrl = featured ?? game.CoverImageUrl;

            if (content.Length == 0)
            {
                game.DetailsLoaded = false;   // API hides content: load the page when opened
            }
            else if (game.DownloadLinks.Count == 0 && game.RepackSize is null && game.OriginalSize is null && game.Genres.Count == 0)
            {
                continue;                     // news / announcement post, not a game
            }
            games.Add(game);
        }

        return (games, totalPages > page);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Rendered(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? Str(v, "rendered") : null;

    private static string? FeaturedImage(JsonElement post)
    {
        if (!post.TryGetProperty("_embedded", out var embedded) ||
            !embedded.TryGetProperty("wp:featuredmedia", out var media) || media.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in media.EnumerateArray())
        {
            // Prefer a mid-size rendition for cards; fall back to the original.
            if (item.TryGetProperty("media_details", out var details) && details.TryGetProperty("sizes", out var sizes))
            {
                foreach (var size in new[] { "medium_large", "large", "full" })
                    if (sizes.TryGetProperty(size, out var s) && Str(s, "source_url") is { } sized) return sized;
            }
            if (Str(item, "source_url") is { } url) return url;
        }
        return null;
    }

    private static DateTime? ParseWpDate(string? value) =>
        DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var d)
            ? DateTime.SpecifyKind(d, DateTimeKind.Utc)
            : null;

    // =============================================================================================
    // HTML search / listing pages
    // =============================================================================================

    private async Task<(List<GameMetadata> Games, bool HasMore)> QueryHtmlAsync(GameSource source, Uri url, int page, CancellationToken ct)
    {
        var (status, body, finalUri, _) = await GetAsync(url, ct).ConfigureAwait(false);
        if (status == HttpStatusCode.NotFound && page > 1) return (new List<GameMetadata>(), false); // ran past the last page
        if (status != HttpStatusCode.OK) throw new HttpRequestException($"The site answered {(int)status}.", null, status);

        using var doc = await GamePageParser.ParseHtmlAsync(body, finalUri, ct).ConfigureAwait(false);
        var warnings = new List<string>();

        // A search with a single hit sometimes redirects straight to the game page.
        var itemLinks = parser.FindItemLinks(doc, finalUri, source.Profile, warnings);
        if (!parser.IsListingPage(doc, finalUri, source.Profile, itemLinks, warnings))
        {
            var single = parser.ExtractGame(doc, finalUri, source, warnings);
            var found = GamePageParser.IsGamePage(doc, single) ? new List<GameMetadata> { single } : new List<GameMetadata>();
            return (found, false);
        }

        var games = await parser.ExtractListingResultsAsync(doc, finalUri, source, warnings, ct).ConfigureAwait(false);
        var hasMore = parser.FindNextPageLink(doc, finalUri, source.Profile, warnings) is not null;
        return (games, hasMore);
    }

    /// <summary>A site with its own reader (see <see cref="SiteReader"/>): its browse or search page.</summary>
    private async Task<(List<GameMetadata> Games, bool HasMore)> QueryReaderAsync(SiteReader reader, GameSource source, Uri url, int page,
        CancellationToken ct)
    {
        var (status, body, finalUri, _) = await GetAsync(url, ct).ConfigureAwait(false);
        if (status == HttpStatusCode.NotFound && page > 1) return (new List<GameMetadata>(), false);
        if (status != HttpStatusCode.OK) throw new HttpRequestException($"{reader.Name} answered {(int)status}.", null, status);

        using var doc = await GamePageParser.ParseHtmlAsync(body, finalUri, ct).ConfigureAwait(false);
        return SiteReaderParser.ParseListing(reader, doc, finalUri, source);
    }

    /// <summary>A browser game's own site: the one game it is (see <see cref="WebGameSite"/>).</summary>
    private async Task<(List<GameMetadata> Games, bool HasMore)> QueryWebGameAsync(GameSource source, CancellationToken ct)
    {
        var (status, body, finalUri, _) = await GetAsync(new Uri(source.Url), ct).ConfigureAwait(false);
        if (status != HttpStatusCode.OK) throw new HttpRequestException($"The game's site answered {(int)status}.", null, status);
        using var doc = await GamePageParser.ParseHtmlAsync(body, finalUri, ct).ConfigureAwait(false);
        return (new List<GameMetadata> { WebGameSite.Build(doc, finalUri, source) }, false);
    }

    /// <summary>itch.io's browse and search pages (not WordPress; see <see cref="ItchIo"/>).</summary>
    private async Task<(List<GameMetadata> Games, bool HasMore)> QueryItchAsync(GameSource source, string? query, int page, CancellationToken ct)
    {
        var url = ItchIo.BuildUrl(source, query, page);
        var (status, body, finalUri, _) = await GetAsync(url, ct).ConfigureAwait(false);
        if (status == HttpStatusCode.NotFound && page > 1) return (new List<GameMetadata>(), false);
        if (status != HttpStatusCode.OK) throw new HttpRequestException($"itch.io answered {(int)status}.", null, status);

        using var doc = await GamePageParser.ParseHtmlAsync(body, finalUri, ct).ConfigureAwait(false);
        return ItchIo.ParseListing(doc, finalUri, source);
    }

    private static Uri BuildSearchPageUrl(GameSource source, string? query, int page)
    {
        if (query is null) return UrlRules.BuildPageUrl(new Uri(source.Url), page); // browse latest
        var path = page > 1 ? $"/page/{page}/" : "/";
        return new Uri(Origin(source), $"{path}?s={Uri.EscapeDataString(query)}");
    }

    private static Uri BuildCustomUrl(GameSource source, string? query, int page)
    {
        var url = source.SearchUrlTemplate!
            .Replace("{query}", Uri.EscapeDataString(query ?? string.Empty), StringComparison.OrdinalIgnoreCase)
            .Replace("{page}", page.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        return new Uri(new Uri(source.Url), url);
    }

    // =============================================================================================
    // Helpers
    // =============================================================================================

    private static Uri Origin(GameSource source) => new(new Uri(source.Url).GetLeftPart(UriPartial.Authority) + "/");

    private async Task<(HttpStatusCode Status, string Body, Uri FinalUri, System.Net.Http.Headers.HttpResponseHeaders Headers)> GetAsync(
        Uri url, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(HttpClientNames.Scraper);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", repository.GetSettings().UserAgent);
        request.Headers.Accept.ParseAdd(url.AbsolutePath.Contains("/wp-json/", StringComparison.Ordinal) ? "application/json" : "text/html");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return (response.StatusCode, body, response.RequestMessage?.RequestUri ?? url, response.Headers);
    }

    public void InvalidateCache()
    {
        _cache.Clear();
        _fullTextQueries.Clear();
    }

    private void TrimCache()
    {
        foreach (var key in _cache.Where(kv => DateTime.UtcNow - kv.Value.At > CacheLifetime).Select(kv => kv.Key).ToList())
            _cache.TryRemove(key, out _);
    }

    private static string Describe(Exception ex) => ex switch
    {
        WebChallenge.ChallengeException challenge => challenge.Message,
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden } => "The site refused the request (403).",
        HttpRequestException http => http.Message,
        TimeoutException => "The site took too long to answer.",
        _ => ex.Message,
    };
}
