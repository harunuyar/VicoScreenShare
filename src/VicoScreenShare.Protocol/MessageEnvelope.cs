using System.Text.Json;

namespace VicoScreenShare.Protocol;

/// <summary>
/// Wire envelope for every signaling message. <see cref="Type"/> is the discriminator
/// (see <see cref="MessageType"/>), <see cref="CorrelationId"/> optionally pairs requests
/// with responses, and <see cref="Payload"/> holds the type-specific body.
/// All serialization must go through <see cref="ProtocolJson.Options"/>.
/// </summary>
public sealed record MessageEnvelope(
    string Type,
    string? CorrelationId,
    JsonElement Payload);
