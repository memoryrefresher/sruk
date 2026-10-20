using LiteDB;

namespace SeaRaider.Core.Models;

/// <summary>
/// The lightweight summary of a game that the store grid needs (≈ 300 bytes instead of the full page data).
/// Kept in its own collection and in memory by <c>CatalogIndexService</c>, so tens of thousands of games
/// can be filtered and sorted instantly; full <see cref="GameMetadata"/> is only read when a game is opened.
/// </summary>
public sealed class CatalogEntry
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public Guid SourceId { get; set; }
    public string PageUrl { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? CoverImageUrl { get; set; }
    public string ShortDescription { get; set; } = string.Empty;
    public List<string> Genres { get; set; } = [];
    public string? Developer { get; set; }
    public string? Version { get; set; }
    public string? DlcInfo { get; set; }
    public string? Size { get; set; }
    public string? ReleaseDate { get; set; }
    public DateTime? ReleaseDateUtc { get; set; }
    public DateTime? UploadedAtUtc { get; set; }
    public bool DetailsLoaded { get; set; }
    public DateTime FetchedAtUtc { get; set; }

    private string? _sizeParsedFrom;
    private bool _sizeParsed;
    private long? _sizeBytes;

    /// <summary><see cref="Size"/> in bytes ("4.1 GB" → 4 402 341 478), or null when the page gave no size.</summary>
    [BsonIgnore]
    public long? SizeBytes
    {
        get
        {
            if (!_sizeParsed || !string.Equals(_sizeParsedFrom, Size, StringComparison.Ordinal))
            {
                _sizeBytes = Formatting.ParseBytes(Size) is > 0 and var bytes ? bytes : null;
                _sizeParsedFrom = Size;
                _sizeParsed = true;
            }
            return _sizeBytes;
        }
    }

    public static CatalogEntry From(GameMetadata game) => new()
    {
        Id = game.Id,
        SourceId = game.SourceId,
        PageUrl = game.PageUrl,
        Title = game.Title,
        CoverImageUrl = game.CoverImageUrl,
        ShortDescription = game.ShortDescription,
        Genres = (game.Genres.Count > 0 ? game.Genres : game.Tags).Take(4).ToList(),
        Developer = game.Developer,
        Version = game.Version,
        DlcInfo = game.DlcInfo,
        Size = game.DisplaySize,
        ReleaseDate = game.ReleaseDate,
        ReleaseDateUtc = game.ReleaseDateUtc,
        UploadedAtUtc = game.UploadedAtUtc,
        DetailsLoaded = game.DetailsLoaded,
        FetchedAtUtc = game.FetchedAtUtc,
    };
}

/// <summary>Store sort orders. Entries without the relevant date always sort after dated ones.</summary>
public enum CatalogSort
{
    UploadNewest,
    UploadOldest,
    ReleaseNewest,
    ReleaseOldest,
    TitleAscending,

    /// <summary>Smallest download first (by the size the page gives); games without a size last.</summary>
    SizeSmallest,

    /// <summary>Largest download first; games without a size last.</summary>
    SizeLargest,

    /// <summary>Search relevance (<c>SearchScorer</c>): exact title matches first. Without a search, newest uploads.</summary>
    Relevance,
}

/// <summary>Filter + sort for the in-memory catalog.</summary>
/// <param name="Text">Search text, scored by <c>SearchScorer</c> against title (and genres / developer); weak matches are dropped.</param>
/// <param name="SourceId">Restrict to one source.</param>
/// <param name="SourceIds">Restrict to these sources (the store's source filter, and only sources that are switched on).</param>
public sealed record CatalogQuery(string? Text = null, CatalogSort Sort = CatalogSort.UploadNewest, Guid? SourceId = null,
    IReadOnlyCollection<Guid>? SourceIds = null);

/// <summary>Sorting for catalog entries — shared by the index (cached games) and live search results.</summary>
public static class CatalogSorting
{
    public static IEnumerable<CatalogEntry> Sort(this IEnumerable<CatalogEntry> entries, CatalogSort sort) => sort switch
    {
        CatalogSort.UploadNewest => entries
            .OrderBy(e => e.UploadedAtUtc is null)                 // dated first, undated last
            .ThenByDescending(e => e.UploadedAtUtc)
            .ThenByDescending(e => e.FetchedAtUtc)
            .ThenBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase),
        CatalogSort.UploadOldest => entries
            .OrderBy(e => e.UploadedAtUtc is null)
            .ThenBy(e => e.UploadedAtUtc)
            .ThenBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase),
        CatalogSort.ReleaseNewest => entries
            .OrderBy(e => e.ReleaseDateUtc is null)
            .ThenByDescending(e => e.ReleaseDateUtc)
            .ThenByDescending(e => e.UploadedAtUtc)
            .ThenBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase),
        CatalogSort.ReleaseOldest => entries
            .OrderBy(e => e.ReleaseDateUtc is null)
            .ThenBy(e => e.ReleaseDateUtc)
            .ThenBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase),
        CatalogSort.SizeSmallest => entries
            .OrderBy(e => e.SizeBytes is null)                    // sized first, unknown sizes last
            .ThenBy(e => e.SizeBytes)
            .ThenBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase),
        CatalogSort.SizeLargest => entries
            .OrderBy(e => e.SizeBytes is null)
            .ThenByDescending(e => e.SizeBytes)
            .ThenBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase),
        _ => entries.OrderBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase),
    };

    /// <summary>All search words appear in the title, a genre or the developer.</summary>
    public static bool Matches(this CatalogEntry entry, string[] words) =>
        words.All(w =>
            entry.Title.Contains(w, StringComparison.CurrentCultureIgnoreCase) ||
            entry.Genres.Any(g => g.Contains(w, StringComparison.CurrentCultureIgnoreCase)) ||
            (entry.Developer?.Contains(w, StringComparison.CurrentCultureIgnoreCase) ?? false));

    public static string[] SplitWords(string? text) =>
        (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
}
