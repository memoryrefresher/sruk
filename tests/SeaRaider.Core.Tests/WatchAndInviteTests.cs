using SeaRaider.Core.Social;

namespace SeaRaider.Core.Tests;

public class WatchAndInviteTests
{
    private static readonly DateTime Start = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_playing_video_keeps_running_and_a_paused_one_stays_put()
    {
        var playing = new MediaActivity { Title = "Trailer", Url = "https://youtu.be/x", Position = 30, Playing = true, AtUtc = Start };
        Assert.Equal(40, playing.PositionAt(Start.AddSeconds(10)), 3);
        Assert.Equal(30, (playing with { Playing = false }).PositionAt(Start.AddSeconds(10)), 3);
        Assert.Equal(30, (playing with { IsLive = true }).PositionAt(Start.AddSeconds(10)), 3);
    }

    [Fact]
    public void A_snapshot_is_measured_from_now()
    {
        var playing = new MediaActivity { Title = "Trailer", Url = "https://youtu.be/x", Position = 30, Playing = true, AtUtc = Start };
        var later = Start.AddSeconds(5);
        var snapshot = playing.Snapshot(later);
        Assert.Equal(35, snapshot.Position, 3);
        Assert.Equal(later, snapshot.AtUtc);
    }

    [Fact]
    public void Friends_show_what_they_watch_unless_in_a_game()
    {
        var friend = new Friend { DisplayName = "Sam", Status = UserStatus.Online, Watching = new MediaActivity { Title = "Elden Ring trailer" } };
        Assert.Equal("Watching: Elden Ring trailer", friend.StatusText);

        friend.Status = UserStatus.InGame;
        friend.GameTitle = "Hades II";
        Assert.Equal("In-Game: Hades II", friend.StatusText);

        friend.Status = UserStatus.Offline;
        Assert.Equal("Offline", friend.StatusText);
    }

    [Fact]
    public void Invites_are_kept_with_their_answer()
    {
        var path = Path.Combine(Path.GetTempPath(), $"searaider-social-{Guid.NewGuid():N}.db");
        try
        {
            string id;
            using (var store = new SocialStore(path))
            {
                var message = new ChatMessage
                {
                    FriendId = "SR-AAAAA-BBBBB",
                    Text = "🎮 Come play Hades II with me!",
                    Invite = new ChatInvite { Kind = InviteKind.Game, Title = "Hades II", PageUrl = "https://example.com/hades-2" },
                    Answer = InviteAnswer.Accepted,
                };
                id = message.Id;
                store.AddMessage(message);
            }
            using (var reopened = new SocialStore(path))
            {
                var message = reopened.GetMessage(id)!;
                Assert.Equal(InviteKind.Game, message.Invite!.Kind);
                Assert.Equal("Hades II", message.Invite.Title);
                Assert.Equal(InviteAnswer.Accepted, message.Answer);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
