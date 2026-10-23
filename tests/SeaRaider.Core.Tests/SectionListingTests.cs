using Microsoft.Extensions.Logging.Abstractions;
using SeaRaider.Core.Emulation;
using SeaRaider.Core.Models;
using SeaRaider.Core.Scraping;

namespace SeaRaider.Core.Tests;

/// <summary>Homebrew archives laid out like PDRoms: a section listing per console and a "Download" button to a script.</summary>
public class SectionListingTests
{
    private static readonly Uri Listing = new("https://pdroms.de/files/nintendo-game-boy-advance-gba");

    private const string ListingHtml = """
        <html><body>
          <nav><a href="/news">News</a> <a href="/files">Files</a> <a href="/files/nintendo-ds">DS</a></nav>
          <div class="entry"><a href="/files/nintendo-game-boy-advance-gba/lvr"><img src="/a.jpg"></a>
            <a href="/files/nintendo-game-boy-advance-gba/lvr">LvR</a> <a href="/news/nintendo-game-boy-advance-gba">GBA</a></div>
          <div class="entry"><a href="/files/nintendo-game-boy-advance-gba/kukoo-2-advance">Kukoo 2 Advance</a></div>
          <div class="entry"><a href="/files/nintendo-game-boy-advance-gba/lost-marbles">Lost Marble</a></div>
          <a href="/files/nintendo-game-boy-advance-gba/page/2">2</a> <a href="/files/nintendo-game-boy-advance-gba/page/2">»</a>
        </body></html>
        """;

    [Fact]
    public async Task A_section_listing_yields_the_entries_below_it()
    {
        using var doc = await GamePageParser.ParseHtmlAsync(ListingHtml, Listing, CancellationToken.None);
        var links = new GamePageParser(NullLogger<GamePageParser>.Instance).FindItemLinks(doc, Listing, new ScraperProfile(), []);

        Assert.Equal(
            new[] { "lvr", "kukoo-2-advance", "lost-marbles" },
            links.Select(l => l.Segments[^1]).ToArray());
        Assert.All(links, l => Assert.True(GamePageParser.IsChildOf(l, Listing)));
    }

    [Fact]
    public void Page_two_of_a_section_is_the_same_section()
    {
        var page2 = new Uri("https://pdroms.de/files/nintendo-game-boy-advance-gba/page/2/");
        Assert.True(GamePageParser.IsChildOf(new Uri("https://pdroms.de/files/nintendo-game-boy-advance-gba/lvr"), page2));
        Assert.False(GamePageParser.IsChildOf(new Uri("https://pdroms.de/files/nintendo-ds/x"), page2));
        Assert.False(GamePageParser.IsChildOf(new Uri("https://pdroms.de/lvr"), new Uri("https://pdroms.de/")));
    }

    [Theory]
    [InlineData("https://pdroms.de/?__df=543a270c113a", "💾 Download 1 Bit Ball Wall v1f", true)]
    [InlineData("https://example.org/download.php?id=12", "Download", true)]
    [InlineData("https://example.org/?dl=9", "Download now", true)]
    [InlineData("https://example.org/?p=123", "Download", false)]          // a WordPress post, not a file
    [InlineData("https://example.org/?__df=1", "Read more", false)]       // not a download button
    [InlineData("https://example.org/games/thing", "Download", false)]    // a page
    public void Download_buttons_to_a_script_are_downloads(string url, string label, bool expected)
    {
        var link = LinkClassifier.Classify(new Uri(url), label);
        Assert.Equal(expected, link is { Kind: DownloadLinkKind.DirectHttp });
        if (expected)
        {
            Assert.Equal(string.Empty, link!.FileName); // named by the server
            Assert.StartsWith("Download", link.Label);
        }
    }

    [Theory]
    [InlineData("https://pdroms.de/files/nintendo-game-boy-advance-gba/lvr", "gba")]
    [InlineData("https://pdroms.de/files/nintendo-entertainment-system-nes-famicom/1k2p", "nes")]
    [InlineData("https://pdroms.de/files/nintendo-game-boy-color-gbc/x", "gbc")]
    [InlineData("https://pdroms.de/files/sega-genesis-megadrive/x", "genesis")]
    public void Section_addresses_name_the_console(string url, string system) =>
        Assert.Equal(system, GameSystems.FromText(url)?.Id);
}

public class FileTypeSnifferTests
{
    [Fact]
    public void Recognises_archives_and_cartridges_by_their_first_bytes()
    {
        Assert.Equal(".zip", SeaRaider.Core.Downloads.FileTypeSniffer.Sniff(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00 }));
        Assert.Equal(".7z", SeaRaider.Core.Downloads.FileTypeSniffer.Sniff(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00 }));
        Assert.Equal(".nes", SeaRaider.Core.Downloads.FileTypeSniffer.Sniff("NES\u001A\u0002\u0001"u8));

        var gba = new byte[0xC0];
        new byte[] { 0x24, 0xFF, 0xAE, 0x51, 0x69, 0x9A, 0xA2, 0x21 }.CopyTo(gba, 4);
        gba[0xB2] = 0x96;
        Assert.Equal(".gba", SeaRaider.Core.Downloads.FileTypeSniffer.Sniff(gba));

        var gbc = new byte[0x150];
        new byte[] { 0xCE, 0xED, 0x66, 0x66, 0xCC, 0x0D, 0x00, 0x0B }.CopyTo(gbc, 0x104);
        gbc[0x143] = 0x80;
        Assert.Equal(".gbc", SeaRaider.Core.Downloads.FileTypeSniffer.Sniff(gbc));

        Assert.Null(SeaRaider.Core.Downloads.FileTypeSniffer.Sniff("<html>"u8));
    }
}
