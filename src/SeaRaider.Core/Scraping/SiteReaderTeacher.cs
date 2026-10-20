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
    public static async Task<SiteReaderLesson> LearnAsync(string html, string? address, CancellationToken ct = default)
    {
        html = (html ?? string.Empty).Trim();
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
        return Learn(doc, pageUri, domain);
    }

    // =============================================================================================
    // The lesson
    // =============================================================================================

    private static SiteReaderLesson Learn(IDocument doc, Uri pageUri, string domain)
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

        // A game page has a body of text; a list has many cards. A page wins when it has real text.
        var isPage = content is not null && contentScore >= 200;
        var isList = !isPage && cards is not null;

        if (isPage) reader.Page = LearnPage(doc, pageUri, content!, fields, notes);
        else if (isList) reader.Listing = LearnListing(doc, pageUri, cards!, fields, notes);
        else
            notes.Add("I couldn't tell what kind of page this is. It needs a game's page (a title and a block of text) " +
                      "or a list of at least four games. Paste a complete page, not a part of it.");

        if (isPage && cards is { Count: >= 8 })
            notes.Add("This page also has a grid of games (probably \"related games\"). Paste the site's own list page to teach it the list.");
        if (isPage) notes.Add("Learned the game page. Paste the site's list of games next to teach it the list too.");
        if (isList) notes.Add("Learned the list. Paste a game's page next to teach it the game pages too.");

        return new SiteReaderLesson(reader, fields, notes, isPage, isList);
    }

    // ---------------------------------------------------------------------------------------------
    // A game's page
    // ---------------------------------------------------------------------------------------------

    private static PageRecipe LearnPage(IDocument doc, Uri pageUri, IElement content, List<LearnedField> fields, List<string> notes)
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
        var downloads = FindDownloads(doc, pageUri, content, out var downloadSample);
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
        existing.Page = Combine(existing.Page, learned.Page);
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
