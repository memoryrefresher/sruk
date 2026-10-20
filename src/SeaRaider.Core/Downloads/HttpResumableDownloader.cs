using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.WebChallenge;

namespace SeaRaider.Core.Downloads;

/// <summary>
/// Resumable HTTP(S) downloader.
/// <list type="bullet">
///   <item>Streams to "&lt;name&gt;.part" and renames only when complete, so a finished-looking file is never partial.</item>
///   <item>Resumes with a <c>Range: bytes=N-</c> request — also after a crash, since the ".part" file's length is the
///         resume point. The server's ETag / Last-Modified are kept in "&lt;name&gt;.part.meta" and sent as <c>If-Range</c>,
///         so a file that changed on the server restarts cleanly instead of being stitched together.</item>
///   <item>Falls back to a clean restart if the server ignores ranges.</item>
///   <item>Detects stalled connections (no bytes for <see cref="StallTimeout"/>) and retries with backoff,
///         resuming from where it stopped.</item>
///   <item>Refuses HTML error/captcha pages served in place of a file.</item>
///   <item>Honours the job's download limits (<see cref="TransferThrottle"/>), reading in small chunks when limited.</item>
/// </list>
/// </summary>
public sealed class HttpResumableDownloader(
    IHttpClientFactory httpClientFactory,
    ILibraryRepository repository,
    ILogger<HttpResumableDownloader> logger) : IHttpDownloader
{
    private const int BufferSize = 1 << 20; // 1 MiB
    private const int MaxAttempts = 6;
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    public async Task<long?> TryGetContentLengthAsync(Uri url, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientNames.Downloads);
        try
        {
            using var head = CreateRequest(HttpMethod.Head, url);
            head.Options.Set(ChallengeRequestOptions.NoChallenge, true); // size probes never open a verification window
            using var headResponse = await client.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (headResponse.IsSuccessStatusCode && headResponse.Content.Headers.ContentLength is > 0 and var length)
                return length;

            // Some hosts reject HEAD; a one-byte ranged GET reveals the full size in Content-Range.
            using var probe = CreateRequest(HttpMethod.Get, url);
            probe.Options.Set(ChallengeRequestOptions.NoChallenge, true);
            probe.Headers.Range = new RangeHeaderValue(0, 0);
            using var probeResponse = await client.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            return probeResponse.Content.Headers.ContentRange?.Length;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task<string> DownloadAsync(Uri url, string destinationDirectory, string? fileNameHint,
        IProgress<TransferProgress>? progress, CancellationToken cancellationToken, HttpDownloadOptions? options = null)
    {
        options ??= new HttpDownloadOptions();
        Directory.CreateDirectory(destinationDirectory);
        var fileName = ChooseFileName(url, fileNameHint);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await DownloadOnceAsync(url, destinationDirectory, fileName, progress, options, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsTransient(ex) && !cancellationToken.IsCancellationRequested)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt)));
                logger.LogWarning(ex, "Download of {Url} failed (attempt {Attempt}/{Max}); retrying in {Delay}",
                    url, attempt, MaxAttempts, delay);
                progress?.Report(new TransferProgress(0, 0, 0, Detail: $"Connection lost — retrying in {delay.TotalSeconds:0}s"));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<string> DownloadOnceAsync(Uri url, string directory, string fileName,
        IProgress<TransferProgress>? progress, HttpDownloadOptions options, CancellationToken ct)
    {
        var finalPath = Path.Combine(directory, fileName);
        var partPath = finalPath + ".part";

        // Completed in an earlier session (we only ever create finalPath by renaming a finished .part).
        if (File.Exists(finalPath)) return finalPath;

        var metaPath = partPath + ".meta";
        var existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0L;
        var client = httpClientFactory.CreateClient(HttpClientNames.Downloads);

        using var request = CreateRequest(HttpMethod.Get, url);
        if (options.Referer is not null) request.Headers.Referrer = options.Referer;
        // With verification allowed, a captcha / landing page instead of the file opens the verification window
        // (ChallengeHandler). Without it, the page is reported to the caller, who downloads in the built-in browser.
        if (options.AllowVerification) request.Options.Set(ChallengeRequestOptions.ExpectFile, true);
        else request.Options.Set(ChallengeRequestOptions.NoChallenge, true);
        if (existing > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existing, null);
            if (ResumeValidator.Load(metaPath)?.ToIfRange() is { } ifRange) request.Headers.IfRange = ifRange;
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0)
        {
            // We already hold every byte; the previous session died just before the rename.
            File.Move(partPath, finalPath, overwrite: true);
            return finalPath;
        }

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            throw new DeadLinkException($"{url.Host} no longer has this file ({(int)response.StatusCode}).");
        if (!options.AllowVerification && response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                or HttpStatusCode.ServiceUnavailable && IsHtml(response))
            throw new BrowserRequiredException($"{url.Host} only lets a real browser download ({(int)response.StatusCode}).");

        response.EnsureSuccessStatusCode();
        await ThrowIfHtmlInsteadOfFileAsync(response, url, fileName, strict: !options.AllowVerification, ct).ConfigureAwait(false);

        var resumed = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!resumed) existing = 0; // server ignored Range, or the file changed (If-Range) → start over
        if (!resumed) ResumeValidator.From(response).Save(metaPath);

        long total = resumed
            ? response.Content.Headers.ContentRange?.Length ?? existing + (response.Content.Headers.ContentLength ?? 0)
            : response.Content.Headers.ContentLength ?? 0;

        var meter = new TransferRateMeter();
        var lastReport = Stopwatch.StartNew();
        var received = existing;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

        try
        {
            await using (var file = new FileStream(partPath, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write,
                             FileShare.None, bufferSize: 1 << 16, useAsync: true))
            await using (var network = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            using (var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var throttle = TransferThrottle.Current; // the job's speed limits (queue settings), if any
                while (true)
                {
                    stallCts.CancelAfter(StallTimeout); // re-arms the watchdog on every read
                    int read;
                    try
                    {
                        var chunk = throttle?.ChunkSize(BufferSize) ?? BufferSize;
                        read = await network.ReadAsync(buffer.AsMemory(0, chunk), stallCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new TimeoutException($"No data received for {StallTimeout.TotalSeconds:0}s.");
                    }

                    if (read == 0) break;

                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;
                    if (throttle is not null) await throttle.WaitAsync(read, ct).ConfigureAwait(false);

                    if (lastReport.Elapsed >= ProgressInterval)
                    {
                        progress?.Report(new TransferProgress(received, total, meter.Sample(received)));
                        lastReport.Restart();
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (total > 0 && received < total)
            throw new IOException($"Connection closed early ({received:N0} of {total:N0} bytes).");

        progress?.Report(new TransferProgress(received, Math.Max(total, received), 0));

        var targetPath = ApplyContentDispositionName(response, finalPath);
        File.Move(partPath, targetPath, overwrite: true);
        try { File.Delete(metaPath); } catch (IOException) { }
        return targetPath;
    }

    // ---- helpers --------------------------------------------------------------------------------

    private HttpRequestMessage CreateRequest(HttpMethod method, Uri url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("User-Agent", repository.GetSettings().UserAgent);
        request.Headers.Accept.ParseAdd("*/*");
        return request;
    }

    private static string ChooseFileName(Uri url, string? hint)
    {
        var candidate = !string.IsNullOrWhiteSpace(hint)
            ? hint
            : Uri.UnescapeDataString(url.Segments.LastOrDefault()?.Trim('/') ?? string.Empty);

        var safe = string.Concat(candidate.Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim();
        return safe.Length > 0 ? safe : $"download-{Math.Abs(url.AbsoluteUri.GetHashCode()):x8}.bin";
    }

    /// <summary>
    /// If the URL gave us no usable extension (e.g. /download?id=123) but the server names the file,
    /// use the server's name so the extractor can recognise the format.
    /// </summary>
    private static string ApplyContentDispositionName(HttpResponseMessage response, string finalPath)
    {
        if (Path.HasExtension(finalPath) && !finalPath.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) return finalPath;

        var serverName = response.Content.Headers.ContentDisposition?.FileNameStar
                         ?? response.Content.Headers.ContentDisposition?.FileName;
        if (string.IsNullOrWhiteSpace(serverName)) return finalPath;

        var safe = string.Concat(serverName.Trim('"').Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        return safe.Length == 0 ? finalPath : Path.Combine(Path.GetDirectoryName(finalPath)!, safe);
    }

    private static bool IsHtml(HttpResponseMessage response) =>
        response.Content.Headers.ContentType?.MediaType is "text/html" or "application/xhtml+xml";

    /// <summary>
    /// A web page where a file was expected: "file not found" pages become <see cref="DeadLinkException"/>, anything
    /// else (captcha, download button, browser check) <see cref="BrowserRequiredException"/> when
    /// <paramref name="strict"/>, or the old "returned a web page" error otherwise.
    /// </summary>
    private static async Task ThrowIfHtmlInsteadOfFileAsync(HttpResponseMessage response, Uri url, string fileName, bool strict,
        CancellationToken ct)
    {
        var expectsBinary = Path.HasExtension(fileName) &&
                            !fileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase) &&
                            !fileName.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);
        if (!IsHtml(response) || !(expectsBinary || strict)) return;

        var text = await ReadPageTextAsync(response, ct).ConfigureAwait(false);
        if (DeadLinkDetector.LooksDead(text))
            throw new DeadLinkException($"{url.Host} says the file is gone (\"file not found\").");

        if (strict)
            throw new BrowserRequiredException($"{url.Host} shows a page (button, captcha or browser check) instead of the file.");

        throw new InvalidDataException(
            "The server returned a web page instead of the file. The host probably requires a browser " +
            "(captcha, countdown or login). Open the link manually, or add a per-site download selector.");
    }

    /// <summary>The visible text of a (small) HTML page: scripts, styles and tags removed.</summary>
    private static async Task<string> ReadPageTextAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[512 * 1024];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false)) > 0)
                total += read;
            return PageText(System.Text.Encoding.UTF8.GetString(buffer, 0, total));
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            return string.Empty;
        }
    }

    internal static string PageText(string html)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(html, @"<(script|style|noscript)\b[^>]*>.*?</\1>", " ",
            System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", " ");
        return WebUtility.HtmlDecode(text);
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        InvalidDataException => false, // HTML instead of a file: retrying won't help (must precede IOException)
        TimeoutException => true,
        IOException => true,
        TaskCanceledException => true, // HttpClient timeout
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: { } code } => (int)code >= 500 || code == HttpStatusCode.TooManyRequests,
        _ => false,
    };
}

/// <summary>What identifies the server's copy of a partly downloaded file (saved next to the ".part" file).</summary>
internal sealed record ResumeValidator(string? ETag, DateTimeOffset? LastModified, long? Length)
{
    public static ResumeValidator From(HttpResponseMessage response)
    {
        var etag = response.Headers.ETag is { IsWeak: false } strong ? strong.Tag : null; // weak tags aren't valid for If-Range
        return new ResumeValidator(etag, response.Content.Headers.LastModified, response.Content.Headers.ContentLength);
    }

    public RangeConditionHeaderValue? ToIfRange() =>
        ETag is not null ? new RangeConditionHeaderValue(new EntityTagHeaderValue(ETag))
        : LastModified is { } date ? new RangeConditionHeaderValue(date)
        : null;

    public void Save(string path)
    {
        try { File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(this)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* resume still works by length */ }
    }

    public static ResumeValidator? Load(string path)
    {
        try { return File.Exists(path) ? System.Text.Json.JsonSerializer.Deserialize<ResumeValidator>(File.ReadAllText(path)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return null; }
    }
}
