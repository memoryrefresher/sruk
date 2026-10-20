using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace SeaRaider.App.Services;

/// <summary>OS integration the view-models need: open links/folders and file/folder pickers.</summary>
public sealed class ShellService
{
    /// <summary>Opens a web page or trailer in the default browser.</summary>
    public void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return; // never shell-execute arbitrary scraped strings
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    /// <summary>Opens a local page SeaRaider wrote (the trailer player) in the default browser.</summary>
    public void OpenFile(string path)
    {
        if (!File.Exists(path) || !path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) return;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public void OpenFolder(string path)
    {
        if (Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public async Task CopyToClipboardAsync(string text)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window } &&
            TopLevel.GetTopLevel(window)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    public async Task<string?> PickExecutableAsync(string startFolder)
    {
        if (GetStorage() is not { } storage) return null;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose the game's executable",
            AllowMultiple = false,
            SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(startFolder),
            FileTypeFilter = [new FilePickerFileType("Programs") { Patterns = ["*.exe"] }],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickFolderAsync(string title, string? startFolder)
    {
        if (GetStorage() is not { } storage) return null;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = startFolder is null ? null : await storage.TryGetFolderFromPathAsync(startFolder),
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    /// <summary>Picks a text file to open (e.g. a sources list).</summary>
    public async Task<string?> PickTextFileAsync(string title)
    {
        if (GetStorage() is not { } storage) return null;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Text files") { Patterns = ["*.txt"] }, FilePickerFileTypes.All],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <summary>Picks where to save a text file.</summary>
    public async Task<string?> PickSaveTextFileAsync(string title, string suggestedName)
    {
        if (GetStorage() is not { } storage) return null;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = "txt",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType("Text files") { Patterns = ["*.txt"] }],
        });
        return file?.TryGetLocalPath();
    }

    private static IStorageProvider? GetStorage() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window }
            ? TopLevel.GetTopLevel(window)?.StorageProvider
            : null;
}
