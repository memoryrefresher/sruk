using SeaRaider.Core.Abstractions;

namespace SeaRaider.Core.Downloads;

/// <summary>A file host page to download from inside the built-in browser.</summary>
/// <param name="PageUrl">The host's page for the file (exactly as the game page linked it).</param>
/// <param name="Referer">The game page that linked it; some hosts refuse visitors that don't come from a link.</param>
/// <param name="DestinationDirectory">Where the file goes.</param>
/// <param name="FileName">Name to save as (else the host's name for it).</param>
/// <param name="HostName">"DataNodes", "FuckingFast" … for messages.</param>
/// <param name="Label">"Part 3 of 8" … for the window's header.</param>
/// <param name="WantTorrent">
/// Collect a torrent instead of a file: the page (a link shortener's "Torrent" button) is followed to its magnet link or
/// .torrent file, which the torrent client then downloads. The result carries the magnet, or the .torrent as the file.
/// </param>
/// <param name="CollectLinks">
/// Collect the download links instead of a file: the page (a short link, a FileCrypt container) is followed to the file
/// host. The result lists the host's link, or every part's link of a FileCrypt container, in order.
/// </param>
public sealed record BrowserDownloadRequest(
    Uri PageUrl, Uri? Referer, string DestinationDirectory, string? FileName, string HostName, string Label, bool WantTorrent = false,
    bool CollectLinks = false);

/// <summary>A link collected by the download browser: a file host's page, with the file name the container showed.</summary>
public sealed record CollectedLink(string Url, string? FileName);

public enum BrowserDownloadOutcome
{
    Completed,

    /// <summary>The host says the file is gone (expired, deleted, "File not found").</summary>
    DeadLink,

    Failed,

    /// <summary>No built-in browser on this PC.</summary>
    Unavailable,
}

/// <param name="FilePath">The finished file, when <see cref="Outcome"/> is Completed.</param>
/// <param name="Message">Why it didn't complete.</param>
/// <param name="Magnet">For a torrent request: the magnet link the page led to (else the .torrent is <see cref="FilePath"/>).</param>
/// <param name="Links">For a links request: the file host links, in download order.</param>
public sealed record BrowserDownloadResult(BrowserDownloadOutcome Outcome, string? FilePath, string? Message, string? Magnet = null,
    IReadOnlyList<CollectedLink>? Links = null)
{
    public static BrowserDownloadResult Collected(IReadOnlyList<CollectedLink> links) => new(BrowserDownloadOutcome.Completed, null, null, null, links);

    public static BrowserDownloadResult FoundMagnet(string magnet) => new(BrowserDownloadOutcome.Completed, null, null, magnet);

    public static BrowserDownloadResult Dead(string message) => new(BrowserDownloadOutcome.DeadLink, null, message);
    public static BrowserDownloadResult Fail(string message) => new(BrowserDownloadOutcome.Failed, null, message);
}

/// <summary>
/// Downloads a file the way a person would: the host's page opens in SeaRaider's built-in browser, the download
/// button is clicked (by SeaRaider when it can, otherwise by the user after a captcha), and the browser itself
/// downloads the file to SeaRaider's folder.
///
/// <para>Used for hosts that only serve real browsers: landing pages with buttons and countdowns, captchas, and
/// Cloudflare checks that compare the browser's TLS fingerprint (cookies alone can't satisfy those, which is why
/// handing a verified session to SeaRaider's HTTP client failed with "still blocks SeaRaider after verification").
/// Single-use download tokens are no problem either, since the browser that got the token uses it.</para>
///
/// Implemented by the desktop app (WebView2). <see cref="NullBrowserDownloader"/> is used without UI.
/// </summary>
public interface IBrowserDownloader
{
    bool IsAvailable { get; }

    /// <summary>Downloads one file. Cancelling pauses it when the browser can resume it later.</summary>
    Task<BrowserDownloadResult> DownloadAsync(BrowserDownloadRequest request, IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed class NullBrowserDownloader : IBrowserDownloader
{
    public bool IsAvailable => false;

    public Task<BrowserDownloadResult> DownloadAsync(BrowserDownloadRequest request, IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken) =>
        Task.FromResult(new BrowserDownloadResult(BrowserDownloadOutcome.Unavailable, null, "No built-in browser here."));
}

/// <summary>The host no longer has the file (404 / 410 / "File not found" page). Another mirror may.</summary>
public sealed class DeadLinkException(string message) : Exception(message);

/// <summary>The host answered with a page (captcha, button, browser check) instead of the file: a browser is needed.</summary>
public sealed class BrowserRequiredException(string message) : Exception(message);

/// <summary>Every host (and alternate) has lost a file of the set.</summary>
public sealed class NoWorkingSourceException(string message) : Exception(message);

/// <summary>Low disk space mode: the archive couldn't be unpacked (as opposed to a download problem).</summary>
public sealed class StreamingExtractionException(Exception inner) : Exception(inner.Message, inner);

/// <summary>Recognises "this file is gone" pages from file hosts.</summary>
public static class DeadLinkDetector
{
    private static readonly string[] Markers =
    [
        "file not found", "file could not be found", "could not be found", "file was not found", "file does not exist",
        "file doesn't exist", "file has been deleted", "file was deleted", "file has been removed", "file was removed",
        "file is no longer available", "no longer available", "file expired", "file has expired", "link has expired",
        "the file you were looking for", "this file is unavailable", "file unavailable", "404 not found", "error 404",
        "removed due to", "deleted by the owner", "deleted by its owner", "not available anymore",
    ];

    /// <summary>True when <paramref name="text"/> (page text or HTML) says the file is gone.</summary>
    public static bool LooksDead(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var lower = text.Length > 300_000 ? text[..300_000].ToLowerInvariant() : text.ToLowerInvariant();
        return Markers.Any(lower.Contains);
    }

    /// <summary>The JavaScript array of markers, for the browser-side check.</summary>
    public static string MarkersJson => System.Text.Json.JsonSerializer.Serialize(Markers);
}
