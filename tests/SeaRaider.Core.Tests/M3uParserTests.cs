using SeaRaider.Core.Iptv;
using SeaRaider.Core.Scraping;

namespace SeaRaider.Core.Tests;

/// <summary>Media › Live TV › reading an IPTV M3U playlist.</summary>
public class M3uParserTests
{
    private const string Sample = """
        #EXTM3U
        #EXTINF:-1 tvg-id="espn.us" tvg-name="ESPN HD" tvg-logo="https://img.example/espn.png" group-title="USA | Sports",ESPN HD
        http://panel.example:8080/live/user/pass/1001.ts
        #EXTINF:-1 tvg-id="" tvg-logo="" group-title="News",BBC News, London
        https://cdn.example/bbc/index.m3u8
        #EXTVLCOPT:http-user-agent=Foo
        #EXTINF:-1 group-title="Radio",Skipped RTMP
        rtmp://x.example/live
        #EXTINF:-1,Plain Channel
        http://panel.example/stream/77
        """;

    [Fact]
    public void ReadsChannelsWithAttributes()
    {
        var channels = M3uParser.Parse(new StringReader(Sample));
        Assert.Equal(3, channels.Count);

        var espn = channels[0];
        Assert.Equal("ESPN HD", espn.Name);
        Assert.Equal("USA | Sports", espn.Group);
        Assert.Equal("https://img.example/espn.png", espn.LogoUrl);
        Assert.Equal("espn.us", espn.TvgId);
        Assert.Equal(IptvCategory.Sports, espn.Category);
        Assert.Equal(1, espn.Number);

        Assert.Equal("BBC News, London", channels[1].Name); // a comma inside the name
        Assert.Equal(IptvCategory.News, channels[1].Category);
        Assert.Null(channels[1].LogoUrl);

        Assert.Equal("Plain Channel", channels[2].Name);
        Assert.Equal(3, channels[2].Number); // the rtmp:// entry is skipped, numbering stays dense
    }

    [Theory]
    [InlineData("http://p.example:8080/live/u/p/1001.ts", "http://p.example:8080/live/u/p/1001.m3u8")]
    [InlineData("http://p.example:8080/u/p/1001", "http://p.example:8080/live/u/p/1001.m3u8")]
    [InlineData("http://p.example/some/stream.ts", "http://p.example/some/stream.m3u8")]
    [InlineData("https://cdn.example/a/index.m3u8?token=1", "https://cdn.example/a/index.m3u8?token=1")]
    public void StreamAddressesAreMadePlayable(string input, string expected) => Assert.Equal(expected, M3uParser.PlayUrl(input));

    [Fact]
    public void AddressWithoutFormatIsMarkedAsHls() =>
        Assert.Equal(StreamFormat.Hls, MediaStreams.FormatOf(M3uParser.PlayUrl("http://p.example/stream/77")));

    [Theory]
    [InlineData("", "Fox Sports 1", IptvCategory.Sports)]
    [InlineData("UK", "Sky Sports Main Event", IptvCategory.Sports)]
    [InlineData("", "F1 TV", IptvCategory.Sports)]
    [InlineData("", "Nick Jr", IptvCategory.Kids)]
    [InlineData("", "Local Weather", IptvCategory.News)]
    [InlineData("", "Gold TV", IptvCategory.Other)]
    [InlineData("", "Transformers", IptvCategory.Other)] // "f1"/"tsn"-style short words don't match inside other words
    public void Categorizes(string group, string name, IptvCategory expected) => Assert.Equal(expected, M3uParser.Categorize(group, name));
}
