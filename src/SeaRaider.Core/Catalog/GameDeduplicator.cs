using SeaRaider.Core.Models;

namespace SeaRaider.Core.Catalog;

/// <summary>One game as the store shows it: every source's copy of it, merged.</summary>
/// <param name="Key">Shared <see cref="TitleNormalizer.DedupKey"/>.</param>
/// <param name="Primary">Copy whose art and summary the card shows.</param>
/// <param name="Variants">Every copy (one per source page), best first.</param>
public sealed record GameGroup(string Key, CatalogEntry Primary, IReadOnlyList<CatalogEntry> Variants)
{
    public int SourceCount => Variants.Select(v => v.SourceId).Distinct().Count();

    public bool IsMerged => Variants.Count > 1;

    /// <summary>Newest upload among the copies.</summary>
    public DateTime? NewestUploadUtc => Variants.Max(v => v.UploadedAtUtc);
}

/// <summary>
/// Merges the same game found on several sites (or posted twice on one) into a single store card, keyed by
/// <see cref="TitleNormalizer.DedupKey"/>: "ELDEN RING Deluxe Edition (v1.10 + DLC)" on one site and "Elden Ring Free
/// Download" on another are one game; "Hades" and "Hades II" are not.
/// </summary>
public static class GameDeduplicator
{
    public static string KeyFor(CatalogEntry entry)
    {
        var key = TitleNormalizer.DedupKey(entry.Title);
        return key.Length > 0 ? key : "id:" + entry.Id; // titles that normalise to nothing are never merged
    }

    /// <summary>
    /// Groups an already sorted / ranked list. Each group takes the position of its best-ranked copy, so sorting and
    /// relevance carry over; the copy with art and full details becomes the card's face.
    /// </summary>
    public static List<GameGroup> Group(IEnumerable<CatalogEntry> ordered)
    {
        var groups = new Dictionary<string, List<CatalogEntry>>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var entry in ordered)
        {
            var key = KeyFor(entry);
            if (!groups.TryGetValue(key, out var list))
            {
                groups[key] = list = [];
                order.Add(key);
            }
            if (list.All(e => e.Id != entry.Id)) list.Add(entry);
        }

        return order.Select(key => Build(key, groups[key])).ToList();
    }

    /// <summary>Builds a group from its copies (any order).</summary>
    public static GameGroup Build(string key, IReadOnlyList<CatalogEntry> copies)
    {
        var ranked = copies
            .Select((entry, index) => (entry, index))
            .OrderByDescending(x => !string.IsNullOrEmpty(x.entry.CoverImageUrl))
            .ThenByDescending(x => x.entry.DetailsLoaded)
            .ThenBy(x => x.index) // keep the caller's ranking otherwise
            .Select(x => x.entry)
            .ToList();
        return new GameGroup(key, ranked[0], ranked);
    }
}
