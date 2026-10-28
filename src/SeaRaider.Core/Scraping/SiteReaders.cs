using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Microsoft.Extensions.Logging;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Scraping;

/// <summary>
/// A dedicated reader for one site, written as a small JSON file instead of code: where the games are listed, and where
/// each piece of a game's page is (CSS selectors). Put it in a <c>readers</c> folder (next to SeaRaider.exe, or
/// %LOCALAPPDATA%\SeaRaider\readers) and SeaRaider uses it for that site's search, browsing and game pages, on top of its
/// generic reader. See readers\README.txt for the format.
///
/// <para><b>Selectors</b>: any CSS selector. Text by default; <c>selector@attribute</c> takes an attribute instead
/// (<c>img.cover@data-src</c>). Addresses (cover, screenshots, links, downloads) take the usual attribute by themselves
/// (href, src, data-src …). Several tries: <c>h1.title || h1</c>.</para>
/// </summary>
public sealed class SiteReader
{
    /// <summary>The site's name in SeaRaider ("Example Games").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Its domains ("example.com" also covers www.example.com and other sub-domains).</summary>
    public List<string> Domains { get; set; } = [];

    /// <summary>Its list of games, page by page: <c>{page}</c> is 1, 2, 3 …</summary>
    public string? BrowseUrl { get; set; }

    /// <summary>Its search: <c>{query}</c> and <c>{page}</c>.</summary>
    public string? SearchUrl { get; set; }

    /// <summary>How to read a list (browse or search page).</summary>
    public ListingRecipe? Listing { get; set; }

    /// <summary>How to read a game's page.</summary>
    public PageRecipe? Page { get; set; }

    /// <summary>The site's own pictures are the game's: don't look it up on Steam / IGDB (default true).</summary>
    public bool UseOwnPictures { get; set; } = true;

    /// <summary>Where it was loaded from.</summary>
    [JsonIgnore] public string FilePath { get; set; } = string.Empty;

    public bool Covers(string? host) =>
        !string.IsNullOrEmpty(host) && Domains.Any(d =>
            host.Equals(d, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + d.TrimStart('.'), StringComparison.OrdinalIgnoreCase));
}

/// <summary>A list page: one <see cref="Item"/> per game, and what's inside it.</summary>
public sealed class ListingRecipe
{
    /// <summary>One game in the list (".game-card").</summary>
    public string Item { get; set; } = string.Empty;

    /// <summary>The link to its page, inside the item (default: the first link).</summary>
    public string? Link { get; set; }

    public string? Title { get; set; }
    public string? Cover { get; set; }
    public string? Summary { get; set; }
    public string? Developer { get; set; }
    public string? Genre { get; set; }
    public string? Date { get; set; }
    public string? Size { get; set; }

    /// <summary>A "next page" link: there are more pages while it's there.</summary>
    public string? NextPage { get; set; }
}

/// <summary>A game's page.</summary>
public sealed class PageRecipe
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Summary { get; set; }
    public string? Cover { get; set; }
    public string? Screenshots { get; set; }

    /// <summary>Trailer videos (YouTube / Vimeo embeds or links, video files).</summary>
    public string? Trailers { get; set; }

    public string? Genres { get; set; }
    public string? Tags { get; set; }
    public string? Developer { get; set; }
    public string? Publisher { get; set; }
    public string? Version { get; set; }
    public string? Size { get; set; }
    public string? ReleaseDate { get; set; }
    public string? Updated { get; set; }

    /// <summary>Download links (file hosts, direct files, magnets, torrents). Replaces what the generic reader found.</summary>
    public string? Downloads { get; set; }

    /// <summary>
    /// For pages with one line of parts per provider ("Link Gofile: Part 1 – Part 2 – Part 3"): CSS for the label that starts
    /// each line ("b.uk-heading-bullet"). Each provider's parts are one set that is taken whole or not at all: a part that is
    /// plain text, has no link or no href rejects that provider. Parts of a set are not also read by <see cref="Downloads"/>.
    /// </summary>
    public string? DownloadSets { get; set; }

    /// <summary>
    /// The download's address worked out from the game page's own address, for sites whose Download button only adds to it
    /// (a homebrew ROM site's "…/some-game" → "…/some-game/download"). Either what's added ("/download", "/download/",
    /// "?download=1") or a pattern: <c>{url}</c> the page's address, <c>{slug}</c> its last part, <c>{id}</c> its last number
    /// ("{url}/download", "/get/{id}"). Comes first among the downloads; the <see cref="Downloads"/> links stay too.
    /// </summary>
    public string? DownloadUrl { get; set; }

    /// <summary>
    /// The download starts from the game page's own Download button, and only from there: the site checks you came from the
    /// page (a session: "Invalid session … find the game you are trying to download"), then counts down and starts the file.
    /// SeaRaider's download browser opens the game's page, presses Download, waits out the countdown and saves the file.
    /// </summary>
    public bool? DownloadByButton { get; set; }

    /// <summary>Things to leave out of the description (ads, share buttons, "related games").</summary>
    public string? Remove { get; set; }
}

/// <summary>What a reader found on a page (the reader test in Sources shows it).</summary>
public sealed record SiteReaderTestResult(string Reader, GameMetadata? Game, IReadOnlyList<GameMetadata> Listed, IReadOnlyList<string> Notes);

/// <summary>Loads <see cref="SiteReader"/> files and finds the one for a site.</summary>
public sealed class SiteReaderCatalog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly ILogger<SiteReaderCatalog> _logger;
    private volatile IReadOnlyList<SiteReader> _readers = [];

    public SiteReaderCatalog(ILogger<SiteReaderCatalog> logger)
    {
        _logger = logger;
        Reload();
    }

    /// <summary>The folders read: next to SeaRaider.exe (shipped with it) and the user's own.</summary>
    public static IReadOnlyList<string> Folders { get; } =
    [
        Path.Combine(AppContext.BaseDirectory, "readers"),
        Path.Combine(AppPaths.DataDirectory, "readers"),
    ];

    /// <summary>The user's folder (where new readers go).</summary>
    public static string UserFolder => Folders[1];

    public IReadOnlyList<SiteReader> Readers => _readers;

    /// <summary>Files that couldn't be read, with why.</summary>
    public IReadOnlyList<string> Problems { get; private set; } = [];

    /// <summary>Reads every *.json in the folders again (a later file for the same domain wins: yours over the shipped one).</summary>
    public void Reload()
    {
        var readers = new List<SiteReader>();
        var problems = new List<string>();
        foreach (var folder in Folders.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var reader = JsonSerializer.Deserialize<SiteReader>(File.ReadAllText(file), Json);
                    if (reader is null || reader.Domains.Count == 0)
                    {
                        problems.Add($"{Path.GetFileName(file)}: it needs \"domains\".");
                        continue;
                    }
                    if (reader.Listing is { Item.Length: 0 })
                        problems.Add($"{Path.GetFileName(file)}: \"listing\" needs \"item\"; lists won't be read.");
                    reader.FilePath = file;
                    if (reader.Name.Length == 0) reader.Name = reader.Domains[0];
                    readers.RemoveAll(r => r.Domains.Intersect(reader.Domains, StringComparer.OrdinalIgnoreCase).Any());
                    readers.Add(reader);
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    problems.Add($"{Path.GetFileName(file)}: {ex.Message}");
                    _logger.LogWarning(ex, "Site reader {File} couldn't be read", file);
                }
            }
        }
        _readers = readers;
        Problems = problems;
        _logger.LogInformation("{Count} site readers loaded", readers.Count);
    }

    /// <summary>Creates the user's readers folder with the guide and a sample (left alone when already there).</summary>
    public static string EnsureUserFolder()
    {
        Directory.CreateDirectory(UserFolder);
        var guide = Path.Combine(UserFolder, "README.txt");
        if (!File.Exists(guide)) File.WriteAllText(guide, Guide);
        var sample = Path.Combine(UserFolder, "example-site.json.sample");
        if (!File.Exists(sample)) File.WriteAllText(sample, Sample);
        return UserFolder;
    }

    /// <summary>Starts a new reader file for a site (from the sample, with its domain filled in) and returns its path.</summary>
    public static string CreateReaderFile(string domain)
    {
        EnsureUserFolder();
        domain = domain.Trim().ToLowerInvariant();
        if (domain.StartsWith("www.", StringComparison.Ordinal)) domain = domain[4..];
        var name = string.Concat(domain.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '-'));
        var path = Path.Combine(UserFolder, name + ".json");
        if (!File.Exists(path))
        {
            // Lists stay with SeaRaider's own reader until their addresses are filled in (a made-up address would
            // break this site's browsing and search meanwhile).
            var text = Sample
                .Replace("\"browseUrl\": \"https://example.com/games?page={page}\",", "\"browseUrl\": null,   // e.g. \"https://example.com/games?page={page}\"", StringComparison.Ordinal)
                .Replace("\"searchUrl\": \"https://example.com/search?q={query}&page={page}\",", "\"searchUrl\": null,   // e.g. \"https://example.com/search?q={query}&page={page}\"", StringComparison.Ordinal)
                .Replace("// A SeaRaider site reader. Rename to <something>.json (the .sample ending keeps it switched off).", "// SeaRaider site reader. See README.txt in this folder.", StringComparison.Ordinal)
                .Replace("example.com", domain, StringComparison.Ordinal)
                .Replace("\"Example Games\"", $"\"{domain}\"", StringComparison.Ordinal);
            File.WriteAllText(path, text);
        }
        return path;
    }

    /// <summary>
    /// Writes what <see cref="SiteReaderTeacher"/> learned into the site's reader file: a new file in the user's folder, or
    /// the existing reader with the learned selectors put on top (its other fields stay; the old file is kept as .bak).
    /// Returns the file's path.
    /// </summary>
    public string SaveLesson(SiteReaderLesson lesson)
    {
        if (lesson.Reader.Domains.Count == 0) throw new ArgumentException("The lesson has no site.");
        if (lesson.Reader.Page is null && lesson.Reader.Listing is null && string.IsNullOrWhiteSpace(lesson.Reader.BrowseUrl))
            throw new ArgumentException("Nothing was learned from that page, so there is nothing to save.");

        EnsureUserFolder();
        var domain = lesson.Reader.Domains[0];
        var existing = _readers.FirstOrDefault(r => r.Covers(domain));

        var reader = existing is null ? lesson.Reader : SiteReaderTeacher.Merge(existing, lesson.Reader);
        var inUserFolder = existing is not null && !string.IsNullOrEmpty(existing.FilePath) &&
                           Path.GetFullPath(existing.FilePath).StartsWith(Path.GetFullPath(UserFolder), StringComparison.OrdinalIgnoreCase);
        var path = inUserFolder
            ? existing!.FilePath
            : Path.Combine(UserFolder, string.Concat(domain.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '-')) + ".json");

        if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
        var suggestions = lesson.Notes.Where(n => n.StartsWith("Suggested", StringComparison.Ordinal));
        File.WriteAllText(path, SiteReaderTeacher.ToJson(reader, suggestions));
        Reload();
        return path;
    }

    /// <summary>Writes a reader changed in SeaRaider (Site readers › a reader's settings) back to its file; a reader that
    /// shipped next to SeaRaider.exe is written to the user's readers folder instead. Returns the file.</summary>
    public string SaveReader(SiteReader reader)
    {
        EnsureUserFolder();
        var inUserFolder = !string.IsNullOrEmpty(reader.FilePath) &&
                           Path.GetFullPath(reader.FilePath).StartsWith(Path.GetFullPath(UserFolder), StringComparison.OrdinalIgnoreCase);
        var domain = reader.Domains.FirstOrDefault() ?? reader.Name;
        var path = inUserFolder
            ? reader.FilePath
            : Path.Combine(UserFolder, string.Concat(domain.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '-')) + ".json");
        if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
        File.WriteAllText(path, SiteReaderTeacher.ToJson(reader, []));
        Reload();
        return path;
    }

    public const string Sample = """
        {
          // A SeaRaider site reader. Rename to <something>.json (the .sample ending keeps it switched off).
          // Selectors are CSS. "selector@attribute" reads an attribute. Several tries: "h1.title || h1".
          // Leave out (or delete) anything the site doesn't have: SeaRaider's own reader fills the rest.
          "name": "Example Games",
          "domains": ["example.com"],

          // The list of games, and the search. {page} = 1, 2, 3 …   {query} = what was typed.
          "browseUrl": "https://example.com/games?page={page}",
          "searchUrl": "https://example.com/search?q={query}&page={page}",

          "listing": {
            "item": ".game-card",            // one game in the list
            "link": "a.game-link",           // its page (default: first link in the item)
            "title": ".game-title",
            "cover": "img",                  // pictures: src / data-src / srcset are found by themselves
            "summary": ".excerpt",
            "developer": ".author",
            "genre": ".genre",
            "date": "time@datetime",
          // "@owntext": only the element's own words, not what's inside it (a title with a little info line inside)
            "nextPage": "a.next"             // there are more pages while this exists
          },

          "page": {
            "title": "h1",
            "description": ".game-description",
            "remove": ".share-buttons || .related",   // left out of the description
            "summary": "meta[property='og:description']@content",
            "cover": "meta[property='og:image']@content || .cover img",
            "screenshots": ".screenshots a || .screenshots img",
            "trailers": ".trailer iframe",
            "genres": ".genres a",
            "tags": ".tags a",
            "developer": ".developer",
            "publisher": ".publisher",
            "version": ".version",
            "size": ".file-size",
            "releaseDate": ".release-date",
            "updated": "time.updated@datetime",
            "downloads": ".download-buttons a",  // file hosts (Pixeldrain, MediaFire …), direct files, magnets
            // the Download button only adds to the page's address ("…/game" → "…/game/download"): what it adds,
            // or a pattern with {url} (the page), {slug} (its last part), {id} (its last number)
            "downloadUrl": "/download",
            // or: the site only lets you download from the game page's Download button (a session check, a countdown)
            // "downloadByButton": true
          },

          // true: the site's pictures are the game's own (no Steam / IGDB lookup). false: look them up.
          "useOwnPictures": true
        }
        """;

    public const string Guide = """
        SeaRaider site readers
        ======================

        A reader teaches SeaRaider one site: where its games are listed and where each part of a game's page is.
        It's a .json file in this folder (or in a "readers" folder next to SeaRaider.exe, for readers you ship).

        1. Sources > Site readers > New reader, type the site's domain. A file opens with every field explained.
        2. Open a game's page on the site in your browser, right-click a part (the title, the description, a download
           button) > Inspect, and note its class or id. Put that in the file as a CSS selector: ".game-title", "#download a".
        3. Save, then Sources > Site readers > Test with the game's page (and the site's list of games). It shows what
           the reader found, and which selectors matched nothing.
        4. Add the site in Sources if it isn't there yet. Its search, browsing and game pages now go through the reader.

        Selectors
          ".class", "#id", "h1", "div.info > span"     any CSS
          "img@data-src", "time@datetime"              an attribute instead of the text
          "h2.title@owntext"                           only the element's own text, not the bits inside it
          "h1.title || h1"                             try in order, first that matches wins
        Pictures and links find their address by themselves (href, src, data-src, srcset, content).
        Fields left out are filled by SeaRaider's generic reader, as before.

        "downloads" replaces the download options the generic reader found with exactly the links it selects: file hosts
        SeaRaider knows download inside the app (Pixeldrain, MediaFire, Buzzheavier …), others open in the browser.
        """;

    public SiteReader? Find(string? host) => _readers.FirstOrDefault(r => r.Covers(host));

    public SiteReader? Find(Uri? uri) => Find(uri?.Host);

    public SiteReader? Find(GameSource source) =>
        Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) ? Find(uri) : null;
}

/// <summary>Applies a <see cref="SiteReader"/> to pages.</summary>
public static partial class SiteReaderParser
{
    [GeneratedRegex(@"^(?<css>.+?)@(?<attr>[A-Za-z_:][-A-Za-z0-9_:.]*)$")]
    private static partial Regex AttrRegex();

    [GeneratedRegex(@"[ \t\r\f\v]+")]
    private static partial Regex SpacesRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLinesRegex();

    /// <summary>The browse page <paramref name="page"/>, or the search for <paramref name="query"/>, or null when the reader has no such address.</summary>
    public static Uri? BuildUrl(SiteReader reader, string? query, int page)
    {
        var template = string.IsNullOrWhiteSpace(query) ? reader.BrowseUrl : reader.SearchUrl;
        if (string.IsNullOrWhiteSpace(template)) return null;
        var url = template
            .Replace("{query}", Uri.EscapeDataString(query ?? string.Empty), StringComparison.OrdinalIgnoreCase)
            .Replace("{page}", Math.Max(1, page).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null;
    }

    // =============================================================================================
    // Lists
    // =============================================================================================

    /// <summary>
    /// A game page's address → its download's address, from <see cref="PageRecipe.DownloadUrl"/>: "/download" is added to the
    /// page's address; {url}, {slug} and {id} are filled in. Null when no web address comes out.
    /// </summary>
    public static Uri? DownloadFromPage(string template, Uri pageUri)
    {
        var text = template.Trim();
        if (text.Length == 0) return null;
        var page = pageUri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        var slug = pageUri.AbsolutePath.TrimEnd('/').Split('/').LastOrDefault() ?? string.Empty;
        var id = Regex.Match(pageUri.AbsolutePath, @"\d+(?!.*\d)").Value;
        string url;
        if (text.Contains('{'))
        {
            if ((text.Contains("{slug}", StringComparison.OrdinalIgnoreCase) && slug.Length == 0) ||
                (text.Contains("{id}", StringComparison.OrdinalIgnoreCase) && id.Length == 0)) return null;
            url = text.Replace("{url}", page, StringComparison.OrdinalIgnoreCase)
                .Replace("{slug}", slug, StringComparison.OrdinalIgnoreCase)
                .Replace("{id}", id, StringComparison.OrdinalIgnoreCase);
            if (url.StartsWith('/')) url = new Uri(pageUri, url).AbsoluteUri; // "/get/{id}": on the same site
        }
        else if (text.Contains("://", StringComparison.Ordinal)) return null; // a fixed address isn't one per game
        else url = page + (text.StartsWith('/') || text.StartsWith('?') || text.StartsWith('#') ? text : "/" + text);
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri : null;
    }

    /// <summary>
    /// A <see cref="PageRecipe.DownloadUrl"/> that leads to a page (not straight to the file): that page is read and the file
    /// links on it (or the file host it sends to) become the game's downloads. Returns a note, or null when nothing changed.
    /// </summary>
    public static async Task<string?> FollowDownloadUrlAsync(HttpClient client, string userAgent, SiteReader reader, GameMetadata game,
        Uri pageUri, CancellationToken ct)
    {
        if (reader.Page?.DownloadByButton == true) return null; // the browser goes through the page itself
        if (reader.Page?.DownloadUrl is not { Length: > 0 } template || DownloadFromPage(template, pageUri) is not { } url) return null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            request.Headers.Referrer = pageUri;
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return $"downloadUrl: {url} answered {(int)response.StatusCode}";
            if (response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) != true) return null; // the file itself

            var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var final = response.RequestMessage?.RequestUri ?? url;
            using var doc = await GamePageParser.ParseHtmlAsync(html, final, ct).ConfigureAwait(false);
            var found = new List<DownloadLink>();
            var targets = doc.QuerySelectorAll("a[href]").Select(a => (Href: a.GetAttribute("href"), Text: Clean(a.TextContent))).ToList();
            if (doc.QuerySelector("meta[http-equiv='refresh' i]")?.GetAttribute("content") is { } refresh &&
                Regex.Match(refresh, @"url\s*=\s*['""]?(?<u>[^'""]+)", RegexOptions.IgnoreCase) is { Success: true } r)
                targets.Insert(0, (r.Groups["u"].Value, "Download"));
            foreach (var (href, text) in targets)
            {
                if (string.IsNullOrWhiteSpace(href) || !Uri.TryCreate(final, href, out var target)) continue;
                if (target.AbsoluteUri.Equals(pageUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase)) continue;
                if (LinkClassifier.Classify(target, text) is { } link && !HostPolicy.IsBlocked(link) &&
                    found.All(f => !f.Url.Equals(link.Url, StringComparison.OrdinalIgnoreCase)))
                    found.Add(link);
            }
            if (found.Count == 0) return $"downloadUrl: {url} is a page with no file link on it";
            if (game.SizeText is { } size) foreach (var link in found) link.SizeText ??= size;
            game.DownloadLinks = LinkClassifier.NormalizeGroups(found.Concat(game.DownloadLinks.Where(l =>
                !l.Url.Equals(url.AbsoluteUri, StringComparison.OrdinalIgnoreCase))));
            return $"downloadUrl: {url} is a page; took {found.Count} file link(s) from it";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return $"downloadUrl: couldn't open {url} ({ex.Message})";
        }
    }

    /// <summary>The games on a browse or search page (stubs; each page is read when the game is opened).</summary>
    public static (List<GameMetadata> Games, bool HasMore) ParseListing(SiteReader reader, IDocument doc, Uri pageUri, GameSource source)
    {
        var games = new List<GameMetadata>();
        if (reader.Listing is not { Item.Length: > 0 } recipe) return (games, false);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in SelectAll(doc, recipe.Item))
        {
            var link = Url(item, recipe.Link ?? "a[href]", pageUri) ?? (item.LocalName == "a" ? Resolve(item.GetAttribute("href"), pageUri) : null);
            if (link is null || !seen.Add(link)) continue;

            var title = Text(item, recipe.Title) ?? Text(item, recipe.Link ?? "a[href]") ?? string.Empty;
            if (title.Length < 2) continue;

            var summary = Text(item, recipe.Summary) ?? string.Empty;
            var genre = Text(item, recipe.Genre);
            var date = Text(item, recipe.Date);
            games.Add(new GameMetadata
            {
                Id = GameMetadata.CreateId(link),
                SourceId = source.Id,
                PageUrl = link,
                Title = title,
                RawTitle = title,
                CoverImageUrl = Url(item, recipe.Cover ?? "img", pageUri),
                ShortDescription = summary.Length > 220 ? summary[..220].TrimEnd() + "…" : summary,
                Developer = Text(item, recipe.Developer),
                Genres = genre is null ? [] : [genre],
                SizeText = SizeOf(Text(item, recipe.Size)),
                UploadedAtUtc = date is null ? null : MetadataParser.ParseDate(date),
                DetailsLoaded = false,
                MetadataVersion = GameMetadata.CurrentMetadataVersion,
                FetchedAtUtc = DateTime.UtcNow,
            }.Normalize());
        }

        // A "next page" link says so; without one, a numbered address ("…/page/{page}/") goes on while pages have games.
        var hasMore = recipe.NextPage is not null
            ? Url(doc, recipe.NextPage, pageUri) is not null
            : games.Count > 0 && reader.BrowseUrl?.Contains("{page}", StringComparison.OrdinalIgnoreCase) == true;
        return (games, hasMore);
    }

    // =============================================================================================
    // Game pages
    // =============================================================================================

    /// <summary>
    /// Fills a game from its page on top of what the generic reader found: every field the reader names and finds
    /// replaces the generic guess; fields it doesn't name (or doesn't find) are left as they were.
    /// </summary>
    public static List<string> EnrichFromGamePage(SiteReader reader, IDocument doc, Uri pageUri, GameMetadata game)
    {
        var notes = new List<string>();
        if (reader.Page is not { } recipe) return notes;

        void Note(string field, bool found, string? spec)
        {
            if (spec is not null && !found) notes.Add($"{field}: nothing matched \"{spec}\"");
        }

        if (Text(doc, recipe.Title) is { } title)
        {
            game.Title = title;
            game.RawTitle = title;
        }
        Note("title", game.Title.Length > 0, recipe.Title);

        if (recipe.Description is not null)
        {
            var description = Description(doc, recipe.Description, recipe.Remove);
            if (description.Length > 0) game.Description = description;
            Note("description", description.Length > 0, recipe.Description);
        }
        if (Text(doc, recipe.Summary) is { } summary) game.ShortDescription = summary.Length > 220 ? summary[..220].TrimEnd() + "…" : summary;
        else if (game.ShortDescription.Length == 0 && game.Description.Length > 0)
            game.ShortDescription = game.Description.Length > 220 ? game.Description[..220].TrimEnd() + "…" : game.Description;

        if (Url(doc, recipe.Cover, pageUri) is { } cover) game.CoverImageUrl = cover;
        Note("cover", game.CoverImageUrl is not null, recipe.Cover);

        var shots = Urls(doc, recipe.Screenshots, pageUri).Where(IsImage).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToList();
        if (shots.Count > 0) game.ScreenshotUrls = shots;
        Note("screenshots", shots.Count > 0, recipe.Screenshots);

        var trailers = Urls(doc, recipe.Trailers, pageUri).Select(MediaUrlHelper.NormalizeVideoUrl).OfType<string>().Distinct().Take(4).ToList();
        if (trailers.Count > 0)
        {
            game.TrailerUrls = trailers;
            game.TrailerThumbnailUrls = trailers.Select(MediaUrlHelper.GetThumbnailUrl).ToList();
        }
        Note("trailers", trailers.Count > 0, recipe.Trailers);

        var genres = Texts(doc, recipe.Genres).Take(6).ToList();
        if (genres.Count > 0) game.Genres = genres;
        var tags = Texts(doc, recipe.Tags).Take(10).ToList();
        if (tags.Count > 0) game.Tags = tags;
        if (Text(doc, recipe.Developer) is { } developer) game.Developer = developer;
        if (Text(doc, recipe.Publisher) is { } publisher) game.Publisher = publisher;
        if (Text(doc, recipe.Version) is { } version) game.Version = version;
        if (SizeOf(Text(doc, recipe.Size)) is { } size) game.SizeText = size;
        if (Text(doc, recipe.ReleaseDate) is { } released)
        {
            game.ReleaseDate = released;
            game.ReleaseDateUtc = MetadataParser.ParseDate(released) ?? game.ReleaseDateUtc;
        }
        if (Text(doc, recipe.Updated) is { } updated && MetadataParser.ParseDate(updated) is { } when) game.UploadedAtUtc = when;

        if (recipe.Downloads is not null || recipe.DownloadSets is not null)
        {
            var links = new List<DownloadLink>();

            // Provider lines first: complete sets only (the rejected ones are named in the notes).
            var sets = recipe.DownloadSets is null ? PartSetResult.Empty : PartSetParser.Parse(doc, pageUri, recipe.DownloadSets);
            notes.AddRange(sets.Rejections);
            links.AddRange(sets.ToDownloadLinks());
            var setLinks = links.Count;

            foreach (var (css, attr) in Specs(recipe.Downloads))
            {
                foreach (var element in SelectAll(doc, css))
                {
                    if (sets.Claims(element)) continue; // a provider line's part: judged as a set, never alone
                    var url = UrlOf(element, attr, pageUri);
                    if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
                    // Links the reader points at are downloads, whatever they look like.
                    if (LinkClassifier.Classify(uri, Clean(element.TextContent), forceDownload: true) is { } link)
                    {
                        if (HostPolicy.IsBlocked(link)) continue; // MixDrop & co.
                        if (FileHosts.IsLinkShortener(uri.Host)) MirrorLabels.Apply(link, MirrorLabels.ContextOf(element), []);
                        links.Add(link);
                    }
                }
                if (links.Count > setLinks) break;
            }
            MirrorLabels.MakeDistinct(links.Where(l => FileHosts.IsLinkShortener(l.Host) && l.Kind != DownloadLinkKind.MultipartArchivePart).ToList());
            if (links.Count > 0)
            {
                game.DownloadLinks = LinkClassifier.NormalizeGroups(links);
                if (game.SizeText is { } knownSize) foreach (var link in game.DownloadLinks) link.SizeText ??= knownSize;
            }
            Note("downloads", links.Count > 0, recipe.Downloads ?? recipe.DownloadSets);
        }

        if (recipe.DownloadByButton == true)
        {
            // The game's page is the download: the browser presses its Download button.
            if (LinkClassifier.Classify(pageUri, "Download", forceDownload: true) is { } link)
            {
                link.SizeText ??= game.SizeText;
                link.Mirror = "Download button";
                game.DownloadLinks = [link];
            }
        }
        else if (!string.IsNullOrWhiteSpace(recipe.DownloadUrl))
        {
            if (DownloadFromPage(recipe.DownloadUrl, pageUri) is { } addressed &&
                LinkClassifier.Classify(addressed, "Download", forceDownload: true) is { } link && !HostPolicy.IsBlocked(link))
            {
                link.SizeText ??= game.SizeText;
                // The reader's own download selectors' links stay; the generic reader's guesses are replaced.
                var others = recipe.Downloads is not null || recipe.DownloadSets is not null
                    ? game.DownloadLinks.Where(l => !string.Equals(l.Url, link.Url, StringComparison.OrdinalIgnoreCase))
                    : Enumerable.Empty<DownloadLink>();
                game.DownloadLinks = LinkClassifier.NormalizeGroups(new[] { link }.Concat(others));
            }
            else notes.Add($"downloadUrl: \"{recipe.DownloadUrl}\" doesn't make an address from {pageUri.AbsoluteUri}");
        }

        if (reader.UseOwnPictures)
        {
            game.MediaChecked = true;
            game.MediaVersion = GameMetadata.CurrentMediaVersion;
            game.MediaSource = reader.Name;
        }
        game.DetailsLoaded = true;
        game.MetadataVersion = GameMetadata.CurrentMetadataVersion;
        game.Normalize();
        return notes;
    }

    // =============================================================================================
    // Selectors
    // =============================================================================================

    /// <summary>"css@attr || css2" → (css, attr) pairs.</summary>
    private static IEnumerable<(string Css, string? Attr)> Specs(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) yield break;
        foreach (var part in spec.Split("||", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = AttrRegex().Match(part);
            yield return match.Success ? (match.Groups["css"].Value.Trim(), match.Groups["attr"].Value) : (part, null);
        }
    }

    private static IEnumerable<IElement> SelectAll(IParentNode root, string css)
    {
        try { return root.QuerySelectorAll(css).ToList(); }
        catch (DomException) { return []; } // a selector AngleSharp doesn't understand
    }

    /// <summary>The first non-empty text (or attribute) the selectors find.</summary>
    public static string? Text(IParentNode root, string? spec)
    {
        foreach (var (css, attr) in Specs(spec))
        {
            foreach (var element in SelectAll(root, css))
            {
                var value = Clean(ValueOf(element, attr));
                if (value.Length > 0) return value;
            }
        }
        return null;
    }

    /// <summary>
    /// An element's text, or an attribute. Two special names: <c>@owntext</c> is only the element's own text, without
    /// what's inside its child elements (a title with a "2026 | 1.2 GB" line inside it); <c>@text</c> is all of it.
    /// </summary>
    private static string? ValueOf(IElement element, string? attr) => attr?.ToLowerInvariant() switch
    {
        null or "text" => element.TextContent,
        "owntext" => string.Concat(element.ChildNodes.OfType<IText>().Select(t => t.Data)),
        _ => element.GetAttribute(attr),
    };

    /// <summary>The size in a line like "2026 | 1.2 GB" ("1.2 GB"); the whole line when there's no size in it.</summary>
    private static string? SizeOf(string? text)
    {
        if (text is null) return null;
        var match = SizeRegex().Match(text);
        return match.Success ? match.Value.Trim() : text;
    }

    [GeneratedRegex(@"\d+(?:[.,]\d+)?\s?(?:TB|GB|MB|KB|GiB|MiB)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SizeRegex();

    /// <summary>Every non-empty text the first matching selector finds.</summary>
    public static List<string> Texts(IParentNode root, string? spec)
    {
        foreach (var (css, attr) in Specs(spec))
        {
            var values = SelectAll(root, css)
                .Select(e => Clean(ValueOf(e, attr)))
                .Where(v => v.Length > 0 && v.Length <= 60)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (values.Count > 0) return values;
        }
        return [];
    }

    public static string? Url(IParentNode root, string? spec, Uri baseUri)
    {
        foreach (var (css, attr) in Specs(spec))
            foreach (var element in SelectAll(root, css))
                if (UrlOf(element, attr, baseUri) is { } url) return url;
        return null;
    }

    public static List<string> Urls(IParentNode root, string? spec, Uri baseUri)
    {
        foreach (var (css, attr) in Specs(spec))
        {
            var urls = SelectAll(root, css).Select(e => UrlOf(e, attr, baseUri)).OfType<string>().ToList();
            if (urls.Count > 0) return urls;
        }
        return [];
    }

    /// <summary>An element's address: the named attribute, else the one that usually holds it.</summary>
    private static string? UrlOf(IElement element, string? attr, Uri baseUri)
    {
        if (attr is not null) return Resolve(FirstOfSrcset(attr, element.GetAttribute(attr)), baseUri);
        string[] candidates = element.LocalName switch
        {
            "a" or "link" => ["href", "data-href", "data-url", "data-link", "data-download"],
            "img" => ["data-src", "data-lazy-src", "data-original", "data-srcset", "srcset", "src"],
            "meta" => ["content"],
            "source" or "video" or "iframe" or "embed" => ["src", "data-src"],
            "form" => ["action"],
            "button" or "input" => ["formaction", "data-href", "data-url", "data-link", "data-download"],
            _ => ["href", "data-href", "data-url", "data-link", "data-download", "data-src", "src", "data-bg", "content"],
        };
        foreach (var name in candidates)
            if (Resolve(FirstOfSrcset(name, element.GetAttribute(name)), baseUri) is { } url) return url;

        // A download button with no link of its own: "onclick=location.href='…'" / "window.open('…')", or the form it sends.
        if (element.GetAttribute("onclick") is { Length: > 0 } onclick && OnClickUrlRegex().Match(onclick) is { Success: true } click &&
            Resolve(click.Groups["u"].Value, baseUri) is { } clicked)
            return clicked;
        if (element.LocalName is "button" or "input" && element.Closest("form") is { } form &&
            Resolve(form.GetAttribute("action"), baseUri) is { } sent)
            return sent;

        // A wrapper around the real link or picture.
        var inner = element.QuerySelector("a[href], img");
        return inner is null ? null : UrlOf(inner, null, baseUri);
    }

    [GeneratedRegex(@"(?:location(?:\.href)?|window\.open|open)\s*(?:=|\()\s*['""](?<u>[^'""]+)['""]", RegexOptions.IgnoreCase)]
    private static partial Regex OnClickUrlRegex();

    /// <summary>The address an element leads to (a link, a picture, a download button or its form), or null.</summary>
    public static string? UrlOfElement(IElement element, Uri baseUri) => UrlOf(element, null, baseUri);

    /// <summary>"a.jpg 1x, b.jpg 2x" → the biggest; anything else as is.</summary>
    private static string? FirstOfSrcset(string attribute, string? value)
    {
        if (value is null || !attribute.Contains("srcset", StringComparison.OrdinalIgnoreCase)) return value;
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => c.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0])
            .LastOrDefault();
    }

    /// <summary>The description as paragraphs, without what <paramref name="remove"/> names.</summary>
    private static string Description(IDocument doc, string spec, string? remove)
    {
        foreach (var (css, attr) in Specs(spec))
        {
            var element = SelectAll(doc, css).FirstOrDefault();
            if (element is null) continue;
            if (attr is not null) return Clean(element.GetAttribute(attr));

            var copy = (IElement)element.Clone(deep: true);
            foreach (var (removeCss, _) in Specs(remove))
                foreach (var unwanted in SelectAll(copy, removeCss)) unwanted.Remove();
            foreach (var junk in SelectAll(copy, "script, style, noscript, iframe, form, button")) junk.Remove();
            foreach (var br in SelectAll(copy, "br")) br.Replace(doc.CreateTextNode("\n"));
            foreach (var block in SelectAll(copy, "p, div, li, h1, h2, h3, h4, h5, h6, tr")) block.Append(doc.CreateTextNode("\n"));

            var text = SpacesRegex().Replace(WebUtility.HtmlDecode(copy.TextContent), " ");
            text = string.Join('\n', text.Split('\n').Select(l => l.Trim()));
            text = BlankLinesRegex().Replace(text, "\n\n").Trim();
            if (text.Length > 0) return text.Length > 8000 ? text[..8000] : text;
        }
        return string.Empty;
    }

    private static string Clean(string? text) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty : SpacesRegex().Replace(WebUtility.HtmlDecode(text).Replace('\n', ' '), " ").Trim();

    private static string? Resolve(string? href, Uri baseUri) =>
        !string.IsNullOrWhiteSpace(href) && !href.StartsWith("data:", StringComparison.OrdinalIgnoreCase) &&
        Uri.TryCreate(baseUri, WebUtility.HtmlDecode(href.Trim()), out var uri) && uri.Scheme is "http" or "https" or "magnet"
            ? uri.AbsoluteUri
            : null;

    private static bool IsImage(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        (u.AbsolutePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || u.AbsolutePath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
         u.AbsolutePath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) || u.AbsolutePath.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
         u.AbsolutePath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) || u.Query.Contains("format=", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Tries a reader on one page (Sources › Site readers › Test), without saving anything.</summary>
public sealed class SiteReaderTester(IHttpClientFactory httpClientFactory, GamePageParser parser, SiteReaderCatalog readers,
    Abstractions.ILibraryRepository repository, Catalog.RomInfoService romInfo)
{
    /// <exception cref="ArgumentException">Not a web address, or no reader covers its site.</exception>
    public async Task<SiteReaderTestResult> TestAsync(string url, CancellationToken ct = default)
    {
        url = (url ?? string.Empty).Trim();
        if (!url.Contains("://", StringComparison.Ordinal)) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Paste the address of a page on the site: a game's page, or its list of games.");
        readers.Reload(); // pick up the file as it is now
        var reader = readers.Find(uri)
                     ?? throw new ArgumentException($"No reader covers {uri.Host}. Add its domain to a reader's \"domains\", or press Reload after saving the file.");

        var client = httpClientFactory.CreateClient(HttpClientNames.Scraper);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", repository.GetSettings().UserAgent);
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var finalUri = response.RequestMessage?.RequestUri ?? uri;
        using var doc = await GamePageParser.ParseHtmlAsync(html, finalUri, ct).ConfigureAwait(false);

        var notes = new List<string>();
        var source = new GameSource { Url = $"{finalUri.Scheme}://{finalUri.Host}/" };

        var listed = new List<GameMetadata>();
        if (reader.Listing is not null)
        {
            (listed, var more) = SiteReaderParser.ParseListing(reader, doc, finalUri, source);
            notes.Add(listed.Count == 0
                ? $"listing: no games found with item \"{reader.Listing.Item}\" (fine if this is a game's page)"
                : $"listing: {listed.Count} games{(more ? ", and a next page" : string.Empty)}");
        }

        GameMetadata? game = null;
        if (reader.Page is not null)
        {
            game = parser.ExtractGame(doc, finalUri, source, []);
            notes.AddRange(SiteReaderParser.EnrichFromGamePage(reader, doc, finalUri, game).Select(n => "page: " + n));
            if (await SiteReaderParser.FollowDownloadUrlAsync(client, repository.GetSettings().UserAgent, reader, game, finalUri, ct)
                    .ConfigureAwait(false) is { } followed)
                notes.Add("page: " + followed);
            // A ROM: what the store will show, with its information from the web.
            if (repository.GetSettings().RomInfoFromWeb && Catalog.RomInfoService.SystemOf(game) is not null)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    notes.AddRange((await romInfo.EnrichAsync(game, timeout.Token).ConfigureAwait(false)).Select(n => "web: " + n));
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    notes.Add("web: the ROM information look-up took too long");
                }
            }
        }
        return new SiteReaderTestResult(reader.Name, game, listed, notes);
    }
}
