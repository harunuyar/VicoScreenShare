namespace VicoScreenShare.Server.Sfu;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;

/// <summary>
/// Per-room SFU session. Owns:
/// <list type="bullet">
/// <item>One <see cref="SfuPeer"/> per connected peer — receives the peer's own
/// upstream RTP when they publish. No fan-out happens here.</item>
/// <item>One <see cref="SfuSubscriberPeer"/> per (viewer, publisher) pair. Each
/// subscriber PC has its own outbound track + SSRC, so the viewer can demux
/// multiple publishers naturally via one <see cref="RTCPeerConnection"/> per
/// publisher — no dynamic renegotiation, no multi-track SDP gymnastics.</item>
/// </list>
/// The fan-out is driven by <see cref="OnPublisherStarted"/> /
/// <see cref="OnPublisherStopped"/> — called from <c>WsSession</c> when a
/// StreamStarted/StreamEnded crosses the wire — and by
/// <see cref="OnViewerJoined"/> when someone new arrives mid-room so they
/// auto-subscribe to every already-live publisher.
/// </summary>
public sealed class SfuSession : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, SfuPeer> _peers = new();
    private readonly ConcurrentDictionary<Guid, PublisherForwarder> _forwarders = new();

    // Key: (viewerPeerId, publisherPeerId). One entry per active subscription.
    private readonly ConcurrentDictionary<(Guid viewer, Guid publisher), SfuSubscriberPeer> _subscribers = new();

    // Currently-publishing peers. Used when a new viewer joins to auto-create
    // their subscriptions to every in-progress stream.
    private readonly ConcurrentDictionary<Guid, byte> _publishers = new();

    // Room members known to this SFU session. A peer is a potential viewer the
    // moment they join the room — well before (or without ever) setting up an
    // upstream SfuPeer via SDP offer. Kept here separately from <see cref="_peers"/>
    // so publisher fan-out can find every room member, not just those who have
    // already done the upstream handshake.
    private readonly ConcurrentDictionary<Guid, byte> _viewers = new();

    private readonly ILoggerFactory? _loggerFactory;

    public SfuSession(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory;
    }

    public IReadOnlyCollection<SfuPeer> Peers => _peers.Values.ToArray();

    /// <summary>
    /// Fired when a subscriber PC is created and ready for the server to drive
    /// its SDP offer out to the viewer. The handler (<c>WsSession</c>) creates
    /// the offer and ships it with SubscriptionId = publisher's PeerId.
    /// </summary>
    public event Func<SfuSubscriberPeer, Task>? SubscriberReady;

    public SfuPeer GetOrCreatePeer(Guid peerId)
    {
        return _peers.GetOrAdd(peerId, id =>
        {
            var logger = _loggerFactory?.CreateLogger<SfuPeer>();
            var peer = new SfuPeer(id, logger);
            var forwarder = new PublisherForwarder(this, id);
            peer.RtpPacketReceived += forwarder.OnRtpReceived;
            _forwarders[id] = forwarder;
            return peer;
        });
    }

    public SfuPeer? Find(Guid peerId) =>
        _peers.TryGetValue(peerId, out var peer) ? peer : null;

    public SfuSubscriberPeer? FindSubscriber(Guid viewerPeerId, Guid publisherPeerId) =>
        _subscribers.TryGetValue((viewerPeerId, publisherPeerId), out var sub) ? sub : null;

    /// <summary>
    /// Viewer joined the room. For every already-streaming publisher, create a
    /// subscriber PC paired to this viewer and ask the host to drive its offer
    /// out. Publisher-to-self subscriptions are skipped.
    /// </summary>
    public async Task OnViewerJoinedAsync(Guid viewerPeerId)
    {
        _viewers[viewerPeerId] = 1;

        foreach (var publisher in _publishers.Keys)
        {
            if (publisher == viewerPeerId)
            {
                continue;
            }

            await EnsureSubscriberAsync(viewerPeerId, publisher).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Publisher just announced StreamStarted. Mark them as a live publisher and
    /// create a subscriber PC for every other room member.
    /// </summary>
    public async Task OnPublisherStartedAsync(Guid publisherPeerId)
    {
        _publishers[publisherPeerId] = 1;

        // Fan out to every known viewer, not just those who have already built
        // their upstream SfuPeer via SDP. A pure viewer never hits GetOrCreatePeer
        // but still expects to receive publisher frames.
        foreach (var viewer in _viewers.Keys)
        {
            if (viewer == publisherPeerId)
            {
                continue;
            }

            await EnsureSubscriberAsync(viewer, publisherPeerId).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Publisher stopped streaming. Tear down every subscriber PC for them so
    /// viewers dispose their downstream state (the client-side StreamReceiver
    /// + RecvOnly PC) on its own Closed event.
    /// </summary>
    public async Task OnPublisherStoppedAsync(Guid publisherPeerId)
    {
        _publishers.TryRemove(publisherPeerId, out _);

        var keysToRemove = _subscribers.Keys
            .Where(k => k.publisher == publisherPeerId)
            .ToArray();
        foreach (var key in keysToRemove)
        {
            if (_subscribers.TryRemove(key, out var sub))
            {
                await sub.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Is the given peer currently publishing a stream?</summary>
    public bool IsPublishing(Guid peerId) => _publishers.ContainsKey(peerId);

    /// <summary>
    /// Worst <c>fraction-lost</c> (byte, 0-255 per RFC 3550) across every
    /// downstream subscriber for <paramref name="publisherPeerId"/>, as last
    /// observed from each viewer's RTCP Receiver Reports. Drives the periodic
    /// <c>DownstreamLossReport</c> the server pushes up to the publisher so
    /// the publisher's adaptive-bitrate controller can respond to congestion
    /// on the SFU → viewer hop (which the publisher's own upstream RRs are
    /// blind to).
    /// </summary>
    public byte GetWorstDownstreamFractionLost(Guid publisherPeerId)
    {
        byte worst = 0;
        foreach (var kv in _subscribers)
        {
            if (kv.Key.publisher != publisherPeerId)
            {
                continue;
            }
            var v = kv.Value.LatestFractionLost;
            if (v > worst)
            {
                worst = v;
            }
        }
        return worst;
    }

    /// <summary>
    /// Client-driven subscribe: viewer wants to (re-)receive this publisher's
    /// stream. No-op if the publisher isn't currently streaming or if a
    /// subscription already exists. Fires the usual SubscriberReady event so
    /// the WsSession drives the SDP offer.
    /// </summary>
    public async Task SubscribeAsync(Guid viewerPeerId, Guid publisherPeerId)
    {
        if (viewerPeerId == publisherPeerId)
        {
            return;
        }

        if (!_publishers.ContainsKey(publisherPeerId))
        {
            return;
        }

        await EnsureSubscriberAsync(viewerPeerId, publisherPeerId).ConfigureAwait(false);
    }

    /// <summary>
    /// Client-driven unsubscribe: viewer no longer wants this publisher's
    /// stream. Disposes the matching <see cref="SfuSubscriberPeer"/>; the
    /// SFU's own OnConnectionStateChange on the viewer-side PC will clean
    /// the client's tile up on its next teardown event.
    /// </summary>
    public async Task UnsubscribeAsync(Guid viewerPeerId, Guid publisherPeerId)
    {
        var key = (viewerPeerId, publisherPeerId);
        if (_subscribers.TryRemove(key, out var sub))
        {
            await sub.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task EnsureSubscriberAsync(Guid viewerPeerId, Guid publisherPeerId)
    {
        var key = (viewerPeerId, publisherPeerId);
        if (_subscribers.ContainsKey(key))
        {
            return;
        }

        var logger = _loggerFactory?.CreateLogger<SfuSubscriberPeer>();
        var sub = new SfuSubscriberPeer(viewerPeerId, publisherPeerId, logger);
        if (!_subscribers.TryAdd(key, sub))
        {
            await sub.DisposeAsync().ConfigureAwait(false);
            return;
        }

        // Let WsSession drive the SDP offer out to the viewer tagged with
        // publisherPeerId as SubscriptionId. SubscriberReady is a multicast
        // Func<T, Task> — Delegate.Invoke only returns the last handler's Task,
        // so fan out explicitly so every handler's Task is observed. Individual
        // handler failures are logged but don't abort the other handlers.
        var handler = SubscriberReady;
        if (handler is not null)
        {
            var invocationList = handler.GetInvocationList();
            var tasks = new Task[invocationList.Length];
            for (var i = 0; i < invocationList.Length; i++)
            {
                tasks[i] = ((Func<SfuSubscriberPeer, Task>)invocationList[i])(sub);
            }
            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _loggerFactory?.CreateLogger<SfuSession>()?
                    .LogDebug(ex, "SubscriberReady handler threw for {Viewer}<-{Publisher}",
                        viewerPeerId, publisherPeerId);
            }
        }
    }

    public async ValueTask RemovePeerAsync(Guid peerId)
    {
        // Peer leaving: tear down anything where they are the viewer OR the publisher.
        _viewers.TryRemove(peerId, out _);
        _publishers.TryRemove(peerId, out _);

        var keysToRemove = _subscribers.Keys
            .Where(k => k.viewer == peerId || k.publisher == peerId)
            .ToArray();
        foreach (var key in keysToRemove)
        {
            if (_subscribers.TryRemove(key, out var sub))
            {
                try { await sub.DisposeAsync().ConfigureAwait(false); } catch { }
            }
        }

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
        foreach (var sub in _subscribers.Values)
        {
            try { await sub.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        _subscribers.Clear();
        _publishers.Clear();
        _viewers.Clear();

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

    /// <summary>
    /// Fan-out for one publisher: iterate every live subscription where this
    /// peer is the publisher, forward the packet down its PC. Skips allocating
    /// per-packet closures by using the captured <see cref="PublisherForwarder"/>.
    /// </summary>
    private void ForwardPublisherRtp(Guid sourcePeerId, SDPMediaTypesEnum mediaType, RTPPacket packet)
    {
        foreach (var kv in _subscribers)
        {
            if (kv.Key.publisher != sourcePeerId)
            {
                continue;
            }

            kv.Value.SendForwardedRtp(mediaType, packet);
        }
    }

    /// <summary>
    /// One of these per SfuPeer. Captures the source peer id so RTP forwarding
    /// is an O(1) event dispatch plus O(subscribers-with-this-publisher) loop,
    /// no allocations per packet.
    /// </summary>
    private sealed class PublisherForwarder
    {
        private readonly SfuSession _session;
        private readonly Guid _sourcePeerId;

        public PublisherForwarder(SfuSession session, Guid sourcePeerId)
        {
            _session = session;
            _sourcePeerId = sourcePeerId;
        }

        public void OnRtpReceived(SDPMediaTypesEnum mediaType, RTPPacket packet)
        {
            _session.ForwardPublisherRtp(_sourcePeerId, mediaType, packet);
        }
    }
}
