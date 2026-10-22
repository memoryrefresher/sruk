namespace SeaRaider.Core.WebChallenge;

/// <summary>Recognises bot checks, captchas and file-host landing pages in HTTP responses.</summary>
public static class ChallengeDetector
{
    private static readonly string[] BotCheckMarkers =
    [
        "challenge-platform", "cf_chl_opt", "cf-browser-verification", "cf-challenge", "challenges.cloudflare.com",
        "<title>just a moment", "checking your browser before accessing", "attention required! | cloudflare",
        "ddos-guard", "sucuri_cloudproxy", "enable javascript and cookies to continue",
    ];

    private static readonly string[] CaptchaMarkers =
    [
        "g-recaptcha", "recaptcha/api.js", "h-captcha", "hcaptcha.com/1/api.js", "cf-turnstile", "turnstile/v0/api.js",
    ];

    /// <param name="statusCode">HTTP status.</param>
    /// <param name="mitigatedHeader">Value of Cloudflare's <c>cf-mitigated</c> header, if any.</param>
    /// <param name="isHtml">Response content type is HTML.</param>
    /// <param name="body">Response body (may be null when not read).</param>
    /// <param name="expectFile">The caller asked for a file, so any HTML page is a page to click through.</param>
    public static ChallengeKind Detect(int statusCode, string? mitigatedHeader, bool isHtml, string? body, bool expectFile)
    {
        if (mitigatedHeader?.Contains("challenge", StringComparison.OrdinalIgnoreCase) == true) return ChallengeKind.BotCheck;
        if (!isHtml || body is null) return ChallengeKind.None;

        var lower = body.Length > 200_000 ? body[..200_000].ToLowerInvariant() : body.ToLowerInvariant();
        var blockedStatus = statusCode is 403 or 429 or 503;

        if (blockedStatus && BotCheckMarkers.Any(lower.Contains)) return ChallengeKind.BotCheck;
        if ((blockedStatus || expectFile) && CaptchaMarkers.Any(lower.Contains)) return ChallengeKind.Captcha;
        if (expectFile && statusCode == 200) return ChallengeKind.LandingPage;
        return ChallengeKind.None;
    }

    /// <summary>Should the body be read to decide? (Keeps normal pages and files untouched.)</summary>
    public static bool NeedsBody(int statusCode, bool isHtml, bool expectFile, long? contentLength) =>
        isHtml && contentLength is null or < 3_000_000 && (statusCode is 403 or 429 or 503 || (expectFile && statusCode == 200));
}
