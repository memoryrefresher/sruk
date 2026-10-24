using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SeaRaider.App.Services;
using SeaRaider.Core;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Emulation;
using SeaRaider.Core.Models;
using SeaRaider.Core.Social;

namespace SeaRaider.App.ViewModels;

/// <summary>Library location, sources, download queue, setup programs and torrents — one tab each.</summary>
public sealed partial class SettingsViewModel(ILibraryRepository repository, ShellService shell, NavigationService navigation,
    SoundService sounds, IInstallerService installer, UpdateViewModel updates, ShortcutService shortcuts,
    SourcesViewModel sources) : PageViewModel
{
    /// <summary>Settings › Updates.</summary>
    public UpdateViewModel Updates { get; } = updates;

    /// <summary>Settings › Sources: add, update and remove download sources; import and export sources.txt.</summary>
    public SourcesViewModel Sources { get; } = sources;

    // ---- leftover setup files (Settings › Setup) ---------------------------------------------------

    /// <summary>"3 leftover setup folders, 18.2 GB" / "No leftover setup files".</summary>
    [ObservableProperty] private string _leftoverSetupText = "Checking…";
    [ObservableProperty] private bool _hasLeftoverSetupFiles;
    [ObservableProperty] private bool _isCleaningSetupFiles;

    private async Task CheckLeftoverSetupFilesAsync()
    {
        try
        {
            var leftovers = await Task.Run(() => installer.GetLeftoverSetupFiles());
            ShowLeftovers(leftovers);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LeftoverSetupText = $"Couldn't look: {ex.Message}";
        }
    }

    private void ShowLeftovers(SetupLeftovers leftovers)
    {
        HasLeftoverSetupFiles = leftovers.Folders > 0;
        LeftoverSetupText = leftovers.Folders == 0
            ? "No leftover setup files."
            : $"{leftovers.Folders} leftover setup {(leftovers.Folders == 1 ? "folder" : "folders")}, {Formatting.Bytes(leftovers.Bytes)}, from games that were removed or uninstalled.";
    }

    [RelayCommand]
    private Task CleanUpSetupFilesAsync() => RunSafeAsync(async () =>
    {
        IsCleaningSetupFiles = true;
        try
        {
            var left = await Task.Run(() => installer.CleanUpLeftoverSetupFilesAsync());
            ShowLeftovers(left);
            if (left.Problem is { } problem) throw new IOException(problem);
        }
        finally
        {
            IsCleaningSetupFiles = false;
        }
    });

    [RelayCommand]
    private void OpenSetupFolder() => RunSafe(() =>
    {
        var root = Core.Installing.SetupPaths.SetupRoot(repository.GetSettings().LibraryRoot);
        shell.OpenFolder(Directory.Exists(root) ? root : repository.GetSettings().LibraryRoot);
    });

    // ---- sounds (saved as soon as they change, like the skin) --------------------------------------
    [ObservableProperty] private bool _soundsEnabled = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SoundVolumeText))]
    private double _soundVolume = 45;
    [ObservableProperty] private bool _soundClicks = true;
    [ObservableProperty] private bool _soundDownloads = true;
    [ObservableProperty] private bool _soundFriends = true;
    [ObservableProperty] private bool _soundAlerts = true;

    /// <summary>The faint background sound, and its own volume.</summary>
    [ObservableProperty] private bool _ambienceEnabled = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AmbienceVolumeText))]
    private double _ambienceVolume = 2;

    public string AmbienceVolumeText => $"{(int)Math.Round(AmbienceVolume)} %";
    partial void OnAmbienceEnabledChanged(bool value) => SaveSoundsSoon();
    partial void OnAmbienceVolumeChanged(double value) => SaveSoundsSoon();
    [ObservableProperty] private bool _animations = true;
    [ObservableProperty] private bool _blurAndGlow = true;

    partial void OnAnimationsChanged(bool value) => SaveSoundsSoon();
    partial void OnBlurAndGlowChanged(bool value) => SaveSoundsSoon();
    private bool _loadingSounds;
    private Avalonia.Threading.DispatcherTimer? _soundSaveTimer;

    public string SoundVolumeText => $"{(int)Math.Round(SoundVolume)} %";

    partial void OnSoundsEnabledChanged(bool value) => SaveSoundsSoon();
    partial void OnSoundVolumeChanged(double value) => SaveSoundsSoon();
    partial void OnSoundClicksChanged(bool value) => SaveSoundsSoon();
    partial void OnSoundDownloadsChanged(bool value) => SaveSoundsSoon();
    partial void OnSoundFriendsChanged(bool value) => SaveSoundsSoon();
    partial void OnSoundAlertsChanged(bool value) => SaveSoundsSoon();

    /// <summary>The volume slider moves in many small steps: save once it rests.</summary>
    private void SaveSoundsSoon()
    {
        if (_loadingSounds) return;
        if (_soundSaveTimer is null)
        {
            _soundSaveTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _soundSaveTimer.Tick += (_, _) =>
            {
                _soundSaveTimer.Stop();
                var s = repository.GetSettings();
                s.SoundsEnabled = SoundsEnabled;
                s.SoundVolume = (int)Math.Round(Math.Clamp(SoundVolume, 0, 100));
                s.SoundClicks = SoundClicks;
                s.SoundDownloads = SoundDownloads;
                s.SoundFriends = SoundFriends;
                s.SoundAlerts = SoundAlerts;
                s.AmbienceEnabled = AmbienceEnabled;
                s.AmbienceVolume = (int)Math.Round(Math.Clamp(AmbienceVolume, 0, 100));
                s.Animations = Animations;
                s.BlurAndGlow = BlurAndGlow;
                repository.SaveSettings(s);
            };
        }
        _soundSaveTimer.Stop();
        _soundSaveTimer.Start();
    }

    /// <summary>"Play a sample": the download chime at the slider's volume.</summary>
    [RelayCommand]
    private void PreviewSound() => sounds.Preview(UiSound.DownloadComplete, (int)Math.Round(SoundVolume));

    /// <summary>The selected tab, as its <see cref="SettingsTab"/> number (General 0, Sources 1, Downloads 2 …).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSourcesTab), nameof(IsNotSourcesTab), nameof(PageMaxWidth))]
    private int _selectedTabIndex;

    /// <summary>Sources saves as you go (like Skin and Sounds), so the Save settings row is hidden there.</summary>
    public bool IsSourcesTab => SelectedTabIndex == (int)SettingsTab.Sources;
    public bool IsNotSourcesTab => !IsSourcesTab;

    /// <summary>The source list's buttons need more room than the other tabs.</summary>
    public double PageMaxWidth => IsSourcesTab ? 1000 : 780;

    partial void OnSelectedTabIndexChanged(int value)
    {
        if (value == (int)SettingsTab.Sources) Sources.OnNavigatedTo(); // read the list and the site readers afresh
    }

    // ---- downloads tab ---------------------------------------------------------------------------
    [ObservableProperty] private decimal _maxActiveTorrents = 2;
    [ObservableProperty] private decimal _maxActiveExtractions = 1;
    [ObservableProperty] private decimal _globalDownloadLimitKBps;
    [ObservableProperty] private decimal _globalUploadLimitKBps;
    [ObservableProperty] private bool _seedAfterDownload;
    [ObservableProperty] private decimal _maxActiveSeeds = 2;
    [ObservableProperty] private decimal _seedRatioLimit = 1;
    [ObservableProperty] private decimal _seedTimeLimitMinutes = 60;

    // ---- friends tab -----------------------------------------------------------------------------
    [ObservableProperty] private bool _friendsEnabled = true;
    [ObservableProperty] private string _relayUrl = string.Empty;

    /// <summary>"Default: wss://…" or a note that this build has none.</summary>
    public string DefaultRelayText => RelayDefaults.DefaultRelayUrl is { } relay
        ? $"Leave empty to use this build's relay: {relay}"
        : "This build has no default relay: enter one, or ask whoever runs your group's relay for the address.";

    // ---- media & verification (General tab) ------------------------------------------------------
    [ObservableProperty] private bool _useSteamMedia = true;
    [ObservableProperty] private bool _preferSteamInfo = true;
    [ObservableProperty] private string _igdbClientId = string.Empty;
    [ObservableProperty] private string _igdbClientSecret = string.Empty;
    [ObservableProperty] private bool _autoInstallWebView2 = true;

    // ---- v0.6 --------------------------------------------------------------------------------------
    /// <summary>0 = Automatic, 1 = Always, 2 = Never (order of <see cref="LowDiskMode"/>).</summary>
    [ObservableProperty] private int _lowDiskModeIndex;
    [ObservableProperty] private bool _browserAutoClick = true;

    // ---- Media player (General tab) ----------------------------------------------------------------
    /// <summary>Preferred player sites for titles, as typed: one per line or comma ("embed.st").</summary>
    [ObservableProperty] private string _mediaPreferredPlayers = string.Empty;

    /// <summary>Which stream button to pick on a title's page (1 = "Stream 1"); 0 = don't pick.</summary>
    [ObservableProperty] private decimal _mediaStreamNumber = 1;

    /// <summary>Click the stream button in a hidden browser when the page only shows its player after a click.</summary>
    [ObservableProperty] private bool _mediaClickStreams = true;

    /// <summary>Video quality: 0 Highest (adapts if needed), 1 Always highest, 2 Automatic, 3 Data saver.</summary>
    [ObservableProperty] private int _playerQualityIndex;

    private static readonly string[] QualityModes = ["highest", "max", "auto", "saver"];

    /// <summary>The sportsbook beside the player (off by default).</summary>
    [ObservableProperty] private bool _betSlipEnabled;
    [ObservableProperty] private bool _betSlipAutoOpen = true;
    [ObservableProperty] private string _betSlipUrl = "https://roobet.com/sports";
    [ObservableProperty] private bool _routeGameShortcuts = true;
    [ObservableProperty] private bool _muteSetupAudio = true;
    [ObservableProperty] private bool _shareGameActivity = true;

    // Discord status (Settings › Friends › Discord)
    [ObservableProperty] private bool _discordPresence = true;
    [ObservableProperty] private bool _discordAddButton = true;
    [ObservableProperty] private string _discordAppId = string.Empty;
    [ObservableProperty] private bool _playerSoundForDiscord = true;

    [RelayCommand]
    private void OpenDiscordDevelopers() => shell.OpenUrl("https://discord.com/developers/applications");
    [ObservableProperty] private bool _shareMediaActivity = true;
    [ObservableProperty] private decimal _awayAfterMinutes = 10;

    // ---- setup tab -------------------------------------------------------------------------------
    [ObservableProperty] private bool _showSetupProgress;
    [ObservableProperty] private decimal _installStallSeconds = 15;
    [ObservableProperty] private bool _autoFallbackToInteractive = true;

    [ObservableProperty] private string _libraryRoot = string.Empty;
    [ObservableProperty] private string _tempRoot = string.Empty;
    [ObservableProperty] private decimal _maxConcurrentJobs = 2;
    [ObservableProperty] private bool _preferTorrents;
    [ObservableProperty] private bool _deleteArchivesAfterInstall;
    [ObservableProperty] private bool _preserveInFolderSaves;
    [ObservableProperty] private bool _askBeforeDownload;
    [ObservableProperty] private bool _preferSilentInstall;
    [ObservableProperty] private bool _autoRunInstallers;
    /// <summary>0 = Ask, 1 = Delete, 2 = Keep (order of <see cref="SetupFilesMode"/>).</summary>
    [ObservableProperty] private int _setupFilesIndex;

    /// <summary>0 = install quietly, 1 = skip, 2 = show their installers (order of <see cref="SetupRuntimes"/>).</summary>
    [ObservableProperty] private int _setupRuntimesIndex;

    [ObservableProperty] private bool _blockSetupWebPages = true;
    [ObservableProperty] private bool _uncheckSetupLowRam = true;
    [ObservableProperty] private decimal _recrawlAfterDays = 14;
    [ObservableProperty] private bool _addPublicTrackers;
    [ObservableProperty] private string _publicTrackersText = string.Empty;
    [ObservableProperty] private decimal _torrentNoPeersTimeoutMinutes = 10;
    [ObservableProperty] private string? _savedMessage;

    // ---- look & store (v0.7) -----------------------------------------------------------------------
    /// <summary>0 = The Next Generation, 1 = Legacy (order of <see cref="AppSkin"/>). Applied at once, no Save needed.</summary>
    [ObservableProperty] private int _skinIndex;

    /// <summary>Shows the expert tabs and controls everywhere. Applied at once, no Save needed.</summary>
    [ObservableProperty] private bool _showAdvancedOptions;

    partial void OnShowAdvancedOptionsChanged(bool value)
    {
        if (!value && SelectedTabIndex is (int)SettingsTab.Downloads or (int)SettingsTab.Setup or (int)SettingsTab.Torrents)
            SelectedTabIndex = (int)SettingsTab.General;
        if (_loading) return;
        var settings = repository.GetSettings();
        if (settings.ShowAdvancedOptions == value) return;
        settings.ShowAdvancedOptions = value;
        repository.SaveSettings(settings);
    }
    // ---- emulators (Settings › General) ------------------------------------------------------------

    /// <summary>One row per system: the emulator that plays its games.</summary>
    public System.Collections.ObjectModel.ObservableCollection<EmulatorRowViewModel> EmulatorRows { get; } = new();

    /// <summary>The console whose emulator is shown (Settings › Emulators: one at a time).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedEmulatorRow))]
    private EmulatorRowViewModel? _selectedEmulatorRow;

    public bool HasSelectedEmulatorRow => SelectedEmulatorRow is not null;

    /// <summary>Which games the store opens on: PC games unless chosen otherwise.</summary>
    [ObservableProperty] private SystemChoice _storeStartSystem = StorefrontViewModel.SystemChoices[1];

    [ObservableProperty] private string? _emulatorScanText;

    /// <summary>RetroArch plays every system it has a core for (on by default).</summary>
    [ObservableProperty] private bool _preferRetroArch = true;
    [ObservableProperty] private bool _romInfoFromWeb = true;
    [ObservableProperty] private bool _showConsoleGames;
    [ObservableProperty] private bool _hideNonEnglish = true;

    /// <summary>retroarch.exe; empty = found by itself.</summary>
    [ObservableProperty] private string _retroArchPath = string.Empty;

    [RelayCommand]
    private async Task BrowseRetroArchAsync()
    {
        if (await shell.PickEmulatorAsync("RetroArch") is { } path) RetroArchPath = path;
    }
    [ObservableProperty] private bool _isScanningEmulators;

    private void LoadEmulatorRows(AppSettings settings)
    {
        EmulatorRows.Clear();
        foreach (var system in GameSystems.All)
        {
            var saved = settings.Emulators.FirstOrDefault(e => string.Equals(e.SystemId, system.Id, StringComparison.OrdinalIgnoreCase));
            EmulatorRows.Add(new EmulatorRowViewModel(system, saved?.ExePath ?? string.Empty, saved?.Arguments ?? string.Empty, shell.PickEmulatorAsync));
        }
        SelectedEmulatorRow = EmulatorRows.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.ExePath)) ?? EmulatorRows.FirstOrDefault();
    }

    /// <summary>Looks for emulators installed on this PC and fills in the systems that have none yet (nothing is saved until Save).</summary>
    [RelayCommand]
    private Task FindEmulatorsAsync() => RunSafeAsync(async () =>
    {
        IsScanningEmulators = true;
        EmulatorScanText = "Looking for emulators…";
        try
        {
            var root = string.IsNullOrWhiteSpace(LibraryRoot) ? repository.GetSettings().LibraryRoot : LibraryRoot;
            var preferRetroArch = PreferRetroArch;
            var found = await Task.Run(() => EmulatorFinder.Scan(EmulatorFinder.DefaultFolders(root), preferRetroArch));
            var retroArch = found.Values.FirstOrDefault(RetroArch.IsRetroArch)
                            ?? await Task.Run(() => RetroArch.UsualPlaces().FirstOrDefault(File.Exists));
            if (retroArch is not null && string.IsNullOrWhiteSpace(RetroArchPath)) RetroArchPath = retroArch;
            var filled = 0;
            foreach (var row in EmulatorRows)
            {
                if (!found.TryGetValue(row.SystemId, out var path)) continue;
                // With RetroArch for everything, its systems switch to it; otherwise only empty rows are filled.
                var replace = preferRetroArch && RetroArch.IsRetroArch(path) && !RetroArch.IsRetroArch(row.ExePath);
                if (!string.IsNullOrWhiteSpace(row.ExePath) && !replace) continue;
                row.ExePath = path;
                filled++;
            }
            EmulatorScanText = filled == 0
                ? "No emulators found in the usual places. Use Browse to point a system at its emulator."
                : $"Found emulators for {filled} {(filled == 1 ? "system" : "systems")}. Press Save to keep them.";
        }
        finally
        {
            IsScanningEmulators = false;
        }
    });

    [ObservableProperty] private bool _hideNonGames = true;
    [ObservableProperty] private bool _hideAdultGames = true;

    /// <summary>Store › Multiplayer: titles with "online" in them that aren't online games (comma separated).</summary>
    [ObservableProperty] private string _multiplayerExceptions = SeaRaider.Core.Catalog.MultiplayerGames.DefaultExceptions;
    [ObservableProperty] private string _hiddenGamesText = string.Empty;
    [ObservableProperty] private bool _hasHiddenGames;
    private bool _loading;

    partial void OnSkinIndexChanged(int value)
    {
        if (_loading || !Enum.IsDefined(typeof(AppSkin), value)) return;
        var settings = repository.GetSettings();
        if ((int)settings.Skin == value) return;
        settings.Skin = (AppSkin)value;
        repository.SaveSettings(settings); // SkinService switches the look as soon as this is saved
    }

    private void ShowHiddenCount(AppSettings settings)
    {
        var count = settings.HiddenStoreItems.Count;
        HasHiddenGames = count > 0;
        HiddenGamesText = count switch
        {
            0 => "No games hidden with the \u00D7 on their card.",
            1 => $"1 game hidden: {settings.HiddenStoreItems[0].Title}",
            _ => $"{count} games hidden, most recently {settings.HiddenStoreItems[^1].Title}",
        };
    }

    [RelayCommand]
    private void ShowHiddenGames()
    {
        var settings = repository.GetSettings();
        settings.HiddenStoreItems.Clear();
        repository.SaveSettings(settings);
        ShowHiddenCount(settings);
    }

    public string DataFolder => AppPaths.DataDirectory;

    public override void OnNavigatedTo()
    {
        _ = CheckLeftoverSetupFilesAsync();
        var s = repository.GetSettings();
        _loadingSounds = true;
        SoundsEnabled = s.SoundsEnabled;
        SoundVolume = s.SoundVolume;
        SoundClicks = s.SoundClicks;
        SoundDownloads = s.SoundDownloads;
        SoundFriends = s.SoundFriends;
        SoundAlerts = s.SoundAlerts;
        AmbienceEnabled = s.AmbienceEnabled;
        AmbienceVolume = s.AmbienceVolume;
        Animations = s.Animations;
        BlurAndGlow = s.BlurAndGlow;
        _loadingSounds = false;
        _loading = true;
        SkinIndex = (int)s.Skin;
        ShowAdvancedOptions = s.ShowAdvancedOptions;
        _loading = false;
        HideNonGames = s.HideNonGames;
        HideAdultGames = s.HideAdultGames;
        MultiplayerExceptions = s.MultiplayerExceptions ?? SeaRaider.Core.Catalog.MultiplayerGames.DefaultExceptions;
        LoadEmulatorRows(s);
        ShowHiddenCount(s);
        LibraryRoot = s.LibraryRoot;
        TempRoot = s.TempRoot;
        MaxConcurrentJobs = s.MaxConcurrentJobs;
        PreferTorrents = s.PreferTorrents;
        DeleteArchivesAfterInstall = s.DeleteArchivesAfterInstall;
        PreserveInFolderSaves = s.PreserveInFolderSaves;
        AskBeforeDownload = s.AskBeforeDownload;
        PreferSilentInstall = s.PreferSilentInstall;
        AutoRunInstallers = s.AutoRunInstallers;
        SetupFilesIndex = (int)s.SetupFilesAfterInstall;
        SetupRuntimesIndex = (int)s.SetupRuntimes;
        BlockSetupWebPages = s.BlockSetupWebPages;
        UncheckSetupLowRam = s.UncheckSetupLowRam;
        RecrawlAfterDays = s.RecrawlAfterDays;
        AddPublicTrackers = s.AddPublicTrackers;
        PublicTrackersText = string.Join(Environment.NewLine, s.PublicTrackers);
        TorrentNoPeersTimeoutMinutes = s.TorrentNoPeersTimeoutMinutes;
        MaxActiveTorrents = s.MaxActiveTorrents;
        MaxActiveExtractions = s.MaxActiveExtractions;
        GlobalDownloadLimitKBps = s.GlobalDownloadLimitKBps;
        GlobalUploadLimitKBps = s.GlobalUploadLimitKBps;
        SeedAfterDownload = s.SeedAfterDownload;
        MaxActiveSeeds = s.MaxActiveSeeds;
        SeedRatioLimit = (decimal)s.SeedRatioLimit;
        SeedTimeLimitMinutes = s.SeedTimeLimitMinutes;
        ShowSetupProgress = s.ShowSetupProgress;
        InstallStallSeconds = s.InstallStallSeconds;
        AutoFallbackToInteractive = s.AutoFallbackToInteractive;
        FriendsEnabled = s.FriendsEnabled;
        RelayUrl = s.RelayUrl ?? string.Empty;
        UseSteamMedia = s.UseSteamMedia;
        PreferSteamInfo = s.PreferSteamInfo;
        IgdbClientId = s.IgdbClientId ?? string.Empty;
        IgdbClientSecret = s.IgdbClientSecret ?? string.Empty;
        AutoInstallWebView2 = s.AutoInstallWebView2;
        LowDiskModeIndex = (int)s.LowDiskSpaceMode;
        BrowserAutoClick = s.BrowserAutoClick;
        PreferRetroArch = s.PreferRetroArch;
        RomInfoFromWeb = s.RomInfoFromWeb;
        ShowConsoleGames = s.ShowConsoleGames;
        StoreStartSystem = StorefrontViewModel.SystemChoices.FirstOrDefault(c => c.Id == s.StoreStartSystem) ?? StorefrontViewModel.SystemChoices[1];
        HideNonEnglish = s.HideNonEnglish;
        RetroArchPath = s.RetroArchPath ?? string.Empty;
        MediaPreferredPlayers = string.Join(", ", s.MediaPreferredPlayers);
        MediaStreamNumber = s.MediaStreamNumber;
        MediaClickStreams = s.MediaClickStreams;
        PlayerQualityIndex = Math.Max(0, Array.IndexOf(QualityModes, s.PlayerQuality));
        BetSlipEnabled = s.BetSlipEnabled;
        BetSlipAutoOpen = s.BetSlipAutoOpen;
        BetSlipUrl = s.BetSlipUrl;
        RouteGameShortcuts = s.RouteGameShortcuts;
        MuteSetupAudio = s.MuteSetupAudio;
        ShareGameActivity = s.ShareGameActivity;
        DiscordPresence = s.DiscordPresence;
        DiscordAddButton = s.DiscordAddButton;
        DiscordAppId = s.DiscordAppId ?? string.Empty;
        PlayerSoundForDiscord = s.PlayerSoundForDiscord;
        ShareMediaActivity = s.ShareMediaActivity;
        AwayAfterMinutes = s.AwayAfterMinutes;
        SavedMessage = null;

        if (navigation.RequestedSettingsTab is { } tab)
        {
            // Asked for an expert tab (e.g. from the Downloads page): show the expert settings.
            if (tab is SettingsTab.Downloads or SettingsTab.Setup or SettingsTab.Torrents) ShowAdvancedOptions = true;
            SelectedTabIndex = (int)tab;
            navigation.RequestedSettingsTab = null;
        }

        // Settings was opened while the Sources tab was already selected (the change handler above didn't run).
        if (SelectedTabIndex == (int)SettingsTab.Sources) Sources.OnNavigatedTo();
    }

    [RelayCommand]
    private void Save() => RunSafe(() =>
    {
        if (string.IsNullOrWhiteSpace(LibraryRoot) || !Path.IsPathFullyQualified(LibraryRoot))
            throw new ArgumentException("Library folder must be a full path, like C:\\MyGames.");

        Directory.CreateDirectory(LibraryRoot);
        Directory.CreateDirectory(TempRoot);

        var s = repository.GetSettings();
        s.LibraryRoot = LibraryRoot;
        s.TempRoot = TempRoot;
        s.Emulators = EmulatorRows
            .Where(r => !string.IsNullOrWhiteSpace(r.ExePath))
            .Select(r => new EmulatorSetting
            {
                SystemId = r.SystemId,
                ExePath = r.ExePath.Trim().Trim('"'),
                Arguments = string.IsNullOrWhiteSpace(r.Arguments) ? null : r.Arguments.Trim(),
            })
            .ToList();
        s.MaxConcurrentJobs = (int)Math.Clamp(MaxConcurrentJobs, 1, 20);
        s.MaxActiveTorrents = (int)Math.Clamp(MaxActiveTorrents, 1, 20);
        s.MaxActiveExtractions = (int)Math.Clamp(MaxActiveExtractions, 1, 8);
        s.GlobalDownloadLimitKBps = (int)Math.Clamp(GlobalDownloadLimitKBps, 0, 10_000_000);
        s.GlobalUploadLimitKBps = (int)Math.Clamp(GlobalUploadLimitKBps, 0, 10_000_000);
        s.SeedAfterDownload = SeedAfterDownload;
        s.MaxActiveSeeds = (int)Math.Clamp(MaxActiveSeeds, 0, 50);
        s.SeedRatioLimit = (double)Math.Clamp(SeedRatioLimit, 0, 100);
        s.SeedTimeLimitMinutes = (int)Math.Clamp(SeedTimeLimitMinutes, 0, 100_000);
        s.ShowSetupProgress = ShowSetupProgress;
        s.InstallStallSeconds = (int)Math.Clamp(InstallStallSeconds, 5, 300);
        s.AutoFallbackToInteractive = AutoFallbackToInteractive;
        if (!string.IsNullOrWhiteSpace(RelayUrl) && !FriendsService.TryParseRelay(RelayUrl, out _))
            throw new ArgumentException("The friends relay address should look like wss://relay.example.com/ws or ws://192.168.1.20:5280/ws.");
        s.RelayUrl = string.IsNullOrWhiteSpace(RelayUrl) ? null : RelayUrl.Trim();
        s.FriendsEnabled = FriendsEnabled;
        s.UseSteamMedia = UseSteamMedia;
        s.PreferSteamInfo = PreferSteamInfo;
        s.IgdbClientId = string.IsNullOrWhiteSpace(IgdbClientId) ? null : IgdbClientId.Trim();
        s.IgdbClientSecret = string.IsNullOrWhiteSpace(IgdbClientSecret) ? null : IgdbClientSecret.Trim();
        s.AutoInstallWebView2 = AutoInstallWebView2;
        s.HideNonGames = HideNonGames;
        s.HideAdultGames = HideAdultGames;
        s.MultiplayerExceptions = (MultiplayerExceptions ?? string.Empty).Trim();
        s.LowDiskSpaceMode = Enum.IsDefined(typeof(LowDiskMode), LowDiskModeIndex) ? (LowDiskMode)LowDiskModeIndex : LowDiskMode.Automatic;
        s.BrowserAutoClick = BrowserAutoClick;
        s.PreferRetroArch = PreferRetroArch;
        s.RomInfoFromWeb = RomInfoFromWeb;
        s.ShowConsoleGames = ShowConsoleGames;
        s.StoreStartSystem = StoreStartSystem?.Id;
        s.HideNonEnglish = HideNonEnglish;
        var retroArchPath = RetroArchPath.Trim().Trim('"');
        if (retroArchPath.Length > 0 && !File.Exists(retroArchPath))
            throw new ArgumentException("RetroArch wasn't found at that path. Point it at retroarch.exe, or leave it empty to have it found.");
        s.RetroArchPath = retroArchPath.Length == 0 ? null : retroArchPath;
        s.MediaPreferredPlayers = SeaRaider.Core.Scraping.PlayerPreference.Normalize([MediaPreferredPlayers]);
        MediaPreferredPlayers = string.Join(", ", s.MediaPreferredPlayers); // shown tidied: "https://www.embed.st/" → "embed.st"
        s.MediaStreamNumber = (int)Math.Clamp(MediaStreamNumber, 0, 20);
        s.MediaClickStreams = MediaClickStreams;
        s.PlayerQuality = QualityModes[Math.Clamp(PlayerQualityIndex, 0, QualityModes.Length - 1)];
        var betSlip = string.IsNullOrWhiteSpace(BetSlipUrl) ? "https://roobet.com/sports" : BetSlipUrl.Trim();
        if (!betSlip.Contains("://", StringComparison.Ordinal)) betSlip = "https://" + betSlip;
        if (!Uri.TryCreate(betSlip.Replace("{query}", "x").Replace("{home}", "x").Replace("{away}", "x"), UriKind.Absolute, out var betUri) ||
            betUri.Scheme != "https")
            throw new ArgumentException("The bet slip address should be a web address starting with https://, like https://roobet.com/sports.");
        s.BetSlipEnabled = BetSlipEnabled;
        s.BetSlipAutoOpen = BetSlipAutoOpen;
        s.BetSlipUrl = betSlip;
        BetSlipUrl = betSlip;
        s.MuteSetupAudio = MuteSetupAudio;
        s.ShareGameActivity = ShareGameActivity;
        var discordId = (DiscordAppId ?? string.Empty).Trim();
        if (discordId.Length > 0 && (!discordId.All(char.IsAsciiDigit) || discordId.Length is < 15 or > 22))
            throw new ArgumentException("The Discord Application ID is the long number on your application's General Information page (like 1290345678901234567).");
        s.DiscordAppId = discordId.Length > 0 ? discordId : null;
        s.DiscordPresence = DiscordPresence;
        s.DiscordAddButton = DiscordAddButton;
        s.PlayerSoundForDiscord = PlayerSoundForDiscord;
        s.ShareMediaActivity = ShareMediaActivity;
        s.AwayAfterMinutes = (int)Math.Clamp(AwayAfterMinutes, 0, 240);
        s.PreferTorrents = PreferTorrents;
        s.DeleteArchivesAfterInstall = DeleteArchivesAfterInstall;
        s.PreserveInFolderSaves = PreserveInFolderSaves;
        s.AskBeforeDownload = AskBeforeDownload;
        s.PreferSilentInstall = PreferSilentInstall;
        s.AutoRunInstallers = AutoRunInstallers;
        s.SetupFilesAfterInstall = Enum.IsDefined(typeof(SetupFilesMode), SetupFilesIndex) ? (SetupFilesMode)SetupFilesIndex : SetupFilesMode.Ask;
        s.SetupRuntimes = Enum.IsDefined(typeof(SetupRuntimes), SetupRuntimesIndex) ? (SetupRuntimes)SetupRuntimesIndex : SetupRuntimes.InstallQuietly;
        s.BlockSetupWebPages = BlockSetupWebPages;
        s.UncheckSetupLowRam = UncheckSetupLowRam;
        s.RecrawlAfterDays = (int)Math.Clamp(RecrawlAfterDays, 1, 365);
        s.AddPublicTrackers = AddPublicTrackers;
        s.PublicTrackers = ParseTrackers(PublicTrackersText);
        s.TorrentNoPeersTimeoutMinutes = (int)Math.Clamp(TorrentNoPeersTimeoutMinutes, 0, 1440);
        var shortcutsChanged = s.RouteGameShortcuts != RouteGameShortcuts;
        s.RouteGameShortcuts = RouteGameShortcuts;
        repository.SaveSettings(s);
        if (shortcutsChanged)
        {
            if (RouteGameShortcuts) shortcuts.RouteAll();
            else shortcuts.RestoreAll();
        }

        SavedMessage = "Saved. Download limits apply right away; new installs go to the library folder above.";
    });

    /// <summary>First click asks; the "Reset everything" button confirms.</summary>
    [ObservableProperty] private bool _isConfirmingReset;

    [RelayCommand]
    private void RequestReset() => IsConfirmingReset = true;

    [RelayCommand]
    private void CancelReset() => IsConfirmingReset = false;

    [RelayCommand]
    private void ResetToDefaults() => RunSafe(() =>
    {
        IsConfirmingReset = false;
        repository.SaveSettings(repository.GetSettings().WithDefaults());
        OnNavigatedTo(); // show the defaults
        SavedMessage = "Settings are back to their defaults. Your library folder, hidden games and wishlist were kept.";
    });

    [RelayCommand]
    private void ResetTrackers() => PublicTrackersText = string.Join(Environment.NewLine, AppSettings.DefaultPublicTrackers);

    /// <summary>One tracker URL per line; rejects anything that isn't udp/http/https.</summary>
    private static List<string> ParseTrackers(string text)
    {
        var trackers = new List<string>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Uri.TryCreate(line, UriKind.Absolute, out var uri) || uri.Scheme is not ("udp" or "http" or "https"))
                throw new ArgumentException($"\"{line}\" isn't a tracker address. Use one udp://, http:// or https:// URL per line.");
            trackers.Add(uri.OriginalString);
        }
        return trackers.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    [RelayCommand]
    private Task BrowseLibraryAsync() => RunSafeAsync(async () =>
    {
        if (await shell.PickFolderAsync("Choose where games are installed", LibraryRoot) is { } folder) LibraryRoot = folder;
    });

    [RelayCommand]
    private Task BrowseTempAsync() => RunSafeAsync(async () =>
    {
        if (await shell.PickFolderAsync("Choose a folder for temporary downloads", TempRoot) is { } folder) TempRoot = folder;
    });

    [RelayCommand]
    private void OpenDataFolder() => RunSafe(() => shell.OpenFolder(AppPaths.DataDirectory));
}
