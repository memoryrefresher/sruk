using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Extraction;
using SeaRaider.Core.Installing;
using SeaRaider.Core.Scraping;
using SeaRaider.Core.WebChallenge;
using SeaRaider.Core.Launching;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Downloads;

/// <summary>
/// The one-click install pipeline.
///
/// <code>
///  EnqueueAsync ─► queue (ordered) ─► scheduler ─► job task (download slots: DownloadQueueManager)
///                               │
///                               ├─ 1. Download   HTTP (resumable, multipart) │ built-in browser (file hosts) │ MonoTorrent
///                               │                a host that lost a file → the same file on the next host → the torrent
///                               ├─ 2. Extract    SharpCompress → &lt;Library&gt;\.staging\&lt;job&gt;
///                               │                (low disk space mode: RAR volumes unpack while the next ones download)
///                               ├─ 3. Inspect    InstallerDetector: playable folder, or a setup program?
///                               │      playable → move staging → &lt;Library&gt;\&lt;Folder&gt;, restore saves, find .exe → Installed ("Play")
///                               │      setup    → move staging → &lt;Library&gt;\.searaider-setup\&lt;game&gt; → ReadyToInstall ("Install")
///                               └─ 4. Cleanup    delete temp archives / torrent payload (setup files go after the installer succeeds)
/// </code>
///
/// Folder and file names chosen in the pre-download dialog (<see cref="InstallRequest"/>) are stored on the
/// job: files are saved under their new names and the game lands in the chosen folder.
///
/// Extraction goes to a staging folder that is moved into place only on success, so a crash
/// or cancellation never leaves a half-installed game in the library.
///
/// Concurrency model: a single scheduler loop starts jobs when <see cref="DownloadQueueManager"/> says a slot is
/// free (woken by every queue change, and every 2 s). Each job is owned by exactly one task while running; it gives its
/// download slot back as soon as the transfer ends and then waits for an extraction slot. Finished torrents may keep
/// seeding in the background without holding any slot. The UI receives immutable <see cref="DownloadJobSnapshot"/>s
/// (throttled to ~5/s per job) via <see cref="JobChanged"/>.
/// </summary>
public sealed class DownloadAndExtractManager(
    ILibraryRepository repository,
    IHttpDownloader httpDownloader,
    ITorrentDownloader torrentDownloader,
    IArchiveExtractor extractor,
    IExecutableLocator executableLocator,
    ISaveDataGuard saveDataGuard,
    InstallerDetector installerDetector,
    IInstallerService installerService,
    DownloadQueueManager queue,
    FileHostResolver hostResolver,
    IBrowserDownloader browserDownloader,
    ILogger<DownloadAndExtractManager> logger) : IDownloadAndExtractManager
{
    private enum StopReason { None, Pause, Cancel }

    private static readonly TimeSpan PublishInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan SchedulerTick = TimeSpan.FromSeconds(2);

    /// <summary>How often a running job's progress is written to the database (crash recovery).</summary>
    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<Guid, DownloadJob> _jobs = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();
    private readonly ConcurrentDictionary<Guid, StopReason> _stopReasons = new();
    private readonly ConcurrentDictionary<Guid, long> _lastPublish = new();
    private readonly ConcurrentDictionary<Guid, long> _lastCheckpoint = new();
    private readonly ConcurrentDictionary<Guid, Task> _jobTasks = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _seeding = new();

    /// <summary>Hosts that turned out to need the built-in browser this session (no point asking them over HTTP again).</summary>
    private readonly ConcurrentDictionary<string, byte> _browserOnlyHosts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Free space kept in reserve when deciding whether a download fits.</summary>
    private const long SpaceMargin = 2L * 1024 * 1024 * 1024;
    private readonly SemaphoreSlim _wakeup = new(0, int.MaxValue);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private Task? _scheduler;
    private BandwidthShare? _appliedShare;
    private bool _started;

    public event EventHandler<DownloadJobSnapshot>? JobChanged;
    public event EventHandler<Guid>? JobRemoved;

    /// <summary>Unfinished jobs in queue order, then finished ones newest first.</summary>
    public IReadOnlyList<DownloadJobSnapshot> Jobs =>
        DownloadQueueManager.InQueueOrder(_jobs.Values).Select(j => j.ToSnapshot())
            .Concat(_jobs.Values.Where(j => j.IsTerminalOrCompleted()).OrderByDescending(j => j.CompletedAtUtc ?? j.CreatedAtUtc)
                .Select(j => j.ToSnapshot()))
            .ToList();

    // =============================================================================================
    // Lifecycle
    // =============================================================================================

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_started) return Task.CompletedTask;
            _started = true;
        }

        foreach (var job in repository.GetJobs())
        {
            // Anything that was mid-flight when the app closed resumes automatically
            // (.part files and torrent fast-resume make this cheap). Paused jobs stay paused.
            if (job.Status is DownloadJobStatus.Downloading or DownloadJobStatus.Extracting or DownloadJobStatus.Finalizing)
                job.Status = DownloadJobStatus.Queued;

            // Jobs created by v0.1 only have Links.
            if (job.Files.Count == 0 && job.Links.Count > 0)
                job.Files = job.Links.Select(DownloadFileSpec.FromLink).ToList();

            _jobs[job.Id] = job;
        }

        // Jobs from before v0.4 have no queue position: keep their age order.
        var position = 1L;
        foreach (var job in DownloadQueueManager.InQueueOrder(_jobs.Values).OrderBy(j => j.QueuePosition == 0 ? 1 : 0)
                     .ThenBy(j => j.QueuePosition).ThenBy(j => j.CreatedAtUtc))
        {
            if (job.QueuePosition != position) { job.QueuePosition = position; repository.UpsertJob(job); }
            position++;
        }

        var settings = repository.GetSettings();
        queue.ApplySettings(settings);
        repository.SettingsChanged += (_, changed) =>
        {
            queue.ApplySettings(changed);
            _appliedShare = null; // re-apply bandwidth on the next tick
            Signal();
        };

        _scheduler = Task.Run(() => SchedulerLoopAsync(_shutdown.Token), CancellationToken.None);

        // Torrents that were seeding when the app closed (or crashed): share them again, or tidy up their files.
        foreach (var seeding in _jobs.Values.Where(j => j.Status == DownloadJobStatus.Completed && j.SeedingActive).ToList())
            _ = Task.Run(() => ResumeSeedingAfterRestartAsync(seeding), CancellationToken.None);
        logger.LogInformation("Download queue started: {Jobs} restored job(s), {Max} active download(s) allowed",
            _jobs.Count, QueueLimits.From(settings).MaxActiveDownloads);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        try
        {
            var pending = _jobTasks.Values.ToList();
            if (_scheduler is not null) pending.Add(_scheduler);
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            // Workers persist their state as they unwind; nothing else to do.
        }
        _shutdown.Dispose();
    }

    // =============================================================================================
    // Public commands
    // =============================================================================================

    public InstallPreview Preview(GameMetadata game, DownloadLink? preferredLink = null)
    {
        var settings = repository.GetSettings();
        var plan = DownloadPlanner.Plan(game, preferredLink, settings.PreferTorrents);
        var files = plan.Links.Select(DownloadFileSpec.FromLink).ToList();
        var canRename = plan.Transport == DownloadTransport.Http && files.All(f => f.OriginalFileName.Length > 0);
        var baseName = canRename ? FileNameRules.Split(files[0].OriginalFileName).BaseName : string.Empty;

        return new InstallPreview(plan.Transport, files, PathSafety.ToFolderName(game.Title), settings.LibraryRoot, baseName,
            canRename, settings.PreferSilentInstall, settings.AutoRunInstallers)
        {
            Space = SpaceEstimator.Estimate(game, plan.Transport, plan.Links, files, settings),
        };
    }

    public Task<DownloadJobSnapshot> EnqueueAsync(GameMetadata game, DownloadLink? preferredLink = null,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(new InstallRequest(game) { PreferredLink = preferredLink }, cancellationToken);

    public Task<DownloadJobSnapshot> EnqueueAsync(InstallRequest request, CancellationToken cancellationToken = default)
    {
        var game = request.Game;
        if (repository.GetInstall(game.Id) is { State: GameInstallState.Installed or GameInstallState.ReadyToInstall or GameInstallState.RunningInstaller })
            throw new InvalidOperationException($"{game.Title} is already installed or waiting to be installed.");

        if (_jobs.Values.Any(j => j.GameId == game.Id && j.Status is not (DownloadJobStatus.Completed or DownloadJobStatus.Cancelled)))
            throw new InvalidOperationException($"{game.Title} is already in the download queue.");

        var settings = repository.GetSettings();
        var plan = DownloadPlanner.Plan(game, request.PreferredLink, settings.PreferTorrents);

        // ---- user-chosen names ----------------------------------------------------------------
        var folderName = PathSafety.ToFolderName(string.IsNullOrWhiteSpace(request.InstallFolderName) ? game.Title : request.InstallFolderName);
        var files = plan.Links.Select(DownloadFileSpec.FromLink).ToList();
        if (plan.Transport == DownloadTransport.Http && !string.IsNullOrWhiteSpace(request.FileBaseName))
        {
            if (FileNameRules.Validate(request.FileBaseName) is { } error) throw new ArgumentException(error);
            foreach (var file in files.Where(f => f.OriginalFileName.Length > 0))
                file.SaveAsFileName = FileNameRules.Rename(file.OriginalFileName, request.FileBaseName);
            if (files.Select(f => f.EffectiveFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count)
                throw new ArgumentException("Renaming would give two files the same name.");
        }

        // The same files on other hosts, for when a host has lost one.
        foreach (var (file, link) in files.Zip(plan.Links))
            file.Alternates = DownloadPlanner.AlternatesFor(game.DownloadLinks, link).Select(DownloadSource.FromLink).ToList();

        Directory.CreateDirectory(settings.LibraryRoot);
        var job = new DownloadJob
        {
            GameId = game.Id,
            GameTitle = game.Title,
            CoverImageUrl = game.CoverImageUrl,
            Transport = plan.Transport,
            Links = plan.Links.ToList(),
            Files = files,
            Status = DownloadJobStatus.Queued,
            InstallFolderName = folderName,
            TargetDirectory = UniqueDirectory(Path.Combine(settings.LibraryRoot, folderName)),
            RunInstallerWhenReady = request.RunInstallerWhenReady ?? settings.AutoRunInstallers,
            SilentInstall = request.SilentInstall ?? settings.PreferSilentInstall,
            CustomInstallerArguments = string.IsNullOrWhiteSpace(request.CustomInstallerArguments) ? null : request.CustomInstallerArguments.Trim(),
            LaunchFileName = string.IsNullOrWhiteSpace(request.LaunchFileName) ? null : request.LaunchFileName.Trim(),
            QueuePosition = DownloadQueueManager.NextPosition(_jobs.Values),
            SourcePageUrl = string.IsNullOrWhiteSpace(game.PageUrl) ? null : game.PageUrl,
            FallbackTorrent = plan.Transport == DownloadTransport.Http ? DownloadPlanner.TorrentOf(game.DownloadLinks) : null,
        };
        job.TempDirectory = Path.Combine(settings.TempRoot, job.Id.ToString("N"));
        job.StatusDetail = plan.Transport == DownloadTransport.Torrent
            ? "Queued · torrent"
            : $"Queued · {files.Count} file(s) via {NameOf(files[0])}";

        _jobs[job.Id] = job;
        repository.UpsertJob(job);
        UpdateInstall(job, GameInstallState.Queued);
        Publish(job, force: true);

        Signal();
        logger.LogInformation("Queued {Title} via {Transport} ({Count} file(s)) into {Dir}", game.Title, plan.Transport, files.Count, job.TargetDirectory);
        return Task.FromResult(job.ToSnapshot());
    }

    public Task PauseAsync(Guid jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return Task.CompletedTask;

        lock (_gate)
        {
            job.ForceStart = false;
            if (_running.TryGetValue(jobId, out var cts))
            {
                _stopReasons[jobId] = StopReason.Pause;
                cts.Cancel(); // the job task records the Paused state as it unwinds
            }
            else if (job.Status == DownloadJobStatus.Queued)
            {
                SetStatus(job, DownloadJobStatus.Paused, "Paused");
            }
        }
        Signal();
        return Task.CompletedTask;
    }

    public Task ResumeAsync(Guid jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return Task.CompletedTask;

        lock (_gate)
        {
            if (job.Status is not (DownloadJobStatus.Paused or DownloadJobStatus.Failed)) return Task.CompletedTask;
            job.Error = null;
            SetStatus(job, DownloadJobStatus.Queued, "Queued");
            UpdateInstall(job, GameInstallState.Queued);
        }
        Signal();
        return Task.CompletedTask;
    }

    public Task ForceStartAsync(Guid jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return Task.CompletedTask;

        lock (_gate)
        {
            if (_running.ContainsKey(jobId) || job.IsTerminalOrCompleted()) return Task.CompletedTask;
            job.ForceStart = true;
            job.Error = null;
            SetStatus(job, DownloadJobStatus.Queued, "Starting (forced)…");
            UpdateInstall(job, GameInstallState.Queued);
        }
        Signal();
        return Task.CompletedTask;
    }

    public Task MoveAsync(Guid jobId, QueueMove move)
    {
        lock (_gate)
        {
            var ordered = DownloadQueueManager.InQueueOrder(_jobs.Values);
            var before = ordered.ToDictionary(j => j.Id, j => j.QueuePosition);
            DownloadQueueManager.Move(ordered, jobId, move);
            foreach (var job in ordered.Where(j => before[j.Id] != j.QueuePosition))
            {
                repository.UpsertJob(job);
                Publish(job, force: true);
            }
        }
        Signal();
        return Task.CompletedTask;
    }

    public Task SetJobLimitsAsync(Guid jobId, int downloadKBps, int uploadKBps)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return Task.CompletedTask;
        job.DownloadLimitKBps = Math.Clamp(downloadKBps, 0, 10_000_000);
        job.UploadLimitKBps = Math.Clamp(uploadKBps, 0, 10_000_000);
        queue.ThrottleFor(job); // running transfers read it on their next chunk / poll
        repository.UpsertJob(job);
        Publish(job, force: true);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops seeding now: the seeding task is cancelled, which stops the torrent (bounded tracker announce), removes it
    /// from the engine so its files are released, clears the persisted seeding flag and cleans up the downloaded files.
    /// </summary>
    public async Task StopSeedingAsync(Guid jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return;
        if (_seeding.TryGetValue(jobId, out var cts))
        {
            job.StatusDetail = "Stopping seeding…";
            Publish(job, force: true);
            CancelQuietly(cts);
            return; // the seeding task's finally block does the rest
        }

        // Not running (e.g. the flag survived a crash): just clear it.
        if (job.SeedingActive)
        {
            job.SeedingActive = false;
            job.IsSeeding = false;
            repository.UpsertJob(job);
            await torrentDownloader.ForgetAsync(TorrentLink(job)).ConfigureAwait(false);
            Publish(job, force: true);
        }
    }

    private async Task ResumeSeedingAfterRestartAsync(DownloadJob job)
    {
        var settings = repository.GetSettings();
        var completion = job.ProducedInstaller ? "Ready to install" : "Installed";
        try
        {
            if (job.Transport == DownloadTransport.Torrent && settings.SeedAfterDownload &&
                _seeding.Count < Math.Max(0, settings.MaxActiveSeeds) &&
                await torrentDownloader.PrepareSeedingAsync(TorrentLink(job), job.TempDirectory, _shutdown.Token).ConfigureAwait(false))
            {
                logger.LogInformation("Resuming seeding of {Title} after restart", job.GameTitle);
                StartSeeding(job, completion, settings);
                return;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Couldn't resume seeding {Title}", job.GameTitle);
        }

        job.SeedingActive = false;
        repository.UpsertJob(job);
        if (settings.DeleteArchivesAfterInstall) await DeleteQuietlyAsync(job.TempDirectory).ConfigureAwait(false);
    }

    private static void CancelQuietly(CancellationTokenSource cts)
    {
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { /* seeding just ended */ }
    }

    public async Task CancelAsync(Guid jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return;

        lock (_gate)
        {
            if (_running.TryGetValue(jobId, out var cts))
            {
                _stopReasons[jobId] = StopReason.Cancel;
                cts.Cancel(); // the worker cleans up as it unwinds
                return;
            }
        }

        if (job.IsTerminalOrCompleted()) return;
        await CleanupCancelledAsync(job).ConfigureAwait(false);
    }

    public async Task RemoveAsync(Guid jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return;
        if (_running.ContainsKey(jobId) || job.Status == DownloadJobStatus.Queued)
            throw new InvalidOperationException("Cancel the download before removing it.");

        if (job.Status is DownloadJobStatus.Paused or DownloadJobStatus.Failed)
            await CleanupCancelledAsync(job).ConfigureAwait(false);

        if (_seeding.TryGetValue(jobId, out var seeding)) CancelQuietly(seeding);
        _jobs.TryRemove(jobId, out _);
        _lastPublish.TryRemove(jobId, out _);
        queue.Forget(jobId);
        repository.DeleteJob(jobId);
        JobRemoved?.Invoke(this, jobId);
    }

    // =============================================================================================
    // Worker
    // =============================================================================================

    /// <summary>Wakes the scheduler (new job, slot freed, order or settings changed).</summary>
    private void Signal()
    {
        try { _wakeup.Release(); }
        catch (SemaphoreFullException) { /* already awake */ }
    }

    private async Task SchedulerLoopAsync(CancellationToken shutdown)
    {
        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                StartEligibleJobs(shutdown);
                await ApplyBandwidthAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Download scheduler tick failed");
            }

            try
            {
                await _wakeup.WaitAsync(SchedulerTick, shutdown).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void StartEligibleJobs(CancellationToken shutdown)
    {
        lock (_gate)
        {
            var limits = QueueLimits.From(repository.GetSettings());
            foreach (var job in DownloadQueueManager.SelectJobsToStart(_jobs.Values, _running.ContainsKey, limits))
            {
                var jobCts = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
                _running[job.Id] = jobCts;
                _stopReasons.TryRemove(job.Id, out _);
                job.Status = DownloadJobStatus.Downloading; // takes the slot now, before the task runs
                job.StatusDetail = "Starting…";

                var task = Task.Run(() => RunJobOwnedAsync(job, jobCts), CancellationToken.None);
                _jobTasks[job.Id] = task;
            }
        }
    }

    private async Task RunJobOwnedAsync(DownloadJob job, CancellationTokenSource jobCts)
    {
        try
        {
            await RunJobAsync(job, jobCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Job task for {Title} crashed", job.GameTitle);
        }
        finally
        {
            _running.TryRemove(job.Id, out _);
            _jobTasks.TryRemove(job.Id, out _);
            jobCts.Dispose();
            if (job.ForceStart && job.Status != DownloadJobStatus.Queued)
            {
                job.ForceStart = false;
                repository.UpsertJob(job);
            }
            Signal(); // a slot is free
        }
    }

    /// <summary>Shares the global limits between HTTP and the torrent engine by what's transferring now.</summary>
    private async Task ApplyBandwidthAsync()
    {
        var transferring = _jobs.Values.Where(j => _running.ContainsKey(j.Id) && j.Status == DownloadJobStatus.Downloading).ToList();
        var share = DownloadQueueManager.ShareGlobalLimits(repository.GetSettings(),
            transferring.Count(j => j.Transport == DownloadTransport.Http),
            transferring.Count(j => j.Transport == DownloadTransport.Torrent));
        if (share == _appliedShare) return;

        queue.HttpDownloadLimiter.SetLimit(share.HttpDownloadBytesPerSecond);
        await torrentDownloader.ApplyGlobalLimitsAsync(share.TorrentDownloadBytesPerSecond, share.TorrentUploadBytesPerSecond)
            .ConfigureAwait(false);
        _appliedShare = share;
    }

    private async Task RunJobAsync(DownloadJob job, CancellationToken ct)
    {
        var settings = repository.GetSettings();
        var stagingDirectory = Path.Combine(settings.LibraryRoot, ".searaider-staging", job.Id.ToString("N"));

        // The job's speed limits flow to the transports with the async context.
        using var throttleScope = TransferThrottle.Use(queue.ThrottleFor(job));
        var seedingPlanned = job.Transport == DownloadTransport.Torrent && settings.SeedAfterDownload;

        // While a verification window is open for this job's download, the row says so.
        using var verificationStatus = ChallengeContext.Use(message =>
        {
            job.StatusDetail = message ?? "Verification complete, continuing…";
            job.BytesPerSecond = 0;
            Publish(job, force: true);
        });

        try
        {
            // ---- 1. Download (in low disk space mode: and unpack at the same time) ------------------
            Directory.CreateDirectory(job.TempDirectory);
            SetStatus(job, DownloadJobStatus.Downloading, "Starting…");
            UpdateInstall(job, GameInstallState.Downloading);

            // Short links and FileCrypt containers: find the file host links (all of a container's parts) first.
            if (job.Transport == DownloadTransport.Http) await ExpandLinksAsync(job, ct).ConfigureAwait(false);

            var unpacked = false;
            if (job.Transport == DownloadTransport.Http && DecideStreaming(job, settings))
            {
                try
                {
                    await DownloadAndUnpackAsync(job, stagingDirectory, ct).ConfigureAwait(false);
                    unpacked = true;
                }
                catch (NoWorkingSourceException ex) when (job.FallbackTorrent is not null)
                {
                    await SwitchToTorrentAsync(job, stagingDirectory, ex).ConfigureAwait(false);
                    seedingPlanned = settings.SeedAfterDownload;
                }
            }

            if (!unpacked)
            {
                try
                {
                    job.DownloadedFiles = job.Transport == DownloadTransport.Torrent
                        ? (await DownloadTorrentAsync(job, seedingPlanned, ct).ConfigureAwait(false)).ToList()
                        : await DownloadHttpAsync(job, feed: null, startVolume: 0, ct).ConfigureAwait(false);
                }
                catch (NoWorkingSourceException ex) when (job.FallbackTorrent is not null && job.Transport == DownloadTransport.Http)
                {
                    // Every host lost a file: the page's torrent is the last way to get the game.
                    await SwitchToTorrentAsync(job, stagingDirectory, ex).ConfigureAwait(false);
                    seedingPlanned = settings.SeedAfterDownload;
                    job.DownloadedFiles = (await DownloadTorrentAsync(job, seedingPlanned, ct).ConfigureAwait(false)).ToList();
                }
                repository.UpsertJob(job);

                // ---- 2. Extract -----------------------------------------------------------------
                // The transfer is done: give the download slot back so the next game starts while this one extracts.
                job.BytesPerSecond = 0;
                SetStatus(job, DownloadJobStatus.Extracting, "Waiting for another game to finish extracting…");
                UpdateInstall(job, GameInstallState.Extracting);
                Signal();

                using var extractionSlot = await queue.ExtractionSlots.AcquireAsync(ct).ConfigureAwait(false);
                EnsureFreeSpace(settings.LibraryRoot, job.DownloadedFiles.Sum(f => new FileInfo(f).Length));
                SetStatus(job, DownloadJobStatus.Extracting, "Extracting…");

                if (Directory.Exists(stagingDirectory)) await PathSafety.DeleteDirectoryRobustAsync(stagingDirectory, ct).ConfigureAwait(false);
                var extractProgress = new SyncProgress<double>(percent =>
                {
                    job.ExtractionPercent = percent;
                    job.StatusDetail = $"Extracting… {percent:0}%";
                    Publish(job);
                });
                await extractor.ExtractAsync(job.DownloadedFiles, stagingDirectory, extractProgress, ct).ConfigureAwait(false);
            }

            // ---- 3. Inspect: playable folder or setup program? -----------------------------------
            SetStatus(job, DownloadJobStatus.Finalizing, "Checking what was extracted…");
            ct.ThrowIfCancellationRequested();

            var inspection = installerDetector.Inspect(stagingDirectory, job.GameTitle);
            var install = repository.GetInstall(job.GameId) ?? new InstalledGame { GameId = job.GameId };
            install.Title = job.GameTitle;
            install.CoverImageUrl = job.CoverImageUrl;

            string completionMessage;
            if (inspection.IsInstaller)
            {
                // Keep the setup files in the library's setup area (same drive → instant move) until setup succeeds.
                var setupDirectory = SetupPaths.SetupDirectory(settings.LibraryRoot, job.GameId);
                if (Directory.Exists(setupDirectory)) await PathSafety.DeleteDirectoryRobustAsync(setupDirectory, ct).ConfigureAwait(false);
                Directory.CreateDirectory(Path.GetDirectoryName(setupDirectory)!);
                Directory.Move(stagingDirectory, setupDirectory);
                TryDeleteEmptyStagingRoot(settings.LibraryRoot);

                var installerPath = Path.Combine(setupDirectory, Path.GetRelativePath(stagingDirectory, inspection.InstallerPath!));
                if (Directory.Exists(job.TargetDirectory) && Directory.EnumerateFileSystemEntries(job.TargetDirectory).Any())
                    job.TargetDirectory = UniqueDirectory(job.TargetDirectory);

                install.State = GameInstallState.ReadyToInstall;
                install.InstallerState = InstallerState.Pending;
                install.InstallerKind = inspection.Kind;
                install.InstallerEngine = inspection.Engine?.Describe();
                install.InstallerIsRepack = inspection.Engine?.IsRepack ?? false;
                install.InstallerPath = installerPath;
                install.SetupDirectory = setupDirectory;
                install.InstallDirectory = job.TargetDirectory; // where the installer will be told to install
                install.SilentInstall = job.SilentInstall;
                install.InstallerArguments = job.CustomInstallerArguments;
                install.LaunchFileHint = job.LaunchFileName;
                install.ExecutablePath = null;
                install.LastError = null;
                repository.UpsertInstall(install);

                job.ProducedInstaller = true;
                completionMessage = inspection.Kind == InstallerKind.Unknown
                    ? "Ready to install (setup wizard)"
                    : $"Ready to install ({inspection.Engine?.Describe() ?? inspection.Kind.ToString()})";
            }
            else
            {
                if (Directory.Exists(job.TargetDirectory) && Directory.EnumerateFileSystemEntries(job.TargetDirectory).Any())
                    job.TargetDirectory = UniqueDirectory(job.TargetDirectory);
                else if (Directory.Exists(job.TargetDirectory))
                    Directory.Delete(job.TargetDirectory);

                Directory.Move(stagingDirectory, job.TargetDirectory); // same volume → instant
                TryDeleteEmptyStagingRoot(settings.LibraryRoot);

                if (settings.PreserveInFolderSaves)
                    await saveDataGuard.RestoreAsync(job.GameId, job.TargetDirectory, CancellationToken.None).ConfigureAwait(false);

                var exe = LaunchFiles.Resolve(job.TargetDirectory, job.LaunchFileName) ??
                          executableLocator.FindPrimaryExecutable(job.TargetDirectory, job.GameTitle);
                install.LaunchFileHint = job.LaunchFileName;
                install.InstallDirectory = job.TargetDirectory;
                install.ExecutablePath = exe;
                install.SizeOnDiskBytes = PathSafety.GetDirectorySize(job.TargetDirectory);
                install.InstalledAtUtc = DateTime.UtcNow;
                install.State = GameInstallState.Installed;
                install.InstallerState = InstallerState.None;
                install.LastError = exe is null ? "No executable found. Choose one from the Library." : null;
                repository.UpsertInstall(install);
                completionMessage = exe is null ? "Installed (pick the game's .exe in Library)" : "Installed";
            }

            // ---- 4. Cleanup (or seed first, then clean up) -----------------------------------------
            job.StreamingExtraction = false;
            job.StreamFirstVolume = 0;
            var seedNow = seedingPlanned && _seeding.Count < Math.Max(0, settings.MaxActiveSeeds);
            if (seedingPlanned && !seedNow) await torrentDownloader.ForgetAsync(TorrentLink(job)).ConfigureAwait(false);

            if (!seedNow && settings.DeleteArchivesAfterInstall)
            {
                if (job.SpaceSavedBytes == 0) job.SpaceSavedBytes = PathSafety.GetDirectorySize(job.TempDirectory);
                await PathSafety.DeleteDirectoryRobustAsync(job.TempDirectory, CancellationToken.None).ConfigureAwait(false);
            }

            job.CompletedAtUtc = DateTime.UtcNow;
            job.BytesPerSecond = 0;
            SetStatus(job, DownloadJobStatus.Completed, completionMessage);
            logger.LogInformation("{Title}: {Message} ({Dir})", job.GameTitle, completionMessage, job.TargetDirectory);

            if (seedNow) StartSeeding(job, completionMessage, settings);

            if (job.ProducedInstaller && job.RunInstallerWhenReady)
            {
                // Outside the job: a setup wizard can run for a long time and must not hold a download slot.
                var gameId = job.GameId;
                _ = Task.Run(async () =>
                {
                    try { await installerService.RunInstallerAsync(gameId).ConfigureAwait(false); }
                    catch (Exception ex) { logger.LogWarning(ex, "Automatic installer run failed for {Game}", gameId); }
                });
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (seedingPlanned) await torrentDownloader.ForgetAsync(TorrentLink(job)).ConfigureAwait(false);
            var reason = _stopReasons.TryRemove(job.Id, out var r) ? r : StopReason.None;
            job.BytesPerSecond = 0;

            // Low disk space mode keeps what's unpacked: the parts it came from are already deleted.
            if (!job.StreamingExtraction || reason == StopReason.Cancel)
            {
                await DeleteQuietlyAsync(stagingDirectory).ConfigureAwait(false);
                job.StreamingExtraction = false;
                job.StreamFirstVolume = 0;
            }

            switch (reason)
            {
                case StopReason.Pause:
                    SetStatus(job, DownloadJobStatus.Paused, "Paused");
                    break;
                case StopReason.Cancel:
                    await CleanupCancelledAsync(job).ConfigureAwait(false);
                    break;
                default:
                    // App shutdown: leave it Queued so it resumes on next launch.
                    job.Status = DownloadJobStatus.Queued;
                    repository.UpsertJob(job);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job {Title} failed", job.GameTitle);
            if (seedingPlanned) await torrentDownloader.ForgetAsync(TorrentLink(job)).ConfigureAwait(false);

            // A download problem in low disk space mode keeps what's unpacked, so Retry continues from there.
            // A broken archive starts over.
            if (!job.StreamingExtraction || ex is StreamingExtractionException)
            {
                await DeleteQuietlyAsync(stagingDirectory).ConfigureAwait(false);
                job.StreamFirstVolume = 0;
            }

            job.Error = Describe(ex);
            job.BytesPerSecond = 0;
            SetStatus(job, DownloadJobStatus.Failed, job.Error);

            var install = repository.GetInstall(job.GameId);
            if (install is not null)
            {
                install.State = GameInstallState.Failed;
                install.LastError = job.Error;
                repository.UpsertInstall(install);
            }
        }
    }

    // ---- transports -----------------------------------------------------------------------------

    /// <param name="feed">Low disk space mode: finished volumes go to the extractor, which may hold the download back.</param>
    /// <param name="startVolume">First file to fetch (low disk space mode resumes after the unpacked, deleted volumes).</param>
    private async Task<List<string>> DownloadHttpAsync(DownloadJob job, VolumeFeed? feed, int startVolume, CancellationToken ct)
    {
        var files = job.Files;

        // Best-effort total size across all volumes (parallel HEADs) for an honest progress bar / ETA.
        // File-host pages only reveal their size once resolved, so those sets estimate from the first part.
        job.StatusDetail = "Checking files…";
        Publish(job, force: true);
        var onHosts = files.Any(f => FileHosts.Find(f.Host) is not null);

        // File-host pages don't say how big their file is before the download starts. So the bar has a size from the
        // start, use the page's own size ("Repack Size: 4.1 GB"); the first part's real size corrects it below.
        var pageSize = job.Links.Select(l => Formatting.ParseBytes(l.SizeText)).FirstOrDefault(b => b is > 0) ?? 0;
        if (job.TotalBytes == 0 && pageSize > 0 && files.Count > 1) job.TotalBytes = pageSize;
        var estimated = job.TotalBytes > 0 && onHosts;
        if (!onHosts && job.TotalBytes == 0)
        {
            var sizes = await Task.WhenAll(files.Select(f => httpDownloader.TryGetContentLengthAsync(new Uri(f.Url), ct))).ConfigureAwait(false);
            job.TotalBytes = sizes.All(s => s.HasValue) ? sizes.Sum(s => s!.Value) : 0;
        }

        var paths = new List<string>();
        long completedBytes = startVolume > 0 && job.TotalBytes > 0 ? job.TotalBytes * startVolume / files.Count : 0;

        for (var i = startVolume; i < files.Count; i++)
        {
            var spec = files[i];
            var partLabel = files.Count > 1 ? $"Part {i + 1} of {files.Count}" : "Downloading";

            if (feed is not null)
            {
                if (feed.IsAhead(i))
                {
                    job.BytesPerSecond = 0;
                    job.StatusDetail = $"{partLabel}: waiting for part {feed.OpenedIndex + 1} to finish unpacking (saving disk space)…";
                    Publish(job, force: true);
                }
                await feed.WaitForRoomAsync(i, ct).ConfigureAwait(false);
            }

            var statusLabel = feed is null ? partLabel : $"{partLabel} · unpacking part {Math.Max(1, feed.OpenedIndex + 1)}";
            var isFirstPart = i == startVolume;
            var progress = new SyncProgress<TransferProgress>(p =>
            {
                if (p.Detail is not null && p.BytesDownloaded == 0)
                {
                    job.StatusDetail = p.Detail; // e.g. "Connection lost — retrying in 4s"
                }
                else
                {
                    job.BytesDownloaded = completedBytes + p.BytesDownloaded;
                    job.BytesPerSecond = p.BytesPerSecond;
                    if (files.Count == 1 && p.TotalBytes > 0) job.TotalBytes = p.TotalBytes;
                    else if (estimated && isFirstPart && p.TotalBytes > 0 && files.Count > 1)
                    {
                        // Parts are cut to one size (the last is smaller): this part × parts, unless the page's size is
                        // close to that, which also accounts for the smaller last part.
                        var byParts = p.TotalBytes * files.Count;
                        if (pageSize <= 0 || pageSize < byParts * 0.5 || pageSize > byParts * 1.2) job.TotalBytes = byParts;
                    }
                    else if (job.TotalBytes == 0 && p.TotalBytes > 0 && files.Count > 1)
                    {
                        job.TotalBytes = p.TotalBytes * files.Count;
                        estimated = true;
                    }
                    job.StatusDetail = p.Detail ?? statusLabel;
                }
                Publish(job);
            });

            var path = await FetchFileAsync(job, spec, partLabel, progress, ct).ConfigureAwait(false);

            spec.LocalPath = path;
            var fileSize = new FileInfo(path).Length;
            if (onHosts && job.TotalBytes == 0 && files.Count > 1) job.TotalBytes = fileSize * files.Count; // estimate: parts are equal-sized
            completedBytes += fileSize;
            job.BytesDownloaded = completedBytes;
            paths.Add(path);

            job.DownloadedFiles = paths.ToList();
            repository.UpsertJob(job); // checkpoint after every volume
            feed?.Completed(i, path);
        }

        if (feed is not null)
        {
            // All volumes are here; the extractor finishes the last ones. The download slot is free from now on.
            job.BytesPerSecond = 0;
            job.SpaceSavedBytes = completedBytes; // every volume was deleted as soon as it was unpacked
            SetStatus(job, DownloadJobStatus.Extracting, "Unpacking the last parts…");
            UpdateInstall(job, GameInstallState.Extracting);
            Signal();
        }
        // The real size now that every part is here (the bar's size was an estimate for host downloads).
        if (completedBytes > 0 && startVolume == 0) job.TotalBytes = completedBytes;
        job.BytesDownloaded = Math.Max(job.BytesDownloaded, completedBytes);
        return paths;
    }

    /// <summary>
    /// One file, from the first source that still has it: the chosen host, then the same file on the other hosts.
    /// </summary>
    /// <exception cref="NoWorkingSourceException">Every source has lost the file.</exception>
    private async Task<string> FetchFileAsync(DownloadJob job, DownloadFileSpec spec, string partLabel,
        IProgress<TransferProgress> progress, CancellationToken ct)
    {
        var sources = new List<DownloadSource> { new() { Url = spec.Url, Host = spec.Host, FileName = spec.OriginalFileName } };
        sources.AddRange(spec.Alternates.Where(a => !string.Equals(a.Url, spec.Url, StringComparison.Ordinal)));
        var gone = new List<string>();

        for (var s = 0; s < sources.Count; s++)
        {
            if (s > 0)
            {
                spec.UseSource(sources[s]);
                job.StatusDetail = $"{partLabel}: {string.Join(", ", gone)} lost the file. Trying {HostName(sources[s].Url)}…";
                Publish(job, force: true);
                repository.UpsertJob(job); // a restart continues with the host that works
            }

            try
            {
                return await FetchFromSourceAsync(job, spec, partLabel, progress, ct).ConfigureAwait(false);
            }
            catch (DeadLinkException ex)
            {
                logger.LogWarning("{Title} {Part}: {Message}", job.GameTitle, partLabel, ex.Message);
                gone.Add(NameOf(spec));
            }
        }

        throw new NoWorkingSourceException(
            $"{partLabel} is no longer available on {JoinNames(gone)}." +
            (job.FallbackTorrent is null ? " Try another source for this game." : string.Empty));
    }

    /// <summary>
    /// Plain links and hosts SeaRaider can resolve go over HTTP (resumable, speed limits apply). Hosts that need a person
    /// or a real browser — buttons, captchas, Cloudflare checks — download in the built-in browser.
    /// </summary>
    private async Task<string> FetchFromSourceAsync(DownloadJob job, DownloadFileSpec spec, string partLabel,
        IProgress<TransferProgress> progress, CancellationToken ct)
    {
        var saveAs = string.IsNullOrWhiteSpace(spec.EffectiveFileName) ? null : spec.EffectiveFileName;
        if (saveAs is not null && File.Exists(Path.Combine(job.TempDirectory, saveAs))) return Path.Combine(job.TempDirectory, saveAs);

        // A short link: go straight to the host it leads to when its page says where that is.
        await UnshortenAsync(job, spec, wantTorrent: false, ct).ConfigureAwait(false);
        saveAs = string.IsNullOrWhiteSpace(spec.EffectiveFileName) ? null : spec.EffectiveFileName;

        var source = new Uri(spec.Url);
        var host = FileHosts.Find(source.Host);
        var name = NameOf(spec);
        var referer = Uri.TryCreate(job.SourcePageUrl, UriKind.Absolute, out var page) ? page : null;
        var browser = browserDownloader.IsAvailable;
        var needsBrowser = host?.Access == HostAccess.Browser || _browserOnlyHosts.ContainsKey(host?.Key ?? source.Host);

        if (!needsBrowser || !browser)
        {
            try
            {
                var target = source;
                if (host is { Access: HostAccess.Resolvable })
                {
                    job.StatusDetail = $"{partLabel}: getting the file link from {name}…";
                    Publish(job, force: true);
                    var resolved = await hostResolver.ResolveAsync(source, ct).ConfigureAwait(false);
                    if (resolved is null && browser) throw new BrowserRequiredException($"{name} didn't give SeaRaider a file link.");
                    if (resolved is not null)
                    {
                        target = resolved.FileUrl;
                        saveAs ??= resolved.FileName;
                    }
                }

                // Without the built-in browser, the old verification window is the only way past a captcha.
                var options = new HttpDownloadOptions(referer, AllowVerification: !browser);
                return await httpDownloader.DownloadAsync(target, job.TempDirectory, saveAs, progress, ct, options).ConfigureAwait(false);
            }
            catch (Exception ex) when (browser && ex is BrowserRequiredException
                                           or HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable })
            {
                logger.LogInformation("{Host} needs a browser ({Reason}); downloading in the built-in browser", name, ex.Message);
                _browserOnlyHosts[host?.Key ?? source.Host] = 0;
            }
        }

        if (!browser)
            throw new InvalidOperationException($"{name} only works in a browser, and SeaRaider's built-in browser isn't available here.");

        job.StatusDetail = $"{partLabel}: fetching download from {name}…";
        Publish(job, force: true);
        var result = await browserDownloader.DownloadAsync(
            new BrowserDownloadRequest(source, referer, job.TempDirectory, saveAs, name, $"{job.GameTitle} · {partLabel}"), progress, ct)
            .ConfigureAwait(false);

        if (result.Outcome == BrowserDownloadOutcome.Unavailable)
        {
            // No built-in browser after all (WebView2 won't start): the verification window / browser hand-off is left.
            logger.LogWarning("{Message}", result.Message);
            return await httpDownloader.DownloadAsync(source, job.TempDirectory, saveAs, progress, ct,
                new HttpDownloadOptions(referer, AllowVerification: true)).ConfigureAwait(false);
        }

        return result.Outcome switch
        {
            BrowserDownloadOutcome.Completed when result.FilePath is not null => result.FilePath,
            BrowserDownloadOutcome.DeadLink => throw new DeadLinkException(result.Message ?? $"{name} says the file is gone."),
            _ => throw new IOException(result.Message ?? $"The download from {name} didn't finish."),
        };
    }

    /// <summary>
    /// A file whose link is a short link or a FileCrypt container stands for whatever is behind it: one file on a host, or
    /// a container's parts. The download browser follows it (you click through the link page and any check; SeaRaider
    /// reads the container's part list and each part's host link), and the job's files become those host links, so every
    /// part downloads and the set is extracted together. Saved, so a restart doesn't ask again.
    /// </summary>
    private async Task ExpandLinksAsync(DownloadJob job, CancellationToken ct)
    {
        if (!job.Files.Any(NeedsExpanding) || !browserDownloader.IsAvailable) return;

        var referer = Uri.TryCreate(job.SourcePageUrl, UriKind.Absolute, out var page) ? page : null;
        var sizeText = job.Links.Select(l => l.SizeText).FirstOrDefault(s => s is not null);
        var files = new List<DownloadFileSpec>();
        foreach (var spec in job.Files)
        {
            if (!NeedsExpanding(spec))
            {
                files.Add(spec);
                continue;
            }

            var name = NameOf(spec);
            job.StatusDetail = $"Fetching download from {name}…";
            job.BytesPerSecond = 0;
            Publish(job, force: true);

            var result = await browserDownloader.DownloadAsync(
                new BrowserDownloadRequest(new Uri(spec.Url), referer, job.TempDirectory, null, name, job.GameTitle, CollectLinks: true),
                null, ct).ConfigureAwait(false);

            if (result.Outcome == BrowserDownloadOutcome.DeadLink) throw new DeadLinkException(result.Message ?? $"{name} no longer has this download.");
            if (result.Links is not { Count: > 0 } links) throw new IOException(result.Message ?? $"Couldn't get the download links from {name}.");

            foreach (var found in links)
            {
                if (!Uri.TryCreate(found.Url, UriKind.Absolute, out var url)) continue;
                var link = LinkClassifier.Classify(url, found.FileName, forceDownload: true)
                           ?? new DownloadLink { Url = url.AbsoluteUri, Host = url.Host, Kind = DownloadLinkKind.FileHost };
                var part = DownloadFileSpec.FromLink(link);
                if (part.OriginalFileName.Length == 0 && found.FileName is { Length: > 0 } fileName)
                    part.OriginalFileName = BrowserPageScript.SafeFileName(fileName);
                if (LinkClassifier.TryParseVolume(part.OriginalFileName, out _, out var number)) part.PartNumber = number;
                part.MirrorName = HostName(part.Url);
                files.Add(part);
            }
            logger.LogInformation("{Title}: {Name} leads to {Count} file(s) on {Host}", job.GameTitle, name, links.Count,
                files.Count > 0 ? files[^1].MirrorName : "?");
        }

        // A container's parts in volume order (part1, part2 …), whatever order the page listed them in.
        if (files.All(f => f.PartNumber > 0)) files = files.OrderBy(f => f.PartNumber).ToList();
        job.Files = files;
        job.Links = files.Select(f => new DownloadLink
        {
            Url = f.Url, Kind = f.Kind, Host = f.Host, FileName = f.OriginalFileName, Mirror = f.MirrorName ?? string.Empty,
            PartNumber = f.PartNumber, SizeText = sizeText,
        }).ToList();
        repository.UpsertJob(job);

        static bool NeedsExpanding(DownloadFileSpec spec) =>
            Uri.TryCreate(spec.Url, UriKind.Absolute, out var url) &&
            (FileHosts.IsLinkShortener(url.Host) || FileHosts.IsLinkProtector(url.Host));
    }

    /// <summary>
    /// The name to show for a file's source: the mirror's name for a short link (the shortener itself is never named),
    /// else the host's.
    /// </summary>
    private static string NameOf(DownloadFileSpec spec) =>
        Uri.TryCreate(spec.Url, UriKind.Absolute, out var url) && FileHosts.IsLinkShortener(url.Host)
            ? spec.MirrorName ?? "the download host"
            : HostName(spec.Url);

    /// <summary>
    /// Replaces a short link with the page it leads to when the short link's own page says where that is (redirect, link,
    /// meta refresh or encoded URL), so the download skips the shortener's wait screen and ads. Saved on the job, so a
    /// retry or a restart goes straight there too.
    /// </summary>
    private async Task UnshortenAsync(DownloadJob job, DownloadFileSpec spec, bool wantTorrent, CancellationToken ct)
    {
        if (!Uri.TryCreate(spec.Url, UriKind.Absolute, out var url) || !FileHosts.IsLinkShortener(url.Host)) return;

        job.StatusDetail = $"Fetching download from {NameOf(spec)}…";
        job.BytesPerSecond = 0;
        Publish(job, force: true);

        if (await hostResolver.ResolveShortLinkAsync(url, wantTorrent, ct).ConfigureAwait(false) is not { } destination) return;

        var magnet = destination.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase);
        spec.Url = destination.OriginalString;
        spec.Host = magnet ? "magnet" : destination.Host;
        spec.Kind = magnet ? DownloadLinkKind.Magnet
            : destination.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) ? DownloadLinkKind.TorrentFile
            : FileHosts.Find(destination.Host) is not null ? DownloadLinkKind.FileHost
            : DownloadLinkKind.DirectHttp;
        if (!magnet && spec.OriginalFileName.Length == 0 && FileHosts.GuessFileName(destination) is { Length: > 0 } fileName)
            spec.OriginalFileName = fileName;
        if (spec.Kind is DownloadLinkKind.FileHost or DownloadLinkKind.DirectHttp) spec.MirrorName = HostName(spec.Url);
        repository.UpsertJob(job);
        logger.LogInformation("{Title}: download link leads to {Host}", job.GameTitle, magnet ? "a magnet" : destination.Host);
    }

    private static string HostName(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? FileHosts.Find(uri.Host)?.DisplayName ?? (uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host)
            : url;

    private static string JoinNames(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "any host",
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " or " + names[^1],
    };

    /// <summary>Every host lost a file: start over with the page's torrent.</summary>
    private async Task SwitchToTorrentAsync(DownloadJob job, string stagingDirectory, NoWorkingSourceException reason)
    {
        var torrent = job.FallbackTorrent!;
        logger.LogWarning("{Title}: {Reason} Switching to the torrent", job.GameTitle, reason.Message);
        job.StatusDetail = $"{reason.Message} Switching to the torrent…";
        Publish(job, force: true);

        await DeleteQuietlyAsync(stagingDirectory).ConfigureAwait(false);
        await DeleteQuietlyAsync(job.TempDirectory).ConfigureAwait(false);
        Directory.CreateDirectory(job.TempDirectory);

        job.Transport = DownloadTransport.Torrent;
        job.Links = [torrent];
        job.Files = [DownloadFileSpec.FromLink(torrent)];
        job.FallbackTorrent = null;
        job.StreamingExtraction = false;
        job.StreamFirstVolume = 0;
        job.BytesDownloaded = 0;
        job.TotalBytes = 0;
        job.DownloadedFiles = [];
        repository.UpsertJob(job);
    }

    // ---- low disk space mode --------------------------------------------------------------------

    /// <summary>
    /// Should this job unpack while downloading? Only multi-part RAR sets can (other formats need the whole archive
    /// before anything can be read). Once chosen it sticks: unpacked volumes are deleted, so there's no going back.
    /// </summary>
    private bool DecideStreaming(DownloadJob job, AppSettings settings)
    {
        if (job.StreamingExtraction) return true;
        if (settings.LowDiskSpaceMode == LowDiskMode.Never || !IsRarVolumeSet(job.Files)) return false;

        var stream = settings.LowDiskSpaceMode == LowDiskMode.Always || !HasRoomForArchiveAndGame(job, settings);
        if (!stream) return false;

        job.StreamingExtraction = true;
        job.StreamedWhileDownloading = true;
        job.StreamFirstVolume = 0;
        repository.UpsertJob(job);
        logger.LogInformation("{Title}: low disk space mode (unpacking {Count} volumes while downloading)", job.GameTitle, job.Files.Count);
        return true;
    }

    /// <summary>A complete multi-volume RAR set (game.part1.rar … or game.rar + game.r00 …), in volume order.</summary>
    internal static bool IsRarVolumeSet(IReadOnlyList<DownloadFileSpec> files)
    {
        if (files.Count < 2) return false;
        string? key = null;
        var previous = 0;
        foreach (var file in files)
        {
            var name = file.EffectiveFileName;
            string group;
            int part;
            if (LinkClassifier.TryParseVolume(name, out group, out part, out var scheme))
            {
                if (scheme is not (VolumeScheme.RarParts or VolumeScheme.RarOldStyle)) return false;
            }
            else if (name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
            {
                group = name[..^4].ToLowerInvariant(); // game.rar: the first volume of the old naming
                part = 1;
            }
            else
            {
                return false;
            }

            key ??= group;
            if (!string.Equals(key, group, StringComparison.OrdinalIgnoreCase) || part <= previous) return false;
            previous = part;
        }
        return true;
    }

    /// <summary>Is there room for the archive and the unpacked game at the same time (the normal way)?</summary>
    private bool HasRoomForArchiveAndGame(DownloadJob job, AppSettings settings)
    {
        var archive = job.TotalBytes > 0
            ? job.TotalBytes
            : job.Links.Select(l => Formatting.ParseBytes(l.SizeText)).FirstOrDefault(b => b is > 0) ?? 0;
        if (archive <= 0) return true; // unknown size: download normally, extraction checks the space it needs

        var unpacked = (long)(archive * 1.15);
        var tempFree = FreeSpace(settings.TempRoot);
        var libraryFree = FreeSpace(settings.LibraryRoot);
        if (tempFree is null || libraryFree is null) return true;

        var sameDrive = string.Equals(Path.GetPathRoot(Path.GetFullPath(settings.TempRoot)),
            Path.GetPathRoot(Path.GetFullPath(settings.LibraryRoot)), StringComparison.OrdinalIgnoreCase);
        var fits = sameDrive
            ? tempFree >= archive + unpacked + SpaceMargin
            : tempFree >= archive + SpaceMargin && libraryFree >= unpacked + SpaceMargin;
        if (!fits)
            logger.LogInformation("{Title}: {Archive} archive + ~{Unpacked} unpacked doesn't fit ({Free} free)", job.GameTitle,
                Formatting.Bytes(archive), Formatting.Bytes(unpacked), Formatting.Bytes(tempFree.Value));
        return fits;
    }

    private static long? FreeSpace(string path)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
            return drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Downloads the volumes in order while <see cref="IArchiveExtractor.ExtractVolumesAsync"/> unpacks them into the
    /// staging folder and deletes each one it has finished. The downloader stays at most two volumes ahead.
    /// After an interruption it continues at the volume where the unfinished file started (re-downloading that
    /// volume if it was already deleted). Solid archives can't start in the middle, so they start over.
    /// </summary>
    private async Task DownloadAndUnpackAsync(DownloadJob job, string stagingDirectory, CancellationToken ct)
    {
        if (job.StreamFirstVolume > 0 && (job.StreamSolidArchive || !Directory.Exists(stagingDirectory)))
        {
            logger.LogInformation("{Title}: unpacking starts over from part 1 (solid archive or missing files)", job.GameTitle);
            await DeleteQuietlyAsync(stagingDirectory).ConfigureAwait(false);
            job.StreamFirstVolume = 0;
        }
        var start = Math.Clamp(job.StreamFirstVolume, 0, job.Files.Count - 1);
        Directory.CreateDirectory(stagingDirectory);
        if (start > 0)
        {
            job.StatusDetail = $"Continuing from part {start + 1} (parts 1–{start} are already unpacked)…";
            Publish(job, force: true);
        }

        var feed = new VolumeFeed(job.Files.Count, aheadLimit: 2);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var callbacks = new StreamingCallbacks(
            Progress: percent =>
            {
                job.ExtractionPercent = percent;
                if (job.Status == DownloadJobStatus.Extracting) job.StatusDetail = $"Unpacking… {percent:0}%";
                Publish(job);
            },
            ResumePoint: volume =>
            {
                if (volume == job.StreamFirstVolume) return;
                job.StreamFirstVolume = volume;
                repository.UpsertJob(job);
            },
            Solid: solid => job.StreamSolidArchive = solid);

        var extraction = extractor.ExtractVolumesAsync(feed, start, stagingDirectory, callbacks, linked.Token);
        var download = DownloadHttpAsync(job, feed, start, linked.Token);
        _ = download.ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) feed.Fail(t.Exception?.GetBaseException() ?? new OperationCanceledException());
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        try
        {
            await extraction.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            // The archive itself is the problem: stop downloading the rest.
            await linked.CancelAsync().ConfigureAwait(false);
            try { await download.ConfigureAwait(false); }
            catch (Exception) { /* stopped because unpacking failed */ }
            throw new StreamingExtractionException(ex);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && (download.IsFaulted || download.IsCanceled))
        {
            // Unpacking stopped because the download did; its error is the one to report (below).
        }

        await download.ConfigureAwait(false);
        job.DownloadedFiles = [];
        job.ExtractionPercent = 100;
    }

    private async Task<IReadOnlyList<string>> DownloadTorrentAsync(DownloadJob job, bool keepForSeeding, CancellationToken ct)
    {
        await CollectTorrentAsync(job, ct).ConfigureAwait(false);

        var progress = new SyncProgress<TransferProgress>(p =>
        {
            job.BytesDownloaded = p.BytesDownloaded;
            if (p.TotalBytes > 0) job.TotalBytes = p.TotalBytes;
            job.BytesPerSecond = p.BytesPerSecond;
            job.Seeds = p.Seeds;
            job.Peers = p.Peers;
            job.PeersFound = p.PeersFound;
            job.StatusDetail = p.Detail ?? "Downloading";
            Publish(job);
        });

        return await torrentDownloader.DownloadAsync(TorrentLink(job), job.TempDirectory, progress, ct, keepForSeeding).ConfigureAwait(false);
    }

    /// <summary>
    /// A torrent mirror behind a link shortener ("Torrent: Click Here" → Zovo): the download browser goes through the
    /// shortener in the background (wait screen, Visit, the download page) and brings back the magnet link or .torrent
    /// file, which then replaces the page link so the torrent client (and a restart) uses it directly.
    /// </summary>
    private async Task CollectTorrentAsync(DownloadJob job, CancellationToken ct)
    {
        var spec = job.Files[0];
        if (spec.Kind is DownloadLinkKind.Magnet or DownloadLinkKind.TorrentFile) return;

        // The short link's page may say where it leads: a magnet or .torrent needs no browser at all.
        await UnshortenAsync(job, spec, wantTorrent: true, ct).ConfigureAwait(false);
        if (spec.Kind is DownloadLinkKind.Magnet or DownloadLinkKind.TorrentFile)
        {
            job.Links = [new DownloadLink { Url = spec.Url, Kind = spec.Kind, Host = spec.Host, FileName = spec.OriginalFileName, Mirror = "Torrent" }];
            repository.UpsertJob(job);
            return;
        }

        var name = NameOf(spec);
        if (!browserDownloader.IsAvailable)
            throw new InvalidOperationException($"Getting the torrent from {name} needs SeaRaider's built-in browser (WebView2), which isn't available here.");

        job.StatusDetail = $"Fetching download from {name}…";
        job.BytesPerSecond = 0;
        Publish(job, force: true);

        var folder = Path.Combine(job.TempDirectory, ".torrent");
        Directory.CreateDirectory(folder);
        var referer = Uri.TryCreate(job.SourcePageUrl, UriKind.Absolute, out var page) ? page : null;
        var result = await browserDownloader.DownloadAsync(
            new BrowserDownloadRequest(new Uri(spec.Url), referer, folder, null, name, $"{job.GameTitle} · torrent", WantTorrent: true),
            null, ct).ConfigureAwait(false);

        DownloadLink torrent = result switch
        {
            { Outcome: BrowserDownloadOutcome.Completed, Magnet: { Length: > 0 } magnet } =>
                new DownloadLink { Url = magnet, Kind = DownloadLinkKind.Magnet, Host = "magnet", Mirror = "Torrent",
                                   FileName = LinkClassifier.TryGetMagnetName(magnet) ?? string.Empty, Label = "Magnet link" },
            { Outcome: BrowserDownloadOutcome.Completed, FilePath: { Length: > 0 } file }
                when file.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) =>
                new DownloadLink { Url = new Uri(file).AbsoluteUri, Kind = DownloadLinkKind.TorrentFile, Host = "local", Mirror = "Torrent",
                                   FileName = Path.GetFileName(file), Label = "Torrent file" },
            { Outcome: BrowserDownloadOutcome.Completed } =>
                throw new IOException($"{name} led to a file that isn't a torrent. Pick one of the page's file mirrors instead."),
            { Outcome: BrowserDownloadOutcome.DeadLink } => throw new DeadLinkException(result.Message ?? $"{name} says the torrent is gone."),
            _ => throw new IOException(result.Message ?? $"Couldn't get the torrent from {name}."),
        };

        logger.LogInformation("{Title}: got the {Kind} from {Host}", job.GameTitle, torrent.Kind, name);
        job.Links = [torrent];
        job.Files = [DownloadFileSpec.FromLink(torrent)];
        repository.UpsertJob(job);
    }

    private static DownloadLink TorrentLink(DownloadJob job)
    {
        var spec = job.Files[0];
        return new DownloadLink { Url = spec.Url, Kind = spec.Kind, FileName = spec.OriginalFileName, Host = spec.Host };
    }

    /// <summary>Shares a finished torrent in the background (no download slot), then removes its temporary files.</summary>
    private void StartSeeding(DownloadJob job, string completionMessage, AppSettings settings)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        if (!_seeding.TryAdd(job.Id, cts))
        {
            cts.Dispose();
            return;
        }

        var options = new SeedOptions(Math.Max(0, settings.SeedRatioLimit),
            settings.SeedTimeLimitMinutes > 0 ? TimeSpan.FromMinutes(settings.SeedTimeLimitMinutes) : null);
        var throttle = queue.ThrottleFor(job);

        job.SeedingActive = true;
        repository.UpsertJob(job); // survives a crash: seeding resumes on the next start

        _ = Task.Run(async () =>
        {
            using var scope = TransferThrottle.Use(throttle);
            job.IsSeeding = true;
            job.StatusDetail = $"{completionMessage} · seeding";
            Publish(job, force: true);
            try
            {
                var progress = new SyncProgress<SeedProgress>(p =>
                {
                    job.SeedRatio = p.Ratio;
                    job.UploadBytesPerSecond = p.UploadBytesPerSecond;
                    job.Peers = p.Peers;
                    job.StatusDetail = $"{completionMessage} · seeding to {p.Peers} peer(s), ratio {p.Ratio:0.00}";
                    Publish(job);
                });
                await torrentDownloader.SeedAsync(TorrentLink(job), options, progress, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Seeding {Title} stopped with an error", job.GameTitle);
            }
            finally
            {
                _seeding.TryRemove(job.Id, out _);
                var stoppedByUser = cts.IsCancellationRequested && !_shutdown.IsCancellationRequested;
                cts.Dispose();
                job.IsSeeding = false;
                // App shutdown keeps the flag so seeding resumes next time; finishing or "Stop seeding" clears it.
                if (!_shutdown.IsCancellationRequested)
                {
                    job.SeedingActive = false;
                    repository.UpsertJob(job);
                }
                job.UploadBytesPerSecond = 0;
                job.Peers = 0;
                job.StatusDetail = stoppedByUser ? $"{completionMessage} · seeding stopped" : completionMessage;
                if (!_shutdown.IsCancellationRequested && repository.GetSettings().DeleteArchivesAfterInstall)
                    await DeleteQuietlyAsync(job.TempDirectory).ConfigureAwait(false);
                Publish(job, force: true);
            }
        }, CancellationToken.None);
    }

    // =============================================================================================
    // Helpers
    // =============================================================================================

    private async Task CleanupCancelledAsync(DownloadJob job)
    {
        await DeleteQuietlyAsync(job.TempDirectory).ConfigureAwait(false);
        if (job.StreamingExtraction)
        {
            // What low disk space mode had unpacked so far.
            await DeleteQuietlyAsync(Path.Combine(repository.GetSettings().LibraryRoot, ".searaider-staging", job.Id.ToString("N"))).ConfigureAwait(false);
            job.StreamingExtraction = false;
            job.StreamFirstVolume = 0;
        }
        job.BytesDownloaded = 0;
        job.BytesPerSecond = 0;
        job.DownloadedFiles = [];
        SetStatus(job, DownloadJobStatus.Cancelled, "Cancelled");

        var install = repository.GetInstall(job.GameId);
        if (install is not null && install.State != GameInstallState.Installed)
        {
            install.State = GameInstallState.NotInstalled;
            install.LastError = null;
            repository.UpsertInstall(install);
        }
    }

    private void SetStatus(DownloadJob job, DownloadJobStatus status, string detail)
    {
        job.Status = status;
        job.StatusDetail = detail;
        repository.UpsertJob(job);
        Publish(job, force: true);
    }

    private void UpdateInstall(DownloadJob job, GameInstallState state)
    {
        var install = repository.GetInstall(job.GameId) ?? new InstalledGame
        {
            GameId = job.GameId,
            Title = job.GameTitle,
            CoverImageUrl = job.CoverImageUrl,
        };
        if (install.State == GameInstallState.Installed) return; // never downgrade a working install
        install.State = state;
        install.LastError = null;
        repository.UpsertInstall(install);
    }

    /// <summary>Raises <see cref="JobChanged"/>, throttled per job unless <paramref name="force"/>.</summary>
    private void Publish(DownloadJob job, bool force = false)
    {
        var now = Stopwatch.GetTimestamp();
        if (!force && _lastPublish.TryGetValue(job.Id, out var last) &&
            Stopwatch.GetElapsedTime(last, now) < PublishInterval)
        {
            return;
        }
        _lastPublish[job.Id] = now;
        Checkpoint(job, now);

        try
        {
            JobChanged?.Invoke(this, job.ToSnapshot());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "A JobChanged subscriber threw"); // a UI bug must never kill a download
        }
    }

    /// <summary>
    /// Writes a running job's progress to the database every few seconds, so a crash loses at most a few seconds of
    /// bookkeeping. (The bytes themselves are safe in the ".part" files and torrent fast-resume data.)
    /// </summary>
    private void Checkpoint(DownloadJob job, long now)
    {
        if (job.Status is not (DownloadJobStatus.Downloading or DownloadJobStatus.Extracting)) return;
        if (_lastCheckpoint.TryGetValue(job.Id, out var last) && Stopwatch.GetElapsedTime(last, now) < CheckpointInterval) return;
        _lastCheckpoint[job.Id] = now;
        job.CheckpointAtUtc = DateTime.UtcNow;
        try
        {
            repository.UpsertJob(job);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Progress checkpoint failed for {Title}", job.GameTitle);
        }
    }

    private static string UniqueDirectory(string desired)
    {
        if (!Directory.Exists(desired) || !Directory.EnumerateFileSystemEntries(desired).Any()) return desired;
        for (var i = 2; ; i++)
        {
            var candidate = $"{desired} ({i})";
            if (!Directory.Exists(candidate)) return candidate;
        }
    }

    private static void EnsureFreeSpace(string libraryRoot, long minimumBytes)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(libraryRoot))!);
            if (drive.IsReady && drive.AvailableFreeSpace < minimumBytes)
            {
                throw new IOException(
                    $"Not enough disk space on {drive.Name}: need at least {Formatting.Bytes(minimumBytes)}, " +
                    $"{Formatting.Bytes(drive.AvailableFreeSpace)} free.");
            }
        }
        catch (ArgumentException)
        {
            // Network/UNC paths: DriveInfo can't tell; let extraction find out.
        }
    }

    private static void TryDeleteEmptyStagingRoot(string libraryRoot)
    {
        var root = Path.Combine(libraryRoot, ".searaider-staging");
        try
        {
            if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
        }
        catch (IOException) { }
    }

    private async Task DeleteQuietlyAsync(string directory)
    {
        try
        {
            await PathSafety.DeleteDirectoryRobustAsync(directory).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete {Dir}", directory);
        }
    }

    /// <summary>Turns exceptions into messages fit for the Download Manager row.</summary>
    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.NotFound } => "The file is no longer available on the server (404).",
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden } => "The server refused the download (403). The link may have expired.",
        HttpRequestException { StatusCode: { } code } => $"Server error {(int)code} ({code}).",
        HttpRequestException http => $"Network error: {http.Message}",
        StreamingExtractionException { InnerException: { } inner } => $"Unpacking failed: {inner.Message}",
        DeadLinkException or NoWorkingSourceException or BrowserRequiredException => ex.Message,
        InvalidDataException => ex.Message,
        IOException => ex.Message,
        UnauthorizedAccessException => "Access denied writing to the library folder. Choose a folder you own in Settings.",
        _ => ex.Message,
    };
}

internal static class DownloadJobExtensions
{
    public static bool IsTerminalOrCompleted(this DownloadJob job) =>
        job.Status is DownloadJobStatus.Completed or DownloadJobStatus.Cancelled;
}
