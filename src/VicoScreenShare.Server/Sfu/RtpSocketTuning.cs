namespace VicoScreenShare.Server.Sfu;

using System;
using System.Net.Sockets;
using System.Reflection;
using SIPSorcery.Net;

/// <summary>
/// Reflection-based UDP socket buffer tuning for the server-side SFU's
/// SIPSorcery <see cref="RTCPeerConnection"/> instances.
///
/// Mirrors the client-side <c>VicoScreenShare.Client.Services.RtpSocketTuning</c>
/// for the same reason: SIPSorcery 10.0.3 does not expose socket-option
/// APIs, and the OS-default UDP buffers are small. On the SFU this matters
/// even more than on the client — a single publisher fan-outs to N
/// subscribers through N separate subscriber peer connections, each with
/// its own egress UDP socket. At default ~64 KB send buffers per socket, a
/// burst of RTP packets from a keyframe or steady-state send can overflow
/// and the kernel silently drops. That surfaces as "down" loss in the
/// publisher's stats overlay even when upstream is fine.
///
/// We reflect into the PC's private <c>MultiplexRtpChannel</c> field to
/// reach the <see cref="Socket"/> and set both
/// <see cref="Socket.ReceiveBufferSize"/> and <see cref="Socket.SendBufferSize"/>
/// to 2 MiB — large enough to absorb realistic bursts at our bitrates,
/// small enough to be negligible even with many concurrent connections.
///
/// Duplicated instead of shared because the server project does not and
/// should not reference the client project. The total code here is small
/// enough that one copy per side is cheaper than the cross-project
/// plumbing a shared location would require.
/// </summary>
internal static class RtpSocketTuning
{
    /// <summary>
    /// Default send/receive buffer size in bytes. 2 MiB — same constant
    /// the client-side helper uses, chosen to absorb a ~270 ms burst at
    /// 60 Mbit aggregate without bloating memory per connection.
    /// </summary>
    public const int DefaultBufferBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Walk the peer connection's private <c>MultiplexRtpChannel</c> field,
    /// locate the bound UDP socket, and raise its receive + send buffer
    /// sizes to <paramref name="bufferBytes"/>. Idempotent and best-effort:
    /// wrapped in try/catch so a SIPSorcery internal rename in a future
    /// version degrades gracefully (the SFU keeps working, just without
    /// the boost).
    /// </summary>
    /// <param name="log">
    /// Optional sink for one-line diagnostic messages. Pass the ambient
    /// logger's <c>LogInformation</c> wrapper; leave null to silence.
    /// </param>
    /// <returns>True if at least one socket's buffers were applied.</returns>
    public static bool TryApply(RTCPeerConnection pc, Action<string>? log = null, int bufferBytes = DefaultBufferBytes)
    {
        if (pc is null)
        {
            return false;
        }

        try
        {
            var channel = ReadPrivateField(pc, "MultiplexRtpChannel");
            if (channel is null)
            {
                return false;
            }

            var applied = false;

            if (ReadPublicProperty(channel, "RtpSocket") is Socket rtp)
            {
                if (SetBuffers(rtp, bufferBytes, "RtpSocket", log))
                {
                    applied = true;
                }
            }

            if (ReadPrivateField(channel, "m_controlSocket") is Socket rtcp)
            {
                if (SetBuffers(rtcp, bufferBytes, "m_controlSocket", log))
                {
                    applied = true;
                }
            }

            return applied;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[rtp-tune] TryApply threw: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static bool SetBuffers(Socket socket, int bytes, string label, Action<string>? log)
    {
        try
        {
            socket.ReceiveBufferSize = bytes;
            socket.SendBufferSize = bytes;
            log?.Invoke($"[rtp-tune] {label} buffers set to {bytes / 1024} KiB (effective recv={socket.ReceiveBufferSize} send={socket.SendBufferSize})");
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[rtp-tune] {label} buffer set threw: {ex.Message}");
            return false;
        }
    }

    private static object? ReadPrivateField(object target, string name)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        return field?.GetValue(target);
    }

    private static object? ReadPublicProperty(object target, string name)
    {
        var prop = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return prop?.GetValue(target);
    }
}
