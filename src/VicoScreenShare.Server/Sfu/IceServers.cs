namespace VicoScreenShare.Server.Sfu;

using System.Collections.Generic;
using System.Linq;
using SIPSorcery.Net;
using VicoScreenShare.Protocol.Messages;
using VicoScreenShare.Server.Config;

/// <summary>
/// One place where the operator's <see cref="RoomServerOptions.IceServers"/>
/// is translated into both the wire-format <see cref="IceServerConfig"/>
/// that ships to clients in <see cref="RoomJoined"/>, and the SIPSorcery
/// <see cref="RTCIceServer"/> list that the server's own SFU peer
/// connections use.
///
/// Fallback policy: if the operator hasn't configured anything, default
/// to Google's public STUN so existing deployments keep working. A log
/// entry at startup (emitted by the caller) makes the fallback visible
/// instead of silent.
/// </summary>
internal static class IceServers
{
    /// <summary>Default STUN URL used when no entries are configured.</summary>
    public const string FallbackStunUrl = "stun:stun.l.google.com:19302";

    /// <summary>
    /// Map the options list to the wire-format DTO, applying the Google
    /// STUN fallback on an empty list so clients always receive at least
    /// one entry.
    /// </summary>
    public static IReadOnlyList<IceServerConfig> ToWireConfig(IReadOnlyList<IceServerOptions>? options)
    {
        if (options is null || options.Count == 0)
        {
            return new[] { new IceServerConfig(new[] { FallbackStunUrl }, Username: null, Credential: null) };
        }

        return options
            .Where(o => o.Urls is { Count: > 0 })
            .Select(o => new IceServerConfig(
                Urls: o.Urls.ToArray(),
                Username: string.IsNullOrEmpty(o.Username) ? null : o.Username,
                Credential: string.IsNullOrEmpty(o.Credential) ? null : o.Credential))
            .ToArray();
    }

    /// <summary>
    /// Build the SIPSorcery <see cref="RTCIceServer"/> list the server's
    /// SFU peer connections register with, applying the same fallback
    /// policy. SIPSorcery joins multiple URLs with commas inside
    /// <see cref="RTCIceServer.urls"/>.
    /// </summary>
    public static List<RTCIceServer> ToRtc(IReadOnlyList<IceServerOptions>? options)
    {
        if (options is null || options.Count == 0)
        {
            return new List<RTCIceServer> { new() { urls = FallbackStunUrl } };
        }

        var result = new List<RTCIceServer>(options.Count);
        foreach (var o in options)
        {
            if (o.Urls is null || o.Urls.Count == 0)
            {
                continue;
            }
            result.Add(new RTCIceServer
            {
                urls = string.Join(",", o.Urls),
                username = o.Username ?? string.Empty,
                credential = o.Credential ?? string.Empty,
            });
        }
        if (result.Count == 0)
        {
            result.Add(new RTCIceServer { urls = FallbackStunUrl });
        }
        return result;
    }
}
