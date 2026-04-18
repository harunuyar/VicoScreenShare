using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VicoScreenShare.Server.Config;
using VicoScreenShare.Server.Sfu;

namespace VicoScreenShare.Server.Rooms;

public sealed class RoomManager
{
    private static readonly char[] Alphabet =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789".ToCharArray();

    private const int IdLength = 6;
    private const int CreateCollisionRetries = 10;

    private readonly ConcurrentDictionary<string, Room> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SfuSession> _sfuSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly IOptionsMonitor<RoomServerOptions> _options;
    private readonly ILoggerFactory? _loggerFactory;

    // Per-peer grace-timer cancellation source. Keyed by PeerId (unique across
    // all rooms since each WsSession generates its own Guid.NewGuid()).
    // Interlocked.Exchange on the CancellationTokenSource disambiguates the
    // resume-vs-expiry race: whichever caller gets the non-null CTS wins.
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _graceTimers = new();

    public RoomManager(
        IOptionsMonitor<RoomServerOptions> options,
        ILoggerFactory? loggerFactory = null)
    {
        _options = options;
        _loggerFactory = loggerFactory;
    }

    public int RoomCount => _rooms.Count;

    public SfuSession? GetSfuSession(string roomId) =>
        _sfuSessions.TryGetValue(roomId, out var sfu) ? sfu : null;

    public CreateRoomResult CreateRoom()
    {
        var opts = _options.CurrentValue;
        if (_rooms.Count >= opts.MaxTotalRooms)
        {
            return CreateRoomResult.ServerFull();
        }

        for (var attempt = 0; attempt < CreateCollisionRetries; attempt++)
        {
            var id = GenerateId();
            var room = new Room(id);
            if (_rooms.TryAdd(id, room))
            {
                _sfuSessions[id] = new SfuSession(_loggerFactory);
                return CreateRoomResult.Success(room);
            }
        }
        return CreateRoomResult.CollisionRetriesExceeded();
    }

    public JoinRoomResult TryJoin(string roomId, RoomPeer peer)
    {
        if (!_rooms.TryGetValue(roomId, out var room))
        {
            return JoinRoomResult.NotFound();
        }

        var add = room.TryAddPeer(peer, _options.CurrentValue.MaxRoomCapacity);
        if (add.Status == AddPeerStatus.Ok)
        {
            // Issue the initial resume token so a late disconnect is recoverable.
            peer.ResumeToken = GenerateResumeToken();
        }

        return add.Status switch
        {
            AddPeerStatus.Ok => JoinRoomResult.Success(room, add.SnapshotAfterAdd),
            AddPeerStatus.Full => JoinRoomResult.Full(),
            AddPeerStatus.AlreadyIn => JoinRoomResult.AlreadyIn(),
            _ => JoinRoomResult.NotFound(),
        };
    }

    /// <summary>
    /// Mark a peer as disconnected and schedule the hard-removal callback to run
    /// after <see cref="RoomServerOptions.PeerGracePeriod"/>. Returns the room
    /// the peer belongs to so the caller can broadcast <c>PeerConnectionState</c>
    /// without a second lookup. Null if the peer/room is already gone.
    /// </summary>
    public Room? BeginPeerGrace(string roomId, Guid peerId, Func<Task> onExpire)
    {
        if (!_rooms.TryGetValue(roomId, out var room)) return null;
        if (!room.TrySetPeerConnected(peerId, false)) return null;

        var cts = new CancellationTokenSource();
        if (!_graceTimers.TryAdd(peerId, cts))
        {
            // A previous grace already exists — shouldn't happen with clean
            // teardown, but coalesce just in case. Cancel the new CTS so we
            // don't leak it, and reuse the existing one.
            cts.Dispose();
            return room;
        }

        var graceWindow = _options.CurrentValue.PeerGracePeriod;

        // Fire-and-forget. The continuation runs onExpire if still-disconnected
        // when the delay completes — the resume path cancels this CTS atomically
        // via TryResume's Interlocked.Exchange.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(graceWindow, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Resumed within grace; do nothing.
                return;
            }

            // Timer elapsed. Take the CTS out of the map atomically — if
            // TryResume snatched it first we lose and do nothing.
            if (_graceTimers.TryRemove(new KeyValuePair<Guid, CancellationTokenSource>(peerId, cts)))
            {
                try { await onExpire().ConfigureAwait(false); }
                catch { /* caller logs */ }
                cts.Dispose();
            }
        });

        return room;
    }

    /// <summary>
    /// Attempt to rebind a disconnected peer on resume. Atomically claims the
    /// grace-timer's cancellation source — if the expiry-path grabbed it first,
    /// returns <see cref="ResumeOutcomeStatus.Expired"/>. On success returns the
    /// room + peer (with its <see cref="RoomPeer.IsConnected"/> flipped true and
    /// a fresh <see cref="RoomPeer.ResumeToken"/> rotated in).
    /// </summary>
    public ResumeOutcome TryResume(string roomId, string resumeToken)
    {
        if (!_rooms.TryGetValue(roomId, out var room))
        {
            return ResumeOutcome.RoomGone();
        }

        var peer = room.FindByResumeToken(resumeToken);
        if (peer is null)
        {
            return ResumeOutcome.TokenUnknown();
        }

        // Atomically claim the grace CTS. If _graceTimers has no entry for this
        // peer, either they never disconnected (weird) or the timer already fired.
        if (!_graceTimers.TryRemove(peer.PeerId, out var cts))
        {
            // Timer already fired and removed the CTS — peer is about to be
            // hard-removed (or already has been). The room may still contain
            // the peer for a microsecond; we treat this as expiry.
            return ResumeOutcome.Expired();
        }

        // Cancel the timer so the delay task unwinds.
        try { cts.Cancel(); } catch { }
        cts.Dispose();

        // Rotate the token so this one can't be reused.
        var newToken = GenerateResumeToken();
        peer.ResumeToken = newToken;

        // Flip IsConnected back — this broadcast will be emitted by WsSession.
        room.TrySetPeerConnected(peer.PeerId, true);

        return ResumeOutcome.Success(room, peer, newToken);
    }

    private static string GenerateResumeToken()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes);
    }

    public RemovePeerOutcome RemovePeer(string roomId, Guid peerId)
    {
        if (!_rooms.TryGetValue(roomId, out var room))
        {
            return new RemovePeerOutcome(false, 0, false);
        }
        var result = room.RemovePeer(peerId);
        var roomDeleted = false;

        // Tear down the peer's SFU state whether or not the room is deleted.
        if (_sfuSessions.TryGetValue(roomId, out var sfu))
        {
            _ = sfu.RemovePeerAsync(peerId).AsTask();
        }

        if (result.PeerCountAfter == 0)
        {
            _rooms.TryRemove(roomId, out _);
            if (_sfuSessions.TryRemove(roomId, out var deadSession))
            {
                _ = deadSession.DisposeAsync().AsTask();
            }
            roomDeleted = true;
        }
        return new RemovePeerOutcome(
            Found: result.Found,
            PeerCountAfter: result.PeerCountAfter,
            RoomDeleted: roomDeleted);
    }

    public Room? FindRoom(string roomId) =>
        _rooms.TryGetValue(roomId, out var r) ? r : null;

    private static string GenerateId()
    {
        Span<byte> bytes = stackalloc byte[IdLength];
        RandomNumberGenerator.Fill(bytes);
        Span<char> chars = stackalloc char[IdLength];
        for (var i = 0; i < IdLength; i++)
        {
            chars[i] = Alphabet[bytes[i] % Alphabet.Length];
        }
        return new string(chars);
    }
}

public enum CreateRoomStatus
{
    Success,
    ServerFull,
    CollisionRetriesExceeded,
}

public readonly record struct CreateRoomResult(CreateRoomStatus Status, Room? Room)
{
    public static CreateRoomResult Success(Room room) => new(CreateRoomStatus.Success, room);
    public static CreateRoomResult ServerFull() => new(CreateRoomStatus.ServerFull, null);
    public static CreateRoomResult CollisionRetriesExceeded() => new(CreateRoomStatus.CollisionRetriesExceeded, null);
}

public enum JoinRoomStatus
{
    Success,
    NotFound,
    Full,
    AlreadyIn,
}

public readonly record struct JoinRoomResult(
    JoinRoomStatus Status,
    Room? Room,
    IReadOnlyList<RoomPeer> SnapshotAfterJoin)
{
    public static JoinRoomResult Success(Room room, IReadOnlyList<RoomPeer> snapshot) =>
        new(JoinRoomStatus.Success, room, snapshot);
    public static JoinRoomResult NotFound() => new(JoinRoomStatus.NotFound, null, Array.Empty<RoomPeer>());
    public static JoinRoomResult Full() => new(JoinRoomStatus.Full, null, Array.Empty<RoomPeer>());
    public static JoinRoomResult AlreadyIn() => new(JoinRoomStatus.AlreadyIn, null, Array.Empty<RoomPeer>());
}

public readonly record struct RemovePeerOutcome(
    bool Found,
    int PeerCountAfter,
    bool RoomDeleted);

public enum ResumeOutcomeStatus
{
    /// <summary>Peer rebound to their existing slot; new token in <see cref="ResumeOutcome.NewResumeToken"/>.</summary>
    Success,

    /// <summary>Token isn't recognized — probably consumed by an earlier successful resume.</summary>
    TokenUnknown,

    /// <summary>Grace window elapsed before the resume arrived.</summary>
    Expired,

    /// <summary>Room no longer exists.</summary>
    RoomGone,
}

public readonly record struct ResumeOutcome(
    ResumeOutcomeStatus Status,
    Room? Room,
    RoomPeer? Peer,
    string? NewResumeToken)
{
    public static ResumeOutcome Success(Room room, RoomPeer peer, string newToken) =>
        new(ResumeOutcomeStatus.Success, room, peer, newToken);
    public static ResumeOutcome TokenUnknown() => new(ResumeOutcomeStatus.TokenUnknown, null, null, null);
    public static ResumeOutcome Expired() => new(ResumeOutcomeStatus.Expired, null, null, null);
    public static ResumeOutcome RoomGone() => new(ResumeOutcomeStatus.RoomGone, null, null, null);
}
