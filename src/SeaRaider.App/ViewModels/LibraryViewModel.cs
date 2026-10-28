using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SeaRaider.App.Services;
using SeaRaider.Core.Catalog;
using SeaRaider.Core;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Installing;
using SeaRaider.Core.Models;

namespace SeaRaider.App.ViewModels;

/// <summary>Installed games (Play / Uninstall) and downloaded setups waiting to be run (Install / Remove).</summary>
public sealed partial class LibraryViewModel : PageViewModel
{
    private readonly ILibraryRepository _repository;
    private readonly IGameLauncherService _launcher;
    private readonly IInstallerService _installer;
    private readonly GameActionService _actions;
    private readonly ShellService _shell;
    private readonly SeaRaider.Core.Launching.LibraryImportService _import;
    private readonly GameStatusService _status;

    public LibraryViewModel(ILibraryRepository repository, IGameLauncherService launcher, IInstallerService installer,
        GameActionService actions, ShellService shell, GameStatusService status, SeaRaider.Core.Launching.LibraryImportService import)
    {
        _import = import;
        _status = status;
        _repository = repository;
        _launcher = launcher;
        _installer = installer;
        _actions = actions;
        _shell = shell;

        status.InstallChanged += _ => Reload();
        status.GameChanged += id => Games.FirstOrDefault(g => g.GameId == id)?.Refresh();
        status.SetupActivityChanged += activity => Games.FirstOrDefault(g => g.GameId == activity.GameId)?.ApplySetupActivity(activity);
        RecoverInterruptedUninstalls();
        Reload();
    }

    /// <summary>Every row (PC and console), for live updates.</summary>
    public ObservableCollection<InstalledGameViewModel> Games { get; } = [];

    /// <summary>What the page shows: the PC games, or the console games in one section per console.</summary>
    public ObservableCollection<LibrarySection> Sections { get; } = [];

    /// <summary>The console part of the library is showing (else PC games).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPc))]
    private bool _showConsoles;

    public bool ShowPc => !ShowConsoles;

    [ObservableProperty] private string _pcCountText = "0";
    [ObservableProperty] private string _consoleCountText = "0";

    /// <summary>Console games are installed: the PC / Consoles switch shows.</summary>
    [ObservableProperty] private bool _hasConsoleGames;

    /// <summary>The part showing has nothing (the other part may).</summary>
    [ObservableProperty] private bool _isPartEmpty;

    [ObservableProperty] private string _partEmptyText = string.Empty;

    private bool _choseConsoles; // the user picked a part: don't switch for them

    [RelayCommand]
    private void ShowPcGames()
    {
        _choseConsoles = true;
        ShowConsoles = false;
    }

    [RelayCommand]
    private void ShowConsoleGames()
    {
        _choseConsoles = true;
        ShowConsoles = true;
    }

    partial void OnShowConsolesChanged(bool value) => BuildSections();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private int _count;

    [ObservableProperty] private string _summary = string.Empty;

    public bool IsEmpty => Count == 0;

    public override void OnNavigatedTo()
    {
        RecoverInterruptedUninstalls();
        Reload();
    }

    /// <summary>An uninstall that never finished (SeaRaider closed meanwhile) mustn't leave a row stuck on "Uninstalling".</summary>
    private void RecoverInterruptedUninstalls()
    {
        try { _launcher.RecoverInterruptedUninstalls(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* tried again next time */ }
    }

    private void Reload()
    {
        var installs = _repository.GetInstalls()
            .Where(i => i.State is GameInstallState.Installed or GameInstallState.Uninstalling
                or GameInstallState.ReadyToInstall or GameInstallState.RunningInstaller)
            // Setups waiting to be run first, then most recently played.
            .OrderBy(i => i.State is GameInstallState.ReadyToInstall or GameInstallState.RunningInstaller ? 0 : 1)
            .ThenByDescending(i => i.LastPlayedAtUtc ?? i.InstalledAtUtc ?? DateTime.MinValue)
            .ToList();

        // Reuse row view-models so an inline "confirm uninstall" state isn't lost on refresh.
        var existing = Games.ToDictionary(g => g.GameId);
        Games.Clear();
        foreach (var install in installs)
        {
            var row = existing.TryGetValue(install.GameId, out var vm) ? vm : new InstalledGameViewModel(_launcher, _installer, _actions, _repository, _shell);
            row.Load(install);
            Games.Add(row);
        }

        Count = Games.Count;
        var consoles = Games.Count(g => g.SystemName is not null);
        HasConsoleGames = consoles > 0;
        PcCountText = (Games.Count - consoles).ToString("N0");
        ConsoleCountText = consoles.ToString("N0");
        if (!_choseConsoles && consoles > 0 && consoles == Games.Count) ShowConsoles = true;  // only console games: show them
        if (consoles == 0 && ShowConsoles) ShowConsoles = false;
        BuildSections();

        var playable = installs.Count(i => i.State == GameInstallState.Installed);
        var pending = installs.Count(i => i.State is GameInstallState.ReadyToInstall or GameInstallState.RunningInstaller);
        Summary = Count == 0
            ? string.Empty
            : $"{playable} installed, {Formatting.Bytes(installs.Sum(i => i.SizeOnDiskBytes))} on disk" +
              (pending > 0 ? $", {pending} waiting to install" : string.Empty);
    }

    /// <summary>PC games in one list; console games in a section per console (alphabetical).</summary>
    private void BuildSections()
    {
        Sections.Clear();
        if (ShowConsoles)
        {
            foreach (var console in Games.Where(g => g.SystemName is not null).GroupBy(g => g.SystemName!).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
                Sections.Add(new LibrarySection(console.Key, console.ToList()));
        }
        else if (Games.Where(g => g.SystemName is null).ToList() is { Count: > 0 } pc)
        {
            Sections.Add(new LibrarySection(null, pc));
        }
        IsPartEmpty = Count > 0 && Sections.Count == 0;
        PartEmptyText = ShowConsoles
            ? "No console games yet. Pick a console in the Store to find some."
            : "No PC games yet. Your console games are under Consoles.";
    }

    [RelayCommand]
    private void OpenLibraryFolder() => RunSafe(() => _shell.OpenFolder(_repository.GetSettings().LibraryRoot));

    // ---- Add a game / ROM that's already on this PC ----------------------------------------------------

    /// <summary>The consoles to pick from when a game file doesn't say which one it's for.</summary>
    public IReadOnlyList<SystemChoice> ConsoleChoices { get; } =
        SeaRaider.Core.Emulation.GameSystems.All.Select(s => new SystemChoice(s.Id, s.Name)).ToList();

    /// <summary>A game file waiting for its console to be picked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoosingConsole))]
    private string? _pendingRom;

    [ObservableProperty] private SystemChoice? _pendingConsole;

    public bool IsChoosingConsole => PendingRom is not null;

    public string PendingRomName => PendingRom is null ? string.Empty : Path.GetFileName(PendingRom);

    partial void OnPendingRomChanged(string? value) => OnPropertyChanged(nameof(PendingRomName));

    [ObservableProperty] private string? _addStatus;

    [RelayCommand]
    private Task AddPcGameAsync() => RunSafeAsync(async () =>
    {
        if (await _shell.PickGameProgramAsync() is not { } path) return;
        var install = await Task.Run(() => _import.AddPcGame(path));
        await AddedAsync(install);
    });

    [RelayCommand]
    private Task AddRomAsync() => RunSafeAsync(async () =>
    {
        if (await _shell.PickRomAsync() is not { } path) return;
        if (SeaRaider.Core.Launching.LibraryImportService.SystemOf(path) is null)
        {
            PendingRom = path; // which console? asked inline, then ConfirmRom adds it
            PendingConsole = null;
            return;
        }
        var install = await Task.Run(() => _import.AddRom(path, null));
        await AddedAsync(install);
    });

    [RelayCommand]
    private Task ConfirmRomAsync() => RunSafeAsync(async () =>
    {
        if (PendingRom is not { } path) return;
        if (PendingConsole?.Id is not { } console) throw new InvalidOperationException("Pick the console this game is for.");
        PendingRom = null;
        var install = await Task.Run(() => _import.AddRom(path, console));
        await AddedAsync(install);
    });

    [RelayCommand]
    private void CancelRom() => PendingRom = null;

    /// <summary>Shows the new game at once, then its cover when one is found.</summary>
    private async Task AddedAsync(InstalledGame install)
    {
        if (install.SystemId is not null) ShowConsoles = true;
        else ShowConsoles = false;
        _choseConsoles = true;
        _status.NotifyInstallChanged(install.GameId);
        AddStatus = $"Added {install.Title}. Looking for its cover…";
        var found = await Task.Run(() => _import.FindCoverAsync(install.GameId));
        if (found) _status.NotifyInstallChanged(install.GameId);
        AddStatus = found ? $"Added {install.Title}." : $"Added {install.Title} (no cover found for it).";
    }
}

/// <summary>A part of the library: a console's games under its name, or the PC games (no heading).</summary>
public sealed record LibrarySection(string? Header, IReadOnlyList<InstalledGameViewModel> Games)
{
    public bool HasHeader => Header is not null;
    public string CountText => Games.Count == 1 ? "1 game" : $"{Games.Count} games";
}

/// <summary>One library row: a playable game, or a setup that still has to run.</summary>
public sealed partial class InstalledGameViewModel(
    IGameLauncherService launcher,
    IInstallerService installer,
    GameActionService actions,
    ILibraryRepository repository,
    ShellService shell) : ViewModelBase
{
    private InstalledGame _install = new();
    private bool _loading;

    public string GameId => _install.GameId;

    /// <summary>The console it's for ("Game Boy Advance"), or null for a PC game. From the ROM found, else what the store page
    /// called it (so a console game whose ROM wasn't found yet still sits with its console).</summary>
    public string? SystemName { get; private set; }

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string? _coverImageUrl;
    [ObservableProperty] private string _playtimeText = string.Empty;
    [ObservableProperty] private string _detailText = string.Empty;
    [ObservableProperty] private string? _warning;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UninstallStatusText))]
    private bool _isUninstalling;
    [ObservableProperty] private bool _isConfirmingUninstall;

    /// <summary>Downloaded setup waiting to be run ("Install").</summary>
    [ObservableProperty] private bool _isSetupReady;

    /// <summary>The setup program is running now.</summary>
    [ObservableProperty] private bool _isInstalling;

    /// <summary>Run the setup with unattended switches (saved per game).</summary>
    [ObservableProperty] private bool _silentInstall;

    [ObservableProperty] private bool _canInstallSilently;
    [ObservableProperty] private string _installerText = string.Empty;

    /// <summary>What the running setup is doing ("Installing silently…", "Setup is waiting for an answer…").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSetupActivity))]
    private string? _setupActivityText;

    [ObservableProperty] private bool _setupNeedsAttention;
    [ObservableProperty] private bool _canShowSetupWindow;
    [ObservableProperty] private bool _canRestartWithWizard;

    public bool HasSetupActivity => !string.IsNullOrEmpty(SetupActivityText);

    /// <summary>An installed game whose setup files are still on disk (kept, or not deleted yet).</summary>
    [ObservableProperty] private bool _hasSetupFiles;

    [ObservableProperty] private bool _isDeletingSetupFiles;

    /// <summary>Deletes them (closing setup helpers still running from them) and checks they're gone.</summary>
    [RelayCommand]
    private Task DeleteSetupFilesAsync() => RunSafeAsync(async () =>
    {
        IsDeletingSetupFiles = true;
        try
        {
            await Task.Run(() => installer.DeleteSetupFilesAsync(_install.GameId));
            HasSetupFiles = false;
        }
        finally
        {
            IsDeletingSetupFiles = false;
        }
    });

    public bool IsPlayable => !IsSetupReady && !IsInstalling;

    /// <summary>
    /// Installed desktop games (not browser games) can have their launch file chosen; so can a setup that finished without its
    /// program being found (it may have installed it somewhere SeaRaider didn't look).
    /// </summary>
    public bool CanSetLaunchFile => !IsUninstalling && _install.WebGameUrl is not { Length: > 0 } && _install.RomPath is not { Length: > 0 } &&
                                    (IsPlayable || IsIncompleteSetup);

    /// <summary>Setup ran but the game's program wasn't found: Install runs it again, the setup files are kept.</summary>
    public bool IsIncompleteSetup => IsSetupReady && !IsInstalling && _install.InstallerState == InstallerState.Failed &&
                                     !string.IsNullOrEmpty(_install.InstallDirectory) && Directory.Exists(_install.InstallDirectory);

    /// <summary>Uninstalling can keep the setup files (to install again without downloading): only when they're still there.</summary>
    public bool CanKeepSetup => !IsSetupReady && HasSetupFiles;
    public bool HasWarning => !string.IsNullOrEmpty(Warning);
    public bool HasCover => !string.IsNullOrEmpty(CoverImageUrl);
    public string PlayLabel => IsRunning ? "Running" : "Play";
    public bool CanPlay => !IsRunning && !IsUninstalling && IsPlayable;
    public string InstallLabel => IsInstalling ? "Installing…" : "Install";
    public bool CanInstall => IsSetupReady && !IsInstalling;
    public string RemoveLabel => IsSetupReady ? "Remove setup" : _install.AddedFromPc ? "Remove" : "Uninstall";
    public string UninstallStatusText => IsUninstalling
        ? "Uninstalling… If the game's uninstaller asks a question, answer it in its window."
        : string.Empty;
    /// <summary>The confirm button: a game added from this PC only leaves the library (its files are never deleted).</summary>
    public string ConfirmLabel => _install.AddedFromPc && !IsSetupReady ? "Remove from library" : "Delete files";

    public string ConfirmText => IsSetupReady ? "Delete the downloaded setup files?"
        : _install.AddedFromPc ? "Remove it from the library? Its files stay where they are."
        : "Delete the game files? Saves are kept.";

    public void Load(InstalledGame install)
    {
        _loading = true;
        _install = install;
        Title = DisplayText.Title(install.Title);
        var system = Core.Emulation.GameSystems.Find(install.SystemId);
        if (system is null && install.ExecutablePath is null && install.WebGameUrl is not { Length: > 0 } && repository.GetGame(install.GameId) is { } game)
            system = Core.Emulation.GameSystems.FromText($"{game.Title} {string.Join(' ', game.Genres)} {game.PageUrl}");
        SystemName = system?.Name;
        CoverImageUrl = install.CoverImageUrl;
        IsUninstalling = install.State == GameInstallState.Uninstalling || launcher.IsUninstalling(install.GameId);
        IsSetupReady = install.State is GameInstallState.ReadyToInstall or GameInstallState.RunningInstaller;
        HasSetupFiles = install.State == GameInstallState.Installed && !string.IsNullOrEmpty(install.SetupDirectory) &&
                        Directory.Exists(install.SetupDirectory) &&
                        !string.Equals(Path.GetFullPath(install.SetupDirectory).TrimEnd('\\', '/'),
                            string.IsNullOrEmpty(install.InstallDirectory) ? "" : Path.GetFullPath(install.InstallDirectory).TrimEnd('\\', '/'),
                            StringComparison.OrdinalIgnoreCase);
        CanInstallSilently = InstallerDetector.SupportsSilent(install.InstallerKind);
        SilentInstall = install.SilentInstall && CanInstallSilently;
        InstallerText = IsSetupReady
            ? $"{install.InstallerEngine ?? GameDetailViewModel.DescribeInstaller(install.InstallerKind)}, will install to {install.InstallDirectory}"
            : string.Empty;
        Warning = install.LastError ??
                  (!IsSetupReady && install.ExecutablePath is null && install.WebGameUrl is null && install.RomPath is null ? "No executable found. Choose the .exe or .bat that starts the game." : null);
        _loading = false;
        Refresh();
    }

    public void Refresh()
    {
        IsRunning = launcher.IsRunning(_install.GameId);
        IsInstalling = installer.IsRunning(_install.GameId) || _install.State == GameInstallState.RunningInstaller;
        if (installer.GetSetupActivity(_install.GameId) is { } activity) ApplySetupActivity(activity);
        else ClearSetupActivity();
        PlaytimeText = IsSetupReady ? "Ready to install" : Formatting.Playtime(_install.TotalPlaytime);
        var lastPlayed = _install.LastPlayedAtUtc is { } last ? $"last played {last.ToLocalTime():d}" : "not played yet";
        var consolePrefix = Core.Emulation.GameSystems.Find(_install.SystemId) is { } console ? console.Name + ", " : string.Empty;
        DetailText = IsSetupReady
            ? InstallerText
            : $"{consolePrefix}{Formatting.Bytes(_install.SizeOnDiskBytes)}, {lastPlayed}";

        foreach (var name in new[] { nameof(PlayLabel), nameof(CanPlay), nameof(IsPlayable), nameof(InstallLabel), nameof(CanInstall),
                     nameof(RemoveLabel), nameof(ConfirmText), nameof(ConfirmLabel), nameof(HasWarning), nameof(HasCover), nameof(CanSetLaunchFile),
                     nameof(IsIncompleteSetup), nameof(CanKeepSetup) })
            OnPropertyChanged(name);
    }

    public void ApplySetupActivity(SetupActivity activity)
    {
        SetupActivityText = activity.Message;
        SetupNeedsAttention = activity.Phase is SetupPhase.NeedsInput or SetupPhase.Stuck;
        CanShowSetupWindow = activity.CanShowWindow || activity.Phase == SetupPhase.Working;
        CanRestartWithWizard = activity.CanRestartInteractive;
    }

    private void ClearSetupActivity()
    {
        SetupActivityText = null;
        SetupNeedsAttention = false;
        CanShowSetupWindow = false;
        CanRestartWithWizard = false;
    }

    [RelayCommand]
    private void ShowSetupWindow() => RunSafe(() =>
    {
        if (!installer.ShowSetupWindow(_install.GameId))
            throw new InvalidOperationException("The setup has no window to show right now.");
    });

    [RelayCommand]
    private void RestartWithWizard() => RunSafe(() =>
    {
        if (!installer.RestartWithWizard(_install.GameId))
            throw new InvalidOperationException("The setup isn't running silently, so there's nothing to restart.");
        SetupActivityText = "Closing the silent setup…";
        CanRestartWithWizard = false;
    });

    /// <summary>Persists the per-game silent preference as soon as it's toggled.</summary>
    partial void OnSilentInstallChanged(bool value)
    {
        if (_loading || !IsSetupReady) return;
        if (repository.GetInstall(_install.GameId) is not { } current) return;
        current.SilentInstall = value;
        repository.UpsertInstall(current);
        _install = current;
    }

    [RelayCommand]
    private Task PlayAsync() => RunSafeAsync(() => launcher.LaunchAsync(_install.GameId));

    [RelayCommand]
    private Task InstallAsync() => RunSafeAsync(async () =>
    {
        IsInstalling = true;
        Refresh();
        try
        {
            await actions.RunInstallerAsync(_install.GameId, SilentInstall);
        }
        finally
        {
            IsInstalling = installer.IsRunning(_install.GameId);
        }
    });

    /// <summary>First click arms the removal; the inline "Delete files" button confirms it.</summary>
    [RelayCommand]
    private void RequestUninstall() => IsConfirmingUninstall = true;

    [RelayCommand]
    private void CancelUninstall() => IsConfirmingUninstall = false;

    [RelayCommand]
    private Task ConfirmUninstallAsync() => RunSafeAsync(async () =>
    {
        IsConfirmingUninstall = false;
        IsUninstalling = true;
        try
        {
            if (IsSetupReady) await installer.DiscardInstallerAsync(_install.GameId);
            else await launcher.UninstallAsync(_install.GameId);
        }
        finally
        {
            IsUninstalling = false;
        }
    });

    /// <summary>"Uninstall, keep setup": the game goes, its setup files stay, and Install runs setup again.</summary>
    [RelayCommand]
    private Task ConfirmUninstallKeepSetupAsync() => RunSafeAsync(async () =>
    {
        IsConfirmingUninstall = false;
        IsUninstalling = true;
        try
        {
            await launcher.UninstallAsync(_install.GameId, keepSetupFiles: true);
        }
        finally
        {
            IsUninstalling = false;
        }
    });

    /// <summary>The uninstaller seems stuck (or its window was closed oddly): stop waiting and remove the folder.</summary>
    [RelayCommand]
    private void StopWaitingForUninstaller() => launcher.StopWaitingForUninstaller(_install.GameId);

    [RelayCommand]
    private void OpenFolder() => RunSafe(() =>
    {
        // A browser game has no folder: its page instead.
        if (_install.WebGamePageUrl is { Length: > 0 } page && string.IsNullOrEmpty(_install.InstallDirectory)) shell.OpenUrl(page);
        else shell.OpenFolder(IsSetupReady ? _install.SetupDirectory ?? string.Empty : _install.InstallDirectory);
    });

    /// <summary>Forget the chosen launch file and let SeaRaider find the game's program again.</summary>
    [RelayCommand]
    private Task DetectExecutableAsync() => RunSafeAsync(async () =>
    {
        await launcher.RedetectExecutableAsync(_install.GameId);
    });

    [RelayCommand]
    private Task ChooseExecutableAsync() => RunSafeAsync(async () =>
    {
        var path = await shell.PickExecutableAsync(_install.InstallDirectory);
        if (path is not null) launcher.SetExecutable(_install.GameId, path);
    });
}
