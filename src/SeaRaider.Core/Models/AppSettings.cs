using LiteDB;

namespace SeaRaider.Core.Models;

/// <summary>User-tunable settings. Stored as a single LiteDB document (Id = 1).</summary>
public sealed class AppSettings
{
    [BsonId]
    public int Id { get; set; } = 1;

    /// <summary>
    /// Settings back to their defaults, keeping what isn't a preference: the library and temporary folders (games and
    /// downloads live there), the games hidden from the store, and the IGDB keys the user typed in.
    /// </summary>
    public AppSettings WithDefaults() => new()
    {
        Id = Id,
        LibraryRoot = LibraryRoot,
        TempRoot = TempRoot,
        HiddenStoreItems = [.. HiddenStoreItems],
        Emulators = [.. Emulators], // where the player's emulators are, not a preference
        IgdbClientId = IgdbClientId,
        IgdbClientSecret = IgdbClientSecret,
        FirstRun = FirstRun, // not a preference: a reset must not bring the welcome questions back
    };

    /// <summary>
    /// Where the first-run welcome questions (sources file, name, folders) stand. <see cref="FirstRunState.Unknown"/> until the
    /// app has decided: a brand-new install becomes Pending, an install from before this existed becomes Completed.
    /// </summary>
    public FirstRunState FirstRun { get; set; }

    /// <summary>Where games are installed. Each game gets &lt;LibraryRoot&gt;\&lt;folder name&gt;.</summary>
    public string LibraryRoot { get; set; } = OperatingSystem.IsWindows()
        ? @"C:\MyGames"
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "MyGames");

    /// <summary>Scratch space for archives and torrent payloads before extraction.</summary>
    public string TempRoot { get; set; } = Path.Combine(AppPaths.DataDirectory, "temp");

    /// <summary>Maximum active downloads (HTTP and torrent together). Queued games wait for a free slot.</summary>
    public int MaxConcurrentJobs { get; set; } = 2;

    /// <summary>Of those, how many may be torrents.</summary>
    public int MaxActiveTorrents { get; set; } = 2;

    /// <summary>How many finished downloads may extract at once (extraction is disk-heavy; 1 is usually fastest).</summary>
    public int MaxActiveExtractions { get; set; } = 1;

    /// <summary>Overall download limit in KB/s for all downloads together (0 = unlimited).</summary>
    public int GlobalDownloadLimitKBps { get; set; }

    /// <summary>Overall torrent upload limit in KB/s (0 = unlimited).</summary>
    public int GlobalUploadLimitKBps { get; set; }

    /// <summary>Keep sharing a finished torrent after it's extracted, until the ratio or time limit.</summary>
    public bool SeedAfterDownload { get; set; }

    /// <summary>How many finished torrents may seed at once; the rest stop when done.</summary>
    public int MaxActiveSeeds { get; set; } = 2;

    /// <summary>Stop seeding at this upload/download ratio (0 = no ratio limit).</summary>
    public double SeedRatioLimit { get; set; } = 1.0;

    /// <summary>Stop seeding after this many minutes (0 = no time limit).</summary>
    public int SeedTimeLimitMinutes { get; set; } = 60;

    /// <summary>When a page offers both HTTP and torrent links, pick the torrent.</summary>
    public bool PreferTorrents { get; set; }

    /// <summary>
    /// Unpack multi-part RAR downloads while they download, deleting each part once it's unpacked, so a game needs about
    /// its unpacked size on disk instead of archive + unpacked. Automatic turns it on when space is short.
    /// </summary>
    public LowDiskMode LowDiskSpaceMode { get; set; } = LowDiskMode.Automatic;

    /// <summary>
    /// Media player: the player sites to use for titles from a site's list, best first ("embed.st"). When a title's page offers
    /// several streams, a player on one of these is picked. Empty: whatever player the page has.
    /// </summary>
    public List<string> MediaPreferredPlayers { get; set; } = [];

    /// <summary>
    /// Video quality in SeaRaider's player for streams and videos it plays itself: "highest" (start at the best and stay
    /// there unless the connection can't keep up, the default), "max" (always the best), "auto" (the player's own balance)
    /// or "saver" (480p at most). Any video can still be switched in the player's quality menu.
    /// </summary>
    public string PlayerQuality { get; set; } = "highest";

    /// <summary>Media player: which stream / server button to pick on a title's page (1 = "Stream 1"); 0 = don't pick.</summary>
    public int MediaStreamNumber { get; set; } = 1;

    /// <summary>
    /// Media player: when a title's page only shows its player after "Stream 1" is clicked, open it in a hidden browser, click
    /// it, and play the player that loads.
    /// </summary>
    public bool MediaClickStreams { get; set; } = true;

    /// <summary>
    /// A bet slip next to SeaRaider's player: the sportsbook's own page (<see cref="BetSlipUrl"/>) in a panel beside the video,
    /// signed in with the user's own account there. Off until switched on.
    /// </summary>
    public bool BetSlipEnabled { get; set; }

    /// <summary>
    /// The sportsbook page the bet slip panel opens. May hold {query} (the match or title), {home} and {away} (a match's sides),
    /// filled in for what's playing.
    /// </summary>
    public string BetSlipUrl { get; set; } = "https://roobet.com/sports";

    /// <summary>With the bet slip on: it opens by itself when a match ("A vs B") starts playing.</summary>
    public bool BetSlipAutoOpen { get; set; } = true;

    /// <summary>
    /// Console games play in RetroArch for every system it has a core for (the core is downloaded the first time); the
    /// per-system emulators are used for the rest (Switch, PS3, Wii U), or when this is off.
    /// </summary>
    public bool PreferRetroArch { get; set; } = true;

    /// <summary>retroarch.exe (found by itself when empty: the usual install places, then the emulator folders).</summary>
    public string? RetroArchPath { get; set; }

    /// <summary>In the built-in browser, click file hosts' download buttons and skip their ad pop-ups for you.</summary>
    public bool BrowserAutoClick { get; set; } = true;

    /// <summary>
    /// Point games' desktop shortcuts at SeaRaider (which then starts the game), so play time and friends' "playing" work
    /// when a game is started from its shortcut.
    /// </summary>
    public bool RouteGameShortcuts { get; set; } = true;

    /// <summary>Sources unticked in the store's source filter (searched and shown only when ticked again).</summary>
    public List<Guid> StoreExcludedSources { get; set; } = [];

    /// <summary>Look for a new SeaRaider version at start-up and every few hours (Settings › Updates).</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>A version the user chose to skip: not offered again (a newer one is).</summary>
    public string? SkippedUpdateVersion { get; set; }

    /// <summary>Mute setup programs (repack installers play music) while they run.</summary>
    public bool MuteSetupAudio { get; set; } = true;

    /// <summary>Delete archives / torrent payload once extraction succeeds.</summary>
    public bool DeleteArchivesAfterInstall { get; set; } = true;

    /// <summary>Back up in-folder save directories before uninstalling, and restore them on reinstall.</summary>
    public bool PreserveInFolderSaves { get; set; } = true;

    /// <summary>Show the folder / file-name dialog before every download.</summary>
    public bool AskBeforeDownload { get; set; } = true;

    /// <summary>Default for "run setup silently" when a release turns out to be an installer.</summary>
    public bool PreferSilentInstall { get; set; } = true;

    /// <summary>Default for "start the installer as soon as it's extracted".</summary>
    public bool AutoRunInstallers { get; set; } = true;

    /// <summary>What to do with a game's setup files once its setup has installed it (they aren't needed to play).</summary>
    public SetupFilesMode SetupFilesAfterInstall { get; set; } = SetupFilesMode.Ask;

    /// <summary>The DirectX / Visual C++ / .NET installers repack setups run at the end, each with its own window.</summary>
    public SetupRuntimes SetupRuntimes { get; set; } = SetupRuntimes.InstallQuietly;

    /// <summary>Close the web page some repack setups open when they finish.</summary>
    public bool BlockSetupWebPages { get; set; } = true;

    /// <summary>Springy motion: buttons press and spring back, cards lift, pages and pop-outs settle in.</summary>
    public bool Animations { get; set; } = true;

    /// <summary>Blur behind pop-outs, shadows and glows. Off: a flat look that's lighter on the graphics card.</summary>
    public bool BlurAndGlow { get; set; } = true;

    /// <summary>Interface sounds (clicks, finished downloads, friend requests …). Each kind can be switched off.</summary>
    public bool SoundsEnabled { get; set; } = true;

    /// <summary>0–100. Even 100 stays polite; the default is soft.</summary>
    public int SoundVolume { get; set; } = 45;

    /// <summary>Soft clicks for buttons, toggles and list picks.</summary>
    public bool SoundClicks { get; set; } = true;

    /// <summary>A chime when a download or an install finishes.</summary>
    public bool SoundDownloads { get; set; } = true;

    /// <summary>Friend requests and chat messages.</summary>
    public bool SoundFriends { get; set; } = true;

    /// <summary>Wishlisted games arriving in the store, and failed downloads / installs.</summary>
    public bool SoundAlerts { get; set; } = true;

    /// <summary>A faint background sound while SeaRaider is in front (paused during videos and when it's in the background).</summary>
    public bool AmbienceEnabled { get; set; } = true;

    /// <summary>0–100: the background sound's own volume (soft by default).</summary>
    public int AmbienceVolume { get; set; } = 2;

    /// <summary>Show what you're doing in SeaRaider on your Discord profile (needs Discord open on this PC).</summary>
    public bool DiscordPresence { get; set; } = true;

    /// <summary>The Discord application SeaRaider shows up as (its "Application ID" from discord.com/developers).</summary>
    public string? DiscordAppId { get; set; }

    /// <summary>The "Add me on SeaRaider" button on your Discord status (needs a friends relay).</summary>
    public bool DiscordAddButton { get; set; } = true;

    /// <summary>
    /// While Discord is running, the player's sound is played by SeaRaider itself (not by its built-in browser), so a Discord
    /// Go Live stream of the SeaRaider window carries it. Adds a few hundredths of a second of delay to the sound.
    /// </summary>
    public bool PlayerSoundForDiscord { get; set; } = true;

    /// <summary>Console games (ROMs) get their description, details, box art, screenshots and a trailer from the web
    /// (Wikipedia, itch.io, libretro's thumbnails, YouTube): ROM sites rarely give more than a name.</summary>
    public bool RomInfoFromWeb { get; set; } = true;

    /// <summary>Console games (ROMs, emulators) show in the store: off until switched on in Settings › Emulators.</summary>
    public bool ShowConsoleGames { get; set; }

    /// <summary>The switch to Hallow's Eve has been made once (on the first start of this version); later choices stay.</summary>
    public bool HalloweenSkinGiven { get; set; }

    /// <summary>Untick the "Limit installer to 2 GB of RAM usage" (low RAM) option on the first page of repack setups.</summary>
    public bool UncheckSetupLowRam { get; set; } = true;

    /// <summary>Silent installs show the installer's own progress window (/SILENT, /passive, /qb) instead of nothing at all.</summary>
    public bool ShowSetupProgress { get; set; }

    /// <summary>
    /// A setup that uses no CPU and does no disk work for this many seconds is treated as waiting for input:
    /// its windows are brought to the front.
    /// </summary>
    public int InstallStallSeconds { get; set; } = 15;

    /// <summary>
    /// If a silent setup is stalled with no window to show, close it and start it again with its normal wizard
    /// (only when it hasn't written anything to the game folder yet).
    /// </summary>
    public bool AutoFallbackToInteractive { get; set; } = true;

    /// <summary>
    /// Also announce torrents to the public trackers in <see cref="PublicTrackers"/>. Helps magnet links that
    /// list no (or only dead) trackers find peers. Never applied to private torrents.
    /// </summary>
    public bool AddPublicTrackers { get; set; } = true;

    /// <summary>Extra trackers used when <see cref="AddPublicTrackers"/> is on (http, https or udp URLs).</summary>
    public List<string> PublicTrackers { get; set; } = [.. DefaultPublicTrackers];

    /// <summary>A torrent that has found no peers at all after this many minutes is stopped with an explanation (0 = never).</summary>
    public int TorrentNoPeersTimeoutMinutes { get; set; } = 10;

    public static IReadOnlyList<string> DefaultPublicTrackers { get; } =
    [
        "udp://tracker.opentrackr.org:1337/announce",
        "udp://open.stealth.si:80/announce",
        "udp://tracker.torrent.eu.org:451/announce",
        "udp://exodus.desync.com:6969/announce",
        "udp://explodie.org:6969/announce",
        "udp://open.demonii.com:1337/announce",
        "https://tracker.opentrackr.org:443/announce",
    ];

    /// <summary>
    /// When the verification window can't start because the Microsoft Edge WebView2 Runtime is missing or broken,
    /// download Microsoft's installer and install / repair it silently (per-user, no admin needed).
    /// </summary>
    public bool AutoInstallWebView2 { get; set; } = true;

    // ---- media ---------------------------------------------------------------------------------------

    /// <summary>Fill missing screenshots and trailers from the Steam store (by title).</summary>
    public bool UseSteamMedia { get; set; } = true;

    /// <summary>For games Steam doesn't have: find the official trailer on YouTube (matched by title).</summary>
    public bool UseYouTubeTrailers { get; set; } = true;

    /// <summary>When no sharp picture is found elsewhere, search the web for HD wallpapers of the game (banner art).</summary>
    public bool UseWebArtwork { get; set; } = true;

    /// <summary>Optional IGDB access (a free Twitch developer app): client id.</summary>
    public string? IgdbClientId { get; set; }

    /// <summary>Optional IGDB access: client secret.</summary>
    public string? IgdbClientSecret { get; set; }

    // ---- friends -------------------------------------------------------------------------------------

    /// <summary>Friends, presence and chat (the social overlay). Off = no relay connection and no background polling.</summary>
    public bool FriendsEnabled { get; set; } = true;

    /// <summary>
    /// Your own SeaRaider.Relay address, e.g. wss://relay.example.com/ws. Empty = the build's default relay
    /// (<c>RelayDefaults</c>). Everyone who wants to be friends must use the same relay.
    /// </summary>
    public string? RelayUrl { get; set; }

    /// <summary>Show friends the title of the game you're playing (otherwise just "In-Game").</summary>
    public bool ShareGameActivity { get; set; } = true;

    /// <summary>Show friends what you're watching in SeaRaider's player (and let them watch along).</summary>
    public bool ShareMediaActivity { get; set; } = true;

    /// <summary>Show as Away after this many minutes without input (0 = never).</summary>
    public int AwayAfterMinutes { get; set; } = 10;

    /// <summary>
    /// Show the expert controls: download-source picker open on game pages, file names and installer switches in the
    /// download dialog, technical details on download rows, and the Downloads / Setup programs / Torrents settings tabs.
    /// Off by default: everything works with one click without them.
    /// </summary>
    public bool ShowAdvancedOptions { get; set; }

    /// <summary>The app's look: "The Next Generation" (glass and neon) or "Legacy" (the original night-water look).</summary>
    public AppSkin Skin { get; set; } = AppSkin.NextGeneration;

    /// <summary>Hide site posts that aren't games (update digests, upcoming-repack lists, donation calls, announcements).</summary>
    public bool HideNonGames { get; set; } = true;

    /// <summary>Hide games with nudity or sexual content from the store (a keyword check; see AdultContentFilter).</summary>
    public bool HideAdultGames { get; set; } = true;

    /// <summary>The store's Multiplayer section (games with "Online" in the title): titles or phrases that aren't online games.</summary>
    public string MultiplayerExceptions { get; set; } = Catalog.MultiplayerGames.DefaultExceptions;

    /// <summary>The emulator to play each system's games with (Settings › Emulators). A system without an entry can't be played yet.</summary>
    public List<EmulatorSetting> Emulators { get; set; } = [];

    /// <summary>Store cards hidden with their × button (by merged-game key), with the title for the Settings list.</summary>
    public List<HiddenStoreItem> HiddenStoreItems { get; set; } = [];

    /// <summary>Re-fetch already-known game pages that have no sitemap date after this many days.</summary>
    public int RecrawlAfterDays { get; set; } = 14;

    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0 Safari/537.36 SeaRaider/0.2";
}

/// <summary>See <see cref="AppSettings.FirstRun"/>.</summary>
public enum FirstRunState
{
    /// <summary>Not decided yet (a settings document written before the welcome questions existed reads as this).</summary>
    Unknown,

    /// <summary>A new install that hasn't answered the welcome questions yet; they're asked at every start until it has.</summary>
    Pending,

    /// <summary>Answered, or an existing install that never needs them.</summary>
    Completed,
}

/// <summary>When to unpack while downloading (see <see cref="AppSettings.LowDiskSpaceMode"/>).</summary>
public enum LowDiskMode
{
    /// <summary>Only when the drive doesn't have room for the archive and the unpacked game.</summary>
    Automatic,

    /// <summary>For every multi-part RAR download.</summary>
    Always,

    /// <summary>Download everything first, then unpack.</summary>
    Never,
}

/// <summary>See <see cref="AppSettings.SetupFilesAfterInstall"/>.</summary>
public enum SetupFilesMode
{
    /// <summary>Ask once the game is installed.</summary>
    Ask,

    /// <summary>Delete them straight away.</summary>
    Delete,

    /// <summary>Keep them (to reinstall later without downloading).</summary>
    Keep,
}

/// <summary>See <see cref="AppSettings.SetupRuntimes"/>.</summary>
public enum SetupRuntimes
{
    /// <summary>Close their windows and install the ones with a quiet mode silently once the setup is done.</summary>
    InstallQuietly,

    /// <summary>Close them and install nothing (for PCs that already have them).</summary>
    Skip,

    /// <summary>Leave them alone: their own windows open as the setup intended.</summary>
    ShowInstallers,
}

/// <summary>The app's skins.</summary>
public enum AppSkin
{
    /// <summary>Glass panels over a blurred backdrop of the featured game, neon cyan accents.</summary>
    NextGeneration,

    /// <summary>The original look (v0.1–v0.6).</summary>
    Legacy,

    /// <summary>A seasonal skin: The Next Generation in midnight plum, pumpkin and candle gold, with a quiet moon and bats.</summary>
    Halloween,
}

/// <summary>A store card the user hid.</summary>
public sealed class HiddenStoreItem
{
    public string Key { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime HiddenAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>The emulator that plays one system's games.</summary>
public sealed class EmulatorSetting
{
    /// <summary><see cref="Emulation.GameSystem.Id"/> ("snes", "ps2").</summary>
    public string SystemId { get; set; } = string.Empty;

    /// <summary>The emulator program (an .exe).</summary>
    public string ExePath { get; set; } = string.Empty;

    /// <summary>Command-line template ({rom}, {romdir}, {emu}, {core}); empty = the known switches for that emulator.</summary>
    public string? Arguments { get; set; }
}
