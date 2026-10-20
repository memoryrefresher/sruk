using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SeaRaider.Core;
using SeaRaider.Core.Downloads;
using SeaRaider.Core.Models;

namespace SeaRaider.App.ViewModels;

/// <summary>Base for in-window modal dialogs.</summary>
public abstract class DialogViewModelBase : ViewModelBase
{
    public abstract string DialogTitle { get; }
}

/// <summary>
/// Pre-download dialog: choose the install folder name and the downloaded file names before anything is fetched,
/// and how to handle the release if it turns out to be a setup program.
/// </summary>
public sealed partial class InstallOptionsDialogViewModel : DialogViewModelBase
{
    private const int MaxPreviewNames = 6;

    private readonly GameMetadata _game;
    private readonly DownloadLink? _preferredLink;
    private readonly InstallPreview _preview;

    /// <summary>Settings → "Show advanced options": the Advanced section starts open.</summary>
    public bool ShowAdvanced { get; }

    [ObservableProperty] private bool _advancedOpen;
    private readonly TaskCompletionSource<InstallRequest?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="showAdvanced">Open the "Advanced options" section (file names, installer switches) right away.</param>
    public InstallOptionsDialogViewModel(GameMetadata game, DownloadLink? preferredLink, InstallPreview preview, bool showAdvanced = false)
    {
        ShowAdvanced = showAdvanced;
        AdvancedOpen = showAdvanced;
        _game = game;
        _preferredLink = preferredLink;
        _preview = preview;

        FileBaseName = preview.SuggestedFileBaseName;
        FolderName = preview.SuggestedFolderName;
        SilentInstall = preview.DefaultSilentInstall;
        RunInstallerWhenReady = preview.DefaultRunInstallerWhenReady;
        Refresh();
    }

    /// <summary>Completes with the request, or null when cancelled.</summary>
    public Task<InstallRequest?> Completion => _completion.Task;

    public override string DialogTitle => $"Download {_game.Title}";
    public string? CoverImageUrl => _game.CoverImageUrl;
    public bool CanRenameFiles => _preview.CanRenameFiles;

    public string SourceText => _preview.Transport == DownloadTransport.Torrent
        ? "Torrent download. File names inside a torrent are set by the torrent itself, so only the folder can be renamed."
        : _preview.Files.Count == 1
            ? $"One file from {_preview.Files[0].Host}"
            : $"{_preview.Files.Count}-part archive from {_preview.Files[0].Host}. Renaming keeps each part's number so the parts still join.";

    // ---- disk space ---------------------------------------------------------------------------

    /// <summary>How much space the whole download-extract-setup needs, or null when the size isn't known.</summary>
    public string? SpaceNote => _preview.Space?.Summary;

    /// <summary>Shown in red when a drive doesn't have that much free.</summary>
    public string? SpaceWarning => _preview.Space?.Warning;

    public bool HasSpaceNote => SpaceNote is not null;
    public bool HasSpaceWarning => SpaceWarning is not null;

    // ---- install folder -----------------------------------------------------------------------

    [ObservableProperty] private string _folderName = string.Empty;
    [ObservableProperty] private string _folderPath = string.Empty;
    [ObservableProperty] private string? _folderNote;

    partial void OnFolderNameChanged(string value) => Refresh();

    // ---- file names ---------------------------------------------------------------------------

    [ObservableProperty] private string _fileBaseName = string.Empty;
    [ObservableProperty] private string? _fileNameError;
    [ObservableProperty] private string _fileSuffix = string.Empty;

    public ObservableCollection<string> ResultingFileNames { get; } = [];
    [ObservableProperty] private string? _moreFilesText;

    partial void OnFileBaseNameChanged(string value) => Refresh();

    // ---- installer options --------------------------------------------------------------------

    [ObservableProperty] private bool _silentInstall;
    [ObservableProperty] private bool _runInstallerWhenReady;
    [ObservableProperty] private string? _customInstallerArguments;
    [ObservableProperty] private bool _showInstallerOptions;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private bool _isValid = true;

    /// <summary>Recomputes previews and validation after any edit.</summary>
    private void Refresh()
    {
        var folder = PathSafety.ToFolderName(string.IsNullOrWhiteSpace(FolderName) ? _game.Title : FolderName);
        FolderPath = Path.Combine(_preview.LibraryRoot, folder);
        FolderNote = folder != (FolderName ?? string.Empty).Trim() && !string.IsNullOrWhiteSpace(FolderName)
            ? $"Characters Windows doesn't allow are removed: \"{folder}\"."
            : _preview.FolderExists(folder)
                ? $"A folder named \"{folder}\" already exists. SeaRaider will use \"{folder} (2)\" instead of overwriting it."
                : null;

        ResultingFileNames.Clear();
        MoreFilesText = null;
        if (CanRenameFiles)
        {
            FileNameError = FileNameRules.Validate(FileBaseName);
            FileSuffix = FileNameRules.Split(_preview.Files[0].OriginalFileName).Suffix;
            if (FileNameError is null)
            {
                foreach (var file in _preview.Files.Take(MaxPreviewNames))
                    ResultingFileNames.Add(FileNameRules.Rename(file.OriginalFileName, FileBaseName));
                if (_preview.Files.Count > MaxPreviewNames)
                    MoreFilesText = $"and {_preview.Files.Count - MaxPreviewNames} more";
            }
        }

        IsValid = !CanRenameFiles || FileNameError is null;
    }

    [RelayCommand]
    private void ResetNames()
    {
        FolderName = _preview.SuggestedFolderName;
        FileBaseName = _preview.SuggestedFileBaseName;
    }

    [RelayCommand(CanExecute = nameof(IsValid))]
    private void Confirm()
    {
        var renamed = CanRenameFiles && !string.Equals(FileBaseName, _preview.SuggestedFileBaseName, StringComparison.Ordinal);
        _completion.TrySetResult(new InstallRequest(_game)
        {
            PreferredLink = _preferredLink,
            InstallFolderName = FolderName,
            FileBaseName = renamed ? FileBaseName : null,
            SilentInstall = SilentInstall,
            RunInstallerWhenReady = RunInstallerWhenReady,
            CustomInstallerArguments = CustomInstallerArguments,
        });
    }

    [RelayCommand]
    private void Cancel() => _completion.TrySetResult(null);
}
