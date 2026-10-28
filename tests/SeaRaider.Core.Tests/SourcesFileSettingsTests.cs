using SeaRaider.Core.Catalog;

namespace SeaRaider.Core.Tests;

/// <summary>sources.txt › link games' names and consoles, media settings, and the Live TV link.</summary>
public class SourcesFileSettingsTests
{
    private const string Drive = "https://drive.google.com/file/d/1AbCdEfGhIjKlMnOp/view?usp=sharing";

    [Fact]
    public void LinkGameKeepsItsNameAndConsole()
    {
        var text = SourcesFile.FormatEntries([SourcesFile.WithSettings(Drive, ("name", "Metal Gear Solid 3"), ("console", "ps2"))], []);
        var list = SourcesFile.Parse(text);

        Assert.Single(list.Sources);
        var settings = list.SettingsOf(list.Sources[0], media: false);
        Assert.Equal("Metal Gear Solid 3", settings["name"]);
        Assert.Equal("ps2", settings["console"]);
    }

    [Fact]
    public void MediaKeepsItsSettings()
    {
        var text = SourcesFile.FormatEntries([], [], [
            SourcesFile.WithSettings("https://example.com/live/main", ("name", "My stream"), ("format", "hls"), ("hint", "iframe#player"), ("page-mode", "yes")),
            "https://www.youtube.com/@IGN"]);
        var list = SourcesFile.Parse(text);

        Assert.Equal(2, list.Media.Count);
        var settings = list.SettingsOf(list.Media[0], media: true);
        Assert.Equal("hls", settings["format"]);
        Assert.Equal("iframe#player", settings["hint"]);
        Assert.Equal("yes", settings["page-mode"]);
        Assert.Empty(list.SettingsOf(list.Media[1], media: true));
    }

    [Fact]
    public void LiveTvLinkRoundTrips()
    {
        const string link = "https://iptv.example/get.php?username=u&password=p&type=m3u_plus";
        var list = SourcesFile.Parse(SourcesFile.FormatEntries([], [], null, link));
        Assert.Equal(link, list.LiveTv);
        Assert.Empty(list.Sources);
        Assert.Empty(list.Media);
    }

    [Fact]
    public void LiveTvFilePathWithSpacesIsKept()
    {
        var list = SourcesFile.Parse("[livetv]\n\"D:\\My Lists\\tv list.m3u\"\n");
        Assert.Equal("D:\\My Lists\\tv list.m3u", list.LiveTv);
    }

    [Fact]
    public void AddressesAreNotMistakenForSettings()
    {
        // "name" / "link" at the start of a host must not be read as a setting.
        var list = SourcesFile.Parse("[media]\nhttps://example.com/a\nlink.example.com/b\nname.example.org\n");
        Assert.Equal(3, list.Media.Count);
    }
}
