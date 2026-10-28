using System.Text.RegularExpressions;
using SeaRaider.Core.Scraping;

namespace SeaRaider.Core.Iptv;

/// <summary>Reads an M3U / M3U8 IPTV playlist (#EXTINF lines with tvg-* and group-title attributes) into channels.</summary>
public static partial class M3uParser
{
    [GeneratedRegex(@"([\w-]+)=""([^""]*)""")]
    private static partial Regex AttributeRegex();

    private static readonly string[] SportsWords =
    [
        "sport", "espn", "nfl", "nba", "mlb", "nhl", "ufc", "wwe", "f1", "formula", "motogp", "golf", "tennis", "soccer", "football",
        "futbol", "fútbol", "cricket", "rugby", "boxing", "dazn", "bein", "tsn", "arena", "eurosport", "match", "premier league",
        "laliga", "bundesliga", "serie a", "ligue", "nascar", "pga", "ppv", "fight", "racing", "bally", "nbc sports", "cbs sports",
        "fox sports", "fs1", "fs2", "btn", "acc network", "sec network", "stadium", "supersport", "optus sport",
    ];

    private static readonly string[] NewsWords =
        ["news", "cnn", "bbc", "msnbc", "cnbc", "bloomberg", "al jazeera", "sky news", "euronews", "newsmax", "weather", "c-span", "cspan"];

    private static readonly string[] MovieWords = ["movie", "cinema", "film", "hbo", "showtime", "starz", "cinemax", "amc", "tcm", "series", "vod"];
    private static readonly string[] KidsWords = ["kids", "cartoon", "disney", "nick", "boomerang", "junior", "baby", "children", "pbs kids"];
    private static readonly string[] MusicWords = ["music", "mtv", "vh1", "radio", "vevo", "hits", "concert"];

    /// <summary>Which category a channel with this group and name belongs to.</summary>
    public static IptvCategory Categorize(string group, string name)
    {
        var text = (group + " " + name).ToLowerInvariant();
        // Whole short words ("f1", "fs1", "tsn", "btn") must not match inside other words; longer ones may.
        bool Has(string[] words) => words.Any(w => w.Length <= 4 ? Regex.IsMatch(text, $@"(?<![a-z0-9]){Regex.Escape(w)}(?![a-z0-9])") : text.Contains(w, StringComparison.Ordinal));
        if (Has(SportsWords)) return IptvCategory.Sports;
        if (Has(NewsWords)) return IptvCategory.News;
        if (Has(KidsWords)) return IptvCategory.Kids;
        if (Has(MusicWords)) return IptvCategory.Music;
        if (Has(MovieWords)) return IptvCategory.Movies;
        return IptvCategory.Other;
    }

    public static List<IptvChannel> Parse(TextReader reader)
    {
        var channels = new List<IptvChannel>();
        string? name = null, group = null, logo = null, tvgId = null;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            line = line.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
            {
                (name, group, logo, tvgId) = ReadInfo(line);
            }
            else if (line.StartsWith("#EXTGRP:", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(group)) group = line[8..].Trim();
            }
            else if (line[0] == '#')
            {
                continue; // #EXTM3U, #EXTVLCOPT … carry nothing needed here
            }
            else if (Uri.TryCreate(line, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                var title = string.IsNullOrWhiteSpace(name) ? uri.Host : name!.Trim();
                var g = (group ?? string.Empty).Trim();
                channels.Add(new IptvChannel(channels.Count + 1, title, g,
                    Uri.TryCreate(logo, UriKind.Absolute, out var l) && l.Scheme is "http" or "https" ? l.AbsoluteUri : null,
                    line, tvgId ?? string.Empty, Categorize(g, title)));
                name = group = logo = tvgId = null;
            }
            else
            {
                name = group = logo = tvgId = null; // an address that can't play here (rtmp://, udp://)
            }
        }
        return channels;
    }

    private static (string? Name, string? Group, string? Logo, string? TvgId) ReadInfo(string line)
    {
        string? group = null, logo = null, id = null, tvgName = null;
        foreach (Match m in AttributeRegex().Matches(line))
        {
            var value = m.Groups[2].Value;
            switch (m.Groups[1].Value.ToLowerInvariant())
            {
                case "group-title": group = value; break;
                case "tvg-logo": logo = value; break;
                case "tvg-id": id = value; break;
                case "tvg-name": tvgName = value; break;
            }
        }
        // The shown name follows the last comma after the attributes.
        var from = Math.Max(line.LastIndexOf('"'), 0);
        var comma = line.IndexOf(',', from);
        var name = comma >= 0 ? line[(comma + 1)..].Trim() : null;
        if (string.IsNullOrWhiteSpace(name)) name = tvgName;
        return (name, group, logo, id);
    }

    /// <summary>
    /// The address to give the player. A browser engine can't play raw MPEG-TS, which is what many panels hand out for a bare
    /// "…/user/pass/12345" or "….ts" address; these panels also serve the same channel as HLS at "….m3u8", so that's asked for.
    /// An address that says nothing about its format is marked as HLS.
    /// </summary>
    public static string PlayUrl(string streamUrl)
    {
        if (!Uri.TryCreate(streamUrl, UriKind.Absolute, out var uri)) return streamUrl;
        var path = uri.AbsolutePath;
        if (path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
            return streamUrl;

        var xtream = XtreamRegex().Match(path);
        if (xtream.Success)
        {
            var b = new UriBuilder(uri) { Path = $"/live/{xtream.Groups["rest"].Value}.m3u8" };
            return b.Uri.AbsoluteUri;
        }
        if (path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
        {
            var b = new UriBuilder(uri) { Path = path[..^3] + ".m3u8" };
            return b.Uri.AbsoluteUri;
        }
        return MediaStreams.WithHint(streamUrl, StreamFormat.Hls);
    }

    // /user/pass/123  ·  /live/user/pass/123.ts  (movie/ and series/ stay as they are: those are files)
    [GeneratedRegex(@"^/(?:live/)?(?!(?:movie|series|timeshift)/)(?<rest>[^/]+/[^/]+/\d+)(?:\.ts)?$", RegexOptions.IgnoreCase)]
    private static partial Regex XtreamRegex();
}
