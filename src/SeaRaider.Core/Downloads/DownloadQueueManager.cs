using System.Collections.Concurrent;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Downloads;

/// <summary>Reorder commands for the download queue.</summary>
public enum QueueMove
{
    Up,
    Down,
    Top,
    Bottom,
}

/// <summary>Slot limits for the scheduler.</summary>
/// <param name="MaxActiveDownloads">Downloads (HTTP + torrent) transferring at once.</param>
/// <param name="MaxActiveTorrents">Of those, torrents.</param>
public sealed record QueueLimits(int MaxActiveDownloads, int MaxActiveTorrents)
{
    public static QueueLimits From(AppSettings settings) => new(
        Math.Clamp(settings.MaxConcurrentJobs, 1, 20),
        Math.Clamp(settings.MaxActiveTorrents, 1, 20));
}

/// <summary>How the global limits are shared out right now.</summary>
/// <param name="HttpDownloadBytesPerSecond">Shared by all HTTP downloads (0 = unlimited).</param>
/// <param name="TorrentDownloadBytesPerSecond">MonoTorrent engine download limit (0 = unlimited).</param>
/// <param name="TorrentUploadBytesPerSecond">MonoTorrent engine upload limit (0 = unlimited).</param>
public sealed record BandwidthShare(long HttpDownloadBytesPerSecond, int TorrentDownloadBytesPerSecond, int TorrentUploadBytesPerSecond);

/// <summary>
/// BitTorrent-client-style queue policy for <see cref="DownloadAndExtractManager"/>:
/// <list type="bullet">
///   <item><b>Slots.</b> Only <see cref="AppSettings.MaxConcurrentJobs"/> jobs transfer at once, and only
///         <see cref="AppSettings.MaxActiveTorrents"/> of them may be torrents. A job gives its slot back the moment
///         its download finishes, so the next game downloads while the previous one extracts.</item>
///   <item><b>Order.</b> Queued jobs start by <see cref="DownloadJob.QueuePosition"/> (Move Up / Down / Top / Bottom).
///         A torrent blocked by the torrent limit doesn't hold up an HTTP job behind it.</item>
///   <item><b>Force start.</b> Ignores the limits for that job (it still counts toward them for the others).</item>
///   <item><b>Extraction.</b> At most <see cref="AppSettings.MaxActiveExtractions"/> jobs extract at once
///         (<see cref="ExtractionSlots"/>).</item>
///   <item><b>Bandwidth.</b> A global download limit shared by everything (split between HTTP and the torrent
///         engine by how many of each are active), a global upload limit for torrents, and per-job limits
///         (<see cref="ThrottleFor"/>).</item>
/// </list>
/// The selection and ordering rules are static and side-effect free, so they're unit-tested directly.
/// </summary>
public sealed class DownloadQueueManager
{
    private readonly ConcurrentDictionary<Guid, TransferThrottle> _throttles = new();

    /// <summary>Shared by every HTTP download.</summary>
    public BandwidthLimiter HttpDownloadLimiter { get; } = new();

    /// <summary>Extraction gate.</summary>
    public AdjustableSlots ExtractionSlots { get; } = new(1);

    public void ApplySettings(AppSettings settings) =>
        ExtractionSlots.Capacity = Math.Clamp(settings.MaxActiveExtractions, 1, 8);

    /// <summary>The throttle for a job (created on first use), with the job's current limits applied.</summary>
    public TransferThrottle ThrottleFor(DownloadJob job)
    {
        var throttle = _throttles.GetOrAdd(job.Id, _ => new TransferThrottle(HttpDownloadLimiter));
        throttle.SetJobLimits(job.DownloadLimitKBps, job.UploadLimitKBps);
        return throttle;
    }

    public void Forget(Guid jobId) => _throttles.TryRemove(jobId, out _);

    // =============================================================================================
    // Policy
    // =============================================================================================

    /// <summary>Jobs that appear in the queue (everything not finished), in start order.</summary>
    public static List<DownloadJob> InQueueOrder(IEnumerable<DownloadJob> jobs) => jobs
        .Where(j => j.Status is not (DownloadJobStatus.Completed or DownloadJobStatus.Cancelled))
        .OrderBy(j => j.QueuePosition)
        .ThenBy(j => j.CreatedAtUtc)
        .ToList();

    /// <summary>Position for a newly queued job: after everything else.</summary>
    public static long NextPosition(IEnumerable<DownloadJob> jobs) =>
        jobs.Select(j => j.QueuePosition).DefaultIfEmpty(0).Max() + 1;

    /// <summary>
    /// Which queued jobs to start now. A job counts as active while it's transferring
    /// (<see cref="DownloadJobStatus.Downloading"/>); extracting jobs have given their slot back.
    /// </summary>
    /// <param name="jobs">All jobs.</param>
    /// <param name="isRunning">Whether a worker currently owns the job.</param>
    /// <param name="limits">Slot limits.</param>
    public static List<DownloadJob> SelectJobsToStart(IEnumerable<DownloadJob> jobs, Func<Guid, bool> isRunning, QueueLimits limits)
    {
        var all = jobs.ToList();
        var transferring = all.Where(j => isRunning(j.Id) && j.Status == DownloadJobStatus.Downloading).ToList();
        var activeDownloads = transferring.Count;
        var activeTorrents = transferring.Count(j => j.Transport == DownloadTransport.Torrent);

        var picks = new List<DownloadJob>();
        var waiting = all
            .Where(j => j.Status == DownloadJobStatus.Queued && !isRunning(j.Id))
            .OrderByDescending(j => j.ForceStart)
            .ThenBy(j => j.QueuePosition)
            .ThenBy(j => j.CreatedAtUtc);

        foreach (var job in waiting)
        {
            var isTorrent = job.Transport == DownloadTransport.Torrent;
            if (!job.ForceStart)
            {
                if (activeDownloads >= limits.MaxActiveDownloads) break;               // no slot for anyone behind it either
                if (isTorrent && activeTorrents >= limits.MaxActiveTorrents) continue; // let HTTP jobs behind it go
            }

            picks.Add(job);
            activeDownloads++;
            if (isTorrent) activeTorrents++;
        }
        return picks;
    }

    /// <summary>
    /// Moves a job within the queue and renumbers positions 1..n.
    /// </summary>
    /// <param name="queue">Jobs in queue order (from <see cref="InQueueOrder"/>); positions are rewritten.</param>
    /// <returns>True when the order changed.</returns>
    public static bool Move(List<DownloadJob> queue, Guid jobId, QueueMove move)
    {
        var index = queue.FindIndex(j => j.Id == jobId);
        if (index < 0) return false;

        var target = move switch
        {
            QueueMove.Up => Math.Max(0, index - 1),
            QueueMove.Down => Math.Min(queue.Count - 1, index + 1),
            QueueMove.Top => 0,
            _ => queue.Count - 1,
        };

        var changed = target != index;
        if (changed)
        {
            var job = queue[index];
            queue.RemoveAt(index);
            queue.Insert(target, job);
        }

        for (var i = 0; i < queue.Count; i++) queue[i].QueuePosition = i + 1;
        return changed;
    }

    /// <summary>
    /// Splits the global download limit between HTTP and the torrent engine in proportion to how many of each
    /// are transferring, so the total stays under the limit. Uploads are torrent-only.
    /// </summary>
    public static BandwidthShare ShareGlobalLimits(AppSettings settings, int activeHttp, int activeTorrents)
    {
        long global = Math.Max(0, settings.GlobalDownloadLimitKBps) * 1024L;
        var upload = Math.Max(0, settings.GlobalUploadLimitKBps) * 1024;
        if (global == 0) return new BandwidthShare(0, 0, upload);

        var total = activeHttp + activeTorrents;
        if (total == 0 || activeTorrents == 0) return new BandwidthShare(global, (int)Math.Min(int.MaxValue, global), upload);
        if (activeHttp == 0) return new BandwidthShare(global, (int)Math.Min(int.MaxValue, global), upload);

        var torrentShare = global * activeTorrents / total;
        return new BandwidthShare(global - torrentShare, (int)Math.Min(int.MaxValue, torrentShare), upload);
    }
}
