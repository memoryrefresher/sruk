using SeaRaider.Core.Models;
using SeaRaider.Core.Scraping;

namespace SeaRaider.Core.Tests;

/// <summary>itch.io browser (HTML5) games: found on their page, and a single game page works as a source.</summary>
public class ItchWebGameTests
{
    private static readonly Uri Page = new("https://developmentlp.itch.io/astra-strike");

    private const string GamePage = """
        <html><head>
          <meta property="og:title" content="Astra Strike by DevelopmentLP" />
          <meta property="og:image" content="https://img.itch.zone/cover.png" />
          <meta property="og:description" content="Clear the asteroids from every planet." />
        </head><body>
          <div class="view_game_page">
            <h1 class="game_title">Astra Strike</h1>
            <div class="iframe_placeholder" data-iframe="&lt;iframe frameborder=&quot;0&quot; src=&quot;https://html.itch.zone/html/8421337/index.html&quot; allowfullscreen&gt;&lt;/iframe&gt;">
              <button class="load_iframe_btn">Run game</button>
            </div>
            <div class="game_info_panel_widget"><table><tr><td>Author</td><td><a>DevelopmentLP</a></td></tr></table></div>
          </div>
        </body></html>
        """;

    [Fact]
    public async Task The_html5_build_is_found_behind_run_game()
    {
        using var doc = await GamePageParser.ParseHtmlAsync(GamePage, Page, CancellationToken.None);
        Assert.Equal("https://html.itch.zone/html/8421337/index.html", ItchIo.WebGameUrlOf(doc));

        var game = new GameMetadata { PageUrl = Page.AbsoluteUri, Title = "Astra Strike" };
        ItchIo.EnrichFromGamePage(doc, Page, game);
        Assert.Equal("https://html.itch.zone/html/8421337/index.html", game.WebGameUrl);
    }

    [Fact]
    public async Task A_single_game_page_is_a_source_with_that_game()
    {
        using var doc = await GamePageParser.ParseHtmlAsync(GamePage, Page, CancellationToken.None);
        var source = new GameSource { Url = Page.AbsoluteUri };

        var (games, hasMore) = ItchIo.ParseListing(doc, Page, source);

        var game = Assert.Single(games);
        Assert.Equal("Astra Strike", game.Title);
        Assert.Equal("https://img.itch.zone/cover.png", game.CoverImageUrl);
        Assert.False(hasMore);
        Assert.Equal(Page, ItchIo.BuildUrl(source, "astra", 1));
    }

    [Fact]
    public async Task A_downloadable_game_has_no_web_build()
    {
        using var doc = await GamePageParser.ParseHtmlAsync("<html><body><h1 class='game_title'>X</h1></body></html>", Page, CancellationToken.None);
        Assert.Null(ItchIo.WebGameUrlOf(doc));
    }
}
