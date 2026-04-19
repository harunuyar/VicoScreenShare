namespace VicoScreenShare.Server;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VicoScreenShare.Protocol;
using VicoScreenShare.Server.Config;
using VicoScreenShare.Server.Rooms;
using VicoScreenShare.Server.Signaling;

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
        services.AddSingleton<RoomManager>();
        services.AddSingleton<SessionRegistry>();
    }

    public static void ConfigureEndpoints(WebApplication app)
    {
        app.UseWebSockets();

        // /healthz is the client's pre-connect probe: it reports liveness plus
        // whether the server requires a shared password, so the client's
        // connection picker can render "Password required" before the user
        // tries to join. Returns protocol version too, so a future client
        // mismatch shows up here rather than only at ClientHello time.
        app.MapGet("/healthz", (IOptionsMonitor<RoomServerOptions> options) =>
        {
            var pw = options.CurrentValue.AccessPassword;
            return Results.Ok(new
            {
                status = "ok",
                requiresAuth = !string.IsNullOrEmpty(pw),
                protocolVersion = ProtocolVersion.Current,
            });
        });

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
