using System.Collections.Concurrent;

namespace SeaRaider.Core.Scraping;

/// <summary>What SeaRaider's player does on a site's page it plays whole: which link to start on, and what the player area is.</summary>
/// <param name="Link">The page's numbered link to use (1 = "Link-1"); 0 tries each link until one plays.</param>
/// <param name="PlayerSteps">The user's own "click first" steps (a selector or text:…); the last one is the player area. Null: found automatically.</param>
public sealed record PageAssistPlan(int Link, string? PlayerSteps);

/// <summary>
/// A site's page played whole in the player (Media tab › the source's "Play the page and press play for me"): the player
/// opens the page, picks the link and presses the player's play button with real clicks, until something plays. The plan for a
/// page is set when it's resolved and read by the player when it opens the page.
/// </summary>
public static class PageAssist
{
    private static readonly ConcurrentDictionary<string, PageAssistPlan> Plans = new(StringComparer.OrdinalIgnoreCase);

    private static string KeyOf(string pageUrl) => MediaStreams.WithoutHint(pageUrl).Split('#')[0];

    public static void Set(string pageUrl, PageAssistPlan plan) => Plans[KeyOf(pageUrl)] = plan;

    private static readonly ConcurrentDictionary<string, int> LinkCounts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many numbered links a site's pages have, as last seen on one (3 until one has been seen).</summary>
    public static int LinkCountFor(string? pageUrl) =>
        Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri) && LinkCounts.TryGetValue(uri.Host, out var n) ? n : 3;

    public static void SetLinkCount(string? pageUrl, int count)
    {
        if (count > 0 && Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri)) LinkCounts[uri.Host] = Math.Min(count, 9);
    }

    public static PageAssistPlan? Get(string? pageUrl) =>
        !string.IsNullOrEmpty(pageUrl) && Plans.TryGetValue(KeyOf(pageUrl), out var plan) ? plan : null;
}
