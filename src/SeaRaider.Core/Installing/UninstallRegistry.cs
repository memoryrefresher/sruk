using System.Runtime.Versioning;
using Microsoft.Win32;

namespace SeaRaider.Core.Installing;

/// <summary>One entry under ...\CurrentVersion\Uninstall (what "Apps &amp; features" lists).</summary>
public sealed record UninstallEntry(
    string Key,
    string? DisplayName,
    string? DisplayVersion,
    string? InstallLocation,
    string? DisplayIcon,
    string? UninstallString,
    string? QuietUninstallString)
{
    /// <summary>The executable a DisplayIcon value points at ("C:\Game\game.exe,0" → C:\Game\game.exe), if it exists.</summary>
    public string? DisplayIconExecutable
    {
        get
        {
            if (string.IsNullOrWhiteSpace(DisplayIcon)) return null;
            var path = DisplayIcon.Trim().Trim('"');
            var comma = path.LastIndexOf(',');
            if (comma > 2 && int.TryParse(path[(comma + 1)..], out _)) path = path[..comma].Trim('"');
            return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? path : null;
        }
    }
}

/// <summary>
/// Snapshots the Windows "installed programs" registry before and after a setup runs. The new entry tells us
/// where an installer actually put the game (even if it ignored our target folder) and how to uninstall it.
/// Returns empty results on non-Windows platforms.
/// </summary>
public static class UninstallRegistry
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static IReadOnlyDictionary<string, UninstallEntry> Snapshot()
    {
        var result = new Dictionary<string, UninstallEntry>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows()) return result;

        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                Read(hive, view, result);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                // Some keys aren't readable without admin rights; that's fine.
            }
        }
        return result;
    }

    /// <summary>Entries that appeared (or changed location/version) between two snapshots, best match for the title first.</summary>
    public static List<UninstallEntry> Diff(IReadOnlyDictionary<string, UninstallEntry> before,
        IReadOnlyDictionary<string, UninstallEntry> after, string gameTitle)
    {
        var changed = after.Values
            .Where(entry => !before.TryGetValue(entry.Key, out var old) ||
                            old.InstallLocation != entry.InstallLocation || old.DisplayVersion != entry.DisplayVersion)
            .ToList();

        var titleWords = Words(gameTitle);
        return changed
            .OrderByDescending(e => Words(e.DisplayName ?? string.Empty).Intersect(titleWords).Count())
            .ToList();
    }

    [SupportedOSPlatform("windows")]
    private static void Read(RegistryHive hive, RegistryView view, Dictionary<string, UninstallEntry> into)
    {
        using var root = RegistryKey.OpenBaseKey(hive, view);
        using var uninstall = root.OpenSubKey(UninstallPath);
        if (uninstall is null) return;

        foreach (var name in uninstall.GetSubKeyNames())
        {
            using var key = uninstall.OpenSubKey(name);
            if (key is null) continue;

            var id = $"{hive}\\{name}";
            if (into.ContainsKey(id)) continue; // 32/64-bit views can alias the same key

            into[id] = new UninstallEntry(
                id,
                key.GetValue("DisplayName") as string,
                key.GetValue("DisplayVersion") as string,
                key.GetValue("InstallLocation") as string,
                key.GetValue("DisplayIcon") as string,
                key.GetValue("UninstallString") as string,
                key.GetValue("QuietUninstallString") as string);
        }
    }

    private static HashSet<string> Words(string text) =>
        text.ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray()))
            .Where(w => w.Length > 1)
            .ToHashSet();
}

/// <summary>Splits registry-style command lines ("C:\x\unins000.exe" /SILENT, MsiExec.exe /X{GUID}, unquoted paths with spaces).</summary>
public static class CommandLine
{
    public static (string FileName, string Arguments)? Split(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        var text = commandLine.Trim();

        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            if (end < 0) return (text.Trim('"'), string.Empty);
            return (text[1..end], text[(end + 1)..].Trim());
        }

        var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe >= 0) return (text[..(exe + 4)], text[(exe + 4)..].Trim());

        var space = text.IndexOf(' ');
        return space < 0 ? (text, string.Empty) : (text[..space], text[(space + 1)..].Trim());
    }
}
