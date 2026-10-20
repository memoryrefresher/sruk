using System.Collections.Concurrent;
using Avalonia.Threading;
using SeaRaider.Core;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Catalog;
using SeaRaider.Core.Installing;
using SeaRaider.Core.WebChallenge;
using SeaRaider.Core.Models;

namespace SeaRaider.App.Services;

/// <summary>What a game looks like from the UI's point of view.</summary>
public enum GameUiState
{
    /// <summary>Not on disk: "Download &amp; Install".</summary>
    Available,
    Queued,
    Downloading,
    Paused,
    Extracting,

    /// <summary>Extracted to a setup program: "Install".</summary>
    ReadyToInstall,

    /// <summary>The setup program is running.</summary>
    Installing,

    /// <summary>Playable: "Play".</summary>
    Installed,
    Running,
    Failed,
}

/// <param name="State">Overall state.</param>
/// <param name="Percent">0–100 for active jobs, null otherwise or when unknown.</param>
/// <param name="Label">Text for the game's main button ("Download &amp; Install", "Install", "Play"…).</param>
public sealed record GameStatus(GameUiState State, double? Percent, string Label)
{
    public bool IsBusy => State is GameUiState.Queued or GameUiState.Downloading or GameUiState.Extracting or GameUiState.Installing;

    /// <summary>"Play" state: the button uses the brass (owned) style.</summary>
    public bool IsPlayable => State is GameUiState.Installed or GameUiState.Running;
}

/// <summary>
/// Single funnel between the background engine and the UI. Subscribes to every core event
/// (raised on worker threads), keeps the latest job snapshot per game, and re-raises
/// everything on the Avalonia UI thread — so view-models never touch threading.
/// </summary>
public sealed class GameStatusService
{
    private readonly ILibraryRepository _repository;
    private readonly IGameLauncherService _launcher;
    private readonly IInstallerService _installer;
    private readonly ConcurrentDictionary<string, DownloadJobSnapshot> _latestJobByGame = new();

    /// <summary>A game's status may have changed (UI thread).</summary>
    public event Action<string>? GameChanged;

    /// <summary>A download job changed (UI thread).</summary>
    public event Action<DownloadJobSnapshot>? JobUpdated;

    /// <summary>A download job was removed (UI thread).</summary>
    public event Action<Guid>? JobRemoved;

    /// <summary>Catalog entries were added/updated, or the catalog must be reloaded (UI thread).</summary>
    public event Action<CatalogChangedEventArgs>? CatalogChanged;

    /// <summary>A verification (captcha) window opened or closed (UI thread).</summary>
    public event Action<ChallengePrompt>? VerificationPromptChanged;

    /// <summary>A source's crawl progressed, started or ended (UI thread).</summary>
    public event Action<CrawlStatusEventArgs>? CrawlStatusChanged;

    /// <summary>An install record changed — installed, uninstalled, setup finished, play time (UI thread).</summary>
    public event Action<string>? InstallChanged;

    /// <summary>A running setup is working, waiting for input, stuck or restarting (UI thread).</summary>
    public event Action<SetupActivity>? SetupActivityChanged;

    /// <summary>A game finished installing and its setup files could be deleted (UI thread).</summary>
    public event Action<SetupCleanupOffer>? SetupCleanupOffered;

    public GameStatusService(
        ILibraryRepository repository,
        IDownloadAndExtractManager downloads,
        IGameLauncherService launcher,
        IInstallerService installer,
        IIngestionService ingestion,
        CatalogIndexService catalogIndex,
        IChallengeResolver challengeResolver)
    {
        _repository = repository;
        _launcher = launcher;
        _installer = installer;

        foreach (var job in downloads.Jobs) Track(job);

        downloads.JobChanged += (_, job) =>
        {
            Track(job);
            Dispatcher.UIThread.Post(() =>
            {
                JobUpdated?.Invoke(job);
                GameChanged?.Invoke(job.GameId);
                if (job.Status is DownloadJobStatus.Completed or DownloadJobStatus.Cancelled or DownloadJobStatus.Failed)
                    InstallChanged?.Invoke(job.GameId);
            });
        };
        downloads.JobRemoved += (_, jobId) => Dispatcher.UIThread.Post(() => JobRemoved?.Invoke(jobId));

        launcher.SessionChanged += (_, e) => Dispatcher.UIThread.Post(() => RaiseGameAndInstall(e.GameId));
        launcher.InstallChanged += (_, gameId) => Dispatcher.UIThread.Post(() => RaiseGameAndInstall(gameId));
        installer.InstallerStateChanged += (_, gameId) => Dispatcher.UIThread.Post(() => RaiseGameAndInstall(gameId));
        installer.SetupCleanupOffered += (_, offer) => Dispatcher.UIThread.Post(() => SetupCleanupOffered?.Invoke(offer));
        installer.SetupActivityChanged += (_, activity) => Dispatcher.UIThread.Post(() =>
        {
            SetupActivityChanged?.Invoke(activity);
            GameChanged?.Invoke(activity.GameId);
        });

        // The in-memory index sees every catalog write (crawls, live search, lazy details).
        catalogIndex.Changed += (_, e) => Dispatcher.UIThread.Post(() => CatalogChanged?.Invoke(e));
        challengeResolver.PromptChanged += (_, prompt) => Dispatcher.UIThread.Post(() => VerificationPromptChanged?.Invoke(prompt));
        ingestion.CrawlStatusChanged += (_, e) => Dispatcher.UIThread.Post(() => CrawlStatusChanged?.Invoke(e));
    }

    /// <summary>An install record changed outside the usual services (a browser game added to the library).</summary>
    public void NotifyInstallChanged(string gameId) => Dispatcher.UIThread.Post(() => RaiseGameAndInstall(gameId));

    private void RaiseGameAndInstall(string gameId)
    {
        GameChanged?.Invoke(gameId);
        InstallChanged?.Invoke(gameId);
    }

    private void Track(DownloadJobSnapshot job) =>
        _latestJobByGame.AddOrUpdate(job.GameId, job, (_, existing) =>
            job.CreatedAtUtc >= existing.CreatedAtUtc ? job : existing);

    /// <summary>Resolves the current UI state of a game from its install record and newest job.</summary>
    public GameStatus GetStatus(string gameId)
    {
        if (_launcher.IsRunning(gameId)) return new GameStatus(GameUiState.Running, null, "Running");

        var install = _repository.GetInstall(gameId);
        switch (install?.State)
        {
            case GameInstallState.Installed:
                return new GameStatus(GameUiState.Installed, null, "Play");
            case GameInstallState.RunningInstaller:
                return new GameStatus(GameUiState.Installing, null, InstallingLabel(gameId));
            case GameInstallState.ReadyToInstall:
                return _installer.IsRunning(gameId)
                    ? new GameStatus(GameUiState.Installing, null, InstallingLabel(gameId))
                    : new GameStatus(GameUiState.ReadyToInstall, null, "Install");
        }

        if (_latestJobByGame.TryGetValue(gameId, out var job))
        {
            switch (job.Status)
            {
                case DownloadJobStatus.Queued:
                    return new GameStatus(GameUiState.Queued, null, "Queued");
                case DownloadJobStatus.Downloading:
                    return new GameStatus(GameUiState.Downloading, job.DownloadPercent,
                        job.DownloadPercent is { } p ? $"Downloading {p:0}%" : $"Downloading {Formatting.Bytes(job.BytesDownloaded)}");
                case DownloadJobStatus.Extracting:
                case DownloadJobStatus.Finalizing:
                    return new GameStatus(GameUiState.Extracting, job.ExtractionPercent, $"Extracting {job.ExtractionPercent:0}%");
                case DownloadJobStatus.Paused:
                    return new GameStatus(GameUiState.Paused, job.DownloadPercent, "Resume download");
                case DownloadJobStatus.Failed:
                    return new GameStatus(GameUiState.Failed, null, "Retry install");
            }
        }

        return new GameStatus(GameUiState.Available, null, "Download & Install");
    }

    private string InstallingLabel(string gameId) => _installer.GetSetupActivity(gameId)?.Phase switch
    {
        SetupPhase.NeedsInput => "Setup needs you",
        SetupPhase.Stuck => "Setup stuck",
        SetupPhase.Restarting => "Restarting setup…",
        _ => "Installing…",
    };

    /// <summary>What a running setup is doing, if one is running.</summary>
    public SetupActivity? GetSetupActivity(string gameId) => _installer.GetSetupActivity(gameId);

    /// <summary>The newest job for a game, if any.</summary>
    public DownloadJobSnapshot? GetLatestJob(string gameId) =>
        _latestJobByGame.TryGetValue(gameId, out var job) ? job : null;
}
