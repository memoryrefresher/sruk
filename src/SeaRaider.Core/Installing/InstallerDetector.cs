using System.Diagnostics;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Launching;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Installing;

/// <summary>What extraction produced.</summary>
/// <param name="IsInstaller">True when the folder holds a setup program rather than a playable game.</param>
/// <param name="InstallerPath">The primary setup executable / .msi (absolute), when <paramref name="IsInstaller"/>.</param>
/// <param name="Kind">Detected installer technology.</param>
/// <param name="GameExecutable">Best game executable when the folder is playable as-is.</param>
/// <param name="Engine">Full engine details (version, repack), when <paramref name="IsInstaller"/>.</param>
public sealed record InstallerInspection(bool IsInstaller, string? InstallerPath, InstallerKind Kind, string? GameExecutable,
    SetupEngineInfo? Engine = null);

/// <summary>
/// Inspects an extracted release and decides between "ready to play" and "run the setup first",
/// and knows each installer technology's silent switches.
///
/// <para>Decision: if a convincing game executable exists (score ≥ <see cref="PlayableScore"/> from
/// <see cref="IExecutableLocator"/>), the folder is playable even if a setup.exe sits next to it (old games
/// often ship a "Setup" configuration tool). Otherwise, if any setup program is present, it's an installer.</para>
/// </summary>
public sealed class InstallerDetector(IExecutableLocator locator)
{
    /// <summary>Minimum locator score for "this folder is already a playable game".</summary>
    public const int PlayableScore = 40;

    public InstallerInspection Inspect(string directory, string gameTitle)
    {
        var best = locator.RankCandidates(directory, gameTitle).FirstOrDefault();
        var installers = FindInstallers(directory);

        if (installers.Count == 0 || best is { Score: >= PlayableScore })
            return new InstallerInspection(false, null, InstallerKind.Unknown, best?.Path);

        var primary = installers[0];
        var engine = SetupEngineDetector.Detect(primary);
        return new InstallerInspection(true, primary, engine.Kind, null, engine);
    }

    /// <summary>
    /// Setup programs within two folder levels, best first: shallow before deep, .exe before .msi
    /// (an .exe bootstrapper usually wraps the .msi and installs prerequisites), "setup" before other names.
    /// </summary>
    public static List<string> FindInstallers(string directory)
    {
        if (!Directory.Exists(directory)) return [];

        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true };
        return Directory.EnumerateFiles(directory, "*", options)
            .Where(path =>
            {
                var ext = Path.GetExtension(path);
                var name = Path.GetFileNameWithoutExtension(path);
                var relative = Path.GetRelativePath(directory, path);
                if (ExecutableLocator.IsInExcludedFolder(relative) || ExecutableLocator.IsExcludedName(name)) return false;
                return ext.Equals(".msi", StringComparison.OrdinalIgnoreCase) ||
                       (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) && ExecutableLocator.IsInstallerName(name));
            })
            .OrderBy(path => Path.GetRelativePath(directory, path).Count(c => c is '\\' or '/'))
            .ThenBy(path => path.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(path => Path.GetFileNameWithoutExtension(path).StartsWith("setup", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenByDescending(path => new FileInfo(path).Length)
            .ToList();
    }

    /// <summary>Identifies the installer technology from the file's own bytes (see <see cref="SetupEngineDetector"/>).</summary>
    public static InstallerKind DetectKind(string installerPath) => SetupEngineDetector.Detect(installerPath).Kind;

    /// <summary>True if the kind has well-known unattended switches.</summary>
    public static bool SupportsSilent(InstallerKind kind) => SetupEngineDetector.SupportsSilent(kind);

    /// <summary>
    /// Builds the process start info for an installer run (<paramref name="silent"/> = fully hidden).
    /// The installer service uses <see cref="SilentInstallManager"/>, which also picks progress-window modes for repacks.
    /// </summary>
    public static ProcessStartInfo BuildStartInfo(string installerPath, InstallerKind kind, string targetDirectory, bool silent,
        string? customArguments)
    {
        var mode = silent && SupportsSilent(kind) ? InstallMode.Silent : InstallMode.Interactive;
        var (fileName, arguments) = SetupEngineDetector.BuildCommand(installerPath, kind, targetDirectory, mode, customArguments);
        return new ProcessStartInfo(fileName)
        {
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(installerPath)!,
            UseShellExecute = true, // lets installers that need admin rights raise a UAC prompt
        };
    }

    /// <summary>
    /// Interprets an installer exit code. Returns (success, cancelledByUser).
    /// </summary>
    public static (bool Success, bool Cancelled) ClassifyExitCode(InstallerKind kind, int exitCode) => (kind, exitCode) switch
    {
        (_, 0) => (true, false),
        (_, 3010 or 1641) => (true, false),                // success, reboot required / initiated
        (InstallerKind.Msi or InstallerKind.WixBurn or InstallerKind.InstallShield or InstallerKind.AdvancedInstaller, 1602)
            => (false, true),                              // user cancelled
        (InstallerKind.InnoSetup, 2 or 5) => (false, true), // cancelled before / during install
        (InstallerKind.Nsis, 1) => (false, true),           // NSIS: cancelled by user (or error)
        _ => (false, false),
    };
}
