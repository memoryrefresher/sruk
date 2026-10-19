using LiteDB;

namespace SeaRaider.Core.Models;

/// <summary>A web page listing games that are coming soon (freeware, demos …) that the user added to the store.</summary>
public sealed class UpcomingSource
{
    [BsonId]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The listing page (an itch.io or Steam "coming soon" list, a site's upcoming page, an RSS feed …).</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Host name, shown in the list ("itch.io").</summary>
    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastFetchedUtc { get; set; }
    public int LastCount { get; set; }
    public string? LastError { get; set; }

    /// <summary>
    /// A "custom list": a page or feed that is just a list of game names. Only the part between <see cref="StartHtml"/> and
    /// <see cref="EndHtml"/> is read, and each game's picture and details are looked up on Steam.
    /// </summary>
    public bool IsCustom { get; set; }

    /// <summary>Custom list: HTML (or text) that marks where to start reading.</summary>
    public string? StartHtml { get; set; }

    /// <summary>Custom list: HTML (or text) that marks where to stop reading.</summary>
    public string? EndHtml { get; set; }
}

/// <summary>One upcoming game found on an <see cref="UpcomingSource"/>.</summary>
public sealed class UpcomingRelease
{
    /// <summary><see cref="Catalog.TitleNormalizer.DedupKey"/> of the title: the same game from two sites is one entry.</summary>
    [BsonId]
    public string Key { get; set; } = string.Empty;

    public Guid SourceId { get; set; }
    public string Title { get; set; } = string.Empty;

    /// <summary>The picture on the listing page.</summary>
    public string? ImageUrl { get; set; }

    /// <summary>A large picture found on the web (Steam library hero, screenshots, wallpapers), when there is one.</summary>
    public string? HeroUrl { get; set; }

    /// <summary>"Coming soon", "Q1 2027", "March 3, 2027" …, as the page puts it.</summary>
    public string? ReleaseText { get; set; }

    public string? Description { get; set; }

    /// <summary>The game's own page on the listing site.</summary>
    public string? PageUrl { get; set; }

    public string? Developer { get; set; }
    public List<string> Tags { get; set; } = [];
    public bool ArtworkChecked { get; set; }

    /// <summary>The Steam app, when the listing is a Steam page: its real pictures and details come from Steam's API.</summary>
    public int? SteamAppId { get; set; }

    /// <summary>Steam says this listing is a demo (its title may not say so). Demos are never shown in "Coming soon".</summary>
    public bool IsDemo { get; set; }

    /// <summary>Only the name is known (a custom list): its Steam page is searched for, then its details come from Steam.</summary>
    public bool FindOnSteam { get; set; }

    /// <summary>Which rules found the pictures; older ones are looked up again (see <see cref="CurrentArtworkVersion"/>).</summary>
    public int ArtworkVersion { get; set; }

    /// <summary>2: Steam titles use Steam's own (hashed) image addresses and details, not guessed ones.</summary>
    public const int CurrentArtworkVersion = 2;

    [BsonIgnore]
    public bool ArtworkDone => ArtworkChecked && ArtworkVersion >= CurrentArtworkVersion;
    public DateTime FetchedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Best picture for a big banner.</summary>
    [BsonIgnore]
    public string? BestImage => HeroUrl ?? ImageUrl;
}

/// <summary>A game the user wants to hear about when it can be downloaded from the store.</summary>
public sealed class WishlistItem
{
    /// <summary><see cref="Catalog.TitleNormalizer.DedupKey"/> of the title.</summary>
    [BsonId]
    public string Key { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public string? ReleaseText { get; set; }
    public string? PageUrl { get; set; }
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>When the game first turned up in the store (one of the user's download sources).</summary>
    public DateTime? AvailableSinceUtc { get; set; }

    /// <summary>The store entry it was found as (opens its store page).</summary>
    public string? AvailableGameId { get; set; }

    /// <summary>The user has seen the "now available" notice.</summary>
    public bool Notified { get; set; }

    public DateTime? LastSearchedUtc { get; set; }

    [BsonIgnore]
    public bool IsAvailable => AvailableGameId is not null;
}
