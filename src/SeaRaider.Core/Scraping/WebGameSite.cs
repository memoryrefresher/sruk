using System.Net;
using AngleSharp.Dom;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Scraping;

/// <summary>
/// A browser game's own website as a source (<see cref="GameSource.IsWebGame"/>): the page is the game. Its store entry
/// comes from the site's title, share picture and description; "Download &amp; Install" adds it to the library and Play
/// opens the site in SeaRaider's game window.
/// </summary>
public static class WebGameSite
{
    private static readonly string[] TitleSeparators = [": ", " - ", " – ", " — ", " | "];

    /// <summary>Phrases a browser game's own page uses about itself.</summary>
    private static readonly string[] BrowserGamePhrases =
    [
        "no download", "in your browser", "in the browser", "browser game", "play in browser", "play in your browser",
        "play free", "play now", "play for free", "runs in a browser", "runs in your browser",
    ];

    /// <summary>
    /// A page that offers no downloads but says it plays in the browser ("Play free · no download", "runs in a browser"):
    /// a browser game, even if its source wasn't marked as one.
    /// </summary>
    public static bool LooksLikeBrowserGame(IDocument doc)
    {
        var text = string.Join(' ',
            doc.Title ?? string.Empty,
            doc.QuerySelector("meta[property='og:title']")?.GetAttribute("content") ?? string.Empty,
            doc.QuerySelector("meta[property='og:description']")?.GetAttribute("content") ?? string.Empty,
            doc.QuerySelector("meta[name='description']")?.GetAttribute("content") ?? string.Empty,
            doc.Body?.TextContent is { } body ? body[..Math.Min(body.Length, 20_000)] : string.Empty).ToLowerInvariant();
        return BrowserGamePhrases.Any(text.Contains);
    }

    /// <param name="gameUrl">The address to play (default: the source's own address).</param>
    public static GameMetadata Build(IDocument doc, Uri pageUri, GameSource source, string? gameUrl = null)
    {
        var url = string.IsNullOrWhiteSpace(gameUrl) ? source.Url : gameUrl;
        string? Meta(string selector) => doc.QuerySelector(selector)?.GetAttribute("content") is { Length: > 0 } v ? Collapse(v) : null;

        var siteName = Meta("meta[property='og:site_name']");
        var docTitle = Collapse(doc.Title);
        var fullTitle = Meta("meta[property='og:title']") ?? (docTitle.Length > 0 ? docTitle : pageUri.Host);
        // "Astra Strike: free CS-style shooter in your browser" → "Astra Strike" (the rest is the tagline).
        var title = siteName ?? ShortTitle(fullTitle);
        var description = Meta("meta[property='og:description']") ?? Meta("meta[name='description']") ?? string.Empty;
        var tagline = fullTitle.StartsWith(title, StringComparison.Ordinal)
            ? fullTitle[title.Length..].Trim(' ', ':', '-', '–', '—', '|')
            : string.Empty;
        if (tagline.Length > 0 && !description.Contains(tagline, StringComparison.OrdinalIgnoreCase))
            description = $"{char.ToUpperInvariant(tagline[0])}{tagline[1..]}. {description}".Trim();
        var image = Meta("meta[property='og:image']") ?? Meta("meta[name='twitter:image']");
        var cover = image is not null && Uri.TryCreate(pageUri, WebUtility.HtmlDecode(image), out var c) ? c.AbsoluteUri : null;

        return new GameMetadata
        {
            Id = GameMetadata.CreateId(url),
            SourceId = source.Id,
            PageUrl = url,
            Title = title,
            RawTitle = fullTitle,
            CoverImageUrl = cover,
            ScreenshotUrls = cover is null ? [] : [cover],
            ShortDescription = description.Length > 220 ? description[..220].TrimEnd() + "…" : description,
            Description = description,
            Genres = ["Browser game"],
            WebGameUrl = url,
            UploadedAtUtc = DateTime.UtcNow.Date,
            // The site's own picture: no Steam / IGDB lookup (a fan game can share a name with something unrelated).
            MediaChecked = true,
            MediaVersion = GameMetadata.CurrentMediaVersion,
            DetailsLoaded = true,
            MetadataVersion = GameMetadata.CurrentMetadataVersion,
            FetchedAtUtc = DateTime.UtcNow,
        };
    }

    internal static string ShortTitle(string title)
    {
        foreach (var separator in TitleSeparators)
        {
            var at = title.IndexOf(separator, StringComparison.Ordinal);
            if (at >= 2) return title[..at].Trim();
        }
        return title;
    }

    private static string Collapse(string? text) =>
        string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
