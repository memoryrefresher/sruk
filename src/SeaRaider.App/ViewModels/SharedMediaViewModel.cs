using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SeaRaider.App.Services;
using SeaRaider.Core.Social;

namespace SeaRaider.App.ViewModels;

/// <summary>
/// Media › Shared: links friends shared (videos, trailers, streams, optionally from a time), with emoji reactions and
/// comments whose times ("2:15") play the video from there. See <see cref="MediaShareService"/> for how it travels.
/// </summary>
public sealed partial class SharedMediaViewModel : ViewModelBase
{
    private readonly MediaShareService _share;
    private readonly IFriendsService _friends;
    private readonly TrailerPlayerService _player;
    private readonly ShellService _shell;
    private readonly NavigationService _navigation;
    private readonly DispatcherTimer _reloadTimer;
    private readonly Dictionary<string, SharedPostViewModel> _byId = new(StringComparer.Ordinal);
    private bool _isOnScreen;

    public SharedMediaViewModel(MediaShareService share, IFriendsService friends, TrailerPlayerService player, ShellService shell,
        NavigationService navigation)
    {
        _share = share;
        _friends = friends;
        _player = player;
        _shell = shell;
        _navigation = navigation;

        _reloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _reloadTimer.Tick += (_, _) =>
        {
            _reloadTimer.Stop();
            Reload();
        };
        share.Changed += (_, _) => Dispatcher.UIThread.Post(ReloadSoon);
        friends.FriendsChanged += (_, _) => Dispatcher.UIThread.Post(UpdateHint);
        friends.ConnectionChanged += (_, _) => Dispatcher.UIThread.Post(UpdateHint);
        Reload();
        UpdateHint();
    }

    public ObservableCollection<SharedPostViewModel> Posts { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private int _postCount;

    public bool IsEmpty => PostCount == 0;

    /// <summary>Friends' posts and comments not seen yet (shown on the "Shared" switch).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread))]
    private int _unreadCount;

    public bool HasUnread => UnreadCount > 0;

    // ---- composer ------------------------------------------------------------------------------------

    [ObservableProperty] private string _shareUrl = string.Empty;
    [ObservableProperty] private string _shareNote = string.Empty;

    /// <summary>"Watch from" time; filled in from the link when it carries one (?t=…).</summary>
    [ObservableProperty] private string _shareStart = string.Empty;

    [ObservableProperty] private bool _isSharing;

    /// <summary>Why friends might not get it (Friends off, no friends, offline), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHint))]
    private string? _hint;

    [ObservableProperty] private bool _canOpenFriendsSettings;

    /// <summary>"Goes to your 4 friends".</summary>
    [ObservableProperty] private string _audienceText = string.Empty;

    public bool HasHint => !string.IsNullOrEmpty(Hint);

    public int MaxNoteLength => MediaShareService.MaxNoteLength;

    partial void OnShareUrlChanged(string value)
    {
        if (ShareStart.Length == 0 && MediaTime.FromUrl(value) is { } start) ShareStart = MediaTime.Format(start);
    }

    /// <summary>Fills in the composer (Share on a video box in Channels).</summary>
    public void Prefill(string url)
    {
        ShareUrl = url;
        ShareNote = string.Empty;
        ShareStart = MediaTime.FromUrl(url) is { } start ? MediaTime.Format(start) : string.Empty;
    }

    [RelayCommand]
    private Task ShareAsync() => RunSafeAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(ShareUrl)) throw new ArgumentException("Paste a link to a video, trailer or stream first.");
        int? start = null;
        if (!string.IsNullOrWhiteSpace(ShareStart))
            start = MediaTime.Parse(ShareStart) ?? throw new ArgumentException("Write the start time like 1:23 or 1:02:03 (or leave it empty).");

        IsSharing = true;
        try
        {
            var (url, note) = (ShareUrl, ShareNote);
            await Task.Run(() => _share.ShareAsync(url, note, start));
            ShareUrl = string.Empty;
            ShareNote = string.Empty;
            ShareStart = string.Empty;
        }
        finally
        {
            IsSharing = false;
        }
    });

    [RelayCommand]
    private void OpenFriendsSettings()
    {
        _navigation.RequestedSettingsTab = SettingsTab.Friends;
        _navigation.ShowPage(AppPage.Settings);
    }

    // ---- on screen / unread --------------------------------------------------------------------------

    /// <summary>The feed is showing (Media page open on "Shared"): what arrives counts as read.</summary>
    public void SetOnScreen(bool onScreen)
    {
        _isOnScreen = onScreen;
        if (onScreen) MarkSeen();
    }

    private void MarkSeen()
    {
        _share.MarkSeen();
        UnreadCount = 0;
    }

    private void ReloadSoon()
    {
        _reloadTimer.Stop();
        _reloadTimer.Start();
    }

    /// <summary>Brings the list up to date in place (typed comments and open threads survive).</summary>
    private void Reload()
    {
        var me = _share.MyId;
        var posts = _share.GetPosts();
        for (var i = 0; i < posts.Count; i++)
        {
            var post = posts[i];
            if (_byId.TryGetValue(post.Id, out var vm)) vm.Update(post, me);
            else _byId[post.Id] = vm = new SharedPostViewModel(this, post, me);

            var at = Posts.IndexOf(vm);
            if (at < 0) Posts.Insert(i, vm);
            else if (at != i) Posts.Move(at, i);
        }
        for (var j = Posts.Count - 1; j >= posts.Count; j--)
        {
            _byId.Remove(Posts[j].Id);
            Posts.RemoveAt(j);
        }
        PostCount = Posts.Count;

        if (_isOnScreen) MarkSeen();
        else UnreadCount = _share.UnreadCount;
    }

    private void UpdateHint()
    {
        var friends = _share.FriendCount;
        AudienceText = friends switch
        {
            0 => "Only you will see it until you add friends",
            1 => "Goes to your friend",
            _ => $"Goes to your {friends} friends",
        };
        CanOpenFriendsSettings = !_friends.IsEnabled;
        Hint = !_friends.IsEnabled
            ? "Friends are turned off, so nobody else sees what you share. Turn them on in Settings › Friends."
            : friends == 0
                ? "Add friends from the Friends panel (your friend code is there) and what you share shows up on their Media tab."
                : _friends.ConnectionState != SocialConnectionState.Connected
                    ? "Not connected to the friends relay right now. What you share, react and comment goes out as soon as it's back."
                    : null;
    }

    // ---- actions from posts --------------------------------------------------------------------------

    internal void Play(SharedPostViewModel post, int? seconds) =>
        RunSafe(() => _player.Play(post.Url, post.Url, post.Title, seconds ?? post.StartSeconds));

    internal void OpenLink(string url) => RunSafe(() => _shell.OpenUrl(url));

    internal Task CopyLinkAsync(string url) => RunSafeAsync(() => _shell.CopyToClipboardAsync(url));

    internal Task ReactAsync(SharedPostViewModel post, string emoji) =>
        RunSafeAsync(() => Task.Run(() => _share.ToggleReactionAsync(post.Id, emoji)));

    internal Task CommentAsync(SharedPostViewModel post, string text) =>
        RunSafeAsync(() => Task.Run(() => _share.CommentAsync(post.Id, text)));

    internal Task RemoveAsync(SharedPostViewModel post) =>
        RunSafeAsync(() => Task.Run(() => _share.RemovePostAsync(post.Id)));

    internal Task RemoveCommentAsync(SharedPostViewModel post, string commentId) =>
        RunSafeAsync(() => Task.Run(() => _share.RemoveCommentAsync(post.Id, commentId)));

    internal static string Monogram(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0 ? "?" : string.Concat(words.Take(2).Select(w => char.ToUpperInvariant(w[0])));
    }
}

/// <summary>One shared link: the video box, who shared it and why, reactions and comments.</summary>
public sealed partial class SharedPostViewModel : ObservableObject
{
    private const int CollapsedComments = 3;
    private readonly SharedMediaViewModel _owner;
    private readonly List<SharedCommentViewModel> _allComments = [];
    private SharedMediaPost _post;

    public SharedPostViewModel(SharedMediaViewModel owner, SharedMediaPost post, string me)
    {
        _owner = owner;
        _post = post;
        Id = post.Id;
        Url = post.Url;
        IsMine = post.AuthorId == me;
        foreach (var (emoji, name) in MediaReactions.All) Reactions.Add(new ReactionChipViewModel(this, emoji, name));
        Update(post, me);
    }

    public string Id { get; }
    public string Url { get; }
    public bool IsMine { get; }

    public string Title => _post.Title;
    public string? ThumbnailUrl => _post.ThumbnailUrl;
    public bool HasThumbnail => !string.IsNullOrEmpty(_post.ThumbnailUrl);
    public string TitleMonogram => SharedMediaViewModel.Monogram(_post.Title);
    public string? Note => _post.Note;
    public bool HasNote => !string.IsNullOrEmpty(_post.Note);
    public string AuthorName => IsMine ? "You" : _post.AuthorName;
    public string AuthorMonogram => SharedMediaViewModel.Monogram(_post.AuthorName);
    public int? StartSeconds => _post.StartSeconds;
    public bool HasStart => _post.StartSeconds is > 0;
    public string StartText => _post.StartSeconds is { } s ? $"from {MediaTime.Format(s)}" : string.Empty;

    /// <summary>"shared 3 h ago · YouTube".</summary>
    public string Detail => string.Join(" · ", new[] { $"shared {MediaViewModel.Age(_post.PostedAtUtc)}", _post.Channel }
        .Where(s => !string.IsNullOrEmpty(s)));

    public string RemoveText => IsMine ? "Delete" : "Hide";
    public string RemoveTip => IsMine ? "Delete this post for everyone" : "Hide this post from your feed";

    public ObservableCollection<ReactionChipViewModel> Reactions { get; } = [];

    /// <summary>The comments shown: the latest few, or all of them.</summary>
    public ObservableCollection<SharedCommentViewModel> Comments { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandText))]
    private bool _showAllComments;

    [ObservableProperty] private int _commentCount;
    [ObservableProperty] private bool _canExpand;
    [ObservableProperty] private string _commentText = string.Empty;
    [ObservableProperty] private bool _isSending;

    public string ExpandText => ShowAllComments ? "Show fewer comments" : $"View all {CommentCount} comments";

    public string CommentWatermark => HasStart || _post.Comments.Count > 0 ? "Add a comment (times like 2:15 become links)" : "Add a comment";

    /// <summary>Takes in a newer copy of the post (a reaction or comment arrived).</summary>
    public void Update(SharedMediaPost post, string me)
    {
        _post = post;
        foreach (var chip in Reactions)
        {
            var who = post.Reactions.Where(r => r.Emoji == chip.Emoji).ToList();
            chip.Set(who.Count, who.Any(r => r.UserId == me),
                who.Count == 0 ? chip.Name : string.Join(", ", who.Select(r => r.UserId == me ? "You" : r.Name)));
        }

        // Comments by id, oldest first; existing view-models are kept.
        var byId = _allComments.ToDictionary(c => c.Id, StringComparer.Ordinal);
        _allComments.Clear();
        foreach (var comment in post.Comments.OrderBy(c => c.PostedAtUtc))
        {
            if (byId.TryGetValue(comment.Id, out var vm)) vm.RefreshAge(comment);
            else vm = new SharedCommentViewModel(this, comment, me, IsMine);
            _allComments.Add(vm);
        }
        CommentCount = _allComments.Count;
        CanExpand = CommentCount > CollapsedComments;
        OnPropertyChanged(nameof(ExpandText));
        FillComments();

        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(CommentWatermark));
    }

    private void FillComments()
    {
        var shown = ShowAllComments ? _allComments : _allComments.Skip(Math.Max(0, _allComments.Count - CollapsedComments)).ToList();
        for (var i = 0; i < shown.Count; i++)
        {
            var at = Comments.IndexOf(shown[i]);
            if (at < 0) Comments.Insert(i, shown[i]);
            else if (at != i) Comments.Move(at, i);
        }
        for (var j = Comments.Count - 1; j >= shown.Count; j--) Comments.RemoveAt(j);
    }

    partial void OnShowAllCommentsChanged(bool value) => FillComments();

    [RelayCommand]
    private void ToggleComments() => ShowAllComments = !ShowAllComments;

    [RelayCommand]
    private void Play() => _owner.Play(this, null);

    internal void PlayAt(int seconds) => _owner.Play(this, seconds);

    [RelayCommand]
    private void OpenLink() => _owner.OpenLink(Url);

    [RelayCommand]
    private Task CopyLinkAsync() => _owner.CopyLinkAsync(Url);

    [RelayCommand]
    private Task RemoveAsync() => _owner.RemoveAsync(this);

    [RelayCommand]
    private async Task PostCommentAsync()
    {
        var text = CommentText.Trim();
        if (text.Length == 0 || IsSending) return;
        IsSending = true;
        try
        {
            CommentText = string.Empty;
            await _owner.CommentAsync(this, text);
        }
        finally
        {
            IsSending = false;
        }
    }

    internal Task ReactAsync(string emoji) => _owner.ReactAsync(this, emoji);

    internal Task RemoveCommentAsync(string commentId) => _owner.RemoveCommentAsync(this, commentId);
}

/// <summary>One reaction button under a post: the emoji, how many, and whether one of them is yours.</summary>
public sealed partial class ReactionChipViewModel(SharedPostViewModel post, string emoji, string name) : ObservableObject
{
    public string Emoji { get; } = emoji;
    public string Name { get; } = name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCount), nameof(CountText))]
    private int _count;

    [ObservableProperty] private bool _isMine;

    /// <summary>Who reacted ("You, Sam"), or the reaction's name when nobody has.</summary>
    [ObservableProperty] private string _tip = name;

    public bool HasCount => Count > 0;
    public string CountText => Count.ToString(System.Globalization.CultureInfo.CurrentCulture);

    internal void Set(int count, bool mine, string tip)
    {
        Count = count;
        IsMine = mine;
        Tip = tip;
    }

    [RelayCommand]
    private Task ToggleAsync() => post.ReactAsync(Emoji);
}

/// <summary>A comment, with its times ("2:15") as play buttons.</summary>
public sealed partial class SharedCommentViewModel : ObservableObject
{
    private readonly SharedPostViewModel _post;

    public SharedCommentViewModel(SharedPostViewModel post, SharedMediaComment comment, string me, bool postIsMine)
    {
        _post = post;
        Id = comment.Id;
        IsMine = comment.AuthorId == me;
        AuthorName = IsMine ? "You" : comment.AuthorName;
        Monogram = SharedMediaViewModel.Monogram(comment.AuthorName);
        Text = comment.Text;
        CanDelete = IsMine || postIsMine;
        Times = MediaTime.FindInText(comment.Text).Take(4).Select(s => new TimeChipViewModel(post, s)).ToList();
        RefreshAge(comment);
    }

    public string Id { get; }
    public bool IsMine { get; }
    public string AuthorName { get; }
    public string Monogram { get; }
    public string Text { get; }
    public bool CanDelete { get; }
    public IReadOnlyList<TimeChipViewModel> Times { get; }
    public bool HasTimes => Times.Count > 0;

    [ObservableProperty] private string _age = string.Empty;

    internal void RefreshAge(SharedMediaComment comment) => Age = MediaViewModel.Age(comment.PostedAtUtc);

    [RelayCommand]
    private Task DeleteAsync() => _post.RemoveCommentAsync(Id);
}

/// <summary>"▶ 2:15": plays the post's video from that time.</summary>
public sealed partial class TimeChipViewModel : ObservableObject
{
    private readonly SharedPostViewModel _post;

    public TimeChipViewModel(SharedPostViewModel post, int seconds)
    {
        _post = post;
        Seconds = seconds;
        Label = MediaTime.Format(seconds);
    }

    public int Seconds { get; }
    public string Label { get; }

    [RelayCommand]
    private void Play() => _post.PlayAt(Seconds);
}
