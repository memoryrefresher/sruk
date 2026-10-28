using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SeaRaider.App.Services;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Catalog;
using SeaRaider.Core.Models;

namespace SeaRaider.App.ViewModels;

/// <summary>Settings › Sources: paste a site, listing page or feed; SeaRaider crawls it in the background.</summary>
public sealed partial class SourcesViewModel : PageViewModel
{
    private const int MaxLogLines = 200;

    public static IReadOnlyList<string> SearchModes { get; } =
    [
        "Automatic (WordPress API, else the site's search page)",
        "WordPress API",
        "The site's search page (/?s=…)",
        "Custom search address",
        "Don't search this site live",
    ];

    public static IReadOnlyList<string> CrawlModes { get; } =
    [
        "Automatic (sitemap, then page by page)",
        "XML sitemap only",
        "Page by page only (/page/2/, /page/3/ …)",
        "This page only (one game)",
    ];

    private readonly ILibraryRepository _repository;
    private readonly IIngestionService _ingestion;
    private readonly NavigationService _navigation;
    private readonly ILiveSearchService _live;
    private readonly SeaRaider.Core.Catalog.TorrentSourceService _torrentSources;
    private readonly SeaRaider.Core.Catalog.CatalogIndexService _index;
    private readonly LinkSourceService _linkSources;

    /// <summary>Where a game added by link is filed: worked out from its name, PC games, or a console.</summary>
    public static IReadOnlyList<SystemChoice> LinkSystemChoices { get; } =
        new[] { new SystemChoice(null, "Work it out (PC game if unsure)"), new SystemChoice(LinkSourceService.PcId, "PC game") }
            .Concat(SeaRaider.Core.Emulation.GameSystems.All.Select(s => new SystemChoice(s.Id, s.Name)))
            .ToList();

    public SourcesViewModel(ILibraryRepository repository, IIngestionService ingestion, NavigationService navigation,
        GameStatusService status, ILiveSearchService live, UpcomingViewModel upcoming, SourcesFileService sourcesFile,
        ShellService shell, SiteReadersViewModel siteReaders, SeaRaider.Core.Catalog.TorrentSourceService torrentSources,
        SeaRaider.Core.Catalog.CatalogIndexService index, LinkSourceService linkSources)
    {
        _index = index;
        _linkSources = linkSources;
        _torrentSources = torrentSources;
        SiteReaders = siteReaders;
        siteReaders.SourceAdded += (_, name) =>
        {
            Reload();
            AppendLog($"Added {name} from Site readers, with its new reader.");
        };
        Upcoming = upcoming;
        _sourcesFile = sourcesFile;
        _shell = shell;
        _repository = repository;
        _ingestion = ingestion;
        _navigation = navigation;
        _live = live;

        status.CrawlStatusChanged += OnCrawlStatusChanged;
        Reload();
    }

    public ObservableCollection<SourceItemViewModel> Sources { get; } = [];

    /// <summary>Upcoming-release sites for the store's "Coming soon" shelf.</summary>
    public UpcomingViewModel Upcoming { get; }
    public ObservableCollection<string> Log { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddSourceCommand))]
    [NotifyPropertyChangedFor(nameof(ShowLinkOptions))]
    private string _newUrl = string.Empty;

    /// <summary>The address is one game's download link (any host), not a site: ticked by hand, or seen from the address.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLinkOptions))]
    private bool _isGameLink;

    /// <summary>The name to show for a game added by link (blank: worked out from the link).</summary>
    [ObservableProperty] private string? _newName;

    /// <summary>Where a game added by link is filed in the store.</summary>
    [ObservableProperty] private SystemChoice _newLinkSystem = LinkSystemChoices[0];

    /// <summary>The name and console boxes show when the address is a game's link.</summary>
    public bool ShowLinkOptions => IsGameLink || LinkSourceService.IsGameLink(NewUrl);

    [ObservableProperty] private bool _showAdvanced;

    /// <summary>The address is a game played in the browser (astrastrike.fun …), not a site of downloads.</summary>
    [ObservableProperty] private bool _isWebGame;

    // Crawl settings for the source being added.
    [ObservableProperty] private string _selectedCrawlMode = CrawlModes[0];

    // Live search settings for the source being added.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomSearch))]
    private string _selectedSearchMode = SearchModes[0];

    [ObservableProperty] private string? _searchUrlTemplate;

    public bool IsCustomSearch => SelectedSearchMode == SearchModes[3];
    [ObservableProperty] private string? _urlIncludePattern;
    [ObservableProperty] private string? _urlExcludePattern;
    [ObservableProperty] private decimal _maxPages = 1000;
    [ObservableProperty] private decimal _requestDelayMs = 750;
    [ObservableProperty] private bool _respectRobotsTxt = true;

    // Optional per-site selector overrides (see ScraperProfile).
    [ObservableProperty] private string? _itemLinkSelector;
    [ObservableProperty] private string? _nextPageSelector;
    [ObservableProperty] private string? _titleSelector;
    [ObservableProperty] private string? _descriptionSelector;
    [ObservableProperty] private string? _coverSelector;
    [ObservableProperty] private string? _downloadLinkSelector;

    /// <summary>The names the site prints next to its "Click Here" buttons ("Torrent, Gofile, Pixeldrain").</summary>
    [ObservableProperty] private string? _mirrorLabels;

    public bool IsEmpty => Sources.Count == 0 && EmulationSources.Count == 0;

    /// <summary>Console game (ROM) sources: their own section, under the game sources.</summary>
    public ObservableCollection<SourceItemViewModel> EmulationSources { get; } = [];

    public bool HasEmulationSources => EmulationSources.Count > 0;

    /// <summary>A source whose games are console games: a console's torrent, a ROM site, or one that's mostly ROMs.</summary>
    private bool IsEmulation(GameSource source)
    {
        if (source.IsLink) return LinkSourceService.SystemFor(source) is not null;
        if (source.IsTorrent && (source.TorrentSystemId is not null || TorrentSourceService.ConsoleIn(source.DisplayName) is not null)) return true;
        if (SeaRaider.Core.Emulation.GameSystems.FromText($"{source.DisplayName} {source.Url}") is not null) return true;
        if (Regex.IsMatch($"{source.DisplayName} {source.Url}", @"(?<![a-z])(roms?|emulat\w*|homebrew|retro)(?![a-z])", RegexOptions.IgnoreCase)) return true;
        var sample = _index.Query(new CatalogQuery(null, CatalogSort.UploadNewest, SourceId: source.Id)).Take(200).ToList();
        return sample.Count > 0 && sample.Count(e => e.SystemId is not null) * 2 > sample.Count;
    }

    public override void OnNavigatedTo()
    {
        Reload();
        SiteReaders.Load();
    }

    /// <summary>Site readers: dedicated readers for sites, as JSON files of CSS selectors.</summary>
    public SiteReadersViewModel SiteReaders { get; }

    private void Reload()
    {
        Sources.Clear();
        EmulationSources.Clear();
        foreach (var source in _repository.GetSources())
        {
            var row = new SourceItemViewModel(source, this);
            row.ApplyProgress(_ingestion.GetLastProgress(source.Id), _ingestion.IsCrawling(source.Id));
            (IsEmulation(source) ? EmulationSources : Sources).Add(row);
        }
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasEmulationSources));
    }

    private void OnCrawlStatusChanged(CrawlStatusEventArgs e)
    {
        var row = Sources.Concat(EmulationSources).FirstOrDefault(s => s.Id == e.SourceId);
        if (row is null)
        {
            Reload();
            row = Sources.Concat(EmulationSources).FirstOrDefault(s => s.Id == e.SourceId);
        }
        row?.ApplyProgress(e.Progress, e.IsRunning);

        if (!e.IsRunning && row is not null)
        {
            AppendLog($"{row.Name}: {e.Progress.Message}");
            // Pick up LastResult / counts written at the end of the crawl.
            if (_repository.GetSource(e.SourceId) is { } updated) row.Reload(updated);
        }
    }

    private bool CanAddSource() => !string.IsNullOrWhiteSpace(NewUrl);

    /// <summary>A backups torrent from a file on this PC: every game folder in it goes in the store.</summary>
    [RelayCommand]
    private Task AddTorrentFileAsync() => RunSafeAsync(async () =>
    {
        if (await _shell.PickTorrentAsync() is not { } path) return;
        await AddTorrentAsync(path);
    });

    private async Task AddTorrentAsync(string origin)
    {
        AppendLog(origin.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
            ? "Reading the torrent's file list from its peers (needs someone sharing it)…"
            : "Reading the torrent's file list…");
        try
        {
            var (source, games) = await Task.Run(() => _torrentSources.AddAsync(origin));
            NewUrl = string.Empty;
            AppendLog($"Added {source.DisplayName}: {games} games in the store. Each downloads only its own folder from the torrent.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppendLog($"Couldn't add the torrent: {ex.Message}");
            throw;
        }
        finally
        {
            Reload();
        }
    }

    /// <summary>One game's download link (any host) as a source: a store game with the name and console chosen here.</summary>
    private void AddLink()
    {
        try
        {
            var (source, _) = _linkSources.Add(NewUrl, NewName, NewLinkSystem?.Id);
            var filed = LinkSourceService.SystemFor(source)?.Name ?? "PC games";
            NewUrl = string.Empty;
            NewName = null;
            IsGameLink = false;
            NewLinkSystem = LinkSystemChoices[0];
            AppendLog($"Added {source.DisplayName} to the store under {filed}. Looking up its picture and details…");
            _ = Task.Run(async () =>
            {
                await _linkSources.EnrichAsync(source);
                Avalonia.Threading.Dispatcher.UIThread.Post(() => AppendLog($"{source.DisplayName}: picture and details checked. If nothing was found it stays in the store with its name."));
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppendLog($"Couldn't add the link: {ex.Message}");
            throw;
        }
        finally
        {
            Reload();
        }
    }

    /// <summary>Renames a link game (blank: named from the link again); it is looked up again under the new name.</summary>
    internal void RenameLink(SourceItemViewModel row, string? name) => RunSafe(() =>
    {
        if (_repository.GetSource(row.Id) is not { IsLink: true } source) return;
        _linkSources.Update(source, name, source.LinkSystemId);
        AppendLog($"{source.DisplayName}: renamed. Looking up its picture and details again…");
        _ = Task.Run(() => _linkSources.EnrichAsync(source));
        Avalonia.Threading.Dispatcher.UIThread.Post(Reload);
    });

    /// <summary>Files a link game under another console (or PC games); it is looked up again as that.</summary>
    internal void SetLinkSystem(SourceItemViewModel row, string? systemId) => RunSafe(() =>
    {
        if (_repository.GetSource(row.Id) is not { IsLink: true } source) return;
        _linkSources.Update(source, source.LinkNameChosen ? source.DisplayName : null, systemId);
        AppendLog($"{source.DisplayName}: now filed under {LinkSourceService.SystemFor(source)?.Name ?? "PC games"}. Looking up its picture and details again…");
        _ = Task.Run(() => _linkSources.EnrichAsync(source));
        Avalonia.Threading.Dispatcher.UIThread.Post(Reload); // after the list's own event is done with this row
    });

    [RelayCommand(CanExecute = nameof(CanAddSource))]
    private Task AddSourceAsync() => RunSafeAsync(async () =>
    {
        if (SeaRaider.Core.Catalog.TorrentSourceService.IsTorrentAddress(NewUrl))
        {
            await AddTorrentAsync(NewUrl);
            return;
        }
        if (IsGameLink || LinkSourceService.IsGameLink(NewUrl))
        {
            AddLink();
            return;
        }
        var template = new GameSource
        {
            Mode = (CrawlMode)Math.Max(0, CrawlModes.ToList().IndexOf(SelectedCrawlMode)),
            SearchMode = (SearchMode)Math.Max(0, SearchModes.ToList().IndexOf(SelectedSearchMode)),
            SearchUrlTemplate = Blank(SearchUrlTemplate),
            IsWebGame = IsWebGame,
            UrlIncludePattern = Blank(UrlIncludePattern),
            UrlExcludePattern = Blank(UrlExcludePattern),
            MaxPages = (int)Math.Clamp(MaxPages, 1, 100_000),
            RequestDelayMs = (int)Math.Clamp(RequestDelayMs, 0, 60_000),
            RespectRobotsTxt = RespectRobotsTxt,
            Profile = new ScraperProfile
            {
                ItemLinkSelector = Blank(ItemLinkSelector),
                NextPageSelector = Blank(NextPageSelector),
                TitleSelector = Blank(TitleSelector),
                DescriptionSelector = Blank(DescriptionSelector),
                CoverImageSelector = Blank(CoverSelector),
                DownloadLinkSelector = Blank(DownloadLinkSelector),
                MirrorLabels = Blank(MirrorLabels),
            },
        };

        try
        {
            if (template.SearchMode == SearchMode.Custom &&
                (template.SearchUrlTemplate is null || !template.SearchUrlTemplate.Contains("{query}", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("A custom search address needs {query} where the search words go, e.g. https://example.com/?s={query}.");

            var source = await _ingestion.AddSourceAsync(NewUrl, template);
            NewUrl = string.Empty;
            IsWebGame = false;
            SearchUrlTemplate = null;
            AppendLog($"Added {source.DisplayName}. Checking its newest uploads…");

            // No upfront crawl: one live request shows whether search works and fills the store's first page.
            var preview = await _live.SearchSourceAsync(source.Id, null, 1, CancellationToken.None);
            AppendLog(preview.Error is null
                ? source.IsWebGame
                    ? $"{source.DisplayName}: added to the store as a browser game. Open it there and press Download & Install to put it in your library."
                    : $"{source.DisplayName}: {preview.Results.Count} newest games added to the store. Search it from the Store page."
                : $"{source.DisplayName}: couldn't read its newest uploads ({preview.Error}).");
            UrlIncludePattern = UrlExcludePattern = null;
            ItemLinkSelector = NextPageSelector = TitleSelector = DescriptionSelector = CoverSelector = DownloadLinkSelector = MirrorLabels = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppendLog($"Couldn't add source: {ex.Message}");
            throw;
        }
        finally
        {
            Reload();
        }
    });

    [RelayCommand]
    private void RefreshAll() => RunSafe(() => _ = _ingestion.StartCrawlAll());

    [RelayCommand]
    private void BrowseStore() => _navigation.ShowPage(AppPage.Store);

    internal void StartCrawl(SourceItemViewModel row, bool fullRescan) => RunSafe(() =>
    {
        if (row.IsWebGame || row.IsLink) return; // one game, nothing to index
        if (_repository.GetSource(row.Id) is { IsTorrent: true } torrent)
        {
            AppendLog($"{row.Name}: reading the torrent's list…");
            _ = Task.Run(() => _torrentSources.Refresh(torrent)).ContinueWith(t => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                AppendLog(t.IsFaulted ? $"{row.Name}: {t.Exception!.InnerException?.Message}" : $"{row.Name}: {t.Result} games in the torrent");
                Reload();
            }), TaskScheduler.Default);
            return;
        }
        _ = _ingestion.StartCrawl(row.Id, fullRescan);
        row.ApplyProgress(CrawlProgress.Starting, isRunning: true);
        AppendLog(fullRescan ? $"{row.Name}: full rescan started" : $"{row.Name}: checking for new games");
    });

    /// <summary>A backups torrent's console, chosen in its row: saved, and its games filed under it again.</summary>
    internal void SetTorrentConsole(SourceItemViewModel row, string? systemId)
    {
        if (_repository.GetSource(row.Id) is not { IsTorrent: true } source) return;
        source.TorrentSystemId = systemId;
        _repository.UpsertSource(source);
        AppendLog($"{source.DisplayName}: {(systemId is null ? "console worked out from the names" : $"games filed under {SeaRaider.Core.Emulation.GameSystems.Find(systemId)?.Name}")}…");
        _ = Task.Run(() => _torrentSources.Refresh(source)).ContinueWith(t => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            AppendLog(t.IsFaulted ? $"{source.DisplayName}: {t.Exception!.InnerException?.Message}" : $"{source.DisplayName}: {t.Result} games");
            Reload();
        }), TaskScheduler.Default);
    }

    /// <summary>Saves a source's mirror labels; games are read again with them the next time they're opened.</summary>
    internal void SaveMirrorLabels(SourceItemViewModel row, string? labels) => RunSafe(() =>
    {
        if (_repository.GetSource(row.Id) is not { } source) return;
        source.Profile ??= new ScraperProfile();
        var parsed = SeaRaider.Core.Scraping.MirrorLabels.Parse(labels);
        source.Profile.MirrorLabels = parsed.Count == 0 ? null : string.Join(", ", parsed);
        _repository.UpsertSource(source);
        row.Reload(source);
        AppendLog(parsed.Count == 0
            ? $"{row.Name}: mirror labels cleared (the text next to each button is used as it is)."
            : $"{row.Name}: mirror labels saved ({string.Join(", ", parsed)}). Open a game, or press Reload on its page, to see them.");
    });

    /// <summary>Switches a source on or off: off means it isn't searched, shown in the store, or included in "update all".</summary>
    internal void SetSourceEnabled(SourceItemViewModel row, bool enabled) => RunSafe(() =>
    {
        if (_repository.GetSource(row.Id) is not { } source || source.IsEnabled == enabled) return;
        source.IsEnabled = enabled;
        _repository.UpsertSource(source);
        if (!enabled) _ingestion.CancelCrawl(row.Id);
        row.Reload(source);
        AppendLog(enabled ? $"{row.Name} is on: searched and shown in the store again." : $"{row.Name} is off: not searched or shown in the store.");
    });

    /// <summary>Marks a source as a browser game (or not) and reads it again, so its store entry changes to match.</summary>
    internal void SetWebGame(SourceItemViewModel row, bool webGame) => RunSafe(() =>
    {
        if (_repository.GetSource(row.Id) is not { } source || source.IsWebGame == webGame) return;
        source.IsWebGame = webGame;
        _repository.UpsertSource(source);
        row.Reload(source);
        _live.InvalidateCache();
        _ = _live.SearchSourceAsync(source.Id, null, 1, CancellationToken.None);
        AppendLog(webGame
            ? $"{row.Name} is a browser game now: open it in the store and press Download & Install to put it in your library."
            : $"{row.Name} is read as a normal site again.");
    });

    internal void StopCrawl(SourceItemViewModel row) => RunSafe(() => _ingestion.CancelCrawl(row.Id));

    internal Task RemoveSourceAsync(SourceItemViewModel row) => RunSafeAsync(async () =>
    {
        await _ingestion.RemoveSourceAsync(row.Id, removeGames: true);
        AppendLog($"Removed {row.Name} and its games");
        Reload();
    });

    // ---- sources file, import / export --------------------------------------------------------------

    private readonly SourcesFileService _sourcesFile;
    private readonly ShellService _shell;

    /// <summary>"Kept in sync with D:\Games\SeaRaider\sources.txt".</summary>
    public string SourcesFileText => $"Your sources are kept in {_sourcesFile.FilePath}, one address per line. Edit it, share it, or drop one next to SeaRaider before its first start.";

    [RelayCommand]
    private Task ImportSourcesAsync() => RunSafeAsync(async () =>
    {
        if (await _shell.PickTextFileAsync("Import sources") is not { } path) return;
        var (added, skipped) = await _sourcesFile.ImportAsync(path);
        AppendLog(added == 0
            ? $"Nothing new in {Path.GetFileName(path)} ({skipped} already in your sources or not usable)."
            : $"Imported {added} source(s) from {Path.GetFileName(path)}" + (skipped > 0 ? $", {skipped} skipped (already there or not usable)." : "."));
        Reload();
    });

    [RelayCommand]
    private Task ExportSourcesAsync() => RunSafeAsync(async () =>
    {
        if (await _shell.PickSaveTextFileAsync("Export sources", SeaRaider.Core.Catalog.SourcesFile.FileName) is not { } path) return;
        await _sourcesFile.ExportAsync(path);
        AppendLog($"Exported your sources to {path}");
    });

    [RelayCommand]
    private void ShowSourcesFile() => RunSafe(() =>
    {
        if (Path.GetDirectoryName(_sourcesFile.FilePath) is { } folder) _shell.OpenFolder(folder);
    });

    private void AppendLog(string line)
    {
        Log.Insert(0, $"{DateTime.Now:HH:mm:ss}  {line}");
        while (Log.Count > MaxLogLines) Log.RemoveAt(Log.Count - 1);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>One saved source, with live crawl progress.</summary>
public sealed partial class SourceItemViewModel : ViewModelBase
{
    private readonly SourcesViewModel _owner;
    private GameSource _source;

    public SourceItemViewModel(GameSource source, SourcesViewModel owner)
    {
        _source = source;
        _owner = owner;
        Reload(source);
    }

    public Guid Id => _source.Id;
    public string Name => _source.DisplayName;
    public string Url => _source.IsTorrent ? $"Backups torrent · {_source.TorrentOrigin}" : _source.IsLink ? $"Download link · {_source.Url}" : _source.Url;

    /// <summary>A backups torrent (its console can be chosen).</summary>
    public bool IsTorrent => _source.IsTorrent;

    /// <summary>One game's download link: it can be renamed and filed under another console.</summary>
    public bool IsLink => _source.IsLink;

    /// <summary>A site (not a browser game or a download link): it can be crawled into an offline index.</summary>
    public bool ShowIndexButtons => !_source.IsWebGame && !_source.IsLink;

    /// <summary>Editable: the link game's name (Rename saves it).</summary>
    [ObservableProperty] private string _linkName = string.Empty;

    [RelayCommand]
    private void RenameLink() => _owner.RenameLink(this, LinkName);

    /// <summary>Where this link game is filed: chosen here, and it is looked up again as that.</summary>
    public SystemChoice SelectedLinkSystem
    {
        get => SourcesViewModel.LinkSystemChoices.FirstOrDefault(c => string.Equals(c.Id, _source.LinkSystemId, StringComparison.OrdinalIgnoreCase))
               ?? SourcesViewModel.LinkSystemChoices[0];
        set
        {
            if (value is null || string.Equals(value.Id, _source.LinkSystemId, StringComparison.OrdinalIgnoreCase)) return;
            _owner.SetLinkSystem(this, value.Id);
        }
    }

    /// <summary>"Work it out from the names", then every console.</summary>
    public static IReadOnlyList<SystemChoice> ConsoleChoices { get; } =
        new[] { new SystemChoice(null, "Work it out from the names") }
            .Concat(SeaRaider.Core.Emulation.GameSystems.All.Select(s => new SystemChoice(s.Id, s.Name)))
            .ToList();

    /// <summary>The console this torrent's games are for: chosen here, every game is filed under it.</summary>
    public SystemChoice SelectedConsole
    {
        get => ConsoleChoices.FirstOrDefault(c => string.Equals(c.Id, _source.TorrentSystemId, StringComparison.OrdinalIgnoreCase)) ?? ConsoleChoices[0];
        set
        {
            if (value is null || string.Equals(value.Id, _source.TorrentSystemId, StringComparison.OrdinalIgnoreCase)) return;
            _owner.SetTorrentConsole(this, value.Id);
        }
    }

    /// <summary>A browser game's own site: one game, nothing to index; Play opens it in SeaRaider's game window.</summary>
    public bool IsWebGame
    {
        get => _source.IsWebGame;
        set
        {
            if (_source.IsWebGame == value) return;
            _owner.SetWebGame(this, value);
            OnPropertyChanged();
        }
    }

    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isHealthy = true;
    [ObservableProperty] private bool _isCrawling;
    [ObservableProperty] private bool _isProgressIndeterminate = true;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string? _progressText;
    [ObservableProperty] private string? _progressDetail;

    /// <summary>Editable: the names next to this site's "Click Here" buttons.</summary>
    [ObservableProperty] private string? _mirrorLabels;

    [ObservableProperty] private bool _isEditingLabels;

    /// <summary>On: searched and shown in the store. Off: kept, but left out until switched on again.</summary>
    public bool IsEnabled
    {
        get => _source.IsEnabled;
        set
        {
            if (_source.IsEnabled == value) return;
            _owner.SetSourceEnabled(this, value);
            OnPropertyChanged();
        }
    }

    public void Reload(GameSource source)
    {
        _source = source;
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(IsWebGame));
        OnPropertyChanged(nameof(IsLink));
        OnPropertyChanged(nameof(ShowIndexButtons));
        OnPropertyChanged(nameof(SelectedLinkSystem));
        MirrorLabels = source.Profile?.MirrorLabels;
        IsHealthy = source.LastResult is null || source.LastResult.StartsWith("OK", StringComparison.Ordinal);
        if (source.IsLink)
        {
            LinkName = source.DisplayName;
            var filed = SeaRaider.Core.Catalog.LinkSourceService.SystemFor(source)?.Name ?? "PC games";
            StatusText = (source.IsEnabled ? string.Empty : "Off: not shown in the store. ") +
                         $"One game from a download link. Filed under {filed}.";
            return;
        }
        var method = source.LastCrawlMethod is { Length: > 0 } m ? $" via {m}" : string.Empty;
        var search = (source.SearchMode == SearchMode.Auto ? source.DetectedSearchMode : source.SearchMode) switch
        {
            SearchMode.WordPressApi => "Live search via WordPress API",
            SearchMode.SiteSearchPage => "Live search via the site's search page",
            SearchMode.Custom => "Live search via custom address",
            SearchMode.Disabled => "Live search off",
            _ => "Live search: detected on first search",
        };
        if (source.IsWebGame)
        {
            StatusText = (source.IsEnabled ? string.Empty : "Off: not shown in the store. ") +
                         "Browser game: plays full screen in SeaRaider's game window.";
            return;
        }
        StatusText = (source.IsEnabled ? string.Empty : "Off: not searched or shown in the store. ") + (source.LastScrapedAtUtc is { } checkedAt
            ? $"{search}. Offline index: {source.LastGameCount:N0} games, built {checkedAt.ToLocalTime():g}{method}" +
              (IsHealthy ? string.Empty : $". {source.LastResult}")
            : $"{search}. No offline index (not needed for searching).");
    }

    public void ApplyProgress(CrawlProgress? progress, bool isRunning)
    {
        IsCrawling = isRunning;
        if (progress is null || !isRunning)
        {
            ProgressText = null;
            ProgressDetail = null;
            return;
        }

        IsProgressIndeterminate = progress.Percent is null;
        ProgressPercent = progress.Percent ?? 0;
        var method = progress.Method.Length > 0 ? $" ({progress.Method})" : string.Empty;
        ProgressText = $"{progress.GamesFound:N0} games found, {progress.UrlsProcessed + progress.UrlsSkipped:N0} of " +
                       $"{progress.UrlsDiscovered:N0} pages checked{method}";
        ProgressDetail = progress.Errors > 0 ? $"{progress.Message}. {progress.Errors} page(s) failed." : progress.Message;
    }

    [RelayCommand]
    private void Refresh() => _owner.StartCrawl(this, fullRescan: false);

    [RelayCommand]
    private void FullRescan() => _owner.StartCrawl(this, fullRescan: true);

    [RelayCommand]
    private void Stop() => _owner.StopCrawl(this);

    [RelayCommand]
    private Task RemoveAsync() => _owner.RemoveSourceAsync(this);

    [RelayCommand]
    private void EditLabels() => IsEditingLabels = !IsEditingLabels;

    [RelayCommand]
    private void SaveLabels()
    {
        _owner.SaveMirrorLabels(this, MirrorLabels);
        IsEditingLabels = false;
    }
}
