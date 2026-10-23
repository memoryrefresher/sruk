using SeaRaider.Core.Scraping;

namespace SeaRaider.Core.Tests;

public class MediaSectionsTests
{
    private static readonly Uri Home = new("https://www.example-sports.test/");

    [Fact]
    public async Task A_sites_sections_are_found_kinds_first()
    {
        const string html = """
            <html><body>
              <nav>
                <a href="/">Home</a>
                <a href="/genre/action">Action</a>
                <a href="/category/football">Football</a>
                <a href="/category/basketball">Basketball</a>
                <a href="/tv-shows">tv shows</a>
                <a href="/dmca">DMCA</a>
                <a href="/login">Login</a>
              </nav>
              <div class="grid"><a href="/watch/x"><img src="/x.jpg" alt="X vs Y"></a></div>
              <footer><a href="/category/football">Football</a></footer>
            </body></html>
            """;
        using var doc = await MediaTitleParser.OpenAsync(html, Home, default);
        var sections = MediaTitleParser.FindSections(doc, Home);

        Assert.Equal(new[] { "Football", "Basketball", "TV Shows", "Action" }, sections.Select(s => s.Label));
        Assert.Equal("https://www.example-sports.test/category/football", sections[0].Url.AbsoluteUri);
    }

    private static string SportCard(string slug, string name, string sport) =>
        $"""<a class="card" href="/watch/{slug}"><img src="/api/poster/{slug}.webp" alt="Match Poster"><span class="badge">LIVE</span><span>123</span>""" +
        $"""<h3 class="title">{name}</h3><p><span>{sport}</span> <span>| 7:00 PM</span></p></a>""";

    [Fact]
    public async Task Sport_links_with_icons_are_sections_and_cards_keep_their_sport()
    {
        var html = "<html><body>" +
                   """<div class="sports"><a href="/category/basketball"><img src="/icons/basketball.svg" alt="Basketball">Basketball</a>""" +
                   """<a href="/category/fight"><img src="/icons/fight.svg">Fight (UFC, Boxing)</a></div><div class="grid">""" +
                   SportCard("a-vs-b-1", "Alpha vs Bravo", "Football") + SportCard("c-vs-d-2", "Charlie vs Delta", "Football") +
                   SportCard("e-vs-f-3", "Echo vs Foxtrot", "Basketball") + SportCard("g-vs-h-4", "Golf vs Hotel", "Hockey") +
                   "</div></body></html>";
        using var doc = await MediaTitleParser.OpenAsync(html, Home, default);

        Assert.Equal(new[] { "Basketball", "Fight" }, MediaTitleParser.FindSections(doc, Home).Select(s => s.Label));

        var titles = MediaTitleParser.ParseTitles(doc, Home, Guid.NewGuid());
        Assert.Equal(new[] { "Alpha vs Bravo", "Charlie vs Delta", "Echo vs Foxtrot", "Golf vs Hotel" }, titles.Select(t => t.Title));
        Assert.Equal(new[] { "Football", "Football", "Basketball", "Hockey" }, titles.Select(t => t.Category));
        Assert.All(titles, t => Assert.True(t.IsLive));
    }
}
