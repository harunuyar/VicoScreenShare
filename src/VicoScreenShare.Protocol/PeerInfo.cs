namespace VicoScreenShare.Protocol;

using System;

public sealed record PeerInfo(
    Guid PeerId,
    string DisplayName,
    bool IsStreaming,
    bool IsConnected = true);
