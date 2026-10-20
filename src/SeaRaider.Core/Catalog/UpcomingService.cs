using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Models;
using SeaRaider.Core.Scraping;

namespace SeaRaider.Core.Catalog;

/// <summary>A wishlisted game turned up in the store.</summary>
public sealed record WishlistAvailableEventArgs(WishlistItem Item, CatalogEntry Entry);

/// <summary>
/// The store's "Coming soon" shelf and the wishlist.
/// <list type="bullet">
///   <item><b>Upcoming sites</b> the user adds (any page listing upcoming freeware / demos, or a feed) are read with
///         <see cref="UpcomingListParser"/> when added, then every <see cref="RefreshEvery"/>.</item>
///   <item><b>Pictures</b>: each game's high-resolution art is looked up on the web (<see cref="ArtworkService"/>: Steam's
///         library hero, screenshots, HD wallpapers), so the shelf and the coming-soon page aren't blurry thumbnails.</item>
///   <item><b>Wishlist</b>: whenever the store's catalog changes (browsing, searches, crawls) the wishlist is compared with
///         it by title (<see cref="TitleNormalizer.DedupKey"/>, "Demo" ignored); every few hours each waiting game is also
///         searched for on the user's download sites. A match raises <see cref="WishlistItemAvailable"/> once.</item>
///   <item><b>Coming soon vs. the store</b>: a game the store already has is never on the shelf, and one that arrives in
///         the store is taken off it (the shelf is rebuilt through <see cref="Changed"/>).</item>
/// </list>
/// Events are raised on background threads.
/// </summary>
public sealed partial class UpcomingService(
    IHttpClientFactory httpClientFactory,
    ILibraryRepository repository,
    CatalogIndexService index,
    ILiveSearchService live,
    ArtworkService artwork,
    ILogger<UpcomingService> logger) : IDisposable
{
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromHours(6);
    private static readonly TimeSpan SearchWishlistEvery = TimeSpan.FromHours(4);
    private static readonly TimeSpan LoopInterval = TimeSpan.FromMinutes(20);

    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _artworkGate = new(1, 1);
    private readonly object _checkLock = new();
    private Timer? _checkTimer;
    private int _started;
    private volatile HashSet<string>? _shelfKeys; // keys of the games the shelf last showed

    /// <summary>Upcoming sites or their games changed (reload the shelf).</summary>
    public event EventHandler? Changed;

    /// <summary>One game's picture was found (update just that tile).</summary>
    public event EventHandler<UpcomingRelease>? ReleaseUpdated;

    /// <summary>Something was added to, removed from or updated on the wishlist.</summary>
    public event EventHandler? WishlistChanged;

    /// <summary>A wishlisted game can now be downloaded from the store.</summary>
    public event EventHandler<WishlistAvailableEventArgs>? WishlistItemAvailable;

    /// <summary>Starts watching the catalog and the background refresh. Safe to call more than once.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        _checkTimer = new Timer(_ =>
        {
            try { CheckWishlist(); }
            catch (Exception ex) { logger.LogWarning(ex, "Checking the wishlist against the store failed"); } // never crash a timer thread
        }, null, Timeout.Infinite, Timeout.Infinite);
        index.Changed += (_, _) => _checkTimer?.Change(TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan); // coalesce bursts
        _ = Task.Run(() => BackgroundLoopAsync(_lifetime.Token));
    }

    private async Task BackgroundLoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false); // let the app finish starting
            await index.EnsureLoadedAsync(ct).ConfigureAwait(false);
            CheckWishlist();
            while (!ct.IsCancellationRequested)
            {
                var stale = repository.GetUpcomingSources()
                    .Where(s => s.Enabled && (s.LastFetchedUtc is null || DateTime.UtcNow - s.LastFetchedUtc > RefreshEvery))
                    .ToList();
                foreach (var source in stale) await RefreshSourceAsync(source, ct).ConfigureAwait(false);
                if (stale.Count == 0) _ = EnrichArtworkAsync(ct); // pictures still missing from last time

                await SearchWishlistAsync(force: false, ct).ConfigureAwait(false);
                await Task.Delay(LoopInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Upcoming releases background loop stopped");
        }
    }

    // ---- sources ----------------------------------------------------------------------------------

    public IReadOnlyList<UpcomingSource> GetSources() => repository.GetUpcomingSources();

    /// <summary>
    /// Upcoming games, in the order the sites were added (games with pictures first). A game that can already be
    /// downloaded from the store is never listed: "Coming soon" only holds games that aren't available yet.
    /// </summary>
    public IReadOnlyList<UpcomingRelease> GetReleases()
    {
        // A site that is switched off keeps its games stored, but they stay off the shelf.
        var order = repository.GetUpcomingSources().Select((s, i) => (s, i)).Where(x => x.s.Enabled).ToDictionary(x => x.s.Id, x => x.i);
        var ordered = repository.GetUpcomingReleases()
            .Where(r => order.ContainsKey(r.SourceId) && !IsDemo(r) && !IsInStore(r))
            .OrderBy(r => order[r.SourceId])
            .ThenByDescending(r => r.BestImage is not null)
            .ThenBy(r => r.FetchedUtc);

        // Each game once: the same title (spacing aside) or the same Steam page from two lists is one tile.
        var titles = new HashSet<string>(StringComparer.Ordinal);
        var apps = new HashSet<int>();
        var releases = new List<UpcomingRelease>();
        foreach (var release in ordered)
        {
            if (!titles.Add(release.Key.Replace(" ", string.Empty, StringComparison.Ordinal))) continue;
            if (release.SteamAppId is { } appId && !apps.Add(appId)) continue;
            releases.Add(release);
        }
        _shelfKeys = releases.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        return releases;
    }

    /// <summary>Demos and playtests aren't "coming soon" games (by title, or because Steam says so).</summary>
    private static bool IsDemo(UpcomingRelease release) => release.IsDemo || TitleNormalizer.IsDemo(release.Title);

    /// <summary>True when the store already has this game (same title, or the full game for an upcoming demo).</summary>
    public bool IsInStore(UpcomingRelease release) =>
        FindInStore(release.Key) is not null ||
        KeysFor(release.Key).Any(index.HasSimilarKey) ||
        (TitleNormalizer.DedupKey(CustomFeedParser.TidyTitle(release.Title)) is { Length: > 0 } tidy && (index.FindByKey(tidy).Count > 0 || index.HasSimilarKey(tidy)));

    /// <summary>
    /// A game may have reached the store since the shelf was last built: reload the shelf if any game must now leave it.
    /// </summary>
    private void NotifyShelfIfChanged()
    {
        var shown = _shelfKeys;
        if (shown is null || shown.Count == 0) return;
        var releases = repository.GetUpcomingReleases();
        if (releases.Any(r => shown.Contains(r.Key) && IsInStore(r)))
            Changed?.Invoke(this, EventArgs.Empty); // the listener rebuilds the shelf through GetReleases()
    }

    /// <summary>Adds a site and reads it at once.</summary>
    /// <param name="startHtml">With this, the site is a "custom list" of game names: reading starts after this HTML.</param>
    /// <param name="endHtml">Custom list: reading stops at this HTML (empty: the end of the page).</param>
    /// <exception cref="ArgumentException">Not a web address, or already added.</exception>
    /// <param name="replaceCustomOnSameUrl">Custom list: a custom list already added for this same page (one that stopped
    /// working, say) is replaced by this one instead of sitting beside it.</param>
    public async Task<UpcomingSource> AddSourceAsync(string url, string? startHtml = null, string? endHtml = null,
        CancellationToken ct = default, bool replaceCustomOnSameUrl = false)
    {
        url = url.Trim();
        startHtml = string.IsNullOrWhiteSpace(startHtml) ? null : startHtml.Trim();
        endHtml = string.IsNullOrWhiteSpace(endHtml) ? null : endHtml.Trim();
        var uri = ParseAddress(url);
        if (replaceCustomOnSameUrl && startHtml is not null)
            foreach (var old in repository.GetUpcomingSources().Where(s => s.IsCustom && string.Equals(s.Url, uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                if (old.StartHtml == startHtml && old.EndHtml == endHtml) continue; // identical: the duplicate check below says so
                repository.DeleteUpcomingSource(old.Id);
            }
        if (repository.GetUpcomingSources().Any(s => string.Equals(s.Url, uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase) &&
                                                     s.IsCustom == (startHtml is not null) && s.StartHtml == startHtml && s.EndHtml == endHtml))
            throw new ArgumentException("That page is already in your upcoming sites.");

        var source = new UpcomingSource
        {
            Url = uri.AbsoluteUri,
            Name = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host,
            IsCustom = startHtml is not null,
            StartHtml = startHtml,
            EndHtml = endHtml,
        };
        repository.UpsertUpcomingSource(source);
        Changed?.Invoke(this, EventArgs.Empty);
        await RefreshSourceAsync(source, ct).ConfigureAwait(false);
        return source;
    }

    private static Uri ParseAddress(string url)
    {
        url = url.Trim();
        if (!url.Contains("://", StringComparison.Ordinal)) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("That doesn't look like a web address. Paste the page's full address, like https://itch.io/games/upcoming.");
        return uri;
    }

    /// <summary>
    /// "Teach it by example": reads the page and, from the name of one game that is on it right now, works out where the list
    /// starts and ends. The guesses come best first, each with the games it would read; the caller shows them and the user says
    /// whether one is right (then <see cref="AddSourceAsync"/> keeps it and refreshes it automatically from then on).
    /// </summary>
    /// <exception cref="ArgumentException">Not a web address, or the game isn't on the page, or no guess reads it.</exception>
    public async Task<IReadOnlyList<FeedRegionCandidate>> FindListRegionAsync(string url, string gameName, CancellationToken ct = default)
    {
        var uri = ParseAddress(url);
        if (string.IsNullOrWhiteSpace(gameName) || gameName.Trim().Length < 2)
            throw new ArgumentException("Type the name of a game that is on that page right now.");

        var client = httpClientFactory.CreateClient(HttpClientNames.Scraper);
        var content = await client.GetStringAsync(uri, ct).ConfigureAwait(false);

        // Regex work over a whole feed: keep it off the UI thread.
        var candidates = await Task.Run(() => FeedRegionFinder.Find(content, gameName), ct).ConfigureAwait(false);
        if (candidates.Count > 0) return candidates;

        throw new ArgumentException(FeedRegionFinder.ContainsName(content, gameName)
            ? $"\"{gameName.Trim()}\" is on the page, but SeaRaider couldn't work out where its list starts and ends. Try another game from the same list, or paste the start and end HTML yourself."
            : $"\"{gameName.Trim()}\" isn't in this page's source. Check the spelling, or try another game that is listed there right now (a page that builds its list with JavaScript won't show it; the site's RSS feed usually does).");
    }

    /// <summary>
    /// Switches a site on or off. Off: its games leave the Coming soon shelf and it isn't read again; on: they come back at once
    /// (and the page is read again if it hasn't been lately).
    /// </summary>
    public void SetSourceEnabled(Guid sourceId, bool enabled)
    {
        var source = repository.GetUpcomingSources().FirstOrDefault(s => s.Id == sourceId);
        if (source is null || source.Enabled == enabled) return;
        source.Enabled = enabled;
        repository.UpsertUpcomingSource(source);
        Changed?.Invoke(this, EventArgs.Empty);
        if (enabled && (source.LastFetchedUtc is null || DateTime.UtcNow - source.LastFetchedUtc > RefreshEvery))
            _ = Task.Run(() => RefreshSourceAsync(source, _lifetime.Token));
    }

    public void RemoveSource(Guid sourceId)
    {
        repository.DeleteUpcomingSource(sourceId);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reads every enabled site again ("Refresh").</summary>
    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        foreach (var source in repository.GetUpcomingSources().Where(s => s.Enabled))
            await RefreshSourceAsync(source, ct).ConfigureAwait(false);
    }

    private async Task RefreshSourceAsync(UpcomingSource source, CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var uri = new Uri(source.Url);
            var client = httpClientFactory.CreateClient(HttpClientNames.Scraper);
            var content = await client.GetStringAsync(uri, ct).ConfigureAwait(false);
            var releases = source.IsCustom
                ? CustomFeedParser.Parse(content, source.StartHtml, source.EndHtml, source.Id)
                : await UpcomingListParser.ParseAsync(content, uri, source.Id, ct).ConfigureAwait(false);

            repository.ReplaceUpcomingReleases(source.Id, releases);
            source.LastCount = releases.Count;
            source.LastError = releases.Count == 0
                ? "No games found on this page. Try the page that lists the games themselves (or its RSS feed)."
                : null;
            logger.LogInformation("Upcoming site {Site}: {Count} games", source.Name, releases.Count);
            if (source.IsCustom) // so a wrong pick (headings vs. lines) is easy to see in the log
                logger.LogInformation("Custom upcoming list {Site} read as: {Names}", source.Name, string.Join(" | ", releases.Take(10).Select(r => r.Title)));
        }
        catch (FeedMarkerException ex)
        {
            source.LastError = ex.Message; // the games already found stay on the shelf
            logger.LogWarning("Custom upcoming list {Site}: {Message}", source.Name, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            source.LastError = $"Couldn't read the page: {ex.Message}";
            logger.LogWarning(ex, "Upcoming site {Site} failed", source.Name);
        }
        finally
        {
            source.LastFetchedUtc = DateTime.UtcNow;
            repository.UpsertUpcomingSource(source);
            _refreshGate.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        _ = EnrichArtworkAsync(ct);
    }

    /// <summary>High-resolution pictures for games that don't have one yet, one at a time.</summary>
    private async Task EnrichArtworkAsync(CancellationToken ct)
    {
        if (!await _artworkGate.WaitAsync(0, ct).ConfigureAwait(false)) return; // already running
        try
        {
            var demoFound = false;
            foreach (var release in repository.GetUpcomingReleases().Where(r => !r.ArtworkDone && !IsDemo(r)).ToList())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // A custom list only has names: find each game's Steam page first.
                    if (release.FindOnSteam && release.SteamAppId is null) await FindOnSteamAsync(release, ct).ConfigureAwait(false);

                    // Steam listings: the app's own header, screenshots and details, straight from Steam.
                    var fromSteam = release.SteamAppId is { } appId && await EnrichFromSteamAsync(release, appId, ct).ConfigureAwait(false);
                    if (release.IsDemo) // Steam says it's a demo: it leaves the shelf, no pictures needed
                    {
                        release.ArtworkChecked = true;
                        release.ArtworkVersion = UpcomingRelease.CurrentArtworkVersion;
                        repository.UpsertUpcomingRelease(release);
                        demoFound = true;
                        continue;
                    }

                    // Anything else (or Steam had no screenshots yet): the web, by the game's name.
                    if (!fromSteam || release.HeroUrl is null)
                    {
                        var record = await artwork.GetAsync(LookupTitle(release.Title), null, ct).ConfigureAwait(false);
                        if (record is not null)
                        {
                            release.HeroUrl ??= record.HeroUrl;
                            if (!fromSteam) release.ImageUrl = record.HeaderUrl ?? release.ImageUrl ?? record.HeroUrl;
                        }
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    continue; // try again next round
                }
                release.ArtworkChecked = true;
                release.ArtworkVersion = UpcomingRelease.CurrentArtworkVersion;
                repository.UpsertUpcomingRelease(release);
                ReleaseUpdated?.Invoke(this, release);
            }
            if (demoFound) Changed?.Invoke(this, EventArgs.Empty); // rebuild the shelf without the demos
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Looking up upcoming games' pictures failed");
        }
        finally
        {
            _artworkGate.Release();
        }
    }

    /// <summary>
    /// Looks a game up by name in Steam's store search. Takes an exact title match (or a close one: the same name plus or
    /// minus a subtitle) and records its app id and store page; then <see cref="EnrichFromSteamAsync"/> does the rest.
    /// </summary>
    private async Task FindOnSteamAsync(UpcomingRelease release, CancellationToken ct)
    {
        var term = LookupTitle(release.Title);
        var client = httpClientFactory.CreateClient(HttpClientNames.Metadata);
        try
        {
            using var json = await client.GetFromJsonAsync<JsonDocument>(
                $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(term)}&l=english&cc=US", ct).ConfigureAwait(false);
            if (json is not null && json.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                var wanted = release.Key;
                var candidates = items.EnumerateArray()
                    .Select(i => (Id: i.TryGetProperty("id", out var id) && id.TryGetInt32(out var n) ? n : 0, Name: Str(i, "name") ?? string.Empty))
                    .Where(c => c.Id > 0 && c.Name.Length > 0)
                    .Select(c => (c.Id, c.Name, Key: TitleNormalizer.DedupKey(LookupTitle(c.Name)), Demo: TitleNormalizer.IsDemo(c.Name)))
                    .ToList();

                var match = candidates.FirstOrDefault(c => c.Key == wanted && !c.Demo);
                if (match.Id == 0)
                    match = candidates.FirstOrDefault(c => !c.Demo && c.Key.Length >= 4 && wanted.Length >= 4 &&
                                                          (c.Key.StartsWith(wanted, StringComparison.Ordinal) || wanted.StartsWith(c.Key, StringComparison.Ordinal)) &&
                                                          Math.Min(c.Key.Length, wanted.Length) >= 0.6 * Math.Max(c.Key.Length, wanted.Length));
                if (match.Id != 0)
                {
                    release.SteamAppId = match.Id;
                    release.PageUrl = $"https://store.steampowered.com/app/{match.Id}/";
                }
            }
            release.FindOnSteam = false; // asked; whatever Steam said, don't ask again
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "Steam search for {Title} failed", term);
            release.FindOnSteam = false;
        }
        await Task.Delay(TimeSpan.FromMilliseconds(350), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Fills a Steam listing from Steam's app details: the header (wide card art), the first screenshot (1920×1080, the
    /// coming-soon page's banner), description, developer, genres and release date. A demo without screenshots of its own
    /// borrows its full game's.
    /// </summary>
    private async Task<bool> EnrichFromSteamAsync(UpcomingRelease release, int appId, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(HttpClientNames.Metadata);
        if (await SteamAppAsync(client, appId, ct).ConfigureAwait(false) is not { } app) return false;

        if (Str(app, "type") is "demo")
        {
            release.IsDemo = true;
            return false;
        }

        Apply(release, app);
        if (release.HeroUrl is null && app.TryGetProperty("fullgame", out var full) && full.ValueKind == JsonValueKind.Object &&
            full.TryGetProperty("appid", out var fullId) && int.TryParse(fullId.ToString(), out var gameId) && gameId != appId &&
            await SteamAppAsync(client, gameId, ct).ConfigureAwait(false) is { } game)
            Apply(release, game);

        await Task.Delay(TimeSpan.FromMilliseconds(350), ct).ConfigureAwait(false); // Steam allows ~200 lookups per 5 minutes
        return release.ImageUrl is not null;

        static void Apply(UpcomingRelease r, JsonElement data)
        {
            if (Str(data, "header_image") is { } header && (r.ImageUrl is null || !r.ImageUrl.Contains("/header", StringComparison.Ordinal)))
                r.ImageUrl = header;
            if (r.HeroUrl is null && data.TryGetProperty("screenshots", out var shots) && shots.ValueKind == JsonValueKind.Array)
                r.HeroUrl = shots.EnumerateArray().Select(s => Str(s, "path_full")).FirstOrDefault(u => u is not null);
            r.HeroUrl ??= Str(data, "background_raw");
            if (string.IsNullOrWhiteSpace(r.Description) && Str(data, "short_description") is { } about)
                r.Description = System.Net.WebUtility.HtmlDecode(TagRegex().Replace(about, " ")).Trim();
            if (r.Developer is null && data.TryGetProperty("developers", out var devs) && devs.ValueKind == JsonValueKind.Array)
                r.Developer = devs.EnumerateArray().Select(d => d.GetString()).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d));
            if (r.Tags.Count == 0 && data.TryGetProperty("genres", out var genres) && genres.ValueKind == JsonValueKind.Array)
                r.Tags = genres.EnumerateArray().Select(g => Str(g, "description")).OfType<string>().Take(5).ToList();
            if (data.TryGetProperty("release_date", out var date) && Str(date, "date") is { } when) r.ReleaseText = when;
        }
    }

    private async Task<JsonElement?> SteamAppAsync(HttpClient client, int appId, CancellationToken ct)
    {
        try
        {
            using var json = await client.GetFromJsonAsync<JsonDocument>(
                $"https://store.steampowered.com/api/appdetails?appids={appId}&l=english&cc=US", ct).ConfigureAwait(false);
            if (json is null || !json.RootElement.TryGetProperty(appId.ToString(), out var root) ||
                !root.TryGetProperty("success", out var ok) || ok.ValueKind != JsonValueKind.True ||
                !root.TryGetProperty("data", out var data))
                return null;
            return data.Clone(); // outlives the document
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Steam details for app {App} failed", appId);
            return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    /// <summary>"Hollow Depths Demo" → "Hollow Depths": the full game's pictures.</summary>
    public static string LookupTitle(string title) => TitleNormalizer.WithoutDemo(title);

    // ---- wishlist ---------------------------------------------------------------------------------

    public IReadOnlyList<WishlistItem> GetWishlist() => repository.GetWishlist();

    public bool IsWishlisted(string key) => repository.GetWishlistItem(key) is not null;

    public WishlistItem AddToWishlist(UpcomingRelease release)
    {
        var item = repository.GetWishlistItem(release.Key) ?? new WishlistItem { Key = release.Key };
        item.Title = release.Title;
        item.ImageUrl = release.BestImage ?? item.ImageUrl;
        item.ReleaseText = release.ReleaseText;
        item.PageUrl = release.PageUrl;
        repository.UpsertWishlistItem(item);
        WishlistChanged?.Invoke(this, EventArgs.Empty);

        CheckItem(item); // it may be in the store already
        if (!item.IsAvailable) _ = Task.Run(() => SearchForAsync(item, _lifetime.Token));
        return item;
    }

    public void RemoveFromWishlist(string key)
    {
        repository.DeleteWishlistItem(key);
        WishlistChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The user has seen the "now in the store" notice.</summary>
    public void MarkNotified(string key)
    {
        if (repository.GetWishlistItem(key) is not { } item || item.Notified) return;
        item.Notified = true;
        repository.UpsertWishlistItem(item);
        WishlistChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Available games the user hasn't been told about yet (e.g. found while SeaRaider was closed).</summary>
    public IReadOnlyList<WishlistItem> GetUnnotified() => repository.GetWishlist().Where(w => w.IsAvailable && !w.Notified).ToList();

    /// <summary>Compares the waiting wishlist with the store's catalog.</summary>
    public void CheckWishlist()
    {
        lock (_checkLock)
        {
            foreach (var item in repository.GetWishlist().Where(w => !w.IsAvailable)) CheckItem(item);
        }
        NotifyShelfIfChanged(); // same moment: games that just reached the store leave "Coming soon"
    }

    private void CheckItem(WishlistItem item)
    {
        if (item.IsAvailable || FindInStore(item.Key) is not { } entry) return;
        MarkAvailable(item, entry);
    }

    private void MarkAvailable(WishlistItem item, CatalogEntry entry)
    {
        item.AvailableGameId = entry.Id;
        item.AvailableSinceUtc = DateTime.UtcNow;
        item.Notified = false;
        repository.UpsertWishlistItem(item);
        logger.LogInformation("Wishlisted {Title} is now in the store ({Id})", item.Title, entry.Id);
        WishlistChanged?.Invoke(this, EventArgs.Empty);
        WishlistItemAvailable?.Invoke(this, new WishlistAvailableEventArgs(item, entry));
    }

    /// <summary>A store entry for the game: same title, or the full game for a wishlisted demo.</summary>
    private CatalogEntry? FindInStore(string key)
    {
        foreach (var candidate in KeysFor(key))
        {
            var match = index.FindByKey(candidate)
                .OrderByDescending(e => e.DetailsLoaded)
                .ThenByDescending(e => e.UploadedAtUtc)
                .FirstOrDefault();
            if (match is not null) return match;
        }
        return null;
    }

    private static IEnumerable<string> KeysFor(string key)
    {
        yield return key;
        var stripped = TitleNormalizer.DedupKey(LookupTitle(key));
        if (stripped.Length > 1 && stripped != key) yield return stripped;
    }

    /// <summary>Asks the download sites for waiting wishlist games (results land in the catalog and are checked).</summary>
    public async Task SearchWishlistAsync(bool force, CancellationToken ct = default)
    {
        var waiting = repository.GetWishlist()
            .Where(w => !w.IsAvailable && (force || w.LastSearchedUtc is null || DateTime.UtcNow - w.LastSearchedUtc > SearchWishlistEvery))
            .ToList();
        foreach (var item in waiting)
        {
            await SearchForAsync(item, ct).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); // gentle on the sites
        }
        CheckWishlist();
    }

    private async Task SearchForAsync(WishlistItem item, CancellationToken ct)
    {
        if (repository.GetSources().Count == 0) return;
        try
        {
            var keys = KeysFor(item.Key).ToHashSet(StringComparer.Ordinal);
            await foreach (var batch in live.SearchAsync(LookupTitle(item.Title), 1, ct).ConfigureAwait(false))
            {
                var hit = batch.Results.FirstOrDefault(e => keys.Contains(GameDeduplicator.KeyFor(e)));
                if (hit is not null && repository.GetWishlistItem(item.Key) is { IsAvailable: false } current)
                {
                    MarkAvailable(current, hit);
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Wishlist search for {Title} failed", item.Title);
        }
        finally
        {
            if (repository.GetWishlistItem(item.Key) is { } latest)
            {
                latest.LastSearchedUtc = DateTime.UtcNow;
                repository.UpsertWishlistItem(latest);
            }
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _checkTimer?.Dispose();
        _lifetime.Dispose();
    }
}
