using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using ScreenSharing.Server.Auth;
using ScreenSharing.Server.Config;

namespace ScreenSharing.Server.Rooms;

public sealed class RoomManager
{
    private static readonly char[] Alphabet =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789".ToCharArray();

    private const int IdLength = 6;
    private const int CreateCollisionRetries = 10;

    private readonly ConcurrentDictionary<string, Room> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly PasswordHasher _hasher;
    private readonly IOptionsMonitor<RoomServerOptions> _options;

    public RoomManager(PasswordHasher hasher, IOptionsMonitor<RoomServerOptions> options)
    {
        _hasher = hasher;
        _options = options;
    }

    public int RoomCount => _rooms.Count;

    public CreateRoomResult CreateRoom(string? password)
    {
        var opts = _options.CurrentValue;
        if (_rooms.Count >= opts.MaxTotalRooms)
        {
            return CreateRoomResult.ServerFull();
        }

        var hash = string.IsNullOrEmpty(password) ? null : _hasher.Hash(password!);

        for (var attempt = 0; attempt < CreateCollisionRetries; attempt++)
        {
            var id = GenerateId();
            var room = new Room(id, hash);
            if (_rooms.TryAdd(id, room))
            {
                return CreateRoomResult.Success(room);
            }
        }
        return CreateRoomResult.CollisionRetriesExceeded();
    }

    public JoinRoomResult TryJoin(string roomId, string? password, RoomPeer peer)
    {
        if (!_rooms.TryGetValue(roomId, out var room))
        {
            return JoinRoomResult.NotFound();
        }

        if (room.PasswordHash is not null)
        {
            if (!_hasher.Verify(password ?? string.Empty, room.PasswordHash))
            {
                return JoinRoomResult.InvalidPassword();
            }
        }
        else if (!string.IsNullOrEmpty(password))
        {
            // Room is public; a client sending a password is a protocol mismatch,
            // but accept for convenience in Phase 1.
        }

        var add = room.TryAddPeer(peer, _options.CurrentValue.MaxRoomCapacity);
        return add.Status switch
        {
            AddPeerStatus.Ok => JoinRoomResult.Success(room, add.HostPeerId, add.SnapshotAfterAdd),
            AddPeerStatus.Full => JoinRoomResult.Full(),
            AddPeerStatus.AlreadyIn => JoinRoomResult.AlreadyIn(),
            _ => JoinRoomResult.NotFound(),
        };
    }

    public RemovePeerOutcome RemovePeer(string roomId, Guid peerId)
    {
        if (!_rooms.TryGetValue(roomId, out var room))
        {
            return new RemovePeerOutcome(false, false, null, 0, false);
        }
        var result = room.RemovePeer(peerId);
        var roomDeleted = false;
        if (result.PeerCountAfter == 0)
        {
            _rooms.TryRemove(roomId, out _);
            roomDeleted = true;
        }
        return new RemovePeerOutcome(
            Found: result.Found,
            WasHost: result.WasHost,
            NewHostPeerId: result.NewHostPeerId,
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
    InvalidPassword,
    Full,
    AlreadyIn,
}

public readonly record struct JoinRoomResult(
    JoinRoomStatus Status,
    Room? Room,
    Guid HostPeerId,
    IReadOnlyList<RoomPeer> SnapshotAfterJoin)
{
    public static JoinRoomResult Success(Room room, Guid hostPeerId, IReadOnlyList<RoomPeer> snapshot) =>
        new(JoinRoomStatus.Success, room, hostPeerId, snapshot);
    public static JoinRoomResult NotFound() => new(JoinRoomStatus.NotFound, null, Guid.Empty, Array.Empty<RoomPeer>());
    public static JoinRoomResult InvalidPassword() => new(JoinRoomStatus.InvalidPassword, null, Guid.Empty, Array.Empty<RoomPeer>());
    public static JoinRoomResult Full() => new(JoinRoomStatus.Full, null, Guid.Empty, Array.Empty<RoomPeer>());
    public static JoinRoomResult AlreadyIn() => new(JoinRoomStatus.AlreadyIn, null, Guid.Empty, Array.Empty<RoomPeer>());
}

public readonly record struct RemovePeerOutcome(
    bool Found,
    bool WasHost,
    Guid? NewHostPeerId,
    int PeerCountAfter,
    bool RoomDeleted);
