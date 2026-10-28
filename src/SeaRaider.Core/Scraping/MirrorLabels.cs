using System.Text.RegularExpressions;
using AngleSharp.Dom;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Scraping;

/// <summary>
/// Names the mirrors behind link-shortener buttons. Sites that send every download through a shortener (Zovo) print the
/// mirror's name next to an otherwise identical "Click Here": "Torrent: Click Here", "Gofile – Click Here",
/// a table row "Pixeldrain | Click Here", or a heading above the button. Without the name, six such buttons looked like
/// one download in six parts.
///
/// <para>The text next to each button is read; when the source has <see cref="ScraperProfile.MirrorLabels"/> ("Torrent,
/// Gofile, Pixeldrain"), the longest label found in that text names the mirror, otherwise the text itself does. A mirror
/// whose name says torrent or magnet is a torrent: its page leads to a magnet or .torrent, which SeaRaider's torrent
/// downloader takes over.</para>
/// </summary>
public static partial class MirrorLabels
{
    private const int MaxLabelLength = 40;

    [GeneratedRegex(@"\b(?:torrent|magnet)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TorrentWordRegex();

    // What to strip from the text around a button to leave a name: "click here", arrows, separators, "download".
    [GeneratedRegex(@"\b(?:click|tap|press)\s+here\b|\bdownload(?:\s+(?:link|from|via|here))?\b|\b(?:link|mirror|via|from)\b\s*$|[:|•»→>\-–—=~*\[\](){}]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoiseRegex();

    [GeneratedRegex(@"\b(?:part|pt|parte|teil)\.?\s*#?\d{1,3}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PartWordRegex();

    /// <summary>Words between two buttons that aren't a name: "or", "and", "/", "alternative", "mirror 2".</summary>
    [GeneratedRegex(@"^(?:or|and|also|alt|alternative(?:ly)?|backup|mirror\s*\d*|link\s*\d*|here|\d{1,2})$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConnectorRegex();

    private static bool IsConnector(string? text)
    {
        var cleaned = Clean(text);
        return cleaned.Length == 0 && Collapse(text).Length > 0 || ConnectorRegex().IsMatch(cleaned);
    }

    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "P", "DIV", "LI", "UL", "OL", "TR", "TD", "TH", "TABLE", "SECTION", "ARTICLE", "H1", "H2", "H3", "H4", "H5", "H6",
        "DD", "DT", "DL", "BLOCKQUOTE", "PRE", "CENTER", "FIGURE", "HEADER", "FOOTER", "FORM",
    };

    /// <summary>The labels a person typed for a source: one per line or separated by commas / semicolons.</summary>
    public static IReadOnlyList<string> Parse(string? text) =>
        (text ?? string.Empty)
            .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>True when a mirror's name or the text around its button says it is a torrent.</summary>
    public static bool IsTorrentLabel(string? text) => !string.IsNullOrWhiteSpace(text) && TorrentWordRegex().IsMatch(text);

    /// <summary>
    /// The text that names the link: on its own line before it ("Torrent: "), else after it ("(Torrent)"), else the
    /// short heading / table cell just before its block. Empty when there's nothing.
    /// </summary>
    public static string ContextOf(IElement anchor)
    {
        var block = anchor.ParentElement;
        while (block is not null && !BlockTags.Contains(block.TagName)) block = block.ParentElement;
        block ??= anchor.ParentElement;
        if (block is null) return string.Empty;

        var (before, after, previousName) = Segments(block, anchor);
        if (Usable(before) is { } b) return b;
        // "Gofile: Click Here or Click Here": the second button belongs to the same name as the first.
        if (IsConnector(before) && Usable(previousName) is { } p0) return p0;
        if (Usable(after) is { } a) return a;

        // "<td>Torrent</td><td><a>Click Here</a></td>" or "<h3>Torrent</h3><p><a>Click Here</a></p>".
        for (var element = block; element is not null && element.TagName is not ("BODY" or "ARTICLE" or "MAIN"); element = element.ParentElement)
        {
            if (element.PreviousElementSibling is { } previous && Usable(previous.TextContent) is { } p && p.Length <= 60 &&
                previous.QuerySelector("a[href]") is null)
                return p;
            if (element.PreviousElementSibling is not null) break;
        }
        return string.Empty;

        static string? Usable(string? text)
        {
            var collapsed = Collapse(text);
            var cleaned = Clean(collapsed);
            return cleaned.Length > 0 && !ConnectorRegex().IsMatch(cleaned) ? collapsed : null;
        }
    }

    /// <summary>
    /// Names a shortener link from the text around it: <see cref="DownloadLink.Mirror"/> and <see cref="DownloadLink.Label"/>
    /// become the matched label (or the cleaned text), <see cref="DownloadLink.LeadsToTorrent"/> is set for torrents, and a
    /// part number in the text ("Part 2") makes it a part of that mirror's set.
    /// </summary>
    public static void Apply(DownloadLink link, string context, IReadOnlyList<string> labels)
    {
        var matched = labels
            .Where(l => Contains(context, l))
            .OrderByDescending(l => l.Length)
            .FirstOrDefault();
        var name = matched ?? Clean(PartWordRegex().Replace(context, " "));
        if (name.Length > 0)
        {
            link.Mirror = name;
            link.Label = name;
        }
        link.LeadsToTorrent = IsTorrentLabel(matched ?? context);

        if (!link.LeadsToTorrent && PartOf(context) is { } part)
        {
            link.Kind = DownloadLinkKind.MultipartArchivePart;
            link.GroupKey = $"{link.Mirror}#parts";
            link.PartNumber = part;
            link.Label = $"{link.Mirror} part {part}";
        }
    }

    /// <summary>
    /// Every shortener mirror is its own download option unless its text numbered it as a part: two buttons with the same
    /// (or no) name become "Gofile" and "Gofile (2)", never one download in two files.
    /// </summary>
    public static void MakeDistinct(IEnumerable<DownloadLink> shortenerLinks)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in shortenerLinks.Where(l => l.Kind != DownloadLinkKind.MultipartArchivePart))
        {
            var name = string.IsNullOrWhiteSpace(link.Mirror) ? "Mirror" : link.Mirror;
            var count = seen.TryGetValue(name, out var n) ? n + 1 : 1;
            seen[name] = count;
            if (count > 1)
            {
                link.Mirror = $"{name} ({count})";
                link.Label = link.Mirror;
            }
        }
    }

    /// <summary>"Part 2", "Pt. 3" in the text around a button.</summary>
    internal static int? PartOf(string? context)
    {
        var m = PartWordRegex().Match(context ?? string.Empty);
        if (!m.Success) return null;
        var digits = new string(m.Value.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) && n > 0 ? n : null;
    }

    /// <summary>The name left after dropping "click here", "download", separators and arrows (at most 40 characters).</summary>
    internal static string Clean(string? text)
    {
        var cleaned = Collapse(NoiseRegex().Replace(text ?? string.Empty, " ")).Trim(' ', '.', ',', ';', '/', '\\', '#', '"', '\'');
        if (cleaned.Length <= MaxLabelLength) return cleaned;
        // A long sentence: its last words are the ones next to the button.
        var tail = cleaned[^MaxLabelLength..];
        var space = tail.IndexOf(' ');
        return (space > 0 ? tail[(space + 1)..] : tail).Trim();
    }

    private static bool Contains(string text, string label) =>
        Regex.IsMatch(text, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(label)}(?![\p{{L}}\p{{N}}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static string Collapse(string? text) =>
        string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// The text on the anchor's own line inside <paramref name="block"/>: the last non-empty line before it (lines end at
    /// line breaks and blocks; another link starts over, since the text before that link is its own name) and what follows
    /// it up to the next line break, block or link.
    /// </summary>
    private static (string Before, string After, string Previous) Segments(IElement block, IElement anchor)
    {
        var before = new System.Text.StringBuilder();
        var previous = string.Empty; // the name before the last other button on the way (for "… or Click Here")
        var after = new System.Text.StringBuilder();
        var passed = false;
        var done = false;

        void LineEnd()
        {
            if (passed) done = true;
            else before.Append('\n');
        }

        void Walk(INode node)
        {
            foreach (var child in node.ChildNodes)
            {
                if (done) return;
                switch (child)
                {
                    case IText text:
                        (passed ? after : before).Append(text.Data);
                        break;
                    case IElement element when ReferenceEquals(element, anchor):
                        passed = true;
                        break;
                    case IElement element when element.TagName is "BR" or "HR":
                        LineEnd();
                        break;
                    case IElement element when element.TagName == "A" && element.HasAttribute("href"):
                        if (passed) done = true;
                        else
                        {
                            // Another button: the text before it was its name. Kept, unless it was just "or".
                            var name = LastLine(before.ToString());
                            if (name.Length > 0 && !ConnectorRegex().IsMatch(Clean(name))) previous = name;
                            before.Clear();
                        }
                        break;
                    case IElement element when element.TagName is "SCRIPT" or "STYLE" or "NOSCRIPT":
                        break;
                    case IElement element when BlockTags.Contains(element.TagName):
                        if (element.Contains(anchor))
                        {
                            LineEnd();
                            Walk(element);
                        }
                        else
                        {
                            LineEnd();
                            if (done) return;
                            Walk(element);
                            LineEnd();
                        }
                        break;
                    case IElement element:
                        Walk(element);
                        break;
                }
            }
        }

        Walk(block);
        return (LastLine(before.ToString()), after.ToString(), previous);

        static string LastLine(string text) =>
            text.Split('\n').Select(Collapse).LastOrDefault(l => Clean(l).Length > 0) ?? string.Empty;
    }
}
