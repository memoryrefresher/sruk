using System.Text.RegularExpressions;

namespace SeaRaider.Core.Scraping;

/// <summary>URL normalisation and filtering shared by the sitemap reader and the pagination crawler.</summary>
public static partial class UrlRules
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    /// <summary>Paths that are never game pages on typical CMS / blog / store sites.</summary>
    [GeneratedRegex(@"/(tag|tags|category|categories|author|authors|page|search|feed|comments|wp-json|wp-admin|wp-content|wp-login\.php|" +
                    @"cart|checkout|account|my-account|login|register|privacy(-policy)?|terms|contact|about|dmca|faq)(/|$)|" +
                    @"\.(xml|xml\.gz|json|jpg|jpeg|png|gif|webp|svg|css|js|pdf|txt)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex NonContentPathRegex();

    [GeneratedRegex(@"/page/(?<n>\d+)/?$", RegexOptions.IgnoreCase)]
    private static partial Regex PagePathRegex();

    [GeneratedRegex(@"(?<key>[?&](?:page|paged|p|pg)=)(?<n>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PageQueryRegex();

    /// <summary>Drops the fragment and default port, lower-cases scheme and host; keeps path and query.</summary>
    public static Uri Normalize(Uri uri)
    {
        var builder = new UriBuilder(uri) { Fragment = string.Empty, Host = uri.Host.ToLowerInvariant() };
        if (uri.IsDefaultPort) builder.Port = -1;
        return builder.Uri;
    }

    /// <summary>Same site, treating "www.example.com" and "example.com" as one.</summary>
    public static bool SameSite(Uri a, Uri b) =>
        string.Equals(StripWww(a.Host), StripWww(b.Host), StringComparison.OrdinalIgnoreCase);

    /// <summary>The home page, taxonomy/archive pages, feeds, assets and account pages.</summary>
    public static bool IsNonContentPath(Uri uri) =>
        uri.AbsolutePath is "" or "/" || NonContentPathRegex().IsMatch(uri.AbsolutePath);

    /// <summary>Applies a source's optional include / exclude regular expressions (invalid patterns are ignored).</summary>
    public static bool PassesFilters(Uri uri, string? includePattern, string? excludePattern)
    {
        var url = uri.AbsoluteUri;
        if (!string.IsNullOrWhiteSpace(includePattern) && TryMatch(includePattern, url) == false) return false;
        if (!string.IsNullOrWhiteSpace(excludePattern) && TryMatch(excludePattern, url) == true) return false;
        return true;
    }

    /// <summary>Validates a user-entered pattern; returns an error message or null.</summary>
    public static string? ValidatePattern(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return null;
        try
        {
            _ = new Regex(pattern, RegexOptions.IgnoreCase, RegexTimeout);
            return null;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Builds the URL of listing page <paramref name="pageNumber"/> from the first page:
    /// replaces an existing /page/N/ or ?page=N, otherwise appends WordPress-style /page/N/.
    /// Page 1 is the start URL itself.
    /// </summary>
    public static Uri BuildPageUrl(Uri start, int pageNumber)
    {
        if (pageNumber <= 1) return start;

        var path = start.AbsolutePath;
        if (PagePathRegex().IsMatch(path))
        {
            var newPath = PagePathRegex().Replace(path, $"/page/{pageNumber}/");
            return new UriBuilder(start) { Path = newPath }.Uri;
        }

        if (PageQueryRegex().IsMatch(start.Query))
        {
            var newQuery = PageQueryRegex().Replace(start.Query, m => m.Groups["key"].Value + pageNumber);
            return new UriBuilder(start) { Query = newQuery.TrimStart('?') }.Uri;
        }

        var basePath = path.EndsWith('/') ? path : path + "/";
        return new UriBuilder(start) { Path = $"{basePath}page/{pageNumber}/" }.Uri;
    }

    /// <summary>Page number encoded in a listing URL (1 when none).</summary>
    public static int PageNumberOf(Uri uri)
    {
        var m = PagePathRegex().Match(uri.AbsolutePath);
        if (m.Success) return int.Parse(m.Groups["n"].Value);
        m = PageQueryRegex().Match(uri.Query);
        return m.Success ? int.Parse(m.Groups["n"].Value) : 1;
    }

    private static bool? TryMatch(string pattern, string input)
    {
        try
        {
            return Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase, RegexTimeout);
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            return null; // broken pattern: don't filter on it
        }
    }

    private static string StripWww(string host) =>
        host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
}
