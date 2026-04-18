using FluentAssertions;
using Microsoft.Extensions.Options;
using VicoScreenShare.Server.Config;
using VicoScreenShare.Server.Rooms;

namespace VicoScreenShare.Tests.Server;

public class RoomManagerTests
{
    private static RoomManager CreateManager(int capacity = 16, int maxRooms = 500, TimeSpan? graceWindow = null)
    {
        var options = Options.Create(new RoomServerOptions
        {
            MaxRoomCapacity = capacity,
            MaxTotalRooms = maxRooms,
            PeerGracePeriod = graceWindow ?? TimeSpan.FromSeconds(20),
        });
        var monitor = new StaticOptionsMonitor<RoomServerOptions>(options.Value);
        return new RoomManager(monitor);
    }

    private static RoomPeer MakePeer(string name = "Alice") =>
        new(Guid.NewGuid(), Guid.NewGuid(), name);

    [Fact]
    public void CreateRoom_returns_success_with_six_char_id()
    {
        var mgr = CreateManager();
        var result = mgr.CreateRoom();

        result.Status.Should().Be(CreateRoomStatus.Success);
        result.Room.Should().NotBeNull();
        result.Room!.Id.Should().HaveLength(6);
    }

    [Fact]
    public void TryJoin_returns_NotFound_for_unknown_room()
    {
        var mgr = CreateManager();
        var result = mgr.TryJoin("NOPE42", MakePeer());
        result.Status.Should().Be(JoinRoomStatus.NotFound);
    }

    [Fact]
    public void TryJoin_first_peer_lands_in_snapshot()
    {
        var mgr = CreateManager();
        var room = mgr.CreateRoom().Room!;
        var peer = MakePeer();

        var result = mgr.TryJoin(room.Id, peer);

        result.Status.Should().Be(JoinRoomStatus.Success);
        result.SnapshotAfterJoin.Should().ContainSingle(p => p.PeerId == peer.PeerId);
    }

    [Fact]
    public void Second_peer_joins_and_snapshot_has_both()
    {
        var mgr = CreateManager();
        var room = mgr.CreateRoom().Room!;
        var alice = MakePeer("Alice");
        var bob = MakePeer("Bob");

        mgr.TryJoin(room.Id, alice);
        var result = mgr.TryJoin(room.Id, bob);

        result.Status.Should().Be(JoinRoomStatus.Success);
        result.SnapshotAfterJoin.Should().HaveCount(2);
    }

    [Fact]
    public void TryJoin_returns_Full_at_capacity()
    {
        var mgr = CreateManager(capacity: 3);
        var room = mgr.CreateRoom().Room!;

        for (var i = 0; i < 3; i++)
        {
            mgr.TryJoin(room.Id, MakePeer($"P{i}"));
        }

        var overflow = mgr.TryJoin(room.Id, MakePeer("Over"));
        overflow.Status.Should().Be(JoinRoomStatus.Full);
    }

    [Fact]
    public void RemovePeer_keeps_room_alive_when_others_remain()
    {
        var mgr = CreateManager();
        var room = mgr.CreateRoom().Room!;
        var alice = MakePeer("Alice");
        var bob = MakePeer("Bob");

        mgr.TryJoin(room.Id, alice);
        mgr.TryJoin(room.Id, bob);

        var outcome = mgr.RemovePeer(room.Id, alice.PeerId);

        outcome.Found.Should().BeTrue();
        outcome.PeerCountAfter.Should().Be(1);
        outcome.RoomDeleted.Should().BeFalse();
    }

    [Fact]
    public void RemovePeer_deletes_room_when_last_peer_leaves()
    {
        var mgr = CreateManager();
        var room = mgr.CreateRoom().Room!;
        var alice = MakePeer("Alice");
        mgr.TryJoin(room.Id, alice);

        var outcome = mgr.RemovePeer(room.Id, alice.PeerId);

        outcome.RoomDeleted.Should().BeTrue();
        mgr.FindRoom(room.Id).Should().BeNull();
    }

    [Fact]
    public void Different_rooms_get_different_ids()
    {
        var mgr = CreateManager();
        var ids = Enumerable.Range(0, 20).Select(_ => mgr.CreateRoom().Room!.Id).ToList();
        ids.Distinct().Should().HaveCount(20);
    }

    [Fact]
    public void TryJoin_success_issues_a_resume_token_on_the_peer()
    {
        var mgr = CreateManager();
        var room = mgr.CreateRoom().Room!;
        var peer = MakePeer();

        mgr.TryJoin(room.Id, peer);

        peer.ResumeToken.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task TryResume_before_grace_expiry_restores_the_peer()
    {
        var mgr = CreateManager(graceWindow: TimeSpan.FromSeconds(5));
        var room = mgr.CreateRoom().Room!;
        var peer = MakePeer();
        mgr.TryJoin(room.Id, peer);
        var originalToken = peer.ResumeToken!;

        var expired = false;
        mgr.BeginPeerGrace(room.Id, peer.PeerId, () => { expired = true; return Task.CompletedTask; });
        peer.IsConnected.Should().BeFalse("BeginPeerGrace flips the flag so broadcasts can reflect the state");

        // Resume before the grace timer fires.
        var outcome = mgr.TryResume(room.Id, originalToken);

        outcome.Status.Should().Be(ResumeOutcomeStatus.Success);
        outcome.Peer.Should().BeSameAs(peer);
        outcome.NewResumeToken.Should().NotBeNullOrEmpty().And.NotBe(originalToken,
            "tokens rotate on each successful resume so the previous one can't be replayed");
        peer.IsConnected.Should().BeTrue();
        peer.ResumeToken.Should().Be(outcome.NewResumeToken);

        // Sleep past the grace window and assert the expiry callback never ran.
        await Task.Delay(TimeSpan.FromSeconds(6));
        expired.Should().BeFalse("the resume atomically cancelled the grace timer before it could fire");
    }

    [Fact]
    public async Task Grace_expiry_invokes_the_callback_when_no_resume_arrives()
    {
        var mgr = CreateManager(graceWindow: TimeSpan.FromMilliseconds(250));
        var room = mgr.CreateRoom().Room!;
        var peer = MakePeer();
        mgr.TryJoin(room.Id, peer);

        var expired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        mgr.BeginPeerGrace(room.Id, peer.PeerId, () => { expired.TrySetResult(true); return Task.CompletedTask; });

        var ran = await expired.Task.WaitAsync(TimeSpan.FromSeconds(3));
        ran.Should().BeTrue();
    }

    [Fact]
    public async Task TryResume_after_expiry_returns_Expired_status()
    {
        var mgr = CreateManager(graceWindow: TimeSpan.FromMilliseconds(200));
        var room = mgr.CreateRoom().Room!;
        var peer = MakePeer();
        mgr.TryJoin(room.Id, peer);
        var token = peer.ResumeToken!;

        var expired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        mgr.BeginPeerGrace(room.Id, peer.PeerId, () =>
        {
            // Grace-expiry callback: in production this is LeaveCurrentRoom,
            // which hard-removes the peer. Stub it so the test can race against
            // TryResume without actually removing the peer from the room.
            expired.TrySetResult(true);
            return Task.CompletedTask;
        });

        await expired.Task.WaitAsync(TimeSpan.FromSeconds(3));

        // Now that the timer has fired, TryResume must reject.
        var outcome = mgr.TryResume(room.Id, token);
        outcome.Status.Should().Be(ResumeOutcomeStatus.Expired);
    }

    [Fact]
    public void TryResume_with_unknown_token_returns_TokenUnknown_status()
    {
        var mgr = CreateManager();
        var room = mgr.CreateRoom().Room!;
        var peer = MakePeer();
        mgr.TryJoin(room.Id, peer);

        var outcome = mgr.TryResume(room.Id, "0123456789ABCDEF");
        outcome.Status.Should().Be(ResumeOutcomeStatus.TokenUnknown);
    }

    [Fact]
    public void TryResume_for_missing_room_returns_RoomGone_status()
    {
        var mgr = CreateManager();
        var outcome = mgr.TryResume("ZZ9999", "deadbeef");
        outcome.Status.Should().Be(ResumeOutcomeStatus.RoomGone);
    }

    [Fact]
    public void TryResume_consumes_the_token_so_a_second_resume_with_the_same_token_fails()
    {
        var mgr = CreateManager(graceWindow: TimeSpan.FromSeconds(5));
        var room = mgr.CreateRoom().Room!;
        var peer = MakePeer();
        mgr.TryJoin(room.Id, peer);
        var originalToken = peer.ResumeToken!;

        mgr.BeginPeerGrace(room.Id, peer.PeerId, () => Task.CompletedTask);
        var first = mgr.TryResume(room.Id, originalToken);
        first.Status.Should().Be(ResumeOutcomeStatus.Success);

        // Replay attack: resume again with the stale token.
        var second = mgr.TryResume(room.Id, originalToken);
        second.Status.Should().Be(ResumeOutcomeStatus.TokenUnknown,
            "the original token was rotated — replaying it must not succeed");
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value) => CurrentValue = value;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
