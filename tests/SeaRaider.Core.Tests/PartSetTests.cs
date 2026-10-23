using Microsoft.Extensions.Logging.Abstractions;
using SeaRaider.Core.Downloads;
using SeaRaider.Core.Models;
using SeaRaider.Core.Scraping;

namespace SeaRaider.Core.Tests;

/// <summary>
/// Sites that list each provider's parts on one line ("Link Gofile: Part 1 – Part 2 – Part 3"), often through a URL generator
/// (bluemediafiles.com). A provider is taken whole or not at all, and captcha-on-every-file hosts (MixDrop) are skipped.
/// </summary>
public class PartSetTests
{
    private static readonly Uri Page = new("https://games.example/game/space-demo/");
    private const string Gen = "https://bluemediafiles.com/url-generator.php?url=";

    private static async Task<(List<DownloadLink> Links, List<string> Warnings)> ParseAsync(string html, ScraperProfile? profile = null)
    {
        using var doc = await GamePageParser.ParseHtmlAsync($"<html><body><article>{html}</article></body></html>", Page, CancellationToken.None);
        var parser = new GamePageParser(NullLogger<GamePageParser>.Instance);
        var warnings = new List<string>();
        return (parser.FindDownloadLinks(doc, Page, profile ?? new ScraperProfile(), warnings), warnings);
    }

    private static string Line(string provider, string parts) =>
        $"""<p><b class="uk-heading-bullet">Link {provider}:</b><br /> {parts} </p>""";

    [Fact]
    public async Task A_providers_linked_parts_are_one_multipart_set()
    {
        var (links, warnings) = await ParseAsync(Line("Gofile",
            $"""<a href="{Gen}AAA">Part 1</a> &#8211; <a href="{Gen}BBB">Part 2</a> &#8211; <a href="{Gen}CCC">Part 3</a>"""));

        var group = Assert.Single(DownloadLinkDeduplicator.Group(links));
        Assert.Equal("Gofile", group.Mirror);
        Assert.Equal(LinkCategory.MultiPartArchive, group.Category);
        Assert.Equal([1, 2, 3], group.Links.Select(l => l.PartNumber));
        Assert.Equal(3, group.Links.Select(l => l.Url).Distinct().Count()); // not folded into one "url-generator.php"
        Assert.Empty(warnings);
    }

    [Fact]
    public async Task Two_providers_behind_the_same_generator_stay_two_options()
    {
        var (links, _) = await ParseAsync(
            Line("Gofile", $"""<a href="{Gen}1">Part 1</a> &#8211; <a href="{Gen}2">Part 2</a>""") +
            Line("Pixeldrain", $"""<a href="{Gen}3">Part 1</a> &#8211; <a href="{Gen}4">Part 2</a>"""));

        var groups = DownloadLinkDeduplicator.Group(links);
        Assert.Equal(["Gofile", "Pixeldrain"], groups.Select(g => g.Mirror).Order());
        Assert.All(groups, g => Assert.Equal(2, g.Links.Count));
    }

    [Fact]
    public async Task Parts_typed_as_plain_text_reject_the_whole_provider()
    {
        // "Part 1 – Part 2 – Part 3" have no link; only Part 4 does.
        var (links, warnings) = await ParseAsync(Line("Pixeldrain", $"""Part 1 &#8211; Part 2 &#8211; Part 3 &#8211; <a href="{Gen}DDD">Part 4</a>"""));

        Assert.Empty(links); // the lone Part 4 does not come back as a file through the general link scan
        Assert.Contains(warnings, w => w.StartsWith("Pixeldrain: rejected", StringComparison.Ordinal) && w.Contains("Part 1", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""<a href="GEN1">Part 1</a> &#8211; <a>Part 2</a>""")]                              // no href at all
    [InlineData("""<a href="GEN1">Part 1</a> &#8211; <a href="">Part 2</a>""")]                      // empty href
    [InlineData("""<a href="GEN1">Part 1</a> &#8211; <a href="#">Part 2</a>""")]                     // placeholder
    [InlineData("""<a href="GEN1">Part 1</a> &#8211; <a href="javascript:void(0)">Part 2</a>""")]
    [InlineData("""<a href="GEN1">Part 1</a> &#8211; <del><a href="GEN2">Part 2</a></del>""")]       // struck through
    [InlineData("""<a href="GEN1">Part 1</a> &#8211; <a href="GEN2">Part 2 (offline)</a>""")]
    [InlineData("""<a href="GEN1">Part 1</a> &#8211; <a class="disabled" href="GEN2">Part 2</a>""")]
    [InlineData("""<a href="GEN1">Part 1</a> &#8211; <a href="GEN3">Part 3</a>""")]                  // Part 2 missing
    [InlineData("""<a href="GEN1">Part 1</a> &#8211; <a href="GEN2">Part 1</a>""")]                  // listed twice
    [InlineData("""<a href="GEN1">Part 1</a> &#8211; <a href="GEN2">Part 2</a> (Part 3 is down)""")]  // notice outside a link
    public async Task A_part_that_is_not_a_working_link_rejects_the_provider(string parts)
    {
        var html = Line("Gofile", parts.Replace("GEN", Gen, StringComparison.Ordinal));
        var (links, warnings) = await ParseAsync(html);

        Assert.Empty(links);
        Assert.Single(warnings);
    }

    [Fact]
    public async Task The_page_stating_its_part_count_must_agree()
    {
        var (links, _) = await ParseAsync(
            $"""<p><b class="uk-heading-bullet">Link Gofile (4 parts):</b><br /><a href="{Gen}1">Part 1</a> &#8211; <a href="{Gen}2">Part 2</a></p>""");
        Assert.Empty(links);
    }

    [Fact]
    public async Task One_broken_provider_does_not_spoil_the_others()
    {
        var (links, warnings) = await ParseAsync(
            Line("Gofile", $"""<a href="{Gen}1">Part 1</a> &#8211; <a href="{Gen}2">Part 2</a>""") +
            Line("Pixeldrain", $"""Part 1 &#8211; <a href="{Gen}4">Part 2</a>"""));

        Assert.Equal(["Gofile"], DownloadLinkDeduplicator.Group(links).Select(g => g.Mirror));
        Assert.Single(warnings);
    }

    [Fact]
    public async Task MixDrop_is_skipped_by_name_by_host_and_inside_an_encoded_generator_link()
    {
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("https://mixdrop.ag/f/abc123"));
        var (links, warnings) = await ParseAsync(
            Line("MixDrop", $"""<a href="{Gen}1">Part 1</a> &#8211; <a href="{Gen}2">Part 2</a>""") +
            Line("Direct", """<a href="https://mixdrop.co/f/xyz">Part 1</a>""") +
            Line("Sneaky", $"""<a href="{Gen}{encoded}">Part 1</a>""") +
            Line("Gofile", $"""<a href="{Gen}7">Part 1</a> &#8211; <a href="{Gen}8">Part 2</a>"""));

        Assert.Equal(["Gofile"], DownloadLinkDeduplicator.Group(links).Select(g => g.Mirror));
        Assert.Equal(3, warnings.Count);
    }

    [Fact]
    public async Task A_provider_with_one_link_is_a_single_file_and_unnumbered_buttons_are_numbered_by_position()
    {
        var (links, _) = await ParseAsync(
            Line("Pixeldrain", """<a href="https://pixeldrain.com/u/abc123">Download</a>""") +
            Line("Gofile", $"""<a href="{Gen}1">Click Here</a> &#8211; <a href="{Gen}2">Click Here</a>"""));

        var groups = DownloadLinkDeduplicator.Group(links);
        Assert.Contains(groups, g => g.Mirror == "Pixeldrain" && g.Links.Count == 1 && g.Links[0].Kind == DownloadLinkKind.FileHost);
        Assert.Contains(groups, g => g.Mirror == "Gofile" && g.Category == LinkCategory.MultiPartArchive && g.Links.Count == 2);
    }

    [Fact]
    public async Task A_planned_download_takes_every_part_of_the_chosen_provider()
    {
        var (links, _) = await ParseAsync(Line("Gofile",
            $"""<a href="{Gen}1">Part 1</a> &#8211; <a href="{Gen}2">Part 2</a> &#8211; <a href="{Gen}3">Part 3</a>"""));
        var game = new GameMetadata { PageUrl = Page.AbsoluteUri, DownloadLinks = links };

        var plan = DownloadPlanner.Plan(game, preferred: links[1], preferTorrents: false);

        Assert.Equal(3, plan.Links.Count);
        Assert.Equal([1, 2, 3], plan.Links.Select(l => l.PartNumber));
    }

    [Fact]
    public async Task A_reader_can_opt_in_with_downloadSets()
    {
        var reader = new SiteReader
        {
            Domains = ["games.example"],
            Page = new PageRecipe { DownloadSets = "b.uk-heading-bullet", Downloads = "article a[href]" },
        };
        var html = $"<html><body><article>{Line("Gofile", $"""<a href="{Gen}1">Part 1</a> &#8211; <a href="{Gen}2">Part 2</a>""")}" +
                   $"{Line("Pixeldrain", $"""Part 1 &#8211; <a href="{Gen}3">Part 2</a>""")}</article></body></html>";
        using var doc = await GamePageParser.ParseHtmlAsync(html, Page, CancellationToken.None);
        var game = new GameMetadata { PageUrl = Page.AbsoluteUri, Title = "Space Demo", DetailsLoaded = false };

        var notes = SiteReaderParser.EnrichFromGamePage(reader, doc, Page, game);

        Assert.Equal(["Gofile"], DownloadLinkDeduplicator.Group(game.DownloadLinks).Select(g => g.Mirror)); // Pixeldrain's Part 2 isn't taken alone
        Assert.Contains(notes, n => n.StartsWith("Pixeldrain: rejected", StringComparison.Ordinal));
    }

    [Fact]
    public void Generator_script_names_are_not_file_names()
    {
        Assert.Equal(string.Empty, FileHosts.GuessFileName(new Uri(Gen + "AAA")));
        Assert.Equal("Game.part01.rar", FileHosts.GuessFileName(new Uri("https://fuckingfast.co/abc123#Game.part01.rar")));
        Assert.NotNull(FileHosts.MatchFileLink(new Uri(Gen + "AAA")));
        Assert.True(FileHosts.IsLinkShortener("bluemediafiles.com"));
    }

    [Theory]
    [InlineData("mixdrop.ag", true)]
    [InlineData("www.mixdrop.co", true)]
    [InlineData("m1xdrop.bz", true)]
    [InlineData("mixdropper.com", false)]
    [InlineData("gofile.io", false)]
    public void Blocked_hosts(string host, bool blocked) => Assert.Equal(blocked, HostPolicy.IsBlockedHost(host));

    [Theory]
    [InlineData("MixDrop", true)]
    [InlineData("Link Mixdrop:", true)]
    [InlineData("Gofile", false)]
    public void Blocked_names(string name, bool blocked) => Assert.Equal(blocked, HostPolicy.IsBlockedName(name));
}
