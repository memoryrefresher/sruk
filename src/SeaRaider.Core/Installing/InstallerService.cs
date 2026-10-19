using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Catalog;
using SeaRaider.Core.Launching;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Installing;

/// <summary>
/// The "Install" button: runs a pending setup program, tracks it, and on success converts the game into a
/// normal playable install (exe resolved, uninstaller recorded, setup files deleted).
/// </summary>
public sealed class InstallerService : IInstallerService
{
    private const int ErrorCancelledByUser = 1223; // ERROR_CANCELLED: the UAC prompt was declined

    private readonly ILibraryRepository _repository;
    private readonly IExecutableLocator _locator;
    private readonly ISaveDataGuard _saveGuard;
    private readonly ILogger<InstallerService> _logger;
    private readonly SilentInstallManager _setups;
    private readonly ConcurrentDictionary<string, byte> _running = new();

    public InstallerService(ILibraryRepository repository, IExecutableLocator locator, ISaveDataGuard saveGuard,
        SilentInstallManager setups, ILogger<InstallerService> logger)
    {
        _repository = repository;
        _locator = locator;
        _saveGuard = saveGuard;
        _setups = setups;
        _logger = logger;
        _setups.ActivityChanged += (_, activity) => SetupActivityChanged?.Invoke(this, activity);
        RecoverInterruptedRuns();
    }

    public event EventHandler<string>? InstallerStateChanged;

    public event EventHandler<SetupActivity>? SetupActivityChanged;

    public event EventHandler<SetupCleanupOffer>? SetupCleanupOffered;

    public bool IsRunning(string gameId) => _running.ContainsKey(gameId);

    public SetupActivity? GetSetupActivity(string gameId) => _setups.GetActivity(gameId);

    public bool ShowSetupWindow(string gameId) => _setups.BringToFront(gameId);

    public bool RestartWithWizard(string gameId) => _setups.RequestInteractiveRestart(gameId);

    public async Task<InstallerRunResult> RunInstallerAsync(string gameId, bool? silent = null, CancellationToken cancellationToken = default)
    {
        var install = _repository.GetInstall(gameId) ?? throw new InvalidOperationException("This game has no pending installer.");
        if (install.State != GameInstallState.ReadyToInstall)
            throw new InvalidOperationException($"{install.Title} has no setup waiting to run.");
        if (!_running.TryAdd(gameId, 0))
            throw new InvalidOperationException($"The installer for {install.Title} is already running.");

        try
        {
            var installerPath = install.InstallerPath;
            if (string.IsNullOrEmpty(installerPath) || !File.Exists(installerPath))
            {
                // Setup folder moved or the file was renamed: look again.
                installerPath = install.SetupDirectory is { } setupDir ? InstallerDetector.FindInstallers(setupDir).FirstOrDefault() : null;
                if (installerPath is null)
                    throw new FileNotFoundException("The setup files are missing. Remove the game and download it again.");
                install.InstallerPath = installerPath;
            }

            // Re-detect every run: older records only know the engine family, and detection is cheap.
            var engine = SilentInstallManager.Inspect(installerPath);
            install.InstallerKind = engine.Kind;
            install.InstallerEngine = engine.Describe();
            install.InstallerIsRepack = engine.IsRepack;

            var settings = _repository.GetSettings();
            var mode = SetupEngineDetector.ChooseMode(engine, silent ?? install.SilentInstall, settings.ShowSetupProgress);
            var target = install.InstallDirectory;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            SetState(install, GameInstallState.RunningInstaller, InstallerState.Running, null);

            var registryBefore = UninstallRegistry.Snapshot();
            int? exitCode;
            try
            {
                var run = new SetupRun(gameId, installerPath, engine, target, mode, install.InstallerArguments, install.SetupDirectory,
                    TimeSpan.FromSeconds(Math.Clamp(settings.InstallStallSeconds, 5, 300)), settings.AutoFallbackToInteractive,
                    MuteAudio: settings.MuteSetupAudio,
                    // Repacks run their extra installers and a web page at the end, and play music: the setup helper handles them.
                    Helper: engine.IsRepack ? new SetupHelperOptions(settings.SetupRuntimes, settings.BlockSetupWebPages, settings.UncheckSetupLowRam) : null);
                var outcome = await _setups.RunAsync(run, cancellationToken).ConfigureAwait(false);
                exitCode = outcome.ExitCode;
                if (outcome.FellBackToInteractive) install.SilentInstall = false; // remember: this one needs its wizard
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelledByUser)
            {
                return Finish(install, InstallerState.Cancelled, "Setup was cancelled at the administrator prompt.", null, cancelled: true);
            }

            install.InstallerExitCode = exitCode;
            install.InstallerLastRunUtc = DateTime.UtcNow;

            var (success, userCancelled) = exitCode is { } code
                ? InstallerDetector.ClassifyExitCode(install.InstallerKind, code)
                : (true, false); // exit code unavailable (handle not granted): judge by the result below

            if (!success)
            {
                return userCancelled
                    ? Finish(install, InstallerState.Cancelled, "Setup was cancelled. Click Install to run it again.", exitCode, cancelled: true)
                    : Finish(install, InstallerState.Failed, $"Setup failed (exit code {exitCode}). Try again without silent mode.", exitCode);
            }

            return await CompleteAsync(install, registryBefore, exitCode).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // We stopped waiting (app closing); the installer itself may still be running.
            var current = _repository.GetInstall(gameId) ?? install;
            if (current.State == GameInstallState.RunningInstaller)
                Finish(current, InstallerState.Pending, "Stopped tracking setup. If the game didn't install, click Install again.", null);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Installer run failed for {Game}", gameId);
            var current = _repository.GetInstall(gameId) ?? install;
            Finish(current, InstallerState.Failed, ex.Message, current.InstallerExitCode);
            throw;
        }
        finally
        {
            _running.TryRemove(gameId, out _);
        }
    }

    public async Task DiscardInstallerAsync(string gameId, CancellationToken cancellationToken = default)
    {
        if (IsRunning(gameId)) throw new InvalidOperationException("The installer is running.");
        var install = _repository.GetInstall(gameId);
        if (install is null) return;

        if (await TryDeleteSetupFolderAsync(install, cancellationToken).ConfigureAwait(false) is { } problem)
        {
            _repository.UpsertInstall(install); // what's still there stays recorded, so the next try (or the clean-up) finds it
            throw new IOException(problem);
        }
        install.State = GameInstallState.NotInstalled;
        install.InstallerState = InstallerState.None;
        install.LastError = null;
        _repository.UpsertInstall(install);
        InstallerStateChanged?.Invoke(this, gameId);
    }

    // =============================================================================================
    // Steps
    // =============================================================================================

    /// <summary>Resolves where the game landed, finds its exe, records the uninstaller and cleans up.</summary>
    private async Task<InstallerRunResult> CompleteAsync(InstalledGame install, IReadOnlyDictionary<string, UninstallEntry> registryBefore,
        int? exitCode)
    {
        var settings = _repository.GetSettings();
        var registryEntry = UninstallRegistry.Diff(registryBefore, UninstallRegistry.Snapshot(), install.Title).FirstOrDefault();

        // 1. Our target folder (Inno / NSIS / most MSIs honour it).
        string? location = HasExecutables(install.InstallDirectory) ? install.InstallDirectory : null;

        // 2. Where the installer says it put the game.
        if (location is null && registryEntry?.InstallLocation is { Length: > 0 } registered && HasExecutables(registered))
            location = registered;
        if (location is null && registryEntry?.DisplayIconExecutable is { } icon)
            location = Path.GetDirectoryName(icon);
        if (location is null && CommandLine.Split(registryEntry?.UninstallString) is { } uninstaller &&
            !uninstaller.FileName.Contains("msiexec", StringComparison.OrdinalIgnoreCase) && File.Exists(uninstaller.FileName))
            location = Path.GetDirectoryName(uninstaller.FileName);

        var exe = location is null ? null : _locator.FindPrimaryExecutable(location, install.Title);
        if (exe is null && registryEntry?.DisplayIconExecutable is { } iconExe &&
            !ExecutableLocator.IsExcludedName(Path.GetFileNameWithoutExtension(iconExe)))
            exe = iconExe;

        if (location is null && exitCode is null)
        {
            // No exit code and nothing installed anywhere we can see: most likely the wizard was closed.
            return Finish(install, InstallerState.Cancelled, "Setup closed without installing. Click Install to try again.", null, cancelled: true);
        }

        install.InstallDirectory = location ?? install.InstallDirectory;
        install.ExecutablePath = exe;
        install.SizeOnDiskBytes = PathSafety.GetDirectorySize(install.InstallDirectory);
        install.InstalledAtUtc = DateTime.UtcNow;
        (install.UninstallCommand, install.QuietUninstallCommand) = ResolveUninstaller(install, registryEntry);

        if (settings.PreserveInFolderSaves && Directory.Exists(install.InstallDirectory))
            await _saveGuard.RestoreAsync(install.GameId, install.InstallDirectory, CancellationToken.None).ConfigureAwait(false);

        if (settings.SetupFilesAfterInstall == SetupFilesMode.Delete &&
            await TryDeleteSetupFolderAsync(install, CancellationToken.None).ConfigureAwait(false) is { } problem)
            _logger.LogWarning("Setup files of {Title} stay for now: {Problem}", install.Title, problem);
        install.KeepSetupFiles = settings.SetupFilesAfterInstall == SetupFilesMode.Keep;

        install.State = GameInstallState.Installed;
        install.InstallerState = InstallerState.Succeeded;
        install.LastError = exe is null ? "Setup finished, but the game's .exe wasn't found. Choose it to play." : null;
        _repository.UpsertInstall(install);
        InstallerStateChanged?.Invoke(this, install.GameId);

        // The setup checked its files as it installed; now its files are dead weight. Ask (default) before deleting them.
        if (settings.SetupFilesAfterInstall == SetupFilesMode.Ask && CleanupOfferFor(install) is { } offer)
            SetupCleanupOffered?.Invoke(this, offer);

        _logger.LogInformation("Installed {Title} via setup into {Dir} (exe: {Exe})", install.Title, install.InstallDirectory, exe ?? "none");
        return new InstallerRunResult(true, false, exitCode, exe, exe is null ? "Installed. Choose the game's .exe to play." : "Installed and ready to play.");
    }

    /// <summary>Registry uninstall strings when present, else the uninstaller the installer type is known to leave behind.</summary>
    private static (string? Command, string? Quiet) ResolveUninstaller(InstalledGame install, UninstallEntry? entry)
    {
        if (entry?.UninstallString is { Length: > 0 } registered)
        {
            var quiet = entry.QuietUninstallString;
            if (quiet is null && registered.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
                quiet = registered.Replace("/I", "/X", StringComparison.OrdinalIgnoreCase) + " /qn /norestart";
            if (quiet is null && install.InstallerKind == InstallerKind.InnoSetup) quiet = registered + " /VERYSILENT /SUPPRESSMSGBOXES /NORESTART";
            if (quiet is null && install.InstallerKind == InstallerKind.Nsis) quiet = registered + " /S";
            return (registered, quiet);
        }

        if (!Directory.Exists(install.InstallDirectory)) return (null, null);
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1, IgnoreInaccessible = true };
        var uninstaller = Directory.EnumerateFiles(install.InstallDirectory, "unins*.exe", options).FirstOrDefault()
                          ?? Directory.EnumerateFiles(install.InstallDirectory, "uninst*.exe", options).FirstOrDefault();
        if (uninstaller is null) return (null, null);

        var command = $"\"{uninstaller}\"";
        return Path.GetFileName(uninstaller).StartsWith("unins0", StringComparison.OrdinalIgnoreCase)
            ? (command, command + " /VERYSILENT /SUPPRESSMSGBOXES /NORESTART") // Inno Setup
            : (command, command + " /S");                                        // NSIS
    }

    public IReadOnlyList<SetupCleanupOffer> GetPendingSetupCleanups() =>
        _repository.GetInstalls().Select(CleanupOfferFor).OfType<SetupCleanupOffer>().ToList();

    public async Task<long> DeleteSetupFilesAsync(string gameId, CancellationToken cancellationToken = default)
    {
        if (IsRunning(gameId)) throw new InvalidOperationException("The installer is running.");
        var install = _repository.GetInstall(gameId);
        if (install?.SetupDirectory is not { } setupDir || !Directory.Exists(setupDir)) return 0;

        var bytes = PathSafety.GetDirectorySize(setupDir);
        if (await TryDeleteSetupFolderAsync(install, cancellationToken).ConfigureAwait(false) is { } problem)
            throw new IOException(problem);
        _repository.UpsertInstall(install);
        InstallerStateChanged?.Invoke(this, gameId);
        _logger.LogInformation("Deleted {Bytes} bytes of setup files for {Title}", bytes, install.Title);
        return bytes;
    }

    public void KeepSetupFiles(string gameId)
    {
        if (_repository.GetInstall(gameId) is not { } install) return;
        install.KeepSetupFiles = true;
        _repository.UpsertInstall(install);
    }

    /// <summary>An offer to delete an installed game's leftover setup files, or null when there's nothing to offer.</summary>
    private SetupCleanupOffer? CleanupOfferFor(InstalledGame install)
    {
        if (install.State != GameInstallState.Installed || install.KeepSetupFiles || IsRunning(install.GameId)) return null;
        if (install.SetupDirectory is not { } setupDir || !Directory.Exists(setupDir) || !IsOurs(setupDir)) return null;
        // A setup folder the game was installed into is the game.
        if (Overlaps(install.InstallDirectory, setupDir)) return null;
        var bytes = PathSafety.GetDirectorySize(setupDir);
        return bytes > 0 ? new SetupCleanupOffer(install.GameId, DisplayText.Title(install.Title), bytes) : null;
    }

    /// <summary>The same folder, or one inside the other.</summary>
    private static bool Overlaps(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), comparison) ||
               PathSafety.IsUnder(a, b) || PathSafety.IsUnder(b, a);
    }

    /// <summary>Inside SeaRaider's own setup or temporary folders (the only places it ever deletes setup files from).</summary>
    private bool IsOurs(string setupDir)
    {
        var settings = _repository.GetSettings();
        return PathSafety.IsUnder(setupDir, SetupPaths.SetupRoot(settings.LibraryRoot)) || PathSafety.IsUnder(setupDir, settings.TempRoot);
    }

    /// <summary>
    /// Deletes a game's setup folder and checks it's really gone. Setup helpers still running from it (an unpacker left
    /// behind, a launcher the setup started) are closed first, since they hold its files open. Returns null when the folder
    /// is gone, else why it isn't (the record keeps pointing at it, so it can be retried or swept up later).
    /// </summary>
    private async Task<string?> TryDeleteSetupFolderAsync(InstalledGame install, CancellationToken ct)
    {
        var setupDir = install.SetupDirectory;
        if (string.IsNullOrEmpty(setupDir) || !Directory.Exists(setupDir))
        {
            install.SetupDirectory = null;
            install.InstallerPath = null;
            return null;
        }

        // Only ever delete inside SeaRaider's own setup area, and never the game itself.
        if (!IsOurs(setupDir)) return $"Not deleting {setupDir}: it's outside SeaRaider's setup folders.";
        if (Overlaps(install.InstallDirectory, setupDir)) return $"Not deleting {setupDir}: the game is installed there.";
        if (IsRunning(install.GameId)) return "The setup is still running. Close it first.";

        var problem = await DeleteFolderAsync(setupDir, ct).ConfigureAwait(false);
        if (problem is not null) return problem;

        install.SetupDirectory = null;
        install.InstallerPath = null;
        RemoveEmptySetupRoot(Path.GetDirectoryName(setupDir));
        return null;
    }

    /// <summary>Closes what runs from the folder, deletes it, and says why not when it's still there.</summary>
    private async Task<string?> DeleteFolderAsync(string folder, CancellationToken ct)
    {
        var closed = CloseProcessesRunningFrom(folder);
        if (closed.Count > 0)
        {
            _logger.LogInformation("Closed {Programs} running from {Dir} to delete it", string.Join(", ", closed), folder);
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false); // let Windows release their files
        }

        try
        {
            await PathSafety.DeleteDirectoryRobustAsync(folder, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete {Dir}", folder);
        }

        if (!Directory.Exists(folder)) return null;
        var left = PathSafety.GetDirectorySize(folder);
        return $"Some setup files couldn't be deleted ({Formatting.Bytes(left)} left in {folder}). " +
               "Something still has them open: an Explorer window on that folder, an antivirus scan, or a setup that's still running. " +
               "Close it and try again.";
    }

    /// <summary>Ends programs whose .exe is inside <paramref name="folder"/> (setup helpers; the game lives elsewhere).</summary>
    private static List<string> CloseProcessesRunningFrom(string folder)
    {
        var closed = new List<string>();
        foreach (var id in ProcessScanner.ProcessIdsRunningFrom(folder))
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(id);
                var name = process.ProcessName;
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
                closed.Add(name);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // already gone, or not ours to close (elevated): the delete reports what's still held
            }
        }
        return closed;
    }

    private static void RemoveEmptySetupRoot(string? root)
    {
        try
        {
            if (root is not null && Path.GetFileName(root) == ".searaider-setup" && Directory.Exists(root) &&
                !Directory.EnumerateFileSystemEntries(root).Any())
                Directory.Delete(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // something just appeared in it
        }
    }

    // =============================================================================================
    // Leftover setup folders
    // =============================================================================================

    public SetupLeftovers GetLeftoverSetupFiles()
    {
        var folders = FindLeftoverFolders();
        return new SetupLeftovers(folders.Count, folders.Sum(PathSafety.GetDirectorySize));
    }

    public async Task<SetupLeftovers> CleanUpLeftoverSetupFilesAsync(CancellationToken cancellationToken = default)
    {
        var folders = FindLeftoverFolders();
        string? lastProblem = null;
        foreach (var folder in folders)
        {
            var problem = await DeleteFolderAsync(folder, cancellationToken).ConfigureAwait(false);
            if (problem is not null)
            {
                lastProblem = problem;
                continue;
            }
            _logger.LogInformation("Deleted leftover setup folder {Dir}", folder);
            RemoveEmptySetupRoot(Path.GetDirectoryName(folder));
        }

        // Records of removed games that still pointed at a (now gone) setup folder.
        foreach (var install in _repository.GetInstalls().Where(i => i.State == GameInstallState.NotInstalled && i.SetupDirectory is not null))
        {
            if (Directory.Exists(install.SetupDirectory)) continue;
            install.SetupDirectory = null;
            install.InstallerPath = null;
            _repository.UpsertInstall(install);
        }

        var left = FindLeftoverFolders();
        return new SetupLeftovers(left.Count, left.Sum(PathSafety.GetDirectorySize), left.Count > 0 ? lastProblem : null);
    }

    /// <summary>Setup folders nothing needs: not waiting to install, not being downloaded, not an installed game's untried setup.</summary>
    private List<string> FindLeftoverFolders()
    {
        var settings = _repository.GetSettings();
        var installs = _repository.GetInstalls();
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        static string Full(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');

        // Every setup area SeaRaider has used: the library's, and older ones (the library folder was moved).
        var roots = new HashSet<string>(comparer);
        if (!string.IsNullOrWhiteSpace(settings.LibraryRoot)) roots.Add(Full(SetupPaths.SetupRoot(settings.LibraryRoot)));
        foreach (var install in installs)
        {
            if (install.SetupDirectory is { Length: > 0 } dir && Path.GetDirectoryName(Full(dir)) is { } parent &&
                Path.GetFileName(parent) == ".searaider-setup")
                roots.Add(parent);
        }

        var needed = installs
            .Where(i => i.SetupDirectory is { Length: > 0 } && i.State != GameInstallState.NotInstalled)
            .Select(i => Full(i.SetupDirectory!))
            .ToHashSet(comparer);
        var busy = _repository.GetJobs()
            .Where(j => j.Status is DownloadJobStatus.Queued or DownloadJobStatus.Downloading or DownloadJobStatus.Paused
                or DownloadJobStatus.Extracting or DownloadJobStatus.Finalizing)
            .Select(j => j.GameId)
            .ToHashSet(comparer);

        var leftovers = new List<string>();
        foreach (var root in roots.Where(Directory.Exists))
        {
            IEnumerable<string> folders;
            try { folders = Directory.GetDirectories(root); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (var folder in folders.Select(Full))
            {
                var gameId = Path.GetFileName(folder);
                if (needed.Contains(folder) || busy.Contains(gameId) || IsRunning(gameId)) continue;
                // A game installed into its own setup folder: that's the game, not leftovers.
                if (installs.Any(i => i.State != GameInstallState.NotInstalled && Overlaps(i.InstallDirectory, folder))) continue;
                leftovers.Add(folder);
            }
        }
        return leftovers;
    }

    // =============================================================================================
    // Helpers
    // =============================================================================================

    private InstallerRunResult Finish(InstalledGame install, InstallerState state, string message, int? exitCode, bool cancelled = false)
    {
        SetState(install, GameInstallState.ReadyToInstall, state, message);
        return new InstallerRunResult(false, cancelled, exitCode, null, message);
    }

    private void SetState(InstalledGame install, GameInstallState state, InstallerState installerState, string? error)
    {
        install.State = state;
        install.InstallerState = installerState;
        install.LastError = error;
        _repository.UpsertInstall(install);
        InstallerStateChanged?.Invoke(this, install.GameId);
    }

    private static bool HasExecutables(string? directory) =>
        !string.IsNullOrEmpty(directory) && Directory.Exists(directory) &&
        Directory.EnumerateFiles(directory, "*.exe", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Any();

    /// <summary>An installer that was running when the app closed is back to "ready to install".</summary>
    private void RecoverInterruptedRuns()
    {
        foreach (var install in _repository.GetInstalls().Where(i => i.State == GameInstallState.RunningInstaller))
        {
            install.State = GameInstallState.ReadyToInstall;
            install.InstallerState = InstallerState.Pending;
            install.LastError = "SeaRaider closed while setup was running. If the game didn't install, click Install again.";
            _repository.UpsertInstall(install);
        }
    }
}

/// <summary>Locations SeaRaider uses for setup programs.</summary>
public static class SetupPaths
{
    /// <summary>&lt;Library&gt;\.searaider-setup — same drive as the library, so moving extracted files there is instant.</summary>
    public static string SetupRoot(string libraryRoot) => Path.Combine(libraryRoot, ".searaider-setup");

    public static string SetupDirectory(string libraryRoot, string gameId) => Path.Combine(SetupRoot(libraryRoot), gameId);
}
