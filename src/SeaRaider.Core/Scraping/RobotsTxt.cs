using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SeaRaider.Core.Scraping;

/// <summary>
/// Minimal robots.txt reader (RFC 9309): user-agent groups, Allow/Disallow with longest-match
/// precedence and <c>*</c> / <c>$</c> wildcards, Crawl-delay, and Sitemap lines.
/// </summary>
public sealed class RobotsTxt
{
    private readonly List<(bool Allow, string Pattern, Regex Matcher)> _rules = [];

    /// <summary>Sitemap URLs declared anywhere in the file.</summary>
    public IReadOnlyList<Uri> Sitemaps { get; private init; } = [];

    /// <summary>Crawl-delay for our group, if declared.</summary>
    public TimeSpan? CrawlDelay { get; private init; }

    /// <summary>Permissive instance used when robots.txt is missing or unreadable.</summary>
    public static RobotsTxt AllowAll { get; } = new();

    /// <param name="content">Raw robots.txt text.</param>
    /// <param name="baseUri">Site root, used to resolve relative Sitemap lines.</param>
    /// <param name="productToken">Our user-agent token (matched case-insensitively), e.g. "SeaRaider".</param>
    public static RobotsTxt Parse(string content, Uri baseUri, string productToken)
    {
        var sitemaps = new List<Uri>();
        var groups = new List<Group>();
        Group? current = null;
        var lastLineWasAgent = false;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Split('#')[0].Trim();
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;

            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();

            if (key == "user-agent")
            {
                // Consecutive User-agent lines share one group.
                if (current is null || !lastLineWasAgent)
                {
                    current = new Group();
                    groups.Add(current);
                }
                current.Agents.Add(value.ToLowerInvariant());
                lastLineWasAgent = true;
                continue;
            }

            lastLineWasAgent = false;
            switch (key)
            {
                case "sitemap":
                    if (Uri.TryCreate(baseUri, value, out var sitemap)) sitemaps.Add(sitemap);
                    break;
                case "allow" or "disallow" when current is not null && value.Length > 0:
                    current.Rules.Add((key == "allow", value));
                    break;
                case "crawl-delay" when current is not null:
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                        current.DelaySeconds = seconds;
                    break;
            }
        }

        var token = productToken.ToLowerInvariant();
        var group = groups.FirstOrDefault(g => g.Agents.Any(a => a != "*" && token.Contains(a)))
                    ?? groups.FirstOrDefault(g => g.Agents.Contains("*"));

        var robots = new RobotsTxt
        {
            Sitemaps = sitemaps,
            CrawlDelay = group?.DelaySeconds is { } delay ? TimeSpan.FromSeconds(Math.Clamp(delay, 0, 30)) : null,
        };
        foreach (var (allow, pattern) in group?.Rules ?? new List<(bool Allow, string Pattern)>())
            robots._rules.Add((allow, pattern, ToRegex(pattern)));
        return robots;
    }

    private sealed class Group
    {
        public List<string> Agents { get; } = [];
        public List<(bool Allow, string Pattern)> Rules { get; } = [];
        public double? DelaySeconds { get; set; }
    }

    /// <summary>True when the path (+query) of <paramref name="uri"/> may be fetched.</summary>
    public bool IsAllowed(Uri uri)
    {
        var target = uri.PathAndQuery;
        (bool Allow, int Length)? best = null;
        foreach (var (allow, pattern, matcher) in _rules)
        {
            if (!matcher.IsMatch(target)) continue;
            // Longest pattern wins; on a tie, Allow wins.
            if (best is null || pattern.Length > best.Value.Length || (pattern.Length == best.Value.Length && allow))
                best = (allow, pattern.Length);
        }
        return best?.Allow ?? true;
    }

    private static Regex ToRegex(string pattern)
    {
        var sb = new StringBuilder("^");
        foreach (var c in pattern)
        {
            sb.Append(c switch
            {
                '*' => ".*",
                '$' => "$",
                _ => Regex.Escape(c.ToString()),
            });
        }
        return new Regex(sb.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }
}
