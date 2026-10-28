using SeaRaider.Core.Models;

namespace SeaRaider.Core.Tests;

public class ReleaseDateTests
{
    [Fact]
    public void A_release_date_on_the_upload_day_is_the_posts_own_date()
    {
        var upload = new DateTime(2026, 10, 20, 15, 0, 0, DateTimeKind.Utc);
        Assert.False(ReleaseDates.IsReal(upload.AddHours(-3), upload, fromSteam: false));
        Assert.True(ReleaseDates.IsReal(upload.AddHours(-3), upload, fromSteam: true)); // Steam says so: it is
        Assert.True(ReleaseDates.IsReal(new DateTime(2021, 5, 4), upload, fromSteam: false));
        Assert.True(ReleaseDates.IsReal(new DateTime(2021, 5, 4), null, fromSteam: false));
        Assert.False(ReleaseDates.IsReal(null, upload, fromSteam: false));
    }

    [Fact]
    public void Release_sorting_ignores_upload_dates_passed_off_as_release_dates()
    {
        var day = new DateTime(2026, 10, 1);
        CatalogEntry[] entries =
        [
            new() { Id = "a", Title = "Posted Yesterday", UploadedAtUtc = day.AddDays(20), ReleaseDateUtc = day.AddDays(20) }, // not real
            new() { Id = "b", Title = "Old Game", UploadedAtUtc = day.AddDays(19), ReleaseDateUtc = new DateTime(2015, 3, 1) },
            new() { Id = "c", Title = "New Game", UploadedAtUtc = day, ReleaseDateUtc = new DateTime(2026, 9, 1) },
        ];
        Assert.Equal("c,b,a", string.Join(",", entries.Sort(CatalogSort.ReleaseNewest).Select(e => e.Id)));
        Assert.Equal("b,c,a", string.Join(",", entries.Sort(CatalogSort.ReleaseOldest).Select(e => e.Id)));
    }

    [Theory]
    [InlineData("20 Oct, 2026", 2026, 10, 20)]
    [InlineData("Oct 20, 2026", 2026, 10, 20)]
    [InlineData("3 Mar, 2017", 2017, 3, 3)]
    public void Reads_Steam_release_dates(string text, int y, int m, int d) =>
        Assert.Equal(new DateTime(y, m, d), ReleaseDates.ParseSteam(text)!.Value.Date);

    [Theory]
    [InlineData("Coming soon")]
    [InlineData("To be announced")]
    [InlineData("Q4 2026")]
    public void Vague_Steam_dates_are_unknown(string text) => Assert.Null(ReleaseDates.ParseSteam(text));
}
