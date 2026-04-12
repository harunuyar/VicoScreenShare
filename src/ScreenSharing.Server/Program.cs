using System.Net.WebSockets;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.TimestampFormat = "HH:mm:ss.fff ";
    options.SingleLine = true;
});

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(5000);
});

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
        "Phase 0 placeholder — signaling lands in Phase 1",
        context.RequestAborted);
});

app.Logger.LogInformation("ScreenSharing server listening on http://localhost:5000");
app.Run();
