using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using VicoScreenShare.Client.Services;
using VicoScreenShare.Server.Config;

namespace VicoScreenShare.Tests.Client;

/// <summary>
/// Exercises <see cref="ServerStatusProbe"/> against a real in-process server,
/// covering the matrix of (open / protected) × (saved-password / no-password)
/// plus the offline and scheme-mapping paths. Uses WebApplicationFactory so
/// the /healthz JSON shape matches production — the probe's response parsing
/// is the bit we care most about.
/// </summary>
public sealed class ServerStatusProbeTests
{
    private static WebApplicationFactory<Program> CreateFactory(string? accessPassword) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureServices(services => services.Configure<RoomServerOptions>(o =>
            {
                o.AccessPassword = accessPassword;
            })));

    [Fact]
    public async Task Open_server_probes_as_Online()
    {
        await using var factory = CreateFactory(null);
        var probe = new ServerStatusProbe(factory.CreateClient(), TimeSpan.FromSeconds(3));

        var result = await probe.ProbeAsync(new Uri("ws://any-host/ws"), hasSavedPassword: false);

        result.Status.Should().Be(ServerStatus.Online);
        result.ProtocolVersion.Should().NotBeNull();
    }

    [Fact]
    public async Task Protected_server_without_saved_password_probes_as_AuthRequired()
    {
        await using var factory = CreateFactory("secret");
        var probe = new ServerStatusProbe(factory.CreateClient(), TimeSpan.FromSeconds(3));

        var result = await probe.ProbeAsync(new Uri("ws://any-host/ws"), hasSavedPassword: false);

        result.Status.Should().Be(ServerStatus.AuthRequired);
    }

    [Fact]
    public async Task Protected_server_with_saved_password_probes_as_Online()
    {
        // We don't actually validate the password at probe time — the user
        // has saved SOMETHING, so surface Online optimistically and let the
        // live connect verify correctness.
        await using var factory = CreateFactory("secret");
        var probe = new ServerStatusProbe(factory.CreateClient(), TimeSpan.FromSeconds(3));

        var result = await probe.ProbeAsync(new Uri("ws://any-host/ws"), hasSavedPassword: true);

        result.Status.Should().Be(ServerStatus.Online);
    }

    [Fact]
    public async Task Unreachable_server_probes_as_Offline()
    {
        // Port 1 is unassigned; any connect attempt times out / refuses fast.
        var probe = new ServerStatusProbe(new HttpClient(), TimeSpan.FromMilliseconds(400));

        var result = await probe.ProbeAsync(new Uri("ws://127.0.0.1:1/ws"), hasSavedPassword: false);

        result.Status.Should().Be(ServerStatus.Offline);
    }

    [Fact]
    public void TranslateToHealthUri_maps_ws_to_http_and_preserves_custom_port()
    {
        var health = ServerStatusProbe.TranslateToHealthUri(new Uri("ws://example.com:5000/ws"));
        health.Should().NotBeNull();
        health!.Scheme.Should().Be("http");
        health.Host.Should().Be("example.com");
        health.Port.Should().Be(5000);
        health.AbsolutePath.Should().Be("/healthz");
    }

    [Fact]
    public void TranslateToHealthUri_maps_wss_to_https_and_uses_default_port()
    {
        var health = ServerStatusProbe.TranslateToHealthUri(new Uri("wss://example.com/ws"));
        health.Should().NotBeNull();
        health!.Scheme.Should().Be("https");
        health.Port.Should().Be(443);
    }

    [Fact]
    public void TranslateToHealthUri_rejects_non_websocket_scheme()
    {
        ServerStatusProbe.TranslateToHealthUri(new Uri("http://example.com/ws")).Should().BeNull();
    }
}
