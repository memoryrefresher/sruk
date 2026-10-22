using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SeaRaider.App.ViewModels;

/// <summary>Base for every view-model: change notification plus a safe way to run async work.</summary>
public abstract partial class ViewModelBase : ObservableObject
{
    /// <summary>Last user-facing error for this view; shown in an inline banner.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    [RelayCommand]
    private void DismissError() => ErrorMessage = null;

    /// <summary>
    /// Runs an async action and turns exceptions into <see cref="ErrorMessage"/> instead of letting
    /// them escape to the dispatcher (an unhandled exception in an async command would crash the app).
    /// </summary>
    protected async Task RunSafeAsync(Func<Task> action)
    {
        try
        {
            ErrorMessage = null;
            await action();
        }
        catch (OperationCanceledException)
        {
            // user cancelled — not an error
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe(ex);
        }
    }

    /// <summary>
    /// The banner text for an error. Errors SeaRaider raises on purpose already read well; anything else is a slip in
    /// SeaRaider itself (a page it didn't expect, a missing value), so its full details go to the error log and the banner
    /// says where to find them.
    /// </summary>
    protected static string Describe(Exception ex)
    {
        var expected = ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException
                           or HttpRequestException or TimeoutException or FileNotFoundException
                       && ex is not ArgumentNullException and not ObjectDisposedException;
        Core.ErrorLog.Write(ex, expected ? "handled" : "unexpected");
        return expected
            ? ex.Message
            : $"Something went wrong ({ex.GetType().Name}: {ex.Message}). The details are saved in {Core.ErrorLog.Path}; send that file to get it fixed.";
    }

    protected void RunSafe(Action action)
    {
        try
        {
            ErrorMessage = null;
            action();
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe(ex);
        }
    }
}

/// <summary>A page hosted in the main content area.</summary>
public abstract class PageViewModel : ViewModelBase
{
    /// <summary>Called each time the page becomes visible (refresh data here).</summary>
    public virtual void OnNavigatedTo() { }

    /// <summary>Called when another page takes its place.</summary>
    public virtual void OnNavigatedFrom() { }
}
