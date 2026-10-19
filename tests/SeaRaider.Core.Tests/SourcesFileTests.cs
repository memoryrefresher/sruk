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

    [Theory]
    [InlineData("https://www.Site.com/", "site.com")]
    [InlineData("http://site.com", "site.com")]
    [InlineData("site.com/games/", "site.com/games")]
    public void Addresses_compare_like_a_person_would(string url, string key) => Assert.Equal(key, SourcesFile.Key(url));
}
