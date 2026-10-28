using SeaRaider.Core.Models;
using SeaRaider.Core.Scraping;

namespace SeaRaider.Core.Downloads;

/// <summary>The links a job will fetch and how.</summary>
public sealed record DownloadPlan(DownloadTransport Transport, IReadOnlyList<DownloadLink> Links);

/// <summary>
/// Picks what to download from a game's (often messy) list of links: mirrors, multiple
/// multipart sets, torrents and magnets side by side.
/// </summary>
public static class DownloadPlanner
{
    private static readonly string[] PreferredSingleExtensions = [".zip", ".7z", ".rar", ".tar", ".gz", ".xz"];

    /// <summary>
    /// Builds a plan. When <paramref name="preferred"/> is given (user picked a mirror on the store page) it is honoured;
    /// for a multipart volume the whole set from that mirror is used. Otherwise links are ranked by how hands-free they
    /// are: plain HTTP files first, then file hosts SeaRaider can resolve itself, then (unless <paramref name="preferTorrents"/>
    /// already chose it) the torrent, and only then hosts that need a person in the verification window.
    /// MEGA-style hosts (decrypted in the browser) are never planned automatically.
    /// </summary>
    /// <exception cref="InvalidOperationException">No usable links, or the only multipart set is incomplete.</exception>
    public static DownloadPlan Plan(GameMetadata game, DownloadLink? preferred, bool preferTorrents)
    {
        // Same options the store page shows: duplicates removed, one option per mirror and kind.
        var groups = DownloadLinkDeduplicator.Group(game.DownloadLinks);
        var links = groups.SelectMany(g => g.Links).ToList();

        if (preferred is not null)
        {
            if (Rank(preferred) == ExternalOnlyRank)
                throw new InvalidOperationException(
                    $"{DownloadLinkDeduplicator.MirrorOf(preferred)} only works in a web browser, so SeaRaider can't download it. Open the link in your browser instead.");

            // A shortener's "Torrent" button too: the download browser collects the magnet / .torrent behind it first.
            if (DownloadLinkDeduplicator.IsTorrent(preferred))
                return new DownloadPlan(DownloadTransport.Torrent, [preferred]);

            var group = DownloadLinkDeduplicator.GroupOf(groups, preferred);
            if (preferred.Kind == DownloadLinkKind.MultipartArchivePart)
            {
                var set = (group is null ? null : CompleteSet(group.Links))
                          ?? CompleteSet(links.Where(l => l.Kind == DownloadLinkKind.MultipartArchivePart && l.GroupKey == preferred.GroupKey &&
                                                          Rank(l) < ExternalOnlyRank))
                          ?? throw new InvalidOperationException($"Some parts of \"{preferred.GroupKey}\" are missing on the page.");
                return new DownloadPlan(DownloadTransport.Http, set);
            }

            // A host's files belong together (a setup plus its .bin files): take them all.
            return new DownloadPlan(DownloadTransport.Http, group?.Links ?? new[] { preferred });
        }

        var torrent = groups.Where(g => g.Category == LinkCategory.Torrent)
            .Select(g => g.Representative)
            .OrderBy(TorrentOrder)
            .FirstOrDefault();

        var candidates = groups
            .Where(g => g.Category is LinkCategory.FileHost or LinkCategory.DirectDownload or LinkCategory.MultiPartArchive)
            .Select(g => (Links: g.Category == LinkCategory.MultiPartArchive ? CompleteSet(g.Links) : g.Links,
                          Rank: g.Links.Max(Rank),
                          HostPriority: g.Links.Min(l => FileHosts.Priority(l)),
                          IsSet: g.Category == LinkCategory.MultiPartArchive,
                          Preferred: g.Links.Any(l => PreferredSingleExtensions.Any(ext => l.FileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))))
            .Where(c => c.Links is not null)
            // FuckingFast, then FileKeeper, then Gofile beat everything else (even a torrent); the rest keep the order below.
            .OrderBy(c => c.HostPriority)
            .ThenBy(c => c.Rank)
            .ThenBy(c => c.IsSet ? 0 : 1)
            .ThenBy(c => c.Preferred ? 0 : 1)
            .ThenByDescending(c => c.Links!.Count)
            .ToList();
        var best = candidates.FirstOrDefault();
        var http = (Links: best.Links, Rank: best.Links is null ? 0 : best.Rank);

        if (best.Links is not null && best.HostPriority != FileHosts.NotPreferred) return new DownloadPlan(DownloadTransport.Http, best.Links);
        if (preferTorrents && torrent is not null) return new DownloadPlan(DownloadTransport.Torrent, [torrent]);
        if (http.Links is not null && http.Rank <= ResolvableRank) return new DownloadPlan(DownloadTransport.Http, http.Links);
        if (torrent is not null) return new DownloadPlan(DownloadTransport.Torrent, [torrent]);
        if (http.Links is not null) return new DownloadPlan(DownloadTransport.Http, http.Links);

        throw new InvalidOperationException(
            links.Any(l => l.Kind == DownloadLinkKind.MultipartArchivePart && Rank(l) < ExternalOnlyRank)
                ? "The multipart download on this page is incomplete (missing parts)."
                : links.Any(l => Rank(l) == ExternalOnlyRank)
                    ? "This game is only offered on hosts that work in a browser (like MEGA). Open the source page to download it."
                    : "No downloadable links were found for this game.");
    }

    /// <summary>
    /// Other copies of <paramref name="chosen"/> on the page: the same volume of the same multi-part set on another host,
    /// or the same file name on another host. Used when a host has lost the file ("File not found"), best hosts first.
    /// </summary>
    public static List<DownloadLink> AlternatesFor(IEnumerable<DownloadLink> all, DownloadLink chosen)
    {
        if (DownloadLinkDeduplicator.IsTorrent(chosen)) return [];
        var chosenKey = DownloadLinkDeduplicator.CanonicalKey(chosen);
        var chosenMirror = DownloadLinkDeduplicator.MirrorOf(chosen);

        return DownloadLinkDeduplicator.Deduplicate(all)
            .Where(l => !DownloadLinkDeduplicator.IsTorrent(l))
            .Where(l => Rank(l) < ExternalOnlyRank)
            .Where(l => DownloadLinkDeduplicator.CanonicalKey(l) != chosenKey &&
                        !string.Equals(DownloadLinkDeduplicator.MirrorOf(l), chosenMirror, StringComparison.OrdinalIgnoreCase))
            .Where(l => chosen.Kind == DownloadLinkKind.MultipartArchivePart && chosen.GroupKey is not null
                ? l.Kind == DownloadLinkKind.MultipartArchivePart &&
                  string.Equals(l.GroupKey, chosen.GroupKey, StringComparison.OrdinalIgnoreCase) && l.PartNumber == chosen.PartNumber
                : chosen.FileName.Length > 0 && string.Equals(l.FileName, chosen.FileName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(l => FileHosts.Priority(l))
            .ThenBy(Rank)
            .ToList();
    }

    /// <summary>The page's torrent, if any: the last resort when every host has lost a file.</summary>
    public static DownloadLink? TorrentOf(IEnumerable<DownloadLink> all) =>
        DownloadLinkDeduplicator.Deduplicate(all)
            .Where(DownloadLinkDeduplicator.IsTorrent)
            .OrderBy(TorrentOrder)
            .FirstOrDefault();

    /// <summary>A .torrent first, then a magnet, then a page that leads to one (needs the download browser).</summary>
    private static int TorrentOrder(DownloadLink link) => link.LeadsToTorrent ? 2 : link.Kind == DownloadLinkKind.TorrentFile ? 0 : 1;

    private const int DirectRank = 0;
    private const int ResolvableRank = 1;
    private const int BrowserRank = 2;
    private const int ExternalOnlyRank = 3;

    /// <summary>0 = plain file, 1 = host SeaRaider resolves itself, 2 = needs the verification window, 3 = browser only.</summary>
    internal static int Rank(DownloadLink link) => FileHosts.Find(link.Host)?.Access switch
    {
        null => DirectRank,
        HostAccess.Resolvable => ResolvableRank,
        HostAccess.Browser => BrowserRank,
        _ => ExternalOnlyRank,
    };

    /// <summary>
    /// Orders one multipart set, collapses mirrors (same part number on several hosts — first wins)
    /// and returns null when volumes are missing (1, 2, 4 …).
    /// </summary>
    internal static IReadOnlyList<DownloadLink>? CompleteSet(IEnumerable<DownloadLink> parts)
    {
        var ordered = parts
            .GroupBy(p => p.PartNumber)
            .Select(g => g.First())
            .OrderBy(p => p.PartNumber)
            .ToList();
        if (ordered.Count == 0) return null;

        var numbered = ordered.Where(p => p.PartNumber != LinkClassifier.SplitZipTerminalPart).Select(p => p.PartNumber).ToList();
        for (var i = 0; i < numbered.Count; i++)
        {
            if (numbered[i] != i + 1) return null;
        }
        return ordered;
    }
}
