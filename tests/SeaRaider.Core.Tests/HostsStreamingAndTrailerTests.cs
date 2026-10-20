using SeaRaider.Core.Downloads;
using SeaRaider.Core.Extraction;
using SeaRaider.Core.Models;
using SeaRaider.Core.Scraping;

namespace SeaRaider.Core.Tests;

public class HostsStreamingAndTrailerTests
{
    private static DownloadLink Link(string url) => LinkClassifier.Classify(new Uri(url), null)!;

    private static DownloadFileSpec Spec(string name) => new() { OriginalFileName = name, Url = "https://x.test/" + name };

    // ---- dead links -----------------------------------------------------------------------------------

    [Fact]
    public void Host_pages_saying_the_file_is_gone_are_dead()
    {
        // DataNodes' page from the bug report.
        Assert.True(DeadLinkDetector.LooksDead(
            "File Not Found. File info. The file you were looking for could not be found, sorry for any inconvenience. " +
            "Possible causes: The file expired. The file was deleted by its owner."));
        // FuckingFast's normal download page.
        Assert.False(DeadLinkDetector.LooksDead(
            "FUCKINGFAST Fucking fast file hosting service. Frosthaven_--_fitgirl-repacks.site_--_.part1.rar DOWNLOAD " +
            "Size: 2.0GB | Downloads: 173. First click - open ads. Second - start download"));
    }

    [Fact]
    public void Page_text_ignores_scripts_and_tags()
    {
        var text = HttpResumableDownloader.PageText(
            "<html><script>var e = 'file not found';</script><body><h1>Download</h1> &amp; more</body></html>");
        Assert.DoesNotContain("file not found", text);
        Assert.Contains("Download", text);
        Assert.Contains("& more", text);
    }

    // ---- download browser rules ---------------------------------------------------------------------

    [Theory]
    [InlineData("Game.part3.rar", "Game.part3.rar", true)]
    [InlineData("Game.part3.rar", "Game_.part3.rar", true)]          // host renamed it slightly
    [InlineData("Game.part3.rar", "FastDownloader_setup.exe", false)] // an ad's "installer"
    [InlineData("setup.exe", "setup (1).exe", true)]
    [InlineData(null, "anything.bin", true)]
    public void Only_the_requested_file_is_accepted(string? expected, string suggested, bool accepted) =>
        Assert.Equal(accepted, BrowserPageScript.IsExpectedFile(expected, suggested, 2_000_000_000));

    [Fact]
    public void Tiny_downloads_are_error_pages_not_game_files() =>
        Assert.False(BrowserPageScript.IsExpectedFile("Game.part3.rar", "Game.part3.rar", 4_000));

    [Theory]
    [InlineData("https://fuckingfast.co/dl/AbCdEfGh1234", true)]
    [InlineData("https://cdn.host.test/files/Game.part12.rar", true)]
    [InlineData("https://cdn.host.test/fg-optional-bonus.bin", true)]
    [InlineData("https://ads.example.com/landing?id=5", false)]
    public void Pop_ups_are_followed_only_when_they_are_the_file(string url, bool follow) =>
        Assert.Equal(follow, BrowserPageScript.LooksLikeFileUrl(new Uri(url)));

    [Fact]
    public void Page_script_carries_the_dead_markers_and_click_switch()
    {
        var withClick = BrowserPageScript.Build(click: true);
        Assert.Contains("could not be found", withClick);
        Assert.Contains("!true", withClick);
        Assert.Contains("!false", BrowserPageScript.Build(click: false));
        Assert.DoesNotContain("__", withClick);
    }

    // ---- other hosts for the same file --------------------------------------------------------------

    private static readonly DownloadLink[] FitGirlPage =
    [
        Link("https://datanodes.to/aaaaaaaa1/Game.part1.rar"),
        Link("https://datanodes.to/aaaaaaaa2/Game.part2.rar"),
        Link("https://fuckingfast.co/bbbbbbb1#Game.part1.rar"),
        Link("https://fuckingfast.co/bbbbbbb2#Game.part2.rar"),
        Link("https://filekeeper.net/files/Game.part1.rar"),
        Link("https://filekeeper.net/files/Game.part2.rar"),
        Link("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Game"),
    ];

    [Fact]
    public void A_lost_part_comes_from_the_other_hosts()
    {
        var alternates = DownloadPlanner.AlternatesFor(FitGirlPage, FitGirlPage[3]); // FuckingFast part 2

        // FileKeeper is the preferred fallback; the other hosts follow.
        Assert.Equal(new[] { "FileKeeper", "DataNodes" }, alternates.Select(DownloadLinkDeduplicator.MirrorOf));
        Assert.All(alternates, a => Assert.Equal(2, a.PartNumber));
    }

    [Fact]
    public void FuckingFast_then_FileKeeper_are_planned_before_every_other_host_and_the_torrent()
    {
        var game = new GameMetadata { DownloadLinks = [.. FitGirlPage] };

        var plan = DownloadPlanner.Plan(game, preferred: null, preferTorrents: true);

        Assert.Equal(DownloadTransport.Http, plan.Transport);
        Assert.All(plan.Links, l => Assert.Equal("FuckingFast", DownloadLinkDeduplicator.MirrorOf(l)));

        var withoutFuckingFast = new GameMetadata { DownloadLinks = FitGirlPage.Where(l => !l.Url.Contains("fuckingfast")).ToList() };
        var second = DownloadPlanner.Plan(withoutFuckingFast, preferred: null, preferTorrents: false);
        Assert.All(second.Links, l => Assert.Equal("FileKeeper", DownloadLinkDeduplicator.MirrorOf(l)));
    }

    [Fact]
    public void FileKeeper_is_a_browser_host_not_a_direct_download()
    {
        Assert.Equal(DownloadLinkKind.MultipartArchivePart, FitGirlPage[4].Kind);
        Assert.Equal("FileKeeper", FitGirlPage[4].Mirror);
        Assert.Equal(HostAccess.Browser, FileHosts.Find("filekeeper.net")!.Access);
    }

    [Fact]
    public void Zovo_click_here_links_are_downloads_on_a_browser_host()
    {
        var link = LinkClassifier.Classify(new Uri("https://zovo.to/aB3xY9"), "Click Here");

        Assert.NotNull(link);
        Assert.Equal(DownloadLinkKind.FileHost, link!.Kind);
        Assert.Equal("Mirror", link.Mirror);
        Assert.Equal("Mirror", link.Label);
        Assert.Equal(HostAccess.Browser, FileHosts.Find("www.zovo.to")!.Access);
        Assert.Null(LinkClassifier.Classify(new Uri("https://zovo.to/"), "Zovo"));
        Assert.Null(LinkClassifier.Classify(new Uri("https://zovo.to/faq"), "FAQ"));
    }

    [Theory]
    [InlineData("zovo.to", true)]
    [InlineData("www.zovo.to", true)]
    [InlineData("go.zovo.to", true)]
    [InlineData("notzovo.to", false)]
    [InlineData("gofile.io", false)]
    public void Zovo_is_a_link_shortener(string host, bool shortener) =>
        Assert.Equal(shortener, FileHosts.IsLinkShortener(host));

    [Fact]
    public void Page_script_knows_the_shorteners_and_the_shortener_switch()
    {
        var viaShortener = BrowserPageScript.Build(click: true, fromShortener: true);
        Assert.Contains("\"zovo.to\"", viaShortener);
        Assert.Contains("shortener || true", viaShortener);
        Assert.Contains("shortener || false", BrowserPageScript.Build(click: true));
        Assert.DoesNotContain("__", viaShortener);
    }

    [Fact]
    public void The_torrent_is_the_last_resort() =>
        Assert.Equal(DownloadLinkKind.Magnet, DownloadPlanner.TorrentOf(FitGirlPage)!.Kind);

    // ---- low disk space mode ------------------------------------------------------------------------

    [Fact]
    public void Only_complete_rar_volume_sets_unpack_while_downloading()
    {
        Assert.True(DownloadAndExtractManager.IsRarVolumeSet([Spec("Game.part1.rar"), Spec("Game.part2.rar"), Spec("Game.part3.rar")]));
        Assert.True(DownloadAndExtractManager.IsRarVolumeSet([Spec("game.rar"), Spec("game.r00"), Spec("game.r01")]));
        Assert.False(DownloadAndExtractManager.IsRarVolumeSet([Spec("game.7z.001"), Spec("game.7z.002")]));   // 7z needs the whole set
        Assert.False(DownloadAndExtractManager.IsRarVolumeSet([Spec("Game.part2.rar"), Spec("Game.part1.rar")])); // out of order
        Assert.False(DownloadAndExtractManager.IsRarVolumeSet([Spec("setup.exe"), Spec("fg-01.bin")]));          // a repack's files
        Assert.False(DownloadAndExtractManager.IsRarVolumeSet([Spec("Game.part1.rar")]));
    }

    [Fact]
    public async Task The_downloader_waits_while_the_extractor_is_behind()
    {
        var feed = new VolumeFeed(5, aheadLimit: 2);
        await feed.WaitForRoomAsync(0, CancellationToken.None); // nothing opened yet: volumes 0 and 1 may download
        await feed.WaitForRoomAsync(1, CancellationToken.None);
        Assert.True(feed.IsAhead(2));

        feed.Completed(0, "v0");
        Assert.Equal("v0", feed.WaitFor(0, CancellationToken.None)); // extractor opens volume 0
        Assert.False(feed.IsAhead(2));

        var waiting = feed.WaitForRoomAsync(3, CancellationToken.None);
        await Task.Delay(100);
        Assert.False(waiting.IsCompleted);
        feed.Completed(1, "v1");
        feed.WaitFor(1, CancellationToken.None);
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void A_failed_download_stops_the_extractor()
    {
        var feed = new VolumeFeed(2);
        feed.Fail(new IOException("gone"));
        Assert.ThrowsAny<OperationCanceledException>(() => feed.WaitFor(0, CancellationToken.None));
    }

    [Theory]
    [InlineData("14.9 GB", 15_998_753_177L)]
    [InlineData("800 MB", 838_860_800L)]
    [InlineData("1,5 GiB", 1_610_612_736L)]
    public void Page_sizes_are_read_for_the_space_check(string text, long expected) =>
        Assert.InRange(Formatting.ParseBytes(text)!.Value, expected - 1024, expected + 1024);

    [Fact]
    public void Text_without_a_size_has_none() => Assert.Null(Formatting.ParseBytes("Repack by FitGirl"));

    // ---- trailers ---------------------------------------------------------------------------------------

    [Fact]
    public void Real_trailers_come_before_clips_and_spoilers()
    {
        // Frosthaven's movie list on Steam (October 2026), in Steam's order.
        var movies = new[]
        {
            ("Frosthaven - Launch Trailer", true), ("Frosthaven - Intro Cinematic", true), ("Frosthaven - Gameplay Overview", true),
            ("Release Announce (EN)", true), ("Into the Abyss - Spoiler Warning", true), ("Character Creation", false),
            ("The Outpost", false), ("Announce Trailer", false),
        }.Select((m, i) => new SteamMovie(m.Item1, m.Item2, i, $"https://video.test/{i}/hls_264_master.m3u8", null)).ToList();

        var ordered = SteamMovie.InTrailerOrder(movies).Select(m => m.Name).ToList();

        Assert.Equal("Frosthaven - Launch Trailer", ordered[0]);
        Assert.True(ordered.IndexOf("Announce Trailer") < ordered.IndexOf("The Outpost"));
        Assert.True(ordered.IndexOf("Into the Abyss - Spoiler Warning") > ordered.IndexOf("Character Creation"));
    }

    [Fact]
    public void Media_from_older_rules_is_looked_at_again()
    {
        var game = new GameMetadata { MediaChecked = true, MediaVersion = 1 };
        Assert.False(game.MediaUpToDate);
        game.MediaVersion = GameMetadata.CurrentMediaVersion;
        Assert.True(game.MediaUpToDate);
    }
}

public class NonGameFilterTests
{
    [Theory]
    [InlineData("Updates Digest for October 15, 2026")]
    [InlineData("Upcoming Repacks")]
    [InlineData("A Call For Donations")]
    [InlineData("Beware of Fake FitGirl Sites!")]
    [InlineData("Site News: we moved")]
    [InlineData("Announcement: new mirrors")]
    [InlineData("FAQ")]
    public void Site_posts_are_not_games(string title) => Assert.True(Catalog.NonGameFilter.IsNotAGame(title));

    [Theory]
    [InlineData("News Tower")]
    [InlineData("Foundation: Supporter Edition")]
    [InlineData("Frosthaven")]
    [InlineData("Bloodborne: Game of the Year Edition")]
    [InlineData("Faqtory")]
    [InlineData("Digestive Tycoon")]
    public void Games_stay(string title) => Assert.False(Catalog.NonGameFilter.IsNotAGame(title, hasGameFacts: true));

    [Fact]
    public void Weak_words_only_count_without_game_facts()
    {
        Assert.True(Catalog.NonGameFilter.IsNotAGame("October 2026 Updates Digest"));
        Assert.False(Catalog.NonGameFilter.IsNotAGame("Donation Simulator", hasGameFacts: true));
        Assert.False(Catalog.NonGameFilter.IsNotAGame(new CatalogEntry { Title = "Donation Simulator", Size = "2.1 GB" }));
    }
}

public class ArtworkMatchTests
{
    [Theory]
    [InlineData("Forza Horizon 6", "Forza Horizon 6", true)]
    [InlineData("College Football 27", "EA SPORTS College Football 27", true)]   // publisher in front
    [InlineData("Frosthaven", "Frosthaven: Official Companion", false)]          // companion app, not the game
    [InlineData("Forza Horizon 6", "Forza Horizon 6 Soundtrack", false)]
    [InlineData("Forza Horizon 6", "Forza Horizon 5", false)]
    [InlineData("Hades", "Hades II", false)]
    public void Steam_results_must_be_the_game_itself(string title, string candidate, bool match) =>
        Assert.Equal(match, Catalog.ArtworkService.IsMatch(title, candidate));
}

public class PartsAndPicturesTests
{
    private static DownloadLink Link(string url, string? text) => LinkClassifier.Classify(new Uri(url), text)!;

    [Theory]
    [InlineData("Part 1", 1)]
    [InlineData("Part 03 (700 MB)", 3)]
    [InlineData("Pt. 2", 2)]
    [InlineData("3/6", 3)]
    [InlineData("4", 4)]
    [InlineData("Download Part 4", 4)]
    [InlineData("Buzzheavier", null)]
    [InlineData("Mirror 2", null)]
    [InlineData("1080p", null)]
    public void Part_numbers_come_from_the_link_text(string label, int? part) =>
        Assert.Equal(part, LinkClassifier.PartFromLabel(label));

    [Fact]
    public void Unnamed_parts_on_a_host_are_one_complete_set()
    {
        // The bug: only "Part 1" was downloaded, and the game was called installed.
        var page = new GameMetadata
        {
            Title = "Castlevania",
            DownloadLinks =
            [
                Link("https://buzzheavier.com/aaaaaaaa1", "Part 1"),
                Link("https://buzzheavier.com/aaaaaaaa2", "Part 2"),
                Link("https://buzzheavier.com/aaaaaaaa3", "Part 3"),
                Link("https://buzzheavier.com/aaaaaaaa4", "Part 4"),
            ],
        };

        Assert.All(page.DownloadLinks, l => Assert.Equal(DownloadLinkKind.MultipartArchivePart, l.Kind));
        Assert.Equal("4-part archive", DownloadLinkDeduplicator.Group(page.DownloadLinks).Single().Tag);
        Assert.Equal(4, DownloadPlanner.Plan(page, null, preferTorrents: false).Links.Count);
        Assert.Equal(4, DownloadPlanner.Plan(page, page.DownloadLinks[0], preferTorrents: false).Links.Count);
    }

    [Theory]
    [InlineData("https://ru.riotpixels.net/data/ab/cd/bloodborne-screenshot-1.jpg.240p.jpg", "https://ru.riotpixels.net/data/ab/cd/bloodborne-screenshot-1.jpg")]
    [InlineData("https://site.test/wp-content/uploads/2026/10/cover-300x169.jpg", "https://site.test/wp-content/uploads/2026/10/cover.jpg")]
    [InlineData("https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/1/ss_0123456789abcdef0123456789abcdef.600x338.jpg",
                "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/1/ss_0123456789abcdef0123456789abcdef.1920x1080.jpg")]
    [InlineData("https://i0.wp.com/site.test/img/shot.jpg?resize=300%2C169", "https://i0.wp.com/site.test/img/shot.jpg")]
    [InlineData("https://cdn.test/full/shot.jpg", "https://cdn.test/full/shot.jpg")]
    public void Thumbnails_turn_into_the_full_picture(string thumbnail, string full) =>
        Assert.Equal(full, Catalog.ImageQuality.Upgrade(thumbnail));

    [Fact]
    public void Imgur_thumbnails_are_tried_full_size_first() =>
        Assert.Equal("https://i.imgur.com/AbCd123.jpg", Catalog.ImageQuality.Candidates("https://i.imgur.com/AbCd123m.jpg")[0]);

    [Fact]
    public void Picture_sizes_are_read_from_their_headers()
    {
        byte[] png = [0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
            0, 0, 0x07, 0x80, 0, 0, 0x04, 0x38];
        Assert.Equal((1920, 1080), Catalog.ImageQuality.ReadSize(png));

        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 4, 0, 0, 0xFF, 0xC0, 0, 17, 8, 0x02, 0xD0, 0x05, 0x00, 3, 0, 0, 0];
        Assert.Equal((1280, 720), Catalog.ImageQuality.ReadSize(jpeg));
    }

    [Theory]
    [InlineData("bloodborne game of the year edition", "Bloodborne: Game of the Year Edition - Official Trailer", true)]
    [InlineData("college football 27", "EA SPORTS College Football 27 | Official Reveal Trailer", true)]
    [InlineData("college football 27", "College Football 27 Trailer Reaction!", false)]
    [InlineData("bloodborne", "Bloodborne OST - Main Theme", false)]
    [InlineData("forza horizon 6", "Forza Horizon 5 Official Trailer", false)]
    public void YouTube_results_must_be_the_games_trailer(string game, string video, bool accepted) =>
        Assert.Equal(accepted, YouTubeSearch.IsTrailerFor(game, video));
}
