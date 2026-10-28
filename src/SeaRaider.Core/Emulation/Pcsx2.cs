using System.Text.Json;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Emulation;

/// <summary>
/// PlayStation 2 games, played in PCSX2 (the PS2 emulator; far better at PS2 games than RetroArch's PS2 core):
/// <list type="bullet">
///   <item>the PCSX2 chosen under Settings › Emulators, an installed one, or SeaRaider's own: downloaded the first time a PS2
///         game is played, from PCSX2's official releases on GitHub, as a portable folder (nothing installed in Windows);</item>
///   <item>set up so a game opens straight away, full screen: no first-run wizard, no update prompt, no "are you sure" on exit;</item>
///   <item>the PS2 BIOS: every PS2 emulator needs one, dumped from your own PlayStation 2 (it's Sony's, so SeaRaider can't
///         download it). Chosen once in Settings › Emulators, it's copied where PCSX2 (and RetroArch's PS2 core) look.</item>
/// </list>
/// </summary>
public static class Pcsx2
{
    private const string ReleasesApi = "https://api.github.com/repos/PCSX2/pcsx2/releases";

    /// <summary>SeaRaider's own PCSX2: %LOCALAPPDATA%\SeaRaider\PCSX2.</summary>
    public static string OwnFolder => Path.Combine(AppPaths.DataDirectory, "PCSX2");

    public static bool IsPcsx2(string? path) =>
        !string.IsNullOrEmpty(path) && Path.GetFileNameWithoutExtension(path).StartsWith("pcsx2", StringComparison.OrdinalIgnoreCase);

    /// <summary>PCSX2 on this PC: SeaRaider's own, or an installed one (Program Files, the usual folders); null if none.</summary>
    public static string? Locate()
    {
        foreach (var folder in new[]
                 {
                     OwnFolder,
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PCSX2"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "PCSX2"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "PCSX2"),
                 })
            if (FindExe(folder) is { } exe) return exe;
        return null;
    }

    private static string? FindExe(string folder)
    {
        if (!Directory.Exists(folder)) return null;
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true };
        return Directory.EnumerateFiles(folder, "pcsx2-qt.exe", options).FirstOrDefault()
               ?? Directory.EnumerateFiles(folder, "pcsx2*.exe", options).FirstOrDefault(f => !f.Contains("updater", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Gets PCSX2's newest release (64-bit Windows, portable) the first time a PS2 game is played. Returns its program.</summary>
    /// <exception cref="InvalidOperationException">It couldn't be downloaded or unpacked.</exception>
    public static async Task<string> InstallOwnAsync(HttpClient http, Abstractions.IArchiveExtractor extractor, CancellationToken ct)
    {
        if (FindExe(OwnFolder) is { } existing) return existing;
        Directory.CreateDirectory(OwnFolder);

        string url, name;
        try
        {
            (url, name) = await FindDownloadAsync(http, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException
                                       or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"SeaRaider couldn't find PCSX2 to download ({ex.Message}). Install PCSX2 from pcsx2.net and choose it under " +
                "Settings › Emulators › PlayStation 2, then press Play again.", ex);
        }

        var archive = Path.Combine(OwnFolder, name);
        try
        {
            var download = archive + ".download";
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", "SeaRaider");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var file = File.Create(download);
                await response.Content.CopyToAsync(file, ct).ConfigureAwait(false);
            }
            File.Move(download, archive, overwrite: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
                                       or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException($"SeaRaider couldn't download PCSX2 ({ex.Message}). Check the connection and press Play again.", ex);
        }

        try
        {
            await extractor.ExtractAsync([archive], OwnFolder, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await RetroArch.ExtractWithSevenZipAsync(http, archive, OwnFolder, ct).ConfigureAwait(false); // filters SharpCompress doesn't read
        }
        try { File.Delete(archive); } catch (IOException) { } catch (UnauthorizedAccessException) { }

        var exe = FindExe(OwnFolder) ?? throw new InvalidOperationException("PCSX2 was downloaded but its program wasn't in it. Press Play again.");
        // Portable: its settings, BIOS and memory cards stay in its own folder.
        var portable = Path.Combine(Path.GetDirectoryName(exe)!, "portable.ini");
        if (!File.Exists(portable)) File.WriteAllText(portable, string.Empty);
        return exe;
    }

    /// <summary>The newest stable release's 64-bit Windows download (its .7z, else .zip).</summary>
    private static async Task<(string Url, string Name)> FindDownloadAsync(HttpClient http, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApi + "?per_page=15");
        request.Headers.TryAddWithoutValidation("User-Agent", "SeaRaider");
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct)
            .ConfigureAwait(false);

        // Stable releases first; a nightly only when no stable one has a Windows download.
        var releases = json.RootElement.EnumerateArray()
            .OrderBy(r => r.TryGetProperty("prerelease", out var p) && p.GetBoolean() ? 1 : 0)
            .ToList();
        foreach (var release in releases)
        {
            if (!release.TryGetProperty("assets", out var assets)) continue;
            var windows = assets.EnumerateArray()
                .Select(a => (Name: a.GetProperty("name").GetString() ?? string.Empty, Url: a.GetProperty("browser_download_url").GetString() ?? string.Empty))
                .Where(a => a.Name.Contains("windows", StringComparison.OrdinalIgnoreCase) && a.Name.Contains("x64", StringComparison.OrdinalIgnoreCase) &&
                            !a.Name.Contains("symbols", StringComparison.OrdinalIgnoreCase) &&
                            (a.Name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) || a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                .OrderBy(a => a.Name.Contains("Qt", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(a => a.Name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .FirstOrDefault();
            if (windows.Url.Length > 0) return (windows.Url, windows.Name);
        }
        throw new InvalidOperationException("no Windows download in PCSX2's releases");
    }

    // =============================================================================================
    // BIOS and settings
    // =============================================================================================

    /// <summary>Where this PCSX2 keeps BIOS files: "bios" next to it when portable, else Documents\PCSX2\bios.</summary>
    public static string BiosFolder(string exe)
    {
        var folder = Path.GetDirectoryName(exe)!;
        return IsPortable(exe)
            ? Path.Combine(folder, "bios")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PCSX2", "bios");
    }

    private static bool IsPortable(string exe)
    {
        var folder = Path.GetDirectoryName(exe)!;
        return File.Exists(Path.Combine(folder, "portable.ini")) || File.Exists(Path.Combine(folder, "portable.txt"));
    }

    private static string SettingsFile(string exe) => IsPortable(exe)
        ? Path.Combine(Path.GetDirectoryName(exe)!, "inis", "PCSX2.ini")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PCSX2", "inis", "PCSX2.ini");

    /// <summary>A PS2 BIOS dump: a file of 4 MB or more (.bin, .rom0) — what PCSX2 lists.</summary>
    private static IEnumerable<string> BiosFiles(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder)
                .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".bin" or ".rom0" or ".rom" && new FileInfo(f).Length >= 4 * 1024 * 1024)
            : Enumerable.Empty<string>();

    /// <summary>
    /// Gets PCSX2 ready to open a game straight away: the BIOS chosen in Settings copied to its BIOS folder (when it has none),
    /// that BIOS selected, the first-run wizard, update check and exit question switched off. Returns the BIOS file used.
    /// </summary>
    /// <exception cref="InvalidOperationException">No PS2 BIOS anywhere: Settings › Emulators says how to add yours.</exception>
    public static string Prepare(string exe, string? chosenBios)
    {
        var folder = BiosFolder(exe);
        Directory.CreateDirectory(folder);
        if (!string.IsNullOrWhiteSpace(chosenBios) && File.Exists(chosenBios))
        {
            var target = Path.Combine(folder, Path.GetFileName(chosenBios));
            if (!File.Exists(target)) File.Copy(chosenBios, target);
        }
        var bios = (chosenBios is not null && File.Exists(Path.Combine(folder, Path.GetFileName(chosenBios)))
                       ? Path.Combine(folder, Path.GetFileName(chosenBios))
                       : null)
                   ?? BiosFiles(folder).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
                   ?? throw new InvalidOperationException(
                       "PlayStation 2 games need a PS2 BIOS, dumped from your own PlayStation 2 (it's Sony's, so SeaRaider can't download " +
                       "it). Choose the BIOS file under Settings › Emulators › PlayStation 2 BIOS, then press Play again.");

        WriteSettings(SettingsFile(exe), new Dictionary<string, Dictionary<string, string>>
        {
            ["UI"] = new() { ["SetupWizardIncomplete"] = "false", ["ConfirmShutdown"] = "false", ["StartFullscreen"] = "true" },
            ["AutoUpdater"] = new() { ["CheckAtStartup"] = "false" },
            ["Filenames"] = new() { ["BIOS"] = Path.GetFileName(bios) },
        }, folder);
        return bios;
    }

    /// <summary>Sets these keys in PCSX2's settings file, keeping everything else in it (and the BIOS folder when it's portable).</summary>
    private static void WriteSettings(string path, Dictionary<string, Dictionary<string, string>> wanted, string biosFolder)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
        if (!File.Exists(path)) wanted["Folders"] = new() { ["Bios"] = biosFolder };

        foreach (var (section, values) in wanted)
        {
            var start = lines.FindIndex(l => l.Trim().Equals($"[{section}]", StringComparison.OrdinalIgnoreCase));
            if (start < 0)
            {
                if (lines.Count > 0 && lines[^1].Length > 0) lines.Add(string.Empty);
                lines.Add($"[{section}]");
                lines.AddRange(values.Select(v => $"{v.Key} = {v.Value}"));
                continue;
            }
            var end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith('['));
            if (end < 0) end = lines.Count;
            foreach (var (key, value) in values)
            {
                var at = lines.FindIndex(start + 1, end - start - 1, l => l.Split('=')[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase));
                if (at >= 0) lines[at] = $"{key} = {value}";
                else
                {
                    lines.Insert(end, $"{key} = {value}");
                    end++;
                }
            }
        }
        File.WriteAllLines(path, lines);
    }

    /// <summary>Also gives RetroArch's PS2 core the BIOS (system\pcsx2\bios), for anyone who plays PS2 games there.</summary>
    public static void ShareBiosWithRetroArch(string? retroArchExe, string? bios)
    {
        if (retroArchExe is null || bios is null || !File.Exists(bios)) return;
        try
        {
            var folder = Path.Combine(Path.GetDirectoryName(retroArchExe)!, "system", "pcsx2", "bios");
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, Path.GetFileName(bios));
            if (!File.Exists(target)) File.Copy(bios, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // RetroArch's folder can't be written: PCSX2 itself still has it
        }
    }
}
