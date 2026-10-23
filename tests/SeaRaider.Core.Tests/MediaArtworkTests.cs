using SeaRaider.Core.Catalog;
using SeaRaider.Core.Scraping;

namespace SeaRaider.Core.Tests;

public class MediaArtworkTests
{
    [Theory]
    [InlineData("LIVE 57729 Bayern Munich vs Arsenal Football | 7:00 PM", "Bayern Munich vs Arsenal", "Football", true)]
    [InlineData("LIVE Sporting CP vs LASK Linz Football | 6:55 PM", "Sporting CP vs LASK Linz", "Football", true)]
    [InlineData("Oct 22 494 The Golazo Show Football", "The Golazo Show", "Football", false)]
    [InlineData("Lakers vs Celtics Basketball", "Lakers vs Celtics", "Basketball", false)]
    [InlineData("300 Rise of an Empire", "300 Rise of an Empire", null, false)]
    [InlineData("1917", "1917", null, false)]
    public void Sports_cards_are_tidied_to_the_match(string text, string title, string? sport, bool live)
    {
        Assert.Equal(title, MediaTitleParser.Tidy(text, out var category, out var isLive));
        Assert.Equal(sport, category);
        Assert.Equal(live, isLive);
    }

    [Fact]
    public void Matches_have_two_sides()
    {
        Assert.Equal(("Bayern Munich", "Arsenal"), MediaTitleParser.Matchup("Bayern Munich vs Arsenal")!.Value);
        Assert.Equal(("Lakers", "Celtics"), MediaTitleParser.Matchup("Lakers @ Celtics")!.Value);
        Assert.Null(MediaTitleParser.Matchup("The Golazo Show"));
    }

    [Theory]
    [InlineData("The Office (US)", "The Office", true)]
    [InlineData("Alphaville", "Alpha", false)]
    [InlineData("Dune: Part Two", "Dune Part Two", true)]
    public void Names_match_only_when_they_are_the_same_title(string found, string wanted, bool same) =>
        Assert.Equal(same, MediaArtworkService.SameName(found, wanted));

    [Theory]
    [InlineData("FC Bayern Munich", "Bayern Munich", true)]
    [InlineData("RC Lens", "Lens", true)]
    [InlineData("Arsenal F.C.", "Arsenal", true)]
    [InlineData("Lens (optics)", "Arsenal", false)]
    public void Team_articles_must_name_the_team(string article, string team, bool ok) =>
        Assert.Equal(ok, MediaArtworkService.ContainsName(article, team));
}
