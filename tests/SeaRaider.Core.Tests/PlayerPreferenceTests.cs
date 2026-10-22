using SeaRaider.Core.Scraping;

namespace SeaRaider.Core.Tests;

public class PlayerPreferenceTests
{
    private static readonly Uri Page = new("https://www.example-movies.test/movie/alpha");

    [Fact]
    public void Typed_sites_are_tidied()
    {
        Assert.Equal(new[] { "embed.st", "player.example.test" },
            PlayerPreference.Normalize(new[] { "https://www.Embed.st/e/1, embed.st", " player.example.test/ ", "nonsense" }));
    }

    [Fact]
    public async Task Stream_1_is_picked_from_its_button()
    {
        const string html = """
            <html><body>
              <ul class="servers">
                <li data-src="https://embed.st/embed/alpha/1"><a href="#">Stream 1</a></li>
                <li data-src="https://embed.st/embed/alpha/2"><a href="#">Stream 2</a></li>
              </ul>
              <iframe src="https://other-player.test/e/alpha" allowfullscreen></iframe>
            </body></html>
            """;
        using var doc = await MediaTitleParser.OpenAsync(html, Page, default);
        var play = MediaTitleParser.FindPreferred(doc, html, Page, PlayerPreference.From(new[] { "embed.st" }, 1, true));
        Assert.Equal("https://embed.st/embed/alpha/1", MediaStreams.WithoutHint(play!));
        Assert.Equal(StreamFormat.Embed, MediaStreams.FormatOf(play!));

        var second = MediaTitleParser.FindPreferred(doc, html, Page, PlayerPreference.From(new[] { "embed.st" }, 2, true));
        Assert.Equal("https://embed.st/embed/alpha/2", MediaStreams.WithoutHint(second!));
    }

    [Fact]
    public async Task A_preferred_site_written_in_a_script_is_found()
    {
        const string html = """<html><body><button onclick="load(1)">Server 1</button><script>var servers = ["https:\/\/embed.st\/embed\/bravo\/1", "https://cdn.other.test/x"];</script></body></html>""";
        using var doc = await MediaTitleParser.OpenAsync(html, Page, default);
        var play = MediaTitleParser.FindPreferred(doc, html, Page, PlayerPreference.From(new[] { "embed.st" }, 1, true));
        Assert.Equal("https://embed.st/embed/bravo/1", MediaStreams.WithoutHint(play!));
    }

    [Fact]
    public async Task Nothing_preferred_on_the_page_means_null_so_the_button_gets_clicked()
    {
        const string html = """<html><body><button onclick="load(1)">Stream 1</button><img src="https://embed.st/logo.png"></body></html>""";
        using var doc = await MediaTitleParser.OpenAsync(html, Page, default);
        Assert.Null(MediaTitleParser.FindPreferred(doc, html, Page, PlayerPreference.From(new[] { "embed.st" }, 1, true)));
    }

    [Theory]
    [InlineData("Stream 1", 1)]
    [InlineData("Server #2", 2)]
    [InlineData("S3", 3)]
    public async Task Stream_buttons_are_recognised(string label, int number)
    {
        var html = $"""<html><body><a data-url="https://player.example.test/{number}">{label}</a></body></html>""";
        using var doc = await MediaTitleParser.OpenAsync(html, Page, default);
        Assert.Equal($"https://player.example.test/{number}", MediaTitleParser.StreamButtonUrl(doc, Page, number));
    }
}
