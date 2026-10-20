using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SeaRaider.App.Services;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Models;
using SeaRaider.Core.Social;

namespace SeaRaider.App.ViewModels;

/// <summary>The questions of the welcome dialog, in the order they are asked.</summary>
public enum OnboardingStep
{
    /// <summary>"Do you have a sources.txt?" (skipped when sources are already there).</summary>
    Sources,

    /// <summary>"What should we call you?"</summary>
    Name,

    /// <summary>"Install games to" and "Temporary downloads".</summary>
    Folders,
}

/// <summary>
/// First-run welcome, shown over the store on a brand-new install: import a sources.txt (only if none was dropped next to
/// SeaRaider already), pick a name, then choose the library and temporary-download folders. Nothing is saved until
/// <b>Finish</b>; closing SeaRaider half-way means the questions are asked again at the next start.
/// </summary>
public sealed partial class OnboardingDialogViewModel : DialogViewModelBase
{
    private readonly ILibraryRepository _repository;
    private readonly SourcesFileService _sourcesFile;
    private readonly IFriendsService _friends;
    private readonly ShellService _shell;
    private readonly OnboardingStep[] _steps;
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public OnboardingDialogViewModel(ILibraryRepository repository, SourcesFileService sourcesFile, IFriendsService friends,
        ShellService shell, bool askForSources)
    {
        _repository = repository;
        _sourcesFile = sourcesFile;
        _friends = friends;
        _shell = shell;
        _steps = askForSources
            ? [OnboardingStep.Sources, OnboardingStep.Name, OnboardingStep.Folders]
            : [OnboardingStep.Name, OnboardingStep.Folders];

        var settings = repository.GetSettings();
        LibraryRoot = settings.LibraryRoot;
        TempRoot = settings.TempRoot;
        DisplayName = friends.Profile.DisplayName; // starts as the Windows user name
        _step = _steps[0];
    }

    /// <summary>Completes (true) once the answers are saved.</summary>
    public Task<bool> Completion => _completion.Task;

    public override string DialogTitle => "Welcome to SeaRaider";

    // ---- where we are ---------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSourcesStep), nameof(IsNameStep), nameof(IsFoldersStep), nameof(CanGoBack),
        nameof(IsLastStep), nameof(StepText), nameof(NextLabel))]
    private OnboardingStep _step;

    public bool IsSourcesStep => Step == OnboardingStep.Sources;
    public bool IsNameStep => Step == OnboardingStep.Name;
    public bool IsFoldersStep => Step == OnboardingStep.Folders;
    public bool CanGoBack => Array.IndexOf(_steps, Step) > 0;
    public bool IsLastStep => Array.IndexOf(_steps, Step) == _steps.Length - 1;
    public string StepText => $"Step {Array.IndexOf(_steps, Step) + 1} of {_steps.Length}";

    /// <summary>Before anything is imported the first question's button says what it does: carry on without a file.</summary>
    public string NextLabel => IsLastStep ? "Finish" : IsSourcesStep && !HasImported ? "No, skip this" : "Continue";

    /// <summary>Importing or saving: the buttons wait.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    private bool _isBusy;

    public bool IsNotBusy => !IsBusy;

    // ---- 1. sources file ------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NextLabel))]
    private bool _hasImported;

    /// <summary>"Imported 12 sources from sources.txt."</summary>
    [ObservableProperty] private string? _importResult;

    [RelayCommand]
    private async Task ImportSourcesAsync()
    {
        if (IsBusy) return;
        await RunSafeAsync(async () =>
        {
            if (await _shell.PickTextFileAsync("Import sources") is not { } path) return;
            IsBusy = true;
            try
            {
                var (added, skipped) = await _sourcesFile.ImportAsync(path);
                ImportResult = added == 0
                    ? $"Nothing usable in {Path.GetFileName(path)} ({skipped} skipped). You can try another file, or add sources later in Settings › Sources."
                    : $"Imported {added} {(added == 1 ? "source" : "sources")} from {Path.GetFileName(path)}" +
                      (skipped > 0 ? $" ({skipped} skipped: already there or not usable)." : ".");
                HasImported = added > 0;
            }
            finally
            {
                IsBusy = false;
            }
        });
    }

    // ---- 2. name --------------------------------------------------------------------------------------

    /// <summary>The name friends see (the Friends profile's display name).</summary>
    [ObservableProperty] private string _displayName = string.Empty;

    // ---- 3. folders -----------------------------------------------------------------------------------

    [ObservableProperty] private string _libraryRoot = string.Empty;
    [ObservableProperty] private string _tempRoot = string.Empty;

    [RelayCommand]
    private Task BrowseLibraryAsync() => RunSafeAsync(async () =>
    {
        if (await _shell.PickFolderAsync("Choose where games are installed", LibraryRoot) is { } folder) LibraryRoot = folder;
    });

    [RelayCommand]
    private Task BrowseTempAsync() => RunSafeAsync(async () =>
    {
        if (await _shell.PickFolderAsync("Choose a folder for temporary downloads", TempRoot) is { } folder) TempRoot = folder;
    });

    // ---- moving between the questions -----------------------------------------------------------------

    [RelayCommand]
    private void Back()
    {
        ErrorMessage = null;
        var index = Array.IndexOf(_steps, Step);
        if (index > 0) Step = _steps[index - 1];
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        if (IsBusy) return;
        ErrorMessage = null;

        if (IsNameStep && string.IsNullOrWhiteSpace(DisplayName))
        {
            ErrorMessage = "Enter a name friends will recognise.";
            return;
        }

        if (!IsLastStep)
        {
            Step = _steps[Array.IndexOf(_steps, Step) + 1];
            return;
        }

        await RunSafeAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(LibraryRoot) || !Path.IsPathFullyQualified(LibraryRoot))
                throw new ArgumentException("\"Install games to\" must be a full path, like C:\\MyGames.");
            if (string.IsNullOrWhiteSpace(TempRoot) || !Path.IsPathFullyQualified(TempRoot))
                throw new ArgumentException("\"Temporary downloads\" must be a full path, like C:\\SeaRaiderTemp.");

            IsBusy = true;
            try
            {
                var library = LibraryRoot.Trim();
                var temp = TempRoot.Trim();
                Directory.CreateDirectory(library);
                Directory.CreateDirectory(temp);

                await _friends.SetDisplayNameAsync(DisplayName);

                var settings = _repository.GetSettings();
                settings.LibraryRoot = library;
                settings.TempRoot = temp;
                settings.FirstRun = FirstRunState.Completed;
                _repository.SaveSettings(settings);

                _completion.TrySetResult(true);
            }
            finally
            {
                IsBusy = false;
            }
        });
    }
}
