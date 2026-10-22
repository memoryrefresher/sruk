using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Catalog;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Scraping;

/// <summary>Screenshots and trailers found for a game by an outside catalogue.</summary>
/// <param name="Provider">"Steam", "IGDB".</param>
/// <param name="Screenshots">Full-size gameplay screenshots.</param>
/// <param name="Trailers">Trailer URLs (direct video, or YouTube watch pages).</param>
/// <param name="TrailerThumbnails">Thumbnail per trailer (same order; may contain nulls).</param>
/// <param name="StorePageUrl">The game's store page (where trailers can always be watched).</param>
public sealed record GameMedia(string Provider, IReadOnlyList<string> Screenshots, IReadOnlyList<string> Trailers,
    IReadOnlyList<string?> TrailerThumbnails, string? StorePageUrl = null);

/// <summary>A public games catalogue that can supply media by title.</summary>
public interface IGameMediaProvider
{
    string Name { get; }

    /// <summary>False when it needs keys that aren't set.</summary>
    bool IsAvailable { get; }

    Task<GameMedia?> FindAsync(string title, CancellationToken cancellationToken);
}

/// <summary>
/// Clean media for store pages.
/// <list type="bullet">
///   <item><b>Filter</b> (<see cref="IsJunk"/>): release pages are full of images that aren't gameplay — torrent
///         seeder/peer stat banners, file-host buttons, site logos, tracking pixels, donation badges. Their URL / alt text
///         (stats, seeders, peers, banner, logo, 1x1 …) or their shape (thin banners, tiny images) gives them away.</item>
///   <item><b>Fallback</b> (<see cref="EnrichAsync"/>): when a page has no clean screenshots and no trailer, the title is
///         looked up on the Steam store (no key needed) and, if keys are set, IGDB. Only a confident title match is
///         used, so a similarly named game's screenshots never show up.</item>
/// </list>
/// </summary>
public sealed partial class MediaScraperService(IEnumerable<IGameMediaProvider> providers, ILogger<MediaScraperService> logger)
{
    /// <summary>A page needs outside media when fewer clean screenshots than this were found and there's no trailer.</summary>
    private const int MinimumScreenshots = 2;

    [GeneratedRegex(
        @"(?<![a-z])(?:stats?|seed(?:s|ers?)?|peers?|leech(?:ers?)?|trackers?|torrent-?stats|banners?|logos?|filehost(?:s|er)?|" +
        @"buttons?|badges?|counters?|pixel|spacer|1x1|blank|donate|paypal|patreon|ko-?fi|discord|telegram|watermark|" +
        @"avatar|gravatar|emoji|smiley|icon|sprite|loader|spinner|placeholder|ads?|advert|qr)(?![a-z])",
        RegexOptions.IgnoreCase)]
    private static partial Regex JunkWordsRegex();

    /// <summary>
    /// True for images that aren't game art: matched by URL, alt text or class, or by an implausible shape when the page
    /// gives dimensions (screenshots are landscape, roughly 4:3 to 21:9, and not tiny).
    /// </summary>
    public static bool IsJunk(string url, string? alt = null, string? cssClass = null, int? width = null, int? height = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return true;
        var lower = url.ToLowerInvariant();
        if (lower.StartsWith("data:", StringComparison.Ordinal) || lower.Contains(".svg") || lower.Contains(".gif")) return true;

        // Words are matched in the path, alt and class (not the domain alone: "steamstatic.com" is fine).
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host.Replace('.', '-') + uri.AbsolutePath : url;
        if (JunkWordsRegex().IsMatch(path.Replace("steamstatic", string.Empty, StringComparison.OrdinalIgnoreCase)) ||
            (alt is not null && JunkWordsRegex().IsMatch(alt)) ||
            (cssClass is not null && JunkWordsRegex().IsMatch(cssClass)))
            return true;

        if (width is { } w && height is { } h && w > 0 && h > 0)
        {
            if (w < 200 || h < 110) return true;      // thumbnails of buttons, counters
            var ratio = (double)w / h;
            if (ratio > 2.6 || ratio < 1.0) return true; // banners and portrait art (covers are handled separately)
        }
        return false;
    }

    /// <summary>The page's screenshots without junk (also cleans records saved by older versions).</summary>
    public static List<string> CleanScreenshots(IEnumerable<string> urls) =>
        urls.Where(u => !IsJunk(u)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Removes junk from <paramref name="game"/>'s media and, when too little is left, fills screenshots and trailers from
    /// the outside catalogues. Returns true when anything changed.
    /// </summary>
    public async Task<bool> EnrichAsync(GameMetadata game, CancellationToken cancellationToken)
    {
        // Filled by older rules: Steam's 7-second silent previews, YouTube links picked up from a site's sidebar (music
        // videos). Drop them and look again.
        var outdated = game.MediaVersion < GameMetadata.CurrentMediaVersion;
        if (outdated)
        {
            if (game.MediaSource is not null) game.ScreenshotUrls = game.ScreenshotUrls.Where(u => !IsProviderUrl(u)).ToList();
            game.TrailerUrls = game.TrailerUrls.Where(u => !IsProviderUrl(u) && !IsYouTube(u)).ToList();
            game.TrailerThumbnailUrls = [];
            game.MediaSource = null;
        }
        game.MediaVersion = GameMetadata.CurrentMediaVersion;

        var cleaned = CleanScreenshots(game.ScreenshotUrls);
        var changed = outdated || cleaned.Count != game.ScreenshotUrls.Count;
        game.ScreenshotUrls = cleaned;
        game.MediaChecked = true;

        // Trailers always come from Steam / IGDB when they have the game (matched by title, so they're the right ones);
        // screenshots only when the page has too few.
        var needScreenshots = cleaned.Count < MinimumScreenshots;
        var haveTrustedTrailers = game.MediaSource is not null && game.TrailerUrls.Count > 0;
        if (!needScreenshots && haveTrustedTrailers) return changed;

        foreach (var provider in providers.Where(p => p.IsAvailable))
        {
            try
            {
                if (await provider.FindAsync(game.Title, cancellationToken).ConfigureAwait(false) is not { } media) continue;

                var used = false;
                if (needScreenshots && media.Screenshots.Count > 0)
                {
                    game.ScreenshotUrls = cleaned.Concat(media.Screenshots).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
                    needScreenshots = false;
                    used = true;
                }
                if (media.Trailers.Count > 0 && !haveTrustedTrailers)
                {
                    game.TrailerUrls = media.Trailers.Take(6).ToList();
                    game.TrailerThumbnailUrls = media.TrailerThumbnails.Take(game.TrailerUrls.Count).ToList();
                    haveTrustedTrailers = true;
                    used = true;
                }
                if (!used) continue;

                game.MediaSource ??= media.Provider;
                game.StorePageUrl ??= media.StorePageUrl;
                changed = true;
                logger.LogInformation("Media for {Title} from {Provider}: {Shots} screenshots, {Trailers} trailers", game.Title,
                    media.Provider, media.Screenshots.Count, media.Trailers.Count);
                if (haveTrustedTrailers && !needScreenshots) break;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation(ex, "{Provider} media lookup failed for {Title}", provider.Name, game.Title);
            }
        }
        return changed;
    }

    private static bool IsYouTube(string url) =>
        url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) || url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("youtube-nocookie.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>Media that came from Steam / IGDB rather than the game page.</summary>
    private static bool IsProviderUrl(string url) =>
        url.Contains("steamstatic.com", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("steampowered.com", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("images.igdb.com", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("youtube.com/watch", StringComparison.OrdinalIgnoreCase);

    /// <summary>Is <paramref name="candidate"/> the same game as <paramref name="title"/>? Exact key, or an exact-ish title score.</summary>
    internal static bool IsSameGame(string title, string candidate) =>
        TitleNormalizer.DedupKey(title) == TitleNormalizer.DedupKey(candidate) ||
        SearchScorer.Score(SearchQuery.Parse(TitleNormalizer.NormalizeForSearch(title)), candidate) >= 0.95;
}

/// <summary>
/// Steam store (no key): <c>/api/storesearch</c> to find the app, <c>/api/appdetails</c> for screenshots and movies.
/// Results are cached for the session.
/// </summary>
public sealed class SteamStoreMediaProvider(IHttpClientFactory httpClientFactory, ILibraryRepository repository) : IGameMediaProvider
{
    private readonly ConcurrentDictionary<string, GameMedia?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public string Name => "Steam";
    public bool IsAvailable => repository.GetSettings().UseSteamMedia;

    public async Task<GameMedia?> FindAsync(string title, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(title, out var cached)) return cached;
        var client = httpClientFactory.CreateClient(HttpClientNames.Metadata);

        var term = Uri.EscapeDataString(TitleNormalizer.NormalizeForSearch(title));
        using var search = await client.GetFromJsonAsync<JsonDocument>(
            $"https://store.steampowered.com/api/storesearch/?term={term}&l=english&cc=US", cancellationToken).ConfigureAwait(false);

        int? appId = null;
        if (search?.RootElement.TryGetProperty("items", out var items) == true && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (name is null || !item.TryGetProperty("id", out var id) || !id.TryGetInt32(out var value)) continue;
                if (!ArtworkService.IsMatch(title, name)) continue;
                appId = value;
                break;
            }
        }
        if (appId is null) return _cache[title] = null;

        using var details = await client.GetFromJsonAsync<JsonDocument>(
            $"https://store.steampowered.com/api/appdetails?appids={appId}&l=english", cancellationToken).ConfigureAwait(false);
        if (details is null || !details.RootElement.TryGetProperty(appId.Value.ToString(), out var app) ||
            !app.TryGetProperty("data", out var data))
            return _cache[title] = null;

        var screenshots = new List<string>();
        if (data.TryGetProperty("screenshots", out var shots) && shots.ValueKind == JsonValueKind.Array)
        {
            foreach (var shot in shots.EnumerateArray())
                if (shot.TryGetProperty("path_full", out var full) && full.GetString() is { } url) screenshots.Add(url);
        }

        var trailers = new List<string>();
        var thumbs = new List<string?>();
        if (data.TryGetProperty("movies", out var movies) && movies.ValueKind == JsonValueKind.Array)
        {
            var candidates = movies.EnumerateArray()
                .Select((movie, index) => new SteamMovie(
                    Str(movie, "name") ?? string.Empty,
                    movie.TryGetProperty("highlight", out var h) && h.ValueKind == JsonValueKind.True,
                    index,
                    // Since 2025 Steam serves trailers as HLS / DASH streams; older responses listed mp4 / webm files.
                    // "microtrailer" files are the store's silent 6–7 second previews, never a trailer.
                    new[] { Str(movie, "hls_h264"), Str(movie, "dash_h264"), Rendition(movie, "mp4", "max"), Rendition(movie, "webm", "max") }
                        .FirstOrDefault(u => u is not null && !u.Contains("microtrailer", StringComparison.OrdinalIgnoreCase)),
                    Str(movie, "thumbnail")))
                .Where(m => m.Url is not null);

            foreach (var movie in SteamMovie.InTrailerOrder(candidates).Take(4))
            {
                trailers.Add(movie.Url!);
                thumbs.Add(movie.Thumbnail);
            }
        }

        return _cache[title] = new GameMedia("Steam", screenshots.Take(12).ToList(), trailers, thumbs,
            $"https://store.steampowered.com/app/{appId}/");

        static string? Rendition(JsonElement movie, string format, string quality) =>
            movie.TryGetProperty(format, out var f) && f.ValueKind == JsonValueKind.Object && f.TryGetProperty(quality, out var q) ? q.GetString() : null;

        static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}

/// <summary>
/// IGDB (Twitch developer keys in Settings): screenshots at 1080p and YouTube trailers.
/// <see href="https://api-docs.igdb.com/"/>
/// </summary>
public sealed class IgdbMediaProvider(IHttpClientFactory httpClientFactory, ILibraryRepository repository) : IGameMediaProvider
{
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _token;
    private DateTime _tokenExpiresUtc;

    public string Name => "IGDB";

    public bool IsAvailable =>
        repository.GetSettings() is { IgdbClientId.Length: > 0, IgdbClientSecret.Length: > 0 };

    public async Task<GameMedia?> FindAsync(string title, CancellationToken cancellationToken)
    {
        var settings = repository.GetSettings();
        var token = await GetTokenAsync(settings.IgdbClientId!, settings.IgdbClientSecret!, cancellationToken).ConfigureAwait(false);
        var client = httpClientFactory.CreateClient(HttpClientNames.Metadata);

        var searchText = TitleNormalizer.NormalizeForSearch(title).Replace("\"", string.Empty);
        var query = $"search \"{searchText}\"; fields name,screenshots.image_id,videos.video_id; limit 10;";
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.igdb.com/v4/games") { Content = new StringContent(query) };
        request.Headers.TryAddWithoutValidation("Client-ID", settings.IgdbClientId);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        foreach (var game in json.RootElement.EnumerateArray())
        {
            var name = game.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (name is null || !MediaScraperService.IsSameGame(title, name)) continue;

            var screenshots = Ids(game, "screenshots", "image_id")
                .Select(id => $"https://images.igdb.com/igdb/image/upload/t_1080p/{id}.jpg").Take(12).ToList();
            var videoIds = Ids(game, "videos", "video_id").Take(3).ToList();
            return new GameMedia("IGDB", screenshots,
                videoIds.Select(id => $"https://www.youtube.com/watch?v={id}").ToList(),
                videoIds.Select(id => (string?)$"https://img.youtube.com/vi/{id}/hqdefault.jpg").ToList());
        }
        return null;

        static IEnumerable<string> Ids(JsonElement game, string list, string field) =>
            game.TryGetProperty(list, out var items) && items.ValueKind == JsonValueKind.Array
                ? items.EnumerateArray().Select(i => i.TryGetProperty(field, out var v) ? v.GetString() : null).OfType<string>()
                : Enumerable.Empty<string>();
    }

    private async Task<string> GetTokenAsync(string clientId, string clientSecret, CancellationToken ct)
    {
        await _tokenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_token is not null && DateTime.UtcNow < _tokenExpiresUtc) return _token;

            var client = httpClientFactory.CreateClient(HttpClientNames.Metadata);
            var url = $"https://id.twitch.tv/oauth2/token?client_id={Uri.EscapeDataString(clientId)}" +
                      $"&client_secret={Uri.EscapeDataString(clientSecret)}&grant_type=client_credentials";
            using var response = await client.PostAsync(url, null, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
                cancellationToken: ct).ConfigureAwait(false);

            _token = json.RootElement.GetProperty("access_token").GetString()
                     ?? throw new HttpRequestException("IGDB didn't return an access token.");
            var seconds = json.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 3600;
            _tokenExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, seconds - 300));
            return _token;
        }
        finally
        {
            _tokenGate.Release();
        }
    }
}

/// <summary>A movie from Steam's app details.</summary>
internal sealed record SteamMovie(string Name, bool Highlight, int Index, string? Url, string? Thumbnail)
{
    /// <summary>
    /// Real trailers first: launch / release / announcement trailers, then gameplay showcases and cinematics, then the
    /// rest in Steam's order; spoiler-flagged videos and short "clips" last.
    /// </summary>
    public static IEnumerable<SteamMovie> InTrailerOrder(IEnumerable<SteamMovie> movies) =>
        movies.OrderBy(Score).ThenBy(m => m.Index);

    private static int Score(SteamMovie movie)
    {
        var name = movie.Name.ToLowerInvariant();
        var score = movie.Highlight ? 0 : 10;
        if (name.Contains("spoiler") || name.Contains("clip") || name.Contains("gif") || name.Contains("loop")) score += 40;
        if (name.Contains("trailer")) score -= 5;
        if (name.Contains("launch") || name.Contains("release") || name.Contains("announce")) score -= 2;
        if (name.Contains("gameplay") || name.Contains("overview") || name.Contains("showcase") || name.Contains("cinematic")) score -= 1;
        return score;
    }
}
