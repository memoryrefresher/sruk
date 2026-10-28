using System.Text;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Scraping;

/// <summary>What kind of download a mirror is; decides its section on the store page.</summary>
public enum LinkCategory
{
    /// <summary>Single files on a file-hosting service (Buzzheavier, DataNodes …).</summary>
    FileHost,

    /// <summary>Plain files served directly by a website.</summary>
    DirectDownload,

    /// <summary>A split archive (part1.rar, .7z.001 …), on one mirror.</summary>
    MultiPartArchive,

    /// <summary>Magnet link or .torrent file.</summary>
    Torrent,

    /// <summary>Only works in a web browser (MEGA, tracker pages).</summary>
    BrowserOnly,
}

/// <summary>
/// One download option after de-duplication: every link of one mirror and kind, e.g. "DataNodes, 24-part archive" or
/// "Torrent". <see cref="Links"/> are all needed (the parts of a set, or a release's files on one host).
/// </summary>
/// <param name="Category">Section on the store page.</param>
/// <param name="Mirror">Host / mirror name ("DataNodes", "filekeeper.net", "Torrent").</param>
/// <param name="Tag">Label ("Direct download", "24-part archive", "Torrent", "3 files").</param>
/// <param name="Links">The links, in download order.</param>
/// <param name="SizeText">Size printed next to the links, when there was one.</param>
public sealed record MirrorGroup(LinkCategory Category, string Mirror, string Tag, IReadOnlyList<DownloadLink> Links, string? SizeText)
{
    /// <summary>The link that stands for this option (first part / first file).</summary>
    public DownloadLink Representative => Links[0];

    public HostAccess? Access => FileHosts.Find(Representative.Host)?.Access;
}

/// <summary>
/// Cleans up a page's download links before they're shown or planned. Release pages repeat themselves: FitGirl lists
/// the same magnet for 1337x and RuTor, a host's links appear in a list and again in a spoiler, URLs differ only by
/// "www.", a trailing slash, tracking parameters or the file name in the fragment.
/// <list type="number">
///   <item><b>Identical endpoints</b>: one canonical key per link (<see cref="CanonicalKey"/>): magnets by info-hash,
///         file-host links by host and file id, other URLs without "www.", default ports, trailing slashes or utm_ parameters.</item>
///   <item><b>Same file on the same mirror</b>: links with the same file name on one host are one file.</item>
///   <item><b>Tracker pages</b> (1337x, RuTor) are dropped when the page also has a magnet or .torrent.</item>
///   <item><b>Blocked hosts</b> (<see cref="HostPolicy"/>: MixDrop) are dropped.</item>
/// </list>
/// <see cref="Group"/> then folds what's left into one option per mirror and kind, so the list never repeats a host
/// without saying why.
/// </summary>
public static class DownloadLinkDeduplicator
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static List<DownloadLink> Deduplicate(IEnumerable<DownloadLink> links)
    {
        var result = new List<DownloadLink>();
        var byKey = new Dictionary<string, DownloadLink>(StringComparer.Ordinal);
        var byMirrorFile = new Dictionary<string, DownloadLink>(StringComparer.OrdinalIgnoreCase);

        // Hosts that stop on a captcha for every file (MixDrop) are never offered, even from a page saved before the rule existed.
        foreach (var link in links.Where(l => !HostPolicy.IsBlocked(l)))
        {
            var key = CanonicalKey(link);
            if (byKey.TryGetValue(key, out var seen))
            {
                Merge(seen, link);
                continue;
            }

            if (link.Kind is not (DownloadLinkKind.Magnet or DownloadLinkKind.TorrentFile) && link.FileName.Length > 0)
            {
                var fileKey = $"{MirrorOf(link)}|{link.FileName}";
                if (byMirrorFile.TryGetValue(fileKey, out var sameFile))
                {
                    Merge(sameFile, link);
                    byKey[key] = sameFile;
                    continue;
                }
                byMirrorFile[fileKey] = link;
            }

            byKey[key] = link;
            result.Add(link);
        }

        // Tracker pages only help when there's nothing better.
        if (result.Any(l => l.Kind is DownloadLinkKind.Magnet or DownloadLinkKind.TorrentFile))
            result.RemoveAll(l => FileHosts.Find(l.Host)?.Key == "torrent-host");

        return result;
    }

    /// <summary>Key that is equal for two links to the same thing.</summary>
    public static string CanonicalKey(DownloadLink link)
    {
        if (link.Kind == DownloadLinkKind.Magnet)
            return MagnetInfoHash(link.Url) is { } hash ? "btih:" + hash : "magnet:" + link.Url.Trim().ToLowerInvariant();

        if (!Uri.TryCreate(link.Url.Trim(), UriKind.Absolute, out var uri)) return link.Url.Trim();

        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        host = host.ToLowerInvariant();
        var path = uri.AbsolutePath.TrimEnd('/');

        // A file host identifies the file by its path; the fragment only carries a display name.
        if (FileHosts.Find(uri.Host) is not null) return $"host:{host}{path}{CleanQuery(uri.Query)}";

        var port = uri.IsDefaultPort ? string.Empty : ":" + uri.Port;
        return $"url:{host}{port}{path}{CleanQuery(uri.Query)}";
    }

    /// <summary>Lower-case hex info-hash of a magnet (v1 "btih", hex or base32), or null.</summary>
    public static string? MagnetInfoHash(string magnet)
    {
        var start = magnet.IndexOf("urn:btih:", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        var value = new string(magnet[(start + 9)..].TakeWhile(char.IsLetterOrDigit).ToArray());

        if (value.Length == 40 && value.All(Uri.IsHexDigit)) return value.ToLowerInvariant();
        if (value.Length == 32) return Base32ToHex(value.ToUpperInvariant());
        return null;
    }

    /// <summary>
    /// One option per mirror and kind, in the order the store page shows them: file hosts and direct downloads,
    /// multi-part archives, torrents, then browser-only links.
    /// </summary>
    public static List<MirrorGroup> Group(IEnumerable<DownloadLink> links)
    {
        var clean = Deduplicate(links);
        var groups = new List<MirrorGroup>();

        foreach (var torrent in clean.Where(IsTorrent))
        {
            var name = torrent.LeadsToTorrent
                ? MirrorOf(torrent)
                : torrent.Kind == DownloadLinkKind.Magnet ? "Magnet link" : "Torrent file";
            groups.Add(new MirrorGroup(LinkCategory.Torrent, name, "Torrent", [torrent], torrent.SizeText));
        }

        foreach (var set in clean.Where(l => l.Kind == DownloadLinkKind.MultipartArchivePart && !l.LeadsToTorrent).GroupBy(l => (Mirror: MirrorOf(l), l.GroupKey)))
        {
            var parts = set.OrderBy(l => l.PartNumber).ToList();
            var count = parts.Select(p => p.PartNumber).Distinct().Count();
            var category = FileHosts.Find(parts[0].Host)?.Access == HostAccess.ExternalOnly ? LinkCategory.BrowserOnly : LinkCategory.MultiPartArchive;
            groups.Add(new MirrorGroup(category, set.Key.Mirror, $"{count}-part archive", parts, parts.Select(p => p.SizeText).FirstOrDefault(s => s is not null)));
        }

        foreach (var mirror in clean.Where(l => l.Kind is DownloadLinkKind.DirectHttp or DownloadLinkKind.FileHost && !l.LeadsToTorrent).GroupBy(MirrorOf))
        {
            var files = mirror.ToList();
            var host = FileHosts.Find(files[0].Host);

            // Several links on one host without file names are kept, all of them: on most sites they're the parts of
            // one download ("Part 1 … Part 6"). Treating them as alternatives downloaded part 1 alone and called it
            // installed.

            var category = host switch
            {
                { Access: HostAccess.ExternalOnly } => LinkCategory.BrowserOnly,
                not null => LinkCategory.FileHost,
                _ => LinkCategory.DirectDownload,
            };
            var tag = category switch
            {
                LinkCategory.BrowserOnly => "Opens in browser",
                _ when files.Count > 1 => $"{files.Count} files",
                LinkCategory.FileHost => "File host",
                _ => "Direct download",
            };
            groups.Add(new MirrorGroup(category, mirror.Key, tag, files, files.Select(f => f.SizeText).FirstOrDefault(s => s is not null)));
        }

        return groups
            .OrderBy(g => g.Category switch
            {
                LinkCategory.FileHost or LinkCategory.DirectDownload => 0,
                LinkCategory.MultiPartArchive => 1,
                LinkCategory.Torrent => 2,
                _ => 3,
            })
            .ToList();
    }

    /// <summary>The option a link belongs to (null when the link isn't in <paramref name="groups"/>).</summary>
    public static MirrorGroup? GroupOf(IEnumerable<MirrorGroup> groups, DownloadLink link)
    {
        var key = CanonicalKey(link);
        return groups.FirstOrDefault(g => g.Links.Any(l => ReferenceEquals(l, link) || CanonicalKey(l) == key));
    }

    /// <summary>A magnet, a .torrent, or a page that leads to one (a shortener button labelled "Torrent").</summary>
    public static bool IsTorrent(DownloadLink link) =>
        link.Kind is DownloadLinkKind.Magnet or DownloadLinkKind.TorrentFile || link.LeadsToTorrent;

    /// <summary>
    /// Mirror display name: the name printed next to a link-shortener button ("Gofile"), the host's name ("DataNodes"),
    /// else the domain without "www.".
    /// </summary>
    public static string MirrorOf(DownloadLink link)
    {
        if (FileHosts.IsLinkShortener(link.Host) && !string.IsNullOrEmpty(link.Mirror)) return link.Mirror;
        if (FileHosts.Find(link.Host) is { } known) return known.DisplayName;
        if (!string.IsNullOrEmpty(link.Mirror)) return link.Mirror;
        return link.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? link.Host[4..] : link.Host;
    }

    private static void Merge(DownloadLink kept, DownloadLink duplicate)
    {
        if (kept.FileName.Length == 0 && duplicate.FileName.Length > 0) kept.FileName = duplicate.FileName;
        kept.SizeText ??= duplicate.SizeText;
    }

    /// <summary>Query without tracking parameters, sorted, so ?a=1&amp;utm_source=x equals ?a=1.</summary>
    private static string CleanQuery(string query)
    {
        if (string.IsNullOrEmpty(query) || query == "?") return string.Empty;
        var kept = query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) &&
                        !p.StartsWith("ref=", StringComparison.OrdinalIgnoreCase) &&
                        !p.StartsWith("fbclid=", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        return kept.Count == 0 ? string.Empty : "?" + string.Join('&', kept);
    }

    private static string? Base32ToHex(string base32)
    {
        var bits = 0;
        var value = 0;
        var bytes = new List<byte>(20);
        foreach (var c in base32)
        {
            var index = Base32Alphabet.IndexOf(c);
            if (index < 0) return null;
            value = (value << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)(value >> (bits - 8)));
                bits -= 8;
                value &= (1 << bits) - 1;
            }
        }
        if (bytes.Count != 20) return null;
        var hex = new StringBuilder(40);
        foreach (var b in bytes) hex.Append(b.ToString("x2"));
        return hex.ToString();
    }
}
