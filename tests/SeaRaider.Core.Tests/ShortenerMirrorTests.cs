using Microsoft.Extensions.Logging.Abstractions;
using SeaRaider.Core.Downloads;
using SeaRaider.Core.Models;
using SeaRaider.Core.Scraping;

namespace SeaRaider.Core.Tests;

/// <summary>Sites whose every download is a "Click Here" through a link shortener (Zovo), named by the text next to it.</summary>
public class ShortenerMirrorTests
{
    private static readonly Uri Page = new("https://demos.example/game/space-demo/");

    private const string SixButtons = """
        <html><body><article>
          <h1>Space Demo</h1>
          <p>A short demo of a space game, with enough text here for the parser to treat it as a description block.</p>
          <div class="downloads">
            <strong>Torrent</strong>: <a href="https://zovo.to/aaa111">Click Here</a><br>
            <strong>Gofile</strong> – <a href="https://zovo.to/bbb222">Click Here</a><br>
            Pixeldrain: <a href="https://zovo.to/ccc333">Click Here</a><br>
            1fichier | <a href="https://zovo.to/ddd444">Click Here</a><br>
            <a href="https://zovo.to/eee555">Click Here</a> (Mega)<br>
            Direct Link: <a href="https://zovo.to/fff666">Click Here</a>
          </div>
        </article></body></html>
        """;

    private static async Task<List<DownloadLink>> LinksAsync(string html, string? labels = null)
    {
        using var doc = await GamePageParser.ParseHtmlAsync(html, Page, CancellationToken.None);
        var parser = new GamePageParser(NullLogger<GamePageParser>.Instance);
        return parser.FindDownloadLinks(doc, Page, new ScraperProfile { MirrorLabels = labels }, []);
    }

    [Fact]
    public async Task Six_click_heres_are_six_mirrors_named_by_their_labels()
    {
        var groups = DownloadLinkDeduplicator.Group(await LinksAsync(SixButtons));

        Assert.Equal(6, groups.Count);
        Assert.All(groups, g => Assert.Single(g.Links)); // never "6 files" of one download
        Assert.Equal(["Gofile", "Pixeldrain", "1fichier", "Mega", "Direct Link", "Torrent"], groups.Select(g => g.Mirror));
        var torrent = Assert.Single(groups, g => g.Category == LinkCategory.Torrent);
        Assert.True(torrent.Representative.LeadsToTorrent);
    }

    [Fact]
    public async Task Typed_labels_pick_the_name_out_of_longer_text()
    {
        const string html = """
            <html><body><div>
              <p>Download Space Demo via uTorrent Magnet (fast) <a href="https://zovo.to/aaa111">Click Here</a></p>
              <p>Mirror 2 on GoFile servers <a href="https://zovo.to/bbb222">Click Here</a></p>
            </div></body></html>
            """;
        var groups = DownloadLinkDeduplicator.Group(await LinksAsync(html, "Torrent, Magnet, GoFile"));

        Assert.Contains(groups, g => g.Mirror == "GoFile" && g.Category == LinkCategory.FileHost);
        Assert.Contains(groups, g => g.Mirror == "Magnet" && g.Category == LinkCategory.Torrent);
    }

    [Fact]
    public async Task Labels_in_a_table_or_a_heading_above_count_too()
    {
        const string html = """
            <html><body>
              <table><tr><td>Pixeldrain</td><td><a href="https://zovo.to/aaa111">Click Here</a></td></tr>
                     <tr><td>Torrent</td><td><a href="https://zovo.to/bbb222">Click Here</a></td></tr></table>
              <h3>Gofile</h3><p><a href="https://zovo.to/ccc333">Click Here</a></p>
            </body></html>
            """;
        var groups = DownloadLinkDeduplicator.Group(await LinksAsync(html));

        Assert.Equal(["Pixeldrain", "Gofile", "Torrent"], groups.Select(g => g.Mirror));
    }

    [Fact]
    public async Task Same_or_missing_names_stay_separate_mirrors_and_parts_form_a_set()
    {
        const string html = """
            <html><body><div>
              <a href="https://zovo.to/aaa111">Click Here</a><br>
              <a href="https://zovo.to/bbb222">Click Here</a><br>
              Gofile Part 1: <a href="https://zovo.to/ccc333">Click Here</a><br>
              Gofile Part 2: <a href="https://zovo.to/ddd444">Click Here</a>
            </div></body></html>
            """;
        var groups = DownloadLinkDeduplicator.Group(await LinksAsync(html));

        Assert.Equal(2, groups.Count(g => g.Category == LinkCategory.FileHost));
        var set = Assert.Single(groups, g => g.Category == LinkCategory.MultiPartArchive);
        Assert.Equal("Gofile", set.Mirror);
        Assert.Equal([1, 2], set.Links.Select(l => l.PartNumber));
    }

    [Fact]
    public void A_shortener_torrent_is_planned_as_a_torrent()
    {
        var torrent = new DownloadLink { Url = "https://zovo.to/aaa111", Kind = DownloadLinkKind.FileHost, Host = "zovo.to", Mirror = "Torrent", LeadsToTorrent = true };
        var file = new DownloadLink { Url = "https://zovo.to/bbb222", Kind = DownloadLinkKind.FileHost, Host = "zovo.to", Mirror = "Gofile" };
        var game = new GameMetadata { DownloadLinks = [torrent, file] };

        Assert.Equal(DownloadTransport.Torrent, DownloadPlanner.Plan(game, torrent, preferTorrents: false).Transport);
        Assert.Equal(DownloadTransport.Http, DownloadPlanner.Plan(game, file, preferTorrents: false).Transport);
        Assert.Same(torrent, DownloadPlanner.TorrentOf(game.DownloadLinks));
        Assert.DoesNotContain(DownloadPlanner.AlternatesFor(game.DownloadLinks, file), l => l.LeadsToTorrent);
    }

    [Fact]
    public async Task A_button_after_or_takes_the_name_before_it()
    {
        const string html = """
            <html><body><div>
              Gofile: <a href="https://zovo.to/aaa111">Click Here</a> or <a href="https://zovo.to/bbb222">Click Here</a><br>
              Torrent: <a href="https://zovo.to/ccc333">Click Here</a>
            </div></body></html>
            """;
        var groups = DownloadLinkDeduplicator.Group(await LinksAsync(html));

        Assert.Equal(["Gofile", "Gofile (2)", "Torrent"], groups.Select(g => g.Mirror));
    }

    // ---- turning a short link into where it leads ---------------------------------------------------

    private static readonly Uri Short = new("https://zovo.to/aB3xY9");

    [Fact]
    public void A_file_host_link_in_the_short_links_page_is_the_destination()
    {
        const string html = """<a href="https://ads.example/win">Download</a> <a href="https://datanodes.to/abcd1234/Game.zip">Visit</a>""";
        Assert.Equal("datanodes.to", FileHostResolver.FindShortLinkDestination(html, Short, wantTorrent: false)?.Host);
    }

    [Fact]
    public void A_base64_destination_in_a_script_is_found()
    {
        var hidden = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("https://filekeeper.net/files/abcdef123"));
        var html = $"<script>var go = atob('{hidden}'); setTimeout(() => location = go, 10000);</script>";
        Assert.Equal("https://filekeeper.net/files/abcdef123", FileHostResolver.FindShortLinkDestination(html, Short, wantTorrent: false)?.AbsoluteUri);
    }

    [Fact]
    public void A_link_container_is_not_a_destination_but_the_host_behind_it_is()
    {
        const string html = """<a href="https://filecrypt.cc/Container/ABC123.html">x</a><iframe src="https://filekeeper.net/files/abcdef123"></iframe>""";
        Assert.Equal("filekeeper.net", FileHostResolver.FindShortLinkDestination(html, new Uri("https://filecrypt.cc/Link/xyz.html"), wantTorrent: false)?.Host);
        Assert.True(FileHosts.IsLinkProtector("filecrypt.cc"));
        Assert.DoesNotContain("__CLICK__", BrowserPageScript.BuildParts(2));
    }

    [Fact]
    public void Ads_and_the_shortener_itself_are_never_destinations()
    {
        const string html = """<a href="https://ads.example/x">Visit</a><a href="https://zovo.to/other1">Next</a><script src="https://cdn.example/app.js"></script>""";
        Assert.Null(FileHostResolver.FindShortLinkDestination(html, Short, wantTorrent: false));
    }

    [Fact]
    public void Magnets_count_only_for_torrent_mirrors()
    {
        const string html = """<a href="magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&amp;dn=Demo">Get</a>""";
        Assert.Equal("magnet", FileHostResolver.FindShortLinkDestination(html, Short, wantTorrent: true)?.Scheme);
        Assert.Null(FileHostResolver.FindShortLinkDestination(html, Short, wantTorrent: false));
    }

    [Fact]
    public void A_destination_in_the_short_link_itself_is_used()
    {
        var link = new Uri("https://zovo.to/go?url=" + Uri.EscapeDataString("https://datanodes.to/zzzz9999/Demo.rar"));
        Assert.Equal("datanodes.to", FileHostResolver.FindShortLinkDestination("<html></html>", link, wantTorrent: false)?.Host);
    }

    [Theory]
    [InlineData("Torrent, Gofile ,pixeldrain", 3)]
    [InlineData("Torrent\nGofile; Torrent", 2)]
    [InlineData("", 0)]
    public void Typed_labels_are_split_on_commas_lines_and_semicolons(string text, int count) =>
        Assert.Equal(count, MirrorLabels.Parse(text).Count);

    [Fact]
    public void Page_script_carries_blocked_links_and_the_ad_trap_check()
    {
        var script = BrowserPageScript.Build(click: true, blocked: ["https://ads.example/x"]);
        Assert.Contains("\"https://ads.example/x\"", script);
        Assert.Contains("res.trap = true", script);
        Assert.DoesNotContain("__", script);
    }

    [Fact]
    public void Torrent_collection_script_looks_for_magnets_and_accepts_torrent_files()
    {
        var script = BrowserPageScript.Build(click: true, fromShortener: true, wantTorrent: true);
        Assert.Contains("const wantTorrent = true", script);
        Assert.DoesNotContain("__", script);
        Assert.True(BrowserPageScript.IsTorrentFile("Space Demo.torrent", null));
        Assert.True(BrowserPageScript.IsTorrentFile("download", "application/x-bittorrent"));
        Assert.False(BrowserPageScript.IsTorrentFile("setup.exe", "application/octet-stream"));
    }
}
