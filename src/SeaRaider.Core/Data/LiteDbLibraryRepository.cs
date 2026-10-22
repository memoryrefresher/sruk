using LiteDB;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Data;

/// <summary>
/// LiteDB-backed <see cref="ILibraryRepository"/>. A single embedded file
/// (%LOCALAPPDATA%\SeaRaider\searaider.db) holds everything, so the app is fully usable offline.
/// <see cref="LiteDatabase"/> is thread-safe in v5, so one shared instance is used.
/// </summary>
public sealed class LiteDbLibraryRepository : ILibraryRepository, IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<GameMetadata> _games;
    private readonly ILiteCollection<GameSource> _sources;
    private readonly ILiteCollection<InstalledGame> _installs;
    private readonly ILiteCollection<DownloadJob> _jobs;
    private readonly ILiteCollection<AppSettings> _settings;
    private readonly ILiteCollection<CrawlUrlRecord> _crawl;
    private readonly ILiteCollection<CatalogEntry> _catalog;
    private readonly ILiteCollection<ArtworkRecord> _artwork;
    private readonly ILiteCollection<UpcomingSource> _upcomingSources;
    private readonly ILiteCollection<UpcomingRelease> _upcoming;
    private readonly ILiteCollection<WishlistItem> _wishlist;
    private readonly ILiteCollection<MediaSource> _mediaSources;
    private readonly ILiteCollection<MediaItem> _media;
    private readonly object _catalogWriteLock = new();

    /// <inheritdoc />
    public event EventHandler<CatalogEntriesChangedEventArgs>? CatalogEntriesChanged;

    public LiteDbLibraryRepository(string? databasePath = null)
    {
        var path = databasePath ?? AppPaths.DatabasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        _db = new LiteDatabase(new ConnectionString { Filename = path, Connection = ConnectionType.Direct });

        _games = _db.GetCollection<GameMetadata>("games");
        _sources = _db.GetCollection<GameSource>("sources");
        _installs = _db.GetCollection<InstalledGame>("installs");
        _jobs = _db.GetCollection<DownloadJob>("jobs");
        _settings = _db.GetCollection<AppSettings>("settings");
        _crawl = _db.GetCollection<CrawlUrlRecord>("crawl_urls");
        _catalog = _db.GetCollection<CatalogEntry>("catalog_index");
        _artwork = _db.GetCollection<ArtworkRecord>("artwork");
        _upcomingSources = _db.GetCollection<UpcomingSource>("upcoming_sources");
        _upcoming = _db.GetCollection<UpcomingRelease>("upcoming");
        _wishlist = _db.GetCollection<WishlistItem>("wishlist");
        _mediaSources = _db.GetCollection<MediaSource>("media_sources");
        _media = _db.GetCollection<MediaItem>("media");

        _games.EnsureIndex(g => g.SourceId);
        _games.EnsureIndex(g => g.Title);
        _installs.EnsureIndex(i => i.State);
        _crawl.EnsureIndex(c => c.SourceId);
        _catalog.EnsureIndex(c => c.SourceId);
        _upcoming.EnsureIndex(u => u.SourceId);
        _media.EnsureIndex(m => m.SourceId);

        BuildCatalogIndexIfMissing();
    }

    /// <summary>One-time migration for databases created before the catalog index existed.</summary>
    private void BuildCatalogIndexIfMissing()
    {
        if (_catalog.Count() > 0 || _games.Count() == 0) return;

        var batch = new List<CatalogEntry>(500);
        foreach (var game in _games.FindAll())
        {
            batch.Add(CatalogEntry.From(game));
            if (batch.Count < 500) continue;
            _catalog.Upsert(batch);
            batch.Clear();
        }
        if (batch.Count > 0) _catalog.Upsert(batch);
    }

    // Catalog -------------------------------------------------------------------------------

    public IReadOnlyList<GameMetadata> GetAllGames() =>
        _games.FindAll().Select(g => g.Normalize()).OrderBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase).ToList();

    public GameMetadata? GetGame(string gameId) => _games.FindById(gameId)?.Normalize();

    public void UpsertGames(IEnumerable<GameMetadata> games)
    {
        var list = games.Select(g => g.Normalize()).ToList();
        if (list.Count == 0) return;

        List<CatalogEntry> entries;
        lock (_catalogWriteLock)
        {
            _games.Upsert(list);
            entries = list.Select(CatalogEntry.From).ToList();
            _catalog.Upsert(entries);
        }
        CatalogEntriesChanged?.Invoke(this, new CatalogEntriesChangedEventArgs(entries, [], false));
    }

    public IReadOnlyList<GameMetadata> MergeGames(IEnumerable<GameMetadata> incoming)
    {
        var changed = new List<GameMetadata>();
        lock (_catalogWriteLock)
        {
            foreach (var game in incoming.Select(g => g.Normalize()))
            {
                var existing = _games.FindById(game.Id)?.Normalize();
                GameMetadata result;

                if (existing is null)
                {
                    result = game;
                }
                else if (game.DetailsLoaded)
                {
                    // Fresh full record wins, but keep facts only the old record knew.
                    game.UploadedAtUtc ??= existing.UploadedAtUtc;
                    game.CoverImageUrl ??= existing.CoverImageUrl;
                    if (game.ScreenshotUrls.Count == 0) game.ScreenshotUrls = existing.ScreenshotUrls;
                    result = game;
                }
                else if (!existing.DetailsLoaded)
                {
                    // Stub replaces stub (newer title / cover / excerpt).
                    game.UploadedAtUtc ??= existing.UploadedAtUtc;
                    game.CoverImageUrl ??= existing.CoverImageUrl;
                    result = game;
                }
                else
                {
                    // Never downgrade a fully loaded game to a search stub; only fill gaps.
                    var dirty = false;
                    if (existing.UploadedAtUtc is null && game.UploadedAtUtc is not null) { existing.UploadedAtUtc = game.UploadedAtUtc; dirty = true; }
                    if (existing.CoverImageUrl is null && game.CoverImageUrl is not null) { existing.CoverImageUrl = game.CoverImageUrl; dirty = true; }
                    if (!dirty) continue;
                    result = existing;
                }

                _games.Upsert(result);
                changed.Add(result);
            }

            if (changed.Count > 0) _catalog.Upsert(changed.Select(CatalogEntry.From));
        }

        if (changed.Count > 0)
            CatalogEntriesChanged?.Invoke(this, new CatalogEntriesChangedEventArgs(changed.Select(CatalogEntry.From).ToList(), [], false));
        return changed;
    }

    public IReadOnlyList<CatalogEntry> GetCatalogEntries() => _catalog.FindAll().ToList();

    public int DeleteGamesForSource(Guid sourceId)
    {
        int removed;
        lock (_catalogWriteLock)
        {
            removed = _games.DeleteMany(g => g.SourceId == sourceId);
            _catalog.DeleteMany(c => c.SourceId == sourceId);
        }
        CatalogEntriesChanged?.Invoke(this, new CatalogEntriesChangedEventArgs([], [], true));
        return removed;
    }

    public int CountGamesForSource(Guid sourceId) => _catalog.Count(c => c.SourceId == sourceId);

    // Sources -------------------------------------------------------------------------------

    public IReadOnlyList<GameSource> GetSources() => _sources.FindAll().OrderBy(s => s.DisplayName).ToList();

    public GameSource? GetSource(Guid sourceId) => _sources.FindById(sourceId);

    public event EventHandler? SourcesChanged;

    public void UpsertSource(GameSource source)
    {
        _sources.Upsert(source);
        SourcesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void DeleteSource(Guid sourceId)
    {
        _sources.Delete(sourceId);
        SourcesChanged?.Invoke(this, EventArgs.Empty);
    }

    // Crawl state ---------------------------------------------------------------------------

    public IReadOnlyDictionary<string, CrawlUrlRecord> GetCrawlRecords(Guid sourceId) =>
        _crawl.Find(c => c.SourceId == sourceId).ToDictionary(c => c.Url, StringComparer.OrdinalIgnoreCase);

    public void UpsertCrawlRecords(IEnumerable<CrawlUrlRecord> records) => _crawl.Upsert(records);

    public int DeleteCrawlRecordsForSource(Guid sourceId) => _crawl.DeleteMany(c => c.SourceId == sourceId);

    // Installs ------------------------------------------------------------------------------

    public IReadOnlyList<InstalledGame> GetInstalls() => _installs.FindAll().ToList();

    public InstalledGame? GetInstall(string gameId) => _installs.FindById(gameId);

    public void UpsertInstall(InstalledGame install) => _installs.Upsert(install);

    // Download jobs -------------------------------------------------------------------------

    public IReadOnlyList<DownloadJob> GetJobs() => _jobs.FindAll().OrderByDescending(j => j.CreatedAtUtc).ToList();

    public void UpsertJob(DownloadJob job) => _jobs.Upsert(job);

    public void DeleteJob(Guid jobId) => _jobs.Delete(jobId);

    // Artwork -------------------------------------------------------------------------------

    public ArtworkRecord? GetArtwork(string key) => string.IsNullOrEmpty(key) ? null : _artwork.FindById(key);

    public void UpsertArtwork(ArtworkRecord record) => _artwork.Upsert(record);

    // Media tab ----------------------------------------------------------------------------------

    public IReadOnlyList<MediaSource> GetMediaSources() => _mediaSources.FindAll().OrderBy(s => s.AddedUtc).ToList();

    public void UpsertMediaSource(MediaSource source)
    {
        _mediaSources.Upsert(source);
        SourcesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void DeleteMediaSource(Guid sourceId)
    {
        _mediaSources.Delete(sourceId);
        _media.DeleteMany(m => m.SourceId == sourceId);
        SourcesChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<MediaItem> GetMediaItems(Guid sourceId) =>
        _media.Find(m => m.SourceId == sourceId).OrderBy(m => m.Order).ToList();

    public void UpsertMediaItems(IReadOnlyCollection<MediaItem> items)
    {
        if (items.Count > 0) _media.Upsert(items);
    }

    public void ReplaceMediaItems(Guid sourceId, IReadOnlyCollection<MediaItem> items)
    {
        _media.DeleteMany(m => m.SourceId == sourceId);
        if (items.Count > 0) _media.Upsert(items); // upsert: a feed can list the same video twice
    }

    // Upcoming releases & wishlist ---------------------------------------------------------------

    public IReadOnlyList<UpcomingSource> GetUpcomingSources() => _upcomingSources.FindAll().OrderBy(s => s.AddedUtc).ToList();

    public void UpsertUpcomingSource(UpcomingSource source)
    {
        _upcomingSources.Upsert(source);
        SourcesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void DeleteUpcomingSource(Guid sourceId)
    {
        _upcomingSources.Delete(sourceId);
        _upcoming.DeleteMany(u => u.SourceId == sourceId);
        SourcesChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<UpcomingRelease> GetUpcomingReleases() => _upcoming.FindAll().ToList();

    public void ReplaceUpcomingReleases(Guid sourceId, IReadOnlyCollection<UpcomingRelease> releases)
    {
        var keep = releases.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        var old = _upcoming.Find(u => u.SourceId == sourceId).ToList();
        foreach (var gone in old.Where(o => !keep.Contains(o.Key))) _upcoming.Delete(gone.Key);

        // Pictures already found on the web are kept for games still listed.
        var previous = old.ToDictionary(o => o.Key, StringComparer.Ordinal);
        foreach (var release in releases)
        {
            if ((previous.TryGetValue(release.Key, out var before) || (before = _upcoming.FindById(release.Key)) is not null) && before.ArtworkDone)
            {
                release.IsDemo |= before.IsDemo;
                // What was found on the web wins over the listing's small picture; the listing's date stays fresh.
                release.HeroUrl = before.HeroUrl ?? release.HeroUrl;
                release.ImageUrl = before.ImageUrl ?? release.ImageUrl;
                release.Description ??= before.Description;
                release.Developer ??= before.Developer;
                if (release.Tags.Count == 0) release.Tags = before.Tags;
                release.SteamAppId ??= before.SteamAppId;
                release.PageUrl ??= before.PageUrl;
                release.ReleaseText ??= before.ReleaseText;
                release.FindOnSteam &= before.FindOnSteam;
                release.ArtworkChecked = true;
                release.ArtworkVersion = before.ArtworkVersion;
            }
        }
        if (releases.Count > 0) _upcoming.Upsert(releases);
    }

    public void UpsertUpcomingRelease(UpcomingRelease release) => _upcoming.Upsert(release);

    public IReadOnlyList<WishlistItem> GetWishlist() => _wishlist.FindAll().OrderByDescending(w => w.AddedUtc).ToList();

    public WishlistItem? GetWishlistItem(string key) => string.IsNullOrEmpty(key) ? null : _wishlist.FindById(key);

    public void UpsertWishlistItem(WishlistItem item) => _wishlist.Upsert(item);

    public void DeleteWishlistItem(string key) => _wishlist.Delete(key);

    // Settings ------------------------------------------------------------------------------

    public AppSettings GetSettings() => _settings.FindById(1) ?? new AppSettings();

    public event EventHandler<AppSettings>? SettingsChanged;

    public void SaveSettings(AppSettings settings)
    {
        settings.Id = 1;
        _settings.Upsert(settings);
        SettingsChanged?.Invoke(this, settings);
    }

    private int _disposed;

    public void Dispose()
    {
        // Idempotent: DI may hold this instance under both its concrete and interface registrations.
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _db.Dispose();
    }
}
