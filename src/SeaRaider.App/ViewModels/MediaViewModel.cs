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
/// The Media tab: game trailers, videos, streams and sites the user adds. <b>Browse</b> lists everything from every source
/// the way the store lists games (search, a chip per source, a grid of titles); <b>Channels &amp; sites</b> manages the
/// sources, grouped by source. A box opens in SeaRaider's own player (the trailer pop-out over the blurred window); a title
/// from a site's list has its page read for its player first.
/// </summary>
public sealed partial class MediaViewModel : PageViewModel
{
    private readonly MediaService _media;
    private readonly TrailerPlayerService _player;
    private readonly ShellService _shell;
    private readonly DispatcherTimer _reloadTimer;
    private readonly HashSet<Guid> _expanded = [];
    private bool _onScreen;

    public MediaViewModel(MediaService media, TrailerPlayerService player, ShellService shell, SharedMediaViewModel shared)
    {
        _media = media;
        _player = player;
        _shell = shell;
        Shared = shared;

        // Refreshes arrive in bursts (several sources at once): redraw once things settle.
        _reloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _reloadTimer.Tick += (_, _) =>
        {
            _reloadTimer.Stop();
            Reload();
        };
        media.Changed += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            _reloadTimer.Stop();
            _reloadTimer.Start();
        });
        Reload();
    }

    public ObservableCollection<MediaSectionViewModel> Sections { get; } = [];

    /// <summary>The "Shared" feed: links friends shared, with reactions and comments.</summary>
    public SharedMediaViewModel Shared { get; }

    private const int BrowseTab = 0, ChannelsTab = 1, SharedTab = 2;

    /// <summary>Which part of the tab shows: Browse (everything, like the store), the sources, or what friends shared.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBrowseTab), nameof(IsChannelsTab), nameof(IsSharedTab))]
    private int _tab = BrowseTab;

    public bool IsBrowseTab => Tab == BrowseTab;
    public bool IsChannelsTab => Tab == ChannelsTab;

    public bool IsSharedTab
    {
        get => Tab == SharedTab;
        set => Tab = value ? SharedTab : ChannelsTab;
    }

    partial void OnTabChanged(int value) => Shared.SetOnScreen(value == SharedTab && _onScreen);

    [RelayCommand]
    private void ShowBrowse() => Tab = BrowseTab;

    [RelayCommand]
    private void ShowChannels() => Tab = ChannelsTab;

    [RelayCommand]
    private void ShowShared() => Tab = SharedTab;

    // ---- Browse: every title and video from every source, like the store ----------------------------------------

    /// <summary>Titles from sites' lists (posters), filtered by <see cref="BrowseSearch"/> and the chosen source and type.</summary>
    public BulkObservableCollection<MediaTileViewModel> BrowseTitles { get; } = new();

    /// <summary>The same titles sorted by type ("Football", "Movies", "TV Shows" …), one heading and grid each.</summary>
    public ObservableCollection<MediaTypeGroupViewModel> BrowseGroups { get; } = [];

    /// <summary>"All types" plus one chip per type the titles have.</summary>
    public ObservableCollection<MediaSourceChipViewModel> TypeChips { get; } = [];

    public bool HasTypeChips => TypeChips.Count > 2; // "All types" and one type: nothing to choose

    private string? _chosenType;

    /// <summary>A title's type: the site's section or sport it was found under, else "Other".</summary>
    internal const string OtherType = "Other";
    internal static string TypeOf(MediaTileViewModel tile) => string.IsNullOrWhiteSpace(tile.Category) ? OtherType : tile.Category!;

    internal void ChooseType(string? type)
    {
        _chosenType = type;
        foreach (var chip in TypeChips) chip.IsSelected = chip.Type == type;
        FilterBrowse();
    }

    /// <summary>Videos and streams from channels, feeds and pages, filtered the same way.</summary>
    public BulkObservableCollection<MediaTileViewModel> BrowseVideos { get; } = new();

    /// <summary>"All" plus one chip per source that has something.</summary>
    public ObservableCollection<MediaSourceChipViewModel> SourceChips { get; } = [];

    private List<MediaTileViewModel> _allTiles = [];
    private Guid? _chosenSource;

    /// <summary>Searches every source's titles and videos by name or channel (Browse).</summary>
    [ObservableProperty] private string _browseSearch = string.Empty;

    [ObservableProperty] private int _browseCount;

    [ObservableProperty] private string _browseResultsText = string.Empty;

    public bool HasBrowseTitles => BrowseTitles.Count > 0;
    public bool HasBrowseVideos => BrowseVideos.Count > 0;
    public bool ShowBothHeadings => HasBrowseTitles && HasBrowseVideos;
    public bool IsBrowseNoResults => BrowseCount == 0 && _allTiles.Count > 0;
    public bool HasAnything => _allTiles.Count > 0;

    partial void OnBrowseSearchChanged(string value) => FilterBrowse();

    [RelayCommand]
    private void ClearBrowseSearch() => BrowseSearch = string.Empty;

    internal void ChooseSource(Guid? sourceId)
    {
        _chosenSource = sourceId;
        foreach (var chip in SourceChips) chip.IsSelected = chip.SourceId == sourceId;
        FilterBrowse();
    }

    /// <summary>Rebuilds the Browse grid's tiles and chips from every source (after a refresh).</summary>
    private void ReloadBrowse()
    {
        var sources = _media.GetSources();
        var tiles = new List<MediaTileViewModel>();
        var chips = new List<MediaSourceChipViewModel>();
        foreach (var source in sources)
        {
            var items = _media.GetItems(source.Id).OrderByDescending(i => i.IsLive).ThenBy(i => i.Order).ToList();
            if (items.Count == 0) continue;
            tiles.AddRange(items.Select(i => new MediaTileViewModel(this, i, source.Name)));
            chips.Add(new MediaSourceChipViewModel(this, source.Id, source.Name, items.Count));
        }
        _allTiles = tiles;
        if (_chosenSource is { } chosen && chips.All(c => c.SourceId != chosen)) _chosenSource = null;

        SourceChips.Clear();
        if (chips.Count > 1) SourceChips.Add(new MediaSourceChipViewModel(this, null, "All", tiles.Count));
        foreach (var chip in chips) SourceChips.Add(chip);
        foreach (var chip in SourceChips) chip.IsSelected = chip.SourceId == _chosenSource;
        OnPropertyChanged(nameof(HasAnything));
        FilterBrowse();
    }

    /// <summary>The type chips for what the chosen source has (most titles first, "Other" last).</summary>
    private void ReloadTypeChips(IReadOnlyCollection<MediaTileViewModel> titlesOfSource)
    {
        var types = titlesOfSource.GroupBy(TypeOf, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key == OtherType).ThenByDescending(g => g.Count()).ThenBy(g => g.Key)
            .Select(g => (Name: g.First().Category is { Length: > 0 } c ? c : OtherType, Count: g.Count()))
            .ToList();
        if (_chosenType is not null && !types.Any(t => t.Name.Equals(_chosenType, StringComparison.OrdinalIgnoreCase))) _chosenType = null;
        TypeChips.Clear();
        TypeChips.Add(new MediaSourceChipViewModel(this, null, "All types", titlesOfSource.Count) { Type = null, IsTypeChip = true });
        foreach (var (name, count) in types)
            TypeChips.Add(new MediaSourceChipViewModel(this, null, name, count) { Type = name, IsTypeChip = true });
        foreach (var chip in TypeChips) chip.IsSelected = string.Equals(chip.Type, _chosenType, StringComparison.OrdinalIgnoreCase);
        OnPropertyChanged(nameof(HasTypeChips));
    }

    private void FilterBrowse()
    {
        var words = BrowseSearch.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ofSource = _allTiles.Where(t => _chosenSource is null || t.SourceId == _chosenSource).ToList();
        ReloadTypeChips(ofSource.Where(t => t.NeedsResolve).ToList());
        // A type chosen: only titles of that type (videos have no type).
        var shown = ofSource.Where(t => (_chosenType is null || (t.NeedsResolve && string.Equals(TypeOf(t), _chosenType, StringComparison.OrdinalIgnoreCase))) &&
                                        words.All(w => t.Title.Contains(w, StringComparison.CurrentCultureIgnoreCase) ||
                                                       t.SourceName.Contains(w, StringComparison.CurrentCultureIgnoreCase) ||
                                                       (t.Category?.Contains(w, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
                                                       (t.Channel?.Contains(w, StringComparison.CurrentCultureIgnoreCase) ?? false)))
            .ToList();
        BrowseTitles.ResetWith(shown.Where(t => t.NeedsResolve));
        BrowseVideos.ResetWith(shown.Where(t => !t.NeedsResolve));

        // Sorted by type: live first inside each, the biggest types first, "Other" last.
        BrowseGroups.Clear();
        foreach (var group in BrowseTitles.GroupBy(TypeOf, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key == OtherType).ThenByDescending(g => g.Count()).ThenBy(g => g.Key))
            BrowseGroups.Add(new MediaTypeGroupViewModel(group.First().Category is { Length: > 0 } c ? c : OtherType,
                group.OrderByDescending(t => t.IsLive).ThenBy(t => t.Order)));
        BrowseCount = shown.Count;
        // The count can stay the same while what's shown changes (another source chosen): say so either way.
        OnPropertyChanged(nameof(HasBrowseTitles));
        OnPropertyChanged(nameof(HasBrowseVideos));
        OnPropertyChanged(nameof(ShowBothHeadings));
        OnPropertyChanged(nameof(IsBrowseNoResults));
        var titles = BrowseTitles.Count;
        var videos = BrowseVideos.Count;
        BrowseResultsText = string.Join(" · ", new[]
        {
            titles > 0 ? $"{titles:N0} {(titles == 1 ? "title" : "titles")}" : null,
            videos > 0 ? $"{videos:N0} {(videos == 1 ? "video" : "videos")}" : null,
        }.Where(s => s is not null));
    }

    /// <summary>Share on a video box: the composer on "Shared", filled in with the video.</summary>
    internal void ShareTile(MediaTileViewModel tile)
    {
        // The video itself (a box's page can be the site it was found on); a title from a site's list is its page, which
        // friends' SeaRaider reads for the player when they press play.
        Shared.Prefill(tile.PlayUrl, tile.Title, tile.ShareImageUrl);
        IsSharedTab = true;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(HasSections))]
    private int _sectionCount;

    [ObservableProperty] private string _newSourceUrl = string.Empty;
    [ObservableProperty] private string _newSourceName = string.Empty;
    [ObservableProperty] private bool _isAdding;
    [ObservableProperty] private bool _isRefreshing;

    /// <summary>Filters every channel's boxes by title or channel name.</summary>
    [ObservableProperty] private string _searchText = string.Empty;

    public bool IsEmpty => SectionCount == 0;
    public bool HasSections => SectionCount > 0;

    partial void OnSearchTextChanged(string value) => ReloadSections();

    public override void OnNavigatedTo()
    {
        _onScreen = true;
        Shared.SetOnScreen(IsSharedTab);
        Reload();
    }

    public override void OnNavigatedFrom()
    {
        _onScreen = false;
        Shared.SetOnScreen(false);
    }

    private void Reload()
    {
        ReloadSections();
        ReloadBrowse();
    }

    private void ReloadSections()
    {
        var filter = SearchText.Trim();
        Sections.Clear();
        foreach (var source in _media.GetSources())
        {
            var items = _media.GetItems(source.Id)
                .Where(i => filter.Length == 0 || i.Title.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
                            (i.Channel?.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ?? false))
                .OrderByDescending(i => i.IsLive)
                .ThenBy(i => i.Order)
                .ToList();
            if (filter.Length > 0 && items.Count == 0) continue;
            Sections.Add(new MediaSectionViewModel(this, source, items, _expanded.Contains(source.Id) || filter.Length > 0));
        }
        SectionCount = Sections.Count;
    }

    /// <summary>Plays a box. A title from a site's list has its page read for its player first (the box shows it's working).</summary>
    internal Task PlayAsync(MediaTileViewModel tile) => RunSafeAsync(async () =>
    {
        var play = tile.PlayUrl;
        if (tile.NeedsResolve)
        {
            if (tile.IsResolving) return;
            tile.IsResolving = true;
            try { play = await Task.Run(() => _media.ResolvePlayUrlAsync(tile.PlayUrl)); }
            finally { tile.IsResolving = false; }
        }
        _player.Play(play, tile.PageUrl, tile.Title, thumbnailUrl: tile.ThumbnailUrl);
    });

    internal void OpenPage(string? url) => RunSafe(() =>
    {
        if (!string.IsNullOrEmpty(url)) _shell.OpenUrl(url);
    });

    internal void Remove(MediaSectionViewModel section) => RunSafe(() => _media.RemoveSource(section.Id));

    internal void SetExpanded(Guid id, bool expanded)
    {
        if (expanded) _expanded.Add(id);
        else _expanded.Remove(id);
    }

    internal Task RefreshOneAsync(MediaSectionViewModel section) => RunSafeAsync(async () =>
    {
        if (_media.GetSources().FirstOrDefault(s => s.Id == section.Id) is { } source)
            await Task.Run(() => _media.RefreshAsync(source));
    });

    [RelayCommand]
    private Task AddSourceAsync() => RunSafeAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(NewSourceUrl))
            throw new ArgumentException("Paste a site that lists movies or shows, a stream or video address, a YouTube channel or playlist, a Twitch channel, a video RSS feed, or a page with videos.");
        IsAdding = true;
        try
        {
            var url = NewSourceUrl;
            var name = string.IsNullOrWhiteSpace(NewSourceName) ? null : NewSourceName.Trim();
            await Task.Run(() => _media.AddSourceAsync(url, default, name));
            NewSourceUrl = string.Empty;
            NewSourceName = string.Empty;
        }
        finally
        {
            IsAdding = false;
        }
    });

    [RelayCommand]
    private Task RefreshAsync() => RunSafeAsync(async () =>
    {
        IsRefreshing = true;
        try { await Task.Run(() => _media.RefreshAllAsync()); }
        finally { IsRefreshing = false; }
    });

    /// <summary>Friendly "3 days ago".</summary>
    internal static string Age(DateTime? utc)
    {
        if (utc is not { } when) return string.Empty;
        var ago = DateTime.UtcNow - when;
        return ago.TotalMinutes < 60 ? $"{Math.Max(1, (int)ago.TotalMinutes)} min ago"
            : ago.TotalHours < 24 ? $"{(int)ago.TotalHours} h ago"
            : ago.TotalDays < 2 ? "yesterday"
            : ago.TotalDays < 30 ? $"{(int)ago.TotalDays} days ago"
            : ago.TotalDays < 365 ? $"{(int)(ago.TotalDays / 30)} months ago"
            : when.ToLocalTime().ToString("MMM yyyy");
    }
}

/// <summary>One channel / feed / page: its header and boxes.</summary>
public sealed partial class MediaSectionViewModel : ObservableObject
{
    private const int CollapsedCount = 8;
    private readonly MediaViewModel _owner;
    private readonly List<MediaTileViewModel> _all;

    public MediaSectionViewModel(MediaViewModel owner, MediaSource source, IReadOnlyList<MediaItem> items, bool expanded)
    {
        _owner = owner;
        Id = source.Id;
        Name = source.Name;
        Url = source.Url;
        AvatarUrl = source.AvatarUrl;
        Error = source.LastError;
        KindText = source.Kind switch
        {
            MediaSourceKind.YouTubeChannel => "YouTube channel",
            MediaSourceKind.YouTubePlaylist => "YouTube playlist",
            MediaSourceKind.Twitch => "Twitch",
            MediaSourceKind.Feed => "Feed",
            MediaSourceKind.Stream => "Stream",
            MediaSourceKind.Catalog => "Site",
            _ => "Web page",
        };
        var noun = source.Kind == MediaSourceKind.Catalog ? (items.Count == 1 ? "title" : "titles") : (items.Count == 1 ? "video" : "videos");
        StatusText = source.LastFetchedUtc is null
            ? "Loading…"
            : $"{items.Count} {noun} · checked {MediaViewModel.Age(source.LastFetchedUtc)}";
        _all = items.Select(i => new MediaTileViewModel(owner, i, source.Name)).ToList();
        _isExpanded = expanded;
        Fill();
    }

    public Guid Id { get; }
    public string Name { get; }
    public string Url { get; }
    public string? AvatarUrl { get; }
    public bool HasAvatar => !string.IsNullOrEmpty(AvatarUrl);
    public string Monogram => Name.Length > 0 ? char.ToUpperInvariant(Name[0]).ToString() : "?";
    public string KindText { get; }
    public string StatusText { get; }
    public string? Error { get; }
    public bool HasError => !string.IsNullOrEmpty(Error);

    public ObservableCollection<MediaTileViewModel> Tiles { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandText))]
    private bool _isExpanded;

    public bool CanExpand => _all.Count > CollapsedCount;
    public string ExpandText => IsExpanded ? "Show less" : $"Show all {_all.Count}";

    private void Fill()
    {
        Tiles.Clear();
        foreach (var tile in IsExpanded ? _all : _all.Take(CollapsedCount)) Tiles.Add(tile);
    }

    [RelayCommand]
    private void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
        _owner.SetExpanded(Id, IsExpanded);
        Fill();
    }

    [RelayCommand]
    private void Remove() => _owner.Remove(this);

    [RelayCommand]
    private Task RefreshAsync() => _owner.RefreshOneAsync(this);

    [RelayCommand]
    private void OpenSite() => _owner.OpenPage(Url);
}

/// <summary>One video, stream or title box.</summary>
public sealed partial class MediaTileViewModel(MediaViewModel owner, MediaItem item, string sourceName) : ObservableObject, IChatAttachmentSource
{
    /// <summary>What goes in a chat message (dragged into the chat, or "Send to a friend…"): plays in the friend's player.</summary>
    public ChatAttachment ToChatAttachment() => ChatAttachment.ForMedia(new Core.Social.SharedMediaLink
    {
        Url = item.PlayUrl,
        Title = item.Title,
        ThumbnailUrl = item.ArtworkUrl ?? item.ThumbnailUrl,
        ThumbnailUrl2 = item.IsMatchup ? item.ArtworkUrl2 : null,
        Detail = Detail,
        IsMatchup = item.IsMatchup,
        IsLive = item.IsLive,
    });

    [RelayCommand]
    private void SendToFriend() => ChatAttachments.RequestAttach(this);

    public Guid SourceId => item.SourceId;
    public string SourceName { get; } = sourceName;
    public string? Channel => item.Channel;
    public string? Category => item.Category;
    public int Order => item.Order;

    /// <summary>A title from a site's list: its page is read for the player when it's played.</summary>
    public bool NeedsResolve => item.NeedsResolve;

    /// <summary>Its page is being read for the player.</summary>
    [ObservableProperty] private bool _isResolving;

    public string Title => item.Title;

    /// <summary>The picture for a title's poster card: the one found for it (poster), else the site's own.</summary>
    public string? PosterUrl => item.IsMatchup ? null : item.ArtworkUrl ?? item.ThumbnailUrl;
    public bool HasPoster => !string.IsNullOrEmpty(PosterUrl);

    /// <summary>The picture shared with friends: the poster or home crest found for it, else the site's.</summary>
    public string? ShareImageUrl => item.ArtworkUrl ?? item.ThumbnailUrl;

    /// <summary>A wide video picture fills its box; a tall poster shows whole (over a blurred copy).</summary>
    public bool FillPicture => !item.NeedsResolve;

    /// <summary>A match: the two sides' crests, "vs" between.</summary>
    public bool IsMatchup => item.IsMatchup;
    public string? HomeBadgeUrl => item.ArtworkUrl;
    public string? AwayBadgeUrl => item.ArtworkUrl2;
    public bool HasHomeBadge => !string.IsNullOrEmpty(item.ArtworkUrl);
    public bool HasAwayBadge => !string.IsNullOrEmpty(item.ArtworkUrl2);
    public string HomeMonogram => Initials((MediaTitleParser.Matchup(item.Title)?.Home) ?? item.Title);
    public string AwayMonogram => Initials((MediaTitleParser.Matchup(item.Title)?.Away) ?? string.Empty);

    /// <summary>Under a title's name: its sport and the site ("Football · streamed.pk").</summary>
    public string TitleDetail => string.Join(" · ", new[] { item.Category, SourceName }.Where(t => !string.IsNullOrEmpty(t)));

    private static string Initials(string name) =>
        string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsLetterOrDigit(w[0])).Take(2).Select(w => char.ToUpperInvariant(w[0])));
    public string PlayUrl => item.PlayUrl;
    public string? PageUrl => item.PageUrl;
    public string? ThumbnailUrl => item.ThumbnailUrl;
    public bool HasThumbnail => !string.IsNullOrEmpty(item.ThumbnailUrl);
    public bool IsLive => item.IsLive;
    public string Monogram => string.Concat(item.Title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));

    public string Detail => item.NeedsResolve
        ? TitleDetail // a title from a site's list: its sport and the site
        : string.Join(" · ", new[] { item.Channel, MediaViewModel.Age(item.PublishedUtc) }.Where(s => !string.IsNullOrEmpty(s)));

    [RelayCommand]
    private Task Play() => owner.PlayAsync(this);

    [RelayCommand]
    private void Share() => owner.ShareTile(this);

    [RelayCommand]
    private void OpenPage() => owner.OpenPage(PageUrl ?? PlayUrl);
}

/// <summary>Titles of one type on the Browse grid ("Football", "Movies" …), under its own heading.</summary>
public sealed class MediaTypeGroupViewModel
{
    public MediaTypeGroupViewModel(string name, IEnumerable<MediaTileViewModel> tiles)
    {
        Name = name;
        Tiles.ResetWith(tiles);
        CountText = Tiles.Count == 1 ? "1 title" : $"{Tiles.Count:N0} titles";
    }

    public string Name { get; }
    public string CountText { get; }
    public BulkObservableCollection<MediaTileViewModel> Tiles { get; } = new();
}

/// <summary>A chip above the Browse grid: a source ("All", or one site with how many it has), or a type ("Football").</summary>
public sealed partial class MediaSourceChipViewModel(MediaViewModel owner, Guid? sourceId, string name, int count) : ObservableObject
{
    public Guid? SourceId { get; } = sourceId;

    /// <summary>A type chip: the type it picks (null: all types).</summary>
    public string? Type { get; init; }
    public bool IsTypeChip { get; init; }
    public string Name { get; } = name;
    public string CountText { get; } = count.ToString("N0");

    [ObservableProperty] private bool _isSelected;

    [RelayCommand]
    private void Choose()
    {
        if (IsTypeChip) owner.ChooseType(Type);
        else owner.ChooseSource(SourceId);
    }
}
