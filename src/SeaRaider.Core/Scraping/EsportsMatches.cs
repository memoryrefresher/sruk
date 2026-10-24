using System.Text.RegularExpressions;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Scraping;

/// <summary>
/// Counter-Strike tournament streams and videos (Twitch, Kick, YouTube, any link): recognised by their title and channel, put in
/// the Media tab's own "Counter-Strike" section, named after the match being played ("NAVI vs FaZe") with the event under it
/// ("IEM Cologne 2026 · Playoffs").
/// </summary>
public static partial class EsportsMatches
{
    public const string CounterStrike = "Counter-Strike";

    [GeneratedRegex(@"(?<![a-z0-9])(cs2|cs 2|cs:?go|csgo|counter[\s-]?strike(\s*2)?)(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex GameRegex();

    /// <summary>Channels that only stream Counter-Strike events.</summary>
    private static readonly string[] CsChannels =
    [
        "eslcs", "esl_csgo", "esl_cs", "blastpremier", "blasttv", "blast", "pglesports", "pgl", "pgl_cs2", "cct_cs", "cctcs",
        "starladder_cs_en", "starladder_cs", "thunderpickcs", "perfectworld_cs", "betboom_cs", "esea", "faceit_cs", "hltvorg",
        "yallacompass", "elisaesports", "budapestmajor", "fissure_cs",
    ];

    [GeneratedRegex(@"(?<![a-z0-9])(major|iem|intel extreme masters|esl|pro league|blast|premier|pgl|cct|faceit|esea|starladder|" +
                    @"thunderpick|perfect world|betboom|fissure|yalla|elisa|cologne|katowice|rio|austin|copenhagen|shanghai|" +
                    @"playoffs?|qualifiers?|qualification|group stage|swiss|stage [0-9]|grand final|finals?|semi-?finals?|quarter-?finals?|" +
                    @"bo[135]|best of [135]|map [1-5]|day [0-9]+|season [0-9]+|cup|masters|open|championship|league|lan|showmatch)(?![a-z0-9])",
        RegexOptions.IgnoreCase)]
    private static partial Regex TournamentRegex();

    [GeneratedRegex(@"\s+(?:vs\.?|v\.?|versus|x)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex VersusRegex();

    /// <summary>Separators between a title's parts ("NAVI vs FaZe | IEM Cologne - Playoffs").</summary>
    [GeneratedRegex(@"\s+[|–—·•/]\s+|\s+-\s+|\s*\|\s*|:\s+")]
    private static partial Regex PartsRegex();

    /// <summary>Stream decoration: "LIVE", "🔴", "[EN]", "(BO3)", "!drops", "@sponsor".</summary>
    [GeneratedRegex(@"[\p{So}\p{Cs}]+|\[[^\]]*\]|\((?:bo[135]|en|eng|english|live|ru|pt|es)\)|(?<![a-z])(?:live|rebroadcast|rerun|hd|4k)(?![a-z])\s*:?|![a-z]+|@\w+|#\w+",
        RegexOptions.IgnoreCase)]
    private static partial Regex DecorationRegex();

    /// <summary>Is this a Counter-Strike tournament's stream or video? Then it gets the match's name and the event under it.</summary>
    public static bool Apply(MediaItem item)
    {
        var title = item.Title ?? string.Empty;
        var channel = (item.Channel ?? string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
        var csChannel = CsChannels.Any(c => channel.Equals(c, StringComparison.OrdinalIgnoreCase) ||
                                            (item.PlayUrl?.Contains($"/{c}", StringComparison.OrdinalIgnoreCase) ?? false));
        var namesCs = GameRegex().IsMatch(title) || GameRegex().IsMatch(item.Category ?? string.Empty);
        if (!csChannel && !namesCs) return false;
        if (!csChannel && !TournamentRegex().IsMatch(title) && !VersusRegex().IsMatch(title)) return false; // someone just playing CS

        var (match, eventName) = Split(title);
        item.Category = CounterStrike;
        if (match is not null) item.Title = match;
        else if (eventName is not null) item.Title = eventName;
        item.EventName = match is not null ? eventName : null;
        return true;
    }

    /// <summary>"🔴 LIVE: NAVI vs FaZe | IEM Cologne 2026 - Playoffs [EN]" → ("NAVI vs FaZe", "IEM Cologne 2026 · Playoffs").</summary>
    internal static (string? Match, string? Event) Split(string title)
    {
        var clean = Regex.Replace(DecorationRegex().Replace(title, " "), @"\s+", " ").Trim(' ', '-', '|', ':');
        var parts = PartsRegex().Split(clean).Select(p => p.Trim(' ', '-', '|', ':', ',')).Where(p => p.Length > 0).ToList();

        string? match = null;
        foreach (var part in parts)
        {
            var sides = VersusRegex().Split(part);
            if (sides.Length != 2 || sides[0].Trim().Length == 0 || sides[1].Trim().Length == 0) continue;
            match = $"{Side(sides[0])} vs {Side(sides[1])}";
            parts.Remove(part);
            break;
        }

        var eventParts = parts
            .Where(p => !GameRegex().IsMatch(p) || TournamentRegex().IsMatch(GameRegex().Replace(p, string.Empty)))
            .Select(p => Regex.Replace(GameRegex().Replace(p, " "), @"\s+", " ").Trim(' ', '-', '|', ':', ','))
            .Where(p => p.Length > 1 && !Regex.IsMatch(p, @"^(watch|stream|official|english|en|today)$", RegexOptions.IgnoreCase))
            .Take(2)
            .ToList();
        var eventName = eventParts.Count > 0 ? string.Join(" · ", eventParts) : null;
        if (eventName?.Length > 90) eventName = eventName[..90].TrimEnd() + "…";
        return (match, eventName);
    }

    /// <summary>A side's name without "(BO3)" and leftovers.</summary>
    private static string Side(string side)
    {
        var s = Regex.Replace(side, @"\((?:bo[135]|[0-9]+-[0-9]+)\)", " ", RegexOptions.IgnoreCase);
        return Regex.Replace(s, @"\s+", " ").Trim(' ', '-', '|', ':', ',');
    }
}
