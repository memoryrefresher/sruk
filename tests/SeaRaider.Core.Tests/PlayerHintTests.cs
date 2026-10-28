using SeaRaider.Core.Scraping;

namespace SeaRaider.Core.Tests;

public class PlayerHintTests
{
    private static readonly Uri Page = new("https://www.example-sports.test/watch/a-vs-b");

    private static async Task<string?> FindAsync(string hint, string html)
    {
        using var doc = await MediaTitleParser.OpenAsync(html, Page, default);
        return PlayerHints.Find(hint, doc, html, Page);
    }

    [Fact]
    public async Task A_selector_reads_the_players_src_and_makes_it_absolute()
    {
        const string html = """<html><body><div class="box"><iframe id="player" src="/embed/42"></iframe></div></body></html>""";
        Assert.Equal("https://www.example-sports.test/embed/42", await FindAsync("iframe#player", html));
        Assert.Equal("https://www.example-sports.test/embed/42", await FindAsync(".box iframe", html));
        Assert.Null(await FindAsync("iframe.missing", html));
    }

    [Fact]
    public async Task A_selector_can_name_the_attribute_and_skips_empty_srcs()
    {
        const string html = """<html><body><iframe class="live" src="about:blank" data-embed="https://cdn.test/e/abc?x=1&amp;y=2"></iframe></body></html>""";
        Assert.Equal("https://cdn.test/e/abc?x=1&y=2", await FindAsync("iframe.live", html));        // src is blank: the next attribute
        Assert.Equal("https://cdn.test/e/abc?x=1&y=2", await FindAsync("iframe.live@data-embed", html));
        Assert.Null(await FindAsync("iframe.live@data-nothing", html));
    }

    [Fact]
    public async Task An_address_template_is_filled_in_from_the_page()
    {
        Assert.Equal("https://embed.test/e/a-vs-b", await FindAsync("https://embed.test/e/{slug}", "<html></html>"));
        Assert.Equal("https://embed.test/watch/a-vs-b?h=www.example-sports.test", await FindAsync("https://embed.test/{path}?h={host}", "<html></html>"));
    }

    [Fact]
    public async Task A_pattern_finds_the_address_in_the_pages_script()
    {
        const string html = """<html><script>player.load({file: "https:\/\/cdn.test\/live\/a.m3u8", autoplay: true});</script></html>""";
        Assert.Equal("https://cdn.test/live/a.m3u8", await FindAsync("""regex:"(https://[^"]+\.m3u8)" """, html));
        Assert.Null(await FindAsync("""regex:"(https://[^"]+\.mpd)" """, html));
    }

    [Fact]
    public void Hints_are_checked_before_they_are_saved()
    {
        Assert.Null(PlayerHints.Validate(null));
        Assert.Null(PlayerHints.Validate("  "));
        Assert.Null(PlayerHints.Validate("iframe#player"));
        Assert.Null(PlayerHints.Validate("iframe.live@data-src"));
        Assert.Null(PlayerHints.Validate("https://embed.test/e/{slug}"));
        Assert.NotNull(PlayerHints.Validate("regex:("));
    }

    [Fact]
    public void A_hint_is_described_for_the_hidden_browser()
    {
        Assert.Equal(("selector", "#videoPlayer iframe", (string?)null), PlayerHints.Describe("#videoPlayer iframe"));
        Assert.Equal(("selector", "iframe.live", (string?)"data-embed"), PlayerHints.Describe("iframe.live@data-embed"));
        Assert.Equal(("regex", """(https://yield\.gs/[^"'\s<>\\]+)""", (string?)null), PlayerHints.Describe("""regex:(https://yield\.gs/[^"'\s<>\\]+)"""));
        Assert.Null(PlayerHints.Describe("https://embed.test/e/{slug}")); // a template needs no browser
        Assert.Null(PlayerHints.Describe(" "));
    }

    [Fact]
    public void The_click_selector_is_checked()
    {
        Assert.Null(PlayerHints.ValidateSelector("#linkSelector a"));
        Assert.Null(PlayerHints.ValidateSelector("#linkSelector a:first-child"));
    }

    [Fact]
    public void The_click_selector_is_part_of_what_tells_preferences_apart()
    {
        var plain = new PlayerPreference([], 0, true) { Hint = "iframe" };
        var clicking = plain with { ClickSelector = "#linkSelector a" };
        Assert.NotEqual(plain.Signature, clicking.Signature);
    }

    [Fact]
    public void The_chosen_link_is_part_of_what_tells_preferences_apart()
    {
        var automatic = new PlayerPreference([], 0, true) { Hint = "iframe" };
        Assert.NotEqual(automatic.Signature, (automatic with { Link = 2 }).Signature);
    }
}
