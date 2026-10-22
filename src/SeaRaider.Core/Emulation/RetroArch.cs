using System.IO.Compression;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Emulation;

/// <summary>
/// RetroArch as the one emulator for every system it plays (Settings › Emulators › "Use RetroArch for every system it plays",
/// on by default): where it is, which core each system uses, and getting a missing core from libretro's own build server the
/// first time a game needs it (the same files RetroArch's Online Updater › Core Downloader fetches).
/// </summary>
public static class RetroArch
{
    /// <summary>libretro's nightly Windows cores: {core}_libretro.dll.zip.</summary>
    private const string CoreServer = "https://buildbot.libretro.com/nightly/windows/x86_64/latest/";

    public static bool IsRetroArch(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.GetFileNameWithoutExtension(path).StartsWith("retroarch", StringComparison.OrdinalIgnoreCase);

    /// <summary>cores\{core}_libretro.dll next to retroarch.exe.</summary>
    public static string CorePath(string retroArchExe, string core) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(retroArchExe)) ?? string.Empty, "cores", core + "_libretro.dll");

    public static bool HasCore(string retroArchExe, GameSystem system) =>
        system.RetroArchCore is { } core && File.Exists(CorePath(retroArchExe, core));

    /// <summary>Where RetroArch's installer and the portable download usually put it.</summary>
    public static IEnumerable<string> UsualPlaces()
    {
        var drive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";
        yield return Path.Combine(drive, "RetroArch-Win64", "retroarch.exe");
        yield return Path.Combine(drive, "RetroArch", "retroarch.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RetroArch", "retroarch.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RetroArch", "retroarch.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "RetroArch", "retroarch.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common", "RetroArch", "retroarch.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam", "steamapps", "common", "RetroArch", "retroarch.exe");
    }

    /// <summary>
    /// RetroArch on this PC: the one set in Settings, else one already chosen for any system, else the usual install places,
    /// else a scan of the emulator folders (<see cref="EmulatorFinder"/>). Null when it isn't installed.
    /// </summary>
    public static string? Locate(AppSettings settings)
    {
        if (settings.RetroArchPath is { Length: > 0 } set && File.Exists(set)) return set;
        if (settings.Emulators.Select(e => e.ExePath).FirstOrDefault(p => IsRetroArch(p) && File.Exists(p)) is { } chosen) return chosen;
        if (UsualPlaces().FirstOrDefault(File.Exists) is { } usual) return usual;
        try
        {
            return EmulatorFinder.FindRetroArch(EmulatorFinder.DefaultFolders(settings.LibraryRoot));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Makes sure RetroArch has the system's core, downloading it from libretro's build server when it's missing.
    /// </summary>
    /// <exception cref="InvalidOperationException">The core couldn't be got (offline, or RetroArch's folder can't be written).</exception>
    public static async Task EnsureCoreAsync(HttpClient http, string retroArchExe, GameSystem system, CancellationToken ct)
    {
        if (system.RetroArchCore is not { } core) throw new InvalidOperationException($"RetroArch can't play {system.Name} games.");
        var target = CorePath(retroArchExe, core);
        if (File.Exists(target)) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var response = await http.GetAsync($"{CoreServer}{core}_libretro.dll.zip", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var zipPath = target + ".download";
            await using (var file = File.Create(zipPath))
                await response.Content.CopyToAsync(file, ct).ConfigureAwait(false);
            try
            {
                using var zip = ZipFile.OpenRead(zipPath);
                var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals(core + "_libretro.dll", StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidOperationException("the download didn't hold the core");
                entry.ExtractToFile(target, overwrite: true);
            }
            finally
            {
                File.Delete(zipPath);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException
                                       or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"RetroArch needs the \"{core}\" core for {system.Name}, and it couldn't be downloaded ({ex.Message}). " +
                "Get it in RetroArch › Online Updater › Core Downloader, then press Play again.", ex);
        }
    }
}
