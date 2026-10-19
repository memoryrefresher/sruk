using System.Text;

namespace SeaRaider.Core.Catalog;

/// <summary>Download sources and upcoming-release sites, as written in a sources file.</summary>
public sealed record SourcesList(IReadOnlyList<string> Sources, IReadOnlyList<string> Upcoming)
{
    /// <summary>The Media tab's channels, feeds and pages (<c>[media]</c>).</summary>
    public IReadOnlyList<string> Media { get; init; } = [];
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
/// </summary>
public static class SourcesFile
{
    public const string FileName = "sources.txt";

    public static SourcesList Parse(string text)
    {
        var sources = new List<string>();
        var upcoming = new List<string>();
        var media = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var target = sources;

        foreach (var raw in text.Replace("\r", string.Empty).Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith(';')) continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var section = line[1..^1].Trim().ToLowerInvariant();
                target = section is "upcoming" or "coming soon" or "coming-soon" or "upcoming releases" ? upcoming
                    : section is "media" or "videos" or "streams" or "trailers" ? media
                    : sources;
                continue;
            }

            // "https://site.com  # my favourite": keep the address only.
            var address = line.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries)[0];
            if (!address.Contains("://", StringComparison.Ordinal)) address = "https://" + address;
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !uri.Host.Contains('.'))
                continue;

            var url = uri.AbsoluteUri;
            if (seen.Add((target == upcoming ? "u|" : target == media ? "m|" : "s|") + Key(url))) target.Add(url);
        }
        return new SourcesList(sources, upcoming) { Media = media };
    }

    public static string Format(IEnumerable<string> sources, IEnumerable<string> upcoming, IEnumerable<string>? media = null)
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
            text.AppendLine("[upcoming]");
            foreach (var url in coming) text.AppendLine(url);
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
