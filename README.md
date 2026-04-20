# VicoScreenShare

Self-hosted, low-latency screen sharing for Windows. Rooms with multiple concurrent publishers, viewer-side fan-out via an SFU, user-controlled bitrate and resolution, no paid tier.

Built for the case Discord can't cover: pair programming where the text needs to stay readable at 1440p, LAN game nights where 8 Mbps turns fast motion into pixel soup, film or design reviews where colors need to survive the pipe. You run the server, you set the numbers, nobody rate-limits you.

> Status: pre-1.0. Working day-to-day but not yet battle-tested in wide deployments. Issues and pull requests welcome.

---

## Why another one

Short version — the gap looked like this:

- **Discord** caps free screen share around 1080p60 / 8 Mbps; Nitro lifts it to 4K60 but you're still renting. Multi-publisher rooms exist but the pipe is theirs.
- **Jitsi / BigBlueButton / similar** are video-conferencing first; screen share is a secondary track with conferencing-tier bitrate and resolution.
- **RustDesk / VNC / RDP** are remote-control tools. One viewer, one host, no room model.
- **Moonlight/Sunshine** are game-streaming tools. Host-to-single-client, not a room of viewers watching multiple publishers.

VicoScreenShare is screen-sharing-first, room-model, self-hosted. The server is ASP.NET Core / Linux-friendly; the client is Windows (WGC + D3D11 + Media Foundation).

## What's in the box

**Rooms and layouts**

- Multiple concurrent publishers per room; every viewer sees every publisher at once.
- Grid layout (uniform N×M) or Focus layout (one big tile + horizontal strip). Click to focus, double-click to fullscreen, Esc unwinds.
- Per-tile pause / stall detection: no frames for 2s → "Paused" badge on the last frame, after 7s → back to "Waiting."
- Floating, draggable, collapsible self-preview so you can see what you're sending.

**Video quality**

- Presets from 720p30 @ 4 Mbps up to 2160p60 @ 40 Mbps — user controls both dimensions and bitrate, no paywall gates.
- Hardware encoder auto-select: async NVENC / Quick Sync / AMF, with software H.264 and VP8 as fallbacks. VP8 is always available for viewers that lack hardware H.264.
- GPU capture end-to-end: Windows Graphics Capture → D3D11 shared textures → hardware encoder → RTP. No PCIe round-trip on the hot path.
- Optional Lanczos3 compute-shader downscaler for sessions where text legibility matters (code, documents). Bilinear is the default.
- Configurable keyframe interval (0.5–10s) and opt-in cyclic intra-refresh — spreads intra-macroblocks across frames instead of firing 250 Mbit keyframe bursts, trades ~1s initial convergence for no periodic bitrate spikes.

**Network**

- Selective Forwarding Unit (SFU) on the server: one `SfuPeer` per publisher upstream, one `SfuSubscriberPeer` per (viewer, publisher) pair downstream. Server never transcodes — it forwards. One publisher feeds N viewers at the cost of one encode.
- Loss-based adaptive bitrate on the publisher, driven by RTCP Receiver Reports. Three zones (back off > 10% loss, probe up < 2%, hold otherwise) with a 1-second cooldown; stops a weak link from repeatedly firing keyframe bursts that make things worse.
- Operator-configured ICE servers (STUN / TURN) advertised per-room, or an open fallback to public STUN for testing.
- Optional access password gating (`/healthz` tells clients whether auth is required before they type in a room code).
- Session resume: a dropped WebSocket gets a 20s grace window to reconnect with the same peer ID, preserving room membership and viewer tiles with no SDP renegotiation.
- RFC 3550 RTP sequence-based loss accounting that counts reorders correctly (reordered packets don't inflate `loss%`).

**Performance & resilience**

- Dedicated decode worker per viewer stream, bounded queue with drop-oldest backpressure and per-tile drop counters.
- Drop-to-IDR recovery: on decoder-queue saturation, skip frames until the next keyframe arrives rather than feed garbage into the decoder.
- Codec and dimension changes mid-session rebuild the decoder cleanly without tearing down the peer connection.
- Stats overlay per tile: fps, bitrate, dropped frames, render fps, packet loss — visible while you use the app, no debugger required.

**Ops**

- Server version is stamped from `git rev-list --count HEAD` and returned on `GET /healthz` so you can confirm which build is live.
- Room and total-room capacity limits configurable via `appsettings.json`.
- Runs on any .NET 10 target; Linux-x64 binaries exercised on Azure VMs under systemd.

## Architecture

```
                        Room server
                     ┌──────────────┐
    publisher A ───▶ │ SfuPeer A ─┬─┼─▶ SfuSubscriberPeer → viewer X
                     │            ├─┼─▶ SfuSubscriberPeer → viewer Y
                     │            └─┼─▶ SfuSubscriberPeer → viewer Z
    publisher B ───▶ │ SfuPeer B ─┬─┼─▶ ...
                     └──────────────┘
                WebSocket signaling +
                RTP/RTCP via SIPSorcery
```

One PeerConnection per (publisher→server) and per (server→viewer, per-publisher). Every subscriber gets its own SSRC and its own RTCP feedback; adaptive bitrate on a publisher is driven by the worst-case loss across its subscribers.

Projects:

- `VicoScreenShare.Protocol` — shared signaling DTOs (`netstandard2.1`).
- `VicoScreenShare.Client` — platform-neutral signaling, WebRTC session management, codec abstractions, adaptive bitrate, RTP stats (`net10.0`).
- `VicoScreenShare.Client.Windows` — WGC capture, D3D11 scalers, Media Foundation H.264 encode/decode (`net10.0-windows`).
- `VicoScreenShare.Desktop.Wpf` — WPF MVVM frontend, D3DImage renderer, stats overlay.
- `VicoScreenShare.Server` — ASP.NET Core + SIPSorcery SFU.
- `tests/` — xUnit + FluentAssertions; `VicoScreenShare.MediaHarness` for manual capture / encode / decode without the UI.

## Quick start

Requires the .NET 10 SDK. Client needs Windows 11 22H2 or later (WGC border API).

Build and test:

```
dotnet build VicoScreenShare.slnx
dotnet test tests/VicoScreenShare.Tests/VicoScreenShare.Tests.csproj
```

Run the server locally (binds to `http://localhost:5000` by default):

```
dotnet run --project src/VicoScreenShare.Server
```

Run the Windows client (Visual Studio: set `VicoScreenShare.Desktop.Wpf` as startup, or):

```
dotnet run --project src/VicoScreenShare.Desktop.Wpf
```

In the client, add a server (Home view → "Add server"), enter `ws://localhost:5000/ws` and a room code, join. A second client on the same LAN (or the same machine with a different peer name) can join the same room and see the first one's stream.

## Server configuration

All tunables under `Rooms` in `appsettings.json`; anything unset uses the defaults in `RoomServerOptions`.

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

- **`AccessPassword`** — optional shared secret clients present in `ClientHello.AccessToken`. `null` or empty = open server.
- **`IceServers`** — STUN/TURN servers shipped to every client in `RoomJoined.IceServers` and used by the server's own SFU peer connections. Empty list falls back to Google's public STUN (testing only — production should configure its own STUN and a TURN for viewers behind symmetric NATs).
- **`PeerGracePeriod`** — how long a disconnected peer's room slot is kept alive for `ResumeSession`. After this window the slot is evicted.

## Deployment

The production path is Azure VM + systemd, published over SSH. A `deploy-screenshare.ps1` script (kept alongside your SSH key, not committed) publishes `linux-x64`, SCPs to `/tmp/screenshare/`, and swaps into `/opt/screenshare/app/` under a systemd unit named `screenshare`. Sketch:

```powershell
dotnet publish src/VicoScreenShare.Server -c Release -r linux-x64 --self-contained
scp -r publish/ azureuser@vm:/tmp/screenshare/
ssh azureuser@vm 'sudo systemctl stop screenshare && \
                  sudo rsync -a --delete /tmp/screenshare/ /opt/screenshare/app/ && \
                  sudo systemctl start screenshare'
```

The server prints its version (git rev count) on `GET /healthz`, so you can verify the deploy landed.

You'll also want `sysctl -w net.core.rmem_max=67108864 net.core.wmem_max=67108864` on a Linux host that carries many publishers — the kernel's default UDP buffer is small enough that bursty keyframe traffic can drop under load.

## Compatibility

- **Client**: Windows 11 22H2+. Uses `Windows.Graphics.Capture` + D3D11; no WinUI dependency.
- **Server**: any .NET 10 target. Tested on Linux (Ubuntu 22.04 / Azure F2s v2).
- **GPUs**: any GPU with a DXVA H.264 decoder works for viewing. For publishing, hardware H.264 encode is auto-selected from NVENC (NVIDIA), Quick Sync (Intel), or AMF (AMD); software H.264 and VP8 are fallbacks.
- **Mobile**: not planned. Browser client is a long-term maybe.

## Contributing

Issues and pull requests welcome. A few things that help:

- File an issue before large changes — I'd rather align up front than ask for rewrites.
- Keep changes behind meaningful names and braces on every control-flow body; see `.editorconfig` for style.
- Prefer tests for protocol / signaling / stats code; manual verification is OK for UI and codec paths (document what you tested).
- Don't introduce dependencies on closed-source SDKs.

Areas where help would move the needle:

- Browser viewer (WebRTC + a WASM decoder or native VP8).
- Linux and macOS capture sources.
- AV1 encode / decode paths (stubs exist).
- Better TURN defaults and a guide for deploying your own coturn.

## License

[GNU AGPLv3](LICENSE) or later. Copyleft with the "network service" clause — anyone running a modified version as a network service must publish their source under the same license. Third-party dependencies retain their own licenses (SIPSorcery BSD-3, CommunityToolkit.Mvvm / Vortice / WPF-UI MIT, vendored Vp8.Net BSD); none are GPL-incompatible.
