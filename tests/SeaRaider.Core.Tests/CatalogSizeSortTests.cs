using SeaRaider.Core.Models;

namespace SeaRaider.Core.Tests;

/// <summary>Store sorts by download size: sized games in order, games without a size last.</summary>
public class CatalogSizeSortTests
{
    private static readonly CatalogEntry[] Entries =
    [
        new() { Id = "a", Title = "Big", Size = "40.2 GB" },
        new() { Id = "b", Title = "Unknown" },
        new() { Id = "c", Title = "Small", Size = "850 MB" },
        new() { Id = "d", Title = "Medium", Size = "4.1 GB" },
    ];

    [Fact]
    public void Smallest_first_puts_unknown_sizes_last() =>
        Assert.Equal(["Small", "Medium", "Big", "Unknown"], Entries.Sort(CatalogSort.SizeSmallest).Select(e => e.Title));

    [Fact]
    public void Largest_first_puts_unknown_sizes_last() =>
        Assert.Equal(["Big", "Medium", "Small", "Unknown"], Entries.Sort(CatalogSort.SizeLargest).Select(e => e.Title));
}
