using SeaRaider.Core.Launching;

namespace SeaRaider.Core.Tests;

/// <summary>Library › Add a game / Add a ROM: the name and console worked out from the file the user picks.</summary>
public class LibraryImportTests
{
    private static string P(params string[] parts) => Path.Combine([Path.GetPathRoot(Path.GetTempPath())!, .. parts]);

    [Fact]
    public void A_pc_game_is_named_after_its_folder_skipping_layout_folders()
    {
        Assert.Equal("Hollow Knight", LibraryImportService.PcTitle(P("Games", "Hollow Knight", "hollow_knight.exe")));
        Assert.Equal("Satisfactory", LibraryImportService.PcTitle(P("Games", "Satisfactory", "Binaries", "Win64", "game.exe")));
        Assert.Equal("Celeste", LibraryImportService.PcTitle(P("Games", "Celeste", "bin", "Celeste.exe")));
    }

    [Fact]
    public void The_console_comes_from_the_file_type_or_its_folders()
    {
        Assert.Equal("gba", LibraryImportService.SystemOf(P("Roms", "Metroid Fusion (USA).gba"))?.Id);
        Assert.Equal("ps2", LibraryImportService.SystemOf(P("Roms", "PS2", "Gran Turismo 4.iso"))?.Id);
        Assert.Equal("ps3", LibraryImportService.SystemOf(P("PS3", "God of War III", "PS3_GAME", "USRDIR", "EBOOT.BIN"))?.Id);
        Assert.Null(LibraryImportService.SystemOf(P("Downloads", "game.iso"))); // a disc image could be anything: the user picks
    }
}
