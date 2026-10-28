using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SeaRaider.Core;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Models;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace SeaRaider.App.Verification;

/// <summary>
/// Plays browser games (itch.io HTML5) in SeaRaider's own game window: borderless full screen, on its own thread, with
/// the game's saves (its browser storage) kept between sessions. The play session lasts as long as the window is open,
/// so play time and friends' "playing" work like any installed game.
/// </summary>
internal sealed class WebGameHost : IWebGameHost
{
    public bool IsAvailable => OperatingSystem.IsWindows();

    public Task PlayAsync(InstalledGame game, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var window = new WebGameWindow(game);
                using var stop = cancellationToken.Register(window.CloseFromHost);
                WinForms.Application.Run(window);
                done.TrySetResult();
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "SeaRaider game window",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }
}

/// <summary>One browser game, full screen. Shift+Esc (or Alt+F4) closes it; F11 switches between full screen and a window.</summary>
internal sealed class WebGameWindow : WinForms.Form
{
    /// <summary>Runs before the game's own scripts, so the game can't swallow SeaRaider's keys.</summary>
    private const string KeyScript = """
        window.addEventListener('keydown', e => {
          if (e.key === 'Escape' && e.shiftKey) { e.preventDefault(); e.stopImmediatePropagation(); window.chrome.webview.postMessage('quit'); }
          else if (e.key === 'F11') { e.preventDefault(); e.stopImmediatePropagation(); window.chrome.webview.postMessage('toggle'); }
        }, true);
        """;

    private readonly InstalledGame _game;
    private readonly WebView2 _webView = new() { Dock = WinForms.DockStyle.Fill, DefaultBackgroundColor = Drawing.Color.Black };
    private readonly WinForms.Label _hint = new();
    private readonly WinForms.Timer _hintTimer = new() { Interval = 4000 };
    private bool _fullScreen = true;
    private Drawing.Rectangle _windowedBounds;

    public WebGameWindow(InstalledGame game)
    {
        _game = game;
        Text = game.Title;
        BackColor = Drawing.Color.Black;
        StartPosition = WinForms.FormStartPosition.Manual;
        KeyPreview = true;

        _hint.Text = "Shift+Esc to quit  ·  F11 window / full screen";
        _hint.AutoSize = true;
        _hint.Padding = new WinForms.Padding(14, 8, 14, 8);
        _hint.BackColor = Drawing.Color.FromArgb(0x0F, 0x21, 0x31);
        _hint.ForeColor = Drawing.Color.FromArgb(0xE3, 0xED, 0xF2);
        _hint.Font = new Drawing.Font("Segoe UI", 10f);
        _hint.Anchor = WinForms.AnchorStyles.Top;

        Controls.Add(_webView);
        Controls.Add(_hint);
        _hint.BringToFront();
        _hintTimer.Tick += (_, _) =>
        {
            _hintTimer.Stop();
            _hint.Visible = false;
        };

        // The screen SeaRaider is on, the whole of it.
        var screen = WinForms.Screen.FromPoint(WinForms.Cursor.Position);
        _windowedBounds = new Drawing.Rectangle(
            screen.WorkingArea.Left + (screen.WorkingArea.Width - 1280) / 2,
            screen.WorkingArea.Top + (screen.WorkingArea.Height - 800) / 2, 1280, 800);
        ApplyFullScreen(true, screen);

        Load += async (_, _) => await StartAsync();
        Shown += (_, _) =>
        {
            CenterHint();
            _hintTimer.Start();
            Activate();
        };
        Resize += (_, _) => CenterHint();
    }

    /// <summary>SeaRaider is closing (or the session was stopped): close the game.</summary>
    public void CloseFromHost()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(Close); }
        catch (InvalidOperationException) { }
    }

    private async Task StartAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(
                WebView2Runtime.FindFixedVersionFolder(), Path.Combine(AppPaths.DataDirectory, "webgames"));
            await _webView.EnsureCoreWebView2Async(environment);

            var core = _webView.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false; // F5, Ctrl+P … belong to the game
            await core.AddScriptToExecuteOnDocumentCreatedAsync(KeyScript);

            core.WebMessageReceived += (_, e) =>
            {
                switch (e.TryGetWebMessageAsString())
                {
                    case "quit": Close(); break;
                    case "toggle": ApplyFullScreen(!_fullScreen, WinForms.Screen.FromControl(this)); break;
                }
            };
            // Links the game opens (the author's page, a store) go to the normal browser; ads don't open at all, even when
            // the click on "play" is what asked for them (pop-up / pop-under networks, see AdHosts).
            // Only the game's own site and the usual places (a store, a community page) are opened: a pop-up to anything else is an
            // ad that got past the host list.
            var gamePage = Uri.TryCreate(_game.WebGameUrl, UriKind.Absolute, out var gameUri) ? gameUri : null;
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (e.IsUserInitiated && Uri.TryCreate(e.Uri, UriKind.Absolute, out var link) && link.Scheme is "http" or "https" &&
                    SeaRaider.App.Services.AdShield.IsSafeLink(link, gamePage))
                {
                    try { Process.Start(new ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true }); }
                    catch (System.ComponentModel.Win32Exception) { }
                }
            };
            core.PermissionRequested += (_, e) =>
            {
                if (e.PermissionKind is CoreWebView2PermissionKind.Notifications or CoreWebView2PermissionKind.Geolocation)
                    e.State = CoreWebView2PermissionState.Deny;
            };
            core.DownloadStarting += (_, e) => e.Cancel = true;
            // The game page (or a frame in it) sending the window to an ad: stopped.
            core.NavigationStarting += (_, e) =>
            {
                if (Core.Scraping.AdHosts.IsAd(e.Uri)) e.Cancel = true;
            };
            core.FrameNavigationStarting += (_, e) =>
            {
                if (Core.Scraping.AdHosts.IsAdRequest(e.Uri)) e.Cancel = true;
            };
            // Pop-ups only for a real click, click-catching layers removed, ad frames taken out (see AdShield).
            await SeaRaider.App.Services.AdShield.InstallAsync(core, SeaRaider.App.Services.AdShieldMode.Game);

            var headers = _game.WebGamePageUrl is { Length: > 0 } page ? $"Referer: {page}" : null;
            core.NavigateWithWebResourceRequest(core.Environment.CreateWebResourceRequest(_game.WebGameUrl!, "GET", null, headers));
            _webView.Focus();
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            WebView2Runtime.Log($"Game window failed: {ex}");
            WinForms.MessageBox.Show(this, $"{_game.Title} couldn't start: SeaRaider's built-in browser (WebView2) isn't working.\n\n{ex.Message}",
                "SeaRaider", WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Warning);
            Close();
        }
    }

    private void ApplyFullScreen(bool fullScreen, WinForms.Screen screen)
    {
        if (fullScreen && !_fullScreen) _windowedBounds = Bounds;
        _fullScreen = fullScreen;
        WindowState = WinForms.FormWindowState.Normal;
        FormBorderStyle = fullScreen ? WinForms.FormBorderStyle.None : WinForms.FormBorderStyle.Sizable;
        Bounds = fullScreen ? screen.Bounds : _windowedBounds;
        CenterHint();
    }

    private void CenterHint()
    {
        if (_hint.IsDisposed) return;
        _hint.Location = new Drawing.Point(Math.Max(0, (ClientSize.Width - _hint.Width) / 2), 24);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hintTimer.Dispose();
            _webView.Dispose();
        }
        base.Dispose(disposing);
    }
}
