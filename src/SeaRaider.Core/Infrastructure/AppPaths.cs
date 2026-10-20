namespace SeaRaider.Core;

/// <summary>Well-known per-user folders used by SeaRaider.</summary>
public static class AppPaths
{
    /// <summary>%LOCALAPPDATA%\SeaRaider (Windows) or ~/.local/share/SeaRaider elsewhere.</summary>
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SeaRaider");

    public static string DatabasePath => Path.Combine(DataDirectory, "searaider.db");

    /// <summary>Disk cache for cover art and screenshots so the store renders offline.</summary>
    public static string ImageCacheDirectory => Path.Combine(DataDirectory, "image-cache");

    /// <summary>MonoTorrent DHT cache, fast-resume data and magnet metadata.</summary>
    public static string TorrentCacheDirectory => Path.Combine(DataDirectory, "torrent-cache");

    /// <summary>Backups of in-folder save data taken before an uninstall.</summary>
    public static string SaveBackupDirectory => Path.Combine(DataDirectory, "save-backups");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ImageCacheDirectory);
        Directory.CreateDirectory(TorrentCacheDirectory);
        Directory.CreateDirectory(SaveBackupDirectory);
    }
}
