using System.Text.RegularExpressions;
using AngleSharp.Dom;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Scraping;

/// <summary>One "Part N" entry in a provider's line: its number, its text, and its address (null when it isn't a usable link).</summary>
/// <param name="Number">1-based part number (0 only while unnumbered; the parser numbers or rejects those).</param>
/// <param name="Text">What the page prints ("Part 3").</param>
/// <param name="Href">Absolute address of a working link; null when there is none.</param>
/// <param name="Problem">Why the part can't be downloaded ("plain text, not a link", "link has no href" …); null when it can.</param>
public sealed record PartSlot(int Number, string Text, string? Href, string? Problem)
{
    public bool IsDownloadable => Problem is null && Href is not null;
}

/// <summary>
/// One provider's line of a game page ("Link Gofile: Part 1 – Part 2 – Part 3"): every part it lists, and whether the set is
/// whole. A set with <see cref="RejectReason"/> is never offered or planned.
/// </summary>
public sealed record ProviderSet(string Provider, IReadOnlyList<PartSlot> Slots, string? RejectReason, IReadOnlyList<IElement> Anchors)
{
    public bool IsValid => RejectReason is null;
}

/// <summary>What <see cref="PartSetParser.Parse"/> found on a page.</summary>
public sealed class PartSetResult
{
    private readonly HashSet<IElement> _claimed;

    internal PartSetResult(IReadOnlyList<ProviderSet> sets, HashSet<IElement> claimed)
    {
        Sets = sets;
        _claimed = claimed;
    }

    public static PartSetResult Empty { get; } = new([], []);

    public IReadOnlyList<ProviderSet> Sets { get; }

    public IEnumerable<ProviderSet> Valid => Sets.Where(s => s.IsValid);

    /// <summary>
    /// True for every link inside a provider line, valid set or not. The general link scan must skip these: a rejected set's
    /// remaining links would otherwise come back as standalone files, which is the half-download this parser prevents.
    /// </summary>
    public bool Claims(IElement anchor) => _claimed.Contains(anchor);

    /// <summary>"Gofile: rejected - Part 2 (plain text, not a link)" for each rejected set, for the crawl / reader-test notes.</summary>
    public IEnumerable<string> Rejections => Sets.Where(s => !s.IsValid).Select(s => $"{s.Provider}: rejected, {s.RejectReason}");

    /// <summary>
    /// The download links of the valid sets, ready for <see cref="LinkClassifier.NormalizeGroups"/>. A provider with several
    /// parts becomes one multipart set (<see cref="DownloadLinkKind.MultipartArchivePart"/>, group "provider#parts",
    /// part numbers as printed); a provider with one link is a single file.
    /// </summary>
    public List<DownloadLink> ToDownloadLinks()
    {
        var links = new List<DownloadLink>();
        foreach (var set in Valid)
        {
            var isSet = set.Slots.Count > 1;
            foreach (var slot in set.Slots.OrderBy(s => s.Number))
            {
                if (!Uri.TryCreate(slot.Href, UriKind.Absolute, out var uri)) continue;
                if (LinkClassifier.Classify(uri, slot.Text, forceDownload: true) is not { } link) continue;

                link.Mirror = set.Provider;
                if (link.Kind is DownloadLinkKind.Magnet or DownloadLinkKind.TorrentFile)
                {
                    links.Add(link);
                    continue;
                }

                // A redirector's script name ("url-generator.php") or a bare id is not the file's name; keep real archive names.
                if (!PartSetParser.LooksLikeArchiveName(link.FileName)) link.FileName = string.Empty;

                if (MirrorLabels.IsTorrentLabel(set.Provider))
                {
                    link.LeadsToTorrent = true;
                    link.Label = set.Provider;
                }
                else if (isSet)
                {
                    link.Kind = DownloadLinkKind.MultipartArchivePart;
                    link.GroupKey = $"{set.Provider}#parts";
                    link.PartNumber = slot.Number;
                    link.Label = $"{set.Provider} part {slot.Number}";
                }
                else
                {
                    link.Label = set.Provider;
                }
                links.Add(link);
            }
        }
        return links;
    }
}

/// <summary>
/// Reads a game page whose downloads are one line per provider, each line a list of parts:
/// <code>
/// &lt;p&gt;&lt;b class="uk-heading-bullet"&gt;Link Gofile:&lt;/b&gt;&lt;br /&gt;
///    &lt;a href="…"&gt;Part 1&lt;/a&gt; &amp;#8211; &lt;a href="…"&gt;Part 2&lt;/a&gt; &amp;#8211; &lt;a href="…"&gt;Part 3&lt;/a&gt; &lt;/p&gt;
/// </code>
/// Everything after the label (up to the next label) belongs to that provider. A provider's parts are one set, and the set is
/// <b>all or nothing</b>: it is valid only when every part is a working link, numbered 1…N with none missing. A part that is
/// plain text, has no <c>&lt;a&gt;</c> or no usable <c>href</c>, is struck through or marked offline rejects the provider,
/// so SeaRaider never starts a download that can't finish. Providers that are blocked (<see cref="HostPolicy"/>) are rejected too.
///
/// <para>In CSS terms: <c>b.uk-heading-bullet</c> is the provider label, and <c>b.uk-heading-bullet ~ a[href]</c> are the
/// links of its line. The parser reads the siblings after the label rather than only <c>a[href]</c>, because the links alone
/// can't show a part that has no link.</para>
/// </summary>
public static partial class PartSetParser
{
    /// <summary>The provider label of the layout this was written for (a UIkit bullet heading).</summary>
    public const string DefaultLabelSelector = "b.uk-heading-bullet, strong.uk-heading-bullet";

    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "P", "DIV", "LI", "UL", "OL", "TR", "TD", "TH", "TABLE", "SECTION", "ARTICLE", "H1", "H2", "H3", "H4", "H5", "H6",
        "DD", "DT", "DL", "BLOCKQUOTE", "PRE", "CENTER", "FIGURE", "HEADER", "FOOTER", "FORM",
    };

    /// <summary>"Part 3", "Pt. 03", "Teil 3" in plain text. Bare numbers are not parts there ("2026", "5.2 GB").</summary>
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:part|pt|parte|teil)\.?\s*#?0*(?<n>\d{1,3})(?!\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PartTokenRegex();

    /// <summary>Words that say a part or a whole provider is gone. Whole words only, so "Download" doesn't match "down".</summary>
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:offline|off-line|down|dead|removed|deleted|broken|unavailable|expired|missing|n/a|not available|taken down)(?![\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OfflineRegex();

    /// <summary>"Link ", "Download Link ", "Mirror:" in front of a provider's name, and the colon after it.</summary>
    [GeneratedRegex(@"^\s*(?:download\s+)?(?:links?|mirrors?)\b\s*[:\-–—]?\s*|\s*[:：]\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProviderNoiseRegex();

    /// <summary>"(6 parts)", "6-part", "[12 Parts]" in the label: the page says how many there are.</summary>
    [GeneratedRegex(@"\s*[\(\[]?\s*(?<n>\d{1,3})\s*-?\s*parts?\s*[\)\]]?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeclaredTotalRegex();

    /// <summary>Finds every provider line on the page and judges each one.</summary>
    /// <param name="root">The parsed page (or a card of it).</param>
    /// <param name="labelSelector">CSS for the provider label; default <see cref="DefaultLabelSelector"/>.</param>
    public static PartSetResult Parse(IParentNode root, Uri pageUri, string? labelSelector = null)
    {
        var selector = string.IsNullOrWhiteSpace(labelSelector) ? DefaultLabelSelector : labelSelector.Trim();
        List<IElement> labels;
        try { labels = root.QuerySelectorAll(selector).ToList(); }
        catch (DomException) { return PartSetResult.Empty; } // a selector AngleSharp doesn't understand
        if (labels.Count == 0) return PartSetResult.Empty;

        var sets = new List<ProviderSet>();
        var claimed = new HashSet<IElement>();
        var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var label in labels)
        {
            var labelText = Collapse(label.TextContent);
            var declared = DeclaredTotalRegex().Match(labelText) is { Success: true } m && int.TryParse(m.Groups["n"].Value, out var n) ? n : (int?)null;
            var provider = Collapse(ProviderNoiseRegex().Replace(DeclaredTotalRegex().Replace(labelText, string.Empty), string.Empty));
            if (provider.Length == 0) continue;

            var (slots, anchors, notices) = ReadLine(label, selector, pageUri);
            foreach (var anchor in anchors) claimed.Add(anchor);

            // "Gofile" twice on one page are two alternatives, not one set.
            var seen = names.TryGetValue(provider, out var count) ? count + 1 : 1;
            names[provider] = seen;
            var name = seen > 1 ? $"{provider} ({seen})" : provider;

            // Unnumbered buttons ("Download", "Click Here"): a lone one is the whole download, several are numbered by position.
            if (slots.Count > 0 && slots.All(s => s.Number == 0))
                slots = slots.Select((s, i) => s with { Number = i + 1 }).ToList();

            sets.Add(new ProviderSet(name, slots, Judge(provider, slots, notices, declared), anchors));
        }
        return new PartSetResult(sets, claimed);
    }

    /// <summary>
    /// The all-or-nothing rule. Null when the provider's set is whole; otherwise why it isn't. Checked in order: a blocked host,
    /// no parts at all, a notice that something is offline, parts that aren't links, parts without a number, duplicate or missing
    /// numbers (1, 2, 4), and a part count that disagrees with the one the label states.
    /// </summary>
    internal static string? Judge(string provider, IReadOnlyList<PartSlot> slots, IReadOnlyList<string> notices, int? declaredTotal)
    {
        if (HostPolicy.IsBlockedName(provider) || slots.Any(s => HostPolicy.IsBlockedUrl(s.Href)))
            return "host needs a captcha on every file (blocked)";

        if (slots.Count == 0) return "no part is linked under this provider";

        if (notices.Count > 0) return $"the page marks it as {notices[0]}";

        var broken = slots.Where(s => s.Problem is not null).ToList();
        if (broken.Count > 0)
            return "not all parts are downloadable: " + string.Join("; ", broken.Select(s => $"{Describe(s)} ({s.Problem})"));

        if (slots.Any(s => s.Number <= 0))
            return $"can't tell which part \"{slots.First(s => s.Number <= 0).Text}\" is";

        var numbers = slots.Select(s => s.Number).OrderBy(x => x).ToList();
        if (numbers.Distinct().Count() != numbers.Count)
            return $"part {numbers.GroupBy(x => x).First(g => g.Count() > 1).Key} is listed more than once";

        var missing = Enumerable.Range(1, numbers[^1]).Except(numbers).ToList();
        if (missing.Count > 0) return "missing part " + string.Join(", ", missing);

        if (declaredTotal is { } total && total != numbers.Count)
            return $"the page says {total} parts but {numbers.Count} are listed";

        return null;
    }

    private static string Describe(PartSlot slot) => slot.Number > 0 ? $"Part {slot.Number}" : $"\"{slot.Text}\"";

    // =============================================================================================
    // Reading one line
    // =============================================================================================

    /// <summary>Everything after <paramref name="label"/>, up to the next label: its parts (links and plain-text parts alike), its links, and offline notices.</summary>
    private static (List<PartSlot> Slots, List<IElement> Anchors, List<string> Notices) ReadLine(IElement label, string selector, Uri pageUri)
    {
        var slots = new List<PartSlot>();
        var anchors = new List<IElement>();
        var notices = new List<string>();

        void AddText(string? text, bool struck)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            // "Part 1 – Part 2" typed without links: each is a part, and none of them can be downloaded.
            foreach (Match match in PartTokenRegex().Matches(text))
                slots.Add(new PartSlot(int.Parse(match.Groups["n"].Value), Collapse(match.Value), null, struck ? "struck through" : "plain text, not a link"));
            if (OfflineRegex().Match(text) is { Success: true } off) notices.Add($"\"{off.Value}\"");
        }

        void AddAnchor(IElement a, bool struck)
        {
            anchors.Add(a);
            var text = Collapse(a.TextContent);
            var number = LinkClassifier.PartFromLabel(text) ?? 0;
            var raw = a.GetAttribute("href");

            string? problem = null;
            string? href = null;
            if (struck) problem = "struck through";
            else if (string.IsNullOrWhiteSpace(raw)) problem = "link has no href";
            else if (IsDisabled(a)) problem = "link is disabled";
            else if (OfflineRegex().IsMatch(text)) problem = "marked offline";
            else if (raw.Trim().StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) href = raw.Trim();
            else if (GamePageParser.Resolve(raw, pageUri) is { } uri) href = uri.AbsoluteUri;
            else problem = $"unusable href \"{raw.Trim()}\"";

            slots.Add(new PartSlot(number, text, href, problem));
        }

        void Walk(INode node, bool struck)
        {
            switch (node)
            {
                case IText text:
                    AddText(text.Data, struck);
                    break;
                case IElement { LocalName: "a" } a:
                    AddAnchor(a, struck);
                    break;
                case IElement { LocalName: "br" or "wbr" or "hr" or "script" or "style" or "noscript" }:
                    break;
                case IElement element when element.QuerySelector("a") is not null:
                    foreach (var child in element.ChildNodes) Walk(child, struck || IsStruck(element));
                    break;
                case IElement element:
                    AddText(element.TextContent, struck || IsStruck(element));
                    break;
            }
        }

        // Start after the label (climbing out of inline wrappers like <span><b class=…>), stop at the next provider's label.
        INode start = label;
        while (start.NextSibling is null && start.ParentElement is { } up && !BlockTags.Contains(up.TagName)) start = up;
        for (var node = start.NextSibling; node is not null; node = node.NextSibling)
        {
            if (node is IElement e && (e.Matches(selector) || e.QuerySelector(selector) is not null)) break;
            Walk(node, struck: false);
        }
        return (slots, anchors, notices);
    }

    private static bool IsStruck(IElement e) =>
        e.LocalName is "del" or "s" or "strike" ||
        (e.GetAttribute("style") ?? string.Empty).Contains("line-through", StringComparison.OrdinalIgnoreCase);

    /// <summary>A link the page itself greys out: disabled / offline / dead classes, aria-disabled, or no pointer events.</summary>
    private static bool IsDisabled(IElement a)
    {
        var style = (a.GetAttribute("style") ?? string.Empty).Replace(" ", string.Empty);
        return a.GetAttribute("aria-disabled") == "true" || a.HasAttribute("disabled") ||
               style.Contains("pointer-events:none", StringComparison.OrdinalIgnoreCase) ||
               style.Contains("line-through", StringComparison.OrdinalIgnoreCase) ||
               a.ClassList.Any(c => c.Equals("disabled", StringComparison.OrdinalIgnoreCase) || c.Equals("offline", StringComparison.OrdinalIgnoreCase) ||
                                    c.Equals("dead", StringComparison.OrdinalIgnoreCase) || c.Equals("broken", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A real archive / installer name (game.part1.rar, setup.exe), as opposed to a script name or a bare id.</summary>
    internal static bool LooksLikeArchiveName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (LinkClassifier.TryParseVolume(name, out _, out _)) return true;
        return Path.GetExtension(name).ToLowerInvariant() is ".rar" or ".zip" or ".7z" or ".tar" or ".gz" or ".exe" or ".iso" or ".bin" or ".msi";
    }

    private static string Collapse(string? text) =>
        string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
