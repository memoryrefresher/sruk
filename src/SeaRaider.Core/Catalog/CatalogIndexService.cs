using System.Collections.Concurrent;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Catalog;

/// <summary>
/// In-memory index of every cached game (lightweight <see cref="CatalogEntry"/> rows), kept in sync with the
/// repository. Filtering and sorting run here in milliseconds even for tens of thousands of games, so the store
/// never has to load full game records to show a page of cards.
/// <para>Each entry's normalised title (<see cref="TitleNormalizer"/>) and duplicate key are cached, so relevance
/// scoring (<see cref="SearchScorer"/>) and "same game on other sites" lookups (<see cref="GetVariants"/>) stay fast.</para>
/// </summary>
public sealed class CatalogIndexService
{
    private readonly ILibraryRepository _repository;
    private readonly ConcurrentDictionary<string, CatalogEntry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string Normalized, string Key)> _titles = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _byKey = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private volatile bool _loaded;
    private volatile HashSet<string>? _compactKeys; // every key with its spaces removed; rebuilt on demand after a change

    public CatalogIndexService(ILibraryRepository repository)
    {
        _repository = repository;
        _repository.CatalogEntriesChanged += OnRepositoryChanged;
    }

    /// <summary>Raised (any thread) after entries changed; empty ids + IsReset means "reload".</summary>
    public event EventHandler<CatalogChangedEventArgs>? Changed;

    public int Count => _entries.Count;

    public bool IsLoaded => _loaded;

    /// <summary>Loads the index from the database once (off the calling thread).</summary>
    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_loaded) return;
        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded) return;
            var all = await Task.Run(() => _repository.GetCatalogEntries(), cancellationToken).ConfigureAwait(false);
            await Task.Run(() => { foreach (var entry in all) Put(entry); }, cancellationToken).ConfigureAwait(false);
            _loaded = true;
        }
        finally
        {
            _loadGate.Release();
        }
        Changed?.Invoke(this, CatalogChangedEventArgs.Reset);
    }

    public CatalogEntry? Get(string id) => _entries.TryGetValue(id, out var entry) ? entry : null;

    /// <summary>
    /// Filters and sorts the cached catalog. With search text, only relevant entries (score ≥
    /// <see cref="SearchScorer.MinimumScore"/>) are returned, ordered by relevance for <see cref="CatalogSort.Relevance"/>.
    /// </summary>
    public IReadOnlyList<CatalogEntry> Query(CatalogQuery query)
    {
        IEnumerable<CatalogEntry> entries = _entries.Values;
        if (query.SourceId is { } sourceId) entries = entries.Where(e => e.SourceId == sourceId);
        if (query.SourceIds is { } sourceIds)
        {
            var allowed = sourceIds as IReadOnlySet<Guid> ?? sourceIds.ToHashSet();
            entries = entries.Where(e => allowed.Contains(e.SourceId));
        }

        var search = SearchQuery.Parse(query.Text);
        if (search.IsEmpty)
            return entries.Sort(query.Sort == CatalogSort.Relevance ? CatalogSort.UploadNewest : query.Sort).ToList();

        var scored = entries
            .Select(e => (Entry: e, Score: Score(search, e)))
            .Where(x => x.Score >= SearchScorer.MinimumScore);

        return query.Sort == CatalogSort.Relevance
            ? scored.OrderByDescending(x => x.Score).ThenByDescending(x => x.Entry.UploadedAtUtc).Select(x => x.Entry).ToList()
            : scored.Select(x => x.Entry).Sort(query.Sort).ToList();
    }

    /// <summary>Relevance of an entry to a search (uses the cached normalised title).</summary>
    public double Score(SearchQuery search, CatalogEntry entry)
    {
        var normalized = _titles.TryGetValue(entry.Id, out var t) ? t.Normalized : TitleNormalizer.NormalizeForSearch(entry.Title);
        return SearchScorer.ScoreNormalized(search, normalized, entry.Genres, entry.Developer);
    }

    /// <summary>Every cached copy of the same game (all sources), including <paramref name="gameId"/> itself.</summary>
    public IReadOnlyList<CatalogEntry> GetVariants(string gameId)
    {
        if (!_titles.TryGetValue(gameId, out var t) || !_byKey.TryGetValue(t.Key, out var ids))
            return Get(gameId) is { } only ? new List<CatalogEntry> { only } : new List<CatalogEntry>();
        return ids.Keys.Select(Get).OfType<CatalogEntry>().ToList();
    }

    /// <summary>Every cached entry whose duplicate key is <paramref name="key"/> (see <see cref="GameDeduplicator.KeyFor"/>).</summary>
    public IReadOnlyList<CatalogEntry> FindByKey(string key)
    {
        if (string.IsNullOrEmpty(key) || !_byKey.TryGetValue(key, out var ids)) return [];
        return ids.Keys.Select(Get).OfType<CatalogEntry>().ToList();
    }

    /// <summary>
    /// True when some store entry has this key or the same one with different spacing ("Star Kite" / "StarKite"): sites don't
    /// agree on spaces, and a game that is in the store must never show as coming soon.
    /// </summary>
    public bool HasSimilarKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        var compact = _compactKeys;
        if (compact is null)
        {
            compact = _byKey.Where(p => !p.Value.IsEmpty).Select(p => p.Key.Replace(" ", string.Empty, StringComparison.Ordinal))
                .ToHashSet(StringComparer.Ordinal);
            _compactKeys = compact;
        }
        var wanted = key.Replace(" ", string.Empty, StringComparison.Ordinal);
        return wanted.Length >= 3 && compact.Contains(wanted);
    }

    /// <summary>The cached entry for a page address (trailing slash and case ignored), or null.</summary>
    public CatalogEntry? FindByPageUrl(string pageUrl)
    {
        if (string.IsNullOrWhiteSpace(pageUrl)) return null;
        var wanted = pageUrl.Trim().TrimEnd('/');
        return _entries.Values.FirstOrDefault(e => string.Equals(e.PageUrl.TrimEnd('/'), wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Duplicate key of a cached entry.</summary>
    public string KeyOf(CatalogEntry entry) => _titles.TryGetValue(entry.Id, out var t) ? t.Key : GameDeduplicator.KeyFor(entry);

    private void Put(CatalogEntry entry)
    {
        _compactKeys = null;
        _entries[entry.Id] = entry;
        var normalized = TitleNormalizer.NormalizeForSearch(entry.Title);
        var key = GameDeduplicator.KeyFor(entry);

        if (_titles.TryGetValue(entry.Id, out var old) && old.Key != key && _byKey.TryGetValue(old.Key, out var oldIds))
            oldIds.TryRemove(entry.Id, out _);
        _titles[entry.Id] = (normalized, key);
        _byKey.GetOrAdd(key, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal))[entry.Id] = 0;
    }

    private void Remove(string id)
    {
        _compactKeys = null;
        _entries.TryRemove(id, out _);
        if (_titles.TryRemove(id, out var t) && _byKey.TryGetValue(t.Key, out var ids)) ids.TryRemove(id, out _);
    }

    private void OnRepositoryChanged(object? sender, CatalogEntriesChangedEventArgs e)
    {
        if (e.IsReset)
        {
            _entries.Clear();
            _titles.Clear();
            _byKey.Clear();
            _compactKeys = null;
            foreach (var entry in _repository.GetCatalogEntries()) Put(entry);
            Changed?.Invoke(this, CatalogChangedEventArgs.Reset);
            return;
        }

        foreach (var entry in e.Upserted) Put(entry);
        foreach (var id in e.RemovedIds) Remove(id);

        var ids = e.Upserted.Select(x => x.Id).Concat(e.RemovedIds).ToList();
        if (ids.Count > 0) Changed?.Invoke(this, new CatalogChangedEventArgs(ids, isReset: false));
    }
}
