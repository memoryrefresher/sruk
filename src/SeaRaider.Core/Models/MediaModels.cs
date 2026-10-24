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

    /// <summary>One video or stream by its address: an HLS / DASH / video file, or a single YouTube, Vimeo, Kick … video. Added at the end: kinds are stored as numbers.</summary>
    Stream,

    /// <summary>
    /// A site that lists titles (a grid of posters, each linking to the title's page), read like a game site on the store: its
    /// titles show on the Browse grid and each title's page is opened for its player when played. Added at the end: kinds are
    /// stored as numbers.
    /// </summary>
    Catalog,
}

/// <summary>A page or channel whose videos and streams show on the Media tab.</summary>
public sealed class MediaSource
{
    [BsonId]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The site draws its list by script (or blocks SeaRaider's own requests): read in SeaRaider's browser.</summary>
    public bool NeedsBrowser { get; set; }

    /// <summary>What the user added.</summary>
    public string Url { get; set; } = string.Empty;

    public MediaSourceKind Kind { get; set; }

    /// <summary>Channel or site name ("IGN", "GameSpot", "shroud").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The name was typed by the user: keep it, don't replace it with the video's own title.</summary>
    public bool NameChosen { get; set; }

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

    /// <summary>
    /// A title from a site's list (<see cref="MediaSourceKind.Catalog"/>): <see cref="PlayUrl"/> is the title's page, which is
    /// read for its player when the title is played.
    /// </summary>
    public bool NeedsResolve { get; set; }

    /// <summary>A title's sport or kind, as the site lists it ("Football").</summary>
    public string? Category { get; set; }

    /// <summary>
    /// A good picture of what the title is, found outside the site (<c>MediaArtworkService</c>): a movie or show poster, or for
    /// a match the home side's crest. Shown instead of the site's own picture.
    /// </summary>
    public string? ArtworkUrl { get; set; }

    /// <summary>For a match: the away side's crest (shown under the home side's, "vs" between).</summary>
    public string? ArtworkUrl2 { get; set; }

    /// <summary>The title is a match between two sides ("Bayern Munich vs Arsenal"): shown as two crests.</summary>
    public bool IsMatchup { get; set; }

    /// <summary>An esports match's event ("IEM Cologne 2026 · Playoffs"), shown under the match's name.</summary>
    public string? EventName { get; set; }
}
