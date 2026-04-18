namespace VicoScreenShare.Protocol;

public static class ProtocolVersion
{
    /// <summary>
    /// Bump whenever a breaking change is made to the wire format. Clients and servers must match.
    /// </summary>
    public const int Current = 1;
}
