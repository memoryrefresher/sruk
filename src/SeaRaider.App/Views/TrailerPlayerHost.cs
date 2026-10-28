using System.Text.Json;
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
    private readonly List<CoreWebView2Frame> _frames = [];
    private volatile bool _assistPlaying;

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

    /// <summary>Settings › Media › "Use the graphics card": read when the player's browser starts (once per run).</summary>
    private static bool _hardwareDecoding;

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
                    "--disable-renderer-backgrounding" +
                    (_hardwareDecoding ? " --ignore-gpu-blocklist --enable-gpu-rasterization --enable-zero-copy --enable-features=D3D11VideoDecoder" : ""));
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
                _hardwareDecoding = _player.HardwareDecoding;
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
            var pageMode = PageAssist.Get(_session.Url) is not null;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, args) =>
            {
                var url = args.Request.Uri;
                // Ad networks and ad-server requests (a "/pagead/" folder, a video-ad file …) get nothing, on a site's page too:
                // no ad script runs, so none can open a window or cover the player. A page mode block is logged, so a link that
                // goes black for want of one shows which address it was.
                if (AdHosts.IsAdRequest(url))
                {
                    args.Response = environment.CreateWebResourceResponse(null, 403, "Blocked", "Content-Type: text/plain");
                    if (pageMode) WebView2Runtime.Log($"Page mode blocked: {url}");
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
                if (AdHosts.IsAdRequest(args.Uri)) args.Cancel = true;
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
            core.FrameCreated += (_, args) => TrackFrame(args.Frame); // the page's own frames, for pressing its buttons
            core.WebMessageReceived += (_, args) =>
            {
                var message = args.TryGetWebMessageAsString();
                if (message == "close") MinimizeSoon(); // Esc in the page: into the corner, still playing
                else if (message == "playing")
                {
                    _assistPlaying = true; // a video started somewhere in the page (see RunAssistAsync)
                    if (PageAssist.Get(_session.Url) is { } playing)
                    {
                        _ = TheaterAsync();
                        if (playing.Link == 0 && _player.SwitchOnStall && !_watching) _ = WatchAsync(core);
                    }
                }
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
            // No ad gets onto the page or into a player frame: no pop-ups, no click-catching layers over the video, no ad boxes
            // (see AdShield). It runs before the page's own scripts, in every frame.
            await SeaRaider.App.Services.AdShield.InstallAsync(core, SeaRaider.App.Services.AdShieldMode.Player);
            // Whatever plays in the page or in a site's player inside it starts by itself (see AutoplayScript).
            await core.AddScriptToExecuteOnDocumentCreatedAsync(AutoplayScript);
            if (!_session.IsGameTrailer) await core.AddScriptToExecuteOnDocumentCreatedAsync(LoadingScript); // streams only
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
            // A site's page played whole: pick the link and press play for the user (see PageAssist).
            if (PageAssist.Get(_session.Url) is { } plan) _ = RunAssistAsync(core, plan);
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
          // Every video starts at 10 % volume (turn it up from there; that stays for this video).
          const quiet = e => {
            const v = e.target;
            if (!(v instanceof HTMLMediaElement) || v.__srVolume) return;
            v.__srVolume = true;
            try { v.volume = 0.1; } catch (x) {}
          };
          document.addEventListener('loadedmetadata', quiet, true);
          document.addEventListener('play', quiet, true);
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
          // "Loading..." → "Grab a beer while you wait.... if you earned it." (once) → "Loading..." until it plays.
          let box = null, text = null, timer = 0, done = false;
          const say = (words, then, hold) => {
            if (done) return;
            text.textContent = words;
            text.style.opacity = '1';
            if (then) timer = setTimeout(() => { text.style.opacity = '0'; timer = setTimeout(then, 800); }, hold);
          };
          const start = () => say('Loading...', () => say('Grab a beer while you wait.... if you earned it.', () => say('Loading...'), 2800), 1600);
          const make = () => {
            if (box || done) return;
            box = document.createElement('div');
            box.style.cssText = 'position:fixed;inset:0;z-index:2147483647;display:flex;align-items:flex-end;justify-content:center;' +
              'padding-bottom:17vh;pointer-events:none;transition:opacity .6s ease';
            text = document.createElement('div');
            text.style.cssText = 'font:600 22px "Segoe UI",system-ui,sans-serif;color:#eef6fb;letter-spacing:.3px;' +
              'text-shadow:0 2px 14px rgba(0,0,0,.85);opacity:0;transition:opacity .75s ease;padding:10px 22px;text-align:center';
            box.appendChild(text);
            (document.body || document.documentElement).appendChild(box);
            requestAnimationFrame(start);
          };
          const finish = () => {
            if (done) return;
            done = true;
            clearTimeout(timer);
            try { window.chrome.webview.postMessage('playing'); } catch (e) {}
            if (box) { box.style.opacity = '0'; setTimeout(() => box && box.remove(), 700); }
          };
          document.addEventListener('playing', finish, true);
          window.addEventListener('message', e => { if (e.data && e.data.__sr === 'playing') finish(); });
          if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', make); else make();
          setTimeout(finish, 60000);
        })();
        """;


    // ---- A site's page played whole: pick its link and press play (the user's own hands, automated) ----

    /// <summary>The page's numbered links ("Link-1 HD", "Link 2" …), seen from the page's own frame. Returns { links: [1, 2] }.</summary>
    private const string AssistLinksScript = """
        (() => {
          if (window.parent !== window.top) return null;
          const re = /^\s*link[\s\-_#]*0*(\d{1,2})(?!\d)/i;
          const nums = new Set();
          // The page's own list of streams (allStreams: [{ label: "Link-1 HD", value: "https://..." }, ...]), when it has one.
          try {
            if (typeof allStreams !== 'undefined' && Array.isArray(allStreams))
              allStreams.forEach((s, i) => { const m = /(\d{1,2})/.exec(String((s && s.label) || '')); nums.add(m ? Number(m[1]) : i + 1); });
          } catch (e) {}
          for (const e of document.querySelectorAll('a, button, li, div, span, label, [role=button], [data-link]')) {
            const t = (e.textContent || '').replace(/\s+/g, ' ').trim();
            if (t.length < 40) { const m = re.exec(t); if (m) nums.add(Number(m[1])); }
          }
          return { links: [...nums].sort((a, b) => a - b) };
        })()
        """;

    /// <summary>
    /// Switches the page to link number __N__ the way its own buttons do (its createIframe over its allStreams), when the page
    /// has them. Returns true when it did.
    /// </summary>
    private const string AssistSelectScript = """
        (() => {
          if (window.parent !== window.top) return false;
          try {
            if (typeof allStreams === 'undefined' || typeof createIframe !== 'function') return false;
            const index = allStreams.findIndex((s, i) => { const m = /(\d{1,2})/.exec(String((s && s.label) || '')); return (m ? Number(m[1]) : i + 1) === __N__; });
            if (index < 0) return false;
            if (typeof currentActiveIndex !== 'undefined') currentActiveIndex = index;
            if (typeof renderLinks === 'function') renderLinks();
            createIframe(allStreams[index]);
            return true;
          } catch (e) { return false; }
        })()
        """;

    /// <summary>Where link number __N__ is (its centre, scrolled into view), or null.</summary>
    private const string AssistLinkScript = """
        (() => {
          if (window.parent !== window.top) return null;
          const re = new RegExp('^\\s*link[\\s\\-_#]*0*' + __N__ + '(?!\\d)', 'i');
          const all = [...document.querySelectorAll('a, button, li, div, span, label, [role=button], [data-link]')]
            .filter(e => { const t = (e.textContent || '').replace(/\s+/g, ' ').trim(); return t.length < 40 && re.test(t); });
          const el = all.find(e => !all.some(o => o !== e && e.contains(o)));
          if (!el) return null;
          try { el.scrollIntoView({ block: 'center' }); } catch (e) {}
          const b = el.getBoundingClientRect();
          return { x: Math.round(b.left + b.width / 2), y: Math.round(b.top + b.height / 2) };
        })()
        """;

    /// <summary>
    /// Where the player is on the page (its centre): the user's own selector or text (the last of their steps, __STEPS__), else
    /// the player box, else the biggest video or frame. Null when the page has none yet.
    /// </summary>
    private const string AssistPlayerScript = """
        (() => {
          if (window.parent !== window.top) return null;
          const steps = __STEPS__;
          const big = e => { const r = e.getBoundingClientRect(); return r.width >= 120 && r.height >= 80; };
          const byText = t => {
            const needle = t.toLowerCase().replace(/\s+/g, ' ');
            const all = [...document.querySelectorAll('a, button, li, div, span, label, [role=button]')]
              .filter(e => { const x = (e.textContent || '').replace(/\s+/g, ' ').trim().toLowerCase(); return x.length < 60 && x.includes(needle); });
            return all.find(e => !all.some(o => o !== e && e.contains(o))) || null;
          };
          let el = null;
          if (steps.length) {
            const s = steps[steps.length - 1];
            if (s.toLowerCase().startsWith('text:')) el = byText(s.slice(5).trim());
            else { try { el = document.querySelector(s); } catch (e) {} }
          }
          if (!el || !big(el)) {
            el = null;
            for (const sel of ['#videoPlayer', '[class*="iframe-placeholder"]', '.player-container video', '.player-container iframe', 'video', 'iframe']) {
              const found = [...document.querySelectorAll(sel)].find(big);
              if (found) { el = found; break; }
            }
          }
          if (!el) return null;
          try { el.scrollIntoView({ block: 'center' }); } catch (e) {}
          const b = el.getBoundingClientRect();
          return { x: Math.round(b.left + b.width / 2), y: Math.round(b.top + b.height / 2) };
        })()
        """;

    /// <summary>
    /// Makes a site's page played whole look like a plain player: everything on the page except the player is hidden and the
    /// player fills the box (so it behaves like any other video here, side by side in multi-view too).
    /// </summary>
    private const string TheaterScript = """
        (() => {
          if (window.parent !== window.top) return false;
          const pick = () => {
            for (const sel of ['#videoPlayer', '[class*="iframe-placeholder"]', '.player-container', 'video', 'iframe']) {
              const found = [...document.querySelectorAll(sel)].find(e => { const r = e.getBoundingClientRect(); return r.width >= 120 && r.height >= 80; });
              if (found) return found;
            }
            return null;
          };
          const player = pick();
          if (!player) return false;
          if (!document.getElementById('sr-theater')) {
            const st = document.createElement('style');
            st.id = 'sr-theater';
            st.textContent =
              'html,body{margin:0!important;padding:0!important;overflow:hidden!important;background:#000!important}' +
              '[data-sr-hide]{display:none!important}' +
              '[data-sr-keep]{transform:none!important;filter:none!important;margin:0!important;padding:0!important;border:0!important;max-width:none!important}' +
              '[data-sr-player]{position:fixed!important;left:0!important;top:0!important;width:100vw!important;height:100vh!important;z-index:2147483647!important;background:#000!important;border-radius:0!important}' +
              '[data-sr-player] iframe,[data-sr-player] video{width:100%!important;height:100%!important;border:0!important;border-radius:0!important}';
            (document.head || document.documentElement).appendChild(st);
          }
          player.setAttribute('data-sr-player', '1');
          for (let n = player; n && n !== document.body; n = n.parentElement) {
            n.setAttribute('data-sr-keep', '1');
            const parent = n.parentElement;
            if (!parent) break;
            for (const sib of parent.children) if (sib !== n && !/^(SCRIPT|STYLE|LINK)$/.test(sib.tagName)) sib.setAttribute('data-sr-hide', '1');
          }
          return true;
        })()
        """;

    /// <summary>A black "Loading..." cover over the site's page while SeaRaider picks the link and presses play (clicks pass through it).</summary>
    private const string CoverScript = """
        (() => {
          if (window.parent !== window.top) return false;
          if (!document.getElementById('sr-cover')) {
            const d = document.createElement('div');
            d.id = 'sr-cover';
            d.style.cssText = 'position:fixed;left:0;top:0;width:100vw;height:100vh;background:#0a0a0a;z-index:2147483647;pointer-events:none;';
            (document.body || document.documentElement).appendChild(d);
          }
          return true;
        })()
        """;

    private const string UncoverScript = """
        (() => {
          if (window.parent !== window.top) return false;
          const c = document.getElementById('sr-cover');
          if (c) c.remove();
          return true;
        })()
        """;

    private bool _theater;

    // ---- Settings › Media › "Switch to the next link when the video keeps stalling" (off unless switched on) ----

    private List<int> _assistLinks = [];
    private int _assistLink;
    private bool _watching;

    /// <summary>The page's biggest video as { has, t, paused, ended, ready }, from the frame it is in.</summary>
    private const string StallProbeScript = """
        (() => {
          const v = [...document.querySelectorAll('video')].sort((a, b) => b.videoWidth - a.videoWidth)[0];
          if (!v || !v.videoWidth) return null;
          return { t: v.currentTime, paused: v.paused, ended: v.ended, ready: v.readyState };
        })()
        """;

    private async Task<(double T, bool Paused, bool Ended, int Ready)?> ProbeVideoAsync()
    {
        foreach (var frame in _frames.ToList())
        {
            try
            {
                using var json = JsonDocument.Parse(await frame.ExecuteScriptAsync(StallProbeScript));
                var r = json.RootElement;
                if (r.ValueKind == JsonValueKind.Object)
                    return (r.GetProperty("t").GetDouble(), r.GetProperty("paused").GetBoolean(), r.GetProperty("ended").GetBoolean(), r.GetProperty("ready").GetInt32());
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException or KeyNotFoundException)
            {
                // a frame that went away or has no video
            }
        }
        return null;
    }

    /// <summary>
    /// Watches the playing video; when it has not moved for about ten seconds (and is not paused), moves to the page's next link
    /// and presses play. At most six switches, a quarter of a minute apart.
    /// </summary>
    private async Task WatchAsync(CoreWebView2 core)
    {
        _watching = true;
        try
        {
            var last = -1.0;
            var stalledSeconds = 0;
            var switches = 0;
            var lastSwitch = DateTime.UtcNow;
            while (!_destroyed && switches < 6)
            {
                await Task.Delay(2000);
                if (await ProbeVideoAsync() is not { } v) continue;
                var stalled = !v.Paused && !v.Ended && (v.Ready < 3 || Math.Abs(v.T - last) < 0.01);
                last = v.T;
                stalledSeconds = stalled ? stalledSeconds + 2 : 0;
                if (stalledSeconds < 10 || DateTime.UtcNow - lastSwitch < TimeSpan.FromSeconds(15) || _assistLinks.Count < 2) continue;

                var next = _assistLinks.FirstOrDefault(n => n > _assistLink);
                if (next == 0) next = _assistLinks.First();
                if (next == _assistLink) continue;
                WebView2Runtime.Log($"Page mode: video stalled on link {_assistLink}, switching to link {next}");
                var number = next.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!await AssistSelectAsync(AssistSelectScript.Replace("__N__", number, StringComparison.Ordinal)) &&
                    await AssistPointAsync(AssistLinkScript.Replace("__N__", number, StringComparison.Ordinal)) is { } at)
                    await ClickAtAsync(core, at.X, at.Y);
                _assistLink = next;
                switches++;
                lastSwitch = DateTime.UtcNow;
                stalledSeconds = 0;
                last = -1;
                _theater = false;
                await Task.Delay(1500);
                await TheaterAsync();
                for (var i = 0; i < 2 && !_destroyed; i++)
                {
                    if (await AssistPointAsync(AssistPlayerScript.Replace("__STEPS__", "[]", StringComparison.Ordinal)) is { } p) await ClickAtAsync(core, p.X, p.Y);
                    await Task.Delay(1500);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            WebView2Runtime.Log($"Page mode watch stopped: {ex.Message}");
        }
        finally
        {
            _watching = false;
        }
    }

    private async Task TheaterAsync()
    {
        if (_theater) return;
        _theater = true;
        try
        {
            // once now, and again a moment later: the page swaps its player's content when a link changes
            for (var i = 0; i < 2 && !_destroyed; i++)
            {
                await AssistSelectAsync(TheaterScript);
                await Task.Delay(1500);
            }
            await AssistSelectAsync(UncoverScript);
            WebView2Runtime.Log("Page mode: player now fills the box");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            WebView2Runtime.Log($"Page mode could not fill the box: {ex.Message}");
        }
    }

    private void TrackFrame(CoreWebView2Frame frame)
    {
        _frames.Add(frame);
        frame.Destroyed += (_, _) => _frames.Remove(frame);
        frame.FrameCreated += (_, args) => TrackFrame(args.Frame);
    }

    /// <summary>
    /// What a person does on these pages, done for them: wait for the page's links, pick the link, then press the player a few
    /// times with real clicks (the first click on many sites only opens a pop-up, which is blocked), until a video plays. With
    /// no link chosen, each of the page's links is tried in turn; a link the user chose is the only one tried.
    /// </summary>
    private async Task RunAssistAsync(CoreWebView2 core, PageAssistPlan plan)
    {
        try
        {
            _assistPlaying = false;
            WebView2Runtime.Log($"Page mode started on {_session.Url} (start link {plan.Link})");
            var steps = JsonSerializer.Serialize((plan.PlayerSteps ?? string.Empty).Split(">>", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
            var playerScript = AssistPlayerScript.Replace("__STEPS__", steps, StringComparison.Ordinal);
            var finish = DateTime.UtcNow + TimeSpan.FromSeconds(70);

            async Task<bool> PlayingWithinAsync(double seconds)
            {
                var until = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
                while (DateTime.UtcNow < until && !_destroyed)
                {
                    if (_assistPlaying) return true;
                    await Task.Delay(250);
                }
                return _assistPlaying;
            }

            // The page's frame needs a moment to load its links and player.
            var links = new List<int>();
            for (var i = 0; i < 24 && !_destroyed && !_assistPlaying; i++)
            {
                await Task.Delay(500);
                await AssistSelectAsync(CoverScript); // the page is hidden behind "Loading..." while the link is picked and play pressed
                links = await AssistLinksAsync();
                if (links.Count > 0 || await AssistPointAsync(playerScript) is not null) break;
            }
            if (links.Count > 0) SeaRaider.Core.Scraping.PageAssist.SetLinkCount(_session.Url, links.Max());
            WebView2Runtime.Log($"Page mode links seen: [{string.Join(",", links)}], frames: {_frames.Count}");
            // Automatic: Link 2, 3 … first, Link 1 last (it often stays black), so it starts on a link that plays.
            _assistLinks = links;
            var order = plan.Link > 0 ? new List<int> { plan.Link } : links.Count > 0 ? links.OrderBy(n => n == 1 ? 1 : 0).ThenBy(n => n).Take(6).ToList() : new List<int> { 0 };

            foreach (var link in order)
            {
                if (_destroyed || _assistPlaying || DateTime.UtcNow > finish) return;
                WebView2Runtime.Log($"Page mode trying link {link}");
                _assistLink = link;
                // The page starts on Link 1 by itself.
                if (link > 0 && (link != 1 || plan.Link > 0))
                {
                    var number = link.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    // Click the page's own button like a person (this is what worked); its own function is only the backup.
                    if (await AssistPointAsync(AssistLinkScript.Replace("__N__", number, StringComparison.Ordinal)) is { } at)
                    {
                        await ClickAtAsync(core, at.X, at.Y);
                        await Task.Delay(1500);
                    }
                    else if (await AssistSelectAsync(AssistSelectScript.Replace("__N__", number, StringComparison.Ordinal)))
                    {
                        await Task.Delay(1500);
                    }
                }
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    if (_destroyed || _assistPlaying) return;
                    if (await AssistPointAsync(playerScript) is { } player) await ClickAtAsync(core, player.X, player.Y);
                    if (await PlayingWithinAsync(2.2)) { WebView2Runtime.Log($"Page mode: playing on link {link}"); return; }
                }
                if (await PlayingWithinAsync(2.5) || plan.Link > 0) return; // a link the user chose is the only one tried
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or JsonException or TaskCanceledException or ArgumentException)
        {
            WebView2Runtime.Log($"Page assist stopped: {ex.Message}");
        }
        finally
        {
            // Nothing played: show the page again so the user can pick a link and press play themselves.
            if (!_assistPlaying && !_destroyed)
            {
                try { await AssistSelectAsync(UncoverScript); }
                catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException) { }
            }
        }
    }

    /// <summary>Runs a script in the page's frames until one says true.</summary>
    private async Task<bool> AssistSelectAsync(string script)
    {
        foreach (var frame in _frames.ToList())
        {
            try
            {
                if (await frame.ExecuteScriptAsync(script) == "true") return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException)
            {
                // a frame that went away or isn't ready
            }
        }
        return false;
    }

    private async Task<List<int>> AssistLinksAsync()
    {
        var links = new SortedSet<int>();
        foreach (var frame in _frames.ToList())
        {
            try
            {
                using var json = JsonDocument.Parse(await frame.ExecuteScriptAsync(AssistLinksScript));
                if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("links", out var list) && list.ValueKind == JsonValueKind.Array)
                    foreach (var n in list.EnumerateArray()) if (n.TryGetInt32(out var number)) links.Add(number);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException)
            {
                // a frame that went away or isn't ready
            }
        }
        return links.ToList();
    }

    /// <summary>The first point a script gives in the page's frames ({ x, y } in the page's own coordinates), or null.</summary>
    private async Task<(int X, int Y)?> AssistPointAsync(string script)
    {
        foreach (var frame in _frames.ToList())
        {
            try
            {
                using var json = JsonDocument.Parse(await frame.ExecuteScriptAsync(script));
                var root = json.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("x", out var x) && root.TryGetProperty("y", out var y) &&
                    x.TryGetInt32(out var px) && y.TryGetInt32(out var py))
                    return (px, py);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException)
            {
                // a frame that went away or isn't ready
            }
        }
        return null;
    }

    /// <summary>A real left click at a point of the page, as a person's mouse would make it.</summary>
    private static async Task ClickAtAsync(CoreWebView2 core, int x, int y)
    {
        foreach (var type in new[] { "mouseMoved", "mousePressed", "mouseReleased" })
        {
            var parameters = JsonSerializer.Serialize(new { type, x, y, button = type == "mouseMoved" ? "none" : "left", clickCount = type == "mouseMoved" ? 0 : 1 });
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", parameters);
        }
    }

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
