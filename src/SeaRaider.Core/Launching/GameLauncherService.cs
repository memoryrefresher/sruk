using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Installing;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Launching;

/// <summary>
/// Launches installed games, tracks play time, and uninstalls them safely.
///
/// <para><b>Process monitoring.</b> Many games start through a stub (launcher.exe → game.exe, or an
/// Unreal bootstrapper) whose own process exits within seconds. So a session ends only when
/// <i>no</i> process whose image lives inside the install folder is still running — checked by
/// polling after the root process exits. Image paths are read with
/// <c>QueryFullProcessImageName</c>, which also works for elevated processes.</para>
///
/// <para><b>Uninstall safety.</b> Only the game's own install folder is removed, and only after
/// sanity checks (never a drive root, never a well-known system/user folder, and the folder must
/// be inside the library root or carry the game's folder name). In-folder saves are backed up first
/// by <see cref="ISaveDataGuard"/>; saves in AppData/Documents are never touched.</para>
/// </summary>
public sealed class GameLauncherService(
    ILibraryRepository repository,
    IExecutableLocator executableLocator,
    ISaveDataGuard saveDataGuard,
    IWebGameHost webGames,
    ILogger<GameLauncherService> logger) : IGameLauncherService, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private const int MissesBeforeSessionEnds = 2;
    private const int ErrorCancelledByUser = 1223; // ERROR_CANCELLED (UAC prompt declined)

    private sealed class GameSession(string gameId, string installDirectory)
    {
        public string GameId { get; } = gameId;
        public string InstallDirectory { get; } = installDirectory;
        public DateTime StartedUtc { get; } = DateTime.UtcNow;
        public CancellationTokenSource Stop { get; } = new();
    }

    private readonly ConcurrentDictionary<string, GameSession> _sessions = new();

    /// <summary>Uninstalls in progress; cancelling one's token stops waiting for its uninstaller ("Stop waiting").</summary>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _uninstalls = new();

    public event EventHandler<GameSessionEventArgs>? SessionChanged;
    public event EventHandler<string>? InstallChanged;

    public bool IsRunning(string gameId) => _sessions.ContainsKey(gameId);

    public bool IsUninstalling(string gameId) => _uninstalls.ContainsKey(gameId);

    public void StopWaitingForUninstaller(string gameId)
    {
        if (!_uninstalls.TryGetValue(gameId, out var stop)) return;
        try { stop.Cancel(); }
        catch (ObjectDisposedException) { /* the uninstall just finished */ }
    }

    /// <summary>
    /// Records left "Uninstalling" by an uninstall that didn't finish (SeaRaider was closed, or crashed, meanwhile): back to
    /// installed if the game's files are still there, otherwise removed from the library.
    /// </summary>
    public void RecoverInterruptedUninstalls()
    {
        foreach (var install in repository.GetInstalls().Where(i => i.State == GameInstallState.Uninstalling).ToList())
        {
            if (IsUninstalling(install.GameId)) continue;
            var directory = install.InstallDirectory;
            var hasFiles = !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory) &&
                           Directory.EnumerateFiles(directory, "*.exe", SearchOption.AllDirectories).Any();
            if (hasFiles)
            {
                install.State = GameInstallState.Installed;
                install.LastError = "The last uninstall didn't finish. Uninstall again to remove what's left.";
            }
            else
            {
                MarkNotInstalled(install);
            }
            repository.UpsertInstall(install);
            logger.LogInformation("Recovered interrupted uninstall of {Title} ({State})", install.Title, install.State);
            InstallChanged?.Invoke(this, install.GameId);
        }
    }

    /// <summary>
    /// The game is going: its setup files (kept, or never cleaned up) go too. Only a folder in SeaRaider's own setup area
    /// (&lt;Library&gt;\.searaider-setup\&lt;game&gt;), never one the game lives in. If it can't be deleted now, the leftover
    /// clean-up at the next start finds it.
    /// </summary>
    private async Task DeleteSetupFolderAsync(InstalledGame install, CancellationToken ct)
    {
        if (install.SetupDirectory is not { Length: > 0 } setup) return;
        var full = Path.GetFullPath(setup).TrimEnd('\\', '/');
        var inSetupArea = Path.GetFileName(Path.GetDirectoryName(full)) == ".searaider-setup";
        var isTheGame = !string.IsNullOrWhiteSpace(install.InstallDirectory) &&
                        (PathSafety.IsUnder(install.InstallDirectory, full) || PathSafety.IsUnder(full, install.InstallDirectory));
        if (!inSetupArea || isTheGame) return;

        try
        {
            if (Directory.Exists(full)) await PathSafety.DeleteDirectoryRobustAsync(full, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Setup files of {Title} couldn't be deleted yet; the clean-up at start-up will retry", install.Title);
        }
        if (!Directory.Exists(full))
        {
            install.SetupDirectory = null;
            install.InstallerPath = null;
        }
    }

    private static void MarkNotInstalled(InstalledGame install)
    {
        install.State = GameInstallState.NotInstalled;
        install.InstallerState = InstallerState.None;
        install.ExecutablePath = null;
        install.WebGameUrl = null;
        install.WebGamePageUrl = null;
        install.SizeOnDiskBytes = 0;
        install.InstalledAtUtc = null;
        install.UninstallCommand = null;
        install.QuietUninstallCommand = null;
        install.LastError = null;
        // Play time and last-played are kept: they belong to the player, not the install.
    }

    // =============================================================================================
    // Launch & monitor
    // =============================================================================================

    public Task LaunchAsync(string gameId, CancellationToken cancellationToken = default) =>
        LaunchAsync(gameId, null, null, cancellationToken);

    public async Task LaunchAsync(string gameId, string? executable, string? arguments, CancellationToken cancellationToken = default)
    {
        var install = repository.GetInstall(gameId) ?? throw new InvalidOperationException("This game isn't installed.");
        if (install.State != GameInstallState.Installed) throw new InvalidOperationException($"{install.Title} isn't installed.");
        if (IsRunning(gameId)) throw new InvalidOperationException($"{install.Title} is already running.");

        if (install.WebGameUrl is { Length: > 0 })
        {
            LaunchWebGame(install);
            return;
        }

        // A desktop shortcut's own program: only one that is still inside the game's folder.
        var exe = executable is { Length: > 0 } chosen && File.Exists(chosen) &&
                  !string.IsNullOrWhiteSpace(install.InstallDirectory) && PathSafety.IsUnder(chosen, install.InstallDirectory)
            ? chosen
            : install.ExecutablePath;
        var launchArguments = exe == executable ? arguments : install.LaunchArguments;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
        {
            exe = await RedetectExecutableAsync(gameId, cancellationToken).ConfigureAwait(false)
                  ?? throw new FileNotFoundException($"Couldn't find an executable for {install.Title}.");
        }

        var startInfo = new ProcessStartInfo(exe)
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!,  // many games load assets relative to CWD
            Arguments = launchArguments ?? string.Empty,
            UseShellExecute = true,                          // lets "requireAdministrator" games raise a UAC prompt
        };

        Process? root;
        try
        {
            root = Process.Start(startInfo);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelledByUser)
        {
            throw new OperationCanceledException("Launch cancelled at the administrator prompt.", ex);
        }

        var session = new GameSession(gameId, install.InstallDirectory);
        if (!_sessions.TryAdd(gameId, session))
        {
            root?.Dispose();
            throw new InvalidOperationException($"{install.Title} is already running.");
        }

        install.LastPlayedAtUtc = DateTime.UtcNow;
        repository.UpsertInstall(install);
        logger.LogInformation("Launched {Title} ({Exe}), pid {Pid}", install.Title, exe, root?.Id);
        Raise(new GameSessionEventArgs(gameId, true, TimeSpan.Zero));

        _ = Task.Run(() => MonitorAsync(session, root), CancellationToken.None);
    }

    // ---- browser games ---------------------------------------------------------------------------------

    /// <summary>
    /// A browser game: its session is its game window, from opening to closing (play time, and what friends see you
    /// playing, work like any game).
    /// </summary>
    private void LaunchWebGame(InstalledGame install)
    {
        if (!webGames.IsAvailable)
            throw new InvalidOperationException("Browser games need SeaRaider's built-in browser (WebView2), which isn't available on this PC.");

        var session = new GameSession(install.GameId, string.Empty);
        if (!_sessions.TryAdd(install.GameId, session)) throw new InvalidOperationException($"{install.Title} is already running.");

        install.LastPlayedAtUtc = DateTime.UtcNow;
        repository.UpsertInstall(install);
        logger.LogInformation("Playing browser game {Title} ({Url})", install.Title, install.WebGameUrl);
        Raise(new GameSessionEventArgs(install.GameId, true, TimeSpan.Zero));

        _ = Task.Run(async () =>
        {
            try
            {
                await webGames.PlayAsync(install, session.Stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // SeaRaider is closing
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Game window for {Title} failed", install.Title);
            }
            finally
            {
                // Browser games have no polling tail to subtract.
                EndSession(session, pollingTail: false);
            }
        }, CancellationToken.None);
    }

    // ---- games started outside SeaRaider ----------------------------------------------------------

    private static readonly TimeSpan ExternalScanInterval = TimeSpan.FromSeconds(5);
    private int _watching;

    public void StartWatchingExternalLaunches()
    {
        if (!OperatingSystem.IsWindows() || Interlocked.Exchange(ref _watching, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await Task.Delay(ExternalScanInterval).ConfigureAwait(false);
                    ScanForExternalLaunches();
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "External launch scan failed");
                }
            }
        });
    }

    /// <summary>
    /// One look at the running programs: any installed game with a program running from its folder, and no session yet,
    /// gets one (tracked to its end like any launch). Uninstallers and setup-like helpers don't count as playing.
    /// </summary>
    private void ScanForExternalLaunches()
    {
        var libraryRoot = repository.GetSettings().LibraryRoot;
        var candidates = repository.GetInstalls()
            .Where(i => i.State == GameInstallState.Installed && !_sessions.ContainsKey(i.GameId) && !IsUninstalling(i.GameId) &&
                        !string.IsNullOrWhiteSpace(i.InstallDirectory) && Directory.Exists(i.InstallDirectory) &&
                        !PathSafety.IsUnder(libraryRoot, i.InstallDirectory)) // never a folder that holds the whole library
            // Deepest folders first, so a game installed inside another game's folder is matched to itself.
            .OrderByDescending(i => i.InstallDirectory.Length)
            .ToList();
        if (candidates.Count == 0) return;

        foreach (var path in ProcessScanner.RunningImagePaths())
        {
            var name = Path.GetFileName(path);
            if (name.StartsWith("unins", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("setup", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("crashhandler", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("crashreport", StringComparison.OrdinalIgnoreCase))
                continue;

            var install = candidates.FirstOrDefault(i => PathSafety.IsUnder(path, i.InstallDirectory));
            if (install is null) continue;
            candidates.Remove(install);

            var session = new GameSession(install.GameId, install.InstallDirectory);
            if (!_sessions.TryAdd(install.GameId, session)) continue;
            install.LastPlayedAtUtc = DateTime.UtcNow;
            repository.UpsertInstall(install);
            logger.LogInformation("{Title} was started outside SeaRaider ({Exe}); tracking it", install.Title, path);
            Raise(new GameSessionEventArgs(install.GameId, true, TimeSpan.Zero));
            _ = Task.Run(() => MonitorAsync(session, null), CancellationToken.None);
            if (candidates.Count == 0) break;
        }
    }

    private async Task MonitorAsync(GameSession session, Process? root)
    {
        var ct = session.Stop.Token;
        try
        {
            // Phase 1 — the process we started.
            if (root is not null)
            {
                using (root)
                {
                    try { await root.WaitForExitAsync(ct).ConfigureAwait(false); }
                    catch (InvalidOperationException) { /* no handle (shell-executed via broker) */ }
                }
            }

            // Phase 2 — anything else running from the install folder (launcher stubs, child processes).
            var misses = 0;
            while (misses < MissesBeforeSessionEnds)
            {
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
                misses = ProcessScanner.AnyRunningFrom(session.InstallDirectory) ? 0 : misses + 1;
            }
        }
        catch (OperationCanceledException)
        {
            // Service disposed (app closing): still record the time played so far.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Process monitor for {Game} failed", session.GameId);
        }
        finally
        {
            EndSession(session);
        }
    }

    private void EndSession(GameSession session, bool pollingTail = true)
    {
        _sessions.TryRemove(session.GameId, out _);
        session.Stop.Dispose();

        // Subtract the polling tail so a session isn't over-counted.
        var played = DateTime.UtcNow - session.StartedUtc - (pollingTail ? PollInterval * MissesBeforeSessionEnds : TimeSpan.Zero);
        if (played < TimeSpan.Zero) played = TimeSpan.Zero;

        if (repository.GetInstall(session.GameId) is { } install)
        {
            install.TotalPlaySeconds += (long)played.TotalSeconds;
            install.LastPlayedAtUtc = DateTime.UtcNow;
            repository.UpsertInstall(install);
        }

        logger.LogInformation("Session ended for {Game}: {Played}", session.GameId, played);
        Raise(new GameSessionEventArgs(session.GameId, false, played));
    }

    // =============================================================================================
    // Uninstall
    // =============================================================================================

    public async Task UninstallAsync(string gameId, CancellationToken cancellationToken = default)
    {
        if (IsRunning(gameId)) throw new InvalidOperationException("Close the game before uninstalling it.");

        var install = repository.GetInstall(gameId) ?? throw new InvalidOperationException("This game isn't installed.");
        var settings = repository.GetSettings();
        var directory = install.InstallDirectory;
        var hasFolder = !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory);
        var uninstaller = install.QuietUninstallCommand ?? install.UninstallCommand;

        // Games installed by a setup program are removed with their own uninstaller (cleans registry and
        // shortcuts); the folder check below then only has to sweep up leftovers.
        var folderIsSafe = !hasFolder || IsSafeToDelete(directory, install.Title, settings.LibraryRoot, out _);
        if (hasFolder && !folderIsSafe && uninstaller is null)
        {
            IsSafeToDelete(directory, install.Title, settings.LibraryRoot, out var reason);
            throw new InvalidOperationException(reason);
        }

        using var stopWaiting = new CancellationTokenSource();
        if (!_uninstalls.TryAdd(gameId, stopWaiting)) throw new InvalidOperationException("This game is already being uninstalled.");

        install.State = GameInstallState.Uninstalling;
        repository.UpsertInstall(install);
        InstallChanged?.Invoke(this, gameId);

        try
        {
            if (hasFolder && settings.PreserveInFolderSaves)
                await saveDataGuard.BackupAsync(gameId, directory, cancellationToken).ConfigureAwait(false);

            if (uninstaller is not null)
                await RunUninstallerAsync(install, uninstaller, stopWaiting.Token, cancellationToken).ConfigureAwait(false);

            if (hasFolder && folderIsSafe && Directory.Exists(directory))
                await PathSafety.DeleteDirectoryRobustAsync(directory, cancellationToken).ConfigureAwait(false);

            await DeleteSetupFolderAsync(install, cancellationToken).ConfigureAwait(false);
            MarkNotInstalled(install);
            repository.UpsertInstall(install);
            logger.LogInformation("Uninstalled {Title} from {Dir}", install.Title, directory);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Uninstall of {Title} failed", install.Title);
            install.State = Directory.Exists(directory) ? GameInstallState.Installed : GameInstallState.NotInstalled;
            install.LastError = $"Uninstall failed: {ex.Message}";
            repository.UpsertInstall(install);
            throw;
        }
        finally
        {
            _uninstalls.TryRemove(gameId, out _);
            InstallChanged?.Invoke(this, gameId);
        }
    }

    /// <summary>
    /// Runs the registered uninstaller and waits (max 15 minutes, or until <paramref name="stopWaiting"/>) for it and
    /// everything it starts — Inno's and NSIS's uninstallers hand over to a copy of themselves in %TEMP% and exit at once.
    /// Its windows are brought to the front, so its "Are you sure?" can't hide behind SeaRaider. Failures are logged, not fatal.
    /// </summary>
    private async Task RunUninstallerAsync(InstalledGame install, string commandLine, CancellationToken stopWaiting, CancellationToken ct)
    {
        if (CommandLine.Split(commandLine) is not { } command) return;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(command.FileName)
            {
                Arguments = command.Arguments,
                UseShellExecute = true,
            });
            if (process is null) return;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, stopWaiting);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            try
            {
                if (OperatingSystem.IsWindows()) await WaitForProcessTreeAsync(process.Id, timeout.Token).ConfigureAwait(false);
                else await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopWaiting.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                logger.LogInformation("Stopped waiting for the uninstaller of {Title}; removing its folder", install.Title);
                return;
            }

            // Inno Setup's unins000.exe copies itself to %TEMP% and exits at once, then the copy removes the files.
            // Wait until the folder is gone, or its contents stop changing with nothing running from it (max 2 min).
            var directory = install.InstallDirectory;
            var deadline = DateTime.UtcNow.AddMinutes(2);
            long lastCount = -1;
            var stablePolls = 0;
            while (!string.IsNullOrEmpty(directory) && Directory.Exists(directory) && DateTime.UtcNow < deadline && stablePolls < 2)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token).ConfigureAwait(false);
                long count;
                try { count = Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories).LongCount(); }
                catch (DirectoryNotFoundException) { break; }
                stablePolls = count == lastCount && !ProcessScanner.AnyRunningFrom(directory) ? stablePolls + 1 : 0;
                lastCount = count;
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelledByUser)
        {
            throw new OperationCanceledException("Uninstall cancelled at the administrator prompt.", ex);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Uninstaller for {Title} failed; removing the folder directly", install.Title);
        }
    }

    /// <summary>Waits until the process and every process it started (even after it exits) have ended; raises their windows.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task WaitForProcessTreeAsync(int rootId, CancellationToken ct)
    {
        var tracked = new HashSet<int> { rootId };
        var raised = new HashSet<IntPtr>();
        while (true)
        {
            var all = ProcessTreeTracker.Snapshot();
            var alive = all.Select(p => p.Id).ToHashSet();
            bool added;
            do
            {
                added = false;
                foreach (var (id, parent, _) in all)
                    // Parents are matched before dead ones are dropped below, so a child started just before its parent
                    // exited (the uninstaller's hand-over to its %TEMP% copy) is still caught.
                    if (!tracked.Contains(id) && tracked.Contains(parent))
                    {
                        tracked.Add(id);
                        added = true;
                    }
            } while (added);
            tracked.RemoveWhere(id => !alive.Contains(id));
            if (tracked.Count == 0) return;

            foreach (var window in WindowSurfacer.FindWindows(tracked))
                if (window.Visible && raised.Add(window.Handle)) WindowSurfacer.BringToFront(window.Handle);

            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Last line of defence against deleting the wrong folder because of bad data.</summary>
    private static bool IsSafeToDelete(string directory, string title, string libraryRoot, out string reason)
    {
        var full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        reason = string.Empty;

        if (Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) == full)
        {
            reason = $"Refusing to delete a drive root ({full}).";
            return false;
        }

        var protectedFolders = new[]
        {
            Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Desktop,
            Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.CommonApplicationData,
        }
        .Select(folder => Environment.GetFolderPath(folder))
        .Where(p => !string.IsNullOrEmpty(p))
        .Append(Path.GetFullPath(libraryRoot));

        foreach (var folder in protectedFolders)
        {
            if (string.Equals(full, folder.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                reason = $"Refusing to delete a protected folder ({full}).";
                return false;
            }
        }

        var insideLibrary = PathSafety.IsUnder(full, libraryRoot);
        var namedAfterGame = Path.GetFileName(full).StartsWith(PathSafety.ToFolderName(title), StringComparison.OrdinalIgnoreCase);
        if (!insideLibrary && !namedAfterGame)
        {
            reason = $"{full} is outside your library folder and isn't named after the game. Delete it manually if you're sure.";
            return false;
        }
        return true;
    }

    // =============================================================================================
    // Exe re-detection
    // =============================================================================================

    public Task<string?> RedetectExecutableAsync(string gameId, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var install = repository.GetInstall(gameId);
            if (install is null || !Directory.Exists(install.InstallDirectory)) return null;

            var exe = executableLocator.FindPrimaryExecutable(install.InstallDirectory, install.Title);
            install.ExecutablePath = exe;
            install.LastError = exe is null ? "No executable found — choose one manually." : null;
            repository.UpsertInstall(install);
            InstallChanged?.Invoke(this, gameId);
            return exe;
        }, cancellationToken);

    public void SetExecutable(string gameId, string executablePath)
    {
        var install = repository.GetInstall(gameId) ?? throw new InvalidOperationException("This game isn't installed.");
        var full = Path.GetFullPath(executablePath);

        if (!full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            throw new ArgumentException("Choose an existing .exe file.", nameof(executablePath));
        if (!PathSafety.IsUnder(full, install.InstallDirectory))
            throw new ArgumentException("The executable must be inside the game's install folder.", nameof(executablePath));

        install.ExecutablePath = full;
        install.LastError = null;
        repository.UpsertInstall(install);
        InstallChanged?.Invoke(this, gameId);
    }

    // =============================================================================================
    // Plumbing
    // =============================================================================================

    private void Raise(GameSessionEventArgs args)
    {
        try { SessionChanged?.Invoke(this, args); }
        catch (Exception ex) { logger.LogError(ex, "SessionChanged subscriber threw"); }
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
        {
            try { session.Stop.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }
}
