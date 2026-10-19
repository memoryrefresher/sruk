using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Web.WebView2.Core;
using SeaRaider.App.Services;
using SeaRaider.App.Verification;
using SeaRaider.Core;

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
/// </summary>
public sealed class TrailerPlayerHost : NativeControlHost
{
    private const string PlayerHost = TrailerPlayerService.PlayerHost;

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

            core.AddWebResourceRequestedFilter("*steamstatic.com*", CoreWebView2WebResourceContext.All);
            // Steam's video servers serve the store's own pages: present the player's requests the same way.
            core.WebResourceRequested += (_, args) =>
            {
                args.Request.Headers.SetHeader("Referer", "https://store.steampowered.com/");
                if (args.Request.Headers.Contains("Origin")) args.Request.Headers.SetHeader("Origin", "https://store.steampowered.com");
            };

            // Only the player page itself; anything else goes to the default browser.
            core.NavigationStarting += (_, args) =>
            {
                if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var target) && target.Host == PlayerHost) return;
                args.Cancel = true;
                if (target is not null) _player.OpenExternal(target.AbsoluteUri);
            };
            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                _player.OpenExternal(args.Uri);
            };
            core.WebMessageReceived += (_, args) =>
            {
                var message = args.TryGetWebMessageAsString();
                if (message == "close") CloseSoon();
                else if (message?.StartsWith("open:", StringComparison.Ordinal) == true) _player.OpenExternal(message[5..]);
                else if (message?.StartsWith("state:", StringComparison.Ordinal) == true) OnState(message);
            };
            // Esc closes the player even while the YouTube frame has focus (unless a video is fullscreen).
            controller.AcceleratorKeyPressed += (_, args) =>
            {
                if (args.KeyEventKind == CoreWebView2KeyEventKind.KeyDown && args.VirtualKey == 0x1B && !core.ContainsFullScreenElement)
                {
                    args.Handled = true;
                    CloseSoon();
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
