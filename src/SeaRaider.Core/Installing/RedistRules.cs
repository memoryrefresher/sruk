using System.Text.RegularExpressions;

namespace SeaRaider.Core.Installing;

/// <summary>A runtime installer a game's setup starts (DirectX, Visual C++, .NET …) and how to run it without windows.</summary>
/// <param name="Name">"DirectX", "Visual C++ runtime" …</param>
/// <param name="SilentArguments">Switches for a quiet install, or null when there's no known quiet mode (then it's skipped).</param>
/// <param name="NeedsFolder">The installer needs the files next to it (DirectX's DXSETUP and its .cab files).</param>
public sealed record RedistInstaller(string Name, string? SilentArguments, bool NeedsFolder = false);

/// <summary>
/// Recognises the extra installers repack setups run at the end ("_Redist\dxwebsetup.exe", "vcredist_x64.exe" …), each
/// with its own wizard window, and the web page some setups open when they finish. Used by <see cref="SetupRunner"/>.
/// </summary>
public static partial class RedistRules
{
    [GeneratedRegex(@"[\\/](?:_?redist|_?common ?redist|_?redistributables?|_?directx|_?vcredist|_?prerequisites?|_?prereqs?)[\\/]",
        RegexOptions.IgnoreCase)]
    private static partial Regex RedistFolderRegex();

    private static readonly string[] Browsers =
    [
        "msedge.exe", "microsoftedge.exe", "chrome.exe", "firefox.exe", "brave.exe", "opera.exe", "opera_gx.exe",
        "vivaldi.exe", "iexplore.exe", "browser.exe", "yandex.exe", "waterfox.exe", "librewolf.exe", "arc.exe", "thorium.exe",
    ];

    /// <summary>The runtime installer at <paramref name="path"/>, or null when it isn't one.</summary>
    public static RedistInstaller? Classify(string path)
    {
        var name = FileName(path);
        var known = name switch
        {
            "dxsetup.exe" => new RedistInstaller("DirectX", "/silent", NeedsFolder: true),
            "dxwebsetup.exe" => new RedistInstaller("DirectX", "/Q"),
            _ when name.StartsWith("vcredist") || name.StartsWith("vc_redist") =>
                new RedistInstaller("Visual C++ runtime", "/q /norestart"),
            _ when name.StartsWith("windowsdesktop-runtime") || name.StartsWith("dotnet-runtime") || name.StartsWith("aspnetcore-runtime") =>
                new RedistInstaller(".NET runtime", "/install /quiet /norestart"),
            _ when name.StartsWith("ndp4") || name.StartsWith("ndp3") || name.StartsWith("dotnetfx") || name.StartsWith("netfx") =>
                new RedistInstaller(".NET Framework", "/q /norestart"),
            "oalinst.exe" => new RedistInstaller("OpenAL", "/s"),
            _ when name.StartsWith("physx") => new RedistInstaller("PhysX", "/quiet"),
            _ when name.StartsWith("ue4prereqsetup") || name.StartsWith("ueprereqsetup") => new RedistInstaller("Unreal prerequisites", "/quiet /norestart"),
            _ when name.StartsWith("xnafx") => new RedistInstaller("XNA Framework", null),
            _ => null,
        };
        if (known is not null) return known;

        // Anything else run from a redist folder: an extra installer with its own wizard; no known quiet switches.
        return RedistFolderRegex().IsMatch(path) && name.EndsWith(".exe") && !name.StartsWith("unins")
            ? new RedistInstaller(name[..^4], null)
            : null;
    }

    /// <summary>A web browser (started by a setup to show a web page when it finishes).</summary>
    public static bool IsBrowser(string fileName) => Browsers.Contains(FileName(fileName));

    /// <summary>Lower-case file name of a Windows path (whatever the OS running this).</summary>
    private static string FileName(string path) => path[(path.LastIndexOfAny(['\\', '/']) + 1)..].ToLowerInvariant();
}
