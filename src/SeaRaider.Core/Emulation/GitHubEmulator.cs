using System.Text.Json;

namespace SeaRaider.Core.Emulation;

/// <summary>
/// Downloads an emulator's newest Windows release from its official GitHub releases and unpacks it into SeaRaider's own folder
/// for it (portable: nothing is installed in Windows). Used for xemu (original Xbox) and RPCS3 (PS3), the way PCSX2 is got.
/// </summary>
internal static class GitHubEmulator
{
    /// <param name="releasesApi">"https://api.github.com/repos/{owner}/{repo}/releases".</param>
    /// <param name="isWindowsAsset">Which download of a release is the 64-bit Windows build (by its file name).</param>
    /// <param name="findExe">The emulator's program in the folder, or null.</param>
    /// <param name="name">The emulator's name, for messages.</param>
    /// <param name="settingsHint">Where the user can pick their own copy instead ("Settings › Emulators › PlayStation 3").</param>
    public static async Task<string> InstallAsync(HttpClient http, Abstractions.IArchiveExtractor extractor, string releasesApi,
        Func<string, bool> isWindowsAsset, string folder, Func<string, string?> findExe, string name, string settingsHint, CancellationToken ct)
    {
        if (findExe(folder) is { } existing) return existing;
        Directory.CreateDirectory(folder);

        string url, file;
        try
        {
            (url, file) = await FindDownloadAsync(http, releasesApi, isWindowsAsset, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException
                                       or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"SeaRaider couldn't find {name} to download ({ex.Message}). Install {name} yourself and choose it under {settingsHint}, then press Play again.", ex);
        }

        var archive = Path.Combine(folder, file);
        try
        {
            var download = archive + ".download";
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", "SeaRaider");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var output = File.Create(download);
                await response.Content.CopyToAsync(output, ct).ConfigureAwait(false);
            }
            File.Move(download, archive, overwrite: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
                                       or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException($"SeaRaider couldn't download {name} ({ex.Message}). Check the connection and press Play again.", ex);
        }

        try
        {
            await extractor.ExtractAsync([archive], folder, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await RetroArch.ExtractWithSevenZipAsync(http, archive, folder, ct).ConfigureAwait(false); // formats SharpCompress doesn't read
        }
        try { File.Delete(archive); } catch (IOException) { } catch (UnauthorizedAccessException) { }

        return findExe(folder) ?? throw new InvalidOperationException($"{name} was downloaded but its program wasn't in it. Press Play again.");
    }

    /// <summary>The newest stable release's Windows download (a nightly only when no stable release has one).</summary>
    private static async Task<(string Url, string Name)> FindDownloadAsync(HttpClient http, string releasesApi, Func<string, bool> isWindowsAsset,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, releasesApi + "?per_page=15");
        request.Headers.TryAddWithoutValidation("User-Agent", "SeaRaider");
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct)
            .ConfigureAwait(false);

        var releases = json.RootElement.EnumerateArray()
            .Where(r => !(r.TryGetProperty("draft", out var d) && d.GetBoolean()))
            .OrderBy(r => r.TryGetProperty("prerelease", out var p) && p.GetBoolean() ? 1 : 0)
            .ToList();
        foreach (var release in releases)
        {
            if (!release.TryGetProperty("assets", out var assets)) continue;
            var windows = assets.EnumerateArray()
                .Select(a => (Name: a.GetProperty("name").GetString() ?? string.Empty, Url: a.GetProperty("browser_download_url").GetString() ?? string.Empty))
                .Where(a => isWindowsAsset(a.Name) &&
                            !a.Name.Contains("pdb", StringComparison.OrdinalIgnoreCase) && !a.Name.Contains("symbols", StringComparison.OrdinalIgnoreCase) &&
                            !a.Name.Contains("debug", StringComparison.OrdinalIgnoreCase) &&
                            (a.Name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) || a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                .OrderBy(a => a.Name.Contains("release", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .FirstOrDefault();
            if (windows.Url.Length > 0) return (windows.Url, windows.Name);
        }
        throw new InvalidOperationException("no Windows download in its releases");
    }

    /// <summary>The first program with one of these names in the folder (two levels deep), skipping helpers like updaters.</summary>
    public static string? FindExe(string folder, params string[] names)
    {
        if (!Directory.Exists(folder)) return null;
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true };
        foreach (var name in names)
            if (Directory.EnumerateFiles(folder, name, options).FirstOrDefault(f => !f.Contains("updater", StringComparison.OrdinalIgnoreCase)) is { } exe)
                return exe;
        return null;
    }

    /// <summary>Sets keys in a TOML settings file (xemu's), keeping everything else in it. Values are already TOML ("true", "'C:\x'").</summary>
    public static void SetToml(string path, Dictionary<string, Dictionary<string, string>> wanted)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
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

    /// <summary>A path as a TOML string: a literal string ('C:\Games\x') unless it holds a quote.</summary>
    public static string TomlString(string value) =>
        value.Contains('\'') ? "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"" : "'" + value + "'";
}
