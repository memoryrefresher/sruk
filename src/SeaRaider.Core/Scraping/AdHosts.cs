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
    };

    private static readonly string[] NameHints =
    [
        "popunder", "popads", "popcash", "clickunder", "adserver", "adsrv", "adservice", "adsystem", "adnetwork",
        "trafficjunky", "propeller", "onclick", "pushnotif", "push-notif",
    ];

    /// <summary>True for an ad / pop-up network address.</summary>
    public static bool IsAd(Uri? uri) => uri is not null && IsAdHost(uri.Host);

    /// <summary>True for an ad / pop-up network address (any string; not a web address: false).</summary>
    public static bool IsAd(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsAdHost(uri.Host);

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
            host.StartsWith("adserver.", StringComparison.Ordinal) || host.StartsWith("pop.", StringComparison.Ordinal))
            return true;
        return NameHints.Any(h => host.Contains(h, StringComparison.Ordinal));
    }
}
