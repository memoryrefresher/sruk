using System.Text;

namespace SeaRaider.Core;

/// <summary>
/// Defensive helpers for turning untrusted strings (game titles, archive entry names)
/// into file-system paths, and for guarding destructive operations.
/// </summary>
public static class PathSafety
{
    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Turns a game title into a safe single folder name ("Foo: Bar?" → "Foo - Bar").</summary>
    public static string ToFolderName(string title)
    {
        // GetInvalidFileNameChars() is OS-specific; add the Windows set explicitly so names
        // created on any OS stay valid on Windows.
        var invalid = Path.GetInvalidFileNameChars()
            .Concat(new[] { ':', '*', '?', '"', '<', '>', '|', '/', '\\' })
            .ToHashSet();
        var sb = new StringBuilder(title.Length);
        foreach (var c in title.Trim())
        {
            if (c == ':') sb.Append(" -");
            else if (!invalid.Contains(c) && !char.IsControl(c)) sb.Append(c);
        }

        var name = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.', ' ');
        if (name.Length == 0) name = "Untitled Game";
        if (ReservedWindowsNames.Contains(name)) name = "_" + name;
        return name.Length > 80 ? name[..80].TrimEnd('.', ' ') : name;
    }

    /// <summary>
    /// Resolves an archive entry key under <paramref name="destinationRoot"/> and rejects
    /// "zip-slip" entries (../../Windows/evil.dll, absolute paths, drive letters).
    /// </summary>
    /// <returns>The absolute destination path, or null if the entry must be skipped.</returns>
    public static string? ResolveEntryPath(string destinationRoot, string entryKey)
    {
        if (string.IsNullOrWhiteSpace(entryKey)) return null;

        var relative = entryKey.Replace('\\', '/').TrimStart('/');
        if (relative.Contains(':')) return null; // drive letters / alternate data streams

        var root = Path.GetFullPath(destinationRoot);
        var full = Path.GetFullPath(Path.Combine(root, relative));
        return IsUnder(full, root) ? full : null;
    }

    /// <summary>True when <paramref name="path"/> is strictly inside <paramref name="root"/>.</summary>
    public static bool IsUnder(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return fullPath.StartsWith(fullRoot, comparison);
    }

    /// <summary>
    /// Deletes a directory tree with retries, clearing read-only attributes first.
    /// Games frequently ship read-only files and anti-virus scanners briefly lock new files.
    /// </summary>
    public static async Task DeleteDirectoryRobustAsync(string directory, CancellationToken ct = default)
    {
        if (!Directory.Exists(directory)) return;

        const int maxAttempts = 8; // about 9 s in all: antivirus scans and Explorer previews let go in that time
        for (var attempt = 1; ; attempt++)
        {
            // Read-only files and folders (setups often ship them) can't be deleted until they're made normal.
            ClearAttributes(directory);
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts && ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(2000, 250 * attempt)), ct).ConfigureAwait(false);
            }
        }
    }

    private static void ClearAttributes(string directory)
    {
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options))
            {
                try
                {
                    if ((entry.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)) != 0)
                        entry.Attributes = entry is DirectoryInfo ? FileAttributes.Directory : FileAttributes.Normal;
                }
                catch (IOException) { /* best effort */ }
                catch (UnauthorizedAccessException) { /* best effort */ }
            }
            var root = new DirectoryInfo(directory);
            if ((root.Attributes & FileAttributes.ReadOnly) != 0) root.Attributes = FileAttributes.Directory;
        }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    /// <summary>Sum of file sizes under a folder (0 if it doesn't exist).</summary>
    public static long GetDirectorySize(string directory)
    {
        if (!Directory.Exists(directory)) return 0;
        long total = 0;
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            try { total += file.Length; }
            catch (IOException) { }
        }
        return total;
    }
}
