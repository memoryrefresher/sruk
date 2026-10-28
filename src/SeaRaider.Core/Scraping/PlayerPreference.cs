namespace SeaRaider.Core.Scraping;

/// <summary>
/// How a title's player is chosen on its page (Settings › General › Media player): the player sites to use when a page offers
/// several (by name, like "embed.st"), and which stream / server button to pick ("Stream 1").
/// </summary>
/// <param name="Hosts">Player sites, best first (lower-case host names, no "www."). Empty: any player.</param>
/// <param name="StreamNumber">The stream / server to pick (1 = "Stream 1"); 0 = don't pick, take the page's own player.</param>
/// <param name="ClickInBrowser">When the page only shows its streams after a click, load it in a hidden browser and click it.</param>
public sealed record PlayerPreference(IReadOnlyList<string> Hosts, int StreamNumber = 1, bool ClickInBrowser = true)
{
    /// <summary>
    /// A source's own setup (Media tab › Player…): the hint that says where the video is on the page (<see cref="PlayerHints"/>),
    /// and the element to click first to make it appear (a CSS selector). When <see cref="Hint"/> is set, the hidden browser looks
    /// for the video by the hint instead of by stream button or site.
    /// </summary>
    public string? Hint { get; init; }

    public string? ClickSelector { get; init; }

    /// <summary>
    /// With <see cref="Hint"/>: the page's numbered link to use ("Link-2" = 2), as picked by the user. 0 = automatic: each link
    /// the page offers is tried in turn, and the first one whose video really starts is used.
    /// </summary>
    public int Link { get; init; }

    public static readonly PlayerPreference None = new([], 0, false);

    /// <summary>Anything to do beyond the page's own player.</summary>
    public bool IsActive => Hosts.Count > 0 || StreamNumber > 0;

    /// <summary>A preference from what the user typed: one site per line or comma, as a name or an address.</summary>
    public static PlayerPreference From(IEnumerable<string>? typed, int streamNumber, bool clickInBrowser) =>
        new(Normalize(typed), Math.Clamp(streamNumber, 0, 20), clickInBrowser);

    /// <summary>"https://www.Embed.st/e/1", " embed.st " → "embed.st"; blanks and repeats dropped.</summary>
    public static List<string> Normalize(IEnumerable<string>? typed)
    {
        var hosts = new List<string>();
        foreach (var raw in typed ?? [])
        {
            foreach (var part in raw.Split([',', ';', '\n', '\r', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            {
                var text = part.Trim().Trim('/', '.');
                if (text.Length == 0) continue;
                var host = Uri.TryCreate(text.Contains("://", StringComparison.Ordinal) ? text : "https://" + text, UriKind.Absolute, out var uri)
                    ? uri.Host.ToLowerInvariant()
                    : text.ToLowerInvariant();
                if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
                if (host.Contains('.') && !hosts.Contains(host)) hosts.Add(host);
            }
        }
        return hosts;
    }

    /// <summary>The address is on one of <see cref="Hosts"/> (or a subdomain of one).</summary>
    public bool Matches(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && Matches(uri);

    public bool Matches(Uri uri) =>
        Hosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

    /// <summary>Where a host comes in the list (0 = best); int.MaxValue when it isn't on it.</summary>
    public int RankOf(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return int.MaxValue;
        for (var i = 0; i < Hosts.Count; i++)
            if (uri.Host.Equals(Hosts[i], StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + Hosts[i], StringComparison.OrdinalIgnoreCase))
                return i;
        return int.MaxValue;
    }

    /// <summary>Tells preferences apart (remembered players are kept per preference).</summary>
    public string Signature => $"{string.Join(',', Hosts)}|{StreamNumber}|{ClickInBrowser}|{Hint}|{ClickSelector}|{Link}";
}
