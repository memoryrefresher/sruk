using SeaRaider.Core.Scraping;

namespace SeaRaider.Core.Tests;

public class SiteReaderPointerTests
{
    private const string GamePage = """
        <html><head><title>Star Kite – Example Games</title></head><body>
          <div class="wrap"><h1 class="post-title">Star Kite</h1>
          <div class="entry">
            <p>Star Kite is a game about flying a kite between the stars. It has forty levels, a calm soundtrack and no timers at all.</p>
            <p>Fly through nebulas, collect light and find the lost kites of your friends. A short and gentle adventure for everyone.</p>
            <ul class="facts"><li><span class="k">Studio</span> <span class="v">Paper Moon</span></li><li>Weight: 1.2 GB</li></ul>
            <div class="dl-area"><button class="btn-blue" onclick="window.location.href='https://example.com/get/star-kite'">Download</button></div>
            <form action="https://example.com/fetch/42" method="get"><input type="submit" class="go" value="Mirror"></form>
          </div></div>
        </body></html>
        """;

    private static Task<SiteReaderLesson> Learn(LearnHints hints) =>
        SiteReaderTeacher.LearnAsync(GamePage, "https://example.com/star-kite/", hints);

    [Fact]
    public async Task A_download_button_found_by_its_words_reads_its_onclick_address()
    {
        var lesson = await Learn(new LearnHints { DownloadExamples = ["Download"] });
        var downloads = lesson.Fields.Single(f => f.Field == "downloads");
        Assert.Contains("example.com/get/star-kite", downloads.Sample);
    }

    [Fact]
    public async Task A_download_button_found_by_its_code_from_view_source()
    {
        var lesson = await Learn(new LearnHints { DownloadSnippet = """<input type="submit" class="go" value="Mirror">""" });
        var downloads = lesson.Fields.Single(f => f.Field == "downloads");
        Assert.Contains("example.com/fetch/42", downloads.Sample);
    }

    [Fact]
    public async Task Fields_pointed_at_by_their_words_win()
    {
        var lesson = await Learn(new LearnHints
        {
            Pointers = new Dictionary<string, string> { ["developer"] = "Paper Moon", ["title"] = "<h1 class=\"post-title\">" },
        });
        Assert.Equal("Paper Moon", lesson.Fields.Single(f => f.Field == "developer").Sample!.Replace("  (as you pointed)", ""));
        Assert.StartsWith("Star Kite", lesson.Fields.Single(f => f.Field == "title").Sample);
    }

    [Theory]
    [InlineData("example.com/demo/page/2/", "https://example.com/demo/page/{page}/")]
    [InlineData("https://example.com/games?page=2", "https://example.com/games?page={page}")]
    [InlineData("https://example.com/list/2", "https://example.com/list/{page}")]
    [InlineData("https://example.com/list/", null)]
    public void The_second_page_gives_every_page(string pageTwo, string? expected) =>
        Assert.Equal(expected, SiteReaderTeacher.PageTemplate(pageTwo, null));

    [Fact]
    public async Task The_second_page_alone_is_saved_as_the_list_addresses()
    {
        var lesson = await SiteReaderTeacher.LearnAsync("", null, new LearnHints { PageTwoAddress = "example.com/demo/page/2/" });
        Assert.Equal("https://example.com/demo/page/{page}/", lesson.Reader.BrowseUrl);
        Assert.Equal("example.com", lesson.Reader.Domains[0]);
    }
}
