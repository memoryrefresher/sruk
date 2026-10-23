using Microsoft.Win32;
using SeaRaider.Core;
using SeaRaider.Core.Social;

namespace SeaRaider.App.Services;

/// <summary>A searaider://add/&lt;friend code&gt;?name=… link: someone's "Add me on SeaRaider" button on Discord.</summary>
public sealed record AddFriendLink(string Code, string? Name);

/// <summary>
/// searaider:// links. Registered for the current Windows user (HKCU\Software\Classes\searaider, no administrator prompt) to
/// open this SeaRaider.exe, updated each start so it follows SeaRaider if it moves. A link opened while SeaRaider runs reaches
/// the running copy (the second start passes its command line over and exits).
/// </summary>
public static class SeaRaiderLinks
{
    public const string Scheme = "searaider";

    public static void Register()
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return;
            if (Environment.ProcessPath is not { } exe || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
            var command = $"\"{exe}\" \"%1\"";
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Scheme}");
            using (var existing = key.OpenSubKey(@"shell\open\command"))
                if (string.Equals(existing?.GetValue(null) as string, command, StringComparison.OrdinalIgnoreCase)) return;
            key.SetValue(null, "URL:SeaRaider");
            key.SetValue("URL Protocol", string.Empty);
            using (var icon = key.CreateSubKey("DefaultIcon")) icon.SetValue(null, $"\"{exe}\",0");
            using (var open = key.CreateSubKey(@"shell\open\command")) open.SetValue(null, command);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "register searaider:// links");
        }
    }

    /// <summary>The add-friend link in a command line, or null.</summary>
    public static AddFriendLink? ParseAdd(IEnumerable<string> args)
    {
        foreach (var arg in args)
        {
            if (!arg.StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Uri.TryCreate(arg.Trim().TrimEnd('/'), UriKind.Absolute, out var uri)) continue;
            // searaider://add/SR-XXXXX-XXXXX → host "add", path "/SR-XXXXX-XXXXX"
            if (!string.Equals(uri.Host, "add", StringComparison.OrdinalIgnoreCase)) continue;
            var code = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
            if (FriendCode.Normalize(code) is not { } userId) continue;
            string? name = null;
            foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                if (eq > 0 && part[..eq].Equals("name", StringComparison.OrdinalIgnoreCase))
                    name = Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
            }
            return new AddFriendLink(userId, string.IsNullOrWhiteSpace(name) ? null : name.Trim());
        }
        return null;
    }
}
