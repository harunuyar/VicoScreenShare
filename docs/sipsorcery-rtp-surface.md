# SIPSorcery 10.0.3 — RTP feedback / NACK / RTX / TWCC surface

Audit completed on 2026-04-19. Findings drive the Phase 1–4 implementation of the
"keyframe burst loss on constrained networks" plan (plan file:
`C:\Users\harun\.claude\plans\this-is-a-self-playful-hoare.md`).

## What SIPSorcery already gives us

### 1. SDP feedback-profile toggle

`RTCConfiguration.X_UseRtpFeedbackProfile` (bool).

> Optional. If set to true the feedback profile set in the SDP offers and
> answers will be `UDP/TLS/RTP/SAVPF` instead of `UDP/TLS/RTP/SAVP`.

This is the SDP-level gate. We currently construct `RTCConfiguration` with only
`iceServers` populated (four sites: `SfuPeer`, `SfuSubscriberPeer`,
`SubscriberSession`, `WebRtcSession`), so the feedback profile is off and RTCP
feedback cannot be negotiated at all. Flipping this to `true` is a prerequisite
for every other RTP feedback mechanism below.

### 2. RTCP feedback primitives

- `SIPSorcery.Net.RTCPFeedback` — generic feedback packet, ctors take an RTP
  feedback type (`NACK`, `TMMBR`, `TWCC`, …) or a payload-specific type
  (`PLI`, `FIR`, …).
- `SIPSorcery.Net.RTCPTWCCFeedback` — full parser/serializer for RFC 8888
  transport-wide feedback, with `PacketStatuses` + `ReferenceTime`.
- `SIPSorcery.Net.TransportWideCCExtension` — the RTP header extension that
  carries the TWCC sequence number on outgoing media packets.

**Enum values available:**

```
RTCPFeedbackTypesEnum (RFC 4585 transport-layer):
  unassigned, NACK, TMMBR, TMMBN, RTCP_SR_REQ, RAMS, TLLEI, RTCP_ECN_FB,
  PAUSE_RESUME, DBI, TWCC

PSFBFeedbackTypesEnum (payload-specific):
  unassigned, PLI, SLI, RPSI, FIR, TSTR, TSTN, VBCM, PSLEI, ROI, LRR, AFB
```

### 3. RTCP sending APIs

Both live on `RTPSession` (and mirrored on `MediaStream`):

- `void SendRtcpFeedback(SDPMediaTypesEnum mediaType, RTCPFeedback feedback)`
- `void SendRtcpTWCCFeedback(SDPMediaTypesEnum mediaType, RTCPTWCCFeedback feedback)`

These are fire-and-forget — the library does the RTCP serialization and
transport. We build the feedback packet ourselves (e.g., a NACK covering
specific lost sequence numbers) and hand it over.

### 4. RTCP receive event

`RTPSession.OnReceiveReport(Action<IPEndPoint, SDPMediaTypesEnum, RTCPCompoundPacket>)`
fires on every inbound RTCP compound packet. An `RTCPCompoundPacket` contains
any mix of SR/RR/SDES/BYE/APP/**feedback** reports. We inspect
`FeedbackReports` (and payload-specific feedback) to detect NACKs/PLIs/TWCC
from the remote side.

**There is no dedicated `OnNackReceived` event.** We filter `OnReceiveReport`
ourselves.

### 5. Bitrate controller (!!)

`SIPSorcery.core.TWCCBitrateController.ProcessFeedback(RTCPTWCCFeedback)` —
this is a first-class Google-style congestion-control algorithm, fed the TWCC
feedback, producing a recommended send-bitrate. Paired with a
`BitrateUpdateEventArgs` type. Not wired into `RTPSession` by default; we'd
instantiate it in our send-side code, feed it the feedback from
`OnReceiveReport`, and apply its output to the encoder.

## What SIPSorcery does NOT give us

1. **Automatic NACK generation on the receive side.** Seq-gap detection and
   the "send a NACK" logic is our responsibility. We subscribe to
   `OnRtpPacketReceived`, track sequence numbers, and emit a NACK through
   `SendRtcpFeedback(SDPMediaTypesEnum.video, new RTCPFeedback(ssrc, media_ssrc, NACK))`
   when holes appear.
2. **No retransmission buffer on the send side.** Receiving a NACK via
   `OnReceiveReport` gives us the lost sequence numbers, but we must have
   retained the corresponding RTP packets ourselves to retransmit them.
3. **No RTX payload-type pairing.** SIPSorcery's `MediaStreamTrack` has no
   built-in RTX stream. Two options for retransmitting:
   - Resend the original RTP packet (same payload type, original seq number).
     Simpler; works with any remote that honors NACK but not RTX.
   - Negotiate an RTX payload type and wrap retransmits with an `apt=` fmtp.
     Cleaner per RFC 4588 but more code. Decision: start with option (a).
4. **No send pacer.** `SendRtpRaw` writes to the socket synchronously back-to-
   back. We wrap this call site in a pacer (implemented at the
   `CaptureStreamer → SendVideo` boundary or just above `SendRtpRaw`).
5. **No TWCC header extension injection.** `TransportWideCCExtension` exists
   but we have to populate it with a per-session ascending sequence number on
   each outgoing packet. Needed if we want the remote to generate TWCC
   feedback.

## Implementation shape this enables

**On the server → viewer path (`SfuSubscriberPeer`):**

- Turn on `X_UseRtpFeedbackProfile` so SDP has SAVPF.
- Attach a `headerExtensions` dictionary to the outgoing `MediaStreamTrack`
  that declares TWCC (or at minimum, signal NACK feedback support via the
  track's `sdpFmtpLine` / SDP munging path).
- Maintain a fixed-size ring buffer (default 128 packets) of the RTP payloads
  we sent, keyed by sequence number, in `SendForwardedRtp`.
- Subscribe to `OnReceiveReport`. Filter for NACK feedback. Look up the
  requested sequence numbers in the ring and re-send them via `SendRtpRaw`.

**On the viewer (`SubscriberSession`):**

- `X_UseRtpFeedbackProfile = true`.
- Subscribe to `OnRtpPacketReceived`. Track sequence numbers; on detected
  gap, build `RTCPFeedback(…, NACK)` and call `SendRtcpFeedback`. Fire NACKs
  on a short-debounced cadence (~20 ms) so a burst loss doesn't produce a
  storm of single-packet NACKs.

**On the publisher's upstream PC (`WebRtcSession`) and the server's receive
PC (`SfuPeer`):**

- Same pattern applied symmetrically so the publisher→server hop also has
  NACK+RTX.

**Send pacer (`CaptureStreamer`):**

- Internal `Channel<PacedFrame>` between the encoder callback and the
  `_onEncoded` invocation.
- Worker drains at `TargetBitrate × EnableSendPacer ? SendPacerBurstFactor : 1`
  bytes per sliding 100 ms window.

**TWCC adaptive bitrate (follow-up, after NACK+RTX is proven):**

- Inject `TransportWideCCExtension` on outgoing packets.
- Generate TWCC feedback on the viewer via `SendRtcpTWCCFeedback`.
- Feed received TWCC on the publisher side into `TWCCBitrateController` and
  dynamically adjust the encoder's `TargetBitrate` via the CODECAPI
  attributes already wired in `MediaFoundationH264Encoder`.

## Library swap considered and rejected

`MixedReality-WebRTC` wraps native libwebrtc and would give us NACK/RTX/TWCC
for free. Downsides: (1) the project is in maintenance mode, (2) native
dependency complicates the self-contained client publish, (3) migration
touches every `RTCPeerConnection` site plus the capture→encode→send
pipeline. Given SIPSorcery supplies the parsers + the bitrate controller and
only leaves the glue for us to write, staying on SIPSorcery is the smaller
project. Revisit only if we hit a wall Phase 2.

## Concrete TODOs for Phase 1 onward

1. Set `X_UseRtpFeedbackProfile = true` on all four `RTCConfiguration`
   construction sites (Phase 2, commit 1 — tiny, testable).
2. Stand up the loopback+loss test harness (Phase 1). Target: a xunit fact
   that runs publisher ↔ server ↔ viewer through an injected-loss shim and
   asserts time-to-first-frame.
3. Implement the NACK send side in `SubscriberSession` — seq-gap detector +
   debounced `SendRtcpFeedback`.
4. Implement the NACK receive + retransmit ring buffer on `SfuSubscriberPeer`.
5. Mirror (3) and (4) to the publisher-side upstream (`WebRtcSession` ↔
   `SfuPeer`).
6. Add the send pacer in `CaptureStreamer`.
7. Expose config knobs from the plan's Phase 4.
