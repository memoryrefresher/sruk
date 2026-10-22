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

    /// <summary>RetroArch itself (portable, 64-bit Windows) from libretro's build server.</summary>
    private const string RetroArchDownload = "https://buildbot.libretro.com/nightly/windows/x86_64/RetroArch.7z";

    /// <summary>Where SeaRaider puts its own RetroArch when none is installed: %LOCALAPPDATA%\SeaRaider\RetroArch.</summary>
    public static string OwnFolder => Path.Combine(AppPaths.DataDirectory, "RetroArch");

    /// <summary>
    /// Gets RetroArch from libretro (portable: nothing is installed in Windows, it's just a folder SeaRaider keeps) the first
    /// time a console game is played and none is on the PC. Returns retroarch.exe.
    /// </summary>
    /// <exception cref="InvalidOperationException">It couldn't be downloaded or unpacked.</exception>
    public static async Task<string> InstallOwnAsync(HttpClient http, Abstractions.IArchiveExtractor extractor, CancellationToken ct)
    {
        var existing = FindExe(OwnFolder);
        if (existing is not null) return existing;
        Directory.CreateDirectory(OwnFolder);
        var archive = Path.Combine(OwnFolder, "RetroArch.7z.download");
        var named = Path.ChangeExtension(archive, null); // "RetroArch.7z": the extractor goes by the file's type
        try
        {
            using (var response = await http.GetAsync(RetroArchDownload, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var file = File.Create(archive);
                await response.Content.CopyToAsync(file, ct).ConfigureAwait(false);
            }
            File.Move(archive, named, overwrite: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
                                       or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"SeaRaider couldn't download RetroArch to play this game ({ex.Message}). Check the internet connection and press Play again, " +
                "or install RetroArch from retroarch.com.", ex);
        }

        try
        {
            await extractor.ExtractAsync([named], OwnFolder, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // RetroArch's 7z uses compression filters (BCJ2 and newer) that SharpCompress doesn't read, and reports that as
            // "password-protected". 7-Zip itself reads them: the one on this PC, else its small official stand-alone 7zr.exe.
            await ExtractWithSevenZipAsync(http, named, OwnFolder, ct).ConfigureAwait(false);
        }
        TryDelete(named);
        return FindExe(OwnFolder) ?? throw new InvalidOperationException("RetroArch was downloaded but retroarch.exe wasn't in it. Press Play again.");
    }

    /// <summary>7-Zip's own stand-alone extractor for .7z files (about 600 KB), from 7-zip.org.</summary>
    private const string SevenZipStandalone = "https://www.7-zip.org/a/7zr.exe";

    private static async Task ExtractWithSevenZipAsync(HttpClient http, string archive, string destination, CancellationToken ct)
    {
        var sevenZip = FindSevenZip();
        try
        {
            if (sevenZip is null)
            {
                var tools = Path.Combine(AppPaths.DataDirectory, "Tools");
                Directory.CreateDirectory(tools);
                sevenZip = Path.Combine(tools, "7zr.exe");
                var download = sevenZip + ".download";
                using (var response = await http.GetAsync(SevenZipStandalone, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    await using var file = File.Create(download);
                    await response.Content.CopyToAsync(file, ct).ConfigureAwait(false);
                }
                File.Move(download, sevenZip, overwrite: true);
            }

            var start = new System.Diagnostics.ProcessStartInfo(sevenZip)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("x");
            start.ArgumentList.Add(archive);
            start.ArgumentList.Add("-o" + destination);
            start.ArgumentList.Add("-y");
            using var process = System.Diagnostics.Process.Start(start) ?? throw new IOException("7-Zip didn't start.");
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var errors = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var said = (await errors.ConfigureAwait(false)).Trim();
                if (said.Length == 0) said = (await output.ConfigureAwait(false)).Trim();
                throw new IOException($"7-Zip stopped with code {process.ExitCode}: {said}");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
                                       or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"SeaRaider downloaded RetroArch but couldn't unpack it ({ex.Message}). Press Play again, or install RetroArch from " +
                "retroarch.com and choose it under Settings › Emulators.", ex);
        }
    }

    /// <summary>An installed 7-Zip, or null.</summary>
    private static string? FindSevenZip()
    {
        foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var path = Path.Combine(Environment.GetFolderPath(root), "7-Zip", "7z.exe");
            if (File.Exists(path)) return path;
        }
        var own = Path.Combine(AppPaths.DataDirectory, "Tools", "7zr.exe");
        return File.Exists(own) ? own : null;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* goes next time */ }
    }

    private static string? FindExe(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "retroarch.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true })
                .FirstOrDefault()
            : null;

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
        if (FindExe(OwnFolder) is { } own) return own; // the one SeaRaider downloaded
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
