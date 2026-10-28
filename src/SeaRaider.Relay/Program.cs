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

// "Add me on SeaRaider" (the button on someone's Discord status): a small page that opens SeaRaider on this PC with a
// searaider://add/<code> link, where SeaRaider asks whether to send them a friend request. Discord buttons can only open
// web pages, so this is the step between.
app.MapGet("/add/{code}", (string code, string? name) =>
{
    var friendCode = code.Trim().ToUpperInvariant();
    if (!System.Text.RegularExpressions.Regex.IsMatch(friendCode, "^SR-[0-9A-HJKMNP-TV-Z]{5}-[0-9A-HJKMNP-TV-Z]{5}$"))
        return Results.NotFound("That isn't a SeaRaider friend code.");
    var cleanName = string.IsNullOrWhiteSpace(name) ? null : string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    if (cleanName is { Length: > 32 }) cleanName = cleanName[..32];
    var shown = System.Net.WebUtility.HtmlEncode(cleanName ?? friendCode);
    var link = $"searaider://add/{friendCode}" + (cleanName is null ? "" : "?name=" + Uri.EscapeDataString(cleanName));
    var html = $$"""
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Add {{shown}} on SeaRaider</title>
        <style>
          body { margin: 0; min-height: 100vh; display: grid; place-items: center; background: #04090f; color: #f2f7fa;
                 font-family: "Segoe UI", system-ui, sans-serif; }
          .card { max-width: 420px; padding: 32px; border-radius: 16px; background: #0c1a23; border: 1px solid #1a3142; text-align: center; }
          h1 { font-size: 22px; margin: 0 0 8px; }
          p { color: #9ab0bc; line-height: 1.5; }
          a.button { display: inline-block; margin-top: 12px; padding: 12px 22px; border-radius: 10px; background: #3be3e0; color: #03141a;
                     font-weight: 700; text-decoration: none; }
          code { color: #8af4f1; }
        </style></head>
        <body><div class="card">
          <h1>Add {{shown}} on SeaRaider</h1>
          <p>SeaRaider will open and ask if you want to send them a friend request.</p>
          <a class="button" href="{{link}}">Open SeaRaider</a>
          <p>Nothing happened? Make sure SeaRaider is installed and has been opened once, or add their friend code by hand in
             SeaRaider › Friends: <code>{{friendCode}}</code></p>
        </div>
        <script>setTimeout(function () { location.href = "{{link}}"; }, 300);</script>
        </body></html>
        """;
    return Results.Content(html, "text/html; charset=utf-8");
});

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
