using System;

namespace ScreenSharing.Protocol;

public sealed record PeerInfo(
    Guid PeerId,
    string DisplayName,
    bool IsHost,
    bool IsStreaming);
