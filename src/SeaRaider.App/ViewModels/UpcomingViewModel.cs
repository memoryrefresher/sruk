using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SeaRaider.App.Services;
using SeaRaider.Core.Catalog;
using SeaRaider.Core.Models;
using SeaRaider.Core.Scraping;

namespace SeaRaider.App.ViewModels;

/// <summary>
/// The store's "Coming soon" shelf, its pop-out coming-soon page, and the upcoming sites (managed on the Sources page).
/// One instance, shared by the store, the Sources page and the main window (which shows the pop-out).
/// </summary>
public sealed partial class UpcomingViewModel : ViewModelBase
{
    private readonly UpcomingService _upcoming;
    private readonly ShellService _shell;
    private readonly NavigationService _navigation;
    private readonly Dictionary<string, UpcomingCardViewModel> _byKey = new(StringComparer.Ordinal);

    public UpcomingViewModel(UpcomingService upcoming, ShellService shell, NavigationService navigation)
    {
        _upcoming = upcoming;
        _shell = shell;
        _navigation = navigation;

        upcoming.Changed += (_, _) => Dispatcher.UIThread.Post(Reload);
        upcoming.WishlistChanged += (_, _) => Dispatcher.UIThread.Post(RefreshWishlistState);
        upcoming.ReleaseUpdated += (_, release) => Dispatcher.UIThread.Post(() =>
        {
            if (_byKey.TryGetValue(release.Key, out var card)) card.Update(release);
        });
        Reload();
    }

    public ObservableCollection<UpcomingCardViewModel> Releases { get; } = [];
    public ObservableCollection<UpcomingSourceRow> Sources { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowShelf), nameof(ShowEmptyShelf))]
    private bool _hasReleases;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSetup), nameof(ShowEmptyShelf))]
    private bool _hasSources;

    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private int _wishlistCount;
    [ObservableProperty] private string _newSourceUrl = string.Empty;
    [ObservableProperty] private bool _isAdding;

    /// <summary>Adding a custom list: a page of game names, read between two bits of HTML, with details from Steam.</summary>
    [ObservableProperty] private bool _newIsCustom;
    [ObservableProperty] private string _newStartHtml = string.Empty;
    [ObservableProperty] private string _newEndHtml = string.Empty;

    // ---- "Teach it by example": the user types a game that is on the page now; SeaRaider proposes where the list is ----

    /// <summary>A game that is listed on the page right now (the user types it).</summary>
    [ObservableProperty] private string _sampleGame = string.Empty;
    [ObservableProperty] private bool _isFinding;

    /// <summary>One guess is on screen, waiting for the user's yes or no.</summary>
    [ObservableProperty] private bool _hasProposal;
    [ObservableProperty] private string _proposalTitle = string.Empty;
    [ObservableProperty] private string _proposalStart = string.Empty;
    [ObservableProperty] private string _proposalEnd = string.Empty;
    [ObservableProperty] private string _proposalHint = string.Empty;

    /// <summary>Why no (more) guesses are on screen: the answer to the last "Find it" or "No".</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasFindNote))] private string _findNote = string.Empty;

    /// <summary>The games the current guess would read, as they'd appear on the shelf.</summary>
    public ObservableCollection<string> ProposalNames { get; } = [];

    public bool HasFindNote => FindNote.Length > 0;

    private const int NamesShown = 40;
    private IReadOnlyList<FeedRegionCandidate> _proposals = [];
    private int _proposalIndex;
    private string _proposalsUrl = string.Empty;

    /// <summary>The coming-soon page shown in the pop-out, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPopoutOpen))]
    private UpcomingCardViewModel? _selected;

    public bool IsPopoutOpen => Selected is not null;
    public bool ShowShelf => HasReleases;
    public bool ShowSetup => !HasSources;
    public bool ShowEmptyShelf => HasSources && !HasReleases;
    public string WishlistButtonText => WishlistCount > 0 ? $"Wishlist ({WishlistCount})" : "Wishlist";

    partial void OnWishlistCountChanged(int value) => OnPropertyChanged(nameof(WishlistButtonText));

    private void Reload()
    {
        Sources.Clear();
        foreach (var source in _upcoming.GetSources()) Sources.Add(new UpcomingSourceRow(source, RemoveSource));
        HasSources = Sources.Count > 0;

        var names = Sources.ToDictionary(s => s.Id, s => s.Name);
        var releases = _upcoming.GetReleases();
        _byKey.Clear();
        Releases.Clear();
        foreach (var release in releases)
        {
            var card = new UpcomingCardViewModel(this, release, names.GetValueOrDefault(release.SourceId, string.Empty));
            _byKey[release.Key] = card;
            Releases.Add(card);
        }
        HasReleases = Releases.Count > 0;
        RefreshWishlistState();

        // The pop-out follows a refresh (same game, fresh data), or closes if the game left the list.
        if (Selected is { } selected) Selected = _byKey.GetValueOrDefault(selected.Key);
    }

    private void RefreshWishlistState()
    {
        var wishlist = _upcoming.GetWishlist();
        var keys = wishlist.Select(w => w.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var card in Releases) card.IsWishlisted = keys.Contains(card.Key);
        WishlistCount = wishlist.Count;
    }

    internal void ToggleWishlist(UpcomingCardViewModel card) => RunSafe(() =>
    {
        if (card.IsWishlisted) _upcoming.RemoveFromWishlist(card.Key);
        else _upcoming.AddToWishlist(card.Release);
        card.IsWishlisted = !card.IsWishlisted;
    });

    internal void OpenPage(string? url) => RunSafe(() =>
    {
        if (!string.IsNullOrEmpty(url)) _shell.OpenUrl(url);
    });

    internal void Select(UpcomingCardViewModel card) => Selected = card;

    [RelayCommand]
    private void ClosePopout() => Selected = null;

    [RelayCommand]
    internal void OpenWishlist()
    {
        Selected = null;
        _navigation.ShowPage(AppPage.Wishlist);
    }

    [RelayCommand]
    private void OpenSources()
    {
        Selected = null;
        _navigation.ShowSettings(SettingsTab.Sources);
    }

    [RelayCommand]
    private Task RefreshAsync() => RunSafeAsync(async () =>
    {
        IsRefreshing = true;
        try { await Task.Run(() => _upcoming.RefreshAllAsync()); }
        finally { IsRefreshing = false; }
    });

    [RelayCommand]
    private Task AddSourceAsync() => RunSafeAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(NewSourceUrl)) throw new ArgumentException("Paste the address of a page that lists upcoming games.");
        if (NewIsCustom && string.IsNullOrWhiteSpace(NewStartHtml))
            throw new ArgumentException("Paste the HTML where the list starts, so SeaRaider knows where to begin reading.");
        IsAdding = true;
        try
        {
            var url = NewSourceUrl;
            var (start, end) = NewIsCustom ? (NewStartHtml, NewEndHtml) : (null, null);
            await Task.Run(() => _upcoming.AddSourceAsync(url, start, end, default, replaceCustomOnSameUrl: true));
            NewSourceUrl = string.Empty;
            NewStartHtml = string.Empty;
            NewEndHtml = string.Empty;
            NewIsCustom = false;
            ClearProposals();
        }
        finally
        {
            IsAdding = false;
        }
    });

    /// <summary>"Find it": reads the page and shows the first guess at where the list of games starts and ends.</summary>
    [RelayCommand]
    private Task FindListAsync() => RunSafeAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(NewSourceUrl)) throw new ArgumentException("Paste the address of the page first.");
        if (string.IsNullOrWhiteSpace(SampleGame)) throw new ArgumentException("Type the name of a game that is on that page right now.");
        ClearProposals();
        IsFinding = true;
        try
        {
            var (url, game) = (NewSourceUrl, SampleGame);
            _proposals = await Task.Run(() => _upcoming.FindListRegionAsync(url, game));
            _proposalsUrl = url;
            _proposalIndex = 0;
            ShowProposal();
        }
        finally
        {
            IsFinding = false;
        }
    });

    /// <summary>"Yes, that's the list": keeps it. SeaRaider reads it now and again every few hours by itself.</summary>
    [RelayCommand]
    private Task AcceptProposalAsync() => RunSafeAsync(async () =>
    {
        if (!HasProposal) return;
        var guess = _proposals[_proposalIndex];
        var (url, start, end) = (_proposalsUrl, guess.StartHtml, guess.EndHtml);
        IsAdding = true;
        try
        {
            await Task.Run(() => _upcoming.AddSourceAsync(url, start, end, default, replaceCustomOnSameUrl: true));
            NewSourceUrl = string.Empty;
            NewStartHtml = string.Empty;
            NewEndHtml = string.Empty;
            SampleGame = string.Empty;
            NewIsCustom = false;
            ClearProposals();
            FindNote = "Saved. Its games will show under Coming soon, and SeaRaider checks the page again by itself every few hours.";
        }
        finally
        {
            IsAdding = false;
        }
    });

    /// <summary>"No, that's wrong": shows the next guess.</summary>
    [RelayCommand]
    private void RejectProposal()
    {
        _proposalIndex++;
        if (_proposalIndex < _proposals.Count)
        {
            ShowProposal();
            return;
        }
        ClearProposals();
        FindNote = "That was the last guess. Try a different game from the list, or use \"Let me adjust it\" and fix the HTML by hand.";
    }

    /// <summary>"Let me adjust it": moves the guess into the two HTML boxes to edit.</summary>
    [RelayCommand]
    private void TweakProposal()
    {
        if (!HasProposal) return;
        var guess = _proposals[_proposalIndex];
        NewStartHtml = guess.StartHtml;
        NewEndHtml = guess.EndHtml;
        NewSourceUrl = _proposalsUrl;
        ClearProposals();
        FindNote = "Edit the start and end HTML below, then press Add upcoming site.";
    }

    private void ShowProposal()
    {
        var guess = _proposals[_proposalIndex];
        ProposalStart = guess.StartHtml;
        ProposalEnd = guess.EndHtml.Length > 0 ? guess.EndHtml : "(the end of the page)";
        ProposalNames.Clear();
        foreach (var name in guess.Names.Take(NamesShown)) ProposalNames.Add(name);
        ProposalTitle = $"Guess {_proposalIndex + 1} of {_proposals.Count}: this would read {guess.Names.Count} game{(guess.Names.Count == 1 ? "" : "s")}";
        ProposalHint = guess.Names.Count > NamesShown ? $"…and {guess.Names.Count - NamesShown} more" : string.Empty;
        FindNote = string.Empty;
        HasProposal = true;
    }

    private void ClearProposals()
    {
        HasProposal = false;
        ProposalNames.Clear();
        ProposalHint = string.Empty;
        FindNote = string.Empty;
        _proposals = [];
        _proposalIndex = 0;
    }

    private void RemoveSource(UpcomingSourceRow row) => RunSafe(() => _upcoming.RemoveSource(row.Id));
}

/// <summary>One upcoming game: a tile on the shelf and the content of its coming-soon page.</summary>
public sealed partial class UpcomingCardViewModel : ObservableObject
{
    private readonly UpcomingViewModel _owner;

    public UpcomingCardViewModel(UpcomingViewModel owner, UpcomingRelease release, string sourceName)
    {
        _owner = owner;
        Release = release;
        SourceName = sourceName;
        Update(release);
    }

    public UpcomingRelease Release { get; private set; }
    public string Key => Release.Key;
    public string SourceName { get; }

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string? _tileImageUrl;
    [ObservableProperty] private string? _heroImageUrl;
    [ObservableProperty] private string _releaseText = "Coming soon";
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string? _developer;
    [ObservableProperty] private IReadOnlyList<string> _tags = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WishlistLabel))]
    private bool _isWishlisted;

    public bool HasImage => !string.IsNullOrEmpty(TileImageUrl);
    public bool HasDescription => Description.Length > 0;
    public bool HasDeveloper => !string.IsNullOrEmpty(Developer);
    public bool HasTags => Tags.Count > 0;
    public bool HasPage => !string.IsNullOrEmpty(Release.PageUrl);
    public string Monogram => string.Concat(Title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));
    public string TitleUpper => Title.ToUpperInvariant();
    public string WishlistLabel => IsWishlisted ? "On your wishlist" : "Add to wishlist";
    public string SourceText => SourceName.Length > 0 ? $"Listed on {SourceName}" : string.Empty;

    public void Update(UpcomingRelease release)
    {
        Release = release;
        Title = DisplayText.Title(CustomFeedParser.TidyTitle(release.Title)); // lists from before the arrows were cleaned up
        TileImageUrl = release.ImageUrl ?? release.HeroUrl;
        HeroImageUrl = release.HeroUrl ?? release.ImageUrl;
        ReleaseText = string.IsNullOrWhiteSpace(release.ReleaseText) ? "Coming soon" : release.ReleaseText!;
        Description = release.Description ?? string.Empty;
        Developer = release.Developer;
        Tags = release.Tags;
        foreach (var name in new[] { nameof(HasImage), nameof(HasDescription), nameof(HasDeveloper), nameof(HasTags), nameof(HasPage),
                     nameof(Monogram), nameof(TitleUpper) })
            OnPropertyChanged(name);
    }

    [RelayCommand]
    private void ToggleWishlist() => _owner.ToggleWishlist(this);

    /// <summary>Opens this game's coming-soon page (the pop-out).</summary>
    [RelayCommand]
    private void Open() => _owner.Select(this);

    [RelayCommand]
    private void OpenWishlist() => _owner.OpenWishlist();

    [RelayCommand]
    private void OpenPage() => _owner.OpenPage(Release.PageUrl);
}

/// <summary>An upcoming site in the list on the Sources page.</summary>
public sealed partial class UpcomingSourceRow(UpcomingSource source, Action<UpcomingSourceRow> remove) : ObservableObject
{
    [RelayCommand]
    private void Remove() => remove(this);

    public Guid Id { get; } = source.Id;
    public string Name { get; } = source.IsCustom ? source.Name + " (custom list, details from Steam)" : source.Name;
    public string Url { get; } = source.Url;
    public bool HasError => !string.IsNullOrEmpty(source.LastError);
    public string? Error { get; } = source.LastError;

    public string StatusText => source.LastFetchedUtc is { } fetched
        ? $"{source.LastCount} games, checked {fetched.ToLocalTime():g}"
        : "Reading the page…";
}

/// <summary>The Wishlist page: games the user is waiting for, and which of them can be downloaded now.</summary>
public sealed partial class WishlistViewModel : PageViewModel
{
    private readonly UpcomingService _upcoming;
    private readonly NavigationService _navigation;
    private readonly ShellService _shell;

    public WishlistViewModel(UpcomingService upcoming, NavigationService navigation, ShellService shell)
    {
        _upcoming = upcoming;
        _navigation = navigation;
        _shell = shell;
        upcoming.WishlistChanged += (_, _) => Dispatcher.UIThread.Post(Reload);
        Reload();
    }

    public ObservableCollection<WishlistRowViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private int _count;

    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private bool _isChecking;

    public bool IsEmpty => Count == 0;

    public override void OnNavigatedTo() => Reload();

    private void Reload()
    {
        Items.Clear();
        foreach (var item in _upcoming.GetWishlist().OrderByDescending(w => w.IsAvailable).ThenByDescending(w => w.AddedUtc))
            Items.Add(new WishlistRowViewModel(this, item));
        Count = Items.Count;
        var available = Items.Count(i => i.IsAvailable);
        Summary = Count == 0
            ? string.Empty
            : available > 0
                ? $"{Count} games, {available} ready to download"
                : $"{Count} games. You'll get a notice here the moment one shows up in the store.";
    }

    internal void ViewInStore(WishlistRowViewModel row) => RunSafe(() =>
    {
        if (row.GameId is not { } id) return;
        _upcoming.MarkNotified(row.Key);
        _navigation.ShowGame(id);
    });

    internal void Remove(WishlistRowViewModel row) => RunSafe(() => _upcoming.RemoveFromWishlist(row.Key));

    internal void OpenPage(string? url) => RunSafe(() =>
    {
        if (!string.IsNullOrEmpty(url)) _shell.OpenUrl(url);
    });

    [RelayCommand]
    private Task CheckNowAsync() => RunSafeAsync(async () =>
    {
        IsChecking = true;
        try { await Task.Run(() => _upcoming.SearchWishlistAsync(force: true)); }
        finally { IsChecking = false; }
        Reload();
    });

    [RelayCommand]
    private void BrowseComingSoon() => _navigation.ShowPage(AppPage.Store);
}

public sealed partial class WishlistRowViewModel(WishlistViewModel owner, WishlistItem item) : ObservableObject
{
    public string Key => item.Key;
    public string Title => DisplayText.Title(item.Title);
    public string? ImageUrl => item.ImageUrl;
    public bool HasImage => !string.IsNullOrEmpty(item.ImageUrl);
    public string Monogram => string.Concat(Title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));
    public bool IsAvailable => item.IsAvailable;
    public bool IsWaiting => !item.IsAvailable;
    public string? GameId => item.AvailableGameId;
    public bool HasPage => !string.IsNullOrEmpty(item.PageUrl);

    public string StatusText => item.IsAvailable
        ? $"In the store since {item.AvailableSinceUtc?.ToLocalTime():d}"
        : $"{(string.IsNullOrWhiteSpace(item.ReleaseText) ? "Coming soon" : item.ReleaseText)} · wishlisted {item.AddedUtc.ToLocalTime():d}";

    [RelayCommand]
    private void ViewInStore() => owner.ViewInStore(this);

    [RelayCommand]
    private void Remove() => owner.Remove(this);

    [RelayCommand]
    private void OpenPage() => owner.OpenPage(item.PageUrl);
}
