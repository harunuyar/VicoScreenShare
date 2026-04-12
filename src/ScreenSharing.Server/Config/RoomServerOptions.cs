namespace ScreenSharing.Server.Config;

public sealed class RoomServerOptions
{
    /// <summary>Maximum number of peers allowed in a single room.</summary>
    public int MaxRoomCapacity { get; set; } = 16;

    /// <summary>Global cap on concurrent rooms (DoS protection).</summary>
    public int MaxTotalRooms { get; set; } = 500;

    /// <summary>Server-side ping interval for heartbeat.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Drop the connection if no pong arrives within this window.</summary>
    public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
