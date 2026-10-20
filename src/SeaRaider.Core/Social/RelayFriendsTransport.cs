using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SeaRaider.Core.Social;

/// <summary>How friends' messages travel. The relay transport is the built-in one; others (LAN, P2P) can be added.</summary>
public interface IFriendsTransport : IAsyncDisposable
{
    /// <summary>Raised (on a background thread) for every frame the relay sends.</summary>
    event EventHandler<RelayFrame>? FrameReceived;

    /// <summary>Raised when the connection state changes; the string is a user-facing reason when it dropped.</summary>
    event EventHandler<(SocialConnectionState State, string? Error)>? StateChanged;

    SocialConnectionState State { get; }

    /// <summary>Connects (and keeps reconnecting) to <paramref name="relay"/> as <paramref name="profile"/>.</summary>
    void Start(Uri relay, SocialProfile profile);

    /// <summary>Disconnects and stops reconnecting.</summary>
    Task StopAsync();

    /// <summary>Sends a frame; false when not connected (callers keep what needs keeping).</summary>
    Task<bool> SendAsync(RelayFrame frame, CancellationToken cancellationToken = default);

    /// <summary>Wraps a payload in a "send" frame to a friend.</summary>
    Task<bool> SendToAsync(string to, string kind, object payload, string? id = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// WebSocket client for <c>SeaRaider.Relay</c> (wss:// recommended; ws:// for a LAN relay).
/// Reconnects with exponential backoff (2 s → 60 s), pings every 30 s, and caps frame size. Frames are small
/// JSON texts (<see cref="RelayFrame"/>).
/// </summary>
public sealed class RelayFriendsTransport(ILogger<RelayFriendsTransport> logger) : IFriendsTransport
{
    private const int MaxFrameBytes = 64 * 1024;
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private ClientWebSocket? _socket;

    public event EventHandler<RelayFrame>? FrameReceived;
    public event EventHandler<(SocialConnectionState State, string? Error)>? StateChanged;

    public SocialConnectionState State { get; private set; } = SocialConnectionState.NotConfigured;

    public void Start(Uri relay, SocialProfile profile)
    {
        if (_loop is { IsCompleted: false }) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() => RunAsync(relay, profile, token), CancellationToken.None);
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            if (_loop is not null) await _loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or WebSocketException)
        {
            // stopping anyway
        }
        _cts.Dispose();
        _cts = null;
        _loop = null;
        SetState(SocialConnectionState.NotConfigured, null);
    }

    public async Task<bool> SendAsync(RelayFrame frame, CancellationToken cancellationToken = default)
    {
        var socket = _socket;
        if (socket is not { State: WebSocketState.Open }) return false;

        var bytes = Encoding.UTF8.GetBytes(frame.Serialize());
        if (bytes.Length > MaxFrameBytes) throw new InvalidOperationException("That message is too long.");

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            logger.LogDebug(ex, "Relay send failed");
            return false;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public Task<bool> SendToAsync(string to, string kind, object payload, string? id = null, CancellationToken cancellationToken = default) =>
        SendAsync(new RelayFrame
        {
            Type = "send",
            To = to,
            Kind = kind,
            Id = id ?? Guid.NewGuid().ToString("N"),
            Payload = JsonSerializer.SerializeToElement(payload, payload.GetType(), RelayFrame.Json),
        }, cancellationToken);

    // =============================================================================================
    // Connection loop
    // =============================================================================================

    private async Task RunAsync(Uri relay, SocialProfile profile, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            SetState(SocialConnectionState.Connecting, null);
            string? error = null;
            try
            {
                using var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = PingInterval;
                await socket.ConnectAsync(relay, ct).ConfigureAwait(false);
                _socket = socket;

                await SendAsync(new RelayFrame
                {
                    Type = "hello",
                    UserId = profile.UserId,
                    Secret = profile.Secret,
                    Name = profile.DisplayName,
                    Invisible = profile.PresenceMode == PresenceMode.Invisible ? true : null,
                }, ct).ConfigureAwait(false);

                delay = TimeSpan.FromSeconds(2); // connected: reset the backoff
                using var pinger = StartPinger(ct);
                error = await ReceiveLoopAsync(socket, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException or InvalidOperationException)
            {
                error = $"Can't reach the friends relay at {relay.Host}: {ex.Message}";
                logger.LogInformation(ex, "Relay connection failed");
            }
            finally
            {
                _socket = null;
            }

            SetState(SocialConnectionState.Disconnected, error ?? "Disconnected from the friends relay. Reconnecting…");
            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
        }
    }

    /// <summary>Reads frames until the socket closes. Returns a user-facing reason when the relay refused us.</summary>
    private async Task<string?> ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return result.CloseStatusDescription;
                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxFrameBytes) return "The relay sent an oversized message.";
            } while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text) continue;
            var frame = RelayFrame.Deserialize(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
            if (frame is null) continue;

            switch (frame.Type)
            {
                case "welcome":
                    SetState(SocialConnectionState.Connected, null);
                    break;
                case "error" when frame.Code == "auth":
                    return frame.Message ?? "The relay refused this friend code.";
                case "pong":
                    continue;
            }

            try
            {
                FrameReceived?.Invoke(this, frame);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "A friends frame handler threw");
            }
        }
        return null;
    }

    private IDisposable StartPinger(CancellationToken ct) =>
        new Timer(_ => _ = SendAsync(new RelayFrame { Type = "ping" }, ct), null, PingInterval, PingInterval);

    private void SetState(SocialConnectionState state, string? error)
    {
        if (State == state && error is null) return;
        State = state;
        StateChanged?.Invoke(this, (state, error));
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _sendGate.Dispose();
    }
}
