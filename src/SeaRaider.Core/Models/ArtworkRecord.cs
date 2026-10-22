using LiteDB;

namespace SeaRaider.Core.Models;

/// <summary>
/// Pictures for a game from outside its download page, cached per game (all sources share them): a wide banner for
/// store cards without a cover, and a large hero image for the featured banner and the game page.
/// </summary>
public sealed class ArtworkRecord
{
    /// <summary><see cref="Catalog.TitleNormalizer.DedupKey"/> of the title.</summary>
    [BsonId]
    public string Key { get; set; } = string.Empty;

    /// <summary>Wide image for cards (Steam's 460×215 header), or the page's own screenshot.</summary>
    public string? HeaderUrl { get; set; }

    /// <summary>Large image for banners (Steam library hero 1920×620+, else a 1920×1080 screenshot).</summary>
    public string? HeroUrl { get; set; }

    public int? SteamAppId { get; set; }

    /// <summary>The game's release date from its Steam page (ReleaseDateService), and its text there ("20 Oct, 2026").</summary>
    public DateTime? ReleaseDateUtc { get; set; }
    public string? ReleaseDateText { get; set; }

    /// <summary>When the release date was last looked up (null: never).</summary>
    public DateTime? ReleaseCheckedAtUtc { get; set; }

    /// <summary>"Steam", "Game page".</summary>
    public string? Source { get; set; }

    public bool Found { get; set; }

    /// <summary>Measured size of <see cref="HeroUrl"/> (0 when unknown).</summary>
    public int HeroWidth { get; set; }
    public int HeroHeight { get; set; }

    /// <summary>Rules that produced this record; older records are looked up again (see ArtworkService.RulesVersion).</summary>
    public int RulesVersion { get; set; }

    public DateTime CheckedAtUtc { get; set; } = DateTime.UtcNow;
}
