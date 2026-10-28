using System.Text;
using DiscordRPC;
using SeaRaider.Core;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Models;
using SeaRaider.Core.Social;

namespace SeaRaider.App.Services;

/// <summary>
/// Your Discord status while SeaRaider is open (Settings › Friends › Discord): what you're doing in it ("Watching Bayern
/// Munich vs Arsenal", "Playing Hades II", "Browsing the store"), and for your Discord friends an "Add me on SeaRaider"
/// button. That button opens a small page on your friends relay, which opens SeaRaider on their PC (searaider://add/…),
/// where SeaRaider asks whether to send you a friend request.
///
/// <para>Discord needs an application to show the status under: its Application ID goes in Settings (from
/// discord.com/developers › New Application; the application's name, e.g. "SeaRaider", is what Discord shows). Its
/// Rich Presence art named "searaider" becomes the picture. Nothing happens when Discord isn't running.</para>
/// </summary>
public sealed class DiscordPresenceService : IDisposable
{
    private readonly ILibraryRepository _repository;
    private readonly IFriendsService _friends;
    private readonly TrailerPlayerService _player;
    private readonly IGameLauncherService _launcher;
    private readonly Timer _debounce;
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private readonly object _lock = new();

    private DiscordRpcClient? _client;
    private string? _clientAppId;
    private string _page = "Browsing the store";
    private string? _playingTitle;
    private DateTime? _playingSinceUtc;

    public DiscordPresenceService(ILibraryRepository repository, IFriendsService friends, TrailerPlayerService player, IGameLauncherService launcher)
    {
        _repository = repository;
        _friends = friends;
        _player = player;
        _launcher = launcher;
        _debounce = new Timer(_ => Push(), null, Timeout.Infinite, Timeout.Infinite);
        repository.SettingsChanged += (_, _) => Soon();
        friends.ConnectionChanged += (_, _) => Soon();
        player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TrailerPlayerService.Current) or nameof(TrailerPlayerService.IsOpen)) Soon();
        };
        launcher.SessionChanged += (_, e) =>
        {
            if (e.IsRunning)
            {
                _playingTitle = repository.GetInstall(e.GameId)?.Title ?? repository.GetGame(e.GameId)?.Title;
                _playingSinceUtc = DateTime.UtcNow;
            }
            else
            {
                _playingTitle = null;
                _playingSinceUtc = null;
            }
            Soon();
        };
    }

    public void Start() => Soon();

    /// <summary>The build's own Discord application (SeaRaiderDiscordAppId in Directory.Build.props), or null.</summary>
    public static string? BuiltInAppId { get; } = System.Reflection.Assembly.GetEntryAssembly()?
        .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
        .OfType<System.Reflection.AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "SeaRaiderDiscordAppId")?.Value is { Length: > 0 } id ? id.Trim() : null;

    /// <summary>What the window shows ("Browsing Media", "Looking at Hades II"): used when nothing is playing.</summary>
    public void SetPage(string text)
    {
        _page = text;
        Soon();
    }

    /// <summary>Discord takes a few updates a minute: changes in quick succession are sent as one, a second later.</summary>
    private void Soon() => _debounce.Change(1000, Timeout.Infinite);

    private void Push()
    {
        try
        {
            lock (_lock)
            {
                var settings = _repository.GetSettings();
                var appId = string.IsNullOrWhiteSpace(settings.DiscordAppId) ? BuiltInAppId : settings.DiscordAppId.Trim();
                if (!settings.DiscordPresence || string.IsNullOrEmpty(appId) || !appId.All(char.IsDigit))
                {
                    Stop();
                    return;
                }
                if (_client is null || _clientAppId != appId)
                {
                    Stop();
                    _client = new DiscordRpcClient(appId) { SkipIdenticalPresence = true };
                    _client.Initialize(); // connects in the background; nothing happens while Discord is closed
                    _clientAppId = appId;
                }
                _client.SetPresence(Build(settings));
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "discord presence");
        }
    }

    private RichPresence Build(AppSettings settings)
    {
        string details, state;
        var since = _startedUtc;
        if (_playingTitle is { } game)
        {
            details = $"Playing {game}";
            state = "Launched from SeaRaider";
            since = _playingSinceUtc ?? since;
        }
        else if (_player.Current is { } watching)
        {
            details = $"Watching {watching.Title}";
            state = watching.IsLive ? "Live in SeaRaider" : _player.IsMultiView ? $"{_player.Alongside.Count + 1} streams at once" : "In SeaRaider's player";
        }
        else
        {
            details = _page;
            state = "In SeaRaider";
        }

        var presence = new RichPresence
        {
            Details = Fit(details),
            State = Fit(state),
            Timestamps = new Timestamps(since),
            Assets = new Assets
            {
                LargeImageKey = settings.Skin == AppSkin.Halloween ? "searaider_halloween" : "searaider",
                LargeImageText = "SeaRaider",
            },
        };
        if (settings.DiscordAddButton && AddLink(settings) is { } link)
            presence.Buttons = [new Button { Label = "Add me on SeaRaider", Url = link }];
        return presence;
    }

    /// <summary>https://&lt;relay host&gt;/add/&lt;friend code&gt;?name=…, from the friends relay's address; null without one.</summary>
    internal string? AddLink(AppSettings settings)
    {
        if (!_friends.IsEnabled || !FriendsService.TryParseRelay(RelayDefaults.Effective(settings.RelayUrl), out var relay)) return null;
        var scheme = relay.Scheme == "wss" ? "https" : "http";
        var port = relay.IsDefaultPort ? string.Empty : ":" + relay.Port;
        var name = _friends.Profile.DisplayName;
        var url = $"{scheme}://{relay.Host}{port}/add/{_friends.Profile.UserId}" + (string.IsNullOrWhiteSpace(name) ? "" : "?name=" + Uri.EscapeDataString(name));
        return url.Length <= 512 ? url : null;
    }

    /// <summary>Discord allows 128 bytes per line.</summary>
    private static string Fit(string text)
    {
        text = text.Trim();
        if (text.Length < 2) text += "  ";
        while (Encoding.UTF8.GetByteCount(text) > 120) text = text[..^2].TrimEnd() + "…";
        return text;
    }

    private void Stop()
    {
        var client = _client;
        _client = null;
        _clientAppId = null;
        if (client is null) return;
        try
        {
            client.ClearPresence();
            client.Dispose();
        }
        catch (Exception)
        {
            // Discord went away
        }
    }

    public void Dispose()
    {
        _debounce.Dispose();
        lock (_lock) Stop();
    }
}
