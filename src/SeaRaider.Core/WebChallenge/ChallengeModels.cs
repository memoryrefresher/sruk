using System.Net;

namespace SeaRaider.Core.WebChallenge;

/// <summary>What stood between a request and the content.</summary>
public enum ChallengeKind
{
    None,

    /// <summary>Bot-protection interstitial (Cloudflare "Just a moment…", DDoS-Guard, Sucuri).</summary>
    BotCheck,

    /// <summary>A page with a reCAPTCHA / hCaptcha / Turnstile widget where a file was expected.</summary>
    Captcha,

    /// <summary>A file host's landing page ("click to download", countdown) where a file was expected.</summary>
    LandingPage,

    /// <summary>SeaRaider's download browser needs the user on a file host's page (captcha, or a button it couldn't find).</summary>
    BrowserDownload,
}

/// <param name="Url">Page to show to the user.</param>
/// <param name="Kind">Why verification is needed (decides when the window can close by itself).</param>
public sealed record ChallengeRequest(Uri Url, ChallengeKind Kind);

/// <param name="Solved">True when the user completed the check (or clicked Continue).</param>
/// <param name="UserAgent">The browser's user agent; clearance cookies are only honoured with the same one.</param>
/// <param name="Cookies">Session cookies captured from the browser after verification.</param>
/// <param name="DownloadUrl">The file URL, when the user started a download in the window (file-host pages).</param>
/// <param name="Message">Why it wasn't solved (closed, timed out, WebView2 missing…).</param>
public sealed record ChallengeResolution(bool Solved, string? UserAgent, IReadOnlyList<Cookie> Cookies, Uri? DownloadUrl, string? Message)
{
    public static ChallengeResolution Failed(string message) => new(false, null, [], null, message);
}

/// <summary>A page read out of the verified browser session (the browser's own DOM, not an HttpClient download).</summary>
/// <param name="Content">The page's HTML (or, for non-HTML URLs such as feeds and JSON, the raw text).</param>
/// <param name="ContentType">Media type the browser reported (e.g. "text/html").</param>
/// <param name="FinalUrl">Where the browser ended up after redirects.</param>
public sealed record BrowserPage(string Content, string ContentType, Uri FinalUrl);

/// <summary>A verification window opened or closed (for the main window's banner).</summary>
public sealed record ChallengePrompt(string Host, ChallengeKind Kind, bool IsOpen);

/// <summary>
/// Shows a challenge page to the user and returns the verified session. Implemented by the desktop app with a
/// WebView2 window; the core falls back to <see cref="NullChallengeResolver"/> (no UI available).
/// </summary>
public interface IChallengeResolver
{
    bool IsAvailable { get; }

    /// <summary>Raised when a verification window opens or closes (any thread).</summary>
    event EventHandler<ChallengePrompt>? PromptChanged;

    /// <summary>Shows the page and waits until the user completes it, cancels, or <paramref name="cancellationToken"/> fires.</summary>
    Task<ChallengeResolution> ResolveAsync(ChallengeRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Loads <paramref name="url"/> in the verified browser session and returns what the browser received, for sites
    /// that keep answering SeaRaider's own HTTP client with a bot check even after the user verified (clearance is often
    /// tied to the browser's TLS fingerprint, not just its cookies and user agent). Returns null when this resolver can't
    /// do that, the window was closed, or the page never got past its check.
    /// </summary>
    Task<BrowserPage?> FetchPageAsync(Uri url, CancellationToken cancellationToken) => Task.FromResult<BrowserPage?>(null);

    /// <summary>Brings an open verification window to the front.</summary>
    void BringToFront();
}

/// <summary>Used when no UI is available (tests, headless use): challenges surface as normal errors.</summary>
public sealed class NullChallengeResolver : IChallengeResolver
{
    public bool IsAvailable => false;

    public event EventHandler<ChallengePrompt>? PromptChanged { add { } remove { } }

    public Task<ChallengeResolution> ResolveAsync(ChallengeRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(ChallengeResolution.Failed("Verification isn't available here."));

    public void BringToFront() { }
}

/// <summary>A request needed human verification that wasn't completed.</summary>
public sealed class ChallengeException(string message, ChallengeKind kind) : Exception(message)
{
    public ChallengeKind Kind { get; } = kind;
}

/// <summary>Per-request switches read by <see cref="ChallengeHandler"/>.</summary>
public static class ChallengeRequestOptions
{
    /// <summary>Never open a verification window for this request (probes such as HEAD).</summary>
    public static readonly HttpRequestOptionsKey<bool> NoChallenge = new("SeaRaider.NoChallenge");

    /// <summary>The caller expects a file: an HTML page in reply is a landing page / captcha to show to the user.</summary>
    public static readonly HttpRequestOptionsKey<bool> ExpectFile = new("SeaRaider.ExpectFile");
}

/// <summary>
/// Lets whoever started a request show "waiting for verification" (e.g. the download row) while a verification
/// window is open. Flows with the async call (AsyncLocal), so the handler finds the right job without plumbing.
/// </summary>
public static class ChallengeContext
{
    private static readonly AsyncLocal<Action<string?>?> Current = new();

    /// <summary>Registers a status callback for the current async flow; dispose to unregister.</summary>
    public static IDisposable Use(Action<string?> onStatus)
    {
        var previous = Current.Value;
        Current.Value = onStatus;
        return new Restore(previous);
    }

    internal static void Report(string? status)
    {
        try { Current.Value?.Invoke(status); }
        catch { /* status display must never break a request */ }
    }

    private sealed class Restore(Action<string?>? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
