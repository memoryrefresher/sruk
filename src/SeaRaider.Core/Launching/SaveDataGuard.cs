using Microsoft.Extensions.Logging;
using SeaRaider.Core.Abstractions;

namespace SeaRaider.Core.Launching;

/// <summary>
/// Most games save to %APPDATA%, %LOCALAPPDATA%, Documents\My Games or Saved Games — folders
/// SeaRaider never touches. Some, however, keep saves inside their own install folder, which an
/// uninstall would destroy. This guard copies well-known save folders out to
/// %LOCALAPPDATA%\SeaRaider\save-backups\&lt;gameId&gt;\ before uninstall and copies them back after a
/// reinstall. Backups are never deleted automatically.
/// </summary>
public sealed class SaveDataGuard(ILogger<SaveDataGuard> logger) : ISaveDataGuard
{
    /// <summary>Folder names that commonly hold in-folder saves (matched case-insensitively at any depth ≤ 4).</summary>
    private static readonly HashSet<string> SaveFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "save", "saves", "savegame", "savegames", "savedata", "save data", "saved games", "saved",
        "userdata", "user data", "profiles", "playerdata", "player data", "storage",
    };

    public Task<int> BackupAsync(string gameId, string installDirectory, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            if (!Directory.Exists(installDirectory)) return 0;

            var backupRoot = BackupRoot(gameId);
            var count = 0;
            foreach (var saveDir in FindSaveFolders(installDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(installDirectory, saveDir);
                CopyDirectory(saveDir, Path.Combine(backupRoot, relative), cancellationToken);
                count++;
                logger.LogInformation("Backed up in-folder saves {Folder} for {Game}", relative, gameId);
            }
            return count;
        }, cancellationToken);

    public Task<bool> RestoreAsync(string gameId, string installDirectory, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var backupRoot = BackupRoot(gameId);
            if (!Directory.Exists(backupRoot) || !Directory.EnumerateFileSystemEntries(backupRoot).Any()) return false;

            CopyDirectory(backupRoot, installDirectory, cancellationToken);
            logger.LogInformation("Restored in-folder saves for {Game}", gameId);
            return true;
        }, cancellationToken);

    private static string BackupRoot(string gameId) => Path.Combine(AppPaths.SaveBackupDirectory, gameId);

    private static IEnumerable<string> FindSaveFolders(string root)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 4 };
        var hits = Directory.EnumerateDirectories(root, "*", options)
            .Where(d => SaveFolderNames.Contains(Path.GetFileName(d)) &&
                        Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Any())
            .OrderBy(d => d.Length)
            .ToList();

        // Keep only outermost matches (Saved\SaveGames → just Saved).
        var outermost = new List<string>();
        foreach (var dir in hits)
            if (!outermost.Any(o => PathSafety.IsUnder(dir, o))) outermost.Add(dir);
        return outermost;
    }

    private static void CopyDirectory(string source, string destination, CancellationToken ct)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
