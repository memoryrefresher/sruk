using SeaRaider.Core.Social;

namespace SeaRaider.Core.Tests;

public class SharedMediaTests
{
    [Theory]
    [InlineData("1:23", 83)]
    [InlineData("0:05", 5)]
    [InlineData("1:02:03", 3723)]
    [InlineData("83", 83)]
    [InlineData("83s", 83)]
    [InlineData("1m23s", 83)]
    [InlineData("1h2m3s", 3723)]
    [InlineData(" 2:15 ", 135)]
    public void Times_are_read(string text, int seconds) => Assert.Equal(seconds, MediaTime.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("1:75")]
    [InlineData("0:00")]
    [InlineData("99:99:99")]
    public void Non_times_are_refused(string text) => Assert.Null(MediaTime.Parse(text));

    [Theory]
    [InlineData(5, "0:05")]
    [InlineData(83, "1:23")]
    [InlineData(3723, "1:02:03")]
    public void Times_are_written(int seconds, string text) => Assert.Equal(text, MediaTime.Format(seconds));

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=83", 83)]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?t=1m23s", 83)]
    [InlineData("https://www.twitch.tv/videos/123456789?t=1h2m3s", 3723)]
    [InlineData("https://vimeo.com/76979871#t=90s", 90)]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", null)]
    public void Links_carry_their_start(string url, int? seconds) => Assert.Equal(seconds, MediaTime.FromUrl(url));

    [Fact]
    public void Comment_times_become_links()
    {
        var times = MediaTime.FindInText("the jump at 2:15 then again at 1:02:03, and 2:15 lol (not 12:345 or v1:23x)");
        Assert.Equal(new[] { 135, 3723 }, times);
    }

    [Fact]
    public void Shared_posts_are_kept_with_their_reactions_and_comments()
    {
        var path = Path.Combine(Path.GetTempPath(), $"searaider-social-{Guid.NewGuid():N}.db");
        try
        {
            using (var store = new SocialStore(path))
            {
                var post = new SharedMediaPost { AuthorId = "SR-AAAAA-BBBBB", AuthorName = "Sam", Url = "https://youtu.be/x", Title = "Trailer", StartSeconds = 83 };
                post.Reactions.Add(new SharedMediaReaction { UserId = "SR-CCCCC-DDDDD", Name = "Alex", Emoji = "🔥" });
                post.Comments.Add(new SharedMediaComment { AuthorId = "SR-CCCCC-DDDDD", AuthorName = "Alex", Text = "2:15 is wild" });
                post.Audience.Add("SR-CCCCC-DDDDD");
                store.UpsertSharedPost(post);
                store.AddOutbox(new SharedMediaOutboxItem { To = "SR-CCCCC-DDDDD", Kind = MediaShareKinds.Post, PayloadJson = "{}" });
            }

            using (var reopened = new SocialStore(path))
            {
                var post = Assert.Single(reopened.GetSharedPosts());
                Assert.Equal(83, post.StartSeconds);
                Assert.Equal("🔥", Assert.Single(post.Reactions).Emoji);
                Assert.Equal("2:15 is wild", Assert.Single(post.Comments).Text);
                Assert.Equal(new[] { "SR-CCCCC-DDDDD" }, post.Audience);
                Assert.Single(reopened.GetOutbox());
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
