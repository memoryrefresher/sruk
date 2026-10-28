using SeaRaider.Core.Scraping;

namespace SeaRaider.Core.Tests;

/// <summary>Ad blocking › which requests a player's or game's page is not allowed to make.</summary>
public class AdHostsTests
{
    [Theory]
    [InlineData("https://securepubads.g.doubleclick.net/gampad/ads?x=1")]
    [InlineData("https://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js")]
    [InlineData("https://www.moatads.com/x.js")]
    [InlineData("https://cdn.adsterra.com/a.js")]
    [InlineData("https://example.com/pagead/show_ads.js")]
    [InlineData("https://example.com/video/vast.xml")]
    [InlineData("https://example.com/serve?zoneid=123")]
    [InlineData("https://example.com/ads/banner.html")]
    [InlineData("https://adv.example.com/x")]
    public void AdRequestsAreBlocked(string url) => Assert.True(AdHosts.IsAdRequest(url));

    [Theory]
    [InlineData("https://cdn.example.com/video/master.m3u8")]
    [InlineData("https://uploads.example.com/file.mp4")]
    [InlineData("https://example.com/game/index.html")]
    [InlineData("https://www.youtube.com/embed/abc123")]
    [InlineData("https://example.com/loads/readme")]
    [InlineData("not a url")]
    [InlineData("")]
    public void NormalRequestsAreNotBlocked(string url) => Assert.False(AdHosts.IsAdRequest(url));

    [Fact]
    public void ParentDomainMatches() => Assert.True(AdHosts.IsAdHost("a.b.doubleclick.net"));

    [Fact]
    public void NullIsNotAnAd()
    {
        Assert.False(AdHosts.IsAdRequest((string?)null));
        Assert.False(AdHosts.IsAdRequest((Uri?)null));
        Assert.False(AdHosts.IsAdHost(null));
    }
}
