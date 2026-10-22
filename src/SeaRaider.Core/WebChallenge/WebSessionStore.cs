using System.Collections.Concurrent;
using System.Net;

namespace SeaRaider.Core.WebChallenge;

/// <summary>
/// Browser session state shared by every SeaRaider HTTP client: one cookie jar, plus the browser user agent per
/// site after a verification (clearance cookies are only accepted together with the user agent that earned them).
/// </summary>
public sealed class WebSessionStore
{
    private readonly ConcurrentDictionary<string, string> _userAgents = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _resolvedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Cookie jar used by the scraper and download handlers.</summary>
    public CookieContainer Cookies { get; } = new() { PerDomainCapacity = 100 };

    /// <summary>User agent to send to <paramref name="host"/>, if a verification set one.</summary>
    public string? UserAgentFor(string host) => _userAgents.TryGetValue(SiteKey(host), out var ua) ? ua : null;

    /// <summary>Serialises verifications per site so parallel requests don't open several windows.</summary>
    public SemaphoreSlim GateFor(string host) => _gates.GetOrAdd(SiteKey(host), _ => new SemaphoreSlim(1, 1));

    /// <summary>True if a verification for <paramref name="host"/> completed after <paramref name="since"/>.</summary>
    public bool ResolvedSince(string host, DateTime since) =>
        _resolvedAt.TryGetValue(SiteKey(host), out var at) && at >= since;

    private readonly ConcurrentDictionary<string, DateTime> _declinedAt = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Remembers that the user closed / cancelled a verification, so background requests don't reopen it at once.</summary>
    public void MarkDeclined(string host) => _declinedAt[SiteKey(host)] = DateTime.UtcNow;

    /// <summary>True for two minutes after the user cancelled a verification for this site.</summary>
    public bool DeclinedRecently(string host) =>
        _declinedAt.TryGetValue(SiteKey(host), out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, DateTime> _browserOnlyAt = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Remembers that this site still blocked SeaRaider's HTTP client after a verification but let the real browser
    /// through, so later page requests go to the browser first instead of failing again.
    /// </summary>
    public void MarkBrowserOnly(string host) => _browserOnlyAt[SiteKey(host)] = DateTime.UtcNow;

    /// <summary>True for an hour after <see cref="MarkBrowserOnly"/> (then the plain HTTP client gets another try).</summary>
    public bool IsBrowserOnly(string host) =>
        _browserOnlyAt.TryGetValue(SiteKey(host), out var at) && DateTime.UtcNow - at < TimeSpan.FromHours(1);

    /// <summary>Stores a completed verification's cookies and user agent.</summary>
    public void Apply(Uri site, ChallengeResolution resolution)
    {
        foreach (var cookie in resolution.Cookies)
        {
            try
            {
                if (string.IsNullOrEmpty(cookie.Domain)) Cookies.Add(site, cookie);
                else Cookies.Add(cookie);
            }
            catch (CookieException)
            {
                // A cookie .NET considers invalid (rare); the rest still apply.
            }
        }

        var key = SiteKey(site.Host);
        if (!string.IsNullOrWhiteSpace(resolution.UserAgent))
        {
            _userAgents[key] = resolution.UserAgent;
            if (resolution.DownloadUrl is { } download) _userAgents[SiteKey(download.Host)] = resolution.UserAgent;
        }
        _resolvedAt[key] = DateTime.UtcNow;
        _declinedAt.TryRemove(key, out _);
    }

    /// <summary>Groups "www.example.com" and "dl.example.com" under "example.com".</summary>
    private static string SiteKey(string host)
    {
        var parts = host.ToLowerInvariant().Split('.');
        if (parts.Length <= 2) return string.Join('.', parts);
        // Two-level public suffixes ("co.uk", "com.br"): keep three labels.
        var keep = parts[^1].Length == 2 && parts[^2].Length <= 3 ? 3 : 2;
        return string.Join('.', parts[^keep..]);
    }
}
