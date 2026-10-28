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

    /// <summary>The Live TV playlist (<c>[livetv]</c>): an M3U address or the path of a file, or null.</summary>
    public string? LiveTv { get; init; }

    /// <summary>
    /// Settings written under a source or media address (<c>name:</c>, <c>console:</c>, <c>hint:</c> …), by <see cref="SourcesFile.SettingsKey"/>.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Settings { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>();

    /// <summary>The settings under <paramref name="url"/> in the sources part (<paramref name="media"/> false) or the media part.</summary>
    public IReadOnlyDictionary<string, string> SettingsOf(string url, bool media) =>
        Settings.TryGetValue(SourcesFile.SettingsKey(url, media), out var found) ? found : EmptySettings;

    private static readonly IReadOnlyDictionary<string, string> EmptySettings = new Dictionary<string, string>();
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
///
/// Settings can also sit under an address in the sources part and the <c>[media]</c> part, and the Live TV playlist has its own part:
/// <code>
/// https://drive.google.com/file/d/…/view     (a game's download link)
///   name: Elden Ring
///   console: ps3                              (pc, or a console's id; leave out to work it out)
///
/// [media]
/// https://example.com/live/main
///   name: My stream
///   format: hls                               (hls, dash, video or embed)
///   hint: iframe#player
///   click: button.server
///   page-mode: yes
///   link: 2
///   browser: yes
///
/// [livetv]
/// https://your-provider.example/get.php?username=…&amp;password=…&amp;type=m3u_plus
/// </code>
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

    private static readonly string[] SourceSettingKeys = ["name", "console"];
    private static readonly string[] MediaSettingKeys = ["name", "format", "hint", "click", "page-mode", "link", "browser"];

    public static string SettingsKey(string url, bool media) => (media ? "m|" : "s|") + Key(url);

    /// <summary>An address with its settings written on the lines under it (blank values are left out).</summary>
    public static string WithSettings(string url, params (string Key, string? Value)[] settings)
    {
        var text = new StringBuilder(url);
        foreach (var (key, value) in settings)
            if (!string.IsNullOrWhiteSpace(value)) text.Append(Environment.NewLine).Append("  ").Append(key).Append(": ").Append(Escape(value.Trim()));
        return text.ToString();
    }

    private static bool TryEntrySetting(string line, string[] keys, out string key, out string value)
    {
        key = value = string.Empty;
        foreach (var name in keys)
        {
            if (!line.StartsWith(name, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = line[name.Length..].TrimStart();
            if (rest.Length == 0 || rest[0] is not (':' or '=')) continue;
            if (rest.StartsWith("://", StringComparison.Ordinal)) continue; // an address, not a setting
            key = name;
            value = Unescape(rest[1..].Trim());
            return true;
        }
        return false;
    }

    public static SourcesList Parse(string text)
    {
        string? liveTv = null;
        var settings = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        Dictionary<string, string>? currentSettings = null; // the source or media address the settings lines under it belong to
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
                    : name is "livetv" or "live tv" or "live-tv" or "iptv" or "live" ? 'l'
                    : 's';
                current = null;
                currentSettings = null;
                continue;
            }

            // The Live TV playlist: a web address or a file path, taken whole (a path may hold spaces).
            if (section == 'l')
            {
                liveTv ??= line.Trim('"');
                continue;
            }

            // Settings of the source or media address above: "name: …", "console: …", "hint: …".
            if (section is 's' or 'm' && currentSettings is not null &&
                TryEntrySetting(line, section == 'm' ? MediaSettingKeys : SourceSettingKeys, out var entryKey, out var entryValue))
            {
                currentSettings[entryKey.ToLowerInvariant()] = entryValue;
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
            // A backups torrent's magnet link (Sources): kept as it is.
            if (address.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
            {
                current = null;
                currentSettings = null;
                if (section == 's' && seen.Add("s|" + Key(address))) sources.Add(address);
                continue;
            }
            if (!address.Contains("://", StringComparison.Ordinal)) address = "https://" + address;
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !uri.Host.Contains('.'))
            {
                current = null;
                currentSettings = null;
                continue;
            }

            var url = uri.AbsoluteUri;
            switch (section)
            {
                case 'u':
                    current = new UpcomingBuilder(url);
                    currentSettings = null;
                    upcoming.Add(current);
                    break;
                case 'm':
                    current = null;
                    currentSettings = null;
                    if (seen.Add("m|" + Key(url)))
                    {
                        media.Add(url);
                        currentSettings = [];
                        settings[SettingsKey(url, true)] = currentSettings;
                    }
                    break;
                default:
                    current = null;
                    currentSettings = null;
                    if (seen.Add("s|" + Key(url)))
                    {
                        sources.Add(url);
                        currentSettings = [];
                        settings[SettingsKey(url, false)] = currentSettings;
                    }
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
        return new SourcesList(sources, urls) { Media = media, UpcomingEntries = entries, LiveTv = liveTv, Settings = settings };
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

    public static string FormatEntries(IEnumerable<string> sources, IEnumerable<UpcomingEntry> upcoming, IEnumerable<string>? media = null,
        string? liveTv = null)
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
            text.AppendLine("# Sites with movies or shows, channels, feeds and pages for the Media tab:");
            text.AppendLine("[media]");
            foreach (var url in videos) text.AppendLine(url);
        }
        if (!string.IsNullOrWhiteSpace(liveTv))
        {
            text.AppendLine();
            text.AppendLine("# Your IPTV (M3U) playlist for Media > Live TV (keep this file private: the link may hold your login):");
            text.AppendLine("[livetv]");
            text.AppendLine(liveTv.Trim());
        }
        return text.ToString();
    }

    /// <summary>Compares addresses the way a person would: no scheme, no "www.", no trailing slash, any case.</summary>
    public static string Key(string url)
    {
        var key = url.Trim().ToLowerInvariant();
        // Magnets (and SeaRaider's own torrent sources) by the torrent's info hash: the same torrent however it's written.
        if (System.Text.RegularExpressions.Regex.Match(key, @"xt=urn:btih:([0-9a-z]+)|^https?://torrent\.local/([0-9a-f]+)") is { Success: true } torrent)
            return "torrent:" + (torrent.Groups[1].Success ? torrent.Groups[1].Value : torrent.Groups[2].Value);
        var scheme = key.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) key = key[(scheme + 3)..];
        if (key.StartsWith("www.", StringComparison.Ordinal)) key = key[4..];
        return key.TrimEnd('/');
    }
}
