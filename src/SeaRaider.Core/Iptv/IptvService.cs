using Microsoft.Extensions.Logging;
using SeaRaider.Core.Abstractions;

namespace SeaRaider.Core.Iptv;

/// <summary>
/// Media › Live TV: loads the user's M3U playlist (an address or a file), keeps a copy on disk so the channels are there at
/// once on the next start (and offline), and refreshes it from the address in the background when the copy is old.
/// </summary>
public sealed class IptvService(IHttpClientFactory httpClientFactory, ILibraryRepository repository, ILogger<IptvService> logger)
{
    private static string CachePath => Path.Combine(AppPaths.DataDirectory, "iptv-playlist.m3u");
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);

    private readonly SemaphoreSlim _gate = new(1, 1);

    public IReadOnlyList<IptvChannel> Channels { get; private set; } = [];

    /// <summary>When the playlist was last read from its address, or null.</summary>
    public DateTime? LoadedAt { get; private set; }

    public event EventHandler? Changed;

    public string? PlaylistUrl => repository.GetSettings().IptvPlaylistUrl;

    /// <summary>The saved copy first (instant), then the address when the copy is old or missing.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(PlaylistUrl)) return;
        var cache = new FileInfo(CachePath);
        if (cache.Exists)
        {
            await LoadFileAsync(CachePath, ct).ConfigureAwait(false);
            LoadedAt = cache.LastWriteTimeUtc;
            Changed?.Invoke(this, EventArgs.Empty);
        }
        if (!cache.Exists || DateTime.UtcNow - cache.LastWriteTimeUtc > MaxAge)
            await RefreshAsync(null, ct).ConfigureAwait(false);
    }

    /// <summary>Reads the playlist from <paramref name="url"/> (saved as the playlist when it works) or from the saved address. Returns the channel count.</summary>
    public async Task<int> RefreshAsync(string? url, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            url = (url ?? PlaylistUrl)?.Trim().Trim('"');
            if (string.IsNullOrEmpty(url)) throw new InvalidOperationException("Paste your M3U link first.");

            var temp = CachePath + ".tmp";
            Directory.CreateDirectory(AppPaths.DataDirectory);
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                var http = httpClientFactory.CreateClient(HttpClientNames.Downloads);
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("VLC/3.0.20 LibVLC/3.0.20"); // many panels refuse an unknown agent
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromMinutes(3));
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using (var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
                await using (var file = File.Create(temp))
                    await source.CopyToAsync(file, timeout.Token).ConfigureAwait(false);
            }
            else if (File.Exists(url))
            {
                File.Copy(url, temp, overwrite: true);
            }
            else
            {
                throw new InvalidOperationException("That isn't a web address or a file on this computer.");
            }

            var channels = await ParseFileAsync(temp, ct).ConfigureAwait(false);
            if (channels.Count == 0)
            {
                File.Delete(temp);
                throw new InvalidOperationException("That link didn't give a channel list (is it an M3U link? it may have expired).");
            }
            File.Move(temp, CachePath, overwrite: true);

            var settings = repository.GetSettings();
            if (settings.IptvPlaylistUrl != url)
            {
                settings.IptvPlaylistUrl = url;
                repository.SaveSettings(settings);
            }
            Channels = channels;
            LoadedAt = DateTime.UtcNow;
            Changed?.Invoke(this, EventArgs.Empty);
            return channels.Count;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forgets the playlist and its saved copy.</summary>
    public void Clear()
    {
        var settings = repository.GetSettings();
        settings.IptvPlaylistUrl = null;
        settings.IptvLastChannel = null;
        repository.SaveSettings(settings);
        try { File.Delete(CachePath); }
        catch (IOException ex) { logger.LogDebug(ex, "Couldn't delete the saved playlist"); }
        Channels = [];
        LoadedAt = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetFavorite(IptvChannel channel, bool favorite)
    {
        var settings = repository.GetSettings();
        settings.IptvFavorites.Remove(channel.Key);
        if (favorite) settings.IptvFavorites.Add(channel.Key);
        repository.SaveSettings(settings);
    }

    public void RememberLast(IptvChannel channel)
    {
        var settings = repository.GetSettings();
        settings.IptvLastChannel = channel.Key;
        repository.SaveSettings(settings);
    }

    private async Task LoadFileAsync(string path, CancellationToken ct)
    {
        try { Channels = await ParseFileAsync(path, ct).ConfigureAwait(false); }
        catch (IOException ex) { logger.LogWarning(ex, "Couldn't read the saved playlist"); }
    }

    private static Task<List<IptvChannel>> ParseFileAsync(string path, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        using var reader = new StreamReader(path);
        return M3uParser.Parse(reader);
    }, ct);
}
