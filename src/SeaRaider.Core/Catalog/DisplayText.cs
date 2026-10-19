using System.Net;
using System.Text.RegularExpressions;
using SeaRaider.Core.Scraping;

namespace SeaRaider.Core.Catalog;

/// <summary>
/// Titles and descriptions as a store shows them. Repack sites pack release notes into both: "God of War III Remastered –
/// v1.0.1 (shadPS4 v0.9) + 2 DLCs [FitGirl Repack]" and descriptions that run into "Genres/Tags: … Repack Size: …".
/// The version, DLCs, size and languages are shown as tags on their own, so the title is just the game's name and the
/// description just its story.
/// </summary>
public static partial class DisplayText
{
    // " - v1.x.x.x …", ", Build 1234 …", " + 3 DLCs …": everything from the first release note on.
    [GeneratedRegex(@"\s*(?:[-–—:,|/]\s*|\s)(?:v|ver\.?|version)\s*\d[\w.\-]*.*$|\s*[-–—,|/]?\s*\bbuild\s*\.?\s*\d{3,}.*$|" +
                    @"\s+\+\s+(?:\d+\s+|all\s+)?(?:dlcs?|bonus(?:es)?|ost|soundtrack|updates?|multiplayer|online|fix|crack|emulator|shadps4)\b.*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseNotesRegex();

    [GeneratedRegex(@"\s*[\[(][^\[\]()]*(?:repack|multi\d*|v\d|build|update|dlc|crack|fix|emu|shadps4|rpcs3|yuzu|ryujinx|portable|gog|steam)[^\[\]()]*[\])]",
        RegexOptions.IgnoreCase)]
    private static partial Regex NoteGroupRegex();

    [GeneratedRegex(@"(?:\s*[-–—:,|/+&])+\s*$")]
    private static partial Regex TrailingSeparatorRegex();

    /// <summary>"God of War III Remastered" from "God of War III Remastered - v1.x.x.x + Bonus [FitGirl Repack]".</summary>
    public static string Title(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        var text = WebUtility.HtmlDecode(title).Trim();
        text = NoteGroupRegex().Replace(text, string.Empty);
        text = ReleaseNotesRegex().Replace(text, string.Empty);
        text = TrailingSeparatorRegex().Replace(text, string.Empty).Trim();
        return text.Length >= 2 ? text : title.Trim();
    }

    // Lines that are a repack page's info dump, not the game's story: "Label: value" lines, and section headings.
    [GeneratedRegex(@"^\s*(?:genres?\s*/\s*tags|genres?|tags|companies|company|developers?|publishers?|languages?|interface\s+languages?|" +
                    @"audio|subtitles|original\s+size|repack\s+size|size|download\s+mirrors?|mirrors?|selective\s+download|" +
                    @"based\s+on|game\s+version|version|release\s+date|platforms?|filehosters?|torrent|magnet|note)\s*[:：]" +
                    @"|^\s*(?:repack\s+features|screenshots?(?:\s*\(.*\))?|system\s+requirements|minimum\s+requirements|" +
                    @"problems\s+during\s+installation\??|installation|download\s+mirrors?.*|discussion\s+and\s+\(possible\)\s+future\s+updates.*" +
                    @"|game\s+description|description)\s*:?\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex InfoLineRegex();

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"\s*(?:\[\s*(?:…|\.\.\.)\s*\]|read\s+more\s*(?:»|→)?|continue\s+reading\s*(?:»|→)?|click\s+to\s+show)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ReadMoreRegex();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex SpacesRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLinesRegex();

    /// <summary>
    /// A file name in a description: "Game.part01.rar", "setup.exe", "fg-01.bin", "Game.7z.001". Version numbers
    /// ("v1.0.003") and names like "Node.js" don't count.
    /// </summary>
    [GeneratedRegex(@"(?<![\w.\-])[\w\-\[\]()+'&~.]+\.(?:part\d{1,4}\.rar|(?:7z|zip|rar)\.\d{3}|rar|zip|7z|iso|exe|bin|msi|cab|r\d{2,3}|z\d{2,3}|tar|gz|bz2|xz|torrent|dll|pak|md5|sfv)(?![\w])",
        RegexOptions.IgnoreCase)]
    private static partial Regex FileNameRegex();

    /// <summary>The game's story without the info dump, links, "Read more" and stray whitespace.</summary>
    public static string Description(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return string.Empty;
        var lines = WebUtility.HtmlDecode(description).Replace("\r\n", "\n").Split('\n')
            .Select(line => SpacesRegex().Replace(UrlRegex().Replace(line, string.Empty), " ").Trim())
            .Where(line => !InfoLineRegex().IsMatch(line))
            .Where(line => !FileNameRegex().IsMatch(line)) // file lists and install steps ("Run setup.exe", "Game.part01.rar")
            .Where(line => !(line.Length < 40 && MetadataParser.CleanTitle(line).Version is not null)); // "v1.0.3 + 2 DLCs"
        var text = BlankLinesRegex().Replace(string.Join('\n', lines), "\n\n").Trim();
        text = ReadMoreRegex().Replace(text, string.Empty).TrimEnd();

        // A cut-off teaser ends with an ellipsis, not mid-word.
        if (text.Length > 0 && !".!?…\"”')".Contains(text[^1]))
        {
            var lastStop = text.LastIndexOfAny(['.', '!', '?']);
            text = lastStop > text.Length * 0.6 ? text[..(lastStop + 1)] : text.TrimEnd(',', ';', ':', '-') + "…";
        }
        return text;
    }
}
