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
    MediaService media, ILogger<SourcesFileService> logger) : IDisposable
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
    }

    /// <summary>
    /// Without a snapshot (first start): add everything in the file. With one: add lines new since SeaRaider last wrote
    /// the file, remove lines deleted from it since.
    /// </summary>
    private void ApplyFileChanges(SourcesList inFile, SourcesList? lastWritten)
    {
        var dbSources = repository.GetSources().ToDictionary(s => SourcesFile.Key(s.Url), s => s.Id, StringComparer.Ordinal);
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
            if (dbSources.ContainsKey(key) || lastSources?.Contains(key) == true) continue; // known, or deliberately removed in the app
            TryAddSource(url);
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
            if (dbMedia.ContainsKey(key) || lastMedia?.Contains(key) == true) continue;
            repository.UpsertMediaSource(MediaService.NewSource(new Uri(url))); // read by the Media tab's background refresh
        }

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

    private bool TryAddSource(string url)
    {
        try
        {
            Task.Run(() => ingestion.AddSourceAsync(url)).GetAwaiter().GetResult();
            logger.LogInformation("Added source {Url} from the sources file", url);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UriFormatException)
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
                else if (TryAddSource(url)) added++;
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
                repository.UpsertMediaSource(MediaService.NewSource(new Uri(url)));
                added++;
                addedMedia++;
            }
        });

        if (addedMedia > 0) _ = Task.Run(() => media.RefreshAllAsync()); // read the new channels

        if (addedUpcoming > 0) _ = Task.Run(() => upcoming.RefreshAllAsync()); // read the new upcoming sites
        return (added, skipped);
    }

    /// <summary>Saves every source and upcoming site to <paramref name="path"/>.</summary>
    public Task ExportAsync(string path) => File.WriteAllTextAsync(path, Current());

    private string Current() => SourcesFile.FormatEntries(
        repository.GetSources().Select(s => s.Url),
        repository.GetUpcomingSources().Select(s => new UpcomingEntry(s.Url, s.IsCustom ? s.StartHtml : null, s.IsCustom ? s.EndHtml : null, s.Enabled)),
        repository.GetMediaSources().Select(s => s.Url));

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
