using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Catalog;
using SeaRaider.Core.Emulation;
using SeaRaider.Core.Models;
using SeaRaider.Core.Scraping;

namespace SeaRaider.Core.Launching;

/// <summary>
/// Adds a game that's already on this PC to the library (Library › Add a game / Add a ROM): a PC game by its program, or a
/// console game by its game file (ROM or disc image). Nothing is copied or moved: the files stay where they are, and removing
/// the game from the library never deletes them. Its cover is looked up the way the store's are (Steam for PC games,
/// libretro's box art for console games).
/// </summary>
public sealed class LibraryImportService(ILibraryRepository repository, ArtworkService artwork, RomInfoService romInfo,
    ILogger<LibraryImportService> logger)
{
    /// <summary>Folder names that are only part of a game's layout, never its name ("Game\Binaries\Win64\game.exe").</summary>
    private static readonly HashSet<string> LayoutFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "bin64", "bin32", "binaries", "win64", "win32", "x64", "x86", "game", "games", "release", "retail", "shipping",
        "system", "exe", "program", "app", "client", "launcher", "windows", "windowsnoeditor", "pc",
    };

    private static readonly string[] ProgramTypes = [".exe", ".bat", ".cmd", ".lnk"];

    /// <summary>Adds a PC game by its program. <paramref name="title"/>: the name to show (else worked out from its folder).</summary>
    /// <exception cref="InvalidOperationException">Not a program, or already in the library.</exception>
    public InstalledGame AddPcGame(string programPath, string? title = null)
    {
        var path = Path.GetFullPath(programPath.Trim().Trim('"'));
        if (!File.Exists(path)) throw new InvalidOperationException("That file wasn't found.");
        if (!ProgramTypes.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose the game's program (an .exe). For a console game, use Add a ROM.");

        var install = NewInstall(path, string.IsNullOrWhiteSpace(title) ? PcTitle(path) : title.Trim());
        install.InstallDirectory = Path.GetDirectoryName(path)!;
        install.ExecutablePath = path;
        install.SizeOnDiskBytes = FolderSize(install.InstallDirectory);
        repository.UpsertInstall(install);
        logger.LogInformation("Added {Title} from this PC ({Exe})", install.Title, path);
        return install;
    }

    /// <summary>
    /// The console a game file is for, when it can be told from the file alone: its type (".gba", ".nsp"), a PS3 disc dump's
    /// EBOOT.BIN, or the folders it's in ("…\PS2\game.iso"). Null: the user picks it.
    /// </summary>
    public static GameSystem? SystemOf(string romPath)
    {
        if (IsPs3Eboot(romPath)) return GameSystems.Find("ps3");
        if (GameSystems.FromExtension(Path.GetExtension(romPath)) is { } byType) return byType;
        var folders = Path.GetDirectoryName(romPath) ?? string.Empty;
        return GameSystems.FromText(folders) ?? TorrentSourceService.ConsoleIn(folders.Replace('\\', ' ').Replace('/', ' '));
    }

    /// <summary>Adds a console game by its game file, for <paramref name="systemId"/> (null: told from the file, see <see cref="SystemOf"/>).</summary>
    /// <exception cref="InvalidOperationException">Not found, the console couldn't be told, or already in the library.</exception>
    public InstalledGame AddRom(string romPath, string? systemId, string? title = null)
    {
        var path = Path.GetFullPath(romPath.Trim().Trim('"'));
        if (!File.Exists(path)) throw new InvalidOperationException("That file wasn't found.");
        var system = GameSystems.Find(systemId) ?? SystemOf(path)
                     ?? throw new InvalidOperationException("SeaRaider can't tell which console this game is for. Pick the console, then add it.");

        // A PS3 disc dump is named by its folder (…\God of War III [BCUS98111]\PS3_GAME\USRDIR\EBOOT.BIN).
        var raw = IsPs3Eboot(path)
            ? Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(path)))) ?? "PS3 game"
            : Path.GetFileNameWithoutExtension(path);
        var install = NewInstall(path, string.IsNullOrWhiteSpace(title) ? Clean(raw, fallback: raw) : title.Trim());
        install.SystemId = system.Id;
        install.RomPath = path;
        install.InstallDirectory = Path.GetDirectoryName(path)!;
        install.SizeOnDiskBytes = IsPs3Eboot(path)
            ? FolderSize(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(path)))!)
            : new FileInfo(path).Length;
        install.RawRomName = raw;
        repository.UpsertInstall(install);
        logger.LogInformation("Added {Title} ({System}) from this PC ({Rom})", install.Title, system.Name, path);
        return install;
    }

    /// <summary>
    /// Looks up a cover for a game added from this PC and saves it: libretro's box art for a console game, Steam's picture for a
    /// PC game. Returns true when one was found. Never throws for a failed lookup.
    /// </summary>
    public async Task<bool> FindCoverAsync(string gameId, CancellationToken ct = default)
    {
        if (repository.GetInstall(gameId) is not { } install) return false;
        string? cover = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            if (GameSystems.Find(install.SystemId) is { } system)
                cover = await romInfo.FindBoxArtAsync(install.Title, install.RawRomName ?? install.Title, system, timeout.Token).ConfigureAwait(false);
            else if (await artwork.GetAsync(install.Title, null, timeout.Token).ConfigureAwait(false) is { } art)
                cover = art.HeaderUrl ?? art.HeroUrl;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or InvalidOperationException)
        {
            logger.LogInformation(ex, "No cover found for {Title}", install.Title);
        }
        if (cover is null) return false;

        install = repository.GetInstall(gameId) ?? install; // may have changed meanwhile (played, renamed)
        install.CoverImageUrl = cover;
        repository.UpsertInstall(install);
        return true;
    }

    private InstalledGame NewInstall(string path, string title)
    {
        var id = "local-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())))[..24].ToLowerInvariant();
        if (repository.GetInstall(id) is { State: GameInstallState.Installed } existing)
            throw new InvalidOperationException($"{existing.Title} is already in your library.");
        return new InstalledGame
        {
            GameId = id,
            Title = title,
            State = GameInstallState.Installed,
            AddedFromPc = true,
            InstalledAtUtc = DateTime.UtcNow,
        };
    }

    /// <summary>The game's name from its folder ("D:\Games\Hollow Knight\hollow_knight.exe" → "Hollow Knight"), skipping layout folders.</summary>
    internal static string PcTitle(string exe)
    {
        var folder = new DirectoryInfo(Path.GetDirectoryName(exe)!);
        while (folder.Parent is not null && LayoutFolders.Contains(folder.Name)) folder = folder.Parent;
        var raw = folder.Parent is null ? Path.GetFileNameWithoutExtension(exe) : folder.Name; // a drive root has no name
        return Clean(raw, fallback: Path.GetFileNameWithoutExtension(exe));
    }

    private static string Clean(string raw, string fallback)
    {
        var name = TorrentSourceService.CleanName(raw.Replace('_', ' ').Replace('.', ' '));
        return name.Length > 0 ? name : fallback;
    }

    internal static bool IsPs3Eboot(string path) =>
        Path.GetFileName(path).Equals("EBOOT.BIN", StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(Path.GetDirectoryName(path)) is { } usrdir && usrdir.Equals("USRDIR", StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path))) is { } game && game.Equals("PS3_GAME", StringComparison.OrdinalIgnoreCase);

    /// <summary>The folder's size (stops counting after 200,000 files: a library folder picked by mistake isn't walked for ever).</summary>
    private static long FolderSize(string folder)
    {
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            return new DirectoryInfo(folder).EnumerateFiles("*", options).Take(200_000).Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
