using SeaRaider.Core.Models;

namespace SeaRaider.Core.Abstractions;

/// <summary>
/// Orchestrates the one-click pipeline: download (HTTP or torrent) → extract → detect exe →
/// clean up → mark installed. All work runs on background workers; progress is published as
/// immutable <see cref="DownloadJobSnapshot"/>s through <see cref="JobChanged"/>.
/// </summary>
public interface IDownloadAndExtractManager : IAsyncDisposable
{
    /// <summary>Raised on a worker thread whenever a job changes (throttled for progress ticks).</summary>
    event EventHandler<DownloadJobSnapshot>? JobChanged;

    /// <summary>Raised when a job is removed from the list.</summary>
    event EventHandler<Guid>? JobRemoved;

    /// <summary>Current state of every known job, newest first.</summary>
    IReadOnlyList<DownloadJobSnapshot> Jobs { get; }

    /// <summary>Restores persisted jobs (interrupted ones come back Paused) and starts the workers.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// What a download of this game would look like (transport, files, default folder and file names) —
    /// shown in the pre-download dialog so the user can rename things before anything is fetched.
    /// </summary>
    /// <exception cref="InvalidOperationException">The game has no usable links.</exception>
    Downloads.InstallPreview Preview(GameMetadata game, DownloadLink? preferredLink = null);

    /// <summary>Queues a game with the folder / file names and installer options chosen by the user.</summary>
    /// <exception cref="ArgumentException">An invalid file name.</exception>
    Task<DownloadJobSnapshot> EnqueueAsync(Downloads.InstallRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues a game for installation with default names.
    /// </summary>
    /// <param name="game">Store entry to install.</param>
    /// <param name="preferredLink">
    /// Optional link the user picked. When null the planner chooses (complete multipart set,
    /// single HTTP archive, or torrent — depending on settings).
    /// </param>
    /// <exception cref="InvalidOperationException">Game already installed / queued, or has no usable links.</exception>
    Task<DownloadJobSnapshot> EnqueueAsync(GameMetadata game, DownloadLink? preferredLink = null,
        CancellationToken cancellationToken = default);

    /// <summary>Stops transfer but keeps partial data so the job can resume.</summary>
    Task PauseAsync(Guid jobId);

    Task ResumeAsync(Guid jobId);

    /// <summary>Stops the job and deletes its temporary files.</summary>
    Task CancelAsync(Guid jobId);

    /// <summary>Removes a finished/failed/cancelled job from the list.</summary>
    Task RemoveAsync(Guid jobId);

    /// <summary>Moves a job up / down / to the top / to the bottom of the queue.</summary>
    Task MoveAsync(Guid jobId, Downloads.QueueMove move);

    /// <summary>Starts a queued, paused or failed job now, ignoring the active-download limits.</summary>
    Task ForceStartAsync(Guid jobId);

    /// <summary>Sets a job's own download / upload limits in KB/s (0 = only the global limit). Applies immediately.</summary>
    Task SetJobLimitsAsync(Guid jobId, int downloadKBps, int uploadKBps);

    /// <summary>Stops seeding a finished torrent (its temporary files are then cleaned up).</summary>
    Task StopSeedingAsync(Guid jobId);
}

/// <summary>Progress reported by the transport layer (HTTP or torrent).</summary>
/// <param name="BytesDownloaded">Bytes received for the whole job so far.</param>
/// <param name="TotalBytes">Total job size, or 0 if unknown.</param>
/// <param name="BytesPerSecond">Smoothed current rate.</param>
/// <param name="Seeds">Torrent seeds connected (0 for HTTP).</param>
/// <param name="Peers">Torrent leechers connected (0 for HTTP).</param>
/// <param name="Detail">Short status line ("Part 2 of 5", "Fetching metadata…").</param>
/// <param name="PeersFound">Torrent peers discovered (trackers, DHT, peer exchange) but not necessarily connected.</param>
public readonly record struct TransferProgress(
    long BytesDownloaded, long TotalBytes, double BytesPerSecond, int Seeds = 0, int Peers = 0, string? Detail = null,
    int PeersFound = 0);

/// <summary>Resumable single-file HTTP download.</summary>
public interface IHttpDownloader
{
    /// <summary>Best-effort HEAD request for the file size; null when unknown.</summary>
    Task<long?> TryGetContentLengthAsync(Uri url, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads <paramref name="url"/> into <paramref name="destinationDirectory"/>, resuming a
    /// previous "&lt;name&gt;.part" file when the server supports byte ranges.
    /// </summary>
    /// <param name="fileNameHint">Preferred file name (else Content-Disposition, else URL path).</param>
    /// <param name="progress">Receives (bytes of THIS file, total of THIS file, rate).</param>
    /// <returns>Absolute path of the completed file.</returns>
    /// <param name="options">Referer and whether a verification window may open (see <see cref="HttpDownloadOptions"/>).</param>
    /// <exception cref="Downloads.DeadLinkException">The server says the file is gone (404 / 410 / "File not found" page).</exception>
    /// <exception cref="Downloads.BrowserRequiredException">With <see cref="HttpDownloadOptions.AllowVerification"/> off:
    /// the server answered with a page (captcha, button, browser check) instead of the file.</exception>
    Task<string> DownloadAsync(Uri url, string destinationDirectory, string? fileNameHint,
        IProgress<TransferProgress>? progress, CancellationToken cancellationToken, HttpDownloadOptions? options = null);
}

/// <param name="Referer">Page the link was found on; sent as the Referer header.</param>
/// <param name="AllowVerification">
/// True: a captcha or landing page opens the verification window (old behaviour, used for direct links).
/// False: such a page throws <see cref="Downloads.BrowserRequiredException"/> so the caller can download in the
/// built-in browser instead.
/// </param>
public sealed record HttpDownloadOptions(Uri? Referer = null, bool AllowVerification = true);

/// <summary>Headless BitTorrent transport.</summary>
public interface ITorrentDownloader : IAsyncDisposable
{
    /// <summary>
    /// Downloads the payload of a magnet link or .torrent URL into <paramref name="saveDirectory"/>.
    /// Pausing = cancelling the token; fast-resume data lets the next call continue.
    /// </summary>
    /// <param name="keepForSeeding">Keep the torrent registered (stopped) so <see cref="SeedAsync"/> can share it later.</param>
    /// <returns>Absolute paths of every payload file.</returns>
    Task<IReadOnlyList<string>> DownloadAsync(DownloadLink link, string saveDirectory,
        IProgress<TransferProgress>? progress, CancellationToken cancellationToken, bool keepForSeeding = false);

    /// <summary>
    /// Shares a torrent kept by <see cref="DownloadAsync"/> until the ratio or time limit, or cancellation;
    /// then removes it from the engine (files stay). Returns at once if the torrent wasn't kept.
    /// </summary>
    Task SeedAsync(DownloadLink link, SeedOptions options, IProgress<SeedProgress>? progress, CancellationToken cancellationToken);

    /// <summary>Registers a finished torrent's files again (after a restart) so <see cref="SeedAsync"/> can share them.</summary>
    /// <returns>False when the files are gone.</returns>
    Task<bool> PrepareSeedingAsync(DownloadLink link, string saveDirectory, CancellationToken cancellationToken);

    /// <summary>Drops a torrent kept for seeding without seeding it.</summary>
    Task ForgetAsync(DownloadLink link);

    /// <summary>Engine-wide limits in bytes per second (0 = unlimited); applied live.</summary>
    Task ApplyGlobalLimitsAsync(int downloadBytesPerSecond, int uploadBytesPerSecond);
}

/// <param name="RatioLimit">Stop at this uploaded/size ratio (0 = none).</param>
/// <param name="TimeLimit">Stop after this long (null = none).</param>
public sealed record SeedOptions(double RatioLimit, TimeSpan? TimeLimit);

/// <param name="Ratio">Uploaded bytes divided by torrent size.</param>
/// <param name="UploadBytesPerSecond">Current upload rate.</param>
/// <param name="Peers">Connected peers.</param>
/// <param name="Elapsed">Time spent seeding.</param>
public readonly record struct SeedProgress(double Ratio, double UploadBytesPerSecond, int Peers, TimeSpan Elapsed);

/// <summary>Archive extraction, including split / multi-volume sets.</summary>
public interface IArchiveExtractor
{
    /// <summary>
    /// Extracts every archive set found among <paramref name="files"/> into
    /// <paramref name="destinationDirectory"/>. Non-archive files are copied as-is.
    /// </summary>
    /// <param name="progress">0–100 across all sets.</param>
    Task ExtractAsync(IReadOnlyList<string> files, string destinationDirectory, IProgress<double>? progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// Low disk space mode: unpacks a multi-volume RAR set while it downloads. Volumes come from
    /// <paramref name="feed"/> as they finish; each is closed and deleted as soon as the extractor has moved past it.
    /// Files already unpacked by an earlier (interrupted) run, with the right size, are skipped.
    /// </summary>
    /// <param name="startVolume">First volume to read (0, or a resume point from <see cref="StreamingCallbacks.ResumePoint"/>).</param>
    Task ExtractVolumesAsync(Extraction.VolumeFeed feed, int startVolume, string destinationDirectory, StreamingCallbacks callbacks,
        CancellationToken cancellationToken);
}

/// <param name="Progress">0–100, by volumes unpacked.</param>
/// <param name="ResumePoint">Volume where the entry being unpacked starts: a later run can start reading there.</param>
/// <param name="Solid">Reported once: a solid archive can only be unpacked from its first volume.</param>
public sealed record StreamingCallbacks(Action<double>? Progress = null, Action<int>? ResumePoint = null, Action<bool>? Solid = null);
