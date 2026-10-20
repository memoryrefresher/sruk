using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using SeaRaider.Core;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Launching;
using SeaRaider.Core.Models;

namespace SeaRaider.App.Services;

/// <summary>
/// Desktop shortcuts that start games through SeaRaider. A game's setup puts a shortcut on the desktop that runs the game's
/// exe directly, so SeaRaider never knew it was playing. After an install (and at start-up, for games installed before),
/// such shortcuts are pointed at SeaRaider.exe with <c>--play &lt;game&gt;</c> plus the program and arguments they had, keeping
/// their icon and name. Opening one starts SeaRaider if needed (minimised), which starts the game: play time and what
/// friends see you playing work as if you'd pressed Play. Turning the setting off puts the shortcuts back as they were.
/// </summary>
public sealed class ShortcutService(ILibraryRepository repository, GameStatusService status, ILogger<ShortcutService> logger)
{
    private const string PlaySwitch = ShortcutCommandLine.PlaySwitch;

    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);

    public void Start()
    {
        if (!OperatingSystem.IsWindows()) return;

        // A game just finished installing: its setup has made its shortcuts by now (give it a moment to finish writing).
        status.InstallChanged += gameId =>
        {
            if (!_pending.Add(gameId)) return;
            DispatcherTimer.RunOnce(() =>
            {
                _pending.Remove(gameId);
                if (repository.GetInstall(gameId) is { State: GameInstallState.Installed } install) Route([install]);
            }, TimeSpan.FromSeconds(8));
        };

        // Games installed before (or shortcuts made since), and shortcuts left pointing at a SeaRaider.exe that moved.
        DispatcherTimer.RunOnce(RouteAll, TimeSpan.FromSeconds(30));
    }

    /// <summary>Points every installed game's desktop shortcuts at SeaRaider (when the setting is on).</summary>
    public void RouteAll() => Route(repository.GetInstalls().Where(i => i.State == GameInstallState.Installed).ToList());

    /// <summary>Puts every shortcut SeaRaider changed back to the game's own program.</summary>
    public void RestoreAll()
    {
        foreach (var path in DesktopShortcuts())
        {
            try
            {
                using var link = ShellLink.Load(path);
                if (!IsOurs(link) || ShortcutCommandLine.Parse(ShortcutCommandLine.Split(link.Arguments)) is not { Exe: { Length: > 0 } exe } launch) continue;
                link.Target = exe;
                link.Arguments = launch.Args ?? string.Empty;
                link.WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty;
                link.Save();
                logger.LogInformation("Shortcut {Shortcut} points at the game again", Path.GetFileName(path));
            }
            catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or InvalidCastException)
            {
                logger.LogDebug(ex, "Couldn't restore {Shortcut}", path);
            }
        }
    }

    private void Route(IReadOnlyList<InstalledGame> installs)
    {
        if (!repository.GetSettings().RouteGameShortcuts || installs.Count == 0) return;
        if (Environment.ProcessPath is not { } self || !self.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;

        var games = installs
            .Where(i => !string.IsNullOrWhiteSpace(i.InstallDirectory) && Directory.Exists(i.InstallDirectory))
            .OrderByDescending(i => i.InstallDirectory.Length)
            .ToList();

        foreach (var path in DesktopShortcuts())
        {
            try
            {
                using var link = ShellLink.Load(path);
                if (IsOurs(link))
                {
                    // Already ours: only follow SeaRaider if it moved.
                    if (!string.Equals(link.Target, self, StringComparison.OrdinalIgnoreCase))
                    {
                        link.Target = self;
                        link.WorkingDirectory = Path.GetDirectoryName(self)!;
                        link.Save();
                    }
                    continue;
                }

                var target = link.Target;
                if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                var name = Path.GetFileName(target);
                if (name.StartsWith("unins", StringComparison.OrdinalIgnoreCase) || name.Contains("setup", StringComparison.OrdinalIgnoreCase)) continue;
                var game = games.FirstOrDefault(g => PathSafety.IsUnder(target, g.InstallDirectory));
                if (game is null) continue;

                var arguments = ShortcutCommandLine.Build(game.GameId, target, link.Arguments);
                var icon = link.Icon;
                link.Icon = string.IsNullOrWhiteSpace(icon.Path) ? (target, 0) : icon; // keeps the game's own icon
                link.Target = self;
                link.Arguments = arguments;
                link.WorkingDirectory = Path.GetDirectoryName(self)!;
                link.Save();
                logger.LogInformation("Desktop shortcut {Shortcut} now starts {Title} through SeaRaider", Path.GetFileName(path), game.Title);
            }
            catch (UnauthorizedAccessException)
            {
                // The shared (Public) desktop needs administrator rights; SeaRaider still notices those launches while it runs.
                logger.LogDebug("No permission to change {Shortcut}", path);
            }
            catch (Exception ex) when (ex is COMException or IOException or InvalidCastException)
            {
                logger.LogDebug(ex, "Couldn't read or change {Shortcut}", path);
            }
        }
    }

    private static IEnumerable<string> DesktopShortcuts()
    {
        foreach (var folder in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory })
        {
            var directory = Environment.GetFolderPath(folder);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) continue;
            string[] files;
            try { files = Directory.GetFiles(directory, "*.lnk"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files) yield return file;
        }
    }

    private static bool IsOurs(ShellLink link) =>
        Path.GetFileName(link.Target).Equals("SeaRaider.exe", StringComparison.OrdinalIgnoreCase) &&
        link.Arguments.Contains(PlaySwitch, StringComparison.Ordinal);
}
