using System.Text;

namespace SeaRaider.Core.Launching;

/// <summary>A "start this game" request from a desktop shortcut: the game, and the program and arguments the shortcut had.</summary>
public sealed record ShortcutLaunch(string GameId, string? Exe, string? Args);

/// <summary>
/// The command line of a desktop shortcut routed through SeaRaider:
/// <c>SeaRaider.exe --play "&lt;game&gt;" --exe "&lt;the game's program&gt;" [--args &lt;its arguments, base64url&gt;]</c>.
/// </summary>
public static class ShortcutCommandLine
{
    public const string PlaySwitch = "--play";
    public const string ExeSwitch = "--exe";
    public const string ArgsSwitch = "--args";

    public static string Build(string gameId, string exe, string? originalArguments)
    {
        var text = new StringBuilder($"{PlaySwitch} \"{gameId}\" {ExeSwitch} \"{exe}\"");
        if (!string.IsNullOrEmpty(originalArguments)) text.Append($" {ArgsSwitch} {Encode(originalArguments)}");
        return text.ToString();
    }

    /// <summary>The request in a command line, or null when it has none.</summary>
    public static ShortcutLaunch? Parse(IReadOnlyList<string> args)
    {
        string? Value(string name)
        {
            for (var i = 0; i < args.Count - 1; i++)
                if (args[i] == name) return args[i + 1];
            return null;
        }

        if (Value(PlaySwitch) is not { Length: > 0 } gameId) return null;
        return new ShortcutLaunch(gameId, Value(ExeSwitch), Value(ArgsSwitch) is { } encoded ? Decode(encoded) : null);
    }

    /// <summary>A shortcut's argument string split like a program's command line (quotes group words).</summary>
    public static List<string> Split(string text)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var any = false;
        foreach (var c in text)
        {
            if (c == '"') { quoted = !quoted; any = true; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any) result.Add(current.ToString());
                current.Clear();
                any = false;
                continue;
            }
            current.Append(c);
            any = true;
        }
        if (any) result.Add(current.ToString());
        return result;
    }

    // The game's own arguments travel base64url-encoded, so their quotes can't get mixed up with SeaRaider's.
    private static string Encode(string text) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? Decode(string value)
    {
        try
        {
            var normal = value.Replace('-', '+').Replace('_', '/');
            normal = normal.PadRight(normal.Length + (4 - normal.Length % 4) % 4, '=');
            return Encoding.UTF8.GetString(Convert.FromBase64String(normal));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
