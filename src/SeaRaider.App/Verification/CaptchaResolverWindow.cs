using System.Net;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SeaRaider.Core.WebChallenge;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace SeaRaider.App.Verification;

/// <summary>
/// Small companion browser window (WebView2) that shows a site's verification page so the user can solve it.
///
/// <list type="bullet">
///   <item><b>Bot checks</b> (Cloudflare and similar): closes by itself as soon as the real page has loaded.</item>
///   <item><b>Captchas / file-host pages</b>: closes by itself when the user clicks the site's download button
///         (the download is intercepted and its URL handed back to SeaRaider), or when the user clicks Continue.</item>
/// </list>
/// On close the site's cookies (including HttpOnly clearance cookies) and the browser's user agent are returned,
/// so SeaRaider's own HTTP requests are treated as the same verified browser session.
///
/// Runs on its own STA thread with its own message loop (see <see cref="WebView2ChallengeResolver"/>), independent
/// of Avalonia's UI thread. The WebView2 profile lives in %LOCALAPPDATA%\SeaRaider\webview2, so a site that was
/// verified recently often passes again without any interaction.
///
/// <para><b>Starting the browser</b> goes through <see cref="StartBrowserAsync"/>: fixed-version runtime if present,
/// fresh profile on a profile error, runtime install / repair on a missing-runtime error (the 0x80070002 case). When
/// nothing works, <see cref="RuntimeUnavailable"/> is set and the resolver offers the system browser instead.</para>
/// </summary>
internal sealed class CaptchaResolverWindow : WinForms.Form
{
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(1.5);

    /// <summary>JavaScript returning true while the page is still a verification interstitial.</summary>
    private const string IsChallengePageScript = """
        (() => {
          const t = (document.title || '').toLowerCase();
          if (t.includes('just a moment') || t.includes('attention required') || t.includes('ddos-guard') ||
              t.includes('checking your browser') || t.includes('security check')) return true;
          return !!document.querySelector('#challenge-form, #challenge-running, #cf-challenge-running, .cf-browser-verification, ' +
            '#turnstile-wrapper, iframe[src*="challenges.cloudflare.com"]');
        })()
        """;

    private readonly ChallengeRequest _request;
    private readonly bool _allowRuntimeInstall;
    private readonly TaskCompletionSource<ChallengeResolution> _result;
    private string _userDataFolder;
    private WebView2 _webView = new() { Dock = WinForms.DockStyle.Fill };
    private readonly WinForms.Label _status = new();
    private readonly WinForms.Timer _pollTimer = new() { Interval = 1000 };
    private DateTime _lastNavigationUtc = DateTime.UtcNow;
    private bool _completing;

    public CaptchaResolverWindow(ChallengeRequest request, string userDataFolder, bool allowRuntimeInstall,
        TaskCompletionSource<ChallengeResolution> result)
    {
        _request = request;
        _userDataFolder = userDataFolder;
        _allowRuntimeInstall = allowRuntimeInstall;
        _result = result;

        Text = $"SeaRaider · Verify {request.Url.Host}";
        StartPosition = WinForms.FormStartPosition.CenterScreen;
        Size = new Drawing.Size(1000, 760);
        MinimumSize = new Drawing.Size(640, 480);
        BackColor = Drawing.Color.FromArgb(0x07, 0x14, 0x20);
        ShowInTaskbar = true;

        Controls.Add(_webView);
        Controls.Add(BuildHeader());

        _pollTimer.Tick += async (_, _) => await CheckSolvedAsync();
        FormClosed += (_, _) => _result.TrySetResult(ChallengeResolution.Failed("The verification window was closed."));
    }

    private WinForms.Control BuildHeader()
    {
        var header = new WinForms.Panel
        {
            Dock = WinForms.DockStyle.Top,
            Height = 64,
            Padding = new WinForms.Padding(16, 10, 12, 10),
            BackColor = Drawing.Color.FromArgb(0x0F, 0x21, 0x31),
        };

        _status.Dock = WinForms.DockStyle.Fill;
        _status.ForeColor = Drawing.Color.FromArgb(0xE3, 0xED, 0xF2);
        _status.Font = new Drawing.Font("Segoe UI", 10f);
        _status.TextAlign = Drawing.ContentAlignment.MiddleLeft;
        _status.Text = _request.Kind == ChallengeKind.BotCheck
            ? $"{_request.Url.Host} wants to check you're human. Complete the check below; this window closes by itself."
            : $"Complete the captcha below, then click the site's download button. SeaRaider takes over the download from there.";

        var cancel = MakeButton("Cancel", Drawing.Color.FromArgb(0x16, 0x30, 0x44), Drawing.Color.FromArgb(0xE3, 0xED, 0xF2));
        cancel.Click += (_, _) => Close();

        var done = MakeButton("Continue", Drawing.Color.FromArgb(0x3F, 0xE0, 0xC5), Drawing.Color.FromArgb(0x07, 0x14, 0x20));
        done.Click += async (_, _) => await CompleteAsync(downloadUrl: null);

        var buttons = new WinForms.FlowLayoutPanel
        {
            Dock = WinForms.DockStyle.Right,
            FlowDirection = WinForms.FlowDirection.LeftToRight,
            AutoSize = true,
            WrapContents = false,
            BackColor = header.BackColor,
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(done);

        header.Controls.Add(_status);
        header.Controls.Add(buttons);
        return header;
    }

    private static WinForms.Button MakeButton(string text, Drawing.Color back, Drawing.Color fore)
    {
        var button = new WinForms.Button
        {
            Text = text,
            AutoSize = true,
            Height = 36,
            Padding = new WinForms.Padding(14, 4, 14, 4),
            Margin = new WinForms.Padding(8, 4, 0, 4),
            FlatStyle = WinForms.FlatStyle.Flat,
            BackColor = back,
            ForeColor = fore,
            Font = new Drawing.Font("Segoe UI Semibold", 10f),
            Cursor = WinForms.Cursors.Hand,
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    /// <summary>The WebView2 profile folder this window actually ended up using (it can differ from the one asked for after a repair).</summary>
    public string UserDataFolder => _userDataFolder;

    /// <summary>True when no WebView2 browser could be started (runtime missing and not installable).</summary>
    public bool RuntimeUnavailable { get; private set; }

    /// <summary>
    /// Creates the WebView2 browser, recovering from a missing or broken runtime and from a damaged profile.
    /// A failed WebView2 control can't be initialised again, so each attempt uses a new control.
    /// </summary>
    private async Task<bool> StartBrowserAsync()
    {
        var fixedRuntime = WebView2Runtime.FindFixedVersionFolder();
        var triedFreshProfile = false;
        var triedInstall = false;

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                var environment = await CoreWebView2Environment.CreateAsync(fixedRuntime, _userDataFolder, SeaRaider.App.Services.BackgroundThrottling.CreateWebView2Options());
                await _webView.EnsureCoreWebView2Async(environment);
                return true;
            }
            catch (Exception ex) when (WebView2Runtime.IsRuntimeProblem(ex))
            {
                WebView2Runtime.Log($"Attempt {attempt} for {_request.Url.Host} failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
                ReplaceWebView();

                if (fixedRuntime is not null)
                {
                    fixedRuntime = null; // a broken fixed runtime: use the installed one instead
                }
                else if (!triedFreshProfile && WebView2Runtime.InstalledVersion() is not null)
                {
                    // Runtime is registered, so the profile (or a stale browser process holding it) is the likely problem.
                    triedFreshProfile = true;
                    _userDataFolder = WebView2Runtime.FreshUserDataFolder(_userDataFolder);
                }
                else if (!triedInstall && _allowRuntimeInstall)
                {
                    // Missing, or registered but its files are gone: install / repair with Microsoft's bootstrapper.
                    triedInstall = true;
                    if (!await WebView2Runtime.InstallEvergreenAsync(text => _status.Text = text, CancellationToken.None)) break;
                    _status.Text = "WebView2 Runtime installed. Opening the page…";
                }
                else
                {
                    break;
                }
            }
        }

        RuntimeUnavailable = true;
        return false;
    }

    /// <summary>Swaps in a new browser control after a failed initialisation.</summary>
    private void ReplaceWebView()
    {
        Controls.Remove(_webView);
        _webView.Dispose();
        _webView = new WebView2 { Dock = WinForms.DockStyle.Fill };
        Controls.Add(_webView);
        _webView.BringToFront(); // front of the z-order docks last, so Fill takes what the Top header leaves
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        // Come to the front once without staying on top of everything.
        TopMost = true;
        Activate();
        TopMost = false;

        try
        {
            if (!await StartBrowserAsync())
            {
                _result.TrySetResult(ChallengeResolution.Failed(
                    "The built-in browser (Microsoft Edge WebView2) couldn't start on this PC. Details: " + WebView2Runtime.LogPath));
                Close();
                return;
            }

            var core = _webView.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;

            core.NavigationStarting += (_, _) => _lastNavigationUtc = DateTime.UtcNow;
            core.NavigationCompleted += async (_, _) =>
            {
                _lastNavigationUtc = DateTime.UtcNow;
                await CheckSolvedAsync();
            };

            // Download buttons on file hosts: hand the real file URL to SeaRaider instead of downloading here.
            core.DownloadStarting += async (_, args) =>
            {
                args.Cancel = true;
                if (Uri.TryCreate(args.DownloadOperation.Uri, UriKind.Absolute, out var fileUrl))
                    await CompleteAsync(fileUrl);
            };

            // Pop-ups: follow the ones that stay on the site or are the file, drop the rest (ads would replace the page).
            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var target) || target.Scheme is not ("http" or "https")) return;
                var sameSite = target.Host.EndsWith(BaseDomain(_request.Url.Host), StringComparison.OrdinalIgnoreCase);
                if (sameSite || Core.Downloads.BrowserPageScript.LooksLikeFileUrl(target)) core.Navigate(target.AbsoluteUri);
            };

            core.Navigate(_request.Url.AbsoluteUri);
            _pollTimer.Start();
        }
        catch (Exception ex)
        {
            WebView2Runtime.Log($"Verification window error: {ex}");
            RuntimeUnavailable = WebView2Runtime.IsRuntimeProblem(ex);
            _result.TrySetResult(ChallengeResolution.Failed($"The verification window couldn't start: {ex.Message}"));
            Close();
        }
    }

    private static string BaseDomain(string host)
    {
        var parts = host.Split('.');
        return parts.Length <= 2 ? host : string.Join('.', parts[^2..]);
    }

    /// <summary>Bot checks finish on their own: done once a non-challenge page has loaded and settled.</summary>
    private async Task CheckSolvedAsync()
    {
        if (_completing || _request.Kind != ChallengeKind.BotCheck || _webView.CoreWebView2 is not { } core) return;
        if (DateTime.UtcNow - _lastNavigationUtc < SettleTime) return;

        try
        {
            var raw = await core.ExecuteScriptAsync(IsChallengePageScript);
            var stillChallenge = JsonSerializer.Deserialize<bool>(raw);
            if (!stillChallenge) await CompleteAsync(downloadUrl: null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // Page mid-navigation; try again on the next tick.
        }
    }

    /// <summary>Captures cookies and the user agent, reports success and closes.</summary>
    private async Task CompleteAsync(Uri? downloadUrl)
    {
        if (_completing) return;
        _completing = true;
        _pollTimer.Stop();
        _status.Text = "Verified. Returning to SeaRaider…";

        try
        {
            var core = _webView.CoreWebView2;
            var cookies = new List<Cookie>();
            if (core is not null)
            {
                foreach (var uri in new[] { _request.Url, downloadUrl, core.Source is { Length: > 0 } src ? new Uri(src) : null }.OfType<Uri>().Distinct())
                {
                    foreach (var cookie in await core.CookieManager.GetCookiesAsync(uri.AbsoluteUri))
                        cookies.Add(cookie.ToSystemNetCookie());
                }
            }

            var unique = cookies
                .GroupBy(c => (c.Domain, c.Path, c.Name))
                .Select(g => g.First())
                .ToList();
            _result.TrySetResult(new ChallengeResolution(true, core?.Settings.UserAgent, unique, downloadUrl, null));
        }
        catch (Exception ex)
        {
            _result.TrySetResult(ChallengeResolution.Failed($"Couldn't read the verified session: {ex.Message}"));
        }
        Close();
    }

    /// <summary>Called from the resolver when the download was paused / cancelled while the window was open.</summary>
    public void CancelFromHost()
    {
        _result.TrySetResult(ChallengeResolution.Failed("Cancelled."));
        Close();
    }

    public void BringToFrontFromHost()
    {
        if (WindowState == WinForms.FormWindowState.Minimized) WindowState = WinForms.FormWindowState.Normal;
        TopMost = true;
        Activate();
        TopMost = false;
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
