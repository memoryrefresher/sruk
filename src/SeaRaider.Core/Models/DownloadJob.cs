using LiteDB;

namespace SeaRaider.Core.Models;

/// <summary>Where a job is in the download → extract → install pipeline.</summary>
public enum DownloadJobStatus
{
    Queued,
    Downloading,
    Paused,
    Extracting,
    Finalizing,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>Which transport a job uses; decided once by the planner.</summary>
public enum DownloadTransport
{
    Http,
    Torrent,
}

/// <summary>
/// One file a job downloads, with the name the user chose in the pre-download dialog.
/// </summary>
public sealed class DownloadFileSpec
{
    public string Url { get; set; } = string.Empty;

    /// <summary>Only this folder of the torrent (a backups torrent's one game); null = all of it.</summary>
    public string? TorrentFolder { get; set; }

    public DownloadLinkKind Kind { get; set; }

    /// <summary>Name as published by the source (from the URL).</summary>
    public string OriginalFileName { get; set; } = string.Empty;

    /// <summary>
    /// Name to save under. For split archives the whole set is renamed consistently
    /// (only the base name changes, never the .partN.rar / .7z.001 suffix) so volumes still pair up.
    /// Empty means "keep the original name". Ignored for torrents (names come from the torrent).
    /// </summary>
    public string SaveAsFileName { get; set; } = string.Empty;

    /// <summary>1-based volume order for multipart sets, 0 otherwise.</summary>
    public int PartNumber { get; set; }

    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// The mirror's name on the game page ("DataNodes", "Torrent"). Shown instead of the host for links that go through a
    /// link shortener, whose own name means nothing to the user.
    /// </summary>
    public string? MirrorName { get; set; }

    /// <summary>Absolute path once downloaded.</summary>
    public string? LocalPath { get; set; }

    /// <summary>
    /// The same file on other hosts (same volume of the same set, or same file name), best first. Tried in order when
    /// the chosen host says the file is gone.
    /// </summary>
    public List<DownloadSource> Alternates { get; set; } = [];

    [BsonIgnore]
    public string EffectiveFileName => string.IsNullOrWhiteSpace(SaveAsFileName) ? OriginalFileName : SaveAsFileName;

    /// <summary>Switches this file to another host's copy (after the current one turned out to be gone).</summary>
    public void UseSource(DownloadSource source)
    {
        Url = source.Url;
        Host = source.Host;
        if (source.FileName.Length > 0) OriginalFileName = source.FileName;
    }

    public static DownloadFileSpec FromLink(DownloadLink link) => new()
    {
        Url = link.Url,
        Kind = link.Kind,
        OriginalFileName = link.FileName,
        PartNumber = link.PartNumber,
        Host = link.Host,
        MirrorName = string.IsNullOrWhiteSpace(link.Mirror) ? null : link.Mirror,
        TorrentFolder = link.TorrentFolder,
    };
}

/// <summary>
/// A persisted unit of work: "get this game onto disk". Survives app restarts so
/// interrupted HTTP downloads (".part" files) and torrents (fast-resume) can continue.
/// Mutated only by <c>DownloadAndExtractManager</c>; the UI consumes immutable
/// <see cref="DownloadJobSnapshot"/>s instead.
/// </summary>
/// <summary>Another place to get a file from.</summary>
public sealed class DownloadSource
{
    public string Url { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;

    public static DownloadSource FromLink(DownloadLink link) => new() { Url = link.Url, Host = link.Host, FileName = link.FileName };
}

public sealed class DownloadJob
{
    [BsonId]
    public Guid Id { get; set; } = Guid.NewGuid();

    public string GameId { get; set; } = string.Empty;

    public string GameTitle { get; set; } = string.Empty;

    public string? CoverImageUrl { get; set; }

    public DownloadTransport Transport { get; set; }

    /// <summary>Original links (kept for jobs created by older versions; new code uses <see cref="Files"/>).</summary>
    public List<DownloadLink> Links { get; set; } = [];

    /// <summary>Files to fetch, in volume order, with user-chosen names.</summary>
    public List<DownloadFileSpec> Files { get; set; } = [];

    public DownloadJobStatus Status { get; set; } = DownloadJobStatus.Queued;

    /// <summary>Scratch folder holding archives while downloading (deleted after install).</summary>
    public string TempDirectory { get; set; } = string.Empty;

    /// <summary>Folder name the user chose (already sanitised); the leaf of <see cref="TargetDirectory"/>.</summary>
    public string InstallFolderName { get; set; } = string.Empty;

    /// <summary>Final game folder, e.g. C:\MyGames\&lt;InstallFolderName&gt;. For setup-based releases this is the installer's target.</summary>
    public string TargetDirectory { get; set; } = string.Empty;

    /// <summary>Absolute paths of everything downloaded so far, in volume order.</summary>
    public List<string> DownloadedFiles { get; set; } = [];

    /// <summary>Start the setup program automatically when extraction finds one.</summary>
    public bool RunInstallerWhenReady { get; set; }

    /// <summary>Use silent switches when running the setup program.</summary>
    public bool SilentInstall { get; set; } = true;

    /// <summary>Custom installer arguments chosen before download ("{dir}" = install folder).</summary>
    public string? CustomInstallerArguments { get; set; }

    /// <summary>Launch file (.exe/.bat/.cmd) the user named before download; resolved inside the game's folder after install.</summary>
    public string? LaunchFileName { get; set; }

    /// <summary>Set after extraction: the release is a setup program rather than a ready-to-play folder.</summary>
    public bool ProducedInstaller { get; set; }

    public long BytesDownloaded { get; set; }

    /// <summary>0 when unknown (server sent no Content-Length, or torrent metadata not yet fetched).</summary>
    public long TotalBytes { get; set; }

    public double ExtractionPercent { get; set; }

    public string? Error { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAtUtc { get; set; }

    // ---- queue -----------------------------------------------------------------------------------

    /// <summary>Order in the download queue (lower starts first). Reordered with Move Up / Move Down.</summary>
    public long QueuePosition { get; set; }

    /// <summary>Start now regardless of the active-download limits (cleared when paused).</summary>
    public bool ForceStart { get; set; }

    /// <summary>This job's own download limit in KB/s (0 = only the global limit applies).</summary>
    public int DownloadLimitKBps { get; set; }

    /// <summary>This job's own torrent upload limit in KB/s (0 = only the global limit applies).</summary>
    public int UploadLimitKBps { get; set; }

    /// <summary>
    /// The finished torrent is (or was, when the app closed or crashed) seeding. Persisted, so seeding picks up again on
    /// the next start, or its leftover files are cleaned up.
    /// </summary>
    public bool SeedingActive { get; set; }

    /// <summary>
    /// Times this download started again by itself after the connection gave out (see DownloadAndExtractManager); back to 0
    /// when it finishes or is retried by hand.
    /// </summary>
    public int AutoRetries { get; set; }

    /// <summary>Last time progress was written to the database (crash recovery).</summary>
    public DateTime? CheckpointAtUtc { get; set; }

    // ---- sources & low disk space mode (v0.6) ------------------------------------------------------

    /// <summary>The game page the links came from (sent as the Referer to file hosts).</summary>
    public string? SourcePageUrl { get; set; }

    /// <summary>The page's torrent, used if every host has lost a file.</summary>
    public DownloadLink? FallbackTorrent { get; set; }

    /// <summary>Volumes are unpacked while the next ones download, and deleted once unpacked.</summary>
    public bool StreamingExtraction { get; set; }

    /// <summary>Lowest volume index (0-based, in set order) that may still be on disk; earlier ones were unpacked and deleted.</summary>
    public int StreamFirstVolume { get; set; }

    /// <summary>The archive is solid: unpacking can't start in the middle, so deleted volumes must be fetched again to resume.</summary>
    public bool StreamSolidArchive { get; set; }

    /// <summary>
    /// Download files cleaned up for the user: with low disk space mode, every volume (deleted as soon as it was
    /// unpacked, so they never needed the room); otherwise the archives deleted after installing.
    /// </summary>
    public long SpaceSavedBytes { get; set; }

    // ---- live, non-persisted telemetry -------------------------------------------------------

    [BsonIgnore] public double BytesPerSecond { get; set; }
    [BsonIgnore] public int Seeds { get; set; }
    [BsonIgnore] public int Peers { get; set; }
    [BsonIgnore] public int PeersFound { get; set; }
    [BsonIgnore] public string StatusDetail { get; set; } = string.Empty;
    [BsonIgnore] public bool IsSeeding { get; set; }
    [BsonIgnore] public double SeedRatio { get; set; }
    [BsonIgnore] public double UploadBytesPerSecond { get; set; }

    /// <summary>Creates an immutable copy that is safe to hand to the UI thread.</summary>
    public DownloadJobSnapshot ToSnapshot() => new(
        Id, GameId, GameTitle, CoverImageUrl, Transport, Status, StatusDetail,
        BytesDownloaded, TotalBytes, BytesPerSecond, Seeds, Peers, ExtractionPercent,
        TargetDirectory, Error, CreatedAtUtc, CompletedAtUtc, ProducedInstaller, PeersFound,
        Files.Count > 0 ? Files[0].Url : Links.FirstOrDefault()?.Url,
        QueuePosition, ForceStart, DownloadLimitKBps, UploadLimitKBps, IsSeeding, SeedRatio, UploadBytesPerSecond,
        SpaceSavedBytes, StreamedWhileDownloading);

    /// <summary>Remembered after the job finishes (StreamingExtraction is reset then).</summary>
    public bool StreamedWhileDownloading { get; set; }
}

/// <summary>Thread-safe, immutable view of a <see cref="DownloadJob"/> at one moment.</summary>
public sealed record DownloadJobSnapshot(
    Guid Id,
    string GameId,
    string GameTitle,
    string? CoverImageUrl,
    DownloadTransport Transport,
    DownloadJobStatus Status,
    string StatusDetail,
    long BytesDownloaded,
    long TotalBytes,
    double BytesPerSecond,
    int Seeds,
    int Peers,
    double ExtractionPercent,
    string TargetDirectory,
    string? Error,
    DateTime CreatedAtUtc,
    DateTime? CompletedAtUtc,
    bool ProducedInstaller,
    int PeersFound,
    string? PrimaryUrl,
    long QueuePosition = 0,
    bool ForceStart = false,
    int DownloadLimitKBps = 0,
    int UploadLimitKBps = 0,
    bool IsSeeding = false,
    double SeedRatio = 0,
    double UploadBytesPerSecond = 0,
    long SpaceSavedBytes = 0,
    bool StreamedWhileDownloading = false)
{
    /// <summary>Download completion 0–100, or null when the total size is unknown.</summary>
    public double? DownloadPercent => TotalBytes > 0 ? Math.Clamp(BytesDownloaded * 100.0 / TotalBytes, 0, 100) : null;

    /// <summary>Estimated time remaining for the download phase, or null when it can't be estimated.</summary>
    public TimeSpan? Eta =>
        TotalBytes > 0 && BytesPerSecond > 1 && BytesDownloaded < TotalBytes
            ? TimeSpan.FromSeconds((TotalBytes - BytesDownloaded) / BytesPerSecond)
            : null;

    public bool IsActive => Status is DownloadJobStatus.Queued or DownloadJobStatus.Downloading
        or DownloadJobStatus.Extracting or DownloadJobStatus.Finalizing;

    public bool IsTerminal => Status is DownloadJobStatus.Completed or DownloadJobStatus.Failed
        or DownloadJobStatus.Cancelled;
}
