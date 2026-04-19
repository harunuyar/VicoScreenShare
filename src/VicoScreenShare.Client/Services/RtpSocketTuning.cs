namespace VicoScreenShare.Client.Services;

using System;
using System.Net.Sockets;
using System.Reflection;
using SIPSorcery.Net;
using VicoScreenShare.Client.Diagnostics;

/// <summary>
/// Reflection-based UDP socket buffer tuning for SIPSorcery
/// <see cref="RTCPeerConnection"/> instances.
///
/// Why this exists: SIPSorcery 10.0.3 does not expose an API for sizing the
/// underlying RTP/RTCP sockets, and the Windows OS default UDP receive
/// buffer is tiny (~64 KB). At 60+ Mbit of aggregate received traffic — e.g.
/// three simultaneous 20 Mbit subscriber streams landing on the same box — a
/// brief bursts can fill that buffer in milliseconds, and the kernel
/// silently drops the tail. Because H.264 P-frames are references for every
/// subsequent P-frame until the next IDR, a single dropped packet in the
/// middle of a long GOP produces a corruption streak that persists for the
/// entire GOP period. This is the exact symptom profile reported during
/// local 3-subscriber testing on a fat pipe with idle hardware: distortion
/// that grows in duration the longer the keyframe interval is set to.
///
/// What we do: immediately after the peer connection has gathered its local
/// ICE candidates (which is when SIPSorcery has constructed the underlying
/// <c>RTPChannel</c> and bound its UDP socket), we reflect into the private
/// <c>MultiplexRtpChannel</c> field to reach the <see cref="Socket"/> and
/// set both <see cref="Socket.ReceiveBufferSize"/> and
/// <see cref="Socket.SendBufferSize"/> to a WebRTC-class value (default
/// <see cref="DefaultBufferBytes"/> = 2 MiB). 2 MiB covers ~270 ms of 60
/// Mbit traffic — enough to absorb any plausible burst at our target
/// bitrates without taxing memory.
///
/// Non-goals: we do NOT attempt to fix this upstream — SIPSorcery's public
/// surface would require API changes, and vendoring the library for one
/// setter is disproportionate. Reflection is a pragmatic local patch, wrapped
/// in try/catch so a SIPSorcery internal rename in a future version degrades
/// gracefully (the app still works, it just loses the buffer boost).
/// </summary>
internal static class RtpSocketTuning
{
    /// <summary>
    /// Default send/receive buffer size in bytes. 2 MiB — large enough to
    /// absorb a 270 ms burst at 60 Mbit (our worst-case aggregate receive
    /// rate in the reported 3-stream setup), small enough that per-PC cost
    /// is negligible even with many simultaneous connections.
    /// </summary>
    public const int DefaultBufferBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Walk the peer connection's private <c>MultiplexRtpChannel</c> field,
    /// locate the bound UDP socket, and set its receive + send buffer size.
    /// Safe to call multiple times — the socket is idempotent for these
    /// setters. Safe to call before the channel is constructed — returns a
    /// false-ish "not applied" result, and the caller's plan is to retry
    /// later in the negotiation lifecycle.
    /// </summary>
    /// <returns>True if both buffers were applied on at least one socket.</returns>
    public static bool TryApply(RTCPeerConnection pc, int bufferBytes = DefaultBufferBytes)
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

            // Primary data socket — RTP packets (and RTCP too, when the
            // peer negotiates RTCP-mux, which WebRTC always does).
            if (ReadPublicProperty(channel, "RtpSocket") is Socket rtp)
            {
                if (SetBuffers(rtp, bufferBytes, "RtpSocket"))
                {
                    applied = true;
                }
            }

            // Separate control socket — only populated on legacy non-muxed
            // RTCP paths. Tuning it is free insurance; absence is normal.
            if (ReadPrivateField(channel, "m_controlSocket") is Socket rtcp)
            {
                if (SetBuffers(rtcp, bufferBytes, "m_controlSocket"))
                {
                    applied = true;
                }
            }

            return applied;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[rtp-tune] TryApply threw: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static bool SetBuffers(Socket socket, int bytes, string label)
    {
        try
        {
            socket.ReceiveBufferSize = bytes;
            socket.SendBufferSize = bytes;
            DebugLog.Write($"[rtp-tune] {label} buffers set to {bytes / 1024} KiB (effective recv={socket.ReceiveBufferSize} send={socket.SendBufferSize})");
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[rtp-tune] {label} buffer set threw: {ex.Message}");
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
