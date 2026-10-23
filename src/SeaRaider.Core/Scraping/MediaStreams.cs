using System.Text.RegularExpressions;

namespace SeaRaider.Core.Scraping;

/// <summary>How the in-app player must open a stream.</summary>
public enum StreamFormat
{
    /// <summary>HLS playlist (.m3u8): most live streams.</summary>
    Hls,

    /// <summary>MPEG-DASH manifest (.mpd).</summary>
    Dash,

    /// <summary>A plain video file (.mp4, .webm …).</summary>
    File,

    /// <summary>A web page that is itself a player (an embed page): shown in a frame.</summary>
    Embed,
}

/// <summary>
/// Single videos and streams added to the Media tab by their address: a stream or video file, or one video on a site the
/// player can embed (YouTube, Vimeo, Dailymotion, Kick, a Twitch past broadcast or clip).
///
/// <para>A stream address does not always say what it is ("https://cdn.example.com/live/main"). Its format is read from what
/// the server sends (<see cref="DetectAsync"/>) and written into the address as a fragment ("#sr-hls"), which the player
/// understands and servers never see.</para>
/// </summary>
public static partial class MediaStreams
{
    private const string HintPrefix = "#sr-";

    /// <summary>Addresses a browser engine can't play at all (they need a media server in between).</summary>
    private static readonly string[] UnplayableSchemes = ["rtmp", "rtmps", "rtsp", "rtp", "udp", "srt", "mms"];

    /// <summary>First path segments on Kick that are the site's own pages, not channels.</summary>
    private static readonly HashSet<string> KickPages = new(StringComparer.OrdinalIgnoreCase)
        { "categories", "category", "browse", "search", "following", "dashboard", "video", "videos", "clips", "clip", "terms-of-service", "privacy-policy", "community-guidelines", "help", "settings" };

    [GeneratedRegex(@"youtube(?:-nocookie)?\.com/live/(?<id>[A-Za-z0-9_-]{11})", RegexOptions.IgnoreCase)]
    private static partial Regex YouTubeLiveRegex();

    [GeneratedRegex(@"^https?://(?:www\.|m\.)?twitch\.tv/videos/\d+|^https?://(?:clips\.twitch\.tv/|(?:www\.)?twitch\.tv/[^/]+/clip/)[\w-]+", RegexOptions.IgnoreCase)]
    private static partial Regex TwitchVideoRegex();

    [GeneratedRegex(@"^https?://(?:www\.)?(?:dailymotion\.com/(?:embed/)?video/|dai\.ly/)(?<id>[a-z0-9]+)", RegexOptions.IgnoreCase)]
    private static partial Regex DailymotionRegex();

    [GeneratedRegex(@"^https?://(?:www\.)?kick\.com/video/(?<id>[0-9a-f-]{8,40})", RegexOptions.IgnoreCase)]
    private static partial Regex KickVideoRegex();

    [GeneratedRegex(@"^https?://(?:www\.)?kick\.com/(?<name>[A-Za-z0-9_-]{3,25})/?$", RegexOptions.IgnoreCase)]
    private static partial Regex KickChannelRegex();

    [GeneratedRegex(@"https?:(?:\\?/){2}[^\s""'<>\\)]+?\.(?:m3u8|mpd)(?:\?[^\s""'<>\\)]*)?", RegexOptions.IgnoreCase)]
    private static partial Regex StreamUrlRegex();

    /// <summary>Stream addresses (.m3u8 / .mpd) written in a page's HTML or scripts, in order of appearance, without repeats.</summary>
    public static List<string> FindStreamUrls(string html)
    {
        var found = new List<string>();
        foreach (Match m in StreamUrlRegex().Matches(html))
        {
            var url = System.Net.WebUtility.HtmlDecode(m.Value.Replace("\\/", "/"));
            if (!found.Contains(url)) found.Add(url);
            if (found.Count == 5) break;
        }
        return found;
    }

    /// <summary>The scheme says it can't play here (rtmp://, rtsp:// …).</summary>
    public static bool IsUnplayableScheme(string? scheme) =>
        !string.IsNullOrEmpty(scheme) && UnplayableSchemes.Contains(scheme, StringComparer.OrdinalIgnoreCase);

    /// <summary>The format a path ends in (.m3u8, .mpd, .mp4 …), or null when it doesn't say.</summary>
    public static StreamFormat? FormatOfPath(Uri uri)
    {
        var path = uri.AbsolutePath;
        if (path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)) return StreamFormat.Hls;
        if (path.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase)) return StreamFormat.Dash;
        return new[] { ".mp4", ".webm", ".m4v", ".mov", ".ogv" }.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase)) ? StreamFormat.File : null;
    }

    /// <summary>The format of a stored play address: its path, else the hint written into it.</summary>
    public static StreamFormat? FormatOf(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && FormatOfPath(uri) is { } byPath) return byPath;
        var at = url.LastIndexOf(HintPrefix, StringComparison.Ordinal);
        if (at < 0) return null;
        return url[(at + HintPrefix.Length)..] switch { "hls" => StreamFormat.Hls, "dash" => StreamFormat.Dash, "video" => StreamFormat.File, "embed" => StreamFormat.Embed, _ => null };
    }

    /// <summary>The address with the format written in as a fragment ("…/live/main#sr-hls"), unless its path already says it.</summary>
    public static string WithHint(string url, StreamFormat format)
    {
        var bare = WithoutHint(url);
        if (Uri.TryCreate(bare, UriKind.Absolute, out var uri) && FormatOfPath(uri) is not null) return bare;
        return bare.Split('#')[0] + HintPrefix + format switch { StreamFormat.Hls => "hls", StreamFormat.Dash => "dash", StreamFormat.Embed => "embed", _ => "video" };
    }

    /// <summary>The address without the format hint: what the server is asked for.</summary>
    public static string WithoutHint(string url)
    {
        var at = url.LastIndexOf(HintPrefix, StringComparison.Ordinal);
        return at >= 0 ? url[..at] : url;
    }

    public static bool HasHint(string url) => url.Contains(HintPrefix, StringComparison.Ordinal);

    /// <summary>
    /// What the player opens for an address that is a video or stream by itself: the file / playlist address, or the tidy address
    /// of one embeddable video. Null for anything else (channels, feeds, pages).
    /// </summary>
    public static string? PlayUrlFor(Uri uri)
    {
        if (FormatOfPath(uri) is not null) return uri.AbsoluteUri;

        var text = uri.AbsoluteUri;
        if (MediaUrlHelper.TryGetYouTubeId(text) is { } id) return MediaListParser.Watch(id);
        if (YouTubeLiveRegex().Match(text) is { Success: true } live) return MediaListParser.Watch(live.Groups["id"].Value);
        if (uri.Host.EndsWith("vimeo.com", StringComparison.OrdinalIgnoreCase) && MediaUrlHelper.NormalizeVideoUrl(text) is { } vimeo) return vimeo;
        if (TwitchVideoRegex().IsMatch(text)) return text.Split('?', '#')[0];
        if (DailymotionRegex().Match(text) is { Success: true } daily) return $"https://www.dailymotion.com/video/{daily.Groups["id"].Value}";
        if (KickVideoRegex().Match(text) is { Success: true } kickVideo) return $"https://kick.com/video/{kickVideo.Groups["id"].Value}";
        if (KickChannelRegex().Match(text) is { Success: true } kick && !KickPages.Contains(kick.Groups["name"].Value))
            return $"https://kick.com/{kick.Groups["name"].Value.ToLowerInvariant()}";
        return null;
    }

    /// <summary>
    /// What a server sends tells whether an address is a stream: its content type, or the first bytes of the playlist
    /// ("#EXTM3U", "&lt;MPD"). Null for a web page, a feed or anything unreadable.
    /// </summary>
    public static async Task<StreamFormat?> DetectAsync(HttpClient client, Uri uri, CancellationToken ct)
    {
        try
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var type = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (type.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)) return StreamFormat.Hls;
            if (type.Contains("dash+xml", StringComparison.OrdinalIgnoreCase)) return StreamFormat.Dash;
            if (type.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return StreamFormat.File;
            if (type.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)) return null;

            await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[512];
            var read = await body.ReadAsync(buffer, ct).ConfigureAwait(false);
            return FromStart(System.Text.Encoding.UTF8.GetString(buffer, 0, read));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>HLS and DASH say what they are in their first characters.</summary>
    public static StreamFormat? FromStart(string start)
    {
        var text = start.TrimStart('﻿', ' ', '\r', '\n', '\t');
        if (text.StartsWith("#EXTM3U", StringComparison.Ordinal)) return StreamFormat.Hls;
        return text.Contains("<MPD", StringComparison.OrdinalIgnoreCase) ? StreamFormat.Dash : null;
    }

    /// <summary>
    /// Whether an HLS playlist is a live stream: it lists segments and has no end ("#EXT-X-ENDLIST"). A master playlist (a list
    /// of qualities) says nothing itself, so its first stream is looked at instead through <paramref name="fetch"/>.
    /// </summary>
    public static async Task<bool> IsLiveAsync(string playlist, Uri playlistUri, Func<Uri, Task<string>> fetch)
    {
        if (!playlist.TrimStart('﻿', ' ', '\r', '\n').StartsWith("#EXTM3U", StringComparison.Ordinal))
            throw new InvalidOperationException("that address isn't an HLS playlist");

        if (playlist.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal))
        {
            var variant = playlist.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));
            if (variant is null || !Uri.TryCreate(playlistUri, variant, out var next)) return false;
            playlist = await fetch(next).ConfigureAwait(false);
        }
        return playlist.Contains("#EXTINF", StringComparison.Ordinal) && !playlist.Contains("#EXT-X-ENDLIST", StringComparison.Ordinal);
    }
}
