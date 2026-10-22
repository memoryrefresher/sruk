using System.Text.RegularExpressions;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Scraping;

/// <summary>
/// Hosts SeaRaider never offers or plans because they stop an unattended download (a captcha on every file, with no
/// way for SeaRaider to carry on afterwards). MixDrop is the known one.
///
/// <para>A link can name its host in three places, and all three are checked: the host of its address
/// ("mixdrop.ag"), the mirror / provider name printed on the page ("Link MixDrop:" - the only clue when the link goes
/// through a URL generator such as bluemediafiles.com), and the address hidden in a redirector's <c>?url=</c> parameter
/// when that is plain base64 or percent-encoded.</para>
///
/// <para>This only <i>skips</i> hosts. Nothing here solves or bypasses a captcha: the hosts that stay (BlueMediaFiles
/// and the like) are sent to the verification window, where a person solves it.</para>
/// </summary>
public static partial class HostPolicy
{
    /// <summary>Switch the whole filter off (every host is offered again).</summary>
    public static bool Enabled { get; set; } = true;

    // To block another host, add its name to BOTH patterns: as printed on a page, and as a domain label.
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:mixdrop|m1xdrop|mxdrop)(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BlockedNameRegex();

    [GeneratedRegex(@"(?:^|\.)(?:mixdrop|m1xdrop|mxdrop)\.[a-z]{2,}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BlockedHostRegex();

    /// <summary>A mirror / provider name or a stretch of page text that names a blocked host ("Link MixDrop:").</summary>
    public static bool IsBlockedName(string? text) =>
        Enabled && !string.IsNullOrWhiteSpace(text) && BlockedNameRegex().IsMatch(text);

    /// <summary>A blocked host's domain or one of its sub-domains / look-alike TLDs (mixdrop.ag, mixdrop.co, www.mixdrop.to …).</summary>
    public static bool IsBlockedHost(string? host) =>
        Enabled && !string.IsNullOrWhiteSpace(host) && BlockedHostRegex().IsMatch(host.Trim());

    /// <summary>True when the address, or the address inside a redirector's <c>?url=</c>, is a blocked host.</summary>
    public static bool IsBlockedUrl(string? url)
    {
        if (!Enabled || !Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)) return false;
        if (IsBlockedHost(uri.Host)) return true;
        return InnerTargets(uri).Any(t => IsBlockedHost(t.Host));
    }

    /// <summary>Does this link point at, or is it named after, a blocked host.</summary>
    public static bool IsBlocked(DownloadLink link) =>
        Enabled && (IsBlockedHost(link.Host) || IsBlockedName(link.Mirror) || IsBlockedUrl(link.Url));

    /// <summary>
    /// Addresses carried inside a redirector's query ("url-generator.php?url=aHR0cHM6Ly9taXhkcm9wLmFnL2YvYWJj").
    /// Only what decodes to a plain http(s) address: an encrypted token (BlueMediaFiles usually uses one) yields nothing,
    /// and the provider name is then the only way to tell.
    /// </summary>
    public static IEnumerable<Uri> InnerTargets(Uri url)
    {
        foreach (var pair in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            // Percent-decoded only: a '+' in the value may be base64's own character, not a space.
            var raw = pair.Split('=', 2) is [_, var v] ? Uri.UnescapeDataString(v) : string.Empty;
            if (raw.Length == 0) continue;

            if (Uri.TryCreate(raw.Replace('+', ' '), UriKind.Absolute, out var plain) && plain.Scheme is "http" or "https")
            {
                yield return plain;
            }
            else if (TryBase64(raw) is { } decoded && Uri.TryCreate(decoded, UriKind.Absolute, out var inner) && inner.Scheme is "http" or "https")
            {
                yield return inner;
            }
        }
    }

    private static string? TryBase64(string value)
    {
        if (value.Length < 12) return null;
        try
        {
            var normalized = value.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '=');
            var text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
            return text.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? text : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
