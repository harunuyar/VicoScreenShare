# VicoScreenShare

Self-hosted Discord-alternative screen sharing. Streamer uploads one encoded video stream to a server, the server fans it out to room members without re-encoding (SFU pattern). Goal: user-controlled bitrate and resolution without a paid tier.

## Status

Phase 0 -- solution skeleton. Nothing streams yet. Phased delivery plan is tracked outside the repo.

## Stack

- **UI shell** (`VicoScreenShare.Client`): Avalonia on .NET 10, platform-neutral class library.
- **Windows host** (`VicoScreenShare.Desktop.Windows`): `WinExe` entry point, Avalonia.Win32 backend, Windows-only capture backend wired in.
- **Windows capture** (`VicoScreenShare.Client.Windows`): Windows.Graphics.Capture + Media Foundation H.264 + WASAPI/process-loopback audio.
- **Server** (`VicoScreenShare.Server`): ASP.NET Core + SIPSorcery acting as a WebRTC SFU.
- **Protocol** (`VicoScreenShare.Protocol`): shared WebSocket signaling DTOs (netstandard2.1).
- **Signaling**: custom WebSocket + JSON.
- **Video**: H.264 via Media Foundation (auto-picks NVENC / AMF / QSV).
- **Audio**: WASAPI loopback for screen share, per-process loopback for application share.
- **NAT**: coturn on the same VPS.

## Project layout

```
src/
├── VicoScreenShare.Protocol/          # Shared signaling DTOs (netstandard2.1)
├── VicoScreenShare.Client/            # Avalonia UI class library (net10.0, platform-neutral)
├── VicoScreenShare.Client.Windows/    # Windows capture + encode + audio (net10.0-windows)
├── VicoScreenShare.Desktop.Windows/   # Windows desktop host, WinExe entry point
└── VicoScreenShare.Server/            # ASP.NET Core SFU

docker/server/                       # Dockerfile, compose, coturn config (future)
```

## Build

```
dotnet build
```

## Run

```
dotnet run --project src/VicoScreenShare.Server          # SFU + signaling
dotnet run --project src/VicoScreenShare.Desktop.Windows # Windows desktop client
```

Requires .NET 10 SDK and Windows 10 19041+ for the Windows desktop host and capture library. The server itself is OS-neutral.
