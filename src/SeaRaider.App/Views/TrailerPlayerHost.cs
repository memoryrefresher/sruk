using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Web.WebView2.Core;
using SeaRaider.App.Services;
using SeaRaider.App.Verification;
using SeaRaider.Core;
using SeaRaider.Core.Scraping;

namespace SeaRaider.App.Views;

/// <summary>
/// SeaRaider's trailer player, inside the main window: a WebView2 browser hosted in a native child window of the
/// Avalonia window (<see cref="NativeControlHost"/>), showing the player page from %LOCALAPPDATA%\SeaRaider\player
/// under a private https address. Created when the trailer overlay opens and torn down (browser closed) when it closes.
///
/// <para>Steam's video servers only allow the Steam store to read the stream from a web page (CORS), so this browser
/// runs with its own profile and web security relaxed for the player alone, and sends Steam's store as the Referer to
/// Steam's servers. It never opens anything but the player page; links ("Watch it on the Steam store") go to the
/// default browser.</para>
///
/// <para>A native window always draws above Avalonia content, so the overlay keeps its own controls (title, close)
/// outside the video's rectangle.</para>
///
/// <para><b>No pop-up ads.</b> Video sites' embedded players open an ad in a new window (or send the whole page to one) on the
/// first click on "play". Nothing the video's frame asks to open goes anywhere: new windows never open, the player page is
/// never navigated away, frames aren't sent to ad networks, and nothing is loaded from ad networks at all
/// (<see cref="AdHosts"/>). Only a click on a known video site's own link (YouTube's logo, "Watch on Twitch") still opens the
/// default browser.</para>
/// </summary>
public sealed class TrailerPlayerHost : NativeControlHost
{
    private const string PlayerHost = TrailerPlayerService.PlayerHost;

    /// <summary>Sites whose own links (clicked by the user) may still open in the default browser from the player.</summary>
    private static readonly string[] TrustedLinkHosts =
        ["youtube.com", "youtu.be", "vimeo.com", "twitch.tv", "kick.com", "dailymotion.com", "steampowered.com", "steamcommunity.com"];

    private static bool IsTrustedLink(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
        TrustedLinkHosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase) ||
                                  uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

    private readonly TrailerPlayerService _player;
    private readonly TrailerSession _session;
    private IntPtr _hwnd;
    private CoreWebView2Controller? _controller;
    private bool _destroyed;

    public TrailerPlayerHost(TrailerPlayerService player, TrailerSession session)
    {
        _player = player;
        _session = session;
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var handle = base.CreateNativeControlCore(parent); // a plain child window of SeaRaider's window
        _hwnd = handle.Handle;
        _destroyed = false;
        _ = StartAsync();
        return handle;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _destroyed = true;
        CloseBrowser();
        base.DestroyNativeControlCore(control);
    }

    private async Task StartAsync()
    {
        try
        {
            var options = new CoreWebView2EnvironmentOptions("--disable-web-security --autoplay-policy=no-user-gesture-required");
            var profile = Path.Combine(AppPaths.DataDirectory, "webview2-player");
            Directory.CreateDirectory(profile);
            var environment = await CoreWebView2Environment.CreateAsync(WebView2Runtime.FindFixedVersionFolder(), profile, options);
            if (_destroyed) return;

            var controller = await environment.CreateCoreWebView2ControllerAsync(_hwnd);
            if (_destroyed)
            {
                controller.Close();
                return;
            }
            _controller = controller;
            controller.DefaultBackgroundColor = System.Drawing.Color.Black;
            UpdateBrowserBounds();

            var core = controller.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;

            // Every request, from the page and from the frames in it (a video site's player): ad networks get nothing, and
            // Steam's video servers get the store as the page asking.
            var embedHost = _session.EmbedHost;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, args) =>
            {
                var url = args.Request.Uri;
                if (AdHosts.IsAd(url))
                {
                    args.Response = environment.CreateWebResourceResponse(null, 403, "Blocked", "Content-Type: text/plain");
                    return;
                }
                // A site's player in the frame: asked for by the page it was found on (many refuse to play anywhere else).
                if (embedHost is not null && Uri.TryCreate(url, UriKind.Absolute, out var requested) &&
                    requested.Host.Equals(embedHost, StringComparison.OrdinalIgnoreCase))
                {
                    args.Request.Headers.SetHeader("Referer", _session.EmbedReferer!);
                    return;
                }
                if (!url.Contains("steamstatic.com", StringComparison.OrdinalIgnoreCase)) return;
                // Steam's video servers serve the store's own pages: present the player's requests the same way.
                args.Request.Headers.SetHeader("Referer", "https://store.steampowered.com/");
                if (args.Request.Headers.Contains("Origin")) args.Request.Headers.SetHeader("Origin", "https://store.steampowered.com");
            };

            // Only the player page itself. A frame trying to send the whole player to an ad is stopped; a click on a known
            // video site's own link opens the default browser.
            core.NavigationStarting += (_, args) =>
            {
                if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var target) && target.Host == PlayerHost) return;
                args.Cancel = true;
                if (target is not null && args.IsUserInitiated && IsTrustedLink(target.AbsoluteUri)) _player.OpenExternal(target.AbsoluteUri);
            };
            // Frames inside the player (the video site's own player) may move between their pages, never to an ad network.
            core.FrameNavigationStarting += (_, args) =>
            {
                if (AdHosts.IsAd(args.Uri)) args.Cancel = true;
            };
            // Pop-ups and pop-unders (the ads a click on "play" opens) never open. Only a click on a known video site's own
            // link ("Watch on YouTube") goes to the default browser.
            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (args.IsUserInitiated && IsTrustedLink(args.Uri)) _player.OpenExternal(args.Uri);
            };
            core.DownloadStarting += (_, args) => args.Cancel = true; // "your video is ready" ad downloads
            core.PermissionRequested += (_, args) =>
            {
                // Notification prompts are push-ad sign-ups; the video needs none of these.
                if (args.PermissionKind is CoreWebView2PermissionKind.Notifications or CoreWebView2PermissionKind.Geolocation
                    or CoreWebView2PermissionKind.Camera or CoreWebView2PermissionKind.Microphone or CoreWebView2PermissionKind.ClipboardRead)
                    args.State = CoreWebView2PermissionState.Deny;
            };
            core.Settings.AreDefaultScriptDialogsEnabled = false; // never show a page's alert / confirm boxes over the video
            core.WebMessageReceived += (_, args) =>
            {
                var message = args.TryGetWebMessageAsString();
                if (message == "close") MinimizeSoon(); // Esc in the page: into the corner, still playing
                else if (message?.StartsWith("open:", StringComparison.Ordinal) == true) _player.OpenExternal(message[5..]);
                else if (message?.StartsWith("state:", StringComparison.Ordinal) == true) OnState(message);
            };
            // Esc closes the player even while the YouTube frame has focus (unless a video is fullscreen).
            controller.AcceleratorKeyPressed += (_, args) =>
            {
                if (args.KeyEventKind == CoreWebView2KeyEventKind.KeyDown && args.VirtualKey == 0x1B && !core.ContainsFullScreenElement &&
                    !_player.IsMinimized)
                {
                    args.Handled = true;
                    MinimizeSoon();
                }
            };

            _player.SyncRequested += OnSyncRequested;
            Navigate();
            controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
        }
        catch (Exception ex)
        {
            WebView2Runtime.Log($"Trailer player failed: {ex}");
            _player.ReportFailed(_session, ex);
        }
    }

    /// <summary>"state:&lt;seconds&gt;:&lt;1|0&gt;" from the player page: where it is, playing or paused.</summary>
    private void OnState(string message)
    {
        var parts = message.Split(':');
        if (parts.Length != 3 || !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var position))
            return;
        _player.ReportPlayback(_session, position, parts[2] == "1");
    }

    /// <summary>Watch along: tells the page where the friend's player is.</summary>
    private void OnSyncRequested(object? sender, PlaybackReport report)
    {
        if (!ReferenceEquals(report.Session, _session)) return;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var position = Math.Max(0, report.Position).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                _controller?.CoreWebView2.PostWebMessageAsString($"sync:{position}:{(report.Playing ? 1 : 0)}");
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // the player is closing
            }
        });
    }

    /// <summary>Shrinks the player into its corner after the browser's event returns.</summary>
    private void MinimizeSoon() => Dispatcher.UIThread.Post(() =>
    {
        if (ReferenceEquals(_player.Current, _session)) _player.MinimizeCommand.Execute(null);
    });

    /// <summary>Closes the overlay after the browser's event returns (closing it from inside its own event isn't safe).</summary>
    private void CloseSoon() => Dispatcher.UIThread.Post(() =>
    {
        if (ReferenceEquals(_player.Current, _session)) _player.Close();
    });

    private void Navigate()
    {
        if (_controller is null) return;
        var core = _controller.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping(PlayerHost, _session.PlayerFolder, CoreWebView2HostResourceAccessKind.Allow);
        core.Navigate($"https://{PlayerHost}/{Uri.EscapeDataString(_session.PageName)}");
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty) UpdateBrowserBounds();
    }

    /// <summary>The browser fills the child window, in physical pixels.</summary>
    private void UpdateBrowserBounds()
    {
        if (_controller is null) return;
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        _controller.Bounds = new System.Drawing.Rectangle(0, 0,
            Math.Max(1, (int)Math.Ceiling(Bounds.Width * scale)),
            Math.Max(1, (int)Math.Ceiling(Bounds.Height * scale)));
    }

    private void CloseBrowser()
    {
        _player.SyncRequested -= OnSyncRequested;
        var controller = _controller;
        _controller = null;
        if (controller is null) return;
        try
        {
            controller.Close(); // ends the video (and its sound) at once
        }
        catch (Exception ex)
        {
            WebView2Runtime.Log($"Closing the trailer player: {ex}");
        }
    }
}
