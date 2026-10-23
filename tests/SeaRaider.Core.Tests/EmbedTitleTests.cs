using SeaRaider.Core.Catalog;

namespace SeaRaider.Core.Tests;

public class EmbedTitleTests
{
    [Theory]
    [InlineData("https://embed.example/embed/admin/bayern-munich-vs-arsenal/1", "Bayern Munich vs Arsenal")]
    [InlineData("https://player.example/watch/lakers_v_celtics.html", "Lakers vs Celtics")]
    [InlineData("https://embed.example/e/a8f3k2l9x0", null)]
    [InlineData("https://embed.example/stream/1", null)]
    public void Embed_addresses_name_the_event(string address, string? expected) =>
        Assert.Equal(expected, MediaService.TitleFromAddress(address));

    [Theory]
    [InlineData("Stream 1", "https://embed.example/x", true)]
    [InlineData("Embedsports", "https://embedsports.top/x", true)]
    [InlineData("Live Player", "https://a.example/x", true)]
    [InlineData("", "https://a.example/x", true)]
    [InlineData("Bayern Munich vs Arsenal", "https://embed.example/x", false)]
    public void Player_titles_are_vague(string title, string address, bool vague) =>
        Assert.Equal(vague, MediaService.IsVagueTitle(title, address));
}

public class SportPictureTests
{
    [Theory]
    [InlineData("Football", false, "football")]
    [InlineData("American Football", false, "american-football")]
    [InlineData("Fight (UFC, Boxing)", false, "mma")]
    [InlineData("Motor Sports", false, "motorsport")]
    [InlineData("Horse Racing", false, "horse-racing")]
    [InlineData("Table Tennis", false, "table-tennis")]
    [InlineData("Tennis", false, "tennis")]
    [InlineData("Basketball", false, "basketball")]
    [InlineData(null, true, "any")]
    [InlineData("Movies", false, null)]
    [InlineData(null, false, null)]
    public void Categories_name_their_sport(string? category, bool live, string? expected) =>
        Assert.Equal(expected, MediaArtworkService.SportKey(category, live));
}
