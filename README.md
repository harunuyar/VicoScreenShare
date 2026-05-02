# VicoScreenShare

Self-hosted, low-latency screen sharing for Windows. Rooms with multiple concurrent publishers, viewer-side fan-out via an SFU, user-controlled bitrate and resolution, no paid tier.

Built for the case Discord can't cover: pair programming where the text needs to stay readable at 1440p, LAN game nights where 8 Mbps turns fast motion into pixel soup, film or design reviews where colors need to survive the pipe. You run the server, you set the numbers, nobody rate-limits you.

> Status: pre-1.0. Working day-to-day but not yet battle-tested in wide deployments. Issues and pull requests welcome.

**Download**: pre-built binaries (self-contained, no .NET install required) live on the [Releases](https://github.com/harunuyar/VicoScreenShare/releases) page — desktop client for Windows x64, server for Windows x64 and Linux x64.

---

## Why another one

Short version — the gap looked like this:

- **Discord** caps free screen share at 720p30; Nitro lifts the cap but you're still renting. Multi-publisher rooms exist but the pipe is theirs.
- **Jitsi / BigBlueButton / similar** are video-conferencing first; screen share is a secondary track with conferencing-tier bitrate and resolution.
- **RustDesk / VNC / RDP** are remote-control tools. One viewer, one host, no room model.
- **Moonlight / Sunshine** are game-streaming tools. Host-to-single-client, not a room of viewers watching multiple publishers.

VicoScreenShare is screen-sharing-first, room-model, self-hosted. The server is a plain ASP.NET Core app; the client is Windows (WGC + D3D11 + Media Foundation).

## What's in the box

**Rooms and layouts**

- Multiple concurrent publishers per room; every viewer sees every publisher at once.
- Grid layout (uniform N×M) or Focus layout (one big tile + horizontal strip). Click a tile to focus it, F11 to toggle fullscreen on the focused tile, Esc unwinds.
- Floating, draggable, collapsible self-preview so you can see what you're sending.

**Video quality**

- Codec-aware presets from 720p30 to 2160p60 — H.264 column at Twitch-aligned bitrates, AV1 column at ~60% of those for equivalent quality. User controls both dimensions and bitrate, no paywall gates.
- Hardware H.264 encode auto-selected from NVENC SDK (NVIDIA), Quick Sync (Intel), or AMF (AMD); software H.264 and VP8 are fallbacks.
- Hardware AV1 encode via NVENC SDK on RTX 40+ NVIDIA silicon, or via the platform AV1 encoder MFT on any GPU vendor that ships one (Intel Arc / Xe2, AMD RDNA 3+, NVIDIA's own MFT shim).
- Hardware decode via NVDEC (direct cuvid path) for both H.264 and AV1 on NVIDIA, with Media Foundation MFT fallback elsewhere. NVDEC handles 4K IDRs in single-digit ms; MFT typically spends 30-45 ms per IDR. User-selectable from Settings (Auto / Nvdec / Mft) for both codecs.
- GPU capture end-to-end: Windows Graphics Capture → D3D11 shared textures → hardware encoder → RTP. No PCIe round-trip on the hot path.
- Optional Lanczos3 compute-shader downscaler for sessions where text legibility matters (code, documents). Bilinear is the default.
- Configurable keyframe interval (0.5–10s) and opt-in cyclic intra-refresh — spreads intra-macroblocks across frames instead of firing 250 Mbit keyframe bursts, trades ~1s initial convergence for no periodic bitrate spikes.

**Network**

- Selective Forwarding Unit (SFU) on the server: one `SfuPeer` per publisher upstream, one `SfuSubscriberPeer` per (viewer, publisher) pair downstream. Server never transcodes — it forwards. One publisher feeds N viewers at the cost of one encode.
- Loss-based adaptive bitrate on the publisher, driven by RTCP Receiver Reports. Three zones (back off > 10% loss, probe up < 2%, hold otherwise) with a 1-second cooldown; stops a weak link from repeatedly firing keyframe bursts that make things worse.
- Send-side packet pacer (on by default at 2× target bitrate) smooths keyframe bursts so a 1 MB IDR doesn't overrun a viewer's downlink modem queue.
- Codec-agnostic keyframe recovery: RTCP PLI from receiver → forced IDR at encoder, with a receiver-side output-stagnation watchdog and a renderer-side stale-pixel probe (250 ms detect threshold) backing it up. A poisoned reference frame triggers a fresh IDR end-to-end in roughly the round-trip time + encode + decode latency, instead of freezing the stream.
- Operator-configured ICE servers (STUN / TURN) advertised per-room, or an open fallback to public STUN for testing.
- Optional access password gating (`/healthz` tells clients whether auth is required before they type in a room code).
- Session resume: a dropped WebSocket gets a 20s grace window to reconnect with the same peer ID, preserving room membership and viewer tiles with no SDP renegotiation.
- RFC 3550 RTP sequence-based loss accounting that counts reorders correctly (reordered packets don't inflate `loss%`).
- A/V sync via RTCP Sender Reports: audio and video latch on the same NTP-anchored clock so loopback audio plays in step with the rendered video.

**Performance & resilience**

- Dedicated decode worker per viewer stream, bounded queue with drop-oldest backpressure and per-tile drop counters.
- Drop-to-IDR recovery: on decoder-queue saturation, skip frames until the next keyframe arrives rather than feed garbage into the decoder.
- Codec and dimension changes mid-session rebuild the decoder cleanly without tearing down the peer connection.
- Stats overlay per tile: fps, bitrate, dropped frames, render fps, packet loss — visible while you use the app, no debugger required.
- Diagnostic log at `%TEMP%\screensharing\debug.log` on each client (multiple client processes on one machine append to the same file, lines tagged with `pid=N` for filtering). Captures settings snapshots at startup and at each share, plus codec / SDP / encoder / decoder events and any abnormality the pipeline notices. Send it alongside any bug report. Server-side diagnostics use ASP.NET Core's standard logging — read it from stdout / journalctl / your hosting environment's log capture.

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
- `VicoScreenShare.Client.Windows` — WGC capture, D3D11 scalers, NVENC SDK / Media Foundation H.264 + AV1 encoders, NVDEC / Media Foundation decoders (`net10.0-windows`).
- `VicoScreenShare.Desktop.Wpf` — WPF MVVM frontend, D3DImage renderer, stats overlay.
- `VicoScreenShare.Server` — ASP.NET Core + SIPSorcery SFU.
- `tests/` — xUnit + FluentAssertions; `VicoScreenShare.MediaHarness` for manual capture / encode / decode without the UI.

## Quick start

**Just want to use it**: grab the latest [release](https://github.com/harunuyar/VicoScreenShare/releases), unzip the desktop client, run `VicoScreenShare.exe`. Run the matching server zip on a Windows or Linux box. Self-contained — no .NET install required.

**Want to build from source**: requires the .NET 10 SDK. Client needs Windows 10 version 2004 (May 2020) or later.

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

## Running the server

The server is a standard ASP.NET Core app. Publish for your target:

```
dotnet publish src/VicoScreenShare.Server -c Release -r linux-x64 --self-contained
```

Run the output directly, behind any reverse proxy you like (nginx, Caddy, Traefik), as a systemd unit, in Docker, or on Kubernetes — whatever fits your environment. `GET /healthz` reports status and whether authentication is required.

## Compatibility

- **Client**: Windows 10 version 2004 or later. Uses `Windows.Graphics.Capture` + D3D11; no WinUI dependency. On pre–Windows 11 22H2 the yellow capture border stays visible and the capture frame rate can be DWM-paced below the display refresh; functionally everything else works.
- **Server**: any .NET 10 target (Linux, Windows, macOS).
- **GPUs**: any GPU with a DXVA H.264 decoder works for viewing. NVIDIA viewers get the NVDEC direct path for H.264 and AV1 (single-digit ms per IDR); other vendors fall back to the platform MFT decoder. For publishing, hardware H.264 encode is auto-selected from NVENC SDK (NVIDIA), Quick Sync (Intel), or AMF (AMD). AV1 encode goes through the NVENC SDK direct path on RTX 40+, or through any GPU vendor's AV1 encoder MFT (Intel Arc / Xe2, AMD RDNA 3+, NVIDIA's MFT shim) as a fallback. Software H.264 and VP8 are universal fallbacks.
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
- Better TURN defaults and a guide for deploying your own coturn.

## License

[GNU AGPLv3](LICENSE) or later. Copyleft with the "network service" clause — anyone running a modified version as a network service must publish their source under the same license. Third-party dependencies retain their own licenses (SIPSorcery BSD-3, CommunityToolkit.Mvvm / Vortice / WPF-UI MIT, vendored Vp8.Net BSD); none are GPL-incompatible.
