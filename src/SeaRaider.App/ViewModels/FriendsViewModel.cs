using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SeaRaider.App.Services;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Catalog;
using SeaRaider.Core.Models;
using SeaRaider.Core.Social;

namespace SeaRaider.App.ViewModels;

/// <summary>A presence choice in the friends panel.</summary>
public sealed record PresenceChoice(PresenceMode Mode, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The Friends &amp; Chat overlay (Steam-overlay style side panel): your friend code and status, friend requests,
/// the friend list with live presence, and one-to-one chat with animated emotes. Everything from
/// <see cref="IFriendsService"/> arrives on background threads and is marshalled to the UI thread here.
/// </summary>
public sealed partial class FriendsViewModel : ViewModelBase
{
    private static readonly TimeSpan ToastDuration = TimeSpan.FromSeconds(6);

    public static IReadOnlyList<PresenceChoice> PresenceChoices { get; } =
    [
        new(PresenceMode.Automatic, "Online (automatic)"),
        new(PresenceMode.Away, "Away"),
        new(PresenceMode.Invisible, "Appear offline"),
    ];

    private readonly IFriendsService _friends;
    private readonly ShellService _shell;
    private readonly NavigationService _navigation;
    private readonly DispatcherTimer _toastTimer;
    private bool _loading;

    private readonly SharedGameOpener _opener;
    private readonly WatchTogetherService _watch;
    private readonly PresenceMonitor _presence;
    private readonly ILibraryRepository _repository;
    private readonly IGameLauncherService _launcher;
    private readonly TrailerPlayerService _player;

    public FriendsViewModel(IFriendsService friends, ShellService shell, NavigationService navigation, SharedGameOpener opener,
        WatchTogetherService watch, PresenceMonitor presence, ILibraryRepository repository, IGameLauncherService launcher,
        TrailerPlayerService player)
    {
        _player = player;
        // "Send to a friend…" on a game or media box, and boxes dragged towards the panel.
        ChatAttachments.AttachRequested += attachment => Dispatcher.UIThread.Post(() => Attach(attachment, null));
        ChatAttachments.DragStarted += () => Dispatcher.UIThread.Post(() =>
        {
            if (IsEnabled) IsOpen = true;
        });
        _opener = opener;
        _watch = watch;
        _presence = presence;
        _repository = repository;
        _launcher = launcher;
        _friends = friends;
        _shell = shell;
        _navigation = navigation;
        Emotes = Core.Social.Emotes.All.Select(e => new EmoteViewModel(e, InsertEmote)).ToList();

        _toastTimer = new DispatcherTimer { Interval = ToastDuration };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast = null;
        };

        friends.FriendsChanged += (_, _) => Dispatcher.UIThread.Post(RefreshFriends);
        friends.ConnectionChanged += (_, _) => Dispatcher.UIThread.Post(RefreshConnection);
        friends.MyPresenceChanged += (_, _) => Dispatcher.UIThread.Post(UpdateMyStatus); // e.g. "In-Game: Hades II"
        friends.MessageAdded += (_, message) => Dispatcher.UIThread.Post(() => OnMessageAdded(message));
        friends.MessageUpdated += (_, message) => Dispatcher.UIThread.Post(() => OnMessageUpdated(message));
        friends.InviteAnswered += (_, message) => Dispatcher.UIThread.Post(() => OnInviteAnswered(message));

        _loading = true;
        MyName = friends.Profile.DisplayName;
        SelectedPresence = PresenceChoices.First(p => p.Mode == friends.Profile.PresenceMode);
        _loading = false;

        RefreshFriends();
        RefreshConnection();
    }

    // ---- panel -----------------------------------------------------------------------------------

    [ObservableProperty] private bool _isOpen;

    /// <summary>Friends are switched on in Settings; when off the rail button and overlay are hidden.</summary>
    [ObservableProperty] private bool _isEnabled;

    [RelayCommand]
    private void Toggle()
    {
        IsOpen = IsEnabled && !IsOpen;
        if (!IsOpen) CloseChat();
    }

    [RelayCommand]
    private void Close()
    {
        IsOpen = false;
        CloseChat();
    }

    // ---- me --------------------------------------------------------------------------------------

    public string FriendCode => _friends.Profile.UserId;
    public string Initial => string.IsNullOrWhiteSpace(MyName) ? "?" : MyName.Trim()[..1].ToUpperInvariant();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Initial))]
    private string _myName = string.Empty;

    [ObservableProperty] private PresenceChoice _selectedPresence = PresenceChoices[0];
    [ObservableProperty] private string _myStatusText = string.Empty;
    [ObservableProperty] private string? _copyFeedback;

    partial void OnSelectedPresenceChanged(PresenceChoice value)
    {
        if (_loading) return;
        _ = RunSafeAsync(() => _friends.SetPresenceModeAsync(value.Mode));
    }

    [RelayCommand]
    private Task SaveNameAsync() => RunSafeAsync(() => _friends.SetDisplayNameAsync(MyName));

    [RelayCommand]
    private Task CopyFriendCodeAsync() => RunSafeAsync(async () =>
    {
        await _shell.CopyToClipboardAsync(FriendCode);
        CopyFeedback = "Copied";
    });

    // ---- connection ------------------------------------------------------------------------------

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isNotConfigured;
    [ObservableProperty] private string? _connectionText;

    public bool HasConnectionText => !string.IsNullOrEmpty(ConnectionText);

    partial void OnConnectionTextChanged(string? value) => OnPropertyChanged(nameof(HasConnectionText));

    private void RefreshConnection()
    {
        IsEnabled = _friends.IsEnabled;
        if (!IsEnabled && IsOpen) Close();
        var state = _friends.ConnectionState;
        IsConnected = state == SocialConnectionState.Connected;
        IsNotConfigured = state == SocialConnectionState.NotConfigured && _friends.ConnectionError is null;
        ConnectionText = state switch
        {
            SocialConnectionState.Connected => null,
            SocialConnectionState.Connecting => "Connecting to the friends relay…",
            SocialConnectionState.NotConfigured => _friends.ConnectionError ??
                "This copy of SeaRaider has no friends relay built in. Add a relay address in Settings › Friends.",
            _ => _friends.ConnectionError ?? "Offline. Reconnecting…",
        };
        UpdateMyStatus();
    }

    private void UpdateMyStatus()
    {
        var presence = _friends.MyPresence;
        MyStatusText = !IsConnected
            ? "Offline"
            : presence.Status switch
            {
                UserStatus.InGame => presence.GameTitle is { } game ? $"In-Game: {game}" : "In-Game",
                UserStatus.Offline => "Appearing offline",
                _ when presence.Media is { } media => $"Watching: {media.Title}",
                UserStatus.Away => "Away",
                _ => "Online",
            };
        UpdateInviteOptions();
    }

    // ---- invites & watch along -------------------------------------------------------------------

    /// <summary>"Invite to play Hades II" while a game launched from SeaRaider runs, else null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInviteToPlay))]
    private string? _inviteToPlayText;

    public bool CanInviteToPlay => InviteToPlayText is not null;

    private void UpdateInviteOptions()
    {
        InviteToPlayText = _presence.RunningGameTitle is { } game ? $"Invite to play {game}" : null;
        foreach (var friend in Friends) friend.SetInviteOption(InviteToPlayText);
        if (ActiveFriend is { } active && !Friends.Contains(active)) active.SetInviteOption(InviteToPlayText);
    }

    /// <summary>The game running now, as an invite (its store page lets the friend find the same game).</summary>
    private ChatInvite? CurrentGameInvite()
    {
        if (_presence.RunningGameId is not { } id) return null;
        var game = _repository.GetGame(id);
        var install = _repository.GetInstall(id);
        var title = DisplayText.Title(install?.Title ?? game?.Title ?? _presence.RunningGameTitle);
        if (string.IsNullOrWhiteSpace(title)) return null;
        return new ChatInvite
        {
            Kind = InviteKind.Game,
            Title = title,
            PageUrl = string.IsNullOrEmpty(game?.PageUrl) ? null : game.PageUrl,
            CoverUrl = game?.CoverImageUrl ?? install?.CoverImageUrl,
        };
    }

    internal Task InviteToPlayAsync(FriendItemViewModel friend) => RunSafeAsync(async () =>
    {
        var invite = CurrentGameInvite() ?? throw new InvalidOperationException("Start a game from SeaRaider first, then invite friends to it.");
        await _friends.SendInviteAsync(friend.UserId, invite);
        if (ActiveFriend?.UserId != friend.UserId) ShowToast(friend, $"Invited {friend.Name} to play {invite.Title}");
    });

    [RelayCommand]
    private Task InviteActiveToPlayAsync() => ActiveFriend is { } friend ? InviteToPlayAsync(friend) : Task.CompletedTask;

    internal void WatchAlong(FriendItemViewModel friend) => RunSafe(() =>
    {
        _watch.Join(friend.UserId);
        IsOpen = false;
    });

    [RelayCommand]
    private void WatchAlongActive()
    {
        if (ActiveFriend is { } friend) WatchAlong(friend);
    }

    /// <summary>The game an invite is for, when it's installed here.</summary>
    internal InstalledGame? FindInstalled(string title)
    {
        var key = TitleNormalizer.DedupKey(title);
        return _repository.GetInstalls().FirstOrDefault(i => i.State == GameInstallState.Installed && TitleNormalizer.DedupKey(i.Title) == key);
    }

    /// <summary>Play / Get it / Watch along on an invite card.</summary>
    internal Task AcceptInviteAsync(ChatMessageViewModel message) => RunSafeAsync(async () =>
    {
        if (message.Invite is not { } invite) return;
        if (message.IsIncoming && message.Answer == InviteAnswer.None) await _friends.AnswerInviteAsync(message.Id, true);

        if (invite.Kind == InviteKind.Watch)
        {
            var sender = _friends.Friends.FirstOrDefault(f => f.UserId == message.FriendId);
            if (message.IsIncoming && sender?.Watching is not null) _watch.Join(message.FriendId);
            else _watch.PlayInvite(invite);
            IsOpen = false;
            return;
        }

        IsOpen = false;
        if (FindInstalled(invite.Title) is { } installed) await _launcher.LaunchAsync(installed.GameId);
        else _opener.Open(new SharedGame(invite.Title, invite.PageUrl ?? string.Empty, invite.CoverUrl, invite.SourceName, null));
    });

    internal Task DeclineInviteAsync(ChatMessageViewModel message) =>
        RunSafeAsync(() => _friends.AnswerInviteAsync(message.Id, false));

    private void OnInviteAnswered(ChatMessage message)
    {
        if (message.Invite is not { } invite) return;
        var friend = Friends.FirstOrDefault(f => f.UserId == message.FriendId);
        var name = friend?.Name ?? "Your friend";
        ShowToast(friend, message.Answer == InviteAnswer.Accepted
            ? invite.Kind == InviteKind.Game ? $"{name} is joining you in {invite.Title}" : $"{name} is watching along"
            : invite.Kind == InviteKind.Game ? $"{name} can't play right now" : $"{name} can't watch right now");
    }

    [RelayCommand]
    private void OpenFriendSettings()
    {
        _navigation.RequestedSettingsTab = SettingsTab.Friends;
        _navigation.ShowPage(AppPage.Settings);
        IsOpen = false;
    }

    // ---- friends & requests ----------------------------------------------------------------------

    /// <summary>Friends, sorted In-Game, Online, Away, Offline.</summary>
    public ObservableCollection<FriendItemViewModel> Friends { get; } = [];

    public ObservableCollection<FriendItemViewModel> IncomingRequests { get; } = [];
    public ObservableCollection<FriendItemViewModel> OutgoingRequests { get; } = [];

    [ObservableProperty] private string _onlineSummary = string.Empty;
    [ObservableProperty] private int _badgeCount;

    public bool HasFriends => Friends.Count > 0;
    public bool HasIncoming => IncomingRequests.Count > 0;
    public bool HasOutgoing => OutgoingRequests.Count > 0;
    public bool HasBadge => BadgeCount > 0;

    partial void OnBadgeCountChanged(int value) => OnPropertyChanged(nameof(HasBadge));

    [ObservableProperty] private string _addFriendCode = string.Empty;
    [ObservableProperty] private string _addFriendMessage = string.Empty;

    [RelayCommand]
    private Task AddFriendAsync() => RunSafeAsync(async () =>
    {
        await _friends.SendFriendRequestAsync(AddFriendCode, AddFriendMessage);
        AddFriendCode = string.Empty;
        AddFriendMessage = string.Empty;
    });

    private void RefreshFriends()
    {
        var all = _friends.Friends;
        Sync(Friends, all.Where(f => f.State == FriendshipState.Friend)
            .OrderBy(f => f.Status switch { UserStatus.InGame => 0, UserStatus.Online => 1, UserStatus.Away => 2, _ => 3 })
            .ThenBy(f => f.DisplayName, StringComparer.CurrentCultureIgnoreCase));
        Sync(IncomingRequests, all.Where(f => f.State == FriendshipState.IncomingRequest).OrderByDescending(f => f.SinceUtc));
        Sync(OutgoingRequests, all.Where(f => f.State == FriendshipState.OutgoingRequest).OrderByDescending(f => f.SinceUtc));

        var online = Friends.Count(f => f.IsOnline);
        OnlineSummary = Friends.Count == 0 ? string.Empty : $"{online} of {Friends.Count} online";
        BadgeCount = IncomingRequests.Count + Friends.Sum(f => f.UnreadCount);

        if (ActiveFriend is { } active && Friends.FirstOrDefault(f => f.UserId == active.UserId) is { } current) ActiveFriend = current;
        else if (ActiveFriend is not null) CloseChat(); // unfriended

        OnPropertyChanged(nameof(HasFriends));
        OnPropertyChanged(nameof(HasIncoming));
        OnPropertyChanged(nameof(HasOutgoing));
        UpdateMyStatus();
    }

    /// <summary>Updates rows in place (keeps selection and avoids flicker), adds and removes the rest.</summary>
    private void Sync(ObservableCollection<FriendItemViewModel> target, IEnumerable<Friend> source)
    {
        var wanted = source.ToList();
        var existing = target.ToDictionary(f => f.UserId);
        target.Clear();
        foreach (var friend in wanted)
        {
            var row = existing.TryGetValue(friend.UserId, out var vm) ? vm : new FriendItemViewModel(this);
            row.Update(friend);
            row.SetInviteOption(InviteToPlayText);
            target.Add(row);
        }
    }

    internal Task AcceptAsync(FriendItemViewModel friend) => RunSafeAsync(() => _friends.AcceptAsync(friend.UserId));
    internal Task DeclineAsync(FriendItemViewModel friend) => RunSafeAsync(() => _friends.DeclineAsync(friend.UserId));
    internal Task RemoveAsync(FriendItemViewModel friend) => RunSafeAsync(() => _friends.RemoveAsync(friend.UserId));

    // ---- chat ------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChatOpen), nameof(IsListVisible))]
    private FriendItemViewModel? _activeFriend;

    public bool IsChatOpen => ActiveFriend is not null;
    public bool IsListVisible => ActiveFriend is null;

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];
    public IReadOnlyList<EmoteViewModel> Emotes { get; }

    [ObservableProperty] private string _draft = string.Empty;

    /// <summary>Raised after messages were added, so the view can scroll to the newest.</summary>
    public event Action? MessagesAppended;

    internal void OpenChat(FriendItemViewModel friend)
    {
        ActiveFriend = friend;
        _friends.SetActiveConversation(friend.UserId);
        Messages.Clear();
        foreach (var message in _friends.GetConversation(friend.UserId)) Messages.Add(new ChatMessageViewModel(message, friend.Name, this));
        MessagesAppended?.Invoke();
    }

    [RelayCommand]
    private void CloseChat()
    {
        ActiveFriend = null;
        _friends.SetActiveConversation(null);
        Messages.Clear();
        Draft = string.Empty;
    }

    // ---- a game or media box going with the next message ----------------------------------------------

    /// <summary>The game or video waiting above the message box (sent with the next Send), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAttachment), nameof(AttachmentTitle), nameof(AttachmentPicture), nameof(HasAttachmentPicture),
        nameof(AttachmentKind))]
    private ChatAttachment? _attachment;

    public bool HasAttachment => Attachment is not null;
    public string AttachmentTitle => Attachment?.Title ?? string.Empty;
    public string? AttachmentPicture => Attachment?.PictureUrl;
    public bool HasAttachmentPicture => !string.IsNullOrEmpty(Attachment?.PictureUrl);
    public string AttachmentKind => Attachment?.KindText ?? string.Empty;

    [RelayCommand]
    private void ClearAttachment() => Attachment = null;

    /// <summary>
    /// Puts a game or video above the message box. Dropped on a friend: their chat opens. Otherwise it waits in the open chat,
    /// or for a friend to be picked.
    /// </summary>
    internal void Attach(ChatAttachment attachment, FriendItemViewModel? friend)
    {
        if (!IsEnabled)
        {
            ErrorMessage = "Friends are switched off. Turn them on in Settings › Friends to send games and videos.";
            return;
        }
        Attachment = attachment;
        IsOpen = true;
        if (friend is not null && ActiveFriend?.UserId != friend.UserId) OpenChat(friend);
        else if (ActiveFriend is null) ShowToast(null, $"Pick a friend to send {attachment.Title} to");
    }

    [RelayCommand]
    private Task SendAsync() => RunSafeAsync(async () =>
    {
        if (ActiveFriend is not { } friend) return;
        if (Attachment is { } attachment)
        {
            Attachment = null;
            if (attachment.Game is { } game) await _friends.ShareGameAsync(friend.UserId, game);
            else if (attachment.Media is { } media) await _friends.ShareMediaAsync(friend.UserId, media);
        }
        if (string.IsNullOrWhiteSpace(Draft)) return;
        var text = Draft;
        Draft = string.Empty;
        await _friends.SendChatAsync(friend.UserId, text);
    });

    /// <summary>"Play" on a video or title a friend sent: the panel closes and it plays in SeaRaider's player.</summary>
    internal void PlayShared(SharedMediaLink media) => RunSafe(() =>
    {
        IsOpen = false;
        _player.Play(media.Url, media.Url, media.Title, null, media.ThumbnailUrl);
    });

    private void InsertEmote(Emote emote) =>
        Draft = string.IsNullOrEmpty(Draft) || Draft.EndsWith(' ') ? Draft + emote.Code + " " : $"{Draft} {emote.Code} ";

    internal void Retry(ChatMessageViewModel message) => _ = RunSafeAsync(() => _friends.RetryAsync(message.Id));

    /// <summary>"Open in store" on a shared game: the panel closes and the game's page (or a search for it) opens.</summary>
    internal void OpenShared(SharedGame game) => RunSafe(() =>
    {
        IsOpen = false;
        _opener.Open(game);
    });

    private void OnMessageAdded(ChatMessage message)
    {
        if (ActiveFriend?.UserId == message.FriendId)
        {
            Messages.Add(new ChatMessageViewModel(message, ActiveFriend.Name, this));
            MessagesAppended?.Invoke();
        }

        if (!message.IsOutgoing && (!IsOpen || ActiveFriend?.UserId != message.FriendId))
        {
            var from = Friends.FirstOrDefault(f => f.UserId == message.FriendId);
            var who = from?.Name ?? "A friend";
            ShowToast(from, message.Invite is { } invite
                ? invite.Kind == InviteKind.Game
                    ? $"{who} invited you to play {Truncate(invite.Title, 60)}"
                    : $"{who} invited you to watch {Truncate(invite.Title, 60)}"
                : message.Game is { } game
                    ? $"{who} shared {Truncate(Core.Catalog.DisplayText.Title(game.Title), 60)} with you"
                    : $"{who}: {Truncate(message.Text, 80)}");
        }
    }

    private void OnMessageUpdated(ChatMessage message)
    {
        if (Messages.FirstOrDefault(m => m.Id == message.Id) is { } row) row.Update(message);
    }

    // ---- toast -----------------------------------------------------------------------------------

    [ObservableProperty] private string? _toast;
    private FriendItemViewModel? _toastFriend;

    public bool HasToast => !string.IsNullOrEmpty(Toast);

    partial void OnToastChanged(string? value) => OnPropertyChanged(nameof(HasToast));

    private void ShowToast(FriendItemViewModel? friend, string text)
    {
        _toastFriend = friend;
        Toast = text;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    [RelayCommand]
    private void OpenToast()
    {
        Toast = null;
        IsOpen = true;
        if (_toastFriend is { } friend) OpenChat(friend);
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}

/// <summary>A friend or a request in the panel.</summary>
public sealed partial class FriendItemViewModel(FriendsViewModel owner) : ObservableObject
{
    public string UserId { get; private set; } = string.Empty;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string? _requestMessage;
    [ObservableProperty] private int _unreadCount;
    [ObservableProperty] private bool _isInGame;
    [ObservableProperty] private bool _isOnline;
    [ObservableProperty] private bool _isAway;
    [ObservableProperty] private string? _lastSeenText;

    /// <summary>Watching something in SeaRaider's player (Watch along).</summary>
    [NotifyPropertyChangedFor(nameof(ShowWatching))]
    [ObservableProperty] private bool _isWatching;

    /// <summary>The violet "Watching" look (In-Game's brass wins when both).</summary>
    public bool ShowWatching => IsWatching && !IsInGame;

    /// <summary>"Invite to play Hades II" while you're in a game, else null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInviteToPlay))]
    private string? _inviteToPlayText;

    public bool CanInviteToPlay => InviteToPlayText is not null && IsOnline;

    internal void SetInviteOption(string? text)
    {
        InviteToPlayText = text;
        OnPropertyChanged(nameof(CanInviteToPlay));
    }

    public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name.Trim()[..1].ToUpperInvariant();
    public bool HasUnread => UnreadCount > 0;
    public bool HasRequestMessage => !string.IsNullOrEmpty(RequestMessage);
    public bool IsOffline => !IsOnline;

    /// <summary>US style with an ordinal day: "Oct 20th, 1:05 PM".</summary>
    internal static string UsDateTime(DateTime local)
    {
        var day = local.Day;
        var suffix = day is >= 11 and <= 13 ? "th" : (day % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        var us = CultureInfo.GetCultureInfo("en-US");
        return $"{local.ToString("MMM", us)} {day}{suffix}, {local.ToString("h:mm tt", us)}";
    }

    public void Update(Friend friend)
    {
        UserId = friend.UserId;
        Name = friend.DisplayName;
        StatusText = friend.Status == UserStatus.Offline && friend.LastSeenUtc is { } seen
            ? $"Offline · last seen {UsDateTime(seen.ToLocalTime())}"
            : friend.StatusText;
        RequestMessage = friend.RequestMessage;
        UnreadCount = friend.UnreadCount;
        IsInGame = friend.Status == UserStatus.InGame;
        IsAway = friend.Status == UserStatus.Away;
        IsOnline = friend.Status != UserStatus.Offline;
        IsWatching = friend.Watching is not null && friend.Status != UserStatus.Offline;
        OnPropertyChanged(nameof(ShowWatching));
        foreach (var name in new[] { nameof(Initial), nameof(HasUnread), nameof(HasRequestMessage), nameof(IsOffline) })
            OnPropertyChanged(name);
    }

    [RelayCommand]
    private void Chat() => owner.OpenChat(this);

    [RelayCommand]
    private Task AcceptAsync() => owner.AcceptAsync(this);

    [RelayCommand]
    private Task DeclineAsync() => owner.DeclineAsync(this);

    [RelayCommand]
    private Task RemoveAsync() => owner.RemoveAsync(this);

    [RelayCommand]
    private void WatchAlong() => owner.WatchAlong(this);

    [RelayCommand]
    private Task InviteToPlayAsync() => owner.InviteToPlayAsync(this);
}

/// <summary>An emote in the picker.</summary>
public sealed partial class EmoteViewModel(Emote emote, Action<Emote> insert)
{
    public string Glyph => emote.Glyph;
    public string Tip => $"{emote.Name}  {emote.Code}";

    [RelayCommand]
    private void Insert() => insert(emote);
}

/// <summary>A piece of a chat message: text, or an animated emote.</summary>
public sealed record ChatSegmentViewModel(string Text, bool IsEmote, EmoteAnimation Animation)
{
    public bool IsText => !IsEmote;
    public bool IsBounce => IsEmote && Animation == EmoteAnimation.Bounce;
    public bool IsWobble => IsEmote && Animation == EmoteAnimation.Wobble;
    public bool IsPulse => IsEmote && Animation == EmoteAnimation.Pulse;
    public bool IsSpin => IsEmote && Animation == EmoteAnimation.Spin;
    public bool IsFloat => IsEmote && Animation == EmoteAnimation.Float;
    public bool IsShake => IsEmote && Animation == EmoteAnimation.Shake;
}

/// <summary>A chat bubble.</summary>
public sealed partial class ChatMessageViewModel : ObservableObject
{
    private readonly FriendsViewModel _owner;

    public ChatMessageViewModel(ChatMessage message, string friendName, FriendsViewModel owner)
    {
        _owner = owner;
        Shared = message.Game;
        Invite = message.Invite;
        Media = message.Media;
        FriendId = message.FriendId;
        SentAtUtc = message.SentAtUtc;
        Id = message.Id;
        FriendName = friendName;
        IsOutgoing = message.IsOutgoing;
        var segments = Core.Social.Emotes.Parse(message.Text);
        Segments = segments.Select(s => new ChatSegmentViewModel(s.Text, s.IsEmote, s.Emote?.Animation ?? EmoteAnimation.Bounce)).ToList();
        IsEmoteOnly = Core.Social.Emotes.IsEmoteOnly(segments);
        TimeText = message.SentAtUtc.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);
        if (Invite is { Kind: InviteKind.Game } game && IsIncoming) _isInstalledHere = owner.FindInstalled(game.Title) is not null;
        Update(message);
    }

    public string Id { get; }
    public string FriendId { get; }
    public DateTime SentAtUtc { get; }

    // ---- invite card ----

    private readonly bool _isInstalledHere;

    /// <summary>An invite to play or watch: shown as a card with Play / Watch along and Decline.</summary>
    public ChatInvite? Invite { get; }
    public bool IsInvite => Invite is not null;
    public bool IsWatchInvite => Invite?.Kind == InviteKind.Watch;
    public string InviteTitle => Invite?.Title ?? string.Empty;
    public string? InviteCover => Invite?.CoverUrl;
    public bool HasInviteCover => !string.IsNullOrEmpty(Invite?.CoverUrl);
    public string InviteHeading => Invite is null ? string.Empty
        : IsOutgoing
            ? Invite.Kind == InviteKind.Game ? $"You invited {FriendName} to play" : $"You invited {FriendName} to watch"
            : Invite.Kind == InviteKind.Game ? $"{FriendName} invited you to play" : $"{FriendName} invited you to watch along";
    public string InviteDetail => Invite is null ? string.Empty
        : Invite.Kind == InviteKind.Watch
            ? Invite.StartSeconds is { } s ? $"from {MediaTime.Format(s)}" : "live"
            : IsIncoming ? _isInstalledHere ? "Installed" : "Not installed: get it from the store" : string.Empty;
    public string AcceptText => Invite?.Kind == InviteKind.Watch ? "Watch along" : _isInstalledHere ? "Play" : "Get it";
    public bool IsExpired => DateTime.UtcNow - SentAtUtc > ChatInvite.Lifetime;

    [ObservableProperty] private InviteAnswer _answer;
    [ObservableProperty] private string? _inviteState;
    [ObservableProperty] private bool _canAccept;
    [ObservableProperty] private bool _canDecline;

    [RelayCommand]
    private Task AcceptInviteAsync() => _owner.AcceptInviteAsync(this);

    [RelayCommand]
    private Task DeclineInviteAsync() => _owner.DeclineInviteAsync(this);

    private void UpdateInvite(ChatMessage message)
    {
        if (Invite is null) return;
        Answer = message.Answer;
        var expired = IsExpired && Answer == InviteAnswer.None;
        InviteState = Answer switch
        {
            InviteAnswer.Accepted => IsOutgoing ? $"{FriendName} accepted" : "You accepted",
            InviteAnswer.Declined => IsOutgoing ? $"{FriendName} can't right now" : "You declined",
            _ when expired => "This invite has expired",
            _ => IsOutgoing ? $"Waiting for {FriendName}" : null,
        };
        CanAccept = IsIncoming && Answer != InviteAnswer.Declined && !expired;
        CanDecline = IsIncoming && Answer == InviteAnswer.None && !expired;
    }
    public string FriendName { get; }

    /// <summary>A game shared in this message: shown as a card instead of the text.</summary>
    public SharedGame? Shared { get; }
    public bool IsShare => Shared is not null;
    public bool IsText => Shared is null && Invite is null && Media is null;

    /// <summary>A video, stream or title sent from the Media tab: a card with "Play".</summary>
    public SharedMediaLink? Media { get; }
    public bool IsMediaShare => Media is not null;
    public string MediaTitle => Media?.Title ?? string.Empty;
    public string? MediaPicture => Media is { IsMatchup: false } ? Media.ThumbnailUrl : null;
    public bool HasMediaPicture => !string.IsNullOrEmpty(MediaPicture);
    public bool IsMediaMatchup => Media?.IsMatchup == true;
    public string? MediaBadge1 => Media?.ThumbnailUrl;
    public string? MediaBadge2 => Media?.ThumbnailUrl2;
    public bool IsMediaLive => Media?.IsLive == true;
    public string MediaDetail => Media?.Detail ?? string.Empty;
    public string MediaHeading => IsOutgoing ? "You sent something to watch" : $"{FriendName} sent you something to watch";

    [RelayCommand]
    private void PlayMedia()
    {
        if (Media is not null) _owner.PlayShared(Media);
    }
    public string SharedTitle => Shared is null ? string.Empty : Core.Catalog.DisplayText.Title(Shared.Title);
    public string? SharedCover => Shared?.CoverUrl;
    public bool HasSharedCover => !string.IsNullOrEmpty(Shared?.CoverUrl);
    public string SharedDetail => Shared is null ? string.Empty
        : string.Join(" · ", new[] { Shared.SourceName, Shared.Size }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public string ShareHeading => IsOutgoing ? "You shared a game" : $"{FriendName} shared a game";

    [RelayCommand]
    private void OpenShared()
    {
        if (Shared is not null) _owner.OpenShared(Shared);
    }
    public bool IsOutgoing { get; }
    public bool IsIncoming => !IsOutgoing;
    public IReadOnlyList<ChatSegmentViewModel> Segments { get; }

    /// <summary>Only emotes: shown large.</summary>
    public bool IsEmoteOnly { get; }
    public bool IsNormal => !IsEmoteOnly;
    public string TimeText { get; }

    [ObservableProperty] private string? _deliveryText;
    [ObservableProperty] private bool _canRetry;

    public void Update(ChatMessage message)
    {
        UpdateInvite(message);
        CanRetry = message.IsOutgoing && message.Failed;
        DeliveryText = !message.IsOutgoing
            ? null
            : message.Failed
                ? "Not sent"
                : message.Delivered
                    ? "Delivered"
                    : message.Queued
                        ? $"Waiting for {FriendName} to come online"
                        : "Sending…";
    }

    [RelayCommand]
    private void Retry() => _owner.Retry(this);
}
