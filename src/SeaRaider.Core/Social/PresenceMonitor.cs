using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SeaRaider.Core.Abstractions;

namespace SeaRaider.Core.Social;

/// <summary>
/// Works out your status from what the PC is doing:
/// <list type="bullet">
///   <item><b>In-Game</b> while a game launched from SeaRaider runs (the launcher's process tracking, which also follows
///         launcher stubs into the real game process). The title is shared unless Settings say otherwise.</item>
///   <item><b>Away</b> after <see cref="Models.AppSettings.AwayAfterMinutes"/> without keyboard or mouse input
///         (GetLastInputInfo), or when chosen in the friends panel.</item>
///   <item><b>Watching</b>: what plays in SeaRaider's player (<see cref="SetWatching"/>), shown with any status; a
///         video that's playing keeps you from going Away for lack of input.</item>
///   <item><b>Offline</b> to friends when "Appear offline" is chosen.</item>
///   <item><b>Online</b> otherwise.</item>
/// </list>
/// </summary>
public sealed class PresenceMonitor : IDisposable
{
    private static readonly TimeSpan IdleCheckInterval = TimeSpan.FromSeconds(20);

    private readonly ILibraryRepository _repository;
    private readonly ConcurrentDictionary<string, string> _runningGames = new(StringComparer.Ordinal);
    private readonly Timer _idleTimer;
    private PresenceMode _mode = PresenceMode.Automatic;
    private bool _active = true;
    private bool _idle;
    private MediaActivity? _watching;

    public PresenceMonitor(IGameLauncherService launcher, ILibraryRepository repository)
    {
        _repository = repository;
        launcher.SessionChanged += OnSessionChanged;
        _idleTimer = new Timer(_ => CheckIdle(), null, IdleCheckInterval, IdleCheckInterval);
        Current = Compute();
    }

    /// <summary>Raised (any thread) when <see cref="Current"/> changes.</summary>
    public event EventHandler<Presence>? Changed;

    public Presence Current { get; private set; }

    /// <summary>When false (friends switched off) the idle check stops; nothing is polled in the background.</summary>
    public bool IsActive
    {
        get => _active;
        set
        {
            _active = value;
            _idleTimer.Change(value ? IdleCheckInterval : Timeout.InfiniteTimeSpan, value ? IdleCheckInterval : Timeout.InfiniteTimeSpan);
        }
    }

    public PresenceMode Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            Update();
        }
    }

    /// <summary>Re-evaluates (e.g. after the "share what I'm playing" setting changed).</summary>
    public void Update()
    {
        var next = Compute();
        if (next == Current) return;
        Current = next;
        Changed?.Invoke(this, next);
    }

    /// <summary>What plays in SeaRaider's player now (null: nothing). Call it when the video, play / pause or position
    /// jumps change, not for every tick: each change is sent to friends.</summary>
    public void SetWatching(MediaActivity? activity)
    {
        _watching = activity;
        Update();
    }

    /// <summary>The game running right now (launched from SeaRaider), or null — for "Invite to play".</summary>
    public string? RunningGameId => _runningGames.Keys.FirstOrDefault();

    /// <summary>Its display name.</summary>
    public string? RunningGameTitle => _runningGames.Values.FirstOrDefault();

    private void OnSessionChanged(object? sender, GameSessionEventArgs e)
    {
        if (e.IsRunning)
            // The game's name ("Hades II"), not the release title ("Hades II – Build 18923104 [FitGirl Repack]").
            _runningGames[e.GameId] = Catalog.DisplayText.Title(_repository.GetInstall(e.GameId)?.Title ?? _repository.GetGame(e.GameId)?.Title ?? "a game");
        else
            _runningGames.TryRemove(e.GameId, out _);
        Update();
    }

    private Presence Compute()
    {
        if (_mode == PresenceMode.Invisible) return Presence.Offline;

        var settings = _repository.GetSettings();
        var media = settings.ShareMediaActivity ? _watching : null;
        if (!_runningGames.IsEmpty)
            return new Presence(UserStatus.InGame, settings.ShareGameActivity ? _runningGames.Values.First() : null, media);

        // Watching something isn't being away, even without touching the mouse.
        var away = _mode == PresenceMode.Away || (_idle && _watching is not { Playing: true });
        return new Presence(away ? UserStatus.Away : UserStatus.Online, null, media);
    }

    private void CheckIdle()
    {
        var minutes = _repository.GetSettings().AwayAfterMinutes;
        var idle = minutes > 0 && IdleTime() is { } idleFor && idleFor >= TimeSpan.FromMinutes(minutes);
        if (idle == _idle) return;
        _idle = idle;
        Update();
    }

    /// <summary>Time since the last keyboard / mouse input (Windows), or null when unknown.</summary>
    public static TimeSpan? IdleTime()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return null;
        var idleMs = unchecked((uint)Environment.TickCount - info.dwTime); // both wrap every 49.7 days
        return TimeSpan.FromMilliseconds(idleMs);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    public void Dispose() => _idleTimer.Dispose();
}
