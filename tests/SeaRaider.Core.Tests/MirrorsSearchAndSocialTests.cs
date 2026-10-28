using System.Text.RegularExpressions;
using SeaRaider.Core.Catalog;
using SeaRaider.Core.Downloads;
using SeaRaider.Core.Models;
using SeaRaider.Core.Scraping;
using SeaRaider.Core.Social;

namespace SeaRaider.Core.Tests;

public class MirrorsSearchAndSocialTests
{
    // ---- file hosts & mirrors -----------------------------------------------------------------------

    private static DownloadLink Link(string url, string? text = null) => LinkClassifier.Classify(new Uri(url), text)!;

    [Fact]
    public void File_host_pages_become_mirror_links()
    {
        var buzz = Link("https://buzzheavier.com/abc123xyz", "DOWNLOAD HERE");
        Assert.Equal(DownloadLinkKind.FileHost, buzz.Kind);
        Assert.Equal("Buzzheavier", buzz.Mirror);

        var mega = Link("https://mega.nz/file/AbCdEf12#key");
        Assert.Equal("MEGA", mega.Mirror);
        Assert.Equal(HostAccess.ExternalOnly, FileHosts.Find(mega.Host)!.Access);
    }

    [Fact]
    public void Fitgirl_style_part_names_in_the_fragment_make_a_multipart_set()
    {
        var part = Link("https://fuckingfast.co/k3j4h5g6#Game_--_fitgirl-repacks.site_--_.part01.rar");
        Assert.Equal(DownloadLinkKind.MultipartArchivePart, part.Kind);
        Assert.Equal(1, part.PartNumber);
        Assert.Equal("FuckingFast", part.Mirror);
    }

    [Fact]
    public void Host_home_pages_are_not_downloads() =>
        Assert.Null(LinkClassifier.Classify(new Uri("https://buzzheavier.com/"), "Buzzheavier"));

    [Fact]
    public void Each_mirror_keeps_its_own_multipart_set()
    {
        var links = new[]
        {
            "https://datanodes.to/aaaaaaaaaa/game.part1.rar", "https://datanodes.to/bbbbbbbbbb/game.part2.rar",
            "https://fuckingfast.co/cccccccc#game.part1.rar", "https://fuckingfast.co/dddddddd#game.part2.rar",
        }.Select(u => Link(u)).ToList();

        var normalized = LinkClassifier.NormalizeGroups(links);

        Assert.All(normalized, l => Assert.EndsWith("of 2", l.Label));
        Assert.Equal(2, normalized.Select(l => l.Mirror).Distinct().Count());
    }

    private static GameMetadata Game(params string[] urls) => new() { Title = "Test", DownloadLinks = urls.Select(u => Link(u)).ToList() };

    [Fact]
    public void Planner_prefers_hands_free_hosts_then_torrent_then_captcha_hosts()
    {
        const string magnet = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Game";

        var withResolvable = Game(magnet, "https://gofile.io/d/AbC123", "https://buzzheavier.com/abc123xyz");
        var plan = DownloadPlanner.Plan(withResolvable, null, preferTorrents: false);
        Assert.Equal(DownloadTransport.Http, plan.Transport);
        Assert.Equal("Buzzheavier", plan.Links[0].Mirror);

        Assert.Equal(DownloadTransport.Torrent, DownloadPlanner.Plan(Game(magnet, "https://gofile.io/d/AbC123"), null, false).Transport);
        Assert.Equal("Gofile", DownloadPlanner.Plan(Game("https://gofile.io/d/AbC123"), null, false).Links[0].Mirror);
        Assert.Equal(DownloadTransport.Torrent, DownloadPlanner.Plan(withResolvable, null, preferTorrents: true).Transport);
    }

    [Fact]
    public void Browser_only_hosts_are_never_downloaded_directly()
    {
        var megaOnly = Game("https://mega.nz/file/AbCdEf12#key");
        Assert.Throws<InvalidOperationException>(() => DownloadPlanner.Plan(megaOnly, null, false));
        Assert.Throws<InvalidOperationException>(() => DownloadPlanner.Plan(megaOnly, megaOnly.DownloadLinks[0], false));
    }

    [Fact]
    public void A_picked_part_downloads_the_set_from_the_same_mirror()
    {
        var game = Game("https://datanodes.to/aaaaaaaaaa/game.part1.rar", "https://datanodes.to/bbbbbbbbbb/game.part2.rar",
            "https://fuckingfast.co/cccccccc#game.part1.rar", "https://fuckingfast.co/dddddddd#game.part2.rar");
        var picked = game.DownloadLinks.First(l => l.Mirror == "FuckingFast");

        var plan = DownloadPlanner.Plan(game, picked, false);

        Assert.Equal(2, plan.Links.Count);
        Assert.All(plan.Links, l => Assert.Equal("FuckingFast", l.Mirror));
    }

    // ---- title normalisation & duplicates -------------------------------------------------------------

    [Theory]
    [InlineData("ELDEN RING: Deluxe Edition (v1.10.1 + DLC, MULTi14) [FitGirl Repack]", "Elden Ring Free Download (v1.16)")]
    [InlineData("The Witcher 3: Wild Hunt – Game of the Year Edition", "The Witcher 3: Wild Hunt")]
    [InlineData("Resident Evil 2 (2019) - v20230518 + All DLCs", "Resident Evil 2")]
    [InlineData("Grand Theft Auto V", "Grand Theft Auto 5")]
    [InlineData("Cyberpunk 2077: Ultimate Edition – v2.12 + 4 DLCs", "Cyberpunk 2077 Free Download (v2.13)")]
    public void Same_game_on_different_sites_gets_one_key(string a, string b) =>
        Assert.Equal(TitleNormalizer.DedupKey(a), TitleNormalizer.DedupKey(b));

    [Theory]
    [InlineData("Hades", "Hades II")]
    [InlineData("Cyberpunk 2077", "Cyberpunk")]
    [InlineData("Portal", "Portal 2")]
    public void Different_games_keep_different_keys(string a, string b) =>
        Assert.NotEqual(TitleNormalizer.DedupKey(a), TitleNormalizer.DedupKey(b));

    [Fact]
    public void Deduplicator_merges_copies_and_keeps_the_best_rank()
    {
        var fitgirl = Guid.NewGuid();
        var steamrip = Guid.NewGuid();
        var entries = new[]
        {
            new CatalogEntry { Id = "1", SourceId = steamrip, Title = "Hades II" },
            new CatalogEntry { Id = "2", SourceId = fitgirl, Title = "Elden Ring", CoverImageUrl = "x.jpg" },
            new CatalogEntry { Id = "3", SourceId = fitgirl, Title = "Hades II – Build 18923104", CoverImageUrl = "y.jpg" },
        };

        var groups = GameDeduplicator.Group(entries);

        Assert.Equal(2, groups.Count);
        Assert.Equal("hades 2", groups[0].Key);   // position of its best-ranked copy
        Assert.Equal("3", groups[0].Primary.Id);  // the copy with art is the face
        Assert.Equal(2, groups[0].SourceCount);
    }

    // ---- search scoring ---------------------------------------------------------------------------------

    private static double Score(string query, string title) => SearchScorer.Score(SearchQuery.Parse(query), title);

    [Fact]
    public void Exact_title_beats_longer_titles()
    {
        Assert.Equal(1.0, Score("hades", "Hades"));
        Assert.True(Score("hades", "Hades") > Score("hades", "Hades II"));
        Assert.True(Score("elden ring", "Elden Ring Free Download (v1.16)") > Score("elden ring", "Elden Ring: Shadow of the Erdtree"));
    }

    [Theory]
    [InlineData("eldn ring", "Elden Ring")]                  // typo
    [InlineData("witcher 3", "The Witcher 3: Wild Hunt")]   // inner phrase
    [InlineData("gta 5", "Grand Theft Auto V")]             // acronym + roman numeral
    [InlineData("hollow knight", "Hollow Knight: Silksong")]
    [InlineData("baldurs gate", "Baldur's Gate 3")]
    public void Relevant_titles_pass(string query, string title) =>
        Assert.True(Score(query, title) >= SearchScorer.MinimumScore, $"{query} → {title}: {Score(query, title):0.000}");

    [Theory]
    [InlineData("resident evil 2", "Resident Evil 3")]      // numbers must match
    [InlineData("witcher 3", "The Witcher 2: Assassins of Kings")]
    [InlineData("hades", "Curse of the Dead Gods")]
    [InlineData("\"souls dark\"", "Dark Souls III")]        // quoted phrase is mandatory
    public void Noise_is_dropped(string query, string title) =>
        Assert.True(Score(query, title) < SearchScorer.MinimumScore, $"{query} → {title}: {Score(query, title):0.000}");

    [Fact]
    public void Release_tags_are_stripped_from_what_the_sites_are_asked() =>
        Assert.Equal("Elden Ring", SearchQuery.Parse("Elden Ring v1.10 [FitGirl Repack]").SiteText);

    [Fact]
    public void Levenshtein_and_dice_basics()
    {
        Assert.Equal(1, SearchScorer.Levenshtein("elden", "eldn"));
        Assert.Equal(3, SearchScorer.Levenshtein("kitten", "sitting"));
        Assert.Equal(1.0, SearchScorer.DiceCoefficient("night", "night"));
        Assert.True(SearchScorer.DiceCoefficient("night", "nacht") < 0.5);
    }

    // ---- friends ------------------------------------------------------------------------------------------

    [Fact]
    public void Friend_codes_are_random_and_forgiving_to_type()
    {
        var code = FriendCode.Generate();
        Assert.Matches(new Regex("^SR-[0-9A-HJKMNP-TV-Z]{5}-[0-9A-HJKMNP-TV-Z]{5}$"), code);
        Assert.NotEqual(code, FriendCode.Generate());

        Assert.Equal(code, FriendCode.Normalize(code.ToLowerInvariant().Replace("-", " ")));
        Assert.Equal("SR-00000-11111", FriendCode.Normalize("sr-OOOOO-iiiii"));
        Assert.Null(FriendCode.Normalize("hello"));
        Assert.Null(FriendCode.Normalize("SR-1234"));
    }

    [Fact]
    public void Emote_codes_become_animated_segments()
    {
        var segments = Emotes.Parse("gg :gg: well played :nope:");
        Assert.Equal(3, segments.Count);
        Assert.Equal("gg ", segments[0].Text);
        Assert.True(segments[1].IsEmote);
        Assert.Equal(EmoteAnimation.Bounce, segments[1].Emote!.Animation);
        Assert.Equal(" well played :nope:", segments[2].Text);

        Assert.True(Emotes.IsEmoteOnly(Emotes.Parse(":fire: :fire:")));
        Assert.False(Emotes.IsEmoteOnly(Emotes.Parse("hi :fire:")));
    }

    [Theory]
    [InlineData("relay.example.com", "wss://relay.example.com/ws")]
    [InlineData("http://192.168.1.20:5280", "ws://192.168.1.20:5280/ws")]
    [InlineData("wss://relay.example.com/custom", "wss://relay.example.com/custom")]
    public void Relay_addresses_are_normalised(string input, string expected)
    {
        Assert.True(FriendsService.TryParseRelay(input, out var relay));
        Assert.Equal(expected, relay.AbsoluteUri);
    }

    [Fact]
    public void Non_websocket_relay_addresses_are_rejected() => Assert.False(FriendsService.TryParseRelay("ftp://example.com", out _));

    [Fact]
    public void Relay_frames_omit_empty_fields() =>
        Assert.Equal("{\"type\":\"ping\"}", new RelayFrame { Type = "ping" }.Serialize());

    [Fact]
    public void Social_store_keeps_friends_and_conversations()
    {
        var path = Path.Combine(Path.GetTempPath(), $"searaider-social-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new SocialStore(path);
            var profile = store.GetOrCreateProfile();
            Assert.Equal(profile.UserId, store.GetOrCreateProfile().UserId); // created once

            store.UpsertFriend(new Friend { UserId = "SR-AAAAA-BBBBB", DisplayName = "Alex", State = FriendshipState.Friend });
            store.AddMessage(new ChatMessage { FriendId = "SR-AAAAA-BBBBB", Text = "first", SentAtUtc = DateTime.UtcNow.AddMinutes(-1) });
            store.AddMessage(new ChatMessage { FriendId = "SR-AAAAA-BBBBB", Text = "second :gg:", SentAtUtc = DateTime.UtcNow });

            Assert.Single(store.GetFriends());
            Assert.Equal(new[] { "first", "second :gg:" }, store.GetConversation("SR-AAAAA-BBBBB").Select(m => m.Text));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
