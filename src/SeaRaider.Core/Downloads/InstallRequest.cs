using System.Text.RegularExpressions;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Downloads;

/// <summary>Everything the pre-download dialog lets the user decide. Null properties mean "use the default".</summary>
public sealed record InstallRequest(GameMetadata Game)
{
    /// <summary>Specific link to use; null lets the planner choose.</summary>
    public DownloadLink? PreferredLink { get; init; }

    /// <summary>Folder created under the library root (sanitised before use). Null = the game's title.</summary>
    public string? InstallFolderName { get; init; }

    /// <summary>
    /// New base name for the downloaded file(s). Only the base changes; the extension and any volume suffix
    /// (.part01.rar, .7z.001, .z01…) are kept so the extractor still recognises and pairs the files.
    /// Null = keep the published names. Ignored for torrents.
    /// </summary>
    public string? FileBaseName { get; init; }

    /// <summary>Start the setup program automatically if extraction finds one.</summary>
    public bool? RunInstallerWhenReady { get; init; }

    /// <summary>Use unattended switches when running a setup program.</summary>
    public bool? SilentInstall { get; init; }

    /// <summary>Custom installer arguments ("{dir}" = install folder), replacing the generated switches.</summary>
    public string? CustomInstallerArguments { get; init; }
}

/// <summary>What a download would look like with default settings; feeds the pre-download dialog.</summary>
public sealed record InstallPreview(
    DownloadTransport Transport,
    IReadOnlyList<DownloadFileSpec> Files,
    string SuggestedFolderName,
    string LibraryRoot,
    string SuggestedFileBaseName,
    bool CanRenameFiles,
    bool DefaultSilentInstall,
    bool DefaultRunInstallerWhenReady)
{
    /// <summary>Folder that already exists under the library with this name (the job would use "Name (2)").</summary>
    public bool FolderExists(string folderName) =>
        Directory.Exists(Path.Combine(LibraryRoot, PathSafety.ToFolderName(folderName)));
}

/// <summary>Rules for renaming downloads without breaking archive detection.</summary>
public static partial class FileNameRules
{
    // Base name (shortest possible) + one recognised compound suffix.
    [GeneratedRegex(@"^(?<base>.+?)(?<suffix>\.part\d{1,4}\.rar|\.r\d{2,3}|\.z\d{2,3}|\.(?:7z|zip|rar|tar|iso|bin)\.\d{3}|\.\d{3}|" +
                    @"\.tar\.(?:gz|bz2|xz)|\.[A-Za-z0-9]{1,5})$", RegexOptions.IgnoreCase)]
    private static partial Regex NameRegex();

    /// <summary>Splits "My.Game.part01.rar" into ("My.Game", ".part01.rar").</summary>
    public static (string BaseName, string Suffix) Split(string fileName)
    {
        var m = NameRegex().Match(fileName);
        return m.Success ? (m.Groups["base"].Value, m.Groups["suffix"].Value) : (fileName, string.Empty);
    }

    /// <summary>Applies a new base name, keeping the original suffix.</summary>
    public static string Rename(string originalFileName, string newBaseName)
    {
        var (_, suffix) = Split(originalFileName);
        return Sanitize(newBaseName) + suffix;
    }

    /// <summary>Returns an error for an unusable base name, or null if it's fine.</summary>
    public static string? Validate(string? baseName)
    {
        if (string.IsNullOrWhiteSpace(baseName)) return "Enter a file name.";
        if (baseName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || baseName.IndexOfAny([':', '*', '?', '"', '<', '>', '|', '/', '\\']) >= 0)
            return "File names can't contain \\ / : * ? \" < > |";
        if (baseName.TrimEnd().EndsWith('.')) return "File names can't end with a dot.";
        if (baseName.Length > 150) return "That file name is too long.";
        return null;
    }

    private static string Sanitize(string name) =>
        string.Concat(name.Trim().Where(c => !Path.GetInvalidFileNameChars().Contains(c))).TrimEnd('.', ' ');
}
