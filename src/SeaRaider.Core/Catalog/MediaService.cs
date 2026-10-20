using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Models;
using SeaRaider.Core.Scraping;

namespace SeaRaider.Core.Catalog;

/// <summary>
/// The Media tab's sources (YouTube channels and playlists, Twitch channels, video feeds, pages with videos) and their
/// latest videos. Sources are read when added, then in the background: Twitch every few minutes (live or not), the rest
/// every half hour. Events are raised on background threads.
/// </summary>
public sealed class MediaService(IHttpClientFactory httpClientFactory, ILibraryRepository repository, ILogger<MediaService> logger)
    : IDisposable
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan TwitchRefreshEvery = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan LoopInterval = TimeSpan.FromMinutes(2);

    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _gate = new(2, 2);
    private int _started;

    /// <summary>Sources or their videos changed (reload the tab).</summary>
    public event EventHandler? Changed;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        _ = Task.Run(() => LoopAsync(_lifetime.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                var due = repository.GetMediaSources()
                    .Where(s => s.LastFetchedUtc is null ||
                                DateTime.UtcNow - s.LastFetchedUtc > (s.Kind == MediaSourceKind.Twitch ? TwitchRefreshEvery : RefreshEvery))
                    .ToList();
                await Task.WhenAll(due.Select(s => RefreshAsync(s, ct))).ConfigureAwait(false);
                await Task.Delay(LoopInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Media background refresh stopped");
        }
    }

    public IReadOnlyList<MediaSource> GetSources() => repository.GetMediaSources();

    public IReadOnlyList<MediaItem> GetItems(Guid sourceId) => repository.GetMediaItems(sourceId);

    /// <summary>Adds a channel, playlist, feed or page and reads it at once.</summary>
    /// <exception cref="ArgumentException">Not a web address, or already added.</exception>
    public async Task<MediaSource> AddSourceAsync(string url, CancellationToken ct = default)
    {
        url = url.Trim();
        if (!url.Contains("://", StringComparison.Ordinal)) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("That doesn't look like a web address. Paste a YouTube channel, a Twitch channel, a video feed or a page with videos.");
        if (repository.GetMediaSources().Any(s => SourcesFile.Key(s.Url) == SourcesFile.Key(uri.AbsoluteUri)))
            throw new ArgumentException("That's already on your Media tab.");

        var source = NewSource(uri);
        repository.UpsertMediaSource(source);
        Changed?.Invoke(this, EventArgs.Empty);
        await RefreshAsync(source, ct).ConfigureAwait(false);
        return source;
    }

    /// <summary>A source record for an address (not read yet; the background loop or <see cref="RefreshAsync"/> reads it).</summary>
    public static MediaSource NewSource(Uri uri)
    {
        var (kind, feed, name) = MediaListParser.Classify(uri);
        return new MediaSource
        {
            Url = uri.AbsoluteUri,
            Kind = kind,
            FeedUrl = feed,
            Name = name ?? (uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host),
        };
    }

    public void RemoveSource(Guid sourceId)
    {
        repository.DeleteMediaSource(sourceId);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task RefreshAllAsync(CancellationToken ct = default) =>
        await Task.WhenAll(repository.GetMediaSources().Select(s => RefreshAsync(s, ct))).ConfigureAwait(false);

    /// <summary>Reads one source again and replaces its videos.</summary>
    public async Task RefreshAsync(MediaSource source, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var items = source.Kind switch
            {
                MediaSourceKind.Twitch => new List<MediaItem> { await ReadTwitchAsync(source, ct).ConfigureAwait(false) },
                MediaSourceKind.YouTubeChannel or MediaSourceKind.YouTubePlaylist => await ReadYouTubeAsync(source, ct).ConfigureAwait(false),
                _ => await ReadFeedOrPageAsync(source, ct).ConfigureAwait(false),
            };
            await FillMissingTitlesAsync(items, ct).ConfigureAwait(false);
            items = items.Where(i => i.Title.Length > 0).ToList();

            if (!StillThere(source)) return; // removed while it was being read
            repository.ReplaceMediaItems(source.Id, items);
            source.LastCount = items.Count;
            source.LastError = items.Count == 0 ? "No videos found here. Try the channel's or site's video page, or its RSS feed." : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            source.LastError = $"Couldn't read it: {ex.Message}";
            logger.LogWarning(ex, "Media source {Source} failed", source.Url);
        }
        finally
        {
            source.LastFetchedUtc = DateTime.UtcNow;
            if (StillThere(source)) repository.UpsertMediaSource(source);
            _gate.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private bool StillThere(MediaSource source) => repository.GetMediaSources().Any(s => s.Id == source.Id);

    private HttpClient Pages => httpClientFactory.CreateClient(HttpClientNames.Scraper);
    private HttpClient Api => httpClientFactory.CreateClient(HttpClientNames.Metadata);

    private async Task<List<MediaItem>> ReadYouTubeAsync(MediaSource source, CancellationToken ct)
    {
        if (source.FeedUrl is null)
        {
            // @handle, /c/…, /user/…: the channel page names its id once; the feed is kept from then on.
            var html = await Pages.GetStringAsync(source.Url, ct).ConfigureAwait(false);
            var id = MediaListParser.FindChannelId(html) ?? throw new InvalidOperationException("couldn't find the channel on that page");
            source.FeedUrl = MediaListParser.ChannelFeed(id);
            source.AvatarUrl ??= MediaListParser.MetaContent(html, "og:image");
        }
        var (items, title) = MediaListParser.ParseFeed(await Api.GetStringAsync(source.FeedUrl, ct).ConfigureAwait(false), source.Id);
        if (title is not null) source.Name = title;
        return items;
    }

    private async Task<List<MediaItem>> ReadFeedOrPageAsync(MediaSource source, CancellationToken ct)
    {
        var url = source.FeedUrl ?? source.Url;
        var content = await Pages.GetStringAsync(url, ct).ConfigureAwait(false);
        if (MediaListParser.LooksLikeFeed(content))
        {
            source.Kind = MediaSourceKind.Feed;
            var (items, title) = MediaListParser.ParseFeed(content, source.Id);
            if (title is not null) source.Name = title;
            return items;
        }

        source.Kind = MediaSourceKind.Page;
        var (found, pageTitle) = await MediaListParser.ParsePageAsync(content, new Uri(url), source.Id, ct).ConfigureAwait(false);
        if (pageTitle is not null && source.LastFetchedUtc is null) source.Name = pageTitle;
        return found;
    }

    private async Task<MediaItem> ReadTwitchAsync(MediaSource source, CancellationToken ct)
    {
        var channel = new Uri(source.Url).AbsolutePath.Trim('/').ToLowerInvariant();
        source.Name = channel;

        // Live or not: Twitch's live picture redirects to a "404_preview" image while the channel is offline.
        var live = false;
        try
        {
            using var preview = await Api.GetAsync(MediaListParser.TwitchPreview(channel), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var final = preview.RequestMessage?.RequestUri?.AbsoluteUri ?? string.Empty;
            live = preview.IsSuccessStatusCode && !final.Contains("404_preview", StringComparison.OrdinalIgnoreCase);
        }
        catch (HttpRequestException)
        {
        }

        string? title = null, image = null;
        try
        {
            var html = await Pages.GetStringAsync(source.Url, ct).ConfigureAwait(false);
            title = MediaListParser.MetaContent(html, "og:description");
            image = MediaListParser.MetaContent(html, "og:image");
            source.AvatarUrl = image ?? source.AvatarUrl;
        }
        catch (HttpRequestException)
        {
        }
        return MediaListParser.TwitchItem(source.Id, channel, live, title, image ?? source.AvatarUrl);
    }

    /// <summary>YouTube videos found on a page without a usable name: YouTube's public oEmbed has it (and the channel).</summary>
    private async Task FillMissingTitlesAsync(List<MediaItem> items, CancellationToken ct)
    {
        foreach (var item in items.Where(i => i.Title.Length == 0 && MediaUrlHelper.TryGetYouTubeId(i.PlayUrl) is not null).Take(MediaListParser.MaxItems))
        {
            try
            {
                using var json = await Api.GetFromJsonAsync<JsonDocument>(
                    $"https://www.youtube.com/oembed?format=json&url={Uri.EscapeDataString(item.PlayUrl)}", ct).ConfigureAwait(false);
                if (json?.RootElement.TryGetProperty("title", out var title) == true) item.Title = title.GetString() ?? string.Empty;
                if (json?.RootElement.TryGetProperty("author_name", out var author) == true) item.Channel = author.GetString();
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // private or removed video: dropped (no title)
            }
        }
    }

    // =============================================================================================
    // One link (the "Shared" feed)
    // =============================================================================================

    /// <summary>
    /// What a pasted link is: its title, picture and channel (YouTube and Vimeo through their public oEmbed, Twitch and
    /// any other page through the page's own preview tags), plus the start time written into it (?t=…). Never fails for
    /// a valid web address: without details the link is still shared, named after its site.
    /// </summary>
    /// <exception cref="ArgumentException">Not a web address.</exception>
    public async Task<MediaLink> DescribeLinkAsync(string url, CancellationToken ct = default)
    {
        url = (url ?? string.Empty).Trim();
        if (!url.Contains("://", StringComparison.Ordinal)) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || url.Length > 2048)
            throw new ArgumentException("That doesn't look like a link. Paste a YouTube, Twitch or Vimeo address, a video file or a page.");

        var start = Social.MediaTime.FromUrl(url);
        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        var link = new MediaLink(uri.AbsoluteUri, host, null, host, start);

        try
        {
            if (MediaUrlHelper.TryGetYouTubeId(url) is { } youTubeId)
            {
                var watch = MediaListParser.Watch(youTubeId);
                link = link with { Url = watch, ThumbnailUrl = MediaListParser.YouTubeThumbnail(youTubeId), Channel = "YouTube" };
                return await FromOEmbedAsync(link, $"https://www.youtube.com/oembed?format=json&url={Uri.EscapeDataString(watch)}", ct).ConfigureAwait(false);
            }
            if (uri.Host.EndsWith("vimeo.com", StringComparison.OrdinalIgnoreCase))
                return await FromOEmbedAsync(link with { Channel = "Vimeo" },
                    $"https://vimeo.com/api/oembed.json?url={Uri.EscapeDataString(uri.AbsoluteUri)}", ct).ConfigureAwait(false);
            if (TrailerPlayerUrls.IsVideoFile(uri))
                return link with { Title = Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(uri.AbsolutePath)) is { Length: > 0 } file ? file : host };

            // Twitch and any other page: its preview tags.
            var html = await Pages.GetStringAsync(uri, ct).ConfigureAwait(false);
            var title = MediaListParser.MetaContent(html, "og:title") ?? MediaListParser.MetaContent(html, "twitter:title") ?? HtmlTitle(html);
            var image = MediaListParser.MetaContent(html, "og:image") ?? MediaListParser.MetaContent(html, "twitter:image");
            var site = MediaListParser.MetaContent(html, "og:site_name");
            if (uri.Host.EndsWith("twitch.tv", StringComparison.OrdinalIgnoreCase))
            {
                site = "Twitch";
                // A channel: its live picture while live (Twitch's own og:image is just the profile picture).
                var channel = uri.AbsolutePath.Trim('/');
                if (channel.Length is >= 3 and <= 25 && !channel.Contains('/') && await IsTwitchLiveAsync(channel.ToLowerInvariant(), ct).ConfigureAwait(false))
                {
                    image = MediaListParser.TwitchPreview(channel.ToLowerInvariant());
                    title = MediaListParser.MetaContent(html, "og:description") is { Length: > 0 } stream ? stream : title;
                }
            }
            return link with
            {
                Title = Tidy(title, 200) ?? link.Title,
                ThumbnailUrl = Absolute(image, uri),
                Channel = Tidy(site, 60) ?? link.Channel,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogInformation(ex, "Couldn't read details for {Url}", url);
            return link;
        }
    }

    private async Task<MediaLink> FromOEmbedAsync(MediaLink link, string oEmbed, CancellationToken ct)
    {
        using var json = await Api.GetFromJsonAsync<JsonDocument>(oEmbed, ct).ConfigureAwait(false);
        string? Read(string name) => json?.RootElement.TryGetProperty(name, out var v) == true && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return link with
        {
            Title = Tidy(Read("title"), 200) ?? link.Title,
            Channel = Tidy(Read("author_name"), 60) ?? link.Channel,
            ThumbnailUrl = link.ThumbnailUrl ?? Absolute(Read("thumbnail_url"), new Uri(link.Url)),
        };
    }

    private async Task<bool> IsTwitchLiveAsync(string channel, CancellationToken ct)
    {
        try
        {
            using var preview = await Api.GetAsync(MediaListParser.TwitchPreview(channel), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var final = preview.RequestMessage?.RequestUri?.AbsoluteUri ?? string.Empty;
            return preview.IsSuccessStatusCode && !final.Contains("404_preview", StringComparison.OrdinalIgnoreCase);
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private static string? HtmlTitle(string html) =>
        System.Text.RegularExpressions.Regex.Match(html, @"<title[^>]*>(?<t>[^<]{1,300})</title>", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            is { Success: true } m ? System.Net.WebUtility.HtmlDecode(m.Groups["t"].Value) : null;

    private static string? Tidy(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var clean = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return clean.Length > max ? clean[..max] : clean;
    }

    private static string? Absolute(string? url, Uri page) =>
        !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(page, url.Trim(), out var uri) && uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}

/// <summary>A link described for sharing (see <see cref="MediaService.DescribeLinkAsync"/>).</summary>
/// <param name="Url">The link, tidied (a YouTube video becomes its watch page, without the start time).</param>
/// <param name="StartSeconds">The start time written into the link, if any.</param>
public sealed record MediaLink(string Url, string Title, string? ThumbnailUrl, string? Channel, int? StartSeconds);

/// <summary>Video file addresses (played directly by the in-app player).</summary>
public static class TrailerPlayerUrls
{
    private static readonly string[] Extensions = [".mp4", ".webm", ".m4v", ".mov", ".m3u8", ".mpd"];

    public static bool IsVideoFile(Uri uri) => Extensions.Any(e => uri.AbsolutePath.EndsWith(e, StringComparison.OrdinalIgnoreCase));
}
