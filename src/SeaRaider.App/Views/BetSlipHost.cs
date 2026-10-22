using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Microsoft.Web.WebView2.Core;
using SeaRaider.App.Verification;
using SeaRaider.Core;
using SeaRaider.Core.Scraping;

namespace SeaRaider.App.Views;

/// <summary>
/// The bet slip beside SeaRaider's player: the sportsbook's own web page (Roobet by default, Settings › General › Bet slip) in a
/// browser hosted in the window, like the player itself (<see cref="TrailerPlayerHost"/>).
///
/// <para>It's the site as it is, nothing added: the user signs in there with their own account and places their own bets.
/// SeaRaider never sees the account, the balance or the bets. The sign-in is kept in its own browser profile
/// (%LOCALAPPDATA%\SeaRaider\webview2-betslip), apart from the player's and the downloads' browsers. Pop-up and ad networks
/// (<see cref="AdHosts"/>) are kept out; the site's own sign-in windows open normally.</para>
/// </summary>
public sealed class BetSlipHost : NativeControlHost
{
    private readonly string _url;
    private IntPtr _hwnd;
    private CoreWebView2Controller? _controller;
    private bool _destroyed;

    public BetSlipHost(string url) => _url = url;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var handle = base.CreateNativeControlCore(parent);
        _hwnd = handle.Handle;
        _destroyed = false;
        _ = StartAsync();
        return handle;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _destroyed = true;
        var controller = _controller;
        _controller = null;
        try { controller?.Close(); }
        catch (Exception ex) { WebView2Runtime.Log($"Closing the bet slip: {ex.Message}"); }
        base.DestroyNativeControlCore(control);
    }

    private async Task StartAsync()
    {
        try
        {
            var profile = Path.Combine(AppPaths.DataDirectory, "webview2-betslip");
            Directory.CreateDirectory(profile);
            var environment = await CoreWebView2Environment.CreateAsync(WebView2Runtime.FindFixedVersionFolder(), profile);
            if (_destroyed) return;
            var controller = await environment.CreateCoreWebView2ControllerAsync(_hwnd);
            if (_destroyed)
            {
                controller.Close();
                return;
            }
            _controller = controller;
            controller.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x0B, 0x18, 0x24);
            UpdateBounds();

            var core = controller.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.NewWindowRequested += (_, args) =>
            {
                // Ads never open; the site's own windows (signing in with Google and the like) do.
                if (AdHosts.IsAd(args.Uri)) args.Handled = true;
            };
            core.FrameNavigationStarting += (_, args) =>
            {
                if (AdHosts.IsAd(args.Uri)) args.Cancel = true;
            };
            core.PermissionRequested += (_, args) =>
            {
                if (args.PermissionKind is CoreWebView2PermissionKind.Notifications or CoreWebView2PermissionKind.Geolocation
                    or CoreWebView2PermissionKind.Camera or CoreWebView2PermissionKind.Microphone)
                    args.State = CoreWebView2PermissionState.Deny;
            };
            core.Navigate(_url);
        }
        catch (Exception ex)
        {
            WebView2Runtime.Log($"Bet slip failed: {ex}");
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty) UpdateBounds();
    }

    private void UpdateBounds()
    {
        if (_controller is null) return;
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        _controller.Bounds = new System.Drawing.Rectangle(0, 0,
            Math.Max(1, (int)Math.Ceiling(Bounds.Width * scale)),
            Math.Max(1, (int)Math.Ceiling(Bounds.Height * scale)));
    }
}
