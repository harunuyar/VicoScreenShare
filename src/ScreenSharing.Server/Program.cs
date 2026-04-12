using System.Net.WebSockets;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.TimestampFormat = "HH:mm:ss.fff ";
    options.SingleLine = true;
});

// Kestrel binds via standard configuration: ASPNETCORE_URLS env var,
// appsettings Kestrel:Endpoints, or the --urls CLI arg. Default when nothing is set
// is all interfaces on port 5000 so a VPS deploy works out of the box.
// launchSettings.json pins Development to http://localhost:5000.
if (string.IsNullOrEmpty(builder.Configuration["urls"]) &&
    string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    builder.WebHost.UseUrls("http://0.0.0.0:5000");
}

var app = builder.Build();

app.UseWebSockets();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// Phase 0 placeholder: accept the upgrade but close immediately.
// The real handler lands in Phase 1.
app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await socket.CloseAsync(
        WebSocketCloseStatus.NormalClosure,
        "Phase 0 placeholder -- signaling lands in Phase 1",
        context.RequestAborted);
});

app.Run();
