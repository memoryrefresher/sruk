using SeaRaider.Relay;

// SeaRaider.Relay — friends, presence and chat for SeaRaider.
//
//   dotnet run --project src/SeaRaider.Relay            (listens on http://0.0.0.0:5280, WebSocket at /ws)
//
// Put it behind a TLS reverse proxy (Caddy, nginx, Cloudflare Tunnel) for wss://, or use ws://<lan-ip>:5280/ws on a home
// network. Every SeaRaider that should see each other enters the same address in Settings › Friends.
var builder = WebApplication.CreateBuilder(args);

// Hosts like Render, Railway and Fly tell the app which port to listen on in PORT (Render: 10000). Without it, the
// "Urls" setting in appsettings.json applies (port 5280).
if (Environment.GetEnvironmentVariable("PORT") is { Length: > 0 } port) builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
builder.Services.Configure<RelayOptions>(builder.Configuration.GetSection("Relay"));
builder.Services.AddSingleton<UserRegistry>();
builder.Services.AddSingleton<RelayHub>();

var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

app.MapGet("/", () => Results.Text("SeaRaider relay. Connect SeaRaider to ws(s)://<this host>/ws"));
app.MapGet("/health", (RelayHub hub) => Results.Ok(new { status = "ok", online = hub.OnlineCount }));

app.Map("/ws", async (HttpContext context, RelayHub hub) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("WebSocket connections only.");
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await hub.HandleAsync(socket, context.Connection.RemoteIpAddress?.ToString() ?? "?", context.RequestAborted);
});

app.Run();
