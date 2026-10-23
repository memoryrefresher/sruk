using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Models;
using SeaRaider.Core.Scraping;

namespace SeaRaider.Core.Catalog;

/// <summary>
/// Good, large pictures of what a Media tab title is, instead of the site's own picture (often just its logo on every card).
/// All from public sources that need no key:
/// <list type="bullet">
///   <item><b>Matches</b> ("Bayern Munich vs Arsenal"): each side's crest or logo from Wikipedia (its article's picture, as a
///         500 px PNG), looked up with the sport ("Arsenal football club"). Shown as two crests with "vs" between.</item>
///   <item><b>Shows</b>: TVmaze's poster (original size).</item>
///   <item><b>Movies</b>: the iTunes Store's poster (600×900).</item>
///   <item><b>Anything else</b> (a studio show, an event): its Wikipedia article's picture.</item>
/// </list>
/// Only a confident name match is used. Results (found or not) are kept in the artwork table, so each name is looked up once
/// (misses again after <see cref="RetryMissesAfter"/>). Lookups are slow on purpose (two at a time, a pause between) to stay
/// within the services' free limits.
/// </summary>
public sealed partial class MediaArtworkService(IHttpClientFactory httpClientFactory, ILibraryRepository repository,
    ILogger<MediaArtworkService> logger)
{
    /// <summary>Records made by older rules are looked up again.</summary>
    private const int RulesVersion = 1;

    private static readonly TimeSpan RetryMissesAfter = TimeSpan.FromDays(3);
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(350);
    private readonly SemaphoreSlim _gate = new(2, 2);
    private DateTime _pausedUntilUtc = DateTime.MinValue; // a service said "too many requests"

    [GeneratedRegex(@"\((?<year>(?:19|20)\d{2})\)|\b(?<year>(?:19|20)\d{2})$")]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonWordRegex();

    // "(TNT)", "[HD]", "- Live", "Highlights" … around a name: left out of the search.
    [GeneratedRegex(@"\s*[\(\[][^\)\]]*[\)\]]|\s+[-|:]\s+.*$|\b(?:highlights|full\s+match|replay|live|stream|hd)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NoiseRegex();

    private HttpClient Api => httpClientFactory.CreateClient(HttpClientNames.Metadata);

    /// <summary>The cache key for a title (and its sport, for matches).</summary>
    private static string KeyOf(MediaItem item) => "media|" + Norm(item.Title) + (item.Category is { } c ? "|" + Norm(c) : "");

    /// <summary>
    /// Fills <paramref name="item"/>'s pictures from what was found before. True when there's nothing left to look up for it
    /// (found, or looked up recently without luck).
    /// </summary>
    public bool ApplyCached(MediaItem item)
    {
        item.IsMatchup = MediaTitleParser.Matchup(item.Title) is not null;
        var record = repository.GetArtwork(KeyOf(item));
        if (record is null || record.RulesVersion != RulesVersion) return false;
        if (record.Found)
        {
            item.ArtworkUrl = record.HeaderUrl;
            item.ArtworkUrl2 = record.HeroUrl;
            return true;
        }
        return DateTime.UtcNow - record.CheckedAtUtc < RetryMissesAfter;
    }

    /// <summary>Looks the title up and fills its pictures. True when something was found.</summary>
    public async Task<bool> FindAsync(MediaItem item, CancellationToken ct)
    {
        if (DateTime.UtcNow < _pausedUntilUtc) return false;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Delay(Pause, ct).ConfigureAwait(false);
            string? first, second = null, source;
            if (MediaTitleParser.Matchup(item.Title) is { } match)
            {
                item.IsMatchup = true;
                first = await TeamAsync(match.Home, item.Category, ct).ConfigureAwait(false);
                second = await TeamAsync(match.Away, item.Category, ct).ConfigureAwait(false);
                source = "Wikipedia";
            }
            else
            {
                (first, source) = await PosterAsync(item.Title, item.Category, ct).ConfigureAwait(false);
            }

            var found = first is not null || second is not null;
            repository.UpsertArtwork(new ArtworkRecord
            {
                Key = KeyOf(item),
                HeaderUrl = first,
                HeroUrl = second,
                Source = found ? source : null,
                Found = found,
                RulesVersion = RulesVersion,
                CheckedAtUtc = DateTime.UtcNow,
            });
            if (!found) return false;
            item.ArtworkUrl = first;
            item.ArtworkUrl2 = second;
            return true;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            _pausedUntilUtc = DateTime.UtcNow.AddMinutes(1);
            logger.LogInformation("Media artwork: asked to slow down; pausing a minute");
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Media artwork for {Title} failed", item.Title);
            return false; // not recorded: tried again next time
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- matches: each side's crest ---------------------------------------------------------------------

    private async Task<string?> TeamAsync(string team, string? sport, CancellationToken ct)
    {
        var name = Clean(team);
        if (name.Length < 2) return null;
        var hint = (sport ?? string.Empty).ToLowerInvariant() switch
        {
            "football" or "soccer" => "football club",
            "american football" or "nfl" => "American football team",
            "basketball" or "nba" => "basketball team",
            "hockey" or "ice hockey" or "nhl" => "ice hockey team",
            "baseball" or "mlb" => "baseball team",
            "rugby" => "rugby team",
            "cricket" => "cricket team",
            "afl" => "Australian rules football club",
            "fight" or "fighting" or "mma" or "ufc" or "boxing" or "wrestling" => "fighter",
            "tennis" or "golf" or "darts" or "snooker" or "billiards" => "player",
            _ => "team",
        };
        return await WikipediaImageAsync($"{name} {hint}", name, 500, ct).ConfigureAwait(false);
    }

    // ---- shows, movies, anything else: a poster ----------------------------------------------------------

    private async Task<(string? Url, string Source)> PosterAsync(string title, string? category, CancellationToken ct)
    {
        var year = YearRegex().Match(title) is { Success: true } y ? int.Parse(y.Groups["year"].Value) : (int?)null;
        var name = Clean(YearRegex().Replace(title, string.Empty));
        if (name.Length < 2) return (null, string.Empty);

        // A sports listing's studio show or event: Wikipedia knows it; TVmaze / iTunes would only find namesakes.
        if (category is null)
        {
            if (year is null && await TvMazeAsync(name, ct).ConfigureAwait(false) is { } show) return (show, "TVmaze");
            if (await ITunesMovieAsync(name, year, ct).ConfigureAwait(false) is { } movie) return (movie, "iTunes");
            if (year is not null && await TvMazeAsync(name, ct).ConfigureAwait(false) is { } laterShow) return (laterShow, "TVmaze");
        }
        var query = year is not null ? $"{name} {year} film" : category is not null ? $"{name} {category}" : name;
        return (await WikipediaImageAsync(query, name, 800, ct).ConfigureAwait(false), "Wikipedia");
    }

    /// <summary>TVmaze's best match for a show name: its original-size poster, when the name really matches.</summary>
    private async Task<string?> TvMazeAsync(string name, CancellationToken ct)
    {
        using var response = await Api.GetAsync($"https://api.tvmaze.com/singlesearch/shows?q={Uri.EscapeDataString(name)}", ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = json.RootElement;
        if (!SameName(Text(root, "name"), name)) return null;
        return root.TryGetProperty("image", out var image) && image.ValueKind == JsonValueKind.Object
            ? Text(image, "original") ?? Text(image, "medium")
            : null;
    }

    /// <summary>The iTunes Store's movie poster (600×900) for a name (and year, when known).</summary>
    private async Task<string?> ITunesMovieAsync(string name, int? year, CancellationToken ct)
    {
        using var response = await Api.GetAsync(
            $"https://itunes.apple.com/search?media=movie&entity=movie&limit=5&term={Uri.EscapeDataString(name)}", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (!json.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return null;
        foreach (var movie in results.EnumerateArray())
        {
            if (!SameName(Text(movie, "trackName"), name)) continue;
            if (year is not null && DateTime.TryParse(Text(movie, "releaseDate"), out var released) && Math.Abs(released.Year - year.Value) > 1) continue;
            if (Text(movie, "artworkUrl100") is { } art) return art.Replace("100x100bb", "600x900bb", StringComparison.Ordinal);
        }
        return null;
    }

    /// <summary>
    /// The picture of the first Wikipedia article a search finds (a crest, logo, poster or photo), as a PNG/JPEG of
    /// <paramref name="size"/> px, when the article's name contains the name searched for. Club crests are usually non-free
    /// files, hence pilicense=any.
    /// </summary>
    private async Task<string?> WikipediaImageAsync(string query, string name, int size, CancellationToken ct)
    {
        var url = "https://en.wikipedia.org/w/api.php?action=query&format=json&formatversion=2&generator=search&gsrlimit=3" +
                  $"&prop=pageimages&piprop=thumbnail&pilicense=any&pithumbsize={size}&gsrsearch={Uri.EscapeDataString(query)}";
        using var response = await Api.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (!json.RootElement.TryGetProperty("query", out var q) || !q.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var page in pages.EnumerateArray().OrderBy(p => p.TryGetProperty("index", out var i) ? i.GetInt32() : 99))
        {
            if (!ContainsName(Text(page, "title"), name)) continue;
            if (page.TryGetProperty("thumbnail", out var thumb) && Text(thumb, "source") is { } source) return source;
        }
        return null;
    }

    // ---- sports: which sport a title is (its box shows the sport's logo when nothing else was found) ---------

    /// <summary>Sport names → the sport (the Articles were for photos, unused now).</summary>
    private static readonly (string[] Words, string Key, string[] Articles)[] Sports =
    [
        (["american football", "nfl", "ncaaf", "college football"], "american-football", ["American football", "National Football League"]),
        (["aussie", "afl", "australian rules"], "afl", ["Australian rules football"]),
        (["football", "soccer", "futbol", "premier league", "la liga", "champions league", "serie a", "bundesliga", "mls"], "football", ["Association football", "Stadium"]),
        (["basketball", "nba", "wnba", "ncaab", "euroleague"], "basketball", ["Basketball", "National Basketball Association"]),
        (["ice hockey", "hockey", "nhl"], "hockey", ["Ice hockey", "National Hockey League"]),
        (["baseball", "mlb"], "baseball", ["Baseball", "Major League Baseball"]),
        (["ufc", "mma", "mixed martial"], "mma", ["Mixed martial arts"]),
        (["boxing"], "boxing", ["Boxing"]),
        (["wrestling", "wwe", "aew"], "wrestling", ["Professional wrestling"]),
        (["fight", "fighting", "combat"], "mma", ["Mixed martial arts"]),
        (["horse racing", "horse"], "horse-racing", ["Horse racing"]),
        (["motogp", "motorcycle", "superbike"], "motorcycle", ["Motorcycle racing", "Grand Prix motorcycle racing"]),
        (["formula", "f1", "nascar", "indycar", "motor", "racing", "motorsport", "rally"], "motorsport", ["Formula One car", "Auto racing", "Motorsport"]),
        (["table tennis", "ping pong"], "table-tennis", ["Table tennis"]),
        (["tennis", "atp", "wta"], "tennis", ["Tennis"]),
        (["rugby"], "rugby", ["Rugby union", "Rugby league"]),
        (["golf", "pga"], "golf", ["Golf"]),
        (["cricket", "ipl"], "cricket", ["Cricket"]),
        (["darts"], "darts", ["Darts"]),
        (["snooker", "billiards", "pool"], "cue-sports", ["Snooker", "Cue sports"]),
        (["volleyball"], "volleyball", ["Volleyball"]),
        (["handball"], "handball", ["Handball"]),
        (["cycling", "tour de france"], "cycling", ["Cycle sport", "Road bicycle racing"]),
        (["esports", "e-sports", "gaming"], "esports", ["Esports"]),
        (["badminton"], "badminton", ["Badminton"]),
        (["athletics", "olympics", "track"], "athletics", ["Athletics (sport)", "Olympic Games"]),
    ];

    /// <summary>Which sport a title's category names ("Football", "Fight (UFC, Boxing)"), or "any" for a live event with no known
    /// sport. Null: not a sport (a movie, a show).</summary>
    public static string? SportKey(string? category, bool isLive)
    {
        var text = " " + Norm(category) + " ";
        foreach (var (words, key, _) in Sports)
            if (words.Any(w => text.Contains(" " + w + " ", StringComparison.Ordinal))) return key;
        return isLive || string.Equals(Norm(category), "sports", StringComparison.Ordinal) ? "any" : null;
    }

    // ---- names --------------------------------------------------------------------------------------------

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Clean(string text) => string.Join(' ', NoiseRegex().Replace(text, " ").Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string Norm(string? text) =>
        NonWordRegex().Replace(TitleNormalizer.Fold((text ?? string.Empty).ToLowerInvariant()), " ").Trim();

    /// <summary>Two names are the same title ("The Office" / "The Office (US)" yes, "Alpha" / "Alphaville" no).</summary>
    internal static bool SameName(string? found, string wanted)
    {
        var a = Norm(found);
        var b = Norm(wanted);
        if (a.Length == 0 || b.Length == 0) return false;
        if (a == b) return true;
        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        return longer.StartsWith(shorter + " ", StringComparison.Ordinal) && shorter.Length >= longer.Length * 0.6;
    }

    /// <summary>An article's name holds every word of the name ("FC Bayern Munich" for "Bayern Munich").</summary>
    internal static bool ContainsName(string? article, string wanted)
    {
        var words = Norm(article).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var needed = Norm(wanted).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (needed.Length == 0 || words.Count == 0) return false;
        // "Man Utd" → "Manchester United F.C.": allow one word to be missing in names of three words or more.
        var missing = needed.Count(w => !words.Contains(w) && !words.Any(x => x.StartsWith(w, StringComparison.Ordinal)));
        return missing == 0 || (needed.Length >= 3 && missing == 1);
    }
}
