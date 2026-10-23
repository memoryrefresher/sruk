using SeaRaider.Core.Catalog;

namespace SeaRaider.Core.Tests;

public class MultiplayerGamesTests
{
    private static readonly IReadOnlyList<string> Exceptions = MultiplayerGames.ParseExceptions(null);

    [Theory]
    [InlineData("The Elder Scrolls Online", true)]
    [InlineData("Ready or Not (Online)", true)]
    [InlineData("Deep Rock Galactic + Online-Fix", true)]
    [InlineData("Deep Rock Galactic OnlineFix", true)]
    [InlineData("PHANTASY STAR ONLINE 2", true)]
    [InlineData("Sword Art Online: Fatal Bullet", true)]
    [InlineData("Sword Art Online: Hollow Realization Deluxe Edition", false)] // single-player
    [InlineData("Some Game (No Online)", false)]
    [InlineData("Onliner Tales", false)]          // not the word
    [InlineData("Offline Adventure", false)]
    [InlineData("It Takes Two Co-op", false)]      // Online only
    public void Online_in_the_title(string title, bool expected) =>
        Assert.Equal(expected, MultiplayerGames.IsOnlineTitle(title, Exceptions));

    [Fact]
    public void Exceptions_can_be_changed()
    {
        var none = MultiplayerGames.ParseExceptions("");
        Assert.True(MultiplayerGames.IsOnlineTitle("Some Game (No Online)", none));
        var custom = MultiplayerGames.ParseExceptions("elder scrolls online");
        Assert.False(MultiplayerGames.IsOnlineTitle("The Elder Scrolls Online", custom));
    }
}
