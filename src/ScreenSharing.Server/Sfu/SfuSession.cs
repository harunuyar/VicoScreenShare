using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace ScreenSharing.Server.Sfu;

/// <summary>
/// Per-room SFU session. Owns one <see cref="SfuPeer"/> per connected peer,
/// coordinates lookup by peer id, and forwards every received RTP packet out to
/// every other peer in the session (the core SFU fan-out loop).
/// </summary>
public sealed class SfuSession : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, SfuPeer> _peers = new();
    private readonly ConcurrentDictionary<Guid, RtpForwardHandler> _forwarders = new();
    private readonly ILoggerFactory? _loggerFactory;

    public SfuSession(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory;
    }

    public IReadOnlyCollection<SfuPeer> Peers => _peers.Values.ToArray();

    public SfuPeer GetOrCreatePeer(Guid peerId)
    {
        return _peers.GetOrAdd(peerId, id =>
        {
            var logger = _loggerFactory?.CreateLogger<SfuPeer>();
            var peer = new SfuPeer(id, logger);
            // Wire up the fan-out forwarder. Handler is created once and captured
            // so we can unsubscribe it on removal.
            var forwarder = new RtpForwardHandler(this, id);
            peer.RtpPacketReceived += forwarder.OnRtpReceived;
            _forwarders[id] = forwarder;
            return peer;
        });
    }

    public SfuPeer? Find(Guid peerId) =>
        _peers.TryGetValue(peerId, out var peer) ? peer : null;

    public async ValueTask RemovePeerAsync(Guid peerId)
    {
        if (_peers.TryRemove(peerId, out var peer))
        {
            if (_forwarders.TryRemove(peerId, out var forwarder))
            {
                peer.RtpPacketReceived -= forwarder.OnRtpReceived;
            }
            await peer.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var kv in _peers)
        {
            if (_forwarders.TryGetValue(kv.Key, out var forwarder))
            {
                kv.Value.RtpPacketReceived -= forwarder.OnRtpReceived;
            }
            try { await kv.Value.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        _peers.Clear();
        _forwarders.Clear();
    }

    /// <summary>Fan-out loop. Copies a received packet to every other peer in the session.</summary>
    private void ForwardRtp(Guid sourcePeerId, SDPMediaTypesEnum mediaType, RTPPacket packet)
    {
        foreach (var kv in _peers)
        {
            if (kv.Key == sourcePeerId) continue;
            kv.Value.SendForwardedRtp(mediaType, packet);
        }
    }

    /// <summary>
    /// Captures the source peer id so each subscription can forward without
    /// allocating a fresh closure per packet.
    /// </summary>
    private sealed class RtpForwardHandler
    {
        private readonly SfuSession _session;
        private readonly Guid _sourcePeerId;

        public RtpForwardHandler(SfuSession session, Guid sourcePeerId)
        {
            _session = session;
            _sourcePeerId = sourcePeerId;
        }

        public void OnRtpReceived(SDPMediaTypesEnum mediaType, RTPPacket packet)
        {
            _session.ForwardRtp(_sourcePeerId, mediaType, packet);
        }
    }
}
