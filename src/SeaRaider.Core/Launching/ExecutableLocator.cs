using System.Text.RegularExpressions;
using SeaRaider.Core.Abstractions;

namespace SeaRaider.Core.Launching;

/// <summary>
/// Heuristic "which .exe is the game?" detector. Every .exe under the install folder is scored:
/// <list type="bullet">
///   <item>Excluded outright: uninstallers, redistributables (DirectX, VC++, .NET, PhysX), crash
///         handlers, anti-cheat installers, archive tools, helper processes, and anything inside
///         redist/installer folders.</item>
///   <item>Bonus: name similarity to the game title, shallow depth, Unreal "-Win64-Shipping" binaries,
///         larger files.</item>
///   <item>Penalty: launchers, config tools, dedicated servers, editors, 32-bit duplicates.</item>
/// </list>
/// Setup programs are never returned — <c>InstallerDetector</c> decides whether a folder is an installer.
/// </summary>
public sealed partial class ExecutableLocator : IExecutableLocator
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "_commonredist", "commonredist", "redist", "redistributable", "redistributables", "directx", "dxsetup",
        "vcredist", "vc_redist", "dotnet", "dotnetfx", "physx", "__installer", "_installer", "installers",
        "prereqs", "prerequisites", "support", "tools", "easyanticheat", "battleye", "crashreportclient", "thirdparty",
    };

    [GeneratedRegex(@"^(unins\d*|uninstall.*|vc_?redist.*|vcredist.*|dxsetup|dxwebsetup|directx.*|dotnet.*|ndp\d+.*|" +
                    @"physx.*|oalinst|crashreport.*|crashhandler.*|crashpad_handler|unitycrashhandler(32|64)?|" +
                    @"ue4?prereqsetup.*|ueprereqsetup.*|easyanticheat_setup|be_?service.*|7z.*|unrar|winrar|quicksfv|" +
                    @"cefprocess.*|cefsharp.*|.*helper|notification_helper|zfgamebrowser|dowser|.*updater|update|" +
                    @"vulkan.*|openal.*|xnafx.*|steamcmd|steam_api.*|autorun|.*_setup_x(86|64))$", RegexOptions.IgnoreCase)]
    private static partial Regex ExcludedNameRegex();

    [GeneratedRegex(@"^(setup\d*|install|installer|setup[_ .-].*|install[_ .-].*|.*[_ .-]setup|.*installer)$", RegexOptions.IgnoreCase)]
    private static partial Regex InstallerNameRegex();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonAlphanumericRegex();

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        { "the", "of", "a", "an", "and", "edition", "remastered", "definitive", "goty", "deluxe", "complete", "game" };

    /// <summary>True for setup-program names: setup.exe, setup_game_1.0.exe, Game-Setup.exe, GameInstaller.exe…</summary>
    public static bool IsInstallerName(string fileNameWithoutExtension) => InstallerNameRegex().IsMatch(fileNameWithoutExtension);

    /// <summary>Uninstallers, redistributables, crash handlers, helpers — never the game or its installer.</summary>
    public static bool IsExcludedName(string fileNameWithoutExtension) => ExcludedNameRegex().IsMatch(fileNameWithoutExtension);

    /// <summary>True when any folder of <paramref name="relativePath"/> is a redist / tools / prerequisites folder.</summary>
    public static bool IsInExcludedFolder(string relativePath) =>
        relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[..^1].Any(ExcludedDirectories.Contains);

    public string? FindPrimaryExecutable(string installDirectory, string gameTitle) =>
        RankCandidates(installDirectory, gameTitle).FirstOrDefault()?.Path;

    public IReadOnlyList<ExecutableCandidate> RankCandidates(string installDirectory, string gameTitle)
    {
        if (!Directory.Exists(installDirectory)) return [];

        var root = Path.GetFullPath(installDirectory);
        var titleTokens = Tokenize(gameTitle);
        var titleCompact = string.Concat(titleTokens);
        var titleInitials = string.Concat(titleTokens.Select(t => t[0]));

        var candidates = new List<ExecutableCandidate>();

        foreach (var exe in SafeEnumerateExecutables(root))
        {
            var relative = Path.GetRelativePath(root, exe);
            var dirs = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[..^1];
            if (dirs.Any(ExcludedDirectories.Contains)) continue;

            var name = Path.GetFileNameWithoutExtension(exe);
            if (ExcludedNameRegex().IsMatch(name)) continue;
            if (InstallerNameRegex().IsMatch(name)) continue; // setup programs are handled by InstallerDetector

            var reasons = new List<string>();
            var score = 0;

            // --- name similarity -------------------------------------------------------------
            var nameTokens = Tokenize(Regex.Replace(name, @"[-_](win(32|64)|x(86|64)|shipping|dx1[12]|vulkan|release)", " ",
                RegexOptions.IgnoreCase));
            var nameCompact = string.Concat(nameTokens);

            if (nameCompact.Length > 0 && nameCompact == titleCompact) { score += 60; reasons.Add("exact title"); }
            else if (nameCompact.Length >= 3 && (titleCompact.Contains(nameCompact) || nameCompact.Contains(titleCompact)))
            { score += 40; reasons.Add("title match"); }
            else if (nameCompact.Length >= 2 && nameCompact == titleInitials) { score += 30; reasons.Add("title initials"); }

            var overlap = nameTokens.Intersect(titleTokens).Count();
            if (overlap > 0) { score += overlap * 12; reasons.Add($"{overlap} shared word(s)"); }

            // --- location -------------------------------------------------------------------
            score -= dirs.Length * 6;
            if (dirs.Length == 0) { score += 20; reasons.Add("root folder"); }
            if (name.Contains("-Win64-Shipping", StringComparison.OrdinalIgnoreCase)) { score += 10; reasons.Add("Unreal shipping build"); }
            if (dirs.Any(d => d.Equals("bin64", StringComparison.OrdinalIgnoreCase) || d.Equals("x64", StringComparison.OrdinalIgnoreCase)))
                score += 5;
            if (dirs.Any(d => d.Equals("bin32", StringComparison.OrdinalIgnoreCase) || d.Equals("x86", StringComparison.OrdinalIgnoreCase)))
                score -= 5;

            // --- role penalties ------------------------------------------------------------
            var lower = name.ToLowerInvariant();
            if (lower.Contains("launcher")) { score -= 15; reasons.Add("launcher"); }
            if (lower.Contains("config") || lower.Contains("settings") || lower.Contains("setup")) { score -= 25; reasons.Add("config tool"); }
            if (lower.Contains("server") || lower.Contains("dedicated")) { score -= 30; reasons.Add("server"); }
            if (lower.Contains("editor") || lower.Contains("modkit") || lower.Contains("sdk")) { score -= 30; reasons.Add("editor"); }
            if (lower.EndsWith("32") || lower.EndsWith("_x86")) score -= 5;

            // --- size (real game binaries are rarely tiny) ---------------------------------
            var megabytes = new FileInfo(exe).Length / (1024.0 * 1024.0);
            score += (int)Math.Min(30, Math.Max(0, Math.Log2(megabytes + 1) * 4));
            if (megabytes < 0.3) score -= 10;

            candidates.Add(new ExecutableCandidate(exe, score, string.Join(", ", reasons)));
        }

        return candidates.OrderByDescending(c => c.Score).ThenBy(c => c.Path.Length).ToList();
    }

    private static List<string> Tokenize(string text) =>
        NonAlphanumericRegex().Split(text.ToLowerInvariant())
            .Where(t => t.Length > 0 && !StopWords.Contains(t))
            .ToList();

    private static IEnumerable<string> SafeEnumerateExecutables(string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
            MaxRecursionDepth = 8,
        };
        return Directory.EnumerateFiles(root, "*.exe", options);
    }
}
