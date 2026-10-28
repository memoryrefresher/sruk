namespace SeaRaider.Core.Emulation;

/// <summary>
/// Original Xbox games, played in xemu (the original Xbox emulator), set up the way PS2 games are with PCSX2:
/// <list type="bullet">
///   <item>the xemu chosen under Settings › Emulators, an installed one, or SeaRaider's own: downloaded the first time an Xbox game
///         is played, from xemu's official releases on GitHub, as a portable folder (nothing installed in Windows);</item>
///   <item>set up so a game opens straight away, full screen: no welcome window, no update check, no boot animation;</item>
///   <item>the Xbox's own files: xemu needs the MCPX boot ROM and a flash BIOS dumped from your own Xbox, and a hard disk image.
///         They're Microsoft's, so SeaRaider can't download them. Chosen once in Settings › Emulators, they're set in xemu.</item>
/// </list>
/// Games are disc images in the Xbox's own format (XISO, .iso / .xiso); a plain disc dump has to be converted first.
/// </summary>
public static class Xemu
{
    private const string ReleasesApi = "https://api.github.com/repos/xemu-project/xemu/releases";

    /// <summary>SeaRaider's own xemu: %LOCALAPPDATA%\SeaRaider\xemu.</summary>
    public static string OwnFolder => Path.Combine(AppPaths.DataDirectory, "xemu");

    public static bool IsXemu(string? path) =>
        !string.IsNullOrEmpty(path) && Path.GetFileNameWithoutExtension(path).StartsWith("xemu", StringComparison.OrdinalIgnoreCase);

    private static string? FindExe(string folder) => GitHubEmulator.FindExe(folder, "xemu.exe", "xemu*.exe");

    /// <summary>xemu on this PC: SeaRaider's own, or an installed one (the usual folders); null if none.</summary>
    public static string? Locate()
    {
        foreach (var folder in new[]
                 {
                     OwnFolder,
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "xemu"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "xemu"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "xemu"),
                 })
            if (FindExe(folder) is { } exe) return exe;
        return null;
    }

    /// <summary>Gets xemu's newest release (64-bit Windows, portable) the first time an Xbox game is played. Returns its program.</summary>
    /// <exception cref="InvalidOperationException">It couldn't be downloaded or unpacked.</exception>
    public static async Task<string> InstallOwnAsync(HttpClient http, Abstractions.IArchiveExtractor extractor, CancellationToken ct)
    {
        var exe = await GitHubEmulator.InstallAsync(http, extractor, ReleasesApi,
            name => name.Contains("win", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("arm", StringComparison.OrdinalIgnoreCase) && !name.Contains("aarch64", StringComparison.OrdinalIgnoreCase),
            OwnFolder, FindExe, "xemu", "Settings › Emulators › Xbox", ct).ConfigureAwait(false);
        // Portable: a xemu.toml next to the program keeps its settings (and the paths SeaRaider sets) in its own folder.
        var portable = Path.Combine(Path.GetDirectoryName(exe)!, "xemu.toml");
        if (!File.Exists(portable)) File.WriteAllText(portable, string.Empty);
        return exe;
    }

    /// <summary>xemu's settings file: next to it when portable, else %APPDATA%\xemu\xemu\xemu.toml.</summary>
    private static string SettingsFile(string exe)
    {
        var portable = Path.Combine(Path.GetDirectoryName(exe)!, "xemu.toml");
        return File.Exists(portable)
            ? portable
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "xemu", "xemu", "xemu.toml");
    }

    /// <summary>
    /// Gets xemu ready to open a game straight away: the boot ROM, flash BIOS and hard disk chosen in Settings set as its files,
    /// full screen on start, and the welcome window, update check and boot animation switched off.
    /// </summary>
    /// <exception cref="InvalidOperationException">A file is missing: says which, and where to choose it.</exception>
    public static void Prepare(string exe, string? bootRom, string? flashRom, string? hardDisk)
    {
        static string Need(string? path, string what) =>
            !string.IsNullOrWhiteSpace(path) && File.Exists(path)
                ? path
                : throw new InvalidOperationException(
                    $"Original Xbox games need {what}. The Xbox's files are Microsoft's, so SeaRaider can't download them: dump them from " +
                    "your own Xbox and choose them under Settings › Emulators › Xbox, then press Play again.");

        var boot = Need(bootRom, "the MCPX boot ROM (mcpx_1.0.bin)");
        var flash = Need(flashRom, "a flash BIOS (for example Complex_4627.bin)");
        var hdd = Need(hardDisk, "a hard disk image (xbox_hdd.qcow2)");

        GitHubEmulator.SetToml(SettingsFile(exe), new Dictionary<string, Dictionary<string, string>>
        {
            ["general"] = new() { ["show_welcome"] = "false", ["skip_boot_anim"] = "true" },
            ["general.updates"] = new() { ["check"] = "false" },
            ["sys.files"] = new()
            {
                ["bootrom_path"] = GitHubEmulator.TomlString(boot),
                ["flashrom_path"] = GitHubEmulator.TomlString(flash),
                ["hdd_path"] = GitHubEmulator.TomlString(hdd),
            },
            ["display.window"] = new() { ["fullscreen_on_startup"] = "true" },
        });
    }
}
