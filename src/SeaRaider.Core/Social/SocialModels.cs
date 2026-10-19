using System.Security.Cryptography;
using LiteDB;

namespace SeaRaider.Core.Social;

/// <summary>What friends see.</summary>
public enum UserStatus
{
    Offline,
    Online,
    Away,
    InGame,
}

/// <summary>The status the user picked in the friends panel.</summary>
public enum PresenceMode
{
    /// <summary>Online, Away after inactivity, In-Game while a game launched from SeaRaider runs.</summary>
    Automatic,

    /// <summary>Always Away (still shows In-Game).</summary>
    Away,

    /// <summary>Appear offline; messages still arrive.</summary>
    Invisible,
}

/// <summary>Where a relationship stands.</summary>
public enum FriendshipState
{
    Friend,

    /// <summary>They asked; Accept / Decline.</summary>
    IncomingRequest,

    /// <summary>We asked; waiting for them.</summary>
    OutgoingRequest,
}

/// <summary>Connection to the relay server.</summary>
public enum SocialConnectionState
{
    /// <summary>No relay address set in Settings.</summary>
    NotConfigured,
    Connecting,
    Connected,

    /// <summary>Lost or refused; retrying with backoff.</summary>
    Disconnected,
}

/// <summary>This installation's identity on the friends network.</summary>
public sealed class SocialProfile
{
    [BsonId]
    public int Id { get; set; } = 1;

    /// <summary>Public friend code, e.g. "SR-7K3QM-9XPAD". Share it so friends can add you.</summary>
    public string UserId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = Environment.UserName;

    /// <summary>Private key proving to the relay that this code is ours (never shown or sent to friends).</summary>
    public string Secret { get; set; } = string.Empty;

    public PresenceMode PresenceMode { get; set; } = PresenceMode.Automatic;

    /// <summary>When the Media tab's "Shared" feed was last looked at (newer posts and comments count as unread).</summary>
    public DateTime MediaFeedSeenUtc { get; set; } = DateTime.UtcNow;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public static SocialProfile CreateNew() => new()
    {
        UserId = FriendCode.Generate(),
        Secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
    };
}

/// <summary>A friend, or a pending request either way.</summary>
public sealed class Friend
{
    /// <summary>Their friend code.</summary>
    [BsonId]
    public string UserId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public FriendshipState State { get; set; }

    /// <summary>Note sent with a friend request.</summary>
    public string? RequestMessage { get; set; }

    public DateTime SinceUtc { get; set; } = DateTime.UtcNow;

    public DateTime? LastSeenUtc { get; set; }

    public int UnreadCount { get; set; }

    // ---- live presence (not persisted) -------------------------------------------------------------

    [BsonIgnore] public UserStatus Status { get; set; } = UserStatus.Offline;

    /// <summary>Game they're playing, when they share it.</summary>
    [BsonIgnore] public string? GameTitle { get; set; }

    /// <summary>What they're watching in SeaRaider's player, when they share it (position as of <see cref="MediaActivity.AtUtc"/>,
    /// in this PC's clock).</summary>
    [BsonIgnore] public MediaActivity? Watching { get; set; }

    /// <summary>"Online", "Away", "In-Game: Hades II", "Watching: Elden Ring trailer", "Offline".</summary>
    [BsonIgnore]
    public string StatusText => Status switch
    {
        UserStatus.InGame => GameTitle is { Length: > 0 } game ? $"In-Game: {game}" : "In-Game",
        UserStatus.Offline => "Offline",
        _ when Watching is { } media => $"Watching: {media.Title}",
        UserStatus.Online => "Online",
        UserStatus.Away => "Away",
        _ => "Offline",
    };
}

/// <summary>One chat message, stored locally on both ends.</summary>
public sealed class ChatMessage
{
    [BsonId]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>The other person in the conversation.</summary>
    public string FriendId { get; set; } = string.Empty;

    public bool IsOutgoing { get; set; }

    public string Text { get; set; } = string.Empty;

    public DateTime SentAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Outgoing: the friend's app received it. Incoming: always true.</summary>
    public bool Delivered { get; set; }

    /// <summary>Outgoing: the relay is holding it until the friend comes online.</summary>
    public bool Queued { get; set; }

    /// <summary>Outgoing: couldn't be sent (offline); "Retry" sends it again.</summary>
    public bool Failed { get; set; }

    /// <summary>A game shared in this message (shown as a card with "Open in store"), or null.</summary>
    public SharedGame? Game { get; set; }

    /// <summary>An invite to play a game or to watch along, or null.</summary>
    public ChatInvite? Invite { get; set; }

    /// <summary>For an invite: what the invited friend answered.</summary>
    public InviteAnswer Answer { get; set; }
}

public enum InviteKind
{
    /// <summary>"Come play Hades II with me".</summary>
    Game,

    /// <summary>"Watch this with me": opens the video and follows the sender's player.</summary>
    Watch,
}

public enum InviteAnswer
{
    None,
    Accepted,
    Declined,
}

/// <summary>An invite sent in chat (a card with Play / Watch along and Decline).</summary>
public sealed class ChatInvite
{
    public InviteKind Kind { get; set; }

    /// <summary>The game's or the video's title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Game: its store page on the sender's site (how the friend's store finds it).</summary>
    public string? PageUrl { get; set; }

    public string? CoverUrl { get; set; }
    public string? SourceName { get; set; }

    /// <summary>Watch: the video, and where the sender was in it.</summary>
    public string? MediaUrl { get; set; }

    public int? StartSeconds { get; set; }

    /// <summary>Invites go stale: after this long the card only says it expired.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);
}

/// <summary>
/// What someone is watching in SeaRaider's player: title, address, and where playback was at <see cref="AtUtc"/> (from
/// there it runs on while <see cref="Playing"/>). Sent with presence; friends use it for "Watching: …" and Watch along.
/// </summary>
public sealed record MediaActivity
{
    public string Title { get; set; } = string.Empty;

    /// <summary>What the player plays (YouTube / Twitch / Vimeo address, video file or stream).</summary>
    public string Url { get; set; } = string.Empty;

    public string? ThumbnailUrl { get; set; }

    /// <summary>Seconds into the video at <see cref="AtUtc"/>.</summary>
    public double Position { get; set; }

    public bool Playing { get; set; }

    /// <summary>A live stream: there's no position to follow, only the stream.</summary>
    public bool IsLive { get; set; }

    public DateTime AtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Where playback is now (it kept running while playing).</summary>
    public double PositionAt(DateTime nowUtc) =>
        Playing && !IsLive ? Position + Math.Max(0, (nowUtc - AtUtc).TotalSeconds) : Position;

    /// <summary>The same activity measured from now (what gets sent, so the friend's clock doesn't matter).</summary>
    public MediaActivity Snapshot(DateTime nowUtc) => this with { Position = PositionAt(nowUtc), AtUtc = nowUtc };
}

/// <summary>Our own presence, as computed by <see cref="PresenceMonitor"/>.</summary>
/// <param name="Status">What friends see (Offline when invisible).</param>
/// <param name="GameTitle">Game being played (null unless shared).</param>
/// <param name="Media">What's playing in SeaRaider's player (null unless shared).</param>
public sealed record Presence(UserStatus Status, string? GameTitle, MediaActivity? Media = null)
{
    public static Presence Offline { get; } = new(UserStatus.Offline, null);
}

/// <summary>
/// Friend codes: "SR-" plus ten Crockford base-32 characters (no I, L, O, U), in two groups. About 50 bits of
/// randomness, so codes can't be guessed; case, spaces and the dashes don't matter when typing one in.
/// </summary>
public static class FriendCode
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string Generate()
    {
        Span<char> chars = stackalloc char[10];
        for (var i = 0; i < chars.Length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return $"SR-{new string(chars[..5])}-{new string(chars[5..])}";
    }

    /// <summary>Canonical form of a typed code, or null when it isn't one.</summary>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var compact = new string(text.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (compact.Length == 12 && compact.StartsWith("SR", StringComparison.Ordinal)) compact = compact[2..];
        compact = compact.Replace('O', '0').Replace('I', '1').Replace('L', '1'); // common misreadings
        if (compact.Length != 10 || compact.Any(c => !Alphabet.Contains(c))) return null;
        return $"SR-{compact[..5]}-{compact[5..]}";
    }
}
