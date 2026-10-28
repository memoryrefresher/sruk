using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace SeaRaider.Core.Scraping;

/// <summary>One selector the teacher worked out, with what it reads from the pasted page (so you can see it's right).</summary>
/// <param name="Area">"page" (a game's page) or "list" (a list of games).</param>
public sealed record LearnedField(string Area, string Field, string Selector, string? Sample);

/// <summary>What was learned from one pasted page.</summary>
/// <param name="Reader">A reader holding only what was learned (merged into the site's file when saved).</param>
/// <param name="Notes">Things to know: what looked like what, what wasn't found, suggested addresses.</param>
public sealed record SiteReaderLesson(SiteReader Reader, IReadOnlyList<LearnedField> Fields, IReadOnlyList<string> Notes,
    bool FoundPage, bool FoundListing);

/// <summary>
/// What the user can tell the teacher when its own guess is wrong. Right now: which links are the downloads, either by
/// example (a link's address, or the words on its button, one per line) or exactly (a CSS selector).
/// </summary>
public sealed record LearnHints
{
    /// <summary>Links that are the right downloads: an address ("https://pixeldrain.com/u/abc", "pixeldrain.com") or a button's words ("Mirror 1").</summary>
    public IReadOnlyList<string> DownloadExamples { get; init; } = [];

    /// <summary>A CSS selector for the download links ("a.mirror", "#downloads a"). Used as given when it matches links.</summary>
    public string? DownloadSelector { get; init; }

    /// <summary>The download button's code, copied from View source (Ctrl+U): <c>&lt;a class="btn" href="…"&gt;Download&lt;/a&gt;</c>.</summary>
    public string? DownloadSnippet { get; init; }

    public bool HasDownloadHints => DownloadExamples.Count > 0 || !string.IsNullOrWhiteSpace(DownloadSelector) || !string.IsNullOrWhiteSpace(DownloadSnippet);

    /// <summary>
    /// Where a field is on the page, as the user showed it: the words on the page ("Elden Ring", "Ubisoft"), a piece of the
    /// page's code from View source, or a CSS selector. Keys: a game page's <see cref="PageFields"/>; a list's "listGame"
    /// (one game's name as the list shows it) and "nextPage" (the next-page button).
    /// </summary>
    public IReadOnlyDictionary<string, string> Pointers { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The address of the list's second page ("example.com/demo/page/2/"): every page is then read by its number.</summary>
    public string? PageTwoAddress { get; init; }

    public static readonly string[] PageFields = ["title", "cover", "description", "developer", "publisher", "version", "size", "releaseDate", "updated", "genres"];
    public static readonly string[] ListFields = ["listGame", "nextPage"];

    public bool HasPagePointers => Pointers.Any(p => PageFields.Contains(p.Key, StringComparer.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(p.Value));
    public bool HasListPointers => Pointers.Any(p => ListFields.Contains(p.Key, StringComparer.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(p.Value));

    /// <summary>Splits what was typed into the examples box: one per line (several addresses on a line are told apart too).</summary>
    public static IReadOnlyList<string> SplitExamples(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : Regex.Split(text, @"[\r\n;]+")
                .SelectMany(line => Regex.Split(line.Trim(), @"\s+(?=https?://|magnet:)", RegexOptions.IgnoreCase))
                .Select(s => s.Trim().Trim('"', '\'', '<', '>'))
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
}

/// <summary>
/// Works out a <see cref="SiteReader"/> from a page the user pasted: the page's HTML is read for the title, cover,
/// description, screenshots, the "Developer: …" / "Size: …" lines and the download buttons (a game's page), or for the
/// repeated game cards and the next-page link (a list). Every selector is checked against the pasted page before it is
/// offered, and shown with what it reads. It only ever looks at the HTML it is given; nothing is fetched.
/// </summary>
public static class SiteReaderTeacher
{
    private static readonly RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex BadClass = new(
        @"^(js-|is-|has-|wp-|col-|row$|container$|clearfix|active|current|post-\d|type-|status-|hentry|category-|tag-|format-|page-id|elementor-element|elementor-\d|e-\d|menu-item)|\d{3,}",
        Ci);

    private static readonly Regex CssIdent = new(@"^[A-Za-z_-][A-Za-z0-9_-]*$");
    private static readonly Regex SpacesRx = new(@"\s+");

    private static readonly Regex ChromeClass = new(
        @"(^|[-_ ])(nav|navbar|navigation|menu|footer|sidebar|comment|comments|breadcrumb|cookie|popup|modal)([-_ ]|$)", Ci);

    private static readonly Regex RemoveClass = new(
        @"share|sharing|social|related|rating|advert|adsbygoogle|banner|sidebar|comment|breadcrumb|post-?meta|download|button|toc|author", Ci);

    /// <summary>"Developer: X" style labels, by recipe field.</summary>
    private static readonly (string Field, string Labels)[] FactLabels =
    [
        ("developer", @"developers?|dev|studio|creator"),
        ("publisher", @"publishers?"),
        ("version", @"game version|version|build"),
        ("size", @"game size|file size|download size|size|storage"),
        ("releaseDate", @"release date|released|release"),
        ("updated", @"last updated|date updated|updated|upload date"),
        ("genres", @"genres?|categories|category"),
    ];

    // =============================================================================================
    // Entry point
    // =============================================================================================

    /// <param name="html">The page's HTML (browser: Ctrl+U, select all, copy).</param>
    /// <param name="address">Optional: the page's address (https://example.com/some-game/) or just the site's domain.</param>
    /// <exception cref="ArgumentException">Not HTML, or the site can't be told.</exception>
    /// <param name="hints">Optional: the right download links, when the teacher's own guess picked the wrong ones.</param>
    public static async Task<SiteReaderLesson> LearnAsync(string html, string? address, LearnHints? hints = null, CancellationToken ct = default)
    {
        html = (html ?? string.Empty).Trim();
        hints ??= new LearnHints();
        if (html.Length == 0 && !string.IsNullOrWhiteSpace(hints.PageTwoAddress))
            return PagesOnly(hints.PageTwoAddress, address);
        if (html.Length < 200 || !html.Contains('<') || !Regex.IsMatch(html, @"<(html|body|div|article|main|h1)\b", Ci))
            throw new ArgumentException(
                "That doesn't look like a page's HTML. In your browser open the game's page, press Ctrl+U (View source), " +
                "Select all, Copy, and paste it here.");

        Uri? pageUri = null;
        string? domain = null;
        address = address?.Trim();
        if (!string.IsNullOrEmpty(address))
        {
            if (address.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(address, UriKind.Absolute, out var given)) pageUri = given;
            else if (Uri.TryCreate("https://" + address.TrimStart('/'), UriKind.Absolute, out var host)) pageUri = host;
        }

        // A first parse (without an address) only to find the canonical address when none was given.
        if (pageUri is null)
        {
            using var probe = await GamePageParser.ParseHtmlAsync(html, new Uri("https://unknown.invalid/"), ct).ConfigureAwait(false);
            var guess = probe.QuerySelector("link[rel='canonical']")?.GetAttribute("href")
                        ?? probe.QuerySelector("meta[property='og:url']")?.GetAttribute("content");
            if (guess is not null && Uri.TryCreate(guess, UriKind.Absolute, out var found)) pageUri = found;
        }

        if (pageUri is null || pageUri.Host == "unknown.invalid")
            throw new ArgumentException("I can't tell which site this page is from. Type its address (or just its domain, like example.com) above the box.");

        domain = pageUri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? pageUri.Host[4..] : pageUri.Host;

        using var doc = await GamePageParser.ParseHtmlAsync(html, pageUri, ct).ConfigureAwait(false);
        return Learn(doc, pageUri, domain, hints);
    }

    // =============================================================================================
    // The lesson
    // =============================================================================================

    private static SiteReaderLesson Learn(IDocument doc, Uri pageUri, string domain, LearnHints hints)
    {
        var fields = new List<LearnedField>();
        var notes = new List<string>();

        var siteName = doc.QuerySelector("meta[property='og:site_name']")?.GetAttribute("content");
        var reader = new SiteReader
        {
            Name = string.IsNullOrWhiteSpace(siteName) ? domain : Clean(siteName),
            Domains = [domain],
        };

        var content = FindContent(doc, out var contentScore);
        var cards = FindCards(doc);

        // What the user pointed at says which kind of page this is.
        if (hints.HasListPointers && !hints.HasPagePointers && !hints.HasDownloadHints &&
            hints.Pointers.TryGetValue("listGame", out var gameName) && CardsFromPointer(doc, gameName, notes) is { } pointedCards)
            cards = pointedCards;

        // A game page has a body of text; a list has many cards. A page wins when it has real text.
        var isPage = content is not null && contentScore >= 200;
        var isList = !isPage && cards is not null;
        if ((hints.HasPagePointers || hints.HasDownloadHints) && !hints.HasListPointers && content is not null) (isPage, isList) = (true, false);
        else if (hints.HasListPointers && !hints.HasPagePointers && !hints.HasDownloadHints && cards is not null) (isPage, isList) = (false, true);

        var learnedOnlyDownloads = false;
        if (isPage) reader.Page = LearnPage(doc, pageUri, content!, hints, fields, notes);
        else if (isList)
        {
            reader.Listing = LearnListing(doc, pageUri, cards!, fields, notes);
            if (hints.HasDownloadHints)
                notes.Add("Your download links were left out: this is a list of games. Download links are read from a game's page, so paste one and point at its links.");
        }
        else if (hints.HasDownloadHints || hints.HasPagePointers)
        {
            // Not recognisable as a whole game page, but the user pointed at things on it: that much can be learned.
            var recipe = new PageRecipe();
            if (hints.HasDownloadHints && FindHintedDownloads(doc, pageUri, hints, out var onlySample, notes) is { } onlyDownloads)
            {
                recipe.Downloads = onlyDownloads;
                fields.Add(new LearnedField("page", "downloads", onlyDownloads, onlySample));
            }
            ApplyPagePointers(doc, pageUri, recipe, hints, fields, notes);
            if (fields.Count > 0)
            {
                reader.Page = recipe;
                learnedOnlyDownloads = true;
            }
        }
        else
            notes.Add("I couldn't tell what kind of page this is. It needs a game's page (a title and a block of text) " +
                      "or a list of at least four games. Paste a complete page, not a part of it.");

        if (isList && reader.Listing is not null) ApplyListPointers(doc, pageUri, reader.Listing, hints, fields, notes);
        if (!string.IsNullOrWhiteSpace(hints.PageTwoAddress))
        {
            if (PageTemplate(hints.PageTwoAddress, pageUri) is { } template)
            {
                reader.BrowseUrl = template;
                fields.Add(new LearnedField("site", "browseUrl", template, "page 1, 2, 3 … by number"));
            }
            else notes.Add($"browseUrl: \"{hints.PageTwoAddress}\" has no page number in it to count up (like …/page/2/ or ?page=2).");
        }

        if (isPage && cards is { Count: >= 8 })
            notes.Add("This page also has a grid of games (probably \"related games\"). Paste the site's own list page to teach it the list.");
        if (isPage) notes.Add("Learned the game page. Paste the site's list of games next to teach it the list too.");
        if (isList) notes.Add("Learned the list. Paste a game's page next to teach it the game pages too.");

        return new SiteReaderLesson(reader, fields, notes, isPage || learnedOnlyDownloads, isList);
    }

    // ---------------------------------------------------------------------------------------------
    // A game's page
    // ---------------------------------------------------------------------------------------------

    private static PageRecipe LearnPage(IDocument doc, Uri pageUri, IElement content, LearnHints hints, List<LearnedField> fields, List<string> notes)
    {
        var recipe = new PageRecipe();

        void Learned(string field, string? selector, string? sample)
        {
            if (selector is not null) fields.Add(new LearnedField("page", field, selector, sample));
        }

        // Title.
        var title = FindTitle(doc);
        if (title is not null)
        {
            recipe.Title = title;
            Learned("title", title, SiteReaderParser.Text(doc, title));
        }
        else notes.Add("title: no heading found.");

        // Description (the block of text) and what to leave out of it.
        var contentSelector = Locate(doc, content);
        if (contentSelector is not null)
        {
            recipe.Description = contentSelector;
            Learned("description", contentSelector, Shorten(Clean(content.TextContent), 90));

            var remove = FindRemovals(content);
            if (remove.Count > 0)
            {
                recipe.Remove = string.Join(" || ", remove);
                Learned("remove", recipe.Remove, "left out of the description");
            }

            var images = SafeAll(content, "img").Count;
            if (images > 0)
            {
                recipe.Screenshots = contentSelector + " img";
                Learned("screenshots", recipe.Screenshots, $"{images} pictures");
            }
        }

        // Cover: the page's own preview picture, else the first big picture outside the text.
        var cover = FindCover(doc, content);
        if (cover is not null)
        {
            recipe.Cover = cover;
            Learned("cover", cover, SiteReaderParser.Url(doc, cover, pageUri));
        }

        // "Developer: …", "Size: …" lines.
        foreach (var (field, labels) in FactLabels)
        {
            var spec = FindFact(doc, labels, field == "genres");
            if (spec is null) continue;
            var sample = field == "genres"
                ? string.Join(", ", SiteReaderParser.Texts(doc, spec).Take(4))
                : SiteReaderParser.Text(doc, spec);
            if (string.IsNullOrWhiteSpace(sample)) continue;
            SetPageField(recipe, field, spec);
            Learned(field, spec, sample);
        }

        // Download buttons.
        // When the user pointed at the right links, those win over the teacher's own guess.
        string? downloads = null;
        string? downloadSample = null;
        if (hints.HasDownloadHints)
        {
            downloads = FindHintedDownloads(doc, pageUri, hints, out downloadSample, notes);
            if (downloads is null) notes.Add("downloads: I couldn't use your links, so the download buttons below are my own guess again.");
        }
        if (downloads is null) downloads = FindDownloads(doc, pageUri, content, out downloadSample);
        if (downloads is not null)
        {
            recipe.Downloads = downloads;
            Learned("downloads", downloads, downloadSample);
        }
        else notes.Add("downloads: no download links found in the page's text" + (downloadSample is null ? "." : $" ({downloadSample}).") +
                       " If the page shows them, they may be added by a script after it loads (then they aren't in the HTML); check View source for the links.");

        // Trailers.
        var trailer = SafeAll(doc, "iframe[src*='youtube'], iframe[src*='vimeo'], iframe[data-src*='youtube']").FirstOrDefault(e => !InChrome(e));
        if (trailer is not null && Locate(doc, trailer) is { } trailerSelector)
        {
            recipe.Trailers = trailerSelector;
            Learned("trailers", trailerSelector, "a video");
        }

        // What the user pointed at wins over the guesses above.
        ApplyPagePointers(doc, pageUri, recipe, hints, fields, notes);

        if (recipe.Title is null && recipe.Description is null) notes.Add("Nothing usable was found on this page.");
        if (recipe.Developer is not null && fields.Any(f => f.Field == "developer" && !f.Selector.Contains("@owntext", StringComparison.Ordinal) && !f.Selector.EndsWith(" a", StringComparison.Ordinal)))
            notes.Add("developer / publisher / version: the label (\"Developer:\") is part of the same text here, so the value will include it.");
        return recipe;
    }

    private static void SetPageField(PageRecipe recipe, string field, string spec)
    {
        switch (field)
        {
            case "developer": recipe.Developer = spec; break;
            case "publisher": recipe.Publisher = spec; break;
            case "version": recipe.Version = spec; break;
            case "size": recipe.Size = spec; break;
            case "releaseDate": recipe.ReleaseDate = spec; break;
            case "updated": recipe.Updated = spec; break;
            case "genres": recipe.Genres = spec; break;
        }
    }

    private static string? FindTitle(IDocument doc)
    {
        IElement? heading = SafeAll(doc, "h1")
            .FirstOrDefault(h => Clean(h.TextContent).Length is >= 2 and <= 200 && !Regex.IsMatch(h.ClassName ?? string.Empty, "site|logo|brand", Ci));
        heading ??= SafeAll(doc, ".the-post-title, .entry-title, .post-title, .game-title, [itemprop='name']")
            .FirstOrDefault(h => Clean(h.TextContent).Length is >= 2 and <= 200);
        heading ??= SafeAll(doc, "article h2, main h2").FirstOrDefault(h => Clean(h.TextContent).Length is >= 2 and <= 200);

        if (heading is null)
            return doc.QuerySelector("meta[property='og:title']") is not null ? "meta[property='og:title']@content" : null;

        var selector = Locate(doc, heading);
        return selector is null ? null : WithOwnText(heading, selector);
    }

    /// <summary>The element with the most paragraph text of its own: the game's description.</summary>
    private static IElement? FindContent(IDocument doc, out int score)
    {
        IElement? best = null;
        score = 0;
        foreach (var element in SafeAll(doc, "article, main, section, div, td"))
        {
            if (InChrome(element)) continue;
            var own = element.Children.Where(c => c.LocalName == "p").Sum(p => Clean(p.TextContent).Length);
            if (own > score)
            {
                best = element;
                score = own;
            }
        }
        if (score >= 80) return best;
        score = 0;
        return null;
    }

    private static List<string> FindRemovals(IElement content)
    {
        var contentLength = Math.Max(1, Clean(content.TextContent).Length);
        var found = new List<string>();
        foreach (var element in SafeAll(content, "*[class]"))
        {
            var cls = GoodClasses(element).FirstOrDefault(c => RemoveClass.IsMatch(c));
            if (cls is null) continue;
            if (Clean(element.TextContent).Length > contentLength * 0.6) continue; // the text itself, not a side block
            var selector = "." + cls;
            if (!found.Contains(selector)) found.Add(selector);
            if (found.Count == 6) break;
        }
        return found;
    }

    private static string? FindCover(IDocument doc, IElement content)
    {
        var og = doc.QuerySelector("meta[property='og:image']") is not null ? "meta[property='og:image']@content" : null;

        var picture = SafeAll(doc, "img").FirstOrDefault(i =>
            !InChrome(i) && Regex.IsMatch(i.ClassName ?? string.Empty, "thumb|cover|featured|poster|wp-post-image|header", Ci));
        var own = picture is null ? null : Locate(doc, picture);

        if (og is not null && own is not null) return og + " || " + own;
        return og ?? own;
    }

    /// <summary>The "Developer: X" line (or table row) for one kind of fact, as a selector.</summary>
    private static string? FindFact(IDocument doc, string labels, bool multiple)
    {
        var withValue = new Regex($@"^\s*(?:{labels})\s*[:：–-]\s*\S", Ci | RegexOptions.Singleline);
        var labelOnly = new Regex($@"^\s*(?:{labels})\s*[:：]?\s*$", Ci);

        IElement? target = null;
        var weight = int.MaxValue;
        foreach (var element in SafeAll(doc, "li, p, div, span, td, tr, dd, dt, th, strong, b, label"))
        {
            if (InChrome(element)) continue;
            var text = Clean(element.TextContent);
            if (text.Length is 0 or > 160) continue;
            if (!withValue.IsMatch(text)) continue;
            var elementWeight = element.QuerySelectorAll("*").Length;
            if (elementWeight < weight)
            {
                target = element;
                weight = elementWeight;
            }
        }

        // A table: label in one cell, value in the next.
        if (target is null)
        {
            foreach (var element in SafeAll(doc, "th, td, dt, strong, b, span, label"))
            {
                if (InChrome(element) || !labelOnly.IsMatch(Clean(element.TextContent))) continue;
                var value = element.NextElementSibling;
                if (value is null || Clean(value.TextContent).Length is 0 or > 120) continue;
                target = value;
                break;
            }
            if (target is not null)
                return Locate(doc, target) is { } sibling ? sibling : null;
        }
        if (target is null) return null;

        var selector = Locate(doc, target);
        if (selector is null) return null;

        if (multiple && target.QuerySelector("a") is not null) return selector + " a";

        // "<strong>Developer:</strong> Studio": the value is the element's own text.
        var own = Clean(OwnText(target));
        if (own.Length >= 2 && own.Length < Clean(target.TextContent).Length) return selector + "@owntext";
        // "<strong>Developer:</strong> <a>Studio</a>": the value is in the link.
        if (own.Length < 2 && target.QuerySelector("a") is not null) return selector + " a";
        return selector;
    }

    /// <summary>"Click Here", "link", "here": a button label that says nothing about the file.</summary>
    private static readonly Regex GenericLabel = new(
        @"^\W*(?:click|tap|press)?\s*(?:here|this link|link|get it|get|go|visit|open|direct link|fast link)\W*$", Ci);

    /// <summary>
    /// The links that look like downloads, as one selector. Tried in order: links to a known file host (or a magnet / torrent),
    /// links worded as downloads, then "Click Here" style links and links that sit next to a file host's name in the text.
    /// When nothing is found, <paramref name="sample"/> says what the page's links looked like instead.
    /// </summary>
    private static string? FindDownloads(IDocument doc, Uri pageUri, IElement content, out string? sample)
    {
        sample = null;
        var scope = content.Closest("article, main") ?? content;
        bool InScope(IElement a) => scope.Contains(a);
        static bool Plain(IElement a)
        {
            var href = a.GetAttribute("href") ?? string.Empty;
            return href.Length > 0 && !href.StartsWith('#') && !href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) &&
                   !href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);
        }

        var anchors = SafeAll(doc, "a[href]").Where(a => !InChrome(a)).ToList();

        bool IsFileLink(IElement a)
        {
            var href = a.GetAttribute("href") ?? string.Empty;
            if (href.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) || href.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)) return true;
            return Uri.TryCreate(pageUri, href, out var url) && url.Scheme is "http" or "https" && FileHosts.MatchFileLink(url) is not null;
        }

        var found = anchors.Where(IsFileLink).ToList();

        if (found.Count == 0)
            found = anchors.Where(a => InScope(a) && Plain(a) && Regex.IsMatch(Clean(a.TextContent) + " " + a.ClassName, @"download|mirror|torrent|magnet", Ci)).ToList();

        if (found.Count == 0)
            found = anchors.Where(a => InScope(a) && Plain(a) && (GenericLabel.IsMatch(Clean(a.TextContent)) || NamesAFileHost(a))).ToList();

        if (found.Count == 0)
        {
            var labels = anchors.Where(a => InScope(a) && Plain(a))
                .GroupBy(a => Clean(a.TextContent), StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).Take(3)
                .Select(g => $"\"{Shorten(g.Key, 24)}\" ×{g.Count()}");
            sample = $"the page's links in the text area are: {string.Join(", ", labels)}";
            return null;
        }

        var wanted = found.ToHashSet();
        var candidates = new List<string>();
        foreach (var anchor in found.Take(40))
        {
            foreach (var cls in GoodClasses(anchor))
            {
                candidates.Add($"a.{cls}");
                candidates.Add($".{cls}");
            }
            var level = 0;
            for (var parent = anchor.ParentElement; parent is not null && level < 4 && parent.LocalName is not ("body" or "html"); parent = parent.ParentElement, level++)
            {
                foreach (var cls in GoodClasses(parent))
                {
                    candidates.Add($".{cls} a");
                    candidates.Add($".{cls} p a");
                    candidates.Add($".{cls} li a");
                }
            }
        }
        // Links with no class at all: by where they go ("/go/…" on the site, or a host's name).
        foreach (var anchor in found.Take(40))
        {
            if (!Uri.TryCreate(pageUri, anchor.GetAttribute("href"), out var url) || url.Scheme is not ("http" or "https")) continue;
            var firstSegment = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (firstSegment is { Length: >= 2 and <= 12 } && firstSegment.All(char.IsLetter))
                candidates.Add($"a[href*='{url.Host}/{firstSegment}']");
            candidates.Add($"a[href*='{url.Host}']");
        }

        string? best = null;
        var bestScore = 0.0;
        foreach (var candidate in candidates.Distinct())
        {
            var matches = SafeAll(doc, candidate);
            if (matches.Count == 0) continue;
            var hits = matches.Count(wanted.Contains);
            var recall = hits / (double)wanted.Count;
            var precision = hits / (double)matches.Count;
            if (recall < 0.8 || precision < 0.6) continue;
            var score = recall * precision;
            if (score > bestScore + 1e-9)
            {
                best = candidate;
                bestScore = score;
            }
        }

        if (best is null)
        {
            var hosts = found
                .Select(a => Uri.TryCreate(pageUri, a.GetAttribute("href"), out var u) ? u.Host : null)
                .OfType<string>().Where(h => h.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList();
            best = hosts.Count > 0 ? string.Join(", ", hosts.Select(h => $"a[href*='{h}']")) : "a[href^='magnet:']";
        }

        var urls = SiteReaderParser.Urls(doc, best, pageUri);
        if (urls.Count == 0) return null;
        var names = urls.Select(u => Uri.TryCreate(u, UriKind.Absolute, out var x) ? x.Host : u).Distinct(StringComparer.OrdinalIgnoreCase).Take(3);
        sample = $"{urls.Count} links ({string.Join(", ", names)})";
        return best;
    }

    // ---------------------------------------------------------------------------------------------
    // Download links the user pointed at
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The download links as the user showed them: an exact CSS selector, or example links (by address or by the words on the
    /// button). From the examples a selector is built that reads all of them and the other links of the same kind (the rest of
    /// their group), and nothing that doesn't look like a download. Null (with a note saying why) when the page has none of them.
    /// </summary>
    private static string? FindHintedDownloads(IDocument doc, Uri pageUri, LearnHints hints, out string? sample, List<string> notes)
    {
        sample = null;

        // 1. An exact selector from the user.
        if (!string.IsNullOrWhiteSpace(hints.DownloadSelector))
        {
            var selector = hints.DownloadSelector.Trim();
            var urls = SiteReaderParser.Urls(doc, selector, pageUri);
            if (urls.Count > 0)
            {
                sample = DescribeLinks(urls);
                return selector;
            }
            notes.Add($"downloads: your selector \"{selector}\" doesn't match any link in the pasted page.");
        }

        // 2. Example links (by address or the words on the button), and the button's code from View source.
        if (hints.DownloadExamples.Count == 0 && string.IsNullOrWhiteSpace(hints.DownloadSnippet)) return null;
        var anchors = SafeAll(doc, Clickables).Where(a => SiteReaderParser.UrlOfElement(a, pageUri) is not null).ToList();
        var chosen = new List<IElement>();
        var missed = new List<string>();
        foreach (var example in hints.DownloadExamples)
        {
            var hits = MatchExample(anchors, example, pageUri);
            if (hits.Count == 0) missed.Add(example);
            else chosen.AddRange(hits.Where(h => !chosen.Contains(h)));
        }
        if (!string.IsNullOrWhiteSpace(hints.DownloadSnippet))
        {
            var fromCode = FromSnippet(doc, hints.DownloadSnippet!)
                .Select(e => anchors.Contains(e) ? e : anchors.FirstOrDefault(a => a.Contains(e) || e.Contains(a)))
                .OfType<IElement>().ToList();
            if (fromCode.Count == 0) missed.Add("the button's code you pasted");
            else chosen.AddRange(fromCode.Where(h => !chosen.Contains(h)));
        }
        if (missed.Count > 0)
        {
            var missedList = string.Join(" · ", missed.Select(m => Shorten(m, 60)));
            notes.Add($"downloads: not found in the pasted page: {missedList}. " +
                      "Check it is a link in View source (a script may add it after the page loads).");
        }
        if (chosen.Count == 0) return null;

        var chosenHosts = chosen
            .Select(a => Uri.TryCreate(pageUri, a.GetAttribute("href"), out var u) ? u.Host : null)
            .OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var chosenTexts = chosen.Select(a => Clean(a.TextContent)).Where(t => t.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);

        bool Plausible(IElement a)
        {
            if (chosen.Contains(a)) return true;
            var href = SiteReaderParser.UrlOfElement(a, pageUri) ?? string.Empty;
            if (href.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) || href.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)) return true;
            if (!Uri.TryCreate(pageUri, href, out var url) || url.Scheme is not ("http" or "https")) return false;
            return FileHosts.MatchFileLink(url) is not null || chosenHosts.Contains(url.Host) || chosenTexts.Contains(Clean(a.TextContent)) ||
                   Regex.IsMatch(Clean(a.TextContent) + " " + a.ClassName, @"download|mirror|torrent|magnet", Ci);
        }

        // Candidate selectors: the links' own classes, their containers, the container they all share, and where they go.
        var candidates = new List<string>();
        foreach (var anchor in chosen.Take(40))
        {
            var tag = anchor.LocalName;
            if (anchor.Id is { Length: > 0 and <= 30 } id && CssIdent.IsMatch(id) && !Regex.IsMatch(id, @"\d{3,}")) candidates.Add("#" + id);
            foreach (var cls in GoodClasses(anchor)) candidates.Add($"{tag}.{cls}");
            var level = 0;
            for (var parent = anchor.ParentElement; parent is not null && level < 4 && parent.LocalName is not ("body" or "html"); parent = parent.ParentElement, level++)
            {
                foreach (var cls in GoodClasses(parent))
                {
                    candidates.Add($".{cls} {tag}");
                    candidates.Add($".{cls} p {tag}");
                    candidates.Add($".{cls} li {tag}");
                }
            }
        }
        var shared = chosen[0].ParentElement;
        for (var up = 0; shared is not null && up < 4 && shared.LocalName is not ("body" or "html"); shared = shared.ParentElement, up++)
        {
            var ancestor = shared;
            if (!chosen.All(a => ancestor.Contains(a))) continue;
            if (Locate(doc, ancestor) is { } container) candidates.Add($"{container} {chosen[0].LocalName}");
        }
        // One button: exactly where it is.
        if (chosen.Count == 1 && Locate(doc, chosen[0]) is { } exact) candidates.Add(exact);
        foreach (var anchor in chosen.Take(40))
        {
            if (!Uri.TryCreate(pageUri, anchor.GetAttribute("href"), out var url) || url.Scheme is not ("http" or "https")) continue;
            var firstSegment = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (firstSegment is { Length: >= 2 and <= 12 } && firstSegment.All(char.IsLetter)) candidates.Add($"a[href*='{url.Host}/{firstSegment}']");
            candidates.Add($"a[href*='{url.Host}']");
        }

        // The best selector reads every example and only download-looking links. Of those, the narrowest one that still covers
        // a group (two or more links) wins: it is the examples' own group, not everything else in the text around them.
        var valid = new List<(string Css, int Count)>();
        foreach (var candidate in candidates.Distinct())
        {
            var matches = SafeAll(doc, candidate);
            if (matches.Count == 0 || chosen.Any(c => !matches.Contains(c))) continue;
            if (matches.Count(Plausible) < matches.Count * 0.8) continue;
            valid.Add((candidate, matches.Count));
        }
        var pool = valid.Where(v => v.Count >= 2).ToList();
        if (pool.Count == 0) pool = valid;
        var best = pool.OrderBy(v => v.Count).ThenBy(v => v.Css.Length).Select(v => v.Css).FirstOrDefault();

        // Nothing general fits: the hosts of the links themselves (for plain links), else exactly where the buttons are.
        best ??= chosen.All(a => a.LocalName == "a" && a.HasAttribute("href"))
            ? string.Join(", ", chosenHosts.Select(h => $"a[href*='{h}']"))
            : string.Join(", ", chosen.Select(c => Locate(doc, c)).OfType<string>().Distinct());
        if (best.Length == 0) return null;

        var read = SiteReaderParser.Urls(doc, best, pageUri);
        if (read.Count == 0) return null;
        sample = DescribeLinks(read);
        return best;
    }

    /// <summary>The page's links that one example means: its address (whole, or the part given), or the words on its button.</summary>
    private static List<IElement> MatchExample(IReadOnlyList<IElement> anchors, string example, Uri pageUri)
    {
        var wanted = example.Trim();
        if (wanted.Length == 0) return [];

        static string Trimmed(string url) => url.Trim().TrimEnd('/');
        var looksLikeAddress = wanted.Contains("://", StringComparison.Ordinal) || wanted.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) ||
                               (wanted.Contains('/') || (wanted.Contains('.') && !wanted.Contains(' ')));
        if (looksLikeAddress)
        {
            string Raw(IElement a) => a.GetAttribute("href") ?? string.Empty;
            string Full(IElement a) => SiteReaderParser.UrlOfElement(a, pageUri) ?? Raw(a);
            var exact = anchors.Where(a => string.Equals(Trimmed(Raw(a)), Trimmed(wanted), StringComparison.OrdinalIgnoreCase) ||
                                           string.Equals(Trimmed(Full(a)), Trimmed(wanted), StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count > 0) return exact;
            return anchors.Where(a => Raw(a).Contains(wanted, StringComparison.OrdinalIgnoreCase) ||
                                      Full(a).Contains(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        // The words on the button: its text, or a button's value / title.
        static string Words(IElement a) =>
            Clean(a.TextContent) is { Length: > 0 } text ? text : Clean(a.GetAttribute("value") ?? a.GetAttribute("title") ?? a.GetAttribute("aria-label"));
        var sameText = anchors.Where(a => string.Equals(Words(a), wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        return sameText.Count > 0
            ? sameText
            : anchors.Where(a => Words(a).Contains(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>"6 links: pixeldrain.com/u/abc, gofile.io/d/xyz …" — what a selector reads, short enough to check at a glance.</summary>
    private static string DescribeLinks(IReadOnlyList<string> urls)
    {
        var shown = urls.Take(6).Select(u => Uri.TryCreate(u, UriKind.Absolute, out var x) ? Shorten(x.Host + x.PathAndQuery, 48) : Shorten(u, 48));
        return $"{urls.Count} links: {string.Join(", ", shown)}{(urls.Count > 6 ? " …" : string.Empty)}";
    }

    /// <summary>The text around the link (its line or list item) names a file host: "BuzzHeavier – Click Here".</summary>
    private static bool NamesAFileHost(IElement anchor)
    {
        for (var block = anchor.ParentElement; block is not null; block = block.ParentElement)
        {
            if (block.LocalName is not ("p" or "li" or "div" or "td" or "tr" or "dd" or "span" or "strong" or "b")) continue;
            var text = Clean(block.TextContent);
            if (text.Length > 240) return false;
            foreach (var host in FileHosts.All)
            {
                if (text.Contains(host.DisplayName, StringComparison.OrdinalIgnoreCase)) return true;
                foreach (var domain in host.Domains)
                    if (text.Contains(domain.Split('.')[0], StringComparison.OrdinalIgnoreCase) && domain.Split('.')[0].Length >= 5) return true;
            }
            if (block.LocalName is "p" or "li" or "td" or "tr") return false;
        }
        return false;
    }

    // ---------------------------------------------------------------------------------------------
    // Things the user pointed at (words on the page, a piece of View source, or a CSS selector)
    // ---------------------------------------------------------------------------------------------

    /// <summary>Anything a download can be: a link, a button (its own address, its "onclick", or the form it sends), a form.</summary>
    private const string Clickables =
        "a[href], a[data-href], a[data-url], button, input[type='submit'], input[type='button'], [onclick], [data-href], [data-url], [data-download], form[action]";

    /// <summary>
    /// The elements one pointer means: a CSS selector that matches, a piece of the page's code (matched by its id, address,
    /// classes and words), an address, or the words shown on the page (the smallest element holding exactly them).
    /// </summary>
    internal static List<IElement> FindPointed(IDocument doc, string pointer)
    {
        var wanted = pointer.Trim();
        if (wanted.Length == 0) return [];
        if (wanted.Contains('<')) return FromSnippet(doc, wanted);

        // A CSS selector ("h1.title", ".dev a", "#download"): used when it matches something.
        if (!wanted.Contains(' ') || Regex.IsMatch(wanted, @"^[.#\[]|[>\[\]:]"))
        {
            var bySelector = SafeAll(doc, wanted);
            if (bySelector.Count is > 0 and <= 300 && (Regex.IsMatch(wanted, @"^[.#\[]|[.#\[\]>:]") || bySelector.Count <= 3)) return bySelector;
        }

        // An address: the element that links to it or shows it.
        if (wanted.Contains("://", StringComparison.Ordinal) || wanted.StartsWith('/'))
        {
            var byAddress = SafeAll(doc, "[href], [src], [content], [action], [data-src], [data-href]")
                .Where(e => new[] { "href", "src", "content", "action", "data-src", "data-href" }
                    .Any(a => e.GetAttribute(a) is { } v && v.Contains(wanted.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (byAddress.Count > 0) return byAddress;
        }

        // Words on the page: the deepest element holding exactly them; else its alt / title / value; else the shortest that
        // contains them.
        var clean = Clean(wanted);
        var all = SafeAll(doc, "body *").Where(e => e.LocalName is not ("script" or "style" or "noscript")).ToList();
        var exact = all.Where(e => string.Equals(Clean(e.TextContent), clean, StringComparison.OrdinalIgnoreCase)).ToList();
        exact = exact.Where(e => !exact.Any(other => !ReferenceEquals(other, e) && e.Contains(other))).ToList();
        if (exact.Count > 0) return exact;
        var byAttribute = all.Where(e => new[] { "alt", "title", "value", "aria-label" }
            .Any(a => string.Equals(Clean(e.GetAttribute(a)), clean, StringComparison.OrdinalIgnoreCase))).ToList();
        if (byAttribute.Count > 0) return byAttribute;
        var containing = all.Where(e => Clean(e.TextContent).Contains(clean, StringComparison.OrdinalIgnoreCase) && Clean(e.TextContent).Length <= clean.Length + 60)
            .ToList();
        return containing.Where(e => !containing.Any(other => !ReferenceEquals(other, e) && e.Contains(other))).Take(5).ToList();
    }

    /// <summary>
    /// The page's element(s) a piece of its code means (copied from View source): the same tag, scored by its id, its
    /// addresses and other attributes, its classes and its words. Only the best-scoring ones.
    /// </summary>
    private static List<IElement> FromSnippet(IDocument doc, string snippet)
    {
        List<IElement> parsed;
        try
        {
            var parser = new AngleSharp.Html.Parser.HtmlParser();
            var context = doc.Body ?? doc.DocumentElement;
            parsed = parser.ParseFragment(snippet, context).OfType<IElement>()
                .SelectMany(e => new[] { e }.Concat(e.QuerySelectorAll("*"))).ToList();
        }
        catch (Exception)
        {
            return [];
        }
        if (parsed.Count == 0) return [];

        // The part of the snippet that matters: the element with an address (a link, a button's form), else the first one.
        var model = parsed.FirstOrDefault(e => e.HasAttribute("href") || e.HasAttribute("action") || e.HasAttribute("onclick") || e.HasAttribute("data-href"))
                    ?? parsed.FirstOrDefault(e => e.Id is { Length: > 0 } || e.ClassList.Length > 0)
                    ?? parsed[0];
        var words = Clean(model.TextContent);
        var classes = model.ClassList.ToHashSet(StringComparer.Ordinal);

        var scored = new List<(IElement Element, int Score)>();
        foreach (var element in SafeAll(doc, model.LocalName))
        {
            var score = 0;
            if (model.Id is { Length: > 0 } id && element.Id == id) score += 10;
            foreach (var attribute in model.Attributes)
            {
                if (attribute.Name is "class" or "style" or "id") continue;
                if (element.GetAttribute(attribute.Name) is { } value && value == attribute.Value)
                    score += attribute.Name is "href" or "src" or "action" or "onclick" or "data-href" or "data-url" or "name" ? 5 : 2;
            }
            score += element.ClassList.Count(classes.Contains) * 2;
            if (words.Length > 0 && string.Equals(Clean(element.TextContent), words, StringComparison.OrdinalIgnoreCase)) score += 3;
            if (score >= 2) scored.Add((element, score));
        }
        if (scored.Count == 0) return [];
        var top = scored.Max(x => x.Score);
        return scored.Where(x => x.Score == top).Select(x => x.Element).ToList();
    }

    /// <summary>Puts what the user pointed at on a game's page into the recipe, over the teacher's own guesses.</summary>
    private static void ApplyPagePointers(IDocument doc, Uri pageUri, PageRecipe recipe, LearnHints hints, List<LearnedField> fields, List<string> notes)
    {
        foreach (var (field, pointer) in hints.Pointers)
        {
            if (string.IsNullOrWhiteSpace(pointer) || !LearnHints.PageFields.Contains(field, StringComparer.OrdinalIgnoreCase)) continue;
            var key = LearnHints.PageFields.First(f => f.Equals(field, StringComparison.OrdinalIgnoreCase));
            var element = FindPointed(doc, pointer).FirstOrDefault();
            if (element is null)
            {
                notes.Add($"{key}: \"{Shorten(pointer.Trim(), 60)}\" isn't in the pasted page, so my own guess is kept.");
                continue;
            }

            string? spec;
            string? sample;
            if (key == "cover")
            {
                var picture = element.LocalName is "img" or "meta" ? element : element.QuerySelector("img") ?? element;
                spec = Locate(doc, picture);
                sample = spec is null ? null : SiteReaderParser.Url(doc, spec, pageUri);
            }
            else if (key == "genres")
            {
                var holder = element.QuerySelector("a") is not null ? element : element.ParentElement?.QuerySelector("a") is not null && Clean(element.ParentElement.TextContent).Length < 200 ? element.ParentElement : element;
                spec = Locate(doc, holder) is { } at ? (holder.QuerySelector("a") is not null ? at + " a" : at) : null;
                sample = spec is null ? null : string.Join(", ", SiteReaderParser.Texts(doc, spec).Take(4));
            }
            else if (key == "description")
            {
                // Words from the description: the block that holds them.
                var block = element.Closest("article, section, div, td") ?? element;
                spec = Locate(doc, block);
                sample = spec is null ? null : Shorten(Clean(block.TextContent), 90);
            }
            else
            {
                spec = Locate(doc, element) is { } at ? WithOwnText(element, at) : null;
                sample = spec is null ? null : SiteReaderParser.Text(doc, spec);
            }

            if (spec is null || string.IsNullOrWhiteSpace(sample))
            {
                notes.Add($"{key}: found \"{Shorten(pointer.Trim(), 60)}\" but couldn't make a selector that reads it.");
                continue;
            }
            switch (key)
            {
                case "title": recipe.Title = spec; break;
                case "cover": recipe.Cover = spec; break;
                case "description": recipe.Description = spec; break;
                default: SetPageField(recipe, key, spec); break;
            }
            fields.RemoveAll(f => f.Area == "page" && f.Field == key);
            fields.Add(new LearnedField("page", key, spec, sample + "  (as you pointed)"));
        }
    }

    /// <summary>Puts what the user pointed at on a list into the recipe: one game's name, and the next-page button.</summary>
    private static void ApplyListPointers(IDocument doc, Uri pageUri, ListingRecipe recipe, LearnHints hints, List<LearnedField> fields, List<string> notes)
    {
        if (hints.Pointers.TryGetValue("listGame", out var game) && !string.IsNullOrWhiteSpace(game))
        {
            var element = FindPointed(doc, game).FirstOrDefault(e => SafeAll(doc, recipe.Item).Any(card => card.Contains(e)));
            var card = element is null ? null : SafeAll(doc, recipe.Item).FirstOrDefault(c => c.Contains(element));
            if (element is not null && card is not null && Locate(card, element, 3) is { } inCard)
            {
                recipe.Title = WithOwnText(element, inCard);
                fields.RemoveAll(f => f.Area == "list" && f.Field == "title");
                fields.Add(new LearnedField("list", "title", recipe.Title, SiteReaderParser.Text(card, recipe.Title) + "  (as you pointed)"));
            }
            else notes.Add($"list title: \"{Shorten(game.Trim(), 60)}\" isn't inside a game of the list.");
        }

        if (hints.Pointers.TryGetValue("nextPage", out var next) && !string.IsNullOrWhiteSpace(next))
        {
            var button = FindPointed(doc, next).Select(e => e.LocalName == "a" ? e : e.Closest("a") ?? e.QuerySelector("a")).OfType<IElement>().FirstOrDefault();
            if (button is not null && Locate(doc, button) is { } selector && SiteReaderParser.Url(doc, selector, pageUri) is { } nextUrl)
            {
                recipe.NextPage = selector;
                fields.RemoveAll(f => f.Area == "list" && f.Field == "nextPage");
                fields.Add(new LearnedField("list", "nextPage", selector, nextUrl + "  (as you pointed)"));
                notes.RemoveAll(n => n.StartsWith("nextPage:", StringComparison.Ordinal));
            }
            else notes.Add($"nextPage: \"{Shorten(next.Trim(), 60)}\" isn't a link in the pasted page. Type the second page's address instead.");
        }
    }

    /// <summary>A list's games found from one game's name: the repeated block around it (its card).</summary>
    private static Cards? CardsFromPointer(IDocument doc, string pointer, List<string> notes)
    {
        foreach (var element in FindPointed(doc, pointer))
        {
            for (var card = element; card.ParentElement is { } parent && card.LocalName is not ("body" or "html"); card = parent)
            {
                var signature = card.LocalName + "." + string.Join('.', GoodClasses(card).OrderBy(c => c, StringComparer.Ordinal));
                var siblings = parent.Children.Where(c => c.LocalName + "." + string.Join('.', GoodClasses(c).OrderBy(x => x, StringComparer.Ordinal)) == signature).ToList();
                if (siblings.Count < 3) continue;

                var classes = GoodClasses(card).ToList();
                string? item = null;
                foreach (var candidate in classes.Count > 0 ? new[] { $"{card.LocalName}.{classes[0]}", $".{classes[0]}" } : Array.Empty<string>())
                {
                    var count = SafeAll(doc, candidate).Count;
                    if (count >= siblings.Count && count <= siblings.Count * 2) { item = candidate; break; }
                }
                if (item is null && Locate(doc, parent) is { } parentSelector) item = $"{parentSelector} > {card.LocalName}";
                if (item is not null) return new Cards(item, card, siblings.Count);
            }
        }
        notes.Add($"list: couldn't find a repeated game block around \"{Shorten(pointer.Trim(), 60)}\".");
        return null;
    }

    /// <summary>The second page's address → every page's ("…/page/2/" → "…/page/{page}/"), made complete with the site.</summary>
    internal static string? PageTemplate(string pageTwo, Uri? pageUri)
    {
        var text = pageTwo.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
            text = text.StartsWith('/') && pageUri is not null ? new Uri(pageUri, text).AbsoluteUri : "https://" + text.TrimStart('/');
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        var full = uri.AbsoluteUri;
        if (BrowseTemplate(full) is { } template) return template;
        // "…/games/2", "…-p2.html", "?pg=2": the last 2 in the address.
        var start = full.IndexOf(uri.PathAndQuery, StringComparison.Ordinal); // never the 2 in a domain ("example2.com")
        if (start < 0) return null;
        var match = Regex.Match(full[start..], @"(?<![0-9])2(?![0-9])(?!.*(?<![0-9])2(?![0-9]))");
        if (!match.Success) return null;
        var at = start + match.Index;
        return full[..at] + "{page}" + full[(at + 1)..];
    }

    /// <summary>Only the second page's address was given (no page pasted): a reader that reads the list page by page.</summary>
    private static SiteReaderLesson PagesOnly(string pageTwo, string? address)
    {
        var template = PageTemplate(pageTwo, null) ?? throw new ArgumentException(
            "That address has no page number to count up. Type the list's second page, like example.com/games/page/2/.");
        var host = new Uri(template.Replace("{page}", "1", StringComparison.Ordinal)).Host;
        var domain = host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
        var reader = new SiteReader { Name = domain, Domains = [domain], BrowseUrl = template };
        var fields = new List<LearnedField> { new("site", "browseUrl", template, "page 1, 2, 3 … by number") };
        var notes = new List<string> { "Saved the page addresses. Paste the list page's HTML too, so it knows where the games are on each page." };
        return new SiteReaderLesson(reader, fields, notes, false, true);
    }

    // ---------------------------------------------------------------------------------------------
    // A list of games
    // ---------------------------------------------------------------------------------------------

    private sealed record Cards(string Item, IElement Example, int Count);

    private static Cards? FindCards(IDocument doc)
    {
        Cards? best = null;
        foreach (var parent in SafeAll(doc, "div, ul, ol, section, main, tbody"))
        {
            if (InChrome(parent)) continue;
            var kids = parent.Children.Where(c => c.LocalName == "a" || c.QuerySelector("a[href]") is not null).ToList();
            if (kids.Count < 4) continue;

            var groups = kids
                .GroupBy(k => k.LocalName + "." + string.Join('.', GoodClasses(k).OrderBy(c => c, StringComparer.Ordinal)))
                .OrderByDescending(g => g.Count())
                .First();
            var members = groups.ToList();
            if (members.Count < 4 || (best is not null && members.Count <= best.Count)) continue;

            // Cards have a picture or a heading, and a link text.
            var cardLike = members.Count(m => m.QuerySelector("img") is not null || m.QuerySelector("h1, h2, h3, h4, h5") is not null);
            if (cardLike < members.Count * 0.7) continue;

            var example = members[0];
            var classes = GoodClasses(example).ToList();
            string? item = null;
            if (classes.Count > 0)
            {
                foreach (var candidate in new[] { $"{example.LocalName}.{classes[0]}", $".{classes[0]}", $"{example.LocalName}.{string.Join('.', classes.Take(2))}" })
                {
                    var count = SafeAll(doc, candidate).Count;
                    if (count >= members.Count && count <= members.Count * 2)
                    {
                        item = candidate;
                        break;
                    }
                }
            }
            if (item is null && Locate(doc, parent) is { } parentSelector) item = $"{parentSelector} > {example.LocalName}";
            if (item is null) continue;

            best = new Cards(item, example, members.Count);
        }
        return best;
    }

    private static ListingRecipe LearnListing(IDocument doc, Uri pageUri, Cards cards, List<LearnedField> fields, List<string> notes)
    {
        var recipe = new ListingRecipe { Item = cards.Item };
        var card = cards.Example;
        fields.Add(new LearnedField("list", "item", cards.Item, $"{cards.Count} games"));

        var heading = SafeAll(card, "h1, h2, h3, h4, h5").FirstOrDefault(h => Clean(h.TextContent).Length >= 2);
        if (heading is not null && Locate(card, heading, 2) is { } titleSelector)
        {
            recipe.Title = WithOwnText(heading, titleSelector);
            fields.Add(new LearnedField("list", "title", recipe.Title, SiteReaderParser.Text(card, recipe.Title)));
        }

        if (card.QuerySelector("img") is not null)
        {
            recipe.Cover = "img";
            fields.Add(new LearnedField("list", "cover", "img", SiteReaderParser.Url(card, "img", pageUri)));
        }

        if (card.QuerySelector("time[datetime]") is not null)
        {
            recipe.Date = "time@datetime";
            fields.Add(new LearnedField("list", "date", recipe.Date, SiteReaderParser.Text(card, recipe.Date)));
        }

        foreach (var candidate in new[]
                 {
                     "a[rel~='next']", "link[rel='next']", "a.next.page-numbers", "a.next", "li.next a", ".next a",
                     ".pagination .next a", ".nav-links .next", "a[aria-label='Next']",
                 })
        {
            if (SiteReaderParser.Url(doc, candidate, pageUri) is not { } next) continue;
            recipe.NextPage = candidate;
            fields.Add(new LearnedField("list", "nextPage", candidate, next));
            if (BrowseTemplate(next) is { } template)
                notes.Add($"Suggested \"browseUrl\" (add it to the file when it opens page after page): {template}");
            break;
        }
        if (recipe.NextPage is null) notes.Add("nextPage: no \"next page\" link found on this page (fine if it's the last page).");

        var search = SafeAll(doc, "input[name]").FirstOrDefault(i =>
            Regex.IsMatch(i.GetAttribute("name") ?? string.Empty, @"^(s|q|query|search|keyword|keywords|term)$", Ci) &&
            i.GetAttribute("type") is null or "text" or "search");
        if (search?.Closest("form") is { } form)
        {
            var action = form.GetAttribute("action");
            var target = string.IsNullOrWhiteSpace(action) ? pageUri : Uri.TryCreate(pageUri, action, out var resolved) ? resolved : pageUri;
            var separator = target.AbsoluteUri.Contains('?') ? "&" : "?";
            notes.Add($"Suggested \"searchUrl\" (add it to the file after checking it in your browser): {target.GetLeftPart(UriPartial.Path)}{separator}{search.GetAttribute("name")}={{query}}");
        }
        return recipe;
    }

    /// <summary>"…/page/2/" or "…?page=2" → the same with {page}.</summary>
    private static string? BrowseTemplate(string nextUrl)
    {
        var path = Regex.Replace(nextUrl, @"/page/\d+", "/page/{page}", Ci);
        if (path != nextUrl) return path;
        var query = Regex.Replace(nextUrl, @"([?&](?:page|paged|p|pg)=)\d+", "$1{page}", Ci);
        return query != nextUrl ? query : null;
    }

    // =============================================================================================
    // Selector building
    // =============================================================================================

    /// <summary>
    /// The shortest readable CSS selector whose first match in <paramref name="root"/> is <paramref name="target"/>:
    /// its own id / classes, then with an ancestor's, then with its position. Falls back to a full path.
    /// </summary>
    private static string? Locate(IParentNode root, IElement target, int maxDepth = 4)
    {
        bool Hits(string css)
        {
            try { return ReferenceEquals(root.QuerySelector(css), target); }
            catch (DomException) { return false; }
        }

        var own = Bases(target).ToList();
        foreach (var b in own)
            if (Hits(b)) return b;

        var chain = new List<IElement>();
        for (var p = target.ParentElement; p is not null && chain.Count < maxDepth && !ReferenceEquals(p, root) && p.LocalName is not ("html" or "body"); p = p.ParentElement)
            chain.Add(p);

        var withPosition = own.Append(NthBase(target)).ToList();
        for (var k = 0; k < chain.Count; k++)
        {
            var between = string.Join(' ', Enumerable.Range(0, k).Reverse().Select(i => chain[i].LocalName));
            foreach (var ancestor in Bases(chain[k]).Where(b => b.Contains('.') || b.StartsWith('#')).Take(2))
            {
                foreach (var b in withPosition)
                {
                    var css = between.Length == 0 ? $"{ancestor} {b}" : $"{ancestor} {between} {b}";
                    if (Hits(css)) return css;
                    if (between.Length == 0)
                    {
                        css = $"{ancestor} > {b}";
                        if (Hits(css)) return css;
                    }
                }
            }
        }

        var path = PathTo(root, target);
        return Hits(path) ? path : null;
    }

    private static IEnumerable<string> Bases(IElement element)
    {
        var tag = element.LocalName;
        if (element.Id is { Length: > 0 and <= 30 } id && CssIdent.IsMatch(id) && !Regex.IsMatch(id, @"\d{3,}")) yield return "#" + id;
        var classes = GoodClasses(element).ToList();
        foreach (var c in classes) yield return $"{tag}.{c}";
        foreach (var c in classes) yield return "." + c;
        if (classes.Count >= 2) yield return $"{tag}.{classes[0]}.{classes[1]}";
        yield return tag;
    }

    private static IEnumerable<string> GoodClasses(IElement element) =>
        element.ClassList.Where(c => c.Length is >= 3 and <= 32 && CssIdent.IsMatch(c) && !BadClass.IsMatch(c));

    private static string NthBase(IElement element) => $"{element.LocalName}:nth-child({IndexOf(element)})";

    private static int IndexOf(IElement element)
    {
        var n = 1;
        for (var p = element.PreviousElementSibling; p is not null; p = p.PreviousElementSibling) n++;
        return n;
    }

    private static string PathTo(IParentNode root, IElement target)
    {
        var parts = new List<string>();
        IElement? e = target;
        while (e is not null && !ReferenceEquals(e, root))
        {
            if (e.Id is { Length: > 0 } id && CssIdent.IsMatch(id))
            {
                parts.Add("#" + id);
                break;
            }
            parts.Add(NthBase(e));
            if (e.LocalName == "body") break;
            e = e.ParentElement;
        }
        parts.Reverse();
        return string.Join(" > ", parts);
    }

    // =============================================================================================
    // Small helpers
    // =============================================================================================

    private static List<IElement> SafeAll(IParentNode root, string css)
    {
        try { return root.QuerySelectorAll(css).ToList(); }
        catch (DomException) { return []; }
    }

    /// <summary>In the menu, a footer, a side column, a form or a comment area: not the game's own content.</summary>
    private static bool InChrome(IElement element)
    {
        for (IElement? e = element; e is not null && e.LocalName is not ("body" or "html"); e = e.ParentElement)
        {
            if (e.LocalName is "nav" or "footer" or "aside" or "form" or "script" or "style" or "noscript") return true;
            if (e.ClassName is { Length: > 0 } cls && ChromeClass.IsMatch(cls)) return true;
            if (e.Id is { Length: > 0 } id && ChromeClass.IsMatch(id)) return true;
        }
        return false;
    }

    private static string OwnText(IElement element) =>
        string.Concat(element.ChildNodes.OfType<IText>().Select(t => t.Data));

    /// <summary>Adds <c>@owntext</c> when the element holds more than its own words (a title with an info line inside).</summary>
    private static string WithOwnText(IElement element, string selector)
    {
        var own = Clean(OwnText(element));
        return own.Length >= 2 && own.Length < Clean(element.TextContent).Length ? selector + "@owntext" : selector;
    }

    private static string Clean(string? text) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty : SpacesRx.Replace(WebUtility.HtmlDecode(text), " ").Trim();

    private static string Shorten(string text, int length) => text.Length <= length ? text : text[..length].TrimEnd() + "…";

    // =============================================================================================
    // Saving
    // =============================================================================================

    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The reader as the file's text, with the lesson's suggestions as comments at the top.</summary>
    public static string ToJson(SiteReader reader, IEnumerable<string> comments)
    {
        var header = string.Concat(comments.Select(c => "// " + c.Replace('\n', ' ') + Environment.NewLine));
        return "// SeaRaider site reader, written from a pasted page. Edit freely; see README.txt in this folder." + Environment.NewLine +
               header + JsonSerializer.Serialize(reader, WriteOptions) + Environment.NewLine;
    }

    /// <summary>Puts what was learned on top of an existing reader: learned selectors replace the same fields, the rest stay.</summary>
    public static SiteReader Merge(SiteReader existing, SiteReader learned)
    {
        if (!string.IsNullOrWhiteSpace(learned.BrowseUrl)) existing.BrowseUrl = learned.BrowseUrl;
        if (!string.IsNullOrWhiteSpace(learned.SearchUrl)) existing.SearchUrl = learned.SearchUrl;
        existing.Page = Combine(existing.Page, learned.Page);
        // How downloads are reached: the newest lesson decides (one way or the other, never both).
        if (learned.Page?.DownloadByButton == true && existing.Page is not null)
        {
            existing.Page.DownloadByButton = true;
            existing.Page.DownloadUrl = null;
        }
        else if (!string.IsNullOrWhiteSpace(learned.Page?.DownloadUrl) && existing.Page is not null) existing.Page.DownloadByButton = null;
        existing.Listing = Combine(existing.Listing, learned.Listing);
        return existing;
    }

    private static T? Combine<T>(T? old, T? learned) where T : class, new()
    {
        if (learned is null) return old;
        if (old is null) return learned;
        foreach (var property in typeof(T).GetProperties().Where(p => p.PropertyType == typeof(string) && p.CanWrite))
            if (property.GetValue(learned) is string value && value.Length > 0) property.SetValue(old, value);
        return old;
    }
}
