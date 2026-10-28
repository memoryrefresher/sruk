using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SeaRaider.App.Services;
using SeaRaider.Core.Social;

namespace SeaRaider.App.ViewModels;

/// <summary>
/// The player's live chat: everyone in SeaRaider watching the same event, in one room (see <see cref="EventChatService"/>).
/// The room follows what's playing; the panel shows beside the video ("Chat" in the player's header), and lines that
/// arrive while it's hidden count as unread.
/// </summary>
public sealed partial class EventChatViewModel : ViewModelBase
{
    private const int MaxLines = 150; // a long room history slowed the panel down while the video played

    private readonly EventChatService _chat;
    private readonly TrailerPlayerService _player;
    private readonly IFriendsService _friends;
    private readonly HashSet<string> _seenIds = new(StringComparer.Ordinal);
    private int _leaveCheck;

    private readonly FriendsViewModel _friendsPanel;

    public EventChatViewModel(EventChatService chat, TrailerPlayerService player, IFriendsService friends, FriendsViewModel friendsPanel)
    {
        _chat = chat;
        _player = player;
        _friends = friends;
        _friendsPanel = friendsPanel;
        friends.FriendsChanged += (_, _) => Dispatcher.UIThread.Post(UpdateViewers);
        chat.LineReceived += (_, line) => Dispatcher.UIThread.Post(() => Add(line));
        chat.StateChanged += (_, _) => Dispatcher.UIThread.Post(UpdateState);
        player.PropertyChanged += OnPlayerChanged;
        UpdateState();
    }

    public ObservableCollection<EventChatLineViewModel> Lines { get; } = [];

    // ---- who's watching (the viewer list) -------------------------------------------------------------

    /// <summary>Everyone in the room: you first, then friends, then the rest by name.</summary>
    public ObservableCollection<EventChatViewerViewModel> Viewers { get; } = [];

    /// <summary>The viewer list is showing (instead of the chat lines).</summary>
    [ObservableProperty] private bool _showViewers;

    [RelayCommand]
    private void ToggleViewers() => ShowViewers = !ShowViewers;

    /// <summary>"12 watching" on the list's button, or why there's no list.</summary>
    public string ViewersButtonText => _chat.Watching > 0 ? $"👥 {_chat.Watching:N0}" : "👥";

    public bool HasNoViewerList => _chat.Room is not null && !_chat.RelayListsViewers;

    private void UpdateViewers()
    {
        var friends = _friends.Friends.ToDictionary(f => f.UserId, StringComparer.Ordinal);
        var rows = _chat.Viewers
            .Select(v => new EventChatViewerViewModel(v, friends.GetValueOrDefault(v.UserId), this))
            .OrderByDescending(v => v.IsMe).ThenByDescending(v => v.IsFriend).ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        Viewers.Clear();
        foreach (var row in rows) Viewers.Add(row);
        OnPropertyChanged(nameof(ViewersButtonText));
        OnPropertyChanged(nameof(HasNoViewerList));
    }

    /// <summary>"Message": the player goes into its corner (still playing) and the friends panel opens on the conversation.</summary>
    internal void MessageViewer(EventChatViewerViewModel viewer)
    {
        if (_player.IsOpen) _player.MinimizeCommand.Execute(null);
        if (!_friendsPanel.OpenChatWith(viewer.UserId)) ErrorMessage = $"{viewer.Name} isn't a friend yet. Add them first.";
    }

    /// <summary>"Add friend": a friend request to them (their SeaRaider id is their friend code).</summary>
    internal Task AddViewerAsync(EventChatViewerViewModel viewer) => RunSafeAsync(async () =>
    {
        await _friends.SendFriendRequestAsync(viewer.UserId, $"Hi! We were watching {_chat.Title ?? "the same thing"} together.");
        viewer.MarkRequested();
    });

    /// <summary>The chat panel is showing beside the video.</summary>
    [ObservableProperty]
    private bool _isOpen = true;

    [ObservableProperty] private string _draft = string.Empty;
    [ObservableProperty] private string _watchingText = string.Empty;
    [ObservableProperty] private string? _statusText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread))]
    private int _unread;

    public bool HasUnread => Unread > 0;
    public bool HasStatus => !string.IsNullOrEmpty(StatusText);
    public bool CanChat => _chat.IsAvailable && !_chat.RelayTooOld && _chat.Room is not null;

    /// <summary>The column beside the video: the chat, the bet slip or both (never in the mini-player).</summary>
    public bool ShowSidePanel => !_player.IsMinimized && _player.IsOpen && (IsOpen || _player.IsBetSlipOpen);

    // Both open: the chat on top (2 parts), the bet slip under it (3 parts). One open: it takes the whole column.
    public int ChatRowSpan => _player.IsBetSlipOpen ? 1 : 3;
    public int BetSlipRow => IsOpen ? 2 : 0;
    public int BetSlipRowSpan => IsOpen ? 1 : 3;

    private void LayoutChanged()
    {
        OnPropertyChanged(nameof(ShowSidePanel));
        OnPropertyChanged(nameof(ChatRowSpan));
        OnPropertyChanged(nameof(BetSlipRow));
        OnPropertyChanged(nameof(BetSlipRowSpan));
    }

    /// <summary>"Chat · 12": the header button.</summary>
    public string ButtonText => _chat.Watching > 0 ? $"Chat · {_chat.Watching:N0}" : "Chat";

    /// <summary>Raised after lines were added, so the view can scroll to the newest.</summary>
    public event Action? LinesAppended;

    partial void OnIsOpenChanged(bool value)
    {
        if (value) Unread = 0;
        LayoutChanged();
    }

    partial void OnStatusTextChanged(string? value) => OnPropertyChanged(nameof(HasStatus));

    [RelayCommand]
    private void Toggle() => IsOpen = !IsOpen;

    [RelayCommand]
    private Task SendAsync() => RunSafeAsync(async () =>
    {
        var text = Draft;
        if (string.IsNullOrWhiteSpace(text)) return;
        Draft = string.Empty;
        await _chat.SayAsync(text);
    });

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TrailerPlayerService.Current):
                if (_player.Current is { } session)
                {
                    _leaveCheck++;
                    var room = EventChatService.RoomFor(session.Title, session.PageUrl, session.Url);
                    if (room != _chat.Room)
                    {
                        Lines.Clear();
                        _seenIds.Clear();
                        Unread = 0;
                    }
                    _ = RunSafeAsync(() => _chat.JoinAsync(room, session.Title));
                }
                else
                {
                    // Play() clears the player for a moment before the next video: leave only if it stays closed.
                    var check = ++_leaveCheck;
                    DispatcherTimer.RunOnce(() =>
                    {
                        if (check != _leaveCheck || _player.Current is not null) return;
                        Lines.Clear();
                        _seenIds.Clear();
                        _ = RunSafeAsync(_chat.LeaveAsync);
                    }, TimeSpan.FromSeconds(1));
                }
                LayoutChanged();
                break;
            case nameof(TrailerPlayerService.IsMinimized) or nameof(TrailerPlayerService.IsBetSlipOpen) or nameof(TrailerPlayerService.IsOpen):
                LayoutChanged();
                break;
        }
    }

    private void Add(EventChatLine line)
    {
        if (!_seenIds.Add(line.Id)) return; // the room's history again after a reconnect
        Lines.Add(new EventChatLineViewModel(line));
        while (Lines.Count > MaxLines) Lines.RemoveAt(0);
        if (!IsOpen && !line.IsMine) Unread++;
        LinesAppended?.Invoke();
    }

    private void UpdateState()
    {
        WatchingText = _chat.Watching switch
        {
            0 => string.Empty,
            1 => "Just you watching",
            var n => $"{n:N0} watching",
        };
        StatusText = !_friends.IsEnabled
            ? "Turn on Friends in Settings › Friends to chat with everyone watching this."
            : _chat.RelayTooOld
                ? "Your friends relay doesn't have event chat yet: update SeaRaider.Relay on the server."
                : _friends.ConnectionState != SocialConnectionState.Connected
                    ? "Connecting to the relay…"
                    : null;
        OnPropertyChanged(nameof(ButtonText));
        OnPropertyChanged(nameof(CanChat));
        UpdateViewers();
    }
}

/// <summary>Someone in the viewer list: Message them (a friend) or Add them.</summary>
public sealed partial class EventChatViewerViewModel(EventChatViewer viewer, Friend? friend, EventChatViewModel owner) : ObservableObject
{
    public string UserId => viewer.UserId;
    public string Name { get; } = viewer.IsMe ? viewer.Name + " (you)" : friend is { State: FriendshipState.Friend } f && f.DisplayName.Length > 0 ? f.DisplayName : viewer.Name;
    public bool IsMe => viewer.IsMe;
    public bool IsFriend => friend?.State == FriendshipState.Friend;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd), nameof(StateText), nameof(ShowStateInMenu))]
    private bool _isRequested = friend?.State == FriendshipState.OutgoingRequest;

    public bool CanMessage => IsFriend && !IsMe;
    public bool CanAdd => !IsMe && friend is null && !IsRequested;

    /// <summary>"Friend", "Request sent", "Wants to be friends" …</summary>
    public string StateText => IsMe ? string.Empty
        : IsFriend ? "Friend"
        : IsRequested ? "Request sent"
        : friend?.State == FriendshipState.IncomingRequest ? "Asked to be friends (see Friends)"
        : string.Empty;

    public bool ShowStateInMenu => !CanMessage && StateText.Length > 0;

    internal void MarkRequested() => IsRequested = true;

    [RelayCommand]
    private void Message() => owner.MessageViewer(this);

    [RelayCommand]
    private Task AddAsync() => owner.AddViewerAsync(this);
}

/// <summary>One line in the event chat.</summary>
public sealed class EventChatLineViewModel(EventChatLine line)
{
    /// <summary>Each name keeps one colour (like a stream chat); yours is the app's own colour.</summary>
    private static readonly Avalonia.Media.IBrush[] NameColours =
        new[] { "#FF7FD8FF", "#FFFFB86B", "#FFB79CFF", "#FF7BE495", "#FFFF8FA3", "#FFFFE074", "#FF6FE3D3", "#FFF4A6FF" }
            .Select(c => (Avalonia.Media.IBrush)new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Avalonia.Media.Color.Parse(c))).ToArray();

    private static readonly Avalonia.Media.IBrush MineColour = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Avalonia.Media.Color.Parse("#FF3FE0C5"));

    public Avalonia.Media.IBrush NameBrush { get; } = line.IsMine
        ? MineColour
        : NameColours[(int)((uint)StableHash(line.FromId.Length > 0 ? line.FromId : line.Name) % (uint)NameColours.Length)];

    private static int StableHash(string text)
    {
        unchecked
        {
            var hash = 17;
            foreach (var c in text) hash = hash * 31 + c;
            return hash;
        }
    }

    public string Name { get; } = line.IsMine ? "You" : line.Name;
    public string Text { get; } = line.Text;
    public bool IsMine { get; } = line.IsMine;
    public string TimeText { get; } = line.AtUtc.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);
}
