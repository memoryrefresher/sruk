using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using SeaRaider.Core.Abstractions;

namespace SeaRaider.Core.Catalog;

/// <summary>
/// HD key art for games no store sells on PC (console ports, emulated releases): the first results of a web image search
/// limited to wallpaper-size, wide pictures (Bing's public image search, no key). The artwork service measures each
/// one and keeps the first that is really 1600+ px wide and landscape.
/// <para>A result only counts when its own title or page names the game (every word of it): image searches happily fill
/// the page with popular games' art for an unknown title. Titles too short or too common to tell apart get nothing.</para>
/// </summary>
public sealed partial class WallpaperSearch(IHttpClientFactory httpClientFactory, ILibraryRepository repository)
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<string>> FindAsync(string gameTitle, CancellationToken cancellationToken)
    {
        var name = DisplayText.Title(gameTitle);
        if (name.Length < 2) return [];
        if (_cache.TryGetValue(name, out var cached)) return cached;

        var query = Uri.EscapeDataString($"\"{name}\" game key art wallpaper");
        var url = $"https://www.bing.com/images/search?q={query}&qft=+filterui:imagesize-wallpaper+filterui:aspect-wide&form=IRFLTR&first=1";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", repository.GetSettings().UserAgent); // served in full to browsers
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.8");
        using var response = await httpClientFactory.CreateClient(HttpClientNames.Metadata)
            .SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return _cache[name] = [];

        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return _cache[name] = Pick(html, name);
    }

    /// <summary>Wallpapers from a results page whose title / page address / caption name the game.</summary>
    internal static List<string> Pick(string html, string name)
    {
        var wanted = TitleNormalizer.Tokens(TitleNormalizer.NormalizeForSearch(name))
            .Where(w => w.Length >= 2 && !CommonWords.Contains(w))
            .ToList();
        // One short or very common word ("Ash", "Home") matches anything: don't guess.
        if (wanted.Count == 0 || wanted.Count == 1 && wanted[0].Length < 5) return [];

        var results = new List<string>();
        foreach (Match m in ResultRegex().Matches(html))
        {
            var json = WebUtility.HtmlDecode(m.Groups["j"].Value);
            var url = Field(json, "murl");
            if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
                url.Contains(".gif", StringComparison.OrdinalIgnoreCase))
                continue;

            var about = TitleNormalizer.Fold($"{Field(json, "t")} {Field(json, "desc")} {Field(json, "purl")} {url}");
            var words = TitleNormalizer.Tokens(about).ToHashSet(StringComparer.Ordinal);
            if (!wanted.All(words.Contains)) continue;

            if (!results.Contains(url, StringComparer.OrdinalIgnoreCase)) results.Add(url);
            if (results.Count >= 5) break;
        }
        return results;
    }

    private static readonly HashSet<string> CommonWords = new(StringComparer.Ordinal)
        { "the", "of", "a", "an", "and", "to", "in", "on", "for", "game", "edition", "remastered", "2", "3", "4" };

    // Each result is an <a … m="{…}"> whose HTML-encoded JSON has the picture ("murl"), its title ("t"), page ("purl").
    [GeneratedRegex(@"\bm=""(?<j>\{[^""]*?\})""", RegexOptions.IgnoreCase)]
    private static partial Regex ResultRegex();

    private static string? Field(string json, string name)
    {
        var m = Regex.Match(json, $@"""{name}""\s*:\s*""(?<v>(?:[^""\\]|\\.)*)""");
        if (!m.Success) return null;
        try { return Regex.Unescape(m.Groups["v"].Value); }
        catch (ArgumentException) { return m.Groups["v"].Value.Replace("\\/", "/"); }
    }
}
