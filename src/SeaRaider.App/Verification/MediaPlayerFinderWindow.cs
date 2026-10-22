using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SeaRaider.Core.Scraping;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace SeaRaider.App.Verification;

/// <summary>
/// Finds a title's player by doing what a person does on the title's page: it opens the page in a hidden browser, clicks the
/// chosen stream button ("Stream 1", "Server 1" …) and hands back the player that loads. Used for pages that only put their
/// player on the page after that click (Settings › General › Media player).
///
/// <list type="bullet">
///   <item>The player is noticed however it loads: as a frame (even a frame inside a frame), in a new window the button opens,
///         or by the button sending the page to it. With preferred player sites set ("embed.st"), only a player on one of
///         them counts; otherwise the first player from another site that loads after the click.</item>
///   <item>Ads get nothing: ad networks' requests are blocked, pop-ups never open (the first click on many sites only opens
///         one, so the button is clicked again), downloads and notification prompts are refused.</item>
///   <item>Invisible throughout (opacity 0, no taskbar button). Gives up after <see cref="GiveUpAfter"/>: the result is null
///         and the caller falls back to the page's own player.</item>
/// </list>
/// Runs on its own STA thread (see <see cref="WebView2ChallengeResolver"/>), in the verified profile, so a site that was
/// verified for downloads opens here too.
/// </summary>
internal sealed class MediaPlayerFinderWindow : WinForms.Form
{
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(1.2);
    private static readonly TimeSpan ClickEvery = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(25);
    private const int MaxClicks = 4;

    /// <summary>Looks for the player among the page's frames, else clicks the stream button. Returns { url } or { clicked }.</summary>
    private const string FindOrClickScript = """
        (() => {
          const P = __PARAMS__;
          const hostOf = u => { try { return new URL(u, location.href).hostname.toLowerCase(); } catch (e) { return ''; } };
          const preferred = u => { const h = hostOf(u); return h !== '' && P.hosts.some(x => h === x || h.endsWith('.' + x)); };
          const trailer = u => /youtube\.com|youtu\.be|youtube-nocookie|vimeo\.com/i.test(u);
          const frames = [...document.querySelectorAll('iframe')]
            .map(f => ({ el: f, src: f.getAttribute('src') || f.getAttribute('data-src') || '' }))
            .filter(f => f.src && !/^(about|javascript|data):/i.test(f.src));

          if (P.hosts.length) {
            const hit = frames.find(f => preferred(f.src));
            if (hit) return { url: new URL(hit.src, location.href).href };
          } else if (P.clicks > 0) {
            // No preferred site: the biggest player frame from another site, once the button has been clicked.
            const own = location.hostname.toLowerCase();
            const big = frames
              .filter(f => hostOf(f.src) !== own && !trailer(f.src) && f.el.offsetWidth >= 200 && f.el.offsetHeight >= 120)
              .sort((a, b) => b.el.offsetWidth * b.el.offsetHeight - a.el.offsetWidth * a.el.offsetHeight)[0];
            if (big) return { url: new URL(big.src, location.href).href };
          }

          if (P.n <= 0 || P.clicks >= P.maxClicks) return { clicked: false };
          const label = /^\s*(?:stream|server|source|link|mirror|player|option|host|sv|s)\s*[#:\-]?\s*(\d{1,2})(?!\d)/i;
          const all = [...document.querySelectorAll('a, button, li, option, span, div, label, [role=button], [data-src], [data-url], [data-embed], [data-link]')];
          const matching = all.filter(e => {
            const t = (e.textContent || '').replace(/\s+/g, ' ').trim();
            const m = label.exec(t);
            return t.length > 0 && t.length < 40 && m && Number(m[1]) === P.n;
          });
          const button = matching.find(e => !matching.some(o => o !== e && e.contains(o)));
          if (!button) return { clicked: false };
          try { button.scrollIntoView({ block: 'center' }); } catch (e) {}
          if (button.tagName === 'OPTION' && button.parentElement) {
            button.selected = true;
            button.parentElement.dispatchEvent(new Event('change', { bubbles: true }));
          } else {
            for (const type of ['pointerdown', 'mousedown', 'pointerup', 'mouseup'])
              button.dispatchEvent(new MouseEvent(type, { bubbles: true, cancelable: true, view: window }));
            button.click();
          }
          return { clicked: true };
        })()
        """;

    private readonly Uri _page;
    private readonly PlayerPreference _preference;
    private readonly string _userDataFolder;
    private readonly TaskCompletionSource<string?> _result;
    private readonly WebView2 _webView = new() { Dock = WinForms.DockStyle.Fill };
    private readonly WinForms.Timer _pollTimer = new() { Interval = 600 };
    private DateTime _startedUtc = DateTime.UtcNow;
    private DateTime _lastNavigationUtc = DateTime.UtcNow;
    private DateTime _lastClickUtc = DateTime.MinValue;
    private int _clicks;
    private bool _busy;
    private bool _done;

    public MediaPlayerFinderWindow(Uri page, PlayerPreference preference, string userDataFolder, TaskCompletionSource<string?> result)
    {
        _page = page;
        _preference = preference;
        _userDataFolder = userDataFolder;
        _result = result;

        Text = $"SeaRaider · {page.Host}";
        ShowInTaskbar = false;
        Opacity = 0;
        StartPosition = WinForms.FormStartPosition.Manual;
        Location = new Drawing.Point(-32000, -32000); // never on screen, even for a moment
        Size = new Drawing.Size(1280, 800);           // a desktop-sized page, so the buttons are laid out as usual
        Controls.Add(_webView);

        _pollTimer.Tick += async (_, _) => await TickAsync();
        FormClosed += (_, _) => _result.TrySetResult(null);
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _startedUtc = DateTime.UtcNow;
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(WebView2Runtime.FindFixedVersionFolder(), _userDataFolder,
                SeaRaider.App.Services.BackgroundThrottling.CreateWebView2Options());
            await _webView.EnsureCoreWebView2Async(environment);

            var core = _webView.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.IsMuted = true; // a player that starts playing here must not be heard

            // Every request from the page and its frames: ads blocked; the player noticed as soon as it's asked for.
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, args) =>
            {
                var url = args.Request.Uri;
                if (AdHosts.IsAd(url))
                {
                    args.Response = environment.CreateWebResourceResponse(null, 403, "Blocked", "Content-Type: text/plain");
                    return;
                }
                if (args.ResourceContext == CoreWebView2WebResourceContext.Document && _preference.Hosts.Count > 0 && _preference.Matches(url))
                    Complete(url);
            };
            core.NavigationStarting += (_, args) =>
            {
                _lastNavigationUtc = DateTime.UtcNow;
                if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var target)) return;
                if (_preference.Hosts.Count > 0 && _preference.Matches(target))
                {
                    args.Cancel = true; // the button sent the page to the player: that's the player
                    Complete(target.AbsoluteUri);
                }
                else if (AdHosts.IsAd(target))
                {
                    args.Cancel = true; // sent to an ad instead of the stream
                }
                else if (_clicks > 0 && _preference.Hosts.Count == 0 && !SameSite(target, _page) && target.Scheme is "http" or "https")
                {
                    args.Cancel = true; // no preferred site: the stream button led to another site's player page
                    Complete(target.AbsoluteUri);
                }
                else if (_clicks > 0 && !SameSite(target, _page))
                {
                    args.Cancel = true; // preferred sites set, and this isn't one: an ad or a detour
                }
            };
            core.NavigationCompleted += (_, _) => _lastNavigationUtc = DateTime.UtcNow;
            core.FrameNavigationStarting += (_, args) =>
            {
                if (AdHosts.IsAd(args.Uri)) args.Cancel = true;
            };
            // Pop-ups never open; a "stream" button that opens its player in a new window hands it over here.
            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (_preference.Hosts.Count > 0 && _preference.Matches(args.Uri)) Complete(args.Uri);
            };
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;

            core.Navigate(_page.AbsoluteUri);
            _pollTimer.Start();
        }
        catch (Exception ex)
        {
            WebView2Runtime.Log($"Media player finder for {_page.Host} couldn't start: {ex}");
            _result.TrySetResult(null);
            Close();
        }
    }

    private async Task TickAsync()
    {
        if (_done || _busy || _webView.CoreWebView2 is not { } core) return;
        if (DateTime.UtcNow - _startedUtc > GiveUpAfter)
        {
            WebView2Runtime.Log($"Media player finder for {_page.Host}: no player after {GiveUpAfter.TotalSeconds:0}s ({_clicks} clicks)");
            Complete(null);
            return;
        }
        if (DateTime.UtcNow - _lastNavigationUtc < SettleTime) return;
        if (_clicks > 0 && DateTime.UtcNow - _lastClickUtc < ClickEvery)
        {
            // Between clicks, only look (a player frame may be on its way).
            await RunScriptAsync(core, allowClick: false);
            return;
        }
        await RunScriptAsync(core, allowClick: true);
    }

    private async Task RunScriptAsync(CoreWebView2 core, bool allowClick)
    {
        _busy = true;
        try
        {
            var parameters = JsonSerializer.Serialize(new
            {
                hosts = _preference.Hosts,
                n = allowClick ? _preference.StreamNumber : 0,
                clicks = _clicks,
                maxClicks = MaxClicks,
            });
            var raw = await core.ExecuteScriptAsync(FindOrClickScript.Replace("__PARAMS__", parameters, StringComparison.Ordinal));
            using var json = JsonDocument.Parse(raw);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (root.TryGetProperty("url", out var url) && url.GetString() is { Length: > 0 } found && !AdHosts.IsAd(found))
            {
                Complete(found);
                return;
            }
            if (root.TryGetProperty("clicked", out var clicked) && clicked.ValueKind == JsonValueKind.True)
            {
                _clicks++;
                _lastClickUtc = DateTime.UtcNow;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // page mid-navigation: next tick
        }
        finally
        {
            _busy = false;
        }
    }

    private static bool SameSite(Uri a, Uri b)
    {
        static string Base(string host) => host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..].ToLowerInvariant() : host.ToLowerInvariant();
        return Base(a.Host) == Base(b.Host);
    }

    /// <summary>Hands back the player (null: none) and closes, after the browser event that found it has returned.</summary>
    private void Complete(string? playerUrl)
    {
        if (_done) return;
        _done = true;
        _pollTimer.Stop();
        if (playerUrl is not null) WebView2Runtime.Log($"Media player finder for {_page.Host}: player at {playerUrl}");
        _result.TrySetResult(playerUrl);
        if (IsHandleCreated) BeginInvoke(new Action(Close));
    }

    /// <summary>Called from the resolver when playing was cancelled while the window was open.</summary>
    public void CancelFromHost() => Complete(null);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pollTimer.Dispose();
            _webView.Dispose();
        }
        base.Dispose(disposing);
    }
}
