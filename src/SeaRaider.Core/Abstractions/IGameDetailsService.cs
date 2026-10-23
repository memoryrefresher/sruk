using SeaRaider.Core.Models;

namespace SeaRaider.Core.Abstractions;

/// <summary>On-demand full metadata for a game (lazy loading).</summary>
public interface IGameDetailsService
{
    /// <summary>
    /// Returns the full record, fetching and parsing the game's page first when the stored record is only a
    /// search stub or was produced by an older parser. Falls back to the cached record when offline.
    /// </summary>
    Task<GameMetadata> GetAsync(string gameId, bool forceRefresh = false, CancellationToken cancellationToken = default);
}
