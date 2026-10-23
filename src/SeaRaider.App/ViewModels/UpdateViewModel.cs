using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core;
using SeaRaider.Core.Updates;

namespace SeaRaider.App.ViewModels;

/// <summary>
/// New SeaRaider versions: checks the publisher's release file at start-up and every few hours, shows a banner when one
/// is out, and installs it on request (download, check, restart). Also drives Settings › Updates.
/// </summary>
public sealed partial class UpdateViewModel(UpdateService updates, ILibraryRepository repository) : ViewModelBase
{
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private UpdateManifest? _available;
    private DispatcherTimer? _timer;

    [ObservableProperty] private bool _hasUpdate;
    [ObservableProperty] private string? _updateTitle;
    [ObservableProperty] private string? _updateNotes;
    [ObservableProperty] private bool _isChecking;
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private double _installProgress;
    [ObservableProperty] private string? _statusText;
    [ObservableProperty] private bool _justUpdated;

    public string CurrentVersionText => $"SeaRaider {UpdateDefaults.CurrentVersion}";

    public bool IsConfigured => UpdateService.IsConfigured;

    public bool CanInstall => UpdateService.CanInstall;

    public bool HasNotes => !string.IsNullOrWhiteSpace(UpdateNotes);

    public string SetupText => IsConfigured
        ? CanInstall
            ? "Updates are checked automatically and installed when you choose. Downloads in progress continue after the restart."
            : "This copy runs from a build folder, so it can tell you about new versions but can't replace itself. Use a published SeaRaider.exe for one-click updates."
        : "This build has no update address or release key, so it can't check for updates (see README › Updates).";

    public bool CheckAutomatically
    {
        get => repository.GetSettings().CheckForUpdates;
        set
        {
            var settings = repository.GetSettings();
            if (settings.CheckForUpdates == value) return;
            settings.CheckForUpdates = value;
            repository.SaveSettings(settings);
            OnPropertyChanged();
        }
    }

    partial void OnUpdateNotesChanged(string? value) => OnPropertyChanged(nameof(HasNotes));

    /// <summary>Called once at start-up: the timed checks, and a "just updated" note after an update restart.</summary>
    public void Start(string[] args)
    {
        if (args.Contains(UpdateService.UpdatedSwitch))
        {
            JustUpdated = true;
            StatusText = $"Updated to {UpdateDefaults.CurrentVersion}.";
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMinutes(1));
            UpdateService.CleanUp();
        });

        if (!IsConfigured) return;
        _timer = new DispatcherTimer { Interval = FirstCheckDelay };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = CheckInterval;
            if (CheckAutomatically && !HasUpdate && !IsInstalling) await CheckAsync(userAsked: false);
        };
        _timer.Start();
    }

    [RelayCommand]
    private Task CheckNowAsync() => CheckAsync(userAsked: true);

    private async Task CheckAsync(bool userAsked)
    {
        if (IsChecking || !IsConfigured) return;
        IsChecking = true;
        if (userAsked) StatusText = "Checking for updates…";
        try
        {
            var found = await updates.CheckAsync(CancellationToken.None);
            var skipped = repository.GetSettings().SkippedUpdateVersion;
            if (found is null)
            {
                if (userAsked) StatusText = $"You have the newest version ({UpdateDefaults.CurrentVersion}).";
                return;
            }
            if (!userAsked && string.Equals(skipped, found.ParsedVersion?.ToString(), StringComparison.Ordinal)) return;

            _available = found;
            UpdateTitle = $"SeaRaider {found.ParsedVersion} is available (you have {UpdateDefaults.CurrentVersion})";
            UpdateNotes = string.IsNullOrWhiteSpace(found.Notes) ? null : found.Notes.Trim();
            HasUpdate = true;
            StatusText = $"Version {found.ParsedVersion} is available.";
        }
        catch (UpdateException ex)
        {
            ErrorLog.Write(ex, "update check");
            if (userAsked) StatusText = ex.Message;
        }
        finally
        {
            IsChecking = false;
        }
    }

    [RelayCommand]
    private Task InstallAsync() => RunSafeAsync(async () =>
    {
        if (_available is not { } release || IsInstalling) return;
        if (!CanInstall)
        {
            StatusText = "This copy can't replace itself (it runs from a build folder). Download the new SeaRaider.exe yourself.";
            return;
        }

        IsInstalling = true;
        InstallProgress = 0;
        StatusText = "Downloading the update…";
        try
        {
            var progress = new Progress<double>(p => InstallProgress = p * 100);
            var exe = await updates.DownloadAsync(release, progress, CancellationToken.None);
            StatusText = "Restarting SeaRaider…";
            UpdateService.StartApplying(exe);
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
        }
        catch (UpdateException ex)
        {
            ErrorLog.Write(ex, "update install");
            StatusText = ex.Message;
            IsInstalling = false;
            throw;
        }
    });

    [RelayCommand]
    private void Later() => HasUpdate = false;

    [RelayCommand]
    private void Skip()
    {
        if (_available?.ParsedVersion is { } version)
        {
            var settings = repository.GetSettings();
            settings.SkippedUpdateVersion = version.ToString();
            repository.SaveSettings(settings);
        }
        HasUpdate = false;
    }

    [RelayCommand]
    private void DismissUpdated() => JustUpdated = false;
}
