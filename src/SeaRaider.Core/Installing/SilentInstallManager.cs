using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Installing;

/// <summary>Where a setup run is, for the UI.</summary>
public enum SetupPhase
{
    Starting,

    /// <summary>Using CPU or disk: installing.</summary>
    Working,

    /// <summary>Idle with a window open: waiting for the user. The window has been brought to the front.</summary>
    NeedsInput,

    /// <summary>Idle with no window to show.</summary>
    Stuck,

    /// <summary>Closing the silent run to start the normal wizard.</summary>
    Restarting,
}

/// <param name="GameId">Game being installed.</param>
/// <param name="Phase">Current phase.</param>
/// <param name="Message">User-facing status line.</param>
/// <param name="Mode">How the setup is currently running.</param>
/// <param name="CanShowWindow">The setup has a window SeaRaider can bring to the front.</param>
/// <param name="CanRestartInteractive">The run is silent and can be restarted with the normal wizard.</param>
public sealed record SetupActivity(string GameId, SetupPhase Phase, string Message, InstallMode Mode, bool CanShowWindow,
    bool CanRestartInteractive);

/// <param name="GameId">Game being installed.</param>
/// <param name="InstallerPath">Setup executable or .msi.</param>
/// <param name="Engine">Detected engine.</param>
/// <param name="TargetDirectory">Folder the game should be installed to.</param>
/// <param name="Mode">Requested mode (see <see cref="SetupEngineDetector.ChooseMode"/>).</param>
/// <param name="CustomArguments">User's own switches, replacing the generated ones ("{dir}" = target).</param>
/// <param name="SetupDirectory">Folder with the setup files (processes started from it count as part of the setup).</param>
/// <param name="StallThreshold">Idle time before the setup counts as waiting.</param>
/// <param name="AutoFallback">Restart a windowless stalled silent run with its wizard.</param>
/// <param name="MuteAudio">Mute the setup's processes in the volume mixer (repack installers play music).</param>
/// <param name="Helper">Run the setup under SeaRaider's setup helper (Windows; see <see cref="SetupRunner"/>), or null.</param>
public sealed record SetupRun(string GameId, string InstallerPath, SetupEngineInfo Engine, string TargetDirectory, InstallMode Mode,
    string? CustomArguments, string? SetupDirectory, TimeSpan StallThreshold, bool AutoFallback, bool MuteAudio = false,
    SetupHelperOptions? Helper = null);

/// <param name="Runtimes">DirectX / Visual C++ / .NET installers: install quietly after the setup, skip, or leave alone.</param>
/// <param name="BlockWebPages">Close the web page a setup opens when it finishes.</param>
/// <param name="UncheckLowRam">Untick the "limit installer RAM usage" option on the setup's first page.</param>
public sealed record SetupHelperOptions(SetupRuntimes Runtimes, bool BlockWebPages, bool UncheckLowRam = true);

/// <param name="ExitCode">Exit code of the setup process, when Windows provided it.</param>
/// <param name="FinalMode">The mode of the run that finished.</param>
/// <param name="FellBackToInteractive">The silent run was replaced by the normal wizard.</param>
public sealed record SetupRunOutcome(int? ExitCode, InstallMode FinalMode, bool FellBackToInteractive);

/// <summary>
/// Runs setup programs without leaving them stuck in the background.
/// <list type="number">
///   <item>Engine detection from the binary (<see cref="SetupEngineDetector"/>) picks the right unattended switches.</item>
///   <item>The whole process tree is watched (<see cref="ProcessTreeTracker"/>): a run is finished only when every
///         process the setup started has exited.</item>
///   <item>When the tree uses no CPU and does no disk I/O for the stall threshold (15 s by default), the setup is
///         waiting for something: its windows are brought to the front, hidden wizard/dialog windows of a silent run
///         are revealed, and the UI offers "Show setup window" and "Restart with wizard".</item>
///   <item>If a silent run is stalled with no window at all for another 30 s and hasn't written anything to the game
///         folder, it's closed and started again with its normal wizard, so nobody has to kill it in Task Manager.</item>
/// </list>
/// </summary>
public sealed class SilentInstallManager(ILogger<SilentInstallManager> logger)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MuteInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan FallbackGrace = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, RunControl> _runs = new();

    /// <summary>Raised on a worker thread whenever a run's phase or message changes.</summary>
    public event EventHandler<SetupActivity>? ActivityChanged;

    public static SetupEngineInfo Inspect(string installerPath) => SetupEngineDetector.Detect(installerPath);

    public SetupActivity? GetActivity(string gameId) => _runs.TryGetValue(gameId, out var run) ? run.Last : null;

    /// <summary>Brings a running setup's windows to the front (revealing hidden wizard windows). False if it has none.</summary>
    public bool BringToFront(string gameId)
    {
        if (!_runs.TryGetValue(gameId, out var run)) return false;
        var result = WindowSurfacer.Surface(run.Processes, revealHidden: true);
        return result.Any;
    }

    /// <summary>Asks a running silent setup to be closed and restarted with its wizard (handled within a second).</summary>
    public bool RequestInteractiveRestart(string gameId)
    {
        if (!_runs.TryGetValue(gameId, out var run) || run.Mode == InstallMode.Interactive) return false;
        run.RestartRequested = true;
        return true;
    }

    /// <summary>Starts the setup and returns once it and every process it started have exited.</summary>
    /// <exception cref="Win32Exception">The setup couldn't start (1223 = the administrator prompt was declined).</exception>
    public async Task<SetupRunOutcome> RunAsync(SetupRun run, CancellationToken cancellationToken)
    {
        var control = new RunControl(run.GameId, run.Mode);
        if (!_runs.TryAdd(run.GameId, control)) throw new InvalidOperationException("This game's setup is already running.");

        try
        {
            var mode = run.Mode;
            var fellBack = false;
            while (true)
            {
                control.Mode = mode;
                var (exitCode, restart) = await RunOnceAsync(run, mode, control, cancellationToken).ConfigureAwait(false);
                if (!restart) return new SetupRunOutcome(exitCode, mode, fellBack);

                logger.LogInformation("Restarting setup for {Game} with its wizard", run.GameId);
                mode = InstallMode.Interactive;
                fellBack = true;
                control.RestartRequested = false;
                control.IgnoreCustomArguments = true; // the user's switches are what made it silent
            }
        }
        finally
        {
            _runs.TryRemove(run.GameId, out _);
        }
    }

    private async Task<(int? ExitCode, bool Restart)> RunOnceAsync(SetupRun run, InstallMode mode, RunControl control, CancellationToken ct)
    {
        var (fileName, arguments) = SetupEngineDetector.BuildCommand(run.InstallerPath, run.Engine.Kind, run.TargetDirectory, mode,
            control.IgnoreCustomArguments ? null : run.CustomArguments);
        var workingDirectory = Path.GetDirectoryName(run.InstallerPath)!;
        var startInfo = new ProcessStartInfo(fileName)
        {
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = true, // lets setups that need admin rights raise the UAC prompt
        };

        // Repacks: through the setup helper, which runs it as administrator (the one prompt the repack would raise
        // anyway) and keeps its extra installers, end-of-setup web page and music in check.
        string? helperLog = null;
        if (run.Helper is { } helper && HelperPath() is { } self)
        {
            helperLog = Path.Combine(Path.GetTempPath(), $"searaider-setup-{Guid.NewGuid():N}.log");
            var request = new SetupRunnerRequest(fileName, arguments, workingDirectory, run.TargetDirectory, helper.Runtimes,
                helper.BlockWebPages, run.MuteAudio, helperLog, helper.UncheckLowRam);
            startInfo = new ProcessStartInfo(self)
            {
                Arguments = $"{SetupRunnerRequest.Switch} {request.Encode()}",
                WorkingDirectory = workingDirectory,
                UseShellExecute = true,
                Verb = "runas",
            };
        }

        Report(control, SetupPhase.Starting, mode == InstallMode.Interactive ? "Opening the setup wizard…" : "Starting setup…");
        logger.LogInformation("Running {Engine} setup ({Mode}) for {Game}{Helper}: {File} {Args}", run.Engine.Describe(), mode, run.GameId,
            helperLog is null ? string.Empty : " through the setup helper", fileName, arguments);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Windows didn't start the setup program.");
        try
        {
            return await WatchAsync(run, mode, control, process, fileName, ct).ConfigureAwait(false);
        }
        finally
        {
            if (helperLog is not null) ForwardHelperLog(helperLog, run.GameId);
        }
    }

    private async Task<(int? ExitCode, bool Restart)> WatchAsync(SetupRun run, InstallMode mode, RunControl control, Process process,
        string fileName, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return (TryExitCode(process), false);
        }

        var tracker = new ProcessTreeTracker(process.Id, TryStartTime(process) ?? DateTime.UtcNow, run.SetupDirectory,
            includeMsiexec: run.Engine.Kind == InstallerKind.Msi || fileName.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase));
        var watchdog = new StallWatchdog(run.StallThreshold);
        int? exitCode = null;
        var emptySamples = 0;
        var windowless = false;

        Report(control, SetupPhase.Working, WorkingMessage(mode, run.Engine));

        // Every setup's audio is muted, all the time: a loop of its own (the watch loop below only looks once a second).
        using var muteStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var muteLoop = run.MuteAudio ? Task.Run(() => KeepMutedAsync(run, control, muteStop.Token), CancellationToken.None) : Task.CompletedTask;
        try
        {
            return await WatchLoopAsync(run, mode, control, process, fileName, tracker, watchdog, exitCode, emptySamples, windowless, ct).ConfigureAwait(false);
        }
        finally
        {
            muteStop.Cancel();
            try { await muteLoop.ConfigureAwait(false); }
            catch (Exception) { /* muting is a courtesy; it never fails an install */ }
        }
    }

    private async Task<(int? ExitCode, bool Restart)> WatchLoopAsync(SetupRun run, InstallMode mode, RunControl control, Process process,
        string fileName, ProcessTreeTracker tracker, StallWatchdog watchdog, int? exitCode, int emptySamples, bool windowless, CancellationToken ct)
    {
        while (true)
        {
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);

            if (exitCode is null && HasExited(process)) exitCode = TryExitCode(process);

            var sample = tracker.Sample();
            control.Processes = sample.AliveProcessIds;

            if (sample.AliveProcessIds.Count == 0)
            {
                if (++emptySamples >= 2) break; // two empty looks in a row: bootstrapper handovers are done too
                continue;
            }
            emptySamples = 0;

            if (control.RestartRequested)
            {
                control.RestartRequested = false;
                if (await TryCloseAsync(process, tracker, ct).ConfigureAwait(false)) return (null, true);
                Report(control, SetupPhase.NeedsInput,
                    "Couldn't close the silent setup (it runs as administrator). Close it from its window, then click Install with silent mode off.",
                    canShow: true);
                continue;
            }

            var now = DateTime.UtcNow;
            switch (watchdog.Add(now, sample.CpuDelta, sample.IoDelta))
            {
                case StallTransition.BecameStalled:
                    windowless = !HandleStall(control, mode, sample.AliveProcessIds, run);
                    break;

                case StallTransition.Resumed:
                    windowless = false;
                    Report(control, SetupPhase.Working, WorkingMessage(mode, run.Engine));
                    break;
            }

            // Last resort: a silent run that's idle with nothing on screen, and hasn't touched the game folder yet.
            if (windowless && run.AutoFallback && mode != InstallMode.Interactive &&
                watchdog.StalledFor(now) >= run.StallThreshold + FallbackGrace && IsUntouched(run.TargetDirectory))
            {
                Report(control, SetupPhase.Restarting, "Silent setup is stuck. Restarting it with its normal wizard…");
                if (await TryCloseAsync(process, tracker, ct).ConfigureAwait(false)) return (null, true);
                windowless = false; // couldn't close it; don't keep trying every second
                Report(control, SetupPhase.Stuck,
                    "Silent setup is stuck and couldn't be closed (it runs as administrator). Close it from Task Manager's Details tab, then click Install with silent mode off.");
            }
        }

        exitCode ??= TryExitCode(process);
        return (exitCode, false);
    }

    /// <summary>
    /// Inno Setup's real setup runs as %TEMP%\is-XXXXX.tmp\setup.tmp, and that's what plays a repack's music. Its audio is
    /// muted even if the process tree lost track of it.
    /// </summary>
    internal static bool IsInnoSetupProcess(int processId) =>
        Launching.ProcessScanner.ImagePathOf(processId) is { } path && IsInnoSetupPath(path);

    private static bool IsInnoSetupPath(string path)
    {
        var folder = Path.GetFileName(Path.GetDirectoryName(path)) ?? string.Empty;
        return folder.StartsWith("is-", StringComparison.OrdinalIgnoreCase) && folder.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) &&
               path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Which processes are the setup's even when the process tree lost them (an elevated setup is started by Windows, not by
    /// SeaRaider): the setup file itself, anything running from its folder, and the programs setups unpack into %TEMP%
    /// (Inno Setup's <c>is-XXXX.tmp\setup.tmp</c>, NSIS and self-extractor <c>*.tmp</c> folders).
    /// </summary>
    internal static Func<int, bool> SetupProcessMatcher(string? installerPath, string? setupDirectory)
    {
        var temp = TrimDirectory(Path.GetTempPath());
        var folder = string.IsNullOrWhiteSpace(setupDirectory) ? null : TrimDirectory(setupDirectory);
        return processId =>
        {
            if (Launching.ProcessScanner.ImagePathOf(processId) is not { } path) return false;
            if (IsInnoSetupPath(path)) return true;
            if (installerPath is not null && path.Equals(installerPath, StringComparison.OrdinalIgnoreCase)) return true;
            if (folder is not null && IsUnder(path, folder)) return true;

            // %TEMP%\something.tmp\anything.exe: a setup that unpacked itself.
            var parent = Path.GetDirectoryName(path);
            return parent is not null && IsUnder(path, temp) &&
                   string.Equals(Path.GetDirectoryName(parent)?.TrimEnd('\\', '/'), temp, StringComparison.OrdinalIgnoreCase) &&
                   parent.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
        };

        static string TrimDirectory(string directory) => Path.GetFullPath(directory).TrimEnd('\\', '/');
        static bool IsUnder(string path, string directory) => path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Keeps the setup's audio sessions muted for as long as the setup runs. Looks several times a second: a repack's music
    /// starts the moment its window opens, and installers open new audio sessions (or switch the sound back on) as they go.
    /// </summary>
    private async Task KeepMutedAsync(SetupRun run, RunControl control, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return;
        var muted = new HashSet<string>();
        var isSetupProcess = SetupProcessMatcher(run.InstallerPath, run.SetupDirectory);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var processes = control.Processes;
                if (Native.AudioSessionMuter.MuteNew(processes, muted, isSetupProcess) > 0)
                    logger.LogInformation("Muted the setup's audio for {Game}", run.GameId);
                await Task.Delay(MuteInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // the setup is done
        }
    }

    /// <summary>SeaRaider's own program, to start as the setup helper; null where there's no helper (not Windows, or run via "dotnet").</summary>
    private static string? HelperPath()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var self = Environment.ProcessPath;
        return self is not null && File.Exists(self) &&
               !Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? self
            : null;
    }

    /// <summary>Copies what the helper did (runtimes installed or skipped, pages closed) into SeaRaider's log.</summary>
    private void ForwardHelperLog(string path, string gameId)
    {
        try
        {
            if (!File.Exists(path)) return;
            foreach (var line in File.ReadAllLines(path))
                logger.LogInformation("Setup helper ({Game}): {Line}", gameId, line);
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the log is only informative
        }
    }

    /// <summary>Surfaces the setup's windows. Returns true when there was something to show.</summary>
    private bool HandleStall(RunControl control, InstallMode mode, IReadOnlySet<int> processes, SetupRun run)
    {
        var surfaced = WindowSurfacer.Surface(processes, revealHidden: mode != InstallMode.Interactive);
        logger.LogInformation("Setup for {Game} idle for {Seconds}s; surfaced {Count} window(s)", run.GameId,
            run.StallThreshold.TotalSeconds, surfaced.Shown);

        if (surfaced.Any)
        {
            var name = string.IsNullOrWhiteSpace(surfaced.Title) ? "Its window" : $"Its window \"{surfaced.Title}\"";
            Report(control, SetupPhase.NeedsInput, mode == InstallMode.Interactive
                ? $"The setup wizard is waiting for you. {name} is in front now."
                : $"Setup seems to be waiting for an answer. {name} is in front now.", canShow: true);
            return true;
        }

        var willFallBack = run.AutoFallback && mode != InstallMode.Interactive && IsUntouched(run.TargetDirectory);
        Report(control, SetupPhase.Stuck, mode == InstallMode.Interactive
            ? "Setup is running but isn't doing anything and has no window to show."
            : willFallBack
                ? $"Silent setup has stopped and has no window. It will restart with its normal wizard in {FallbackGrace.TotalSeconds:0} seconds."
                : "Silent setup has stopped and has no window. Use Restart with wizard.");
        return false;
    }

    /// <summary>Closes the setup and everything it started. False if any of it survived (elevated processes).</summary>
    private static async Task<bool> TryCloseAsync(Process root, ProcessTreeTracker tracker, CancellationToken ct)
    {
        try
        {
            if (!HasExited(root)) root.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            // fall through to per-process attempts
        }

        foreach (var id in tracker.Tracked.ToList())
        {
            try
            {
                using var p = Process.GetProcessById(id);
                p.Kill();
            }
            catch (Exception ex) when (ex is ArgumentException or Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // already gone, or not ours to close
            }
        }

        for (var i = 0; i < 6; i++)
        {
            await Task.Delay(500, ct).ConfigureAwait(false);
            if (tracker.Sample().AliveProcessIds.Count == 0) return true;
        }
        return false;
    }

    private void Report(RunControl control, SetupPhase phase, string message, bool canShow = false)
    {
        var activity = new SetupActivity(control.GameId, phase, message, control.Mode, canShow,
            control.Mode != InstallMode.Interactive && phase is SetupPhase.NeedsInput or SetupPhase.Stuck);
        control.Last = activity;
        ActivityChanged?.Invoke(this, activity);
    }

    private static string WorkingMessage(InstallMode mode, SetupEngineInfo engine) => mode switch
    {
        InstallMode.Silent => $"Installing silently ({engine.Describe()})…",
        InstallMode.SilentWithProgress => $"Installing ({engine.Describe()}), progress window open…",
        _ => "Setup wizard is open. Follow its steps.",
    };

    private static bool IsUntouched(string directory)
    {
        try
        {
            return !Directory.Exists(directory) || !Directory.EnumerateFileSystemEntries(directory).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { return false; }
    }

    private static int? TryExitCode(Process process)
    {
        try { return process.HasExited ? process.ExitCode : null; }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { return null; }
    }

    private static DateTime? TryStartTime(Process process)
    {
        try { return process.StartTime.ToUniversalTime(); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { return null; }
    }

    private sealed class RunControl(string gameId, InstallMode mode)
    {
        public string GameId { get; } = gameId;
        public volatile bool RestartRequested;
        public bool IgnoreCustomArguments { get; set; }
        public InstallMode Mode { get; set; } = mode;
        public IReadOnlySet<int> Processes { get; set; } = new HashSet<int>();
        public SetupActivity? Last { get; set; }
    }
}
