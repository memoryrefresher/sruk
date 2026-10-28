using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Emulation;
using SeaRaider.Core.Models;
using SeaRaider.Core.Scraping;

namespace SeaRaider.Core.Catalog;

/// <summary>
/// Console games (ROMs) get their information from the web, because ROM sites rarely give more than a name and a file
/// (Settings › Emulators › "Get ROM info from the web"):
/// <list type="bullet">
///   <item><b>Wikipedia</b>: the description, developer, publisher, release date and genres of known games (only a page with a
///         video-game infobox whose title is this game).</item>
///   <item><b>itch.io</b>: homebrew games, which Wikipedia doesn't have: the author's description, screenshots and cover.</item>
///   <item><b>libretro's thumbnail collection</b>: the box art, title screen and in-game picture of nearly every console game.</item>
///   <item><b>YouTube</b>: a trailer, TV commercial or gameplay video naming the game and its console.</item>
/// </list>
/// Only what the ROM site left empty is filled; its own description stays when it's a real one.
/// </summary>
public sealed partial class RomInfoService(IHttpClientFactory httpClientFactory, ILibraryRepository repository, YouTubeSearch youTube,
    IEnumerable<IGameMediaProvider> mediaProviders, ILogger<RomInfoService> logger)
{
    /// <summary>Bump to look at every ROM again with new rules.</summary>
    public const int CurrentVersion = 2; // 2: IGDB first

    private readonly ConcurrentDictionary<string, Task<IReadOnlyList<string>>> _thumbnailIndex = new(StringComparer.Ordinal);

    // =============================================================================================
    // Which games are ROMs
    // =============================================================================================

    /// <summary>The console a game is for (its title, genres, tags or address say so, or its download is a ROM file), or null.</summary>
    public static GameSystem? SystemOf(GameMetadata game)
    {
        if (GameSystems.FromText($"{game.Title} {string.Join(' ', game.Genres)} {string.Join(' ', game.Tags)} {game.PageUrl}") is { } named)
            return named;
        foreach (var link in game.DownloadLinks)
        {
            var name = link.FileName.Length > 0 ? link.FileName : Uri.TryCreate(link.Url, UriKind.Absolute, out var u) ? u.AbsolutePath : link.Url;
            if (GameSystems.FromExtension(Path.GetExtension(name)) is { } byFile) return byFile;
        }
        // A ROM site ("…roms…", "homebrew"): its description names the console.
        if (Regex.IsMatch(game.PageUrl, @"rom|homebrew|emul", RegexOptions.IgnoreCase))
        {
            var start = game.Description.Length > 400 ? game.Description[..400] : game.Description;
            return GameSystems.FromText(start);
        }
        return null;
    }

    /// <summary>Should this game be looked up (a ROM not looked at with the current rules, and the setting is on)?</summary>
    public bool Wants(GameMetadata game) =>
        repository.GetSettings().RomInfoFromWeb && game.RomInfoVersion < CurrentVersion && SystemOf(game) is not null;

    // =============================================================================================
    // Filling a game in
    // =============================================================================================

    /// <summary>Fills in what the web knows about this ROM. Never throws for network trouble; returns what it did.</summary>
    public async Task<IReadOnlyList<string>> EnrichAsync(GameMetadata game, CancellationToken ct = default)
    {
        var notes = new List<string>();
        if (SystemOf(game) is not { } system) return notes;
        var title = CleanTitle(game.Title, system);
        if (title.Length < 2) return notes;
        notes.Add($"ROM for {system.Name}: looking up \"{title}\"");

        string? sharpCover = null;
        // IGDB first when its keys are set (Settings › Screenshots and trailers): the sharpest covers and screenshots, and trailers.
        if (mediaProviders.OfType<IgdbMediaProvider>().FirstOrDefault(p => p.IsAvailable) is { } igdb &&
            await Safe(() => igdb.FindRomAsync(title, system, ct), "IGDB", title).ConfigureAwait(false) is { } best)
        {
            notes.Add($"IGDB: {best.Name}");
            game.Title = best.Name;
            FillText(game, best.Summary);
            game.Developer ??= best.Developer;
            game.Publisher ??= best.Publisher;
            if (game.Genres.Count <= 1 && best.Genres.Count > 0) game.Genres = [.. game.Genres, .. best.Genres.Take(3)];
            if (string.IsNullOrWhiteSpace(game.ReleaseDate) && best.Released is { } released)
            {
                game.ReleaseDateUtc = released;
                game.ReleaseDate = released.ToString("MMMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture);
            }
            if (best.Cover is { } igdbCover) game.CoverImageUrl = sharpCover = igdbCover;
            if (best.Screenshots.Count > 0)
                game.ScreenshotUrls = best.Screenshots.Concat(game.ScreenshotUrls).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
            if (game.TrailerUrls.Count == 0 && best.Videos.Count > 0)
            {
                game.TrailerUrls = best.Videos.ToList();
                game.TrailerThumbnailUrls = best.Videos.Select(v => (string?)$"https://i.ytimg.com/vi/{v[(v.IndexOf("v=", StringComparison.Ordinal) + 2)..]}/maxresdefault.jpg").ToList();
            }
            game.StorePageUrl ??= best.Url;
        }

        var wiki = await Safe(() => FromWikipediaAsync(title, system, ct), "Wikipedia", title).ConfigureAwait(false);
        var itch = wiki is null ? await Safe(() => FromItchAsync(title, ct), "itch.io", title).ConfigureAwait(false) : null;
        var art = await Safe(() => FromThumbnailsAsync(title, system, ct), "libretro thumbnails", title).ConfigureAwait(false);

        if (wiki is not null)
        {
            notes.Add($"Wikipedia: {wiki.PageTitle}");
            FillText(game, wiki.Description);
            game.Developer ??= wiki.Developer;
            game.Publisher ??= wiki.Publisher;
            if (string.IsNullOrWhiteSpace(game.ReleaseDate) && wiki.Released is { } released)
            {
                game.ReleaseDate = released;
                game.ReleaseDateUtc ??= MetadataParser.ParseDate(released);
            }
            if (game.Genres.Count == 0 && wiki.Genres.Count > 0) game.Genres = wiki.Genres.Take(4).ToList();
            game.StorePageUrl ??= wiki.PageUrl;
        }
        else if (itch is not null)
        {
            notes.Add($"itch.io: {itch.Title}");
            FillText(game, itch.Description);
            game.Developer ??= itch.Developer;
            if (itch.ScreenshotUrls.Count > 0)
                game.ScreenshotUrls = itch.ScreenshotUrls.Concat(game.ScreenshotUrls).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
            if (itch.CoverImageUrl is { } itchCover) game.CoverImageUrl = itchCover;
            game.StorePageUrl ??= itch.PageUrl;
        }
        else notes.Add("No Wikipedia or itch.io page for it.");

        // Pictures: the box art is the cover (sharper than the site's thumbnail); title screen and gameplay as screenshots.
        if (art is not null)
        {
            notes.Add($"Box art and screenshots: libretro ({art.Name})");
            if (art.BoxArt is { } box) game.CoverImageUrl = box;
            var shots = new[] { art.Snap, art.TitleScreen }.OfType<string>();
            game.ScreenshotUrls = game.ScreenshotUrls.Concat(shots).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
        }
        else if (wiki?.Image is { } wikiImage && (string.IsNullOrEmpty(game.CoverImageUrl) || wiki is not null))
        {
            game.CoverImageUrl = wikiImage; // the cover art on the game's Wikipedia page
        }

        // A trailer (or gameplay) when the page has none.
        if (game.TrailerUrls.Count == 0 && repository.GetSettings().UseYouTubeTrailers)
        {
            var words = system.Aliases.FirstOrDefault(a => a.Length > 3) ?? system.Aliases[0];
            if (await Safe(() => youTube.FindRetroVideoAsync(title, words, ct), "YouTube", title).ConfigureAwait(false) is { } video)
            {
                game.TrailerUrls = [video.WatchUrl];
                game.TrailerThumbnailUrls = [video.MaxResThumbnail];
                notes.Add($"Video: {video.Title}");
            }
        }

        if (sharpCover is not null) game.CoverImageUrl = sharpCover; // IGDB's cover is the sharpest
        game.RomInfoVersion = CurrentVersion;
        game.MediaChecked = true;
        game.MediaVersion = GameMetadata.CurrentMediaVersion;
        game.MediaSource ??= wiki is not null ? "Wikipedia" : itch is not null ? "itch.io" : art is not null ? "libretro" : null;
        return notes;
    }

    /// <summary>The web's description goes in when the site's is missing or only a line or two (it's kept after it).</summary>
    private static void FillText(GameMetadata game, string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return;
        var own = game.Description.Trim();
        if (own.Length < 250)
            game.Description = own.Length == 0 || description.Contains(own, StringComparison.OrdinalIgnoreCase)
                ? description
                : description + "\n\n" + own;
        if (game.ShortDescription.Length < 40)
            game.ShortDescription = description.Length > 220 ? description[..220].TrimEnd() + "…" : description;
    }

    private async Task<T?> Safe<T>(Func<Task<T?>> lookup, string where, string title) where T : class
    {
        try
        {
            return await lookup().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
        {
            logger.LogInformation(ex, "{Where} lookup failed for {Title}", where, title);
            return null;
        }
    }

    /// <summary>"Super Mario World (USA) [!] (SNES ROM)" → "Super Mario World".</summary>
    internal static string CleanTitle(string title, GameSystem system)
    {
        var text = Regex.Replace(title, @"\([^)]*\)|\[[^\]]*\]|\{[^}]*\}", " ");
        text = Regex.Replace(text, @"\b(rom|roms|homebrew|hack|v?\d+\.\d+(\.\d+)*|download|free|game)\b", " ", RegexOptions.IgnoreCase);
        foreach (var alias in system.Aliases.OrderByDescending(a => a.Length))
            text = Regex.Replace(text, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(alias)}(?![\p{{L}}\p{{N}}])", " ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\s*[-–—:|]+\s*$", string.Empty);
        text = Regex.Replace(text, @"^\s*[-–—:|]+\s*", string.Empty);
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    // =============================================================================================
    // Wikipedia
    // =============================================================================================

    private sealed record WikiGame(string PageTitle, string PageUrl, string? Description, string? Developer, string? Publisher,
        string? Released, List<string> Genres, string? Image);

    private async Task<WikiGame?> FromWikipediaAsync(string title, GameSystem system, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(HttpClientNames.Metadata);
        var search = Uri.EscapeDataString($"{title} {system.Aliases[0]} video game");
        using var found = JsonDocument.Parse(await client.GetStringAsync(
            $"https://en.wikipedia.org/w/api.php?action=query&list=search&srsearch={search}&srlimit=6&format=json&formatversion=2", ct).ConfigureAwait(false));
        var candidates = found.RootElement.GetProperty("query").GetProperty("search").EnumerateArray()
            .Select(r => r.GetProperty("title").GetString() ?? string.Empty)
            .Where(t => MediaScraperService.IsSameGame(title, Regex.Replace(t, @"\s*\([^)]*\)\s*$", string.Empty)))
            .Take(2)
            .ToList();

        foreach (var pageTitle in candidates)
        {
            var url = "https://en.wikipedia.org/w/api.php?action=query&prop=extracts|pageimages|revisions&exintro=1&explaintext=1" +
                      "&piprop=original&rvprop=content&rvslots=main&rvsection=0&redirects=1&format=json&formatversion=2&titles=" +
                      Uri.EscapeDataString(pageTitle);
            using var doc = JsonDocument.Parse(await client.GetStringAsync(url, ct).ConfigureAwait(false));
            var page = doc.RootElement.GetProperty("query").GetProperty("pages")[0];
            if (!page.TryGetProperty("revisions", out var revisions)) continue;
            var wikitext = revisions[0].GetProperty("slots").GetProperty("main").GetProperty("content").GetString() ?? string.Empty;
            if (!wikitext.Contains("{{Infobox video game", StringComparison.OrdinalIgnoreCase)) continue; // not a game's article

            var extract = page.TryGetProperty("extract", out var e) ? e.GetString() : null;
            var image = page.TryGetProperty("original", out var o) && o.TryGetProperty("source", out var src) ? src.GetString() : null;
            var name = page.GetProperty("title").GetString() ?? pageTitle;
            return new WikiGame(name, "https://en.wikipedia.org/wiki/" + Uri.EscapeDataString(name.Replace(' ', '_')),
                CleanExtract(extract),
                Join(Infobox(wikitext, "developer")),
                Join(Infobox(wikitext, "publisher")),
                FirstDate(InfoboxRaw(wikitext, "released") ?? InfoboxRaw(wikitext, "release")),
                Infobox(wikitext, "genre"),
                image);
        }
        return null;
    }

    private static string? CleanExtract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
        return text.Length > 2400 ? text[..2400].TrimEnd() + "…" : text;
    }

    private static string? Join(List<string> items) => items.Count == 0 ? null : string.Join(", ", items.Take(3));

    /// <summary>The raw value of an infobox field ("| developer = [[Nintendo EAD]]"), up to the next field.</summary>
    private static string? InfoboxRaw(string wikitext, string field)
    {
        var m = Regex.Match(wikitext, $@"^\s*\|\s*{field}s?\s*=(?<v>.*?)(?=^\s*\|\s*[\w ]+=|^\}}\}})", RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var value = m.Success ? m.Groups["v"].Value.Trim() : null;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>An infobox field as a list of plain names (links, lists and references undone).</summary>
    private static List<string> Infobox(string wikitext, string field)
    {
        if (InfoboxRaw(wikitext, field) is not { } raw) return [];
        var text = CleanWiki(raw);
        return Regex.Split(text, @"\s*(?:,|\n|;|\s{2,})\s*")
            .Select(s => s.Trim(' ', '*', '-', '•'))
            .Where(s => s.Length is > 1 and < 60 && !Regex.IsMatch(s, @"^(NA|EU|JP|PAL|WW|AU|KO|BR)$"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string CleanWiki(string raw)
    {
        var text = Regex.Replace(raw, @"<ref[^>]*/>|<ref[^>]*>.*?</ref>|<!--.*?-->", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", " ");
        text = Regex.Replace(text, @"\[\[(?:[^|\]]*\|)?([^\]]*)\]\]", "$1"); // [[a|b]] → b, [[a]] → a
        // List templates keep their items: {{ubl|a|b}}, {{plainlist| * a * b}}, {{collapsible list|…}}
        for (var i = 0; i < 4; i++)
            text = Regex.Replace(text, @"\{\{\s*(?:ubl|unbulleted list|plainlist|plain list|flatlist|hlist|collapsible list|vgrelease|video game release)\s*\|([^{}]*)\}\}",
                m => "\n" + m.Groups[1].Value.Replace('|', '\n'), RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\{\{[^{}]*\}\}", " "); // other templates
        text = text.Replace("'''", string.Empty).Replace("''", string.Empty).Replace("*", "\n");
        text = Regex.Replace(text, @"\w+\s*=\s*", string.Empty); // leftover "title=" parameters
        return WebUtility.HtmlDecode(text);
    }

    private static readonly Regex DateRegex = new(
        @"(?:January|February|March|April|May|June|July|August|September|October|November|December)\s+\d{1,2},\s+\d{4}|" +
        @"\d{1,2}\s+(?:January|February|March|April|May|June|July|August|September|October|November|December)\s+\d{4}|" +
        @"(?:January|February|March|April|May|June|July|August|September|October|November|December)\s+\d{4}|\b(?:19[6-9]\d|20[0-4]\d)\b",
        RegexOptions.Compiled);

    private static string? FirstDate(string? raw)
    {
        if (raw is null) return null;
        var text = CleanWiki(raw);
        // {{Start date|1991|11|21}}
        var start = Regex.Match(raw, @"\{\{\s*start date[^|]*\|\s*(\d{4})\s*(?:\|\s*(\d{1,2})\s*(?:\|\s*(\d{1,2}))?)?", RegexOptions.IgnoreCase);
        if (start.Success)
        {
            var year = int.Parse(start.Groups[1].Value);
            if (start.Groups[2].Success && int.TryParse(start.Groups[2].Value, out var month) && month is >= 1 and <= 12)
                return start.Groups[3].Success && int.TryParse(start.Groups[3].Value, out var day) && day is >= 1 and <= 31
                    ? new DateTime(year, month, Math.Min(day, DateTime.DaysInMonth(year, month))).ToString("MMMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture)
                    : new DateTime(year, month, 1).ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
            return year.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return DateRegex.Match(text) is { Success: true } m ? m.Value : null;
    }

    // =============================================================================================
    // itch.io (homebrew)
    // =============================================================================================

    private async Task<GameMetadata?> FromItchAsync(string title, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(HttpClientNames.Metadata);
        var source = new GameSource { Url = "https://itch.io/games" };
        var searchUri = ItchIo.BuildUrl(source, title, 1);
        var html = await client.GetStringAsync(searchUri, ct).ConfigureAwait(false);
        using var doc = await GamePageParser.ParseHtmlAsync(html, searchUri, ct).ConfigureAwait(false);
        var (games, _) = ItchIo.ParseListing(doc, searchUri, source);
        if (games.FirstOrDefault(g => MediaScraperService.IsSameGame(title, g.Title)) is not { } match) return null;

        var pageUri = new Uri(match.PageUrl);
        var pageHtml = await client.GetStringAsync(pageUri, ct).ConfigureAwait(false);
        using var page = await GamePageParser.ParseHtmlAsync(pageHtml, pageUri, ct).ConfigureAwait(false);
        var full = new GameMetadata { PageUrl = match.PageUrl, Title = match.Title, CoverImageUrl = match.CoverImageUrl, Developer = match.Developer };
        ItchIo.EnrichFromGamePage(page, pageUri, full);
        full.CoverImageUrl ??= page.QuerySelector("meta[property='og:image']")?.GetAttribute("content");
        return full;
    }

    // =============================================================================================
    // libretro thumbnails (box art, title screen, gameplay)
    // =============================================================================================

    private sealed record Thumbnails(string Name, string? BoxArt, string? TitleScreen, string? Snap);

    /// <summary>libretro's folder name for each console ("Nintendo - Super Nintendo Entertainment System").</summary>
    private static readonly Dictionary<string, string> ThumbnailFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["snes"] = "Nintendo - Super Nintendo Entertainment System",
        ["nes"] = "Nintendo - Nintendo Entertainment System",
        ["n64"] = "Nintendo - Nintendo 64",
        ["gba"] = "Nintendo - Game Boy Advance",
        ["gbc"] = "Nintendo - Game Boy Color",
        ["gb"] = "Nintendo - Game Boy",
        ["3ds"] = "Nintendo - Nintendo 3DS",
        ["nds"] = "Nintendo - Nintendo DS",
        ["dsi"] = "Nintendo - Nintendo DSi",
        ["wii"] = "Nintendo - Wii",
        ["gc"] = "Nintendo - GameCube",
        ["vb"] = "Nintendo - Virtual Boy",
        ["ps1"] = "Sony - PlayStation",
        ["ps2"] = "Sony - PlayStation 2",
        ["ps3"] = "Sony - PlayStation 3",
        ["xbox"] = "Microsoft - Xbox",
        ["psp"] = "Sony - PlayStation Portable",
        ["segacd"] = "Sega - Mega-CD - Sega CD",
        ["32x"] = "Sega - 32X",
        ["genesis"] = "Sega - Mega Drive - Genesis",
        ["sms"] = "Sega - Master System - Mark III",
        ["gamegear"] = "Sega - Game Gear",
        ["saturn"] = "Sega - Saturn",
        ["dreamcast"] = "Sega - Dreamcast",
        ["pce"] = "NEC - PC Engine - TurboGrafx 16",
        ["atari2600"] = "Atari - 2600",
        ["atari5200"] = "Atari - 5200",
        ["atari7800"] = "Atari - 7800",
        ["lynx"] = "Atari - Lynx",
        ["jaguar"] = "Atari - Jaguar",
        ["ngp"] = "SNK - Neo Geo Pocket Color",
        ["wonderswan"] = "Bandai - WonderSwan Color",
        ["3do"] = "The 3DO Company - 3DO",
        ["colecovision"] = "Coleco - ColecoVision",
        ["intellivision"] = "Mattel - Intellivision",
        ["vectrex"] = "GCE - Vectrex",
    };

    private const string ThumbnailHost = "https://thumbnails.libretro.com/";

    /// <summary>
    /// libretro's box art for a ROM named like the No-Intro / Redump sets ("Super Mario Bros. (World)"), or null for consoles it
    /// doesn't cover. (Its file names have &amp;*/:`&lt;&gt;?\| as _.)
    /// </summary>
    /// <summary>
    /// The box art for a console game from libretro's collection: matched by name (any region, USA first), else the file's own
    /// name when libretro has that exact picture. Null when there's none.
    /// </summary>
    public async Task<string?> FindBoxArtAsync(string title, string romName, GameSystem system, CancellationToken ct = default)
    {
        if (await FromThumbnailsAsync(title, system, ct).ConfigureAwait(false) is { BoxArt: { } matched }) return matched;
        if (CleanTitle(title, system) is { Length: > 0 } clean && clean != title &&
            await FromThumbnailsAsync(clean, system, ct).ConfigureAwait(false) is { BoxArt: { } byClean }) return byClean;
        return BoxArtFor(system, romName) is { } exact && await ExistsAsync(exact, ct).ConfigureAwait(false) ? exact : null;
    }

    public static string? BoxArtFor(GameSystem system, string romName)
    {
        if (!ThumbnailFolders.TryGetValue(system.Id, out var folder)) return null;
        var name = Regex.Replace(romName, @"[&*/:`<>?\\|]", "_");
        return ThumbnailHost + Uri.EscapeDataString(folder) + "/Named_Boxarts/" + Uri.EscapeDataString(name) + ".png";
    }

    [GeneratedRegex(@"href=""(?<f>[^""]+\.png)""", RegexOptions.IgnoreCase)]
    private static partial Regex PngLinkRegex();

    private async Task<Thumbnails?> FromThumbnailsAsync(string title, GameSystem system, CancellationToken ct)
    {
        if (!ThumbnailFolders.TryGetValue(system.Id, out var folder)) return null;
        var names = await _thumbnailIndex.GetOrAdd(folder, f => LoadIndexAsync(f)).WaitAsync(ct).ConfigureAwait(false);
        if (names.Count == 0)
        {
            _thumbnailIndex.TryRemove(folder, out _); // try again next time
            return null;
        }

        var key = TitleNormalizer.DedupKey(title);
        var matches = names
            .Where(n => TitleNormalizer.DedupKey(ArticleFirst(Regex.Replace(n, @"\s*[\(\[].*$", string.Empty))) == key)
            .OrderBy(n => n.Contains("(USA", StringComparison.Ordinal) ? 0 : n.Contains("(World", StringComparison.Ordinal) ? 1 :
                          n.Contains("(Europe", StringComparison.Ordinal) ? 2 : 3)
            .ThenBy(n => n.Length)
            .ToList();
        if (matches.Count == 0) return null;

        var name = matches[0];
        string Url(string kind) => ThumbnailHost + Uri.EscapeDataString(folder) + "/" + kind + "/" + Uri.EscapeDataString(name) + ".png";
        // The box art is in the list; the title screen and gameplay pictures aren't always there.
        var titleScreen = Url("Named_Titles");
        var snap = Url("Named_Snaps");
        var exists = await Task.WhenAll(ExistsAsync(titleScreen, ct), ExistsAsync(snap, ct)).ConfigureAwait(false);
        return new Thumbnails(name, Url("Named_Boxarts"), exists[0] ? titleScreen : null, exists[1] ? snap : null);
    }

    private async Task<bool> ExistsAsync(string url, CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientNames.Metadata);
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>libretro's names put the article last ("Legend of Zelda, The - A Link to the Past"): put it back in front.</summary>
    private static string ArticleFirst(string name)
    {
        var m = Regex.Match(name, @"^(?<main>[^,]+?),\s*(?<article>The|A|An)\b(?<rest>.*)$", RegexOptions.IgnoreCase);
        return m.Success ? $"{m.Groups["article"].Value} {m.Groups["main"].Value}{m.Groups["rest"].Value}" : name;
    }

    /// <summary>Every box-art name for a console (the folder's file list), read once per run.</summary>
    private async Task<IReadOnlyList<string>> LoadIndexAsync(string folder)
    {
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientNames.Metadata);
            var html = await client.GetStringAsync(ThumbnailHost + Uri.EscapeDataString(folder) + "/Named_Boxarts/").ConfigureAwait(false);
            return PngLinkRegex().Matches(html)
                .Select(m => Uri.UnescapeDataString(WebUtility.HtmlDecode(m.Groups["f"].Value)))
                .Select(f => f[..^4])
                .Where(f => !f.Contains('/'))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogInformation(ex, "libretro thumbnail list for {Folder} unavailable", folder);
            return [];
        }
    }
}
