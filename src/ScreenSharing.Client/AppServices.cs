using ScreenSharing.Client.Services;

namespace ScreenSharing.Client;

/// <summary>
/// Simple service locator for Phase 1. Holds the single instances of long-lived
/// services the view models depend on. Bootstrapped once from App.axaml.cs.
/// Will migrate to Microsoft.Extensions.DependencyInjection if the graph gets
/// larger, but a static is plenty for Home + Room.
/// </summary>
public static class AppServices
{
    private static IdentityStore? _identityStore;
    private static SignalingClient? _signalingClient;
    private static NavigationService? _navigation;
    private static ClientSettings? _settings;

    public static IdentityStore Identity =>
        _identityStore ?? throw new InvalidOperationException("AppServices not initialized.");

    public static SignalingClient Signaling =>
        _signalingClient ?? throw new InvalidOperationException("AppServices not initialized.");

    public static NavigationService Navigation =>
        _navigation ?? throw new InvalidOperationException("AppServices not initialized.");

    public static ClientSettings Settings =>
        _settings ?? throw new InvalidOperationException("AppServices not initialized.");

    public static void Initialize(
        IdentityStore identityStore,
        SignalingClient signalingClient,
        NavigationService navigation,
        ClientSettings settings)
    {
        _identityStore = identityStore;
        _signalingClient = signalingClient;
        _navigation = navigation;
        _settings = settings;
    }
}

public sealed class ClientSettings
{
    /// <summary>
    /// Signaling server WebSocket endpoint. Defaults to localhost:5000 for local dev.
    /// </summary>
    public Uri ServerUri { get; set; } = new("ws://localhost:5000/ws");
}
