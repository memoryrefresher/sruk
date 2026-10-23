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

    // The player's sound through SeaRaider itself, for Discord streams (see PlayerSoundPipe)
    private CoreWebView2SharedBuffer? _soundBuffer;
    private PlayerSoundPipe? _soundPipe;
    private volatile bool _audible = true;

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

    /// <summary>
    /// One browser for every video, made once and kept: opening the next video only adds a view to it (quick, and no
    /// "already in use" errors from a browser that is still shutting down).
    /// </summary>
    private static Task<CoreWebView2Environment>? _environment;

    private static Task<CoreWebView2Environment> EnvironmentAsync()
    {
        if (_environment is { IsFaulted: true } or { IsCanceled: true }) _environment = null; // a failed start is tried again
        return _environment ??= Create();

        static async Task<CoreWebView2Environment> Create()
        {
            try
            {
                // No slowing down of the video's timers and drawing while another window has the focus.
                var options = new CoreWebView2EnvironmentOptions(
                    "--disable-web-security --autoplay-policy=no-user-gesture-required --disable-background-timer-throttling " +
                    "--disable-renderer-backgrounding");
                var profile = Path.Combine(AppPaths.DataDirectory, "webview2-player");
                Directory.CreateDirectory(profile);
                return await CoreWebView2Environment.CreateAsync(WebView2Runtime.FindFixedVersionFolder(), profile, options);
            }
            catch
            {
                _environment = null; // try again next time
                throw;
            }
        }
    }

    private async Task StartAsync()
    {
        try
        {
            CoreWebView2Environment environment;
            CoreWebView2Controller controller;
            try
            {
                environment = await EnvironmentAsync();
                if (_destroyed) return;
                controller = await environment.CreateCoreWebView2ControllerAsync(_hwnd);
            }
            catch (Exception first) when (first is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                // The browser closed under us (it can after the PC sleeps): start a fresh one, once.
                WebView2Runtime.Log($"Trailer player: retrying with a new browser ({first.Message})");
                _environment = null;
                await Task.Delay(300);
                if (_destroyed) return;
                environment = await EnvironmentAsync();
                controller = await environment.CreateCoreWebView2ControllerAsync(_hwnd);
            }
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
            core.Settings.IsSwipeNavigationEnabled = false;
            core.Settings.IsPinchZoomEnabled = false;
            core.Settings.IsBuiltInErrorPageEnabled = false;

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

            // Full screen (the player's own button, double-click, YouTube's button): the browser can't leave SeaRaider's window
            // by itself, so the window goes full screen with only the video in it, and comes back when the video does.
            core.ContainsFullScreenElementChanged += (_, _) =>
            {
                var full = core.ContainsFullScreenElement;
                Dispatcher.UIThread.Post(() =>
                {
                    if (ReferenceEquals(_player.Current, _session) || _player.Alongside.Contains(_session)) _player.SetFullScreen(full);
                    FocusBrowser();
                });
            };

            _player.SyncRequested += OnSyncRequested;
            _player.PropertyChanged += OnPlayerPropertyChanged;
            if (TopLevel.GetTopLevel(this) is WindowBase window)
            {
                _window = window;
                window.Activated += OnWindowActivated;
            }
            // The video sometimes keeps the size it had while the overlay was still opening (drawn small in the corner
            // until something resizes it): once the page has loaded, nudge the browser's size so it lays out again.
            core.NavigationCompleted += (_, _) =>
            {
                RelayoutSoon();
                FocusBrowser();
                ShareSoundBuffer(core);
            };
            // Whatever plays in the page or in a site's player inside it starts by itself (see AutoplayScript).
            await core.AddScriptToExecuteOnDocumentCreatedAsync(AutoplayScript);
            await core.AddScriptToExecuteOnDocumentCreatedAsync(LoadingScript);
            if (_destroyed) return;
            await StartSoundPipeAsync(environment, core);
            if (_destroyed)
            {
                StopSoundPipe();
                return;
            }
            UpdateSound();
            Navigate();
            FocusBrowser();
        }
        catch (Exception ex)
        {
            WebView2Runtime.Log($"Trailer player failed: {ex}");
            _player.ReportFailed(_session, ex);
        }
    }

    private WindowBase? _window;

    /// <summary>
    /// Runs in the player page and in every frame inside it (a site's own player): for the first 15 seconds, a video that
    /// hasn't started is started, and if it still won't, the player's own big play button is pressed (once or twice). It stops
    /// as soon as anything is playing, so it never fights a pause you make.
    /// </summary>
    private const string AutoplayScript = """
        (() => {
          if (window.__srAutoplay) return;
          window.__srAutoplay = true;
          const buttons = ['.vjs-big-play-button', '.jw-icon-display', '.jw-display-icon-container', '.plyr__control--overlaid',
            '.ytp-large-play-button', '.fp-play', '.mejs__overlay-button', '.art-control-play', '.dplayer-mobile-play',
            '.play-button', '.btn-play', '.playbtn', 'button[aria-label="Play"]', 'button[title="Play"]', '[data-plyr="play"]'];
          let tries = 0;
          const playing = () => Array.from(document.querySelectorAll('video')).some(v => !v.paused && v.currentTime > 0);
          const tick = () => {
            tries++;
            if (playing()) return;
            document.querySelectorAll('video').forEach(v => { if (v.paused && v.readyState >= 2) v.play().catch(() => {}); });
            if (tries === 3 || tries === 8) {
              for (const s of buttons) {
                const b = document.querySelector(s);
                if (b && b.offsetParent !== null) { try { b.click(); } catch (e) {} break; }
              }
            }
            if (tries < 15) setTimeout(tick, 1000);
          };
          if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', () => setTimeout(tick, 700));
          else setTimeout(tick, 700);
        })();
        """;

    /// <summary>
    /// While Discord runs (and Settings › Friends › Discord allows it), the video's sound is handed to SeaRaider and played from
    /// its own process, so a Discord stream of the window carries it. Any failure leaves the browser playing it as usual.
    /// </summary>
    private async Task StartSoundPipeAsync(CoreWebView2Environment environment, CoreWebView2 core)
    {
        try
        {
            if (!await Task.Run(() => _player.RouteSoundThroughApp)) return;
            if (_destroyed) return;
            _soundBuffer = environment.CreateSharedBuffer(PlayerSoundPipe.Size);
            _soundPipe = new PlayerSoundPipe(_soundBuffer.Buffer, () => _audible);
            await core.AddScriptToExecuteOnDocumentCreatedAsync("window.__srAppAudio = true;\n" + PlayerSoundPipe.Script);
        }
        catch (Exception ex)
        {
            WebView2Runtime.Log($"Player sound for Discord unavailable: {ex.Message}");
            StopSoundPipe();
        }
    }

    /// <summary>The player page gets the shared memory once it has loaded (again after each new page).</summary>
    private void ShareSoundBuffer(CoreWebView2 core)
    {
        if (_soundBuffer is null || _destroyed) return;
        try
        {
            core.PostSharedBufferToScript(_soundBuffer, CoreWebView2SharedBufferAccess.ReadWrite, "{}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            WebView2Runtime.Log($"Player sound for Discord: couldn't share the buffer ({ex.Message})");
        }
    }

    private void StopSoundPipe()
    {
        var pipe = _soundPipe;
        var buffer = _soundBuffer;
        _soundPipe = null;
        _soundBuffer = null;
        try
        {
            pipe?.Dispose(); // stops reading before the memory goes
            buffer?.Dispose();
        }
        catch (Exception ex)
        {
            WebView2Runtime.Log($"Player sound for Discord: {ex.Message}");
        }
    }

    /// <summary>
    /// "Loading..." fading into "Grab a beer while you wait.... if you earned it." and back, over the player until the video
    /// plays (in the page itself: nothing of SeaRaider's own can draw over the browser). A site's player inside a frame says
    /// when it starts playing; it's cleared after 25 seconds whatever happens.
    /// </summary>
    private const string LoadingScript = """
        (() => {
          if (window.__srLoading) return;
          window.__srLoading = true;
          if (window !== window.top) {
            document.addEventListener('playing', () => { try { window.top.postMessage({ __sr: 'playing' }, '*'); } catch (e) {} }, true);
            return;
          }
          const lines = ['Loading...', 'Grab a beer while you wait.... if you earned it.'];
          let box = null, text = null, timer = 0, step = 0, done = false;
          const show = () => {
            if (done) return;
            text.textContent = lines[Math.min(step, 1)];
            text.style.opacity = '1';
            timer = setTimeout(() => {
              text.style.opacity = '0';
              timer = setTimeout(() => { step = step === 0 ? 1 : 0; show(); }, 800);
            }, step === 0 ? 1500 : 2600);
          };
          const make = () => {
            if (box || done) return;
            box = document.createElement('div');
            box.style.cssText = 'position:fixed;inset:0;z-index:2147483647;display:flex;align-items:center;justify-content:center;' +
              'pointer-events:none;transition:opacity .6s ease';
            text = document.createElement('div');
            text.style.cssText = 'font:600 22px "Segoe UI",system-ui,sans-serif;color:#eef6fb;letter-spacing:.3px;' +
              'text-shadow:0 2px 14px rgba(0,0,0,.85);opacity:0;transition:opacity .75s ease;padding:10px 22px;text-align:center';
            box.appendChild(text);
            (document.body || document.documentElement).appendChild(box);
            requestAnimationFrame(show);
          };
          const finish = () => {
            if (done) return;
            done = true;
            clearTimeout(timer);
            if (box) { box.style.opacity = '0'; setTimeout(() => box && box.remove(), 700); }
          };
          document.addEventListener('playing', finish, true);
          window.addEventListener('message', e => { if (e.data && e.data.__sr === 'playing') finish(); });
          if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', make); else make();
          setTimeout(finish, 25000);
        })();
        """;

    /// <summary>Multi-view: only the stream you chose plays sound.</summary>
    private void UpdateSound()
    {
        _audible = _player.IsAudible(_session);
        try
        {
            if (_controller?.CoreWebView2 is { } core) core.IsMuted = !_audible;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // closing
        }
    }

    /// <summary>
    /// Keyboard and mouse go to the video at once. SeaRaider's window takes the focus back whenever it's activated (a click on
    /// the video from another app, or after the chat had it), which made the first click on the player's buttons do nothing.
    /// </summary>
    private void FocusBrowser()
    {
        if (_controller is null || _destroyed || _player.IsMinimized && !_player.IsFullScreen) return;
        if (!_player.IsAudible(_session)) return; // multi-view: the keys go to the stream you're listening to
        try
        {
            _controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // closing
        }
    }

    private void OnWindowActivated(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        // Typing in the chat (or anywhere else): that keeps the keys.
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox) return;
        FocusBrowser();
    }, DispatcherPriority.Input);

    private void OnPlayerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TrailerPlayerService.SoundFrom) or nameof(TrailerPlayerService.Current))
        {
            UpdateSound();
            return;
        }
        if (e.PropertyName == nameof(TrailerPlayerService.IsMinimized) && !_player.IsMinimized)
            Dispatcher.UIThread.Post(FocusBrowser, DispatcherPriority.Input); // back to full size: the video takes the keys again
        else if (e.PropertyName == nameof(TrailerPlayerService.IsFullScreen) && !_player.IsFullScreen)
        {
            // Full screen ended from SeaRaider's side (F11, minimize): the page leaves its full screen too.
            try
            {
                if (_controller?.CoreWebView2 is { ContainsFullScreenElement: true } core)
                    _ = core.ExecuteScriptAsync("document.fullscreenElement && document.exitFullscreen().catch(() => {})");
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // closing
            }
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
        if (ReferenceEquals(_player.Current, _session) || _player.Alongside.Contains(_session)) _player.MinimizeCommand.Execute(null);
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
        try
        {
            _controller.Bounds = new System.Drawing.Rectangle(0, 0,
                Math.Max(1, (int)Math.Ceiling(Bounds.Width * scale)),
                Math.Max(1, (int)Math.Ceiling(Bounds.Height * scale)));
            _controller.NotifyParentWindowPositionChanged(); // the window moved or resized too: the video layer follows
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // closing
        }
    }

    /// <summary>
    /// Shrinks the browser by a pixel and back a moment later, a few times while the video starts: the page (and the video's
    /// own drawing layer) lays out again at the full size of the box.
    /// </summary>
    private void RelayoutSoon()
    {
        foreach (var ms in new[] { 200, 1500 })
        {
            DispatcherTimer.RunOnce(() =>
            {
                if (_controller is null || _destroyed) return;
                var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
                var width = Math.Max(2, (int)Math.Ceiling(Bounds.Width * scale));
                var height = Math.Max(2, (int)Math.Ceiling(Bounds.Height * scale));
                try
                {
                    _controller.Bounds = new System.Drawing.Rectangle(0, 0, width - 1, height);
                    DispatcherTimer.RunOnce(UpdateBrowserBounds, TimeSpan.FromMilliseconds(40));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
                {
                    // closing
                }
            }, TimeSpan.FromMilliseconds(ms));
        }
    }

    private void CloseBrowser()
    {
        _player.SyncRequested -= OnSyncRequested;
        _player.PropertyChanged -= OnPlayerPropertyChanged;
        if (_window is not null) _window.Activated -= OnWindowActivated;
        _window = null;
        if (ReferenceEquals(_player.Current, _session) || _player.Current is null) _player.SetFullScreen(false);
        var controller = _controller;
        _controller = null;
        if (controller is null)
        {
            StopSoundPipe();
            return;
        }
        try
        {
            controller.Close(); // ends the video (and its sound) at once
        }
        catch (Exception ex)
        {
            WebView2Runtime.Log($"Closing the trailer player: {ex}");
        }
        StopSoundPipe();
    }
}
