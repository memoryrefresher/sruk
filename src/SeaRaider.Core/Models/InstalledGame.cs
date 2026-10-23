using LiteDB;

namespace SeaRaider.Core.Models;

/// <summary>Lifecycle state of a game on this machine.</summary>
public enum GameInstallState
{
    NotInstalled,
    Queued,
    Downloading,
    Extracting,

    /// <summary>Extraction produced a setup program; the user (or auto-run) still has to run it.</summary>
    ReadyToInstall,

    /// <summary>The setup program is running right now.</summary>
    RunningInstaller,

    /// <summary>Playable: <see cref="InstalledGame.ExecutablePath"/> points at the game.</summary>
    Installed,

    Uninstalling,
    Failed,
}

/// <summary>Where a game's setup program is in its lifecycle.</summary>
public enum InstallerState
{
    /// <summary>No installer involved — the download extracted straight to a playable folder.</summary>
    None,

    /// <summary>A setup program was found after extraction and is waiting to be run.</summary>
    Pending,

    /// <summary>The setup program is running.</summary>
    Running,

    /// <summary>Setup finished, the game exe was resolved and setup files were cleaned up.</summary>
    Succeeded,

    /// <summary>Setup exited with an error code; setup files are kept so it can be retried.</summary>
    Failed,

    /// <summary>The user closed or cancelled the setup wizard; setup files are kept.</summary>
    Cancelled,
}

/// <summary>Installer technology, detected from the setup executable. Decides the silent switches.</summary>
public enum InstallerKind
{
    Unknown,

    /// <summary>Inno Setup: /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR="…"</summary>
    InnoSetup,

    /// <summary>Nullsoft (NSIS): /S /D=…</summary>
    Nsis,

    /// <summary>Windows Installer package: msiexec /i … /qn /norestart INSTALLDIR="…"</summary>
    Msi,

    /// <summary>InstallShield: /s /v"/qn INSTALLDIR=…"</summary>
    InstallShield,

    /// <summary>WiX Burn bundle: /quiet /norestart</summary>
    WixBurn,

    /// <summary>Advanced Installer bootstrapper: /exenoui /qn APPDIR="…"</summary>
    AdvancedInstaller,

    /// <summary>Wise Installation System: /s</summary>
    Wise,

    /// <summary>Indigo Rose Setup Factory: /S</summary>
    SetupFactory,

    /// <summary>7-Zip self-extracting installer: no standard unattended mode, runs as-is.</summary>
    SevenZipSfx,
}

/// <summary>
/// A game that has (or had) files on disk. Keyed by <see cref="GameMetadata.Id"/>.
/// Title and cover are denormalised so the Library renders even if the store entry is gone.
/// </summary>
public sealed class InstalledGame
{
    [BsonId]
    public string GameId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? CoverImageUrl { get; set; }

    public GameInstallState State { get; set; } = GameInstallState.NotInstalled;

    /// <summary>Root folder of the game, e.g. C:\MyGames\Hollow Depths (also the installer's target).</summary>
    public string InstallDirectory { get; set; } = string.Empty;

    /// <summary>Primary executable (absolute path). Null until resolved.</summary>
    public string? ExecutablePath { get; set; }

    /// <summary>
    /// A launch file the user named before downloading (file name or path inside the game's folder, .exe/.bat/.cmd). Resolved
    /// to <see cref="ExecutablePath"/> once the game is installed; null = detect the executable automatically.
    /// </summary>
    public string? LaunchFileHint { get; set; }

    /// <summary>A console game: the system it is for (<see cref="Emulation.GameSystem.Id"/>). Null for PC and browser games.</summary>
    public string? SystemId { get; set; }

    /// <summary>A console game: the game file (ROM or disc image) an emulator is started with.</summary>
    public string? RomPath { get; set; }

    /// <summary>A browser game (no files): the game's address, played in SeaRaider's game window.</summary>
    public string? WebGameUrl { get; set; }

    /// <summary>The browser game's store page (sent as the referrer; some games only run when opened from it).</summary>
    public string? WebGamePageUrl { get; set; }

    /// <summary>Optional command-line arguments passed when the game is launched.</summary>
    public string? LaunchArguments { get; set; }

    public long SizeOnDiskBytes { get; set; }

    public DateTime? InstalledAtUtc { get; set; }

    public DateTime? LastPlayedAtUtc { get; set; }

    /// <summary>Accumulated play time in seconds (LiteDB has no native TimeSpan).</summary>
    public long TotalPlaySeconds { get; set; }

    public string? LastError { get; set; }

    // ---- installer lifecycle ---------------------------------------------------------------------

    public InstallerState InstallerState { get; set; } = InstallerState.None;

    public InstallerKind InstallerKind { get; set; } = InstallerKind.Unknown;

    /// <summary>Human-readable engine description, e.g. "Inno Setup 6.2.2, FitGirl repack".</summary>
    public string? InstallerEngine { get; set; }

    /// <summary>
    /// The setup is a game repack (Inno Setup with ISDone/unarc decompression and custom pages). These run with a
    /// visible progress window (/SILENT) because their custom message boxes ignore /SUPPRESSMSGBOXES.
    /// </summary>
    public bool InstallerIsRepack { get; set; }

    /// <summary>Setup executable or .msi found after extraction (absolute path).</summary>
    public string? InstallerPath { get; set; }

    /// <summary>Folder holding the extracted setup files; deleted after a successful install (or when the user says so).</summary>
    public string? SetupDirectory { get; set; }

    /// <summary>The user chose to keep the setup files after installing: don't offer to delete them again.</summary>
    public bool KeepSetupFiles { get; set; }

    /// <summary>Run the installer with silent switches (when the installer kind supports them).</summary>
    public bool SilentInstall { get; set; } = true;

    /// <summary>Optional custom installer arguments ("{dir}" = target folder); replaces the generated switches.</summary>
    public string? InstallerArguments { get; set; }

    /// <summary>Exit code of the last installer run (null if never run).</summary>
    public int? InstallerExitCode { get; set; }

    public DateTime? InstallerLastRunUtc { get; set; }

    /// <summary>Uninstaller registered by the setup program (e.g. unins000.exe), used for a clean uninstall.</summary>
    public string? UninstallCommand { get; set; }

    /// <summary>Silent variant of <see cref="UninstallCommand"/> when the installer registered one.</summary>
    public string? QuietUninstallCommand { get; set; }

    [BsonIgnore]
    public TimeSpan TotalPlaytime => TimeSpan.FromSeconds(TotalPlaySeconds);

    [BsonIgnore]
    public bool InstalledViaSetup => InstallerState == InstallerState.Succeeded;
}
