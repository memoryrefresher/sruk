using CommunityToolkit.Mvvm.ComponentModel;
using SeaRaider.App.ViewModels;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Downloads;
using SeaRaider.Core.Models;

namespace SeaRaider.App.Services;

/// <summary>
/// In-window modal dialogs. The shell (<see cref="MainWindowViewModel"/>) shows <see cref="Current"/> in an
/// overlay above everything; callers simply await the result.
/// </summary>
public sealed partial class DialogService(IDownloadAndExtractManager downloads, ILibraryRepository repository) : ObservableObject
{
    /// <summary>The dialog being shown, or null.</summary>
    [ObservableProperty] private DialogViewModelBase? _current;

    /// <summary>
    /// Shows the pre-download dialog (install folder, file names, installer options).
    /// Returns null if the user cancels.
    /// </summary>
    public async Task<InstallRequest?> ShowInstallOptionsAsync(GameMetadata game, DownloadLink? preferredLink)
    {
        var preview = downloads.Preview(game, preferredLink); // throws if the game has no usable links
        var dialog = new InstallOptionsDialogViewModel(game, preferredLink, preview, repository.GetSettings().ShowAdvancedOptions);
        return await ShowAsync(dialog, dialog.Completion);
    }

    /// <summary>Shows the first-run welcome questions. Completes when they have been answered and saved.</summary>
    public Task<bool> ShowOnboardingAsync(OnboardingDialogViewModel dialog) => ShowAsync(dialog, dialog.Completion);

    private async Task<T> ShowAsync<T>(DialogViewModelBase dialog, Task<T> completion)
    {
        if (Current is not null) throw new InvalidOperationException("Another dialog is already open.");
        Current = dialog;
        try
        {
            return await completion;
        }
        finally
        {
            Current = null;
        }
    }
}
