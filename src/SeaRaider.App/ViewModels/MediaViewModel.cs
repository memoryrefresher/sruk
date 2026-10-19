using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SeaRaider.App.Services;
using SeaRaider.Core.Catalog;
using SeaRaider.Core.Models;

namespace SeaRaider.App.ViewModels;

/// <summary>
/// The Media tab: game trailers, videos and streams from the channels and sites the user adds, as boxes grouped by
/// channel. A box opens in SeaRaider's own player (the trailer pop-out over the blurred window).
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

    /// <summary>Which half of the tab shows: the user's channels, or what friends shared.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChannelsTab))]
    private bool _isSharedTab;

    public bool IsChannelsTab => !IsSharedTab;

    partial void OnIsSharedTabChanged(bool value) => Shared.SetOnScreen(value && _onScreen);

    [RelayCommand]
    private void ShowChannels() => IsSharedTab = false;

    [RelayCommand]
    private void ShowShared() => IsSharedTab = true;

    /// <summary>Share on a video box: the composer on "Shared", filled in with the video.</summary>
    internal void ShareTile(MediaTileViewModel tile)
    {
        Shared.Prefill(tile.PlayUrl); // the video itself (a box's page can be the site it was found on)
        IsSharedTab = true;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(HasSections))]
    private int _sectionCount;

    [ObservableProperty] private string _newSourceUrl = string.Empty;
    [ObservableProperty] private bool _isAdding;
    [ObservableProperty] private bool _isRefreshing;

    /// <summary>Filters every channel's boxes by title or channel name.</summary>
    [ObservableProperty] private string _searchText = string.Empty;

    public bool IsEmpty => SectionCount == 0;
    public bool HasSections => SectionCount > 0;

    partial void OnSearchTextChanged(string value) => Reload();

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

    internal void Play(MediaTileViewModel tile) => RunSafe(() => _player.Play(tile.PlayUrl, tile.PageUrl, tile.Title));

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
            throw new ArgumentException("Paste a YouTube channel or playlist, a Twitch channel, a video RSS feed, or a page with videos.");
        IsAdding = true;
        try
        {
            var url = NewSourceUrl;
            await Task.Run(() => _media.AddSourceAsync(url));
            NewSourceUrl = string.Empty;
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
            _ => "Web page",
        };
        StatusText = source.LastFetchedUtc is null
            ? "Loading…"
            : $"{items.Count} {(items.Count == 1 ? "video" : "videos")} · checked {MediaViewModel.Age(source.LastFetchedUtc)}";
        _all = items.Select(i => new MediaTileViewModel(owner, i)).ToList();
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

/// <summary>One video or stream box.</summary>
public sealed partial class MediaTileViewModel(MediaViewModel owner, MediaItem item) : ObservableObject
{
    public string Title => item.Title;
    public string PlayUrl => item.PlayUrl;
    public string? PageUrl => item.PageUrl;
    public string? ThumbnailUrl => item.ThumbnailUrl;
    public bool HasThumbnail => !string.IsNullOrEmpty(item.ThumbnailUrl);
    public bool IsLive => item.IsLive;
    public string Monogram => string.Concat(item.Title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));

    public string Detail => string.Join(" · ", new[] { item.Channel, MediaViewModel.Age(item.PublishedUtc) }.Where(s => !string.IsNullOrEmpty(s)));

    [RelayCommand]
    private void Play() => owner.Play(this);

    [RelayCommand]
    private void Share() => owner.ShareTile(this);
}
