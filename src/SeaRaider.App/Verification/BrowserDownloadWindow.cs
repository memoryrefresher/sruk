using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Downloads;
using SeaRaider.Core.Scraping;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace SeaRaider.App.Verification;

/// <summary>
/// SeaRaider's own browser for file hosts (see <see cref="BrowserDownloadService"/>). One window, on its own STA thread,
/// handles one host page at a time; the files themselves download in the background (WebView2 keeps downloading after
/// the page moves on), so several can run while the next page is being opened.
///
/// <para>For each page it:</para>
/// <list type="number">
///   <item>opens the link exactly as the game page had it, with the game page as Referer;</item>
///   <item>recognises "File not found" pages (and HTTP 404 / 410) and reports the link as dead, so the next host is tried;</item>
///   <item>clicks the host's download button for you (when allowed in Settings) — but never while a captcha is waiting
///         for you — and finds direct file links in the page (FuckingFast's <c>/dl/</c> link), which skips the
///         "first click opens ads" step;</item>
///   <item>blocks pop-ups that leave the host (ads), but follows pop-ups that are the download;</item>
///   <item>saves the file straight into SeaRaider's download folder, reporting progress, and resumes interrupted transfers.</item>
/// </list>
/// The window stays out of the way (it never steals focus) unless the page needs you: a captcha, or no download
/// started after a few seconds. Then it comes to the front and the main window shows a banner.
/// </summary>
internal sealed class BrowserDownloadWindow : WinForms.Form
{
    /// <summary>
    /// No captcha, no button found, no download yet after this long: ask the user. Long enough for the usual
    /// "wait 15 seconds" countdowns to run out on their own.
    /// </summary>
    private static readonly TimeSpan AttentionAfter = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Short links run fully in the background: the window only comes forward for a human check (captcha, browser check).
    /// One that hasn't reached the file after this long fails, so the next mirror can be tried.
    /// </summary>
    private static readonly TimeSpan ShortenerGiveUpAfter = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ClickInterval = TimeSpan.FromSeconds(3);
    private const int MaxClicks = 8;
    private const int MaxResumes = 3;

    private readonly string _userDataFolder;
    private readonly bool _allowRuntimeInstall;
    private readonly Action<string, bool> _attentionChanged;
    private readonly WebView2 _webView = new() { Dock = WinForms.DockStyle.Fill };
    private readonly WinForms.Label _status = new();
    private readonly WinForms.Timer _tick = new() { Interval = 1500 };
    private readonly List<ActiveDownload> _downloads = [];
    private readonly Dictionary<string, CoreWebView2DownloadOperation> _paused = new(StringComparer.OrdinalIgnoreCase);
    private PageRequest? _page;
    private bool _allowClose;

    public BrowserDownloadWindow(string userDataFolder, bool allowRuntimeInstall, Action<string, bool> attentionChanged)
    {
        _userDataFolder = userDataFolder;
        _allowRuntimeInstall = allowRuntimeInstall;
        _attentionChanged = attentionChanged;

        Text = "SeaRaider · Downloads";
        StartPosition = WinForms.FormStartPosition.Manual;
        ShowInTaskbar = false; // works quietly off-screen; comes forward only when it needs the user
        Size = new Drawing.Size(1000, 760);
        MinimumSize = new Drawing.Size(640, 480);
        BackColor = Drawing.Color.FromArgb(0x07, 0x14, 0x20);

        var header = new WinForms.Panel
        {
            Dock = WinForms.DockStyle.Top,
            Height = 56,
            Padding = new WinForms.Padding(16, 8, 12, 8),
            BackColor = Drawing.Color.FromArgb(0x0F, 0x21, 0x31),
        };
        _status.Dock = WinForms.DockStyle.Fill;
        _status.ForeColor = Drawing.Color.FromArgb(0xE3, 0xED, 0xF2);
        _status.Font = new Drawing.Font("Segoe UI", 10f);
        _status.TextAlign = Drawing.ContentAlignment.MiddleLeft;
        header.Controls.Add(_status);

        Controls.Add(_webView);
        Controls.Add(header);
        _tick.Tick += async (_, _) => await OnTickAsync();
        FormClosing += OnFormClosing;
    }

    /// <summary>False until the browser has started; null result = WebView2 can't run here.</summary>
    public TaskCompletionSource<bool> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>A host page is open (safe to read from any thread).</summary>
    public bool HasPage => Volatile.Read(ref _page) is not null;

    /// <summary>Nothing is loading or downloading (the service closes idle windows).</summary>
    public bool IsIdle => _page is null && _downloads.Count == 0 && _paused.Count == 0;

    protected override bool ShowWithoutActivation => true;

    // =============================================================================================
    // Start-up
    // =============================================================================================

    /// <summary>
    /// Starts the browser. Called on the window's thread after its handle exists; the window itself stays hidden until a
    /// page is opened (it's never passed to Application.Run, which would show it).
    /// </summary>
    public async void Initialize()
    {
        try
        {
            CoreWebView2Environment? environment = null;
            try
            {
                environment = await CoreWebView2Environment.CreateAsync(WebView2Runtime.FindFixedVersionFolder(), _userDataFolder, SeaRaider.App.Services.BackgroundThrottling.CreateWebView2Options());
                await _webView.EnsureCoreWebView2Async(environment);
            }
            catch (Exception ex) when (WebView2Runtime.IsRuntimeProblem(ex) && _allowRuntimeInstall)
            {
                WebView2Runtime.Log($"Download browser failed to start: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
                if (!await WebView2Runtime.InstallEvergreenAsync(text => _status.Text = text, CancellationToken.None)) throw;
                environment = await CoreWebView2Environment.CreateAsync(null, _userDataFolder, SeaRaider.App.Services.BackgroundThrottling.CreateWebView2Options());
                await _webView.EnsureCoreWebView2Async(environment);
            }

            var core = _webView.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false; // hosts' "are you sure?" alerts would block the page
            core.PermissionRequested += OnPermissionRequested;
            core.NavigationCompleted += async (_, args) => await OnNavigationCompletedAsync(args);
            core.NavigationStarting += OnNavigationStarting;
            core.NewWindowRequested += OnNewWindowRequested;
            core.DownloadStarting += OnDownloadStarting;
            Ready.TrySetResult(true);
            _tick.Start();
        }
        catch (Exception ex)
        {
            WebView2Runtime.Log($"Download browser unavailable: {ex}");
            Ready.TrySetResult(false);
        }
    }

    // =============================================================================================
    // Requests (always called on this window's thread)
    // =============================================================================================

    /// <summary>Opens the host page for <paramref name="request"/>; the returned task completes when the file is saved.</summary>
    public Task<BrowserDownloadResult> Start(BrowserDownloadRequest request, IProgress<TransferProgress>? progress, bool autoClick,
        CancellationToken cancellationToken)
    {
        var result = new TaskCompletionSource<BrowserDownloadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = request.FileName is { Length: > 0 } name ? Path.Combine(request.DestinationDirectory, name) : null;

        // A transfer paused earlier (the job was paused): continue it instead of starting over.
        if (target is not null && _paused.Remove(target, out var paused) && paused.CanResume)
        {
            var resumed = new ActiveDownload(request, progress, result, paused, target);
            Track(resumed, cancellationToken);
            paused.Resume();
            UpdateStatus();
            return result.Task;
        }

        _page = new PageRequest(request, progress, autoClick, result, cancellationToken);
        cancellationToken.Register(() => BeginInvokeSafe(() =>
        {
            if (_page?.Result == result) EndPage(BrowserDownloadResult.Fail("Paused."), cancelled: true);
        }));

        _status.Text = $"{request.Label}: opening {request.HostName}…";
        Park();
        var core = _webView.CoreWebView2;
        var headers = request.Referer is null ? null : $"Referer: {request.Referer.AbsoluteUri}";
        core.NavigateWithWebResourceRequest(core.Environment.CreateWebResourceRequest(request.PageUrl.AbsoluteUri, "GET", null, headers));
        return result.Task;
    }

    private void EndPage(BrowserDownloadResult outcome, bool cancelled = false)
    {
        var page = _page;
        if (page is null) return;
        _page = null;
        ClosePopup();
        if (page.NeedsAttention) _attentionChanged(page.Request.HostName, false);
        if (cancelled) page.Result.TrySetCanceled(page.CancellationToken);
        else page.Result.TrySetResult(outcome);
        try { _webView.CoreWebView2?.Navigate("about:blank"); } catch (InvalidOperationException) { }
        if (_downloads.Count == 0) Hide();
        UpdateStatus();
    }

    // =============================================================================================
    // Page handling
    // =============================================================================================

    private async Task OnNavigationCompletedAsync(CoreWebView2NavigationCompletedEventArgs args)
    {
        if (_page is not { } page) return;
        page.LastNavigation = DateTime.UtcNow;
        if (args.HttpStatusCode is 404 or 410 && !page.DownloadSeen)
        {
            EndPage(BrowserDownloadResult.Dead($"{page.Request.HostName} no longer has this file ({args.HttpStatusCode})."));
            return;
        }

        // "Hmm… can't reach this page": an ad domain that doesn't resolve, or a dead redirect. Back out and skip it.
        if (!args.IsSuccess && !page.DownloadSeen &&
            args.WebErrorStatus is not (CoreWebView2WebErrorStatus.OperationCanceled or CoreWebView2WebErrorStatus.ConnectionAborted))
        {
            var failed = Uri.TryCreate(_webView.CoreWebView2.Source, UriKind.Absolute, out var f) ? f : null;
            if (failed is null || string.Equals(failed.AbsoluteUri, page.Request.PageUrl.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
                EndPage(BrowserDownloadResult.Fail($"{page.Request.HostName} couldn't be reached ({args.WebErrorStatus})."));
            else
                LeaveBadPage(page, failed, "couldn't be reached");
            return;
        }
        await InspectAsync(page);
    }

    private async Task OnTickAsync()
    {
        if (_page is { } page) await InspectAsync(page);
        if (_page is { } current && current.Elapsed > TimeSpan.FromMinutes(15))
            EndPage(BrowserDownloadResult.Fail($"{current.Request.HostName} didn't start the download within 15 minutes."));
        // A short link works on its own; one that gets nowhere (and isn't waiting for you) gives up quietly.
        else if (_page is { ViaShortener: true, NeedsAttention: false } stuck && stuck.Elapsed > ShortenerGiveUpAfter)
            EndPage(BrowserDownloadResult.Fail($"Couldn't find the download on {stuck.Request.HostName}. Try another mirror."));
    }

    /// <summary>Looks at the page: gone? waiting for a captcha? a button to click or a file link to follow?</summary>
    private async Task InspectAsync(PageRequest page)
    {
        if (page.Inspecting || page.DownloadSeen || _webView.CoreWebView2 is not { } core) return;
        page.Inspecting = true;
        try
        {
            var mayClick = page.AutoClick && page.Clicks < MaxClicks && DateTime.UtcNow - page.LastClick >= ClickInterval &&
                           DateTime.UtcNow - page.LastNavigation >= TimeSpan.FromSeconds(1);
            // Relaxed rules (follow buttons that lead to other sites) only on the shortener and the unknown page it leads
            // to. On a known file host (DataNodes …) its off-site "Download" buttons are ads.
            var here = Uri.TryCreate(core.Source, UriKind.Absolute, out var current) ? current : page.Request.PageUrl;
            var protector = FileHosts.IsLinkProtector(here.Host);
            var onFileHost = FileHosts.Find(here.Host) is not null && !FileHosts.IsLinkShortener(here.Host) && !protector;

            // A short link's own pages are yours: SeaRaider shows them and presses nothing there. Once the download host
            // opens, it takes over again (hidden) and only comes back for a check on that host.
            var manual = page.ViaShortener && !onFileHost || protector;
            if (manual) mayClick = false;
            var raw = await core.ExecuteScriptAsync(BrowserPageScript.Build(mayClick, fromShortener: false, page.Request.WantTorrent, page.Blocked));
            if (_page != page) return;
            var json = JsonSerializer.Deserialize<string>(raw);
            if (json is null) return;
            var state = JsonSerializer.Deserialize<PageState>(json, JsonOptions);
            if (state is null) return;

            if (state.Dead)
            {
                EndPage(BrowserDownloadResult.Dead($"{page.Request.HostName} says the file is gone (\"file not found\")."));
                return;
            }

            if (state.Trap)
            {
                // An ad ("Press Allow…", "virus detected"): back to where we were, and never follow that link again.
                LeaveBadPage(page, here, "was an ad");
                return;
            }

            if (page.Request.CollectLinks && await CollectLinksAsync(page, core, here, onFileHost, protector, state)) return;

            if (page.Request.WantTorrent && state.Magnet is { Length: > 0 } magnet && magnet.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            {
                EndPage(BrowserDownloadResult.FoundMagnet(magnet));
                return;
            }

            if (state.Href is { Length: > 0 } href && !page.FollowedLinks.Contains(href) &&
                Uri.TryCreate(href, UriKind.Absolute, out var direct) && direct.Scheme is "http" or "https")
            {
                page.FollowedLinks.Add(href);
                page.LastFollowed = direct.AbsoluteUri;
                page.Expected.Add(direct.AbsoluteUri);
                if (state.Clicked is { Length: > 0 } pressed)
                {
                    // A shortener's / download page's button that leads to another site: opened here, not as a pop-up.
                    page.Clicks++;
                    page.LastClick = DateTime.UtcNow;
                    _status.Text = $"{page.Request.Label}: fetching download from {page.Request.HostName}…";
                }
                else
                {
                    _status.Text = $"{page.Request.Label}: starting the download from {page.Request.HostName}…";
                }
                core.Navigate(direct.AbsoluteUri);
                return;
            }

            if (state.Clicked is { Length: > 0 } clicked)
            {
                page.LastFollowed = null; // a button, not a link: nothing to block if it leads to an ad
                page.Clicks++;
                page.LastClick = DateTime.UtcNow;
                _status.Text = $"{page.Request.Label}: fetching download from {page.Request.HostName}…";
                return;
            }

            // Short links: only a real human check brings the window forward. Other hosts keep the old rule (also when
            // nothing has happened for a while).
            var humanCheck = manual || state.Captcha || state.Challenge && page.Elapsed > TimeSpan.FromSeconds(15);
            var needsYou = humanCheck || !page.AutoClick || !page.ViaShortener && page.Elapsed > AttentionAfter;

            // The check is done: out of the way again; SeaRaider carries on in the background.
            if (page.NeedsAttention && page.AttentionForCheck && !humanCheck && page.AutoClick)
            {
                page.NeedsAttention = false;
                page.AttentionForCheck = false;
                _attentionChanged(page.Request.HostName, false);
                HideAway();
            }
            _status.Text = manual
                ? protector
                    ? $"{page.Request.Label}: pass the security check. SeaRaider then gets every part from {page.Request.HostName} by itself."
                    : $"{page.Request.Label}: click through this page yourself (Visit, any checks). SeaRaider takes over when {page.Request.HostName} opens."
                : state.Captcha
                ? $"{page.Request.Label}: solve the captcha on {page.Request.HostName}. SeaRaider clicks Download after that."
                : state.Challenge
                    ? $"{page.Request.Label}: {page.Request.HostName} is checking the browser…"
                    : state.Waiting || page.ViaShortener
                        ? $"{page.Request.Label}: fetching download from {page.Request.HostName}…"
                    : page.AutoClick
                        ? $"{page.Request.Label}: waiting for {page.Request.HostName} (countdown?). Click its download button if you see one."
                        : $"{page.Request.Label}: click {page.Request.HostName}'s download button. SeaRaider takes the file from there.";
            if (needsYou && !page.NeedsAttention)
            {
                page.NeedsAttention = true;
                page.AttentionForCheck = humanCheck;
                _attentionChanged(page.Request.HostName, true);
                BringToFrontFromHost();
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // Page mid-navigation; next tick.
        }
        finally
        {
            page.Inspecting = false;
        }
    }

    /// <summary>
    /// Pages never get notifications, location, camera, microphone or clipboard: download pages don't need them, and the
    /// "Press Allow to prove you are not a robot" ads only want the notification permission to send spam.
    /// </summary>
    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs args)
    {
        if (args.PermissionKind != CoreWebView2PermissionKind.MultipleAutomaticDownloads)
            args.State = CoreWebView2PermissionState.Deny;
    }

    /// <summary>A button that opens a magnet link (the page builds it on click): when a torrent is wanted, that's the answer.</summary>
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!args.Uri.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            GuardNavigation(args);
            return;
        }
        args.Cancel = true; // never hand magnets to another app
        var magnet = args.Uri;
        if (_page is { Request.WantTorrent: true } page)
            BeginInvokeSafe(() => { if (_page == page) EndPage(BrowserDownloadResult.FoundMagnet(magnet)); });
    }

    /// <summary>
    /// Keeps a short link's ads out: a page on the shortener may only go to the shortener itself, a known file host, a
    /// file, the link SeaRaider chose to follow, or (right after SeaRaider pressed its button) the page that button opens.
    /// Its timed redirects to anything else ("virus detected" pages) are cancelled. Links that already led to an ad are
    /// never opened again, anywhere.
    /// </summary>
    private void GuardNavigation(CoreWebView2NavigationStartingEventArgs args)
    {
        if (_page is not { } page || !Uri.TryCreate(args.Uri, UriKind.Absolute, out var target) || target.Scheme is not ("http" or "https")) return;

        // Collecting links: the file host's address is all that's needed, so it isn't even opened.
        if (page.Request.CollectLinks && IsHostLink(target))
        {
            if (page.CurrentPart is { } part)
            {
                args.Cancel = true;
                page.Collected[part] = target.AbsoluteUri;
                page.CurrentPart = null;
                return;
            }
            if (page.Parts is null)
            {
                args.Cancel = true;
                var found = target.AbsoluteUri;
                BeginInvokeSafe(() => { if (_page == page) EndPage(BrowserDownloadResult.Collected([new CollectedLink(found, null)])); });
                return;
            }
        }

        if (page.Blocked.Contains(target.AbsoluteUri))
        {
            args.Cancel = true;
            return;
        }
        if (!page.ViaShortener || args.IsRedirected) return;

        var from = Uri.TryCreate(_webView.CoreWebView2.Source, UriKind.Absolute, out var source) ? source : null;
        if (from is null || !FileHosts.IsLinkShortener(from.Host)) return;

        var allowed = SiteKey(target.Host) == SiteKey(from.Host) ||
                      FileHosts.Find(target.Host) is not null ||
                      BrowserPageScript.LooksLikeFileUrl(target) ||
                      target.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) ||
                      page.Expected.Contains(target.AbsoluteUri) ||
                      args.IsUserInitiated ||
                      DateTime.UtcNow - page.LastClick < TimeSpan.FromSeconds(3);
        if (allowed) return;

        args.Cancel = true;
        _status.Text = $"{page.Request.Label}: skipped an ad, fetching download from {page.Request.HostName}…";
    }

    /// <summary>
    /// An ad page or a page that won't load: go back to the page before it, never open that link again, and give up on
    /// this mirror after a few (the next one is tried).
    /// </summary>
    private void LeaveBadPage(PageRequest page, Uri bad, string why)
    {
        page.Traps++;
        if (page.LastFollowed is { } followed) page.Blocked.Add(followed);
        page.Blocked.Add(bad.AbsoluteUri);
        page.LastFollowed = null;
        if (page.Traps >= 4)
        {
            EndPage(BrowserDownloadResult.Fail($"{page.Request.HostName} kept leading to ads or pages that don't load. Try another mirror."));
            return;
        }
        _status.Text = $"{page.Request.Label}: skipped a page that {why}, fetching download from {page.Request.HostName}…";
        var core = _webView.CoreWebView2;
        if (core.CanGoBack) core.GoBack();
        else core.Navigate(page.Request.PageUrl.AbsoluteUri);
    }

    // =============================================================================================
    // Collecting links (short links and FileCrypt containers)
    // =============================================================================================

    private static readonly TimeSpan PartTimeout = TimeSpan.FromSeconds(25);
    private WebView2? _popup;

    /// <summary>A real file host's page (not a short link or a link container).</summary>
    private static bool IsHostLink(Uri url) =>
        url.Scheme is "http" or "https" && FileHosts.Find(url.Host) is { Key: not ("torrent-host" or "itchio") } &&
        !FileHosts.IsLinkShortener(url.Host) && !FileHosts.IsLinkProtector(url.Host) && FileHosts.MatchFileLink(url) is not null;

    /// <summary>
    /// Links mode: once the page is a file host, its address is the answer. On a FileCrypt container (after you passed its
    /// check) SeaRaider reads the part list, then presses each part's button in turn and records the host link it leads to;
    /// with every part's link it hands the whole list back, and the window goes away. True when this took care of the tick.
    /// </summary>
    private async Task<bool> CollectLinksAsync(PageRequest page, CoreWebView2 core, Uri here, bool onFileHost, bool protector, PageState state)
    {
        if (page.Parts is null)
        {
            if (onFileHost)
            {
                EndPage(BrowserDownloadResult.Collected([new CollectedLink(here.AbsoluteUri, null)]));
                return true;
            }
            if (!protector || state.Captcha || state.Challenge) return false;

            var parts = await ReadPartsAsync(core, -1);
            if (_page != page || parts.Count == 0) return false;
            page.Parts = parts;
            page.ContainerUrl = here;
        }

        // A check on the way (the container asked again): that's yours; the window comes up for it.
        if (state.Captcha || state.Challenge) return false;
        if (page.NeedsAttention)
        {
            page.NeedsAttention = false;
            page.AttentionForCheck = false;
            _attentionChanged(page.Request.HostName, false);
            HideAway();
        }

        if (page.CurrentPart is { } current)
        {
            if (page.Collected.ContainsKey(current)) page.CurrentPart = null;
            else if (DateTime.UtcNow - page.PartStarted < PartTimeout) return true;
            else
            {
                page.CurrentPart = null;
                ClosePopup();
            }
        }

        // A part's link page took over the window: back to the list.
        if (page.ContainerUrl is { } container && !string.Equals(StripFragment(here), StripFragment(container), StringComparison.OrdinalIgnoreCase))
        {
            core.Navigate(container.AbsoluteUri);
            return true;
        }

        var next = page.Parts.FirstOrDefault(p => !page.Collected.ContainsKey(p.Index) && p.Attempts < 3);
        if (next is null)
        {
            if (page.Parts.All(p => page.Collected.ContainsKey(p.Index)))
                EndPage(BrowserDownloadResult.Collected(page.Parts.Select(p => new CollectedLink(page.Collected[p.Index], p.Name)).ToList()));
            else
                EndPage(BrowserDownloadResult.Fail($"Couldn't get every part's link from {here.Host}. Try again or pick another mirror."));
            return true;
        }

        if (next.Href is { } href && Uri.TryCreate(href, UriKind.Absolute, out var direct) && IsHostLink(direct))
        {
            page.Collected[next.Index] = direct.AbsoluteUri;
            return true;
        }

        next.Attempts++;
        page.CurrentPart = next.Index;
        page.PartStarted = DateTime.UtcNow;
        _status.Text = $"{page.Request.Label}: fetching download from {page.Request.HostName} (part {page.Collected.Count + 1} of {page.Parts.Count})…";
        await ReadPartsAsync(core, next.Index);
        return true;

        static string StripFragment(Uri u) => u.GetLeftPart(UriPartial.Query);
    }

    private static async Task<List<PartEntry>> ReadPartsAsync(CoreWebView2 core, int click)
    {
        var raw = await core.ExecuteScriptAsync(BrowserPageScript.BuildParts(click));
        var json = JsonSerializer.Deserialize<string>(raw);
        return json is null ? [] : JsonSerializer.Deserialize<List<PartEntry>>(json, JsonOptions) ?? [];
    }

    /// <summary>A part's link page, opened by its button as a pop-up: loaded in a hidden view just long enough to see the host.</summary>
    private async void OpenCapturePopup(CoreWebView2NewWindowRequestedEventArgs args, PageRequest page, int part)
    {
        var deferral = args.GetDeferral();
        try
        {
            ClosePopup();
            var view = new WebView2 { Location = new Drawing.Point(-5000, -5000), Size = new Drawing.Size(800, 600) };
            Controls.Add(view);
            _popup = view;
            await view.EnsureCoreWebView2Async(_webView.CoreWebView2.Environment);
            var popup = view.CoreWebView2;
            popup.Settings.AreDefaultScriptDialogsEnabled = false;
            popup.PermissionRequested += OnPermissionRequested;
            popup.DownloadStarting += (_, e) => e.Cancel = true;
            popup.NavigationStarting += (_, e) =>
            {
                if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) && IsHostLink(u))
                {
                    e.Cancel = true;
                    CapturePart(page, part, u);
                }
            };
            popup.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) && IsHostLink(u)) CapturePart(page, part, u);
            };
            popup.NavigationCompleted += async (_, _) =>
            {
                // The link page names the host in its markup (a frame, a link, a script) instead of going there.
                try
                {
                    var raw = await popup.ExecuteScriptAsync("document.documentElement.outerHTML");
                    if (JsonSerializer.Deserialize<string>(raw) is { } html && Uri.TryCreate(popup.Source, UriKind.Absolute, out var at) &&
                        FileHostResolver.FindShortLinkDestination(html, at, wantTorrent: false) is { } host && IsHostLink(host))
                        CapturePart(page, part, host);
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or System.Runtime.InteropServices.COMException) { }
            };
            args.NewWindow = popup;
            args.Handled = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            WebView2Runtime.Log($"Couldn't open a part's link page: {ex.Message}");
            args.Handled = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void CapturePart(PageRequest page, int part, Uri host) => BeginInvokeSafe(() =>
    {
        if (_page != page) return;
        page.Collected.TryAdd(part, host.AbsoluteUri);
        if (page.CurrentPart == part) page.CurrentPart = null;
        ClosePopup();
    });

    private void ClosePopup()
    {
        if (_popup is not { } popup) return;
        _popup = null;
        Controls.Remove(popup);
        popup.Dispose();
    }

    /// <summary>Pop-ups: follow the ones that stay on the host or are the file; drop the rest (ads).</summary>
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        if (_page is { Request.WantTorrent: true } wanting && args.Uri.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            var magnet = args.Uri;
            BeginInvokeSafe(() => { if (_page == wanting) EndPage(BrowserDownloadResult.FoundMagnet(magnet)); });
            return;
        }
        if (_page is null || !Uri.TryCreate(args.Uri, UriKind.Absolute, out var target) || target.Scheme is not ("http" or "https")) return;

        var current = Uri.TryCreate(_webView.CoreWebView2.Source, UriKind.Absolute, out var source) ? source : _page.Request.PageUrl;
        var sameSite = SiteKey(target.Host) == SiteKey(current.Host) || SiteKey(target.Host) == SiteKey(_page.Request.PageUrl.Host);

        if (_page.Blocked.Contains(target.AbsoluteUri)) return;

        if (_page.Request.CollectLinks)
        {
            var collecting = _page;
            if (IsHostLink(target))
            {
                if (collecting.CurrentPart is { } part) CapturePart(collecting, part, target);
                else if (collecting.Parts is null)
                    BeginInvokeSafe(() => { if (_page == collecting) EndPage(BrowserDownloadResult.Collected([new CollectedLink(target.AbsoluteUri, null)])); });
                return;
            }
            // A part's button opened its link page (filecrypt.cc/Link/…): let it run in a hidden view until it names the host.
            if (collecting.CurrentPart is { } waiting)
            {
                OpenCapturePopup(args, collecting, waiting);
                return;
            }
        }

        // After a shortener's Continue / Download File press, a pop-up to a known file host is the next step, not an ad.
        var shortenerStep = _page.ViaShortener && DateTime.UtcNow - _page.LastClick < TimeSpan.FromSeconds(5) &&
                            FileHosts.Find(target.Host) is not null;
        var torrentFile = _page.Request.WantTorrent && target.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase);
        // You clicked through a short link and it opened the download host in a new tab: open it here instead.
        var userToHost = _page.ViaShortener && args.IsUserInitiated && FileHosts.Find(target.Host) is not null;
        if (sameSite || shortenerStep || userToHost || torrentFile || BrowserPageScript.LooksLikeFileUrl(target))
        {
            _page.LastFollowed = target.AbsoluteUri;
            _webView.CoreWebView2.Navigate(target.AbsoluteUri);
        }
    }

    // =============================================================================================
    // Downloads
    // =============================================================================================

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs args)
    {
        var page = _page;
        if (page is null)
        {
            args.Cancel = true; // nothing asked for a file (a late ad download)
            return;
        }

        var suggested = Path.GetFileName(args.ResultFilePath);
        var accepted = page.Request.WantTorrent
            ? BrowserPageScript.IsTorrentFile(suggested, args.DownloadOperation.MimeType)
            : BrowserPageScript.IsExpectedFile(page.Request.FileName, suggested,
                args.DownloadOperation.TotalBytesToReceive is { } announced ? (ulong?)announced : null);
        if (!accepted)
        {
            args.Cancel = true; // an ad's "installer", or a tiny page: keep waiting for the real file
            _status.Text = $"{page.Request.Label}: skipped \"{suggested}\" (not the {(page.Request.WantTorrent ? "torrent" : "game file")})…";
            return;
        }

        var name = page.Request.FileName is { Length: > 0 } wanted ? wanted : BrowserPageScript.SafeFileName(suggested);
        if (page.Request.WantTorrent && !name.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)) name += ".torrent";
        var target = Path.Combine(page.Request.DestinationDirectory, name);
        Directory.CreateDirectory(page.Request.DestinationDirectory);
        try { if (File.Exists(target)) File.Delete(target); } catch (IOException) { } catch (UnauthorizedAccessException) { }

        args.ResultFilePath = target;
        args.Handled = true; // no download bubble
        page.DownloadSeen = true;

        var download = new ActiveDownload(page.Request, page.Progress, page.Result, args.DownloadOperation, target);
        Track(download, page.CancellationToken);

        // The page's work is done; the next request can use the browser while this file downloads.
        _page = null;
        if (page.NeedsAttention) _attentionChanged(page.Request.HostName, false);
        Hide(); // the page stays loaded until the next request; a second download it starts is refused (no page waiting)
        _parked = false;
        UpdateStatus();
    }

    private void Track(ActiveDownload download, CancellationToken cancellationToken)
    {
        _downloads.Add(download);
        var operation = download.Operation;

        operation.BytesReceivedChanged += (_, _) => download.Report();
        operation.StateChanged += (_, _) => OnStateChanged(download);

        // Pausing the job pauses the transfer (resumable later); cancelling the job does the same, and the file goes with
        // the job's temporary folder.
        cancellationToken.Register(() => BeginInvokeSafe(() =>
        {
            if (!_downloads.Remove(download)) return;
            try
            {
                if (operation.CanResume && operation.State == CoreWebView2DownloadState.InProgress)
                {
                    operation.Pause();
                    _paused[download.Target] = operation;
                }
                else
                {
                    operation.Cancel();
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // already finished / gone
            }
            download.Result.TrySetCanceled(cancellationToken);
            UpdateStatus();
        }));
    }

    private void OnStateChanged(ActiveDownload download)
    {
        var operation = download.Operation;
        switch (operation.State)
        {
            case CoreWebView2DownloadState.Completed:
                _downloads.Remove(download);
                download.Report();
                download.Result.TrySetResult(File.Exists(download.Target)
                    ? new BrowserDownloadResult(BrowserDownloadOutcome.Completed, download.Target, null)
                    : BrowserDownloadResult.Fail("The browser finished the download, but the file isn't there."));
                UpdateStatus();
                break;

            case CoreWebView2DownloadState.Interrupted:
                if (operation.InterruptReason == CoreWebView2DownloadInterruptReason.UserCanceled || !_downloads.Contains(download)) return;
                if (operation.CanResume && download.Resumes < MaxResumes)
                {
                    download.Resumes++;
                    var delay = new WinForms.Timer { Interval = 3000 };
                    delay.Tick += (_, _) =>
                    {
                        delay.Dispose();
                        try { operation.Resume(); }
                        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
                    };
                    delay.Start();
                    return;
                }
                _downloads.Remove(download);
                download.Result.TrySetResult(BrowserDownloadResult.Fail(
                    $"The download from {download.Request.HostName} was interrupted ({operation.InterruptReason})."));
                UpdateStatus();
                break;
        }
    }

    // =============================================================================================
    // Window
    // =============================================================================================

    private void UpdateStatus()
    {
        if (_page is not null) return;
        _status.Text = _downloads.Count switch
        {
            0 => "SeaRaider · Downloads",
            1 => $"Downloading {_downloads[0].Request.Label} in the background…",
            var n => $"Downloading {n} files in the background…",
        };
    }

    /// <summary>
    /// Quiet mode: the window is open (so the page runs at full speed: hidden pages are throttled and some hosts pause
    /// their countdown while hidden) but placed beyond the edge of the screens, without a taskbar button.
    /// </summary>
    private void Park()
    {
        if (!_parked && Visible) return; // the user has it on screen already
        var screens = WinForms.SystemInformation.VirtualScreen;
        Location = new Drawing.Point(screens.Right + 400, screens.Top + 40);
        _parked = true;
        if (!Visible) Show();
    }

    private bool _parked;

    /// <summary>Back off-screen after the user finished a human check; the page keeps running at full speed.</summary>
    private void HideAway()
    {
        var screens = WinForms.SystemInformation.VirtualScreen;
        Location = new Drawing.Point(screens.Right + 400, screens.Top + 40);
        _parked = true;
    }

    public void BringToFrontFromHost()
    {
        if (_parked)
        {
            var area = WinForms.Screen.PrimaryScreen?.WorkingArea ?? WinForms.SystemInformation.WorkingArea;
            Location = new Drawing.Point(area.Left + Math.Max(0, (area.Width - Width) / 2), area.Top + Math.Max(0, (area.Height - Height) / 2));
            _parked = false;
        }
        if (!Visible) Show();
        if (WindowState == WinForms.FormWindowState.Minimized) WindowState = WinForms.FormWindowState.Normal;
        TopMost = true;
        Activate();
        TopMost = false;
    }

    /// <summary>The user closing the window gives up on the page being opened; running downloads continue.</summary>
    private void OnFormClosing(object? sender, WinForms.FormClosingEventArgs e)
    {
        if (_allowClose || e.CloseReason != WinForms.CloseReason.UserClosing) return;
        e.Cancel = true;
        if (_page is not null)
            EndPage(BrowserDownloadResult.Fail($"The download window was closed before {_page.Request.HostName} sent the file."));
        Hide();
    }

    /// <summary>Called by the service when the app exits or the window has been idle.</summary>
    public void CloseFromHost()
    {
        _allowClose = true;
        EndPage(BrowserDownloadResult.Fail("SeaRaider is closing."));
        foreach (var download in _downloads.ToList())
        {
            try { if (download.Operation.CanResume) download.Operation.Pause(); else download.Operation.Cancel(); }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
            download.Result.TrySetResult(BrowserDownloadResult.Fail("SeaRaider is closing."));
        }
        _downloads.Clear();
        Close();
        WinForms.Application.ExitThread();
    }

    private void BeginInvokeSafe(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }

    private static string SiteKey(string host)
    {
        var parts = host.ToLowerInvariant().Split('.');
        return parts.Length <= 2 ? string.Join('.', parts) : string.Join('.', parts[^2..]);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tick.Dispose();
            _webView.Dispose();
        }
        base.Dispose(disposing);
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed record PageState(bool Dead, bool Captcha, bool Challenge, bool Waiting, bool Trap, string? Href, string? Clicked, string? Magnet);

    private sealed class PageRequest(BrowserDownloadRequest request, IProgress<TransferProgress>? progress, bool autoClick,
        TaskCompletionSource<BrowserDownloadResult> result, CancellationToken cancellationToken)
    {
        private readonly Stopwatch _age = Stopwatch.StartNew();
        public BrowserDownloadRequest Request { get; } = request;
        public IProgress<TransferProgress>? Progress { get; } = progress;
        public bool AutoClick { get; } = autoClick;
        public TaskCompletionSource<BrowserDownloadResult> Result { get; } = result;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public TimeSpan Elapsed => _age.Elapsed;
        public DateTime LastNavigation { get; set; } = DateTime.UtcNow;
        public DateTime LastClick { get; set; } = DateTime.MinValue;
        public int Clicks { get; set; }
        public bool DownloadSeen { get; set; }
        public bool NeedsAttention { get; set; }
        public bool Inspecting { get; set; }
        public HashSet<string> FollowedLinks { get; } = new(StringComparer.Ordinal);

        /// <summary>The link was a shortener (Zovo): wait screen first, then the download page it leads to.</summary>
        public bool ViaShortener { get; } = FileHosts.IsLinkShortener(request.PageUrl.Host);

        /// <summary>The last link the window followed (to block it if it turns out to be an ad trap).</summary>
        public string? LastFollowed { get; set; }

        /// <summary>Links that led to ad traps: skipped by the page script and the pop-up rules.</summary>
        public HashSet<string> Blocked { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int Traps { get; set; }

        /// <summary>Links SeaRaider itself chose to open (allowed away from a shortener).</summary>
        public HashSet<string> Expected { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The window came forward for a captcha / browser check (and goes away again once it's done).</summary>
        public bool AttentionForCheck { get; set; }

        /// <summary>Links mode: a container's part list (null until read), and the host link found for each part.</summary>
        public List<PartEntry>? Parts { get; set; }
        public Uri? ContainerUrl { get; set; }
        public Dictionary<int, string> Collected { get; } = new();
        public int? CurrentPart { get; set; }
        public DateTime PartStarted { get; set; }
    }

    /// <summary>One row of a link container: its position, the file it names, and its link when the row has a plain one.</summary>
    private sealed class PartEntry
    {
        public int Index { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Href { get; set; }
        public int Attempts { get; set; }
    }

    private sealed class ActiveDownload(BrowserDownloadRequest request, IProgress<TransferProgress>? progress,
        TaskCompletionSource<BrowserDownloadResult> result, CoreWebView2DownloadOperation operation, string target)
    {
        private long _lastBytes;
        private DateTime _lastTime = DateTime.UtcNow;
        private double _rate;

        public BrowserDownloadRequest Request { get; } = request;
        public TaskCompletionSource<BrowserDownloadResult> Result { get; } = result;
        public CoreWebView2DownloadOperation Operation { get; } = operation;
        public string Target { get; } = target;
        public int Resumes { get; set; }

        public void Report()
        {
            if (progress is null) return;
            var bytes = Operation.BytesReceived;
            var now = DateTime.UtcNow;
            var seconds = (now - _lastTime).TotalSeconds;
            if (seconds >= 0.5)
            {
                var instant = Math.Max(0, bytes - _lastBytes) / seconds;
                _rate = _rate == 0 ? instant : _rate * 0.7 + instant * 0.3;
                _lastBytes = bytes;
                _lastTime = now;
            }
            var total = Operation.TotalBytesToReceive is { } t ? (long)t : 0;
            progress.Report(new TransferProgress(bytes, total, _rate));
        }
    }
}
