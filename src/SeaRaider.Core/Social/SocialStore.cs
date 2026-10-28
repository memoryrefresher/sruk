using LiteDB;

namespace SeaRaider.Core.Social;

/// <summary>
/// Local storage for the friends system: your profile (friend code and its secret), friends and requests, and chat
/// history. Kept in its own LiteDB file (<c>social.db</c>) next to the library database.
/// </summary>
public sealed class SocialStore : IDisposable
{
    private const int MaxMessagesPerFriend = 1_000;
    private const int MaxSharedPosts = 400;
    private const int MaxOutbox = 2_000;

    private readonly LiteDatabase _db;
    private readonly ILiteCollection<SocialProfile> _profile;
    private readonly ILiteCollection<Friend> _friends;
    private readonly ILiteCollection<ChatMessage> _messages;
    private readonly ILiteCollection<SharedMediaPost> _posts;
    private readonly ILiteCollection<SharedMediaOutboxItem> _outbox;
    private readonly object _lock = new();

    public SocialStore() : this(Path.Combine(AppPaths.DataDirectory, "social.db"))
    {
    }

    public SocialStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _db = new LiteDatabase(new ConnectionString { Filename = path, Connection = ConnectionType.Direct });
        _profile = _db.GetCollection<SocialProfile>("profile");
        _friends = _db.GetCollection<Friend>("friends");
        _messages = _db.GetCollection<ChatMessage>("messages");
        _messages.EnsureIndex(m => m.FriendId);
        _messages.EnsureIndex(m => m.SentAtUtc);
        _posts = _db.GetCollection<SharedMediaPost>("media_posts");
        _posts.EnsureIndex(p => p.PostedAtUtc);
        _outbox = _db.GetCollection<SharedMediaOutboxItem>("media_outbox");
    }

    public SocialProfile GetOrCreateProfile()
    {
        lock (_lock)
        {
            var profile = _profile.FindById(1);
            if (profile is not null) return profile;
            profile = SocialProfile.CreateNew();
            _profile.Upsert(profile);
            return profile;
        }
    }

    public void SaveProfile(SocialProfile profile)
    {
        lock (_lock) _profile.Upsert(profile);
    }

    public List<Friend> GetFriends()
    {
        lock (_lock) return _friends.FindAll().ToList();
    }

    public void UpsertFriend(Friend friend)
    {
        lock (_lock) _friends.Upsert(friend);
    }

    public void DeleteFriend(string userId)
    {
        lock (_lock) _friends.Delete(userId);
    }

    public void AddMessage(ChatMessage message)
    {
        lock (_lock)
        {
            _messages.Upsert(message);
            var count = _messages.Count(m => m.FriendId == message.FriendId);
            if (count <= MaxMessagesPerFriend) return;

            var oldest = _messages.Query()
                .Where(m => m.FriendId == message.FriendId)
                .OrderBy(m => m.SentAtUtc)
                .Limit(count - MaxMessagesPerFriend)
                .ToList();
            foreach (var old in oldest) _messages.Delete(old.Id);
        }
    }

    public void UpdateMessage(ChatMessage message)
    {
        lock (_lock) _messages.Update(message);
    }

    public ChatMessage? GetMessage(string id)
    {
        lock (_lock) return _messages.FindById(id);
    }

    /// <summary>Newest <paramref name="max"/> messages with a friend, oldest first.</summary>
    public List<ChatMessage> GetConversation(string friendId, int max = 200)
    {
        lock (_lock)
        {
            return _messages.Query()
                .Where(m => m.FriendId == friendId)
                .OrderByDescending(m => m.SentAtUtc)
                .Limit(max)
                .ToList()
                .OrderBy(m => m.SentAtUtc)
                .ToList();
        }
    }

    public void DeleteConversation(string friendId)
    {
        lock (_lock) _messages.DeleteMany(m => m.FriendId == friendId);
    }

    // ---- Media tab: shared feed ------------------------------------------------------------------------

    /// <summary>Every shared post, newest first.</summary>
    public List<SharedMediaPost> GetSharedPosts()
    {
        lock (_lock) return _posts.Query().OrderByDescending(p => p.PostedAtUtc).ToList();
    }

    public SharedMediaPost? GetSharedPost(string id)
    {
        lock (_lock) return _posts.FindById(id);
    }

    /// <summary>Adds or replaces a post; the oldest are dropped past <see cref="MaxSharedPosts"/>.</summary>
    public void UpsertSharedPost(SharedMediaPost post)
    {
        lock (_lock)
        {
            _posts.Upsert(post);
            var count = _posts.Count();
            if (count <= MaxSharedPosts) return;
            foreach (var old in _posts.Query().OrderBy(p => p.PostedAtUtc).Limit(count - MaxSharedPosts).ToList())
                _posts.Delete(old.Id);
        }
    }

    public void DeleteSharedPost(string id)
    {
        lock (_lock) _posts.Delete(id);
    }

    public void DeleteSharedPostsBy(string authorId)
    {
        lock (_lock) _posts.DeleteMany(p => p.AuthorId == authorId);
    }

    public void AddOutbox(SharedMediaOutboxItem item)
    {
        lock (_lock)
        {
            _outbox.Upsert(item);
            var count = _outbox.Count();
            if (count <= MaxOutbox) return;
            foreach (var old in _outbox.Query().OrderBy(o => o.CreatedUtc).Limit(count - MaxOutbox).ToList())
                _outbox.Delete(old.Id);
        }
    }

    /// <summary>Waiting messages, oldest first.</summary>
    public List<SharedMediaOutboxItem> GetOutbox()
    {
        lock (_lock) return _outbox.Query().OrderBy(o => o.CreatedUtc).ToList();
    }

    public void DeleteOutbox(string id)
    {
        lock (_lock) _outbox.Delete(id);
    }

    public void Dispose() => _db.Dispose();
}
