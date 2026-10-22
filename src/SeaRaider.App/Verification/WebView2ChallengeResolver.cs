using Microsoft.Extensions.Logging;
using SeaRaider.Core;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Scraping;
using SeaRaider.Core.WebChallenge;
using WinForms = System.Windows.Forms;

namespace SeaRaider.App.Verification;

/// <summary>
/// Desktop implementation of <see cref="IChallengeResolver"/>: opens a <see cref="CaptchaResolverWindow"/> on a
/// dedicated STA thread (WebView2 needs one, and it keeps Avalonia's UI thread free) and waits for the result.
/// One window at a time; requests from other downloads wait their turn.
/// When the built-in browser can't start at all (no usable WebView2 Runtime, even after trying to install it), the
/// request falls back to <see cref="BrowserHandoffWindow"/>: the user's own browser plus a pasted / copied file link.
/// </summary>
public sealed class WebView2ChallengeResolver : IChallengeResolver, IBrowserPlayerFinder
{
    private readonly ILogger<WebView2ChallengeResolver> logger;
    private readonly ILibraryRepository repository;
    private readonly BrowserDownloadService downloads;
    private readonly SemaphoreSlim _oneWindowAtATime = new(1, 1);
    private WinForms.Form? _current;

    /// <summary>
    /// The WebView2 profile folder that last started successfully. The verification window may have had to swap the usual
    /// folder for a fresh one; the verified session (clearance cookies) lives there, so later windows must use it too.
    /// </summary>
    private string? _workingProfile;

    private string ProfileFolder()
    {
        var folder = _workingProfile is { } known && Directory.Exists(known) ? known : Path.Combine(AppPaths.DataDirectory, "webview2");
        Directory.CreateDirectory(folder);
        return folder;
    }

    public WebView2ChallengeResolver(ILogger<WebView2ChallengeResolver> logger, ILibraryRepository repository,
        BrowserDownloadService downloads)
    {
        this.logger = logger;
        this.repository = repository;
        this.downloads = downloads;
        // The download browser's "needs you" prompts use the same banner as verification windows.
        downloads.PromptChanged += (_, prompt) => PromptChanged?.Invoke(this, prompt);
    }

    public bool IsAvailable => OperatingSystem.IsWindows();

    public event EventHandler<ChallengePrompt>? PromptChanged;

    public async Task<ChallengeResolution> ResolveAsync(ChallengeRequest request, CancellationToken cancellationToken)
    {
        await _oneWindowAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var userDataFolder = ProfileFolder();
            var allowInstall = repository.GetSettings().AutoInstallWebView2;

            PromptChanged?.Invoke(this, new ChallengePrompt(request.Url.Host, request.Kind, IsOpen: true));

            // Paused / cancelled download while a window is open: close it.
            await using var registration = cancellationToken.Register(() => InvokeOnWindow(w =>
            {
                if (w is CaptchaResolverWindow captcha) captcha.CancelFromHost();
                else if (w is BrowserHandoffWindow handoff) handoff.CancelFromHost();
            }));

            var runtimeUnavailable = false;
            string? usedFolder = null;
            var resolution = await RunWindowAsync<ChallengeResolution>("SeaRaider verification window", result =>
            {
                var window = new CaptchaResolverWindow(request, userDataFolder, allowInstall, result);
                window.FormClosed += (_, _) =>
                {
                    runtimeUnavailable = window.RuntimeUnavailable;
                    usedFolder = window.UserDataFolder;
                };
                return window;
            }, ChallengeResolution.Failed).ConfigureAwait(false);
            if (resolution.Solved && usedFolder is not null) _workingProfile = usedFolder;
            cancellationToken.ThrowIfCancellationRequested();

            if (!resolution.Solved && runtimeUnavailable)
            {
                logger.LogWarning("WebView2 unavailable; offering the system browser for {Host}", request.Url.Host);
                resolution = await RunWindowAsync<ChallengeResolution>("SeaRaider browser hand-off",
                    result => new BrowserHandoffWindow(request, result), ChallengeResolution.Failed).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            logger.LogInformation("Verification for {Host}: {Outcome}", request.Url.Host,
                resolution.Solved ? $"solved ({resolution.Cookies.Count} cookies)" : resolution.Message);
            return resolution;
        }
        finally
        {
            PromptChanged?.Invoke(this, new ChallengePrompt(request.Url.Host, request.Kind, IsOpen: false));
            _oneWindowAtATime.Release();
        }
    }

    /// <summary>
    /// Shows a WinForms window on its own STA thread (WebView2 and the clipboard need one) and returns its result once the
    /// window has closed.
    /// </summary>
    private async Task<T> RunWindowAsync<T>(string threadName,
        Func<TaskCompletionSource<T>, WinForms.Form> create, Func<string, T> failed)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                WinForms.Application.EnableVisualStyles();
                using var window = create(result);
                _current = window;
                WinForms.Application.Run(window);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "{Window} failed", threadName);
                WebView2Runtime.Log($"{threadName} failed: {ex}");
                result.TrySetResult(failed(ex.Message));
            }
            finally
            {
                _current = null;
                closed.TrySetResult();
            }
        })
        {
            IsBackground = true,
            Name = threadName,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var resolution = await result.Task.ConfigureAwait(false);
        try
        {
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); // flags set on close are final now
        }
        catch (TimeoutException)
        {
            // the window thread is slow to exit; the result is what counts
        }
        return resolution;
    }

    /// <summary>
    /// Loads <paramref name="url"/> in the verified WebView2 profile and returns the browser's own copy of the page (see
    /// <see cref="BrowserPageFetchWindow"/>). Used when SeaRaider's HTTP client is still blocked after a verification.
    /// </summary>
    public async Task<BrowserPage?> FetchPageAsync(Uri url, CancellationToken cancellationToken)
    {
        await _oneWindowAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var userDataFolder = ProfileFolder();
            WebView2Runtime.Log($"Browser fetch of {url} starting (profile {userDataFolder})");

            await using var registration = cancellationToken.Register(() => InvokeOnWindow(w =>
            {
                if (w is BrowserPageFetchWindow fetch) fetch.CancelFromHost();
            }));

            var page = await RunWindowAsync<BrowserPage?>("SeaRaider browser page fetch",
                result => new BrowserPageFetchWindow(url, userDataFolder, result), _ => null).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            logger.LogInformation("Browser fetch of {Url}: {Outcome}", url, page is null ? "no page" : $"{page.Content.Length} chars");
            WebView2Runtime.Log($"Browser fetch of {url}: {(page is null ? "no page returned" : $"{page.Content.Length} chars, {page.ContentType}")}");
            return page;
        }
        finally
        {
            _oneWindowAtATime.Release();
        }
    }

    /// <summary>
    /// Opens a title's page in a hidden browser, clicks the chosen stream button and returns the player that loads (see
    /// <see cref="MediaPlayerFinderWindow"/>). Media tab, Settings › General › Media player.
    /// </summary>
    public async Task<string?> FindPlayerAsync(Uri titlePage, PlayerPreference preference, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return null;
        await _oneWindowAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var userDataFolder = ProfileFolder();
            await using var registration = cancellationToken.Register(() => InvokeOnWindow(w =>
            {
                if (w is MediaPlayerFinderWindow finder) finder.CancelFromHost();
            }));

            var player = await RunWindowAsync<string?>("SeaRaider media player finder",
                result => new MediaPlayerFinderWindow(titlePage, preference, userDataFolder, result), _ => null).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogInformation("Player for {Page}: {Player}", titlePage, player ?? "none found");
            return player;
        }
        finally
        {
            _oneWindowAtATime.Release();
        }
    }

    public void BringToFront()
    {
        if (_current is null && downloads.BringToFront()) return;
        BringVerificationWindowToFront();
    }

    private void BringVerificationWindowToFront() => InvokeOnWindow(w =>
    {
        if (w is CaptchaResolverWindow captcha) captcha.BringToFrontFromHost();
        else
        {
            w.TopMost = true;
            w.Activate();
            w.TopMost = false;
        }
    });

    private void InvokeOnWindow(Action<WinForms.Form> action)
    {
        var window = _current;
        if (window is null || window.IsDisposed || !window.IsHandleCreated) return;
        try
        {
            window.BeginInvoke(() => action(window));
        }
        catch (InvalidOperationException)
        {
            // Window closed between the check and the call.
        }
    }
}
