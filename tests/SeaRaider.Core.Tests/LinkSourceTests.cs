using Microsoft.Extensions.Logging.Abstractions;
using SeaRaider.Core.Catalog;
using SeaRaider.Core.Data;
using SeaRaider.Core.Emulation;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Tests;

/// <summary>Sources › a game's own download link (Drive, MEGA, any host) as one store game.</summary>
public class LinkSourceTests
{
    private const string Drive = "https://drive.google.com/file/d/1AbCdEfGhIjKlMnOp/view?usp=sharing";

    [Theory]
    [InlineData(Drive, true)]
    [InlineData("drive.google.com/file/d/1AbCdEfGhIjKlMnOp/view", true)]
    [InlineData("https://mega.nz/file/AbCdEfGh#SomeKeyHere", true)]
    [InlineData("https://www.mediafire.com/file/abc123def/game.zip/file", true)]
    [InlineData("https://www.dropbox.com/scl/fi/abc123/Game.zip?rlkey=xyz", true)]
    [InlineData("https://example.com/files/Hollow_Knight.zip", true)]
    [InlineData("https://example.com/roms/Metroid Fusion.gba", false)] // a space: not an address the way it's typed
    [InlineData("https://example.com/roms/Metroid%20Fusion.gba", true)]
    [InlineData("https://fitgirl-repacks.site", false)]
    [InlineData("https://example.com/games/", false)]
    [InlineData("https://1337x.to/torrent/123/some-game/", false)] // a tracker page is never a download in itself
    [InlineData("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567", false)]
    [InlineData("", false)]
    public void A_games_link_is_told_from_a_site(string text, bool isLink) =>
        Assert.Equal(isLink, LinkSourceService.IsGameLink(text));

    [Fact]
    public void The_name_comes_from_the_file_else_from_the_host()
    {
        Assert.Equal("Hollow Knight", LinkSourceService.NameFrom(new Uri("https://example.com/files/Hollow_Knight.zip")));
        Assert.Equal("Celeste", LinkSourceService.NameFrom(new Uri("https://example.com/files/Celeste.7z.001")));
        Assert.Equal("Game from Google Drive", LinkSourceService.NameFrom(new Uri(Drive)));
    }

    [Fact]
    public void The_console_is_the_one_chosen_else_worked_out_else_pc()
    {
        static GameSource Source(string name, string url, string? system) => new() { DisplayName = name, Url = url, IsLink = true, LinkSystemId = system };

        Assert.Null(LinkSourceService.SystemFor(Source("Super Metroid", "https://example.com/metroid.sfc", LinkSourceService.PcId))); // PC chosen: wins
        Assert.Equal("ps2", LinkSourceService.SystemFor(Source("Hollow Knight", Drive, "ps2"))?.Id);                                  // console chosen: wins
        Assert.Equal("gba", LinkSourceService.SystemFor(Source("My Game", "https://example.com/My%20Game.gba", null))?.Id);          // from its file type
        Assert.Equal("ps2", LinkSourceService.SystemFor(Source("Gran Turismo 4 PS2", Drive, null))?.Id);                             // from its name
        Assert.Null(LinkSourceService.SystemFor(Source("Hollow Knight", Drive, null)));                                              // nothing says: PC games
    }

    [Fact]
    public void A_link_becomes_one_store_game_under_the_chosen_console_or_pc_and_can_be_renamed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "searaider-test-" + Guid.NewGuid().ToString("N"));
        using var repository = new LiteDbLibraryRepository(Path.Combine(dir, "library.db"));
        try
        {
            var service = new LinkSourceService(repository, null!, NullLogger<LinkSourceService>.Instance);

            // A PC game with a name typed in.
            var (source, game) = service.Add(Drive, "  Hollow   Knight ", LinkSourceService.PcId);
            Assert.True(source.IsLink);
            Assert.Equal("Hollow Knight", source.DisplayName);
            Assert.Equal("Hollow Knight", game.Title);
            Assert.Equal(source.Id, game.SourceId);
            var link = Assert.Single(game.DownloadLinks);
            Assert.Equal(DownloadLinkKind.FileHost, link.Kind);
            Assert.Equal("Google Drive", link.Mirror);
            Assert.Null(CatalogEntry.From(repository.GetGame(game.Id)!).SystemId); // PC games

            // The same link twice is refused.
            Assert.Throws<InvalidOperationException>(() => service.Add(Drive, null, null));

            // Filed under a console instead: genres name it, and the store files it there; the name is kept.
            var ps2 = GameSystems.Find("ps2")!;
            var moved = service.Update(source, source.DisplayName, ps2.Id);
            Assert.Equal(game.Id, moved.Id); // the same store entry
            Assert.Contains(ps2.Name, moved.Genres);
            Assert.Equal("ps2", CatalogEntry.From(repository.GetGame(game.Id)!).SystemId);
            Assert.Equal("Hollow Knight", repository.GetGame(game.Id)!.Title);

            // Renamed: the store entry is renamed; a blank name works it out from the link again.
            service.Update(source, "Hollow Knight: Silksong", ps2.Id);
            Assert.Equal("Hollow Knight: Silksong", repository.GetGame(game.Id)!.Title);
            service.Update(source, null, ps2.Id);
            Assert.Equal("Game from Google Drive", repository.GetGame(game.Id)!.Title);
            Assert.False(source.LinkNameChosen);

            // One source, one game: removing the source removes the game.
            Assert.Equal(1, repository.DeleteGamesForSource(source.Id));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
