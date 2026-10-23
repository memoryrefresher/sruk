using System.Diagnostics;
using SeaRaider.Core.WebChallenge;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace SeaRaider.App.Verification;

/// <summary>
/// Fallback when the built-in browser can't run on this PC: the page opens in the user's own browser, they get past the
/// check there, and hand the file link back to SeaRaider. SeaRaider watches the clipboard, so "Copy download link"
/// in the browser's download list (or on the host's button) is enough; pasting works too.
///
/// <para>Browsers keep their cookies encrypted, so this can't carry a session over. It works for file hosts that give a
/// signed, time-limited download link (most do). For sites that only check the browser itself (a Cloudflare page in
/// front of a search), it explains why it can't continue and offers to install the WebView2 Runtime instead.</para>
/// </summary>
internal sealed class BrowserHandoffWindow : WinForms.Form
{
    private readonly ChallengeRequest _request;
    private readonly TaskCompletionSource<ChallengeResolution> _result;
    private readonly WinForms.TextBox _link = new();
    private readonly WinForms.Button _continue;
    private readonly WinForms.Label _hint = new();
    private readonly WinForms.Timer _clipboardTimer = new() { Interval = 800 };
    private string? _lastClipboard;

    public BrowserHandoffWindow(ChallengeRequest request, TaskCompletionSource<ChallengeResolution> result)
    {
        _request = request;
        _result = result;

        var background = Drawing.Color.FromArgb(0x0F, 0x21, 0x31);
        var foreground = Drawing.Color.FromArgb(0xE3, 0xED, 0xF2);
        var muted = Drawing.Color.FromArgb(0x8E, 0xA6, 0xB5);

        Text = $"SeaRaider · Continue in your browser ({request.Url.Host})";
        StartPosition = WinForms.FormStartPosition.CenterScreen;
        ClientSize = new Drawing.Size(620, 330);
        FormBorderStyle = WinForms.FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        BackColor = background;
        ForeColor = foreground;
        Font = new Drawing.Font("Segoe UI", 10f);
        Padding = new WinForms.Padding(20);

        var expectsFile = request.Kind != ChallengeKind.BotCheck;
        var steps = new WinForms.Label
        {
            Dock = WinForms.DockStyle.Top,
            Height = 150,
            ForeColor = foreground,
            Text = expectsFile
                ? "SeaRaider's built-in browser can't run on this PC, so let's use yours:\n\n" +
                  "1.  Click Open in browser and get past the check there.\n" +
                  "2.  Click the host's download button. When your browser starts downloading, cancel it.\n" +
                  "3.  Right-click the download (or the button) and choose Copy link. SeaRaider picks it up from the\n" +
                  "     clipboard, or paste it below. Then click Continue."
                : $"{request.Url.Host} checks that it's talking to a real browser. SeaRaider's built-in browser can't run on this " +
                  "PC, and a check passed in your own browser can't be handed over (browsers keep those cookies locked).\n\n" +
                  "Install the Microsoft Edge WebView2 Runtime and try again, or open the page in your browser.",
        };

        _link.Dock = WinForms.DockStyle.Top;
        _link.PlaceholderText = "https://… (the file's download link)";
        _link.BackColor = Drawing.Color.FromArgb(0x16, 0x30, 0x44);
        _link.ForeColor = foreground;
        _link.BorderStyle = WinForms.BorderStyle.FixedSingle;
        _link.Visible = expectsFile;
        _link.TextChanged += (_, _) => _continue!.Enabled = IsFileLink(_link.Text);

        _hint.Dock = WinForms.DockStyle.Top;
        _hint.Height = 28;
        _hint.ForeColor = muted;
        _hint.Text = expectsFile ? "Watching the clipboard for a download link…" : string.Empty;

        var open = Button("Open in browser", Drawing.Color.FromArgb(0x16, 0x30, 0x44), foreground);
        open.Click += (_, _) => OpenInBrowser();

        _continue = Button("Continue", Drawing.Color.FromArgb(0x3F, 0xE0, 0xC5), Drawing.Color.FromArgb(0x07, 0x14, 0x20));
        _continue.Enabled = false;
        _continue.Visible = expectsFile;
        _continue.Click += (_, _) => Finish(new Uri(_link.Text.Trim()));

        var install = Button("Install WebView2", Drawing.Color.FromArgb(0x3F, 0xE0, 0xC5), Drawing.Color.FromArgb(0x07, 0x14, 0x20));
        install.Visible = !expectsFile;
        install.Click += async (_, _) =>
        {
            install.Enabled = false;
            var ok = await WebView2Runtime.InstallEvergreenAsync(text => _hint.Text = text, CancellationToken.None);
            _result.TrySetResult(ChallengeResolution.Failed(ok
                ? "The WebView2 Runtime is installed now. Try again: the verification window will open."
                : $"Installing the WebView2 Runtime didn't work. Details: {WebView2Runtime.LogPath}"));
            Close();
        };

        var cancel = Button("Cancel", Drawing.Color.FromArgb(0x16, 0x30, 0x44), foreground);
        cancel.Click += (_, _) => Close();

        var buttons = new WinForms.FlowLayoutPanel
        {
            Dock = WinForms.DockStyle.Bottom,
            Height = 52,
            FlowDirection = WinForms.FlowDirection.RightToLeft,
            BackColor = background,
        };
        buttons.Controls.AddRange([_continue, install, cancel, open]);

        Controls.Add(_hint);
        Controls.Add(_link);
        Controls.Add(steps);
        Controls.Add(buttons);

        _clipboardTimer.Tick += (_, _) => CheckClipboard();
        FormClosed += (_, _) => _result.TrySetResult(ChallengeResolution.Failed("Cancelled."));
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        TopMost = true;
        Activate();
        TopMost = false;
        try { _lastClipboard = WinForms.Clipboard.ContainsText() ? WinForms.Clipboard.GetText() : null; }
        catch (System.Runtime.InteropServices.ExternalException) { }
        if (_link.Visible) _clipboardTimer.Start();
    }

    private void OpenInBrowser()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_request.Url.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _hint.Text = $"Couldn't open the browser: {ex.Message}";
        }
    }

    /// <summary>A newly copied http(s) link that isn't the page itself fills the box.</summary>
    private void CheckClipboard()
    {
        string? text;
        try
        {
            text = WinForms.Clipboard.ContainsText() ? WinForms.Clipboard.GetText().Trim() : null;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return; // clipboard busy
        }

        if (text is null || text == _lastClipboard) return;
        _lastClipboard = text;
        if (!IsFileLink(text) || string.Equals(text, _request.Url.AbsoluteUri, StringComparison.OrdinalIgnoreCase)) return;

        _link.Text = text;
        _hint.Text = "Got a link from the clipboard. Click Continue.";
        _continue.Focus();
    }

    private static bool IsFileLink(string text) =>
        Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private void Finish(Uri link)
    {
        _clipboardTimer.Stop();
        _result.TrySetResult(new ChallengeResolution(true, null, [], link, null));
        Close();
    }

    private static WinForms.Button Button(string text, Drawing.Color back, Drawing.Color fore)
    {
        var button = new WinForms.Button
        {
            Text = text,
            AutoSize = true,
            Height = 36,
            Padding = new WinForms.Padding(12, 2, 12, 2),
            Margin = new WinForms.Padding(8, 8, 0, 0),
            FlatStyle = WinForms.FlatStyle.Flat,
            BackColor = back,
            ForeColor = fore,
            Font = new Drawing.Font("Segoe UI Semibold", 10f),
            Cursor = WinForms.Cursors.Hand,
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    public void CancelFromHost()
    {
        _result.TrySetResult(ChallengeResolution.Failed("Cancelled."));
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _clipboardTimer.Dispose();
        base.Dispose(disposing);
    }
}
