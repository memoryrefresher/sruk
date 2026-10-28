using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SeaRaider.Core.Emulation;

/// <summary>
/// PlayStation 3 games, played in RPCS3 (the PS3 emulator), set up the way PS2 games are with PCSX2:
/// <list type="bullet">
///   <item>the RPCS3 chosen under Settings › Emulators, an installed one, or SeaRaider's own: downloaded the first time a PS3 game
///         is played, from RPCS3's official Windows builds on GitHub (RPCS3 is portable: nothing installed in Windows);</item>
///   <item>the PS3 system software (firmware), which RPCS3 needs once. Sony gives it away on playstation.com: SeaRaider uses the
///         PS3UPDAT.PUP chosen in Settings, or gets it from Sony's own update server, and has RPCS3 install it.</item>
/// </list>
/// Games are disc dumps (a folder with PS3_GAME\USRDIR\EBOOT.BIN, started from that file) or .iso images.
/// </summary>
public static partial class Rpcs3
{
    private const string ReleasesApi = "https://api.github.com/repos/RPCS3/rpcs3-binaries-win/releases";

    /// <summary>Sony's list of the current PS3 system software (it names the PS3UPDAT.PUP to download).</summary>
    private const string FirmwareList = "https://fus01.ps3.update.playstation.net/update/ps3/list/us/ps3-updatelist.txt";

    [GeneratedRegex(@"CDN=(?<url>https?://[^;\s]+PS3UPDAT\.PUP)", RegexOptions.IgnoreCase)]
    private static partial Regex FirmwareUrlRegex();

    /// <summary>SeaRaider's own RPCS3: %LOCALAPPDATA%\SeaRaider\RPCS3.</summary>
    public static string OwnFolder => Path.Combine(AppPaths.DataDirectory, "RPCS3");

    public static bool IsRpcs3(string? path) =>
        !string.IsNullOrEmpty(path) && Path.GetFileNameWithoutExtension(path).StartsWith("rpcs3", StringComparison.OrdinalIgnoreCase);

    private static string? FindExe(string folder) => GitHubEmulator.FindExe(folder, "rpcs3.exe");

    /// <summary>RPCS3 on this PC: SeaRaider's own, or one in the usual folders; null if none.</summary>
    public static string? Locate()
    {
        foreach (var folder in new[]
                 {
                     OwnFolder,
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RPCS3"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "RPCS3"),
                     Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", "RPCS3"),
                 })
            if (FindExe(folder) is { } exe) return exe;
        return null;
    }

    /// <summary>Gets RPCS3's newest Windows build the first time a PS3 game is played. Returns its program.</summary>
    /// <exception cref="InvalidOperationException">It couldn't be downloaded or unpacked.</exception>
    public static Task<string> InstallOwnAsync(HttpClient http, Abstractions.IArchiveExtractor extractor, CancellationToken ct) =>
        GitHubEmulator.InstallAsync(http, extractor, ReleasesApi,
            name => name.Contains("win64", StringComparison.OrdinalIgnoreCase) || name.Contains("win_x64", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("windows", StringComparison.OrdinalIgnoreCase) && !name.Contains("arm", StringComparison.OrdinalIgnoreCase),
            OwnFolder, FindExe, "RPCS3", "Settings › Emulators › PlayStation 3", ct);

    /// <summary>The PS3 system software is installed in this RPCS3 (its dev_flash holds the system menu).</summary>
    public static bool FirmwareInstalled(string exe) =>
        File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "dev_flash", "vsh", "module", "vsh.self"));

    /// <summary>
    /// Makes sure RPCS3 has the PS3 system software. When it hasn't, the PS3UPDAT.PUP chosen in Settings (or the one from Sony's
    /// update server) is handed to RPCS3 to install, and the user is told to press Play again once it's done.
    /// </summary>
    /// <exception cref="InvalidOperationException">The firmware is being installed now, or couldn't be got: says what to do.</exception>
    public static async Task PrepareAsync(HttpClient http, string exe, string? chosenFirmware, CancellationToken ct)
    {
        if (FirmwareInstalled(exe)) return;

        var pup = !string.IsNullOrWhiteSpace(chosenFirmware) && File.Exists(chosenFirmware)
            ? chosenFirmware
            : await DownloadFirmwareAsync(http, ct).ConfigureAwait(false);

        try
        {
            Process.Start(new ProcessStartInfo(exe, $"--installfw \"{pup}\"")
            {
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false,
            })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"RPCS3 couldn't be started to install the PS3 system software: {ex.Message}", ex);
        }
        throw new InvalidOperationException(
            "RPCS3 is installing the PS3 system software (needed once). When it says it's finished, close RPCS3 and press Play again.");
    }

    /// <summary>The current PS3UPDAT.PUP from Sony's own update server, kept in SeaRaider's RPCS3 folder.</summary>
    private static async Task<string> DownloadFirmwareAsync(HttpClient http, CancellationToken ct)
    {
        var target = Path.Combine(OwnFolder, "firmware", "PS3UPDAT.PUP");
        if (LooksLikeFirmware(target)) return target;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            var list = await http.GetStringAsync(FirmwareList, ct).ConfigureAwait(false);
            var url = FirmwareUrlRegex().Match(list) is { Success: true } m
                ? m.Groups["url"].Value
                : throw new InvalidOperationException("Sony's update list had no PS3UPDAT.PUP");
            var partial = target + ".download";
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var file = File.Create(partial);
                await response.Content.CopyToAsync(file, ct).ConfigureAwait(false);
            }
            if (!LooksLikeFirmware(partial)) throw new InvalidOperationException("the file from Sony's server isn't PS3 system software");
            File.Move(partial, target, overwrite: true);
            return target;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException
                                       or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"PS3 games need the PS3 system software once, and SeaRaider couldn't get it from Sony ({ex.Message}). Download " +
                "PS3UPDAT.PUP from playstation.com (PS3 system software update), choose it under Settings › Emulators › PlayStation 3, " +
                "then press Play again.", ex);
        }
    }

    /// <summary>A PS3 update file starts with "SCEUF".</summary>
    internal static bool LooksLikeFirmware(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length < 10 * 1024 * 1024) return false;
            using var file = File.OpenRead(path);
            Span<byte> head = stackalloc byte[5];
            return file.Read(head) == 5 && head.SequenceEqual("SCEUF"u8);
        }
        catch (IOException)
        {
            return false;
        }
    }
}
