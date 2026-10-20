using SeaRaider.Core.Scraping;

namespace SeaRaider.Core.Extraction;

/// <summary>How one group of downloaded files must be opened.</summary>
public enum ArchiveSetKind
{
    /// <summary>A normal single-file archive (.zip, .rar, .7z, .tar.gz …).</summary>
    Single,

    /// <summary>True multi-volume RAR/ZIP — opened together by SharpCompress.</summary>
    MultiVolume,

    /// <summary>One archive cut into raw byte chunks (.001, .002 …) — read through a concatenated stream.</summary>
    ByteSplit,

    /// <summary>Not an archive (installer, ISO, already-extracted file): copied into place.</summary>
    Passthrough,
}

/// <param name="Kind">How to open the set.</param>
/// <param name="Files">Absolute paths in read order.</param>
public sealed record ArchiveSet(ArchiveSetKind Kind, IReadOnlyList<string> Files)
{
    public long TotalBytes => Files.Sum(f => new FileInfo(f).Length);
}

/// <summary>
/// Groups a flat list of downloaded files into archive sets and orders volumes correctly:
/// <c>game.part1.rar…partN.rar</c>, <c>game.rar + .r00…</c>, <c>game.z01… + game.zip</c>,
/// <c>game.7z.001…</c>, plus single archives and pass-through files.
/// </summary>
public static class ArchiveSetResolver
{
    private static readonly string[] SingleArchiveExtensions =
        [".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".tbz2", ".xz", ".txz"];

    /// <summary>Checksum / shortcut clutter that isn't worth copying into a game folder.</summary>
    private static readonly string[] JunkExtensions = [".sfv", ".md5", ".sha1", ".sha256", ".url", ".part"];

    public static IReadOnlyList<ArchiveSet> Resolve(IEnumerable<string> files)
    {
        var all = files.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var sets = new List<ArchiveSet>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Explicit volume names (.partN.rar, .rNN, .zNN, .NNN), grouped per directory + base name.
        var volumes = all
            .Select(path => LinkClassifier.TryParseVolume(Path.GetFileName(path), out var key, out var part, out var scheme)
                ? new { Path = path, Key = Path.Combine(Path.GetDirectoryName(path)!, key).ToLowerInvariant(), Part = part, Scheme = scheme }
                : null)
            .Where(v => v is not null)
            .Select(v => v!)
            .ToList();

        foreach (var group in volumes.GroupBy(v => (v.Key, v.Scheme)))
        {
            var ordered = group.OrderBy(v => v.Part).Select(v => v.Path).ToList();
            var (key, scheme) = group.Key;

            switch (scheme)
            {
                case VolumeScheme.ByteSplit:
                    sets.Add(new ArchiveSet(ArchiveSetKind.ByteSplit, ordered));
                    break;

                case VolumeScheme.RarOldStyle:
                    // game.rar is volume 1 of an old-style set.
                    if (FindSibling(all, key, ".rar") is { } firstRar) ordered.Insert(0, firstRar);
                    sets.Add(new ArchiveSet(ArchiveSetKind.MultiVolume, ordered));
                    break;

                case VolumeScheme.SplitZip:
                    // game.zip is the LAST volume of a split zip.
                    if (FindSibling(all, key, ".zip") is { } lastZip) ordered.Add(lastZip);
                    sets.Add(new ArchiveSet(ArchiveSetKind.MultiVolume, ordered));
                    break;

                default:
                    sets.Add(new ArchiveSet(ordered.Count > 1 ? ArchiveSetKind.MultiVolume : ArchiveSetKind.Single, ordered));
                    break;
            }

            foreach (var f in sets[^1].Files) claimed.Add(f);
        }

        // 2. Remaining files: single archives or pass-through.
        foreach (var path in all.Where(p => !claimed.Contains(p)))
        {
            if (JunkExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))) continue;

            var kind = SingleArchiveExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                ? ArchiveSetKind.Single
                : ArchiveSetKind.Passthrough;
            sets.Add(new ArchiveSet(kind, [path]));
        }

        return sets;
    }

    public static bool IsArchiveFile(string path) =>
        SingleArchiveExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) ||
        LinkClassifier.TryParseVolume(Path.GetFileName(path), out _, out _);

    private static string? FindSibling(IEnumerable<string> files, string lowerKeyPath, string extension) =>
        files.FirstOrDefault(f => string.Equals(
            Path.Combine(Path.GetDirectoryName(f)!, Path.GetFileNameWithoutExtension(f)).ToLowerInvariant(),
            lowerKeyPath, StringComparison.Ordinal) && f.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
}
