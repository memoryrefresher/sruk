namespace SeaRaider.Core.Emulation;

/// <summary>The game file to start and the system it belongs to.</summary>
public sealed record RomMatch(GameSystem System, string Path);

/// <summary>
/// Finds the game file in a downloaded folder, and which system it is for. The system comes from the file type when only one
/// system uses it (.nes, .gba, .nsp), otherwise from what the page called the game (title, genres, tags, address: "PS2",
/// "Nintendo 64"), which is how disc images (.iso, .cue, .chd) are told apart from ordinary files.
/// </summary>
public static class RomDetector
{
    /// <summary>Disc entry points first: a .cue or .m3u opens the whole disc, a .bin beside it must not be chosen instead.</summary>
    private static readonly string[] Preferred =
        [".m3u", ".cue", ".gdi", ".cdi", ".chd", ".rvz", ".wbfs", ".nsp", ".xci", ".wux", ".wud", ".rpx", ".iso", ".cso", ".pbp", ".bin"];

    /// <summary>Folders and names that hold a game's extras, never the game.</summary>
    private static readonly string[] SkipNames = ["update", "dlc", "patch", "manual", "bios"];

    /// <param name="folder">The extracted game folder.</param>
    /// <param name="text">Whatever names the console: the title, genres, tags and page address. May be null.</param>
    /// <returns>The ROM, or null when the folder holds no game file for a known system.</returns>
    public static RomMatch? Detect(string folder, string? text)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;

        var root = Path.GetFullPath(folder);
        var files = Enumerate(root).ToList();
        if (files.Count == 0) return null;

        // The page names the console: take that console's best file.
        if (GameSystems.FromText(text) is { } named && Pick(files, named, root) is { } byName)
            return new RomMatch(named, byName);

        // Otherwise a file type only one console uses.
        var byType = files
            .Select(f => (File: f, System: GameSystems.FromExtension(Path.GetExtension(f))))
            .Where(x => x.System is not null)
            .OrderBy(x => Depth(x.File, root))
            .ThenByDescending(x => Size(x.File))
            .FirstOrDefault();
        return byType.System is { } system && Pick(files, system, root) is { } path ? new RomMatch(system, path) : null;
    }

    /// <summary>The game file for <paramref name="system"/>: disc entry points first, then the shallowest, then the largest.</summary>
    internal static string? Pick(IReadOnlyList<string> files, GameSystem system, string root) =>
        files.Where(f => system.Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => Rank(Path.GetExtension(f)))
            .ThenBy(f => Depth(f, root))
            .ThenByDescending(f => Size(f))
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    private static int Rank(string extension)
    {
        var index = Array.FindIndex(Preferred, e => e.Equals(extension, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? Preferred.Length : index;
    }

    private static int Depth(string path, string root) => Path.GetRelativePath(root, path).Count(c => c is '\\' or '/');

    private static long Size(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (IOException) { return 0; }
    }

    private static IEnumerable<string> Enumerate(string root)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 6 };
        foreach (var file in Directory.EnumerateFiles(root, "*", options))
        {
            if (string.IsNullOrEmpty(Path.GetExtension(file))) continue;
            var relative = Path.GetRelativePath(root, file);
            var parts = relative.Split('\\', '/');
            if (parts.Any(p => SkipNames.Contains(p, StringComparer.OrdinalIgnoreCase))) continue;
            yield return file;
        }
    }
}
