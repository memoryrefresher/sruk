using Avalonia;
using SeaRaider.Core.Installing;

namespace SeaRaider.App;

internal static class Program
{
    // Avalonia isn't ready before AppMain is called: don't touch Avalonia APIs or
    // third-party UI code before this point.
    [STAThread]
    public static int Main(string[] args)
    {
        // Started again as the setup helper (as administrator, for a repack's setup): no window, just the setup.
        if (args.Length == 2 && args[0] == SetupRunnerRequest.Switch) return SetupRunner.Run(args[1]);

        // Started as the updater's helper (the new version, from the updates folder): replace SeaRaider.exe, restart it.
        if (args.Length >= 3 && args[0] == SeaRaider.Core.Updates.UpdateService.ApplySwitch)
            return SeaRaider.Core.Updates.UpdateService.RunApplyHelper(args);

        // One SeaRaider at a time: a second start (a game's desktop shortcut, the Start menu) hands its command line to the
        // running one, which starts the game or comes to the front.
        if (!Services.SingleInstance.TryBecomePrimary())
        {
            if (Services.SingleInstance.Forward(args)) return 0;
            // The running SeaRaider is closing: wait for it, then carry on as the only one.
            var primary = false;
            for (var i = 0; i < 20 && !primary; i++)
            {
                Thread.Sleep(500);
                primary = Services.SingleInstance.TryBecomePrimary();
            }
            if (!primary) return 0;
        }

        // Before anything opens the library database: is this the very first start on this PC? (asks the welcome questions)
        Services.FirstRunDetector.Detect();

        // Downloads and extraction keep full speed when SeaRaider isn't the window in front (Alt+Tab, behind a game, minimised).
        Services.BackgroundThrottling.KeepFullSpeed();

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // SeaRaider is picture-heavy (covers, banners, screenshots): keep more of them on the graphics card, so
            // scrolling back doesn't re-upload what was just on screen (Avalonia's default budget is ~28 MB).
            .With(new SkiaOptions { MaxGpuResourceSizeBytes = 256L * 1024 * 1024 })
            .WithInterFont()
            .LogToTrace();
}
