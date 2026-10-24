using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Models;
using SeaRaider.Core.Scraping;

namespace SeaRaider.Core.Catalog;

/// <summary>
/// Lazy loading of full game metadata. Search and listing results are stored as stubs (title, link, cover); the
/// game's page is fetched and parsed the first time it's opened or installed, and again whenever the record was
/// produced by an older parser version (so existing catalogs get cleaned titles and info pills as you browse).
/// Media is cleaned of banners and, when a page has no screenshots or trailer, filled from Steam / IGDB
/// (<see cref="MediaScraperService"/>).
/// </summary>
public sealed class GameDetailsService(
    ILibraryRepository repository,
    IHttpClientFactory httpClientFactory,
    GamePageParser parser,
    MediaScraperService media,
    SiteReaderCatalog readers,
    RomInfoService romInfo,
    ILogger<GameDetailsService> logger) : IGameDetailsService
{
    private readonly ConcurrentDictionary<string, Task<GameMetadata>> _inFlight = new(StringComparer.Ordinal);

    public async Task<GameMetadata> GetAsync(string gameId, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var game = repository.GetGame(gameId) ?? throw new InvalidOperationException("This game is no longer in the catalog.");
        if (!forceRefresh && !game.NeedsDetails)
        {
            if (romInfo.Wants(game))
            {
                // A ROM read before its information came from the web: fill it in now.
                var romTask = _inFlight.GetOrAdd("rom:" + gameId, _ => EnrichRomAsync(game));
                return await romTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            if (game.MediaUpToDate) return game;
            // Full record from search results / an older version: only the media needs a look.
            var mediaTask = _inFlight.GetOrAdd("media:" + gameId, _ => EnrichMediaAsync(game));
            return await mediaTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        // One fetch per game, however many cards / buttons ask at once.
        var task = _inFlight.GetOrAdd(gameId, _ => FetchAsync(game));
        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && game.DetailsLoaded && game.DownloadLinks.Count > 0)
        {
            // Offline / site down: an older full record is still useful.
            logger.LogInformation(ex, "Using cached details for {Title}", game.Title);
            return game;
        }
    }

    private async Task<GameMetadata> FetchAsync(GameMetadata stub)
    {
        try
        {
            var source = repository.GetSource(stub.SourceId) ?? new GameSource { Id = stub.SourceId, Url = stub.PageUrl };
            var client = httpClientFactory.CreateClient(HttpClientNames.Scraper);

            using var request = new HttpRequestMessage(HttpMethod.Get, stub.PageUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", repository.GetSettings().UserAgent);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var finalUri = response.RequestMessage?.RequestUri ?? new Uri(stub.PageUrl);
            using var doc = await GamePageParser.ParseHtmlAsync(html, finalUri, CancellationToken.None).ConfigureAwait(false);

            var full = source.IsWebGame
                ? WebGameSite.Build(doc, finalUri, source, stub.PageUrl)
                : parser.ExtractGame(doc, new Uri(stub.PageUrl), source, []);

            // No downloads, and the page says it plays in the browser: it's a browser game (the source just wasn't marked).
            if (!source.IsWebGame && full.DownloadLinks.Count == 0 && !ItchIo.IsItchHost(finalUri.Host) && WebGameSite.LooksLikeBrowserGame(doc))
            {
                logger.LogInformation("{Url} plays in the browser; treating {Source} as a browser game", stub.PageUrl, source.Url);
                if (repository.GetSource(source.Id) is { } saved && new Uri(saved.Url).Host == finalUri.Host)
                {
                    saved.IsWebGame = true;
                    repository.UpsertSource(saved);
                }
                source.IsWebGame = true;
                full = WebGameSite.Build(doc, finalUri, source, stub.PageUrl);
            }
            full.Id = stub.Id;                     // keep the id even if the page redirected
            full.SourceId = stub.SourceId;
            full.UploadedAtUtc ??= stub.UploadedAtUtc;
            full.CoverImageUrl ??= stub.CoverImageUrl;
            if (string.IsNullOrWhiteSpace(full.Title)) full.Title = stub.Title;

            if (source.IsWebGame)
            {
                // A browser game's own site: its title, picture and description are all there is.
            }
            else if (ItchIo.IsItchHost(finalUri.Host))
            {
                // itch.io's own page layout: description, screenshots, info table, file list (no Steam / IGDB lookup).
                full.Title = stub.Title.Length > 0 ? stub.Title : full.Title;
                ItchIo.EnrichFromGamePage(doc, new Uri(stub.PageUrl), full);
            }
            else if ((readers.Find(finalUri) ?? readers.Find(new Uri(stub.PageUrl))) is { Page: not null } reader)
            {
                // A dedicated reader (readers\*.json) for this site: its selectors win over the generic guesses.
                var notes = SiteReaderParser.EnrichFromGamePage(reader, doc, finalUri, full);
                if (await SiteReaderParser.FollowDownloadUrlAsync(client, repository.GetSettings().UserAgent, reader, full, finalUri,
                        CancellationToken.None).ConfigureAwait(false) is { } followed)
                    notes.Add(followed);
                if (notes.Count > 0) logger.LogInformation("Reader {Reader} on {Url}: {Notes}", reader.Name, finalUri, string.Join("; ", notes));
                // Steam's details, pictures and trailer too (a reader made by the teacher reads the site; Steam fills the rest).
                if (!reader.UseOwnPictures || repository.GetSettings().PreferSteamInfo) await TryEnrichAsync(full).ConfigureAwait(false);
            }
            else
            {
                await TryEnrichAsync(full).ConfigureAwait(false);
            }
            if (romInfo.Wants(full)) await TryEnrichRomAsync(full).ConfigureAwait(false);
            repository.UpsertGames([full]);
            return full;
        }
        finally
        {
            _inFlight.TryRemove(stub.Id, out _);
        }
    }

    private async Task<GameMetadata> EnrichRomAsync(GameMetadata game)
    {
        try
        {
            await TryEnrichRomAsync(game).ConfigureAwait(false);
            repository.UpsertGames([game]);
            return game;
        }
        finally
        {
            _inFlight.TryRemove("rom:" + game.Id, out _);
        }
    }

    /// <summary>ROM information never fails a page load.</summary>
    private async Task TryEnrichRomAsync(GameMetadata game)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var notes = await romInfo.EnrichAsync(game, timeout.Token).ConfigureAwait(false);
            logger.LogInformation("ROM info for {Title}: {Notes}", game.Title, string.Join("; ", notes));
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "ROM info lookup for {Title} failed", game.Title);
            game.RomInfoVersion = RomInfoService.CurrentVersion; // don't retry on every open
        }
    }

    private async Task<GameMetadata> EnrichMediaAsync(GameMetadata game)
    {
        try
        {
            await TryEnrichAsync(game).ConfigureAwait(false);
            repository.UpsertGames([game]);
            return game;
        }
        finally
        {
            _inFlight.TryRemove("media:" + game.Id, out _);
        }
    }

    /// <summary>Media problems never fail a page load.</summary>
    private async Task TryEnrichAsync(GameMetadata game)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            // Steam's clean details over a site's messy ones (never for console games: those come from RomInfoService).
            if (repository.GetSettings().PreferSteamInfo && RomInfoService.SystemOf(game) is null)
                await media.ApplyStoreInfoAsync(game, timeout.Token).ConfigureAwait(false);
            await media.EnrichAsync(game, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "Media lookup for {Title} failed", game.Title);
            game.MediaChecked = true; // don't retry on every open
            game.MediaVersion = GameMetadata.CurrentMediaVersion;
        }
    }
}
