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
            Title = "Choose the file that starts the game (.exe or .bat)",
            AllowMultiple = false,
            SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(startFolder),
            FileTypeFilter =
            [
                new FilePickerFileType("Programs and scripts") { Patterns = ["*.exe", "*.bat", "*.cmd"] },
                new FilePickerFileType("Programs") { Patterns = ["*.exe"] },
                new FilePickerFileType("Batch files") { Patterns = ["*.bat", "*.cmd"] },
            ],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <summary>Picks a picture (a profile picture).</summary>
    public async Task<string?> PickImageAsync()
    {
        if (GetStorage() is not { } storage) return null;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a profile picture",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Pictures") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.bmp"] }],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <summary>Picks a .torrent file (Sources › a backups torrent).</summary>
    public async Task<string?> PickTorrentAsync()
    {
        if (GetStorage() is not { } storage) return null;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose the torrent of your backups",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Torrent files") { Patterns = ["*.torrent"] }],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <summary>Picks an emulator program (Settings › Emulators).</summary>
    public async Task<string?> PickEmulatorAsync(string systemName)
    {
        if (GetStorage() is not { } storage) return null;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Choose the emulator that plays {systemName}",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Programs") { Patterns = ["*.exe"] }, FilePickerFileTypes.All],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <summary>Picks a PS2 BIOS file (Settings › Emulators).</summary>
    public async Task<string?> PickBiosAsync()
    {
        if (GetStorage() is not { } storage) return null;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose your PlayStation 2 BIOS (dumped from your console)",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("PS2 BIOS") { Patterns = ["*.bin", "*.rom0", "*.rom"] }, FilePickerFileTypes.All],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <summary>Picks a game's program on this PC (Library › Add a game).</summary>
    public Task<string?> PickGameProgramAsync() =>
        PickConsoleFileAsync("Choose the game's program", "Programs", ["*.exe", "*.bat", "*.cmd", "*.lnk"]);

    /// <summary>Picks a console game's file on this PC (Library › Add a ROM): any type, since consoles use many.</summary>
    public Task<string?> PickRomAsync() =>
        PickConsoleFileAsync("Choose the game file (ROM or disc image; for a PS3 disc folder, its PS3_GAME\\USRDIR\\EBOOT.BIN)", "Game files", ["*.*"]);

    /// <summary>Picks one of the console's own files (Settings › Emulators): an Xbox boot ROM, BIOS or hard disk, PS3 system software.</summary>
    public async Task<string?> PickConsoleFileAsync(string title, string kind, string[] patterns)
    {
        if (GetStorage() is not { } storage) return null;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(kind) { Patterns = patterns }, FilePickerFileTypes.All],
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
