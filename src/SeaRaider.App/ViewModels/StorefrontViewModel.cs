using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SeaRaider.App.Services;
using SeaRaider.Core;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Catalog;
using SeaRaider.Core.Emulation;
using SeaRaider.Core.Models;
using SeaRaider.Core.Scraping;

namespace SeaRaider.App.ViewModels;

/// <summary>A sort order in the store's dropdown. <see cref="Sort"/> null = "Best match" (the site's own ranking).</summary>
public sealed record SortChoice(CatalogSort? Sort, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A choice in the store's "System" dropdown. <see cref="Id"/> null = every game, <see cref="StorefrontViewModel.PcGamesId"/> = no console.</summary>
public sealed record SystemChoice(string? Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A console's area of the store: a button with how many of its games the store has ("Nintendo 64 · 312").</summary>
public sealed partial class SystemChipViewModel(StorefrontViewModel owner, SystemChoice choice, int count) : ObservableObject
{
    public SystemChoice Choice { get; } = choice;
    public string Label => Choice.Id is null ? "All" : Choice.Label;
    public string CountText { get; } = count.ToString("N0");

    [ObservableProperty] private bool _isSelected;

    [RelayCommand]
    private void Choose() => owner.SelectedSystem = Choice;
}

/// <summary>
/// The store: live search across sources, merged with the locally cached catalog, sortable by upload or release
/// date, loaded a page at a time.
///
/// <para>Typing searches the cache instantly and, after a short pause, asks every source site directly
/// (<see cref="ILiveSearchService"/>); site results stream in per source. With an empty search box the store shows
/// the cache sorted by the chosen order and pulls the sites' newest uploads; scrolling to the end loads more —
/// first from the cache, then the next page from the sites. Cards only hold lightweight summaries; full details
/// load when a game is opened or installed.</para>
///
/// <para>Results are ranked by <see cref="SearchScorer"/> (exact titles first, typo-tolerant, noise dropped) and the
/// same game from several sites is merged into one card (<see cref="GameDeduplicator"/>); its store page offers each
/// source and mirror.</para>
/// </summary>
public sealed partial class StorefrontViewModel : PageViewModel
{
    private const int PageSize = 48;
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(400);

    public static IReadOnlyList<SortChoice> SortChoices { get; } =
    [
        new(null, "Best match"),
        new(CatalogSort.UploadNewest, "Upload date: newest"),
        new(CatalogSort.UploadOldest, "Upload date: oldest"),
        new(CatalogSort.ReleaseNewest, "Release date: newest"),
        new(CatalogSort.ReleaseOldest, "Release date: oldest"),
        new(CatalogSort.TitleAscending, "Title A to Z"),
        new(CatalogSort.SizeSmallest, "Size: smallest"),
        new(CatalogSort.SizeLargest, "Size: largest"),
    ];

    /// <summary><see cref="SystemChoice.Id"/> for "PC games": everything that isn't a console game.</summary>
    public const string PcGamesId = "pc";

    /// <summary>The store's "System" dropdown: all games, PC games, then one entry per console.</summary>
    public static IReadOnlyList<SystemChoice> SystemChoices { get; } =
        new[] { new SystemChoice(null, "All systems"), new SystemChoice(PcGamesId, "PC games") }
            .Concat(GameSystems.All.Select(s => new SystemChoice(s.Id, s.Name)))
            .ToList();

    private readonly CatalogIndexService _index;
    private readonly ILiveSearchService _live;
    private readonly ILibraryRepository _repository;
    private readonly IIngestionService _ingestion;
    private readonly GameStatusService _status;
    private readonly GameActionService _actions;
    private readonly NavigationService _navigation;
    private readonly ArtworkService _artwork;
    private readonly ReleaseDateService _releaseDates;
    private CancellationTokenSource? _releaseFill;

    /// <summary>Cards by duplicate key, and every copy's game id → its card (for status updates).</summary>
    private readonly Dictionary<string, GameCardViewModel> _cards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GameCardViewModel> _cardByGameId = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<Guid, string> _sourceNames = new Dictionary<Guid, string>();
    private readonly List<string> _liveOrder = [];
    private readonly HashSet<string> _liveIds = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, CrawlProgress> _activeCrawls = [];
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _refreshTimer;

    private List<GameGroup> _ordered = [];
    private string _query = string.Empty;
    private int _shown;
    private int _livePage;
    private bool _liveHasMore;
    private bool _initialized;
    private CancellationTokenSource? _liveCts;
    private readonly DispatcherTimer _undoTimer;
    private string _hiddenSignature = string.Empty;

    public StorefrontViewModel(CatalogIndexService index, ILiveSearchService live, ILibraryRepository repository,
        IIngestionService ingestion, GameStatusService status, GameActionService actions, NavigationService navigation,
        ArtworkService artwork, UpcomingViewModel upcoming, ReleaseDateService releaseDates)
    {
        Upcoming = upcoming;
        _releaseDates = releaseDates;
        _artwork = artwork;
        _index = index;
        _live = live;
        _repository = repository;
        _ingestion = ingestion;
        _status = status;
        _actions = actions;
        _navigation = navigation;

        _searchTimer = new DispatcherTimer { Interval = SearchDelay };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            StartQuery();
        };

        // Coalesce bursts of catalog changes (live results, background crawls) into one re-sort.
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            Rebuild(resetPaging: false);
        };

        // The "Hid … Undo" bar goes away by itself.
        _undoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _undoTimer.Tick += (_, _) =>
        {
            _undoTimer.Stop();
            HasUndo = false;
        };

        // Settings → "Hide posts that aren't games" / "Show hidden games again": re-filter at once.
        _hiddenSignature = HiddenSignature(repository.GetSettings());
        repository.SettingsChanged += (_, settings) => Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(HideAdultGames)); // the Settings page may have changed it
            var signature = HiddenSignature(settings);
            if (signature == _hiddenSignature) return;
            _hiddenSignature = signature;
            if (_initialized) Rebuild(resetPaging: false);
        });

        status.CatalogChanged += OnCatalogChanged;
        status.GameChanged += id =>
        {
            if (_cardByGameId.TryGetValue(id, out var card)) card.RefreshStatus();
            if (Featured is { } featured && featured.VariantIds.Contains(id)) featured.RefreshStatus();
        };
        status.CrawlStatusChanged += OnCrawlStatusChanged;
        navigation.SearchRequested += text => SearchText = text; // e.g. a game a friend shared that isn't in the catalog
    }

    /// <summary>The "Coming soon" shelf (upcoming sites the user added) and its wishlist buttons.</summary>
    public UpcomingViewModel Upcoming { get; }

    /// <summary>Cards currently shown (grows as you scroll).</summary>
    public BulkObservableCollection<GameCardViewModel> Games { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFeatured))]
    private GameCardViewModel? _featured;

    public bool HasFeatured => Featured is not null && _query.Length == 0;

    /// <summary>No search typed: the store shows its shelves (featured, coming soon) above the newest uploads.</summary>
    public bool IsBrowsing => _query.Length == 0;

    [ObservableProperty] private string _searchText = string.Empty;

    // ---- source filter ------------------------------------------------------------------------------------------

    /// <summary>One tick box per source that is switched on (Sources page): what the store shows and searches.</summary>
    public ObservableCollection<SourceFilterItem> SourceFilters { get; } = [];

    /// <summary>"All sources", "FitGirl", "2 of 5 sources".</summary>
    [ObservableProperty] private string _sourceFilterText = "All sources";

    public bool HasNoSourceFilters => SourceFilters.Count == 0;

    /// <summary>Ticked sources that are switched on: the only ones shown and searched.</summary>
    private HashSet<Guid> _activeSources = [];
    private string _sourcesSignature = string.Empty;

    /// <summary>Re-reads the sources and the saved filter; true when the set of sources shown changed.</summary>
    private bool RefreshSourceFilters()
    {
        var excluded = _repository.GetSettings().StoreExcludedSources.ToHashSet();
        var enabled = _repository.GetSources().Where(s => s.IsEnabled).OrderBy(SiteProfiles.NameFor, StringComparer.CurrentCultureIgnoreCase).ToList();
        SourceFilters.Clear();
        foreach (var source in enabled)
            SourceFilters.Add(new SourceFilterItem(source.Id, SiteProfiles.NameFor(source), !excluded.Contains(source.Id), OnSourceFilterToggled, OnlySource));
        OnPropertyChanged(nameof(HasNoSourceFilters));
        return UpdateActiveSources();
    }

    private bool UpdateActiveSources()
    {
        _activeSources = SourceFilters.Where(f => f.IsSelected).Select(f => f.Id).ToHashSet();
        var ticked = SourceFilters.Where(f => f.IsSelected).ToList();
        SourceFilterText = SourceFilters.Count == 0 ? "No sources"
            : ticked.Count == SourceFilters.Count ? "All sources"
            : ticked.Count == 0 ? "No sources ticked"
            : ticked.Count == 1 ? ticked[0].Name
            : $"{ticked.Count} of {SourceFilters.Count} sources";

        var signature = string.Join(",", _activeSources.OrderBy(g => g));
        var changed = signature != _sourcesSignature;
        _sourcesSignature = signature;
        return changed;
    }

    private void OnSourceFilterToggled(SourceFilterItem item)
    {
        SaveExclusions();
        if (UpdateActiveSources()) ApplySourceChange();
    }

    /// <summary>"Only": this source alone.</summary>
    private void OnlySource(SourceFilterItem only)
    {
        foreach (var item in SourceFilters) item.SetSelectedQuietly(ReferenceEquals(item, only));
        OnSourceFilterToggled(only);
    }

    [RelayCommand]
    private void SelectAllSources()
    {
        foreach (var item in SourceFilters) item.SetSelectedQuietly(true);
        SaveExclusions();
        if (UpdateActiveSources()) ApplySourceChange();
    }

    private void SaveExclusions()
    {
        var settings = _repository.GetSettings();
        // Sources switched off on the Sources page keep their tick state for when they're switched on again.
        var shownHere = SourceFilters.Select(f => f.Id).ToHashSet();
        settings.StoreExcludedSources = settings.StoreExcludedSources.Where(id => !shownHere.Contains(id))
            .Concat(SourceFilters.Where(f => !f.IsSelected).Select(f => f.Id))
            .Distinct()
            .ToList();
        _repository.SaveSettings(settings);
    }

    /// <summary>The sources shown changed: start the results over and ask the (remaining) sites again.</summary>
    private void ApplySourceChange()
    {
        if (!_initialized) return;
        _liveCts?.Cancel();
        _liveOrder.Clear();
        _liveIds.Clear();
        _livePage = 0;
        _liveHasMore = false;
        Rebuild(resetPaging: true);
        if (HasSources && (_query.Length >= 2 || _query.Length == 0)) _ = FetchLiveAsync(page: 1);
    }
    [ObservableProperty] private SortChoice _selectedSort = SortChoices[1];

    /// <summary>Which system's games the store shows (emulator games, one console at a time); "All systems" by default.</summary>
    [ObservableProperty] private SystemChoice _selectedSystem = SystemChoices[0];
    [ObservableProperty] private string _resultsText = string.Empty;

    /// <summary>A live search / browse request is running.</summary>
    [ObservableProperty] private bool _isSearching;

    /// <summary>"Searching 2 sites for 'hades'…", "34 results", or which sites failed.</summary>
    [ObservableProperty] private string? _searchStatus;

    [ObservableProperty] private bool _hasMore;

    /// <summary>A card was just hidden: "Hid … from the store. Undo".</summary>
    [ObservableProperty] private bool _hasUndo;
    [ObservableProperty] private string _undoText = string.Empty;
    private string? _lastHiddenKey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCatalogEmpty))]
    private bool _hasSources;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCatalogEmpty), nameof(IsNoResults))]
    private int _resultCount;

    public bool IsCatalogEmpty => !HasSources;
    public bool IsNoResults => HasSources && ResultCount == 0 && !IsSearching;

    [ObservableProperty] private bool _isCrawling;
    [ObservableProperty] private string? _crawlText;

    partial void OnIsSearchingChanged(bool value) => OnPropertyChanged(nameof(IsNoResults));

    partial void OnSearchTextChanged(string value)
    {
        _searchTimer.Stop();
        // Local results update on the next tick; the sites are asked once typing pauses.
        _searchTimer.Start();
    }

    partial void OnSelectedSortChanged(SortChoice value)
    {
        Rebuild(resetPaging: true);
        StartReleaseDateLookup();
    }

    private bool IsReleaseSort => SelectedSort.Sort is CatalogSort.ReleaseNewest or CatalogSort.ReleaseOldest;

    /// <summary>"Finding release dates…" while games without one are looked up on Steam (release-date sorting only).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReleaseLookupText))]
    private string? _releaseLookupText;

    public bool HasReleaseLookupText => !string.IsNullOrEmpty(ReleaseLookupText);

    /// <summary>
    /// Sorted by release date: most download sites don't say when a game came out, so the games without a known release date
    /// (newest uploads first) are looked up on Steam in the background. Each date found is saved, and the store re-sorts as
    /// they come in. Changing the sort stops it.
    /// </summary>
    private void StartReleaseDateLookup()
    {
        _releaseFill?.Cancel();
        _releaseFill = null;
        ReleaseLookupText = null;
        if (!IsReleaseSort) return;

        var missing = _ordered.Where(g => g.KnownReleaseUtc is null)
            .OrderByDescending(g => g.NewestUploadUtc)
            .Take(300)
            .ToList();
        if (missing.Count == 0) return;

        var cts = new CancellationTokenSource();
        _releaseFill = cts;
        ReleaseLookupText = "Finding release dates on Steam for games whose page doesn't give one…";
        _ = Task.Run(async () =>
        {
            try
            {
                await _releaseDates.FillAsync(missing, max: 150, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // sort changed
            }
            catch (Exception ex)
            {
                ErrorLog.Write(ex, "release date lookup");
            }
            finally
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_releaseFill != cts) return;
                    _releaseFill = null;
                    ReleaseLookupText = null;
                });
            }
        });
    }

    partial void OnSelectedSystemChanged(SystemChoice value) => Rebuild(resetPaging: true);

    /// <summary>
    /// One button per console the store has games for (with how many), plus All and PC games: each console's games in their own
    /// area, the same as choosing it in the System dropdown. Only shown once there are console games.
    /// </summary>
    public ObservableCollection<SystemChipViewModel> SystemChips { get; } = [];

    public bool HasSystemChips => SystemChips.Count > 0;

    private static string? SystemOf(GameGroup group) => group.Variants.Select(v => v.SystemId).FirstOrDefault(id => id is not null);

    private void UpdateSystemChips(IReadOnlyCollection<GameGroup> groups)
    {
        var counts = groups.GroupBy(g => SystemOf(g) ?? PcGamesId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        SystemChips.Clear();
        if (counts.Keys.Any(k => k != PcGamesId))
        {
            foreach (var choice in SystemChoices)
            {
                var count = choice.Id is null ? groups.Count : counts.GetValueOrDefault(choice.Id);
                if (count == 0 && !string.Equals(choice.Id, SelectedSystem?.Id, StringComparison.OrdinalIgnoreCase)) continue;
                SystemChips.Add(new SystemChipViewModel(this, choice, count)
                {
                    IsSelected = string.Equals(choice.Id, SelectedSystem?.Id, StringComparison.OrdinalIgnoreCase),
                });
            }
        }
        OnPropertyChanged(nameof(HasSystemChips));
    }

    /// <summary>The "System" dropdown: a card is shown when any of its sites' entries is for the chosen console (or none, for PC games).</summary>
    private bool MatchesSystem(GameGroup group)
    {
        var wanted = SelectedSystem?.Id;
        if (wanted is null) return true;
        var system = group.Variants.Select(v => v.SystemId).FirstOrDefault(id => id is not null);
        return wanted == PcGamesId ? system is null : string.Equals(system, wanted, StringComparison.OrdinalIgnoreCase);
    }

    // =============================================================================================
    // Navigation
    // =============================================================================================

    /// <summary>
    /// Where the card grid was scrolled to when you left the store (to open a game). The store view is rebuilt when you
    /// come back, so it scrolls here again instead of starting at the top.
    /// </summary>
    public double ScrollOffset { get; set; }

    public override async void OnNavigatedTo()
    {
        HasSources = _repository.GetSources().Count > 0;
        foreach (var card in _cards.Values) card.RefreshStatus();
        // Sources switched on or off on the Sources page since the last visit: show and search the new set.
        var sourcesChanged = RefreshSourceFilters();
        if (_initialized)
        {
            if (sourcesChanged) ApplySourceChange();
            return;
        }
        _initialized = true;

        try
        {
            await _index.EnsureLoadedAsync();
            Rebuild(resetPaging: true);
            if (HasSources) await FetchLiveAsync(page: 1); // newest uploads straight from the sites
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    // =============================================================================================
    // Query
    // =============================================================================================

    private void StartQuery()
    {
        var query = SearchText.Trim();
        if (query == _query) return;

        _query = query;
        _liveCts?.Cancel();
        _liveOrder.Clear();
        _liveIds.Clear();
        _livePage = 0;
        _liveHasMore = false;
        OnPropertyChanged(nameof(HasFeatured));
        OnPropertyChanged(nameof(IsBrowsing));

        Rebuild(resetPaging: true);
        if (HasSources && (query.Length >= 2 || query.Length == 0)) _ = FetchLiveAsync(page: 1);
    }

    /// <summary>Asks the sites for one page of results (search, or newest uploads when the box is empty).</summary>
    private async Task FetchLiveAsync(int page)
    {
        _liveCts?.Cancel();
        var cts = _liveCts = new CancellationTokenSource();
        var query = _query;
        var active = _activeSources.ToList();
        var sourceCount = _repository.GetSources().Count(s => s.IsEnabled && s.SearchMode != SearchMode.Disabled && _activeSources.Contains(s.Id));
        if (sourceCount == 0)
        {
            SearchStatus = SourceFilters.Count > 0 && _activeSources.Count == 0 ? "No sources ticked: pick some in the sources list." : null;
            return;
        }

        IsSearching = true;
        SearchStatus = query.Length == 0
            ? "Checking your sources for new uploads…"
            : $"Searching {sourceCount} site{(sourceCount == 1 ? string.Empty : "s")} for “{query}”…";

        var found = 0;
        var anyMore = false;
        var failures = new List<string>();
        try
        {
            await foreach (var batch in _live.SearchAsync(query, page, active, cts.Token))
            {
                if (cts.IsCancellationRequested) return;
                if (batch.Error is not null) failures.Add($"{batch.SourceName}: {batch.Error}");
                anyMore |= batch.HasMore;

                foreach (var entry in batch.Results)
                {
                    found++;
                    if (_liveIds.Add(entry.Id)) _liveOrder.Add(entry.Id);
                }
                Rebuild(resetPaging: false);
            }

            _livePage = page;
            _liveHasMore = anyMore;
            SearchStatus = failures.Count > 0
                ? string.Join("  ·  ", failures)
                : query.Length == 0
                    ? null
                    : found == 0 && page == 1 ? $"No site results for “{query}”." : null;
        }
        catch (OperationCanceledException)
        {
            // Replaced by a newer query.
        }
        finally
        {
            if (ReferenceEquals(_liveCts, cts))
            {
                IsSearching = false;
                UpdatePagingState();
            }
        }
    }

    /// <summary>Recomputes the ordered result list from the cache + live results, keeping the page count.</summary>
    private void Rebuild(bool resetPaging)
    {
        _sourceNames = _repository.GetSources().ToDictionary(s => s.Id, SiteProfiles.NameFor);
        var bestMatch = SelectedSort.Sort is null;
        var sort = SelectedSort.Sort ?? (_query.Length > 0 ? CatalogSort.Relevance : CatalogSort.UploadNewest);
        var active = _activeSources;
        var local = _index.Query(new CatalogQuery(_query, sort, SourceIds: active));

        var union = new Dictionary<string, CatalogEntry>(StringComparer.Ordinal);
        foreach (var e in local) union[e.Id] = e;
        foreach (var e in _liveOrder.Select(_index.Get).OfType<CatalogEntry>().Where(e => active.Contains(e.SourceId)))
            union[e.Id] = e; // already relevance-filtered

        List<CatalogEntry> ordered;
        if (_query.Length > 0 && bestMatch)
        {
            // Best match: relevance first (exact titles on top); the sites' own order breaks ties.
            var search = SearchQuery.Parse(_query);
            var liveRank = _liveOrder.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i, StringComparer.Ordinal);
            ordered = union.Values
                .OrderByDescending(e => Math.Round(_index.Score(search, e), 3))
                .ThenBy(e => liveRank.TryGetValue(e.Id, out var r) ? r : int.MaxValue)
                .ThenByDescending(e => e.UploadedAtUtc)
                .ToList();
        }
        else
        {
            ordered = union.Values.Sort(sort == CatalogSort.Relevance ? CatalogSort.UploadNewest : sort).ToList();
        }

        // One card per game, however many sites have it; minus what's hidden (× on a card) and site posts that aren't games.
        var settings = _repository.GetSettings();
        var hidden = settings.HiddenStoreItems.Select(h => h.Key).ToHashSet(StringComparer.Ordinal);
        var shownGroups = GameDeduplicator.Group(ordered).Where(g => IsShown(g.Key, g.Primary, hidden, settings.HideNonGames) &&
                                                                   !(settings.HideAdultGames && g.Variants.Any(v => AdultContentFilter.IsAdult(v))))
            .ToList();
        UpdateSystemChips(shownGroups); // each console's area, counted before one is chosen
        _ordered = shownGroups.Where(MatchesSystem).ToList();
        _cardByGameId.Clear();
        if (resetPaging || _shown == 0) _shown = PageSize;
        Games.ResetWith(_ordered.Take(_shown).Select(CardFor));
        ResultCount = _ordered.Count;

        if (_query.Length == 0)
        {
            // The banner is the newest game overall; with a system chosen it would be a game from another one.
            var featured = SelectedSystem.Id is not null ? null : _index.Query(new CatalogQuery(null, CatalogSort.UploadNewest, SourceIds: active))
                .FirstOrDefault(e => e.CoverImageUrl is not null && IsShown(_index.KeyOf(e), e, hidden, settings.HideNonGames) &&
                                     !(settings.HideAdultGames && AdultContentFilter.IsAdult(e)));
            Featured = featured is null ? null : CardFor(GameDeduplicator.Build(_index.KeyOf(featured), _index.GetVariants(featured.Id)));
            Featured?.LoadHero(); // full-width banner: high-resolution art instead of the small cover
        }
        OnPropertyChanged(nameof(HasFeatured));
        UpdatePagingState();
    }

    private void UpdatePagingState()
    {
        HasMore = _shown < _ordered.Count || _liveHasMore;
        var shown = Math.Min(_shown, _ordered.Count);
        ResultsText = _query.Length == 0
            ? $"{_index.Count:N0} games cached"
            : $"{_ordered.Count:N0} result{(_ordered.Count == 1 ? string.Empty : "s")}";
        if (shown < _ordered.Count) ResultsText += $", showing {shown:N0}";
    }

    private GameCardViewModel CardFor(GameGroup group)
    {
        if (_cards.TryGetValue(group.Key, out var card)) card.Update(group, _sourceNames);
        else _cards[group.Key] = card = new GameCardViewModel(group, _sourceNames, _status, _actions, _navigation, HideCard, _artwork);
        card.ShowReleaseDate = IsReleaseSort;

        foreach (var variant in group.Variants) _cardByGameId[variant.Id] = card;
        return card;
    }

    // =============================================================================================
    // Hiding
    // =============================================================================================

    /// <summary>The store's "Hide adult games" switch (also in Settings → Store). Saved at once; the grid re-filters.</summary>
    public bool HideAdultGames
    {
        get => _repository.GetSettings().HideAdultGames;
        set
        {
            var settings = _repository.GetSettings();
            if (settings.HideAdultGames == value) return;
            settings.HideAdultGames = value;
            _repository.SaveSettings(settings); // SettingsChanged: the signature differs, so the grid is rebuilt
            OnPropertyChanged();
        }
    }

    private static bool IsShown(string key, CatalogEntry entry, HashSet<string> hidden, bool hideNonGames) =>
        !hidden.Contains(key) && !(hideNonGames && NonGameFilter.IsNotAGame(entry));

    private static string HiddenSignature(AppSettings settings) =>
        $"{settings.HideNonGames}|{settings.HideAdultGames}|{string.Join(",", settings.HiddenStoreItems.Select(h => h.Key))}";

    /// <summary>The card's ×: hidden from the store until restored (Undo, or Settings → General → Store).</summary>
    private void HideCard(GameCardViewModel card)
    {
        var settings = _repository.GetSettings();
        if (settings.HiddenStoreItems.All(h => h.Key != card.Key))
            settings.HiddenStoreItems.Add(new HiddenStoreItem { Key = card.Key, Title = card.Title });
        _hiddenSignature = HiddenSignature(settings); // this view updates itself below
        _repository.SaveSettings(settings);

        Games.Remove(card);
        _ordered.RemoveAll(g => g.Key == card.Key);
        ResultCount = _ordered.Count;
        if (Featured?.Key == card.Key) Rebuild(resetPaging: false);
        UpdatePagingState();

        _lastHiddenKey = card.Key;
        UndoText = $"Hid \u201C{card.Title}\u201D from the store.";
        HasUndo = true;
        _undoTimer.Stop();
        _undoTimer.Start();
    }

    [RelayCommand]
    private void UndoHide()
    {
        _undoTimer.Stop();
        HasUndo = false;
        if (_lastHiddenKey is not { } key) return;

        var settings = _repository.GetSettings();
        settings.HiddenStoreItems.RemoveAll(h => h.Key == key);
        _hiddenSignature = HiddenSignature(settings);
        _repository.SaveSettings(settings);
        _lastHiddenKey = null;
        Rebuild(resetPaging: false);
    }

    // =============================================================================================
    // Paging
    // =============================================================================================

    /// <summary>Called when the grid is scrolled near its end (and by the "Load more" button).</summary>
    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (_shown < _ordered.Count)
        {
            var next = _ordered.Skip(_shown).Take(PageSize).Select(CardFor).ToList();
            _shown += next.Count;
            foreach (var card in next) Games.Add(card);
            UpdatePagingState();
            return;
        }

        if (_liveHasMore && !IsSearching) await FetchLiveAsync(_livePage + 1);
    }

    // =============================================================================================
    // Catalog / crawl events
    // =============================================================================================

    private void OnCatalogChanged(CatalogChangedEventArgs e)
    {
        if (e.IsReset)
        {
            _refreshTimer.Stop();
            _refreshTimer.Start();
            return;
        }

        var needsRebuild = false;
        foreach (var id in e.GameIds)
        {
            var entry = _index.Get(id);
            if (entry is null) { needsRebuild = true; continue; }
            if (_cardByGameId.TryGetValue(id, out var card))
            {
                // e.g. details finished loading: refresh this card's copies in place.
                var variants = card.Group.Variants.Select(v => v.Id == id ? entry : v).ToList();
                card.Update(GameDeduplicator.Build(card.Key, variants), _sourceNames);
            }
            else
            {
                needsRebuild = true; // new game (or a new copy of one): may belong in the list
            }
        }

        if (needsRebuild && !IsSearching)
        {
            _refreshTimer.Stop();
            _refreshTimer.Start();
        }
    }

    private void OnCrawlStatusChanged(CrawlStatusEventArgs e)
    {
        if (e.IsRunning) _activeCrawls[e.SourceId] = e.Progress;
        else _activeCrawls.Remove(e.SourceId);

        IsCrawling = _activeCrawls.Count > 0;
        CrawlText = IsCrawling
            ? $"Building offline index: {_activeCrawls.Values.Sum(p => p.GamesFound):N0} games found"
            : null;
    }

    [RelayCommand]
    private Task RefreshAsync()
    {
        HasSources = _repository.GetSources().Count > 0;
        _liveOrder.Clear();
        _liveIds.Clear();
        _live.InvalidateCache();
        return FetchLiveAsync(page: 1);
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    [RelayCommand]
    private void AddSource() => _navigation.ShowSettings(SettingsTab.Sources);
}

/// <summary>One source in the store's source filter.</summary>
public sealed partial class SourceFilterItem : ObservableObject
{
    private readonly Action<SourceFilterItem> _toggled;
    private readonly Action<SourceFilterItem> _only;
    private bool _quiet;

    public SourceFilterItem(Guid id, string name, bool isSelected, Action<SourceFilterItem> toggled, Action<SourceFilterItem> only)
    {
        Id = id;
        Name = name;
        _isSelected = isSelected;
        _toggled = toggled;
        _only = only;
    }

    public Guid Id { get; }
    public string Name { get; }

    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        if (!_quiet) _toggled(this);
    }

    /// <summary>Changes the tick without reporting it (the caller applies several at once).</summary>
    public void SetSelectedQuietly(bool value)
    {
        _quiet = true;
        IsSelected = value;
        _quiet = false;
    }

    [RelayCommand]
    private void Only() => _only(this);
}
