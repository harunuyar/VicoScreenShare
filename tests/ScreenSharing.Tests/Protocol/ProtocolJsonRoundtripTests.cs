using System.Text.Json;
using FluentAssertions;
using ScreenSharing.Protocol;
using ScreenSharing.Protocol.Messages;

namespace ScreenSharing.Tests.Protocol;

public class ProtocolJsonRoundtripTests
{
    [Fact]
    public void Envelope_uses_camelCase_field_names()
    {
        var payload = JsonSerializer.SerializeToElement(new JoinRoom("ABC123"), ProtocolJson.Options);
        var envelope = new MessageEnvelope(MessageType.JoinRoom, "corr-1", payload);

        var json = JsonSerializer.Serialize(envelope, ProtocolJson.Options);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("type", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("correlationId", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("payload", out var payloadElement).Should().BeTrue();
        payloadElement.TryGetProperty("roomId", out _).Should().BeTrue();
    }

    [Fact]
    public void Roundtrip_preserves_all_messages()
    {
        var peerId = Guid.NewGuid();
        var original = new RoomJoined(
            RoomId: "ABC123",
            YourPeerId: peerId,
            Peers: new[]
            {
                new PeerInfo(peerId, "Alice", IsHost: true, IsStreaming: false),
                new PeerInfo(Guid.NewGuid(), "Bob", IsHost: false, IsStreaming: false),
            },
            IceServers: Array.Empty<IceServerConfig>());

        var payload = JsonSerializer.SerializeToElement(original, ProtocolJson.Options);
        var envelope = new MessageEnvelope(MessageType.RoomJoined, null, payload);
        var json = JsonSerializer.Serialize(envelope, ProtocolJson.Options);

        var reparsed = JsonSerializer.Deserialize<MessageEnvelope>(json, ProtocolJson.Options);
        reparsed.Should().NotBeNull();
        reparsed!.Type.Should().Be(MessageType.RoomJoined);
        var payloadOut = reparsed.Payload.Deserialize<RoomJoined>(ProtocolJson.Options);
        payloadOut.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void Error_code_serializes_as_camelCase_string()
    {
        var err = new Error(ErrorCode.RoomNotFound, "nope");
        var json = JsonSerializer.Serialize(err, ProtocolJson.Options);

        json.Should().Contain("\"code\":\"roomNotFound\"");
    }
}
