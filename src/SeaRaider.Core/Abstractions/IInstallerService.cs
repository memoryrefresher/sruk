using SeaRaider.Core.Installing;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Abstractions;

/// <summary>Runs setup programs found after extraction and turns their result into a playable install.</summary>
public interface IInstallerService
{
    /// <summary>Raised (on a background thread) when a game's installer state changes. Argument: game id.</summary>
    event EventHandler<string>? InstallerStateChanged;

    /// <summary>A running setup's phase changed: working, waiting for input, stuck, restarting (background thread).</summary>
    event EventHandler<SetupActivity>? SetupActivityChanged;

    bool IsRunning(string gameId);

    /// <summary>Latest activity of a running setup, or null when none is running.</summary>
    SetupActivity? GetSetupActivity(string gameId);

    /// <summary>Brings the running setup's windows to the front. False when it has no window.</summary>
    bool ShowSetupWindow(string gameId);

    /// <summary>Closes a running silent setup and starts it again with its normal wizard. False if not applicable.</summary>
    bool RestartWithWizard(string gameId);

    /// <summary>
    /// Launches the game's pending setup program and waits for it to finish:
    /// <list type="number">
    ///   <item>detects the setup engine and uses its unattended switches when requested, with the target folder;</item>
    ///   <item>tracks the whole process tree until it exits, surfacing its windows when it stalls waiting for input
    ///         and falling back to the normal wizard when a silent run is stuck (see <see cref="SilentInstallManager"/>);</item>
    ///   <item>on success: finds where the game was installed (target folder, else the new Windows "installed
    ///         programs" entry), detects the game's .exe, records the uninstaller, deletes the setup files,
    ///         and marks the game <see cref="GameInstallState.Installed"/>.</item>
    /// </list>
    /// On failure or cancellation the setup files are kept so it can be retried.
    /// </summary>
    /// <param name="silent">Override the stored silent preference for this run (null = use stored).</param>
    Task<InstallerRunResult> RunInstallerAsync(string gameId, bool? silent = null, CancellationToken cancellationToken = default);

    /// <summary>Deletes a pending installer's setup files without installing (used by "Remove").</summary>
    Task DiscardInstallerAsync(string gameId, CancellationToken cancellationToken = default);

    /// <summary>
    /// A game finished installing and its setup files are still there (with "Ask" for
    /// <see cref="AppSettings.SetupFilesAfterInstall"/>): the UI asks whether to delete them (background thread).
    /// </summary>
    event EventHandler<SetupCleanupOffer>? SetupCleanupOffered;

    /// <summary>Installed games whose setup files are still there and haven't been kept on purpose (asked again at start-up).</summary>
    IReadOnlyList<SetupCleanupOffer> GetPendingSetupCleanups();

    /// <summary>Deletes an installed game's setup files; returns the bytes freed. The game itself is untouched.</summary>
    /// <exception cref="IOException">They couldn't all be deleted (the message says why); what's left stays recorded.</exception>
    Task<long> DeleteSetupFilesAsync(string gameId, CancellationToken cancellationToken = default);

    /// <summary>Keeps an installed game's setup files and stops asking about them.</summary>
    void KeepSetupFiles(string gameId);

    /// <summary>
    /// Setup folders in &lt;Library&gt;\.searaider-setup that no game needs any more: games removed or uninstalled, downloads
    /// discarded, deletions that failed earlier. Folders of games waiting to install, being downloaded, or installed and
    /// not yet tried (the "delete the setup files?" question) are never counted.
    /// </summary>
    SetupLeftovers GetLeftoverSetupFiles();

    /// <summary>Deletes those folders (closing setup helpers still running from them); returns what couldn't be deleted.</summary>
    Task<SetupLeftovers> CleanUpLeftoverSetupFilesAsync(CancellationToken cancellationToken = default);
}

/// <param name="Folders">How many folders.</param>
/// <param name="Bytes">Their size.</param>
/// <param name="Problem">Why some couldn't be deleted, if any.</param>
public sealed record SetupLeftovers(int Folders, long Bytes, string? Problem = null)
{
    public static SetupLeftovers None { get; } = new(0, 0);
}

/// <param name="GameId">The installed game.</param>
/// <param name="Title">Its display title.</param>
/// <param name="Bytes">Size of its setup files.</param>
public sealed record SetupCleanupOffer(string GameId, string Title, long Bytes);

/// <param name="Succeeded">Setup finished with a success exit code.</param>
/// <param name="Cancelled">The user cancelled the wizard or the elevation prompt.</param>
/// <param name="ExitCode">Installer exit code, if known.</param>
/// <param name="ExecutablePath">Resolved game executable, if found.</param>
/// <param name="Message">User-facing summary.</param>
public sealed record InstallerRunResult(bool Succeeded, bool Cancelled, int? ExitCode, string? ExecutablePath, string Message);
