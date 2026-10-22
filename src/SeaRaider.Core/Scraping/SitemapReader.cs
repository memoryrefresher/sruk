using System.Globalization;
using System.Xml;

namespace SeaRaider.Core.Scraping;

/// <summary>
/// Parses XML sitemaps (sitemaps.org protocol): sitemap indexes (<c>&lt;sitemapindex&gt;</c>) and URL sets
/// (<c>&lt;urlset&gt;</c>), including WordPress core (<c>wp-sitemap.xml</c>) and Yoast / Rank Math
/// (<c>sitemap_index.xml</c>) layouts.
/// </summary>
public static class SitemapReader
{
    public sealed record SitemapEntry(Uri Location, DateTime? LastModifiedUtc);

    /// <param name="IsIndex">True for a sitemap index (entries are child sitemaps), false for a URL set.</param>
    public sealed record ParsedSitemap(bool IsIndex, IReadOnlyList<SitemapEntry> Entries);

    /// <summary>Where sitemaps usually live when robots.txt doesn't say.</summary>
    public static IEnumerable<Uri> CandidateLocations(Uri site)
    {
        yield return new Uri(site, "/sitemap_index.xml");   // Yoast, Rank Math
        yield return new Uri(site, "/wp-sitemap.xml");      // WordPress core (5.5+)
        yield return new Uri(site, "/sitemap.xml");         // generic
        yield return new Uri(site, "/sitemap-index.xml");
        yield return new Uri(site, "/post-sitemap.xml");
    }

    /// <summary>
    /// Child sitemaps worth reading. Taxonomy, author, image and static-page sitemaps never list
    /// game posts, so skipping them saves hundreds of requests on large sites.
    /// </summary>
    public static bool IsLikelyContentSitemap(Uri sitemap)
    {
        var name = sitemap.AbsolutePath.ToLowerInvariant();
        string[] skip =
        [
            "category", "post_tag", "tag-sitemap", "taxonom", "author", "users", "user-sitemap", "attachment",
            "image-sitemap", "video-sitemap", "wp-sitemap-posts-page", "page-sitemap", "product_cat", "product_tag",
            "local-sitemap", "news-sitemap", "web-story", "elementor",
        ];
        return !skip.Any(name.Contains);
    }

    /// <summary>Parses one sitemap document; returns null if it isn't a sitemap.</summary>
    public static ParsedSitemap? Parse(string xml, Uri sitemapUri)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, // no entity expansion from untrusted XML
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        };

        var entries = new List<SitemapEntry>();
        bool? isIndex = null;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml.TrimStart('\uFEFF', ' ', '\r', '\n', '\t')), settings);
            string? loc = null;
            DateTime? lastMod = null;

            reader.MoveToContent();
            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    switch (reader.LocalName)
                    {
                        // ReadElementContentAsString already advances past </loc>: don't call Read() again.
                        case "loc": loc = reader.ReadElementContentAsString().Trim(); continue;
                        case "lastmod": lastMod = ParseDate(reader.ReadElementContentAsString()); continue;
                        case "sitemapindex" when isIndex is null: isIndex = true; break;
                        case "urlset" when isIndex is null: isIndex = false; break;
                        case "sitemap" or "url": loc = null; lastMod = null; break;
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName is "sitemap" or "url")
                {
                    if (loc is not null && Uri.TryCreate(sitemapUri, loc, out var uri) &&
                        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                    {
                        entries.Add(new SitemapEntry(uri, lastMod));
                    }
                    loc = null;
                }
                reader.Read();
            }
        }
        catch (XmlException)
        {
            if (isIndex is null) return null; // not XML at all (HTML 404 page, etc.)
        }

        return isIndex is null ? null : new ParsedSitemap(isIndex.Value, entries);
    }

    private static DateTime? ParseDate(string value) =>
        DateTimeOffset.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.UtcDateTime
            : null;
}
