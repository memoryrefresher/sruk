using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using SeaRaider.App.Services;
using SeaRaider.Core;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Catalog;
using SeaRaider.Core.Models;
using SeaRaider.Core.WebChallenge;

namespace SeaRaider.App.ViewModels;

/// <summary>Left-rail destination.</summary>
public sealed record NavItem(AppPage Page, string Label, string IconData)
{
    /// <summary>Outline icon geometry (stroked, not filled, by the rail template).</summary>
    public Geometry Icon { get; } = StreamGeometry.Parse(IconData);
}

/// <summary>
/// The shell: owns navigation between pages, the store-page back stack, and the
/// download status bar pinned to the bottom of the window.
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    /// <summary>New SeaRaider versions (the update banner).</summary>
    public UpdateViewModel Updates { get; }

    /// <summary>Legacy shows the original line logo; The Next Generation shows the SeaRaider logo.</summary>
    public bool IsLegacySkin => _skin.Current == SeaRaider.Core.Models.AppSkin.Legacy;

    private readonly IServiceProvider _services;
    private readonly IDownloadAndExtractManager _downloads;
    private readonly Dictionary<AppPage, PageViewModel> _pages;
    private readonly Stack<PageViewModel> _backStack = new();
    private bool _suppressNavSelection;

    private readonly IChallengeResolver _challengeResolver;

    private readonly SkinService _skin;
    private readonly BackdropService _backdrops;
    private string? _backdropUrl;

    public MainWindowViewModel(IServiceProvider services, NavigationService navigation, GameStatusService status,
        IDownloadAndExtractManager downloads, DialogService dialogs, IChallengeResolver challengeResolver, FriendsViewModel friends,
        SkinService skin, BackdropService backdrops, IInstallerService installer, TrailerPlayerService trailer,
        UpcomingService upcomingService, UpcomingViewModel upcoming, EffectsService effects, WatchTogetherService watchTogether)
    {
        WatchTogether = watchTogether;
        Updates = services.GetRequiredService<UpdateViewModel>();
        EventChat = services.GetRequiredService<EventChatViewModel>();
        skin.Changed += (_, _) => OnPropertyChanged(nameof(IsLegacySkin));
        _effects = effects;
        effects.Changed += (_, _) => OnPropertyChanged(nameof(IsOverlayBlurred));
        Friends = friends;
        Trailer = trailer;
        Upcoming = upcoming;
        _upcomingService = upcomingService;
        trailer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TrailerPlayerService.IsOpen) or nameof(TrailerPlayerService.IsMinimized)) OverlayChanged();
        };
        upcoming.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(UpcomingViewModel.IsPopoutOpen)) OverlayChanged(); };
        _installer = installer;
        status.SetupCleanupOffered += offer => OfferCleanup(offer, afterPlaying: false);
        // A game with untried setup files was just closed: ask whether it worked.
        _launcher = services.GetRequiredService<IGameLauncherService>();
        _launcher.SessionChanged += (_, e) =>
        {
            if (!e.IsRunning) Dispatcher.UIThread.Post(() => _ = AskAfterPlayingAsync(e.GameId));
        };
        _skin = skin;
        _backdrops = backdrops;
        _challengeResolver = challengeResolver;
        status.VerificationPromptChanged += prompt =>
        {
            HasVerificationPrompt = prompt.IsOpen;
            VerificationText = prompt.Kind switch
            {
                ChallengeKind.BotCheck =>
                    $"{prompt.Host} wants to check you're human. Complete it in the verification window; SeaRaider continues automatically.",
                ChallengeKind.BrowserDownload =>
                    $"{prompt.Host} needs you in SeaRaider's download window (captcha or download button). The download continues from there.",
                _ => $"{prompt.Host} needs a captcha. Solve it in the verification window, then click the site's download button.",
            };
        };
        _services = services;
        _downloads = downloads;
        Dialogs = dialogs;

        _pages = new Dictionary<AppPage, PageViewModel>
        {
            [AppPage.Store] = services.GetRequiredService<StorefrontViewModel>(),
            [AppPage.Library] = services.GetRequiredService<LibraryViewModel>(),
            [AppPage.Wishlist] = services.GetRequiredService<WishlistViewModel>(),
            [AppPage.Media] = services.GetRequiredService<MediaViewModel>(),
            [AppPage.Downloads] = services.GetRequiredService<DownloadsViewModel>(),
            [AppPage.Settings] = services.GetRequiredService<SettingsViewModel>(),
        };

        NavItems =
        [
            new(AppPage.Store, "Store", Icons.Store),
            new(AppPage.Library, "Library", Icons.Library),
            new(AppPage.Wishlist, "Wishlist", Icons.Wishlist),
            new(AppPage.Media, "Media", Icons.Media),
            new(AppPage.Downloads, "Downloads", Icons.Download),
        ];
        SettingsItem = new NavItem(AppPage.Settings, "Settings", Icons.Settings);
        FriendsIcon = StreamGeometry.Parse(Icons.Friends);

        // "The Next Generation": the window's backdrop is the featured game (or the open game page), blurred.
        if (_pages[AppPage.Store] is StorefrontViewModel store)
            store.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(StorefrontViewModel.Featured)) return;
                WatchHero(store.Featured);
                UpdateBackdrop();
            };
        skin.Changed += (_, _) => UpdateBackdrop();

        navigation.PageRequested += ShowPage;
        navigation.GameRequested += ShowGame;
        navigation.BackRequested += GoBack;
        navigation.SearchRequested += _ => ShowPage(AppPage.Store);
        status.JobUpdated += _ => UpdateDownloadBar();
        status.JobRemoved += _ => UpdateDownloadBar();

        // Always start in the store. A brand-new install is then asked its welcome questions on top of it (see RunFirstRunAsync).
        ShowPage(AppPage.Store);
        UpdateDownloadBar();
        Dispatcher.UIThread.Post(() => _ = RunFirstRunAsync(), DispatcherPriority.Background);
        _ = OfferLeftoverCleanupsAsync();

        // Wishlist: a notice when a wishlisted game shows up in the store (also ones found while SeaRaider was closed).
        upcomingService.WishlistItemAvailable += (_, e) => Dispatcher.UIThread.Post(() => QueueWishlistNotice(e.Item));
        foreach (var item in upcomingService.GetUnnotified()) QueueWishlistNotice(item);
        upcomingService.Start();
    }

    public IReadOnlyList<NavItem> NavItems { get; }

    // ---- first run -----------------------------------------------------------------------------------

    /// <summary>
    /// A brand-new install is asked the welcome questions (sources file, name, folders) over the store, and the answer is
    /// remembered (<see cref="AppSettings.FirstRun"/>). An install from before the questions existed is marked as answered
    /// without asking.
    /// </summary>
    private async Task RunFirstRunAsync()
    {
        try
        {
            var repository = _services.GetRequiredService<ILibraryRepository>();
            var settings = repository.GetSettings();
            if (settings.FirstRun == FirstRunState.Unknown)
            {
                settings.FirstRun = FirstRunDetector.IsFreshInstall ? FirstRunState.Pending : FirstRunState.Completed;
                repository.SaveSettings(settings);
            }
            if (settings.FirstRun != FirstRunState.Pending) return;

            // A sources.txt dropped next to SeaRaider (or any source already added) means there is nothing to import.
            var hasSources = repository.GetSources().Count > 0 || repository.GetUpcomingSources().Count > 0
                                                              || repository.GetMediaSources().Count > 0;
            var dialog = new OnboardingDialogViewModel(repository, _services.GetRequiredService<SourcesFileService>(),
                _services.GetRequiredService<SeaRaider.Core.Social.IFriendsService>(), _services.GetRequiredService<ShellService>(),
                askForSources: !hasSources);
            await Dialogs.ShowOnboardingAsync(dialog);

            ShowPage(AppPage.Store); // the store picks up whatever sources were just imported
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "first run");
        }
    }

    /// <summary>False when Settings › Appearance has Animations off: the window then shows at once instead of fading in.</summary>
    public bool AnimationsEnabled => _effects.AnimationsEnabled;

    /// <summary>The Friends &amp; Chat side panel (toggled from the rail or with Ctrl+Shift+F).</summary>
    public FriendsViewModel Friends { get; }

    /// <summary>In-window modal dialogs (shown in an overlay above the pages).</summary>
    public DialogService Dialogs { get; }

    /// <summary>The in-app trailer player (shown in an overlay above a blurred window).</summary>
    public TrailerPlayerService Trailer { get; }

    /// <summary>Watch along and "Invite to watch" in the player's top bar.</summary>
    public WatchTogetherService WatchTogether { get; }

    /// <summary>The store's "Coming soon" shelf; its coming-soon page pops out over the blurred window.</summary>
    public UpcomingViewModel Upcoming { get; }

    /// <summary>A trailer or a coming-soon page is popped out: the window behind it is blurred.</summary>
    /// <summary>The live chat of what's playing (beside the player).</summary>
    public EventChatViewModel EventChat { get; }

    /// <summary>Something covers the app (blurred behind it). The mini-player doesn't: the app stays usable around it.</summary>
    public bool IsOverlayOpen => (Trailer.IsOpen && !Trailer.IsMinimized) || Upcoming.IsPopoutOpen;

    /// <summary>
    /// The window blurs itself behind a coming-soon page unless "Blur and glow" is off (then it's only dimmed). Not behind the
    /// player: a full-window blur redrawn under a playing video (and the chat and bet slip beside it) made it all lag.
    /// </summary>
    public bool IsOverlayBlurred => IsOverlayOpen && !IsPlayerCovering && _effects.BlurEnabled;

    /// <summary>The player fills the window (not in its corner): the app behind it isn't drawn at all while it plays.</summary>
    public bool IsPlayerCovering => Trailer.IsOpen && !Trailer.IsMinimized;

    private readonly EffectsService _effects;

    private void OverlayChanged()
    {
        OnPropertyChanged(nameof(IsOverlayOpen));
        OnPropertyChanged(nameof(IsOverlayBlurred));
        OnPropertyChanged(nameof(IsPlayerCovering));
    }

    // ---- "now in the store" wishlist notice ------------------------------------------------------

    private readonly UpcomingService _upcomingService;
    private readonly List<WishlistItem> _wishlistNotices = [];
    private WishlistItem? _wishlistNotice;

    [ObservableProperty] private bool _hasWishlistNotice;
    [ObservableProperty] private string _wishlistNoticeTitle = string.Empty;
    [ObservableProperty] private string? _wishlistNoticeImage;

    private void QueueWishlistNotice(WishlistItem item)
    {
        if (_wishlistNotice?.Key == item.Key || _wishlistNotices.Any(n => n.Key == item.Key)) return;
        _wishlistNotices.Add(item);
        if (_wishlistNotice is null) ShowNextWishlistNotice();
    }

    private void ShowNextWishlistNotice()
    {
        _wishlistNotice = _wishlistNotices.Count > 0 ? _wishlistNotices[0] : null;
        if (_wishlistNotice is null)
        {
            HasWishlistNotice = false;
            return;
        }
        _wishlistNotices.RemoveAt(0);
        WishlistNoticeTitle = $"{SeaRaider.Core.Catalog.DisplayText.Title(_wishlistNotice.Title)} from your wishlist is in the store";
        WishlistNoticeImage = _wishlistNotice.ImageUrl;
        HasWishlistNotice = true;
    }

    [RelayCommand]
    private void ViewWishlistGame()
    {
        if (_wishlistNotice is not { } item) return;
        _upcomingService.MarkNotified(item.Key);
        if (item.AvailableGameId is { } id) ShowGame(id);
        ShowNextWishlistNotice();
    }

    [RelayCommand]
    private void DismissWishlistNotice()
    {
        if (_wishlistNotice is { } item) _upcomingService.MarkNotified(item.Key);
        ShowNextWishlistNotice();
    }
    public NavItem SettingsItem { get; }
    public Geometry FriendsIcon { get; }

    [ObservableProperty] private PageViewModel? _currentPage;

    /// <summary>Blurred art behind the whole window ("The Next Generation" skin only).</summary>
    [ObservableProperty] private Bitmap? _backdrop;
    [ObservableProperty] private NavItem? _selectedNav;
    [ObservableProperty] private bool _canGoBack;

    // ---- verification (captcha) banner -----------------------------------------------------------

    [ObservableProperty] private bool _hasVerificationPrompt;
    [ObservableProperty] private string _verificationText = string.Empty;

    [RelayCommand]
    private void ShowVerificationWindow() => _challengeResolver.BringToFront();

    // ---- "try it, then delete the setup files" banner ------------------------------------------------
    //
    // 1. Installed:      "Hades II is installed. Try it first!"   [Keep them] [Delete now] [Play now]
    // 2. Closed again:   "Did Hades II work?"                       [Not yet] [Keep them] [It works, delete them]
    // 3. Deleting:       checked afterwards: "12.4 GB freed", or what's holding them, with [Open folder] [Try again].

    private readonly IInstallerService _installer;
    private readonly IGameLauncherService _launcher;
    private readonly List<(SetupCleanupOffer Offer, bool AfterPlaying)> _cleanupOffers = [];
    private SetupCleanupOffer? _cleanupOffer;
    private bool _cleanupAfterPlaying;
    private string? _cleanupFailedGameId;

    [ObservableProperty] private bool _hasCleanupOffer;

    /// <summary>The question is showing (its buttons).</summary>
    [ObservableProperty] private bool _cleanupAsking;

    /// <summary>"Did it work?" (after playing) rather than "try it first".</summary>
    [ObservableProperty] private bool _cleanupAskingWorked;

    /// <summary>Deleting failed: Open folder / Try again.</summary>
    [ObservableProperty] private bool _cleanupFailed;

    [ObservableProperty] private string _cleanupTitle = string.Empty;
    [ObservableProperty] private string _cleanupText = string.Empty;

    private bool _deletingSetup;

    /// <summary>Which buttons show: try first, did it work, or deleting failed.</summary>
    public bool CleanupShowTry => CleanupAsking && !CleanupAskingWorked && !CleanupFailed;
    public bool CleanupShowWorked => CleanupAsking && CleanupAskingWorked && !CleanupFailed;

    partial void OnCleanupAskingChanged(bool value) => NotifyCleanupButtons();
    partial void OnCleanupAskingWorkedChanged(bool value) => NotifyCleanupButtons();
    partial void OnCleanupFailedChanged(bool value) => NotifyCleanupButtons();

    private void NotifyCleanupButtons()
    {
        OnPropertyChanged(nameof(CleanupShowTry));
        OnPropertyChanged(nameof(CleanupShowWorked));
    }

    private void OfferCleanup(SetupCleanupOffer offer, bool afterPlaying)
    {
        if (_cleanupOffer?.GameId == offer.GameId)
        {
            // Already showing it: after playing, switch to "did it work?".
            if (afterPlaying && !CleanupFailed && !_deletingSetup) Show(offer, afterPlaying: true);
            return;
        }
        _cleanupOffers.RemoveAll(o => o.Offer.GameId == offer.GameId);
        if (afterPlaying) _cleanupOffers.Insert(0, (offer, true)); // just closed the game: this question first
        else _cleanupOffers.Add((offer, false));
        if (_cleanupOffer is null || !CleanupAsking) ShowNextCleanup();
    }

    /// <summary>Games installed earlier whose setup files are still there (the question was never answered).</summary>
    private async Task OfferLeftoverCleanupsAsync()
    {
        try
        {
            var offers = await Task.Run(() => _installer.GetPendingSetupCleanups());
            foreach (var offer in offers)
            {
                // Played since it was installed: the question is whether it worked.
                var install = _services.GetRequiredService<ILibraryRepository>().GetInstall(offer.GameId);
                var played = install?.LastPlayedAtUtc is { } last && install.InstalledAtUtc is { } installed && last > installed;
                OfferCleanup(offer, played);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            // only an offer
        }
    }

    /// <summary>The game was closed: if its setup files are still waiting, ask whether it worked.</summary>
    private async Task AskAfterPlayingAsync(string gameId)
    {
        try
        {
            var offer = (await Task.Run(() => _installer.GetPendingSetupCleanups())).FirstOrDefault(o => o.GameId == gameId);
            if (offer is not null) OfferCleanup(offer, afterPlaying: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            // only an offer
        }
    }

    private void ShowNextCleanup()
    {
        CleanupFailed = false;
        if (_cleanupOffers.Count == 0)
        {
            _cleanupOffer = null;
            HasCleanupOffer = false;
            CleanupAsking = false;
            return;
        }
        var (offer, afterPlaying) = _cleanupOffers[0];
        _cleanupOffers.RemoveAt(0);
        Show(offer, afterPlaying);
    }

    private void Show(SetupCleanupOffer offer, bool afterPlaying)
    {
        _cleanupOffer = offer;
        _cleanupAfterPlaying = afterPlaying;
        CleanupFailed = false;
        if (afterPlaying)
        {
            CleanupTitle = $"Did {offer.Title} work?";
            CleanupText = $"If it played fine, its setup files ({Formatting.Bytes(offer.Bytes)}) aren't needed any more. " +
                          "If something's wrong, keep them: reinstalling then doesn't need a new download.";
        }
        else
        {
            CleanupTitle = $"{offer.Title} is installed. Try it first!";
            CleanupText = $"Play it to make sure it works. Its setup files ({Formatting.Bytes(offer.Bytes)}) stay until you say so, " +
                          "in case it needs installing again.";
        }
        CleanupAskingWorked = afterPlaying;
        CleanupAsking = true;
        HasCleanupOffer = true;
    }

    /// <summary>"Play now": starts the game; when it closes, the banner asks whether it worked.</summary>
    [RelayCommand]
    private async Task PlayToTryAsync()
    {
        if (_cleanupOffer is not { } offer) return;
        try
        {
            await _launcher.LaunchAsync(offer.GameId);
            CleanupAsking = false;
            CleanupTitle = $"Playing {offer.Title}";
            CleanupText = "When you close it, SeaRaider asks whether it worked.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or IOException or OperationCanceledException
                                       or System.ComponentModel.Win32Exception)
        {
            CleanupTitle = $"{offer.Title} didn't start";
            CleanupText = $"{ex.Message} Keep the setup files for now: you may need to install it again.";
            CleanupAskingWorked = true; // offer Not yet / Keep them
            CleanupAsking = true;
        }
    }

    [RelayCommand]
    private async Task DeleteSetupFilesAsync()
    {
        if (_cleanupOffer is not { } offer || _deletingSetup) return;
        _deletingSetup = true;
        CleanupAsking = false;
        CleanupFailed = false;
        CleanupTitle = $"Deleting {offer.Title}'s setup files…";
        CleanupText = "Closing anything still running from them, then deleting and checking they're gone.";
        try
        {
            var freed = await Task.Run(() => _installer.DeleteSetupFilesAsync(offer.GameId));
            CleanupTitle = freed > 0 ? $"{Formatting.Bytes(freed)} freed" : "Nothing to delete";
            CleanupText = freed > 0 ? $"The setup files are gone. Enjoy {offer.Title}!" : "The setup files were already gone.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            CleanupTitle = "Couldn't delete all the setup files";
            CleanupText = ex.Message;
            CleanupFailed = true;
            _cleanupFailedGameId = offer.GameId;
            return; // stays up with Open folder / Try again
        }
        finally
        {
            _deletingSetup = false;
        }
        await Task.Delay(TimeSpan.FromSeconds(4));
        if (ReferenceEquals(_cleanupOffer, offer)) ShowNextCleanup();
    }

    [RelayCommand]
    private void OpenSetupFolder()
    {
        if (_cleanupFailedGameId is not { } id) return;
        var dir = _services.GetRequiredService<ILibraryRepository>().GetInstall(id)?.SetupDirectory;
        if (!string.IsNullOrEmpty(dir)) _services.GetRequiredService<ShellService>().OpenFolder(dir);
    }

    /// <summary>"Not yet": ask again the next time the game is closed (or SeaRaider starts).</summary>
    [RelayCommand]
    private void AskLater() => ShowNextCleanup();

    [RelayCommand]
    private void KeepSetupFiles()
    {
        if (_cleanupOffer is { } offer) _installer.KeepSetupFiles(offer.GameId);
        ShowNextCleanup();
    }

    // ---- download status bar ---------------------------------------------------------------------

    [ObservableProperty] private bool _hasActiveDownloads;
    [ObservableProperty] private string _downloadBarTitle = string.Empty;
    [ObservableProperty] private string _downloadBarDetail = string.Empty;
    [ObservableProperty] private double _downloadBarPercent;
    [ObservableProperty] private bool _downloadBarIndeterminate;

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (!_suppressNavSelection && value is not null) ShowPage(value.Page);
    }

    private void ShowPage(AppPage page)
    {
        _backStack.Clear();
        SetPage(_pages[page]);
        SyncNavSelection(page);
    }

    private void ShowGame(string gameId)
    {
        try
        {
            var detail = ActivatorUtilities.CreateInstance<GameDetailViewModel>(_services, gameId);
            if (CurrentPage is not null) _backStack.Push(CurrentPage);
            SetPage(detail);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void GoBack()
    {
        if (_backStack.Count == 0) return;
        var previous = _backStack.Pop();
        SetPage(previous);
        var page = _pages.FirstOrDefault(p => p.Value == previous).Key;
        SyncNavSelection(page);
    }

    [RelayCommand]
    private void OpenSettings() => ShowPage(AppPage.Settings);

    [RelayCommand]
    private void OpenDownloads() => ShowPage(AppPage.Downloads);

    private void SetPage(PageViewModel page)
    {
        if (!ReferenceEquals(CurrentPage, page)) CurrentPage?.OnNavigatedFrom();
        if (CurrentPage is GameDetailViewModel oldDetail && !_backStack.Contains(oldDetail)) oldDetail.Dispose();
        CurrentPage = page;
        CanGoBack = _backStack.Count > 0;
        if (page is GameDetailViewModel) WatchHero(page);
        else WatchHero((_pages[AppPage.Store] as StorefrontViewModel)?.Featured);
        UpdateBackdrop();
        page.OnNavigatedTo();
        _services.GetService<DiscordPresenceService>()?.SetPage(DiscordActivity(page));
    }

    /// <summary>What the Discord status says while nothing is playing.</summary>
    private static string DiscordActivity(PageViewModel page) => page switch
    {
        GameDetailViewModel detail => $"Looking at {detail.Title}",
        LibraryViewModel => "In their library",
        WishlistViewModel => "Checking their wishlist",
        MediaViewModel => "Browsing Media",
        DownloadsViewModel => "Watching downloads",
        SettingsViewModel => "In settings",
        _ => "Browsing the store",
    };

    private void SyncNavSelection(AppPage page)
    {
        _suppressNavSelection = true;
        SelectedNav = NavItems.FirstOrDefault(n => n.Page == page);
        _suppressNavSelection = false;
    }

    private void UpdateDownloadBar()
    {
        var active = _downloads.Jobs.Where(j => j.IsActive).ToList();
        HasActiveDownloads = active.Count > 0;
        if (active.Count == 0) return;

        // Show the job that is actually moving; others are summarised.
        var lead = active.FirstOrDefault(j => j.Status == DownloadJobStatus.Downloading)
                   ?? active.FirstOrDefault(j => j.Status is DownloadJobStatus.Extracting or DownloadJobStatus.Finalizing)
                   ?? active[0];

        var leadTitle = SeaRaider.Core.Catalog.DisplayText.Title(lead.GameTitle);
        DownloadBarTitle = active.Count > 1 ? $"{leadTitle} and {active.Count - 1} more" : leadTitle;

        if (lead.Status is DownloadJobStatus.Extracting or DownloadJobStatus.Finalizing)
        {
            DownloadBarPercent = lead.ExtractionPercent;
            DownloadBarIndeterminate = false;
            DownloadBarDetail = lead.StatusDetail;
        }
        else
        {
            DownloadBarPercent = lead.DownloadPercent ?? 0;
            DownloadBarIndeterminate = lead.DownloadPercent is null;
            var speed = active.Sum(j => j.Status == DownloadJobStatus.Downloading ? j.BytesPerSecond : 0);
            DownloadBarDetail = lead.Status == DownloadJobStatus.Queued
                ? "Waiting to start"
                : $"{lead.DownloadPercent ?? 0:0}%, {Formatting.Speed(speed)}, {Formatting.Duration(lead.Eta)} left";
        }
    }

    private System.ComponentModel.INotifyPropertyChanged? _watchedHero;

    /// <summary>The featured card finds its high-resolution art a moment later; the backdrop follows.</summary>
    private void WatchHero(System.ComponentModel.INotifyPropertyChanged? source)
    {
        if (ReferenceEquals(_watchedHero, source)) return;
        if (_watchedHero is not null) _watchedHero.PropertyChanged -= OnHeroChanged;
        _watchedHero = source;
        if (source is not null) source.PropertyChanged += OnHeroChanged;
    }

    private void OnHeroChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "HeroImageUrl") UpdateBackdrop();
    }

    private async void UpdateBackdrop()
    {
        if (_skin.Current is not (AppSkin.NextGeneration or AppSkin.Halloween)) return;
        var featured = (_pages[AppPage.Store] as StorefrontViewModel)?.Featured?.HeroImageUrl;
        var url = CurrentPage is GameDetailViewModel detail ? detail.HeroImageUrl ?? featured : featured;
        if (url is null || url == _backdropUrl) return;

        _backdropUrl = url;
        var bitmap = await _backdrops.GetAsync(url);
        if (bitmap is not null && _backdropUrl == url) Backdrop = bitmap;
    }
}

/// <summary>24×24 outline icons (path data) for the navigation rail.</summary>
internal static class Icons
{
    public const string Store = "M4 7h16l-1.2 11.1A2 2 0 0 1 16.8 20H7.2a2 2 0 0 1-2-1.9L4 7Zm4 0V6a4 4 0 0 1 8 0v1";
    public const string Library = "M4 4h4v16H4zM10 4h4v16h-4zM16.5 5.2l3.8-1 3.2 15.5-3.8.9z";
    public const string Download = "M12 3v12m0 0-5-5m5 5 5-5M4 19h16";
    public const string Link = "M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1";
    public const string Media = "M3 6.5A2.5 2.5 0 0 1 5.5 4h13A2.5 2.5 0 0 1 21 6.5v11a2.5 2.5 0 0 1-2.5 2.5h-13A2.5 2.5 0 0 1 3 17.5v-11ZM10 9v6l5-3-5-3Z";
    public const string Wishlist = "M12 20.5s-7.5-4.6-9.2-9.3C1.6 7.8 3.9 4.5 7.3 4.5c2 0 3.6 1.1 4.7 2.8 1.1-1.7 2.7-2.8 4.7-2.8 3.4 0 5.7 3.3 4.5 6.7-1.7 4.7-9.2 9.3-9.2 9.3Z";
    public const string Friends = "M9 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8Zm-7 10v-1a6 6 0 0 1 6-6h2a6 6 0 0 1 6 6v1M16 3.1a4 4 0 0 1 0 7.8M22 21v-1a6 6 0 0 0-4-5.7";
    public const string Settings = "M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6Zm7.4-3a7.4 7.4 0 0 0-.1-1.3l2-1.6-2-3.4-2.4 1a7.5 7.5 0 0 0-2.2-1.3L14.4 3h-4l-.4 2.4a7.5 7.5 0 0 0-2.2 1.3l-2.4-1-2 3.4 2 1.6a7.4 7.4 0 0 0 0 2.6l-2 1.6 2 3.4 2.4-1a7.5 7.5 0 0 0 2.2 1.3l.4 2.4h4l.4-2.4a7.5 7.5 0 0 0 2.2-1.3l2.4 1 2-3.4-2-1.6c.1-.4.1-.9.1-1.3Z";

}
