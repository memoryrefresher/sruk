using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SeaRaider.Core.WebChallenge;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace SeaRaider.App.Verification;

/// <summary>
/// Loads one page in the verified WebView2 profile (same user-data folder as <see cref="CaptchaResolverWindow"/>, so the
/// clearance cookies and TLS fingerprint are the ones the site already accepted) and hands back what the browser got.
/// This is the fallback for sites that keep blocking SeaRaider's own HTTP client after verification.
///
/// <list type="bullet">
///   <item>Starts invisible (opacity 0, no taskbar button). A site that trusts the profile passes without any interaction.</item>
///   <item>If the page is still a bot check after <see cref="ShowAfter"/>, the window appears so the user can solve it again.</item>
///   <item>Once a non-challenge page has loaded and settled, its HTML is read from the DOM and the window closes.
///         Non-HTML URLs (feeds, JSON, sitemaps) are re-read as raw text with an in-page fetch, because the browser would
///         otherwise show them in a viewer.</item>
/// </list>
/// Runs on its own STA thread (see <see cref="WebView2ChallengeResolver"/>). The result is null when the browser couldn't
/// start, the window was closed, or the resolver was cancelled.
/// </summary>
internal sealed class BrowserPageFetchWindow : WinForms.Form
{
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan ShowAfter = TimeSpan.FromSeconds(8);

    private const string IsChallengePageScript = """
        (() => {
          const t = (document.title || '').toLowerCase();
          if (t.includes('just a moment') || t.includes('attention required') || t.includes('ddos-guard') ||
              t.includes('checking your browser') || t.includes('security check')) return true;
          return !!document.querySelector('#challenge-form, #challenge-running, #cf-challenge-running, .cf-browser-verification, ' +
            '#turnstile-wrapper, iframe[src*="challenges.cloudflare.com"]');
        })()
        """;

    /// <summary>Returns { ct, body, url }: the DOM for HTML pages, the raw text (in-page fetch) for anything else.</summary>
    private const string ReadPageScript = """
        (async () => {
          const ct = document.contentType || '';
          if (ct.toLowerCase().includes('html'))
            return { ct: 'text/html', body: '<!DOCTYPE html>\n' + document.documentElement.outerHTML, url: location.href };
          const r = await fetch(location.href, { credentials: 'include' });
          return { ct: r.headers.get('content-type') || ct || 'text/plain', body: await r.text(), url: location.href };
        })()
        """;

    private readonly Uri _url;
    private readonly string _userDataFolder;
    private readonly TaskCompletionSource<BrowserPage?> _result;
    private readonly WebView2 _webView = new() { Dock = WinForms.DockStyle.Fill };
    private readonly WinForms.Label _status = new();
    private readonly WinForms.Timer _pollTimer = new() { Interval = 700 };
    private DateTime _lastNavigationUtc = DateTime.UtcNow;
    private DateTime _startedUtc = DateTime.UtcNow;
    private bool _visible;
    private bool _completing;

    public BrowserPageFetchWindow(Uri url, string userDataFolder, TaskCompletionSource<BrowserPage?> result)
    {
        _url = url;
        _userDataFolder = userDataFolder;
        _result = result;

        Text = $"SeaRaider · {url.Host}";
        ShowInTaskbar = false;
        Opacity = 0;
        StartPosition = WinForms.FormStartPosition.CenterScreen;
        Size = new Drawing.Size(1000, 760);
        MinimumSize = new Drawing.Size(640, 480);
        BackColor = Drawing.Color.FromArgb(0x07, 0x14, 0x20);

        _status.Dock = WinForms.DockStyle.Top;
        _status.Height = 44;
        _status.Padding = new WinForms.Padding(16, 0, 12, 0);
        _status.ForeColor = Drawing.Color.FromArgb(0xE3, 0xED, 0xF2);
        _status.BackColor = Drawing.Color.FromArgb(0x0F, 0x21, 0x31);
        _status.Font = new Drawing.Font("Segoe UI", 10f);
        _status.TextAlign = Drawing.ContentAlignment.MiddleLeft;
        _status.Text = $"{url.Host} wants to check you're human again. Complete the check below; this window closes by itself.";

        Controls.Add(_webView);
        Controls.Add(_status);

        _pollTimer.Tick += async (_, _) => await TickAsync();
        FormClosed += (_, _) =>
        {
            if (_result.TrySetResult(null)) WebView2Runtime.Log($"Browser fetch of {_url.Host}: window closed before the page was read");
        };
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _startedUtc = DateTime.UtcNow;
        try
        {
            WebView2Runtime.Log($"Browser fetch of {_url.Host}: starting the browser");
            var environment = await CoreWebView2Environment.CreateAsync(WebView2Runtime.FindFixedVersionFolder(), _userDataFolder, SeaRaider.App.Services.BackgroundThrottling.CreateWebView2Options());
            await _webView.EnsureCoreWebView2Async(environment);

            var core = _webView.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.NavigationStarting += (_, _) => _lastNavigationUtc = DateTime.UtcNow;
            core.NavigationCompleted += async (_, args) =>
            {
                _lastNavigationUtc = DateTime.UtcNow;
                WebView2Runtime.Log($"Browser fetch of {_url.Host}: navigation completed (success={args.IsSuccess}, {args.WebErrorStatus}, http={args.HttpStatusCode}, at {core.Source})");
                await TickAsync();
            };
            // Never let a pop-up or a download hijack the fetch.
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.DownloadStarting += (_, args) => args.Cancel = true;

            core.Navigate(_url.AbsoluteUri);
            _pollTimer.Start();
        }
        catch (Exception ex)
        {
            WebView2Runtime.Log($"Browser fetch of {_url.Host} couldn't start: {ex}");
            _result.TrySetResult(null);
            Close();
        }
    }

    private async Task TickAsync()
    {
        if (_completing || _webView.CoreWebView2 is not { } core) return;
        if (DateTime.UtcNow - _lastNavigationUtc < SettleTime) return;

        try
        {
            var stillChallenge = JsonSerializer.Deserialize<bool>(await core.ExecuteScriptAsync(IsChallengePageScript));
            if (!stillChallenge)
            {
                await CompleteAsync(core);
                return;
            }

            // Still a bot check: let the user see it (once).
            if (!_visible && DateTime.UtcNow - _startedUtc > ShowAfter)
            {
                _visible = true;
                WebView2Runtime.Log($"Browser fetch of {_url.Host}: still a check page after {ShowAfter.TotalSeconds:0}s (title \"{core.DocumentTitle}\"); showing the window");
                ShowInTaskbar = true;
                Opacity = 1;
                CenterToScreen();
                TopMost = true;
                Activate();
                TopMost = false;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // Page mid-navigation; try again on the next tick.
        }
    }

    private async Task CompleteAsync(CoreWebView2 core)
    {
        if (_completing) return;
        _completing = true;
        _pollTimer.Stop();

        BrowserPage? page = null;
        try
        {
            var raw = await core.ExecuteScriptAsync(ReadPageScript);
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("body", out _))
                WebView2Runtime.Log($"Browser fetch of {_url.Host}: unexpected script result: {(raw.Length > 200 ? raw[..200] : raw)}");
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("body", out var body) && body.GetString() is { Length: > 0 } text)
            {
                var type = root.TryGetProperty("ct", out var ct) ? ct.GetString() ?? "text/html" : "text/html";
                var finalUrl = root.TryGetProperty("url", out var u) && Uri.TryCreate(u.GetString(), UriKind.Absolute, out var parsed) ? parsed : _url;
                page = new BrowserPage(text, type, finalUrl);
            }
        }
        catch (Exception ex)
        {
            WebView2Runtime.Log($"Reading {_url.Host} from the browser failed: {ex.GetType().Name} {ex.Message}");
        }

        _result.TrySetResult(page);
        Close();
    }

    /// <summary>Called from the resolver when the request was cancelled while the window was open.</summary>
    public void CancelFromHost()
    {
        WebView2Runtime.Log($"Browser fetch of {_url.Host}: cancelled by the app");
        _result.TrySetResult(null);
        Close();
    }

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
