using System.Text.Json;
using System.Text.Json.Serialization;

namespace SeaRaider.Core.Social;

/// <summary>
/// One JSON text frame on the relay WebSocket (the same shape both ways; unused fields are omitted).
/// <code>
/// client → relay   hello      { userId, secret, name }            first frame; registers the code on first use
///                  subscribe  { users: [...] }                    friend list: get "online"/"offline" for mutual friends
///                  send       { to, kind, id, payload }           to a friend (queued by the relay while they're offline)
///                  visibility { invisible }                       appear offline (relay announces us as offline)
///                  ping
/// relay → client   welcome    { userId, online: [...] }
///                  deliver    { from, kind, id, payload, at }
///                  sent       { id, queued }                      the relay accepted a "send"
///                  online / offline { user }
///                  error      { code, message }                   auth | rate | too-big | bad-request
///                  pong
/// </code>
/// The relay never reads payloads; message kinds are defined by <see cref="MessageKinds"/>.
/// </summary>
public sealed class RelayFrame
{
    public string Type { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string? Secret { get; set; }
    public string? Name { get; set; }
    public List<string>? Users { get; set; }
    public List<string>? Online { get; set; }
    public string? User { get; set; }
    public string? To { get; set; }
    public string? From { get; set; }
    public string? Kind { get; set; }
    public string? Id { get; set; }
    public JsonElement? Payload { get; set; }
    public DateTime? At { get; set; }
    public bool? Queued { get; set; }

    /// <summary>"hello" / "visibility": appear offline to friends (the relay stops announcing us).</summary>
    public bool? Invisible { get; set; }
    public string? Code { get; set; }
    public string? Message { get; set; }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    public static RelayFrame? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<RelayFrame>(json, Json); }
        catch (JsonException) { return null; }
    }
}

/// <summary>Friend-to-friend message kinds (the "kind" of a send / deliver frame) and their payloads.</summary>
public static class MessageKinds
{
    public const string FriendRequest = "friend-request";   // { name, message }
    public const string FriendAccept = "friend-accept";     // { name }
    public const string FriendDecline = "friend-decline";   // { }
    public const string FriendRemove = "friend-remove";     // { }
    public const string Presence = "presence";              // { name, status, game }
    public const string Chat = "chat";                      // { text, sentAt }
    public const string ChatAck = "chat-ack";               // { id }
    public const string InviteReply = "invite-reply";       // { inviteId, accepted }
}

public sealed record FriendRequestPayload(string Name, string? Message);
public sealed record FriendAcceptPayload(string Name);
/// <param name="Media">What they're watching (newer versions; older ones ignore it).</param>
public sealed record PresencePayload(string Name, UserStatus Status, string? Game, MediaActivity? Media = null);
/// <param name="Game">A game shared with this message (older versions ignore it and show <paramref name="Text"/>).</param>
/// <param name="Invite">An invite to play or watch (older versions show <paramref name="Text"/>).</param>
public sealed record ChatPayload(string Text, DateTime SentAt, SharedGame? Game = null, ChatInvite? Invite = null);

/// <summary>The answer to an invite (<paramref name="InviteId"/> is the invite message's id).</summary>
public sealed record InviteReplyPayload(string InviteId, bool Accepted);

/// <summary>A store page shared in chat: what the friend needs to find the same game in their own store.</summary>
/// <remarks>Title, page on the sharer's download site (also how the friend's store finds it), picture, site name, size.</remarks>
public sealed record SharedGame
{
    /// <summary>For the JSON reader and the chat database.</summary>
    public SharedGame() { }

    public SharedGame(string title, string pageUrl, string? coverUrl, string? sourceName, string? size)
    {
        Title = title;
        PageUrl = pageUrl;
        CoverUrl = coverUrl;
        SourceName = sourceName;
        Size = size;
    }

    public string Title { get; set; } = string.Empty;
    public string PageUrl { get; set; } = string.Empty;
    public string? CoverUrl { get; set; }
    public string? SourceName { get; set; }
    public string? Size { get; set; }
}
public sealed record ChatAckPayload(string Id);
