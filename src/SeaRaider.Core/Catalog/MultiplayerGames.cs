using System.Text.RegularExpressions;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Catalog;

/// <summary>
/// The store's "Multiplayer" section: every game with "Online" in its title ("… Online", "(Online)", "+ Online-Fix",
/// "OnlineFix", "ONLINE"), except titles where "online" doesn't mean you can play online: the default exceptions below (and
/// whatever is added in Settings › General › Store), e.g. Sword Art Online games that are single-player, or a repack noted
/// "No Online".
/// </summary>
public static partial class MultiplayerGames
{
    /// <summary>
    /// Titles (or parts of titles) with "online" in them that aren't online games, one per line or comma. Matched in the
    /// title, ignoring case and punctuation.
    /// </summary>
    public const string DefaultExceptions =
        "no online, without online, online disabled, online not working, online removed, offline only, " +
        "sword art online re hollow fragment, sword art online hollow realization, sword art online alicization lycoris, " +
        "accel world vs sword art online, sword art online lost song, sword art online infinity moment, " +
        "online dating simulator, online mod manager";

    // "online" as a word of its own, or glued to "fix" ("Online-Fix", "OnlineFix", "online_fix"); never inside another word
    // ("Onliner") or "offline".
    [GeneratedRegex(@"(?<![a-z0-9])online(?:[\s._-]*fix)?(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OnlineRegex();

    /// <summary>"no online, sword art online …" → normalized phrases (lower case, punctuation as spaces).</summary>
    public static IReadOnlyList<string> ParseExceptions(string? text) =>
        (text ?? DefaultExceptions)
            .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize)
            .Where(w => w.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>The title says Online, and none of the exceptions are in it.</summary>
    public static bool IsMultiplayer(CatalogEntry entry, IReadOnlyList<string> exceptions) => IsOnlineTitle(entry.Title, exceptions);

    public static bool IsOnlineTitle(string? title, IReadOnlyList<string> exceptions)
    {
        if (string.IsNullOrWhiteSpace(title) || !OnlineRegex().IsMatch(title)) return false;
        var padded = " " + Normalize(title) + " ";
        return !exceptions.Any(e => padded.Contains(" " + e + " ", StringComparison.Ordinal));
    }

    /// <summary>Lower case; anything but letters and digits becomes one space ("Sword Art Online: Re: Hollow" → "sword art online re hollow").</summary>
    private static string Normalize(string text) =>
        string.Join(' ', new string(text.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
