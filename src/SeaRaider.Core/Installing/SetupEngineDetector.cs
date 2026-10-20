using System.Text;
using System.Text.RegularExpressions;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Installing;

/// <summary>What a setup executable is built with.</summary>
/// <param name="Kind">Setup engine; decides the unattended switches.</param>
/// <param name="EngineVersion">Engine version when the file states it (Inno Setup does).</param>
/// <param name="IsRepack">A game repack (custom Inno Setup with ISDone/unarc, music, extra pages).</param>
/// <param name="RepackGroup">Repack group named in the file or folder, when recognisable.</param>
/// <param name="Evidence">The signature that decided it (for logs and the UI).</param>
public sealed record SetupEngineInfo(InstallerKind Kind, string? EngineVersion, bool IsRepack, string? RepackGroup, string Evidence)
{
    public static SetupEngineInfo Unknown { get; } = new(InstallerKind.Unknown, null, false, null, "no known signature");

    /// <summary>"Inno Setup 6.2.2, FitGirl repack" / "NSIS installer" / "Unknown setup program".</summary>
    public string Describe()
    {
        var name = Kind switch
        {
            InstallerKind.InnoSetup => "Inno Setup",
            InstallerKind.Nsis => "NSIS",
            InstallerKind.Msi => "Windows Installer",
            InstallerKind.InstallShield => "InstallShield",
            InstallerKind.WixBurn => "WiX bundle",
            InstallerKind.AdvancedInstaller => "Advanced Installer",
            InstallerKind.Wise => "Wise",
            InstallerKind.SetupFactory => "Setup Factory",
            InstallerKind.SevenZipSfx => "7-Zip self-extractor",
            _ => "Unknown setup program",
        };
        if (EngineVersion is not null) name += " " + EngineVersion;
        if (IsRepack) name += RepackGroup is null ? ", repack" : $", {RepackGroup} repack";
        return name;
    }
}

/// <summary>How visible an install run is.</summary>
public enum InstallMode
{
    /// <summary>The installer's normal wizard.</summary>
    Interactive,

    /// <summary>No questions, but the installer's progress window is visible (/SILENT, /passive, /qb).</summary>
    SilentWithProgress,

    /// <summary>No windows at all (/VERYSILENT, /S, /quiet, /qn).</summary>
    Silent,
}

/// <summary>
/// Identifies the setup engine from the executable's own bytes: PE section names, loader window classes,
/// embedded headers and version-info strings (which are UTF-16LE). Reads the first 6 MB and the last 2 MB,
/// which is where every engine below keeps its markers, so multi-gigabyte installers are cheap to inspect.
/// Files next to the setup (ISDone.dll, unarc.dll, fg-*.bin …) identify repacks.
/// </summary>
public static partial class SetupEngineDetector
{
    private const int HeadBytes = 6 * 1024 * 1024;
    private const int TailBytes = 2 * 1024 * 1024;

    private static readonly byte[] OleSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    /// <summary>Signatures in priority order: wrappers (Burn, Advanced Installer) before the engines they may embed.</summary>
    private static readonly (InstallerKind Kind, string[] Markers)[] Signatures =
    [
        (InstallerKind.WixBurn, [".wixburn", "WixBundleManifest", "WixBundleOriginalSource"]),
        (InstallerKind.AdvancedInstaller, ["Advanced Installer", "Caphyon"]),
        (InstallerKind.InnoSetup, ["Inno Setup Setup Data", "InnoSetupLdrWindow", "Inno Setup Messages", "JR.Inno.Setup", "rDlPtS"]),
        (InstallerKind.Nsis, ["NullsoftInst", "Nullsoft.NSIS.exehead", "Nullsoft Install System"]),
        (InstallerKind.InstallShield, ["InstallShield"]),
        (InstallerKind.Wise, ["WiseMain", "Wise Installation", "WISE_SETUP"]),
        (InstallerKind.SetupFactory, ["Setup Factory", "irsetup.exe"]),
        (InstallerKind.SevenZipSfx, [";!@Install@!UTF-8!", "7zS.sfx", "7-Zip SFX"]),
    ];

    /// <summary>Repack group names as they appear in setup strings and folder/file names.</summary>
    private static readonly (string Marker, string Group)[] RepackGroups =
    [
        ("FitGirl", "FitGirl"), ("DODI", "DODI"), ("ElAmigos", "ElAmigos"), ("KaOs", "KaOs"), ("Xatab", "Xatab"),
        ("R.G. Mechanics", "R.G. Mechanics"), ("Chovka", "Chovka"), ("Masquerade", "Masquerade"), ("DIAKOV", "Diakov"),
        ("Decepticon", "Decepticon"), ("SteamRIP", "SteamRIP"),
    ];

    /// <summary>Files that only repacks ship next to their setup.</summary>
    private static readonly string[] RepackSiblingMarkers = ["isdone.dll", "unarc.dll", "fg-", "verify bin files", ".doi", "english.ini"];

    [GeneratedRegex(@"Inno Setup Setup Data \((?<v>\d+\.\d+\.\d+)")]
    private static partial Regex InnoVersionRegex();

    public static SetupEngineInfo Detect(string installerPath)
    {
        var extension = Path.GetExtension(installerPath);
        var siblings = SiblingNames(installerPath);

        byte[] head, tail;
        try
        {
            using var file = new FileStream(installerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            head = new byte[(int)Math.Min(file.Length, HeadBytes)];
            file.ReadExactly(head);

            var tailLength = (int)Math.Min(Math.Max(0, file.Length - head.Length), TailBytes);
            tail = new byte[tailLength];
            if (tailLength > 0)
            {
                file.Seek(-tailLength, SeekOrigin.End);
                file.ReadExactly(tail);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return extension.Equals(".msi", StringComparison.OrdinalIgnoreCase)
                ? new SetupEngineInfo(InstallerKind.Msi, null, false, null, ".msi file")
                : SetupEngineInfo.Unknown;
        }

        return Detect(head, tail, extension, siblings, Path.GetFileName(Path.GetDirectoryName(installerPath)));
    }

    /// <summary>Byte-level detection (testable without files).</summary>
    /// <param name="head">Start of the file.</param>
    /// <param name="tail">End of the file (may be empty).</param>
    /// <param name="extension">File extension including the dot.</param>
    /// <param name="siblingNames">Lower-case names of files in the same folder.</param>
    /// <param name="folderName">Name of the containing folder (often carries the repack group).</param>
    public static SetupEngineInfo Detect(ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, string extension,
        IReadOnlyCollection<string> siblingNames, string? folderName = null)
    {
        if (extension.Equals(".msi", StringComparison.OrdinalIgnoreCase) || head.StartsWith((ReadOnlySpan<byte>)OleSignature))
            return new SetupEngineInfo(InstallerKind.Msi, null, false, null, "Windows Installer package");

        foreach (var (kind, markers) in Signatures)
        {
            string? marker = null;
            foreach (var candidate in markers) // a loop, not LINQ: spans can't be captured by lambdas
            {
                if (!Contains(head, candidate) && !Contains(tail, candidate)) continue;
                marker = candidate;
                break;
            }
            if (marker is null) continue;

            string? version = null;
            if (kind == InstallerKind.InnoSetup)
            {
                var m = InnoVersionRegex().Match(Encoding.ASCII.GetString(head));
                if (!m.Success && !tail.IsEmpty) m = InnoVersionRegex().Match(Encoding.ASCII.GetString(tail));
                if (m.Success) version = m.Groups["v"].Value;
            }

            var (isRepack, group) = kind == InstallerKind.InnoSetup ? DetectRepack(head, tail, siblingNames, folderName) : (false, null);
            return new SetupEngineInfo(kind, version, isRepack, group, $"\"{marker}\" signature");
        }

        // No engine marker, but unmistakable repack files next to it: these are Inno-based in practice.
        var (siblingRepack, siblingGroup) = DetectRepack([], [], siblingNames, folderName);
        return siblingRepack
            ? new SetupEngineInfo(InstallerKind.InnoSetup, null, true, siblingGroup, "repack files next to the setup")
            : SetupEngineInfo.Unknown;
    }

    private static (bool IsRepack, string? Group) DetectRepack(ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail,
        IReadOnlyCollection<string> siblingNames, string? folderName)
    {
        string? group = null;
        foreach (var (marker, name) in RepackGroups)
        {
            if (Contains(head, marker) || Contains(tail, marker) ||
                (folderName?.Contains(marker, StringComparison.OrdinalIgnoreCase) ?? false) ||
                siblingNames.Any(n => n.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                group = name;
                break;
            }
        }

        var hasRepackFiles = siblingNames.Any(n => RepackSiblingMarkers.Any(m => n.Contains(m, StringComparison.OrdinalIgnoreCase)));
        if (group is null && siblingNames.Any(n => n.StartsWith("fg-", StringComparison.OrdinalIgnoreCase))) group = "FitGirl";
        return (hasRepackFiles || group is not null, group);
    }

    private static List<string> SiblingNames(string installerPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(installerPath);
            if (dir is null || !Directory.Exists(dir)) return [];
            return Directory.EnumerateFileSystemEntries(dir).Take(500).Select(p => Path.GetFileName(p).ToLowerInvariant()).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool Contains(ReadOnlySpan<byte> haystack, string text) =>
        !haystack.IsEmpty &&
        (haystack.IndexOf(Encoding.ASCII.GetBytes(text)) >= 0 ||
         haystack.IndexOf(Encoding.Unicode.GetBytes(text)) >= 0); // version-info strings are UTF-16LE

    // =============================================================================================
    // Switches
    // =============================================================================================

    /// <summary>Does the engine have documented unattended switches?</summary>
    public static bool SupportsSilent(InstallerKind kind) => kind is not (InstallerKind.Unknown or InstallerKind.SevenZipSfx);

    /// <summary>
    /// The mode to use when the user asked for a silent install. Repacks always get a visible progress window:
    /// their custom [Code] pages call plain MsgBox (not SuppressibleMsgBox), so under /VERYSILENT a question can
    /// sit on a hidden window forever — the "music plays but nothing happens" freeze.
    /// </summary>
    public static InstallMode ChooseMode(SetupEngineInfo engine, bool silentRequested, bool showProgress)
    {
        if (!silentRequested || !SupportsSilent(engine.Kind)) return InstallMode.Interactive;
        if (engine.IsRepack || showProgress) return InstallMode.SilentWithProgress;
        return InstallMode.Silent;
    }

    /// <summary>Arguments for an engine and mode. "{dir}" in <paramref name="customArguments"/> is the target folder.</summary>
    /// <returns>File to start (msiexec for .msi) and its arguments.</returns>
    public static (string FileName, string Arguments) BuildCommand(string installerPath, InstallerKind kind, string targetDirectory,
        InstallMode mode, string? customArguments)
    {
        var dir = targetDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!string.IsNullOrWhiteSpace(customArguments))
        {
            var custom = customArguments.Replace("{dir}", dir, StringComparison.OrdinalIgnoreCase);
            return kind == InstallerKind.Msi ? ("msiexec.exe", $"/i \"{installerPath}\" {custom}") : (installerPath, custom);
        }

        var silent = mode == InstallMode.Silent;
        var progress = mode == InstallMode.SilentWithProgress;

        switch (kind)
        {
            case InstallerKind.InnoSetup:
                // /SP- skips the "This will install…" prompt, /LANG skips the language picker (ignored if unknown).
                return (installerPath, mode switch
                {
                    InstallMode.Silent => $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /LANG=english /DIR=\"{dir}\"",
                    InstallMode.SilentWithProgress => $"/SILENT /SUPPRESSMSGBOXES /NORESTART /SP- /LANG=english /DIR=\"{dir}\"",
                    _ => $"/NORESTART /DIR=\"{dir}\"",
                });

            case InstallerKind.Nsis:
                // NSIS has no "progress only" mode. /D must be last and must NOT be quoted, even with spaces.
                return (installerPath, silent || progress ? $"/S /D={dir}" : $"/D={dir}");

            case InstallerKind.Msi:
                var ui = silent ? "/qn" : progress ? "/qb!" : string.Empty;
                return ("msiexec.exe", $"/i \"{installerPath}\" {ui} /norestart INSTALLDIR=\"{dir}\" TARGETDIR=\"{dir}\"".Replace("  ", " "));

            case InstallerKind.InstallShield:
                return (installerPath, silent ? $"/s /v\"/qn INSTALLDIR=\\\"{dir}\\\"\""
                    : progress ? $"/s /v\"/qb INSTALLDIR=\\\"{dir}\\\"\"" : string.Empty);

            case InstallerKind.WixBurn:
                return (installerPath, silent ? "/quiet /norestart" : progress ? "/passive /norestart" : string.Empty);

            case InstallerKind.AdvancedInstaller:
                return (installerPath, silent ? $"/exenoui /qn /norestart APPDIR=\"{dir}\""
                    : progress ? $"/exebasicui /qb! /norestart APPDIR=\"{dir}\"" : string.Empty);

            case InstallerKind.Wise:
                return (installerPath, silent || progress ? "/s" : string.Empty);

            case InstallerKind.SetupFactory:
                return (installerPath, silent || progress ? "/S" : string.Empty);

            default:
                return (installerPath, string.Empty); // no known switches: run the wizard as-is
        }
    }
}
