using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SeaRaider.App.Services;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Models;
using SeaRaider.Core.Scraping;

namespace SeaRaider.App.ViewModels;

/// <summary>
/// Sources › Site readers: the dedicated readers in the readers folders (JSON files of CSS selectors), a way to start
/// one for a site, and a test that shows what a reader finds on a page before it's used for real.
/// </summary>
public sealed partial class SiteReadersViewModel(SiteReaderCatalog catalog, SiteReaderTester tester, ShellService shell,
    SiteReaderAutoBuilder builder, IIngestionService ingestion, ILibraryRepository repository) : ViewModelBase
{
    // ---- a reader from just an address -----------------------------------------------------------

    /// <summary>Any page on the site: its home page, its list of games, or one game's page.</summary>
    [ObservableProperty] private string _autoAddress = string.Empty;

    /// <summary>Also add the site to the sources (so its games come into the store).</summary>
    [ObservableProperty] private bool _autoAddSource = true;

    [ObservableProperty] private bool _isAutoBuilding;

    /// <summary>What it's doing right now ("Opening a game from the list: …").</summary>
    [ObservableProperty] private string? _autoStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAutoLesson))]
    private SiteReaderLessonViewModel? _autoLesson;

    public bool HasAutoLesson => AutoLesson is not null;

    /// <summary>A site was added to the sources from here (the Sources list shows it).</summary>
    public event EventHandler<string>? SourceAdded;

    private CancellationTokenSource? _autoCancel;

    [RelayCommand]
    private Task MakeReaderAsync() => RunSafeAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(AutoAddress)) throw new ArgumentException("Type the site's address, like example.com.");
        AutoLesson = null;
        AutoLesson = await TeachSiteAsync(AutoAddress, hints: null);
        AutoAddress = string.Empty;
    });

    /// <summary>
    /// The whole job from an address (Make reader, and the teacher when it's given an address instead of HTML): reads the site,
    /// finds its list and a game's page, the page addresses, search and the downloads (behind the Download button too), saves
    /// the reader, tries it, adds the site to the sources; then teaches it again with anything the user pointed out.
    /// </summary>
    private async Task<SiteReaderLessonViewModel> TeachSiteAsync(string address, LearnHints? hints)
    {
        _autoCancel?.Cancel();
        var cancel = _autoCancel = new CancellationTokenSource();
        var ct = cancel.Token;
        IsAutoBuilding = true;
        TestResult = null;
        var progress = new Progress<string>(line => AutoStatus = line);
        try
        {
            var result = await Task.Run(() => builder.BuildAsync(address, progress, ct), ct);
            var notes = result.Log.ToList();

            // What the user pointed out (the title is here, the download is that button, page 2 is …) wins.
            if (hints is not null)
            {
                var pageHints = hints with { PageTwoAddress = null, Pointers = Only(hints.Pointers, LearnHints.PageFields) };
                var listHints = new LearnHints { PageTwoAddress = hints.PageTwoAddress, Pointers = Only(hints.Pointers, LearnHints.ListFields) };
                if ((pageHints.HasPagePointers || pageHints.HasDownloadHints) && result.GamePage is { } gamePage)
                {
                    AutoStatus = "Teaching it what you pointed out on the game's page …";
                    var lesson = await Task.Run(() => SiteReaderTeacher.LearnAsync(gamePage.Html, gamePage.Url.AbsoluteUri, pageHints, ct), ct);
                    lesson.Reader.Listing = null;
                    catalog.SaveLesson(lesson);
                    notes.Add("Taught the game page again with what you pointed out.");
                    notes.AddRange(lesson.Notes.Where(n => !n.StartsWith("Learned", StringComparison.Ordinal)));
                }
                if ((listHints.HasListPointers || listHints.PageTwoAddress is not null) && result.ListPage is { } listPage)
                {
                    AutoStatus = "Teaching it what you pointed out on the list …";
                    var lesson = await Task.Run(() => SiteReaderTeacher.LearnAsync(listPage.Html, listPage.Url.AbsoluteUri, listHints, ct), ct);
                    lesson.Reader.Page = null;
                    catalog.SaveLesson(lesson);
                    notes.Add("Taught the list again with what you pointed out.");
                }
            }
            Load();

            // Try it straight away, on the game's page it learned (or the list).
            TestUrl = result.TestUrl;
            AutoStatus = "Trying the new reader …";
            try
            {
                TestResult = new SiteReaderTestViewModel(await Task.Run(() => tester.TestAsync(result.TestUrl, ct), ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                notes.Add($"Couldn't try it here ({ex.Message}). Press Test below to try again.");
            }

            if (AutoAddSource) notes.Add(await AddAsSourceAsync(result));
            return new SiteReaderLessonViewModel(result, notes);
        }
        finally
        {
            IsAutoBuilding = false;
            AutoStatus = null;
        }
    }

    /// <summary>One web address, not a page's HTML.</summary>
    private static bool LooksLikeAddress(string? text)
    {
        var t = (text ?? string.Empty).Trim();
        return t.Length is > 3 and < 2000 && !t.Contains('<') && !t.Any(char.IsWhiteSpace) && t.Contains('.');
    }

    private static IReadOnlyDictionary<string, string> Only(IReadOnlyDictionary<string, string> pointers, string[] keys) =>
        pointers.Where(p => keys.Contains(p.Key, StringComparer.OrdinalIgnoreCase)).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

    [RelayCommand]
    private void CancelMakeReader() => _autoCancel?.Cancel();

    private async Task<string> AddAsSourceAsync(AutoReaderResult result)
    {
        var site = new Uri(result.TestUrl);
        var host = site.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? site.Host[4..] : site.Host;
        var already = repository.GetSources().Any(s => Uri.TryCreate(s.Url, UriKind.Absolute, out var u) &&
            (u.Host.Equals(host, StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase)));
        if (already) return $"{host} is already in your sources: it reads with the new reader from now on.";
        try
        {
            var source = await ingestion.AddSourceAsync($"{site.Scheme}://{site.Host}/", new GameSource());
            SourceAdded?.Invoke(this, source.DisplayName);
            return $"Added {source.DisplayName} to your sources: its games come into the store.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return $"Couldn't add it to your sources: {ex.Message}";
        }
    }

    public ObservableCollection<SiteReaderRowViewModel> Readers { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    private string? _problems;

    public bool HasProblems => !string.IsNullOrEmpty(Problems);
    public bool HasNoReaders => Readers.Count == 0;

    /// <summary>The site a new reader is for ("example.com").</summary>
    [ObservableProperty] private string _newDomain = string.Empty;

    /// <summary>A page to test a reader on.</summary>
    [ObservableProperty] private string _testUrl = string.Empty;

    [ObservableProperty] private bool _isTesting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestResult))]
    private SiteReaderTestViewModel? _testResult;

    public bool HasTestResult => TestResult is not null;

    /// <summary>Optional: the pasted page's address, or just the site's domain (when the page's HTML doesn't say).</summary>
    [ObservableProperty] private string _learnAddress = string.Empty;

    /// <summary>A page's HTML (browser: Ctrl+U, select all, copy), to teach a reader from.</summary>
    [ObservableProperty] private string _learnHtml = string.Empty;

    /// <summary>Optional: links that are the right downloads, one per line (an address, or the words on the button).</summary>
    [ObservableProperty] private string _learnDownloadExamples = string.Empty;

    /// <summary>Optional: an exact CSS selector for the download links.</summary>
    [ObservableProperty] private string _learnDownloadSelector = string.Empty;

    /// <summary>Optional: the download button's code, copied from View source (Ctrl+U).</summary>
    [ObservableProperty] private string _learnDownloadSnippet = string.Empty;

    /// <summary>Optional: the list's second page ("example.com/demo/page/2/"): every page is then read by its number.</summary>
    [ObservableProperty] private string _learnPageTwo = string.Empty;

    /// <summary>Where things are on a game's page, as the user shows them (words, View-source code, or CSS).</summary>
    public IReadOnlyList<LearnPointerViewModel> PagePointers { get; } =
    [
        new("title", "Title", "The game's name as the page shows it"),
        new("cover", "Cover picture", "Its address, or its <img …> code"),
        new("description", "Description", "A few words from the description"),
        new("developer", "Developer", "e.g. Ubisoft"),
        new("publisher", "Publisher", string.Empty),
        new("version", "Version", "e.g. v1.0.5"),
        new("size", "Size", "e.g. 12.4 GB"),
        new("releaseDate", "Release date", "e.g. March 3, 2024"),
        new("updated", "Upload / updated date", string.Empty),
        new("genres", "Genres", "e.g. Action"),
    ];

    /// <summary>Where things are on a list of games.</summary>
    public IReadOnlyList<LearnPointerViewModel> ListPointers { get; } =
    [
        new("listGame", "One game's name in the list", "As the list shows it"),
        new("nextPage", "Next page button", "Its words (\"Next\", \"»\"), or its code"),
    ];

    /// <summary>The "wrong download links?" section is open.</summary>
    [ObservableProperty] private bool _learnHintsOpen;

    [ObservableProperty] private bool _isLearning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLesson))]
    private SiteReaderLessonViewModel? _lesson;

    public bool HasLesson => Lesson is not null;

    public void Load()
    {
        Readers.Clear();
        foreach (var reader in catalog.Readers.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase))
            Readers.Add(new SiteReaderRowViewModel(this, reader));
        Problems = catalog.Problems.Count == 0 ? null : string.Join(Environment.NewLine, catalog.Problems);
        OnPropertyChanged(nameof(HasNoReaders));
    }

    [RelayCommand]
    private void Reload() => RunSafe(() =>
    {
        catalog.Reload();
        Load();
    });

    [RelayCommand]
    private void OpenFolder() => RunSafe(() => shell.OpenFolder(SiteReaderCatalog.EnsureUserFolder()));

    [RelayCommand]
    private void NewReader() => RunSafe(() =>
    {
        var text = NewDomain.Trim();
        if (text.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(text, UriKind.Absolute, out var uri)) text = uri.Host;
        if (text.Length < 3 || !text.Contains('.'))
            throw new ArgumentException("Type the site's domain, like example.com, or paste any address on it.");
        var path = SiteReaderCatalog.CreateReaderFile(text);
        shell.OpenFile(path); // opens in the user's text editor
        NewDomain = string.Empty;
        catalog.Reload();
        Load();
    });

    internal void OpenFile(string path) => RunSafe(() => shell.OpenFile(path));

    /// <summary>A reader's "download address" setting (what the site's Download button adds to the page's address).</summary>
    internal void SaveDownloadUrl(SiteReader reader, string? value, bool byButton) => RunSafe(() =>
    {
        var text = string.IsNullOrWhiteSpace(value) || byButton ? null : value.Trim();
        if (text is not null && SiteReaderParser.DownloadFromPage(text, new Uri("https://example.com/roms/some-game-123")) is null)
            throw new ArgumentException("The download address should be what's added to a game page's address, like /download, or a pattern like {url}/download.");
        if (text is null && !byButton && reader.Page is null) return;
        reader.Page ??= new PageRecipe();
        reader.Page.DownloadUrl = text;
        reader.Page.DownloadByButton = byButton ? true : null;
        var path = catalog.SaveReader(reader);
        Load();
        TestResult = null;
        SavedNote = byButton
            ? $"{reader.Name}: downloads now start from each game page's Download button, in SeaRaider's download browser ({Path.GetFileName(path)})."
            : text is null
            ? $"{reader.Name}: download address cleared ({Path.GetFileName(path)})."
            : $"{reader.Name}: downloads now come from the game page's address + \"{text}\" ({Path.GetFileName(path)}). Test a game's page below to check.";
    });

    /// <summary>Said after a reader's setting was saved.</summary>
    [ObservableProperty] private string? _savedNote;

    [RelayCommand]
    private Task LearnAsync() => RunSafeAsync(async () =>
    {
        var html = LearnHtml;
        var address = LearnAddress;
        var hints = new LearnHints
        {
            DownloadExamples = LearnHints.SplitExamples(LearnDownloadExamples),
            DownloadSelector = string.IsNullOrWhiteSpace(LearnDownloadSelector) ? null : LearnDownloadSelector.Trim(),
            DownloadSnippet = string.IsNullOrWhiteSpace(LearnDownloadSnippet) ? null : LearnDownloadSnippet.Trim(),
            PageTwoAddress = string.IsNullOrWhiteSpace(LearnPageTwo) ? null : LearnPageTwo.Trim(),
            Pointers = PagePointers.Concat(ListPointers)
                .Where(p => !string.IsNullOrWhiteSpace(p.Value))
                .ToDictionary(p => p.Key, p => p.Value.Trim(), StringComparer.OrdinalIgnoreCase),
        };
        // An address and no HTML (or an address pasted into the HTML box): the whole site, by itself.
        if (LooksLikeAddress(html))
        {
            address = html.Trim();
            html = string.Empty;
        }
        if (string.IsNullOrWhiteSpace(html) && !string.IsNullOrWhiteSpace(address))
        {
            IsLearning = true;
            Lesson = null;
            try
            {
                Lesson = await TeachSiteAsync(address, hints);
                LearnHtml = string.Empty;
            }
            finally
            {
                IsLearning = false;
            }
            return;
        }
        IsLearning = true;
        Lesson = null;
        try
        {
            var lesson = await Task.Run(() => SiteReaderTeacher.LearnAsync(html, address, hints));
            var path = catalog.SaveLesson(lesson);
            Load();
            Lesson = new SiteReaderLessonViewModel(lesson, path);
            if (lesson.FoundPage) LearnHintsOpen = true; // the place to fix the download links, if they came out wrong
            if (lesson.Reader.Domains.Count > 0 && string.IsNullOrWhiteSpace(TestUrl)) TestUrl = "https://" + lesson.Reader.Domains[0] + "/";
        }
        finally
        {
            IsLearning = false;
        }
    });

    [RelayCommand]
    private void ClearLearn()
    {
        LearnHtml = string.Empty;
        LearnDownloadExamples = string.Empty;
        LearnDownloadSelector = string.Empty;
        LearnDownloadSnippet = string.Empty;
        LearnPageTwo = string.Empty;
        foreach (var pointer in PagePointers.Concat(ListPointers)) pointer.Value = string.Empty;
        Lesson = null;
    }

    [RelayCommand]
    private Task TestAsync() => RunSafeAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(TestUrl)) throw new ArgumentException("Paste a game's page (or the site's list of games) to test.");
        IsTesting = true;
        TestResult = null;
        try
        {
            var url = TestUrl;
            var result = await Task.Run(() => tester.TestAsync(url));
            TestResult = new SiteReaderTestViewModel(result);
            Load();
        }
        finally
        {
            IsTesting = false;
        }
    });
}

/// <summary>One "it's here" box: a field and what the user typed to show where it is.</summary>
public sealed partial class LearnPointerViewModel(string key, string label, string example) : ObservableObject
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    public string Watermark { get; } = example.Length > 0 ? example : "Words as the page shows them, its code, or a CSS selector";

    [ObservableProperty] private string _value = string.Empty;
}

/// <summary>A reader in the list.</summary>
public sealed partial class SiteReaderRowViewModel(SiteReadersViewModel owner, SiteReader reader) : ObservableObject
{
    public string Name => reader.Name;
    public string Domains => string.Join(", ", reader.Domains);
    public string FileName => Path.GetFileName(reader.FilePath);

    /// <summary>"Lists and game pages", "Game pages only" …</summary>
    public string Covers => (reader.Listing is not null && (reader.BrowseUrl ?? reader.SearchUrl) is not null, reader.Page is not null) switch
    {
        (true, true) => "Lists, search and game pages",
        (true, false) => "Lists and search",
        (false, true) => "Game pages",
        _ => "Nothing yet: fill in \"listing\" or \"page\"",
    };

    [RelayCommand]
    private void Edit() => owner.OpenFile(reader.FilePath);

    // ---- download address setting ----------------------------------------------------------------

    /// <summary>The settings under the reader are open.</summary>
    [ObservableProperty] private bool _isSettingsOpen;

    /// <summary>What the site's Download button adds to a game page's address ("/download"), or a pattern.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadUrlExample))]
    private string _downloadUrl = reader.Page?.DownloadUrl ?? string.Empty;

    /// <summary>"https://site/some-game → https://site/some-game/download", for the site's own address.</summary>
    public string DownloadUrlExample
    {
        get
        {
            var site = reader.Domains.FirstOrDefault() ?? "example.com";
            var page = new Uri($"https://{site}/roms/some-game-123");
            if (string.IsNullOrWhiteSpace(DownloadUrl)) return "Empty: the download buttons on the page are used as they are.";
            return SiteReaderParser.DownloadFromPage(DownloadUrl, page) is { } download
                ? $"{page.AbsoluteUri}  →  {download.AbsoluteUri}"
                : "That doesn't make an address. Type what's added (/download) or a pattern with {url}, {slug} or {id}.";
        }
    }

    /// <summary>Downloads start from the game page's Download button (a session check, a countdown): done in the download browser.</summary>
    [ObservableProperty] private bool _downloadByButton = reader.Page?.DownloadByButton == true;

    [RelayCommand]
    private void SaveSettings() => owner.SaveDownloadUrl(reader, DownloadUrl, DownloadByButton);
}

/// <summary>What the test found, laid out for reading.</summary>
public sealed class SiteReaderTestViewModel
{
    public SiteReaderTestViewModel(SiteReaderTestResult result)
    {
        Reader = result.Reader;
        var game = result.Game;
        HasGame = game is not null;
        Title = game?.Title ?? string.Empty;
        CoverUrl = game?.CoverImageUrl;
        HasCover = !string.IsNullOrEmpty(CoverUrl);
        Facts = game is null
            ? []
            : new[]
                {
                    ("Developer", game.Developer),
                    ("Genres", game.Genres.Count > 0 ? string.Join(", ", game.Genres) : null),
                    ("Tags", game.Tags.Count > 0 ? string.Join(", ", game.Tags) : null),
                    ("Version", game.Version),
                    ("Size", game.DisplaySize),
                    ("Released", game.ReleaseDate),
                    ("Updated", game.UploadedAtUtc?.ToLocalTime().ToString("d")),
                    ("Screenshots", game.ScreenshotUrls.Count.ToString()),
                    ("Trailers", game.TrailerUrls.Count.ToString()),
                }
                .Select(f => $"{f.Item1}: {(string.IsNullOrWhiteSpace(f.Item2) ? "—" : f.Item2)}")
                .ToList();
        Description = game is null ? string.Empty : game.Description.Length > 600 ? game.Description[..600] + "…" : game.Description;
        Downloads = game?.DownloadLinks.Select(l => $"{l.Kind} · {(l.Mirror.Length > 0 ? l.Mirror : l.Host)} · {l.Url}").Take(20).ToList() ?? [];
        Listed = result.Listed.Take(12).Select(g => g.Title).ToList();
        ListedText = result.Listed.Count == 0 ? string.Empty : $"{result.Listed.Count} games in the list (first {Listed.Count}):";
        Notes = result.Notes.ToList();
    }

    public string Reader { get; }
    public bool HasGame { get; }
    public string Title { get; }
    public string? CoverUrl { get; }
    public bool HasCover { get; }
    public IReadOnlyList<string> Facts { get; }
    public string Description { get; }
    public IReadOnlyList<string> Downloads { get; }
    public bool HasDownloads => Downloads.Count > 0;
    public IReadOnlyList<string> Listed { get; }
    public string ListedText { get; }
    public bool HasListed => Listed.Count > 0;
    public IReadOnlyList<string> Notes { get; }
    public bool HasNotes => Notes.Count > 0;
}

/// <summary>What the teacher worked out from a pasted page, laid out for reading.</summary>
public sealed class SiteReaderLessonViewModel
{
    public SiteReaderLessonViewModel(SiteReaderLesson lesson, string savedPath)
    {
        Summary = (lesson.FoundPage, lesson.FoundListing) switch
        {
            (true, _) => $"Learned a game page for {lesson.Reader.Name}. Saved to {Path.GetFileName(savedPath)}.",
            (_, true) => $"Learned a list of games for {lesson.Reader.Name}. Saved to {Path.GetFileName(savedPath)}.",
            _ => "Nothing was learned.",
        };
        Lines = lesson.Fields
            .Select(f => $"{f.Area} · {f.Field}:  {f.Selector}   →   {(string.IsNullOrWhiteSpace(f.Sample) ? "—" : f.Sample)}")
            .ToList();
        Notes = lesson.Notes.ToList();
    }

    /// <summary>A reader made from an address: what it covers, the selectors it chose, and every step it took.</summary>
    public SiteReaderLessonViewModel(AutoReaderResult result, IReadOnlyList<string> notes)
    {
        var covers = (result.HasList, result.HasPage) switch
        {
            (true, true) => "its list of games and its game pages",
            (true, false) => "its list of games",
            _ => "its game pages",
        };
        var extras = new List<string>();
        if (result.BrowseUrl?.Contains("{page}", StringComparison.OrdinalIgnoreCase) == true) extras.Add("every page of the list");
        if (result.SearchUrl is not null) extras.Add("its search");
        Summary = $"Made a reader for {result.Name}: {covers}{(extras.Count > 0 ? ", " + string.Join(" and ", extras) : string.Empty)}. " +
                  $"Saved to {Path.GetFileName(result.Path)}.";
        Lines = result.Fields
            .Select(f => $"{f.Area} · {f.Field}:  {f.Selector}   →   {(string.IsNullOrWhiteSpace(f.Sample) ? "—" : f.Sample)}")
            .ToList();
        Notes = notes;
    }

    public string Summary { get; }
    public IReadOnlyList<string> Lines { get; }
    public bool HasLines => Lines.Count > 0;
    public IReadOnlyList<string> Notes { get; }
    public bool HasNotes => Notes.Count > 0;
}
