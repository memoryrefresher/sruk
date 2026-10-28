using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Emulation;
using SeaRaider.Core.Models;
using SeaRaider.Core.Scraping;

namespace SeaRaider.Core.Catalog;

/// <summary>
/// A game's own download link as a source (Sources › Add a source): a Google Drive, MEGA, MediaFire, Dropbox … link, or a direct
/// file on any site. It becomes one store game, named as the user types it (else from the file's name), with that link as its
/// download. The store looks its picture and details up the way it does for every game (Steam / IGDB for a PC game, the box art
/// and game database for a console game); when nothing is found it is still in the store, under the console (or PC games) the
/// user chose, with just its name.
/// </summary>
public sealed partial class LinkSourceService(ILibraryRepository repository, IGameDetailsService details, ILogger<LinkSourceService> logger)
{
    /// <summary>The "PC game" choice of <see cref="GameSource.LinkSystemId"/>.</summary>
    public const string PcId = "pc";

    /// <summary>File types that say "this address is a download", not a web page.</summary>
    private static readonly HashSet<string> FileExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".exe", ".msi", ".iso" };

    private static readonly HashSet<string> RomExtensions = new(GameSystems.DirectDownloadExtensions, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Sharing services that SeaRaider has no special handling for but whose addresses are always files (their share links have no
    /// file type in them). The download browser opens them like any other page.
    /// </summary>
    private static readonly string[] ShareHosts =
    [
        "dropbox.com", "1drv.ms", "onedrive.live.com", "sharepoint.com", "box.com", "drive.usercontent.google.com",
        "wetransfer.com", "we.tl", "catbox.moe", "transfer.sh", "files.fm", "terabox.com", "4shared.com", "sendspace.com",
    ];

    [GeneratedRegex(@"[. _-]part\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PartSuffixRegex();

    [GeneratedRegex(@"^\.[rz]?\d{2,3}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VolumeExtensionRegex();

    // =============================================================================================
    // Telling a game's link from a site
    // =============================================================================================

    /// <summary>A web address from text a person typed ("drive.google.com/file/d/…" works without https://).</summary>
    public static bool TryParse(string? text, out Uri uri)
    {
        uri = null!;
        var t = (text ?? string.Empty).Trim().Trim('"');
        if (t.Length == 0 || t.Any(char.IsWhiteSpace) || t.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) return false;
        if (!t.Contains("://", StringComparison.Ordinal)) t = "https://" + t;
        if (!Uri.TryCreate(t, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https") || !parsed.Host.Contains('.'))
            return false;
        uri = parsed;
        return true;
    }

    /// <summary>
    /// Is this address one game's download (a file host's link, a share link, or a file such as game.zip) rather than a site that
    /// lists games? Anything else can still be added as a link with the "download link for one game" box ticked.
    /// </summary>
    public static bool IsGameLink(string? text)
    {
        if (!TryParse(text, out var uri)) return false;
        if (FileHosts.MatchFileLink(uri) is { Key: not "torrent-host" }) return true;
        if (IsShareHost(uri.Host)) return true;
        var ext = Path.GetExtension(uri.AbsolutePath);
        return ext.Length > 1 && (FileExtensions.Contains(ext) || RomExtensions.Contains(ext));
    }

    private static bool IsShareHost(string host)
    {
        var h = host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
        return ShareHosts.Any(d => h.Equals(d, StringComparison.OrdinalIgnoreCase) || h.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
    }

    // =============================================================================================
    // Name and console
    // =============================================================================================

    /// <summary>The game's name from its link: the file's name cleaned up ("Hollow_Knight_v1.5.zip" → "Hollow Knight"), else "Game from Google Drive".</summary>
    public static string NameFrom(Uri uri)
    {
        var stem = StemOf(FileHosts.GuessFileName(uri));
        if (stem.Length > 0)
        {
            // Dots are word separators in "Hollow.Knight.v1.5-GOG", never in a name that has spaces.
            var words = stem.Contains(' ') ? stem.Replace('_', ' ') : stem.Replace('_', ' ').Replace('.', ' ');
            var clean = TorrentSourceService.CleanName(words);
            if (clean.Length >= 2) return clean;
        }
        return $"Game from {HostName(uri)}";
    }

    /// <summary>"Hollow Knight.part01.rar" → "Hollow Knight": the file's name without its type, volume number or second type.</summary>
    private static string StemOf(string file)
    {
        var stem = file;
        for (var i = 0; i < 2 && stem.Length > 0; i++)
        {
            var ext = Path.GetExtension(stem);
            if (ext.Length < 2 || ext.Length > 6) break;
            if (FileExtensions.Contains(ext) || RomExtensions.Contains(ext) || VolumeExtensionRegex().IsMatch(ext)) stem = stem[..^ext.Length];
            else break;
        }
        return PartSuffixRegex().Replace(stem, string.Empty).Trim();
    }

    private static string HostName(Uri uri) =>
        FileHosts.Find(uri.Host)?.DisplayName ?? (uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host);

    /// <summary>
    /// Where the game is filed: the PC choice or the console chosen; else the console its name or file type says; else null (PC games).
    /// </summary>
    public static GameSystem? SystemFor(GameSource source)
    {
        if (string.Equals(source.LinkSystemId, PcId, StringComparison.OrdinalIgnoreCase)) return null;
        if (GameSystems.Find(source.LinkSystemId) is { } chosen) return chosen;
        if (TorrentSourceService.ConsoleIn(source.DisplayName) is { } named) return named;
        return Uri.TryCreate(source.Url, UriKind.Absolute, out var uri)
            ? GameSystems.FromExtension(Path.GetExtension(FileHosts.GuessFileName(uri)))
            : null;
    }

    private static string Tidy(string name)
    {
        var t = string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return t.Length > 80 ? t[..80].TrimEnd() : t;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // =============================================================================================
    // Adding and changing
    // =============================================================================================

    /// <summary>
    /// Adds the link as a source and puts its game in the store (at once, with its name; see <see cref="EnrichAsync"/> for the picture
    /// and details).
    /// </summary>
    /// <param name="name">The name to show; blank works it out from the link.</param>
    /// <param name="systemId">null works the console out (else PC games), "pc" is a PC game, or a console's id.</param>
    /// <exception cref="ArgumentException">Not a web address.</exception>
    /// <exception cref="InvalidOperationException">Already a source.</exception>
    public (GameSource Source, GameMetadata Game) Add(string link, string? name, string? systemId)
    {
        if (!TryParse(link, out var uri)) throw new ArgumentException("That isn't a web address.");
        var key = SourcesFile.Key(uri.AbsoluteUri);
        if (repository.GetSources().FirstOrDefault(s => SourcesFile.Key(s.Url) == key) is { } existing)
            throw new InvalidOperationException($"That link is already in your sources (\"{existing.DisplayName}\").");

        var chosen = !string.IsNullOrWhiteSpace(name);
        var source = new GameSource
        {
            Url = uri.AbsoluteUri,
            DisplayName = chosen ? Tidy(name!) : NameFrom(uri),
            IsLink = true,
            LinkNameChosen = chosen,
            LinkSystemId = Blank(systemId),
            Mode = CrawlMode.SinglePage,
            SearchMode = SearchMode.Disabled, // nothing to search: its one game is in the store already
            RespectRobotsTxt = false,
        };
        repository.UpsertSource(source);
        var game = Publish(source);
        logger.LogInformation("Added {Title} from a link ({Host}), filed under {System}", source.DisplayName, uri.Host, SystemFor(source)?.Name ?? "PC games");
        return (source, game);
    }

    /// <summary>Renames the game and/or files it under another console (or PC games); it is looked up again under its new name.</summary>
    /// <param name="name">Blank: the name is worked out from the link again.</param>
    public GameMetadata Update(GameSource source, string? name, string? systemId)
    {
        var chosen = !string.IsNullOrWhiteSpace(name);
        source.LinkNameChosen = chosen;
        source.DisplayName = chosen ? Tidy(name!) : NameFrom(new Uri(source.Url));
        source.LinkSystemId = Blank(systemId);
        repository.UpsertSource(source);
        return Publish(source);
    }

    /// <summary>The store address of a link source's one game (a made-up one: it is never fetched).</summary>
    public static string PageUrlOf(GameSource source) => $"https://link.invalid/{GameMetadata.CreateId(source.Url)}/";

    /// <summary>
    /// Writes the source's one game to the store: its name, its console (or PC), the link as its download, and, for a console
    /// game named like a ROM, its box art. Anything looked up before is dropped (a new name or console is looked up again).
    /// </summary>
    public GameMetadata Publish(GameSource source)
    {
        var uri = new Uri(source.Url);
        var system = SystemFor(source);
        var now = DateTime.UtcNow;
        var pageUrl = PageUrlOf(source);
        var id = GameMetadata.CreateId(pageUrl);
        var existing = repository.GetGame(id);

        var file = FileHosts.GuessFileName(uri);
        var raw = StemOf(file);
        var description = $"Added from a download link ({HostName(uri)}).";
        var game = new GameMetadata
        {
            Id = id,
            SourceId = source.Id,
            PageUrl = pageUrl,
            Title = source.DisplayName,
            RawTitle = raw.Length > 0 ? raw : source.DisplayName,
            ShortDescription = description,
            Description = description,
            Genres = system is null ? [] : [system.Name],
            // A console game named like a ROM ("Metroid Fusion (USA).zip") has its box art straight away; the rest waits for the lookup.
            CoverImageUrl = system is not null && raw.Length > 0 ? RomInfoService.BoxArtFor(system, raw) : null,
            DetailsLoaded = true,
            MetadataVersion = GameMetadata.CurrentMetadataVersion,
            UploadedAtUtc = existing?.UploadedAtUtc ?? now,
            FetchedAtUtc = now,
        };

        // The label is the file's name, never the game's name: "Metal Gear Pt 2" would read as part 2 of a split archive.
        var link = LinkClassifier.Classify(uri, file.Length > 0 ? file : null, forceDownload: true) ??
                   new DownloadLink { Url = uri.AbsoluteUri, Kind = DownloadLinkKind.DirectHttp, Host = uri.Host, Mirror = HostName(uri), FileName = file };
        if (string.IsNullOrWhiteSpace(link.Label)) link.Label = HostName(uri);
        game.DownloadLinks = [link];

        repository.UpsertGames([game]);
        source.LastScrapedAtUtc = now;
        source.LastGameCount = 1;
        source.LastResult = "OK: one game from a download link";
        repository.UpsertSource(source);
        return game;
    }

    /// <summary>
    /// Looks the game's picture, screenshots, trailer and details up (what opening it in the store does), so they are there when
    /// it's looked at. A name typed by the user stays whatever the lookup finds. Never throws for a failed lookup: the game is in
    /// the store, with its name, either way.
    /// </summary>
    public async Task EnrichAsync(GameSource source, CancellationToken ct = default)
    {
        try
        {
            var game = await details.GetAsync(GameMetadata.CreateId(PageUrlOf(source)), false, ct).ConfigureAwait(false);
            if (source.LinkNameChosen && !string.Equals(game.Title, source.DisplayName, StringComparison.Ordinal))
            {
                game.Title = source.DisplayName;
                repository.UpsertGames([game]);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation(ex, "No picture or details found for {Title}", source.DisplayName);
        }
    }
}
