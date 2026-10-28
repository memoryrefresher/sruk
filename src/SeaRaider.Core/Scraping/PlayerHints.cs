using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace SeaRaider.Core.Scraping;

/// <summary>
/// A media source's own answer to "where is the video on this site's pages" (Media tab › the source's "Player…"), for sites
/// whose player SeaRaider can't find by itself. One line, in one of three forms:
/// <list type="bullet">
///   <item><b>A CSS selector</b> for the element that holds the player's address: <c>iframe#player</c>, <c>.stream-box iframe</c>.
///         The address is read from its <c>src</c> (then <c>data-src</c>, <c>data-url</c>, <c>href</c> …); name another attribute
///         after an @: <c>iframe.live@data-embed</c>.</item>
///   <item><b>An address template</b> (starts with http): <c>https://embed.example.com/e/{slug}</c>. Filled in per page:
///         <c>{slug}</c> / <c>{id}</c> (the page address's last part), <c>{path}</c>, <c>{host}</c>, <c>{url}</c>, <c>{urlencoded}</c>.</item>
///   <item><b>A regular expression</b> on the page's HTML: <c>regex:"(https://[^"]+\.m3u8)"</c>. The first group is the address.</item>
/// </list>
/// </summary>
public static partial class PlayerHints
{
    /// <summary>Where a player's address is written on an element, in the order they're tried.</summary>
    private static readonly string[] Attributes =
        ["src", "data-src", "data-lazy-src", "data-url", "data-embed", "data-link", "data-iframe", "data-video", "data-href", "href", "content", "value"];

    [GeneratedRegex(@"^(?<sel>.+?)\s*@(?<attr>[A-Za-z][\w:-]*)$")]
    private static partial Regex AttributeSuffixRegex();

    [GeneratedRegex(@"\{(?<name>[A-Za-z]+)\}")]
    private static partial Regex PlaceholderRegex();

    private const string RegexPrefix = "regex:";

    /// <summary>The hint is an address template: it needs no page to be read.</summary>
    public static bool IsTemplate(string hint) => hint.Contains("://", StringComparison.Ordinal) || hint.StartsWith("//", StringComparison.Ordinal);

    /// <summary>Null when the hint is usable; otherwise what's wrong with it, for the user.</summary>
    public static string? Validate(string? hint)
    {
        hint = hint?.Trim();
        if (string.IsNullOrEmpty(hint)) return null;
        if (hint.StartsWith(RegexPrefix, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                _ = new Regex(hint[RegexPrefix.Length..].Trim(), RegexOptions.IgnoreCase | RegexOptions.Singleline);
                return null;
            }
            catch (ArgumentException ex)
            {
                return "That isn't a valid pattern: " + ex.Message;
            }
        }
        if (IsTemplate(hint))
            return FromTemplate(hint, new Uri("https://example.com/watch/sample-1")) is null ? "That address template doesn't make a web address." : null;
        try
        {
            var (selector, _) = SplitAttribute(hint);
            new HtmlParser().ParseDocument("<html><body></body></html>").QuerySelector(selector);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "That isn't a valid selector. Try something like iframe#player, or an address starting with https://.";
        }
    }

    /// <summary>
    /// Null when the click steps are usable; otherwise what's wrong, for the user. Steps are separated by <c>&gt;&gt;</c>; each is a
    /// CSS selector or <c>text:Link-1</c> (the element whose text says that).
    /// </summary>
    public static string? ValidateSelector(string clickSteps)
    {
        foreach (var step in clickSteps.Split(">>", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (step.StartsWith("text:", StringComparison.OrdinalIgnoreCase))
            {
                if (step.Length <= "text:".Length) return "\"text:\" needs the words on the button, like text:Link-1.";
                continue;
            }
            try
            {
                new HtmlParser().ParseDocument("<html><body></body></html>").QuerySelector(step);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return $"\"{step}\" isn't a valid selector. Use a selector like #videoPlayer, or text:Link-1 for a button by its words.";
            }
        }
        return null;
    }

    /// <summary>
    /// The player's address on <paramref name="page"/> by the hint, or null when the hint finds nothing there (the caller then
    /// looks for the player the usual way).
    /// </summary>
    /// <param name="doc">The page, parsed (not needed for an address template).</param>
    /// <param name="html">The page's HTML (for a pattern).</param>
    public static string? Find(string? hint, IDocument? doc, string? html, Uri page)
    {
        hint = hint?.Trim();
        if (string.IsNullOrEmpty(hint)) return null;
        if (hint.StartsWith(RegexPrefix, StringComparison.OrdinalIgnoreCase)) return FromPattern(hint[RegexPrefix.Length..].Trim(), html, page);
        if (IsTemplate(hint)) return FromTemplate(hint, page);
        return doc is null ? null : FromSelector(hint, doc, page);
    }

    /// <summary>
    /// What a hidden browser needs to look for the hint in a live page: ("selector", the selector, its attribute or null), or
    /// ("regex", the pattern, null). Null for an address template (nothing to look for) or an empty hint.
    /// </summary>
    public static (string Kind, string Value, string? Attribute)? Describe(string? hint)
    {
        hint = hint?.Trim();
        if (string.IsNullOrEmpty(hint) || IsTemplate(hint)) return null;
        if (hint.StartsWith(RegexPrefix, StringComparison.OrdinalIgnoreCase)) return ("regex", hint[RegexPrefix.Length..].Trim(), null);
        var (selector, attribute) = SplitAttribute(hint);
        return ("selector", selector, attribute);
    }

    /// <summary>The attributes an address is read from when the hint names none.</summary>
    public static IReadOnlyList<string> DefaultAttributes => Attributes;

    /// <summary>"iframe.live@data-embed" → ("iframe.live", "data-embed"); no @ → (the whole hint, null).</summary>
    private static (string Selector, string? Attribute) SplitAttribute(string hint) =>
        AttributeSuffixRegex().Match(hint) is { Success: true } m ? (m.Groups["sel"].Value, m.Groups["attr"].Value) : (hint, null);

    internal static string? FromTemplate(string template, Uri page)
    {
        var path = page.AbsolutePath.Trim('/');
        var slug = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
        var filled = PlaceholderRegex().Replace(template, m => m.Groups["name"].Value.ToLowerInvariant() switch
        {
            "slug" or "id" => slug,
            "path" => path,
            "host" => page.Host,
            "url" => page.AbsoluteUri,
            "urlencoded" => Uri.EscapeDataString(page.AbsoluteUri),
            _ => m.Value,
        });
        if (filled.StartsWith("//", StringComparison.Ordinal)) filled = page.Scheme + ":" + filled;
        return Uri.TryCreate(filled, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;
    }

    private static string? FromPattern(string pattern, string? html, Uri page)
    {
        if (pattern.Length == 0 || string.IsNullOrEmpty(html)) return null;
        try
        {
            var text = WebUtility.HtmlDecode(html.Replace("\\/", "/", StringComparison.Ordinal)); // "https:\/\/…" in scripts
            var match = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(2)).Match(text);
            if (!match.Success) return null;
            var value = match.Groups["url"].Success ? match.Groups["url"].Value : match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;
            return Absolute(value, page);
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static string? FromSelector(string hint, IDocument doc, Uri page)
    {
        var (selector, attribute) = SplitAttribute(hint);
        IEnumerable<IElement> found;
        try
        {
            found = doc.QuerySelectorAll(selector).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null; // not a selector the parser understands
        }
        foreach (var element in found)
        {
            var names = attribute is null ? Attributes : new[] { attribute };
            foreach (var name in names)
            {
                if (Absolute(element.GetAttribute(name), page) is { } address) return address;
            }
            // A <video> keeps its file on a <source> inside it.
            foreach (var source in element.QuerySelectorAll("source[src]"))
            {
                if (Absolute(source.GetAttribute("src"), page) is { } file) return file;
            }
        }
        return null;
    }

    private static string? Absolute(string? value, Uri page)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = WebUtility.HtmlDecode(value.Trim());
        if (text.StartsWith('#') || text.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || text.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return null;
        return Uri.TryCreate(page, text, out var uri) && uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;
    }
}
