using FluentAssertions;
using Microsoft.Extensions.Options;
using ScreenSharing.Server.Auth;
using ScreenSharing.Server.Config;
using ScreenSharing.Server.Rooms;

namespace ScreenSharing.Tests.Server;

public class RoomManagerTests
{
    private static RoomManager CreateManager(int capacity = 16, int maxRooms = 500)
    {
        var options = Options.Create(new RoomServerOptions
        {
            MaxRoomCapacity = capacity,
            MaxTotalRooms = maxRooms,
        });
        var monitor = new StaticOptionsMonitor<RoomServerOptions>(options.Value);
        return new RoomManager(new PasswordHasher(), monitor);
    }

    private static RoomPeer MakePeer(string name = "Alice") =>
        new(Guid.NewGuid(), Guid.NewGuid(), name);

    [Fact]
    public void CreateRoom_without_password_returns_success()
    {
        var mgr = CreateManager();
        var result = mgr.CreateRoom(null);

        result.Status.Should().Be(CreateRoomStatus.Success);
        result.Room.Should().NotBeNull();
        result.Room!.Id.Should().HaveLength(6);
        result.Room.HasPassword.Should().BeFalse();
    }

    [Fact]
    public void CreateRoom_with_password_stores_hash_not_plaintext()
    {
        var mgr = CreateManager();
        var result = mgr.CreateRoom("hunter2");
        result.Room!.PasswordHash.Should().NotBeNull();
        result.Room.PasswordHash.Should().NotBe("hunter2");
    }

    [Fact]
    public void TryJoin_returns_NotFound_for_unknown_room()
    {
        var mgr = CreateManager();
        var result = mgr.TryJoin("NOPE42", null, MakePeer());
        result.Status.Should().Be(JoinRoomStatus.NotFound);
    }

    [Fact]
    public void TryJoin_rejects_wrong_password()
    {
        var mgr = CreateManager();
        var room = mgr.CreateRoom("secret").Room!;

        var result = mgr.TryJoin(room.Id, "wrong", MakePeer());
        result.Status.Should().Be(JoinRoomStatus.InvalidPassword);
    }

    [Fact]
    public void TryJoin_accepts_correct_password_and_becomes_host()
    {
        var mgr = CreateManager();
        var room = mgr.CreateRoom("secret").Room!;
        var peer = MakePeer();

        var result = mgr.TryJoin(room.Id, "secret", peer);

        result.Status.Should().Be(JoinRoomStatus.Success);
        result.HostPeerId.Should().Be(peer.PeerId);
        result.SnapshotAfterJoin.Should().ContainSingle(p => p.PeerId == peer.PeerId);
    }

    [Fact]
    public void Second_peer_joins_but_first_remains_host()
    {
        var mgr = CreateManager();
        var room = mgr.CreateRoom(null).Room!;
        var alice = MakePeer("Alice");
        var bob = MakePeer("Bob");

        mgr.TryJoin(room.Id, null, alice);
        var result = mgr.TryJoin(room.Id, null, bob);

        result.Status.Should().Be(JoinRoomStatus.Success);
        result.HostPeerId.Should().Be(alice.PeerId);
        result.SnapshotAfterJoin.Should().HaveCount(2);
    }

    [Fact]
    public void TryJoin_returns_Full_at_capacity()
    {
        var mgr = CreateManager(capacity: 3);
        var room = mgr.CreateRoom(null).Room!;

        for (var i = 0; i < 3; i++)
        {
            mgr.TryJoin(room.Id, null, MakePeer($"P{i}"));
        }

        var overflow = mgr.TryJoin(room.Id, null, MakePeer("Over"));
        overflow.Status.Should().Be(JoinRoomStatus.Full);
    }

    [Fact]
    public void RemovePeer_promotes_next_oldest_when_host_leaves()
    {
        var mgr = CreateManager();
        var room = mgr.CreateRoom(null).Room!;
        var alice = MakePeer("Alice");
        var bob = MakePeer("Bob");

        mgr.TryJoin(room.Id, null, alice);
        mgr.TryJoin(room.Id, null, bob);

        var outcome = mgr.RemovePeer(room.Id, alice.PeerId);

        outcome.Found.Should().BeTrue();
        outcome.WasHost.Should().BeTrue();
        outcome.NewHostPeerId.Should().Be(bob.PeerId);
        outcome.RoomDeleted.Should().BeFalse();
    }

    [Fact]
    public void RemovePeer_deletes_room_when_last_peer_leaves()
    {
        var mgr = CreateManager();
        var room = mgr.CreateRoom(null).Room!;
        var alice = MakePeer("Alice");
        mgr.TryJoin(room.Id, null, alice);

        var outcome = mgr.RemovePeer(room.Id, alice.PeerId);

        outcome.RoomDeleted.Should().BeTrue();
        mgr.FindRoom(room.Id).Should().BeNull();
    }

    [Fact]
    public void Different_rooms_get_different_ids()
    {
        var mgr = CreateManager();
        var ids = Enumerable.Range(0, 20).Select(_ => mgr.CreateRoom(null).Room!.Id).ToList();
        ids.Distinct().Should().HaveCount(20);
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value) => CurrentValue = value;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
