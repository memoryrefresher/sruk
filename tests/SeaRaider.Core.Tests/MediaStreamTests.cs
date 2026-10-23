using SeaRaider.Core.Models;
using SeaRaider.Core.Scraping;

namespace SeaRaider.Core.Tests;

public class MediaStreamTests
{
    [Theory]
    [InlineData("https://cdn.example.com/live/main.m3u8", "https://cdn.example.com/live/main.m3u8")]
    [InlineData("https://cdn.example.com/vod/manifest.mpd?token=abc", "https://cdn.example.com/vod/manifest.mpd?token=abc")]
    [InlineData("https://files.example.com/trailer.mp4", "https://files.example.com/trailer.mp4")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/live/dQw4w9WgXcQ?feature=share", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://vimeo.com/76979871", "https://vimeo.com/76979871")]
    [InlineData("https://www.twitch.tv/videos/123456789?t=1h2m", "https://www.twitch.tv/videos/123456789")]
    [InlineData("https://www.dailymotion.com/video/x8abc12", "https://www.dailymotion.com/video/x8abc12")]
    [InlineData("https://dai.ly/x8abc12", "https://www.dailymotion.com/video/x8abc12")]
    [InlineData("https://kick.com/SomeStreamer", "https://kick.com/somestreamer")]
    public void A_stream_or_single_video_is_added_as_a_stream(string url, string play)
    {
        var (kind, feed, _) = MediaListParser.Classify(new Uri(url));
        Assert.Equal(MediaSourceKind.Stream, kind);
        Assert.Equal(play, feed);
    }

    [Theory]
    [InlineData("https://kick.com/categories")]
    [InlineData("https://www.youtube.com/@IGN")]
    [InlineData("https://www.gamespot.com/videos/")]
    public void Channels_and_pages_are_not_streams(string url)
    {
        Assert.NotEqual(MediaSourceKind.Stream, MediaListParser.Classify(new Uri(url)).Kind);
    }

    [Theory]
    [InlineData("rtmp", true)]
    [InlineData("RTSP", true)]
    [InlineData("srt", true)]
    [InlineData("https", false)]
    [InlineData("http", false)]
    public void Streams_a_browser_cannot_play_are_recognised(string scheme, bool unplayable) =>
        Assert.Equal(unplayable, MediaStreams.IsUnplayableScheme(scheme));

    [Fact]
    public void The_format_is_remembered_in_the_address_and_removed_again()
    {
        const string url = "https://cdn.example.com/live/main";
        var hinted = MediaStreams.WithHint(url, StreamFormat.Hls);
        Assert.Equal(url + "#sr-hls", hinted);
        Assert.Equal(StreamFormat.Hls, MediaStreams.FormatOf(hinted));
        Assert.True(MediaStreams.HasHint(hinted));
        Assert.Equal(url, MediaStreams.WithoutHint(hinted));
        Assert.Equal(StreamFormat.Dash, MediaStreams.FormatOf(MediaStreams.WithHint(url, StreamFormat.Dash)));
        Assert.Equal(StreamFormat.File, MediaStreams.FormatOf(MediaStreams.WithHint(url, StreamFormat.File)));
        Assert.Null(MediaStreams.FormatOf(url));
    }

    [Fact]
    public void An_address_that_already_names_its_format_gets_no_hint()
    {
        const string url = "https://cdn.example.com/live/main.m3u8";
        Assert.Equal(url, MediaStreams.WithHint(url, StreamFormat.Hls));
        Assert.Equal(StreamFormat.Hls, MediaStreams.FormatOf(url));
    }

    [Theory]
    [InlineData("#EXTM3U\n#EXT-X-VERSION:3", StreamFormat.Hls)]
    [InlineData("﻿#EXTM3U", StreamFormat.Hls)]
    [InlineData("<?xml version=\"1.0\"?><MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\">", StreamFormat.Dash)]
    public void Playlists_say_what_they_are_in_their_first_characters(string start, StreamFormat format) =>
        Assert.Equal(format, MediaStreams.FromStart(start));

    [Theory]
    [InlineData("<!doctype html><html>")]
    [InlineData("<?xml version=\"1.0\"?><rss version=\"2.0\">")]
    [InlineData("")]
    public void Pages_and_feeds_are_not_streams(string start) => Assert.Null(MediaStreams.FromStart(start));

    [Fact]
    public async Task A_playlist_without_an_end_is_live()
    {
        const string live = "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXT-X-MEDIA-SEQUENCE:100\n#EXTINF:6.0,\nseg100.ts\n#EXTINF:6.0,\nseg101.ts\n";
        Assert.True(await MediaStreams.IsLiveAsync(live, new Uri("https://x.test/a.m3u8"), _ => throw new InvalidOperationException()));
    }

    [Fact]
    public async Task A_playlist_with_an_end_is_a_recording()
    {
        const string vod = "#EXTM3U\n#EXTINF:6.0,\nseg0.ts\n#EXT-X-ENDLIST\n";
        Assert.False(await MediaStreams.IsLiveAsync(vod, new Uri("https://x.test/a.m3u8"), _ => throw new InvalidOperationException()));
    }

    [Fact]
    public async Task A_list_of_qualities_is_judged_by_its_first_stream()
    {
        const string master = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=2000000\nhigh/index.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=800000\nlow/index.m3u8\n";
        Uri? asked = null;
        var live = await MediaStreams.IsLiveAsync(master, new Uri("https://x.test/live/master.m3u8"), uri =>
        {
            asked = uri;
            return Task.FromResult("#EXTM3U\n#EXTINF:4.0,\ns1.ts\n");
        });
        Assert.True(live);
        Assert.Equal("https://x.test/live/high/index.m3u8", asked?.AbsoluteUri);
    }

    [Fact]
    public async Task Something_that_is_not_a_playlist_is_refused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MediaStreams.IsLiveAsync("<html></html>", new Uri("https://x.test/a"), _ => Task.FromResult(string.Empty)));
    }

    [Fact]
    public void Stream_addresses_written_in_a_page_are_found()
    {
        const string html = """<script>var a="https:\/\/cdn.x.test\/live\/index.m3u8?t=1&amp;k=2"; load('https://cdn.x.test/v/manifest.mpd');</script><a href="https://x.test/a.m3u8">""";
        var found = MediaStreams.FindStreamUrls(html);
        Assert.Equal(3, found.Count);
        Assert.Equal("https://cdn.x.test/live/index.m3u8?t=1&k=2", found[0]);
        Assert.Equal("https://cdn.x.test/v/manifest.mpd", found[1]);
    }

    [Fact]
    public void A_page_that_is_a_player_is_remembered_as_one()
    {
        var hinted = MediaStreams.WithHint("https://embed.st/embed/match-1", StreamFormat.Embed);
        Assert.Equal("https://embed.st/embed/match-1#sr-embed", hinted);
        Assert.Equal(StreamFormat.Embed, MediaStreams.FormatOf(hinted));
        Assert.Equal("https://embed.st/embed/match-1", MediaStreams.WithoutHint(hinted));
    }
}
