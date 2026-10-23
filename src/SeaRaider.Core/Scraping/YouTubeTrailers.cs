using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Catalog;

namespace SeaRaider.Core.Scraping;

/// <summary>A YouTube video found by search.</summary>
public sealed record YouTubeVideo(string Id, string Title)
{
    public string WatchUrl => $"https://www.youtube.com/watch?v={Id}";

    /// <summary>1280×720 frame (YouTube makes one for every HD upload; "hqdefault" is the 480×360 fallback).</summary>
    public string MaxResThumbnail => $"https://i.ytimg.com/vi/{Id}/maxresdefault.jpg";
}

/// <summary>
/// Finds a game's official trailer on YouTube without an API key (the public search page). Used for games Steam
/// doesn't have (console ports, EA / Ubisoft titles): their trailer, and its 1280×720 frame as a sharp banner image.
/// A result only counts when its title has every word of the game's title and says "trailer" — never reactions,
/// reviews, soundtracks or fan edits.
/// </summary>
public sealed partial class YouTubeSearch(IHttpClientFactory httpClientFactory, ILibraryRepository repository)
{
    private static readonly string[] Rejected =
        ["reaction", "review", "fan made", "fanmade", "concept", "soundtrack", "ost", "music", "remix", "parody", "explained",
         "breakdown", "analysis", "leak", "mod ", "mods", "comparison", "vs"];

    private readonly ConcurrentDictionary<string, YouTubeVideo?> _cache = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"""videoRenderer"":\{""videoId"":""(?<id>[\w-]{11})"".{0,4000}?""title"":\{""runs"":\[\{""text"":""(?<title>(?:[^""\\]|\\.)*)""",
        RegexOptions.Singleline)]
    private static partial Regex VideoRegex();

    public async Task<YouTubeVideo?> FindTrailerAsync(string gameTitle, CancellationToken cancellationToken)
    {
        var wanted = TitleNormalizer.NormalizeForSearch(gameTitle);
        if (wanted.Length < 2) return null;
        if (_cache.TryGetValue(wanted, out var cached)) return cached;

        var client = httpClientFactory.CreateClient(HttpClientNames.Metadata);
        var query = Uri.EscapeDataString($"{wanted} official trailer");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://www.youtube.com/results?search_query={query}&hl=en&gl=US");
        // The search page is only served in full to a browser.
        request.Headers.TryAddWithoutValidation("User-Agent", repository.GetSettings().UserAgent);
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.8");
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return _cache[wanted] = null;
        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        foreach (Match m in VideoRegex().Matches(html).Take(8))
        {
            string title;
            try { title = JsonSerializer.Deserialize<string>($"\"{m.Groups["title"].Value}\"") ?? string.Empty; }
            catch (JsonException) { continue; }
            if (IsTrailerFor(wanted, WebUtility.HtmlDecode(title))) return _cache[wanted] = new YouTubeVideo(m.Groups["id"].Value, title);
        }
        return _cache[wanted] = null;
    }

    /// <summary>
    /// For console games (ROMs), which rarely have an "official trailer" on YouTube: a trailer, TV commercial or gameplay
    /// video whose title names the game (every word) — searched with the console's name so another game of the same name
    /// isn't picked.
    /// </summary>
    public async Task<YouTubeVideo?> FindRetroVideoAsync(string gameTitle, string systemWords, CancellationToken cancellationToken)
    {
        var wanted = TitleNormalizer.NormalizeForSearch(gameTitle);
        if (wanted.Length < 2) return null;
        var key = $"retro:{wanted}|{systemWords}";
        if (_cache.TryGetValue(key, out var cached)) return cached;

        var client = httpClientFactory.CreateClient(HttpClientNames.Metadata);
        foreach (var kind in new[] { "trailer", "gameplay" })
        {
            var query = Uri.EscapeDataString($"{wanted} {systemWords} {kind}");
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://www.youtube.com/results?search_query={query}&hl=en&gl=US");
            request.Headers.TryAddWithoutValidation("User-Agent", repository.GetSettings().UserAgent);
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.8");
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) continue;
            var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            foreach (Match m in VideoRegex().Matches(html).Take(10))
            {
                string title;
                try { title = JsonSerializer.Deserialize<string>($"\"{m.Groups["title"].Value}\"") ?? string.Empty; }
                catch (JsonException) { continue; }
                if (IsRetroVideoFor(wanted, WebUtility.HtmlDecode(title))) return _cache[key] = new YouTubeVideo(m.Groups["id"].Value, title);
            }
        }
        return _cache[key] = null;
    }

    /// <summary>Names the game and is a trailer, commercial or gameplay video (not a review, reaction or music).</summary>
    internal static bool IsRetroVideoFor(string normalizedGameTitle, string videoTitle)
    {
        var video = Words(videoTitle);
        if (!new[] { "trailer", "gameplay", "commercial", "longplay", "playthrough", "intro", "teaser" }.Any(video.Contains)) return false;
        if (Rejected.Any(word => Words(word).All(video.Contains))) return false;
        var game = Words(normalizedGameTitle);
        return game.Count > 0 && game.All(video.Contains);
    }

    /// <summary>Does the video title name this game and call itself a trailer?</summary>
    internal static bool IsTrailerFor(string normalizedGameTitle, string videoTitle)
    {
        var video = Words(videoTitle);
        if (!video.Contains("trailer")) return false;
        if (Rejected.Any(word => Words(word).All(video.Contains))) return false;

        var game = Words(normalizedGameTitle);
        return game.Count > 0 && game.All(video.Contains);
    }

    /// <summary>Lower-case words, punctuation and symbols (™, |, :) removed.</summary>
    private static List<string> Words(string text) =>
        Regex.Split(text.ToLowerInvariant(), @"[^\p{L}\p{Nd}]+").Where(w => w.Length > 0).ToList();
}

/// <summary>YouTube as the last trailer source (after Steam and IGDB): the official trailer, matched by title.</summary>
public sealed class YouTubeTrailerProvider(YouTubeSearch search, ILibraryRepository repository) : IGameMediaProvider
{
    public string Name => "YouTube";

    public bool IsAvailable => repository.GetSettings().UseYouTubeTrailers;

    public async Task<GameMedia?> FindAsync(string title, CancellationToken cancellationToken)
    {
        var video = await search.FindTrailerAsync(title, cancellationToken).ConfigureAwait(false);
        return video is null ? null : new GameMedia("YouTube", [], [video.WatchUrl], [video.MaxResThumbnail]);
    }
}
