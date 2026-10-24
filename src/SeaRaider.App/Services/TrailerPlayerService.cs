using System.Text.Json;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SeaRaider.Core;
using SeaRaider.Core.Scraping;

namespace SeaRaider.App.Services;

/// <summary>One trailer to show in the in-app player overlay.</summary>
/// <param name="PlayerFolder">%LOCALAPPDATA%\SeaRaider\player (mapped to a private https host inside the player).</param>
/// <param name="PageName">The player page in that folder (unique per session so a reused browser reloads it).</param>
/// <param name="Title">Game title, shown above the video.</param>
/// <param name="Url">What's playing (for "Watching: …" and Watch along).</param>
/// <param name="StartSeconds">Where it started.</param>
/// <param name="ThumbnailUrl">Its picture, when known.</param>
/// <param name="IsLive">A live stream (Twitch channel): no position to follow.</param>
/// <param name="EmbedReferer">
/// For a video site's player shown in a frame (a title from a site's list): the page it was found on, sent to the player's
/// site as the page asking for it — many only play on the site that embeds them.
/// </param>
/// <param name="PageUrl">The page it was opened from (a title's page on its site), when known: its event chat room.</param>
public sealed record TrailerSession(string PlayerFolder, string PageName, string Title, string Url = "", int StartSeconds = 0,
    string? ThumbnailUrl = null, bool IsLive = false, string? EmbedReferer = null, string? PageUrl = null)
{
    /// <summary>The host of the framed player <see cref="EmbedReferer"/> is sent to.</summary>
    /// <summary>A game's trailer (from its store page), not a stream: no "Loading…" text over it.</summary>
    public bool IsGameTrailer { get; init; }

    public string? EmbedHost => EmbedReferer is not null && Uri.TryCreate(MediaStreams.WithoutHint(Url), UriKind.Absolute, out var uri) ? uri.Host : null;
}

/// <summary>
/// Plays trailers inside SeaRaider: <see cref="Current"/> is shown by the main window in an overlay (a WebView2 hosted in
/// the window, over the blurred app), never the user's browser unless WebView2 is missing.
/// <list type="bullet">
///   <item><b>Steam</b> (2025+: streams only): DASH first with dash.js, as Steam's own store player does — video and
///         audio come as separate tracks, which is why the HLS attempt in v0.6 could end up silent. HLS (hls.js) is the
///         fallback. Both libraries are bundled.</item>
///   <item><b>YouTube</b> (IGDB trailers, videos embedded in a post): YouTube's embedded player in the same overlay.</item>
///   <item>Plain video files play directly.</item>
/// </list>
/// </summary>
public sealed partial class TrailerPlayerService : ObservableObject
{
    private readonly ILogger<TrailerPlayerService> logger;
    private readonly ShellService shell;
    private readonly SeaRaider.Core.Catalog.MediaService media;
    private readonly SeaRaider.Core.Abstractions.ILibraryRepository repository;

    private readonly NavigationService navigation;

    public TrailerPlayerService(ILogger<TrailerPlayerService> logger, ShellService shell, SeaRaider.Core.Catalog.MediaService media,
        SeaRaider.Core.Abstractions.ILibraryRepository repository, NavigationService navigation)
    {
        this.logger = logger;
        this.shell = shell;
        this.media = media;
        this.repository = repository;
        this.navigation = navigation;
        Alongside.CollectionChanged += (_, _) =>
        {
            if (SoundFrom is { } sound && !ReferenceEquals(sound, Current) && !Alongside.Contains(sound)) SoundFrom = null;
            OnPropertyChanged(nameof(IsMultiView));
            OnPropertyChanged(nameof(CanAddAlongside));
        };
        repository.SettingsChanged += (_, settings) => Avalonia.Threading.Dispatcher.UIThread.Post(() => UpdateBetSlip(settings));
        UiMode.Current.PropertyChanged += (_, _) => OnPropertyChanged(nameof(ShowBetSlipButton));
        UpdateBetSlip(repository.GetSettings());
    }

    // ---- multi-view: several streams at once ------------------------------------------------------------------

    /// <summary>Most streams on screen at once (the first one and three more), in a 2 × 2 grid.</summary>
    public const int MaxStreams = 4;

    /// <summary>Streams watched alongside <see cref="Current"/> (multi-view). Muted unless chosen for the sound.</summary>
    public System.Collections.ObjectModel.ObservableCollection<TrailerSession> Alongside { get; } = [];

    public bool IsMultiView => Alongside.Count > 0;
    public bool CanAddAlongside => Current is not null && Alongside.Count < MaxStreams - 1;

    /// <summary>The stream you hear (null: the first one). Only one plays sound at a time.</summary>
    [ObservableProperty] private TrailerSession? _soundFrom;

    public bool IsAudible(TrailerSession session) => ReferenceEquals(SoundFrom ?? Current, session);

    /// <summary>"+ Stream" was pressed: the next thing played (on Media, or anywhere) joins the grid instead of replacing it.</summary>
    [ObservableProperty] private bool _isPickingAlongside;

    /// <summary>"+ Stream" in the player: it moves into its corner and the Media tab opens to pick one.</summary>
    [RelayCommand]
    private void AddStream()
    {
        if (!CanAddAlongside) return;
        IsPickingAlongside = true;
        IsFullScreen = false;
        IsMinimized = true;
        navigation.ShowPage(AppPage.Media);
    }

    [RelayCommand]
    private void CancelPicking() => IsPickingAlongside = false;

    /// <summary>Plays something next to what's already playing (Media › right-click › Watch alongside).</summary>
    public void PlayAlongside(string url, string? storePageUrl, string title, string? thumbnailUrl = null) =>
        Open(url, storePageUrl, title, null, thumbnailUrl, alongside: Current is not null);

    /// <summary>Takes a stream out of the grid (its × in multi-view).</summary>
    public void RemoveAlongside(TrailerSession session)
    {
        if (ReferenceEquals(session, Current))
        {
            // The first stream closed: the next one takes its place (its chat and bet slip follow).
            if (Alongside.Count == 0) { Close(); return; }
            var next = Alongside[0];
            Alongside.RemoveAt(0);
            var rest = Alongside.ToList();
            Alongside.Clear();
            Current = null;
            Current = next;
            foreach (var other in rest) Alongside.Add(other);
            return;
        }
        Alongside.Remove(session);
    }

    /// <summary>🔊 on a stream in multi-view: that one plays sound, the others go quiet.</summary>
    public void ListenTo(TrailerSession session) => SoundFrom = ReferenceEquals(session, Current) ? null : session;

    // ---- mini-player: keeps playing in a corner while the app is used ---------------------------------------

    /// <summary>
    /// The player is shrunk into a corner of the window (still playing, the app usable around it) instead of covering the app.
    /// The same browser keeps playing: nothing reloads.
    /// </summary>
    [ObservableProperty] private bool _isMinimized;

    [RelayCommand]
    private void Minimize()
    {
        if (Current is null) return;
        IsFullScreen = false;
        IsMinimized = true;
    }

    [RelayCommand]
    private void Restore() => IsMinimized = false;

    /// <summary>The video asked to be full screen (its own button): the window shows only the video until it's done.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPlayerHeader))]
    private bool _isFullScreen;

    /// <summary>The bar above the video (title, chat, bet slip, minimize, close): not in full screen or in the corner.</summary>
    public bool ShowPlayerHeader => !IsMinimized && !IsFullScreen;

    partial void OnIsMinimizedChanged(bool value) => OnPropertyChanged(nameof(ShowPlayerHeader));

    internal void SetFullScreen(bool value)
    {
        if (value && Current is null) return;
        if (value) IsMinimized = false;
        IsFullScreen = value;
    }

    // ---- bet slip: the sportsbook beside the player (Settings › General › Bet slip) ---------------------------

    /// <summary>Switched on in Settings: the player shows its "Bet slip" button.</summary>
    [ObservableProperty] private bool _isBetSlipAvailable;

    /// <summary>The player's "Bet slip" button: always there, in simple mode too (pressing it switches the bet slip on).</summary>
    public bool ShowBetSlipButton => true;

    partial void OnIsBetSlipAvailableChanged(bool value) => OnPropertyChanged(nameof(ShowBetSlipButton));

    /// <summary>The bet slip panel is showing beside the video.</summary>
    [ObservableProperty] private bool _isBetSlipOpen;

    /// <summary>What the bet slip panel opens for what's playing (the settings' address, {query} / {home} / {away} filled in).</summary>
    [ObservableProperty] private string? _betSlipUrl;

    private string _betSlipTemplate = "https://roobet.com/sports";
    private bool _betSlipAutoOpen = true;

    /// <summary>
    /// "Bet slip" in the player: shows or hides the panel. The button is always there; pressing it while the bet slip is
    /// switched off in Settings switches it on (and it stays on).
    /// </summary>
    [RelayCommand]
    private void ToggleBetSlip()
    {
        if (!IsBetSlipAvailable)
        {
            var settings = repository.GetSettings();
            settings.BetSlipEnabled = true;
            repository.SaveSettings(settings);
            UpdateBetSlip(settings);
            if (!IsBetSlipAvailable) return; // the address in Settings isn't usable
        }
        IsBetSlipOpen = !IsBetSlipOpen;
        if (IsBetSlipOpen) BetSlipUrl = BetSlipAddressFor(Current);
    }

    private void UpdateBetSlip(SeaRaider.Core.Models.AppSettings settings)
    {
        _betSlipTemplate = string.IsNullOrWhiteSpace(settings.BetSlipUrl) ? "https://roobet.com/sports" : settings.BetSlipUrl.Trim();
        _betSlipAutoOpen = settings.BetSlipAutoOpen;
        IsBetSlipAvailable = settings.BetSlipEnabled && Uri.TryCreate(BetSlipAddressFor(null), UriKind.Absolute, out var u) && u.Scheme == "https";
        if (!IsBetSlipAvailable) IsBetSlipOpen = false;
        else if (IsBetSlipOpen) BetSlipUrl = BetSlipAddressFor(Current);
    }

    /// <summary>The bet slip's address for a session: {query} = the title, {home} / {away} = a match's sides.</summary>
    internal string BetSlipAddressFor(TrailerSession? session)
    {
        var title = session?.Title ?? string.Empty;
        var match = MediaTitleParser.Matchup(title);
        return _betSlipTemplate
            .Replace("{query}", Uri.EscapeDataString(title), StringComparison.OrdinalIgnoreCase)
            .Replace("{home}", Uri.EscapeDataString(match?.Home ?? title), StringComparison.OrdinalIgnoreCase)
            .Replace("{away}", Uri.EscapeDataString(match?.Away ?? string.Empty), StringComparison.OrdinalIgnoreCase);
    }

    partial void OnCurrentChanged(TrailerSession? value)
    {
        OnPropertyChanged(nameof(CanAddAlongside));
        // (Play sets null first, then the new video: the panel stays open across videos and closes with Close.)
        if (value is null) return;
        // A match starting: the bet slip opens by itself (when it's on and set to).
        if (!IsBetSlipOpen && IsBetSlipAvailable && _betSlipAutoOpen && MediaTitleParser.Matchup(value.Title) is not null)
            IsBetSlipOpen = true;
        if (IsBetSlipOpen) BetSlipUrl = BetSlipAddressFor(value); // same address unless it names the match: the page stays
    }

    private Action? _fallback;
    private int _sessionNumber;
    private int _findNumber;

    /// <summary>A page is being read for its player (a shared title, an invite): "Finding the player for …" shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FindingText))]
    private string? _findingTitle;

    public bool IsFinding => FindingTitle is not null;
    public string FindingText => $"Finding the player for {FindingTitle}…";

    partial void OnFindingTitleChanged(string? value) => OnPropertyChanged(nameof(IsFinding));

    /// <summary>The trailer being shown in the overlay, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen))]
    private TrailerSession? _current;

    public bool IsOpen => Current is not null;

    /// <summary>
    /// The player's sound is played by SeaRaider itself (Settings › Friends › Discord), while Discord is running: a Go Live
    /// stream of SeaRaider's window then carries it. See <see cref="PlayerSoundPipe"/>.
    /// </summary>
    public bool RouteSoundThroughApp => repository.GetSettings().PlayerSoundForDiscord && PlayerSoundPipe.DiscordIsRunning();

    /// <summary>The player page says where it is (every couple of seconds, and on play / pause / seek).</summary>
    public event EventHandler<PlaybackReport>? PlaybackReported;

    /// <summary>Asks the open player to be at this position and play / pause (Watch along).</summary>
    public event EventHandler<PlaybackReport>? SyncRequested;

    internal void ReportPlayback(TrailerSession session, double position, bool playing)
    {
        if (ReferenceEquals(Current, session)) PlaybackReported?.Invoke(this, new PlaybackReport(session, position, playing));
    }

    /// <summary>Moves the open player to <paramref name="position"/> if it's more than a moment off, and plays or pauses.</summary>
    public void Sync(double position, bool playing)
    {
        if (Current is { } session) SyncRequested?.Invoke(this, new PlaybackReport(session, position, playing));
    }

    [GeneratedRegex(@"\.(?:m3u8|mpd|mp4|webm|m4v)(?:[?#]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex StreamRegex();

    /// <summary>A video file or stream: by its path (.m3u8, .mp4 …), or by the format hint written into it ("#sr-hls") for streams whose address doesn't say.</summary>
    public static bool IsStream(string url) =>
        MediaStreams.HasHint(url) || (Uri.TryCreate(url, UriKind.Absolute, out var uri) && StreamRegex().IsMatch(uri.AbsolutePath));

    /// <param name="url">The trailer (stream, file or video page).</param>
    /// <param name="storePageUrl">Where to watch it if it can't play here.</param>
    /// <param name="title">Game title for the overlay.</param>
    /// <param name="startSeconds">Start playing from here (a shared "watch from 1:23", a time in a comment).</param>
    /// <param name="thumbnailUrl">Its picture, for friends' "Watching" (optional).</param>
    /// <summary>A game's trailer from its page (no stream "Loading…" text).</summary>
    public void PlayTrailer(string url, string? storePageUrl, string title)
    {
        _nextIsTrailer = true;
        try
        {
            Play(url, storePageUrl, title);
        }
        finally
        {
            _nextIsTrailer = false;
        }
    }

    private bool _nextIsTrailer;

    public void Play(string url, string? storePageUrl, string title, int? startSeconds = null, string? thumbnailUrl = null)
    {
        // After "+ Stream": what's picked joins the grid.
        var alongside = IsPickingAlongside && Current is not null && CanAddAlongside;
        IsPickingAlongside = false;
        Open(url, storePageUrl, title, startSeconds, thumbnailUrl, alongside);
    }

    private void Open(string url, string? storePageUrl, string title, int? startSeconds, string? thumbnailUrl, bool alongside)
    {
        var start = startSeconds is > 0 ? startSeconds.Value : 0;
        var youTubeId = MediaUrlHelper.TryGetYouTubeId(url);
        var embed = youTubeId is null ? EmbedUrl(url, start) : null; // Twitch, Vimeo
        if (embed is null && youTubeId is null && MediaStreams.FormatOf(url) == StreamFormat.Embed) embed = MediaStreams.WithoutHint(url); // a page that is a player
        if (youTubeId is null && embed is null && !IsStream(url))
        {
            // A page, not a player (a title shared by a friend, a site's title page): read it for its player and play that
            // here, the same way a title on Browse plays. Only something that isn't a web page at all goes to the browser.
            if (Uri.TryCreate(url, UriKind.Absolute, out var pageUri) && pageUri.Scheme is "http" or "https")
                _ = FindAndPlayAsync(url, storePageUrl, title, startSeconds, thumbnailUrl, alongside);
            else
                shell.OpenUrl(url);
            return;
        }

        string page;
        try
        {
            var keep = (alongside ? Alongside.Append(Current!) : Enumerable.Empty<TrailerSession>()).Select(x => x.PageName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            page = WritePlayerPage(url, youTubeId, embed, storePageUrl, title, start, ++_sessionNumber, repository.GetSettings().PlayerQuality, keep);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Couldn't write the trailer player; opening the stream in the browser");
            shell.OpenUrl(storePageUrl ?? url);
            return;
        }

        // Without SeaRaider's player: YouTube refuses embeds in a local file, so open YouTube itself; streams use the page.
        void Fallback()
        {
            if (youTubeId is not null) shell.OpenUrl($"https://www.youtube.com/watch?v={youTubeId}" + (start > 0 ? $"&t={start}s" : ""));
            else if (embed is not null) shell.OpenUrl(MediaStreams.WithoutHint(url)); // Twitch / Vimeo / embed page: their own site
            else shell.OpenFile(page);
        }

        if (!OperatingSystem.IsWindows())
        {
            Fallback();
            return;
        }

        var live = embed?.Contains("player.twitch.tv/?channel=", StringComparison.Ordinal) == true ||
                   embed?.StartsWith("https://player.kick.com/", StringComparison.Ordinal) == true && !embed.Contains("/video/", StringComparison.Ordinal);
        // A video site's player in a frame, found on another site's page: that page is who asks for it.
        string? referer = null;
        if (youTubeId is null && MediaStreams.FormatOf(url) == StreamFormat.Embed &&
            Uri.TryCreate(storePageUrl, UriKind.Absolute, out var foundOn) && foundOn.Scheme is "http" or "https" &&
            Uri.TryCreate(MediaStreams.WithoutHint(url), UriKind.Absolute, out var framed) &&
            !framed.Host.Equals(foundOn.Host, StringComparison.OrdinalIgnoreCase))
            referer = foundOn.AbsoluteUri;
        var session = new TrailerSession(Path.GetDirectoryName(page)!, Path.GetFileName(page), title, url, start,
            thumbnailUrl ?? (youTubeId is not null ? $"https://i.ytimg.com/vi/{youTubeId}/hqdefault.jpg" : null), live, referer, storePageUrl)
        {
            IsGameTrailer = _nextIsTrailer,
        };

        if (alongside && Current is not null)
        {
            // Multi-view: added to the grid, muted; the oldest extra makes room when it's full.
            if (Alongside.Count >= MaxStreams - 1) Alongside.RemoveAt(0);
            Alongside.Add(session);
            IsMinimized = false;
            return;
        }

        // Null first, so the overlay builds a fresh player for the new trailer (and leaves multi-view).
        Current = null;
        Alongside.Clear();
        SoundFrom = null;
        _fallback = Fallback;
        Current = session;
        IsMinimized = false; // something new to watch: full size
    }

    /// <summary>Reads a page for its player (<see cref="SeaRaider.Core.Catalog.MediaService.ResolvePlayUrlAsync"/>) and plays it.</summary>
    private async Task FindAndPlayAsync(string pageUrl, string? storePageUrl, string title, int? startSeconds, string? thumbnailUrl, bool alongside)
    {
        var number = ++_findNumber; // a newer Play wins
        FindingTitle = string.IsNullOrWhiteSpace(title) ? new Uri(pageUrl).Host : title;
        try
        {
            var play = await Task.Run(() => media.ResolvePlayUrlAsync(pageUrl));
            if (number != _findNumber) return;
            // Always something the player shows (a stream, an embedded player, or the page itself in a frame): no loop back here.
            Open(play, storePageUrl ?? pageUrl, title, startSeconds, thumbnailUrl, alongside);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't find a player on {Page}", pageUrl);
            if (number == _findNumber) Open(MediaStreams.WithHint(pageUrl, StreamFormat.Embed), storePageUrl ?? pageUrl, title, startSeconds, thumbnailUrl, alongside);
        }
        finally
        {
            if (number == _findNumber) FindingTitle = null;
        }
    }

    [RelayCommand]
    public void Close()
    {
        _findNumber++; // a player still being looked for doesn't pop up after Close
        FindingTitle = null;
        IsBetSlipOpen = false; // the bet slip closes with the player
        IsMinimized = false;
        IsFullScreen = false;
        IsPickingAlongside = false;
        Alongside.Clear();
        SoundFrom = null;
        Current = null;
        _fallback = null;
    }

    /// <summary>The player couldn't start (no WebView2 Runtime): close the overlay and use the default browser.</summary>
    public void ReportFailed(TrailerSession session, Exception error)
    {
        logger.LogWarning(error, "Trailer player failed; using the browser");
        if (Alongside.Remove(session)) return; // an extra stream in multi-view: just taken out
        if (!ReferenceEquals(Current, session)) return;
        var fallback = _fallback;
        Close();
        fallback?.Invoke();
    }

    /// <summary>A link in the player ("Watch it on the Steam store"): always the default browser.</summary>
    public void OpenExternal(string url) => shell.OpenUrl(url);

    /// <summary>Writes hls.js (once) and the player page for this trailer into %LOCALAPPDATA%\SeaRaider\player.</summary>
    /// <summary>The private address the player page is served from (Twitch embeds must name it as their "parent").</summary>
    public const string PlayerHost = "player.searaider.local";

    [GeneratedRegex(@"^https?://(?:www\.|m\.)?twitch\.tv/videos/(?<video>\d+)|^https?://(?:clips\.twitch\.tv/|(?:www\.)?twitch\.tv/[^/]+/clip/)(?<clip>[\w-]+)|^https?://(?:www\.|m\.)?twitch\.tv/(?<channel>[A-Za-z0-9_]{3,25})/?$", RegexOptions.IgnoreCase)]
    private static partial Regex TwitchRegex();

    [GeneratedRegex(@"^https?://(?:www\.)?kick\.com/video/(?<id>[0-9a-f-]{8,40})", RegexOptions.IgnoreCase)]
    private static partial Regex KickVideoRegex();

    [GeneratedRegex(@"^https?://(?:www\.)?kick\.com/(?<name>[A-Za-z0-9_-]{3,25})/?$", RegexOptions.IgnoreCase)]
    private static partial Regex KickChannelRegex();

    [GeneratedRegex(@"^https?://(?:www\.)?(?:dailymotion\.com/(?:embed/)?video/|dai\.ly/)(?<id>[a-z0-9]+)", RegexOptions.IgnoreCase)]
    private static partial Regex DailymotionRegex();

    /// <summary>First path segments on Kick that are the site's own pages, not channels.</summary>
    private static readonly HashSet<string> KickPages = new(StringComparer.OrdinalIgnoreCase)
        { "categories", "category", "browse", "search", "following", "dashboard", "video", "videos", "clips", "clip", "terms-of-service", "privacy-policy", "community-guidelines", "help", "settings" };

    [GeneratedRegex(@"vimeo\.com/(?:video/)?(?<id>\d{6,})", RegexOptions.IgnoreCase)]
    private static partial Regex VimeoRegex();

    /// <summary>The embeddable player for a Twitch channel / past broadcast / clip or a Vimeo video, else null.</summary>
    /// <param name="startSeconds">Start time for a Twitch past broadcast or a Vimeo video (live channels and clips ignore it).</param>
    public static string? EmbedUrl(string url, int startSeconds = 0)
    {
        // Links often carry their own time (?t=1h2m3s): the regexes below need the bare address.
        var bare = url.Split('?', '#')[0];
        var twitch = TwitchRegex().Match(bare);
        if (twitch.Success)
        {
            var parent = $"&parent={PlayerHost}&autoplay=true";
            if (twitch.Groups["video"].Success)
            {
                var time = startSeconds > 0 ? $"&time={startSeconds / 3600}h{startSeconds / 60 % 60}m{startSeconds % 60}s" : "";
                return $"https://player.twitch.tv/?video=v{twitch.Groups["video"].Value}{parent}{time}";
            }
            if (twitch.Groups["clip"].Success) return $"https://clips.twitch.tv/embed?clip={twitch.Groups["clip"].Value}{parent}";
            return $"https://player.twitch.tv/?channel={twitch.Groups["channel"].Value.ToLowerInvariant()}{parent}";
        }
        if (KickVideoRegex().Match(bare) is { Success: true } kickVideo) return $"https://player.kick.com/video/{kickVideo.Groups["id"].Value}";
        if (KickChannelRegex().Match(bare) is { Success: true } kick && !KickPages.Contains(kick.Groups["name"].Value))
            return $"https://player.kick.com/{kick.Groups["name"].Value.ToLowerInvariant()}";
        if (DailymotionRegex().Match(bare) is { Success: true } daily) return $"https://www.dailymotion.com/embed/video/{daily.Groups["id"].Value}?autoplay=1";
        return VimeoRegex().Match(url) is { Success: true } vimeo
            ? $"https://player.vimeo.com/video/{vimeo.Groups["id"].Value}?autoplay=1" + (startSeconds > 0 ? $"#t={startSeconds}s" : "")
            : null;
    }

    /// <param name="quality">
    /// Settings › General › Video quality: "highest" (start at the best and stay there unless the connection can't keep up),
    /// "max" (always the best, never lower), "auto" (the player's own balance) or "saver" (480p at most).
    /// </param>
    private static string WritePlayerPage(string url, string? youTubeId, string? embed, string? storePageUrl, string title, int start, int session,
        string? quality, IReadOnlySet<string> keep)
    {
        var folder = Path.Combine(AppPaths.DataDirectory, "player");
        Directory.CreateDirectory(folder);
        CopyResource("SeaRaider.Player.hls.min.js", Path.Combine(folder, "hls.min.js"));
        CopyResource("SeaRaider.Player.LICENSE-hls.js.txt", Path.Combine(folder, "LICENSE-hls.js.txt"));
        CopyResource("SeaRaider.Player.dash.all.min.js", Path.Combine(folder, "dash.all.min.js"));
        CopyResource("SeaRaider.Player.LICENSE-dash.js.txt", Path.Combine(folder, "LICENSE-dash.js.txt"));

        // A stream added by its address may carry its format ("…/live/main#sr-hls"): read it, and give the player the bare address.
        var format = MediaStreams.FormatOf(url);
        url = MediaStreams.WithoutHint(url);

        // Steam lists the same trailer as HLS and as DASH in one folder: hls_264_master.m3u8 / dash_h264.mpd.
        var dash = url.Contains("hls_264_master.m3u8", StringComparison.OrdinalIgnoreCase)
            ? url.Replace("hls_264_master.m3u8", "dash_h264.mpd", StringComparison.OrdinalIgnoreCase)
            : url.Contains(".mpd", StringComparison.OrdinalIgnoreCase) || format == StreamFormat.Dash ? url : null;
        var hls = url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) || format == StreamFormat.Hls ? url : null;
        var parameters = JsonSerializer.Serialize(new
        {
            src = dash is null && hls is null && youTubeId is null && embed is null ? url : null,
            dash,
            hls,
            youtube = youTubeId,
            embed,
            store = storePageUrl,
            title,
            start,
            quality = quality is "max" or "auto" or "saver" ? quality : "highest",
        });
        // Earlier trailers' pages (each session gets its own name so the player always loads the new one).
        foreach (var old in Directory.EnumerateFiles(folder, "trailer*.html"))
        {
            if (keep.Contains(Path.GetFileName(old))) continue; // still on screen (multi-view)
            try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        var page = Path.Combine(folder, $"trailer-{session}.html");
        // Inside a <script>: "</" can't appear in the JSON.
        File.WriteAllText(page, PlayerHtml.Replace("__PARAMS__", parameters.Replace("</", "<\\/"), StringComparison.Ordinal));
        return page;
    }

    private static void CopyResource(string name, string path)
    {
        using var resource = typeof(TrailerPlayerService).Assembly.GetManifestResourceStream(name);
        if (resource is null) return;
        if (File.Exists(path) && new FileInfo(path).Length == resource.Length) return;
        using var file = File.Create(path);
        resource.CopyTo(file);
    }

    private const string PlayerHtml = """
        <!doctype html>
        <html>
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Trailer</title>
        <style>
          html, body { margin: 0; height: 100%; background: #000; color: #e3edf2; font-family: "Segoe UI", sans-serif; overflow: hidden; }
          video { width: 100%; height: 100%; background: #000; outline: none; display: block; }
          #msg { position: absolute; inset: 0; display: none; align-items: center; justify-content: center; flex-direction: column;
                 gap: 16px; text-align: center; padding: 24px; font-size: 15px; }
          #msg a { color: #3fe0c5; font-size: 16px; font-weight: 600; cursor: pointer; }
          #q { position: absolute; top: 12px; right: 12px; z-index: 5; display: none; opacity: 0; transition: opacity .2s;
               background: rgba(7,20,32,.88); color: #e3edf2; border: 1px solid #3fe0c5; border-radius: 6px; padding: 4px 8px;
               font: 12px "Segoe UI", sans-serif; cursor: pointer; }
          body.active #q, #q:focus { opacity: 1; }
        </style>
        </head>
        <body>
        <video id="v" controls autoplay playsinline></video>
        <div id="msg"><div id="why"></div><a id="store" style="display:none"></a></div>
        <select id="q" title="Video quality"></select>
        <script src="dash.all.min.js"></script>
        <script src="hls.min.js"></script>
        <script>
          const p = __PARAMS__;
          document.title = p.title ? p.title + ' · Trailer' : 'Trailer';
          const v = document.getElementById('v');
          const post = m => { if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(m); };
          document.addEventListener('keydown', e => { if (e.key === 'Escape' && !document.fullscreenElement) post('close'); });
          const external = url => {
            if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage('open:' + url);
            else window.location.href = url;
          };
          const fail = why => {
            v.style.display = 'none';
            document.getElementById('msg').style.display = 'flex';
            document.getElementById('why').textContent = why;
            if (p.store) {
              const a = document.getElementById('store');
              a.textContent = /steampowered\.com/i.test(p.store) ? 'Watch it on the Steam store' : 'Open it in your browser';
              a.style.display = 'block';
              a.onclick = e => { e.preventDefault(); external(p.store); };
            }
          };
          // ---- Quality: best by default (Settings › Video quality), with a picker over the video (top right) ----
          const q = document.getElementById('q');
          let idle = 0;
          document.addEventListener('mousemove', () => {
            document.body.classList.add('active');
            clearTimeout(idle);
            idle = setTimeout(() => document.body.classList.remove('active'), 2500);
          });
          const label = (height, bitrate) => (height ? height + 'p' : 'Level') + (bitrate ? ' · ' + (bitrate / 1e6).toFixed(1) + ' Mbps' : '');
          // levels: [{ index, height, bitrate }], best last. pick(index) locks one; pick(-1) goes back to automatic.
          const qualityMenu = (levels, current, pick) => {
            if (!levels || levels.length < 2) return;
            q.innerHTML = '';
            const auto = document.createElement('option');
            auto.value = '-1';
            auto.textContent = p.quality === 'saver' ? 'Data saver (auto)' : 'Auto (best)';
            q.appendChild(auto);
            levels.slice().reverse().forEach(l => {
              const o = document.createElement('option');
              o.value = String(l.index);
              o.textContent = label(l.height, l.bitrate);
              q.appendChild(o);
            });
            q.value = String(current);
            q.style.display = 'block';
            q.onchange = () => pick(parseInt(q.value, 10));
          };

          const tryDash = (url, next) => {
            if (!url || !window.dashjs) return next();
            const player = dashjs.MediaPlayer().create();
            try {
              // dash.js 4: start at the top bitrate unless saving data; "max" never switches down.
              const abr = p.quality === 'auto' ? {} : {
                initialBitrate: { video: p.quality === 'saver' ? 800 : 100000 },
                autoSwitchBitrate: { video: p.quality !== 'max' },
              };
              if (p.quality === 'saver') abr.maxBitrate = { video: 1500 };
              // A short buffer behind the playhead (memory, and less lag over a long stream); a few retries before giving up.
              player.updateSettings({ streaming: {
                abr: abr,
                buffer: { fastSwitchEnabled: true, bufferToKeep: 20, bufferTimeAtTopQuality: 20, bufferTimeAtTopQualityLongForm: 30 },
                retryAttempts: { MPD: 3, MediaSegment: 5, InitializationSegment: 3 },
              } });
              player.on(dashjs.MediaPlayer.events.STREAM_INITIALIZED, () => {
                const list = player.getBitrateInfoListFor('video') || [];
                const top = list.length - 1;
                if (p.quality === 'max' && top >= 0) player.setQualityFor('video', top);
                qualityMenu(list.map((b, i) => ({ index: i, height: b.height, bitrate: b.bitrate })),
                  p.quality === 'max' ? top : -1,
                  i => {
                    player.updateSettings({ streaming: { abr: { autoSwitchBitrate: { video: i < 0 } } } });
                    if (i >= 0) player.setQualityFor('video', i);
                  });
              });
            } catch (e) {}
            let failed = false;
            player.on(dashjs.MediaPlayer.events.ERROR, () => {
              if (failed) return;
              failed = true;
              player.reset();
              next();
            });
            player.initialize(v, url, true);
          };
          const tryHls = (url, next) => {
            if (!url) return next();
            if (window.Hls && Hls.isSupported()) {
              // Best first: the bandwidth guess starts high, so the first pieces load at the top quality and the player only
              // steps down if the connection really can't keep up ("max" never does; "saver" caps at 480p).
              // backBufferLength: what's already played is let go of after 30 s (hls.js keeps it all by default, and a long live
              // stream then slows everything down). Retries ride out a server's hiccups instead of failing the stream.
              const config = {
                enableWorker: true, maxBufferLength: 30, maxMaxBufferLength: 60, backBufferLength: 30, capLevelToPlayerSize: false,
                fragLoadingMaxRetry: 6, fragLoadingRetryDelay: 500, manifestLoadingMaxRetry: 4, levelLoadingMaxRetry: 4,
                startFragPrefetch: true, liveSyncDurationCount: 3, liveMaxLatencyDurationCount: 10,
              };
              if (p.quality === 'highest' || p.quality === 'max')
                Object.assign(config, { abrEwmaDefaultEstimate: 50e6, abrBandWidthFactor: 0.95, abrBandWidthUpFactor: 0.9, maxBufferLength: 40 });
              const hls = new Hls(config);
              current = hls;
              hls.on(Hls.Events.MANIFEST_PARSED, () => {
                const levels = hls.levels.map((l, i) => ({ index: i, height: l.height, bitrate: l.bitrate }));
                const top = levels.reduce((best, l) => (l.bitrate || l.height || 0) > (levels[best].bitrate || levels[best].height || 0) ? l.index : best, 0);
                if (p.quality === 'max') hls.currentLevel = top;
                else if (p.quality === 'highest') hls.startLevel = top;
                else if (p.quality === 'saver') {
                  const ok = levels.filter(l => !l.height || l.height <= 480);
                  if (ok.length) hls.autoLevelCapping = ok.reduce((a, l) => (l.bitrate || 0) > (a.bitrate || 0) ? l : a, ok[0]).index;
                }
                const sorted = levels.slice().sort((a, b) => (a.bitrate || a.height || 0) - (b.bitrate || b.height || 0));
                qualityMenu(sorted, p.quality === 'max' ? top : -1, i => {
                  if (i < 0) { hls.currentLevel = -1; if (p.quality !== 'saver') hls.nextAutoLevel = top; }
                  else hls.currentLevel = i;
                });
              });
              let networkRetries = 0, mediaRetries = 0;
              hls.on(Hls.Events.FRAG_LOADED, () => { networkRetries = 0; });
              hls.on(Hls.Events.ERROR, (event, data) => {
                if (!data.fatal) return;
                if (data.type === Hls.ErrorTypes.MEDIA_ERROR && mediaRetries++ < 3) hls.recoverMediaError();
                else if (data.type === Hls.ErrorTypes.NETWORK_ERROR && networkRetries++ < 4) setTimeout(() => hls.startLoad(), 1000 * networkRetries);
                else { current = null; hls.destroy(); next(); }
              });
              hls.on(Hls.Events.MANIFEST_PARSED, () => v.play().catch(() => {}));
              hls.loadSource(url);
              hls.attachMedia(v);
            } else if (v.canPlayType('application/vnd.apple.mpegurl')) {
              v.onerror = next;
              v.src = url;
            } else {
              next();
            }
          };
          const youtube = id => frameOf('https://www.youtube.com/embed/' + encodeURIComponent(id) + '?autoplay=1&rel=0&playsinline=1&modestbranding=1' + (p.quality === 'saver' ? '' : '&vq=hd1080')
            + '&enablejsapi=1&origin=' + encodeURIComponent(location.origin)
            + (p.start > 0 ? '&start=' + Math.floor(p.start) : ''), 'youtube');
          // The video's own drawing layer can keep the size it had while the player was opening (small, in a corner):
          // whenever the page or the video changes size, lay it out again at the full size of the box.
          const relayout = () => {
            v.style.width = 'calc(100% - 1px)';
            requestAnimationFrame(() => requestAnimationFrame(() => { v.style.width = '100%'; }));
          };
          window.addEventListener('resize', relayout);
          ['loadedmetadata', 'playing', 'resize'].forEach(e => v.addEventListener(e, relayout));
          [300, 1000, 2500].forEach(ms => setTimeout(relayout, ms));
          // Video files and Steam streams: jump to the shared time once the video knows its length.
          if (p.start > 0) v.addEventListener('loadedmetadata', () => { try { v.currentTime = p.start; } catch (e) {} }, { once: true });
          const frameOf = (src, kind) => {
            v.style.display = 'none';
            const frame = document.createElement('iframe');
            frame.onload = () => {
              watchFrame(frame, kind || (src.indexOf('player.vimeo.com') >= 0 ? 'vimeo' : 'other'));
              try { frame.focus(); } catch (e) {} // the first click goes to the video's own buttons
            };
            frame.src = src;
            frame.allow = 'autoplay; encrypted-media; picture-in-picture; fullscreen';
            frame.allowFullscreen = true;
            frame.referrerPolicy = 'strict-origin-when-cross-origin';
            frame.style.cssText = 'position:absolute;inset:0;width:100%;height:100%;border:0;background:#000';
            document.body.appendChild(frame);
          };

          // ---- Stalls: a stream that stops for 8 s is nudged (back to the live edge, or the loading restarted) ----
          let current = null, stallTimer = 0;
          v.addEventListener('waiting', () => {
            clearTimeout(stallTimer);
            stallTimer = setTimeout(() => {
              if (v.paused || v.readyState >= 3) return;
              try {
                if (current && current.liveSyncPosition) v.currentTime = current.liveSyncPosition;
                else if (current) current.startLoad();
                else if (isFinite(v.currentTime)) v.currentTime = v.currentTime + 0.1;
              } catch (e) {}
            }, 8000);
          });
          ['playing', 'pause', 'seeking'].forEach(e => v.addEventListener(e, () => clearTimeout(stallTimer)));

          // ---- Where playback is (for friends' "Watching" and Watch along), and following a friend's player ----
          // Reported to SeaRaider as "state:<seconds>:<1 playing|0 paused>" on play / pause / seek and every 2 s.
          let pos = p.start || 0, playing = true, lastSent = 0, control = null, noPos = false;
          const report = force => {
            const now = Date.now();
            if (!force && now - lastSent < 2000) return;
            lastSent = now;
            // -1: this player can't say where it is (Twitch).
            post('state:' + (noPos ? -1 : Math.round(pos * 10) / 10) + ':' + (playing ? 1 : 0));
          };
          setInterval(() => report(true), 4000);

          // Plain video (files, Steam streams).
          ['play', 'playing'].forEach(e => v.addEventListener(e, () => { playing = true; pos = v.currentTime; report(true); }));
          v.addEventListener('pause', () => { playing = false; pos = v.currentTime; report(true); });
          v.addEventListener('seeked', () => { pos = v.currentTime; report(true); });
          v.addEventListener('timeupdate', () => { pos = v.currentTime; report(false); });
          control = {
            get: () => v.currentTime,
            seek: t => { try { v.currentTime = t; } catch (e) {} },
            play: () => v.play().catch(() => {}),
            pause: () => v.pause(),
          };

          // YouTube and Vimeo players talk over postMessage.
          const watchFrame = (frame, kind) => {
            const send = msg => { try { frame.contentWindow.postMessage(JSON.stringify(msg), '*'); } catch (e) {} };
            if (kind === 'youtube') {
              const yt = (func, args) => send({ event: 'command', func, args: args || [], id: 1, channel: 'widget' });
              send({ event: 'listening', id: 1, channel: 'widget' });
              setTimeout(() => send({ event: 'listening', id: 1, channel: 'widget' }), 1500);
              window.addEventListener('message', e => {
                if (e.source !== frame.contentWindow) return;
                let d; try { d = typeof e.data === 'string' ? JSON.parse(e.data) : e.data; } catch (x) { return; }
                const info = d && d.info;
                if (!info || typeof info !== 'object') return;
                const wasPlaying = playing;
                if (typeof info.currentTime === 'number') {
                  const jumped = Math.abs(info.currentTime - pos) > 3;
                  pos = info.currentTime;
                  if (jumped) report(true);
                }
                if (typeof info.playerState === 'number' && info.playerState !== 3) playing = info.playerState === 1;
                if (wasPlaying !== playing) report(true); else report(false);
              });
              control = { get: () => pos, seek: t => yt('seekTo', [t, true]), play: () => yt('playVideo'), pause: () => yt('pauseVideo') };
            } else if (kind === 'vimeo') {
              const vm = (method, value) => send(value === undefined ? { method } : { method, value });
              const listen = () => ['timeupdate', 'play', 'pause', 'seeked'].forEach(n => vm('addEventListener', n));
              listen();
              window.addEventListener('message', e => {
                if (e.source !== frame.contentWindow) return;
                let d; try { d = typeof e.data === 'string' ? JSON.parse(e.data) : e.data; } catch (x) { return; }
                if (!d || !d.event) return;
                if (d.event === 'ready') listen();
                if (d.data && typeof d.data.seconds === 'number') pos = d.data.seconds;
                if (d.event === 'play') { playing = true; report(true); }
                else if (d.event === 'pause') { playing = false; report(true); }
                else if (d.event === 'seeked') report(true);
                else report(false);
              });
              control = { get: () => pos, seek: t => vm('setCurrentTime', t), play: () => vm('play'), pause: () => vm('pause') };
            } else {
              control = null; // Twitch: live, or a past broadcast we can't steer
              noPos = true;
            }
          };

          // Watch along: SeaRaider sends "sync:<seconds>:<1|0>" with where the friend's player is now.
          if (window.chrome && window.chrome.webview) window.chrome.webview.addEventListener('message', e => {
            const m = /^sync:([0-9.]+):([01])$/.exec(String(e.data));
            if (!m || !control) return;
            const target = parseFloat(m[1]), play = m[2] === '1';
            if (Math.abs(control.get() - target) > 2.5) { control.seek(target); pos = target; }
            if (play && !playing) control.play();
            if (!play && playing) control.pause();
          });

          if (p.youtube) youtube(p.youtube);
          else if (p.embed) frameOf(p.embed);
          else if (p.dash || p.hls) tryDash(p.dash, () => tryHls(p.hls, () => fail('The stream could not be loaded. It may be offline, or its server may not allow playing it here.')));
          else if (p.src) { v.onerror = () => fail('The trailer could not be loaded.'); v.src = p.src; }
          else fail('No trailer address.');
        </script>
        </body>
        </html>
        """;
}

/// <summary>Where a player is: <paramref name="Position"/> seconds in, playing or paused.</summary>
public sealed record PlaybackReport(TrailerSession Session, double Position, bool Playing);
