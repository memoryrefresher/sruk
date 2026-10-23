using SeaRaider.Core.Scraping;

namespace SeaRaider.Core.Tests;

public class MediaBrowseTests
{
    private static readonly Guid Source = Guid.NewGuid();
    private static readonly Uri Home = new("https://www.example-movies.test/");

    private static string Card(string slug, string name) =>
        $"""<div class="card post-{slug.Length}"><a class="poster" href="/movie/{slug}"><img data-src="/img/{slug}.jpg" src="data:image/gif;base64,R0lGOD" alt="{name}"></a>""" +
        $"""<h3 class="name"><a href="/movie/{slug}">Watch {name} (2024) Full Movie Online Free</a></h3><a href="/movie/{slug}">Watch now</a></div>""";

    private static string ListPage(params (string Slug, string Name)[] titles) =>
        "<html><head><title>Example Movies</title></head><body>" +
        """<header><nav><a href="/movies"><img src="/img/menu-movies.jpg">Movies</a><a href="/genre/action">Action</a></nav></header>""" +
        """<div class="grid">""" + string.Concat(titles.Select(t => Card(t.Slug, t.Name))) + "</div>" +
        """<div class="pagination"><a href="/page/1">1</a><a class="next page-numbers" href="/page/2">Next</a></div>""" +
        """<footer><a href="/dmca">DMCA</a><a href="/contact">Contact</a></footer></body></html>""";

    private static readonly (string, string)[] Six =
    [
        ("alpha", "Alpha"), ("bravo", "Bravo"), ("charlie", "Charlie"), ("delta", "Delta"), ("echo", "Echo"), ("foxtrot", "Foxtrot"),
    ];

    [Fact]
    public async Task A_grid_of_posters_becomes_a_list_of_titles()
    {
        var html = ListPage(Six);
        using var doc = await MediaTitleParser.OpenAsync(html, Home, default);
        var titles = MediaTitleParser.ParseTitles(doc, Home, Source);

        Assert.Equal(6, titles.Count);
        Assert.All(titles, t => Assert.True(t.NeedsResolve));
        Assert.Equal("Alpha (2024)", titles[0].Title); // the card's name link, tidied
        Assert.Equal("https://www.example-movies.test/movie/alpha", titles[0].PlayUrl);
        Assert.Equal("https://www.example-movies.test/img/alpha.jpg", titles[0].ThumbnailUrl); // lazy image, not the placeholder
        Assert.DoesNotContain(titles, t => t.PlayUrl.Contains("/genre/") || t.PlayUrl.Contains("/dmca") || t.PlayUrl.EndsWith("/movies"));
        Assert.Equal(Enumerable.Range(0, 6), titles.Select(t => t.Order));
    }

    [Fact]
    public async Task A_page_with_a_few_links_is_not_a_list()
    {
        const string html = """<html><body><a href="/about"><img src="/a.jpg" alt="About us"></a><a href="/movie/x"><img src="/x.jpg" alt="X Movie"></a></body></html>""";
        using var doc = await MediaTitleParser.OpenAsync(html, Home, default);
        Assert.Empty(MediaTitleParser.ParseTitles(doc, Home, Source));
    }

    [Fact]
    public async Task The_next_page_of_the_list_is_found()
    {
        using var doc = await MediaTitleParser.OpenAsync(ListPage(Six), Home, default);
        Assert.Equal("https://www.example-movies.test/page/2", MediaTitleParser.FindNextPage(doc, Home)?.AbsoluteUri);
    }

    [Fact]
    public async Task A_title_page_plays_its_player_frame_not_its_trailer_or_an_ad()
    {
        var page = new Uri("https://www.example-movies.test/movie/alpha");
        const string html = """
            <html><body>
              <iframe src="https://www.youtube.com/embed/abcdefghijk"></iframe>
              <iframe src="https://ads.example-adnet.test/banner" width="300"></iframe>
              <div id="player-wrap"><iframe src="https://embed.example-host.test/e/xyz123" allowfullscreen></iframe></div>
            </body></html>
            """;
        using var doc = await MediaTitleParser.OpenAsync(html, page, default);
        var play = MediaTitleParser.FindPlayer(doc, html, page);
        Assert.Equal(StreamFormat.Embed, MediaStreams.FormatOf(play));
        Assert.Equal("https://embed.example-host.test/e/xyz123", MediaStreams.WithoutHint(play));
    }

    [Fact]
    public async Task A_title_page_with_a_stream_plays_the_stream()
    {
        var page = new Uri("https://www.example-movies.test/movie/bravo");
        const string html = """<html><body><script>var src = "https:\/\/cdn.example-host.test\/hls\/master.m3u8?t=1";</script></body></html>""";
        using var doc = await MediaTitleParser.OpenAsync(html, page, default);
        Assert.Equal("https://cdn.example-host.test/hls/master.m3u8?t=1", MediaTitleParser.FindPlayer(doc, html, page));
    }

    [Fact]
    public async Task A_title_page_without_a_player_is_shown_itself()
    {
        var page = new Uri("https://www.example-movies.test/movie/charlie");
        const string html = "<html><body><p>Nothing here</p></body></html>";
        using var doc = await MediaTitleParser.OpenAsync(html, page, default);
        var play = MediaTitleParser.FindPlayer(doc, html, page);
        Assert.Equal(StreamFormat.Embed, MediaStreams.FormatOf(play));
        Assert.Equal(page.AbsoluteUri, MediaStreams.WithoutHint(play));
    }

    [Theory]
    [InlineData("Watch Alpha Full Movie Online Free", "Alpha")]
    [InlineData("Bravo (2023) - Watch Online", "Bravo (2023)")]
    [InlineData("Watch now", null)]
    [InlineData("HD", null)]
    [InlineData("12", null)]
    public void Names_are_tidied_and_labels_dropped(string text, string? expected) =>
        Assert.Equal(expected, MediaTitleParser.Tidy(text));

    [Theory]
    [InlineData("https://cdn.popads.net/pop.js", true)]
    [InlineData("https://www.googlesyndication.com/x", true)]
    [InlineData("https://ads.example.test/banner", true)]
    [InlineData("https://my-popunder-network.test/go", true)]
    [InlineData("https://www.youtube.com/embed/abcdefghijk", false)]
    [InlineData("https://embed.example-host.test/e/xyz", false)]
    [InlineData("https://downloads.example.test/file", false)]
    public void Ad_networks_are_recognised(string url, bool isAd) => Assert.Equal(isAd, AdHosts.IsAd(url));
}
