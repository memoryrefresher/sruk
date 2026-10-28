using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SeaRaider.Core.Social;

namespace SeaRaider.App.Services;

/// <summary>
/// Watching with friends:
/// <list type="bullet">
///   <item><b>Your "Watching" status</b>: whatever plays in SeaRaider's player (Media tab, shared links, store trailers)
///         goes into your presence with its position. The player reports every couple of seconds; friends only get an
///         update when something changes that they couldn't work out themselves (another video, play / pause, a jump),
///         so the relay isn't flooded. Settings › Friends can turn it off.</item>
///   <item><b>Watch along</b>: opens what a friend is watching at the point they're at, then follows their player —
///         pause, play, jumps, and the next video they open — until you close the player or press Stop.</item>
///   <item><b>Invite to watch</b>: from the player's top bar, a chat invite with the video and your position.</item>
/// </list>
/// Everything here runs on the UI thread.
/// </summary>
public sealed partial class WatchTogetherService : ObservableObject
{
    /// <summary>How far off (seconds) a player may be before it's moved.</summary>
    private const double Tolerance = 3;

    private readonly TrailerPlayerService _player;
    private readonly PresenceMonitor _presence;
    private readonly IFriendsService _friends;
    private readonly DispatcherTimer _publishTimer;
    private MediaActivity? _published;
    private MediaActivity? _pending;
    private bool _clearPending;
    private double _lastPosition;
    private bool _joining;
    private string? _followUrl;
    private DateTime _lastSyncUtc = DateTime.MinValue;

    public WatchTogetherService(TrailerPlayerService player, PresenceMonitor presence, IFriendsService friends)
    {
        _player = player;
        _presence = presence;
        _friends = friends;

        // Scrubbing sends a burst of jumps: tell friends once it settles.
        _publishTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _publishTimer.Tick += (_, _) =>
        {
            _publishTimer.Stop();
            if (_clearPending) Publish(null);
            else if (_pending is { } pending) Publish(pending);
        };

        player.PropertyChanged += OnPlayerChanged;
        player.PlaybackReported += (_, report) => OnReport(report);
        friends.FriendsChanged += (_, _) => Dispatcher.UIThread.Post(OnFriendsChanged);
        RefreshInviteTargets();
    }

    // =============================================================================================
    // Your own "Watching"
    // =============================================================================================

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TrailerPlayerService.Current)) return;
        var session = _player.Current;
        if (!_joining) StopFollowing(); // closed, or the user picked something else
        InviteStatus = null;
        CanInvite = session is not null;

        _publishTimer.Stop();
        _pending = null;
        if (session is null || string.IsNullOrEmpty(session.Url))
        {
            // Switching videos closes the old one first: only say "stopped" if nothing follows straight away.
            _clearPending = true;
            _publishTimer.Start();
            return;
        }
        _clearPending = false;
        _lastPosition = session.StartSeconds;
        Publish(new MediaActivity
        {
            Title = session.Title,
            Url = session.Url,
            ThumbnailUrl = session.ThumbnailUrl,
            Position = session.StartSeconds,
            Playing = true,
            IsLive = session.IsLive,
            AtUtc = DateTime.UtcNow,
        });
    }

    private void OnReport(PlaybackReport report)
    {
        var now = DateTime.UtcNow;
        if (report.Position >= 0) _lastPosition = report.Position;

        // What friends think is happening, against what is.
        var reference = _pending ?? _published;
        if (reference is not null && reference.Url == report.Session.Url)
        {
            var knowsPosition = report.Position >= 0 && !reference.IsLive;
            var changed = reference.Playing != report.Playing ||
                          (knowsPosition && Math.Abs(reference.PositionAt(now) - report.Position) > Tolerance);
            if (changed && !_clearPending)
            {
                _pending = reference with { Position = knowsPosition ? report.Position : reference.Position, Playing = report.Playing, AtUtc = now };
                _publishTimer.Stop();
                _publishTimer.Start();
            }
        }

        if (IsFollowing) FollowFriend(force: false, myPosition: report.Position, myPlaying: report.Playing);
    }

    private void Publish(MediaActivity? activity)
    {
        _pending = null;
        _clearPending = false;
        _published = activity;
        _presence.SetWatching(activity);
    }

    // =============================================================================================
    // Watch along
    // =============================================================================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFollowing))]
    private string? _followingId;

    [ObservableProperty] private string? _followText;

    public bool IsFollowing => FollowingId is not null;

    /// <summary>Opens what <paramref name="friendId"/> is watching, where they are, and follows their player.</summary>
    /// <exception cref="InvalidOperationException">They aren't watching anything (any more).</exception>
    public void Join(string friendId)
    {
        var friend = _friends.Friends.FirstOrDefault(f => f.UserId == friendId && f.State == FriendshipState.Friend);
        if (friend?.Watching is not { } media)
            throw new InvalidOperationException($"{friend?.DisplayName ?? "Your friend"} isn't watching anything right now.");

        FollowingId = friendId;
        FollowText = $"Watching along with {friend.DisplayName}";
        PlayFriend(media);
    }

    /// <summary>Plays something without following anyone (an invite whose sender stopped watching).</summary>
    public void PlayInvite(ChatInvite invite)
    {
        if (invite.MediaUrl is not { } url) return;
        _player.Play(url, url, invite.Title, invite.StartSeconds, invite.CoverUrl);
    }

    [RelayCommand]
    public void StopFollowing()
    {
        FollowingId = null;
        FollowText = null;
        _followUrl = null;
    }

    private void PlayFriend(MediaActivity media)
    {
        _followUrl = media.Url;
        _lastSyncUtc = DateTime.UtcNow; // the player starts at their position; give it time to load before steering
        _joining = true;
        try
        {
            var start = media.IsLive ? (int?)null : (int)Math.Round(media.PositionAt(DateTime.UtcNow));
            _player.Play(media.Url, media.Url, media.Title, start, media.ThumbnailUrl);
        }
        finally
        {
            _joining = false;
        }
    }

    private void OnFriendsChanged()
    {
        RefreshInviteTargets();
        if (IsFollowing) FollowFriend(force: true, myPosition: _lastPosition, myPlaying: null);
    }

    /// <summary>Keeps up with the friend: their next video, their position, play / pause.</summary>
    private void FollowFriend(bool force, double myPosition, bool? myPlaying)
    {
        var friend = _friends.Friends.FirstOrDefault(f => f.UserId == FollowingId && f.State == FriendshipState.Friend);
        if (friend is null)
        {
            StopFollowing();
            return;
        }
        if (friend.Watching is not { } media)
        {
            FollowText = $"{friend.DisplayName} stopped watching";
            return;
        }
        FollowText = $"Watching along with {friend.DisplayName}";

        if (media.Url != _followUrl)
        {
            PlayFriend(media); // they opened something else
            return;
        }
        if (media.IsLive || _player.Current is null) return;

        var now = DateTime.UtcNow;
        var target = media.PositionAt(now);
        var off = myPosition >= 0 && Math.Abs(target - myPosition) > Tolerance;
        var wrongState = myPlaying is { } playing && playing != media.Playing;
        // Not more than every 4 s, so a seek has time to land before it's judged.
        if ((force || off || wrongState) && now - _lastSyncUtc > TimeSpan.FromSeconds(4))
        {
            _lastSyncUtc = now;
            _player.Sync(target, media.Playing);
        }
    }

    // =============================================================================================
    // Invite to watch (player's top bar)
    // =============================================================================================

    public ObservableCollection<WatchInviteTargetViewModel> InviteTargets { get; } = [];

    [ObservableProperty] private bool _canInvite;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInviteStatus))]
    private string? _inviteStatus;

    public bool HasInviteStatus => !string.IsNullOrEmpty(InviteStatus);
    public bool HasNoInviteTargets => InviteTargets.Count == 0;

    private void RefreshInviteTargets()
    {
        List<Friend> friends;
        try { friends = _friends.Friends.Where(f => f.State == FriendshipState.Friend).ToList(); }
        catch (InvalidOperationException) { return; }
        InviteTargets.Clear();
        foreach (var friend in friends.OrderBy(f => f.Status == UserStatus.Offline).ThenBy(f => f.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            InviteTargets.Add(new WatchInviteTargetViewModel(this, friend.UserId, friend.DisplayName, friend.StatusText, friend.Status != UserStatus.Offline));
        OnPropertyChanged(nameof(HasNoInviteTargets));
    }

    internal async Task InviteAsync(WatchInviteTargetViewModel target)
    {
        if (_player.Current is not { } session || string.IsNullOrEmpty(session.Url)) return;
        try
        {
            InviteStatus = $"Inviting {target.Name}…";
            var message = await _friends.SendInviteAsync(target.UserId, new ChatInvite
            {
                Kind = InviteKind.Watch,
                Title = session.Title,
                MediaUrl = session.Url,
                CoverUrl = session.ThumbnailUrl,
                StartSeconds = session.IsLive ? null : (int)Math.Round(_lastPosition),
            });
            InviteStatus = message.Failed ? $"Couldn't reach the relay; the invite to {target.Name} will be retried from the chat." : $"Invited {target.Name}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            InviteStatus = ex.Message;
        }
    }
}

/// <summary>A friend in the player's "Invite to watch" menu.</summary>
public sealed partial class WatchInviteTargetViewModel(WatchTogetherService owner, string userId, string name, string status, bool isOnline)
    : ObservableObject
{
    public string UserId { get; } = userId;
    public string Name { get; } = name;
    public string Status { get; } = status;
    public bool IsOnline { get; } = isOnline;

    [RelayCommand]
    private Task InviteAsync() => owner.InviteAsync(this);
}
