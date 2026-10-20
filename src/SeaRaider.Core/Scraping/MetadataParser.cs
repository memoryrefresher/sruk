using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace SeaRaider.Core.Scraping;

/// <summary>Title split into its clean name and the release noise that was attached to it.</summary>
/// <param name="Title">Clean display title.</param>
/// <param name="Version">Version / build found in the title, if any.</param>
/// <param name="DlcInfo">DLC note found in the title ("5 DLCs", "All DLCs", "DLC included").</param>
/// <param name="LanguageHint">"MULTi12"-style language count found in the title.</param>
public sealed record TitleParts(string Title, string? Version, string? DlcInfo, string? LanguageHint);

/// <summary>Structured fields extracted from a game page.</summary>
public sealed class StructuredMetadata
{
    public string Title { get; set; } = string.Empty;
    public string? Version { get; set; }
    public string? DlcInfo { get; set; }
    public List<string> Genres { get; set; } = [];
    public string? Developer { get; set; }
    public string? Publisher { get; set; }
    public string? ReleaseDateText { get; set; }
    public DateTime? ReleaseDateUtc { get; set; }
    public DateTime? UploadedAtUtc { get; set; }
    public List<string> Languages { get; set; } = [];
    public string? RepackSize { get; set; }
    public string? OriginalSize { get; set; }

    /// <summary>Cleaned description: the game's blurb only, without info lines, mirrors, install notes or links.</summary>
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Turns messy release pages into clean, separate fields.
///
/// <para><b>Titles</b>: "Portal Dungeon: Goblin Escape - v1.1 + 2 DLCs [FitGirl Repack]" →
/// title "Portal Dungeon: Goblin Escape", version "1.1", DLC "2 DLCs".</para>
///
/// <para><b>Info lines</b>: the content is flattened to text lines (paragraphs, list items, line breaks and
/// two-cell table rows become "Label: value" lines), then known labels and their synonyms are matched —
/// several labels on one line are split correctly ("Genre: RPG Release Date: 2024").</para>
///
/// <para><b>Description</b>: a "Game Description" / "About the game" section when one exists, otherwise the
/// longest prose paragraphs with info lines, links, mirror lists and install instructions removed.</para>
///
/// Pure functions only — no I/O — so everything here is unit-testable.
/// </summary>
public static partial class MetadataParser
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    // =============================================================================================
    // Titles
    // =============================================================================================

    /// <summary>Release-group / site words that are never part of a game's name.</summary>
    [GeneratedRegex(@"\b(?:free\s+download|download\s+free|direct\s+download|torrent\s+download|full\s+version|pc\s+game|" +
                    @"(?:fitgirl|dodi|elamigos|kaos|xatab)\s+repack|repack(?:ed)?|pre-?installed|multi\s*-?\s*\d{1,2})\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex NoiseWordsRegex();

    [GeneratedRegex(@"(?:^|(?<=[\s\[(,+\-–—|:]))(?:v|ver\.?|version)\s*(?<v>\d+(?:[.\-_]\d+){0,5}[a-z]?(?:\s*(?:hotfix|patch|rev\.?|r)\s*\d+)?)(?=$|[\s\])+,\-–—|])",
        RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"\b(?<v>build\s*\.?\s*\d{3,})\b", RegexOptions.IgnoreCase)]
    private static partial Regex BuildRegex();

    [GeneratedRegex(@"[\[(]\s*(?<v>\d+(?:\.\d+){1,4}[a-z]?)\s*[\])]", RegexOptions.IgnoreCase)]
    private static partial Regex BracketedNumberVersionRegex();

    [GeneratedRegex(@"(?:\+|\bincl(?:uding)?\b\.?|\bwith\b|&)\s*(?<n>\d+|all|every)?\s*(?:the\s+)?(?:bonus\s+)?dlcs?\b(?:\s*(?:&|and|\+)\s*bonus(?:es| content)?)?",
        RegexOptions.IgnoreCase)]
    private static partial Regex DlcInTitleRegex();

    [GeneratedRegex(@"\bmulti\s*-?\s*(?<n>\d{1,2})\b", RegexOptions.IgnoreCase)]
    private static partial Regex MultiLanguageRegex();

    [GeneratedRegex(@"\+\s*(?:bonus\s+)?(?:content|ost|soundtrack|artbook|bonus(?:es)?|windows\s*7\s*fix|online|multiplayer|update\s*\d*)\b[^+\[\]()]*",
        RegexOptions.IgnoreCase)]
    private static partial Regex PlusExtrasRegex();

    [GeneratedRegex(@"[\[(][^\[\]()]*[\])]")]
    private static partial Regex BracketGroupRegex();

    [GeneratedRegex(@"^\(\s*(?:19|20)\d{2}\s*\)$")]
    private static partial Regex YearGroupRegex();

    /// <summary>Splits a published title into a clean name plus version / DLC / language hints.</summary>
    /// <param name="siteName">og:site_name, stripped when it's appended to the title.</param>
    public static TitleParts CleanTitle(string rawTitle, string? siteName = null)
    {
        var text = Collapse(WebUtility.HtmlDecode(rawTitle ?? string.Empty))
            .Replace('–', '-').Replace('—', '-').Replace('’', '\'');

        if (!string.IsNullOrWhiteSpace(siteName))
        {
            text = Regex.Replace(text, $@"\s*[-|:»]\s*{Regex.Escape(siteName.Trim())}\s*$", string.Empty, RegexOptions.IgnoreCase,
                RegexTimeout);
        }

        string? version = null, dlc = null, languages = null;

        // Version: "v1.2.3", "Version 2", "Build 12345", "(1.0.5)".
        if (VersionRegex().Match(text) is { Success: true } v)
        {
            version = v.Groups["v"].Value.Trim();
            text = text.Remove(v.Index, v.Length).Insert(v.Index, " ");
        }
        else if (BuildRegex().Match(text) is { Success: true } b)
        {
            version = Regex.Replace(b.Groups["v"].Value, @"build\s*\.?\s*", "Build ", RegexOptions.IgnoreCase, RegexTimeout).Trim();
            text = text.Remove(b.Index, b.Length).Insert(b.Index, " ");
        }
        else if (BracketedNumberVersionRegex().Match(text) is { Success: true } n)
        {
            version = n.Groups["v"].Value;
            text = text.Remove(n.Index, n.Length).Insert(n.Index, " ");
        }

        // DLC: "+ 5 DLCs", "+ All DLCs", "incl. DLC".
        if (DlcInTitleRegex().Match(text) is { Success: true } d)
        {
            dlc = DescribeDlcCount(d.Groups["n"].Value);
            text = text.Remove(d.Index, d.Length).Insert(d.Index, " ");
        }

        if (MultiLanguageRegex().Match(text) is { Success: true } m)
            languages = $"{m.Groups["n"].Value} languages";

        text = PlusExtrasRegex().Replace(text, " ");

        // Bracket groups are release notes ("[FitGirl Repack]", "(MULTi10)", "(+ OST)") — except a lone year.
        text = BracketGroupRegex().Replace(text, g => YearGroupRegex().IsMatch(g.Value) ? g.Value : " ");
        text = NoiseWordsRegex().Replace(text, " ");

        // Tidy separators left behind ("Game -  - ", "Game:", "Game ,").
        text = Collapse(text);
        text = Regex.Replace(text, @"\s+([:,])", "$1", RegexOptions.None, RegexTimeout);
        text = Regex.Replace(text, @"(?:\s*[-+|,/&]\s*)+$", string.Empty, RegexOptions.None, RegexTimeout);
        text = Regex.Replace(text, @"^(?:\s*[-+|,/&:]\s*)+", string.Empty, RegexOptions.None, RegexTimeout);
        text = Regex.Replace(text, @"\s+-\s+(?=-|$)", string.Empty, RegexOptions.None, RegexTimeout);
        text = text.Trim().TrimEnd(':', '-', ',').Trim();

        if (text.Length == 0) text = Collapse(rawTitle ?? string.Empty); // never return an empty title
        return new TitleParts(text, version, dlc, languages);
    }

    private static string DescribeDlcCount(string count) => count.ToLowerInvariant() switch
    {
        "" => "DLC included",
        "all" or "every" => "All DLCs",
        "1" => "1 DLC",
        _ => $"{count} DLCs",
    };

    // =============================================================================================
    // Labelled info lines
    // =============================================================================================

    private enum Field { Genre, ReleaseDate, UploadDate, Version, Dlc, Languages, RepackSize, OriginalSize, Developer, Publisher, Companies }

    /// <summary>Label synonyms → field. Longer synonyms are listed first so they win ("repack size" before "size").</summary>
    private static readonly (string Label, Field Field)[] LabelSynonyms =
    [
        ("genres/tags", Field.Genre), ("genre/tags", Field.Genre), ("genres", Field.Genre), ("genre", Field.Genre),
        ("release date", Field.ReleaseDate), ("released on", Field.ReleaseDate), ("date released", Field.ReleaseDate),
        ("initial release", Field.ReleaseDate), ("released", Field.ReleaseDate),
        ("upload date", Field.UploadDate), ("uploaded on", Field.UploadDate), ("uploaded", Field.UploadDate),
        ("date added", Field.UploadDate), ("posted on", Field.UploadDate),
        ("game version", Field.Version), ("current version", Field.Version), ("version", Field.Version),
        ("included dlcs", Field.Dlc), ("dlc included", Field.Dlc), ("dlcs", Field.Dlc), ("dlc's", Field.Dlc), ("dlc", Field.Dlc),
        ("interface languages", Field.Languages), ("interface language", Field.Languages), ("audio languages", Field.Languages),
        ("text languages", Field.Languages), ("supported languages", Field.Languages), ("languages", Field.Languages),
        ("language", Field.Languages), ("subtitles", Field.Languages),
        ("repacked size", Field.RepackSize), ("repack size", Field.RepackSize), ("download size", Field.RepackSize),
        ("compressed size", Field.RepackSize), ("size (repack)", Field.RepackSize),
        ("original size", Field.OriginalSize), ("installed size", Field.OriginalSize), ("install size", Field.OriginalSize),
        ("unpacked size", Field.OriginalSize), ("game size", Field.OriginalSize), ("full size", Field.OriginalSize),
        ("size", Field.OriginalSize),
        ("developers", Field.Developer), ("developer", Field.Developer), ("developed by", Field.Developer),
        ("publishers", Field.Publisher), ("publisher", Field.Publisher), ("published by", Field.Publisher),
        ("companies", Field.Companies), ("company", Field.Companies),
    ];

    private static readonly Dictionary<string, Field> LabelMap =
        LabelSynonyms.ToDictionary(s => s.Label, s => s.Field, StringComparer.OrdinalIgnoreCase);

    private static readonly Regex LabelRegex = new(
        @"(?<![\p{L}\d])(?<label>" + string.Join("|", LabelSynonyms.Select(s => Regex.Escape(s.Label).Replace(@"\ ", @"\s+"))) +
        @")\s*[:：]\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    /// <summary>Every "Label: value" pair found in the lines, in order (labels normalised to lower case).</summary>
    public static IReadOnlyList<(string Label, string Value)> ExtractLabeledFields(IEnumerable<string> lines)
    {
        var result = new List<(string, string)>();
        foreach (var line in lines)
        {
            var matches = LabelRegex.Matches(line);
            for (var i = 0; i < matches.Count; i++)
            {
                var start = matches[i].Index + matches[i].Length;
                var end = i + 1 < matches.Count ? matches[i + 1].Index : line.Length;
                var value = line[start..end].Trim().TrimEnd(',', ';', '|').Trim();
                if (value.Length == 0 || value.Length > 300) continue;
                var label = Regex.Replace(matches[i].Groups["label"].Value.ToLowerInvariant(), @"\s+", " ");
                result.Add((label, value));
            }
        }
        return result;
    }

    /// <summary>Parses a page (or one article element) into structured metadata.</summary>
    /// <param name="contentRoot">The post body (article / entry-content).</param>
    /// <param name="rawTitle">Title as published.</param>
    /// <param name="siteName">og:site_name, stripped from titles.</param>
    /// <param name="document">Whole page, for upload-date meta tags; may be null for API content.</param>
    public static StructuredMetadata Parse(IElement contentRoot, string rawTitle, string? siteName, IDocument? document)
    {
        var titleParts = CleanTitle(rawTitle, siteName);
        var result = new StructuredMetadata
        {
            Title = titleParts.Title,
            Version = titleParts.Version,
            DlcInfo = titleParts.DlcInfo,
        };

        var lines = ToLines(contentRoot);
        var labelled = ExtractLabeledFields(lines);

        foreach (var (label, value) in labelled)
        {
            if (!LabelMap.TryGetValue(label, out var field)) continue;
            switch (field)
            {
                case Field.Genre when result.Genres.Count == 0:
                    result.Genres = SplitList(value).Where(g => g.Length is > 1 and < 30).Take(8).ToList();
                    break;
                case Field.ReleaseDate when result.ReleaseDateText is null:
                    result.ReleaseDateText = CleanValue(value);
                    result.ReleaseDateUtc = ParseDate(value);
                    break;
                case Field.UploadDate when result.UploadedAtUtc is null:
                    result.UploadedAtUtc = ParseDate(value);
                    break;
                case Field.Version when result.Version is null:
                    result.Version = NormalizeVersion(value);
                    break;
                case Field.Dlc when result.DlcInfo is null:
                    result.DlcInfo = NormalizeDlc(value);
                    break;
                case Field.Languages when result.Languages.Count == 0:
                    result.Languages = ParseLanguages(value);
                    break;
                case Field.RepackSize when result.RepackSize is null:
                    result.RepackSize = NormalizeSize(value);
                    break;
                case Field.OriginalSize when result.OriginalSize is null:
                    result.OriginalSize = NormalizeSize(value);
                    break;
                case Field.Developer when result.Developer is null:
                    result.Developer = CleanValue(value);
                    break;
                case Field.Publisher when result.Publisher is null:
                    result.Publisher = CleanValue(value);
                    break;
                case Field.Companies when result.Developer is null:
                    // FitGirl style "Companies: Developer / Publisher"
                    var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    result.Developer = CleanValue(parts[0]);
                    if (parts.Length > 1) result.Publisher ??= CleanValue(parts[1]);
                    break;
            }
        }

        if (result.Languages.Count == 0 && titleParts.LanguageHint is { } hint) result.Languages = [hint];
        if (document is not null) result.UploadedAtUtc ??= FindUploadDate(document);
        result.Description = ExtractDescription(contentRoot, lines);
        return result;
    }

    // =============================================================================================
    // Values
    // =============================================================================================

    [GeneratedRegex(@"(?<n>\d+(?:[.,]\d+)?)\s*(?<u>TB|GB|MB|KB|GiB|MiB|TiB)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SizeValueRegex();

    /// <summary>"from 5.21 GB [Selective Download]" → "from 5.21 GB"; null when there's no size in the text.</summary>
    public static string? NormalizeSize(string text)
    {
        var m = SizeValueRegex().Match(text);
        if (!m.Success) return null;

        var unit = m.Groups["u"].Value.ToUpperInvariant() switch { "GIB" => "GB", "MIB" => "MB", "TIB" => "TB", var u => u };
        var size = $"{m.Groups["n"].Value.Replace(',', '.')} {unit}";
        var prefix = text[..m.Index];
        return Regex.IsMatch(prefix, @"\bfrom\b|~|≈|approx", RegexOptions.IgnoreCase, RegexTimeout) ? "from " + size : size;
    }

    /// <summary>"v1.0.5 (Build 123)" → "1.0.5"; keeps "Build 123" when that's all there is.</summary>
    public static string? NormalizeVersion(string text)
    {
        var value = CleanValue(text);
        if (value is null) return null;
        if (VersionRegex().Match(" " + value) is { Success: true } v) return v.Groups["v"].Value;
        if (BuildRegex().Match(value) is { Success: true } b) return Regex.Replace(b.Value, @"build\s*\.?\s*", "Build ", RegexOptions.IgnoreCase, RegexTimeout);
        var bare = Regex.Match(value, @"^\d+(?:\.\d+){0,5}[a-z]?\b", RegexOptions.IgnoreCase, RegexTimeout);
        if (bare.Success) return bare.Value;
        return value.Length <= 40 ? value : null;
    }

    /// <summary>"Yes" / "All" / "5 DLCs included" / "None" → "DLC included" / "All DLCs" / "5 DLCs" / null.</summary>
    public static string? NormalizeDlc(string text)
    {
        var value = (CleanValue(text) ?? string.Empty).ToLowerInvariant();
        if (value.Length == 0 || value is "no" or "none" or "n/a" or "-" or "not included") return null;
        if (value.StartsWith("yes", StringComparison.Ordinal) || value.Contains("included") && !Regex.IsMatch(value, @"\d"))
            return value.Contains("all") ? "All DLCs" : "DLC included";
        if (value.Contains("all")) return "All DLCs";
        var count = Regex.Match(value, @"\d+", RegexOptions.None, RegexTimeout);
        return count.Success ? DescribeDlcCount(count.Value) : "DLC included";
    }

    private static readonly Dictionary<string, string> LanguageCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["eng"] = "English", ["en"] = "English", ["rus"] = "Russian", ["ru"] = "Russian", ["fre"] = "French", ["fra"] = "French",
        ["fr"] = "French", ["ger"] = "German", ["deu"] = "German", ["de"] = "German", ["spa"] = "Spanish", ["esp"] = "Spanish",
        ["es"] = "Spanish", ["ita"] = "Italian", ["it"] = "Italian", ["jap"] = "Japanese", ["jpn"] = "Japanese", ["ja"] = "Japanese",
        ["chi"] = "Chinese", ["chn"] = "Chinese", ["zh"] = "Chinese", ["kor"] = "Korean", ["ko"] = "Korean", ["por"] = "Portuguese",
        ["pt"] = "Portuguese", ["br"] = "Portuguese (Brazil)", ["pol"] = "Polish", ["pl"] = "Polish", ["tur"] = "Turkish",
        ["tr"] = "Turkish", ["ukr"] = "Ukrainian", ["ua"] = "Ukrainian", ["cze"] = "Czech", ["cz"] = "Czech", ["dut"] = "Dutch",
        ["nl"] = "Dutch", ["ara"] = "Arabic", ["ar"] = "Arabic", ["swe"] = "Swedish", ["hun"] = "Hungarian", ["tha"] = "Thai",
    };

    /// <summary>"ENG/MULTI10" → ["English", "10 languages"]; "English, French, German" → the three names.</summary>
    public static List<string> ParseLanguages(string text)
    {
        var result = new List<string>();
        foreach (var token in Regex.Split(CleanValue(text) ?? string.Empty, @"\s*(?:[,/;|&+]|\band\b)\s*", RegexOptions.IgnoreCase, RegexTimeout))
        {
            var t = token.Trim().TrimEnd('.', '*');
            if (t.Length == 0) continue;

            if (MultiLanguageRegex().Match(t) is { Success: true } m) result.Add($"{m.Groups["n"].Value} languages");
            else if (LanguageCodes.TryGetValue(t, out var name)) result.Add(name);
            else if (t.Length is > 2 and < 30 && char.IsLetter(t[0]) && !t.Contains(':')) result.Add(CultureInfo.InvariantCulture.TextInfo.ToTitleCase(t.ToLowerInvariant()));
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToList();
    }

    private static readonly string[] DateFormats =
    [
        "d MMMM yyyy", "d MMM yyyy", "MMMM d, yyyy", "MMM d, yyyy", "MMMM d yyyy", "MMM d yyyy", "d MMMM, yyyy",
        "yyyy-MM-dd", "yyyy/MM/dd", "dd.MM.yyyy", "dd/MM/yyyy", "d.M.yyyy", "MMMM yyyy", "MMM yyyy", "yyyy",
        "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ssK", "yyyy-MM-ddTHH:mm:ss.fffK",
    ];

    /// <summary>Parses the many ways release dates are written ("12th March 2024", "Mar 12, 2024", "2024-03-12", "2024").</summary>
    public static DateTime? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = Collapse(text);
        value = Regex.Replace(value, @"(\d)(st|nd|rd|th)\b", "$1", RegexOptions.IgnoreCase, RegexTimeout); // 12th → 12
        value = Regex.Replace(value, @"^(on|released|release)\s+", string.Empty, RegexOptions.IgnoreCase, RegexTimeout);

        // Take just the date-looking prefix ("12 March 2024 (Steam)" → "12 March 2024").
        var dateLike = Regex.Match(value,
            @"\d{4}-\d{2}-\d{2}(?:T[\d:.]+(?:Z|[+-]\d{2}:?\d{2})?)?|\d{1,2}[./]\d{1,2}[./]\d{4}|" +
            @"\d{1,2}\s+[A-Za-z]{3,9},?\s+\d{4}|[A-Za-z]{3,9}\.?\s+\d{1,2},?\s+\d{4}|[A-Za-z]{3,9}\s+\d{4}|\b(?:19|20)\d{2}\b",
            RegexOptions.None, RegexTimeout);
        if (!dateLike.Success) return null;

        var candidate = dateLike.Value;
        if (Regex.IsMatch(candidate, @"^[A-Za-z]{3,9}\.", RegexOptions.None, RegexTimeout))
            candidate = candidate.Replace(".", string.Empty); // "Mar. 12, 2024" → "Mar 12, 2024"

        if (DateTime.TryParseExact(candidate, DateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var exact))
            return DateTime.SpecifyKind(exact, DateTimeKind.Utc);

        if (DateTime.TryParse(candidate, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var loose))
            return DateTime.SpecifyKind(loose, DateTimeKind.Utc);

        return null;
    }

    /// <summary>Post date from meta tags / &lt;time&gt; / JSON-LD BlogPosting (WordPress and most CMSs).</summary>
    public static DateTime? FindUploadDate(IDocument document)
    {
        var candidates = new[]
        {
            document.QuerySelector("meta[property='article:published_time']")?.GetAttribute("content"),
            document.QuerySelector("meta[itemprop='datePublished']")?.GetAttribute("content"),
            document.QuerySelector("article time[datetime], .entry-date[datetime], time.published[datetime], time[datetime]")
                ?.GetAttribute("datetime"),
            Regex.Match(string.Join(" ", document.QuerySelectorAll("script[type='application/ld+json']").Select(s => s.TextContent)),
                "\"datePublished\"\\s*:\\s*\"(?<d>[^\"]+)\"", RegexOptions.None, RegexTimeout).Groups["d"].Value,
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (DateTimeOffset.TryParse(candidate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                return parsed.UtcDateTime;
        }
        return null;
    }

    /// <summary>Splits "Action, Adventure / RPG; Indie" into items.</summary>
    public static List<string> SplitList(string text) =>
        Regex.Split(CleanValue(text) ?? string.Empty, @"\s*[,;/|•]\s*", RegexOptions.None, RegexTimeout)
            .Select(t => t.Trim().TrimEnd('.'))
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string? CleanValue(string text)
    {
        var value = Collapse(WebUtility.HtmlDecode(text)).Trim(' ', '-', '–', ':', '|', '*');
        return value.Length == 0 ? null : value;
    }

    // =============================================================================================
    // Description
    // =============================================================================================

    [GeneratedRegex(@"^(?:game\s+description|description|about\s+(?:the|this)\s+game|game\s+overview|overview|synopsis|story|plot)\s*:?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex DescriptionHeadingRegex();

    [GeneratedRegex(@"\b(?:download|torrent|magnet|mirror|filehoster|hosters?|repack\s+features|installation|install\s+notes?|" +
                    @"crack(?:ed)?|password|selective\s+download|md5|\.rar|\.zip|\.7z|seed(?:ing|ers)?|leech|" +
                    @"system\s+requirements|minimum\s*:|recommended\s*:|share\s+this|related\s+posts?|leave\s+a\s+(?:reply|comment))\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex NonDescriptionRegex();

    private static readonly string[] SectionHeadingTags = ["h1", "h2", "h3", "h4", "h5", "h6"];

    private static string ExtractDescription(IElement root, IReadOnlyList<string> lines)
    {
        // 1. An explicit "Game Description" section (spoiler boxes, headings, bold labels).
        foreach (var heading in root.QuerySelectorAll("h1, h2, h3, h4, h5, h6, strong, b, .su-spoiler-title, summary, dt"))
        {
            if (!DescriptionHeadingRegex().IsMatch(Collapse(heading.TextContent))) continue;

            var section = CollectSection(heading);
            if (section.Length >= 80) return section;
        }

        // 2. Otherwise the prose lines: long, sentence-like, not info lines / links / install notes.
        var prose = lines
            .Where(l => l.Length >= 80 && l.Count(c => c == '.') + l.Count(c => c == '!') >= 1)
            .Where(l => !LabelRegex.IsMatch(l) && !NonDescriptionRegex().IsMatch(l) && !l.Contains("http", StringComparison.OrdinalIgnoreCase))
            .Take(6);
        return string.Join("\n\n", prose).Trim();
    }

    /// <summary>Text of a section: the spoiler body / dd after the heading, or following siblings up to the next heading.</summary>
    private static string CollectSection(IElement heading)
    {
        // Heading wrapped in <p><strong>…</strong></p>: walk from the block container instead.
        var anchor = heading.LocalName is "strong" or "b" && heading.ParentElement is { LocalName: "p" or "div" } parent &&
                     Collapse(parent.TextContent).Length <= Collapse(heading.TextContent).Length + 2
            ? parent
            : heading;

        var paragraphs = new List<string>();
        for (var sibling = anchor.NextElementSibling; sibling is not null; sibling = sibling.NextElementSibling)
        {
            if (SectionHeadingTags.Contains(sibling.LocalName) || sibling.ClassList.Contains("su-spoiler-title")) break;

            // A short paragraph that is just a bold label ("Repack Features") starts the next section.
            var siblingText = Collapse(sibling.TextContent);
            var isBoldLabel = sibling.QuerySelector("strong, b") is { } bold && Collapse(bold.TextContent).Length >= siblingText.Length - 2;
            if (paragraphs.Count > 0 && isBoldLabel && siblingText.Length < 60) break;

            foreach (var line in ToLines(sibling))
            {
                if (LabelRegex.IsMatch(line) || NonDescriptionRegex().IsMatch(line)) continue;
                if (line.Length >= 30) paragraphs.Add(line);
            }
            if (paragraphs.Sum(p => p.Length) > 4000) break;
        }
        return string.Join("\n\n", paragraphs).Trim();
    }

    // =============================================================================================
    // HTML → text lines
    // =============================================================================================

    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "li", "ul", "ol", "h1", "h2", "h3", "h4", "h5", "h6", "section", "article", "blockquote",
        "dd", "dt", "table", "tbody", "thead", "pre", "header", "footer", "figure", "figcaption", "details", "summary",
    };

    private static readonly HashSet<string> SkipTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "iframe", "svg", "button", "form", "nav", "select", "textarea", "template",
    };

    /// <summary>
    /// Flattens HTML into trimmed text lines: block elements and &lt;br&gt; break lines, two-cell table rows become
    /// "label: value", &lt;dt&gt;/&lt;dd&gt; pairs likewise.
    /// </summary>
    public static IReadOnlyList<string> ToLines(IElement root)
    {
        var sb = new StringBuilder();
        Walk(root, sb);
        return sb.ToString()
            .Split('\n')
            .Select(Collapse)
            .Where(l => l.Length > 0)
            .ToList();
    }

    private static void Walk(INode node, StringBuilder sb)
    {
        foreach (var child in node.ChildNodes)
        {
            switch (child)
            {
                case IText text:
                    sb.Append(text.Data);
                    break;

                case IElement element when SkipTags.Contains(element.LocalName):
                    break;

                case IElement { LocalName: "br" or "hr" }:
                    sb.Append('\n');
                    break;

                case IElement { LocalName: "tr" } row:
                    var cells = row.Children.Where(c => c.LocalName is "td" or "th").Select(c => Collapse(c.TextContent)).ToList();
                    sb.Append('\n')
                      .Append(cells.Count == 2 ? $"{cells[0].TrimEnd(':').Trim()}: {cells[1]}" : string.Join(" | ", cells))
                      .Append('\n');
                    break;

                case IElement { LocalName: "dt" } term:
                    sb.Append('\n').Append(Collapse(term.TextContent).TrimEnd(':')).Append(": ");
                    break;

                case IElement { LocalName: "dd" } definition:
                    Walk(definition, sb);
                    sb.Append('\n');
                    break;

                case IElement element when BlockTags.Contains(element.LocalName):
                    sb.Append('\n');
                    Walk(element, sb);
                    sb.Append('\n');
                    break;

                case IElement element:
                    Walk(element, sb); // inline: strong, a, span, em…
                    break;
            }
        }
    }

    private static string Collapse(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : string.Join(' ', text.Replace(' ', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
