using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SeaRaider.App.Services;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Iptv;

namespace SeaRaider.App.ViewModels;

/// <summary>
/// Media › Live TV: the channels of the user's M3U playlist as a wall of channel tiles, with categories (Sports first), search,
/// favorites, a channel-number keypad and channel up / down / last. Channels play in SeaRaider's own player, so multi-view
/// (several games side by side), the bet slip and the event chat work on them like on any other live stream.
/// </summary>
public sealed partial class IptvViewModel : ViewModelBase
{
    /// <summary>Tiles drawn at once: a playlist can have tens of thousands of channels; search narrows them.</summary>
    private const int MaxTiles = 600;

    private const string AllChip = "All", FavoritesChip = "★ Favorites";

    private readonly IptvService _iptv;
    private readonly TrailerPlayerService _player;
    private readonly ILibraryRepository _repository;
    private readonly DispatcherTimer _filterTimer;
    private readonly DispatcherTimer _hudTimer;
    private List<IptvChannelViewModel> _all = [];
    private HashSet<string> _favorites = [];
    private IptvChannelViewModel? _current, _previous;
    private bool _started;

    public IptvViewModel(IptvService iptv, TrailerPlayerService player, ILibraryRepository repository)
    {
        _iptv = iptv;
        _player = player;
        _repository = repository;
        _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _filterTimer.Tick += (_, _) =>
        {
            _filterTimer.Stop();
            ApplyFilter();
        };
        _hudTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
        _hudTimer.Tick += (_, _) =>
        {
            _hudTimer.Stop();
            if (!string.IsNullOrEmpty(KeypadText)) GoToTypedNumber();
        };
        iptv.Changed += (_, _) => Dispatcher.UIThread.Post(Rebuild);
        PlaylistInput = iptv.PlaylistUrl ?? string.Empty;
        _selectedChip = AllChip;
        Rebuild();
    }

    public BulkObservableCollection<IptvChannelViewModel> Tiles { get; } = new();
    public ObservableCollection<IptvChipViewModel> Chips { get; } = [];

    /// <summary>The M3U link box.</summary>
    [ObservableProperty] private string _playlistInput = string.Empty;

    [ObservableProperty] private string _searchText = string.Empty;

    [ObservableProperty] private string _selectedChip;

    /// <summary>Digits typed on the keypad ("1", "12", "123"): the channel number being dialled.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasKeypadText))]
    private string _keypadText = string.Empty;

    public bool HasKeypadText => KeypadText.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSetup), nameof(ShowGuide), nameof(ConnectText))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSetup), nameof(ShowGuide))]
    private bool _hasPlaylist;

    /// <summary>"Change link" was pressed: the link box shows even though channels are loaded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSetup), nameof(ShowGuide))]
    private bool _isEditingLink;

    [ObservableProperty] private string _statusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNowPlaying))]
    private string _nowPlayingName = string.Empty;

    [ObservableProperty] private string _nowPlayingDetail = string.Empty;
    [ObservableProperty] private string? _nowPlayingLogo;

    public bool HasNowPlaying => NowPlayingName.Length > 0;
    public bool ShowSetup => !HasPlaylist || IsEditingLink;
    public bool ShowGuide => HasPlaylist && !IsEditingLink;
    public string ConnectText => IsBusy ? "Loading…" : "Connect";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoResults))]
    private int _shownCount;

    public bool NoResults => ShowGuide && ShownCount == 0;

    public void OnNavigatedTo()
    {
        if (_started) return;
        _started = true;
        if (string.IsNullOrWhiteSpace(_iptv.PlaylistUrl)) return;
        _ = Task.Run(async () =>
        {
            try { await _iptv.StartAsync(); }
            catch (Exception ex) { Dispatcher.UIThread.Post(() => ErrorMessage = Describe(ex)); }
        });
    }

    partial void OnSearchTextChanged(string value)
    {
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    partial void OnSelectedChipChanged(string value)
    {
        foreach (var chip in Chips) chip.IsSelected = chip.Name == value;
        ApplyFilter();
    }

    // ---- loading ---------------------------------------------------------------------------------------------------

    [RelayCommand]
    private Task Connect() => RunSafeAsync(async () =>
    {
        IsBusy = true;
        StatusText = "Tuning in…";
        try
        {
            var count = await _iptv.RefreshAsync(PlaylistInput);
            IsEditingLink = false;
            StatusText = $"{count:N0} channels";
        }
        finally
        {
            IsBusy = false;
        }
    });

    [RelayCommand]
    private Task Refresh() => RunSafeAsync(async () =>
    {
        IsBusy = true;
        try
        {
            var count = await _iptv.RefreshAsync(null);
            StatusText = $"{count:N0} channels, just updated";
        }
        finally
        {
            IsBusy = false;
        }
    });

    [RelayCommand]
    private void ChangeLink() => IsEditingLink = true;

    [RelayCommand]
    private void CancelChangeLink() => IsEditingLink = false;

    [RelayCommand]
    private void Remove()
    {
        _iptv.Clear();
        PlaylistInput = string.Empty;
        IsEditingLink = false;
        StatusText = string.Empty;
    }

    private void Rebuild()
    {
        _favorites = [.. _repository.GetSettings().IptvFavorites];
        var last = _repository.GetSettings().IptvLastChannel;
        _all = _iptv.Channels.Select(c => new IptvChannelViewModel(this, c, _favorites.Contains(c.Key))).ToList();
        HasPlaylist = _all.Count > 0;
        if (HasPlaylist && string.IsNullOrEmpty(StatusText) && _iptv.LoadedAt is { } at)
            StatusText = $"{_all.Count:N0} channels · updated {at.ToLocalTime():MMM d, h:mm tt}";
        _previous = last is null ? null : _all.FirstOrDefault(c => c.Channel.Key == last);
        _current = null;
        BuildChips();
        ApplyFilter();
    }

    private void BuildChips()
    {
        var chips = new List<IptvChipViewModel> { new(this, AllChip, _all.Count, AllChip == SelectedChip) };
        var favorites = _all.Count(c => c.IsFavorite);
        if (favorites > 0) chips.Add(new(this, FavoritesChip, favorites, FavoritesChip == SelectedChip));
        foreach (var category in new[] { IptvCategory.Sports, IptvCategory.News, IptvCategory.Movies, IptvCategory.Kids, IptvCategory.Music })
        {
            var n = _all.Count(c => c.Channel.Category == category);
            if (n > 0) chips.Add(new(this, CategoryName(category), n, CategoryName(category) == SelectedChip));
        }
        // Then the playlist's own biggest groups ("USA | Sports HD", "UK Entertainment" …).
        foreach (var group in _all.Where(c => c.Channel.Group.Length > 0).GroupBy(c => c.Channel.Group).OrderByDescending(g => g.Count()).Take(14))
            chips.Add(new(this, group.Key, group.Count(), group.Key == SelectedChip));
        if (!chips.Any(c => c.Name == SelectedChip)) _selectedChip = AllChip;
        Chips.Clear();
        foreach (var chip in chips) Chips.Add(chip);
        foreach (var chip in Chips) chip.IsSelected = chip.Name == SelectedChip;
    }

    private static string CategoryName(IptvCategory c) => c switch
    {
        IptvCategory.Sports => "🏟 Sports",
        IptvCategory.News => "📰 News",
        IptvCategory.Movies => "🎬 Movies & series",
        IptvCategory.Kids => "🧸 Kids",
        IptvCategory.Music => "🎵 Music",
        _ => "Other",
    };

    private static IptvCategory? CategoryOf(string chip) =>
        Enum.GetValues<IptvCategory>().Cast<IptvCategory?>().FirstOrDefault(c => c != IptvCategory.Other && CategoryName(c!.Value) == chip);

    private IEnumerable<IptvChannelViewModel> Filtered()
    {
        IEnumerable<IptvChannelViewModel> items = _all;
        var chip = SelectedChip;
        if (chip == FavoritesChip) items = items.Where(c => c.IsFavorite);
        else if (chip != AllChip)
        {
            if (CategoryOf(chip) is { } category) items = items.Where(c => c.Channel.Category == category);
            else items = items.Where(c => c.Channel.Group == chip);
        }
        var q = SearchText.Trim();
        if (q.Length > 0)
        {
            var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            items = items.Where(c => words.All(w => c.Channel.Name.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                                                     c.Channel.Group.Contains(w, StringComparison.OrdinalIgnoreCase)));
        }
        return items;
    }

    private void ApplyFilter()
    {
        var list = Filtered().Take(MaxTiles + 1).ToList();
        ShownCount = Math.Min(list.Count, MaxTiles);
        Tiles.ResetWith(list.Take(MaxTiles));
        OnPropertyChanged(nameof(NoResults));
        TooManyText = list.Count > MaxTiles ? $"Showing the first {MaxTiles} — search or pick a category to narrow it down." : string.Empty;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTooMany))]
    private string _tooManyText = string.Empty;

    public bool HasTooMany => TooManyText.Length > 0;

    internal void SelectChip(string name) => SelectedChip = name;

    // ---- watching --------------------------------------------------------------------------------------------------

    internal void Play(IptvChannelViewModel channel, bool alongside)
    {
        var url = M3uParser.PlayUrl(channel.Channel.StreamUrl);
        _player.PlayLive(url, channel.Channel.Name, channel.Channel.LogoUrl, alongside);
        if (alongside && _current is not null) return; // the grid's first stream stays "the channel"
        if (_current is not null && !ReferenceEquals(_current, channel)) _previous = _current;
        if (_current is not null) _current.IsPlaying = false;
        _current = channel;
        channel.IsPlaying = true;
        NowPlayingName = channel.Channel.Name;
        NowPlayingDetail = $"CH {channel.NumberText}" + (channel.Channel.Group.Length > 0 ? $" · {channel.Channel.Group}" : string.Empty);
        NowPlayingLogo = channel.Channel.LogoUrl;
        _iptv.RememberLast(channel.Channel);
    }

    internal void ToggleFavorite(IptvChannelViewModel channel)
    {
        channel.IsFavorite = !channel.IsFavorite;
        _iptv.SetFavorite(channel.Channel, channel.IsFavorite);
        BuildChips();
        if (SelectedChip == FavoritesChip) ApplyFilter();
    }

    /// <summary>Channel up / down in whatever list is on screen (wraps around), like a remote.</summary>
    private void Step(int direction)
    {
        var list = Tiles.ToList();
        if (list.Count == 0) return;
        var at = _current is null ? -1 : list.IndexOf(_current);
        var next = at < 0 ? (direction > 0 ? 0 : list.Count - 1) : (at + direction + list.Count) % list.Count;
        Play(list[next], alongside: false);
    }

    [RelayCommand] private void ChannelUp() => Step(1);
    [RelayCommand] private void ChannelDown() => Step(-1);

    /// <summary>The "last channel" button.</summary>
    [RelayCommand]
    private void LastChannel()
    {
        if (_previous is not null) Play(_previous, alongside: false);
    }

    [RelayCommand]
    private void RandomChannel()
    {
        var list = Tiles.ToList();
        if (list.Count > 0) Play(list[Random.Shared.Next(list.Count)], alongside: false);
    }

    /// <summary>A digit pressed on the keypad (or typed): channel numbers are dialled, and tune in when typing pauses or Enter is pressed.</summary>
    [RelayCommand]
    private void Digit(string digit)
    {
        if (KeypadText.Length >= 5) KeypadText = string.Empty;
        KeypadText += digit;
        _hudTimer.Stop();
        _hudTimer.Start();
    }

    [RelayCommand]
    private void ClearKeypad()
    {
        _hudTimer.Stop();
        KeypadText = string.Empty;
    }

    [RelayCommand]
    private void GoToTypedNumber()
    {
        _hudTimer.Stop();
        var text = KeypadText;
        KeypadText = string.Empty;
        if (!int.TryParse(text, out var number)) return;
        var channel = _all.FirstOrDefault(c => c.Channel.Number == number);
        if (channel is null) StatusText = $"No channel {number}";
        else Play(channel, alongside: false);
    }

    /// <summary>Whether a key typed on the Live TV page dials a number (digits) — handled by the view.</summary>
    public void OnDigitTyped(char c) => Digit(c.ToString());
}

/// <summary>One channel tile.</summary>
public sealed partial class IptvChannelViewModel(IptvViewModel owner, IptvChannel channel, bool favorite) : ObservableObject
{
    public IptvChannel Channel { get; } = channel;
    public string Name => Channel.Name;
    public string NumberText => Channel.Number.ToString("000");
    public string GroupText => Channel.Group;
    public string? LogoUrl => Channel.LogoUrl;
    public bool HasLogo => Channel.LogoUrl is not null;
    public bool NoLogo => !HasLogo;
    public bool IsSports => Channel.Category == IptvCategory.Sports;

    public string Monogram
    {
        get
        {
            var letters = new string(Channel.Name.Where(char.IsLetterOrDigit).Take(2).ToArray());
            return letters.Length == 0 ? "TV" : letters.ToUpperInvariant();
        }
    }

    [ObservableProperty] private bool _isFavorite = favorite;
    [ObservableProperty] private bool _isPlaying;

    [RelayCommand] private void Play() => owner.Play(this, alongside: false);
    [RelayCommand] private void Alongside() => owner.Play(this, alongside: true);
    [RelayCommand] private void ToggleFavorite() => owner.ToggleFavorite(this);
}

/// <summary>A category chip ("🏟 Sports  42").</summary>
public sealed partial class IptvChipViewModel(IptvViewModel owner, string name, int count, bool selected) : ObservableObject
{
    [RelayCommand] private void Select() => owner.SelectChip(Name);

    public string Name { get; } = name;
    public string CountText { get; } = count.ToString("N0");
    [ObservableProperty] private bool _isSelected = selected;
}
