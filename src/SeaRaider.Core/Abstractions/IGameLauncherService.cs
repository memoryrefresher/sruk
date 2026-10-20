using SeaRaider.Core.Models;

namespace SeaRaider.Core.Abstractions;

/// <summary>Play / track / uninstall installed games.</summary>
public interface IGameLauncherService
{
    /// <summary>Raised (on a background thread) when a game starts or stops running.</summary>
    event EventHandler<GameSessionEventArgs>? SessionChanged;

    /// <summary>Raised after an install record changed (uninstalled, exe re-detected…).</summary>
    event EventHandler<string>? InstallChanged;

    bool IsRunning(string gameId);

    /// <summary>An uninstall of this game is in progress (its uninstaller may be waiting for an answer).</summary>
    bool IsUninstalling(string gameId);

    /// <summary>Stops waiting for the game's uninstaller and goes on to remove its folder.</summary>
    void StopWaitingForUninstaller(string gameId);

    /// <summary>Fixes records left "Uninstalling" by an uninstall that never finished (app closed meanwhile).</summary>
    void RecoverInterruptedUninstalls();

    /// <summary>
    /// Starts the game's executable and monitors it (including launcher stubs that spawn a child
    /// process and exit) to accumulate play time.
    /// </summary>
    /// <exception cref="InvalidOperationException">Not installed, exe missing, or already running.</exception>
    Task LaunchAsync(string gameId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Like <see cref="LaunchAsync(string, CancellationToken)"/>, but starts <paramref name="executable"/> (a program inside the
    /// game's folder, e.g. what the game's own desktop shortcut pointed at) with <paramref name="arguments"/>.
    /// </summary>
    Task LaunchAsync(string gameId, string? executable, string? arguments, CancellationToken cancellationToken = default);

    /// <summary>
    /// Watches for installed games started outside SeaRaider (their own shortcuts, Start menu, the exe itself) and tracks
    /// them like a launch from SeaRaider: play time, and what friends see you playing.
    /// </summary>
    void StartWatchingExternalLaunches();

    /// <summary>
    /// Deletes the install folder (after backing up in-folder saves if enabled) and marks the game
    /// <see cref="GameInstallState.NotInstalled"/>. Saves in %APPDATA%, Documents, Saved Games
    /// etc. are never touched.
    /// </summary>
    Task UninstallAsync(string gameId, CancellationToken cancellationToken = default);

    /// <summary>Re-runs executable detection, e.g. if the user's game was patched.</summary>
    Task<string?> RedetectExecutableAsync(string gameId, CancellationToken cancellationToken = default);

    /// <summary>Lets the user pick the launch file manually: an .exe, .bat or .cmd inside the install folder.</summary>
    /// <exception cref="ArgumentException">The file is not an .exe/.bat/.cmd inside the game's install folder.</exception>
    void SetExecutable(string gameId, string executablePath);
}

public sealed class GameSessionEventArgs(string gameId, bool isRunning, TimeSpan sessionLength) : EventArgs
{
    public string GameId { get; } = gameId;
    public bool IsRunning { get; } = isRunning;

    /// <summary>Length of the session that just ended (zero when starting).</summary>
    public TimeSpan SessionLength { get; } = sessionLength;
}

/// <summary>Finds the "real" game executable among the files of an install.</summary>
public interface IExecutableLocator
{
    /// <summary>Candidates ordered best-first, with their scores (useful for a "choose exe" UI).</summary>
    IReadOnlyList<ExecutableCandidate> RankCandidates(string installDirectory, string gameTitle);

    string? FindPrimaryExecutable(string installDirectory, string gameTitle);
}

public sealed record ExecutableCandidate(string Path, int Score, string Reason);

/// <summary>Protects save data that some games keep inside their install folder.</summary>
public interface ISaveDataGuard
{
    /// <summary>Copies known save folders out of the install directory. Returns the number of folders backed up.</summary>
    Task<int> BackupAsync(string gameId, string installDirectory, CancellationToken cancellationToken);

    /// <summary>Copies a previous backup back into a fresh install. Returns true if anything was restored.</summary>
    Task<bool> RestoreAsync(string gameId, string installDirectory, CancellationToken cancellationToken);
}

/// <summary>
/// Plays browser games (itch.io HTML5) in SeaRaider's own full-screen game window. Implemented by the desktop app
/// (WebView2); <see cref="NullWebGameHost"/> is used without UI.
/// </summary>
public interface IWebGameHost
{
    bool IsAvailable { get; }

    /// <summary>Opens the game; completes when its window is closed (the end of the play session).</summary>
    Task PlayAsync(InstalledGame game, CancellationToken cancellationToken);
}

public sealed class NullWebGameHost : IWebGameHost
{
    public bool IsAvailable => false;

    public Task PlayAsync(InstalledGame game, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Browser games need SeaRaider's built-in browser, which isn't available here.");
}
