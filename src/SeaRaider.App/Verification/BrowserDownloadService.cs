using Microsoft.Extensions.Logging;
using SeaRaider.Core;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Downloads;
using SeaRaider.Core.WebChallenge;
using WinForms = System.Windows.Forms;

namespace SeaRaider.App.Verification;

/// <summary>
/// Desktop implementation of <see cref="IBrowserDownloader"/>: runs one <see cref="BrowserDownloadWindow"/> on its own
/// STA thread, created on first use and closed after a minute with nothing to do. Pages are opened one at a time (a
/// captcha needs the user's attention); the downloads they start run side by side.
/// Shares the verification window's browser profile, so a host verified there is verified here too.
/// </summary>
public sealed class BrowserDownloadService(ILogger<BrowserDownloadService> logger, ILibraryRepository repository)
    : IBrowserDownloader, IDisposable
{
    private static readonly TimeSpan IdleClose = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _onePage = new(1, 1);
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private BrowserDownloadWindow? _window;
    private bool _unavailable;
    private System.Threading.Timer? _idleTimer;

    public bool IsAvailable => OperatingSystem.IsWindows() && !_unavailable;

    /// <summary>A page needs the user (captcha / download button): drives the main window's banner.</summary>
    public event EventHandler<ChallengePrompt>? PromptChanged;

    public async Task<BrowserDownloadResult> DownloadAsync(BrowserDownloadRequest request, IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var window = await EnsureWindowAsync().ConfigureAwait(false);
        if (window is null)
            return new BrowserDownloadResult(BrowserDownloadOutcome.Unavailable, null,
                $"SeaRaider's built-in browser (WebView2) can't run on this PC. Details: {WebView2Runtime.LogPath}");

        var autoClick = repository.GetSettings().BrowserAutoClick;

        // One page at a time; the gate is released as soon as the page has handed over its download.
        await _onePage.WaitAsync(cancellationToken).ConfigureAwait(false);
        Task<BrowserDownloadResult> transfer;
        try
        {
            var started = new TaskCompletionSource<Task<BrowserDownloadResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!Invoke(window, () => started.TrySetResult(window.Start(request, progress, autoClick, cancellationToken))))
                return BrowserDownloadResult.Fail("The download window closed.");
            transfer = await started.Task.ConfigureAwait(false);

            // Wait until the page is done with: download started (the window moves on), dead link, or failure.
            await Task.WhenAny(transfer, PageDoneAsync(window, transfer, cancellationToken)).ConfigureAwait(false);
        }
        finally
        {
            _onePage.Release();
        }

        try
        {
            var result = await transfer.ConfigureAwait(false);
            logger.LogInformation("Browser download from {Host}: {Outcome} {Message}", request.HostName, result.Outcome, result.Message);
            return result;
        }
        finally
        {
            ArmIdleTimer();
        }
    }

    /// <summary>Completes when the window has no page open any more (its download is running in the background).</summary>
    private static async Task PageDoneAsync(BrowserDownloadWindow window, Task transfer, CancellationToken cancellationToken)
    {
        while (!transfer.IsCompleted && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
            if (window.IsDisposed || !window.HasPage) return;
        }
    }

    private async Task<BrowserDownloadWindow?> EnsureWindowAsync()
    {
        if (_unavailable || !OperatingSystem.IsWindows()) return null;
        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_window is { IsDisposed: false } existing) return existing;

            var userDataFolder = Path.Combine(AppPaths.DataDirectory, "webview2");
            Directory.CreateDirectory(userDataFolder);
            var allowInstall = repository.GetSettings().AutoInstallWebView2;
            var created = new TaskCompletionSource<BrowserDownloadWindow?>(TaskCreationOptions.RunContinuationsAsynchronously);

            var thread = new Thread(() =>
            {
                BrowserDownloadWindow? window = null;
                try
                {
                    WinForms.Application.EnableVisualStyles();
                    window = new BrowserDownloadWindow(userDataFolder, allowInstall, OnAttentionChanged);
                    _ = window.Handle; // a window handle without showing the window (needed for BeginInvoke)
                    window.BeginInvoke(new Action(window.Initialize));
                    created.TrySetResult(window);
                    WinForms.Application.Run();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Download browser window failed");
                    WebView2Runtime.Log($"Download browser window failed: {ex}");
                    created.TrySetResult(null);
                    window?.Ready.TrySetResult(false);
                }
                finally
                {
                    window?.Dispose();
                    if (ReferenceEquals(_window, window)) _window = null;
                }
            })
            {
                IsBackground = true,
                Name = "SeaRaider download browser",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            var window = await created.Task.ConfigureAwait(false);
            if (window is null || !await window.Ready.Task.WaitAsync(TimeSpan.FromMinutes(8)).ConfigureAwait(false))
            {
                _unavailable = true;
                if (window is not null) Invoke(window, window.CloseFromHost);
                logger.LogWarning("Download browser unavailable; file hosts fall back to the verification window");
                return null;
            }

            _window = window;
            return window;
        }
        catch (TimeoutException)
        {
            _unavailable = true;
            return null;
        }
        finally
        {
            _startGate.Release();
        }
    }

    private void OnAttentionChanged(string host, bool needsYou) =>
        PromptChanged?.Invoke(this, new ChallengePrompt(host, ChallengeKind.BrowserDownload, needsYou));

    /// <summary>Brings the download window forward (from the main window's banner).</summary>
    public bool BringToFront()
    {
        var window = _window;
        if (window is null) return false;
        if (!window.HasPage) return false;
        return Invoke(window, window.BringToFrontFromHost);
    }

    /// <summary>Closes the window once it has had nothing to do for a while (it's recreated on demand).</summary>
    private void ArmIdleTimer()
    {
        _idleTimer?.Dispose();
        _idleTimer = new System.Threading.Timer(_ =>
        {
            var window = _window;
            if (window is null) return;
            Invoke(window, () =>
            {
                if (!window.IsIdle) return;
                _window = null;
                window.CloseFromHost();
            });
        }, null, IdleClose, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Runs <paramref name="action"/> on the window's thread. False when the window is gone.</summary>
    private static bool Invoke(BrowserDownloadWindow window, Action action)
    {
        if (window.IsDisposed || !window.IsHandleCreated) return false;
        try
        {
            window.BeginInvoke(action);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _idleTimer?.Dispose();
        if (_window is { } window) Invoke(window, window.CloseFromHost);
    }
}
