using SeaRaider.Core.Models;

namespace SeaRaider.Core.Scraping;

/// <summary>Defaults for a well-known game site layout.</summary>
/// <param name="DisplayName">Name shown on source tabs and cards ("SteamRIP").</param>
/// <param name="Domains">Domains, without "www.".</param>
/// <param name="UrlExcludePattern">Posts that aren't games (FAQ, digests, request pages).</param>
public sealed record KnownSite(string DisplayName, string[] Domains, string? UrlExcludePattern);

/// <summary>
/// Well-known repack / pre-installed sites. They are ordinary WordPress sites, so the generic parser reads them; this
/// only gives them a proper name and keeps their non-game posts (FAQ, update digests, request pages) out of the store.
/// </summary>
public static class SiteProfiles
{
    public static IReadOnlyList<KnownSite> All { get; } =
    [
        new("FitGirl Repacks", ["fitgirl-repacks.site"],
            @"/(?:upcoming-repacks|updates-digest|donations?|faq|contacts?|repacks-troubleshooting|switch-emulated-repacks|games-with-my-personal-pink-paw-award|all-my-repacks-a-z|popular-repacks)"),
        new("SteamRIP", ["steamrip.com"],
            @"/(?:faq|how-to-|contact|dmca|request|games-list|top-games|about|privacy)"),
        new("DODI Repacks", ["dodi-repacks.site", "dodi-repacks.download"], @"/(?:faq|contact|donate|request)"),
        new("ElAmigos", ["elamigos.site"], null),
        new("KaOsKrew", ["kaoskrew.org"], null),
        new("itch.io", ["itch.io"], null),
        new("GOG Games", ["gog-games.to", "gog-games.com"], @"/(?:faq|request|news)"),
    ];

    public static KnownSite? Find(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        var domain = host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
        return All.FirstOrDefault(s => s.Domains.Any(d => d.Equals(domain, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Friendly source name: the known site name when the source still carries its bare host name.</summary>
    public static string NameFor(GameSource source)
    {
        var host = Uri.TryCreate(source.Url, UriKind.Absolute, out var u) ? u.Host : null;
        var known = Find(host);
        if (known is null) return source.DisplayName;
        var bareHost = host is not null && (source.DisplayName.Equals(host, StringComparison.OrdinalIgnoreCase) ||
                                            source.DisplayName.Equals(host.Replace("www.", string.Empty), StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(source.DisplayName) || bareHost ? known.DisplayName : source.DisplayName;
    }
}
