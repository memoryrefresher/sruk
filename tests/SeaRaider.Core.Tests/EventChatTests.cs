using SeaRaider.Core.Social;

namespace SeaRaider.Core.Tests;

public class EventChatTests
{
    private static readonly DateTime Day = new(2026, 10, 21, 19, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Everyone_watching_a_match_shares_one_room_whatever_the_site()
    {
        var a = EventChatService.RoomFor("Bayern Munich vs Arsenal", "https://streamed.example/watch/bayern-munich-vs-arsenal-1", "https://embed.example/e/1#sr-embed", Day);
        var b = EventChatService.RoomFor("Arsenal vs Bayern Munich", "https://other.example/match/42", "https://cdn.example/live.m3u8", Day.AddHours(2));
        Assert.Equal(a, b);
        Assert.StartsWith("ev:", a);
        Assert.Matches("^[a-z0-9][a-z0-9:_\\-\\.]{2,119}$", a); // what the relay accepts
    }

    [Fact]
    public void Other_events_and_other_days_get_their_own_rooms()
    {
        var match = EventChatService.RoomFor("Bayern Munich vs Arsenal", null, "https://x.example/1", Day);
        Assert.NotEqual(match, EventChatService.RoomFor("Bayern Munich vs Arsenal", null, "https://x.example/1", Day.AddDays(1)));
        Assert.NotEqual(match, EventChatService.RoomFor("Real Madrid vs RB Leipzig", null, "https://x.example/1", Day));

        // Not a match: one room per page (the same page from www. or not, with or without a trailing slash).
        Assert.Equal(EventChatService.RoomFor("The Golazo Show", "https://www.site.example/watch/golazo/", "https://a.example/1"),
                     EventChatService.RoomFor("Golazo", "https://site.example/watch/golazo", "https://b.example/2"));
    }
}
