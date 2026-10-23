using SeaRaider.Core.Catalog;
using SeaRaider.Core.Models;
using SeaRaider.Core.Scraping;

namespace SeaRaider.Core.Tests;

public class AdultFilterAndHostPriorityTests
{
    [Theory]
    [InlineData("Hentai Girls v1.2", true)]
    [InlineData("Summer Fling [18+]", true)]
    [InlineData("Lewd Island Uncensored", true)]
    [InlineData("Contains nudity and sexual content", true)]
    [InlineData("SnowRunner", false)]
    [InlineData("Sextant", false)]
    [InlineData("Harem Hotel Manager", false)]
    [InlineData("Essex County", false)]
    public void Adult_keywords(string text, bool adult) => Assert.Equal(adult, AdultContentFilter.IsAdult(text));

    [Fact]
    public void An_entry_is_checked_by_title_genres_teaser_and_address()
    {
        Assert.True(AdultContentFilter.IsAdult(new CatalogEntry { Title = "Nice Game", Genres = ["Action", "NSFW"] }));
        Assert.True(AdultContentFilter.IsAdult(new CatalogEntry { Title = "Nice Game", PageUrl = "https://x.example/lewd-island-free/" }));
        Assert.False(AdultContentFilter.IsAdult(new CatalogEntry { Title = "SnowRunner", Genres = ["Simulation"] }));
    }

    [Fact]
    public void Gofile_ranks_after_FuckingFast_and_FileKeeper_and_before_the_rest_even_behind_BlueMediaFiles()
    {
        DownloadLink Behind(string mirror) => new() { Url = "https://bluemediafiles.com/url-generator.php?url=" + mirror, Host = "bluemediafiles.com", Mirror = mirror };

        Assert.Equal(0, FileHosts.Priority(new DownloadLink { Host = "fuckingfast.co" }));
        Assert.Equal(1, FileHosts.Priority(new DownloadLink { Host = "filekeeper.net" }));
        Assert.Equal(2, FileHosts.Priority(new DownloadLink { Host = "gofile.io" }));
        Assert.Equal(2, FileHosts.Priority(Behind("GoFile")));
        Assert.Equal(0, FileHosts.Priority(Behind("FuckingFast")));
        Assert.Equal(1, FileHosts.Priority(Behind("FileKeeper")));
        Assert.Equal(FileHosts.NotPreferred, FileHosts.Priority(Behind("1Fichier")));
        Assert.Equal(FileHosts.NotPreferred, FileHosts.Priority(new DownloadLink { Host = "pixeldrain.com" }));
    }
}
