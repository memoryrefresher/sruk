using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AngleSharp;
using AngleSharp.Dom;
using SeaRaider.Core.Catalog;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Scraping;

/// <summary>
/// Reads the games off a "coming soon" page the user added to the store. Tries, in order:
/// <list type="number">
///   <item><b>Steam</b> lists (search results, "coming soon", "popular upcoming"): title, release date and app id, whose
///         header and library hero are Steam's own high-resolution art.</item>
///   <item><b>itch.io</b> listings (<c>.game_cell</c>): title, cover, teaser, genre.</item>
///   <item><b>RSS / Atom feeds</b>: item titles, links and their pictures.</item>
///   <item><b>Structured data</b> (JSON-LD <c>VideoGame</c> / <c>ItemList</c>).</item>
///   <item>Any other page: repeated "cards" — a link with a picture and a heading — the way game sites lay out lists.</item>
/// </list>
/// </summary>
public static partial class UpcomingListParser
{
    public const int MaxItems = 60;

    [GeneratedRegex(@"/app/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SteamAppRegex();

    [GeneratedRegex(@"\b(?:coming soon|to be announced|tba|tbd|q[1-4]\s*\d{4}|(?:early|mid|late)\s+\d{4}|(?:jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?\s+\d{1,2},?\s+\d{4}|\d{1,2}\s+(?:jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?,?\s+\d{4}|(?:jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\s+\d{4}|\d{4}-\d{2}-\d{2})\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseTextRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacesRegex();

    /// <summary>Words that mark navigation and site furniture, never a game.</summary>
    private static readonly string[] NotAGame =
        ["log in", "login", "sign in", "sign up", "register", "subscribe", "newsletter", "privacy", "cookie", "terms", "contact",
         "about us", "next page", "previous", "load more", "view all", "see all", "read more", "home", "menu", "search", "advertis"];

    /// <summary>Parses a fetched page (HTML or a feed).</summary>
    public static async Task<List<UpcomingRelease>> ParseAsync(string content, Uri pageUri, Guid sourceId, CancellationToken ct)
    {
        var trimmed = content.TrimStart();
        if (trimmed.StartsWith("<?xml", StringComparison.Ordinal) || trimmed.StartsWith("<rss", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("<feed", StringComparison.OrdinalIgnoreCase))
        {
            var fromFeed = ParseFeed(content, pageUri, sourceId);
            if (fromFeed.Count > 0) return fromFeed;
        }

        var context = BrowsingContext.New(Configuration.Default);
        using var doc = await context.OpenAsync(req => req.Content(content).Address(pageUri.AbsoluteUri), ct).ConfigureAwait(false);
        return Parse(doc, pageUri, sourceId);
    }

    public static List<UpcomingRelease> Parse(IDocument doc, Uri pageUri, Guid sourceId)
    {
        foreach (var strategy in new Func<IDocument, Uri, Guid, List<UpcomingRelease>>[] { Steam, Itch, JsonLd, Cards })
        {
            var found = Finish(strategy(doc, pageUri, sourceId));
            if (found.Count > 0) return found;
        }
        return [];
    }

    // ---- Steam ------------------------------------------------------------------------------------

    private static List<UpcomingRelease> Steam(IDocument doc, Uri pageUri, Guid sourceId)
    {
        var list = new List<UpcomingRelease>();
        foreach (var row in doc.QuerySelectorAll("a.search_result_row, a.tab_item, .store_capsule[href*='/app/'], a[href*='/app/'].salepreviewwidgets_TitleCtn"))
        {
            var href = row.GetAttribute("href");
            var appId = row.GetAttribute("data-ds-appid")?.Split(',')[0];
            if (string.IsNullOrEmpty(appId) && href is not null && SteamAppRegex().Match(href) is { Success: true } m) appId = m.Groups[1].Value;
            var title = Clean(row.QuerySelector(".title, .tab_item_name, .StoreSaleWidgetTitle")?.TextContent);
            if (title.Length == 0 || string.IsNullOrEmpty(appId)) continue;

            var release = Clean(row.QuerySelector(".search_released, .release_date")?.TextContent);
            // The row's own capsule (a real address) until Steam's API gives the full-size art: new apps keep their
            // pictures under hashed folders, so ".../apps/{id}/header.jpg" can't be guessed.
            var capsule = row.QuerySelector("img") is { } img ? BestSource(img) : null;
            list.Add(new UpcomingRelease
            {
                SourceId = sourceId,
                Title = title,
                PageUrl = Resolve(href, pageUri),
                SteamAppId = int.TryParse(appId, out var id) ? id : null,
                ImageUrl = capsule is null ? null : Resolve(capsule, pageUri),
                ReleaseText = release.Length > 0 ? release : "Coming soon",
                Tags = row.QuerySelectorAll(".top_tag, .tab_item_top_tags span").Select(t => Clean(t.TextContent).Trim(',', ' ')).Where(t => t.Length > 0).Take(5).ToList(),
            });
        }
        return list;
    }

    // ---- itch.io ----------------------------------------------------------------------------------

    private static List<UpcomingRelease> Itch(IDocument doc, Uri pageUri, Guid sourceId)
    {
        var list = new List<UpcomingRelease>();
        foreach (var cell in doc.QuerySelectorAll(".game_cell"))
        {
            var anchor = cell.QuerySelector("a.title[href], .game_title a[href], a.game_link[href]");
            var title = Clean(anchor?.TextContent);
            if (title.Length == 0) title = Clean(cell.QuerySelector(".game_title")?.TextContent);
            if (title.Length == 0) continue;

            var thumb = cell.QuerySelector(".game_thumb");
            var img = cell.QuerySelector(".game_thumb img, img");
            var image = thumb?.GetAttribute("data-background_image") ?? img?.GetAttribute("data-lazy_src") ?? img?.GetAttribute("src");
            var genre = Clean(cell.QuerySelector(".game_genre")?.TextContent);
            list.Add(new UpcomingRelease
            {
                SourceId = sourceId,
                Title = title,
                PageUrl = Resolve(anchor?.GetAttribute("href"), pageUri),
                ImageUrl = ResolveImage(image, pageUri),
                Description = NullIfEmpty(Clean(cell.QuerySelector(".game_text")?.TextContent)),
                Developer = NullIfEmpty(Clean(cell.QuerySelector(".game_author")?.TextContent)),
                ReleaseText = FindReleaseText(cell.TextContent) ?? "Coming soon",
                Tags = genre.Length > 0 ? [genre] : [],
            });
        }
        return list;
    }

    // ---- JSON-LD ----------------------------------------------------------------------------------

    private static List<UpcomingRelease> JsonLd(IDocument doc, Uri pageUri, Guid sourceId)
    {
        var list = new List<UpcomingRelease>();
        foreach (var script in doc.QuerySelectorAll("script[type='application/ld+json']"))
        {
            try
            {
                using var json = JsonDocument.Parse(script.TextContent);
                Collect(json.RootElement, list, pageUri, sourceId, depth: 0);
            }
            catch (JsonException)
            {
                // not every block is valid
            }
        }
        return list;
    }

    private static void Collect(JsonElement element, List<UpcomingRelease> list, Uri pageUri, Guid sourceId, int depth)
    {
        if (depth > 6 || list.Count >= MaxItems) return;
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) Collect(item, list, pageUri, sourceId, depth + 1);
                return;
            case JsonValueKind.Object:
                var type = element.TryGetProperty("@type", out var t) ? t.ToString() : string.Empty;
                if (type.Contains("VideoGame", StringComparison.OrdinalIgnoreCase) || type.Contains("SoftwareApplication", StringComparison.OrdinalIgnoreCase))
                {
                    var name = Clean(Str(element, "name"));
                    if (name.Length > 0)
                        list.Add(new UpcomingRelease
                        {
                            SourceId = sourceId,
                            Title = name,
                            PageUrl = Resolve(Str(element, "url"), pageUri),
                            ImageUrl = ResolveImage(ImageOf(element), pageUri),
                            Description = NullIfEmpty(Clean(Str(element, "description"))),
                            ReleaseText = NullIfEmpty(Clean(Str(element, "datePublished") ?? Str(element, "releaseDate"))) ?? "Coming soon",
                        });
                }
                foreach (var property in element.EnumerateObject())
                    if (property.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                        Collect(property.Value, list, pageUri, sourceId, depth + 1);
                return;
        }
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? ImageOf(JsonElement element)
    {
        if (!element.TryGetProperty("image", out var image)) return null;
        return image.ValueKind switch
        {
            JsonValueKind.String => image.GetString(),
            JsonValueKind.Array => image.EnumerateArray().Select(i => i.ValueKind == JsonValueKind.String ? i.GetString() : Str(i, "url")).FirstOrDefault(u => u is not null),
            JsonValueKind.Object => Str(image, "url"),
            _ => null,
        };
    }

    // ---- RSS / Atom -------------------------------------------------------------------------------

    private static List<UpcomingRelease> ParseFeed(string xml, Uri pageUri, Guid sourceId)
    {
        var list = new List<UpcomingRelease>();
        XDocument feed;
        try { feed = XDocument.Parse(xml); }
        catch (System.Xml.XmlException) { return list; }

        foreach (var item in feed.Descendants().Where(e => e.Name.LocalName is "item" or "entry"))
        {
            string? Child(string name) => item.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
            var title = Clean(Child("title"));
            if (title.Length == 0) continue;

            var link = item.Elements().FirstOrDefault(e => e.Name.LocalName == "link");
            var href = link?.Attribute("href")?.Value ?? link?.Value;
            var media = item.Descendants().FirstOrDefault(e => e.Name.LocalName is "content" or "thumbnail" or "enclosure" && e.Attribute("url") is not null)
                ?.Attribute("url")?.Value;
            var description = Child("description") ?? Child("summary") ?? Child("content") ?? string.Empty;
            media ??= ImgSrcRegex().Match(description) is { Success: true } img ? WebUtility.HtmlDecode(img.Groups[1].Value) : null;

            list.Add(new UpcomingRelease
            {
                SourceId = sourceId,
                Title = title,
                PageUrl = Resolve(href, pageUri),
                ImageUrl = ResolveImage(media, pageUri),
                Description = NullIfEmpty(Clean(TagRegex().Replace(WebUtility.HtmlDecode(description), " "))),
                ReleaseText = FindReleaseText(title + " " + description) ?? "Coming soon",
            });
        }
        return Finish(list);
    }

    [GeneratedRegex(@"<img[^>]+src=[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex ImgSrcRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    // ---- Any page: repeated cards -------------------------------------------------------------------

    private static List<UpcomingRelease> Cards(IDocument doc, Uri pageUri, Guid sourceId)
    {
        // A card: an element with a link, a picture and a short name, inside a list of similar siblings.
        var candidates = doc.QuerySelectorAll("article, li, .card, [class*='game'], [class*='item'], [class*='tile'], [class*='capsule']")
            .Where(e => e.QuerySelector("img") is not null && e.QuerySelector("a[href]") is not null)
            .Where(e => e.QuerySelectorAll("img").Length <= 4 && e.TextContent.Length < 1200)
            .ToList();

        // Keep the largest group of siblings with the same tag and class (the actual list), not one-off promos.
        var group = candidates
            .GroupBy(e => (e.ParentElement, e.LocalName, e.ClassName))
            .Where(g => g.Count() >= 3)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();
        if (group is null) return [];

        var list = new List<UpcomingRelease>();
        foreach (var card in group)
        {
            var img = card.QuerySelector("img")!;
            var heading = card.QuerySelector("h1, h2, h3, h4, h5, [class*='title'], [class*='name']");
            var anchor = heading?.QuerySelector("a[href]") ?? heading?.Closest("a[href]") ?? card.QuerySelector("a[href]");
            var title = Clean(heading?.TextContent);
            if (title.Length == 0) title = Clean(img.GetAttribute("alt") ?? anchor?.GetAttribute("title"));
            if (title.Length is < 2 or > 90) continue;

            list.Add(new UpcomingRelease
            {
                SourceId = sourceId,
                Title = title,
                PageUrl = Resolve(anchor?.GetAttribute("href"), pageUri),
                ImageUrl = ResolveImage(BestSource(img), pageUri),
                Description = NullIfEmpty(Clean(card.QuerySelector("p, [class*='desc'], [class*='summary']")?.TextContent)),
                ReleaseText = FindReleaseText(card.TextContent) ?? "Coming soon",
            });
        }
        return list;
    }

    /// <summary>The largest picture an &lt;img&gt; offers: srcset's widest, lazy-load attributes, then src.</summary>
    private static string? BestSource(IElement img)
    {
        var srcset = img.GetAttribute("srcset") ?? img.GetAttribute("data-srcset");
        if (!string.IsNullOrWhiteSpace(srcset))
        {
            var widest = srcset.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => part.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Where(bits => bits.Length > 0)
                .Select(bits => (Url: bits[0], Width: bits.Length > 1 && int.TryParse(bits[1].TrimEnd('w', 'x'), out var w) ? w : 0))
                .OrderByDescending(x => x.Width)
                .FirstOrDefault();
            if (widest.Url is not null) return widest.Url;
        }
        return img.GetAttribute("data-src") ?? img.GetAttribute("data-lazy-src") ?? img.GetAttribute("data-original") ?? img.GetAttribute("src");
    }

    // ---- shared -------------------------------------------------------------------------------------

    /// <summary>Drops furniture and duplicates, sets keys, caps the count.</summary>
    private static List<UpcomingRelease> Finish(List<UpcomingRelease> found)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<UpcomingRelease>();
        foreach (var release in found)
        {
            if (TitleNormalizer.IsDemo(release.Title)) continue; // demos don't belong in "Coming soon"
            var lower = release.Title.ToLowerInvariant();
            if (NotAGame.Any(w => lower == w || lower.StartsWith(w + " ", StringComparison.Ordinal))) continue;
            release.Key = TitleNormalizer.DedupKey(release.Title);
            if (release.Key.Length < 2 || !seen.Add(release.Key)) continue;
            release.FetchedUtc = DateTime.UtcNow;
            result.Add(release);
            if (result.Count >= MaxItems) break;
        }
        return result;
    }

    public static string? FindReleaseText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = ReleaseTextRegex().Match(text);
        if (!m.Success) return null;
        var value = m.Value.Trim();
        return value.Equals("tba", StringComparison.OrdinalIgnoreCase) || value.Equals("tbd", StringComparison.OrdinalIgnoreCase)
            ? "To be announced"
            : char.ToUpperInvariant(value[0]) + value[1..];
    }

    private static string Clean(string? text) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty : SpacesRegex().Replace(WebUtility.HtmlDecode(text), " ").Trim();

    private static string? NullIfEmpty(string text) => text.Length == 0 ? null : text.Length > 600 ? text[..600].TrimEnd() + "…" : text;

    private static string? Resolve(string? href, Uri baseUri) =>
        !string.IsNullOrWhiteSpace(href) && Uri.TryCreate(baseUri, WebUtility.HtmlDecode(href.Trim()), out var uri) && uri.Scheme is "http" or "https"
            ? uri.AbsoluteUri
            : null;

    /// <summary>Absolute picture URL, at full size when the site's rule is known.</summary>
    private static string? ResolveImage(string? src, Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(src) || src.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;
        return Resolve(src, baseUri) is { } url ? ImageQuality.Upgrade(url) : null;
    }
}
