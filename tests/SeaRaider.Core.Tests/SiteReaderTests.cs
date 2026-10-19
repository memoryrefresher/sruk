using SeaRaider.Core.Models;
using SeaRaider.Core.Scraping;

namespace SeaRaider.Core.Tests;

public class SiteReaderTests
{
    private static readonly SiteReader Reader = new()
    {
        Name = "Example Games",
        Domains = ["example.com"],
        BrowseUrl = "https://example.com/games?page={page}",
        SearchUrl = "https://example.com/search?q={query}&page={page}",
        Listing = new ListingRecipe { Item = ".game-card", Link = "a.title", Title = "a.title", Cover = "img", NextPage = "a.next" },
        Page = new PageRecipe
        {
            Title = "h1.game-title || h1",
            Description = ".description",
            Remove = ".share",
            Cover = "meta[property='og:image']@content",
            Screenshots = ".shots a",
            Tags = ".tags a",
            Developer = ".dev",
            Size = ".size",
            Downloads = ".downloads a",
        },
    };

    [Fact]
    public void Readers_cover_their_domains_and_sub_domains()
    {
        Assert.True(Reader.Covers("example.com"));
        Assert.True(Reader.Covers("www.example.com"));
        Assert.False(Reader.Covers("notexample.com"));
        Assert.Equal("https://example.com/search?q=hollow%20knight&page=2", SiteReaderParser.BuildUrl(Reader, "hollow knight", 2)!.AbsoluteUri);
        Assert.Equal("https://example.com/games?page=1", SiteReaderParser.BuildUrl(Reader, null, 1)!.AbsoluteUri);
    }

    [Fact]
    public async Task A_list_page_becomes_store_cards()
    {
        const string html = """
            <div class="game-card"><a class="title" href="/game/one">Gadget Tycoon</a><img data-src="/img/one.jpg"></div>
            <div class="game-card"><a class="title" href="/game/two">Sky Pirates</a><img src="/img/two.jpg"></div>
            <a class="next" href="?page=2">Next</a>
            """;
        var uri = new Uri("https://example.com/games?page=1");
        using var doc = await GamePageParser.ParseHtmlAsync(html, uri, CancellationToken.None);
        var (games, more) = SiteReaderParser.ParseListing(Reader, doc, uri, new GameSource { Url = "https://example.com/" });

        Assert.True(more);
        Assert.Equal(new[] { "Gadget Tycoon", "Sky Pirates" }, games.Select(g => g.Title));
        Assert.Equal("https://example.com/game/one", games[0].PageUrl);
        Assert.Equal("https://example.com/img/one.jpg", games[0].CoverImageUrl);
        Assert.False(games[0].DetailsLoaded);
    }

    [Fact]
    public async Task A_game_page_fills_the_store_page()
    {
        const string html = """
            <html><head><meta property="og:image" content="https://cdn.example.com/cover.png"></head><body>
            <h1 class="game-title">Gadget Tycoon</h1>
            <div class="description"><p>Run a consumer-electronics empire.</p><div class="share">Share this!</div><p>Start tiny.</p></div>
            <div class="shots"><a href="/s/1.jpg"></a><a href="/s/2.jpg"></a></div>
            <div class="tags"><a>Indie</a><a>Simulation</a></div>
            <span class="dev">Mike</span><span class="size">1.2 GB</span>
            <div class="downloads"><a href="https://pixeldrain.com/u/abc123">Pixeldrain</a><a href="magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567">Magnet</a></div>
            </body></html>
            """;
        var uri = new Uri("https://example.com/game/one");
        using var doc = await GamePageParser.ParseHtmlAsync(html, uri, CancellationToken.None);
        var game = new GameMetadata { PageUrl = uri.AbsoluteUri, DetailsLoaded = false };
        var notes = SiteReaderParser.EnrichFromGamePage(Reader, doc, uri, game);

        Assert.Empty(notes);
        Assert.Equal("Gadget Tycoon", game.Title);
        Assert.Contains("consumer-electronics", game.Description);
        Assert.DoesNotContain("Share this", game.Description);
        Assert.Equal("https://cdn.example.com/cover.png", game.CoverImageUrl);
        Assert.Equal(2, game.ScreenshotUrls.Count);
        Assert.Equal(new[] { "Indie", "Simulation" }, game.Tags);
        Assert.Equal("Mike", game.Developer);
        Assert.Equal(2, game.DownloadLinks.Count);
        Assert.Contains(game.DownloadLinks, l => l.Kind == DownloadLinkKind.Magnet);
        Assert.True(game.DetailsLoaded);
        Assert.True(game.MediaChecked);
    }

    [Fact]
    public void Records_with_missing_pieces_are_filled_in()
    {
        var game = new GameMetadata
        {
            Title = null!,
            Tags = null!,
            DownloadLinks = [null!, new DownloadLink { Url = " " }, new DownloadLink { Url = "https://pixeldrain.com/u/abc", Host = null!, FileName = null! }],
        }.Normalize();

        Assert.Equal(string.Empty, game.Title);
        Assert.Empty(game.Tags);
        var link = Assert.Single(game.DownloadLinks);
        Assert.Equal("pixeldrain.com", link.Host);
        Assert.Equal(string.Empty, link.FileName);
    }

    [Fact]
    public async Task A_title_with_an_info_line_inside_reads_only_its_own_words()
    {
        const string html = """
            <article class="card"><a href="/error67/">
              <img class="thumbnail-image ls-is-cached lazyloaded" src="wp-content/uploads/2026/10/Error67_thumbnail.jpg" alt="Error67 Free Demo">
              <h2 class="the-post-title"><span class="game-meta-line">2026 | 1.2 GB</span>Error67</h2>
            </a></article>
            """;
        var reader = new SiteReader
        {
            Domains = ["example.com"],
            Listing = new ListingRecipe { Item = "article.card", Title = "h2.the-post-title@owntext", Cover = "img.thumbnail-image", Size = ".game-meta-line" },
        };
        var uri = new Uri("https://example.com/");
        using var doc = await GamePageParser.ParseHtmlAsync(html, uri, CancellationToken.None);
        var game = Assert.Single(SiteReaderParser.ParseListing(reader, doc, uri, new GameSource { Url = "https://example.com/" }).Games);

        Assert.Equal("Error67", game.Title);
        Assert.Equal("1.2 GB", game.SizeText);
        Assert.Equal("https://example.com/wp-content/uploads/2026/10/Error67_thumbnail.jpg", game.CoverImageUrl);
        Assert.Equal("https://example.com/error67/", game.PageUrl);
    }
}
