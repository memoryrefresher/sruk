namespace SeaRaider.Core.Launching;

/// <summary>
/// Files a game can be started from: a program (.exe) or a script (.bat / .cmd). Used when the user picks the launch
/// file themselves, either after install or by naming it in the download dialog.
/// </summary>
public static class LaunchFiles
{
    public static readonly string[] Extensions = [".exe", ".bat", ".cmd"];

    /// <summary>True for a path with a launchable extension.</summary>
    public static bool IsLaunchable(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Finds the file the user asked for inside the game's folder. <paramref name="hint"/> is a relative path
    /// ("bin\start.bat") or just a file name ("start.bat"); without an extension .exe, .bat and .cmd are tried.
    /// The shallowest match wins. Returns null when nothing matches or the hint tries to leave the folder.
    /// </summary>
    public static string? Resolve(string? installDirectory, string? hint)
    {
        if (string.IsNullOrWhiteSpace(installDirectory) || string.IsNullOrWhiteSpace(hint) || !Directory.Exists(installDirectory))
            return null;

        var root = Path.GetFullPath(installDirectory);
        var wanted = hint.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(wanted) || wanted.Split(Path.DirectorySeparatorChar).Contains("..")) return null;

        var names = Path.HasExtension(wanted) ? [wanted] : Extensions.Select(e => wanted + e).ToArray();
        foreach (var name in names)
        {
            var direct = Path.Combine(root, name);
            if (IsLaunchable(direct) && File.Exists(direct)) return direct;
        }

        var fileNames = names.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 8 };
        return Directory.EnumerateFiles(root, "*", options)
            .Where(f => IsLaunchable(f) && fileNames.Contains(Path.GetFileName(f)))
            .OrderBy(f => f.Count(c => c == Path.DirectorySeparatorChar))
            .ThenBy(f => f.Length)
            .FirstOrDefault();
    }
}
