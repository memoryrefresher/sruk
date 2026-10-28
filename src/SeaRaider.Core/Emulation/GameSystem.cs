namespace SeaRaider.Core.Emulation;

/// <summary>
/// A game console (or other platform an emulator runs): how to recognise its games and which emulators play them.
/// </summary>
/// <param name="Id">Stable key stored with a game and in the emulator settings ("snes", "ps2").</param>
/// <param name="Name">Shown in the store filter, the library and Settings ("Super Nintendo").</param>
/// <param name="Aliases">Names a page uses for it, lower case with plain spaces ("nintendo 64", "n64"). Whole words only.</param>
/// <param name="Extensions">Game file types this system's emulators open (with the dot). Disc formats shared by several systems
/// (.iso, .cue, .chd) only count once the page or title names the system.</param>
/// <param name="Emulators">File names (without .exe, lower case) of emulators for it, best first: used to find them on the PC.</param>
/// <param name="RetroArchCore">The libretro core RetroArch uses for it, when RetroArch can play it (the file is cores\{core}_libretro.dll).</param>
public sealed record GameSystem(string Id, string Name, string[] Aliases, string[] Extensions, string[] Emulators, string? RetroArchCore = null)
{
    public override string ToString() => Name;
}

/// <summary>The consoles SeaRaider knows. The order matters for recognition: more specific names come before general ones (Super NES before NES, PlayStation 2 before PlayStation).</summary>
public static class GameSystems
{
    public static readonly IReadOnlyList<GameSystem> All =
    [
        new("snes", "Super Nintendo (SNES)", ["snes", "super nes", "super nintendo", "super famicom", "sfc"],
            [".sfc", ".smc", ".fig", ".swc"], ["snes9x", "bsnes", "higan", "mesen"], "snes9x"),
        new("nes", "Nintendo (NES)", ["nes", "famicom", "nintendo entertainment system"],
            [".nes", ".unf", ".unif", ".fds"], ["mesen", "fceux", "nestopia", "punes"], "fceumm"),
        new("n64", "Nintendo 64", ["n64", "nintendo 64", "nintendo64"],
            [".z64", ".n64", ".v64"], ["project64", "simple64", "m64p"], "mupen64plus_next"),
        new("gba", "Game Boy Advance", ["game boy advance", "gameboy advance", "game boy advanced", "gameboy advanced", "gba"],
            [".gba"], ["mgba", "visualboyadvance", "vbam", "nanoboyadvance"], "mgba"),
        new("gbc", "Game Boy Color", ["game boy color", "gameboy color", "gbc"],
            [".gbc"], ["mgba", "sameboy", "visualboyadvance", "vbam", "bgb"], "gambatte"),
        new("gb", "Game Boy", ["game boy", "gameboy"],
            [".gb"], ["mgba", "sameboy", "visualboyadvance", "vbam", "bgb"], "gambatte"),
        new("3ds", "Nintendo 3DS", ["3ds", "nintendo 3ds"],
            [".3ds", ".cia", ".cci", ".cxi", ".3dsx"], ["azahar", "lime3ds", "citra-qt", "citra"], "citra"),
        new("dsi", "Nintendo DSi", ["nintendo dsi", "dsi", "dsiware"],
            [".dsi"], ["melonds"], "melonds"),
        new("nds", "Nintendo DS", ["nds", "nintendo ds"],
            [".nds"], ["melonds", "desmume", "drastic"], "melonds"),
        new("wiiu", "Wii U", ["wii u", "wiiu"],
            [".wud", ".wux", ".rpx", ".wua"], ["cemu"], null),
        new("wii", "Wii", ["wii", "nintendo wii"],
            [".wbfs", ".iso", ".rvz", ".wia", ".ciso", ".gcz"], ["dolphin"], "dolphin"),
        new("gc", "GameCube", ["gamecube", "game cube", "ngc", "nintendo gamecube"],
            [".gcm", ".iso", ".rvz", ".gcz", ".ciso"], ["dolphin"], "dolphin"),
        new("switch", "Nintendo Switch", ["nintendo switch", "switch rom", "nsw"],
            [".nsp", ".xci", ".nsz", ".xcz"], ["ryujinx", "yuzu", "suyu", "sudachi", "citron"], null),
        new("ps2", "PlayStation 2", ["playstation 2", "ps2", "sony playstation 2"],
            [".iso", ".bin", ".chd", ".cso", ".gz", ".mdf", ".cue", ".m3u"], ["pcsx2"], "pcsx2"),
        new("ps3", "PlayStation 3", ["playstation 3", "ps3", "sony playstation 3"],
            [".iso"], ["rpcs3"], null), // disc dumps start from PS3_GAME\USRDIR\EBOOT.BIN (see RomDetector)
        // The original Xbox (not 360 / One / Series: their names never match these). Games are XISO images, played in xemu.
        new("xbox", "Xbox (original)", ["original xbox", "xbox original", "xbox classic", "og xbox", "xbox og", "microsoft xbox", "xbox xiso", "xiso"],
            [".xiso", ".iso"], ["xemu"], null),
        new("psp", "PlayStation Portable", ["psp", "playstation portable"],
            [".cso", ".pbp", ".iso", ".chd"], ["ppsspp"], "ppsspp"),
        new("ps1", "PlayStation", ["playstation", "playstation 1", "ps1", "psx", "psone", "ps one", "sony playstation"],
            [".cue", ".bin", ".chd", ".img", ".mdf", ".ccd", ".iso", ".m3u", ".pbp"], ["duckstation", "mednafen"], "pcsx_rearmed"),
        new("segacd", "Sega CD / Mega CD", ["sega cd", "mega cd", "segacd", "megacd", "sega mega cd"],
            [".cue", ".chd", ".iso", ".bin"], ["genesis_plus_gx", "kega"], "genesis_plus_gx"),
        new("32x", "Sega 32X", ["sega 32x", "32x", "genesis 32x", "mega drive 32x"],
            [".32x"], ["kega"], "picodrive"),
        new("genesis", "Sega Genesis / Mega Drive", ["sega genesis", "mega drive", "megadrive", "sega mega drive", "sega megadrive", "genesis rom"],
            [".gen", ".smd", ".bin", ".md"], ["gens", "kega", "blastem", "mesen"], "genesis_plus_gx"),
        new("sms", "Sega Master System", ["master system", "sega master system", "mastersystem"],
            [".sms"], ["mesen", "emulicious", "kega"], "genesis_plus_gx"),
        new("gamegear", "Sega Game Gear", ["game gear", "gamegear", "sega game gear"],
            [".gg"], ["mesen", "emulicious", "kega"], "genesis_plus_gx"),
        new("saturn", "Sega Saturn", ["sega saturn", "saturn rom"],
            [".cue", ".chd", ".iso", ".bin", ".mdf"], ["mednafen", "yabause", "ymir"], "mednafen_saturn"),
        new("dreamcast", "Sega Dreamcast", ["dreamcast", "sega dreamcast"],
            [".gdi", ".cdi", ".chd", ".cue", ".iso"], ["flycast", "redream"], "flycast"),
        new("pce", "TurboGrafx-16 / PC Engine", ["turbografx", "turbografx 16", "pc engine", "pcengine"],
            [".pce", ".sgx", ".cue", ".chd"], ["mesen", "ootake"], "mednafen_pce_fast"),
        new("atari2600", "Atari 2600", ["atari 2600", "atari2600"],
            [".a26"], ["stella"], "stella"),
        new("atari5200", "Atari 5200", ["atari 5200", "atari5200"],
            [".a52"], ["altirra"], "a5200"),
        new("atari7800", "Atari 7800", ["atari 7800", "atari7800"],
            [".a78"], ["prosystem"], "prosystem"),
        new("lynx", "Atari Lynx", ["atari lynx", "lynx"],
            [".lnx"], ["mednafen", "handy"], "handy"),
        new("jaguar", "Atari Jaguar", ["atari jaguar", "jaguar cd"],
            [".j64", ".jag"], ["bigpemu"], "virtualjaguar"),
        new("vb", "Virtual Boy", ["virtual boy", "virtualboy", "nintendo virtual boy"],
            [".vb", ".vboy"], ["mednafen"], "mednafen_vb"),
        new("ngp", "Neo Geo Pocket", ["neo geo pocket", "neogeo pocket", "neo geo pocket color", "ngpc"],
            [".ngp", ".ngc"], ["mednafen"], "mednafen_ngp"),
        new("wonderswan", "WonderSwan", ["wonderswan", "wonder swan", "wonderswan color"],
            [".ws", ".wsc"], ["mednafen"], "mednafen_wswan"),
        new("3do", "3DO", ["3do", "panasonic 3do", "3do interactive multiplayer"],
            [".iso", ".chd", ".cue"], ["4do"], "opera"),
        new("colecovision", "ColecoVision", ["colecovision", "coleco vision"],
            [".col"], ["bluemsx"], "gearcoleco"),
        new("intellivision", "Intellivision", ["intellivision", "mattel intellivision"],
            [".int"], ["jzintv"], "freeintv"),
        new("vectrex", "Vectrex", ["vectrex"],
            [".vec"], ["parajve"], "vecx"),
    ];

    /// <summary>File types only one system uses and that are not also ordinary files: they name their system by themselves.</summary>
    private static readonly Lazy<Dictionary<string, GameSystem>> UniqueExtensions = new(() =>
    {
        var map = new Dictionary<string, List<GameSystem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var system in All)
            foreach (var ext in system.Extensions)
            {
                if (!map.TryGetValue(ext, out var list)) map[ext] = list = [];
                list.Add(system);
            }
        return map.Where(p => p.Value.Count == 1 && !NeedsAName.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value[0], StringComparer.OrdinalIgnoreCase);
    });

    /// <summary>Formats that are also ordinary files (archives, disc images, Mega Drive ".md") or shared by several systems.</summary>
    private static readonly HashSet<string> NeedsAName = new(StringComparer.OrdinalIgnoreCase)
        { ".iso", ".bin", ".cue", ".chd", ".img", ".mdf", ".ccd", ".m3u", ".gz", ".zip", ".md", ".pbp", ".cso", ".rvz", ".gcz", ".ciso" };

    /// <summary>Who made each console, for grouping them (the store's console buttons). Anything not listed is "Other".</summary>
    private static readonly Dictionary<string, string> Makers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["snes"] = "Nintendo", ["nes"] = "Nintendo", ["n64"] = "Nintendo", ["gba"] = "Nintendo", ["gbc"] = "Nintendo", ["gb"] = "Nintendo",
        ["3ds"] = "Nintendo", ["dsi"] = "Nintendo", ["nds"] = "Nintendo", ["wiiu"] = "Nintendo", ["wii"] = "Nintendo", ["gc"] = "Nintendo",
        ["switch"] = "Nintendo", ["vb"] = "Nintendo",
        ["ps1"] = "Sony", ["ps2"] = "Sony", ["ps3"] = "Sony", ["psp"] = "Sony",
        ["xbox"] = "Microsoft",
        ["segacd"] = "Sega", ["32x"] = "Sega", ["genesis"] = "Sega", ["sms"] = "Sega", ["gamegear"] = "Sega", ["saturn"] = "Sega",
        ["dreamcast"] = "Sega",
        ["atari2600"] = "Atari", ["atari5200"] = "Atari", ["atari7800"] = "Atari", ["lynx"] = "Atari", ["jaguar"] = "Atari",
    };

    /// <summary>The makers in the order they're shown.</summary>
    public static IReadOnlyList<string> MakerOrder { get; } = ["Nintendo", "Sony", "Microsoft", "Sega", "Atari", "Other"];

    /// <summary>Who made the console ("Nintendo", "Sony", "Microsoft", "Sega", "Atari"), or "Other".</summary>
    public static string MakerOf(string? id) => id is not null && Makers.TryGetValue(id, out var maker) ? maker : "Other";

    public static GameSystem? Find(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The system a file type belongs to, when only one system uses it and nothing else does (".nes", ".gba", ".nsp").</summary>
    public static GameSystem? FromExtension(string? extension) =>
        !string.IsNullOrEmpty(extension) && UniqueExtensions.Value.TryGetValue(extension, out var system) ? system : null;

    /// <summary>File types of game files found by their own extension: safe to offer as direct downloads from a page.</summary>
    public static IReadOnlyCollection<string> DirectDownloadExtensions => UniqueExtensions.Value.Keys;

    /// <summary>
    /// The system a title, genre, tag or page address names ("Super Mario World (SNES)", ".../roms/nintendo-64/..."), or null.
    /// Whole words only, so "Genesis Noir" or "Nesting" are not consoles.
    /// </summary>
    public static GameSystem? FromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var padded = " " + Normalize(text) + " ";
        foreach (var system in All)
            foreach (var alias in system.Aliases)
                if (padded.Contains(" " + alias + " ", StringComparison.Ordinal)) return system;
        return null;
    }

    /// <summary>Lower case, every run of punctuation becomes one space ("Nintendo-64 (USA)" → "nintendo 64 usa").</summary>
    internal static string Normalize(string text)
    {
        var chars = new char[text.Length];
        var length = 0;
        var space = true;
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                chars[length++] = char.ToLowerInvariant(c);
                space = false;
            }
            else if (!space)
            {
                chars[length++] = ' ';
                space = true;
            }
        }
        return new string(chars, 0, length).Trim();
    }
}
