using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Downloads;
using SeaRaider.Core.Models;
using SeaRaider.Core.Scraping;

namespace SeaRaider.Core.Catalog;

/// <summary>One game in a backups torrent: a top folder (or a single file) and its size.</summary>
public sealed record TorrentEntry(string Name, string PathInTorrent, long Size, int Files);

/// <summary>What a .torrent file holds: its name, info hash and file list.</summary>
public sealed record TorrentContents(string Name, string InfoHash, bool SingleFile, IReadOnlyList<(string Path, long Length)> Files)
{
    /// <summary>
    /// One entry per game: each folder at the top of the torrent (all files under it), and each file lying at the top by itself
    /// ("Game.zip"). A torrent that is one file is one entry.
    /// </summary>
    public IReadOnlyList<TorrentEntry> Entries()
    {
        if (SingleFile) return [new TorrentEntry(Name, Name, Files.Sum(f => f.Length), 1)];

        // Every game in the torrent, however deep: each archive (.zip, .7z, .rar) or game file (.nes, .iso, .chd …) is one;
        // a disc game's loose .cue / .bin / .gdi files are one game per folder. Text files, pictures and checksums aren't games.
        var entries = new List<TorrentEntry>();
        var discFolders = new Dictionary<string, (long Size, int Files)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, length) in Files)
        {
            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            var fileName = path.Split('/')[^1];
            if (GameFile.Contains(ext) || Emulation.GameSystems.DirectDownloadExtensions.Contains(ext))
                entries.Add(new TorrentEntry(fileName, $"{Name}/{path}", length, 1));
            else if (DiscPart.Contains(ext))
            {
                var folder = path.Contains('/') ? path[..path.LastIndexOf('/')] : string.Empty;
                discFolders[folder] = discFolders.TryGetValue(folder, out var d) ? (d.Size + length, d.Files + 1) : (length, 1);
            }
        }
        foreach (var (folder, (size, count)) in discFolders)
            entries.Add(new TorrentEntry(folder.Length == 0 ? Name : folder.Split('/')[^1], folder.Length == 0 ? Name : $"{Name}/{folder}", size, count));
        return entries.OrderBy(e => e.PathInTorrent, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static readonly HashSet<string> GameFile = new(StringComparer.OrdinalIgnoreCase)
        { ".zip", ".7z", ".rar", ".iso", ".chd", ".cso", ".rvz", ".wbfs", ".gcz", ".pbp", ".nsp", ".xci", ".nsz", ".3ds", ".cia", ".wua", ".wux" };

    private static readonly HashSet<string> DiscPart = new(StringComparer.OrdinalIgnoreCase) { ".cue", ".bin", ".gdi", ".img", ".ccd", ".mdf", ".m3u" };

    /// <summary>Reads a .torrent file (or a magnet's metadata: the info dictionary alone).</summary>
    /// <exception cref="InvalidDataException">Not a torrent.</exception>
    public static TorrentContents Read(byte[] bytes)
    {
        var position = 0;
        var root = Bencode.Read(bytes, ref position);
        if (root is not Dictionary<string, object> top) throw new InvalidDataException("That isn't a torrent file.");
        var info = top.TryGetValue("info", out var i) && i is Dictionary<string, object> d ? d : top;
        var (start, end) = top.TryGetValue("info", out _) ? Bencode.InfoSpan(bytes) : (0, bytes.Length);
        var hash = Convert.ToHexString(SHA1.HashData(bytes.AsSpan(start, end - start))).ToLowerInvariant();

        var name = Text(info.GetValueOrDefault("name.utf-8") ?? info.GetValueOrDefault("name")) ?? "Torrent";
        var files = new List<(string, long)>();
        if (info.TryGetValue("files", out var list) && list is List<object> entries)
        {
            foreach (var entry in entries.OfType<Dictionary<string, object>>())
            {
                var parts = (entry.GetValueOrDefault("path.utf-8") ?? entry.GetValueOrDefault("path")) as List<object>;
                if (parts is null || parts.Count == 0) continue;
                var path = string.Join('/', parts.Select(Text).Where(p => !string.IsNullOrEmpty(p)));
                if (path.Contains(".____padding_file", StringComparison.Ordinal) || entry.ContainsKey("attr") && Text(entry["attr"])?.Contains('p') == true)
                    continue; // padding files some clients add between files
                files.Add((path, entry.GetValueOrDefault("length") is long length ? length : 0));
            }
            return new TorrentContents(name, hash, false, files);
        }
        files.Add((name, info.GetValueOrDefault("length") is long single ? single : 0));
        return new TorrentContents(name, hash, true, files);
    }

    private static string? Text(object? value) => value is byte[] raw ? Encoding.UTF8.GetString(raw) : value as string;
}

/// <summary>A tiny bencode reader (what .torrent files are written in).</summary>
internal static class Bencode
{
    public static object Read(byte[] data, ref int i)
    {
        if (i >= data.Length) throw new InvalidDataException("That isn't a torrent file.");
        switch (data[i])
        {
            case (byte)'i':
            {
                var end = Array.IndexOf(data, (byte)'e', i);
                if (end < 0) throw new InvalidDataException("That isn't a torrent file.");
                var number = long.Parse(Encoding.ASCII.GetString(data, i + 1, end - i - 1), System.Globalization.CultureInfo.InvariantCulture);
                i = end + 1;
                return number;
            }
            case (byte)'l':
            {
                i++;
                var list = new List<object>();
                while (i < data.Length && data[i] != (byte)'e') list.Add(Read(data, ref i));
                i++;
                return list;
            }
            case (byte)'d':
            {
                i++;
                var dict = new Dictionary<string, object>(StringComparer.Ordinal);
                while (i < data.Length && data[i] != (byte)'e')
                {
                    var key = Encoding.UTF8.GetString((byte[])Read(data, ref i));
                    dict[key] = Read(data, ref i);
                }
                i++;
                return dict;
            }
            default:
            {
                var colon = Array.IndexOf(data, (byte)':', i);
                if (colon < 0 || colon - i > 12) throw new InvalidDataException("That isn't a torrent file.");
                var length = int.Parse(Encoding.ASCII.GetString(data, i, colon - i), System.Globalization.CultureInfo.InvariantCulture);
                if (length < 0 || colon + 1 + length > data.Length) throw new InvalidDataException("That isn't a torrent file.");
                var bytes = data.AsSpan(colon + 1, length).ToArray();
                i = colon + 1 + length;
                return bytes;
            }
        }
    }

    /// <summary>Where the "info" dictionary's bytes are (its SHA-1 is the torrent's info hash).</summary>
    public static (int Start, int End) InfoSpan(byte[] data)
    {
        var i = 1; // inside the top dictionary
        while (i < data.Length && data[i] != (byte)'e')
        {
            var key = Encoding.UTF8.GetString((byte[])Read(data, ref i));
            var start = i;
            Read(data, ref i);
            if (key == "info") return (start, i);
        }
        throw new InvalidDataException("The torrent has no file list.");
    }
}

/// <summary>
/// Backups torrents as store sources (Sources › Add a source: a .torrent file, its address, or a magnet link). Every game folder in
/// the torrent becomes a store entry named after the folder (cleaned, and matched on Steam for its picture, trailer and details when
/// the entry is opened); its download is the same torrent with only that folder chosen.
/// </summary>
public sealed class TorrentSourceService(ILibraryRepository repository, MonoTorrentDownloader torrents, ILogger<TorrentSourceService> logger)
{
    /// <summary>Is this text a torrent rather than a website (a magnet, a .torrent address or file)?</summary>
    public static bool IsTorrentAddress(string? text)
    {
        var t = (text ?? string.Empty).Trim().Trim('"');
        if (t.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)) return true;
        var path = Uri.TryCreate(t, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.AbsolutePath : t;
        return path.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase);
    }

    private static string Folder => Path.Combine(AppPaths.DataDirectory, "torrent-sources");

    /// <summary>
    /// The address another PC can add this source by (sources.txt, Export): the magnet or web address it came from, or a magnet
    /// made from the torrent's info hash when it was added from a file on this PC. (Another PC then reads the game list from
    /// whoever is sharing the torrent.)
    /// </summary>
    public static string ShareAddress(GameSource source)
    {
        if (!source.IsTorrent) return source.Url;
        var origin = source.TorrentOrigin?.Trim() ?? string.Empty;
        if (origin.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) ||
            Uri.TryCreate(origin, UriKind.Absolute, out var web) && web.Scheme is "http" or "https") return origin;
        var hash = source.Url.TrimEnd('/').Split('/')[^1];
        return $"magnet:?xt=urn:btih:{hash}&dn={Uri.EscapeDataString(source.DisplayName)}";
    }

    /// <summary>Adds the torrent as a source and puts its games in the store. Returns the source and how many games.</summary>
    public async Task<(GameSource Source, int Games)> AddAsync(string origin, CancellationToken ct = default)
    {
        origin = origin.Trim().Trim('"');
        var bytes = await torrents.GetTorrentFileAsync(origin, ct).ConfigureAwait(false);
        var contents = TorrentContents.Read(bytes);

        if (repository.GetSources().FirstOrDefault(s => s.IsTorrent && s.Url.Contains(contents.InfoHash, StringComparison.OrdinalIgnoreCase)) is { } existing)
            throw new InvalidOperationException($"\"{existing.DisplayName}\" is already in your sources.");

        // Keep our own copy: magnets and addresses then work without the peers or site, and file:// is what the downloader reads.
        Directory.CreateDirectory(Folder);
        var copy = Path.Combine(Folder, contents.InfoHash + ".torrent");
        var whole = bytes.Length > 0 && bytes[0] == (byte)'d' && !LooksLikeInfoOnly(bytes) ? bytes : WrapInfo(bytes);
        await File.WriteAllBytesAsync(copy, whole, ct).ConfigureAwait(false);

        var source = new GameSource
        {
            Url = $"https://torrent.local/{contents.InfoHash}/",
            DisplayName = Tidy(contents.Name),
            TorrentOrigin = origin,
            TorrentFile = copy,
            SearchMode = SearchMode.Custom,
            RespectRobotsTxt = false,
        };
        repository.UpsertSource(source);
        var count = Publish(source, contents);
        logger.LogInformation("Backups torrent {Name}: {Count} games", contents.Name, count);
        return (source, count);
    }

    /// <summary>Reads the source's torrent again (its copy) and puts its games in the store.</summary>
    public int Refresh(GameSource source)
    {
        if (source.TorrentFile is not { } file || !File.Exists(file)) return 0;
        return Publish(source, TorrentContents.Read(File.ReadAllBytes(file)));
    }

    private int Publish(GameSource source, TorrentContents contents)
    {
        var fileUrl = new Uri(source.TorrentFile!).AbsoluteUri;
        var now = DateTime.UtcNow;
        // The console: chosen for the source (Sources › its Console list), else named by the torrent or its folders.
        var chosen = Emulation.GameSystems.Find(source.TorrentSystemId);
        var whole = chosen ?? ConsoleIn(contents.Name) ?? ConsoleIn(source.DisplayName);

        // One store game per console and clean name: "Tetris (USA)", "Tetris (Japan)" and the same file in "Headered" and
        // "Headerless" are one game, its copies offered as download options (the best first).
        var groups = contents.Entries()
            .Select(entry =>
            {
                var folders = entry.PathInTorrent.Contains('/') ? entry.PathInTorrent[..entry.PathInTorrent.LastIndexOf('/')] : string.Empty;
                var system = chosen ?? ConsoleIn(folders.Replace('/', ' ')) ?? whole ??
                             Emulation.GameSystems.FromExtension(System.IO.Path.GetExtension(entry.Name));
                if (system?.Id is "nds" or "dsi") system = DsOrDsi(entry, folders);
                var raw = entry.Files == 1 ? StripArchiveExtension(entry.Name) : entry.Name;
                return (Entry: entry, System: system, Raw: raw, Title: CleanName(raw), Folder: folders.Split('/')[^1]);
            })
            .Where(x => x.Title.Length > 0 && !IsNotAGame(x.Raw))
            .GroupBy(x => $"{x.System?.Id}|{TitleNormalizer.DedupKey(x.Title)}", StringComparer.OrdinalIgnoreCase)
            .ToList();

        var games = new List<GameMetadata>();
        foreach (var group in groups)
        {
            var copies = group.OrderBy(x => Rank(x.Raw, x.Folder)).ThenBy(x => x.Raw.Length).ToList();
            var best = copies[0];
            var key = $"{best.System?.Id ?? "pc"}/{best.Title}";
            var pageUrl = $"{source.Url}{Uri.EscapeDataString(key)}";
            var existing = repository.GetGame(GameMetadata.CreateId(pageUrl));
            var game = existing ?? new GameMetadata();
            game.Id = GameMetadata.CreateId(pageUrl);
            game.SourceId = source.Id;
            game.PageUrl = pageUrl;
            game.RawTitle = best.Raw;
            if (existing is null || existing.RomInfoVersion == 0 && !existing.MediaChecked) game.Title = best.Title; // a looked-up name stays
            game.SizeText = Formatting.Bytes(best.Entry.Size);
            game.NonEnglish = !copies.Any(c => IsEnglish(c.Raw)); // Settings › Store › Hide games that aren't in English
            if (best.System is { } system)
            {
                game.Genres = [system.Name, .. game.Genres.Where(g => !g.Equals(system.Name, StringComparison.OrdinalIgnoreCase))];
                // The box art straight away (libretro's collection is named exactly like these ROM sets): the store isn't a wall
                // of letters while the full information is looked up.
                if (string.IsNullOrEmpty(game.CoverImageUrl)) game.CoverImageUrl = RomInfoService.BoxArtFor(system, best.Raw);
            }
            if (!game.Tags.Contains(contents.Name, StringComparer.OrdinalIgnoreCase)) game.Tags = [.. game.Tags, contents.Name];
            game.DetailsLoaded = true;
            game.MetadataVersion = GameMetadata.CurrentMetadataVersion;
            game.UploadedAtUtc ??= now;
            game.FetchedAtUtc = now;
            if (string.IsNullOrWhiteSpace(game.Description)) game.Description = $"From {contents.Name}.";
            game.DownloadLinks = copies.Select(copy => new DownloadLink
            {
                // One address per copy (the part after # is ignored when the file is read), so hiding one never hides another.
                Url = fileUrl + "#" + Uri.EscapeDataString(copy.Entry.PathInTorrent),
                Kind = DownloadLinkKind.TorrentFile,
                Host = "torrent",
                Mirror = Describe(copy.Raw, copy.Folder, copies.Count),
                Label = $"From {source.DisplayName}",
                FileName = copy.Entry.Name,
                SizeText = Formatting.Bytes(copy.Entry.Size),
                TorrentFolder = contents.SingleFile ? null : copy.Entry.PathInTorrent,
            }).ToList();
            games.Add(game);
        }
        // Entries an earlier reading made that aren't games now (a "Headered" folder listed as one game) go; the rest keep what
        // was looked up for them (read above, before this).
        repository.DeleteGamesForSource(source.Id);
        repository.UpsertGames(games);
        source.LastScrapedAtUtc = now;
        source.LastGameCount = games.Count;
        source.LastResult = $"{games.Count} games in the torrent";
        repository.UpsertSource(source);
        return games.Count;
    }

    /// <summary>
    /// DS or DSi, game by game: a DSi game is a .dsi file, DSiWare, or sits in a folder named only for the DSi; everything else
    /// in a "DS &amp; DSi" torrent is a DS game.
    /// </summary>
    private static Emulation.GameSystem? DsOrDsi(TorrentEntry entry, string folders)
    {
        bool DsiOnly(string text)
        {
            var words = System.Text.RegularExpressions.Regex.Split(text.ToLowerInvariant(), @"[^a-z0-9]+").ToHashSet();
            return (words.Contains("dsi") || words.Contains("dsiware") || words.Contains("ndsi")) && !words.Contains("ds") && !words.Contains("nds");
        }
        var isDsi = entry.Name.EndsWith(".dsi", StringComparison.OrdinalIgnoreCase) ||
                    entry.Name.Contains("DSiWare", StringComparison.OrdinalIgnoreCase) ||
                    folders.Split('/').Any(DsiOnly);
        return Emulation.GameSystems.Find(isDsi ? "dsi" : "nds");
    }

    /// <summary>
    /// ROM set entries that aren't games: BIOS files, test and utility programs, kiosk and store demos, samples, videos
    /// ("Game Boy Advance Video"), apps, and bad dumps.
    /// </summary>
    internal static bool IsNotAGame(string raw) =>
        System.Text.RegularExpressions.Regex.IsMatch(raw,
            @"\[bios\]|\(bios\)|^\[?bios\b|\((?:[^)]*\b)?(?:program|test program|kiosk|demo|sample|application|app|utility|diagnostic|debug|" +
            @"enhancement chip|competition cart|e-reader)\b[^)]*\)|advance video|(?<![a-z])gba video(?![a-z])|\[b\d*\]|\(channel\)|\bfirmware\b|" +
            @"\bsystem menu\b|\bupdate data\b|\bnintendo channel\b|\bmovie\b\s*\(|\(manual\)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>"Legend of Zelda, The (USA) (Rev 1)" → "The Legend of Zelda".</summary>
    /// <summary>
    /// The console a torrent's or folder's name stands for: the usual names ("Nintendo DS", "SNES"), and the short ones folders
    /// use ("DS", "DSi", "GBA", "PSX", "GC", "MD", "PCE" …).
    /// </summary>
    public static Emulation.GameSystem? ConsoleIn(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (Emulation.GameSystems.FromText(text) is { } named) return named;
        var words = System.Text.RegularExpressions.Regex.Split(text.ToLowerInvariant(), @"[^a-z0-9]+").Where(w => w.Length > 0).ToHashSet();
        foreach (var (word, id) in ShortNames)
            if (words.Contains(word)) return Emulation.GameSystems.Find(id);
        return null;
    }

    private static readonly (string Word, string Id)[] ShortNames =
    [
        ("dsiware", "dsi"), ("dsi", "dsi"), ("ndsi", "dsi"), ("ds", "nds"), ("gba", "gba"), ("gbc", "gbc"), ("gb", "gb"), ("psx", "ps1"), ("ps1", "ps1"),
        ("ps2", "ps2"), ("ps3", "ps3"), ("psp", "psp"), ("gc", "gc"), ("ngc", "gc"), ("gcn", "gc"), ("wii", "wii"), ("wiiu", "wiiu"),
        ("3ds", "3ds"), ("n3ds", "3ds"), ("switch", "switch"), ("nsw", "switch"), ("n64", "n64"), ("snes", "snes"), ("sfc", "snes"),
        ("nes", "nes"), ("fc", "nes"), ("md", "genesis"), ("genesis", "genesis"), ("megadrive", "genesis"), ("sms", "sms"),
        ("gg", "gamegear"), ("pce", "pce"), ("tg16", "pce"), ("dc", "dreamcast"), ("saturn", "saturn"), ("32x", "32x"),
        ("scd", "segacd"), ("ngp", "ngp"), ("ngpc", "ngp"), ("ws", "wonderswan"), ("wsc", "wonderswan"), ("vb", "vb"),
        ("a2600", "atari2600"), ("a7800", "atari7800"), ("lynx", "lynx"), ("jaguar", "jaguar"),
    ];

    /// <summary>
    /// Is this copy in English? Its region or language tags say so: "(USA)", "(World)", "(Europe)", "(En,Fr,De)". A copy with no
    /// region at all counts as English; "(Japan)", "(Germany)", "(Fr,De)" alone don't.
    /// </summary>
    internal static bool IsEnglish(string raw)
    {
        var tags = System.Text.RegularExpressions.Regex.Matches(raw, @"\(([^)]*)\)").Select(m => m.Groups[1].Value).ToList();
        if (tags.Count == 0) return !HasNonLatinScript(raw);
        var english = new[] { "usa", "world", "europe", "uk", "united kingdom", "australia", "canada", "new zealand", "ireland", "en", "eng", "english", "u", "e", "ue", "ju", "jue" };
        var foreign = new[] { "japan", "j", "germany", "france", "spain", "italy", "korea", "china", "taiwan", "hong kong", "brazil", "russia",
                              "netherlands", "sweden", "denmark", "norway", "finland", "portugal", "poland", "greece", "asia", "latin america",
                              "mexico", "fr", "de", "es", "it", "ja", "ko", "zh", "pt", "nl", "sv", "da", "no", "fi", "pl", "ru" };
        var sawRegion = false;
        foreach (var tag in tags)
        {
            var parts = tag.Split([',', '+'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(p => p.ToLowerInvariant()).ToList();
            if (parts.Any(p => english.Contains(p))) return true;
            if (parts.Any(p => foreign.Contains(p))) sawRegion = true;
        }
        return !sawRegion && !HasNonLatinScript(raw);
    }

    /// <summary>Mostly Japanese, Chinese, Korean, Cyrillic, Arabic, Hebrew or Thai letters.</summary>
    public static bool HasNonLatinScript(string text)
    {
        int letters = 0, other = 0;
        foreach (var c in text)
        {
            if (!char.IsLetter(c)) continue;
            letters++;
            if (c > '\u024F') other++; // beyond Latin (and its accented letters)
        }
        return letters > 0 && other * 3 > letters;
    }

    internal static string CleanName(string raw)
    {
        // "0123 - Game (USA)" (numbered sets): the number isn't part of the name.
        raw = System.Text.RegularExpressions.Regex.Replace(raw, @"^\s*[0-9]{3,5}\s*[-–_.]\s*", string.Empty);
        var text = System.Text.RegularExpressions.Regex.Replace(raw, @"\([^)]*\)|\[[^\]]*\]", " ").Replace('_', ' ');
        text = MetadataParser.CleanTitle(text).Title;
        var article = System.Text.RegularExpressions.Regex.Match(text, @"^(?<main>.+?),\s*(?<a>The|A|An)\b(?<rest>.*)$");
        if (article.Success) text = $"{article.Groups["a"].Value} {article.Groups["main"].Value}{article.Groups["rest"].Value}";
        return System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim(' ', '-', '.', ',');
    }

    /// <summary>Which copy comes first: USA, then World, Europe, others; never a beta, prototype, demo or hack before a release.</summary>
    private static int Rank(string raw, string folder)
    {
        var lower = raw.ToLowerInvariant();
        var rank = lower.Contains("(usa") ? 0 : lower.Contains("(world") ? 1 : lower.Contains("(europe") ? 2 : 3;
        if (System.Text.RegularExpressions.Regex.IsMatch(lower, @"\((beta|proto|prototype|demo|sample|hack|pirate|unl|aftermarket|program)")) rank += 10;
        if (lower.Contains("(rev")) rank += 0; // a revision is as good as the release
        if (folder.Contains("headerless", StringComparison.OrdinalIgnoreCase)) rank += 1; // emulators want the header
        return rank;
    }

    /// <summary>A copy's name in the download options: "USA · Headered", "Japan (Rev 1)".</summary>
    private static string Describe(string raw, string folder, int copies)
    {
        var tags = System.Text.RegularExpressions.Regex.Matches(raw, @"\(([^)]*)\)").Select(m => m.Groups[1].Value).Take(3).ToList();
        var label = string.Join(" ", tags.Select((t, i) => i == 0 ? t : $"({t})"));
        if (copies > 1 && folder.Length > 0) label = label.Length > 0 ? $"{label} · {folder}" : folder;
        return label.Length > 0 ? label : "Download";
    }

    private static string StripArchiveExtension(string name)
    {
        var ext = Path.GetExtension(name);
        return ext.Length is > 1 and <= 6 ? name[..^ext.Length] : name;
    }

    private static string Tidy(string name)
    {
        var t = name.Replace('_', ' ').Trim();
        return t.Length > 60 ? t[..60] : t;
    }

    /// <summary>A magnet's metadata is the info dictionary alone (no "info" key at the top).</summary>
    private static bool LooksLikeInfoOnly(byte[] bytes)
    {
        var i = 0;
        return Bencode.Read(bytes, ref i) is Dictionary<string, object> d && !d.ContainsKey("info") && d.ContainsKey("piece length");
    }

    /// <summary>Makes a full .torrent ("d4:info" + the info dictionary + "e") out of a magnet's metadata.</summary>
    private static byte[] WrapInfo(byte[] info) => [.. "d4:info"u8.ToArray(), .. info, (byte)'e'];
}
