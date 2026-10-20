using System.Text;

namespace SeaRaider.Core.Catalog;

/// <summary>Download sources and upcoming-release sites, as written in a sources file.</summary>
public sealed record SourcesList(IReadOnlyList<string> Sources, IReadOnlyList<string> Upcoming)
{
    /// <summary>The Media tab's channels, feeds and pages (<c>[media]</c>).</summary>
    public IReadOnlyList<string> Media { get; init; } = [];

    /// <summary>
    /// The upcoming sites with their settings (custom list start / end HTML, on / off). <see cref="Upcoming"/> holds the
    /// same addresses without the settings.
    /// </summary>
    public IReadOnlyList<UpcomingEntry> UpcomingEntries { get; init; } = [];
}

/// <summary>
/// One upcoming site as written in the sources file. A "custom list" has <see cref="StartHtml"/> (and usually
/// <see cref="EndHtml"/>): only the part of the page between them is read.
/// </summary>
public sealed record UpcomingEntry(string Url, string? StartHtml = null, string? EndHtml = null, bool Enabled = true)
{
    public bool IsCustom => !string.IsNullOrWhiteSpace(StartHtml);

    /// <summary>Tells two sites apart: the address, plus the start / end HTML for a custom list (one page can hold several).</summary>
    public string Identity => Identify(Url, IsCustom ? StartHtml : null, IsCustom ? EndHtml : null);

    public static string Identify(string url, string? startHtml, string? endHtml) =>
        string.IsNullOrWhiteSpace(startHtml)
            ? SourcesFile.Key(url)
            : SourcesFile.Key(url) + "\n" + startHtml.Trim() + "\n" + (endHtml ?? string.Empty).Trim();
}

/// <summary>
/// The plain-text sources file (<c>sources.txt</c>): one web address per line, so it can be edited in Notepad, shared, or
/// dropped next to SeaRaider before its first start.
/// <code>
/// # comments start with # (or //)
/// https://fitgirl-repacks.site
/// https://steamrip.com
///
/// [upcoming]
/// https://store.steampowered.com/search/?filter=comingsoon
///
/// [media]
/// https://www.youtube.com/@IGN
/// </code>
/// Addresses before any section (or under <c>[sources]</c>) are download sources; those under <c>[upcoming]</c> feed the
/// store's Coming soon shelf. "https://" may be left out. Duplicates are ignored.
///
/// An upcoming site can carry its settings on the lines right under its address:
/// <code>
/// [upcoming]
/// https://example.com/feed
///   start: &lt;h2&gt;Coming soon
///   end: &lt;/ul&gt;
///   enabled: no
/// </code>
/// <c>start:</c> / <c>end:</c> make it a custom list (only the page between them is read, details come from Steam);
/// a line break inside the HTML is written <c>\n</c> (and a backslash <c>\\</c>). <c>enabled: no</c> switches the site off
/// (leave the line out for on).
/// </summary>
public static class SourcesFile
{
    public const string FileName = "sources.txt";

    private sealed class UpcomingBuilder(string url)
    {
        public string Url { get; } = url;
        public string? Start { get; set; }
        public string? End { get; set; }
        public bool Enabled { get; set; } = true;
    }

    public static SourcesList Parse(string text)
    {
        var sources = new List<string>();
        var upcoming = new List<UpcomingBuilder>();
        var media = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var section = 's';
        UpcomingBuilder? current = null; // the upcoming site the settings lines under it belong to

        foreach (var raw in text.Replace("\r", string.Empty).Split('\n'))
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith(';')) continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var name = line[1..^1].Trim().ToLowerInvariant();
                section = name is "upcoming" or "coming soon" or "coming-soon" or "upcoming releases" ? 'u'
                    : name is "media" or "videos" or "streams" or "trailers" ? 'm'
                    : 's';
                current = null;
                continue;
            }

            // Settings of the upcoming site above: "start: …", "end: …", "enabled: no".
            if (section == 'u' && TrySetting(line, out var key, out var value))
            {
                if (current is null) continue;
                switch (key)
                {
                    case "start": current.Start = Unescape(value) is { Length: > 0 } st ? st : null; break;
                    case "end": current.End = Unescape(value) is { Length: > 0 } en ? en : null; break;
                    default: current.Enabled = !IsNo(value); break;
                }
                continue;
            }

            // "https://site.com  # my favourite": keep the address only.
            var address = line.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries)[0];
            if (!address.Contains("://", StringComparison.Ordinal)) address = "https://" + address;
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !uri.Host.Contains('.'))
            {
                current = null;
                continue;
            }

            var url = uri.AbsoluteUri;
            switch (section)
            {
                case 'u':
                    current = new UpcomingBuilder(url);
                    upcoming.Add(current);
                    break;
                case 'm':
                    current = null;
                    if (seen.Add("m|" + Key(url))) media.Add(url);
                    break;
                default:
                    current = null;
                    if (seen.Add("s|" + Key(url))) sources.Add(url);
                    break;
            }
        }

        // Duplicates: the same address (and, for a custom list, the same start and end) once.
        var entries = new List<UpcomingEntry>();
        var seenUpcoming = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in upcoming)
        {
            var entry = new UpcomingEntry(b.Url, b.Start?.Trim(), b.End?.Trim(), b.Enabled);
            if (seenUpcoming.Add(entry.Identity)) entries.Add(entry);
        }
        var urls = entries.Select(e => e.Url).Distinct(StringComparer.Ordinal).ToList();
        return new SourcesList(sources, urls) { Media = media, UpcomingEntries = entries };
    }

    private static bool TrySetting(string line, out string key, out string value)
    {
        key = value = string.Empty;
        foreach (var name in new[] { "start", "end", "enabled" })
        {
            if (!line.StartsWith(name, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = line[name.Length..].TrimStart();
            if (rest.Length == 0 || rest[0] is not (':' or '=')) continue;
            key = name;
            value = rest[1..].Trim();
            return true;
        }
        return false;
    }

    private static bool IsNo(string value) =>
        value.Trim().ToLowerInvariant() is "no" or "off" or "false" or "0" or "disabled";

    /// <summary>One line: a line break is written \n, a backslash \\.</summary>
    internal static string Escape(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\r\n", "\\n", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\n", StringComparison.Ordinal);

    internal static string Unescape(string text)
    {
        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                var next = text[i + 1];
                if (next == 'n') { result.Append('\n'); i++; continue; }
                if (next == '\\') { result.Append('\\'); i++; continue; }
            }
            result.Append(text[i]);
        }
        return result.ToString();
    }

    public static string Format(IEnumerable<string> sources, IEnumerable<string> upcoming, IEnumerable<string>? media = null) =>
        FormatEntries(sources, upcoming.Select(u => new UpcomingEntry(u)), media);

    public static string FormatEntries(IEnumerable<string> sources, IEnumerable<UpcomingEntry> upcoming, IEnumerable<string>? media = null)
    {
        var text = new StringBuilder();
        text.AppendLine("# SeaRaider sources: one web address per line. Edit freely; SeaRaider keeps this file up to date as you");
        text.AppendLine("# add and remove sources, and reads changes made here when it starts.");
        text.AppendLine();
        foreach (var url in sources) text.AppendLine(url);
        var coming = upcoming.ToList();
        if (coming.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("# Pages listing upcoming games, for the store's Coming soon shelf:");
            if (coming.Any(e => e.IsCustom || !e.Enabled))
                text.AppendLine("# Under an address: start: / end: (a custom list: only the page between them is read; \\n is a line break) and enabled: no (switched off).");
            text.AppendLine("[upcoming]");
            foreach (var entry in coming)
            {
                text.AppendLine(entry.Url);
                if (entry.IsCustom)
                {
                    text.AppendLine("  start: " + Escape(entry.StartHtml!.Trim()));
                    if (!string.IsNullOrWhiteSpace(entry.EndHtml)) text.AppendLine("  end: " + Escape(entry.EndHtml.Trim()));
                }
                if (!entry.Enabled) text.AppendLine("  enabled: no");
            }
        }
        var videos = media?.ToList() ?? [];
        if (videos.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("# Channels, feeds and pages for the Media tab:");
            text.AppendLine("[media]");
            foreach (var url in videos) text.AppendLine(url);
        }
        return text.ToString();
    }

    /// <summary>Compares addresses the way a person would: no scheme, no "www.", no trailing slash, any case.</summary>
    public static string Key(string url)
    {
        var key = url.Trim().ToLowerInvariant();
        var scheme = key.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) key = key[(scheme + 3)..];
        if (key.StartsWith("www.", StringComparison.Ordinal)) key = key[4..];
        return key.TrimEnd('/');
    }
}
