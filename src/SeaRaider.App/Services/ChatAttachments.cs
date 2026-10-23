using System.Text.Json;
using SeaRaider.Core.Social;

namespace SeaRaider.App.Services;

/// <summary>
/// A game or a Media tab box waiting to be sent in a chat message (the card above the message box), or being dragged there.
/// Exactly one of <see cref="Game"/> and <see cref="Media"/> is set.
/// </summary>
public sealed record ChatAttachment(SharedGame? Game, SharedMediaLink? Media)
{
    public string Title => Game?.Title ?? Media?.Title ?? string.Empty;
    public string? PictureUrl => Game?.CoverUrl ?? Media?.ThumbnailUrl;
    public string KindText => Game is not null ? "Game" : Media?.IsLive == true ? "Live" : "Watch";

    public static ChatAttachment ForGame(SharedGame game) => new(game, null);
    public static ChatAttachment ForMedia(SharedMediaLink media) => new(null, media);

    private sealed record Wire(SharedGame? Game, SharedMediaLink? Media);

    /// <summary>As text, for a drag (any app may look at drag data; only SeaRaider understands this format).</summary>
    public string ToJson() => JsonSerializer.Serialize(new Wire(Game, Media));

    public static ChatAttachment? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var wire = JsonSerializer.Deserialize<Wire>(json);
            return wire is null || (wire.Game is null) == (wire.Media is null) ? null : new ChatAttachment(wire.Game, wire.Media);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>A box that can be sent to a friend (store game cards, Media tab boxes).</summary>
public interface IChatAttachmentSource
{
    ChatAttachment? ToChatAttachment();
}

/// <summary>
/// Hands games and media from anywhere in the app to the friends panel: "Send to a friend…" on a box, or a box dragged
/// towards the panel (which opens it). The friends panel listens; a static hub, so every card can reach it without being
/// handed a service.
/// </summary>
public static class ChatAttachments
{
    /// <summary>The drag-and-drop format of a box's <see cref="ChatAttachment"/> (JSON text).</summary>
    public const string DragFormat = "SeaRaider.ChatAttachment";

    /// <summary>"Send to a friend…": attach it to the next message (the friends panel opens).</summary>
    public static event Action<ChatAttachment>? AttachRequested;

    /// <summary>A box started being dragged (the friends panel opens, ready for the drop).</summary>
    public static event Action? DragStarted;

    public static void RequestAttach(IChatAttachmentSource source)
    {
        if (source.ToChatAttachment() is { } attachment) AttachRequested?.Invoke(attachment);
    }

    internal static void OnDragStarted() => DragStarted?.Invoke();
}
