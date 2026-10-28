using SeaRaider.Core.Scraping;

namespace SeaRaider.Core.Tests;

public class MediaScheduleTests
{
    private static readonly Uri Home = new("https://www.example-sports.test/");
    private static readonly Uri FootballPage = new("https://www.example-sports.test/schedule/football");

    [Fact]
    public async Task Events_listed_under_date_headings_are_read_without_pictures()
    {
        const string html = """
            <html><body>
              <nav><a href="/">Home</a><a href="/schedule/football">Football</a></nav>
              <h3>Tuesday, September 8, 2026</h3>
              <ul>
                <li><a href="/watch/alpha-vs-bravo">19:00 Alpha vs Bravo</a></li>
                <li><a href="/watch/charlie-vs-delta">21:30 Charlie vs Delta</a></li>
              </ul>
              <h3>Wednesday, September 9, 2026</h3>
              <ul><li><a href="/watch/echo-vs-foxtrot">Echo vs Foxtrot</a></li></ul>
              <footer><a href="/dmca">DMCA</a></footer>
            </body></html>
            """;
        using var doc = await MediaTitleParser.OpenAsync(html, FootballPage, default);
        var titles = MediaTitleParser.ParseTitles(doc, FootballPage, Guid.NewGuid());

        Assert.Equal(new[] { "Alpha vs Bravo", "Charlie vs Delta", "Echo vs Foxtrot" }, titles.Select(t => t.Title));
        Assert.Equal("Tuesday, September 8, 2026 · 19:00", titles[0].EventName);
        Assert.Equal("Wednesday, September 9, 2026", titles[2].EventName);
        Assert.All(titles, t => Assert.True(t.NeedsResolve));
        Assert.Equal("https://www.example-sports.test/watch/alpha-vs-bravo", titles[0].PlayUrl);

        // Their start: the day, and the time when the row has one.
        Assert.Equal(new DateTime(2026, 9, 8, 19, 0, 0), titles[0].StartLocal);
        Assert.True(titles[0].StartHasTime);
        Assert.Equal(new DateTime(2026, 9, 8, 21, 30, 0), titles[1].StartLocal);
        Assert.Equal(new DateTime(2026, 9, 9), titles[2].StartLocal);
        Assert.False(titles[2].StartHasTime);
    }

    [Fact]
    public void Day_headings_and_times_are_read()
    {
        var today = new DateTime(2026, 10, 27, 15, 0, 0);
        Assert.Equal(new DateTime(2026, 9, 8), MediaTitleParser.ParseDay("Tuesday, September 8, 2026", today));
        Assert.Equal(new DateTime(2026, 9, 8), MediaTitleParser.ParseDay("Sep 8, 2026", today));
        Assert.Equal(new DateTime(2026, 10, 27), MediaTitleParser.ParseDay("Today", today));
        Assert.Equal(new DateTime(2026, 10, 28), MediaTitleParser.ParseDay("Tomorrow, Oct 28", today));
        Assert.Null(MediaTitleParser.ParseDay("Football", today));
        Assert.Equal(new TimeSpan(19, 0, 0), MediaTitleParser.ParseClock("19:00"));
        Assert.Equal(new TimeSpan(19, 0, 0), MediaTitleParser.ParseClock("7:00 PM"));
        Assert.Equal(new TimeSpan(9, 30, 0), MediaTitleParser.ParseClock("9:30 a.m."));
        Assert.Null(MediaTitleParser.ParseClock(null));
    }

    [Fact]
    public void A_pages_assist_plan_is_found_by_its_address_whatever_hint_it_carries()
    {
        PageAssist.Set("https://www.example-sports.test/watch/a-vs-b", new PageAssistPlan(2, "#videoPlayer"));
        var plan = PageAssist.Get(MediaStreams.WithHint("https://www.example-sports.test/watch/a-vs-b", StreamFormat.Embed));
        Assert.Equal(2, plan?.Link);
        Assert.Equal("#videoPlayer", plan?.PlayerSteps);
        Assert.Null(PageAssist.Get("https://www.example-sports.test/watch/other"));
    }

    [Theory]
    [InlineData("<h2><!--Schedule --></h2>")]
    [InlineData("<h2>Schedule</h2>")]
    public async Task The_sports_under_a_Schedule_heading_are_its_sections(string heading)
    {
        var html = "<html><body>" + heading +
                   """<div class="sports"><a href="/schedule/football"><img src="/i/f.svg">Football</a><a href="/schedule/basketball">Basketball</a></div>""" +
                   """<h2>Videos</h2><a href="/about">About</a></body></html>""";
        using var doc = await MediaTitleParser.OpenAsync(html, Home, default);

        var sections = MediaTitleParser.FindScheduleSections(doc, Home);
        Assert.Equal(new[] { "Football", "Basketball" }, sections.Select(s => s.Label));
        Assert.Equal("https://www.example-sports.test/schedule/football", sections[0].Url.AbsoluteUri);

        Assert.Equal(new[] { "Football", "Basketball" }, MediaTitleParser.FindSections(doc, Home).Select(s => s.Label));
        Assert.Empty(MediaTitleParser.ParseTitles(doc, Home, Guid.NewGuid())); // the home page itself lists no events
    }

    [Fact]
    public async Task Day_tabs_are_found_as_pages()
    {
        const string html = """
            <html><body>
              <a href="/schedule/football?date=2026-09-08">Sep 8</a>
              <a href="/schedule/football?date=2026-09-09">Sep 9</a>
              <a href="/about">About</a>
            </body></html>
            """;
        using var doc = await MediaTitleParser.OpenAsync(html, FootballPage, default);
        Assert.Equal(2, MediaTitleParser.FindDatePages(doc, FootballPage).Count);
    }

    [Fact]
    public async Task A_dated_poster_grid_is_still_read_as_a_grid()
    {
        // A date on every card is card detail, not a heading above a day's events.
        var cards = string.Concat(Enumerable.Range(1, 5).Select(i =>
            $"""<div class="card"><a href="/movie/m{i}"><img src="/p/{i}.jpg" alt="Movie {i}"></a><span>Sep {i}, 2026</span></div>"""));
        var html = "<html><body><div class=\"grid\">" + cards + "</div></body></html>";
        using var doc = await MediaTitleParser.OpenAsync(html, Home, default);

        Assert.Equal(5, MediaTitleParser.ParseTitles(doc, Home, Guid.NewGuid()).Count);
    }
}
