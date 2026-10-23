using SeaRaider.Core.Scraping;

namespace SeaRaider.Core.Abstractions;

/// <summary>
/// Finds a title's player the way a person would: the title's page is opened in a hidden browser, the chosen stream button
/// ("Stream 1") is clicked, and the player that loads (on one of the preferred player sites, when there are any) is handed back.
/// For pages whose streams only appear after a click. The desktop app provides it; without it, players are only read from
/// the page's HTML.
/// </summary>
public interface IBrowserPlayerFinder
{
    /// <returns>The player's address, or null when none showed up in time.</returns>
    Task<string?> FindPlayerAsync(Uri titlePage, PlayerPreference preference, CancellationToken cancellationToken);
}
