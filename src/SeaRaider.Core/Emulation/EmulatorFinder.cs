namespace SeaRaider.Core.Emulation;

/// <summary>
/// Looks for emulators already on this PC (the "Find emulators" button in Settings). Folders meant for emulators are searched
/// fully; in big shared folders (Program Files) only the sub-folders named after an emulator are, so it stays quick.
/// </summary>
public static class EmulatorFinder
{
    /// <summary>Words in a program name that make it a helper of the emulator, not the emulator.</summary>
    private static readonly string[] NotTheEmulator = ["unins", "update", "crash", "tool", "setup", "helper", "redist", "install"];

    /// <summary>A folder's name that says it holds an emulator (so a big shared folder is only searched there).</summary>
    private static readonly string[] FolderWords =
        ["dolphin", "pcsx2", "duckstation", "retroarch", "ryujinx", "yuzu", "suyu", "sudachi", "citra", "lime3ds", "azahar", "cemu", "rpcs3",
         "ppsspp", "mgba", "melonds", "desmume", "snes9x", "bsnes", "project64", "flycast", "redream", "mesen", "fceux", "stella", "mednafen",
         "nestopia", "visualboyadvance", "vbam", "mame", "emulator"];

    /// <summary>Where emulators usually are: ready-made emulator folders, the library's own, and the program folders.</summary>
    public static IReadOnlyList<(string Folder, bool Dedicated)> DefaultFolders(string libraryRoot)
    {
        var folders = new List<(string, bool)>();
        void Add(string? path, bool dedicated)
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) folders.Add((path, dedicated));
        }

        Add(Path.Combine(libraryRoot, "Emulators"), true);
        Add(Path.Combine(Path.GetPathRoot(libraryRoot) ?? string.Empty, "RetroArch-Win64"), true);
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RetroArch"), true);
        Add(Path.Combine(Path.GetPathRoot(libraryRoot) ?? string.Empty, "Emulators"), true);
        Add(Path.Combine(Path.GetPathRoot(libraryRoot) ?? string.Empty, "Emulation"), true);
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Emulators"), true);
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Emulators"), true);
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"), false);
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), false);
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), false);
        Add(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), false);
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), false);
        return folders;
    }

    /// <summary>
    /// The emulator found for each system (system id → program path): RetroArch for every system it has a core for (when
    /// <paramref name="preferRetroArch"/>, the default), the system's own emulator for the rest.
    /// </summary>
    public static Dictionary<string, string> Scan(IEnumerable<(string Folder, bool Dedicated)> folders, bool preferRetroArch = true) =>
        Match(FindPrograms(folders), preferRetroArch);

    /// <summary>RetroArch in these folders, or null.</summary>
    public static string? FindRetroArch(IEnumerable<(string Folder, bool Dedicated)> folders) =>
        Usable(FindPrograms(folders)).FirstOrDefault(RetroArch.IsRetroArch);

    private static List<string> FindPrograms(IEnumerable<(string Folder, bool Dedicated)> folders)
    {
        var programs = new List<string>();
        foreach (var (folder, dedicated) in folders)
        {
            try
            {
                if (dedicated)
                {
                    programs.AddRange(Programs(folder, depth: 4));
                }
                else
                {
                    foreach (var sub in Directory.EnumerateDirectories(folder))
                        if (FolderWords.Any(w => Path.GetFileName(sub).Contains(w, StringComparison.OrdinalIgnoreCase)))
                            programs.AddRange(Programs(sub, depth: 2));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a folder that can't be read is skipped
            }
        }
        return programs;
    }

    private static List<string> Usable(IEnumerable<string> programs) => programs
        .Where(p => !NotTheEmulator.Any(w => Path.GetFileNameWithoutExtension(p).Contains(w, StringComparison.OrdinalIgnoreCase)))
        .OrderBy(p => p.Length)
        .ToList();

    /// <summary>
    /// Which of these programs plays each system: RetroArch for every system it has a core for when
    /// <paramref name="preferRetroArch"/> (its cores are downloaded the first time they're needed), otherwise the system's own
    /// emulator first and RetroArch where there's none.
    /// </summary>
    public static Dictionary<string, string> Match(IReadOnlyCollection<string> programs, bool preferRetroArch = true)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var usable = Usable(programs);
        var retroArch = usable.FirstOrDefault(RetroArch.IsRetroArch);

        foreach (var system in GameSystems.All)
        {
            if (preferRetroArch && retroArch is not null && system.RetroArchCore is not null)
            {
                result[system.Id] = retroArch;
                continue;
            }
            foreach (var name in system.Emulators)
            {
                var found = usable.FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).StartsWith(name, StringComparison.OrdinalIgnoreCase));
                if (found is null) continue;
                result[system.Id] = found;
                break;
            }
            if (!result.ContainsKey(system.Id) && retroArch is not null && system.RetroArchCore is not null) result[system.Id] = retroArch;
        }
        return result;
    }

    private static IEnumerable<string> Programs(string folder, int depth)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = depth };
        return Directory.EnumerateFiles(folder, "*.exe", options);
    }
}
