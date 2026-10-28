using System.Text.Json;
using Microsoft.Extensions.Logging;
using SeaRaider.Core.Catalog;

namespace SeaRaider.Core.Social;

/// <summary>Something friends did on the shared feed that's worth a sound and an unread mark.</summary>
/// <param name="PostId">The post it happened on.</param>
/// <param name="Text">"Sam shared Elden Ring trailer", "Sam commented on your post".</param>
public sealed record SharedMediaActivity(string PostId, string Text);

/// <summary>
/// The Media tab's "Shared" feed: friends share videos, trailers and streams (optionally from a time), react with emoji
/// and comment, with clickable times in comments.
/// <list type="bullet">
///   <item><b>Travel</b>: plain friend messages over the relay (<see cref="MediaShareKinds"/>), so nothing changes on
///         the relay. The relay holds them for friends who are offline (up to its queue limits); messages this app
///         couldn't hand to the relay wait in an outbox and go when the connection is back.</item>
///   <item><b>Who sees what</b>: a post goes to everyone who was your friend when you shared it. Reactions and comments
///         go to the person who shared the post, and their app passes them on to everyone it was shared with, so
///         friends of yours who aren't friends with each other still see one conversation. Each app only takes post
///         changes from the post's sharer (or, on the sharer's own PC, from friends it was shared with).</item>
///   <item><b>Storage</b>: posts, reactions and comments are kept on every PC that got them (social.db).</item>
/// </list>
/// Events are raised on background threads.
/// </summary>
public sealed class MediaShareService : IDisposable
{
    public const int MaxNoteLength = 500;
    public const int MaxCommentLength = 500;
    private const int MaxCommentsPerPost = 300;

    /// <summary>The relay allows 60 messages per 10 s; leave room for chat and presence.</summary>
    private const int SendsPerTenSeconds = 40;

    private readonly SocialStore _store;
    private readonly IFriendsService _friends;
    private readonly IFriendsTransport _transport;
    private readonly MediaService _media;
    private readonly ILogger<MediaShareService> _logger;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Queue<DateTime> _recentSends = new();
    private readonly CancellationTokenSource _lifetime = new();
    private int _flushing;

    public MediaShareService(SocialStore store, IFriendsService friends, IFriendsTransport transport, MediaService media,
        ILogger<MediaShareService> logger)
    {
        _store = store;
        _friends = friends;
        _transport = transport;
        _media = media;
        _logger = logger;

        _transport.FrameReceived += (_, frame) =>
        {
            if (frame is { Type: "deliver", From: { } from, Kind: { } kind } && MediaShareKinds.IsMediaKind(kind))
            {
                try { OnDelivery(from, kind, frame); }
                catch (Exception ex) { _logger.LogWarning(ex, "Shared feed message {Kind} from {From} failed", kind, from); }
            }
        };
        // Back online: send what's waiting.
        _friends.ConnectionChanged += (_, _) =>
        {
            if (_friends.ConnectionState == SocialConnectionState.Connected) _ = FlushOutboxAsync();
        };
    }

    /// <summary>Posts, reactions or comments changed (reload the feed).</summary>
    public event EventHandler? Changed;

    /// <summary>A friend shared something, or reacted / commented where you'd want to know.</summary>
    public event EventHandler<SharedMediaActivity>? ActivityArrived;

    public string MyId => _friends.Profile.UserId;

    /// <summary>Every post, newest first.</summary>
    public IReadOnlyList<SharedMediaPost> GetPosts() => _store.GetSharedPosts();

    /// <summary>Posts and comments from friends that arrived since the feed was last looked at.</summary>
    public int UnreadCount
    {
        get
        {
            var seen = _friends.Profile.MediaFeedSeenUtc;
            var me = MyId;
            return _store.GetSharedPosts().Sum(p =>
                (p.AuthorId != me && p.ReceivedUtc > seen ? 1 : 0) + p.Comments.Count(c => c.AuthorId != me && c.ReceivedUtc > seen));
        }
    }

    /// <summary>The feed is on screen: everything so far counts as read (no <see cref="Changed"/>: nothing in the feed changed).</summary>
    public void MarkSeen()
    {
        _friends.Profile.MediaFeedSeenUtc = DateTime.UtcNow;
        _store.SaveProfile(_friends.Profile);
    }

    /// <summary>Who will get a new post (friends right now).</summary>
    public int FriendCount => _friends.Friends.Count(f => f.State == FriendshipState.Friend);

    // =============================================================================================
    // Your own actions
    // =============================================================================================

    /// <summary>Looks the link up (title, picture), saves the post and sends it to every friend.</summary>
    /// <param name="startSeconds">Where playback starts; null takes the time written into the link, if any.</param>
    /// <exception cref="ArgumentException">Not a link.</exception>
    /// <param name="title">The name to show (a title from a site's list knows it better than the page does).</param>
    /// <param name="thumbnailUrl">The picture to show (the poster or crest found for the title).</param>
    public async Task<SharedMediaPost> ShareAsync(string url, string? note, int? startSeconds, CancellationToken ct = default,
        string? title = null, string? thumbnailUrl = null)
    {
        var link = await _media.DescribeLinkAsync(url, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(title)) link = link with { Title = title.Trim() };
        if (!string.IsNullOrWhiteSpace(thumbnailUrl)) link = link with { ThumbnailUrl = thumbnailUrl };
        var audience = _friends.Friends.Where(f => f.State == FriendshipState.Friend).Select(f => f.UserId).ToList();
        var now = DateTime.UtcNow;
        var post = new SharedMediaPost
        {
            AuthorId = MyId,
            AuthorName = _friends.Profile.DisplayName,
            Url = link.Url,
            Title = link.Title,
            ThumbnailUrl = link.ThumbnailUrl,
            Channel = link.Channel,
            Note = Cut(note, MaxNoteLength),
            StartSeconds = startSeconds is > 0 and <= MediaTime.MaxSeconds ? startSeconds : link.StartSeconds,
            PostedAtUtc = now,
            ReceivedUtc = now,
            Audience = audience,
        };
        _store.UpsertSharedPost(post);
        Changed?.Invoke(this, EventArgs.Empty);

        var payload = new MediaPostPayload(post.Id, post.Url, post.Title, post.ThumbnailUrl, post.Channel, post.Note, post.StartSeconds, post.PostedAtUtc);
        await SendToManyAsync(audience, MediaShareKinds.Post, payload).ConfigureAwait(false);
        return post;
    }

    /// <summary>Adds or takes back your reaction.</summary>
    public async Task ToggleReactionAsync(string postId, string emoji)
    {
        if (!MediaReactions.IsKnown(emoji)) return;
        bool on;
        SharedMediaPost post;
        lock (_lock)
        {
            if (_store.GetSharedPost(postId) is not { } found) return;
            post = found;
            var mine = post.Reactions.FirstOrDefault(r => r.UserId == MyId && r.Emoji == emoji);
            on = mine is null;
            if (on) post.Reactions.Add(new SharedMediaReaction { UserId = MyId, Name = _friends.Profile.DisplayName, Emoji = emoji });
            else post.Reactions.Remove(mine!);
            _store.UpsertSharedPost(post);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        await PublishAsync(post, MediaShareKinds.React, new MediaReactPayload(postId, MyId, _friends.Profile.DisplayName, emoji, on), MyId)
            .ConfigureAwait(false);
    }

    /// <exception cref="ArgumentException">Nothing typed.</exception>
    public async Task CommentAsync(string postId, string text)
    {
        var clean = Cut(text, MaxCommentLength) ?? throw new ArgumentException("Type a comment first.");
        var now = DateTime.UtcNow;
        var comment = new SharedMediaComment
        {
            AuthorId = MyId,
            AuthorName = _friends.Profile.DisplayName,
            Text = clean,
            PostedAtUtc = now,
            ReceivedUtc = now,
        };
        SharedMediaPost post;
        lock (_lock)
        {
            post = _store.GetSharedPost(postId) ?? throw new InvalidOperationException("That post is gone.");
            if (post.Comments.Count >= MaxCommentsPerPost) throw new InvalidOperationException("This post has as many comments as it can hold.");
            post.Comments.Add(comment);
            _store.UpsertSharedPost(post);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        await PublishAsync(post, MediaShareKinds.Comment,
            new MediaCommentPayload(postId, comment.Id, MyId, comment.AuthorName, comment.Text, comment.PostedAtUtc), MyId).ConfigureAwait(false);
    }

    /// <summary>Your post: deleted for everyone. Someone else's: hidden from your feed only.</summary>
    public async Task RemovePostAsync(string postId)
    {
        if (_store.GetSharedPost(postId) is not { } post) return;
        _store.DeleteSharedPost(postId);
        Changed?.Invoke(this, EventArgs.Empty);
        if (post.AuthorId == MyId)
            await SendToManyAsync(post.Audience, MediaShareKinds.Remove, new MediaRemovePayload(postId, null)).ConfigureAwait(false);
    }

    /// <summary>Deletes a comment: your own anywhere, or anyone's on your post.</summary>
    public async Task RemoveCommentAsync(string postId, string commentId)
    {
        SharedMediaPost post;
        lock (_lock)
        {
            if (_store.GetSharedPost(postId) is not { } found) return;
            post = found;
            var comment = post.Comments.FirstOrDefault(c => c.Id == commentId);
            if (comment is null || (comment.AuthorId != MyId && post.AuthorId != MyId)) return;
            post.Comments.Remove(comment);
            _store.UpsertSharedPost(post);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        await PublishAsync(post, MediaShareKinds.Remove, new MediaRemovePayload(postId, commentId), MyId).ConfigureAwait(false);
    }

    /// <summary>A change to a post: on your own post, to everyone it was shared with (but <paramref name="except"/>);
    /// on someone else's, to its sharer, who passes it on.</summary>
    private Task PublishAsync(SharedMediaPost post, string kind, object payload, string? except) =>
        post.AuthorId == MyId
            ? SendToManyAsync(post.Audience.Where(id => id != except), kind, payload)
            : SendAsync(post.AuthorId, kind, payload);

    // =============================================================================================
    // Incoming
    // =============================================================================================

    private void OnDelivery(string from, string kind, RelayFrame frame)
    {
        var friend = _friends.Friends.FirstOrDefault(f => f.UserId == from && f.State == FriendshipState.Friend);
        if (friend is null) return; // only friends

        switch (kind)
        {
            case MediaShareKinds.Post when Read<MediaPostPayload>(frame) is { } p:
                OnPost(friend, p);
                break;
            case MediaShareKinds.React when Read<MediaReactPayload>(frame) is { } r:
                OnReact(friend, r);
                break;
            case MediaShareKinds.Comment when Read<MediaCommentPayload>(frame) is { } c:
                OnComment(friend, c);
                break;
            case MediaShareKinds.Remove when Read<MediaRemovePayload>(frame) is { } x:
                OnRemove(friend, x);
                break;
        }
    }

    private void OnPost(Friend friend, MediaPostPayload p)
    {
        if (!IsId(p.Id) || !IsWebUrl(p.Url) || _store.GetSharedPost(p.Id) is not null) return; // redelivered
        var now = DateTime.UtcNow;
        var post = new SharedMediaPost
        {
            Id = p.Id,
            AuthorId = friend.UserId,
            AuthorName = friend.DisplayName,
            Url = p.Url.Trim(),
            Title = Cut(p.Title, 200) ?? new Uri(p.Url.Trim()).Host,
            ThumbnailUrl = IsWebUrl(p.ThumbnailUrl) ? p.ThumbnailUrl!.Trim() : null,
            Channel = Cut(p.Channel, 60),
            Note = Cut(p.Note, MaxNoteLength),
            StartSeconds = p.StartSeconds is > 0 and <= MediaTime.MaxSeconds ? p.StartSeconds : null,
            PostedAtUtc = SaneTime(p.PostedAt, now),
            ReceivedUtc = now,
        };
        _store.UpsertSharedPost(post);
        Changed?.Invoke(this, EventArgs.Empty);
        ActivityArrived?.Invoke(this, new SharedMediaActivity(post.Id, $"{friend.DisplayName} shared {post.Title}"));
    }

    private void OnReact(Friend sender, MediaReactPayload r)
    {
        if (!MediaReactions.IsKnown(r.Emoji)) return;
        SharedMediaPost post;
        string userId, name;
        lock (_lock)
        {
            if (_store.GetSharedPost(r.PostId) is not { } found || !Accepts(found, sender, r.UserId)) return;
            post = found;
            (userId, name) = Who(post, sender, r.UserId, r.Name);
            var existing = post.Reactions.FirstOrDefault(x => x.UserId == userId && x.Emoji == r.Emoji);
            if (r.On && existing is null) post.Reactions.Add(new SharedMediaReaction { UserId = userId, Name = name, Emoji = r.Emoji });
            else if (!r.On && existing is not null) post.Reactions.Remove(existing);
            else return; // nothing changed (a repeat)
            _store.UpsertSharedPost(post);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        if (post.AuthorId == MyId)
        {
            _ = PublishAsync(post, MediaShareKinds.React, r with { UserId = userId, Name = name }, sender.UserId);
            if (r.On) ActivityArrived?.Invoke(this, new SharedMediaActivity(post.Id, $"{name} reacted {r.Emoji} to {post.Title}"));
        }
    }

    private void OnComment(Friend sender, MediaCommentPayload c)
    {
        if (!IsId(c.Id) || Cut(c.Text, MaxCommentLength) is not { } text) return;
        SharedMediaPost post;
        SharedMediaComment comment;
        lock (_lock)
        {
            if (_store.GetSharedPost(c.PostId) is not { } found || !Accepts(found, sender, c.UserId)) return;
            post = found;
            if (post.Comments.Any(x => x.Id == c.Id) || post.Comments.Count >= MaxCommentsPerPost) return;
            var (userId, name) = Who(post, sender, c.UserId, c.Name);
            var now = DateTime.UtcNow;
            comment = new SharedMediaComment
            {
                Id = c.Id,
                AuthorId = userId,
                AuthorName = name,
                Text = text,
                PostedAtUtc = SaneTime(c.PostedAt, now),
                ReceivedUtc = now,
            };
            post.Comments.Add(comment);
            post.Comments.Sort((a, b) => a.PostedAtUtc.CompareTo(b.PostedAtUtc));
            _store.UpsertSharedPost(post);
        }
        Changed?.Invoke(this, EventArgs.Empty);

        if (post.AuthorId == MyId)
            _ = PublishAsync(post, MediaShareKinds.Comment, c with { UserId = comment.AuthorId, Name = comment.AuthorName, Text = comment.Text }, sender.UserId);
        // Worth a sound: a comment on your post, or on one you've commented on.
        if (comment.AuthorId != MyId && (post.AuthorId == MyId || post.Comments.Any(x => x.AuthorId == MyId)))
            ActivityArrived?.Invoke(this, new SharedMediaActivity(post.Id, $"{comment.AuthorName} commented on {post.Title}"));
    }

    private void OnRemove(Friend sender, MediaRemovePayload x)
    {
        SharedMediaPost? passOn = null;
        lock (_lock)
        {
            if (_store.GetSharedPost(x.PostId) is not { } post) return;
            if (x.CommentId is null)
            {
                if (post.AuthorId != sender.UserId) return; // only the sharer deletes a post
                _store.DeleteSharedPost(post.Id);
            }
            else
            {
                if (post.Comments.FirstOrDefault(c => c.Id == x.CommentId) is not { } comment) return;
                // From the sharer (their own call, or passed on), or on our own post from the comment's writer.
                var allowed = sender.UserId == post.AuthorId || (post.AuthorId == MyId && comment.AuthorId == sender.UserId);
                if (!allowed) return;
                post.Comments.Remove(comment);
                _store.UpsertSharedPost(post);
                if (post.AuthorId == MyId) passOn = post;
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
        if (passOn is not null) _ = PublishAsync(passOn, MediaShareKinds.Remove, x, sender.UserId);
    }

    /// <summary>
    /// Whether a change from <paramref name="sender"/> about <paramref name="claimedUser"/> counts: the post's sharer
    /// speaks for anyone (they pass changes on); on our own post, a friend it was shared with speaks for themselves.
    /// </summary>
    private bool Accepts(SharedMediaPost post, Friend sender, string? claimedUser) =>
        sender.UserId == post.AuthorId ||
        (post.AuthorId == MyId && claimedUser == sender.UserId && post.Audience.Contains(sender.UserId));

    /// <summary>Who did it, and their name: a friend's name from our own list when we know them.</summary>
    private (string UserId, string Name) Who(SharedMediaPost post, Friend sender, string? claimedUser, string? claimedName)
    {
        var userId = sender.UserId == post.AuthorId && FriendCode.Normalize(claimedUser) is { } relayed ? relayed : sender.UserId;
        if (userId == MyId) return (userId, _friends.Profile.DisplayName);
        var known = _friends.Friends.FirstOrDefault(f => f.UserId == userId && f.State == FriendshipState.Friend);
        return (userId, known?.DisplayName ?? Cut(claimedName, 32) ?? userId);
    }

    // =============================================================================================
    // Sending (paced for the relay; anything that can't go now waits in the outbox)
    // =============================================================================================

    private async Task SendToManyAsync(IEnumerable<string> to, string kind, object payload)
    {
        foreach (var friend in to.Distinct().ToList()) await SendAsync(friend, kind, payload).ConfigureAwait(false);
    }

    private async Task SendAsync(string to, string kind, object payload)
    {
        var json = JsonSerializer.Serialize(payload, payload.GetType(), RelayFrame.Json);
        if (!await TrySendAsync(to, kind, json).ConfigureAwait(false))
            _store.AddOutbox(new SharedMediaOutboxItem { To = to, Kind = kind, PayloadJson = json });
    }

    private async Task<bool> TrySendAsync(string to, string kind, string json)
    {
        if (_friends.ConnectionState != SocialConnectionState.Connected) return false;
        await _sendGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            // Pace: at most SendsPerTenSeconds in any ten seconds.
            while (true)
            {
                var now = DateTime.UtcNow;
                while (_recentSends.Count > 0 && now - _recentSends.Peek() > TimeSpan.FromSeconds(10)) _recentSends.Dequeue();
                if (_recentSends.Count < SendsPerTenSeconds) break;
                var wait = TimeSpan.FromSeconds(10) - (now - _recentSends.Peek()) + TimeSpan.FromMilliseconds(50);
                await Task.Delay(wait, _lifetime.Token).ConfigureAwait(false);
            }
            _recentSends.Enqueue(DateTime.UtcNow);

            using var doc = JsonDocument.Parse(json);
            return await _transport.SendToAsync(to, kind, doc.RootElement.Clone()).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException)
        {
            _logger.LogWarning(ex, "Couldn't send a shared feed message");
            return false;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task FlushOutboxAsync()
    {
        if (Interlocked.Exchange(ref _flushing, 1) == 1) return;
        try
        {
            foreach (var item in _store.GetOutbox())
            {
                if (DateTime.UtcNow - item.CreatedUtc > TimeSpan.FromDays(14) ||
                    !_friends.Friends.Any(f => f.UserId == item.To && f.State == FriendshipState.Friend))
                {
                    _store.DeleteOutbox(item.Id); // too old, or no longer a friend
                    continue;
                }
                if (!await TrySendAsync(item.To, item.Kind, item.PayloadJson).ConfigureAwait(false)) break; // offline again
                _store.DeleteOutbox(item.Id);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sending the shared feed's waiting messages failed");
        }
        finally
        {
            Interlocked.Exchange(ref _flushing, 0);
        }
    }

    // =============================================================================================
    // Helpers
    // =============================================================================================

    private static T? Read<T>(RelayFrame frame) where T : class
    {
        if (frame.Payload is not { } payload) return null;
        try { return payload.Deserialize<T>(RelayFrame.Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>Their clock may be off: never in the future, never older than a month.</summary>
    private static DateTime SaneTime(DateTime claimed, DateTime now)
    {
        var utc = claimed.Kind == DateTimeKind.Local ? claimed.ToUniversalTime() : DateTime.SpecifyKind(claimed, DateTimeKind.Utc);
        return utc > now || utc < now.AddDays(-30) ? now : utc;
    }

    private static bool IsId(string? id) => id is { Length: >= 8 and <= 64 } && id.All(char.IsAsciiLetterOrDigit);

    private static bool IsWebUrl(string? url) =>
        url is { Length: <= 2048 } && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    /// <summary>Trimmed, no control characters except line breaks, at most <paramref name="max"/> characters; null when blank.</summary>
    private static string? Cut(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var clean = new string(text.Where(c => c == '\n' || !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length == 0) return null;
        return clean.Length > max ? clean[..max] : clean;
    }

    /// <summary>Stops paced sends (the token source stays undisposed: a send may still be finishing).</summary>
    public void Dispose() => _lifetime.Cancel();
}
