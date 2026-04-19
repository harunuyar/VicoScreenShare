namespace VicoScreenShare.Client.Services;

using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Observable liveness state for a saved server. Drives the colored dot +
/// label in the connection picker UI.
/// </summary>
public enum ServerStatus
{
    /// <summary>Never probed; waiting on the first run.</summary>
    Unknown,
    /// <summary>Probe in flight.</summary>
    Checking,
    /// <summary>/healthz responded OK; either the server is open or a password is saved locally.</summary>
    Online,
    /// <summary>/healthz responded, server requires auth, and no password is saved.</summary>
    AuthRequired,
    /// <summary>No response / timeout / non-200 / malformed body.</summary>
    Offline,
}

/// <summary>Result of one probe: the status + optionally the server-reported protocol version.</summary>
public sealed record ProbeResult(ServerStatus Status, int? ProtocolVersion = null);

/// <summary>
/// Pre-connect server liveness probe. Translates a WebSocket signaling URI
/// (<c>ws://host:port/ws</c>) to the server's <c>/healthz</c> HTTP endpoint,
/// issues a GET with a short timeout, and maps the response to
/// <see cref="ServerStatus"/>. Used by the HomeView connection picker so the
/// user sees "Online / Password required / Offline" without having to
/// actually create or join a room first.
/// </summary>
public sealed class ServerStatusProbe
{
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public ServerStatusProbe() : this(new HttpClient(), TimeSpan.FromSeconds(3)) { }

    public ServerStatusProbe(HttpClient http, TimeSpan timeout)
    {
        _http = http;
        _timeout = timeout;
    }

    /// <summary>
    /// Probe a server. <paramref name="hasSavedPassword"/> influences how
    /// <c>requiresAuth=true</c> responses are classified: if the user already
    /// saved a password for this connection, we optimistically call it Online
    /// (we'll know for real only on a live connect). With no saved password,
    /// a requires-auth server is surfaced as AuthRequired so the UI can prompt.
    /// </summary>
    public async Task<ProbeResult> ProbeAsync(Uri wsServerUri, bool hasSavedPassword, CancellationToken ct = default)
    {
        if (wsServerUri is null) return new ProbeResult(ServerStatus.Offline);

        var healthUri = TranslateToHealthUri(wsServerUri);
        if (healthUri is null) return new ProbeResult(ServerStatus.Offline);

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(_timeout);

            using var response = await _http.GetAsync(healthUri, linked.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new ProbeResult(ServerStatus.Offline);
            }

            var body = await response.Content.ReadFromJsonAsync<HealthResponse>(linked.Token).ConfigureAwait(false);
            if (body is null) return new ProbeResult(ServerStatus.Offline);

            var status = body.RequiresAuth && !hasSavedPassword
                ? ServerStatus.AuthRequired
                : ServerStatus.Online;
            return new ProbeResult(status, body.ProtocolVersion);
        }
        catch (OperationCanceledException)
        {
            // External cancellation propagates; timeout returns Offline.
            if (ct.IsCancellationRequested) throw;
            return new ProbeResult(ServerStatus.Offline);
        }
        catch (HttpRequestException)
        {
            return new ProbeResult(ServerStatus.Offline);
        }
        catch (System.Text.Json.JsonException)
        {
            return new ProbeResult(ServerStatus.Offline);
        }
    }

    /// <summary>
    /// Map <c>ws://host/ws</c> → <c>http://host/healthz</c> and
    /// <c>wss://host/ws</c> → <c>https://host/healthz</c>. Any non-ws/wss
    /// input returns null — the connection picker only knows about
    /// WebSocket URIs.
    /// </summary>
    internal static Uri? TranslateToHealthUri(Uri wsServerUri)
    {
        var scheme = wsServerUri.Scheme.ToLowerInvariant() switch
        {
            "ws" => "http",
            "wss" => "https",
            _ => null,
        };
        if (scheme is null) return null;

        var builder = new UriBuilder(wsServerUri)
        {
            Scheme = scheme,
            Path = "/healthz",
            Query = string.Empty,
            Fragment = string.Empty,
        };
        // UriBuilder doesn't know the default port switched; reset to -1
        // so http gets 80 / https gets 443 unless the original URI
        // specified a non-default port.
        if (!wsServerUri.IsDefaultPort)
        {
            builder.Port = wsServerUri.Port;
        }
        else
        {
            builder.Port = -1;
        }
        return builder.Uri;
    }

    private sealed class HealthResponse
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("requiresAuth")]
        public bool RequiresAuth { get; set; }

        [JsonPropertyName("protocolVersion")]
        public int ProtocolVersion { get; set; }
    }
}
