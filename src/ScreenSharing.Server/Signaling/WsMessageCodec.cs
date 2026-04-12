using System.Text.Json;
using ScreenSharing.Protocol;

namespace ScreenSharing.Server.Signaling;

/// <summary>
/// Thin helpers over <see cref="ProtocolJson.Options"/> for wrapping payloads in the
/// common <see cref="MessageEnvelope"/> and extracting them back. Every serializer on
/// the server must go through these helpers so the wire format is canonical.
/// </summary>
public static class WsMessageCodec
{
    public static string EncodeEnvelope<T>(string type, T payload, string? correlationId = null)
    {
        var element = JsonSerializer.SerializeToElement(payload, ProtocolJson.Options);
        var envelope = new MessageEnvelope(type, correlationId, element);
        return JsonSerializer.Serialize(envelope, ProtocolJson.Options);
    }

    public static MessageEnvelope DecodeEnvelope(string json) =>
        JsonSerializer.Deserialize<MessageEnvelope>(json, ProtocolJson.Options)
            ?? throw new InvalidOperationException("Envelope deserialized to null");

    public static T DecodePayload<T>(JsonElement payload)
    {
        var result = payload.Deserialize<T>(ProtocolJson.Options);
        if (result is null)
        {
            throw new InvalidOperationException($"Payload deserialized to null for {typeof(T).Name}");
        }
        return result;
    }
}
