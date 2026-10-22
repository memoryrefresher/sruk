using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SeaRaider.Core.Catalog;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Data;
using SeaRaider.Core.Downloads;
using SeaRaider.Core.Extraction;
using SeaRaider.Core.Installing;
using SeaRaider.Core.Social;
using SeaRaider.Core.Launching;
using SeaRaider.Core.Scraping;
using SeaRaider.Core.WebChallenge;

namespace SeaRaider.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the whole SeaRaider engine. Every service is a singleton: they hold shared state
    /// (the database, the torrent engine, the download queue, running game sessions).
    /// </summary>
    /// <remarks>
    /// To add a site-specific scraper, implement <see cref="IScraperService"/> with a Priority &gt; 0 and
    /// register it: <c>services.AddSingleton&lt;IScraperService, MySiteScraper&gt;();</c>
    /// </remarks>
    public static IServiceCollection AddSeaRaiderCore(this IServiceCollection services, string? databasePath = null)
    {
        AppPaths.EnsureCreated();

        // Shared browser session (cookie jar + per-site user agent) and the verification pipeline.
        // The app replaces NullChallengeResolver with its WebView2 window.
        services.AddSingleton<WebSessionStore>();
        services.TryAddSingleton<IChallengeResolver, NullChallengeResolver>();
        services.TryAddSingleton<IBrowserDownloader, NullBrowserDownloader>();
        services.TryAddSingleton<IWebGameHost, NullWebGameHost>();
        services.AddTransient<ChallengeHandler>();

        services.AddHttpClient(HttpClientNames.Scraper, client =>
            {
                // Per-attempt timeout lives in TimeoutHandler, so waiting for a captcha doesn't time out.
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.8");
            })
            .ConfigurePrimaryHttpMessageHandler(sp => new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 10,
                UseCookies = true,
                CookieContainer = sp.GetRequiredService<WebSessionStore>().Cookies,
            })
            .AddHttpMessageHandler<ChallengeHandler>()
            .AddHttpMessageHandler(() => new TimeoutHandler(TimeSpan.FromSeconds(30)));

        services.AddHttpClient(HttpClientNames.Metadata, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SeaRaider/0.9 (+game launcher)");
        });

        services.AddHttpClient(HttpClientNames.Downloads, client =>
            {
                // Multi-GB files: no overall timeout; HttpResumableDownloader detects stalls per read instead.
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            .ConfigurePrimaryHttpMessageHandler(sp => new SocketsHttpHandler
            {
                // Don't let the server gzip an archive and break Content-Length/Range math.
                AutomaticDecompression = DecompressionMethods.None,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 15,
                ConnectTimeout = TimeSpan.FromSeconds(30),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                UseCookies = true,
                CookieContainer = sp.GetRequiredService<WebSessionStore>().Cookies,
            })
            .AddHttpMessageHandler<ChallengeHandler>()
            // Covers connecting and response headers only; the body streams for as long as it needs.
            .AddHttpMessageHandler(() => new TimeoutHandler(TimeSpan.FromSeconds(60)))
            // Long-lived downloads must not have their handler recycled mid-transfer.
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);

        services.AddSingleton<LiteDbLibraryRepository>(_ => new LiteDbLibraryRepository(databasePath));
        services.AddSingleton<ILibraryRepository>(sp => sp.GetRequiredService<LiteDbLibraryRepository>());

        // Ingestion: register more IScraperService implementations to support special sites.
        services.AddSingleton<GamePageParser>();
        services.AddSingleton<IScraperService, ScraperService>();
        services.AddSingleton<IScraperService, ItchIoScraper>(); // itch.io listings (priority 10)
        services.AddSingleton<IIngestionService, IngestionService>();

        // Store: in-memory catalog index, live site search, lazy full details.
        services.AddSingleton<CatalogIndexService>();
        services.AddSingleton<ILiveSearchService, LiveSearchService>();
        services.AddSingleton<IGameDetailsService, GameDetailsService>();

        // Download → extract → install pipeline.
        services.AddSingleton<IHttpDownloader, HttpResumableDownloader>();
        services.AddSingleton<ITorrentDownloader, MonoTorrentDownloader>();
        services.AddSingleton<IArchiveExtractor, ArchiveExtractor>();
        services.AddSingleton<IDownloadAndExtractManager, DownloadAndExtractManager>();

        // Setup programs found after extraction.
        services.AddSingleton<InstallerDetector>();
        services.AddSingleton<SilentInstallManager>();
        services.AddSingleton<DownloadQueueManager>();
        services.AddSingleton<FileHostResolver>();

        // Self-update from the publisher's signed release file (Settings › Updates).
        services.AddSingleton<SeaRaider.Core.Updates.UpdateService>();

        // Clean media + Steam / IGDB fallback for pages without screenshots.
        services.AddSingleton<IGameMediaProvider, SteamStoreMediaProvider>();
        services.AddSingleton<IGameMediaProvider, IgdbMediaProvider>();
        services.AddSingleton<YouTubeSearch>();
        services.AddSingleton<IGameMediaProvider, YouTubeTrailerProvider>();
        services.AddSingleton<MediaScraperService>();
        services.AddSingleton<WallpaperSearch>();
        services.AddSingleton<ArtworkService>();

        // Store "Coming soon" shelf (user-added upcoming sites) and the wishlist.
        services.AddSingleton<UpcomingService>();
        services.AddSingleton<MediaArtworkService>(); // pictures for the Media tab's titles (posters, crests)
        services.AddSingleton<MediaService>(); // the Media tab
        services.AddSingleton<SiteReaderCatalog>(); // readers\*.json: dedicated readers for sites, written as selectors
        services.AddSingleton<SiteReaderTester>();

        // Friends & chat (relay address in Settings › Friends).
        services.AddSingleton(_ => new SocialStore()); // social.db next to the library database
        services.AddSingleton<PresenceMonitor>();
        services.AddSingleton<IFriendsTransport, RelayFriendsTransport>();
        services.AddSingleton<IFriendsService, FriendsService>();
        services.AddSingleton<EventChatService>(); // live chat per event in the player, for everyone watching (on the relay)
        services.AddSingleton<MediaShareService>(); // Media tab › Shared: links, reactions, comments between friends
        services.AddSingleton<IInstallerService, InstallerService>();

        // Play / uninstall.
        services.AddSingleton<IExecutableLocator, ExecutableLocator>();
        services.AddSingleton<ISaveDataGuard, SaveDataGuard>();
        services.AddSingleton<IGameLauncherService, GameLauncherService>();

        return services;
    }
}
