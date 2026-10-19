using LiteDB;

namespace SeaRaider.Core.Models;

/// <summary>What kind of place a media source is.</summary>
public enum MediaSourceKind
{
    /// <summary>A YouTube channel (@handle, /channel/UC…, /c/…, /user/…), read through its video feed.</summary>
    YouTubeChannel,

    /// <summary>A YouTube playlist (…list=PL…).</summary>
    YouTubePlaylist,

    /// <summary>A Twitch channel: one box, live or not.</summary>
    Twitch,

    /// <summary>An RSS / Atom feed of videos.</summary>
    Feed,

    /// <summary>Any other page: the videos embedded or linked on it.</summary>
    Page,
}

/// <summary>A page or channel whose videos and streams show on the Media tab.</summary>
public sealed class MediaSource
{
    [BsonId]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>What the user added.</summary>
    public string Url { get; set; } = string.Empty;

    public MediaSourceKind Kind { get; set; }

    /// <summary>Channel or site name ("IGN", "GameSpot", "shroud").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The address actually read (a YouTube channel's feed, found once).</summary>
    public string? FeedUrl { get; set; }

    /// <summary>The channel's picture, when the site has one.</summary>
    public string? AvatarUrl { get; set; }

    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastFetchedUtc { get; set; }
    public int LastCount { get; set; }
    public string? LastError { get; set; }
}

/// <summary>One video or stream from a <see cref="MediaSource"/>.</summary>
public sealed class MediaItem
{
    /// <summary>Source id + the video's own id, so each source keeps its own copy.</summary>
    [BsonId]
    public string Key { get; set; } = string.Empty;

    public Guid SourceId { get; set; }
    public string Title { get; set; } = string.Empty;

    /// <summary>What the player opens (a YouTube / Twitch / Vimeo address, or a video file).</summary>
    public string PlayUrl { get; set; } = string.Empty;

    /// <summary>Its page on the site (opened in the browser when it can't play in SeaRaider).</summary>
    public string? PageUrl { get; set; }

    public string? ThumbnailUrl { get; set; }

    /// <summary>Who made it (the YouTube channel, the Twitch streamer).</summary>
    public string? Channel { get; set; }

    public DateTime? PublishedUtc { get; set; }

    /// <summary>A live stream right now (Twitch).</summary>
    public bool IsLive { get; set; }

    /// <summary>Order on the page (feeds: newest first).</summary>
    public int Order { get; set; }
}
