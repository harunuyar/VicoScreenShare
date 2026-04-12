using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenSharing.Protocol;

/// <summary>
/// Wire envelope for every signaling message. `Type` is the discriminator (see <see cref="MessageType"/>),
/// `CorrelationId` optionally pairs requests with responses, and `Payload` holds the type-specific body.
/// </summary>
public sealed record MessageEnvelope(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("correlationId")] string? CorrelationId,
    [property: JsonPropertyName("payload")] JsonElement Payload);
