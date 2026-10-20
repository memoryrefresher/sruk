using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using SeaRaider.Core.Launching;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Installing;

/// <summary>What the setup helper should run and how to treat what the setup starts.</summary>
/// <param name="FileName">The setup program.</param>
/// <param name="Arguments">Its switches.</param>
/// <param name="WorkingDirectory">Folder to start it in.</param>
/// <param name="TargetDirectory">Where the game is being installed (its own programs are never taken for a browser).</param>
/// <param name="Runtimes">DirectX / Visual C++ / .NET installers: install quietly afterwards, skip, or leave alone.</param>
/// <param name="BlockWebPages">Close web browsers the setup opens (the "visit our site" page at the end).</param>
/// <param name="MuteAudio">Mute the setup's music.</param>
/// <param name="LogFile">Where to write what happened (read by SeaRaider).</param>
/// <param name="UncheckLowRam">Untick the setup's "limit RAM usage" option (see <see cref="SetupOptionTweaker"/>).</param>
public sealed record SetupRunnerRequest(
    string FileName, string Arguments, string WorkingDirectory, string TargetDirectory,
    SetupRuntimes Runtimes, bool BlockWebPages, bool MuteAudio, string LogFile, bool UncheckLowRam = true)
{
    /// <summary>The command-line switch that starts SeaRaider as the setup helper.</summary>
    public const string Switch = "--setup-runner";

    /// <summary>
    /// The helper's command-line argument. The request travels on the command line, not in a file, so nothing can change
    /// it between SeaRaider writing it and the administrator prompt.
    /// </summary>
    public string Encode() => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this));

    public static SetupRunnerRequest? Decode(string argument)
    {
        try { return JsonSerializer.Deserialize<SetupRunnerRequest>(Convert.FromBase64String(argument)); }
        catch (Exception ex) when (ex is FormatException or JsonException) { return null; }
    }
}

/// <summary>
/// The setup helper: SeaRaider started again as <c>SeaRaider.exe --setup-runner &lt;request&gt;</c>, with
/// administrator rights (one Windows prompt, which the repack would ask for anyway). It starts the setup itself, so the
/// setup and everything it starts run under it — and, being elevated too, it can act on them, which SeaRaider's normal
/// window can't (Windows doesn't let a normal program close or mute an administrator's program reliably):
/// <list type="bullet">
///   <item><b>Extra installers</b> (DirectX, Visual C++, .NET, PhysX, anything from a "_Redist" folder) are closed the
///         moment they start, so their wizards never pop up. With "install quietly" (default) the ones with a known quiet
///         mode run again silently after the setup has finished; the rest are skipped. "Skip" installs none of them, and
///         "show their installers" leaves them alone.</item>
///   <item><b>Web pages</b> a setup opens when it finishes (a browser started by the setup) are closed.</item>
///   <item><b>Music</b>: the setup's audio is muted as soon as it starts playing, and switched off with the setup's own
///         speaker button in its bottom-left corner (see <see cref="SetupMusicToggle"/>).</item>
///   <item><b>Low-RAM mode</b>: the "Limit installer to 2 GB of RAM usage" option on the setup's first page is unticked
///         (see <see cref="SetupOptionTweaker"/>).</item>
/// </list>
/// Its exit code is the setup's, so SeaRaider's install logic is unchanged.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SetupRunner
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan RuntimeTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Runs the encoded request (see <see cref="SetupRunnerRequest.Encode"/>); returns the setup's exit code.</summary>
    public static int Run(string encodedRequest)
    {
        var request = SetupRunnerRequest.Decode(encodedRequest);
        if (request is null) return 87; // ERROR_INVALID_PARAMETER

        using var log = OpenLog(request.LogFile);
        void Log(string message)
        {
            try { log?.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {message}"); log?.Flush(); }
            catch (IOException) { }
        }

        Process setup;
        try
        {
            setup = Process.Start(new ProcessStartInfo(request.FileName)
            {
                Arguments = request.Arguments,
                WorkingDirectory = request.WorkingDirectory,
                UseShellExecute = false, // runs with this helper's rights: no second administrator prompt
            }) ?? throw new InvalidOperationException("The setup didn't start.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log($"Setup didn't start: {ex.Message}");
            return 2;
        }
        Log($"Started {request.FileName} {request.Arguments} (pid {setup.Id})");

        var tree = new HashSet<int> { setup.Id };
        var seen = new HashSet<int>();
        var muted = new HashSet<string>();
        var runtimes = new List<(string Path, RedistInstaller Installer)>();
        var stash = Path.Combine(Path.GetTempPath(), "SeaRaider-runtimes", Guid.NewGuid().ToString("N")[..8]);
        var lastMute = Stopwatch.StartNew();
        var lastOptions = Stopwatch.StartNew();
        var lastMusic = Stopwatch.StartNew();
        var handledOptions = new HashSet<IntPtr>();
        var isSetupProcess = SilentInstallManager.SetupProcessMatcher(request.FileName, request.WorkingDirectory);
        var musicButton = request.MuteAudio ? new SetupMusicToggle(isSetupProcess, Log) : null;

        while (true)
        {
            var all = ProcessTreeTracker.Snapshot();
            var alive = all.Select(p => p.Id).ToHashSet();

            bool added;
            do
            {
                added = false;
                foreach (var (id, parent, _) in all)
                    if (!tree.Contains(id) && tree.Contains(parent)) { tree.Add(id); added = true; }
            } while (added);
            tree.RemoveWhere(id => !alive.Contains(id));
            if (tree.Count == 0) break;

            foreach (var (id, _, name) in all)
            {
                if (!tree.Contains(id) || id == setup.Id || !seen.Add(id)) continue;
                var path = ProcessScanner.ImagePathOf(id) ?? name;

                // A browser the setup started (not a program in the game's own folder that happens to share a name).
                if (request.BlockWebPages && RedistRules.IsBrowser(name) && !IsUnder(path, request.TargetDirectory))
                {
                    Kill(id);
                    Log($"Closed the web page the setup opened ({name})");
                    continue;
                }

                if (request.Runtimes != SetupRuntimes.ShowInstallers && RedistRules.Classify(path) is { } runtime)
                {
                    // Keep a copy first: setups often run these from a temporary folder they delete when they finish.
                    var copy = request.Runtimes == SetupRuntimes.InstallQuietly && runtime.SilentArguments is not null
                        ? Stash(path, runtime, stash)
                        : null;
                    Kill(id);
                    Log(copy is not null
                        ? $"{runtime.Name}: closed its window, will install it quietly ({Path.GetFileName(path)})"
                        : $"{runtime.Name}: skipped ({Path.GetFileName(path)})");
                    if (copy is not null) runtimes.Add((copy, runtime));
                }
            }

            if (request.UncheckLowRam && lastOptions.Elapsed >= TimeSpan.FromMilliseconds(150))
            {
                lastOptions.Restart();
                try { SetupOptionTweaker.UncheckLowRam(tree, handledOptions, Log); }
                catch (Exception ex) { Log($"Low-RAM option: {ex.Message}"); } // a courtesy; never stops the setup
            }

            // The setup's own speaker button too: the mixer mute alone doesn't hold for every setup.
            if (musicButton is not null && lastMusic.Elapsed >= TimeSpan.FromMilliseconds(80))
            {
                lastMusic.Restart();
                try { musicButton.Tick(tree); }
                catch (Exception ex) { Log($"Music button: {ex.Message}"); }
            }

            if (request.MuteAudio && lastMute.Elapsed >= TimeSpan.FromMilliseconds(120))
            {
                lastMute.Restart();
                if (Native.AudioSessionMuter.MuteNew(tree, muted, isSetupProcess) > 0) Log("Muted the setup's music");
            }

            Thread.Sleep(Poll);
        }

        setup.WaitForExit();
        var exitCode = setup.ExitCode;
        Log($"Setup finished with exit code {exitCode}");

        // The runtimes, one after another (two Windows Installer setups at once would refuse), without windows.
        foreach (var (path, runtime) in runtimes.DistinctBy(r => Path.GetFileName(r.Path).ToLowerInvariant()))
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(path, runtime.SilentArguments!)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(path)!,
                });
                if (process is null) continue;
                var finished = process.WaitForExit(RuntimeTimeout);
                Log($"{runtime.Name}: {(finished ? $"installed quietly (exit code {process.ExitCode})" : "still running after 10 minutes, left to finish")}");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Log($"{runtime.Name}: couldn't run quietly ({ex.Message})");
            }
        }

        try { if (Directory.Exists(stash)) Directory.Delete(stash, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return exitCode;
    }

    /// <summary>Copies the installer (with its folder for DirectX) somewhere the setup won't delete.</summary>
    private static string? Stash(string path, RedistInstaller runtime, string stash)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var target = Path.Combine(stash, runtime.Name.Replace(' ', '_') + "_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(target);
            if (runtime.NeedsFolder)
            {
                var source = Path.GetDirectoryName(path)!;
                foreach (var file in Directory.EnumerateFiles(source))
                    File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            }
            else
            {
                File.Copy(path, Path.Combine(target, Path.GetFileName(path)), overwrite: true);
            }
            return Path.Combine(target, Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Kill(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // already gone
        }
    }

    private static bool IsUnder(string path, string directory) =>
        directory.Length > 0 && Path.IsPathFullyQualified(path) &&
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(directory).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static StreamWriter? OpenLog(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return new StreamWriter(path, append: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
