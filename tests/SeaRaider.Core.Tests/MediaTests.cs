using SeaRaider.Core.Catalog;
using SeaRaider.Core.Models;
using SeaRaider.Core.Scraping;

namespace SeaRaider.Core.Tests;

public class MediaTests
{
    private static readonly Guid Source = Guid.NewGuid();

    [Theory]
    [InlineData("https://www.youtube.com/@IGN", MediaSourceKind.YouTubeChannel, null)]
    [InlineData("https://www.youtube.com/channel/UCKy1dAqELo0zrOtPkf0eTMw", MediaSourceKind.YouTubeChannel,
        "https://www.youtube.com/feeds/videos.xml?channel_id=UCKy1dAqELo0zrOtPkf0eTMw")]
    [InlineData("https://www.youtube.com/playlist?list=PLraFbwCOsc4bS4V3VxWqAeTnbQb1sOfY8", MediaSourceKind.YouTubePlaylist,
        "https://www.youtube.com/feeds/videos.xml?playlist_id=PLraFbwCOsc4bS4V3VxWqAeTnbQb1sOfY8")]
    [InlineData("https://www.twitch.tv/shroud", MediaSourceKind.Twitch, null)]
    [InlineData("https://www.gamespot.com/videos/", MediaSourceKind.Page, null)]
    public void Addresses_are_recognised(string url, MediaSourceKind kind, string? feed)
    {
        var (found, feedUrl, _) = MediaListParser.Classify(new Uri(url));
        Assert.Equal(kind, found);
        Assert.Equal(feed, feedUrl);
    }

    [Fact]
    public void A_channel_page_names_its_own_id()
    {
        const string html = """<html><head><link rel="canonical" href="https://www.youtube.com/channel/UCKy1dAqELo0zrOtPkf0eTMw"></head>""" +
                            """<body>"channelId":"UCxxxxxxxxxxxxxxxxxxxxxx"</body></html>""";
        Assert.Equal("UCKy1dAqELo0zrOtPkf0eTMw", MediaListParser.FindChannelId(html));
    }

    [Fact]
    public void YouTube_feeds_give_videos_with_titles_channels_and_pictures()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns:yt="http://www.youtube.com/xml/schemas/2015" xmlns:media="http://search.yahoo.com/mrss/" xmlns="http://www.w3.org/2005/Atom">
              <title>IGN</title>
              <author><name>IGN</name></author>
              <entry>
                <yt:videoId>dQw4w9WgXcQ</yt:videoId>
                <title>Hollow Depths - Official Launch Trailer</title>
                <link rel="alternate" href="https://www.youtube.com/watch?v=dQw4w9WgXcQ"/>
                <author><name>IGN</name></author>
                <published>2026-10-15T16:00:00+00:00</published>
                <media:group><media:thumbnail url="https://i1.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg" width="480" height="360"/></media:group>
              </entry>
            </feed>
            """;
        var (items, title) = MediaListParser.ParseFeed(xml, Source);

        Assert.Equal("IGN", title);
        var video = Assert.Single(items);
        Assert.Equal("Hollow Depths - Official Launch Trailer", video.Title);
        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", video.PlayUrl);
        Assert.Equal("https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg", video.ThumbnailUrl);
        Assert.Equal("IGN", video.Channel);
        Assert.Equal(new DateTime(2026, 10, 15, 16, 0, 0, DateTimeKind.Utc), video.PublishedUtc);
    }

    [Fact]
    public async Task Pages_give_their_embedded_and_linked_videos()
    {
        const string html = """
            <article><h3>Moss Town gameplay reveal</h3><iframe src="https://www.youtube.com/embed/abcdefghijk"></iframe></article>
            <a href="https://youtu.be/ABCDEFGHIJK">Lantern Keeper trailer</a>
            <a href="https://youtu.be/ABCDEFGHIJK">same video again</a>
            <iframe src="https://player.vimeo.com/video/123456789" title="Paper Moons teaser"></iframe>
            """;
        var (items, _) = await MediaListParser.ParsePageAsync(html, new Uri("https://example.com/videos"), Source, default);

        Assert.Equal(new[] { "Moss Town gameplay reveal", "Lantern Keeper trailer", "Paper Moons teaser" }, items.Select(i => i.Title));
        Assert.Equal("https://vimeo.com/123456789", items[2].PlayUrl);
    }

    [Fact]
    public void Twitch_boxes_show_the_live_picture_only_while_live()
    {
        var live = MediaListParser.TwitchItem(Source, "shroud", live: true, "Ranked grind", "https://img/profile.png");
        var offline = MediaListParser.TwitchItem(Source, "shroud", live: false, null, "https://img/profile.png");

        Assert.True(live.IsLive);
        Assert.Equal("Ranked grind", live.Title);
        Assert.StartsWith("https://static-cdn.jtvnw.net/previews-ttv/live_user_shroud-640x360.jpg", live.ThumbnailUrl);
        Assert.False(offline.IsLive);
        Assert.Equal("https://img/profile.png", offline.ThumbnailUrl);
        Assert.Equal("https://www.twitch.tv/shroud", offline.PlayUrl);
    }

    [Fact]
    public void Sources_file_keeps_media_channels()
    {
        var text = SourcesFile.Format(["https://a.example.com/"], [], ["https://www.youtube.com/@IGN"]);
        var list = SourcesFile.Parse(text);
        Assert.Equal(new[] { "https://www.youtube.com/@IGN" }, list.Media);
        Assert.Equal(new[] { "https://a.example.com/" }, list.Sources);
    }
}
