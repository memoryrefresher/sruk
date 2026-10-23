namespace SeaRaider.Core.Emulation;

/// <summary>What to run to play a game: the emulator, its command line and the folder it starts in.</summary>
public sealed record EmulatorLaunch(string FileName, string Arguments, string WorkingDirectory);

/// <summary>
/// Builds the command line that starts an emulator straight into a game. Known emulators get their "open this game, full
/// screen, no menu" switches; any other program gets just the game's path. The player can replace the arguments in Settings
/// with a template: <c>{rom}</c> is the game file, <c>{romdir}</c> its folder, <c>{emu}</c> the emulator's folder and
/// <c>{core}</c> the RetroArch core for the system.
/// </summary>
public static class EmulatorCommand
{
    public static EmulatorLaunch Build(string emulatorPath, string? argumentsTemplate, GameSystem system, string romPath)
    {
        var emulatorFolder = Path.GetDirectoryName(Path.GetFullPath(emulatorPath)) ?? string.Empty;
        var template = string.IsNullOrWhiteSpace(argumentsTemplate) ? DefaultArguments(emulatorPath, system) : argumentsTemplate.Trim();
        var arguments = template
            .Replace("{rom}", romPath, StringComparison.OrdinalIgnoreCase)
            .Replace("{romdir}", Path.GetDirectoryName(romPath) ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{emu}", emulatorFolder, StringComparison.OrdinalIgnoreCase)
            .Replace("{core}", system.RetroArchCore ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        return new EmulatorLaunch(emulatorPath, arguments, emulatorFolder);
    }

    /// <summary>The arguments that open a game full screen in a well-known emulator, picked by the program's file name.</summary>
    public static string DefaultArguments(string emulatorPath, GameSystem system)
    {
        var name = Path.GetFileNameWithoutExtension(emulatorPath).ToLowerInvariant();

        if (name.Contains("retroarch", StringComparison.Ordinal))
            return system.RetroArchCore is null
                ? "-f \"{rom}\""
                : "-f -L \"{emu}\\cores\\{core}_libretro.dll\" \"{rom}\"";
        if (name.Contains("dolphin", StringComparison.Ordinal)) return "-b -e \"{rom}\"";
        if (name.Contains("pcsx2", StringComparison.Ordinal) || name.Contains("duckstation", StringComparison.Ordinal))
            return "-batch -fullscreen -- \"{rom}\"";
        if (name.Contains("ppsspp", StringComparison.Ordinal)) return "\"{rom}\" --fullscreen";
        if (name.Contains("cemu", StringComparison.Ordinal)) return "-g \"{rom}\" -f";
        if (name.Contains("rpcs3", StringComparison.Ordinal)) return "--no-gui \"{rom}\"";
        if (name.Contains("ryujinx", StringComparison.Ordinal)) return "\"{rom}\" -f";
        if (name.Contains("yuzu", StringComparison.Ordinal) || name.Contains("suyu", StringComparison.Ordinal) ||
            name.Contains("sudachi", StringComparison.Ordinal) || name.Contains("citron", StringComparison.Ordinal))
            return "-f -g \"{rom}\"";
        if (name.Contains("citra", StringComparison.Ordinal) || name.Contains("lime3ds", StringComparison.Ordinal) ||
            name.Contains("azahar", StringComparison.Ordinal))
            return "\"{rom}\" -f";
        if (name.Contains("melonds", StringComparison.Ordinal) || name.Contains("mgba", StringComparison.Ordinal))
            return "-f \"{rom}\"";
        if (name.Contains("mesen", StringComparison.Ordinal)) return "\"{rom}\" --fullscreen";
        return "\"{rom}\"";
    }
}
