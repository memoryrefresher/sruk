using SeaRaider.Core.Models;
using SeaRaider.Core.Scraping;

namespace SeaRaider.Core.Tests;

/// <summary>A browser game's own website as a source: one store entry, played in SeaRaider's game window.</summary>
public class WebGameSiteTests
{
    [Fact]
    public async Task The_site_becomes_one_playable_game()
    {
        const string html = """
            <html><head>
              <title>Astra Strike: free CS-style shooter in your browser</title>
              <meta property="og:title" content="Astra Strike: free CS-style shooter in your browser" />
              <meta property="og:description" content="Play a free CS-style shooter. No download, on PC or phone." />
              <meta property="og:image" content="/og-card.jpg" />
            </head><body><canvas></canvas></body></html>
            """;
        var page = new Uri("https://astrastrike.fun/");
        var source = new GameSource { Url = page.AbsoluteUri, IsWebGame = true };
        using var doc = await GamePageParser.ParseHtmlAsync(html, page, CancellationToken.None);

        var game = WebGameSite.Build(doc, page, source);

        Assert.Equal("Astra Strike", game.Title);
        Assert.Equal("https://astrastrike.fun/", game.WebGameUrl);
        Assert.Equal("https://astrastrike.fun/og-card.jpg", game.CoverImageUrl);
        Assert.StartsWith("Free CS-style shooter in your browser.", game.Description);
        Assert.True(game.DetailsLoaded);
        Assert.Empty(game.DownloadLinks);
    }

    [Fact]
    public async Task A_page_that_says_it_plays_in_the_browser_is_recognised()
    {
        var page = new Uri("https://astrastrike.fun/");
        using var game = await GamePageParser.ParseHtmlAsync(
            "<html><body><a>PLAY FREE · NO DOWNLOAD</a></body></html>", page, CancellationToken.None);
        using var shop = await GamePageParser.ParseHtmlAsync(
            "<html><body><a href='/files/game.zip'>Download</a></body></html>", page, CancellationToken.None);

        Assert.True(WebGameSite.LooksLikeBrowserGame(game));
        Assert.False(WebGameSite.LooksLikeBrowserGame(shop));
    }
}
