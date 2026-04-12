using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ScreenSharing.Server.Auth;
using ScreenSharing.Server.Config;
using ScreenSharing.Server.Rooms;
using ScreenSharing.Server.Signaling;

namespace ScreenSharing.Server;

/// <summary>
/// Reusable server setup extracted from <c>Program.cs</c>. Lets integration tests
/// spin up a real Kestrel host with the same DI and endpoint wiring the production
/// entry point uses, without duplicating code.
/// </summary>
public static class ServerHost
{
    public static void ConfigureServices(IServiceCollection services, IConfiguration? configuration = null)
    {
        if (configuration is not null)
        {
            services.Configure<RoomServerOptions>(configuration.GetSection("Rooms"));
        }
        else
        {
            services.Configure<RoomServerOptions>(_ => { });
        }
        services.AddSingleton<PasswordHasher>();
        services.AddSingleton<RoomManager>();
        services.AddSingleton<SessionRegistry>();
    }

    public static void ConfigureEndpoints(WebApplication app)
    {
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
    }
}
