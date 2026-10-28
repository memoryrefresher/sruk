using Microsoft.Extensions.Logging;
using SeaRaider.Core;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Catalog;
using SeaRaider.Core.Models;

namespace SeaRaider.App.Services;

/// <summary>
/// Keeps the sources in a plain file, <c>sources.txt</c> next to SeaRaider.exe (see <see cref="SourcesFile"/> for the format):
/// <list type="bullet">
///   <item><b>At start-up</b> the file is read first. On a first start (or a fresh install) every address in it is added.
///         Later, lines added to the file while SeaRaider was closed are added, and lines removed from it are removed
///         (compared with what SeaRaider last wrote, kept in its data folder).</item>
///   <item><b>While running</b> the file is rewritten whenever a source or upcoming site is added, removed, or (upcoming) switched on or off.
///         A custom list's start / end HTML is written under its address, so it travels with the file.</item>
///   <item><b>Import / Export</b> (Sources page): add every address from any such file, or save the list anywhere.</item>
/// </list>
/// If the install folder can't be written (e.g. under Program Files), the file lives in SeaRaider's data folder instead.
/// </summary>
public sealed class SourcesFileService(ILibraryRepository repository, IIngestionService ingestion, UpcomingService upcoming,
    MediaService media, SeaRaider.Core.Catalog.TorrentSourceService torrentSources, SeaRaider.Core.Catalog.LinkSourceService linkSources,
    SeaRaider.Core.Iptv.IptvService iptv, ILogger<SourcesFileService> logger) : IDisposable
{
    private static readonly string InstallPath = Path.Combine(AppContext.BaseDirectory, SourcesFile.FileName);
    private static readonly string FallbackPath = Path.Combine(AppPaths.DataDirectory, SourcesFile.FileName);
    private static string SnapshotPath => Path.Combine(AppPaths.DataDirectory, "sources.synced.txt");

    private readonly object _writeLock = new();
    private Timer? _writeTimer;
    private string? _lastWritten;

    /// <summary>The file being kept in sync.</summary>
    public string FilePath { get; private set; } = File.Exists(InstallPath) || !File.Exists(FallbackPath) ? InstallPath : FallbackPath;

    /// <summary>Reads the file (adding / removing what changed in it), writes it back, then follows every change.</summary>
    public void SyncAtStartup()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var inFile = SourcesFile.Parse(File.ReadAllText(FilePath));
                var lastWritten = File.Exists(SnapshotPath) ? SourcesFile.Parse(File.ReadAllText(SnapshotPath)) : null;
                ApplyFileChanges(inFile, lastWritten);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Couldn't read {File}", FilePath);
        }

        Write();
        repository.SourcesChanged += (_, _) => WriteSoon();
        repository.SettingsChanged += (_, _) => WriteSoon(); // the Live TV link is a setting
    }

    /// <summary>
    /// Without a snapshot (first start): add everything in the file. With one: add lines new since SeaRaider last wrote
    /// the file, remove lines deleted from it since.
    /// </summary>
    private void ApplyFileChanges(SourcesList inFile, SourcesList? lastWritten)
    {
        var dbSources = repository.GetSources().GroupBy(s => SourcesFile.Key(s.Url)).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);
        var dbUpcomingSources = repository.GetUpcomingSources();
        var dbUpcoming = new Dictionary<string, UpcomingSource>(StringComparer.Ordinal);
        foreach (var s in dbUpcomingSources) dbUpcoming.TryAdd(IdentityOf(s), s);
        var lastSources = lastWritten?.Sources.Select(SourcesFile.Key).ToHashSet(StringComparer.Ordinal);
        var lastUpcoming = lastWritten?.UpcomingEntries.ToDictionary(e => e.Identity, StringComparer.Ordinal);
        var dbMedia = repository.GetMediaSources().ToDictionary(s => SourcesFile.Key(s.Url), s => s.Id, StringComparer.Ordinal);
        var lastMedia = lastWritten?.Media.Select(SourcesFile.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var url in inFile.Sources)
        {
            var key = SourcesFile.Key(url);
            if (dbSources.ContainsKey(key))
            {
                // A link game renamed or moved to another console in the file since SeaRaider last wrote it.
                var now = inFile.SettingsOf(url, media: false);
                if (lastWritten is not null && !SameSettings(now, lastWritten.SettingsOf(url, media: false))) UpdateLinkGame(dbSources[key], now);
                continue;
            }
            if (lastSources?.Contains(key) == true) continue; // deliberately removed in the app
            TryAddSource(url, inFile.SettingsOf(url, media: false));
        }
        foreach (var entry in inFile.UpcomingEntries)
        {
            if (dbUpcoming.TryGetValue(entry.Identity, out var existing))
            {
                // Switched on / off in the file since SeaRaider last wrote it.
                if (lastUpcoming?.TryGetValue(entry.Identity, out var before) == true && before.Enabled != entry.Enabled && existing.Enabled != entry.Enabled)
                {
                    existing.Enabled = entry.Enabled;
                    repository.UpsertUpcomingSource(existing);
                }
                continue;
            }
            if (lastUpcoming?.ContainsKey(entry.Identity) == true) continue; // deliberately removed in the app
            AddUpcoming(entry);
        }

        foreach (var url in inFile.Media)
        {
            var key = SourcesFile.Key(url);
            var now = inFile.SettingsOf(url, media: true);
            if (dbMedia.TryGetValue(key, out var mediaId))
            {
                // Settings changed in the file since SeaRaider last wrote it.
                if (lastWritten is not null && !SameSettings(now, lastWritten.SettingsOf(url, media: true)) &&
                    repository.GetMediaSources().FirstOrDefault(m => m.Id == mediaId) is { } existingMedia && ApplyMediaSettings(existingMedia, now))
                    repository.UpsertMediaSource(existingMedia);
                continue;
            }
            if (lastMedia?.Contains(key) == true) continue;
            repository.UpsertMediaSource(NewMediaSource(url, now)); // read by the Media tab's background refresh
        }

        ApplyLiveTv(inFile.LiveTv, lastWritten?.LiveTv, lastWritten is not null);

        if (lastWritten is null) return;
        var fileMedia = inFile.Media.Select(SourcesFile.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var key in lastMedia!.Where(k => !fileMedia.Contains(k) && dbMedia.ContainsKey(k)))
            repository.DeleteMediaSource(dbMedia[key]);
        var fileSources = inFile.Sources.Select(SourcesFile.Key).ToHashSet(StringComparer.Ordinal);
        var fileUpcoming = inFile.UpcomingEntries.Select(e => e.Identity).ToHashSet(StringComparer.Ordinal);
        foreach (var key in lastSources!.Where(k => !fileSources.Contains(k) && dbSources.ContainsKey(k)))
        {
            logger.LogInformation("Removing source {Source}: deleted from {File}", key, FilePath);
            Task.Run(() => ingestion.RemoveSourceAsync(dbSources[key], removeGames: true)).GetAwaiter().GetResult();
        }
        foreach (var key in lastUpcoming!.Keys.Where(k => !fileUpcoming.Contains(k) && dbUpcoming.ContainsKey(k)))
            repository.DeleteUpcomingSource(dbUpcoming[key].Id);
    }

    private static bool SameSettings(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var v) && v == p.Value);

    /// <summary>The Live TV playlist in the file: set when it's new or changed there, cleared when it was deleted there.</summary>
    private void ApplyLiveTv(string? inFile, string? lastWritten, bool hasSnapshot)
    {
        var current = iptv.PlaylistUrl;
        if (!string.IsNullOrWhiteSpace(inFile))
        {
            if (string.Equals(inFile, current, StringComparison.Ordinal)) return;
            if (hasSnapshot && string.Equals(inFile, lastWritten, StringComparison.Ordinal)) return; // changed in the app since: the app wins
            SetLiveTv(inFile);
        }
        else if (hasSnapshot && !string.IsNullOrWhiteSpace(lastWritten) && string.Equals(lastWritten, current, StringComparison.Ordinal))
        {
            iptv.Clear(); // deleted from the file
        }
    }

    private void SetLiveTv(string link) => _ = Task.Run(async () =>
    {
        try
        {
            var count = await iptv.RefreshAsync(link).ConfigureAwait(false);
            logger.LogInformation("Live TV playlist from the sources file: {Count} channels", count);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Skipped the Live TV link from the sources file: {Reason}", ex.Message);
        }
    });

    private void UpdateLinkGame(Guid sourceId, IReadOnlyDictionary<string, string> settings)
    {
        try
        {
            if (repository.GetSources().FirstOrDefault(x => x.Id == sourceId) is not { IsLink: true } source) return;
            settings.TryGetValue("name", out var name);
            settings.TryGetValue("console", out var console);
            linkSources.Update(source, name, console);
            _ = Task.Run(() => linkSources.EnrichAsync(source));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        {
            logger.LogWarning("Couldn't apply the file's changes to a link game: {Reason}", ex.Message);
        }
    }

    /// <summary>A media source for an address with the settings written under it in the file.</summary>
    private static MediaSource NewMediaSource(string url, IReadOnlyDictionary<string, string> settings)
    {
        var source = MediaService.NewSource(new Uri(url));
        ApplyMediaSettings(source, settings);
        return source;
    }

    /// <summary>Returns whether anything changed.</summary>
    private static bool ApplyMediaSettings(MediaSource source, IReadOnlyDictionary<string, string> settings)
    {
        var changed = false;
        void Set<T>(T value, T current, Action<T> apply)
        {
            if (EqualityComparer<T>.Default.Equals(value, current)) return;
            apply(value);
            changed = true;
        }

        var name = settings.GetValueOrDefault("name");
        if (!string.IsNullOrWhiteSpace(name))
        {
            Set(name.Trim(), source.Name, v => source.Name = v);
            Set(true, source.NameChosen, v => source.NameChosen = v);
        }
        Set(Blank(settings.GetValueOrDefault("hint")), source.PlayerHint, v => source.PlayerHint = v);
        Set(Blank(settings.GetValueOrDefault("click")), source.PlayerClick, v => source.PlayerClick = v);
        Set(IsYes(settings.GetValueOrDefault("page-mode")), source.PlayerPageMode, v => source.PlayerPageMode = v);
        Set(IsYes(settings.GetValueOrDefault("browser")), source.NeedsBrowser, v => source.NeedsBrowser = v);
        Set(int.TryParse(settings.GetValueOrDefault("link"), out var link) && link > 0 ? link : 0, source.PlayerLink, v => source.PlayerLink = v);

        // A single stream whose address doesn't say what it is: its format travels as "format: hls".
        var format = settings.GetValueOrDefault("format")?.Trim().ToLowerInvariant() switch
        {
            "hls" => (SeaRaider.Core.Scraping.StreamFormat?)SeaRaider.Core.Scraping.StreamFormat.Hls,
            "dash" => SeaRaider.Core.Scraping.StreamFormat.Dash,
            "video" or "file" => SeaRaider.Core.Scraping.StreamFormat.File,
            "embed" => SeaRaider.Core.Scraping.StreamFormat.Embed,
            _ => null,
        };
        if (format is not null && (source.Kind != MediaSourceKind.Stream || source.FeedUrl is null))
        {
            source.Kind = MediaSourceKind.Stream;
            source.FeedUrl = SeaRaider.Core.Scraping.MediaStreams.WithHint(source.Url, format!.Value);
            changed = true;
        }
        return changed;
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    private static bool IsYes(string? text) => text?.Trim().ToLowerInvariant() is "yes" or "on" or "true" or "1";

    /// <summary>The format a single stream was found to have, for "format:" in the file (null for anything else).</summary>
    private static string? FormatName(MediaSource s) =>
        s.Kind == MediaSourceKind.Stream && s.FeedUrl is { } feed && SeaRaider.Core.Scraping.MediaStreams.HasHint(feed)
            ? SeaRaider.Core.Scraping.MediaStreams.FormatOf(feed) switch
            {
                SeaRaider.Core.Scraping.StreamFormat.Hls => "hls",
                SeaRaider.Core.Scraping.StreamFormat.Dash => "dash",
                SeaRaider.Core.Scraping.StreamFormat.File => "video",
                SeaRaider.Core.Scraping.StreamFormat.Embed => "embed",
                _ => null,
            }
            : null;

    private bool TryAddSource(string url, IReadOnlyDictionary<string, string>? settings = null)
    {
        try
        {
            // A backups torrent: its game list is read from the torrent (from whoever is sharing it, for a magnet).
            if (SeaRaider.Core.Catalog.TorrentSourceService.IsTorrentAddress(url))
            {
                // In the background: a magnet's list comes from its peers, which can take a while (never hold up the start).
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var (source, games) = await torrentSources.AddAsync(url).ConfigureAwait(false);
                        logger.LogInformation("Added backups torrent {Name} ({Games} games) from the sources file", source.DisplayName, games);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning("Skipped torrent {Url} from the sources file: {Reason}", url, ex.Message);
                    }
                });
                return true;
            }
            else if (SeaRaider.Core.Catalog.LinkSourceService.IsGameLink(url))
            {
                // One game's download link (Drive, MEGA …): a store game named from the link, filed under the console its name or
                // file type says (else PC games). The picture and details are looked up in the background.
                var (source, _) = linkSources.Add(url, settings?.GetValueOrDefault("name"), settings?.GetValueOrDefault("console"));
                _ = Task.Run(() => linkSources.EnrichAsync(source));
            }
            else
                Task.Run(() => ingestion.AddSourceAsync(url)).GetAwaiter().GetResult();
            logger.LogInformation("Added source {Url} from the sources file", url);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UriFormatException or InvalidDataException
                                       or HttpRequestException or OperationCanceledException or IOException)
        {
            logger.LogWarning("Skipped {Url} from the sources file: {Reason}", url, ex.Message);
            return false;
        }
    }

    private void AddUpcoming(UpcomingEntry entry)
    {
        var uri = new Uri(entry.Url);
        repository.UpsertUpcomingSource(new UpcomingSource
        {
            Url = uri.AbsoluteUri,
            Name = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host,
            Enabled = entry.Enabled,
            IsCustom = entry.IsCustom,
            StartHtml = entry.IsCustom ? entry.StartHtml!.Trim() : null,
            EndHtml = entry.IsCustom && !string.IsNullOrWhiteSpace(entry.EndHtml) ? entry.EndHtml.Trim() : null,
        });
    }

    /// <summary>Same identity the file uses: the address, plus start / end HTML for a custom list.</summary>
    private static string IdentityOf(UpcomingSource s) =>
        UpcomingEntry.Identify(s.Url, s.IsCustom ? s.StartHtml : null, s.IsCustom ? s.EndHtml : null);

    /// <summary>Adds every address in <paramref name="path"/> that isn't a source yet.</summary>
    /// <returns>How many were added, and how many were already there or couldn't be added.</returns>
    public async Task<(int Added, int Skipped)> ImportAsync(string path)
    {
        var list = SourcesFile.Parse(await File.ReadAllTextAsync(path));
        var known = repository.GetSources().Select(s => SourcesFile.Key(s.Url)).ToHashSet(StringComparer.Ordinal);
        var knownUpcoming = repository.GetUpcomingSources().Select(IdentityOf).ToHashSet(StringComparer.Ordinal);
        var knownMedia = repository.GetMediaSources().Select(s => SourcesFile.Key(s.Url)).ToHashSet(StringComparer.Ordinal);
        int added = 0, skipped = 0, addedUpcoming = 0, addedMedia = 0;

        await Task.Run(() =>
        {
            foreach (var url in list.Sources)
            {
                if (!known.Add(SourcesFile.Key(url))) skipped++;
                else if (TryAddSource(url, list.SettingsOf(url, media: false))) added++;
                else skipped++;
            }
            foreach (var entry in list.UpcomingEntries)
            {
                if (!knownUpcoming.Add(entry.Identity)) { skipped++; continue; }
                AddUpcoming(entry);
                added++;
                addedUpcoming++;
            }
            foreach (var url in list.Media)
            {
                if (!knownMedia.Add(SourcesFile.Key(url))) { skipped++; continue; }
                repository.UpsertMediaSource(NewMediaSource(url, list.SettingsOf(url, media: true)));
                added++;
                addedMedia++;
            }
            if (!string.IsNullOrWhiteSpace(list.LiveTv) && !string.Equals(list.LiveTv, iptv.PlaylistUrl, StringComparison.Ordinal))
            {
                SetLiveTv(list.LiveTv);
                added++;
            }
        });

        if (addedMedia > 0) _ = Task.Run(() => media.RefreshAllAsync()); // read the new channels

        if (addedUpcoming > 0) _ = Task.Run(() => upcoming.RefreshAllAsync()); // read the new upcoming sites
        return (added, skipped);
    }

    /// <summary>Saves every source and upcoming site to <paramref name="path"/>.</summary>
    public Task ExportAsync(string path) => File.WriteAllTextAsync(path, Current());

    private string Current() => SourcesFile.FormatEntries(
        repository.GetSources().Select(s => s.IsLink
            // A game added by its download link keeps the name typed for it and its console.
            ? SourcesFile.WithSettings(s.Url, ("name", s.LinkNameChosen ? s.DisplayName : null), ("console", s.LinkSystemId))
            : SeaRaider.Core.Catalog.TorrentSourceService.ShareAddress(s)),
        repository.GetUpcomingSources().Select(s => new UpcomingEntry(s.Url, s.IsCustom ? s.StartHtml : null, s.IsCustom ? s.EndHtml : null, s.Enabled)),
        repository.GetMediaSources().Select(s => SourcesFile.WithSettings(s.Url,
            ("name", s.NameChosen ? s.Name : null), ("format", FormatName(s)), ("hint", s.PlayerHint), ("click", s.PlayerClick),
            ("page-mode", s.PlayerPageMode ? "yes" : null), ("link", s.PlayerLink > 0 ? s.PlayerLink.ToString() : null),
            ("browser", s.NeedsBrowser ? "yes" : null))),
        iptv.PlaylistUrl);

    /// <summary>Sources change in bursts (a crawl updates its source often): write once things are quiet.</summary>
    private void WriteSoon()
    {
        lock (_writeLock)
        {
            _writeTimer ??= new Timer(_ => Write(), null, Timeout.Infinite, Timeout.Infinite);
            _writeTimer.Change(TimeSpan.FromMilliseconds(800), Timeout.InfiniteTimeSpan);
        }
    }

    private void Write()
    {
        try
        {
            var text = Current();
            lock (_writeLock)
            {
                if (text == _lastWritten) return; // nothing that the file shows has changed (a crawl updated its counts)
                try
                {
                    File.WriteAllText(FilePath, text);
                }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && FilePath == InstallPath)
                {
                    logger.LogInformation("Can't write {File} ({Reason}); keeping the sources file in the data folder", InstallPath, ex.Message);
                    FilePath = FallbackPath;
                    Directory.CreateDirectory(Path.GetDirectoryName(FallbackPath)!);
                    File.WriteAllText(FilePath, text);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(SnapshotPath)!);
                File.WriteAllText(SnapshotPath, text);
                _lastWritten = text;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't write the sources file"); // never take the app down for this
        }
    }

    public void Dispose() => _writeTimer?.Dispose();
}
