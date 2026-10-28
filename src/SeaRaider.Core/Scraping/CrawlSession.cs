using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;

namespace SeaRaider.Core.Scraping;

/// <summary>Result of one crawler request.</summary>
public sealed class FetchResult
{
    public HttpStatusCode? Status { get; init; }
    public byte[] Body { get; init; } = [];
    public string? MediaType { get; init; }
    public string? Charset { get; init; }
    public required Uri FinalUri { get; init; }

    /// <summary>robots.txt forbids this URL; nothing was requested.</summary>
    public bool Disallowed { get; init; }

    public bool IsSuccess => !Disallowed && Status is { } s && (int)s is >= 200 and < 300;

    /// <summary>Body decoded with the declared charset (UTF-8 fallback).</summary>
    public string Text
    {
        get
        {
            Encoding encoding = Encoding.UTF8;
            if (!string.IsNullOrWhiteSpace(Charset))
            {
                try { encoding = Encoding.GetEncoding(Charset.Trim('"')); }
                catch (ArgumentException) { /* unknown charset: keep UTF-8 */ }
            }
            return encoding.GetString(Body);
        }
    }
}

/// <summary>
/// One crawl's polite HTTP client:
/// <list type="bullet">
///   <item>a minimum gap between requests (the source's delay, or robots.txt Crawl-delay if larger);</item>
///   <item>robots.txt Allow/Disallow when the source opts in;</item>
///   <item>retries for 429 / 503 honouring Retry-After, and for transient network errors;</item>
///   <item>transparent gunzip for .xml.gz sitemaps, and a size cap on responses.</item>
/// </list>
/// Safe to share between the crawl's worker tasks.
/// </summary>
internal sealed class CrawlSession(HttpClient client, string userAgent, TimeSpan minimumDelay, bool respectRobots, ILogger logger)
    : IDisposable
{
    private const int MaxAttempts = 3;
    private const long MaxBodyBytes = 25 * 1024 * 1024;

    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private TimeSpan _delay = minimumDelay;
    private DateTime _nextRequestUtc = DateTime.MinValue;

    public RobotsTxt Robots { get; private set; } = RobotsTxt.AllowAll;

    /// <summary>Reads robots.txt (for Sitemap lines, and rules/Crawl-delay when respected).</summary>
    public async Task InitializeAsync(Uri site, CancellationToken ct)
    {
        var robotsUri = new Uri(site, "/robots.txt");
        try
        {
            var result = await SendAsync(robotsUri, ct).ConfigureAwait(false);
            if (!result.IsSuccess) return;

            var parsed = RobotsTxt.Parse(result.Text, robotsUri, "SeaRaider");
            Robots = respectRobots ? parsed : WithSitemapsOnly(parsed);
            if (respectRobots && parsed.CrawlDelay is { } crawlDelay && crawlDelay > _delay) _delay = crawlDelay;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException or WebChallenge.ChallengeException
                                   && !ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "robots.txt unavailable for {Site}", site.Host);
        }
    }

    public async Task<FetchResult> GetAsync(Uri uri, CancellationToken ct)
    {
        if (!Robots.IsAllowed(uri)) return new FetchResult { FinalUri = uri, Disallowed = true };
        return await SendAsync(uri, ct).ConfigureAwait(false);
    }

    private async Task<FetchResult> SendAsync(Uri uri, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await WaitTurnAsync(ct).ConfigureAwait(false);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                if ((response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable) && attempt < MaxAttempts)
                {
                    var wait = RetryAfter(response) ?? TimeSpan.FromSeconds(5 * attempt);
                    logger.LogInformation("{Status} from {Host}; backing off {Wait}", (int)response.StatusCode, uri.Host, wait);
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                    continue;
                }

                var body = response.IsSuccessStatusCode ? await ReadCappedAsync(response.Content, ct).ConfigureAwait(false) : Array.Empty<byte>();
                if (body.Length > 2 && body[0] == 0x1F && body[1] == 0x8B) body = Gunzip(body);

                return new FetchResult
                {
                    Status = response.StatusCode,
                    Body = body,
                    MediaType = response.Content.Headers.ContentType?.MediaType,
                    Charset = response.Content.Headers.ContentType?.CharSet,
                    FinalUri = response.RequestMessage?.RequestUri ?? uri,
                };
            }
            catch (Exception ex) when (attempt < MaxAttempts && !ct.IsCancellationRequested &&
                                       ex is HttpRequestException or TaskCanceledException or IOException or TimeoutException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Spaces request starts at least <c>_delay</c> apart across all workers.</summary>
    private async Task WaitTurnAsync(CancellationToken ct)
    {
        await _turnGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = _nextRequestUtc - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
            _nextRequestUtc = DateTime.UtcNow + _delay;
        }
        finally
        {
            _turnGate.Release();
        }
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        TimeSpan? wait = header?.Delta ?? (header?.Date is { } date ? date - DateTimeOffset.UtcNow : (TimeSpan?)null);
        return wait is { } w && w > TimeSpan.Zero ? TimeSpan.FromSeconds(Math.Min(w.TotalSeconds, 60)) : null;
    }

    private static async Task<byte[]> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxBodyBytes) throw new InvalidDataException("Response too large for a web page.");
        }
        return buffer.ToArray();
    }

    private static byte[] Gunzip(byte[] data)
    {
        using var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private static RobotsTxt WithSitemapsOnly(RobotsTxt parsed)
    {
        // Rules are ignored but Sitemap lines are still useful.
        var text = string.Join('\n', parsed.Sitemaps.Select(s => "Sitemap: " + s.AbsoluteUri));
        return RobotsTxt.Parse(text, new Uri("https://localhost/"), "SeaRaider");
    }

    public void Dispose() => _turnGate.Dispose();
}
