using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Scraping;
using SeaRaider.Core.WebChallenge;

namespace SeaRaider.Core.Downloads;

/// <summary>A file host page turned into the file itself.</summary>
/// <param name="FileUrl">Direct link to the file.</param>
/// <param name="FileName">File name, when the host told us.</param>
public sealed record ResolvedHostFile(Uri FileUrl, string? FileName);

/// <summary>
/// Turns file-host page links into direct file links for hosts with a known, button-free path
/// (<see cref="HostAccess.Resolvable"/>). Anything else — or a resolver that fails because the host changed —
/// returns null, and the file is downloaded in the built-in browser instead (<see cref="IBrowserDownloader"/>).
/// A host that says the file is gone throws <see cref="DeadLinkException"/> so another mirror can be tried.
/// </summary>
public sealed partial class FileHostResolver(IHttpClientFactory httpClientFactory, ILibraryRepository repository,
    ILogger<FileHostResolver> logger)
{
    [GeneratedRegex(@"https://fuckingfast\.co/dl/[A-Za-z0-9_\-]+", RegexOptions.IgnoreCase)]
    private static partial Regex FuckingFastDirectRegex();

    [GeneratedRegex(@"href=""(?<u>https?://download\d*\.mediafire\.com/[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex MediaFireDirectRegex();

    [GeneratedRegex(@"^/(?:u|l)/(?<id>[A-Za-z0-9]+)")]
    private static partial Regex PixeldrainIdRegex();

    public async Task<ResolvedHostFile?> ResolveAsync(Uri pageUrl, CancellationToken cancellationToken)
    {
        var host = FileHosts.Find(pageUrl.Host);
        if (host is not { Access: HostAccess.Resolvable }) return null;

        try
        {
            return host.Key switch
            {
                "pixeldrain" => ResolvePixeldrain(pageUrl),
                "buzzheavier" => await ResolveBuzzheavierAsync(pageUrl, cancellationToken).ConfigureAwait(false),
                "fuckingfast" => await ResolveFromPageAsync(pageUrl, FuckingFastDirectRegex(), cancellationToken).ConfigureAwait(false),
                "mediafire" => await ResolveFromPageAsync(pageUrl, MediaFireDirectRegex(), cancellationToken).ConfigureAwait(false),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or ChallengeException
                                       || ex is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation(ex, "Couldn't resolve {Host} link {Url}; falling back to the page", host.DisplayName, pageUrl);
            return null;
        }
    }

    /// <summary>pixeldrain.com/u/ID → the public file API.</summary>
    private static ResolvedHostFile? ResolvePixeldrain(Uri pageUrl)
    {
        var m = PixeldrainIdRegex().Match(pageUrl.AbsolutePath);
        return m.Success ? new ResolvedHostFile(new Uri($"https://pixeldrain.com/api/file/{m.Groups["id"].Value}?download"), null) : null;
    }

    /// <summary>
    /// Buzzheavier's download button asks <c>/ID/download</c> (as an htmx request) and follows the
    /// <c>hx-redirect</c> header to the file server.
    /// </summary>
    private async Task<ResolvedHostFile?> ResolveBuzzheavierAsync(Uri pageUrl, CancellationToken ct)
    {
        var id = pageUrl.AbsolutePath.Trim('/').Split('/').LastOrDefault();
        if (string.IsNullOrEmpty(id)) return null;

        var client = httpClientFactory.CreateClient(HttpClientNames.Scraper);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(pageUrl, $"/{id}/download"));
        request.Headers.TryAddWithoutValidation("User-Agent", repository.GetSettings().UserAgent);
        request.Headers.TryAddWithoutValidation("HX-Request", "true");
        request.Headers.TryAddWithoutValidation("HX-Current-URL", pageUrl.AbsoluteUri);
        request.Headers.Referrer = pageUrl;
        request.Options.Set(ChallengeRequestOptions.NoChallenge, true);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        var target = response.Headers.TryGetValues("hx-redirect", out var values) ? values.FirstOrDefault() : null;
        target ??= response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.SeeOther
            ? response.Headers.Location?.ToString()
            : null;
        return Uri.TryCreate(pageUrl, target, out var file) && file.Scheme is "http" or "https" ? new ResolvedHostFile(file, null) : null;
    }

    /// <summary>Hosts whose page HTML contains the direct link.</summary>
    private async Task<ResolvedHostFile?> ResolveFromPageAsync(Uri pageUrl, Regex directLink, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(HttpClientNames.Scraper);
        using var request = new HttpRequestMessage(HttpMethod.Get, pageUrl);
        request.Headers.TryAddWithoutValidation("User-Agent", repository.GetSettings().UserAgent);
        // No verification window from here: if the host wants a browser, the download moves to the built-in browser.
        request.Options.Set(ChallengeRequestOptions.NoChallenge, true);
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            throw new DeadLinkException($"{pageUrl.Host} no longer has this file.");
        if (!response.IsSuccessStatusCode) return null;

        var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var m = directLink.Match(html);
        if (!m.Success)
        {
            if (DeadLinkDetector.LooksDead(HttpResumableDownloader.PageText(html)))
                throw new DeadLinkException($"{pageUrl.Host} says the file is gone (\"file not found\").");
            return null;
        }

        var url = WebUtility.HtmlDecode(m.Groups["u"].Success ? m.Groups["u"].Value : m.Value);
        return Uri.TryCreate(url, UriKind.Absolute, out var file) ? new ResolvedHostFile(file, null) : null;
    }

    // =============================================================================================
    // Link shorteners: the destination behind a short link, without its wait screen and ads
    // =============================================================================================

    [GeneratedRegex(@"<meta[^>]+http-equiv\s*=\s*[""']?refresh[""']?[^>]*content\s*=\s*[""'][^""']*?url\s*=\s*(?<u>[^""'>\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex MetaRefreshRegex();

    [GeneratedRegex(@"magnet:\?xt=urn:bt[^""'\s<>\\]+", RegexOptions.IgnoreCase)]
    private static partial Regex MagnetRegex();

    // URLs in markup and scripts, including JSON-escaped ones (https:\/\/host\/path).
    [GeneratedRegex(@"https?:(?:\\?/){2}[^""'\s<>()]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    // Base64 that starts with "http" (aHR0c) or "magnet:" (bWFnbmV0): how many shorteners hide the destination.
    [GeneratedRegex(@"(?:aHR0c|bWFnbmV0)[A-Za-z0-9+/_-]{12,}={0,2}")]
    private static partial Regex Base64UrlRegex();

    [GeneratedRegex(@"(?:https?|magnet)%3A[A-Za-z0-9%._~!$&'()*+,;=:@/?-]+", RegexOptions.IgnoreCase)]
    private static partial Regex PercentEncodedUrlRegex();

    /// <summary>
    /// The page a link-shortener link leads to, read without a browser: the redirect it answers with, or the destination
    /// written into its page (a link, a meta refresh, a script string, base64 or percent-encoded). Only real destinations
    /// count: a known file host's file, a magnet, a .torrent or a direct file (and, for a torrent, a tracker page). Null
    /// when none is found; the download browser then goes through the short link.
    /// </summary>
    /// <param name="wantTorrent">A torrent mirror: magnets, .torrent links and tracker pages first.</param>
    public async Task<Uri?> ResolveShortLinkAsync(Uri shortUrl, bool wantTorrent, CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientNames.Scraper);
            using var request = new HttpRequestMessage(HttpMethod.Get, shortUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", repository.GetSettings().UserAgent);
            request.Options.Set(ChallengeRequestOptions.NoChallenge, true);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

            // Redirected straight to the destination.
            if (response.RequestMessage?.RequestUri is { } final && !FileHosts.IsLinkShortener(final.Host) && IsDestination(final, wantTorrent))
                return final;
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                throw new DeadLinkException("The download link no longer works.");
            if (!response.IsSuccessStatusCode) return null;

            var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var found = FindShortLinkDestination(html, response.RequestMessage?.RequestUri ?? shortUrl, wantTorrent);
            if (found is not null) logger.LogInformation("Short link {Short} leads to {Destination}", shortUrl, found.Host.Length > 0 ? found.Host : "a magnet");
            return found;
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or ChallengeException
                                       || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogInformation(ex, "Couldn't read short link {Url}; the download browser will follow it", shortUrl);
            return null;
        }
    }

    /// <summary>The best destination written into a short link's page (see <see cref="ResolveShortLinkAsync"/>).</summary>
    public static Uri? FindShortLinkDestination(string html, Uri page, bool wantTorrent)
    {
        var candidates = new List<string>();

        // In the short link itself: ?url=https%3A%2F%2F… or ?u=aHR0cHM6Ly8…
        foreach (var pair in page.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var value = pair.Split('=', 2) is [_, var v] ? Uri.UnescapeDataString(v.Replace('+', ' ')) : string.Empty;
            if (value.Length > 0) candidates.Add(value);
            if (TryBase64(value) is { } decoded) candidates.Add(decoded);
        }

        if (MetaRefreshRegex().Match(html) is { Success: true } refresh) candidates.Add(WebUtility.HtmlDecode(refresh.Groups["u"].Value));
        candidates.AddRange(MagnetRegex().Matches(html).Select(m => WebUtility.HtmlDecode(m.Value)));
        candidates.AddRange(UrlRegex().Matches(html).Select(m => WebUtility.HtmlDecode(m.Value.Replace("\\/", "/"))));
        candidates.AddRange(Base64UrlRegex().Matches(html).Select(m => TryBase64(m.Value)).OfType<string>());
        candidates.AddRange(PercentEncodedUrlRegex().Matches(html).Select(m => Uri.UnescapeDataString(m.Value)));

        return candidates
            .Select(c => Uri.TryCreate(c.Trim().TrimEnd('\\', ';', ','), UriKind.Absolute, out var u) ? u : null)
            .OfType<Uri>()
            .Where(u => IsDestination(u, wantTorrent))
            .OrderBy(u => DestinationRank(u, wantTorrent))
            .FirstOrDefault();
    }

    /// <summary>A real destination, never the shortener itself, an ad or a script.</summary>
    internal static bool IsDestination(Uri url, bool wantTorrent)
    {
        if (url.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase))
            return wantTorrent && url.OriginalString.Contains("xt=urn:bt", StringComparison.OrdinalIgnoreCase);
        if (url.Scheme is not ("http" or "https") || FileHosts.IsLinkShortener(url.Host) || FileHosts.IsLinkProtector(url.Host)) return false;
        if (url.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)) return wantTorrent;
        if (FileHosts.MatchFileLink(url) is { } host) return host.Key != "torrent-host" || wantTorrent;
        return !wantTorrent && BrowserPageScript.LooksLikeFileUrl(url) &&
               !url.AbsolutePath.EndsWith(".js", StringComparison.OrdinalIgnoreCase);
    }

    private static int DestinationRank(Uri url, bool wantTorrent)
    {
        var torrentish = url.Scheme == "magnet" ? 0 : url.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
        var host = FileHosts.MatchFileLink(url);
        var rank = host is null ? 3 : host.Key == "torrent-host" ? 4 : 2;
        return wantTorrent ? torrentish * 10 + rank : (torrentish < 2 ? 50 : rank);
    }

    private static string? TryBase64(string value)
    {
        if (value.Length < 16) return null;
        try
        {
            var normalized = value.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '=');
            var text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
            return text.StartsWith("http", StringComparison.OrdinalIgnoreCase) || text.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
                ? text : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
