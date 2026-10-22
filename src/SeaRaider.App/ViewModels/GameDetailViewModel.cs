using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SeaRaider.App.Services;
using SeaRaider.Core;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Catalog;
using SeaRaider.Core.Models;
using SeaRaider.Core.Social;
using SeaRaider.Core.Scraping;

namespace SeaRaider.App.ViewModels;

/// <summary>A screenshot or trailer in the store page's media strip.</summary>
public sealed record MediaItemViewModel(string ImageUrl, string? VideoUrl)
{
    public bool IsVideo => VideoUrl is not null;
}

/// <summary>
/// One way to download the selected source's copy, after duplicates are removed (<see cref="DownloadLinkDeduplicator"/>):
/// the automatic choice, a torrent, a host's files, or a multi-part archive on one host — with a tag, size and what to expect.
/// </summary>
public sealed partial class MirrorOptionViewModel(
    string name, string tag, string? sizeText, string? note, DownloadLink? link, bool browserOnly, Action<MirrorOptionViewModel> select)
    : ObservableObject
{
    /// <summary>"Magnet link", "Buzzheavier", "DataNodes", "filekeeper.net" …</summary>
    public string Name { get; } = name;

    /// <summary>"Torrent", "24-part archive", "File host", "Direct download", "3 files".</summary>
    public string Tag { get; } = tag;

    public string? SizeText { get; } = sizeText;
    public bool HasSize => !string.IsNullOrEmpty(SizeText);

    /// <summary>What happens ("Opens a verification window …"), when it's not hands-free.</summary>
    public string? Note { get; } = note;
    public bool HasNote => !string.IsNullOrEmpty(Note);

    /// <summary>The link (first part / first file); null = automatic.</summary>
    public DownloadLink? Link { get; } = link;

    /// <summary>Only works in a browser (MEGA, tracker pages): the button opens it there.</summary>
    public bool BrowserOnly { get; } = browserOnly;

    [ObservableProperty] private bool _isSelected;

    [RelayCommand]
    private void Select() => select(this);

    public override string ToString() => Name;
}

/// <summary>A heading in the mirror list ("File hosts &amp; direct downloads", "Multi-part archives", "Torrents").</summary>
public sealed record MirrorSectionViewModel(string Title, IReadOnlyList<MirrorOptionViewModel> Options);

/// <summary>One site that has this game (a tab on the merged store page).</summary>
public sealed partial class SourceTabViewModel(string gameId, string name, string? detail, Action<SourceTabViewModel> select)
    : ObservableObject
{
    public string GameId { get; } = gameId;
    public string Name { get; } = name;

    /// <summary>"v1.10 · 47.3 GB" for this source's copy.</summary>
    public string? Detail { get; } = detail;
    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    [ObservableProperty] private bool _isSelected;

    [RelayCommand]
    private void Select() => select(this);
}

/// <summary>A labelled fact on the store page ("Repack size: 5.2 GB").</summary>
public sealed record InfoPill(string Label, string Value);

/// <summary>
/// Steam-style store page for one game. Opens instantly with whatever is cached (often just a search result:
/// title, cover, excerpt) and loads the full page in the background — description, screenshots, structured
/// info pills and download links — then refreshes in place.
/// <para>When several sites have the game (<see cref="CatalogIndexService.GetVariants"/>), each is a source tab; picking
/// one shows that site's copy (version, sizes, description) and its mirrors: torrent, direct files, and file hosts
/// like Buzzheavier or DataNodes, each with its size.</para>
/// </summary>
public sealed partial class GameDetailViewModel : PageViewModel, IDisposable
{
    private readonly GameStatusService _status;
    private readonly GameActionService _actions;
    private readonly NavigationService _navigation;
    private readonly ShellService _shell;
    private readonly TrailerPlayerService _trailers;
    private readonly ILibraryRepository _repository;
    private readonly IGameDetailsService _details;
    private readonly CatalogIndexService _index;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _loadCts;

    public GameDetailViewModel(string gameId, ILibraryRepository repository, IGameDetailsService details, GameStatusService status,
        GameActionService actions, NavigationService navigation, ShellService shell, CatalogIndexService index,
        TrailerPlayerService trailers, ArtworkService artwork, IFriendsService friends)
    {
        _friends = friends;
        _trailers = trailers;
        _status = status;
        _actions = actions;
        _navigation = navigation;
        _shell = shell;
        _repository = repository;
        _details = details;
        _index = index;

        _game = repository.GetGame(gameId) ?? throw new InvalidOperationException("This game is no longer in the catalog.");
        ShowAdvanced = repository.GetSettings().ShowAdvancedOptions;
        OptionsOpen = false; // "More download options" starts closed, whatever the advanced-options setting
        BuildSourceTabs();
        LoadArtwork(artwork);
        ApplyGame();
        _status.GameChanged += OnGameChanged;
        _friends.FriendsChanged += OnFriendsChanged;
        RefreshShareTargets();

        if (_game.NeedsDetails || !_game.MediaUpToDate) _ = LoadDetailsAsync(forceRefresh: false);
    }

    private GameMetadata _game;

    public GameMetadata Game => _game;

    /// <summary>Just the game's name; the release notes in the site's title are shown as tags.</summary>
    public string Title => DisplayText.Title(_game.Title);

    /// <summary>The site's full title, for experts (Settings → Show advanced options).</summary>
    public string? RawTitle => ShowAdvanced && _game.RawTitle is { } raw && raw != Title ? raw : null;
    public bool HasRawTitle => RawTitle is not null;
    public string Description => DisplayText.Description(_game.Description) is { Length: > 0 } story
        ? story
        : IsLoadingDetails ? string.Empty : "No description was found on the source page.";
    public string ShortDescription => DisplayText.Description(_game.ShortDescription);
    public string? CoverImageUrl => string.IsNullOrEmpty(_game.CoverImageUrl) ? _fallbackCover : _game.CoverImageUrl;

    /// <summary>High-resolution art for the top of the page and the window backdrop (Steam), else the cover.</summary>
    public string? HeroImageUrl => _heroUrl ?? CoverImageUrl;

    private string? _fallbackCover;
    private string? _heroUrl;

    private async void LoadArtwork(ArtworkService artwork)
    {
        try
        {
            if (await artwork.GetAsync(_game.Title, _game.Id) is not { } art) return;
            _fallbackCover = art.HeaderUrl;
            _heroUrl = art.HeroUrl;
            OnPropertyChanged(nameof(CoverImageUrl));
            OnPropertyChanged(nameof(HeroImageUrl));
            if (art.HeroUrl is { } hero && Media.Count == 1 && Media[0].VideoUrl is null && Media[0].ImageUrl == _game.CoverImageUrl)
            {
                Media[0] = new MediaItemViewModel(hero, null);
                SelectedMedia = Media[0];
            }
        }
        catch (Exception)
        {
            // keep the page's own pictures
        }
    }
    public IReadOnlyList<string> Genres => _game.Genres.Count > 0 ? _game.Genres : _game.Tags;
    public bool HasGenres => Genres.Count > 0;
    public IReadOnlyList<string> Languages => _game.Languages;
    public bool HasLanguages => _game.Languages.Count > 0;
    public string SourceHost => Uri.TryCreate(_game.PageUrl, UriKind.Absolute, out var u) ? u.Host : _game.PageUrl;
    public string SourceName => _repository.GetSource(_game.SourceId) is { } source ? SiteProfiles.NameFor(source) : SourceHost;
    /// <summary>"Screenshots and trailers from Steam" when the page had none of its own.</summary>
    public string? MediaSourceText => _game.MediaSource is { } source ? $"Screenshots and trailers from {source}" : null;
    public bool HasMediaSource => MediaSourceText is not null;

    public string FetchedText => _game.DetailsLoaded
        ? $"Info from {SourceHost}, updated {_game.FetchedAtUtc.ToLocalTime():d}"
        : $"From {SourceHost} search results";

    /// <summary>Release, upload, version, DLC, sizes, developer — only the facts the page actually had.</summary>
    public ObservableCollection<InfoPill> InfoPills { get; } = [];
    public bool HasInfoPills => InfoPills.Count > 0;

    public ObservableCollection<MediaItemViewModel> Media { get; } = [];
    public bool HasMedia => Media.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedIsVideo))]
    private MediaItemViewModel? _selectedMedia;

    public bool SelectedIsVideo => SelectedMedia?.IsVideo == true;

    /// <summary>Sites that have this game; empty when only one does.</summary>
    public ObservableCollection<SourceTabViewModel> SourceTabs { get; } = [];
    public bool HasSourceTabs => SourceTabs.Count > 1;

    /// <summary>Ways to download the selected source's copy (flat, for selection); shown via <see cref="MirrorSections"/>.</summary>
    public ObservableCollection<MirrorOptionViewModel> Mirrors { get; } = [];

    /// <summary>
    /// The options shown first, outside the drop-down: FuckingFast, then FileKeeper, whichever the page has. When the page
    /// has neither, this is just "Automatic".
    /// </summary>
    public ObservableCollection<MirrorOptionViewModel> TopMirrors { get; } = [];

    /// <summary>Everything else (Automatic, other hosts, archives, torrents) grouped by kind, inside the drop-down.</summary>
    public ObservableCollection<MirrorSectionViewModel> MirrorSections { get; } = [];

    /// <summary>The drop-down has something in it.</summary>
    public bool HasMoreOptions => MirrorSections.Count > 0;

    /// <summary>"More download options (4)" on the drop-down's header.</summary>
    public string MoreOptionsText => $"More download options ({Math.Max(0, Mirrors.Count - 1)})";

    [ObservableProperty] private MirrorOptionViewModel? _automaticMirror;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedMirrorSummary))]
    private MirrorOptionViewModel? _selectedMirror;

    /// <summary>Settings → "Show advanced options": the download-source picker starts open.</summary>
    public bool ShowAdvanced { get; private set; }

    /// <summary>The "More download options" drop-down is open.</summary>
    [ObservableProperty] private bool _optionsOpen;

    /// <summary>One line under "Download options" saying where the game will come from.</summary>
    public string SelectedMirrorSummary => SelectedMirror is null || ReferenceEquals(SelectedMirror, AutomaticMirror)
        ? "Automatic: SeaRaider picks the best source and switches if one fails."
        : $"From {SelectedMirror.Name} ({SelectedMirror.Tag}).";
    public bool HasDownloads => _game.DownloadLinks.Count > 0;
    public bool HasMultipleOptions => Mirrors.Count > 2; // "Automatic" + at least two real choices

    // ---- lazy details ----------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Description))]
    private bool _isLoadingDetails;

    [ObservableProperty] private string? _detailsError;

    public bool HasDetailsError => !string.IsNullOrEmpty(DetailsError);

    partial void OnDetailsErrorChanged(string? value) => OnPropertyChanged(nameof(HasDetailsError));

    private async Task LoadDetailsAsync(bool forceRefresh)
    {
        _loadCts?.Cancel();
        var cts = _loadCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var gameId = _game.Id;
        IsLoadingDetails = true;
        DetailsError = null;
        RefreshStatus();
        try
        {
            var loaded = await _details.GetAsync(gameId, forceRefresh, cts.Token);
            if (_game.Id != gameId) return; // another source tab was picked meanwhile
            _game = loaded;
            ApplyGame();
        }
        catch (OperationCanceledException)
        {
            // Page closed.
        }
        catch (Exception ex)
        {
            DetailsError = $"Couldn't load the full page from {SourceHost}: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                IsLoadingDetails = false;
                RefreshStatus();
            }
        }
    }

    // ---- sources ---------------------------------------------------------------------------------

    /// <summary>One tab per copy of this game in the catalog (several per site when a site posted it twice).</summary>
    private void BuildSourceTabs()
    {
        SourceTabs.Clear();
        var variants = _index.GetVariants(_game.Id);
        if (variants.Count <= 1) return;

        var names = _repository.GetSources().ToDictionary(s => s.Id, SiteProfiles.NameFor);
        foreach (var variant in variants.OrderBy(v => names.TryGetValue(v.SourceId, out var n) ? n : "~").ThenByDescending(v => v.UploadedAtUtc))
        {
            var name = names.TryGetValue(variant.SourceId, out var sourceName) ? sourceName : "Unknown source";
            var facts = new[]
            {
                variant.Version is { Length: > 0 } v ? (char.IsDigit(v[0]) ? "v" + v : v) : null,
                variant.Size,
            }.Where(f => !string.IsNullOrWhiteSpace(f));
            var detail = string.Join(" · ", facts);
            SourceTabs.Add(new SourceTabViewModel(variant.Id, name, detail.Length > 0 ? detail : null, SelectSource)
            {
                IsSelected = variant.Id == _game.Id,
            });
        }
    }

    private void SelectSource(SourceTabViewModel tab)
    {
        foreach (var other in SourceTabs) other.IsSelected = ReferenceEquals(other, tab);
        if (tab.GameId == _game.Id) return;

        RunSafe(() =>
        {
            _game = _repository.GetGame(tab.GameId) ?? throw new InvalidOperationException("That copy is no longer in the catalog.");
            DetailsError = null;
            ApplyGame();
            if (_game.NeedsDetails || !_game.MediaUpToDate) _ = LoadDetailsAsync(forceRefresh: false);
        });
    }

    /// <summary>Rebuilds everything derived from <see cref="Game"/> and refreshes all bindings.</summary>
    private void ApplyGame()
    {
        Media.Clear();
        // Older records may still hold stats banners / buttons: filter at display time too.
        // Shown large: the full-size picture behind each thumbnail URL (riotpixels "….240p.jpg", WordPress "-300x169" …).
        foreach (var url in MediaScraperService.CleanScreenshots(_game.ScreenshotUrls))
            Media.Add(new MediaItemViewModel(ImageQuality.Upgrade(url), null));
        for (var i = _game.TrailerUrls.Count - 1; i >= 0; i--)
        {
            var trailer = _game.TrailerUrls[i];
            var thumb = (i < _game.TrailerThumbnailUrls.Count ? _game.TrailerThumbnailUrls[i] : null)
                        ?? MediaUrlHelper.GetThumbnailUrl(trailer) ?? _game.CoverImageUrl;
            if (thumb is not null) Media.Insert(0, new MediaItemViewModel(thumb, trailer)); // trailers first, like Steam
        }
        // Nothing else to show: the high-resolution art if it's known, else the cover.
        if (Media.Count == 0 && (_heroUrl ?? _game.CoverImageUrl) is { } picture) Media.Add(new MediaItemViewModel(picture, null));
        SelectedMedia = Media.FirstOrDefault();

        InfoPills.Clear();
        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) InfoPills.Add(new InfoPill(label, value));
        }
        // The game's own release date only: a date that is just the day it was posted isn't one.
        if (ReleaseDates.IsReal(_game.ReleaseDateUtc, _game.UploadedAtUtc, _game.ReleaseDateFromSteam) || _game.ReleaseDateUtc is null)
            Add("Release date", _game.ReleaseDate ?? _game.ReleaseDateUtc?.ToString("d MMM yyyy", CultureInfo.CurrentCulture));
        Add("Uploaded", _game.UploadedAtUtc?.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture));
        Add("Version", _game.Version is { Length: > 0 } v && char.IsDigit(v[0]) ? "v" + v : _game.Version);
        Add("DLC", _game.DlcInfo);
        Add("Repack size", _game.RepackSize);
        Add("Original size", _game.OriginalSize ?? (_game.RepackSize is null ? _game.SizeText : null));
        Add("Developer", _game.Developer);
        Add("Publisher", _game.Publisher);

        Mirrors.Clear();
        MirrorSections.Clear();
        TopMirrors.Clear();
        BuildMirrors();

        OnPropertyChanged(string.Empty); // every computed property above depends on the game
        RefreshStatus();
    }

    // ---- live state ------------------------------------------------------------------------------

    [ObservableProperty] private string _actionLabel = string.Empty;
    [ObservableProperty] private string? _statusLine;
    [ObservableProperty] private bool _isBusy;
    /// <summary>"Play" state (brass button).</summary>
    [ObservableProperty] private bool _isPlayable;
    [ObservableProperty] private bool _hasProgress;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _canChooseSource;
    [ObservableProperty] private bool _isActionEnabled;
    [ObservableProperty] private string? _speedText;

    /// <summary>The finished torrent is still being shared: "Stop seeding" is shown.</summary>
    [ObservableProperty] private bool _isSeeding;
    [ObservableProperty] private string? _seedText;

    private void OnGameChanged(string gameId)
    {
        if (gameId == _game.Id || SourceTabs.Any(t => t.GameId == gameId)) RefreshStatus();
    }

    private void RefreshStatus()
    {
        var status = _status.GetStatus(_game.Id);
        var onDisk = status.State is GameUiState.Installed or GameUiState.Running or GameUiState.ReadyToInstall or GameUiState.Installing;
        var canFetchLinks = HasDownloads || !_game.DetailsLoaded || IsLoadingDetails;

        ActionLabel = IsLoadingDetails && !onDisk && !HasDownloads
            ? "Loading…"
            : canFetchLinks || onDisk ? status.Label : "No download found";
        IsBusy = status.IsBusy;
        IsPlayable = status.IsPlayable;
        HasProgress = status.Percent is not null && !onDisk;
        Progress = status.Percent ?? 0;
        CanChooseSource = status.State is GameUiState.Available or GameUiState.Failed && HasMultipleOptions;
        if (status.State == GameUiState.Available && SelectedMirror is { BrowserOnly: true }) ActionLabel = "Open in browser";
        IsActionEnabled = (canFetchLinks || onDisk || status.State != GameUiState.Available) &&
                          status.State != GameUiState.Installing && !(IsLoadingDetails && !onDisk && !HasDownloads);

        var job = _status.GetLatestJob(_game.Id);
        StatusLine = status.State switch
        {
            GameUiState.Installed => "Installed and ready to play",
            GameUiState.Running => "Playing now",
            GameUiState.ReadyToInstall => _repository.GetInstall(_game.Id) is { } pending
                ? pending.LastError ?? $"Downloaded. Run the {DescribeInstaller(pending.InstallerKind)} to finish installing."
                : "Downloaded. Run the setup to finish installing.",
            GameUiState.Installing => _status.GetSetupActivity(_game.Id)?.Message
                                      ?? "The setup program is running. The game becomes playable when it finishes.",
            GameUiState.Failed => job?.Error ?? "The last install failed",
            GameUiState.Available => IsLoadingDetails ? $"Loading the full page from {SourceName}…" : OtherCopyStatus(),
            _ => job?.StatusDetail,
        };
        IsSeeding = job is { IsSeeding: true };
        SeedText = job is { IsSeeding: true } seeding
            ? $"Seeding to {seeding.Peers} peer(s) · uploading {Formatting.Speed(seeding.UploadBytesPerSecond)} · ratio {seeding.SeedRatio:0.00}"
            : null;
        SpeedText = status.State == GameUiState.Downloading && job is { BytesPerSecond: > 0 }
            ? $"{Formatting.Speed(job.BytesPerSecond)}, {Formatting.Duration(job.Eta)} left"
            : null;
    }

    /// <summary>"Installed from FitGirl Repacks" when another source's copy is on disk.</summary>
    private string? OtherCopyStatus()
    {
        foreach (var tab in SourceTabs.Where(t => t.GameId != _game.Id))
        {
            var state = _status.GetStatus(tab.GameId).State;
            if (state is GameUiState.Installed or GameUiState.Running) return $"Already installed from {tab.Name}.";
            if (state is GameUiState.Downloading or GameUiState.Queued or GameUiState.Extracting or GameUiState.ReadyToInstall)
                return $"Already being downloaded from {tab.Name}.";
        }
        return null;
    }

    partial void OnSelectedMirrorChanged(MirrorOptionViewModel? value) => RefreshStatus();

    /// <summary>
    /// One option per mirror and kind (duplicates removed), under headings by kind. A host that has several of a release's
    /// files is one option ("3 files"); a split archive on a host is one option ("24-part archive").
    /// </summary>
    private void BuildMirrors()
    {
        var pageSize = _game.RepackSize ?? _game.OriginalSize ?? _game.SizeText;
        AutomaticMirror = new MirrorOptionViewModel("Automatic", "Best available", pageSize,
            "FuckingFast and FileKeeper first when the page has them, then the best host. If a host has lost a file, the next one is tried, then the torrent.",
            null, false, SelectMirror);
        Mirrors.Add(AutomaticMirror);
        TopMirrors.Clear();

        var groups = DownloadLinkDeduplicator.Group(_game.DownloadLinks);

        // Every way to download sits in the one drop-down: Automatic first, then each kind, with FuckingFast and
        // FileKeeper leading their section when the page has them.
        MirrorSections.Add(new MirrorSectionViewModel("AUTOMATIC", [AutomaticMirror]));
        foreach (var section in groups.GroupBy(g => SectionTitle(g.Category)))
        {
            var options = new List<(int Priority, int Order, MirrorOptionViewModel Option)>();
            foreach (var g in section)
            {
                var (note, browserOnly) = AccessNote(g);
                var option = new MirrorOptionViewModel(g.Mirror, g.Tag, g.SizeText ?? pageSize, note, g.Representative, browserOnly, SelectMirror);
                Mirrors.Add(option);
                options.Add((FileHosts.Priority(g.Representative), options.Count, option));
            }
            MirrorSections.Add(new MirrorSectionViewModel(section.Key,
                options.OrderBy(o => o.Priority == FileHosts.NotPreferred ? int.MaxValue : o.Priority).ThenBy(o => o.Order).Select(o => o.Option).ToList()));
        }

        // A page whose only options need a browser (itch.io, MEGA): that option is the choice, so the button opens it.
        var realOptions = Mirrors.Skip(1).ToList();
        SelectMirror(realOptions.Count > 0 && realOptions.All(m => m.BrowserOnly) ? realOptions[0] : AutomaticMirror);
    }

    private void SelectMirror(MirrorOptionViewModel option)
    {
        foreach (var other in Mirrors) other.IsSelected = ReferenceEquals(other, option);
        SelectedMirror = option;
    }

    private static string SectionTitle(LinkCategory category) => category switch
    {
        LinkCategory.FileHost or LinkCategory.DirectDownload => "FILE HOSTS & DIRECT DOWNLOADS",
        LinkCategory.MultiPartArchive => "MULTI-PART ARCHIVES",
        LinkCategory.Torrent => "TORRENTS",
        _ => "OPENS IN YOUR BROWSER",
    };

    private static (string? Note, bool BrowserOnly) AccessNote(MirrorGroup group) => group.Category == LinkCategory.BrowserOnly
        ? ("Only works in a web browser: opens there instead.", true)
        : group.Representative.LeadsToTorrent
            ? ("SeaRaider goes through the link page in the background to get the torrent, then downloads it with its torrent client.", false)
        : group.Access switch
        {
            HostAccess.Browser =>("Downloads in SeaRaider's browser window. You only step in for a captcha.", false),
            _ when group.Links.Count > 1 && group.Category != LinkCategory.MultiPartArchive =>
                ($"Downloads all {group.Links.Count} files from this host.", false),
            _ => (null, false),
        };

    internal static string DescribeInstaller(InstallerKind kind) => kind switch
    {
        InstallerKind.InnoSetup => "Inno Setup installer",
        InstallerKind.Nsis => "NSIS installer",
        InstallerKind.Msi => "Windows Installer package",
        InstallerKind.InstallShield => "InstallShield setup",
        InstallerKind.WixBurn => "WiX setup bundle",
        InstallerKind.AdvancedInstaller => "Advanced Installer setup",
        InstallerKind.Wise => "Wise installer",
        InstallerKind.SetupFactory => "Setup Factory installer",
        InstallerKind.SevenZipSfx => "self-extracting setup",
        _ => "setup wizard",
    };

    // ---- commands --------------------------------------------------------------------------------

    [RelayCommand]
    private Task PrimaryActionAsync() => RunSafeAsync(async () =>
    {
        if (SelectedMirror is { BrowserOnly: true, Link: { } external } && _status.GetStatus(_game.Id).State == GameUiState.Available)
        {
            _shell.OpenUrl(external.Url);
            return;
        }
        await _actions.ExecutePrimaryAsync(_game.Id, SelectedMirror?.Link);
    });

    [RelayCommand]
    private Task ReloadDetailsAsync() => LoadDetailsAsync(forceRefresh: true);

    [RelayCommand]
    private Task StopSeedingAsync() => RunSafeAsync(() => _actions.StopSeedingAsync(_game.Id));

    [RelayCommand]
    private void SelectMedia(MediaItemViewModel item) => SelectedMedia = item;

    [RelayCommand]
    private void PlayTrailer()
    {
        if (SelectedMedia?.VideoUrl is { } url) RunSafe(() => _trailers.Play(url, _game.StorePageUrl, _game.Title));
    }

    [RelayCommand]
    private void OpenSourcePage() => RunSafe(() => _shell.OpenUrl(_game.PageUrl));

    [RelayCommand]
    private void Back() => _navigation.GoBack();

    /// <summary>A genre / tag chip was clicked: the store opens with it typed in the search box (genres are searched too).</summary>
    [RelayCommand]
    private void SearchTag(string? tag)
    {
        if (!string.IsNullOrWhiteSpace(tag)) _navigation.SearchStore(tag.Trim());
    }

    // ---- share with a friend -------------------------------------------------------------------------

    private readonly IFriendsService _friends;

    /// <summary>Friends to send this game to (online first), for the "Share with a friend" menu.</summary>
    public ObservableCollection<ShareTargetViewModel> ShareTargets { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasShareStatus))]
    private string? _shareStatus;

    public bool HasShareStatus => !string.IsNullOrEmpty(ShareStatus);
    public bool CanShare => _friends.IsEnabled;
    public bool HasNoShareTargets => ShareTargets.Count == 0;

    private void OnFriendsChanged(object? sender, EventArgs e) => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshShareTargets);

    private void RefreshShareTargets()
    {
        List<Friend> friends;
        try { friends = _friends.Friends.Where(f => f.State == FriendshipState.Friend).ToList(); }
        catch (InvalidOperationException) { return; } // changing underneath; the next change event refreshes
        ShareTargets.Clear();
        foreach (var friend in friends.OrderBy(f => f.Status == UserStatus.Offline).ThenBy(f => f.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            ShareTargets.Add(new ShareTargetViewModel(this, friend.UserId, friend.DisplayName, friend.StatusText, friend.Status != UserStatus.Offline));
        OnPropertyChanged(nameof(HasNoShareTargets));
        OnPropertyChanged(nameof(CanShare));
    }

    internal Task ShareWithAsync(ShareTargetViewModel target) => RunSafeAsync(async () =>
    {
        ShareStatus = $"Sending to {target.Name}…";
        var size = _game.RepackSize ?? _game.SizeText;
        var message = await _friends.ShareGameAsync(target.UserId, new SharedGame(_game.Title, _game.PageUrl, CoverImageUrl, SourceName, size));
        ShareStatus = message.Failed
            ? $"Couldn't reach {target.Name} right now. It's in your chat; Retry it from there."
            : $"Shared with {target.Name}.";
    });

    /// <summary>"Come play this with me": an invite card in the friend's chat (Play if they have it, else Get it).</summary>
    internal Task InviteWithAsync(ShareTargetViewModel target) => RunSafeAsync(async () =>
    {
        ShareStatus = $"Inviting {target.Name}…";
        var message = await _friends.SendInviteAsync(target.UserId, new ChatInvite
        {
            Kind = InviteKind.Game,
            Title = Title,
            PageUrl = _game.PageUrl,
            CoverUrl = CoverImageUrl,
            SourceName = SourceName,
        });
        ShareStatus = message.Failed
            ? $"Couldn't reach {target.Name} right now. It's in your chat; Retry it from there."
            : $"Invited {target.Name} to play.";
    });

    public void Dispose()
    {
        _friends.FriendsChanged -= OnFriendsChanged;
        _status.GameChanged -= OnGameChanged;
        _loadCts?.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}

/// <summary>A friend in the "Share with a friend" menu.</summary>
public sealed partial class ShareTargetViewModel(GameDetailViewModel owner, string userId, string name, string status, bool isOnline)
    : ObservableObject
{
    public string UserId { get; } = userId;
    public string Name { get; } = name;
    public string Status { get; } = status;
    public bool IsOnline { get; } = isOnline;

    [RelayCommand]
    private Task ShareAsync() => owner.ShareWithAsync(this);

    [RelayCommand]
    private Task InviteAsync() => owner.InviteWithAsync(this);
}
