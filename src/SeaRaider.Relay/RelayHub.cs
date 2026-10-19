using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace SeaRaider.Relay;

public sealed class RelayOptions
{
    /// <summary>Where friend codes and their secret hashes are kept.</summary>
    public string UsersFile { get; set; } = "relay-users.json";

    /// <summary>Messages kept for someone offline (oldest dropped first).</summary>
    public int MaxQueuedPerUser { get; set; } = 200;

    /// <summary>Queued messages older than this are dropped.</summary>
    public int QueueDays { get; set; } = 7;

    /// <summary>Per-connection rate limit.</summary>
    public int MessagesPerTenSeconds { get; set; } = 60;
}

/// <summary>Wire frame; see SeaRaider.Core.Social.RelayFrame for the protocol (kept field-compatible).</summary>
public sealed class Frame
{
    public string Type { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string? Secret { get; set; }
    public string? Name { get; set; }
    public List<string>? Users { get; set; }
    public List<string>? Online { get; set; }
    public string? User { get; set; }
    public string? To { get; set; }
    public string? From { get; set; }
    public string? Kind { get; set; }
    public string? Id { get; set; }
    public JsonElement? Payload { get; set; }
    public DateTime? At { get; set; }
    public bool? Queued { get; set; }
    public bool? Invisible { get; set; }
    public string? Code { get; set; }
    public string? Message { get; set; }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>
/// Routes frames between connected SeaRaider clients.
/// <list type="bullet">
///   <item>One connection per friend code; signing in elsewhere closes the older one.</item>
///   <item>"online"/"offline" are only announced between <b>mutual</b> subscribers (both have each other in their
///         friend list), and never for someone who chose to appear offline.</item>
///   <item>Messages to someone offline are queued in memory (limits in <see cref="RelayOptions"/>), except presence,
///         which is only meaningful live.</item>
///   <item>The relay doesn't read message payloads.</item>
/// </list>
/// </summary>
public sealed partial class RelayHub(UserRegistry users, IOptions<RelayOptions> options, ILogger<RelayHub> logger)
{
    private const int MaxFrameBytes = 64 * 1024;
    private static readonly HashSet<string> EphemeralKinds = new(StringComparer.Ordinal) { "presence" };

    private readonly ConcurrentDictionary<string, Connection> _online = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<Frame>> _queues = new(StringComparer.Ordinal);
    private readonly RelayOptions _options = options.Value;

    [GeneratedRegex(@"^SR-[0-9A-HJKMNP-TV-Z]{5}-[0-9A-HJKMNP-TV-Z]{5}$")]
    private static partial Regex FriendCodeRegex();

    public int OnlineCount => _online.Count;

    public async Task HandleAsync(WebSocket socket, string remote, CancellationToken aborted)
    {
        // 1. hello
        using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        helloTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        Frame? hello;
        try
        {
            hello = await ReceiveAsync(socket, helloTimeout.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (hello is not { Type: "hello", UserId: { } userId, Secret: { Length: >= 20 and <= 200 } secret } || !FriendCodeRegex().IsMatch(userId))
        {
            await RefuseAsync(socket, "bad-request", "Say hello with a friend code and secret first.");
            return;
        }
        if (!users.VerifyOrRegister(userId, secret))
        {
            logger.LogWarning("Refused {User} from {Remote}: wrong secret", userId, remote);
            await RefuseAsync(socket, "auth", "This friend code belongs to another SeaRaider installation.");
            return;
        }

        var connection = new Connection(userId, socket, _options.MessagesPerTenSeconds) { Invisible = hello.Invisible == true };
        if (_online.TryGetValue(userId, out var previous)) await previous.CloseAsync("Signed in from another place.");
        _online[userId] = connection;
        logger.LogInformation("{User} connected from {Remote} ({Online} online)", userId, remote, _online.Count);

        try
        {
            await connection.SendAsync(new Frame { Type = "welcome", UserId = userId }, aborted);
            await FlushQueueAsync(connection, aborted);

            // 2. frames until the socket closes
            while (socket.State == WebSocketState.Open && !aborted.IsCancellationRequested)
            {
                var frame = await ReceiveAsync(socket, aborted);
                if (frame is null) break;
                await HandleFrameAsync(connection, frame, aborted);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
        {
            // dropped
        }
        finally
        {
            // Only the current connection counts: one replaced by a newer sign-in leaves quietly.
            if (_online.TryRemove(new KeyValuePair<string, Connection>(userId, connection)))
            {
                logger.LogInformation("{User} disconnected ({Online} online)", userId, _online.Count);
                if (!connection.Invisible) await AnnounceAsync(connection, "offline", CancellationToken.None);
            }
        }
    }

    private async Task HandleFrameAsync(Connection connection, Frame frame, CancellationToken ct)
    {
        switch (frame.Type)
        {
            case "ping":
                await connection.SendAsync(new Frame { Type = "pong" }, ct);
                return;

            case "subscribe":
                connection.Subscriptions = (frame.Users ?? new List<string>())
                    .Where(u => FriendCodeRegex().IsMatch(u))
                    .Take(1_000)
                    .ToHashSet(StringComparer.Ordinal);
                // Tell this client who's around, and them that it's here (the friendship may be new).
                foreach (var other in MutualOnline(connection))
                {
                    if (!other.Invisible) await connection.SendAsync(new Frame { Type = "online", User = other.UserId }, ct);
                    if (!connection.Invisible) await other.SendAsync(new Frame { Type = "online", User = connection.UserId }, ct);
                }
                return;

            case "visibility":
                var invisible = frame.Invisible == true;
                if (invisible == connection.Invisible) return;
                connection.Invisible = invisible;
                await AnnounceAsync(connection, invisible ? "offline" : "online", ct);
                return;

            case "send":
                await RouteAsync(connection, frame, ct);
                return;

            default:
                await connection.SendAsync(new Frame { Type = "error", Code = "bad-request", Message = $"Unknown frame \"{frame.Type}\"." }, ct);
                return;
        }
    }

    private async Task RouteAsync(Connection sender, Frame frame, CancellationToken ct)
    {
        if (frame.To is not { } to || !FriendCodeRegex().IsMatch(to) || frame.Kind is not { Length: > 0 and <= 32 } kind ||
            frame.Id is not { Length: > 0 and <= 64 } id)
        {
            await sender.SendAsync(new Frame { Type = "error", Code = "bad-request", Message = "A message needs to, kind and id." }, ct);
            return;
        }
        if (!sender.TryTakeToken())
        {
            await sender.SendAsync(new Frame { Type = "error", Code = "rate", Message = "Slow down: too many messages." }, ct);
            return;
        }

        var delivery = new Frame { Type = "deliver", From = sender.UserId, Kind = kind, Id = id, Payload = frame.Payload, At = DateTime.UtcNow };
        if (_online.TryGetValue(to, out var recipient) && await recipient.SendAsync(delivery, ct))
        {
            await sender.SendAsync(new Frame { Type = "sent", Id = id, Queued = false }, ct);
            return;
        }

        if (EphemeralKinds.Contains(kind)) return; // presence is only worth anything live

        var queue = _queues.GetOrAdd(to, _ => new ConcurrentQueue<Frame>());
        queue.Enqueue(delivery);
        while (queue.Count > _options.MaxQueuedPerUser && queue.TryDequeue(out _)) { }
        await sender.SendAsync(new Frame { Type = "sent", Id = id, Queued = true }, ct);
    }

    private async Task FlushQueueAsync(Connection connection, CancellationToken ct)
    {
        if (!_queues.TryRemove(connection.UserId, out var queue)) return;
        var oldest = DateTime.UtcNow.AddDays(-_options.QueueDays);
        while (queue.TryDequeue(out var frame))
        {
            if (frame.At < oldest) continue;
            await connection.SendAsync(frame, ct);
        }
    }

    /// <summary>Online connections that have <paramref name="connection"/> as a friend, and vice versa.</summary>
    private IEnumerable<Connection> MutualOnline(Connection connection) =>
        connection.Subscriptions
            .Select(id => _online.TryGetValue(id, out var other) ? other : null)
            .OfType<Connection>()
            .Where(other => other.Subscriptions.Contains(connection.UserId))
            .ToList();

    private async Task AnnounceAsync(Connection connection, string type, CancellationToken ct)
    {
        foreach (var other in MutualOnline(connection))
            await other.SendAsync(new Frame { Type = type, User = connection.UserId }, ct);
    }

    private static async Task<Frame?> ReceiveAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxFrameBytes)
            {
                await RefuseAsync(socket, "too-big", "Frames are limited to 64 KB.");
                return null;
            }
            if (!result.EndOfMessage) continue;
            if (result.MessageType != WebSocketMessageType.Text) { message.SetLength(0); continue; }

            try
            {
                return JsonSerializer.Deserialize<Frame>(message.ToArray(), Frame.Json) ?? new Frame { Type = "invalid" };
            }
            catch (JsonException)
            {
                return new Frame { Type = "invalid" };
            }
        }
    }

    private static async Task RefuseAsync(WebSocket socket, string code, string message)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Frame { Type = "error", Code = code, Message = message }, Frame.Json);
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, message, CancellationToken.None);
        }
        catch (Exception ex) when (ex is WebSocketException or InvalidOperationException)
        {
            // already gone
        }
    }

    /// <summary>One signed-in client.</summary>
    private sealed class Connection(string userId, WebSocket socket, int messagesPerTenSeconds)
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly Queue<DateTime> _recent = new();

        public string UserId { get; } = userId;
        public HashSet<string> Subscriptions { get; set; } = new(StringComparer.Ordinal);
        public bool Invisible { get; set; }

        public async Task<bool> SendAsync(Frame frame, CancellationToken ct)
        {
            if (socket.State != WebSocketState.Open) return false;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, Frame.Json);
            await _sendLock.WaitAsync(ct);
            try
            {
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
                return true;
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
            {
                return false;
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public bool TryTakeToken()
        {
            lock (_recent)
            {
                var now = DateTime.UtcNow;
                while (_recent.Count > 0 && now - _recent.Peek() > TimeSpan.FromSeconds(10)) _recent.Dequeue();
                if (_recent.Count >= messagesPerTenSeconds) return false;
                _recent.Enqueue(now);
                return true;
            }
        }

        public async Task CloseAsync(string reason)
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None);
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
            {
                // already closed
            }
        }
    }
}

/// <summary>
/// Friend codes and the SHA-256 of their secrets, saved to <see cref="RelayOptions.UsersFile"/>. The first connection
/// with a code claims it; later connections must present the same secret.
/// </summary>
public sealed class UserRegistry
{
    private readonly string _path;
    private readonly Dictionary<string, string> _hashes;
    private readonly object _lock = new();
    private readonly ILogger<UserRegistry> _logger;

    public UserRegistry(IOptions<RelayOptions> options, ILogger<UserRegistry> logger)
    {
        _logger = logger;
        _path = Path.GetFullPath(options.Value.UsersFile);
        _hashes = File.Exists(_path)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? new()
            : new();
        _logger.LogInformation("{Count} registered friend codes in {Path}", _hashes.Count, _path);
    }

    public bool VerifyOrRegister(string userId, string secret)
    {
        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
        lock (_lock)
        {
            if (_hashes.TryGetValue(userId, out var known))
                return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(known), Encoding.ASCII.GetBytes(hash));

            _hashes[userId] = hash;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_hashes));
            File.Move(temp, _path, overwrite: true);
            return true;
        }
    }
}
