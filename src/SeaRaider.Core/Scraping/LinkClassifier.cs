using System.Text.RegularExpressions;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Scraping;

/// <summary>
/// Decides whether a URL is something SeaRaider can download, and what kind.
/// Recognises magnets, .torrent files, single archives/installers, file-host pages (<see cref="FileHosts"/>) and every
/// common split-archive naming scheme so that volumes can be grouped and ordered. Every link gets a
/// <see cref="DownloadLink.Mirror"/> name ("Torrent", "Buzzheavier", "example.com") for the store page's mirror list.
/// </summary>
public static partial class LinkClassifier
{
    private static readonly HashSet<string> SingleFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".exe", ".msi", ".iso",
    };

    /// <summary>Extensions that are never downloads even when matched by a profile selector.</summary>
    private static readonly HashSet<string> NonDownloadExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".svg", ".bmp", ".ico",
        ".html", ".htm", ".php", ".asp", ".aspx", ".css", ".js", ".xml", ".json",
    };

    // game.part01.rar, game.part1.rar
    [GeneratedRegex(@"^(?<base>.+)\.part(?<n>\d{1,4})\.rar$", RegexOptions.IgnoreCase)]
    private static partial Regex RarPartRegex();

    // Old-style RAR volumes: game.rar (first), game.r00, game.r01 …
    [GeneratedRegex(@"^(?<base>.+)\.r(?<n>\d{2,3})$", RegexOptions.IgnoreCase)]
    private static partial Regex RarOldVolumeRegex();

    // Split zip: game.z01, game.z02 … game.zip (the .zip is the LAST volume)
    [GeneratedRegex(@"^(?<base>.+)\.z(?<n>\d{2,3})$", RegexOptions.IgnoreCase)]
    private static partial Regex ZipSplitRegex();

    // Byte-split: game.7z.001, game.zip.002, game.001
    [GeneratedRegex(@"^(?<base>.+)\.(?<n>\d{3})$", RegexOptions.IgnoreCase)]
    private static partial Regex NumberedSplitRegex();

    [GeneratedRegex(@"^(?<base>.+)\.(?<ext>rar|zip)$", RegexOptions.IgnoreCase)]
    private static partial Regex RarOrZipRegex();

    /// <summary>
    /// A part number in a link's text, for hosts whose links carry no file name: "Part 3", "Pt. 03", "Parte 3",
    /// "Teil 3", "3/6", "3 of 6", or just "3".
    /// </summary>
    [GeneratedRegex(@"^(?:.*?\b(?:part|pt|parte|teil|partie|cz[eę]\u015B\u0107|\u0447\u0430\u0441\u0442\u044C)\.?\s*#?0*(?<n>\d{1,3})\b.*|\s*#?0*(?<n>\d{1,3})\s*(?:(?:/|of|из)\s*\d{1,3})?\s*(?:\(.*\))?\s*)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex LabelPartRegex();

    /// <summary>"Click Here", "here", "link": a button label that says nothing about the file.</summary>
    [GeneratedRegex(@"^\W*(?:click|tap|press)?\s*(?:here|this link|link|get it|get|go|visit|open|direct link|fast link)\W*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GenericLabelRegex();

    /// <summary>Part number from a link's text, or null.</summary>
    internal static int? PartFromLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var m = LabelPartRegex().Match(label.Trim());
        return m.Success && int.TryParse(m.Groups["n"].Value, out var n) && n > 0 ? n : null;
    }

    /// <summary>Volume number used for the final ".zip" of a split zip set (it must be read last).</summary>
    internal const int SplitZipTerminalPart = 9_999;

    /// <summary>
    /// Classifies a URL. Returns null when the URL is not a download.
    /// </summary>
    /// <param name="url">Absolute URL (http/https/magnet).</param>
    /// <param name="anchorText">Link text, used as a label.</param>
    /// <param name="forceDownload">
    /// True when a per-site selector explicitly marked this link as a download: unknown extensions are
    /// then accepted as <see cref="DownloadLinkKind.DirectHttp"/> (file-host landing URLs etc.).
    /// </param>
    public static DownloadLink? Classify(Uri url, string? anchorText, bool forceDownload = false)
    {
        var label = Clean(anchorText);

        if (url.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase))
        {
            if (!url.OriginalString.Contains("xt=urn:bt", StringComparison.OrdinalIgnoreCase)) return null;
            return new DownloadLink
            {
                Url = url.OriginalString,
                Kind = DownloadLinkKind.Magnet,
                Label = string.IsNullOrEmpty(label) ? "Magnet link" : label,
                FileName = TryGetMagnetName(url.OriginalString) ?? string.Empty,
                Host = "magnet",
                Mirror = "Torrent",
            };
        }

        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps) return null;

        // File-hosting services: a page per file, with the file name (if any) in the fragment or path.
        if (FileHosts.MatchFileLink(url) is { } fileHost)
        {
            var hostedName = FileHosts.GuessFileName(url);
            if (hostedName.Length == 0 && LooksLikeFileName(label)) hostedName = label;
            // "Click Here" says nothing: show the host's name instead.
            if (GenericLabelRegex().IsMatch(label)) label = string.Empty;
            var hosted = new DownloadLink
            {
                Url = url.AbsoluteUri,
                FileName = hostedName,
                Host = url.Host,
                Mirror = fileHost.DisplayName,
                Label = string.IsNullOrEmpty(label) ? (hostedName.Length > 0 ? hostedName : fileHost.DisplayName) : label,
                Kind = DownloadLinkKind.FileHost,
            };
            if (hostedName.Length > 0 && TryParseVolume(hostedName, out var hostedKey, out var hostedPart))
            {
                hosted.Kind = DownloadLinkKind.MultipartArchivePart;
                hosted.GroupKey = hostedKey;
                hosted.PartNumber = hostedPart;
            }
            else if (hostedName.Length == 0 && PartFromLabel(label) is { } labelledPart)
            {
                // No file name, but the page says which part it is ("Part 2"): a set on this host, named by the server
                // when it downloads. Without this, only the first of the host's links was planned.
                hosted.Kind = DownloadLinkKind.MultipartArchivePart;
                hosted.GroupKey = $"{fileHost.Key}#parts";
                hosted.PartNumber = labelledPart;
            }
            return hosted;
        }

        var fileName = Uri.UnescapeDataString(url.Segments.LastOrDefault() ?? string.Empty).Trim('/');
        var ext = Path.GetExtension(fileName);
        var link = new DownloadLink
        {
            Url = url.AbsoluteUri,
            FileName = fileName,
            Host = url.Host,
            Label = string.IsNullOrEmpty(label) ? fileName : label,
            Mirror = url.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? url.Host[4..] : url.Host,
        };

        if (ext.Equals(".torrent", StringComparison.OrdinalIgnoreCase))
        {
            link.Kind = DownloadLinkKind.TorrentFile;
            link.Mirror = "Torrent";
            return link;
        }

        if (TryParseVolume(fileName, out var groupKey, out var part))
        {
            link.Kind = DownloadLinkKind.MultipartArchivePart;
            link.GroupKey = groupKey;
            link.PartNumber = part;
            return link;
        }

        if (SingleFileExtensions.Contains(ext))
        {
            link.Kind = DownloadLinkKind.DirectHttp;
            // A plain game.rar / game.zip may turn out to be volume 1 / the last volume of a set;
            // NormalizeGroups promotes it once its siblings are known.
            var m = RarOrZipRegex().Match(fileName);
            if (m.Success)
            {
                link.GroupKey = m.Groups["base"].Value.ToLowerInvariant();
                link.PartNumber = m.Groups["ext"].Value.Equals("rar", StringComparison.OrdinalIgnoreCase)
                    ? 1
                    : SplitZipTerminalPart;
            }
            return link;
        }

        if (forceDownload && !NonDownloadExtensions.Contains(ext))
        {
            link.Kind = DownloadLinkKind.DirectHttp;
            return link;
        }

        return null;
    }

    /// <summary>
    /// Parses split-archive naming schemes. Group keys are lower-cased base names; part numbers are
    /// 1-based and sort into the order the extractor must read them.
    /// </summary>
    public static bool TryParseVolume(string fileName, out string groupKey, out int partNumber) =>
        TryParseVolume(fileName, out groupKey, out partNumber, out _);

    /// <inheritdoc cref="TryParseVolume(string, out string, out int)"/>
    /// <param name="scheme">Which naming scheme matched — decides how the volumes are opened.</param>
    public static bool TryParseVolume(string fileName, out string groupKey, out int partNumber, out VolumeScheme scheme)
    {
        Match m;
        if ((m = RarPartRegex().Match(fileName)).Success)
        {
            groupKey = m.Groups["base"].Value.ToLowerInvariant();
            partNumber = int.Parse(m.Groups["n"].Value);
            scheme = VolumeScheme.RarParts;
            return true;
        }
        if ((m = RarOldVolumeRegex().Match(fileName)).Success)
        {
            groupKey = m.Groups["base"].Value.ToLowerInvariant();
            partNumber = int.Parse(m.Groups["n"].Value) + 2; // game.rar = 1, .r00 = 2, .r01 = 3 …
            scheme = VolumeScheme.RarOldStyle;
            return true;
        }
        if ((m = ZipSplitRegex().Match(fileName)).Success)
        {
            groupKey = m.Groups["base"].Value.ToLowerInvariant();
            partNumber = int.Parse(m.Groups["n"].Value); // .z01 = 1 … and .zip = terminal
            scheme = VolumeScheme.SplitZip;
            return true;
        }
        if ((m = NumberedSplitRegex().Match(fileName)).Success && int.Parse(m.Groups["n"].Value) > 0)
        {
            groupKey = m.Groups["base"].Value.ToLowerInvariant();
            partNumber = int.Parse(m.Groups["n"].Value);
            scheme = VolumeScheme.ByteSplit;
            return true;
        }

        groupKey = string.Empty;
        partNumber = 0;
        scheme = default;
        return false;
    }

    /// <summary>
    /// De-duplicates links and promotes lone ".rar"/".zip" files to multipart volumes when siblings
    /// of the same set are present (game.rar + game.r00, or game.zip + game.z01).
    /// </summary>
    public static List<DownloadLink> NormalizeGroups(IEnumerable<DownloadLink> links)
    {
        var unique = links
            .GroupBy(l => l.Url, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        // Sets are per mirror: the same game.part01.rar on two hosts is two copies of one set, not one set of four parts.
        var multipartKeys = unique
            .Where(l => l.Kind == DownloadLinkKind.MultipartArchivePart && l.GroupKey is not null)
            .Select(l => SetKey(l))
            .ToHashSet();

        foreach (var link in unique)
        {
            if (link.Kind == DownloadLinkKind.DirectHttp && link.GroupKey is not null && multipartKeys.Contains(SetKey(link)))
            {
                link.Kind = DownloadLinkKind.MultipartArchivePart;
            }
            else if (link.Kind == DownloadLinkKind.DirectHttp)
            {
                // A standalone archive: grouping data is meaningless.
                link.GroupKey = null;
                link.PartNumber = 0;
            }
        }

        foreach (var set in unique.Where(l => l.Kind == DownloadLinkKind.MultipartArchivePart).GroupBy(SetKey))
        {
            foreach (var part in set) part.Label = $"{part.GroupKey} — part {OrdinalIndex(set, part)} of {set.Count()}";
        }

        return unique;

        static string SetKey(DownloadLink l) => $"{l.Mirror}|{l.GroupKey}";

        static int OrdinalIndex(IEnumerable<DownloadLink> set, DownloadLink part) =>
            set.OrderBy(p => p.PartNumber).ToList().IndexOf(part) + 1;
    }

    /// <summary>Extracts the "dn" (display name) parameter of a magnet URI.</summary>
    public static string? TryGetMagnetName(string magnet)
    {
        var query = magnet.IndexOf('?') is var i and >= 0 ? magnet[(i + 1)..] : string.Empty;
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0].Equals("dn", StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(kv[1].Replace('+', ' '));
        }
        return null;
    }

    private static bool LooksLikeFileName(string text) =>
        text.Length is > 3 and < 200 && Path.HasExtension(text) && !text.Contains(' ') &&
        Path.GetExtension(text).Length is >= 3 and <= 5;

    private static string Clean(string? text) =>
        string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
}

/// <summary>Split-archive naming schemes.</summary>
public enum VolumeScheme
{
    /// <summary>game.part1.rar, game.part2.rar … (true RAR volumes)</summary>
    RarParts,

    /// <summary>game.rar, game.r00, game.r01 … (true RAR volumes)</summary>
    RarOldStyle,

    /// <summary>game.z01, game.z02 … game.zip (true ZIP split volumes)</summary>
    SplitZip,

    /// <summary>game.7z.001, game.7z.002 … (one archive cut into raw byte chunks — concatenate to read)</summary>
    ByteSplit,
}
