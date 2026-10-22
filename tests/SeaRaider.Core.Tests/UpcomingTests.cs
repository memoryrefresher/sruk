using Microsoft.Extensions.Logging.Abstractions;
using SeaRaider.Core.Catalog;
using SeaRaider.Core.Data;
using SeaRaider.Core.Models;
using SeaRaider.Core.Scraping;

namespace SeaRaider.Core.Tests;

public class UpcomingTests
{
    private static readonly Guid Source = Guid.NewGuid();

    [Fact]
    public async Task Steam_lists_give_titles_dates_and_steam_art_and_skip_demos()
    {
        const string html = """
            <div id="search_resultsRows">
              <a href="https://store.steampowered.com/app/123450/Hollow_Depths/" class="search_result_row" data-ds-appid="123450">
                <div class="search_name"><span class="title">Hollow Depths</span></div>
                <div class="search_released">Coming soon</div>
              </a>
              <a href="https://store.steampowered.com/app/678900/Star_Kite_Demo/" class="search_result_row" data-ds-appid="678900">
                <span class="title">Star Kite Demo</span><div class="search_released">Q2 2027</div>
              </a>
            </div>
            """;
        var games = await UpcomingListParser.ParseAsync(html, new Uri("https://store.steampowered.com/search/?filter=comingsoon"), Source, default);

        var game = Assert.Single(games); // "Star Kite Demo" is a demo: not coming soon
        Assert.Equal("Hollow Depths", game.Title);
        Assert.Equal("Coming soon", game.ReleaseText);
        Assert.Equal(123450, game.SteamAppId); // real art comes from Steam's API, not a guessed address
        Assert.Null(game.HeroUrl);
        Assert.Equal(Source, game.SourceId);
    }

    [Fact]
    public async Task Itch_listings_are_read()
    {
        const string html = """
            <div class="game_grid_widget">
              <div class="game_cell"><a class="game_thumb" href="https://dev.itch.io/moss-town" data-background_image="https://img.itch.zone/abc/315x250%23c/moss.png"></a>
                <div class="game_cell_data"><div class="game_title"><a class="title game_link" href="https://dev.itch.io/moss-town">Moss Town</a></div>
                <div class="game_text">A cosy village builder.</div><div class="game_genre">Simulation</div></div></div>
            </div>
            """;
        var games = await UpcomingListParser.ParseAsync(html, new Uri("https://itch.io/games/upcoming"), Source, default);

        var game = Assert.Single(games);
        Assert.Equal("Moss Town", game.Title);
        Assert.Equal("https://dev.itch.io/moss-town", game.PageUrl);
        Assert.StartsWith("https://img.itch.zone/", game.ImageUrl);
        Assert.Equal("A cosy village builder.", game.Description);
        Assert.Contains("Simulation", game.Tags);
    }

    [Fact]
    public async Task Feeds_are_read()
    {
        const string xml = """
            <?xml version="1.0"?>
            <rss version="2.0"><channel><title>Upcoming</title>
              <item><title>Lantern Keeper</title><link>https://example.com/lantern</link>
                <description><![CDATA[<img src="https://example.com/lantern.jpg"> Out March 3, 2027]]></description></item>
            </channel></rss>
            """;
        var games = await UpcomingListParser.ParseAsync(xml, new Uri("https://example.com/feed"), Source, default);

        var game = Assert.Single(games);
        Assert.Equal("Lantern Keeper", game.Title);
        Assert.Equal("https://example.com/lantern.jpg", game.ImageUrl);
        Assert.Equal("March 3, 2027", game.ReleaseText);
    }

    [Fact]
    public async Task Any_card_grid_is_read_and_furniture_is_skipped()
    {
        const string html = """
            <ul class="grid">
              <li class="card"><a href="/g/one"><img src="/i/one.jpg" alt="One"></a><h3>Glass Garden</h3><p>Coming soon</p></li>
              <li class="card"><a href="/g/two"><img data-src="/i/two.jpg"></a><h3>Paper Moons</h3><p>TBA</p></li>
              <li class="card"><a href="/g/three"><img src="/i/three.jpg"></a><h3>Rust &amp; Rain</h3></li>
              <li class="card"><a href="/login"><img src="/i/x.jpg"></a><h3>Log in</h3></li>
            </ul>
            """;
        var games = await UpcomingListParser.ParseAsync(html, new Uri("https://games.example.org/upcoming"), Source, default);

        Assert.Equal(new[] { "Glass Garden", "Paper Moons", "Rust & Rain" }, games.Select(g => g.Title));
        Assert.Equal("https://games.example.org/i/two.jpg", games[1].ImageUrl);
        Assert.Equal("To be announced", games[1].ReleaseText);
    }

    [Theory]
    [InlineData("Hollow Depths Demo", "Hollow Depths")]
    [InlineData("Hollow Depths - Demo", "Hollow Depths")]
    [InlineData("Hollow Depths (Free Demo)", "Hollow Depths")]
    [InlineData("Hollow Depths Playtest", "Hollow Depths")]
    [InlineData("Demolition Derby", "Demolition Derby")]
    public void Demo_suffix_is_ignored_when_matching(string title, string expected) =>
        Assert.Equal(expected, UpcomingService.LookupTitle(title));

    // ---- "Coming soon" never holds a game the store already has -------------------------------

    private static UpcomingRelease Upcoming(Guid source, string title) =>
        new() { Key = TitleNormalizer.DedupKey(title), SourceId = source, Title = title };

    private static GameMetadata InStore(string title) =>
        new() { Id = "game-" + title, SourceId = Guid.NewGuid(), PageUrl = "https://store.example/" + title.Replace(' ', '-'), Title = title };

    [Fact]
    public void Games_already_in_the_store_are_not_coming_soon_and_arrivals_leave_the_shelf()
    {
        var dir = Path.Combine(Path.GetTempPath(), "searaider-test-" + Guid.NewGuid().ToString("N"));
        using var repository = new LiteDbLibraryRepository(Path.Combine(dir, "library.db"));
        try
        {
            var site = new UpcomingSource { Url = "https://example.com/upcoming", Name = "example.com" };
            repository.UpsertUpcomingSource(site);
            repository.ReplaceUpcomingReleases(site.Id, new[]
            {
                Upcoming(site.Id, "Hollow Depths"),   // already in the store
                Upcoming(site.Id, "Moss Town"),       // already in the store
                Upcoming(site.Id, "Star Kite"),       // not yet
            });
            repository.UpsertGames([InStore("Hollow Depths"), InStore("Moss Town")]);

            var index = new CatalogIndexService(repository);
            index.EnsureLoadedAsync().GetAwaiter().GetResult();
            using var service = new UpcomingService(null!, repository, index, null!, null!, NullLogger<UpcomingService>.Instance);

            Assert.Equal(new[] { "Star Kite" }, service.GetReleases().Select(r => r.Title));

            // Star Kite reaches the store: the shelf is told to reload, and the game is gone from it.
            var reloads = 0;
            service.Changed += (_, _) => reloads++;
            repository.UpsertGames([InStore("Star Kite")]);
            service.CheckWishlist();

            Assert.Equal(1, reloads);
            Assert.Empty(service.GetReleases());

            service.CheckWishlist(); // nothing left to remove: no more reloads
            Assert.Equal(1, reloads);
        }
        finally
        {
            repository.Dispose();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Theory]
    [InlineData("Hollow Depths Demo", true)]
    [InlineData("Hollow Depths (Free Demo)", true)]
    [InlineData("Hollow Depths - Demo Version", true)]
    [InlineData("Demo: Hollow Depths", true)]
    [InlineData("Hollow Depths Playtest", true)]
    [InlineData("Hollow Depths", false)]
    [InlineData("Demolition Derby", false)]
    [InlineData("Demon's Souls", false)]
    public void Demos_are_recognised_by_whole_word(string title, bool expected) =>
        Assert.Equal(expected, TitleNormalizer.IsDemo(title));

    [Fact]
    public void Demos_already_on_the_shelf_are_hidden()
    {
        var dir = Path.Combine(Path.GetTempPath(), "searaider-test-" + Guid.NewGuid().ToString("N"));
        using var repository = new LiteDbLibraryRepository(Path.Combine(dir, "library.db"));
        try
        {
            var site = new UpcomingSource { Url = "https://example.com/upcoming", Name = "example.com" };
            repository.UpsertUpcomingSource(site);
            var steamDemo = Upcoming(site.Id, "Glass Garden");
            steamDemo.IsDemo = true; // Steam's own type says so, though the title doesn't
            repository.ReplaceUpcomingReleases(site.Id, new[]
            {
                Upcoming(site.Id, "Paper Moons Demo"),
                steamDemo,
                Upcoming(site.Id, "Star Kite"),
            });

            var index = new CatalogIndexService(repository);
            using var service = new UpcomingService(null!, repository, index, null!, null!, NullLogger<UpcomingService>.Instance);

            Assert.Equal(new[] { "Star Kite" }, service.GetReleases().Select(r => r.Title));
        }
        finally
        {
            repository.Dispose();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Wallpapers_must_name_the_game()
    {
        const string html = """
            <a class="iusc" m="{&quot;murl&quot;:&quot;https://img.example.com/valorant-4k.jpg&quot;,&quot;t&quot;:&quot;Valorant Agents 4K Wallpaper&quot;,&quot;purl&quot;:&quot;https://wall.example.com/valorant&quot;}"></a>
            <a class="iusc" m="{&quot;murl&quot;:&quot;https://img.example.com/hollow.jpg&quot;,&quot;t&quot;:&quot;Hollow Depths key art&quot;,&quot;purl&quot;:&quot;https://wall.example.com/hollow-depths&quot;}"></a>
            """;
        Assert.Equal(new[] { "https://img.example.com/hollow.jpg" }, WallpaperSearch.Pick(html, "Hollow Depths"));
        Assert.Empty(WallpaperSearch.Pick(html, "Ash")); // too short to tell apart
    }

    [Fact]
    public void Custom_list_reads_only_the_names_between_the_start_and_end_html()
    {
        const string html = """
            <html><body>
              <ul class="menu"><li>Home</li><li>About</li></ul>
              <div class="upcoming">
                <ul>
                  <li>1. Hollow Depths</li>
                  <li><a href="/x">Star Kite &amp; Friends</a></li>
                  <li>Hollow Depths</li>
                  <li>Moon Garden Demo</li>
                </ul>
              </div>
              <footer><p>Privacy</p></footer>
            </body></html>
            """;

        var games = CustomFeedParser.Parse(html, "<div class=\"upcoming\">", "</div>", Source);

        Assert.Equal(new[] { "Hollow Depths", "Star Kite & Friends" }, games.Select(g => g.Title).ToArray()); // duplicates and demos dropped
        Assert.All(games, g => Assert.True(g.FindOnSteam));
        Assert.All(games, g => Assert.Equal(Source, g.SourceId));
    }

    [Fact]
    public void Custom_list_finds_html_inside_a_feeds_escaped_content()
    {
        const string feed = "<rss><channel><item><content:encoded>&lt;ol&gt;&lt;li&gt;Hollow Depths&lt;/li&gt;&lt;li&gt;Star Kite&lt;/li&gt;&lt;/ol&gt;</content:encoded></item></channel></rss>";

        var games = CustomFeedParser.Parse(feed, "<ol>", "</ol>", Source);

        Assert.Equal(2, games.Count);
    }

    [Fact]
    public void A_feed_of_whole_posts_never_gives_the_posts_own_lines_as_games()
    {
        // Every item title is too long to be a name, so none qualifies; the post text must not be read in their place.
        const string feed = "<rss><channel><item><title>Hollow Depths - Digital Deluxe Edition, v1.2.3 + 12 DLCs/Bonuses (MULTi12) [Some Repack Group]</title>" +
                            "<description><![CDATA[<p>Company: Oxymoron Games</p><p>Click to show direct links</p>]]></description></item></channel></rss>";

        Assert.Throws<FeedMarkerException>(() => CustomFeedParser.Parse(feed, "<item>", "</channel>", Source));
    }

    [Fact]
    public void Post_lines_between_the_names_of_a_plain_list_are_skipped()
    {
        const string html = "<ul><li>Hollow Depths</li><li>Company: Oxymoron Games</li><li>Click to show direct links</li><li>Star Kite</li></ul>";

        var games = CustomFeedParser.Parse(html, "<ul>", "</ul>", Source);

        Assert.Equal(new[] { "Hollow Depths", "Star Kite" }, games.Select(g => g.Title).ToArray());
    }

    [Fact]
    public void Custom_list_says_which_marker_is_missing()
    {
        var noStart = Assert.Throws<FeedMarkerException>(() => CustomFeedParser.Parse("<ul><li>A Game</li></ul>", "<div id=\"nope\">", null, Source));
        Assert.Contains("start", noStart.Message, StringComparison.OrdinalIgnoreCase);

        var noEnd = Assert.Throws<FeedMarkerException>(() => CustomFeedParser.Parse("<ul><li>A Game</li></ul>", "<ul>", "</table>", Source));
        Assert.Contains("end", noEnd.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Custom_list_skips_continue_reading_dates_and_excerpts_on_a_blog_style_page()
    {
        const string html = """
            <div class="posts">
              <article>
                <h2 class="entry-title"><a href="/hollow-depths">Hollow Depths</a></h2>
                <span class="date">October 12, 2026</span> <span>by admin</span>
                <p>A moody cave crawler that finally has a release window, and it looks great.</p>
                <a class="more-link" href="/hollow-depths">Continue reading &raquo;</a>
              </article>
              <article>
                <h2 class="entry-title"><a href="/star-kite">Star Kite &amp; Friends</a></h2>
                <span class="date">October 9, 2026</span>
                <p>Posted in News</p>
                <a class="more-link" href="/star-kite">Continue reading</a>
              </article>
            </div>
            <footer>end</footer>
            """;

        var games = CustomFeedParser.Parse(html, "<div class=\"posts\">", "<footer>", Source);

        Assert.Equal(new[] { "Hollow Depths", "Star Kite & Friends" }, games.Select(g => g.Title).ToArray());
    }

    [Fact]
    public void Custom_list_takes_the_title_of_each_rss_item()
    {
        const string feed = """
            <rss><channel><title>Upcoming Games</title>
              <item><title><![CDATA[Hollow Depths]]></title><link>https://example.com/hollow-depths</link>
                <pubDate>Mon, 12 Oct 2026 14:03:00 +0000</pubDate><dc:creator>admin</dc:creator>
                <description><![CDATA[<p>An excerpt about the game.</p><p><a href="/x">Continue reading</a></p>]]></description></item>
              <item><title>Star Kite</title><link>https://example.com/star-kite</link>
                <pubDate>Fri, 09 Oct 2026 10:00:00 +0000</pubDate></item>
            </channel></rss>
            """;

        var games = CustomFeedParser.Parse(feed, "<item>", "</channel>", Source);

        Assert.Equal(new[] { "Hollow Depths", "Star Kite" }, games.Select(g => g.Title).ToArray());
    }

    [Theory]
    [InlineData("Continue reading")]
    [InlineData("Continue reading »")]
    [InlineData("Read more…")]
    [InlineData("3 Comments")]
    [InlineData("Leave a comment")]
    [InlineData("Posted in News")]
    [InlineData("October 12, 2026")]
    [InlineData("Mon, 12 Oct 2026 14:03:00 +0000")]
    [InlineData("2026-10-12")]
    [InlineData("3 days ago")]
    [InlineData("https://example.com/post")]
    [InlineData("The post Hollow Depths appeared first on Example.")]
    [InlineData("A moody cave crawler that finally has a release window, and it looks really great now.")]
    [InlineData("Click to show direct links")]
    [InlineData("Company: Oxymoron Games")]
    [InlineData("Genres/Tags: Action, Indie")]
    [InlineData("Repack Size: 4.2 GB")]
    [InlineData("Download Mirrors (Direct Links)")]
    public void Page_furniture_is_not_a_game_name(string text) => Assert.False(CustomFeedParser.LooksLikeGameName(text));

    [Theory]
    [InlineData("Hollow Depths")]
    [InlineData("Next Level")]
    [InlineData("Home")]
    [InlineData("Marble 2")]
    [InlineData("March")]
    [InlineData("Cyberpunk 2077")]
    [InlineData("Star Kite & Friends")]
    [InlineData("Tag: The Power of Paint")]
    [InlineData("Mirror's Edge")]
    [InlineData("Company of Heroes 3")]
    [InlineData("Fallout: New Vegas")]
    public void Real_game_names_pass(string text) => Assert.True(CustomFeedParser.LooksLikeGameName(text));

    // ---- "type a game that is on the page": SeaRaider finds the start and end HTML itself ------------

    private const string WordPressFeed = """
        <?xml version="1.0"?><rss version="2.0" xmlns:content="http://purl.org/rss/1.0/modules/content/" xmlns:dc="http://purl.org/dc/elements/1.1/"><channel>
        <title>Freeware Hub</title><link>https://example.com</link>
        <item><title>Upcoming Freeware Games - October 2026</title><link>https://example.com/upcoming-oct/</link>
          <dc:creator><![CDATA[admin]]></dc:creator><pubDate>Mon, 12 Oct 2026 14:03:00 +0000</pubDate><category><![CDATA[News]]></category>
          <guid isPermaLink="false">https://example.com/?p=4421</guid>
          <description><![CDATA[<p>Hollow Depths, Star Kite and more...</p><p>The post <a href="https://example.com/upcoming-oct/">Upcoming Freeware Games - October 2026</a> appeared first on <a href="https://example.com">Freeware Hub</a>.</p>]]></description>
          <content:encoded><![CDATA[<ul><li>Hollow Depths</li><li>Star Kite &amp; Friends</li><li>Moss Town</li><li>Glass Garden</li><li>Paper Moons</li></ul><p>The post <a href="https://example.com/upcoming-oct/">Upcoming Freeware Games - October 2026</a> appeared first on <a href="https://example.com">Freeware Hub</a>.</p>]]></content:encoded></item>
        <item><title>Older post about something</title><link>https://example.com/older/</link><pubDate>Mon, 05 Oct 2026 14:03:00 +0000</pubDate>
          <content:encoded><![CDATA[<p>Some other news.</p>]]></content:encoded></item>
        </channel></rss>
        """;

    [Fact]
    public void Finder_proposes_the_list_a_typed_game_is_in_and_the_proposal_reads_it_right()
    {
        var guesses = FeedRegionFinder.Find(WordPressFeed, "Moss Town");

        Assert.NotEmpty(guesses);
        var best = guesses[0];
        Assert.Equal(new[] { "Hollow Depths", "Star Kite & Friends", "Moss Town", "Glass Garden", "Paper Moons" }, best.Names);

        // What the user said yes to is exactly what the shelf will get.
        var games = CustomFeedParser.Parse(WordPressFeed, best.StartHtml, best.EndHtml, Source);
        Assert.Equal(best.Names, games.Select(g => g.Title).ToArray());
    }

    [Fact]
    public void Every_guess_reads_the_typed_game_and_reads_the_same_when_saved()
    {
        var guesses = FeedRegionFinder.Find(WordPressFeed, "Moss Town");

        Assert.All(guesses, g =>
        {
            Assert.Contains("Moss Town", g.Names);
            var saved = CustomFeedParser.Parse(WordPressFeed, g.StartHtml, g.EndHtml, Source);
            Assert.Equal(g.Names, saved.Select(r => r.Title).ToArray());
        });
        Assert.Equal(guesses.Count, guesses.Select(g => string.Join("|", g.Names)).Distinct().Count()); // no two guesses read the same games
    }

    [Fact]
    public void Finder_leaves_out_menus_and_prefers_the_tightest_list()
    {
        const string html = """
            <html><body><ul class="menu"><li>Home</li><li>About</li></ul>
            <div class="upcoming"><ul><li>Hollow Depths</li><li><a href="/x">Star Kite &amp; Friends</a></li><li>Moon Garden Demo</li><li>Glass Garden</li></ul></div>
            <footer><p>Privacy</p></footer></body></html>
            """;

        var guesses = FeedRegionFinder.Find(html, "Star Kite & Friends"); // typed the way a person would, "&" not "&amp;"

        Assert.Equal(new[] { "Hollow Depths", "Star Kite & Friends", "Glass Garden" }, guesses[0].Names);
    }

    [Fact]
    public void Finder_says_nothing_when_the_game_is_not_on_the_page()
    {
        Assert.Empty(FeedRegionFinder.Find(WordPressFeed, "Zzzz Not Here"));
        Assert.False(FeedRegionFinder.ContainsName(WordPressFeed, "Zzzz Not Here"));
        Assert.True(FeedRegionFinder.ContainsName(WordPressFeed, "star kite & friends"));
    }

    [Fact]
    public void An_empty_attribute_in_the_end_html_matches_any_address()
    {
        // The link address changes from post to post, so `href=""` stands for "whatever is there".
        var games = CustomFeedParser.Parse(WordPressFeed, "<ul>", "</ul><p>The post <a href=\"\">Upcoming", Source);

        Assert.Equal(5, games.Count);
    }

    [Fact]
    public void Text_left_over_from_a_start_marker_that_stops_inside_a_title_is_not_a_game()
    {
        const string feed = "<rss><channel><item><title>Upcoming Games - Example Site</title><link>https://example.com/p</link>" +
                            "<content:encoded><![CDATA[<ul><li>Hollow Depths</li><li>Star Kite</li></ul>]]></content:encoded></item></channel></rss>";

        var names = CustomFeedParser.PreviewNames(feed, "<title>Upcoming", "</channel>");

        Assert.Equal(new[] { "Hollow Depths", "Star Kite" }, names);
    }

    // ---- arrows, duplicates, and games the store already has ------------------------------------

    [Theory]
    [InlineData("→ Alice", "Alice")]
    [InlineData("Alice ›", "Alice")]
    [InlineData("➜ Star Kite »", "Star Kite")]
    [InlineData("• Hades!", "Hades!")]
    [InlineData("★ Moss Town ★", "Moss Town")]
    [InlineData("🔥 Hot Game", "Hot Game")]
    [InlineData("Game™", "Game")]
    [InlineData("#Hashtag", "#Hashtag")]
    [InlineData("[Redacted]", "[Redacted]")]
    [InlineData("+Ultra", "+Ultra")]
    [InlineData("Rust & Rain", "Rust & Rain")]
    public void Arrows_and_other_decoration_are_removed_from_a_title(string raw, string expected) =>
        Assert.Equal(expected, CustomFeedParser.TidyTitle(raw));

    [Fact]
    public void Custom_list_titles_come_without_arrows_and_without_duplicates()
    {
        const string html = "<ul><li>→ Alice</li><li>Star Kite »</li><li>&rarr; Alice</li><li>Alice</li><li>&#9733; Moss Town</li></ul>";

        var games = CustomFeedParser.Parse(html, "<ul>", "</ul>", Source);

        Assert.Equal(new[] { "Alice", "Star Kite", "Moss Town" }, games.Select(g => g.Title).ToArray());
    }

    [Fact]
    public void A_game_is_on_the_shelf_once_and_never_when_the_store_has_it_however_it_is_spaced()
    {
        var dir = Path.Combine(Path.GetTempPath(), "searaider-test-" + Guid.NewGuid().ToString("N"));
        using var repository = new LiteDbLibraryRepository(Path.Combine(dir, "library.db"));
        try
        {
            var first = new UpcomingSource { Url = "https://example.com/a", Name = "a" };
            var second = new UpcomingSource { Url = "https://example.com/b", Name = "b" };
            repository.UpsertUpcomingSource(first);
            repository.UpsertUpcomingSource(second);
            repository.ReplaceUpcomingReleases(first.Id, new[] { Upcoming(first.Id, "Moon Garden"), Upcoming(first.Id, "Star Kite") });
            repository.ReplaceUpcomingReleases(second.Id, new[] { Upcoming(second.Id, "Moongarden") });
            repository.UpsertGames([InStore("StarKite")]); // the store spells it without the space

            var index = new CatalogIndexService(repository);
            index.EnsureLoadedAsync().GetAwaiter().GetResult();
            using var service = new UpcomingService(null!, repository, index, null!, null!, NullLogger<UpcomingService>.Instance);

            Assert.Equal(new[] { "Moon Garden" }, service.GetReleases().Select(r => r.Title)); // one Moon Garden, no Star Kite
        }
        finally
        {
            repository.Dispose();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
