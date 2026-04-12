using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace ScreenSharing.Server.Sfu;

/// <summary>
/// Per-room SFU session. Owns one <see cref="SfuPeer"/> per connected peer and
/// coordinates lookup by peer id. In Phase 3.1 it only tracks peers and the
/// WebRTC handshake state; Phase 3.4 will hang the RTP forwarder off of it.
/// </summary>
public sealed class SfuSession : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, SfuPeer> _peers = new();
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
            return new SfuPeer(id, logger);
        });
    }

    public SfuPeer? Find(Guid peerId) =>
        _peers.TryGetValue(peerId, out var peer) ? peer : null;

    public async ValueTask RemovePeerAsync(Guid peerId)
    {
        if (_peers.TryRemove(peerId, out var peer))
        {
            await peer.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var peer in _peers.Values)
        {
            try { await peer.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        _peers.Clear();
    }
}
