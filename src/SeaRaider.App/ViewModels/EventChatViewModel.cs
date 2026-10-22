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
    private const int MaxLines = 250;

    private readonly EventChatService _chat;
    private readonly TrailerPlayerService _player;
    private readonly IFriendsService _friends;
    private readonly HashSet<string> _seenIds = new(StringComparer.Ordinal);
    private int _leaveCheck;

    public EventChatViewModel(EventChatService chat, TrailerPlayerService player, IFriendsService friends)
    {
        _chat = chat;
        _player = player;
        _friends = friends;
        chat.LineReceived += (_, line) => Dispatcher.UIThread.Post(() => Add(line));
        chat.StateChanged += (_, _) => Dispatcher.UIThread.Post(UpdateState);
        player.PropertyChanged += OnPlayerChanged;
        UpdateState();
    }

    public ObservableCollection<EventChatLineViewModel> Lines { get; } = [];

    /// <summary>The chat panel is showing beside the video.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSidePanel))]
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

    /// <summary>"Chat · 12": the header button.</summary>
    public string ButtonText => _chat.Watching > 0 ? $"Chat · {_chat.Watching:N0}" : "Chat";

    /// <summary>Raised after lines were added, so the view can scroll to the newest.</summary>
    public event Action? LinesAppended;

    partial void OnIsOpenChanged(bool value)
    {
        if (value) Unread = 0;
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
                OnPropertyChanged(nameof(ShowSidePanel));
                break;
            case nameof(TrailerPlayerService.IsMinimized) or nameof(TrailerPlayerService.IsBetSlipOpen) or nameof(TrailerPlayerService.IsOpen):
                OnPropertyChanged(nameof(ShowSidePanel));
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
    }
}

/// <summary>One line in the event chat.</summary>
public sealed class EventChatLineViewModel(EventChatLine line)
{
    public string Name { get; } = line.IsMine ? "You" : line.Name;
    public string Text { get; } = line.Text;
    public bool IsMine { get; } = line.IsMine;
    public string TimeText { get; } = line.AtUtc.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);
}
