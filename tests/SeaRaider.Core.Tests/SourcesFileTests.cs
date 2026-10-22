using SeaRaider.Core.Catalog;

namespace SeaRaider.Core.Tests;

public class SourcesFileTests
{
    [Fact]
    public void Reads_sources_and_upcoming_sites()
    {
        const string text = """
            # my sources
            fitgirl-repacks.site
            https://steamrip.com/   # comment after the address
            https://www.FitGirl-Repacks.site/
            not a url

            [upcoming]
            https://store.steampowered.com/search/?filter=comingsoon
            """;
        var list = SourcesFile.Parse(text);

        Assert.Equal(new[] { "https://fitgirl-repacks.site/", "https://steamrip.com/" }, list.Sources);
        Assert.Equal(new[] { "https://store.steampowered.com/search/?filter=comingsoon" }, list.Upcoming);
    }

    [Fact]
    public void What_is_written_reads_back_the_same()
    {
        var text = SourcesFile.Format(["https://a.example.com/", "https://b.example.org/"], ["https://itch.io/games/upcoming"]);
        var list = SourcesFile.Parse(text);

        Assert.Equal(new[] { "https://a.example.com/", "https://b.example.org/" }, list.Sources);
        Assert.Equal(new[] { "https://itch.io/games/upcoming" }, list.Upcoming);
    }

    [Fact]
    public void A_custom_list_keeps_its_start_end_and_switch_in_the_file()
    {
        var custom = new UpcomingEntry("https://example.com/feed", "<h2 class=\"x\">Coming soon</h2>\n<ul>", "</ul>\\n# not a comment", Enabled: false);
        var plain = new UpcomingEntry("https://itch.io/games/upcoming");
        var text = SourcesFile.FormatEntries(["https://a.example.com/"], [plain, custom]);

        var list = SourcesFile.Parse(text);

        Assert.Equal(new[] { "https://a.example.com/" }, list.Sources);
        Assert.Equal(2, list.UpcomingEntries.Count);
        Assert.Equal(plain, list.UpcomingEntries[0]);
        Assert.Equal(custom, list.UpcomingEntries[1]);
        Assert.True(list.UpcomingEntries[1].IsCustom);
        Assert.False(list.UpcomingEntries[1].Enabled);
        Assert.Equal(new[] { "https://itch.io/games/upcoming", "https://example.com/feed" }, list.Upcoming);
    }

    [Fact]
    public void One_page_can_hold_several_custom_lists()
    {
        const string text = """
            [upcoming]
            https://example.com/feed
              start: <h2>A
              end: </ul>
            https://example.com/feed
              start: <h2>B
            https://example.com/feed
              start: <h2>A
              end: </ul>
            """;
        var list = SourcesFile.Parse(text);

        Assert.Equal(2, list.UpcomingEntries.Count); // the third is a repeat of the first
        Assert.Equal("<h2>A", list.UpcomingEntries[0].StartHtml);
        Assert.Equal("<h2>B", list.UpcomingEntries[1].StartHtml);
        Assert.Null(list.UpcomingEntries[1].EndHtml);
    }

    [Fact]
    public void Settings_lines_only_count_under_an_upcoming_address()
    {
        const string text = """
            https://a.example.com/
              start: <h2>nope
            [upcoming]
              enabled: no
            https://b.example.com/
              enabled: off
            """;
        var list = SourcesFile.Parse(text);

        Assert.Equal(new[] { "https://a.example.com/" }, list.Sources);
        var entry = Assert.Single(list.UpcomingEntries);
        Assert.False(entry.Enabled);
        Assert.False(entry.IsCustom);
    }

    [Fact]
    public void A_file_without_settings_still_reads_as_plain_upcoming_sites()
    {
        var list = SourcesFile.Parse("[upcoming]\nhttps://itch.io/games/upcoming\n");
        var entry = Assert.Single(list.UpcomingEntries);
        Assert.True(entry.Enabled);
        Assert.False(entry.IsCustom);
    }

    [Theory]
    [InlineData("https://www.Site.com/", "site.com")]
    [InlineData("http://site.com", "site.com")]
    [InlineData("site.com/games/", "site.com/games")]
    public void Addresses_compare_like_a_person_would(string url, string key) => Assert.Equal(key, SourcesFile.Key(url));
}
