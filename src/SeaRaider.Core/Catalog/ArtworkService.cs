using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Models;
using SeaRaider.Core.Scraping;

namespace SeaRaider.Core.Catalog;

/// <summary>
/// Finds sharp pictures for every game:
/// <list type="bullet">
///   <item><b>Card art</b> for games whose page has no cover: Steam's wide header, else the page's own cover or a
///         screenshot.</item>
///   <item><b>Hero art</b> for the featured banner, the game page and the window backdrop, which are 1600+ pixels wide.
///         A repack site's cover is a few hundred pixels and turns to blocks when stretched. Candidates, best first:
///         Steam's library hero, Steam screenshots (1920×1080), the page's screenshots at full size (riotpixels / WordPress /
///         imgur thumbnails turned into the original), the official YouTube trailer's 1280×720 frame, Steam's page
///         background, the page's cover. Each is <b>measured</b> (first 256 KB only), and the first landscape picture at
///         least 1600 px wide wins, else the largest one.</item>
/// </list>
/// Results are stored per game (<see cref="ArtworkRecord"/>): one lookup per game. Misses are retried after a day, and a
/// lookup that failed because a site was unreachable isn't stored at all. At most two lookups run at a time.
/// </summary>
public sealed class ArtworkService(IHttpClientFactory httpClientFactory, ILibraryRepository repository, IGameDetailsService details,
    YouTubeSearch youTube, WallpaperSearch wallpapers, ILogger<ArtworkService> logger)
{
    /// <summary>2: measured hero candidates (v0.9). Version-1 records are looked up again.</summary>
    internal const int RulesVersion = 4; // 3: HD wallpaper search (v0.10). 4: demos looked up as their game; wallpapers must name it

    private const int SharpWidth = 1600;
    private static readonly TimeSpan RetryMissesAfter = TimeSpan.FromDays(1);
    private static readonly string[] NotTheGame =
        ["soundtrack", "ost", "dlc", "demo", "companion", "artbook", "art book", "season pass", "bundle", "upgrade", "expansion",
         "pack", "playtest", "dedicated server", "editor", "sdk", "wallpaper", "costume", "skin"];

    private readonly SemaphoreSlim _gate = new(2, 2);
    private readonly ConcurrentDictionary<string, Task<ArtworkRecord?>> _inFlight = new(StringComparer.Ordinal);

    /// <summary>Pictures for <paramref name="title"/>; <paramref name="gameId"/> lets it use the game's own page too.</summary>
    public Task<ArtworkRecord?> GetAsync(string title, string? gameId, CancellationToken cancellationToken = default)
    {
        var key = TitleNormalizer.DedupKey(title);
        if (key.Length == 0) return Task.FromResult<ArtworkRecord?>(null);

        if (repository.GetArtwork(key) is { } cached && cached.RulesVersion >= RulesVersion &&
            (cached.Found || DateTime.UtcNow - cached.CheckedAtUtc < RetryMissesAfter))
            return Task.FromResult<ArtworkRecord?>(cached.Found ? cached : null);

        var task = _inFlight.GetOrAdd(key, _ => LookupAsync(key, title, gameId));
        return task.WaitAsync(cancellationToken);
    }

    private sealed class Candidates
    {
        public List<string> Headers { get; } = [];
        public List<(string Url, int Rank)> Heroes { get; } = [];
        public int? SteamAppId { get; set; }
        public bool Reliable { get; set; } = true; // false when a source couldn't be reached
        public string? Source { get; set; }
    }

    private async Task<ArtworkRecord?> LookupAsync(string key, string title, string? gameId)
    {
        // A demo / playtest has the full game's pictures (and Steam lists the game, not "… Demo").
        title = TitleNormalizer.WithoutDemo(title);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var ct = timeout.Token;
            var found = new Candidates();
            var client = httpClientFactory.CreateClient(HttpClientNames.Metadata);

            await AddSteamAsync(found, client, title, ct).ConfigureAwait(false);
            if (found.SteamAppId is null) await AddGamePageAsync(found, gameId, ct).ConfigureAwait(false);
            var (hero, size) = await PickHeroAsync(client, found.Heroes, ct).ConfigureAwait(false);

            // Nothing sharp yet (not on Steam, small pictures on the page): HD wallpapers and the trailer's frame.
            if (!IsSharp(size))
            {
                var before = found.Heroes.Count;
                await AddWallpapersAsync(found, title, ct).ConfigureAwait(false);
                await AddYouTubeAsync(found, title, ct).ConfigureAwait(false);
                var (more, moreSize) = await PickHeroAsync(client, found.Heroes.Skip(before).ToList(), ct).ConfigureAwait(false);
                if (more is not null && Score(moreSize) > Score(size))
                {
                    hero = more;
                    size = moreSize;
                }
            }
            var header = found.Headers.FirstOrDefault() ?? hero;
            var earlier = repository.GetArtwork(key); // its release date (ReleaseDateService) stays
            var record = new ArtworkRecord
            {
                ReleaseDateUtc = earlier?.ReleaseDateUtc,
                ReleaseDateText = earlier?.ReleaseDateText,
                ReleaseCheckedAtUtc = earlier?.ReleaseCheckedAtUtc,
                Key = key,
                SteamAppId = found.SteamAppId,
                HeaderUrl = header,
                HeroUrl = hero ?? header,
                HeroWidth = size?.Width ?? 0,
                HeroHeight = size?.Height ?? 0,
                Source = found.Source,
                Found = header is not null || hero is not null,
                RulesVersion = RulesVersion,
                CheckedAtUtc = DateTime.UtcNow,
            };

            // Don't remember "nothing found" when a site was simply unreachable.
            if (record.Found || found.Reliable) repository.UpsertArtwork(record);
            return record.Found ? record : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
        {
            logger.LogDebug(ex, "Artwork lookup failed for {Title}", title);
            return null;
        }
        finally
        {
            _gate.Release();
            _inFlight.TryRemove(key, out _);
        }
    }

    private async Task AddSteamAsync(Candidates found, HttpClient client, string title, CancellationToken ct)
    {
        try
        {
            var term = Uri.EscapeDataString(TitleNormalizer.NormalizeForSearch(title));
            using var search = await client.GetFromJsonAsync<JsonDocument>(
                $"https://store.steampowered.com/api/storesearch/?term={term}&l=english&cc=US", ct).ConfigureAwait(false);

            int? appId = null;
            if (search?.RootElement.TryGetProperty("items", out var items) == true && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (name is null || !item.TryGetProperty("id", out var id) || !id.TryGetInt32(out var value)) continue;
                    if (!IsMatch(title, name)) continue;
                    appId = value;
                    break;
                }
            }
            if (appId is not { } app) return;

            using var json = await client.GetFromJsonAsync<JsonDocument>(
                $"https://store.steampowered.com/api/appdetails?appids={app}&filters=basic,screenshots&l=english", ct).ConfigureAwait(false);
            if (json is null || !json.RootElement.TryGetProperty(app.ToString(), out var root) || !root.TryGetProperty("data", out var data))
                return;

            found.SteamAppId = app;
            found.Source = "Steam";
            if (Str(data, "header_image") is { } header) found.Headers.Add(header);
            found.Heroes.Add(($"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{app}/library_hero.jpg", 0));
            if (data.TryGetProperty("screenshots", out var shots) && shots.ValueKind == JsonValueKind.Array)
                foreach (var shot in shots.EnumerateArray().Take(3))
                    if (Str(shot, "path_full") is { } full) found.Heroes.Add((full, 1));
            if ((Str(data, "background_raw") ?? Str(data, "background")) is { } background) found.Heroes.Add((background, 4));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            found.Reliable = false;
            logger.LogDebug(ex, "Steam artwork lookup failed for {Title}", title);
        }
    }

    /// <summary>The game page's own pictures, at full size.</summary>
    private async Task AddGamePageAsync(Candidates found, string? gameId, CancellationToken ct)
    {
        if (gameId is null) return;
        try
        {
            var game = await details.GetAsync(gameId, cancellationToken: ct).ConfigureAwait(false);
            if (!game.DetailsLoaded) found.Reliable = false; // the page couldn't be read this time

            if (!string.IsNullOrEmpty(game.CoverImageUrl))
            {
                found.Headers.Add(ImageQuality.Upgrade(game.CoverImageUrl));
                foreach (var url in ImageQuality.Candidates(game.CoverImageUrl)) found.Heroes.Add((url, 5));
            }
            foreach (var shot in MediaScraperService.CleanScreenshots(game.ScreenshotUrls).Where(u => !u.Contains("steamstatic")).Take(4))
            {
                foreach (var url in ImageQuality.Candidates(shot)) found.Heroes.Add((url, 2));
                if (found.Headers.Count == 0) found.Headers.Add(ImageQuality.Upgrade(shot));
            }
            found.Source ??= "Game page";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            found.Reliable = false;
            logger.LogDebug(ex, "Game page artwork lookup failed for {Id}", gameId);
        }
    }

    /// <summary>The official trailer's 1280×720 frame: sharp art for games Steam doesn't sell.</summary>
    private async Task AddYouTubeAsync(Candidates found, string title, CancellationToken ct)
    {
        if (found.Heroes.Any(h => h.Rank <= 1) || !repository.GetSettings().UseYouTubeTrailers) return; // Steam has the game
        try
        {
            if (await youTube.FindTrailerAsync(title, ct).ConfigureAwait(false) is not { } video) return;
            found.Heroes.Add((video.MaxResThumbnail, 4));
            if (found.Headers.Count == 0) found.Headers.Add(video.MaxResThumbnail);
            found.Source ??= "YouTube";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "YouTube artwork lookup failed for {Title}", title);
        }
    }

    /// <summary>Wallpaper-sized key art from a web image search (1920 px and up, wide): for games Steam doesn't sell.</summary>
    private async Task AddWallpapersAsync(Candidates found, string title, CancellationToken ct)
    {
        if (!repository.GetSettings().UseWebArtwork) return;
        try
        {
            foreach (var url in await wallpapers.FindAsync(title, ct).ConfigureAwait(false)) found.Heroes.Add((url, 3));
            if (found.Heroes.Any(h => h.Rank == 3)) found.Source ??= "Web";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Wallpaper search failed for {Title}", title);
        }
    }

    private static bool IsSharp((int Width, int Height)? size) =>
        size is { } s && s.Width >= SharpWidth && s.Width >= s.Height * 1.25;

    private static long Score((int Width, int Height)? size) =>
        size is { } s ? (s.Width >= s.Height * 1.25 ? 2L : 1L) * s.Width * s.Height : -1;

    /// <summary>The first landscape picture at least <see cref="SharpWidth"/> wide (by preference), else the largest.</summary>
    private static async Task<(string? Url, (int Width, int Height)? Size)> PickHeroAsync(HttpClient client,
        List<(string Url, int Rank)> candidates, CancellationToken ct)
    {
        string? best = null;
        (int Width, int Height)? bestSize = null;
        foreach (var url in candidates.OrderBy(c => c.Rank).Select(c => c.Url).Distinct(StringComparer.OrdinalIgnoreCase).Take(12))
        {
            if (await ImageQuality.ProbeAsync(client, url, ct).ConfigureAwait(false) is not { } size) continue;
            var landscape = size.Width >= size.Height * 1.25;
            if (landscape && size.Width >= SharpWidth) return (url, size);

            if (Score(size) > Score(bestSize))
            {
                best = url;
                bestSize = size;
            }
        }
        return (best, bestSize);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    /// <summary>
    /// Is the Steam result <paramref name="candidate"/> this game? The same title, or the title with a publisher in front
    /// ("EA SPORTS College Football 27" for "College Football 27") — never a soundtrack, DLC, demo or companion app.
    /// </summary>
    internal static bool IsMatch(string title, string candidate)
    {
        var lower = candidate.ToLowerInvariant();
        if (NotTheGame.Any(word => System.Text.RegularExpressions.Regex.IsMatch(lower, $@"\b{System.Text.RegularExpressions.Regex.Escape(word)}\b")) &&
            !NotTheGame.Any(word => title.Contains(word, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (MediaScraperService.IsSameGame(title, candidate)) return true;

        var wanted = TitleNormalizer.NormalizeForSearch(title);
        var found = TitleNormalizer.NormalizeForSearch(candidate);
        if (wanted.Length < 4 || !found.EndsWith(" " + wanted, StringComparison.Ordinal)) return false;
        var prefixWords = found[..^wanted.Length].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return prefixWords is > 0 and <= 3;
    }
}
