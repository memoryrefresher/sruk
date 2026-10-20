using SeaRaider.Core.Models;
using SeaRaider.Core.Scraping;

namespace SeaRaider.Core.Tests;

/// <summary>Teaching a reader from a pasted page: the learned selectors must read the page they were learned from.</summary>
public class SiteReaderTeacherTests
{
    private static readonly Uri Page = new("https://games.example.com/cool-game/");

    private const string GamePage = """
        <html><head><title>Cool Game Free Download</title>
        <meta property="og:image" content="https://img.example.com/cover.jpg">
        <meta property="og:site_name" content="Example Games">
        <link rel="canonical" href="https://games.example.com/cool-game/"></head>
        <body>
        <nav class="main-nav"><a href="/">Home</a> <a href="/downloads">Download</a></nav>
        <article class="single-post">
          <h2 class="the-post-title">Cool Game <span class="info">v1.2 | 5 GB</span></h2>
          <div class="plus"><ul>
            <li><strong>Genre:</strong> <a href="/g/action">Action</a></li>
            <li><strong>Developer:</strong> Indie Studio</li>
            <li><strong>Version:</strong> 1.2.0</li>
            <li><strong>Size:</strong> 5 GB</li>
          </ul></div>
          <div class="entry-content">
            <p>Cool Game is a tense survival adventure set on a drifting research vessel, where every choice changes who lives.</p>
            <p>Explore the decks, repair the engines and keep the crew calm while the weather turns against you for the whole night.</p>
            <p>Three difficulty modes, a branching story with seven endings, and a soundtrack recorded live at sea by a small orchestra.</p>
            <img src="https://img.example.com/s1.jpg"><img src="https://img.example.com/s2.jpg">
            <div class="sharedaddy">Share this on your favourite network today</div>
            <a class="shortc-button" href="https://pixeldrain.com/u/abcdef1234">Pixeldrain</a>
            <a class="shortc-button" href="https://pixeldrain.com/u/zyxwvu9876">Pixeldrain mirror</a>
            <a class="shortc-button" href="https://pixeldrain.com/l/qwerty5555">Part list</a>
          </div>
        </article>
        <footer><a href="/about">About</a></footer>
        </body></html>
        """;

    private static string Card(int i) => $"""
        <div class="post-card"><a href="/games/game-{i}/"><img class="thumbnail-image" src="/img/{i}.jpg"></a>
        <h3 class="card-title"><a href="/games/game-{i}/">Game Number {i}</a></h3></div>
        """;

    private static readonly string ListPage = $"""
        <html><head><title>All games</title></head><body>
        <nav class="main-nav"><a href="/">Home</a></nav>
        <main><div class="games-grid">{string.Concat(Enumerable.Range(1, 6).Select(Card))}</div>
        <div class="pagination"><a class="next page-numbers" href="https://games.example.com/page/2/">Next</a></div></main>
        <form action="/" method="get"><input type="search" name="s"></form>
        </body></html>
        """;

    [Fact]
    public async Task Game_page_selectors_read_the_page_they_came_from()
    {
        var lesson = await SiteReaderTeacher.LearnAsync(GamePage, null);
        Assert.True(lesson.FoundPage);
        Assert.Equal("games.example.com", lesson.Reader.Domains[0]);
        Assert.Equal("Example Games", lesson.Reader.Name);

        var page = lesson.Reader.Page!;
        using var doc = await GamePageParser.ParseHtmlAsync(GamePage, Page, CancellationToken.None);

        Assert.Equal("Cool Game", SiteReaderParser.Text(doc, page.Title));
        Assert.Equal("Indie Studio", SiteReaderParser.Text(doc, page.Developer));
        Assert.Equal("1.2.0", SiteReaderParser.Text(doc, page.Version));
        Assert.Contains("5 GB", SiteReaderParser.Text(doc, page.Size));
        Assert.Contains("Action", SiteReaderParser.Texts(doc, page.Genres));
        Assert.Equal("https://img.example.com/cover.jpg", SiteReaderParser.Url(doc, page.Cover, Page));
        Assert.Equal(2, SiteReaderParser.Urls(doc, page.Screenshots, Page).Count);

        // Only the three file links, not the menu's "Download".
        Assert.Contains("shortc-button", page.Downloads);
        Assert.Equal(3, SiteReaderParser.Urls(doc, page.Downloads, Page).Count);

        // The share box is left out of the description.
        Assert.Contains("sharedaddy", page.Remove);
    }

    [Fact]
    public async Task Click_here_links_next_to_a_hosts_name_are_downloads()
    {
        var html = GamePage
            .Replace("""<a class="shortc-button" href="https://pixeldrain.com/u/abcdef1234">Pixeldrain</a>""",
                     """<p><strong>BuzzHeavier</strong> – <a href="https://games.example.com/go/aaa111">Click Here</a> – or – <a href="https://games.example.com/go/bbb222">Click here</a></p>""")
            .Replace("""<a class="shortc-button" href="https://pixeldrain.com/u/zyxwvu9876">Pixeldrain mirror</a>""",
                     """<p><strong>Pixeldrain</strong> – <a href="https://games.example.com/go/ccc333">Click Here</a></p>""")
            .Replace("""<a class="shortc-button" href="https://pixeldrain.com/l/qwerty5555">Part list</a>""",
                     """<p><strong>GoFile</strong> – <a href="https://games.example.com/go/ddd444">Click Here</a></p>""");

        var lesson = await SiteReaderTeacher.LearnAsync(html, null);
        Assert.NotNull(lesson.Reader.Page!.Downloads);

        using var doc = await GamePageParser.ParseHtmlAsync(html, Page, CancellationToken.None);
        var links = SiteReaderParser.Urls(doc, lesson.Reader.Page.Downloads, Page);
        Assert.Equal(4, links.Count);
        Assert.All(links, l => Assert.Contains("/go/", l));
    }

    [Fact]
    public async Task List_page_learns_the_cards_and_the_next_page()
    {
        var lesson = await SiteReaderTeacher.LearnAsync(ListPage, "games.example.com");
        Assert.True(lesson.FoundListing);
        Assert.False(lesson.FoundPage);

        var reader = lesson.Reader;
        using var doc = await GamePageParser.ParseHtmlAsync(ListPage, new Uri("https://games.example.com/"), CancellationToken.None);
        var (games, hasMore) = SiteReaderParser.ParseListing(reader, doc, new Uri("https://games.example.com/"), new GameSource { Url = "https://games.example.com/" });

        Assert.Equal(6, games.Count);
        Assert.Equal("Game Number 1", games[0].Title);
        Assert.True(hasMore);
        Assert.Contains(lesson.Notes, n => n.Contains("/page/{page}", StringComparison.Ordinal));
        Assert.Contains(lesson.Notes, n => n.Contains("{query}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_html_is_explained()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => SiteReaderTeacher.LearnAsync("just some text the user copied from the page", "example.com"));
        Assert.Contains("Ctrl+U", ex.Message);
    }

    [Fact]
    public async Task A_site_that_cant_be_told_asks_for_the_address()
    {
        var html = GamePage.Replace("<link rel=\"canonical\" href=\"https://games.example.com/cool-game/\">", string.Empty);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => SiteReaderTeacher.LearnAsync(html, null));
        Assert.Contains("domain", ex.Message);
    }

    [Fact]
    public void Learned_selectors_replace_the_same_fields_and_keep_the_rest()
    {
        var existing = new SiteReader
        {
            Name = "Mine",
            Domains = ["games.example.com"],
            BrowseUrl = "https://games.example.com/?page={page}",
            Page = new PageRecipe { Title = "h1", Trailers = ".trailer iframe" },
        };
        var learned = new SiteReader { Domains = ["games.example.com"], Page = new PageRecipe { Title = "h2.the-post-title@owntext" } };

        var merged = SiteReaderTeacher.Merge(existing, learned);

        Assert.Equal("Mine", merged.Name);
        Assert.Equal("https://games.example.com/?page={page}", merged.BrowseUrl);
        Assert.Equal("h2.the-post-title@owntext", merged.Page!.Title);
        Assert.Equal(".trailer iframe", merged.Page.Trailers);
    }
}
