using System.Text.RegularExpressions;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Scraping;

/// <summary>How SeaRaider can fetch a file from a host.</summary>
public enum HostAccess
{
    /// <summary>SeaRaider turns the page link into the file link itself (see <c>FileHostResolver</c>).</summary>
    Resolvable,

    /// <summary>The page needs a person (captcha, countdown, button): the verification window opens on it.</summary>
    Browser,

    /// <summary>Files are decrypted in the browser (MEGA): only "open in browser" works.</summary>
    ExternalOnly,
}

/// <summary>A known file-hosting service.</summary>
/// <param name="Key">Stable id ("buzzheavier").</param>
/// <param name="DisplayName">Name shown on mirror tabs ("Buzzheavier").</param>
/// <param name="Domains">Domains, without "www.".</param>
/// <param name="FileUrlPattern">Path (+query/fragment) of an actual file link, so the host's home or FAQ links don't count.</param>
/// <param name="Access">How a download works.</param>
public sealed record FileHostInfo(string Key, string DisplayName, string[] Domains, Regex FileUrlPattern, HostAccess Access);

/// <summary>
/// File hosts used by repack and pre-installed game sites (FitGirl's per-host part lists, SteamRIP's one-button-per-host
/// layout). Links to these hosts have no file extension, so <see cref="LinkClassifier"/> recognises them here and tags
/// them with a mirror name for the store page's mirror list.
/// </summary>
public static class FileHosts
{
    private static Regex P(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static IReadOnlyList<FileHostInfo> All { get; } =
    [
        new("buzzheavier", "Buzzheavier", ["buzzheavier.com", "bzzhr.co"], P(@"^/(?:f/)?[a-z0-9]{6,}/?$"), HostAccess.Resolvable),
        new("pixeldrain", "Pixeldrain", ["pixeldrain.com", "pixeldra.in"], P(@"^/(?:u|l)/[a-z0-9]+"), HostAccess.Resolvable),
        new("fuckingfast", "FuckingFast", ["fuckingfast.co"], P(@"^/[a-z0-9]{6,}"), HostAccess.Resolvable),
        new("mediafire", "MediaFire", ["mediafire.com"], P(@"^/(?:file|file_premium|download)/"), HostAccess.Resolvable),
        new("gofile", "Gofile", ["gofile.io"], P(@"^/d/"), HostAccess.Browser),
        new("datanodes", "DataNodes", ["datanodes.to"], P(@"^/[a-z0-9]{8,}"), HostAccess.Browser),
        new("ddownload", "DDownload", ["ddownload.com", "ddl.to"], P(@"^/[a-z0-9]{8,}"), HostAccess.Browser),
        new("1fichier", "1fichier", ["1fichier.com"], P(@"^/?\?[a-z0-9]{6,}"), HostAccess.Browser),
        new("megadb", "MegaDB", ["megadb.net"], P(@"^/[a-z0-9]{8,}"), HostAccess.Browser),
        new("multiup", "MultiUp", ["multiup.io", "multiup.org"], P(@"^/(?:download/|en/mirror/|[a-f0-9]{32})"), HostAccess.Browser),
        new("filecrypt", "FileCrypt", ["filecrypt.cc", "filecrypt.co"], P(@"^/container/"), HostAccess.Browser),
        new("qiwi", "Qiwi", ["qiwi.gg"], P(@"^/(?:file|folder)/"), HostAccess.Browser),
        new("rapidgator", "Rapidgator", ["rapidgator.net", "rg.to"], P(@"^/file/"), HostAccess.Browser),
        new("krakenfiles", "KrakenFiles", ["krakenfiles.com"], P(@"^/view/"), HostAccess.Browser),
        new("vikingfile", "VikingFile", ["vikingfile.com"], P(@"^/f/"), HostAccess.Browser),
        new("sendcm", "Send.cm", ["send.cm", "send.now"], P(@"^/(?:d/)?[a-z0-9]{8,}"), HostAccess.Browser),
        new("fileditch", "FileDitch", ["fileditchfiles.me", "fileditch.com"], P(@"^/(?:file|[a-z0-9-]+/)"), HostAccess.Browser),
        new("uploadhaven", "UploadHaven", ["uploadhaven.com"], P(@"^/download/"), HostAccess.Browser),
        new("filekeeper", "FileKeeper", ["filekeeper.net"], P(@"^/(?:files?/|d/|f/)?[a-z0-9]{6,}"), HostAccess.Browser),
        // Zovo: a link shortener that sites link as a bare "Click Here" button. Ids can be short, so any non-site path counts.
        // It opens in the download browser, which waits out its timer, presses Continue, then presses "Download File"
        // on the page it leads to (see LinkShortenerDomains).
        new("zovo", "Mirror", ["zovo.to"],
            P(@"^/(?!(?:faq|about|contact|terms|tos|privacy|premium|pricing|register|signup|login|upload|api|help|dmca|abuse|blog|account)(?:[/?#]|$))[a-z0-9_-]{3,}"),
            HostAccess.Browser),
        // MegaUp: a page per file ("Fingun.Forever.rar (298 MB) - DOWNLOAD / VIEW NOW"); the button leads to download.megaup.net,
        // which runs a Cloudflare check first. Works in the download browser, which waits the check out.
        new("megaup", "MegaUp", ["megaup.net"], P(@"^/[a-z0-9]{4,}"), HostAccess.Browser),
        // BlueMediaFiles: a URL generator ("/url-generator.php?url=…"). Every provider's parts on some sites (Gofile, Pixeldrain …)
        // sit behind one of these, so the address says nothing about the host: the provider name printed on the page does (see
        // PartSetParser). It shows a captcha and a "Get Link" step in front of the real host, so it works like a link shortener:
        // the download browser stops for the person at the captcha and carries on from the host it leads to.
        new("bluemediafiles", "BlueMediaFiles", ["bluemediafiles.com", "bluemediafiles.net", "bluemediafiles.org", "urlbluemedia.shop", "urlbluemedia.click", "urlbluemedia.net"],
            P(@"^/(?:url-generator|[a-z0-9_-]*(?:url|link)[a-z0-9_-]*)\.php"), HostAccess.Browser),
        // Tracker pages (FitGirl links its 1337x / RuTor pages next to the magnet): never a download in themselves.
        new("torrent-host", "Torrent page", ["1337x.to", "1337x.st", "rutor.info", "rutor.org", "tapochek.net", "nnmclub.to", "rutracker.org"],
            P(@"^/(?:torrent|t|forum/viewtopic\.php)"), HostAccess.ExternalOnly),
        // itch.io: its files sit behind a per-file click-through on the game's page, so the page opens in the browser.
        // Added by the itch.io reader itself (ItchIo), never picked up from other sites' links (see MatchFileLink).
        new("itchio", "itch.io", ["itch.io"], P(@"^/[a-z0-9._-]+/?$"), HostAccess.ExternalOnly),
        new("mega", "MEGA", ["mega.nz", "mega.co.nz", "mega.io"], P(@"^/(?:file|folder|#!|#F!)"), HostAccess.ExternalOnly),
        new("gdrive", "Google Drive", ["drive.google.com"], P(@"^/(?:file/d/|uc\b|open\b)"), HostAccess.Browser),
    ];

    /// <summary>
    /// Link shorteners: a wait screen (countdown, then a "Continue" / "Get Link" button) in front of the real download
    /// page. The download browser waits for the button to unlock, follows it even when it leads to another site, and then
    /// presses that page's "Download File" button.
    /// </summary>
    public static IReadOnlyList<string> LinkShortenerDomains { get; } = ["zovo.to", "bluemediafiles.com", "bluemediafiles.net", "bluemediafiles.org", "urlbluemedia.shop", "urlbluemedia.click", "urlbluemedia.net"];

    /// <summary>
    /// Link-protection pages (FileCrypt): a security check, then a list of links to the real host. Like a short link's
    /// pages, they're left to the user; the download continues on its own once the host opens.
    /// </summary>
    public static bool IsLinkProtector(string? host) => Find(host)?.Key is "filecrypt";

    /// <summary>
    /// A redirector that moves on by itself after a countdown (BlueMediaFiles): the download browser stays in the background and
    /// only comes forward for a captcha. Other shorteners (Zovo) still ask you to click through.
    /// </summary>
    public static bool IsAutoRedirector(string? host) => Find(host)?.Key is "bluemediafiles";

    /// <summary>A URL-generator page ("/url-generator.php?url=…"), the redirector's shape, on any domain.</summary>
    public static bool IsGeneratorUrl(Uri url) =>
        url.AbsolutePath.EndsWith("/url-generator.php", StringComparison.OrdinalIgnoreCase) &&
        url.Query.Contains("url=", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for a link shortener's domain or one of its sub-domains.</summary>
    public static bool IsLinkShortener(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var h = host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
        return LinkShortenerDomains.Any(d => h.Equals(d, StringComparison.OrdinalIgnoreCase) ||
                                             h.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly Dictionary<string, FileHostInfo> ByDomain = All
        .SelectMany(h => h.Domains.Select(d => (Domain: d, Host: h)))
        .ToDictionary(x => x.Domain, x => x.Host, StringComparer.OrdinalIgnoreCase);

    /// <summary>The host a domain belongs to ("www." and sub-domains like "download1.mediafire.com" included).</summary>
    public static FileHostInfo? Find(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        var domain = host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
        while (true)
        {
            if (ByDomain.TryGetValue(domain, out var info)) return info;
            var dot = domain.IndexOf('.');
            if (dot < 0 || domain.IndexOf('.', dot + 1) < 0) return null; // stop at the registrable domain
            domain = domain[(dot + 1)..];
        }
    }

    /// <summary>
    /// Hosts that are always the best choice when a page has them: FuckingFast first, then FileKeeper, then Gofile. They come first in the
    /// store page's list and in the automatic plan, and are the first fallbacks when a host has lost a file.
    /// </summary>
    public const int NotPreferred = int.MaxValue;

    /// <summary>0 = FuckingFast, 1 = FileKeeper, 2 = Gofile, <see cref="NotPreferred"/> for every other host.</summary>
    public static int Priority(string? host) => Find(host)?.Key switch
    {
        "fuckingfast" => 0,
        "filekeeper" => 1,
        "gofile" => 2,
        _ => NotPreferred,
    };

    /// <summary>
    /// <see cref="Priority(string?)"/> for a link. A link behind a redirector (BlueMediaFiles, Zovo) has the redirector's host, so
    /// the host the page names for it ("Link GoFile:") decides instead.
    /// </summary>
    public static int Priority(DownloadLink link)
    {
        var byHost = Priority(link.Host);
        if (byHost != NotPreferred || !IsLinkShortener(link.Host)) return byHost;

        var name = new string((link.Mirror ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (name.Contains("fuckingfast", StringComparison.Ordinal)) return 0;
        if (name.Contains("filekeeper", StringComparison.Ordinal)) return 1;
        if (name.Contains("gofile", StringComparison.Ordinal)) return 2;
        return NotPreferred;
    }

    /// <summary>First path segments that are a host's own pages, never files.</summary>
    private static readonly HashSet<string> SitePages = new(StringComparer.OrdinalIgnoreCase)
    {
        "faq", "about", "contact", "terms", "tos", "privacy", "premium", "pricing", "register", "signup", "login", "upload",
        "api", "help", "dmca", "abuse", "blog", "news", "static", "assets", "images", "img", "css", "js", "account", "affiliate",
    };

    /// <summary>
    /// The host when <paramref name="url"/> is a link to a file on it (not its home page or FAQ). Each host's pattern is
    /// tried first; hosts change their URL shapes, so any other path with an id-like segment on a known host counts too.
    /// </summary>
    public static FileHostInfo? MatchFileLink(Uri url)
    {
        // The generator page by its shape, whatever domain it is on this week ("/url-generator.php?url=…").
        if (IsGeneratorUrl(url)) return All.First(h => h.Key == "bluemediafiles");
        var info = Find(url.Host);
        if (info is null || info.Key == "itchio") return null;
        var target = url.AbsolutePath + url.Query + url.Fragment;
        if (info.FileUrlPattern.IsMatch(target) || info.FileUrlPattern.IsMatch(url.Query)) return info;

        var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || SitePages.Contains(segments[0])) return null;
        return segments.Any(s => s.Length >= 6 && s.Count(char.IsLetterOrDigit) >= 6) ? info : null;
    }

    private static readonly HashSet<string> PageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".php", ".asp", ".aspx", ".jsp", ".cgi", ".html", ".htm", ".shtml" };

    /// <summary>File name a host link carries: FitGirl puts it in the fragment ("#Game.part01.rar"), others in the path.</summary>
    public static string GuessFileName(Uri url)
    {
        var fragment = Sanitize(Uri.UnescapeDataString(url.Fragment.TrimStart('#')));
        if (LooksLikeFile(fragment)) return fragment;

        var last = Sanitize(Uri.UnescapeDataString(url.Segments.LastOrDefault()?.Trim('/') ?? string.Empty));
        return LooksLikeFile(last) ? last : string.Empty;

        // "url-generator.php", "download.html": a page or script name, never the file (and the same for every link of a redirector,
        // which used to fold a whole set of parts into one "url-generator.php").
        static bool LooksLikeFile(string name) =>
            name.Length is > 2 and < 200 && Path.GetExtension(name) is { Length: >= 2 and <= 6 } ext && ext.Skip(1).All(char.IsLetterOrDigit) &&
            !PageExtensions.Contains(ext);

        // Names may carry characters Windows doesn't allow (FitGirl titles with ':' or '?'): keep the name, swap the character.
        static string Sanitize(string name) =>
            new string(name.Select(c => c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' || char.IsControl(c) ? '_' : c).ToArray()).Trim();
    }
}
