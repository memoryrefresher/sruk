using Microsoft.Extensions.Logging;

namespace SeaRaider.Core.WebChallenge;

/// <summary>
/// HTTP pipeline step that turns "the site wants a human" into a short pause instead of a failure:
/// <list type="number">
///   <item>Applies the verified browser session (cookies via the shared jar, the browser's user agent per site).</item>
///   <item>Detects bot checks (Cloudflare / DDoS-Guard / Sucuri), captchas and — for downloads — file-host landing pages.</item>
///   <item>Asks <see cref="IChallengeResolver"/> to show the page (the app's WebView2 window) and waits while the
///         user solves it. The current download shows "Waiting for verification…" meanwhile.</item>
///   <item>Stores the captured cookies / user agent and retries once — against the captured file URL if the user
///         started a download in the window.</item>
///   <item>If that retry is still a bot check (the site only trusts the real browser), page requests are loaded through the
///         verified WebView2 session instead and the browser's HTML is returned as the response. The site is remembered, so
///         later page requests go to the browser first.</item>
/// </list>
/// One window per site at a time: parallel requests to the same site wait for the first verification and then reuse it.
/// Without a resolver (or with <see cref="ChallengeRequestOptions.NoChallenge"/>) responses pass through unchanged.
/// </summary>
public sealed class ChallengeHandler(WebSessionStore session, IChallengeResolver resolver, ILogger<ChallengeHandler> logger)
    : DelegatingHandler
{
    private static readonly TimeSpan MaxWaitForUser = TimeSpan.FromMinutes(15);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        ApplySession(request);
        // The inner handler follows redirects by rewriting request.RequestUri. The window must open the address that
        // was asked for (a host's file page), not wherever the host bounced a non-browser client to: opening the
        // redirect target is what showed "File not found" pages for DataNodes / FileKeeper links.
        var requestedUri = request.RequestUri;

        // A site that already needed the browser for its pages: ask the browser first.
        if (requestedUri is not null && CanUseBrowser(request) && session.IsBrowserOnly(requestedUri.Host))
        {
            var viaBrowser = await TryFetchViaBrowserAsync(request, requestedUri, cancellationToken).ConfigureAwait(false);
            if (viaBrowser is not null) return viaBrowser;
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (request.RequestUri is null || Flag(request, ChallengeRequestOptions.NoChallenge)) return response;

        var expectFile = Flag(request, ChallengeRequestOptions.ExpectFile);
        var kind = await InspectAsync(response, expectFile).ConfigureAwait(false);
        if (kind == ChallengeKind.None || !resolver.IsAvailable) return response;

        var site = requestedUri ?? request.RequestUri!;
        Uri? downloadUrl = null;
        var gate = session.GateFor(site.Host);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another request to this site may have been verified while we waited for the gate.
            if (session.DeclinedRecently(site.Host) && !Flag(request, ChallengeRequestOptions.ExpectFile))
            {
                response.Dispose();
                throw new ChallengeException($"{site.Host} needs a human check, and the last one was cancelled. Try again in a moment.", kind);
            }

            if (!(kind == ChallengeKind.BotCheck && session.ResolvedSince(site.Host, startedAt)))
            {
                logger.LogInformation("{Kind} on {Host}; asking the user to verify", kind, site.Host);
                ChallengeContext.Report(kind == ChallengeKind.BotCheck
                    ? $"Waiting for you to complete the verification for {site.Host}…"
                    : $"Waiting for you on {site.Host}: solve the captcha / click its download button in the window…");

                ChallengeResolution resolution;
                using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    limit.CancelAfter(MaxWaitForUser);
                    try
                    {
                        resolution = await resolver.ResolveAsync(new ChallengeRequest(site, kind), limit.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        resolution = ChallengeResolution.Failed("The verification window wasn't completed within 15 minutes.");
                    }
                }
                ChallengeContext.Report(null);

                if (!resolution.Solved)
                {
                    session.MarkDeclined(site.Host);
                    response.Dispose();
                    throw new ChallengeException(
                        $"{site.Host} needs a human check before it lets SeaRaider in. {resolution.Message ?? "The check wasn't completed."} " +
                        "Retry to open the window again.", kind);
                }

                session.Apply(site, resolution);
                downloadUrl = resolution.DownloadUrl;
            }
        }
        finally
        {
            gate.Release();
        }

        response.Dispose();
        using var retry = Clone(request, downloadUrl ?? site);
        if (downloadUrl is not null) retry.Headers.Range = null; // a captured download starts from scratch
        ApplySession(retry);

        var second = await base.SendAsync(retry, cancellationToken).ConfigureAwait(false);
        if (await InspectAsync(second, expectFile).ConfigureAwait(false) == ChallengeKind.BotCheck)
        {
            second.Dispose();

            // The browser got through, the HTTP client doesn't: read the page from the verified browser session instead.
            // (Not for captured file downloads — those need the bytes, not a rendered page.)
            string detail;
            if (downloadUrl is not null) detail = "the window captured a file link, which has to be downloaded as a file";
            else if (!CanUseBrowser(request)) detail = "this was a file download request, which can't be read as a browser page";
            else
            {
                var viaBrowser = await TryFetchViaBrowserAsync(request, site, cancellationToken).ConfigureAwait(false);
                if (viaBrowser is not null)
                {
                    session.MarkBrowserOnly(site.Host);
                    return viaBrowser;
                }
                detail = "the browser window couldn't load the page either";
            }

            logger.LogWarning("{Host} still blocked after verification; browser fallback not used: {Detail}", site.Host, detail);
            throw new ChallengeException(
                $"{site.Host} still blocks SeaRaider after verification. The site may only allow real browsers; " +
                $"open the page in your browser instead. (Browser fallback: {detail}.)", ChallengeKind.BotCheck);
        }
        return second;
    }

    /// <summary>Only plain page reads can be answered by the browser: GETs that don't expect a file and may show a window.</summary>
    private bool CanUseBrowser(HttpRequestMessage request) =>
        resolver.IsAvailable
        && request.Method == HttpMethod.Get
        && request.Content is null
        && !Flag(request, ChallengeRequestOptions.ExpectFile)
        && !Flag(request, ChallengeRequestOptions.NoChallenge);

    /// <summary>
    /// Loads <paramref name="uri"/> in the verified WebView2 session and wraps what the browser got as an HTTP 200 response.
    /// Returns null if the browser couldn't do it (window closed, never passed the check, timed out).
    /// </summary>
    private async Task<HttpResponseMessage?> TryFetchViaBrowserAsync(HttpRequestMessage request, Uri uri, CancellationToken cancellationToken)
    {
        BrowserPage? page;
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            limit.CancelAfter(MaxWaitForUser);
            ChallengeContext.Report($"Loading {uri.Host} through the browser window…");
            try
            {
                logger.LogInformation("Reading {Url} through the verified browser session", uri);
                page = await resolver.FetchPageAsync(uri, limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                page = null; // the 15-minute limit, not the caller cancelling
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Browser fetch of {Url} failed", uri);
                page = null;
            }
            finally
            {
                ChallengeContext.Report(null);
            }
        }
        if (page is null) return null;

        var mediaType = string.IsNullOrWhiteSpace(page.ContentType) ? "text/html" : page.ContentType.Split(';')[0].Trim();
        System.Net.Http.Headers.MediaTypeHeaderValue? contentType;
        try { contentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType) { CharSet = "utf-8" }; }
        catch (FormatException) { contentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html") { CharSet = "utf-8" }; }

        var content = new StringContent(page.Content, System.Text.Encoding.UTF8);
        content.Headers.ContentType = contentType;
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = content,
            RequestMessage = request,
            ReasonPhrase = "OK (via browser)",
        };
    }

    private void ApplySession(HttpRequestMessage request)
    {
        if (request.RequestUri is null || session.UserAgentFor(request.RequestUri.Host) is not { } userAgent) return;
        request.Headers.UserAgent.Clear();
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
    }

    /// <summary>Classifies a response, buffering the body only for small HTML replies that might be a challenge.</summary>
    private static async Task<ChallengeKind> InspectAsync(HttpResponseMessage response, bool expectFile)
    {
        var status = (int)response.StatusCode;
        var mitigated = response.Headers.TryGetValues("cf-mitigated", out var values) ? string.Join(",", values) : null;
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        var isHtml = mediaType is "text/html" or "application/xhtml+xml";

        string? body = null;
        if (ChallengeDetector.NeedsBody(status, isHtml, expectFile, response.Content.Headers.ContentLength))
        {
            await response.Content.LoadIntoBufferAsync().ConfigureAwait(false); // caller can still read it afterwards
            body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        return ChallengeDetector.Detect(status, mitigated, isHtml, body, expectFile);
    }

    private static bool Flag(HttpRequestMessage request, HttpRequestOptionsKey<bool> key) =>
        request.Options.TryGetValue(key, out var value) && value;

    /// <summary>Requests can't be sent twice; SeaRaider only retries body-less GET/HEAD requests.</summary>
    private static HttpRequestMessage Clone(HttpRequestMessage original, Uri uri)
    {
        var clone = new HttpRequestMessage(original.Method, uri) { Version = original.Version, VersionPolicy = original.VersionPolicy };
        foreach (var header in original.Headers) clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (var option in original.Options) ((IDictionary<string, object?>)clone.Options)[option.Key] = option.Value;
        return clone;
    }
}

/// <summary>
/// Per-attempt network timeout, placed inside <see cref="ChallengeHandler"/> so time spent waiting for the user
/// to solve a captcha never counts against it (HttpClient.Timeout is disabled for these clients).
/// </summary>
public sealed class TimeoutHandler(TimeSpan timeout) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            return await base.SendAsync(request, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{request.RequestUri?.Host} didn't answer within {timeout.TotalSeconds:0} seconds.");
        }
    }
}
