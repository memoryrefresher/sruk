using System.Net.Http.Headers;
using SeaRaider.Core.Downloads;
using SeaRaider.Core.Models;
using SeaRaider.Core.Scraping;
using SeaRaider.Core.Social;

namespace SeaRaider.Core.Tests;

public class DedupMediaAndResumeTests
{
    private static DownloadLink Link(string url, string? text = null) => LinkClassifier.Classify(new Uri(url), text)!;

    // ---- link de-duplication ------------------------------------------------------------------------

    [Fact]
    public void Same_magnet_from_two_trackers_is_one_link()
    {
        var a = Link("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Game&tr=udp://a.test:1337");
        var b = Link("magnet:?xt=urn:btih:AERUKZ4JVPG66AJDIVTYTK6N54ASGRLH&dn=Game&tr=udp://b.test:80"); // same hash, base32

        Assert.Single(DownloadLinkDeduplicator.Deduplicate([a, b]));
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", DownloadLinkDeduplicator.MagnetInfoHash(b.Url));
    }

    [Fact]
    public void Urls_that_differ_only_cosmetically_are_one_link()
    {
        var links = new[]
        {
            Link("https://files.example.com/game.zip"),
            Link("https://www.files.example.com/game.zip"),
            Link("https://files.example.com/game.zip?utm_source=forum"),
        };
        Assert.Single(DownloadLinkDeduplicator.Deduplicate(links));
    }

    [Fact]
    public void File_host_ids_count_not_the_name_in_the_fragment()
    {
        var links = new[]
        {
            Link("https://fuckingfast.co/k3j4h5g6#Game.part01.rar"),
            Link("https://fuckingfast.co/k3j4h5g6#Game_.part01.rar"),
        };
        Assert.Single(DownloadLinkDeduplicator.Deduplicate(links));
    }

    [Fact]
    public void Same_file_name_on_the_same_host_is_one_file()
    {
        var links = new[]
        {
            Link("https://datanodes.to/aaaaaaaaaa/fg-optional-bonus.bin"),
            Link("https://datanodes.to/bbbbbbbbbb/fg-optional-bonus.bin"),
        };
        Assert.Single(DownloadLinkDeduplicator.Deduplicate(links));
    }

    [Fact]
    public void Tracker_pages_are_dropped_when_there_is_a_magnet()
    {
        var links = new[]
        {
            Link("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Game"),
            Link("https://1337x.to/torrent/123456/game-repack/"),
        };
        var clean = DownloadLinkDeduplicator.Deduplicate(links);
        Assert.Single(clean);
        Assert.Equal(DownloadLinkKind.Magnet, clean[0].Kind);
    }

    [Fact]
    public void Options_are_one_per_mirror_and_kind_with_tags()
    {
        var links = new[]
        {
            Link("https://datanodes.to/aaaaaaaaaa/setup.exe"),
            Link("https://datanodes.to/bbbbbbbbbb/fg-01.bin"),
            Link("https://gofile.io/d/AbC123"),
            Link("https://gofile.io/d/XyZ789"),              // unnamed links on one host: kept, all of them (usually parts)
            Link("https://filekeeper.net/files/game.part1.rar"),
            Link("https://filekeeper.net/files/game.part2.rar"),
            Link("https://filekeeper.net/files/game.part3.rar"),
            Link("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Game"),
        };

        var groups = DownloadLinkDeduplicator.Group(links);

        Assert.Equal(4, groups.Count);
        var dataNodes = groups.Single(g => g.Mirror == "DataNodes");
        Assert.Equal("2 files", dataNodes.Tag);
        Assert.Equal(LinkCategory.FileHost, dataNodes.Category);
        Assert.Equal(2, groups.Single(g => g.Mirror == "Gofile").Links.Count);
        Assert.Equal("3-part archive", groups.Single(g => g.Category == LinkCategory.MultiPartArchive).Tag);
        Assert.Equal(LinkCategory.Torrent, groups[^1].Category); // hosts first, torrents last
    }

    [Fact]
    public void Picking_a_host_downloads_all_its_files()
    {
        var game = new GameMetadata
        {
            Title = "Test",
            DownloadLinks = [Link("https://datanodes.to/aaaaaaaaaa/setup.exe"), Link("https://datanodes.to/bbbbbbbbbb/fg-01.bin")],
        };
        var plan = DownloadPlanner.Plan(game, game.DownloadLinks[0], preferTorrents: false);
        Assert.Equal(2, plan.Links.Count);
    }

    [Fact]
    public void Known_hosts_match_even_when_their_url_shape_changes()
    {
        Assert.Equal(DownloadLinkKind.FileHost, Link("https://datanodes.to/download/Ab12Cd34Ef").Kind); // not the usual /id/name
        Assert.Null(LinkClassifier.Classify(new Uri("https://datanodes.to/faq"), "FAQ"));
    }

    [Fact]
    public void Names_with_characters_windows_forbids_are_kept_not_lost()
    {
        var part = Link("https://fuckingfast.co/k3j4h5g6#Game:_Subtitle.part01.rar");
        Assert.Equal(DownloadLinkKind.MultipartArchivePart, part.Kind);
        Assert.Equal("Game__Subtitle.part01.rar", part.FileName);
    }

    // ---- media filter ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("https://torrent-stats.info/abc.png")]
    [InlineData("https://fitgirl-repacks.site/wp-content/uploads/2024/stats-banner.jpg")]
    [InlineData("https://fitgirl-repacks.site/wp-content/uploads/2016/08/cropped-icon.png")]
    [InlineData("https://example.com/images/download-button.jpg")]
    public void Banners_buttons_and_stats_are_junk(string url) => Assert.True(MediaScraperService.IsJunk(url));

    [Theory]
    [InlineData("https://riotpixels.net/data/ab/cd/screenshot-1.jpg")]
    [InlineData("https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/1145360/ss_abc.1920x1080.jpg")]
    [InlineData("https://x.test/img/space-station.jpg")]
    [InlineData("https://images.igdb.com/igdb/image/upload/t_1080p/abc.jpg")]
    public void Gameplay_screenshots_are_kept(string url) => Assert.False(MediaScraperService.IsJunk(url));

    [Fact]
    public void Alt_text_and_shape_catch_unnamed_banners()
    {
        Assert.True(MediaScraperService.IsJunk("https://i.imgur.com/AbCd123.png", alt: "Seeders: 264 Peers: 9"));
        Assert.True(MediaScraperService.IsJunk("https://i.imgur.com/AbCd123.png", width: 600, height: 100)); // 6:1 banner
        Assert.False(MediaScraperService.IsJunk("https://i.imgur.com/AbCd123.png", width: 1920, height: 1080));
    }

    [Fact]
    public void Outside_media_needs_a_confident_title_match()
    {
        Assert.True(MediaScraperService.IsSameGame("Hades II – Build 18923104 [FitGirl Repack]", "Hades II"));
        Assert.True(MediaScraperService.IsSameGame("ELDEN RING Deluxe Edition", "ELDEN RING"));
        Assert.False(MediaScraperService.IsSameGame("Hades", "Hades II"));
        Assert.False(MediaScraperService.IsSameGame("Portal", "Portal 2"));
    }

    // ---- resume & relay ---------------------------------------------------------------------------------

    [Fact]
    public void Resume_uses_the_strong_etag_for_if_range()
    {
        var response = new HttpResponseMessage { Content = new ByteArrayContent([]) };
        response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
        var validator = ResumeValidator.From(response);
        Assert.Equal("\"v1\"", validator.ToIfRange()!.EntityTag!.Tag);

        response.Headers.ETag = new EntityTagHeaderValue("\"v1\"", isWeak: true);
        Assert.Null(ResumeValidator.From(response).ToIfRange()); // weak tags can't be used; no date either
    }

    [Fact]
    public void Resume_validator_round_trips_through_its_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"searaider-{Guid.NewGuid():N}.part.meta");
        try
        {
            new ResumeValidator("\"abc\"", DateTimeOffset.UnixEpoch, 123).Save(path);
            Assert.Equal(new ResumeValidator("\"abc\"", DateTimeOffset.UnixEpoch, 123), ResumeValidator.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_custom_relay_beats_the_build_default()
    {
        Assert.Equal("wss://mine.example.com/ws", RelayDefaults.Effective("  wss://mine.example.com/ws "));
        Assert.Equal(RelayDefaults.DefaultRelayUrl, RelayDefaults.Effective(null));
    }
}
