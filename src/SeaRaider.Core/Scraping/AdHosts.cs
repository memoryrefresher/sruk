namespace SeaRaider.Core.Scraping;

/// <summary>
/// Ad, pop-up and pop-under networks. The in-app player and the web-game window never open these, and the player doesn't
/// load anything from them: pages that embed a video often open one of these in a new window on the first click on
/// "play". A host matches when it is one of these domains or a subdomain of one, or when its name gives it away
/// ("ads.", "popunder" …).
/// </summary>
public static class AdHosts
{
    private static readonly HashSet<string> Domains = new(StringComparer.OrdinalIgnoreCase)
    {
        // Pop-up / pop-under / redirect networks
        "popads.net", "popcash.net", "propellerads.com", "propellerclick.com", "propu.sh", "onclickads.net", "onclkds.com",
        "onclickperformance.com", "onclickalgo.com", "onclickgenius.com", "adsterra.com", "adsterratools.com", "adcash.com",
        "clickadu.com", "clickaine.com", "hilltopads.net", "hilltopads.com", "ad-maven.com", "admaven.com", "monetag.com",
        "galaksion.com", "zeropark.com", "popmyads.com", "popunder.net", "pophit.com", "popadscdn.net", "ppcnt.co",
        "richpush.co", "evadav.com", "pushwhy.com", "pushame.com", "pushno.com", "push-sdk.com", "realsrv.com",
        "exoclick.com", "exosrv.com", "exdynsrv.com", "juicyads.com", "trafficjunky.net", "trafficjunky.com", "trafficstars.com",
        "tsyndicate.com", "trafficfactory.biz", "adskeeper.com", "adskeeper.co.uk", "bidvertiser.com", "yllix.com",
        "a-ads.com", "adnium.com", "plugrush.com", "clickadilla.com", "adspyglass.com", "highperformanceformat.com",
        "highperformancecpm.com", "profitablecpmrate.com", "profitablegatecpm.com", "toprevenuegate.com", "effectiveratecpm.com",
        "displaycontentnetwork.com", "dolohen.com", "whos.amung.us", "mobiletraffic.com", "pemsrv.com", "tapioni.com",
        "acint.net", "betteradsystem.com", "s.orbsrv.com", "orbsrv.com", "adxpremium.services", "aclib.net", "acscdn.com",
        "runative-syndicate.com", "admanmedia.com", "vidoomy.com", "a.magsrv.com", "magsrv.com", "kadam.net", "go.oclasrv.com",
        "oclasrv.com", "luckypushh.com", "glizauvo.net", "ophoacit.com", "dexpredict.com", "streamads.net",

        // Display ad and tracking networks (loaded inside players and game pages)
        "doubleclick.net", "googlesyndication.com", "googleadservices.com", "adservice.google.com", "googletagservices.com",
        "imasdk.googleapis.com", "amazon-adsystem.com", "adnxs.com", "criteo.com", "criteo.net", "pubmatic.com",
        "rubiconproject.com", "openx.net", "casalemedia.com", "advertising.com", "adform.net", "smartadserver.com",
        "taboola.com", "outbrain.com", "mgid.com", "revcontent.com", "adblade.com", "zergnet.com", "media.net",
        "scorecardresearch.com", "histats.com", "statcounter.com", "yandexadexchange.net", "adsco.re", "adtelligent.com",
        "spotxchange.com", "springserve.com", "aniview.com", "primis.tech", "connatix.com", "ezoic.net", "ezojs.com",

        // Adsterra's rotating domains, video-ad and header-bidding networks, ad verification, in-app and mobile ad networks
        "effectivegatecpm.com", "highcpmgate.com", "profitableratecpm.com", "cpmstar.com", "pushails.com", "notifpush.com",
        "adtng.com", "adtrue.com", "ero-advertising.com", "crakrevenue.com", "xlviirdr.com", "xlivrdr.com",
        "moatads.com", "adsafeprotected.com", "doubleverify.com", "serving-sys.com", "2mdn.net", "adtechus.com", "yieldmo.com",
        "sharethrough.com", "33across.com", "indexww.com", "lijit.com", "sovrn.com", "teads.tv", "teads.com", "inmobi.com",
        "applovin.com", "adcolony.com", "chartboost.com", "vungle.com", "ironsrc.com", "smaato.net", "bidswitch.net",
        "adsrvr.org", "rlcdn.com", "bluekai.com", "demdex.net", "everesttech.net", "adthrive.com", "mediavine.com",
        "snigelweb.com", "kueezrtb.com", "richaudience.com", "adscale.de", "disqusads.com", "pubmine.com", "adsninja.ca",
        "adsco.re", "sellads.net", "buysellads.com", "carbonads.net", "carbonads.com", "adgrx.com", "gumgum.com",
        "triplelift.com", "improvedigital.com", "undertone.com", "freewheel.tv", "fwmrm.net", "innovid.com", "tremorhub.com",
        "videoplaza.tv", "adotmob.com", "mookie1.com", "turn.com", "contextweb.com", "lkqd.net", "quantserve.com", "tapad.com",
        "bounceexchange.com", "adform.com", "weborama.com", "emxdgt.com", "admixer.net", "adition.com", "e-planning.net",
    };

    private static readonly string[] NameHints =
    [
        "popunder", "popads", "popcash", "clickunder", "adserver", "adsrv", "adservice", "adsystem", "adnetwork",
        "trafficjunky", "propeller", "onclick", "pushnotif", "push-notif",
        // Adsterra and its look-alikes name their domains after what they sell
        "cpmgate", "cpmrate", "cpmstar", "adsterra", "exoclick", "clickadu", "monetag", "doubleclick", "googlesyndication",
    ];

    /// <summary>The ad networks' domains (a host matches when it is one of them or a subdomain of one). For the page script, which asks the same question.</summary>
    public static IReadOnlyCollection<string> KnownDomains => Domains;

    /// <summary>Words in a host name that give it away as an ad host. For the page script.</summary>
    public static IReadOnlyList<string> NameHintList => NameHints;

    /// <summary>
    /// Shapes of address that are ad requests whatever site they are on: ad-server folders and video-ad (VAST) files, and the query
    /// words ad servers are asked with.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex AdPath = new(
        @"(?:^|/)(?:pagead|adserver|adsense|adframe|adview|popunder|popads|pop-under|vast|vpaid|prebid|ads)(?:[/._-]|$)|" +
        @"[?&](?:ad_?(?:type|unit|slot|zone|id)|zoneid|adzone)=",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    /// <summary>True for an ad / pop-up network address.</summary>
    public static bool IsAd(Uri? uri) => uri is not null && IsAdHost(uri.Host);

    /// <summary>True for an ad / pop-up network address (any string; not a web address: false).</summary>
    public static bool IsAd(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsAdHost(uri.Host);

    /// <summary>
    /// True for a request that is an ad: one to an ad network (<see cref="IsAd(string?)"/>), or one for an ad-server folder or video-ad
    /// file on any site ("/pagead/", "vast.xml", "?zoneid="). For the requests a player's or game's page makes; a request for a page
    /// (a link the user followed) is judged by <see cref="IsAd(string?)"/> alone.
    /// </summary>
    public static bool IsAdRequest(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
        if (IsAdHost(uri.Host)) return true;
        try
        {
            return AdPath.IsMatch(uri.PathAndQuery);
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <inheritdoc cref="IsAdRequest(string?)"/>
    public static bool IsAdRequest(Uri? uri) => uri is not null && IsAdRequest(uri.AbsoluteUri);

    public static bool IsAdHost(string? host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        host = host.ToLowerInvariant().TrimEnd('.');

        // The domain or any parent domain on the list ("cdn.popads.net" → "popads.net").
        for (var name = host; ;)
        {
            if (Domains.Contains(name)) return true;
            var dot = name.IndexOf('.');
            if (dot < 0 || name.IndexOf('.', dot + 1) < 0) break; // stop at "example.com"
            name = name[(dot + 1)..];
        }

        if (host.StartsWith("ads.", StringComparison.Ordinal) || host.StartsWith("ad.", StringComparison.Ordinal) ||
            host.StartsWith("adserver.", StringComparison.Ordinal) || host.StartsWith("pop.", StringComparison.Ordinal) ||
            host.StartsWith("adv.", StringComparison.Ordinal) || host.StartsWith("banners.", StringComparison.Ordinal) ||
            host.StartsWith("pagead.", StringComparison.Ordinal))
            return true;
        return NameHints.Any(h => host.Contains(h, StringComparison.Ordinal));
    }
}
