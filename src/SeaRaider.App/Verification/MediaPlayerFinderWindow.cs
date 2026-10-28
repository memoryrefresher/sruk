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

          // The user's own hint (Media tab › Player…): look for the video by it, clicking what they named until it shows up.
          if (P.hint) {
            const bad = v => !v || /^\s*(?:#|about:|javascript:|data:)/i.test(v);
            const fromHint = () => {
              if (P.hint.kind === 'regex') {
                try {
                  const text = document.documentElement.outerHTML.replace(/\\\//g, '/').replace(/&amp;/g, '&');
                  const m = new RegExp(P.hint.value, 'is').exec(text);
                  const v = m && (m.groups && m.groups.url ? m.groups.url : (m.length > 1 ? m[1] : m[0]));
                  return bad(v) ? null : new URL(v, location.href).href;
                } catch (e) { return null; }
              }
              let els = [];
              try { els = [...document.querySelectorAll(P.hint.value)]; } catch (e) { return null; }
              const names = P.hint.attr ? [P.hint.attr] : P.attrs;
              for (const el of els) {
                for (const name of names) {
                  const v = el.getAttribute(name);
                  if (!bad(v)) { try { return new URL(v, location.href).href; } catch (e) {} }
                }
                for (const src of el.querySelectorAll('source[src]')) {
                  const v = src.getAttribute('src');
                  if (!bad(v)) { try { return new URL(v, location.href).href; } catch (e) {} }
                }
              }
              return null;
            };
            const found = fromHint();
            if (found) return { url: found };
            if (P.clicks >= P.maxClicks) return { clicked: false };

            // What to click, in order: the user's steps ("a >> text:Link-1"), else a guess at a play button and then the first
            // "Link-1" button, which is how these player pages work.
            const innermost = list => list.find(e => !list.some(o => o !== e && e.contains(o))) || null;
            const byText = t => {
              const needle = t.toLowerCase().replace(/\s+/g, ' ');
              const all = [...document.querySelectorAll('a, button, li, div, span, label, [role=button]')]
                .filter(e => { const x = (e.textContent || '').replace(/\s+/g, ' ').trim().toLowerCase(); return x.length < 60 && x.includes(needle); });
              return innermost(all);
            };
            const bySelector = sel => { try { return document.querySelector(sel); } catch (e) { return null; } };
            const inLinks = e => !!e.closest('#linkSelector, [class*="link-selector" i], [id*="link-selector" i]');
            const playArea = () => {
              for (const sel of ['#videoPlayer', '[class*="iframe-placeholder"]', '.player-container']) {
                const root = document.querySelector(sel);
                if (!root) continue;
                const hit = [...root.querySelectorAll('[class*="play" i], [id*="play" i], [aria-label*="play" i], [title*="play" i], svg, img, button')]
                  .find(e => e.tagName !== 'IFRAME' && !inLinks(e));
                if (hit) return hit;
                if (sel !== '.player-container') return root;
              }
              return null;
            };
            const playBox = () => { const r = document.querySelector('#videoPlayer, [class*="iframe-placeholder"]'); return r && !inLinks(r) ? r : null; };
            const steps = (P.click || '').split('>>').map(x => x.trim()).filter(Boolean);
            let target = null;
            if (steps.length) {
              const step = steps[Math.min(P.clicks, steps.length - 1)];
              target = step.toLowerCase().startsWith('text:') ? byText(step.slice(5).trim()) : bySelector(step);
            } else {
              // The page starts on Link 1 by itself: press play (the button, then the player box itself), not the link.
              target = [playArea, playBox, playArea, playBox][P.clicks % 4]();
            }
            if (!target) return { clicked: false };
            try { target.scrollIntoView({ block: 'center' }); } catch (e) {}
            // The click itself is made by the host as a real mouse click at this spot (players often ignore scripted clicks).
            const box = target.getBoundingClientRect();
            return { clickAt: { x: Math.round(box.left + box.width / 2), y: Math.round(box.top + box.height / 2) } };
          }
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

    /// <summary>Which numbered links ("Link-1 HD", "Link 2" …) the page offers. Returns { links: [1, 2, 3] }.</summary>
    private const string DiscoverLinksScript = """
        (() => {
          const re = /^\s*link[\s\-_#]*0*(\d{1,2})(?!\d)/i;
          const nums = new Set();
          for (const e of document.querySelectorAll('a, button, li, div, span, label, [role=button], [data-link]')) {
            const t = (e.textContent || '').replace(/\s+/g, ' ').trim();
            if (t.length < 40) { const m = re.exec(t); if (m) nums.add(Number(m[1])); }
          }
          return { links: [...nums].sort((a, b) => a - b) };
        })()
        """;

    /// <summary>Where link number __N__ is on the page (its centre), scrolled into view. Returns { x, y } or null.</summary>
    private const string LinkPointScript = """
        (() => {
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

    /// <summary>The centre of the page's biggest frame (the player), scrolled into view. Returns { x, y } or null.</summary>
    private const string FramePointScript = """
        (() => {
          const frames = [...document.querySelectorAll('iframe')].filter(f => f.offsetWidth >= 150 && f.offsetHeight >= 100)
            .sort((a, b) => b.offsetWidth * b.offsetHeight - a.offsetWidth * a.offsetHeight);
          if (!frames.length) return null;
          try { frames[0].scrollIntoView({ block: 'center' }); } catch (e) {}
          const b = frames[0].getBoundingClientRect();
          return { x: Math.round(b.left + b.width / 2), y: Math.round(b.top + b.height / 2) };
        })()
        """;

    /// <summary>Run inside the player's own frame: is its video playing, did it show an error. Returns { video, ok, err }.</summary>
    private const string ProbeScript = """
        (() => {
          const v = document.querySelector('video');
          const shown = e => {
            if (!e) return false;
            const s = getComputedStyle(e), r = e.getBoundingClientRect();
            return s.display !== 'none' && s.visibility !== 'hidden' && Number(s.opacity) > 0 && r.width > 0 && r.height > 0;
          };
          const err = shown(document.getElementById('errorMsg')) || shown(document.querySelector('.error-msg, .error-message, .player-error'));
          const ok = !!v && v.videoWidth > 0 && v.readyState >= 3 && !v.paused && v.currentTime > 0;
          return { video: !!v, ok, err };
        })()
        """;

    private readonly Uri _page;
    private readonly PlayerPreference _preference;
    private readonly string _userDataFolder;
    private readonly TaskCompletionSource<string?> _result;
    private readonly WebView2 _webView = new() { Dock = WinForms.DockStyle.Fill };
    private readonly WinForms.Timer _pollTimer = new() { Interval = 600 };
    private readonly List<CoreWebView2Frame> _frames = [];
    private static readonly TimeSpan HintGiveUpAfter = TimeSpan.FromSeconds(80);
    private static readonly TimeSpan ProbeFor = TimeSpan.FromSeconds(8);
    private List<int>? _links;
    private int _linkIndex;
    private bool _linkClicked;
    private string? _candidate;
    private string? _firstCandidate;
    private DateTime _candidateUtc;
    private DateTime _lastProbeClickUtc;
    private int _probeClicks;
    private bool _hintBusy;
    private bool HintMode => _preference.Hint is not null;

    private enum Probe { None, NeedsPlay, Failed, Working }

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
                if (AdHosts.IsAdRequest(url))
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
                if (_preference.Hint is not null)
                {
                    // The user's own hint decides what the player is: a click that sends the page to another site is an ad or a detour.
                    if (_clicks > 0 && (!SameSite(target, _page) || AdHosts.IsAd(target))) args.Cancel = true;
                    return;
                }
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
                if (AdHosts.IsAdRequest(args.Uri)) args.Cancel = true;
            };
            await SeaRaider.App.Services.AdShield.InstallAsync(core, SeaRaider.App.Services.AdShieldMode.Player); // no pop-ups or click-catching layers in the way
            // Pop-ups never open; a "stream" button that opens its player in a new window hands it over here.
            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (_preference.Hosts.Count > 0 && _preference.Matches(args.Uri)) Complete(args.Uri);
            };
            core.FrameCreated += (_, args) => TrackFrame(args.Frame); // the player's own frame, to see whether its video plays
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
        if (_done || _busy || _hintBusy || _webView.CoreWebView2 is not { } core) return;
        if (HintMode)
        {
            _hintBusy = true;
            try { await HintTickAsync(core); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // page mid-navigation: next tick
            }
            finally { _hintBusy = false; }
            return;
        }
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
            var hint = SeaRaider.Core.Scraping.PlayerHints.Describe(_preference.Hint);
            var parameters = JsonSerializer.Serialize(new
            {
                hosts = _preference.Hosts,
                n = allowClick ? _preference.StreamNumber : 0,
                clicks = _clicks,
                maxClicks = Math.Max(MaxClicks, (_preference.ClickSelector?.Split(">>").Length ?? 0) + 2),
                hint = hint is { } h ? new { kind = h.Kind, value = h.Value, attr = h.Attribute } : null,
                click = allowClick ? _preference.ClickSelector : null,
                attrs = SeaRaider.Core.Scraping.PlayerHints.DefaultAttributes,
            });
            var raw = await core.ExecuteScriptAsync(FindOrClickScript.Replace("__PARAMS__", parameters, StringComparison.Ordinal));
            using var json = JsonDocument.Parse(raw);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (root.TryGetProperty("url", out var url) && url.GetString() is { Length: > 0 } found && !AdHosts.IsAd(found))
            {
                if (HintMode)
                {
                    // The link's player address: used as it is when the user picked the link, else checked by playing it first.
                    _candidate = found;
                    _firstCandidate ??= found;
                    _candidateUtc = DateTime.UtcNow;
                    _probeClicks = 0;
                    if (_preference.Link > 0) Complete(found);
                    return;
                }
                Complete(found);
                return;
            }
            if (root.TryGetProperty("clickAt", out var at) && at.TryGetProperty("x", out var x) && at.TryGetProperty("y", out var y) &&
                x.TryGetInt32(out var px) && y.TryGetInt32(out var py))
            {
                await ClickAtAsync(core, px, py);
                _clicks++;
                _lastClickUtc = DateTime.UtcNow;
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

    private void TrackFrame(CoreWebView2Frame frame)
    {
        _frames.Add(frame);
        frame.Destroyed += (_, _) => _frames.Remove(frame);
        frame.FrameCreated += (_, args) => TrackFrame(args.Frame);
    }

    /// <summary>
    /// The user's own hint, with the page's numbered links (Link-1, Link-2 …). For each link in turn: the page is loaded, the
    /// link chosen, play pressed, the player's address read by the hint, and then the player is watched for a few seconds: the
    /// first link whose video really plays is the answer. A link the user picked is taken as it is. When none can be shown to
    /// work, the first link's player is returned anyway.
    /// </summary>
    private async Task HintTickAsync(CoreWebView2 core)
    {
        var now = DateTime.UtcNow;
        if (now - _startedUtc > HintGiveUpAfter)
        {
            WebView2Runtime.Log($"Media player finder for {_page.Host}: gave up after {HintGiveUpAfter.TotalSeconds:0}s");
            Complete(_firstCandidate);
            return;
        }
        if (now - _lastNavigationUtc < SettleTime) return;

        // 1. Which links the page offers (once).
        if (_links is null)
        {
            if (_preference.Link > 0) _links = [_preference.Link];
            else
            {
                var numbers = new List<int>();
                using var found = JsonDocument.Parse(await core.ExecuteScriptAsync(DiscoverLinksScript));
                if (found.RootElement.ValueKind == JsonValueKind.Object && found.RootElement.TryGetProperty("links", out var list) && list.ValueKind == JsonValueKind.Array)
                    numbers.AddRange(list.EnumerateArray().Where(n => n.TryGetInt32(out _)).Select(n => n.GetInt32()));
                _links = numbers.Count > 0 ? numbers.Take(6).ToList() : [0]; // no numbered links: the page's own single player
            }
        }
        var link = _links[_linkIndex];

        // 2. Choose the link (the page starts on Link 1 by itself).
        if (!_linkClicked)
        {
            _linkClicked = true;
            if (link > 0 && (link != 1 || _preference.Link > 0))
            {
                var point = await core.ExecuteScriptAsync(LinkPointScript.Replace("__N__", link.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
                if (TryPoint(point, out var x, out var y))
                {
                    await ClickAtAsync(core, x, y);
                    _lastNavigationUtc = DateTime.UtcNow; // let the page react before pressing play
                    return;
                }
            }
        }

        // 3. Press play and read the player's address.
        if (_candidate is null)
        {
            if (_clicks > 0 && now - _lastClickUtc < ClickEvery) await RunScriptAsync(core, allowClick: false);
            else await RunScriptAsync(core, allowClick: true);
            return;
        }

        // 4. Does its video play? (A link the user picked already ended the run.)
        var verdict = await ProbeFramesAsync();
        if (verdict == Probe.Working)
        {
            WebView2Runtime.Log($"Media player finder for {_page.Host}: link {link} plays");
            Complete(_candidate);
            return;
        }
        if (verdict != Probe.Failed && now - _candidateUtc <= ProbeFor)
        {
            // The player waits for a click of its own: press it (a real click on the frame), a few times at most.
            if (verdict == Probe.NeedsPlay && now - _candidateUtc > TimeSpan.FromSeconds(2) && now - _lastProbeClickUtc > TimeSpan.FromSeconds(3) && _probeClicks < 3 &&
                TryPoint(await core.ExecuteScriptAsync(FramePointScript), out var fx, out var fy))
            {
                await ClickAtAsync(core, fx, fy);
                _probeClicks++;
                _lastProbeClickUtc = DateTime.UtcNow;
            }
            return;
        }

        // 5. This link doesn't play: the next one, from a fresh page.
        WebView2Runtime.Log($"Media player finder for {_page.Host}: link {link} did not play ({verdict})");
        _linkIndex++;
        if (_linkIndex >= _links.Count)
        {
            Complete(_firstCandidate);
            return;
        }
        _candidate = null;
        _linkClicked = false;
        _clicks = 0;
        _frames.Clear();
        _lastNavigationUtc = DateTime.UtcNow;
        core.Navigate(_page.AbsoluteUri);
    }

    /// <summary>Looks in the page's frames for a video: playing (working), showing an error (failed), or waiting to be started.</summary>
    private async Task<Probe> ProbeFramesAsync()
    {
        var result = Probe.None;
        foreach (var frame in _frames.ToList())
        {
            try
            {
                using var json = JsonDocument.Parse(await frame.ExecuteScriptAsync(ProbeScript));
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True) return Probe.Working;
                if (root.TryGetProperty("err", out var err) && err.ValueKind == JsonValueKind.True) result = Probe.Failed;
                else if (result != Probe.Failed && root.TryGetProperty("video", out var video) && video.ValueKind == JsonValueKind.True) result = Probe.NeedsPlay;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException)
            {
                // a frame that went away or isn't ready: skip it
            }
        }
        return result;
    }

    private static bool TryPoint(string raw, out int x, out int y)
    {
        x = y = 0;
        try
        {
            using var json = JsonDocument.Parse(raw);
            return json.RootElement.ValueKind == JsonValueKind.Object &&
                   json.RootElement.TryGetProperty("x", out var px) && px.TryGetInt32(out x) &&
                   json.RootElement.TryGetProperty("y", out var py) && py.TryGetInt32(out y);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>A real left click at a point of the page (as a person's mouse would make it); a scripted click if the browser refuses.</summary>
    private static async Task ClickAtAsync(CoreWebView2 core, int x, int y)
    {
        try
        {
            foreach (var type in new[] { "mouseMoved", "mousePressed", "mouseReleased" })
            {
                var parameters = JsonSerializer.Serialize(new { type, x, y, button = type == "mouseMoved" ? "none" : "left", clickCount = type == "mouseMoved" ? 0 : 1 });
                await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", parameters);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            await core.ExecuteScriptAsync($"document.elementFromPoint({x}, {y})?.click()");
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
