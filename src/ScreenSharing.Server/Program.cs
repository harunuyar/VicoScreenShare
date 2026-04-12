using System.Net.WebSockets;
using Microsoft.Extensions.DependencyInjection;
using ScreenSharing.Server.Auth;
using ScreenSharing.Server.Config;
using ScreenSharing.Server.Rooms;
using ScreenSharing.Server.Signaling;

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

builder.Services.Configure<RoomServerOptions>(
    builder.Configuration.GetSection("Rooms"));
builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<RoomManager>();
builder.Services.AddSingleton<SessionRegistry>();

var app = builder.Build();

app.UseWebSockets();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var session = ActivatorUtilities.CreateInstance<WsSession>(context.RequestServices, socket);
    await session.RunAsync(context.RequestAborted);
});

app.Run();

// Exposed so WebApplicationFactory<Program> can target this entry point in tests.
public partial class Program { }
