using AsyncImageLoader;
using AsyncImageLoader.Loaders;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SeaRaider.App.Services;
using SeaRaider.App.ViewModels;
using SeaRaider.App.Views;
using SeaRaider.Core;
using SeaRaider.App.Verification;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Catalog;
using SeaRaider.Core.Downloads;
using SeaRaider.Core.WebChallenge;
using SeaRaider.Core.Social;

namespace SeaRaider.App;

/// <summary>Composition root: wires the core engine and the UI together.</summary>
public sealed partial class App : Application
{
    private ServiceProvider? _services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        _services = ConfigureServices();
        UiMode.Current.Attach(_services.GetRequiredService<SeaRaider.Core.Abstractions.ILibraryRepository>()); // simple / advanced everywhere

        // Cover art & screenshots are cached on disk so the store renders offline.
        // Named like a browser app: some image servers (Wikimedia's, used for the Media tab's crests and posters) refuse
        // downloads that don't say who's asking.
        var imageClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        imageClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) SeaRaider/0.9");
        ImageLoader.AsyncImageLoader = new DiskCachedWebImageLoader(imageClient, true, AppPaths.ImageCacheDirectory);

        // Load the store's in-memory index in the background; the store page awaits it when first shown.
        _ = _services.GetRequiredService<CatalogIndexService>().EnsureLoadedAsync();

        // Restore persisted jobs BEFORE any view-model reads the job list.
        _services.GetRequiredService<IDownloadAndExtractManager>().StartAsync().GetAwaiter().GetResult();

        // Setup folders no game needs any more (removed games, failed deletions): cleared quietly in the background.
        var installer = _services.GetRequiredService<IInstallerService>();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15)); // after start-up's own work
                await installer.CleanUpLeftoverSetupFilesAsync();
            }
            catch (Exception ex)
            {
                ErrorLog.Write(ex, "leftover setup clean-up");
            }
        });

        // sources.txt next to SeaRaider.exe: read first (before the first page is chosen), then kept up to date.
        _services.GetRequiredService<SourcesFileService>().SyncAtStartup();

        // Friends & chat: connects to the relay set in Settings › Friends (does nothing until one is set).
        _ = _services.GetRequiredService<IFriendsService>().StartAsync();
        _services.GetRequiredService<MediaShareService>(); // listens for friends' shared links from the start

        // Skin before the first window, so it opens in the chosen look.
        _services.GetRequiredService<SkinService>().ApplySaved();
        _services.GetRequiredService<EffectsService>().ApplySaved();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = _services.GetRequiredService<MainWindowViewModel>(),
            };
            desktop.ShutdownRequested += (_, _) => ShutdownServices();
            _services.GetRequiredService<SoundCues>().Start();
            _services.GetRequiredService<MediaService>().Start(); // Media tab: new videos, Twitch live status
            _services.GetRequiredService<UpdateViewModel>().Start(desktop.Args ?? []); // new SeaRaider versions

            // Games started from their desktop shortcuts (routed through SeaRaider), or from anywhere else: tracked like
            // Play, so play time and friends' "playing" see them.
            _services.GetRequiredService<ShortcutService>().Start();
            _services.GetRequiredService<IGameLauncherService>().StartWatchingExternalLaunches();
            var startArgs = desktop.Args ?? [];
            if (SeaRaider.Core.Launching.ShortcutCommandLine.Parse(startArgs) is not null) desktop.MainWindow.WindowState = Avalonia.Controls.WindowState.Minimized;
            HandleCommandLine(startArgs, firstStart: true);
            SingleInstance.Listen(args => HandleCommandLine(args, firstStart: false));
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// A command line from this start or a later one: "--play &lt;game&gt;" (a desktop shortcut) starts the game; a plain
    /// second start brings SeaRaider to the front.
    /// </summary>
    private void HandleCommandLine(string[] args, bool firstStart)
    {
        if (SeaRaider.Core.Launching.ShortcutCommandLine.Parse(args) is { } play) _ = PlayFromShortcutAsync(play);
        else if (!firstStart) BringToFront();
    }

    private async Task PlayFromShortcutAsync(SeaRaider.Core.Launching.ShortcutLaunch play)
    {
        if (_services is not { } services) return; // not started yet
        var launcher = services.GetRequiredService<IGameLauncherService>();
        var repository = services.GetRequiredService<ILibraryRepository>();
        try
        {
            if (launcher.IsRunning(play.GameId)) return;
            if (repository.GetInstall(play.GameId) is { State: SeaRaider.Core.Models.GameInstallState.Installed })
            {
                await launcher.LaunchAsync(play.GameId, play.Exe, play.Args);
                return;
            }
            // Not in the library any more, but the program is still there: the shortcut still starts it.
            if (play.Exe is { Length: > 0 } exe && File.Exists(exe))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, play.Args ?? string.Empty)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exe)!,
                });
                return;
            }
            throw new InvalidOperationException("That game isn't in your library any more.");
        }
        catch (OperationCanceledException)
        {
            // declined at the administrator prompt
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "play from shortcut");
            services.GetRequiredService<MainWindowViewModel>().ErrorMessage = $"Couldn't start the game from its shortcut: {ex.Message}";
            BringToFront();
        }
    }

    private void BringToFront()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } window }) return;
        if (window.WindowState == Avalonia.Controls.WindowState.Minimized) window.WindowState = Avalonia.Controls.WindowState.Normal;
        window.Show();
        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Information);
            logging.AddDebug();
        });

        // WebView2 verification window for captchas / bot checks (registered before the core so it wins).
        services.AddSingleton<BrowserDownloadService>();
        // Browser games (itch.io HTML5) play in SeaRaider's own full-screen game window.
        services.AddSingleton<IWebGameHost, WebGameHost>();
        services.AddSingleton<IBrowserDownloader>(sp => sp.GetRequiredService<BrowserDownloadService>());
        services.AddSingleton<IChallengeResolver, WebView2ChallengeResolver>();
        // The same browser clicks "Stream 1" on title pages for the Media tab (one hidden window at a time, verified profile).
        services.AddSingleton<IBrowserPlayerFinder>(sp => (WebView2ChallengeResolver)sp.GetRequiredService<IChallengeResolver>());
        services.AddSeaRaiderCore();

        // UI services
        services.AddSingleton<NavigationService>();
        services.AddSingleton<GameStatusService>();
        services.AddSingleton<GameActionService>();
        services.AddSingleton<ShellService>();
        services.AddSingleton<TrailerPlayerService>();
        services.AddSingleton<SkinService>();
        services.AddSingleton<BackdropService>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<SoundService>();
        services.AddSingleton<SharedGameOpener>();
        services.AddSingleton<SourcesFileService>();
        services.AddSingleton<EffectsService>();
        services.AddSingleton<SoundCues>();
        services.AddSingleton<WatchTogetherService>(); // "Watching" presence, Watch along, watch invites

        // Pages are long-lived (they keep scroll state and live-update in the background).
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<UpdateViewModel>();
        services.AddSingleton<ShortcutService>();
        services.AddSingleton<StorefrontViewModel>();
        services.AddSingleton<LibraryViewModel>();
        services.AddSingleton<DownloadsViewModel>();
        services.AddSingleton<SiteReadersViewModel>();
        services.AddSingleton<SourcesViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<FriendsViewModel>();
        services.AddSingleton<EventChatViewModel>();
        services.AddSingleton<UpcomingViewModel>();
        services.AddSingleton<WishlistViewModel>();
        services.AddSingleton<SharedMediaViewModel>();
        services.AddSingleton<MediaViewModel>();
        // GameDetailViewModel is created per navigation via ActivatorUtilities.

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    /// <summary>
    /// Stops workers (persisting job state for resume), stops the torrent engine and closes the DB.
    /// Bounded so a stuck tracker can't hang app exit.
    /// </summary>
    private void ShutdownServices()
    {
        if (_services is null) return;
        var services = _services;
        _services = null;
        Task.Run(async () => await services.DisposeAsync()).Wait(TimeSpan.FromSeconds(8));
    }
}
