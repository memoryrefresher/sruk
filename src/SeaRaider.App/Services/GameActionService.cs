using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Downloads;
using SeaRaider.Core.Models;
using SeaRaider.Core.Scraping;

namespace SeaRaider.App.Services;

/// <summary>
/// The single main-button logic shared by store cards, store pages and the library:
/// download (via the pre-download dialog), resume, retry, run the installer, or play.
/// Search results are lightweight stubs, so full details (download links) are fetched here, on demand.
/// </summary>
public sealed class GameActionService(
    IDownloadAndExtractManager downloads,
    IGameLauncherService launcher,
    IInstallerService installer,
    IGameDetailsService details,
    ILibraryRepository repository,
    GameStatusService status,
    DialogService dialogs,
    NavigationService navigation,
    ShellService shell)
{
    public async Task ExecutePrimaryAsync(string gameId, DownloadLink? preferredLink = null)
    {
        switch (status.GetStatus(gameId).State)
        {
            case GameUiState.Installed:
                await launcher.LaunchAsync(gameId);
                break;

            case GameUiState.ReadyToInstall:
                await RunInstallerAsync(gameId);
                break;

            case GameUiState.Paused:
            case GameUiState.Failed when status.GetLatestJob(gameId) is not null && preferredLink is null:
                await downloads.ResumeAsync(status.GetLatestJob(gameId)!.Id);
                break;

            case GameUiState.Queued:
            case GameUiState.Downloading:
            case GameUiState.Extracting:
                navigation.ShowPage(AppPage.Downloads);
                break;

            case GameUiState.Installing:
            case GameUiState.Running:
                break;

            default:
                await StartDownloadAsync(gameId, preferredLink);
                break;
        }
    }

    /// <summary>Stops seeding the game's finished torrent right away (files released and cleaned up).</summary>
    public Task StopSeedingAsync(string gameId) =>
        status.GetLatestJob(gameId) is { IsSeeding: true } job ? downloads.StopSeedingAsync(job.Id) : Task.CompletedTask;

    /// <summary>Runs a pending setup program; surfaces failures (not user cancellations) as errors.</summary>
    public async Task RunInstallerAsync(string gameId, bool? silent = null)
    {
        var result = await installer.RunInstallerAsync(gameId, silent);
        if (!result.Succeeded && !result.Cancelled) throw new InvalidOperationException(result.Message);
    }

    private async Task StartDownloadAsync(string gameId, DownloadLink? preferredLink)
    {
        // Lazy loading: a search result only knows its title and link until now.
        var game = await details.GetAsync(gameId);

        // Read before the page was known to be a browser game (or before its source was marked as one): read it again.
        if (game.WebGameUrl is null && game.DownloadLinks.Count == 0)
            game = await details.GetAsync(gameId, forceRefresh: true);

        // A browser game: nothing to download. It goes straight into the library, ready to Play in SeaRaider's game window.
        if (game.WebGameUrl is { Length: > 0 } web)
        {
            repository.UpsertInstall(new InstalledGame
            {
                GameId = game.Id,
                Title = game.Title,
                CoverImageUrl = game.CoverImageUrl,
                State = GameInstallState.Installed,
                InstallDirectory = string.Empty,
                WebGameUrl = web,
                WebGamePageUrl = game.PageUrl,
                InstalledAtUtc = DateTime.UtcNow,
            });
            status.NotifyInstallChanged(game.Id);
            return;
        }

        if (game.DownloadLinks.Count == 0)
            throw new InvalidOperationException("No download links were found on this game's page. Open the source page to check.");

        // Only offered on a page that needs a browser (itch.io's click-through download, MEGA …): open it there.
        var options = DownloadLinkDeduplicator.Group(game.DownloadLinks);
        if (preferredLink is null && options.Count > 0 && options.All(o => o.Category == LinkCategory.BrowserOnly))
        {
            shell.OpenUrl(options[0].Representative.Url);
            return;
        }

        // A failed job blocks a fresh enqueue for the same game; clear it first.
        if (status.GetLatestJob(game.Id) is { Status: DownloadJobStatus.Failed } failed)
            await downloads.RemoveAsync(failed.Id);

        InstallRequest? request = new(game) { PreferredLink = preferredLink };
        if (repository.GetSettings().AskBeforeDownload)
        {
            request = await dialogs.ShowInstallOptionsAsync(game, preferredLink);
            if (request is null) return; // cancelled in the dialog
        }

        await downloads.EnqueueAsync(request);
    }
}
