using SeaRaider.Core.Emulation;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Tests;

/// <summary>Console games: which system a title is for, which file to start, and how the emulator is called.</summary>
public class EmulationTests
{
    [Theory]
    [InlineData("Super Mario World (SNES)", "snes")]
    [InlineData("Super NES - Chrono Trigger", "snes")]
    [InlineData("Legend of Zelda (NES)", "nes")]
    [InlineData("Game Boy Advance - Metroid Fusion", "gba")]
    [InlineData("Pokemon Crystal Game Boy Color", "gbc")]
    [InlineData("Tetris Game Boy", "gb")]
    [InlineData("Gran Turismo 4 PS2", "ps2")]
    [InlineData("Final Fantasy VII Sony PlayStation", "ps1")]
    [InlineData("https://roms.example/nintendo-64/goldeneye-007/", "n64")]
    [InlineData("Mario Kart Wii U", "wiiu")]
    [InlineData("Mario Kart Wii", "wii")]
    [InlineData("Sonic The Hedgehog Sega Genesis", "genesis")]
    [InlineData("Halo 2 (Original Xbox)", "xbox")]
    [InlineData("Fable - Microsoft Xbox", "xbox")]
    [InlineData("Halo 3 Xbox 360", null)]
    [InlineData("Supports Xbox controllers", null)]
    [InlineData("Genesis Noir", null)]
    [InlineData("Nesting Doll Simulator", null)]
    [InlineData("Hollow Depths", null)]
    public void The_system_comes_from_whole_words_in_the_text(string text, string? id) => Assert.Equal(id, GameSystems.FromText(text)?.Id);

    [Theory]
    [InlineData(".nes", "nes")]
    [InlineData(".GBA", "gba")]
    [InlineData(".nsp", "switch")]
    [InlineData(".xiso", "xbox")]
    [InlineData(".iso", null)]   // a disc image could be anything: the page has to name the system
    [InlineData(".zip", null)]
    [InlineData(".md", null)]
    public void Only_a_file_type_that_names_its_system_counts_on_its_own(string extension, string? id) =>
        Assert.Equal(id, GameSystems.FromExtension(extension)?.Id);

    private static string TempFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "searaider-rom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void A_cartridge_file_is_found_without_the_page_naming_the_system()
    {
        var dir = TempFolder();
        try
        {
            File.WriteAllText(Path.Combine(dir, "readme.txt"), "x");
            File.WriteAllText(Path.Combine(dir, "Game.gba"), "rom");

            var match = RomDetector.Detect(dir, "Some Game");

            Assert.NotNull(match);
            Assert.Equal("gba", match!.System.Id);
            Assert.EndsWith("Game.gba", match.Path);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_disc_image_needs_the_system_from_the_page_and_the_cue_beats_the_bin()
    {
        var dir = TempFolder();
        try
        {
            File.WriteAllText(Path.Combine(dir, "Game.bin"), new string('x', 100));
            File.WriteAllText(Path.Combine(dir, "Game.cue"), "FILE");

            Assert.Null(RomDetector.Detect(dir, "Some Game"));

            var match = RomDetector.Detect(dir, "Some Game - PlayStation");
            Assert.NotNull(match);
            Assert.Equal("ps1", match!.System.Id);
            Assert.EndsWith("Game.cue", match.Path);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Updates_and_dlc_folders_are_never_the_game()
    {
        var dir = TempFolder();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "Update"));
            File.WriteAllText(Path.Combine(dir, "Update", "patch.nsp"), "x");
            File.WriteAllText(Path.Combine(dir, "Game.nsp"), "xxxx");

            Assert.EndsWith("Game.nsp", RomDetector.Detect(dir, null)!.Path);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("Original Xbox", "xbox")]
    [InlineData("Microsoft Xbox Redump", "xbox")]
    [InlineData("Xbox", "xbox")]
    [InlineData("Xbox 360", null)]
    [InlineData("Xbox One", null)]
    public void A_torrent_named_Xbox_is_the_original_Xbox_but_not_its_successors(string name, string? id) =>
        Assert.Equal(id, SeaRaider.Core.Catalog.TorrentSourceService.ConsoleIn(name)?.Id);

    [Fact]
    public void A_PS3_disc_dump_starts_from_its_EBOOT()
    {
        var dir = TempFolder();
        try
        {
            var usrdir = Path.Combine(dir, "Game [BLUS30001]", "PS3_GAME", "USRDIR");
            Directory.CreateDirectory(usrdir);
            File.WriteAllText(Path.Combine(usrdir, "EBOOT.BIN"), "x");
            File.WriteAllText(Path.Combine(dir, "Game [BLUS30001]", "PS3_GAME", "PARAM.SFO"), "x");
            var match = RomDetector.Detect(dir, "Some Game");
            Assert.NotNull(match);
            Assert.Equal("ps3", match!.System.Id);
            Assert.EndsWith(Path.Combine("USRDIR", "EBOOT.BIN"), match.Path);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Xemu_opens_the_disc_full_screen()
    {
        var xbox = GameSystems.Find("xbox")!;
        Assert.Equal("-full-screen -dvd_path \"C:\\Roms\\Halo.iso\"", EmulatorCommand.Build(@"C:\Emu\xemu.exe", null, xbox, @"C:\Roms\Halo.iso").Arguments);
    }

    [Fact]
    public void Known_emulators_open_the_game_full_screen_and_others_just_get_the_file()
    {
        var snes = GameSystems.Find("snes")!;
        var ps2 = GameSystems.Find("ps2")!;

        Assert.Equal("-batch -fullscreen -- \"C:\\Roms\\Game.iso\"", EmulatorCommand.Build(@"C:\Emu\pcsx2-qt.exe", null, ps2, @"C:\Roms\Game.iso").Arguments);
        Assert.Equal("\"C:\\Roms\\Game.sfc\"", EmulatorCommand.Build(@"C:\Emu\snes9x-x64.exe", null, snes, @"C:\Roms\Game.sfc").Arguments);
        var retroArch = EmulatorCommand.Build(@"C:\RA\retroarch.exe", null, snes, @"C:\Roms\Game.sfc").Arguments;
        Assert.StartsWith("--appendconfig ", retroArch); // sound on (SeaRaider's RetroArch settings)
        Assert.EndsWith("-f -L \"C:\\RA\\cores\\snes9x_libretro.dll\" \"C:\\Roms\\Game.sfc\"", retroArch);
    }

    [Fact]
    public void A_custom_command_line_replaces_the_default_and_fills_in_the_placeholders()
    {
        var launch = EmulatorCommand.Build(@"C:\Emu\x.exe", "--fs {rom} --dir {romdir}", GameSystems.Find("nes")!, @"C:\Roms\Game.nes");

        Assert.Equal(@"--fs C:\Roms\Game.nes --dir C:\Roms", launch.Arguments);
        Assert.Equal(@"C:\Emu\x.exe", launch.FileName);
    }

    [Fact]
    public void The_finder_matches_emulators_to_systems_and_falls_back_to_RetroArch()
    {
        var found = EmulatorFinder.Match([@"C:\E\Dolphin.exe", @"C:\E\DolphinTool.exe", @"C:\E\retroarch.exe", @"C:\E\unins000.exe"],
            preferRetroArch: false);

        Assert.Equal(@"C:\E\Dolphin.exe", found["wii"]);
        Assert.Equal(@"C:\E\Dolphin.exe", found["gc"]);
        Assert.Equal(@"C:\E\retroarch.exe", found["nes"]);   // nothing better: RetroArch has a core for it
        Assert.False(found.ContainsKey("ps3"));              // RetroArch has no PS3 core
    }

    [Fact]
    public void Store_entries_know_their_system_and_PC_games_have_none()
    {
        Assert.Equal("snes", new CatalogEntry { Title = "Chrono Trigger (SNES)" }.SystemId);
        Assert.Equal("gba", new CatalogEntry { Title = "Metroid", Genres = ["Game Boy Advance"] }.SystemId);
        Assert.Null(new CatalogEntry { Title = "Hollow Depths", Genres = ["Action"] }.SystemId);
    }

    [Fact]
    public void RetroArch_is_the_default_for_every_system_it_has_a_core_for()
    {
        var found = EmulatorFinder.Match([@"C:\E\Dolphin.exe", @"C:\E\retroarch.exe", @"C:\E\rpcs3.exe"]);

        Assert.Equal(@"C:\E\retroarch.exe", found["wii"]);   // RetroArch over Dolphin
        Assert.Equal(@"C:\E\retroarch.exe", found["snes"]);
        Assert.Equal(@"C:\E\retroarch.exe", found["ps2"]);
        Assert.Equal(@"C:\E\rpcs3.exe", found["ps3"]);      // no RetroArch core: its own emulator
        Assert.Equal(@"C:\E\cores\snes9x_libretro.dll", RetroArch.CorePath(@"C:\E\retroarch.exe", "snes9x"));
    }

    [Theory]
    [InlineData("https://roms.example.test/roms/sega-cd/sonic-cd", "segacd")]
    [InlineData("https://roms.example.test/roms/neo-geo-pocket-color/metal-slug", "ngp")]
    [InlineData("https://roms.example.test/roms/nintendo-64/super-mario-64", "n64")]
    [InlineData("https://roms.example.test/roms/gameboy-advance/metroid-fusion", "gba")]
    public void Rom_site_addresses_name_their_system(string url, string id) =>
        Assert.Equal(id, new CatalogEntry { Title = "Game", PageUrl = url }.SystemId);
}
