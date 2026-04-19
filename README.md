# VicoScreenShare

Self-hosted screen sharing. One publisher uploads an encoded video stream to the server; the server fans it out unchanged (SFU pattern) to every viewer in the room. Goal: user-controlled bitrate / resolution / codec without a paid tier, multiple concurrent streams per room.

## Stack

- **Shared protocol** (`VicoScreenShare.Protocol`): WebSocket signaling DTOs, `netstandard2.1`.
- **Client core** (`VicoScreenShare.Client`): `net10.0`, platform-neutral. Signaling, WebRTC session management, codec abstractions, adaptive bitrate control, RTP stats.
- **Windows capture + codecs** (`VicoScreenShare.Client.Windows`): `net10.0-windows`. WGC (`Windows.Graphics.Capture`) capture source, D3D11 scaler + Lanczos3 compute shader, Media Foundation H.264 encoder/decoder (NVENC / QSV / AMF auto-picked).
- **Windows desktop shell** (`VicoScreenShare.Desktop.Wpf`): WPF MVVM frontend. Room/home/settings views, stats overlay, D3DImage renderer.
- **Server** (`VicoScreenShare.Server`): ASP.NET Core + SIPSorcery. WebSocket signaling, SFU, adaptive-bitrate feedback aggregation, ICE config distribution. Deployed to Linux (`linux-x64`).
- **Tests** (`tests/VicoScreenShare.Tests`, `tests/VicoScreenShare.MediaHarness`): xUnit + FluentAssertions.

## Build and run

```
dotnet build VicoScreenShare.slnx
dotnet test tests/VicoScreenShare.Tests/VicoScreenShare.Tests.csproj
```

Run the server locally:

```
dotnet run --project src/VicoScreenShare.Server
```

Run the Windows client from Visual Studio (set `VicoScreenShare.Desktop.Wpf` as the startup project) or:

```
dotnet run --project src/VicoScreenShare.Desktop.Wpf
```

Requires .NET 10 SDK. Windows 11 22H2+ for the desktop client (WGC border-required API). Server runs on any .NET 10 target.

## Server configuration

All tunables live under `Rooms` in `appsettings.json`. Unset fields use the defaults in `RoomServerOptions`.

```jsonc
{
  "Rooms": {
    "MaxRoomCapacity": 16,
    "MaxTotalRooms": 500,
    "HeartbeatInterval": "00:00:15",
    "HeartbeatTimeout": "00:00:30",
    "PeerGracePeriod": "00:00:20",
    "AccessPassword": null,
    "IceServers": [
      { "Urls": [ "stun:stun.cloudflare.com:3478" ] },
      {
        "Urls": [ "turn:turn.example.com:3478?transport=udp" ],
        "Username": "vicoshare",
        "Credential": "replace-with-shared-secret"
      }
    ]
  }
}
```

- **`AccessPassword`**: optional shared secret clients present in `ClientHello.AccessToken`. `null`/empty = open server.
- **`IceServers`**: STUN/TURN servers shipped to every client in `RoomJoined.IceServers` and used by the server's own SFU peer connections. Empty list falls back to Google's public STUN (`stun:stun.l.google.com:19302`) — fine for testing with friends, but production should configure its own STUN + a TURN for viewers behind strict NATs.
- **`PeerGracePeriod`**: how long a disconnected peer's room slot is kept alive for reconnection via `ResumeSession`. After this window the slot is evicted.

## Deployment

Azure VM + systemd, over SSH. The `deploy-screenshare.ps1` script (not in this repo — keep beside your SSH key) publishes the server for `linux-x64`, scps to `/tmp/screenshare/`, and swaps into `/opt/screenshare/app/` under a systemd unit named `screenshare`. Version is stamped from `git rev-list --count HEAD` and returned on `GET /healthz` so you can confirm which build is live.

## Testing

- Unit tests cover protocol round-trips, adaptive bitrate controller, RFC-3550 RTP sequence accounting, settings persistence, signaling lifecycle, and SFU routing. `dotnet test` from the solution root runs the full suite headless.
- `VicoScreenShare.MediaHarness` is a manual testbed for capture + encode + decode flows without the full WPF UI.

## License

[GNU AGPLv3](LICENSE) or later. Copyleft with the "network service" clause — anyone running a modified version as a network service must publish their source under the same license. Third-party dependencies retain their own licenses (SIPSorcery BSD-3, CommunityToolkit.Mvvm / Vortice / WPF-UI MIT, vendored Vp8.Net BSD); none are GPL-incompatible.
