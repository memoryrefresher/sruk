using System.Text.RegularExpressions;
using SeaRaider.Core.Models;
using SeaRaider.Core.WebChallenge;

namespace SeaRaider.Core.Scraping;

/// <summary>What making a reader from an address came to.</summary>
/// <param name="Path">The reader file it was saved to.</param>
/// <param name="TestUrl">A page to try the reader on (a game's page if one was learned, else the list).</param>
/// <param name="Log">What it did, step by step.</param>
public sealed record AutoReaderResult(string Path, string Name, string Domain, bool HasList, bool HasPage, string? BrowseUrl,
    string? SearchUrl, string TestUrl, IReadOnlyList<LearnedField> Fields, IReadOnlyList<string> Log)
{
    /// <summary>The game page it learned from (to teach it again with the user's pointers), or null.</summary>
    public (Uri Url, string Html)? GamePage { get; init; }

    /// <summary>The list page it learned from, or null.</summary>
    public (Uri Url, string Html)? ListPage { get; init; }
}

/// <summary>
/// Makes a site reader from nothing but an address: reads the page (in SeaRaider's browser when the site has a bot check or
/// builds its pages with script), teaches the reader from it the same way a pasted page does (<see cref="SiteReaderTeacher"/>),
/// then goes looking for the rest by itself: the site's list of games if it was given a game's page, a game's page from the
/// list if it was given the list, the list's page 2 addresses (checked), and the site's search (checked).
/// </summary>
public sealed class SiteReaderAutoBuilder(IHttpClientFactory httpClientFactory, SiteReaderCatalog catalog,
    Abstractions.ILibraryRepository repository, IChallengeResolver challenge, GamePageParser parser)
{
    private static readonly RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>Bot-check pages (Cloudflare, DDoS-Guard, Sucuri), not the site.</summary>
    private static readonly Regex Blocked = new(
        @"<title>\s*(Just a moment|Attention Required|DDoS-Guard|Checking your browser|Access denied|Please wait)|cf_chl_opt|cf-browser-verification|sucuri-cloudproxy",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Menu links that usually lead to the whole list of games.</summary>
    private static readonly Regex ListLinkWords = new(
        @"^\s*(all\s*games|games|game\s*list|pc\s*games|browse|catalog(ue)?|library|latest(\s*games|\s*uploads|\s*releases)?|new(\s*games|\s*releases)?|recent(ly\s*added)?|a\s*-\s*z(\s*list)?|repacks|downloads?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SuggestedBrowse = new(@"^Suggested ""browseUrl"".*?:\s*(\S+)\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex SuggestedSearch = new(@"^Suggested ""searchUrl"".*?:\s*(\S+)\s*$", RegexOptions.CultureInvariant);

    private sealed record Learned(Uri Url, string Html, SiteReaderLesson Lesson, bool ViaBrowser);

    /// <exception cref="ArgumentException">Not a web address.</exception>
    /// <exception cref="InvalidOperationException">The site couldn't be read, or nothing on it looked like games.</exception>
    public async Task<AutoReaderResult> BuildAsync(string address, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var log = new List<string>();
        void Say(string text)
        {
            log.Add(text);
            progress?.Report(text);
        }

        var start = Normalize(address);
        var home = new Uri($"{start.Scheme}://{start.Host}/");
        var source = new GameSource { Url = home.AbsoluteUri };

        Say($"Reading {start.AbsoluteUri} …");
        var first = await ReadAndLearnAsync(start, Say, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        $"Couldn't read {start.Host}. Check the address opens in your browser; if it does, use \"Teach it from a page\" below with the page's HTML.");

        Learned? page = first.Lesson.FoundPage ? first : null;
        Learned? list = page is null && first.Lesson.FoundListing ? first : null;
        Say(page is not null ? "That's a game's page: learned it." : list is not null ? "That's a list of games: learned it." : "That page isn't a game or a list of games.");

        // ---- the list of games -----------------------------------------------------------------
        // Given a game's page: its console's own list first (the breadcrumb "Home › Game Boy › …", the address one level up,
        // a link named after the console), then the home page and menu links. A list with only a few games (a "featured" or
        // "related" strip) isn't taken while a fuller one may be found.
        if (list is null)
        {
            var best = (Learned: (Learned?)null, Count: 0);
            var tried = 0;
            foreach (var candidate in await ListCandidatesAsync(first, home, ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                if (++tried > 7) break;
                Say($"Looking for the list of games at {candidate.AbsoluteUri} …");
                var learned = await ReadAndLearnAsync(candidate, Say, ct).ConfigureAwait(false);
                if (learned is { Lesson.FoundListing: true })
                {
                    var count = await CountListedAsync(learned, source, ct).ConfigureAwait(false);
                    Say($"  {count} games listed there.");
                    if (count > best.Count) best = (learned, count);
                    if (count >= 8) break; // a real list (it pages on to the rest)
                }
                // The home page was a game page (and none was found yet): good enough for the game pages.
                if (page is null && learned is { Lesson.FoundPage: true }) page = learned;
            }
            if (best.Learned is { } chosen)
            {
                list = chosen;
                Say($"Found the list of games at {chosen.Url.AbsoluteUri} ({best.Count} on its first page): learned it.");
            }
        }

        // ---- a game's page, from the list ------------------------------------------------------------
        if (page is null && list is not null)
        {
            using var listDoc = await GamePageParser.ParseHtmlAsync(list.Html, list.Url, ct).ConfigureAwait(false);
            var (games, _) = SiteReaderParser.ParseListing(list.Lesson.Reader, listDoc, list.Url, source);
            foreach (var game in games.Where(g => SameSite(g.PageUrl, home)).Take(3))
            {
                ct.ThrowIfCancellationRequested();
                Say($"Opening a game from the list: {game.Title} …");
                var learned = await ReadAndLearnAsync(new Uri(game.PageUrl), Say, ct).ConfigureAwait(false);
                if (learned is not { Lesson.FoundPage: true }) continue;
                page = learned;
                Say("Learned the game page.");
                break;
            }
            if (page is null) Say("Couldn't learn a game's page from the list (SeaRaider's own reader fills that part in).");
        }

        if (page is null && list is null)
            throw new InvalidOperationException(
                $"Nothing on {start.Host} looked like a list of games or a game's page. Try the address of one game's page, or of the site's list of games.");

        // One lesson per part: the list's lesson says nothing about game pages and the reverse.
        if (page is not null && list is not null && !ReferenceEquals(page, list))
        {
            page.Lesson.Reader.Listing = null;
            list.Lesson.Reader.Page = null;
        }

        var main = (list ?? page)!.Lesson.Reader;
        main.Name = await SiteNameAsync(first, start.Host, ct).ConfigureAwait(false);

        // ---- page 1, 2, 3 … --------------------------------------------------------------------------
        string? browse = null;
        if (list is not null)
        {
            browse = await FindPagesAsync(list, source, Say, ct).ConfigureAwait(false);
            if (browse is null)
            {
                browse = list.Url.AbsoluteUri; // only its first page, but the newest games are there
                Say("No numbered pages found: the store will show the list's first page.");
            }
            list.Lesson.Reader.BrowseUrl = browse;
        }

        // ---- search ----------------------------------------------------------------------------------
        string? search = null;
        if (list is not null)
        {
            search = await FindSearchAsync(list, home, source, Say, ct).ConfigureAwait(false);
            list.Lesson.Reader.SearchUrl = search;
        }

        // ---- the downloads: on the page, or behind its Download button -----------------------------------
        if (page is not null) await FindDownloadsAsync(page, source, Say, ct).ConfigureAwait(false);

        // ---- save --------------------------------------------------------------------------------------
        catalog.Reload();
        string path = string.Empty;
        if (page is not null) path = catalog.SaveLesson(page.Lesson);
        if (list is not null && !ReferenceEquals(list, page)) path = catalog.SaveLesson(list.Lesson);
        Say($"Saved the reader: {System.IO.Path.GetFileName(path)}.");

        var fields = new List<LearnedField>();
        if (list is not null) fields.AddRange(list.Lesson.Fields.Where(f => f.Area != "page"));
        if (page is not null) fields.AddRange(page.Lesson.Fields.Where(f => f.Area != "list" || ReferenceEquals(page, list)));
        if (page?.Lesson.Reader.Page?.DownloadUrl is { } downloadUrl) fields.Add(new LearnedField("page", "downloadUrl", downloadUrl, null));
        if (browse is not null) fields.Add(new LearnedField("site", "browseUrl", browse, null));
        if (search is not null) fields.Add(new LearnedField("site", "searchUrl", search, null));

        var domain = main.Domains.FirstOrDefault() ?? start.Host;
        return new AutoReaderResult(path, main.Name, domain, list is not null, page is not null, browse, search,
            (page ?? list)!.Url.AbsoluteUri, fields.DistinctBy(f => (f.Area, f.Field)).ToList(), log)
        {
            GamePage = page is null ? null : (page.Url, page.Html),
            ListPage = list is null ? null : (list.Url, list.Html),
        };
    }

    // =============================================================================================
    // Reading pages
    // =============================================================================================

    private static Uri Normalize(string address)
    {
        var text = (address ?? string.Empty).Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text.TrimStart('/');
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !uri.Host.Contains('.'))
            throw new ArgumentException("Type the site's address, like example.com or https://example.com/games/.");
        return uri;
    }

    private static bool SameSite(string? url, Uri home)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        static string Bare(string host) => host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
        return Bare(uri.Host).Equals(Bare(home.Host), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads a page and teaches from it. A page that teaches nothing from a plain download is tried once more in the
    /// browser (sites that build their pages with script).</summary>
    private async Task<Learned?> ReadAndLearnAsync(Uri url, Action<string> say, CancellationToken ct)
    {
        var fetched = await FetchAsync(url, say, browserFirst: false, ct).ConfigureAwait(false);
        if (fetched is null) return null;
        var lesson = await TryLearnAsync(fetched.Value.Html, fetched.Value.Url, ct).ConfigureAwait(false);
        if (lesson is { FoundPage: true } or { FoundListing: true }) return new Learned(fetched.Value.Url, fetched.Value.Html, lesson, fetched.Value.ViaBrowser);
        if (fetched.Value.ViaBrowser || !challenge.IsAvailable) return lesson is null ? null : new Learned(fetched.Value.Url, fetched.Value.Html, lesson, false);

        say("Nothing readable in the plain page; trying it in SeaRaider's browser (it may build the page with script) …");
        var rendered = await FetchAsync(url, say, browserFirst: true, ct).ConfigureAwait(false);
        if (rendered is null) return lesson is null ? null : new Learned(fetched.Value.Url, fetched.Value.Html, lesson, false);
        var again = await TryLearnAsync(rendered.Value.Html, rendered.Value.Url, ct).ConfigureAwait(false);
        return again is null ? null : new Learned(rendered.Value.Url, rendered.Value.Html, again, true);
    }

    private static async Task<SiteReaderLesson?> TryLearnAsync(string html, Uri url, CancellationToken ct)
    {
        try
        {
            return await SiteReaderTeacher.LearnAsync(html, url.AbsoluteUri, null, ct).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private async Task<(string Html, Uri Url, bool ViaBrowser)?> FetchAsync(Uri url, Action<string>? say, bool browserFirst, CancellationToken ct)
    {
        if (!browserFirst)
        {
            try
            {
                var client = httpClientFactory.CreateClient(HttpClientNames.Scraper);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", repository.GetSettings().UserAgent);
                using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var final = response.RequestMessage?.RequestUri ?? url;
                var blocked = Blocked.IsMatch(body.Length > 60_000 ? body[..60_000] : body);
                if (response.IsSuccessStatusCode && !blocked) return (body, final, false);
                say?.Invoke($"{url.Host} answered {(int)response.StatusCode}{(blocked ? " with a bot check" : string.Empty)}.");
            }
            catch (HttpRequestException ex)
            {
                say?.Invoke($"Couldn't reach {url.Host}: {ex.Message}");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                say?.Invoke($"{url.Host} took too long to answer.");
            }
            if (say is null || !challenge.IsAvailable) return null; // quiet checks don't open the browser
            say("Opening it in SeaRaider's browser (solve the check there if it asks) …");
        }
        if (!challenge.IsAvailable) return null;
        var page = await challenge.FetchPageAsync(url, ct).ConfigureAwait(false);
        return page is { Content.Length: > 200 } ? (page.Content, page.FinalUrl, true) : null;
    }

    // =============================================================================================
    // Finding the rest
    // =============================================================================================

    /// <summary>
    /// Where the list of games may be, most likely first: the game page's breadcrumb links (its console's page), the addresses
    /// above it (…/roms/gameboy/ for …/roms/gameboy/some-game/), links named after its console, the home page, then menu
    /// links named like a list ("Games", "Browse").
    /// </summary>
    private static async Task<IReadOnlyList<Uri>> ListCandidatesAsync(Learned first, Uri home, CancellationToken ct)
    {
        var found = new List<Uri>();
        void Add(Uri? uri)
        {
            if (uri is null || found.Count >= 10 || uri.Scheme is not ("http" or "https") || !SameSite(uri.AbsoluteUri, home)) return;
            uri = new Uri(uri.GetLeftPart(UriPartial.Query));
            if (Uri.Compare(uri, first.Url, UriComponents.HttpRequestUrl, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0) return;
            if (found.Any(f => f.AbsoluteUri.TrimEnd('/').Equals(uri.AbsoluteUri.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))) return;
            found.Add(uri);
        }
        Uri? Link(AngleSharp.Dom.IElement a) => Uri.TryCreate(first.Url, a.GetAttribute("href"), out var target) ? target : null;
        bool IsHome(Uri? uri) => uri is not null && uri.AbsolutePath.Trim('/').Length == 0;

        using var doc = await GamePageParser.ParseHtmlAsync(first.Html, first.Url, ct).ConfigureAwait(false);

        // 1. Breadcrumbs, nearest first (the console before "Home").
        var crumbs = doc.QuerySelectorAll(".breadcrumb a[href], .breadcrumbs a[href], [class*='breadcrumb'] a[href], " +
                                          "nav[aria-label*='readcrumb'] a[href], [itemtype*='BreadcrumbList'] a[href]")
            .Select(Link).Where(u => !IsHome(u)).Reverse().ToList();
        foreach (var crumb in crumbs) Add(crumb);

        // 2. The addresses above this page.
        var segments = first.Url.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var k = segments.Length - 1; k >= 1; k--)
            Add(new Uri(home, "/" + string.Join('/', segments.Take(k)) + (first.Url.AbsolutePath.EndsWith('/') ? "/" : string.Empty)));

        // 3. Links named after this game's console ("Game Boy", "SNES ROMs").
        var system = Emulation.GameSystems.FromText($"{first.Url.AbsolutePath} {doc.Title}");
        if (system is not null)
            foreach (var a in doc.QuerySelectorAll("a[href]"))
            {
                var words = Regex.Replace(a.TextContent ?? string.Empty, @"\s+", " ").Trim();
                if (words.Length is > 0 and <= 40 && Emulation.GameSystems.FromText(words)?.Id == system.Id) Add(Link(a));
            }

        // 4. The home page and menu links named like a list.
        Add(home);
        foreach (var a in doc.QuerySelectorAll("nav a[href], header a[href], .menu a[href], [class*='menu'] a[href], a[href]"))
        {
            var words = Regex.Replace(a.TextContent ?? string.Empty, @"\s+", " ").Trim();
            if (words.Length is 0 or > 30 || !ListLinkWords.IsMatch(words)) continue;
            Add(Link(a));
        }
        return found;
    }

    /// <summary>How many games a learned list shows on its page.</summary>
    private static async Task<int> CountListedAsync(Learned list, GameSource source, CancellationToken ct)
    {
        using var doc = await GamePageParser.ParseHtmlAsync(list.Html, list.Url, ct).ConfigureAwait(false);
        return SiteReaderParser.ParseListing(list.Lesson.Reader, doc, list.Url, source).Games.Count;
    }

    /// <summary>The list's numbered pages ("…/page/{page}/"), only when page 2 really shows other games.</summary>
    private async Task<string?> FindPagesAsync(Learned list, GameSource source, Action<string> say, CancellationToken ct)
    {
        var reader = list.Lesson.Reader;
        var templates = new List<string>();
        foreach (var note in list.Lesson.Notes)
            if (SuggestedBrowse.Match(note) is { Success: true } m) templates.Add(m.Groups[1].Value);

        var path = list.Url.GetLeftPart(UriPartial.Path).TrimEnd('/');
        var join = list.Url.Query.Length > 0 ? "&" : "?";
        foreach (var pageTwo in new[]
                 {
                     path + "/page/2/", list.Url.AbsoluteUri + join + "page=2", list.Url.AbsoluteUri + join + "paged=2",
                     path + "/2/", list.Url.AbsoluteUri + join + "p=2",
                 })
            if (SiteReaderTeacher.PageTemplate(pageTwo, list.Url) is { } template && !templates.Contains(template)) templates.Add(template);

        using var firstDoc = await GamePageParser.ParseHtmlAsync(list.Html, list.Url, ct).ConfigureAwait(false);
        var firstIds = SiteReaderParser.ParseListing(reader, firstDoc, list.Url, source).Games.Select(g => g.PageUrl)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var template in templates.Take(6))
        {
            ct.ThrowIfCancellationRequested();
            if (!Uri.TryCreate(template.Replace("{page}", "2", StringComparison.OrdinalIgnoreCase), UriKind.Absolute, out var two)) continue;
            var fetched = await FetchAsync(two, null, browserFirst: false, ct).ConfigureAwait(false);
            if (fetched is null)
            {
                // The site needs the browser for every page: trust the site's own "next" link without checking.
                if (list.ViaBrowser && templates.IndexOf(template) == 0 && list.Lesson.Notes.Any(n => SuggestedBrowse.IsMatch(n)))
                {
                    say($"Page addresses: {template} (from the site's next-page link)");
                    return template;
                }
                continue;
            }
            using var doc = await GamePageParser.ParseHtmlAsync(fetched.Value.Html, fetched.Value.Url, ct).ConfigureAwait(false);
            var games = SiteReaderParser.ParseListing(reader, doc, fetched.Value.Url, source).Games;
            if (games.Count == 0 || games.All(g => firstIds.Contains(g.PageUrl))) continue;
            say($"Page addresses: {template} (checked: page 2 has {games.Count} other games)");
            return template;
        }
        return null;
    }

    /// <summary>The site's search, only when searching for a game it lists finds games.</summary>
    private async Task<string?> FindSearchAsync(Learned list, Uri home, GameSource source, Action<string> say, CancellationToken ct)
    {
        var reader = list.Lesson.Reader;
        var templates = new List<string>();
        foreach (var note in list.Lesson.Notes)
            if (SuggestedSearch.Match(note) is { Success: true } m) templates.Add(m.Groups[1].Value);
        if (list.Html.Contains("wp-content", StringComparison.OrdinalIgnoreCase) || list.Html.Contains("wp-json", StringComparison.OrdinalIgnoreCase))
            templates.Add(home.AbsoluteUri + "?s={query}");
        templates.Add(home.AbsoluteUri + "search?q={query}");
        templates.Add(home.AbsoluteUri + "?s={query}");

        using var listDoc = await GamePageParser.ParseHtmlAsync(list.Html, list.Url, ct).ConfigureAwait(false);
        var word = SiteReaderParser.ParseListing(reader, listDoc, list.Url, source).Games
            .SelectMany(g => Regex.Split(g.Title, @"[^\p{L}\p{N}]+"))
            .FirstOrDefault(w => w.Length >= 4 && !Regex.IsMatch(w, @"^(the|edition|deluxe|version|update|build|repack|free|download|game|games)$", Ci));
        if (word is null) return null;

        foreach (var template in templates.Distinct(StringComparer.OrdinalIgnoreCase).Take(4))
        {
            ct.ThrowIfCancellationRequested();
            var url = template.Replace("{query}", Uri.EscapeDataString(word), StringComparison.OrdinalIgnoreCase)
                .Replace("{page}", "1", StringComparison.OrdinalIgnoreCase);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
            var fetched = await FetchAsync(uri, null, browserFirst: false, ct).ConfigureAwait(false);
            if (fetched is null) continue;
            using var doc = await GamePageParser.ParseHtmlAsync(fetched.Value.Html, fetched.Value.Url, ct).ConfigureAwait(false);
            var games = SiteReaderParser.ParseListing(reader, doc, fetched.Value.Url, source).Games;
            if (!games.Any(g => g.Title.Contains(word, StringComparison.OrdinalIgnoreCase))) continue;
            say($"Search: {template} (checked: \"{word}\" finds {games.Count} games)");
            return template;
        }
        say("No working search found: searching this site uses SeaRaider's own search instead.");
        return null;
    }

    // =============================================================================================
    // Downloads
    // =============================================================================================

    private static readonly Regex DownloadWords = new(
        @"download|descargar|t[ée]l[ée]charger|herunterladen|baixar|scarica|get\s+(it|the\s+(rom|file|game))|get\s+rom",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly string[] AddressAttributes = ["href", "data-href", "data-url", "data-link", "data-download", "data-file", "data-src", "action"];

    /// <summary>A way to the download: an address, how to read it on every page (a selector, or what's added to the page's address).</summary>
    private sealed record DownloadCandidate(Uri Url, string? Selector, string? Suffix, string Why);

    /// <summary>
    /// Checks the learned game page gives download links. When it doesn't, it does what you'd do by hand: looks at the Download
    /// button (its address, its data-… attributes, its onclick code, the form around it) and at the usual "page address +
    /// /download", opens each to see whether a file comes back (or a page linking the file), and makes the first that works the
    /// reader's rule for every game.
    /// </summary>
    private async Task FindDownloadsAsync(Learned page, GameSource source, Action<string> say, CancellationToken ct)
    {
        var recipe = page.Lesson.Reader.Page ??= new PageRecipe();
        using var doc = await GamePageParser.ParseHtmlAsync(page.Html, page.Url, ct).ConfigureAwait(false);
        var game = parser.ExtractGame(doc, page.Url, source, []);
        SiteReaderParser.EnrichFromGamePage(new SiteReader { Name = "test", Domains = [page.Url.Host], Page = recipe }, doc, page.Url, game);
        if (game.DownloadLinks.Count > 0)
        {
            say($"Downloads: {game.DownloadLinks.Count} found on the game's page ({string.Join(", ", game.DownloadLinks.Select(l => l.Host).Distinct().Take(4))}).");
            return;
        }

        say("No download links on the game's page itself: looking behind its Download button …");
        var pageBase = page.Url.GetLeftPart(UriPartial.Path).TrimEnd('/');
        var candidates = new List<DownloadCandidate>();
        void Add(DownloadCandidate candidate)
        {
            if (candidate.Url.Scheme is not ("http" or "https")) return;
            if (candidate.Url.AbsoluteUri.TrimEnd('/').Equals(pageBase, StringComparison.OrdinalIgnoreCase)) return; // the page itself
            if (candidates.Any(c => c.Url.AbsoluteUri.Equals(candidate.Url.AbsoluteUri, StringComparison.OrdinalIgnoreCase))) return;
            candidates.Add(candidate);
        }

        foreach (var element in doc.QuerySelectorAll("a, button, input[type=button], input[type=submit], [onclick], [data-href], [data-url], [data-link], [data-download], form"))
        {
            var words = $"{element.TextContent} {element.GetAttribute("value")} {element.GetAttribute("title")} {element.GetAttribute("aria-label")} {element.ClassName} {element.Id}";
            if (!DownloadWords.IsMatch(words)) continue;
            if (element.Closest("nav, header, footer") is not null) continue; // the site's own menu ("Downloads" section)

            foreach (var attribute in AddressAttributes)
            {
                var value = element.GetAttribute(attribute)?.Trim();
                if (string.IsNullOrEmpty(value) || value.StartsWith('#') || value.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) continue;
                if (!Uri.TryCreate(page.Url, value, out var target)) continue;
                var selector = SelectorFor(doc, element);
                Add(new DownloadCandidate(target, selector is null ? null : attribute == "href" ? selector : $"{selector}@{attribute}", null,
                    $"the \"{Shorten(element.TextContent)}\" button's {attribute}"));
            }
            // onclick="location.href='/file/123/download'" / window.open('…')
            foreach (Match m in Regex.Matches(element.GetAttribute("onclick") ?? string.Empty, @"['""](?<u>(?:https?:)?/[^'""\s]+)['""]"))
                if (Uri.TryCreate(page.Url, m.Groups["u"].Value, out var target))
                    Add(new DownloadCandidate(target, null, null, $"the \"{Shorten(element.TextContent)}\" button's click code"));
        }
        foreach (var suffix in new[] { "/download", "/download/", "?download=1", "/dl" })
            Add(new DownloadCandidate(new Uri(pageBase + suffix), null, suffix, $"the page's address + {suffix}"));

        // 1. Straight to it, as SeaRaider's downloader would go (no visit to the game page first).
        foreach (var candidate in candidates.Take(10))
        {
            ct.ThrowIfCancellationRequested();
            var what = await ProbeAsync(candidate.Url, null, ct).ConfigureAwait(false);
            if (what is null) continue;
            if (what == Countdown) { UseButton(candidate, "a countdown page that starts the file by itself"); return; }

            // How to find it on every game's page: what's added to the address, a pattern, or the button's selector.
            var rule = candidate.Suffix
                       ?? (candidate.Url.AbsoluteUri.StartsWith(pageBase, StringComparison.OrdinalIgnoreCase) && candidate.Url.AbsoluteUri.Length > pageBase.Length
                           ? candidate.Url.AbsoluteUri[pageBase.Length..]
                           : null);
            if (rule is not null) recipe.DownloadUrl = rule;
            else if (candidate.Selector is not null) recipe.Downloads = candidate.Selector;
            else if (PatternFor(candidate.Url, page.Url) is { } pattern) recipe.DownloadUrl = pattern;
            else continue; // found for this one game only: no rule for the others
            recipe.DownloadByButton = null;

            var used = recipe.Downloads == candidate.Selector && candidate.Selector is not null && rule is null
                ? "\"downloads\": \"" + recipe.Downloads + "\""
                : "\"downloadUrl\": \"" + recipe.DownloadUrl + "\"";
            say($"Downloads: {candidate.Why} gives {what} → the reader uses {used}.");
            return;
        }

        // 2. Like a person: open the game's page first (the site remembers you), then follow its button. Sites that answer
        //    "Invalid session" to a direct visit, or count down before the file, only work this way: in the download browser.
        var buttons = candidates.Where(c => c.Suffix is null && SameSite(c.Url.AbsoluteUri, page.Url)).ToList();
        foreach (var candidate in buttons.Concat(candidates.Where(c => c.Suffix is not null)).Take(6))
        {
            ct.ThrowIfCancellationRequested();
            if (await ProbeAsync(candidate.Url, page.Url, ct).ConfigureAwait(false) is not { } what) continue;
            UseButton(candidate, what == Countdown ? "a countdown page that starts the file" : what + ", but only after visiting the game's page");
            return;
        }
        if (buttons.Count > 0)
        {
            UseButton(buttons[0], "a page SeaRaider can't read without a browser");
            return;
        }
        say("Couldn't find where the Download button leads. SeaRaider's own reader still looks on each game's page; if the site only adds " +
            "to the address (…/game/download), set it under the reader's Settings › Download address.");

        void UseButton(DownloadCandidate candidate, string what)
        {
            recipe.DownloadByButton = true;
            recipe.DownloadUrl = null;
            say($"Downloads: {candidate.Why} gives {what} → the reader downloads by pressing the game page's Download button in " +
                "SeaRaider's download browser (it waits out any countdown).");
        }
    }

    private const string Countdown = "a countdown";

    private static readonly Regex CountdownText = new(
        @"(download|file)\s+(will|should)\s+(start|begin)\s+(in|after)|start(s)?\s+in\s+\d+\s*(s|sec|seconds)|please\s+wait\s+\d+\s*(s|sec|seconds)|countdown",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// What an address gives: "a file", "a page with the file", <see cref="Countdown"/>, or null (nothing to download there:
    /// an error page such as "Invalid session"). With <paramref name="visitFirst"/>, that page is opened first in the same
    /// session (cookies kept, and sent as the page you came from), like clicking its button.
    /// </summary>
    private async Task<string?> ProbeAsync(Uri url, Uri? visitFirst, CancellationToken ct)
    {
        HttpClient? own = null;
        try
        {
            var userAgent = repository.GetSettings().UserAgent;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            HttpClient client;
            if (visitFirst is null) client = httpClientFactory.CreateClient(HttpClientNames.Scraper);
            else
            {
                client = own = new HttpClient(new HttpClientHandler
                {
                    CookieContainer = new System.Net.CookieContainer(),
                    AutomaticDecompression = System.Net.DecompressionMethods.All,
                });
                using var visit = new HttpRequestMessage(HttpMethod.Get, visitFirst);
                visit.Headers.TryAddWithoutValidation("User-Agent", userAgent);
                using var visited = await client.SendAsync(visit, timeout.Token).ConfigureAwait(false);
                if (!visited.IsSuccessStatusCode) return null;
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            if (visitFirst is not null) request.Headers.Referrer = visitFirst;
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var type = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            var attachment = response.Content.Headers.ContentDisposition?.DispositionType?.Equals("attachment", StringComparison.OrdinalIgnoreCase) == true;
            if (attachment || (type.Length > 0 && !type.Contains("html", StringComparison.OrdinalIgnoreCase) && !type.StartsWith("text/", StringComparison.OrdinalIgnoreCase) &&
                               !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && !type.Contains("json", StringComparison.OrdinalIgnoreCase)))
                return "a file";
            if (!type.Contains("html", StringComparison.OrdinalIgnoreCase)) return null;

            // A page: does it link the file (or a file host), or send there by itself?
            var html = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (html.Length > 600_000) html = html[..600_000];
            var final = response.RequestMessage?.RequestUri ?? url;
            using var doc = await GamePageParser.ParseHtmlAsync(html, final, timeout.Token).ConfigureAwait(false);
            var words = doc.Body?.TextContent ?? string.Empty;
            if (CountdownText.IsMatch(words.Length > 20_000 ? words[..20_000] : words)) return Countdown;
            var refresh = doc.QuerySelector("meta[http-equiv='refresh' i]")?.GetAttribute("content");
            var links = doc.QuerySelectorAll("a[href]").Select(a => (Href: a.GetAttribute("href"), Text: a.TextContent)).ToList();
            if (refresh is not null && Regex.Match(refresh, @"url\s*=\s*['""]?(?<u>[^'""]+)", RegexOptions.IgnoreCase) is { Success: true } r)
                links.Add((r.Groups["u"].Value, "download"));
            foreach (var (href, text) in links)
            {
                if (string.IsNullOrWhiteSpace(href) || !Uri.TryCreate(final, href, out var target) || target.Scheme is not ("http" or "https" or "magnet")) continue;
                if (LinkClassifier.Classify(target, text) is { } link && !HostPolicy.IsBlocked(link)) return "a page with the file";
            }
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            own?.Dispose();
        }
    }

    /// <summary>A download address made from this page's own name or number ("/get/123" from "…/game-123"), as a pattern.</summary>
    private static string? PatternFor(Uri download, Uri page)
    {
        var slug = page.AbsolutePath.TrimEnd('/').Split('/').LastOrDefault() ?? string.Empty;
        var id = Regex.Match(page.AbsolutePath, @"\d+(?!.*\d)").Value;
        var path = download.PathAndQuery;
        string? pattern = null;
        if (slug.Length > 2 && path.Contains(slug, StringComparison.OrdinalIgnoreCase)) pattern = path.Replace(slug, "{slug}", StringComparison.OrdinalIgnoreCase);
        else if (id.Length > 0 && Regex.IsMatch(path, $@"(?<!\d){id}(?!\d)")) pattern = Regex.Replace(path, $@"(?<!\d){id}(?!\d)", "{id}");
        if (pattern is null) return null;
        return download.Host.Equals(page.Host, StringComparison.OrdinalIgnoreCase) ? pattern : $"{download.Scheme}://{download.Host}{pattern}";
    }

    /// <summary>A CSS selector that picks out this element on the page (and few others), or null.</summary>
    private static string? SelectorFor(AngleSharp.Dom.IDocument doc, AngleSharp.Dom.IElement element)
    {
        var tag = element.LocalName;
        var tries = new List<string>();
        if (element.Id is { Length: > 0 } id && Regex.IsMatch(id, @"^[A-Za-z][\w-]*$") && !Regex.IsMatch(id, @"\d{3,}")) tries.Add("#" + id);
        foreach (var cls in element.ClassList.Where(c => Regex.IsMatch(c, @"^[A-Za-z_-][\w-]*$") && !Regex.IsMatch(c, @"\d{3,}|^(active|current|is-|js-)")))
            tries.Add($"{tag}.{cls}");
        foreach (var attribute in AddressAttributes.Skip(1).Where(a => element.HasAttribute(a))) tries.Add($"{tag}[{attribute}]");
        foreach (var selector in tries)
        {
            try
            {
                var found = doc.QuerySelectorAll(selector);
                if (found.Length is > 0 and <= 6 && found.Contains(element)) return selector;
            }
            catch (AngleSharp.Dom.DomException)
            {
                // not a selector AngleSharp takes
            }
        }
        return null;
    }

    private static string Shorten(string? text)
    {
        var clean = Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();
        return clean.Length > 30 ? clean[..30] + "…" : clean.Length == 0 ? "Download" : clean;
    }

    /// <summary>The site's own name (og:site_name), else its domain.</summary>
    private static async Task<string> SiteNameAsync(Learned first, string host, CancellationToken ct)
    {
        using var doc = await GamePageParser.ParseHtmlAsync(first.Html, first.Url, ct).ConfigureAwait(false);
        var name = doc.QuerySelector("meta[property='og:site_name']")?.GetAttribute("content")?.Trim();
        var bare = host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
        return string.IsNullOrWhiteSpace(name) || name.Length > 60 ? bare : name;
    }
}
