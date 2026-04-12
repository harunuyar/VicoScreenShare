# ScreenSharing

Self-hosted Discord-alternative screen sharing. Streamer uploads one encoded video stream to a server, the server fans it out to room members without re-encoding (SFU pattern). Goal: user-controlled bitrate and resolution without a paid tier.

## Status

Phase 0 — solution skeleton. Nothing streams yet. See `C:\Users\harun\.claude\plans\whimsical-beaming-yao.md` for the phased delivery plan.

## Stack

- **Client**: Avalonia (.NET 10), cross-platform UI. Windows-only capture library.
- **Server**: ASP.NET Core + SIPSorcery as a WebRTC SFU.
- **Signaling**: Custom WebSocket + JSON.
- **Video**: H.264 via Media Foundation (auto-picks NVENC / AMF / QSV).
- **Audio**: WASAPI loopback for screen share, per-process loopback for application share.
- **NAT**: coturn on the same VPS.

## Project layout

```
src/
├── ScreenSharing.Protocol/          # Shared signaling DTOs
├── ScreenSharing.Client/            # Avalonia app
├── ScreenSharing.Client.Windows/    # Windows capture + encode + audio
└── ScreenSharing.Server/            # ASP.NET Core SFU

docker/server/                       # Dockerfile, compose, coturn config
```

## Build

```
dotnet build
dotnet run --project src/ScreenSharing.Server
dotnet run --project src/ScreenSharing.Client
```

Requires .NET 10 SDK and Windows 10 19041+ for the Windows capture library.
