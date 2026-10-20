using System.Text.Json;
using Microsoft.Extensions.Logging;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Social;

/// <summary>Friends, presence and chat.</summary>
public interface IFriendsService : IAsyncDisposable
{
    /// <summary>The friend list, a request or someone's presence changed (background thread).</summary>
    event EventHandler? FriendsChanged;

    /// <summary>A message was added to a conversation, incoming or outgoing (background thread).</summary>
    event EventHandler<ChatMessage>? MessageAdded;

    /// <summary>A message's delivery state changed (background thread).</summary>
    event EventHandler<ChatMessage>? MessageUpdated;

    /// <summary>Connection to the relay changed (background thread).</summary>
    event EventHandler? ConnectionChanged;

    SocialProfile Profile { get; }
    SocialConnectionState ConnectionState { get; }

    /// <summary>Why the relay connection is down, when it is.</summary>
    string? ConnectionError { get; }

    /// <summary>What friends currently see for you.</summary>
    Presence MyPresence { get; }

    /// <summary>Your own status changed (a game started or stopped, Away, Appear offline) — any thread.</summary>
    event EventHandler? MyPresenceChanged;

    IReadOnlyList<Friend> Friends { get; }

    Task StartAsync();

    /// <summary>Friends are switched on in Settings.</summary>
    bool IsEnabled { get; }

    /// <summary>Relay address in use (custom or the build default), or null.</summary>
    string? RelayAddress { get; }

    IReadOnlyList<ChatMessage> GetConversation(string friendId, int max = 200);

    /// <summary>The conversation open in the UI (its messages count as read).</summary>
    void SetActiveConversation(string? friendId);

    Task SetDisplayNameAsync(string name);
    Task SetPresenceModeAsync(PresenceMode mode);

    /// <exception cref="ArgumentException">Not a friend code, or your own.</exception>
    /// <exception cref="InvalidOperationException">Already friends, or not connected.</exception>
    Task SendFriendRequestAsync(string friendCode, string? message);
    Task AcceptAsync(string userId);
    Task DeclineAsync(string userId);
    Task RemoveAsync(string userId);

    /// <summary>Sends a chat message (emote codes like ":gg:" included). Stored even when it can't be sent yet.</summary>
    Task<ChatMessage> SendChatAsync(string friendId, string text);

    /// <summary>Sends a friend a game's store page (a card in the chat with "Open in store").</summary>
    Task<ChatMessage> ShareGameAsync(string friendId, SharedGame game);

    /// <summary>Re-sends a message that failed while offline.</summary>
    Task RetryAsync(string messageId);

    /// <summary>Invites a friend to play a game or to watch along (a card in their chat with Play / Watch along, Decline).</summary>
    Task<ChatMessage> SendInviteAsync(string friendId, ChatInvite invite);

    /// <summary>Answers an invite that arrived in chat; the friend sees the answer.</summary>
    Task AnswerInviteAsync(string messageId, bool accept);

    /// <summary>A friend answered one of your invites (background thread).</summary>
    event EventHandler<ChatMessage>? InviteAnswered;
}

/// <summary>
/// The friends system, over the relay transport (<see cref="IFriendsTransport"/>):
/// <list type="bullet">
///   <item><b>Identity</b>: a random friend code (<see cref="FriendCode"/>) plus a secret only this PC knows; the relay
///         ties the code to the secret the first time it sees it, so nobody else can sign in as you.</item>
///   <item><b>Requests</b>: send by friend code; the other side accepts or declines. Requests to someone offline wait
///         on the relay. Two people who add each other become friends straight away.</item>
///   <item><b>Presence</b>: <see cref="PresenceMonitor"/> decides Online / Away / In-Game: title / Offline; changes are
///         pushed to every friend. The relay tells us when friends connect or drop (only for mutual friends), and
///         newly-online friends are sent our status.</item>
///   <item><b>Chat</b>: messages are stored on both PCs; the relay queues them while the friend is offline; the
///         friend's app acknowledges delivery. Only friends can message you.</item>
/// </list>
/// </summary>
public sealed class FriendsService : IFriendsService
{
    private const int MaxNameLength = 32;
    private const int MaxMessageLength = 2_000;
    private const int MaxPendingRequests = 50;

    private readonly SocialStore _store;
    private readonly IFriendsTransport _transport;
    private readonly PresenceMonitor _presence;
    private readonly ILibraryRepository _repository;
    private readonly ILogger<FriendsService> _logger;
    private readonly Dictionary<string, Friend> _friends = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private string? _activeConversation;
    private string? _relayUrl;
    private bool _started;

    public FriendsService(SocialStore store, IFriendsTransport transport, PresenceMonitor presence, ILibraryRepository repository,
        ILogger<FriendsService> logger)
    {
        _store = store;
        _transport = transport;
        _presence = presence;
        _repository = repository;
        _logger = logger;
        Profile = store.GetOrCreateProfile();
    }

    public event EventHandler? FriendsChanged;
    public event EventHandler<ChatMessage>? MessageAdded;
    public event EventHandler<ChatMessage>? MessageUpdated;
    public event EventHandler? ConnectionChanged;
    public event EventHandler<ChatMessage>? InviteAnswered;

    public SocialProfile Profile { get; }
    public SocialConnectionState ConnectionState => _transport.State;
    public string? ConnectionError { get; private set; }
    public Presence MyPresence => _presence.Current;

    public event EventHandler? MyPresenceChanged;

    public IReadOnlyList<Friend> Friends
    {
        get { lock (_lock) return _friends.Values.ToList(); }
    }

    // =============================================================================================
    // Lifecycle
    // =============================================================================================

    public Task StartAsync()
    {
        if (_started) return Task.CompletedTask;
        _started = true;

        lock (_lock)
        {
            foreach (var friend in _store.GetFriends()) _friends[friend.UserId] = friend;
        }

        _presence.Mode = Profile.PresenceMode;
        _presence.Changed += (_, presence) =>
        {
            MyPresenceChanged?.Invoke(this, EventArgs.Empty); // your own panel shows it too
            _ = BroadcastPresenceAsync(presence);
        };
        _transport.FrameReceived += (_, frame) => OnFrame(frame);
        _transport.StateChanged += (_, change) =>
        {
            ConnectionError = change.Error;
            if (change.State != SocialConnectionState.Connected) MarkAllOffline();
            ConnectionChanged?.Invoke(this, EventArgs.Empty);
        };
        _repository.SettingsChanged += (_, settings) =>
        {
            _presence.Update(); // "share what I'm playing" may have changed
            var enabledChanged = settings.FriendsEnabled != IsEnabled;
            var relayChanged = !string.Equals(RelayDefaults.Effective(settings.RelayUrl), _relayUrl, StringComparison.OrdinalIgnoreCase);
            if (enabledChanged || relayChanged) _ = ReconnectAsync();
        };

        Connect();
        return Task.CompletedTask;
    }

    /// <summary>Friends are switched on in Settings (when off: no socket, no presence polling).</summary>
    public bool IsEnabled { get; private set; }

    /// <summary>The relay in use: the user's address, else the build's default (<see cref="RelayDefaults"/>).</summary>
    public string? RelayAddress => _relayUrl;

    private void Connect()
    {
        var settings = _repository.GetSettings();
        IsEnabled = settings.FriendsEnabled;
        _presence.IsActive = IsEnabled; // idle polling stops too
        if (!IsEnabled)
        {
            _relayUrl = null;
            ConnectionError = null;
            ConnectionChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        _relayUrl = RelayDefaults.Effective(settings.RelayUrl);
        if (!TryParseRelay(_relayUrl, out var relay))
        {
            ConnectionError = string.IsNullOrEmpty(_relayUrl) ? null : $"\"{_relayUrl}\" isn't a relay address (use ws:// or wss://).";
            ConnectionChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        _transport.Start(relay, Profile);
    }

    private async Task ReconnectAsync()
    {
        await _transport.StopAsync().ConfigureAwait(false);
        MarkAllOffline();
        Connect();
    }

    /// <summary>Accepts ws:// and wss:// (http/https are converted); a bare host gets wss:// and /ws.</summary>
    public static bool TryParseRelay(string? text, out Uri relay)
    {
        relay = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.Trim();
        if (!value.Contains("://", StringComparison.Ordinal)) value = "wss://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;

        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme switch { "http" => "ws", "https" => "wss", var s => s },
        };
        if (builder.Scheme is not ("ws" or "wss")) return false;
        if (uri.IsDefaultPort) builder.Port = -1;
        if (builder.Path is "" or "/") builder.Path = "/ws";
        relay = builder.Uri;
        return true;
    }

    /// <summary>Disconnects; the store, transport and presence monitor are disposed by the container.</summary>
    public async ValueTask DisposeAsync() => await _transport.StopAsync().ConfigureAwait(false);

    // =============================================================================================
    // Commands
    // =============================================================================================

    public IReadOnlyList<ChatMessage> GetConversation(string friendId, int max = 200) => _store.GetConversation(friendId, max);

    public void SetActiveConversation(string? friendId)
    {
        _activeConversation = friendId;
        if (friendId is null) return;
        Friend? changed = null;
        lock (_lock)
        {
            if (_friends.TryGetValue(friendId, out var friend) && friend.UnreadCount > 0)
            {
                friend.UnreadCount = 0;
                _store.UpsertFriend(friend);
                changed = friend;
            }
        }
        if (changed is not null) FriendsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetDisplayNameAsync(string name)
    {
        var clean = CleanName(name);
        if (clean.Length == 0) throw new ArgumentException("Enter a name friends will recognise.");
        Profile.DisplayName = clean;
        _store.SaveProfile(Profile);
        await BroadcastPresenceAsync(_presence.Current).ConfigureAwait(false);
    }

    public async Task SetPresenceModeAsync(PresenceMode mode)
    {
        Profile.PresenceMode = mode;
        _store.SaveProfile(Profile);
        await _transport.SendAsync(new RelayFrame { Type = "visibility", Invisible = mode == PresenceMode.Invisible }).ConfigureAwait(false);
        _presence.Mode = mode; // raises Changed → broadcast
    }

    public async Task SendFriendRequestAsync(string friendCode, string? message)
    {
        var userId = FriendCode.Normalize(friendCode) ?? throw new ArgumentException("That isn't a friend code. Codes look like SR-7K3QM-9XPAD.");
        if (userId == Profile.UserId) throw new ArgumentException("That's your own friend code.");
        RequireConnected();

        Friend? existing;
        lock (_lock) _friends.TryGetValue(userId, out existing);
        switch (existing?.State)
        {
            case FriendshipState.Friend:
                throw new InvalidOperationException($"You and {existing.DisplayName} are already friends.");
            case FriendshipState.IncomingRequest:
                await AcceptAsync(userId).ConfigureAwait(false); // they asked first
                return;
        }

        var friend = existing ?? new Friend { UserId = userId, DisplayName = userId };
        friend.State = FriendshipState.OutgoingRequest;
        friend.SinceUtc = DateTime.UtcNow;
        Save(friend);

        var note = string.IsNullOrWhiteSpace(message) ? null : message.Trim()[..Math.Min(200, message.Trim().Length)];
        await _transport.SendToAsync(userId, MessageKinds.FriendRequest, new FriendRequestPayload(Profile.DisplayName, note))
            .ConfigureAwait(false);
        FriendsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task AcceptAsync(string userId)
    {
        RequireConnected();
        Friend? friend;
        lock (_lock) _friends.TryGetValue(userId, out friend);
        if (friend is null) return;

        friend.State = FriendshipState.Friend;
        friend.RequestMessage = null;
        friend.SinceUtc = DateTime.UtcNow;
        Save(friend);

        await _transport.SendToAsync(userId, MessageKinds.FriendAccept, new FriendAcceptPayload(Profile.DisplayName)).ConfigureAwait(false);
        await SubscribeAsync().ConfigureAwait(false);
        await SendPresenceAsync(userId, _presence.Current).ConfigureAwait(false);
        FriendsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeclineAsync(string userId)
    {
        Forget(userId);
        await _transport.SendToAsync(userId, MessageKinds.FriendDecline, new { }).ConfigureAwait(false);
        FriendsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RemoveAsync(string userId)
    {
        Forget(userId);
        _store.DeleteConversation(userId);
        await _transport.SendToAsync(userId, MessageKinds.FriendRemove, new { }).ConfigureAwait(false);
        await SubscribeAsync().ConfigureAwait(false);
        FriendsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<ChatMessage> SendChatAsync(string friendId, string text)
    {
        var clean = (text ?? string.Empty).Trim();
        if (clean.Length == 0) throw new ArgumentException("Type a message first.");
        if (clean.Length > MaxMessageLength) clean = clean[..MaxMessageLength];

        lock (_lock)
        {
            if (!_friends.TryGetValue(friendId, out var friend) || friend.State != FriendshipState.Friend)
                throw new InvalidOperationException("You can only message friends.");
        }

        var message = new ChatMessage { FriendId = friendId, IsOutgoing = true, Text = clean, SentAtUtc = DateTime.UtcNow };
        _store.AddMessage(message);
        MessageAdded?.Invoke(this, message);
        await TrySendAsync(message).ConfigureAwait(false);
        return message;
    }

    public async Task<ChatMessage> ShareGameAsync(string friendId, SharedGame game)
    {
        var clean = Clean(game) ?? throw new ArgumentException("This game has no page to share.");
        lock (_lock)
        {
            if (!_friends.TryGetValue(friendId, out var friend) || friend.State != FriendshipState.Friend)
                throw new InvalidOperationException("You can only share games with friends.");
        }

        var message = new ChatMessage
        {
            FriendId = friendId,
            IsOutgoing = true,
            Text = $"Shared a game: {clean.Title} {clean.PageUrl}", // what older versions show
            SentAtUtc = DateTime.UtcNow,
            Game = clean,
        };
        _store.AddMessage(message);
        MessageAdded?.Invoke(this, message);
        await TrySendAsync(message).ConfigureAwait(false);
        return message;
    }

    /// <summary>A shared game with sane lengths and only web addresses (it came from someone else's app).</summary>
    private static SharedGame? Clean(SharedGame? game)
    {
        if (game is null || string.IsNullOrWhiteSpace(game.Title) || !IsWebUrl(game.PageUrl)) return null;
        static string? Cut(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length > max ? s.Trim()[..max] : s.Trim();
        return new SharedGame(Cut(game.Title, 160)!, game.PageUrl.Trim(), IsWebUrl(game.CoverUrl) ? game.CoverUrl!.Trim() : null,
            Cut(game.SourceName, 60), Cut(game.Size, 30));
    }

    private static bool IsWebUrl(string? url) =>
        url is { Length: <= 2048 } && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    public async Task RetryAsync(string messageId)
    {
        var message = _store.GetMessage(messageId);
        if (message is not { IsOutgoing: true, Failed: true }) return;
        await TrySendAsync(message).ConfigureAwait(false);
    }

    public async Task<ChatMessage> SendInviteAsync(string friendId, ChatInvite invite)
    {
        var clean = Clean(invite) ?? throw new ArgumentException("There's nothing to invite to.");
        lock (_lock)
        {
            if (!_friends.TryGetValue(friendId, out var friend) || friend.State != FriendshipState.Friend)
                throw new InvalidOperationException("You can only invite friends.");
        }

        var message = new ChatMessage
        {
            FriendId = friendId,
            IsOutgoing = true,
            // What older versions show.
            Text = clean.Kind == InviteKind.Game
                ? $"🎮 Come play {clean.Title} with me!"
                : $"📺 Watch {clean.Title} with me: {clean.MediaUrl}",
            SentAtUtc = DateTime.UtcNow,
            Invite = clean,
        };
        _store.AddMessage(message);
        MessageAdded?.Invoke(this, message);
        await TrySendAsync(message).ConfigureAwait(false);
        return message;
    }

    public async Task AnswerInviteAsync(string messageId, bool accept)
    {
        if (_store.GetMessage(messageId) is not { IsOutgoing: false, Invite: not null } message) return;
        message.Answer = accept ? InviteAnswer.Accepted : InviteAnswer.Declined;
        _store.UpdateMessage(message);
        MessageUpdated?.Invoke(this, message);
        await _transport.SendToAsync(message.FriendId, MessageKinds.InviteReply, new InviteReplyPayload(message.Id, accept)).ConfigureAwait(false);
    }

    /// <summary>An invite with sane lengths and only web addresses (it came from someone else's app).</summary>
    private static ChatInvite? Clean(ChatInvite? invite)
    {
        if (invite is null || string.IsNullOrWhiteSpace(invite.Title) || !Enum.IsDefined(invite.Kind)) return null;
        static string? Cut(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length > max ? s.Trim()[..max] : s.Trim();
        var clean = new ChatInvite
        {
            Kind = invite.Kind,
            Title = Cut(invite.Title, 160)!,
            PageUrl = IsWebUrl(invite.PageUrl) ? invite.PageUrl!.Trim() : null,
            CoverUrl = IsWebUrl(invite.CoverUrl) ? invite.CoverUrl!.Trim() : null,
            SourceName = Cut(invite.SourceName, 60),
            MediaUrl = IsWebUrl(invite.MediaUrl) ? invite.MediaUrl!.Trim() : null,
            StartSeconds = invite.StartSeconds is > 0 and <= MediaTime.MaxSeconds ? invite.StartSeconds : null,
        };
        return clean.Kind == InviteKind.Watch && clean.MediaUrl is null ? null : clean;
    }

    /// <summary>What a friend says they're watching, checked (title length, web address, sane position).</summary>
    private static MediaActivity? Clean(MediaActivity? media)
    {
        if (media is null || string.IsNullOrWhiteSpace(media.Title) || !IsWebUrl(media.Url)) return null;
        var title = media.Title.Trim();
        return new MediaActivity
        {
            Title = title.Length > 160 ? title[..160] : title,
            Url = media.Url.Trim(),
            ThumbnailUrl = IsWebUrl(media.ThumbnailUrl) ? media.ThumbnailUrl!.Trim() : null,
            Position = double.IsFinite(media.Position) ? Math.Clamp(media.Position, 0, MediaTime.MaxSeconds) : 0,
            Playing = media.Playing,
            IsLive = media.IsLive,
            AtUtc = DateTime.UtcNow, // their position is as of now (sent the moment it was measured); our clock from here
        };
    }

    private async Task TrySendAsync(ChatMessage message)
    {
        var sent = await _transport.SendToAsync(message.FriendId, MessageKinds.Chat,
                new ChatPayload(message.Text, message.SentAtUtc, message.Game, message.Invite), message.Id)
            .ConfigureAwait(false);
        message.Failed = !sent;
        _store.UpdateMessage(message);
        MessageUpdated?.Invoke(this, message);
    }

    // =============================================================================================
    // Incoming frames
    // =============================================================================================

    private void OnFrame(RelayFrame frame)
    {
        switch (frame.Type)
        {
            case "welcome":
                _ = OnWelcomeAsync(frame);
                break;
            case "online" when frame.User is { } user:
                SetPresence(user, UserStatus.Online, null, onlyIfOffline: true);
                _ = SendPresenceAsync(user, _presence.Current);
                break;
            case "offline" when frame.User is { } user:
                SetPresence(user, UserStatus.Offline, null, onlyIfOffline: false);
                break;
            case "sent" when frame.Id is { } id:
                if (_store.GetMessage(id) is { IsOutgoing: true, Delivered: false } queued)
                {
                    queued.Queued = frame.Queued == true;
                    queued.Failed = false;
                    _store.UpdateMessage(queued);
                    MessageUpdated?.Invoke(this, queued);
                }
                break;
            case "deliver" when frame.From is { } from && frame.Kind is { } kind:
                HandleDelivery(from, kind, frame);
                break;
            case "error":
                _logger.LogInformation("Relay error {Code}: {Message}", frame.Code, frame.Message);
                if (frame.Code is "rate" or "too-big")
                {
                    ConnectionError = frame.Message;
                    ConnectionChanged?.Invoke(this, EventArgs.Empty);
                }
                break;
        }
    }

    private async Task OnWelcomeAsync(RelayFrame frame)
    {
        ConnectionError = null;
        await SubscribeAsync().ConfigureAwait(false);
        foreach (var user in frame.Online ?? new List<string>())
        {
            SetPresence(user, UserStatus.Online, null, onlyIfOffline: true);
            await SendPresenceAsync(user, _presence.Current).ConfigureAwait(false);
        }
        ConnectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void HandleDelivery(string from, string kind, RelayFrame frame)
    {
        Friend? friend;
        lock (_lock) _friends.TryGetValue(from, out friend);
        var isFriend = friend?.State == FriendshipState.Friend;

        switch (kind)
        {
            case MessageKinds.FriendRequest when Read<FriendRequestPayload>(frame) is { } request:
                OnFriendRequest(from, friend, request);
                break;

            case MessageKinds.FriendAccept when friend is { State: FriendshipState.OutgoingRequest or FriendshipState.Friend }:
                friend.State = FriendshipState.Friend;
                friend.DisplayName = CleanName(Read<FriendAcceptPayload>(frame)?.Name) is { Length: > 0 } accepted ? accepted : friend.DisplayName;
                Save(friend);
                _ = SubscribeAsync();
                _ = SendPresenceAsync(from, _presence.Current);
                FriendsChanged?.Invoke(this, EventArgs.Empty);
                break;

            case MessageKinds.FriendDecline when friend is { State: FriendshipState.OutgoingRequest }:
            case MessageKinds.FriendRemove when friend is not null:
                Forget(from);
                FriendsChanged?.Invoke(this, EventArgs.Empty);
                break;

            case MessageKinds.Presence when isFriend && Read<PresencePayload>(frame) is { } presence:
                if (CleanName(presence.Name) is { Length: > 0 } name && name != friend!.DisplayName)
                {
                    friend.DisplayName = name;
                    _store.UpsertFriend(friend);
                }
                SetPresence(from, presence.Status, presence.Game, onlyIfOffline: false, Clean(presence.Media));
                break;

            case MessageKinds.InviteReply when Read<InviteReplyPayload>(frame) is { } reply:
                if (_store.GetMessage(reply.InviteId) is { IsOutgoing: true, Invite: not null } invite && invite.FriendId == from)
                {
                    invite.Answer = reply.Accepted ? InviteAnswer.Accepted : InviteAnswer.Declined;
                    _store.UpdateMessage(invite);
                    MessageUpdated?.Invoke(this, invite);
                    InviteAnswered?.Invoke(this, invite);
                }
                break;

            case MessageKinds.Chat when isFriend && Read<ChatPayload>(frame) is { } chat && frame.Id is { } id:
                OnChat(friend!, id, chat);
                break;

            case MessageKinds.ChatAck when Read<ChatAckPayload>(frame) is { } ack:
                if (_store.GetMessage(ack.Id) is { IsOutgoing: true } delivered && delivered.FriendId == from)
                {
                    delivered.Delivered = true;
                    delivered.Queued = false;
                    delivered.Failed = false;
                    _store.UpdateMessage(delivered);
                    MessageUpdated?.Invoke(this, delivered);
                }
                break;
        }
    }

    private void OnFriendRequest(string from, Friend? existing, FriendRequestPayload request)
    {
        var name = CleanName(request.Name);
        switch (existing?.State)
        {
            case FriendshipState.Friend:
                // They lost us (reinstall?): confirm again.
                _ = _transport.SendToAsync(from, MessageKinds.FriendAccept, new FriendAcceptPayload(Profile.DisplayName));
                return;
            case FriendshipState.OutgoingRequest:
                // Both asked: friends.
                existing.DisplayName = name.Length > 0 ? name : existing.DisplayName;
                _ = AcceptAsync(from);
                return;
        }

        lock (_lock)
        {
            if (existing is null && _friends.Values.Count(f => f.State == FriendshipState.IncomingRequest) >= MaxPendingRequests) return; // spam guard
        }

        var friend = existing ?? new Friend { UserId = from };
        friend.State = FriendshipState.IncomingRequest;
        friend.DisplayName = name.Length > 0 ? name : from;
        friend.RequestMessage = request.Message is { Length: > 0 } note ? note[..Math.Min(200, note.Length)] : null;
        friend.SinceUtc = DateTime.UtcNow;
        Save(friend);
        FriendsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnChat(Friend friend, string id, ChatPayload chat)
    {
        if (_store.GetMessage(id) is not null) return; // redelivered after a reconnect

        var text = (chat.Text ?? string.Empty).Trim();
        if (text.Length == 0) return;
        var now = DateTime.UtcNow;
        var message = new ChatMessage
        {
            Id = id,
            FriendId = friend.UserId,
            IsOutgoing = false,
            Text = text.Length > MaxMessageLength ? text[..MaxMessageLength] : text,
            // Their clock may be off; never show a message from the future.
            SentAtUtc = chat.SentAt > now || chat.SentAt < now.AddDays(-30) ? now : chat.SentAt,
            Delivered = true,
            Game = Clean(chat.Game),
            Invite = Clean(chat.Invite),
        };
        _store.AddMessage(message);

        if (_activeConversation != friend.UserId)
        {
            lock (_lock) friend.UnreadCount++;
            _store.UpsertFriend(friend);
        }

        _ = _transport.SendToAsync(friend.UserId, MessageKinds.ChatAck, new ChatAckPayload(id));
        MessageAdded?.Invoke(this, message);
        FriendsChanged?.Invoke(this, EventArgs.Empty);
    }

    // =============================================================================================
    // Presence
    // =============================================================================================

    private async Task BroadcastPresenceAsync(Presence presence)
    {
        foreach (var friend in Friends.Where(f => f.State == FriendshipState.Friend && f.Status != UserStatus.Offline))
            await SendPresenceAsync(friend.UserId, presence).ConfigureAwait(false);
    }

    private Task<bool> SendPresenceAsync(string userId, Presence presence)
    {
        bool isFriend;
        lock (_lock) isFriend = _friends.TryGetValue(userId, out var f) && f.State == FriendshipState.Friend;
        return isFriend
            ? _transport.SendToAsync(userId, MessageKinds.Presence,
                new PresencePayload(Profile.DisplayName, presence.Status, presence.GameTitle, presence.Media?.Snapshot(DateTime.UtcNow)))
            : Task.FromResult(false);
    }

    /// <summary>Tells the relay who our friends are, so it announces them when they come and go.</summary>
    private Task<bool> SubscribeAsync()
    {
        var ids = Friends.Where(f => f.State == FriendshipState.Friend).Select(f => f.UserId).ToList();
        return _transport.SendAsync(new RelayFrame { Type = "subscribe", Users = ids });
    }

    private void SetPresence(string userId, UserStatus status, string? game, bool onlyIfOffline, MediaActivity? media = null)
    {
        lock (_lock)
        {
            if (!_friends.TryGetValue(userId, out var friend) || friend.State != FriendshipState.Friend) return;
            if (onlyIfOffline && friend.Status != UserStatus.Offline) return;
            friend.Status = status;
            friend.GameTitle = status == UserStatus.InGame ? (game is { Length: > 0 } g ? g[..Math.Min(80, g.Length)] : null) : null;
            friend.Watching = status == UserStatus.Offline ? null : media;
            if (status == UserStatus.Offline)
            {
                friend.LastSeenUtc = DateTime.UtcNow;
                _store.UpsertFriend(friend);
            }
        }
        FriendsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void MarkAllOffline()
    {
        lock (_lock)
        {
            foreach (var friend in _friends.Values)
            {
                friend.Status = UserStatus.Offline;
                friend.GameTitle = null;
                friend.Watching = null;
            }
        }
        FriendsChanged?.Invoke(this, EventArgs.Empty);
    }

    // =============================================================================================
    // Helpers
    // =============================================================================================

    private void Save(Friend friend)
    {
        lock (_lock) _friends[friend.UserId] = friend;
        _store.UpsertFriend(friend);
    }

    private void Forget(string userId)
    {
        lock (_lock) _friends.Remove(userId);
        _store.DeleteFriend(userId);
    }

    private void RequireConnected()
    {
        if (!IsEnabled) throw new InvalidOperationException("Friends are turned off in Settings › Friends.");
        if (_transport.State != SocialConnectionState.Connected)
            throw new InvalidOperationException(string.IsNullOrEmpty(_relayUrl)
                ? "Set a friends relay address in Settings › Friends first."
                : "Not connected to the friends relay right now. Try again in a moment.");
    }

    private static T? Read<T>(RelayFrame frame) where T : class
    {
        if (frame.Payload is not { } payload) return null;
        try { return payload.Deserialize<T>(RelayFrame.Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>Single line, no control characters, at most 32 characters.</summary>
    private static string CleanName(string? name)
    {
        var clean = new string((name ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length > MaxNameLength ? clean[..MaxNameLength] : clean;
    }
}
