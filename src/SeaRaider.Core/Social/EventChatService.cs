using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SeaRaider.Core.Scraping;

namespace SeaRaider.Core.Social;

/// <summary>One line in an event's chat.</summary>
public sealed record EventChatLine(string Id, string FromId, string Name, string Text, DateTime AtUtc, bool IsMine);

/// <summary>
/// Event chat: a public chat room for each event being watched in SeaRaider's player, shared by everyone on the same relay
/// who's watching it (not only friends). Uses the friends relay connection (Settings › Friends), with its
/// "room-join" / "room-say" frames (<see cref="RelayFrame"/>).
///
/// <para><b>Which room</b> (<see cref="RoomFor"/>): a match ("Bayern Munich vs Arsenal") is one room for everyone watching it
/// that day, whatever site or stream they opened it from; anything else is one room per page or stream address. Rooms are
/// named by a hash, so the relay never sees what's being watched.</para>
///
/// <para>One room at a time: playing something else moves to its room. After a reconnect the room is joined again.
/// Events are raised on background threads.</para>
/// </summary>
public sealed class EventChatService
{
    public const int MaxLineLength = 300;

    private readonly IFriendsTransport _transport;
    private readonly IFriendsService _friends;
    private readonly ILogger<EventChatService> _logger;
    private readonly object _lock = new();

    public EventChatService(IFriendsTransport transport, IFriendsService friends, ILogger<EventChatService> logger)
    {
        _transport = transport;
        _friends = friends;
        _logger = logger;
        _transport.FrameReceived += (_, frame) => OnFrame(frame);
        _friends.ConnectionChanged += (_, _) =>
        {
            if (_friends.ConnectionState == SocialConnectionState.Connected && Room is not null) _ = SendJoinAsync();
            StateChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>The room joined (a hash), or null.</summary>
    public string? Room { get; private set; }

    /// <summary>What the room is about ("Bayern Munich vs Arsenal").</summary>
    public string? Title { get; private set; }

    /// <summary>How many are in the room (including you), as the relay last said.</summary>
    public int Watching { get; private set; }

    /// <summary>The relay can carry event chat: friends are on and connected.</summary>
    public bool IsAvailable => _friends.IsEnabled && _friends.ConnectionState == SocialConnectionState.Connected;

    /// <summary>The relay answered that it doesn't know event chat (an older relay).</summary>
    public bool RelayTooOld { get; private set; }

    public event EventHandler<EventChatLine>? LineReceived;

    /// <summary>The room, the count or the connection changed.</summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// The room for an event: a match by its two sides and the day (so every site's stream of it shares one room), anything
    /// else by its page or stream address. A hash, so the relay doesn't learn what's being watched.
    /// </summary>
    public static string RoomFor(string title, string? pageUrl, string playUrl, DateTime? utcNow = null)
    {
        string key;
        if (MediaTitleParser.Matchup(title) is { } match)
        {
            var day = (utcNow ?? DateTime.UtcNow).ToString("yyyy-MM-dd");
            static string Norm(string s) => string.Join(' ', Catalog.TitleNormalizer.Fold(s.ToLowerInvariant())
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));
            // Either order: "A vs B" and "B vs A" are the same match.
            var sides = new[] { Norm(match.Home), Norm(match.Away) }.Order(StringComparer.Ordinal);
            key = "match|" + string.Join('|', sides) + "|" + day;
        }
        else
        {
            var address = MediaStreams.WithoutHint(pageUrl ?? playUrl);
            key = Uri.TryCreate(address, UriKind.Absolute, out var uri)
                ? "page|" + uri.Host.ToLowerInvariant().Replace("www.", string.Empty) + uri.AbsolutePath.TrimEnd('/').ToLowerInvariant()
                : "page|" + address.ToLowerInvariant();
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return "ev:" + Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    /// <summary>Joins the room for what's playing (leaving the previous one). Nothing happens when it's already the room.</summary>
    public async Task JoinAsync(string room, string title)
    {
        string? previous;
        lock (_lock)
        {
            if (Room == room) return;
            previous = Room;
            Room = room;
            Title = title;
            Watching = 0;
        }
        if (previous is not null) await _transport.SendAsync(new RelayFrame { Type = "room-leave", Room = previous }).ConfigureAwait(false);
        await SendJoinAsync().ConfigureAwait(false);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Leaves the room (the player closed).</summary>
    public async Task LeaveAsync()
    {
        string? previous;
        lock (_lock)
        {
            previous = Room;
            Room = null;
            Title = null;
            Watching = 0;
        }
        if (previous is not null) await _transport.SendAsync(new RelayFrame { Type = "room-leave", Room = previous }).ConfigureAwait(false);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Says something in the room.</summary>
    /// <exception cref="InvalidOperationException">Not in a room, or not connected.</exception>
    public async Task SayAsync(string text)
    {
        var clean = (text ?? string.Empty).Trim();
        if (clean.Length == 0) return;
        if (clean.Length > MaxLineLength) clean = clean[..MaxLineLength];
        var room = Room ?? throw new InvalidOperationException("Play something first: each event has its own chat.");
        if (!IsAvailable) throw new InvalidOperationException("Event chat needs the friends relay: turn on Friends in Settings and connect.");
        if (RelayTooOld) throw new InvalidOperationException("Your friends relay doesn't have event chat yet. Update SeaRaider.Relay on the server.");
        var sent = await _transport.SendAsync(new RelayFrame { Type = "room-say", Room = room, Id = Guid.NewGuid().ToString("N"), Message = clean })
            .ConfigureAwait(false);
        if (!sent) throw new InvalidOperationException("Not connected to the relay right now. Try again in a moment.");
    }

    private Task<bool> SendJoinAsync() =>
        Room is { } room
            ? _transport.SendAsync(new RelayFrame { Type = "room-join", Room = room, Name = _friends.Profile.DisplayName })
            : Task.FromResult(false);

    private void OnFrame(RelayFrame frame)
    {
        try
        {
            switch (frame.Type)
            {
                case "room-message" when frame.Room == Room && frame.Message is { Length: > 0 } text:
                    var mine = frame.From == _friends.Profile.UserId;
                    LineReceived?.Invoke(this, new EventChatLine(frame.Id ?? Guid.NewGuid().ToString("N"), frame.From ?? string.Empty,
                        Cut(frame.Name, 32) ?? "Someone", Cut(text, MaxLineLength)!, frame.At ?? DateTime.UtcNow, mine));
                    break;
                case "room-count" when frame.Room == Room && frame.Count is { } count:
                    Watching = Math.Max(0, count);
                    StateChanged?.Invoke(this, EventArgs.Empty);
                    break;
                case "error" when frame.Code == "bad-request" && frame.Message?.Contains("\"room-", StringComparison.Ordinal) == true:
                    RelayTooOld = true; // "Unknown frame "room-join"": a relay from before event chat
                    StateChanged?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Event chat frame failed");
        }
    }

    private static string? Cut(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var clean = text.Trim();
        return clean.Length > max ? clean[..max] : clean;
    }
}
