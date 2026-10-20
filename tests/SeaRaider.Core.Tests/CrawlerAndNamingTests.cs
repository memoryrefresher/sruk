using SeaRaider.Core.Downloads;
using SeaRaider.Core.Installing;
using SeaRaider.Core.Scraping;

namespace SeaRaider.Core.Tests;

public class CrawlerAndNamingTests
{
    // ---- pagination URLs ----------------------------------------------------------------------

    [Theory]
    [InlineData("https://site.test/games/", 2, "https://site.test/games/page/2/")]
    [InlineData("https://site.test/games", 3, "https://site.test/games/page/3/")]
    [InlineData("https://site.test/page/4/", 5, "https://site.test/page/5/")]
    [InlineData("https://site.test/list?page=1&sort=new", 2, "https://site.test/list?page=2&sort=new")]
    [InlineData("https://site.test/games/", 1, "https://site.test/games/")]
    public void Builds_page_urls(string start, int page, string expected) =>
        Assert.Equal(expected, UrlRules.BuildPageUrl(new Uri(start), page).AbsoluteUri);

    [Theory]
    [InlineData("https://site.test/", true)]
    [InlineData("https://site.test/category/action/", true)]
    [InlineData("https://site.test/tag/rpg/", true)]
    [InlineData("https://site.test/page/3/", true)]
    [InlineData("https://site.test/sitemap.xml", true)]
    [InlineData("https://site.test/hollow-depths/", false)]
    [InlineData("https://site.test/2024/05/hollow-depths/", false)]
    public void Recognises_non_game_paths(string url, bool expected) =>
        Assert.Equal(expected, UrlRules.IsNonContentPath(new Uri(url)));

    [Fact]
    public void Same_site_ignores_www() =>
        Assert.True(UrlRules.SameSite(new Uri("https://www.site.test/a"), new Uri("https://site.test/b")));

    // ---- sitemaps -----------------------------------------------------------------------------

    [Fact]
    public void Parses_sitemap_index()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <sitemap><loc>https://site.test/post-sitemap.xml</loc><lastmod>2026-01-02T10:00:00+00:00</lastmod></sitemap>
              <sitemap><loc>https://site.test/category-sitemap.xml</loc></sitemap>
            </sitemapindex>
            """;
        var parsed = SitemapReader.Parse(xml, new Uri("https://site.test/sitemap_index.xml"));

        Assert.NotNull(parsed);
        Assert.True(parsed!.IsIndex);
        Assert.Equal(2, parsed.Entries.Count);
        Assert.True(SitemapReader.IsLikelyContentSitemap(parsed.Entries[0].Location));
        Assert.False(SitemapReader.IsLikelyContentSitemap(parsed.Entries[1].Location));
    }

    [Fact]
    public void Parses_url_set_with_lastmod()
    {
        const string xml = """
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://site.test/game-one/</loc><lastmod>2025-12-01</lastmod></url>
              <url><loc>https://site.test/game-two/</loc></url>
            </urlset>
            """;
        var parsed = SitemapReader.Parse(xml, new Uri("https://site.test/post-sitemap.xml"))!;

        Assert.False(parsed.IsIndex);
        Assert.Equal(new DateTime(2025, 12, 1, 0, 0, 0, DateTimeKind.Utc), parsed.Entries[0].LastModifiedUtc);
        Assert.Null(parsed.Entries[1].LastModifiedUtc);
        Assert.Equal("https://site.test/game-two/", parsed.Entries[1].Location.AbsoluteUri);
    }

    [Fact]
    public void Html_is_not_a_sitemap() =>
        Assert.Null(SitemapReader.Parse("<html><body>Not found</body></html>", new Uri("https://site.test/sitemap.xml")));

    // ---- robots.txt ---------------------------------------------------------------------------

    [Fact]
    public void Robots_rules_use_longest_match_and_collect_sitemaps()
    {
        const string text = """
            User-agent: *
            Disallow: /private/
            Allow: /private/games/
            Crawl-delay: 2
            Sitemap: https://site.test/sitemap_index.xml
            """;
        var robots = RobotsTxt.Parse(text, new Uri("https://site.test/robots.txt"), "SeaRaider");

        Assert.False(robots.IsAllowed(new Uri("https://site.test/private/notes")));
        Assert.True(robots.IsAllowed(new Uri("https://site.test/private/games/one")));
        Assert.True(robots.IsAllowed(new Uri("https://site.test/games/one")));
        Assert.Equal(TimeSpan.FromSeconds(2), robots.CrawlDelay);
        Assert.Single(robots.Sitemaps);
    }

    // ---- pre-download renaming ----------------------------------------------------------------

    [Theory]
    [InlineData("My.Game.part01.rar", "My.Game", ".part01.rar")]
    [InlineData("game.7z.001", "game", ".7z.001")]
    [InlineData("game.z02", "game", ".z02")]
    [InlineData("game.v1.2.zip", "game.v1.2", ".zip")]
    [InlineData("game.tar.gz", "game", ".tar.gz")]
    public void Splits_names_without_breaking_volume_suffixes(string name, string expectedBase, string expectedSuffix)
    {
        var (baseName, suffix) = FileNameRules.Split(name);
        Assert.Equal(expectedBase, baseName);
        Assert.Equal(expectedSuffix, suffix);
    }

    [Fact]
    public void Renaming_keeps_parts_paired()
    {
        var renamed = new[] { "x.part1.rar", "x.part2.rar" }.Select(n => FileNameRules.Rename(n, "Hollow Depths")).ToList();
        Assert.Equal(new[] { "Hollow Depths.part1.rar", "Hollow Depths.part2.rar" }, renamed);
        Assert.True(LinkClassifier.TryParseVolume(renamed[1], out var key, out var part));
        Assert.Equal("hollow depths", key);
        Assert.Equal(2, part);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad:name")]
    [InlineData("ends with dot.")]
    public void Rejects_bad_file_names(string name) => Assert.NotNull(FileNameRules.Validate(name));

    // ---- installer command lines --------------------------------------------------------------

    [Theory]
    [InlineData("\"C:\\Games\\X\\unins000.exe\" /SILENT", "C:\\Games\\X\\unins000.exe", "/SILENT")]
    [InlineData("MsiExec.exe /X{1234}", "MsiExec.exe", "/X{1234}")]
    [InlineData("C:\\Program Files\\X Game\\uninst.exe /S", "C:\\Program Files\\X Game\\uninst.exe", "/S")]
    public void Splits_uninstall_command_lines(string commandLine, string file, string args)
    {
        var split = CommandLine.Split(commandLine);
        Assert.Equal<(string, string)?>((file, args), split);
    }

    [Fact]
    public void Inno_silent_install_targets_the_chosen_folder()
    {
        var info = InstallerDetector.BuildStartInfo(@"C:\setup\setup.exe", Models.InstallerKind.InnoSetup, @"D:\Games\Hollow Depths", silent: true, null);
        Assert.Contains("/VERYSILENT", info.Arguments);
        Assert.Contains("/DIR=\"D:\\Games\\Hollow Depths\"", info.Arguments);
    }

    // ---- torrent DHT bootstrap -----------------------------------------------------------------

    [Fact]
    public void Compact_dht_node_has_id_then_ipv4_then_big_endian_port()
    {
        var node = MonoTorrentDownloader.CompactNode(System.Net.IPAddress.Parse("67.215.246.10"), 6881).ToArray();

        Assert.Equal(26, node.Length);
        Assert.Equal(new byte[] { 67, 215, 246, 10 }, node[20..24]);
        Assert.Equal(6881, (node[24] << 8) | node[25]);
    }
}
