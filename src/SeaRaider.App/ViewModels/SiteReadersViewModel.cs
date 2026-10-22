using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SeaRaider.App.Services;
using SeaRaider.Core.Scraping;

namespace SeaRaider.App.ViewModels;

/// <summary>
/// Sources › Site readers: the dedicated readers in the readers folders (JSON files of CSS selectors), a way to start
/// one for a site, and a test that shows what a reader finds on a page before it's used for real.
/// </summary>
public sealed partial class SiteReadersViewModel(SiteReaderCatalog catalog, SiteReaderTester tester, ShellService shell) : ViewModelBase
{
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

    [RelayCommand]
    private Task LearnAsync() => RunSafeAsync(async () =>
    {
        var html = LearnHtml;
        var address = LearnAddress;
        var hints = new LearnHints
        {
            DownloadExamples = LearnHints.SplitExamples(LearnDownloadExamples),
            DownloadSelector = string.IsNullOrWhiteSpace(LearnDownloadSelector) ? null : LearnDownloadSelector.Trim(),
        };
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

    public string Summary { get; }
    public IReadOnlyList<string> Lines { get; }
    public bool HasLines => Lines.Count > 0;
    public IReadOnlyList<string> Notes { get; }
    public bool HasNotes => Notes.Count > 0;
}
