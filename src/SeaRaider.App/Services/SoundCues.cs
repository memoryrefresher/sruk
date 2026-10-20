using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Catalog;
using SeaRaider.Core.Models;
using SeaRaider.Core.Social;

namespace SeaRaider.App.Services;

/// <summary>
/// When SeaRaider makes a sound: every button and list pick (a soft click; check boxes, switches and the heart "pop";
/// coming-soon tiles open with a rising pair), a download finishing or failing, a game finishing its install, a friend
/// request or message arriving, a friend sharing a video or commenting on yours, and a wishlisted game showing up in the store. What plays, and how loud, is up to
/// <see cref="SoundService"/> and Settings.
/// </summary>
public sealed class SoundCues(SoundService sounds, GameStatusService status, ILibraryRepository repository,
    IFriendsService friends, UpcomingService upcoming, MediaShareService shared)
{
    private readonly Dictionary<Guid, DownloadJobStatus> _jobs = [];
    private readonly HashSet<string> _installed = new(StringComparer.Ordinal);
    private int _incomingRequests;
    private bool _started;

    public void Start()
    {
        if (_started) return;
        _started = true;

        // What's already true at start-up never chimes.
        foreach (var install in repository.GetInstalls().Where(i => i.State is GameInstallState.Installed or GameInstallState.Uninstalling))
            _installed.Add(install.GameId);
        _incomingRequests = CountRequests();

        status.JobUpdated += job =>
        {
            var had = _jobs.TryGetValue(job.Id, out var before);
            _jobs[job.Id] = job.Status;
            if (!had || before == job.Status) return;
            if (job.Status == DownloadJobStatus.Completed) sounds.Play(UiSound.DownloadComplete);
            else if (job.Status == DownloadJobStatus.Failed) sounds.Play(UiSound.Error);
        };
        status.JobRemoved += id => _jobs.Remove(id);

        status.InstallChanged += gameId =>
        {
            var installed = repository.GetInstall(gameId)?.State == GameInstallState.Installed;
            if (installed && _installed.Add(gameId)) sounds.Play(UiSound.InstallComplete);
            else if (!installed && repository.GetInstall(gameId)?.State != GameInstallState.Uninstalling) _installed.Remove(gameId);
        };

        friends.FriendsChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            var requests = CountRequests();
            if (requests > _incomingRequests) sounds.Play(UiSound.FriendRequest);
            _incomingRequests = requests;
        });
        friends.MessageAdded += (_, message) =>
        {
            if (message.IsOutgoing) return;
            // An invite to play or watch rings like a friend request: it wants an answer.
            sounds.Play(message.Invite is null ? UiSound.Message : UiSound.FriendRequest);
        };
        friends.InviteAnswered += (_, _) => sounds.Play(UiSound.Message);

        upcoming.WishlistItemAvailable += (_, _) => sounds.Play(UiSound.Wishlist);

        // Media › Shared: a friend shared something, or reacted / commented on your post or a thread you're in.
        shared.ActivityArrived += (_, _) => sounds.Play(UiSound.Message);

        // Every button in the app (only the one actually clicked, not the buttons it sits inside).
        Button.ClickEvent.AddClassHandler<Button>(OnButtonClick, handledEventsToo: true);
        // List picks: the navigation rail, media thumbnails …
        InputElement.PointerReleasedEvent.AddClassHandler<ListBoxItem>((_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left) sounds.Play(UiSound.Click);
        }, handledEventsToo: true);
    }

    private void OnButtonClick(Button button, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, button)) return;
        var sound = button.Classes.Contains("upcoming-tile") || button.Classes.Contains("media-tile") ? UiSound.Open
            : button is ToggleButton || button.Classes.Contains("wish-heart") || button.Classes.Contains("wish-big") ? UiSound.Toggle
            : UiSound.Click;
        sounds.Play(sound);
    }

    private int CountRequests()
    {
        try { return friends.Friends.Count(f => f.State == FriendshipState.IncomingRequest); }
        catch (InvalidOperationException) { return _incomingRequests; } // list changing underneath
    }
}
