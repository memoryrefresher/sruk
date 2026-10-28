using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AngleSharp;
using AngleSharp.Dom;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Scraping;

/// <summary>
/// Reads the videos off a media source (Media tab):
/// <list type="bullet">
///   <item><b>YouTube</b> channels and playlists through their public video feed (no key): newest uploads, title,
///         channel, date. A channel added by @handle / /c/ / /user/ is looked up once to find its feed.</item>
///   <item><b>Twitch</b> channels: one box, showing the live picture while the channel is live.</item>
///   <item><b>RSS / Atom</b> feeds whose items are YouTube / Vimeo links or video files.</item>
///   <item><b>Any page</b>: the YouTube and Vimeo videos embedded or linked on it, and its own video files.</item>
/// </list>
/// </summary>
public static partial class MediaListParser
{
    public const int MaxItems = 30;

    [GeneratedRegex(@"^/channel/(?<id>UC[\w-]{22})", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelPathRegex();

    [GeneratedRegex(@"<link rel=""canonical"" href=""https://www\.youtube\.com/channel/(?<id>UC[\w-]{22})""|""externalId"":""(?<id>UC[\w-]{22})""|itemprop=""identifier"" content=""(?<id>UC[\w-]{22})""|""channelId"":""(?<id>UC[\w-]{22})""")]
    private static partial Regex ChannelIdRegex();

    [GeneratedRegex(@"(?:[?&]list=)(?<id>[\w-]{10,})")]
    private static partial Regex PlaylistRegex();

    [GeneratedRegex(@"^/(?<name>[A-Za-z0-9_]{3,25})/?$")]
    private static partial Regex TwitchChannelRegex();

    [GeneratedRegex(@"vimeo\.com/(?:video/)?(?<id>\d{6,})", RegexOptions.IgnoreCase)]
    private static partial Regex VimeoRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacesRegex();

    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace YouTubeNs = "http://www.youtube.com/xml/schemas/2015";
    private static readonly XNamespace MediaNs = "http://search.yahoo.com/mrss/";

    /// <summary>What a pasted address is, and the feed to read when that's known without fetching anything.</summary>
    public static (MediaSourceKind Kind, string? FeedUrl, string? Name) Classify(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        if (MediaStreams.FormatOfPath(uri) is not null) return (MediaSourceKind.Stream, uri.AbsoluteUri, null);
        if (host.EndsWith("youtube.com", StringComparison.Ordinal))
        {
            if (uri.AbsolutePath.StartsWith("/feeds/videos.xml", StringComparison.OrdinalIgnoreCase))
                return (MediaSourceKind.Feed, uri.AbsoluteUri, null);
            if (PlaylistRegex().Match(uri.Query) is { Success: true } list)
                return (MediaSourceKind.YouTubePlaylist, $"https://www.youtube.com/feeds/videos.xml?playlist_id={list.Groups["id"].Value}", null);
            if (ChannelPathRegex().Match(uri.AbsolutePath) is { Success: true } channel)
                return (MediaSourceKind.YouTubeChannel, ChannelFeed(channel.Groups["id"].Value), null);
            if (MediaStreams.PlayUrlFor(uri) is { } video) return (MediaSourceKind.Stream, video, null); // one video (watch?v=…, /live/…, /shorts/…)
            return (MediaSourceKind.YouTubeChannel, null, null); // @handle, /c/…, /user/…: looked up from the page
        }
        if (host.EndsWith("twitch.tv", StringComparison.Ordinal) && TwitchChannelRegex().Match(uri.AbsolutePath) is { Success: true } twitch)
            return (MediaSourceKind.Twitch, null, twitch.Groups["name"].Value.ToLowerInvariant());
        if (MediaStreams.PlayUrlFor(uri) is { } single) return (MediaSourceKind.Stream, single, null); // youtu.be, Vimeo, Kick, Dailymotion, a Twitch video
        return (MediaSourceKind.Page, null, null); // or a feed: decided once the content is seen
    }

    public static string ChannelFeed(string channelId) => $"https://www.youtube.com/feeds/videos.xml?channel_id={channelId}";

    /// <summary>A YouTube channel page's own id (UC…), from its canonical link or page data.</summary>
    public static string? FindChannelId(string html)
    {
        foreach (Match m in ChannelIdRegex().Matches(html))
            if (m.Groups["id"].Success) return m.Groups["id"].Value;
        return null;
    }

    public static bool LooksLikeFeed(string content)
    {
        var start = content.TrimStart();
        return start.StartsWith("<?xml", StringComparison.Ordinal) || start.StartsWith("<rss", StringComparison.OrdinalIgnoreCase) ||
               start.StartsWith("<feed", StringComparison.OrdinalIgnoreCase);
    }

    // ---- feeds (YouTube's and any other) ----------------------------------------------------------------

    /// <summary>Videos in an RSS / Atom feed, newest first as the feed lists them; also returns the feed's title.</summary>
    public static (List<MediaItem> Items, string? FeedTitle) ParseFeed(string xml, Guid sourceId)
    {
        var items = new List<MediaItem>();
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (System.Xml.XmlException) { return (items, null); }

        var root = doc.Root;
        var feedTitle = root?.Element(Atom + "title")?.Value ?? root?.Element("channel")?.Element("title")?.Value;
        var feedAuthor = root?.Element(Atom + "author")?.Element(Atom + "name")?.Value;

        foreach (var entry in doc.Descendants().Where(e => e.Name.LocalName is "entry" or "item"))
        {
            string? Child(string name) => entry.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
            var title = Clean(Child("title"));
            var link = entry.Elements().FirstOrDefault(e => e.Name.LocalName == "link");
            var href = link?.Attribute("href")?.Value ?? link?.Value;
            var videoId = entry.Element(YouTubeNs + "videoId")?.Value ?? MediaUrlHelper.TryGetYouTubeId(href);
            var enclosure = entry.Elements().FirstOrDefault(e => e.Name.LocalName == "enclosure" &&
                                                                 (e.Attribute("type")?.Value?.StartsWith("video", StringComparison.OrdinalIgnoreCase) ?? false))
                ?.Attribute("url")?.Value;
            var thumbnail = entry.Descendants(MediaNs + "thumbnail").FirstOrDefault()?.Attribute("url")?.Value;

            var play = videoId is not null ? Watch(videoId)
                : VimeoRegex().Match(href ?? string.Empty) is { Success: true } vimeo ? $"https://vimeo.com/{vimeo.Groups["id"].Value}"
                : enclosure ?? (MediaUrlHelper.NormalizeVideoUrl(href) is { } direct ? direct : null);
            if (play is null || title.Length == 0) continue;

            var published = DateTime.TryParse(Child("published") ?? Child("pubDate") ?? Child("updated"), null,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var when)
                ? when
                : (DateTime?)null;

            items.Add(new MediaItem
            {
                SourceId = sourceId,
                Key = $"{sourceId:N}|{videoId ?? play}",
                Title = title,
                PlayUrl = play,
                PageUrl = href ?? play,
                ThumbnailUrl = videoId is not null ? YouTubeThumbnail(videoId) : thumbnail ?? VimeoThumbnail(play),
                Channel = Clean(entry.Element(Atom + "author")?.Element(Atom + "name")?.Value ?? feedAuthor) is { Length: > 0 } a ? a : null,
                PublishedUtc = published,
                Order = items.Count,
            });
            if (items.Count >= MaxItems) break;
        }
        return (items, Clean(feedTitle) is { Length: > 0 } t ? t : null);
    }

    // ---- any page -----------------------------------------------------------------------------------------

    /// <summary>The videos embedded or linked on a page (YouTube, Vimeo, video files), with the best title nearby.</summary>
    public static async Task<(List<MediaItem> Items, string? PageTitle)> ParsePageAsync(string html, Uri pageUri, Guid sourceId, CancellationToken ct)
    {
        var context = BrowsingContext.New(Configuration.Default);
        using var doc = await context.OpenAsync(req => req.Content(html).Address(pageUri.AbsoluteUri), ct).ConfigureAwait(false);

        var items = new List<MediaItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string key, string play, string? title, string? thumbnail, string? page)
        {
            if (!seen.Add(key) || items.Count >= MaxItems) return;
            items.Add(new MediaItem
            {
                SourceId = sourceId,
                Key = $"{sourceId:N}|{key}",
                Title = string.IsNullOrWhiteSpace(title) ? string.Empty : Clean(title), // blank: looked up later (YouTube)
                PlayUrl = play,
                PageUrl = page ?? play,
                ThumbnailUrl = thumbnail,
                Order = items.Count,
            });
        }

        foreach (var element in doc.QuerySelectorAll("iframe[src], iframe[data-src], a[href], video, video source[src], [data-youtube-id], [data-video-id]"))
        {
            var url = element.GetAttribute("src") ?? element.GetAttribute("data-src") ?? element.GetAttribute("href");
            if (element.GetAttribute("data-youtube-id") is { Length: 11 } dataId) url = Watch(dataId);
            var absolute = Resolve(url, pageUri);

            if (MediaUrlHelper.TryGetYouTubeId(absolute) is { } id)
            {
                Add(id, Watch(id), TitleNear(element), YouTubeThumbnail(id), Watch(id));
            }
            else if (absolute is not null && VimeoRegex().Match(absolute) is { Success: true } vimeo)
            {
                var play = $"https://vimeo.com/{vimeo.Groups["id"].Value}";
                Add(play, play, TitleNear(element), VimeoThumbnail(play), play);
            }
            else if (absolute is not null && element.LocalName is "video" or "source" && MediaUrlHelper.NormalizeVideoUrl(absolute) is { } file)
            {
                var poster = Resolve((element.LocalName == "source" ? element.ParentElement : element)?.GetAttribute("poster"), pageUri);
                Add(file, file, TitleNear(element) ?? doc.Title, poster, pageUri.AbsoluteUri);
            }
        }

        var pageTitle = doc.QuerySelector("meta[property='og:site_name']")?.GetAttribute("content") ?? doc.Title;
        return (items, Clean(pageTitle) is { Length: > 0 } t ? t : null);
    }

    /// <summary>A video's name from the element itself or what's around it (title attribute, link text, a heading).</summary>
    private static string? TitleNear(IElement element)
    {
        foreach (var candidate in new[]
                 {
                     element.GetAttribute("title"), element.GetAttribute("aria-label"),
                     element.LocalName == "a" ? element.TextContent : null,
                     element.QuerySelector("img[alt]")?.GetAttribute("alt"),
                     element.ParentElement?.QuerySelector("h1, h2, h3, h4, [class*='title']")?.TextContent,
                     element.ParentElement?.ParentElement?.QuerySelector("h1, h2, h3, h4, [class*='title']")?.TextContent,
                 })
        {
            var text = Clean(candidate);
            if (text.Length is >= 4 and <= 160 && !text.Equals("YouTube video player", StringComparison.OrdinalIgnoreCase) &&
                !text.Contains("youtube.com", StringComparison.OrdinalIgnoreCase))
                return text;
        }
        return null;
    }

    // ---- Twitch ---------------------------------------------------------------------------------------------

    /// <summary>A Twitch channel's box: its live picture while live, else its profile picture.</summary>
    public static MediaItem TwitchItem(Guid sourceId, string channel, bool live, string? streamTitle, string? offlineImage) => new()
    {
        SourceId = sourceId,
        Key = $"{sourceId:N}|twitch:{channel}",
        Title = live
            ? string.IsNullOrWhiteSpace(streamTitle) ? $"{channel} is live" : Clean(streamTitle)
            : $"{channel} is offline. Open to see the channel",
        PlayUrl = $"https://www.twitch.tv/{channel}",
        PageUrl = $"https://www.twitch.tv/{channel}",
        ThumbnailUrl = live ? TwitchPreview(channel) + $"?t={DateTime.UtcNow:yyyyMMddHHmm}" : offlineImage,
        Channel = channel,
        IsLive = live,
    };

    /// <summary>Twitch's live picture; when the channel is offline Twitch redirects it to a "404_preview" image.</summary>
    public static string TwitchPreview(string channel) => $"https://static-cdn.jtvnw.net/previews-ttv/live_user_{channel}-640x360.jpg";

    public static string? MetaContent(string html, string property) =>
        Regex.Match(html, $@"<meta[^>]+(?:property|name)=""{Regex.Escape(property)}""[^>]+content=""(?<v>[^""]*)""", RegexOptions.IgnoreCase) is { Success: true } m
            ? WebUtility.HtmlDecode(m.Groups["v"].Value)
            : null;

    // ---- shared ---------------------------------------------------------------------------------------------

    public static string Watch(string youTubeId) => $"https://www.youtube.com/watch?v={youTubeId}";

    /// <summary>480×360, always there (the 16:9 picture with bars above and below, which the box crops away).</summary>
    public static string YouTubeThumbnail(string youTubeId) => $"https://i.ytimg.com/vi/{youTubeId}/hqdefault.jpg";

    private static string? VimeoThumbnail(string url) =>
        VimeoRegex().Match(url) is { Success: true } m ? $"https://vumbnail.com/{m.Groups["id"].Value}.jpg" : null;

    private static string Clean(string? text) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty : SpacesRegex().Replace(WebUtility.HtmlDecode(text), " ").Trim();

    private static string? Resolve(string? href, Uri baseUri) =>
        !string.IsNullOrWhiteSpace(href) && Uri.TryCreate(baseUri, WebUtility.HtmlDecode(href.Trim()), out var uri) && uri.Scheme is "http" or "https"
            ? uri.AbsoluteUri
            : null;
}
