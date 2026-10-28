using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Catalog;

/// <summary>
/// The real release date of games whose page doesn't give one (most download sites only show when they posted it): looked up
/// on the game's Steam page and saved with every copy of the game in the store, so "Release date" sorting and the cards use
/// it. Asked for by the store while it's sorted by release date, a few games at a time.
///
/// <para>One lookup per game title (kept in its <see cref="ArtworkRecord"/>); games Steam doesn't have are asked again
/// after a week. Steam allows about 200 lookups in 5 minutes, so lookups are spaced out.</para>
/// </summary>
public sealed class ReleaseDateService(IHttpClientFactory httpClientFactory, ILibraryRepository repository, ILogger<ReleaseDateService> logger)
{
    private static readonly TimeSpan RetryMissesAfter = TimeSpan.FromDays(7);
    private static readonly TimeSpan Spacing = TimeSpan.FromMilliseconds(1600);
    private readonly SemaphoreSlim _one = new(1, 1);

    /// <summary>A lookup is still needed for this game (it has no real release date, and Steam wasn't asked lately).</summary>
    public bool NeedsLookup(GameGroup group)
    {
        if (group.Variants.Any(v => v.KnownReleaseUtc is not null)) return false;
        var record = repository.GetArtwork(group.Key);
        return record?.ReleaseCheckedAtUtc is not { } checkedAt || (record.ReleaseDateUtc is null && DateTime.UtcNow - checkedAt > RetryMissesAfter);
    }

    /// <summary>
    /// Looks up the release dates of <paramref name="groups"/> (those that need it), one after another, saving each into
    /// the catalog as it's found. Returns how many were found.
    /// </summary>
    public async Task<int> FillAsync(IEnumerable<GameGroup> groups, int max, CancellationToken ct)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var found = 0;
            var client = httpClientFactory.CreateClient(HttpClientNames.Metadata);
            foreach (var group in groups.Where(NeedsLookup).Take(max).ToList())
            {
                ct.ThrowIfCancellationRequested();
                var record = repository.GetArtwork(group.Key);
                if (record?.ReleaseDateUtc is null || record.ReleaseCheckedAtUtc is null)
                {
                    record = await LookupAsync(client, group, record, ct).ConfigureAwait(false);
                    await Task.Delay(Spacing, ct).ConfigureAwait(false);
                }
                if (record?.ReleaseDateUtc is { } date && Apply(group, date, record.ReleaseDateText)) found++;
            }
            return found;
        }
        finally
        {
            _one.Release();
        }
    }

    private async Task<ArtworkRecord?> LookupAsync(HttpClient client, GameGroup group, ArtworkRecord? record, CancellationToken ct)
    {
        var title = TitleNormalizer.WithoutDemo(group.Primary.Title);
        try
        {
            var app = record?.SteamAppId ?? await FindAppAsync(client, title, ct).ConfigureAwait(false);
            DateTime? date = null;
            string? text = null;
            if (app is { } id)
            {
                using var json = await client.GetFromJsonAsync<JsonDocument>(
                    $"https://store.steampowered.com/api/appdetails?appids={id}&filters=release_date&l=english&cc=US", ct).ConfigureAwait(false);
                if (json is not null && json.RootElement.TryGetProperty(id.ToString(), out var root) && root.TryGetProperty("data", out var data) &&
                    data.ValueKind == JsonValueKind.Object && data.TryGetProperty("release_date", out var release) &&
                    release.TryGetProperty("date", out var dateText) && dateText.ValueKind == JsonValueKind.String)
                {
                    text = dateText.GetString();
                    date = ReleaseDates.ParseSteam(text);
                }
            }

            record ??= new ArtworkRecord { Key = group.Key };
            record.SteamAppId ??= app;
            record.ReleaseDateUtc = date;
            record.ReleaseDateText = date is null ? null : text;
            record.ReleaseCheckedAtUtc = DateTime.UtcNow;
            repository.UpsertArtwork(record);
            return record;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Release date lookup failed for {Title}", title); // not remembered: asked again next time
            return null;
        }
    }

    private static async Task<int?> FindAppAsync(HttpClient client, string title, CancellationToken ct)
    {
        var term = Uri.EscapeDataString(TitleNormalizer.NormalizeForSearch(title));
        using var search = await client.GetFromJsonAsync<JsonDocument>(
            $"https://store.steampowered.com/api/storesearch/?term={term}&l=english&cc=US", ct).ConfigureAwait(false);
        if (search?.RootElement.TryGetProperty("items", out var items) != true || items.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in items.EnumerateArray())
        {
            var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (name is null || !item.TryGetProperty("id", out var id) || !id.TryGetInt32(out var value)) continue;
            if (ArtworkService.IsMatch(title, name)) return value;
        }
        return null;
    }

    /// <summary>Saves the date into every copy of the game that has no real release date of its own.</summary>
    private bool Apply(GameGroup group, DateTime date, string? text)
    {
        var updated = new List<GameMetadata>();
        foreach (var variant in group.Variants.Where(v => v.KnownReleaseUtc is null))
        {
            if (repository.GetGame(variant.Id) is not { } game) continue;
            game.ReleaseDateUtc = date;
            game.ReleaseDate = text ?? date.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
            game.ReleaseDateFromSteam = true;
            updated.Add(game);
        }
        if (updated.Count > 0) repository.UpsertGames(updated);
        return updated.Count > 0;
    }
}
