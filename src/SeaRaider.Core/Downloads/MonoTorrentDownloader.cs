using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Dht;
using MonoTorrent.Trackers;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Downloads;

/// <summary>
/// Headless BitTorrent transport built on MonoTorrent 3.x.
///
/// <para><b>Finding peers.</b> A magnet link only carries the info-hash, so peers (and the torrent's
/// metadata) must come from trackers or the DHT:</para>
/// <list type="bullet">
///   <item><b>DHT bootstrap:</b> MonoTorrent 3.0 seeds its DHT from a single host (router.bittorrent.com). If that
///         lookup fails the DHT never gets a node and magnet-only torrents can't find anyone. We resolve several
///         well-known bootstrap routers ourselves and hand them to the DHT before it starts.</item>
///   <item><b>Public trackers:</b> optionally added to every non-private torrent (setting), so magnets that list
///         no or dead trackers still get peers.</item>
///   <item><b>Diagnostics:</b> the status line shows DHT state and node count, trackers reachable, and peers
///         <i>found</i> vs <i>connected</i>, so a dead swarm and a blocked network look different.</item>
///   <item><b>No endless waiting:</b> if no peer at all has been found after
///         <see cref="AppSettings.TorrentNoPeersTimeoutMinutes"/>, the job stops with an explanation. Fast-resume
///         data is kept, so Retry continues where it left off.</item>
/// </list>
/// .torrent files are fetched into memory; nothing but the payload touches the disk. When the download completes
/// the torrent is stopped (files released for extraction) and removed from the engine, unless the queue asked to
/// keep it for seeding (<see cref="SeedAsync"/>), which shares it until a ratio or time limit.
/// <para><b>Limits.</b> Engine-wide download/upload limits come from the queue (<see cref="ApplyGlobalLimitsAsync"/>);
/// per-torrent limits come from the job's <see cref="TransferThrottle"/> and are re-applied live while it runs.</para>
/// </summary>
public sealed class MonoTorrentDownloader(
    IHttpClientFactory httpClientFactory,
    ILibraryRepository repository,
    ILogger<MonoTorrentDownloader> logger) : ITorrentDownloader
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Well-known public DHT routers; any one answering is enough to join the DHT.</summary>
    private static readonly (string Host, int Port)[] DhtBootstrapRouters =
    [
        ("router.bittorrent.com", 6881),
        ("router.utorrent.com", 6881),
        ("dht.transmissionbt.com", 6881),
        ("dht.libtorrent.org", 25401),
        ("dht.aelitis.com", 6881),
    ];

    private readonly SemaphoreSlim _engineGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TorrentManager> _keptForSeeding = new(StringComparer.Ordinal);
    private ClientEngine? _engine;
    private int _globalDownloadLimit;
    private int _globalUploadLimit;

    public async Task<IReadOnlyList<string>> DownloadAsync(DownloadLink link, string saveDirectory,
        IProgress<TransferProgress>? progress, CancellationToken cancellationToken, bool keepForSeeding = false)
    {
        Directory.CreateDirectory(saveDirectory);
        var settings = repository.GetSettings();

        progress?.Report(new TransferProgress(0, 0, 0, Detail: "Starting BitTorrent…"));
        var engine = await GetEngineAsync(cancellationToken).ConfigureAwait(false);
        var manager = await AddOrGetManagerAsync(engine, link, saveDirectory, cancellationToken).ConfigureAwait(false);

        // Two games from one backups torrent: the engine holds a torrent once, so the second waits for the first (a paused one
        // makes way; it picks up again where it was when resumed).
        while (!string.IsNullOrWhiteSpace(link.TorrentFolder) &&
               !string.Equals(Path.GetFullPath(manager.SavePath).TrimEnd('\\', '/'), Path.GetFullPath(saveDirectory).TrimEnd('\\', '/'),
                   StringComparison.OrdinalIgnoreCase))
        {
            if (manager.State is TorrentState.Stopped or TorrentState.Error)
            {
                try { await engine.RemoveAsync(manager, RemoveMode.CacheDataOnly).ConfigureAwait(false); }
                catch (Exception ex) { logger.LogDebug(ex, "Ignoring error removing a paused torrent"); }
            }
            else
            {
                progress?.Report(new TransferProgress(0, 0, 0, Detail: "Waiting for another game from this torrent to finish…"));
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            manager = await AddOrGetManagerAsync(engine, link, saveDirectory, cancellationToken).ConfigureAwait(false);
        }

        var discovery = new PeerDiscoveryStats();
        manager.PeersFound += discovery.OnPeersFound;
        // A backups torrent's one game: only its folder is downloaded (the rest of the torrent is skipped).
        var only = string.IsNullOrWhiteSpace(link.TorrentFolder) ? null : link.TorrentFolder.Replace('\\', '/').Trim('/');
        var selected = false;
        if (only is not null) keepForSeeding = false; // shared by the whole torrent's own seeder, not per game

        try
        {
            if (settings.AddPublicTrackers)
                await AddPublicTrackersAsync(manager, settings.PublicTrackers).ConfigureAwait(false);

            if (only is not null && manager.HasMetadata) selected = await SelectFolderAsync(manager, saveDirectory, only).ConfigureAwait(false);
            if (manager.State is TorrentState.Stopped or TorrentState.Error)
                await manager.StartAsync().ConfigureAwait(false);
            logger.LogInformation("Torrent started: {Name} ({Trackers} tracker tier(s), DHT {Dht} with {Nodes} nodes)",
                manager.Torrent?.Name ?? link.Label, manager.TrackerManager.Tiers.Count, engine.Dht.State, engine.Dht.NodeCount);

            var timeout = settings.TorrentNoPeersTimeoutMinutes > 0
                ? TimeSpan.FromMinutes(settings.TorrentNoPeersTimeoutMinutes)
                : (TimeSpan?)null;
            var started = Stopwatch.StartNew();

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (only is not null && !selected && manager.HasMetadata)
                {
                    selected = await SelectFolderAsync(manager, saveDirectory, only).ConfigureAwait(false);
                    if (!selected) throw new InvalidDataException($"\"{only}\" isn't in this torrent any more. Add the source again to read its new list.");
                }
                if (only is null ? IsFinished(manager) : selected && IsFinishedSelection(manager)) break;

                if (manager.State == TorrentState.Error)
                {
                    var error = manager.Error?.Exception;
                    throw new IOException($"Torrent failed: {error?.Message ?? "unknown error"}", error);
                }

                var peersSeen = Math.Max(discovery.Total, manager.Peers.Available + manager.Peers.Seeds + manager.Peers.Leechs);
                if (timeout is { } limit && peersSeen == 0 && started.Elapsed > limit)
                    throw new TimeoutException(NoPeersMessage(engine, manager, limit));

                await ApplyJobLimitsAsync(manager, TransferThrottle.Current).ConfigureAwait(false);
                ReportProgress(engine, manager, peersSeen, started.Elapsed, progress, only is not null);
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // Pause / cancel / timeout / failure: persist fast-resume and release the files, then let the job decide.
            await StopQuietlyAsync(manager).ConfigureAwait(false);
            throw;
        }
        finally
        {
            manager.PeersFound -= discovery.OnPeersFound;
        }

        ReportProgress(engine, manager, discovery.Total, TimeSpan.Zero, progress, only is not null);
        await StopQuietlyAsync(manager).ConfigureAwait(false);

        var files = manager.Files
            .Where(f => only is null || f.Priority != Priority.DoNotDownload)
            .Select(f => f.FullPath).Where(File.Exists).ToList();
        if (only is not null)
        {
            // Pieces shared with the next game's first file leave a partial copy of it: not part of this download.
            var wanted = files.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var stray in manager.Files.Where(f => f.Priority == Priority.DoNotDownload).Select(f => f.FullPath))
                if (!wanted.Contains(stray) && File.Exists(stray))
                    try { File.Delete(stray); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        logger.LogInformation("Torrent complete: {Count} files in {Dir}", files.Count, saveDirectory);

        if (keepForSeeding)
        {
            _keptForSeeding[link.Url] = manager; // stopped: files are free for extraction; SeedAsync restarts it
            return files;
        }

        // Keep the data, drop the engine registration and cached fast-resume state.
        await engine.RemoveAsync(manager, RemoveMode.CacheDataOnly).ConfigureAwait(false);
        return files;
    }

    // =============================================================================================
    // Seeding & limits
    // =============================================================================================

    public async Task SeedAsync(DownloadLink link, SeedOptions options, IProgress<SeedProgress>? progress, CancellationToken cancellationToken)
    {
        if (!_keptForSeeding.TryRemove(link.Url, out var manager) || _engine is not { } engine) return;

        var size = Math.Max(1, manager.Torrent?.Size ?? 1);
        var startedAt = Stopwatch.StartNew();
        var uploadedBefore = manager.Monitor.DataBytesSent;
        try
        {
            await ApplyJobLimitsAsync(manager, TransferThrottle.Current).ConfigureAwait(false);
            if (manager.State is TorrentState.Stopped or TorrentState.Error) await manager.StartAsync().ConfigureAwait(false);
            logger.LogInformation("Seeding {Name} (ratio limit {Ratio}, time limit {Time})", manager.Torrent?.Name,
                options.RatioLimit, options.TimeLimit);

            while (true)
            {
                var ratio = (manager.Monitor.DataBytesSent - uploadedBefore) / (double)size;
                progress?.Report(new SeedProgress(ratio, manager.Monitor.UploadRate, manager.Peers.Seeds + manager.Peers.Leechs,
                    startedAt.Elapsed));

                if (options.RatioLimit > 0 && ratio >= options.RatioLimit) break;
                if (options.TimeLimit is { } limit && startedAt.Elapsed >= limit) break;
                if (manager.State == TorrentState.Error) break;

                await ApplyJobLimitsAsync(manager, TransferThrottle.Current).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // stopped by the user or app shutdown
        }
        finally
        {
            await StopQuietlyAsync(manager).ConfigureAwait(false);
            try { await engine.RemoveAsync(manager, RemoveMode.CacheDataOnly).ConfigureAwait(false); }
            catch (Exception ex) { logger.LogDebug(ex, "Ignoring error removing seeded torrent"); }
        }
    }

    public async Task<bool> PrepareSeedingAsync(DownloadLink link, string saveDirectory, CancellationToken cancellationToken)
    {
        if (_keptForSeeding.ContainsKey(link.Url)) return true;
        if (!Directory.Exists(saveDirectory)) return false;

        // After a restart: re-register the finished torrent over its existing files. Fast-resume data (or a hash check)
        // confirms the data, then SeedAsync shares it.
        var engine = await GetEngineAsync(cancellationToken).ConfigureAwait(false);
        var manager = await AddOrGetManagerAsync(engine, link, saveDirectory, cancellationToken).ConfigureAwait(false);
        _keptForSeeding[link.Url] = manager;
        return true;
    }

    public async Task ForgetAsync(DownloadLink link)
    {
        if (!_keptForSeeding.TryRemove(link.Url, out var manager) || _engine is not { } engine) return;
        await StopQuietlyAsync(manager).ConfigureAwait(false);
        try { await engine.RemoveAsync(manager, RemoveMode.CacheDataOnly).ConfigureAwait(false); }
        catch (Exception ex) { logger.LogDebug(ex, "Ignoring error removing torrent"); }
    }

    public async Task ApplyGlobalLimitsAsync(int downloadBytesPerSecond, int uploadBytesPerSecond)
    {
        downloadBytesPerSecond = Math.Max(0, downloadBytesPerSecond);
        uploadBytesPerSecond = Math.Max(0, uploadBytesPerSecond);
        if (downloadBytesPerSecond == _globalDownloadLimit && uploadBytesPerSecond == _globalUploadLimit) return;
        _globalDownloadLimit = downloadBytesPerSecond;
        _globalUploadLimit = uploadBytesPerSecond;

        if (_engine is not { } engine) return; // used when the engine is created
        var updated = new EngineSettingsBuilder(engine.Settings)
        {
            MaximumDownloadRate = downloadBytesPerSecond,
            MaximumUploadRate = uploadBytesPerSecond,
        }.ToSettings();
        await engine.UpdateSettingsAsync(updated).ConfigureAwait(false);
    }

    /// <summary>Applies the job's own limits to its torrent when they changed.</summary>
    private static async Task ApplyJobLimitsAsync(TorrentManager manager, TransferThrottle? throttle)
    {
        var down = throttle?.JobDownloadBytesPerSecond ?? 0;
        var up = throttle?.JobUploadBytesPerSecond ?? 0;
        if (manager.Settings.MaximumDownloadRate == down && manager.Settings.MaximumUploadRate == up) return;

        var updated = new TorrentSettingsBuilder(manager.Settings) { MaximumDownloadRate = down, MaximumUploadRate = up }.ToSettings();
        await manager.UpdateSettingsAsync(updated).ConfigureAwait(false);
    }

    // =============================================================================================
    // Progress & diagnostics
    // =============================================================================================

    /// <summary>Downloads only the files under <paramref name="folder"/> (a path inside the torrent, with its top folder).</summary>
    private static async Task<bool> SelectFolderAsync(TorrentManager manager, string saveDirectory, string folder)
    {
        var any = false;
        foreach (var file in manager.Files)
        {
            var inside = Path.GetRelativePath(saveDirectory, file.FullPath).Replace('\\', '/');
            var wanted = inside.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
                         inside.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
            any |= wanted;
            await manager.SetFilePriorityAsync(file, wanted ? Priority.Normal : Priority.DoNotDownload).ConfigureAwait(false);
        }
        return any;
    }

    /// <summary>Every chosen file is complete.</summary>
    private static bool IsFinishedSelection(TorrentManager manager) =>
        manager.HasMetadata && manager.Files.Where(f => f.Priority != Priority.DoNotDownload).All(f => f.BitField.AllTrue);

    /// <summary>The torrent file itself, from a magnet (asked of the peers), an address or a local file: for a backups source.</summary>
    public async Task<byte[]> GetTorrentFileAsync(string origin, CancellationToken ct)
    {
        if (origin.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            if (!MagnetLink.TryParse(origin, out var magnet) || magnet is null) throw new InvalidDataException("This magnet link is malformed.");
            var engine = await GetEngineAsync(ct).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            var metadata = await engine.DownloadMetadataAsync(magnet, timeout.Token).ConfigureAwait(false);
            return metadata.ToArray();
        }
        if (File.Exists(origin)) return await File.ReadAllBytesAsync(origin, ct).ConfigureAwait(false);
        if (Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.IsFile) return await File.ReadAllBytesAsync(uri.LocalPath, ct).ConfigureAwait(false);
        return await FetchTorrentFileAsync(origin, ct).ConfigureAwait(false);
    }

    private static bool IsFinished(TorrentManager manager) =>
        manager.HasMetadata && (manager.Complete || manager.State == TorrentState.Seeding);

    private static void ReportProgress(ClientEngine engine, TorrentManager manager, int peersFound, TimeSpan elapsed,
        IProgress<TransferProgress>? progress, bool selection = false)
    {
        if (progress is null) return;

        var total = manager.Torrent?.Size ?? 0;
        var done = (long)(total * (manager.Progress / 100.0));
        if (selection && manager.HasMetadata)
        {
            // One game out of the torrent: its own size and how much of it is here.
            var chosen = manager.Files.Where(f => f.Priority != Priority.DoNotDownload).ToList();
            total = chosen.Sum(f => f.Length);
            done = Math.Min(total, chosen.Sum(f => f.BytesDownloaded()));
        }
        var connected = manager.Peers.Seeds + manager.Peers.Leechs;

        string detail;
        if (!manager.HasMetadata)
        {
            detail = connected > 0
                ? $"Getting torrent info from {connected} peer(s)…"
                : $"Looking for peers ({FormatElapsed(elapsed)}): {NetworkSummary(engine, manager)}";
        }
        else
        {
            detail = manager.State switch
            {
                TorrentState.Hashing => "Checking existing data…",
                TorrentState.Downloading when connected == 0 => $"Waiting for peers: {NetworkSummary(engine, manager)}",
                TorrentState.Downloading => "Downloading",
                _ => manager.State.ToString(),
            };
        }

        progress.Report(new TransferProgress(done, total, manager.Monitor.DownloadRate,
            manager.Peers.Seeds, manager.Peers.Leechs, detail, peersFound));
    }

    /// <summary>"DHT 214 nodes · trackers 3/7 reachable" — tells a dead swarm from a blocked network.</summary>
    private static string NetworkSummary(ClientEngine engine, TorrentManager manager)
    {
        var dht = engine.Dht.State switch
        {
            DhtState.Ready => $"DHT {engine.Dht.NodeCount} nodes",
            DhtState.Initialising => "DHT connecting",
            _ => "DHT unavailable",
        };

        var trackers = manager.TrackerManager.Tiers.SelectMany(t => t.Trackers).ToList();
        if (trackers.Count == 0) return $"{dht}, no trackers";

        var ok = trackers.Count(t => t.Status == TrackerState.Ok);
        var tried = trackers.Count(t => t.Status is TrackerState.Ok or TrackerState.Offline or TrackerState.InvalidResponse);
        return tried == 0
            ? $"{dht}, contacting {trackers.Count} tracker(s)"
            : $"{dht}, {ok} of {trackers.Count} tracker(s) reachable";
    }

    private static string NoPeersMessage(ClientEngine engine, TorrentManager manager, TimeSpan waited)
    {
        var trackers = manager.TrackerManager.Tiers.SelectMany(t => t.Trackers).ToList();
        var trackersOk = trackers.Any(t => t.Status == TrackerState.Ok);
        var dhtOk = engine.Dht.State == DhtState.Ready && engine.Dht.NodeCount > 0;

        var cause = (dhtOk || trackersOk)
            ? "The BitTorrent network was reachable, but nobody is sharing this torrent right now (no seeders)."
            : "SeaRaider couldn't reach the DHT or any tracker. BitTorrent traffic may be blocked by a firewall, " +
              "antivirus, VPN or your network. Allow SeaRaider through Windows Firewall and try again.";

        return $"No peers found after {waited.TotalMinutes:0} minutes. {cause} Retry continues from where it stopped.";
    }

    private static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalMinutes < 1 ? $"{elapsed.Seconds}s" : $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:00}s";

    /// <summary>Counts peers discovered by every source (raised on MonoTorrent's thread).</summary>
    private sealed class PeerDiscoveryStats
    {
        private int _total;
        public int Total => Volatile.Read(ref _total);

        public void OnPeersFound(object? sender, PeersAddedEventArgs e) => Interlocked.Add(ref _total, e.NewPeers);
    }

    // =============================================================================================
    // Torrent setup
    // =============================================================================================

    private async Task<TorrentManager> AddOrGetManagerAsync(ClientEngine engine, DownloadLink link, string saveDirectory,
        CancellationToken ct)
    {
        if (link.Kind == DownloadLinkKind.Magnet)
        {
            if (!MagnetLink.TryParse(link.Url, out var magnet) || magnet is null)
                throw new InvalidDataException("This magnet link is malformed.");

            return engine.Torrents.FirstOrDefault(t => t.InfoHashes.Equals(magnet.InfoHashes))
                   ?? await engine.AddAsync(magnet, saveDirectory).ConfigureAwait(false);
        }

        // .torrent: tiny file, keep it in memory. One the download browser collected (behind a link shortener) is
        // already on disk, as a file:// link.
        var bytes = Uri.TryCreate(link.Url, UriKind.Absolute, out var location) && location.IsFile
            ? await File.ReadAllBytesAsync(location.LocalPath, ct).ConfigureAwait(false)
            : await FetchTorrentFileAsync(link.Url, ct).ConfigureAwait(false);

        Torrent torrent;
        try
        {
            torrent = Torrent.Load(bytes);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("The .torrent link did not return a valid torrent file.", ex);
        }

        return engine.Torrents.FirstOrDefault(t => t.InfoHashes.Equals(torrent.InfoHashes))
               ?? await engine.AddAsync(torrent, saveDirectory).ConfigureAwait(false);
    }

    /// <summary>Adds the configured public trackers that the torrent doesn't already have (never for private torrents).</summary>
    private async Task AddPublicTrackersAsync(TorrentManager manager, IEnumerable<string> trackerUrls)
    {
        if (manager.TrackerManager.Private) return;

        var existing = manager.TrackerManager.Tiers
            .SelectMany(t => t.Trackers)
            .Select(t => t.Uri.AbsoluteUri.TrimEnd('/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var url in trackerUrls)
        {
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("udp" or "http" or "https")) continue;
            if (!existing.Add(uri.AbsoluteUri.TrimEnd('/'))) continue;

            try
            {
                await manager.TrackerManager.AddTrackerAsync(uri);
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
            {
                logger.LogDebug(ex, "Skipping tracker {Tracker}", uri);
            }
        }
    }

    // =============================================================================================
    // Engine
    // =============================================================================================

    private async Task<byte[]> FetchTorrentFileAsync(string url, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(HttpClientNames.Downloads);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", repository.GetSettings().UserAgent);
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private async Task<ClientEngine> GetEngineAsync(CancellationToken ct)
    {
        if (_engine is not null) return _engine;

        await _engineGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_engine is not null) return _engine;

            Directory.CreateDirectory(AppPaths.TorrentCacheDirectory);
            var settings = new EngineSettingsBuilder
            {
                CacheDirectory = AppPaths.TorrentCacheDirectory,
                AllowPortForwarding = true,        // UPnP / NAT-PMP for better connectivity
                AllowLocalPeerDiscovery = true,
                AutoSaveLoadDhtCache = true,       // faster peer discovery on next start
                AutoSaveLoadFastResume = true,     // instant resume after pause / restart
                // BestEffort keeps writing fast-resume data while downloading, so even after a crash or power cut the
                // next start loads it instead of re-hashing everything (at worst a few pieces are fetched again).
                FastResumeMode = FastResumeMode.BestEffort,
                AutoSaveLoadMagnetLinkMetadata = true,
                MaximumDownloadRate = _globalDownloadLimit,
                MaximumUploadRate = _globalUploadLimit,
            }.ToSettings();

            // Resolve several bootstrap routers up front and give them to the DHT before it starts.
            var bootstrapNodes = await ResolveBootstrapNodesAsync(ct).ConfigureAwait(false);
            logger.LogInformation("DHT bootstrap: {Count} router address(es) resolved", bootstrapNodes.Count);

            var factories = Factories.Default.WithDhtCreator(() =>
            {
                var dht = Factories.Default.CreateDht();
                if (bootstrapNodes.Count > 0) dht.Add(bootstrapNodes); // queued until the DHT starts
                return dht;
            });

            _engine = new ClientEngine(settings, factories);
            return _engine;
        }
        finally
        {
            _engineGate.Release();
        }
    }

    /// <summary>Resolves the bootstrap routers to compact node entries (20-byte id, IPv4, port), in parallel.</summary>
    private async Task<IReadOnlyList<ReadOnlyMemory<byte>>> ResolveBootstrapNodesAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));

        var lookups = DhtBootstrapRouters.Select(async router =>
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(router.Host, AddressFamily.InterNetwork, timeout.Token).ConfigureAwait(false);
                return addresses.Select(address => CompactNode(address, router.Port));
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                logger.LogDebug(ex, "Could not resolve DHT router {Host}", router.Host);
                return Enumerable.Empty<ReadOnlyMemory<byte>>();
            }
        });

        return (await Task.WhenAll(lookups).ConfigureAwait(false)).SelectMany(n => n).ToList();
    }

    internal static ReadOnlyMemory<byte> CompactNode(IPAddress address, int port)
    {
        var node = new byte[26];
        RandomNumberGenerator.Fill(node.AsSpan(0, 20));       // node id is unknown for routers: any id works
        address.GetAddressBytes().CopyTo(node, 20);           // IPv4
        node[24] = (byte)(port >> 8);                         // port, big-endian
        node[25] = (byte)port;
        return node;
    }

    private async Task StopQuietlyAsync(TorrentManager manager)
    {
        try
        {
            // Bounded: the "stopped" announce to a dead tracker must not hold the files (or the UI) hostage.
            if (manager.State is not (TorrentState.Stopped or TorrentState.Stopping))
                await manager.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Ignoring error while stopping torrent");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_engine is null) return;
        try
        {
            await _engine.StopAllAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Ignoring error while stopping torrent engine");
        }
        _engine.Dispose();
        _engine = null;
    }
}
