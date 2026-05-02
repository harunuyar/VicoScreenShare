# TODO

Things worth doing but not yet. Ordered roughly by impact.

## Share modes

### Readability mode vs. motion mode
Offer two explicit share modes. Readability mode is tuned end-to-end for sharp text (pair programming, docs, UI work) — favors higher quality per pixel, sharper scaling, bit allocation that protects edges, lower frame rate if needed. Motion mode is tuned for smooth video (games, video playback) — favors frame rate and temporal smoothness, accepts softer edges. Each mode should tune the full pipeline (pre-processing, scaler, encoder preset, AQ balance, keyframe strategy, buffer depth) as a coherent set, not just flip one knob.

### Resolution and bitrate guardrails
Users can currently pick a resolution and a bitrate independently. Bad combinations (high resolution, low bitrate) produce blurry output that looks like the app is broken. Warn in the UI, or auto-correct to a pairing that's actually watchable.

## Audio

## Encoder quality

### Raise quality-vs-speed preset
NVENC SDK path currently uses preset P4 (`NV_ENC_PRESET_P4_GUID`, mid). P5 / P6 trade encode time for visibly better quality at the same bitrate; P4 was picked for safety but the headroom on a 4070-class card is large. Push higher, measure encode-time on target hardware via `bench-encode --backend nvenc`, keep the new preset if quality improves without blowing the real-time budget. The "Readability mode" entry above is the natural place to gate this.

## Loss resilience

### NACK
We currently recover from packet loss by asking for a full keyframe (RTCP PLI → forced IDR, with a stale-pixel probe and an output-stagnation watchdog backing it up). That costs a huge burst and often causes more loss. Add selective retransmission so a single lost packet can be recovered without a keyframe.

Prerequisite: a real packet-level jitter buffer (entry below). The custom AV1 RTP depacketizer reassembles in arrival order and can't accept late inserts, so RTX packets (RFC 4588, separate PT) need a reorder buffer that sits between the network and the depacketizer. Two attempts on this branch shipped same-SSRC retransmits and then RFC 4588 RTX, both reverted: same-SSRC was silently dropped by SRTP anti-replay, and RTX without a reorder buffer fed late-timestamped packets straight into the AV1 reassembler and crashed it.

### FEC
Add forward error correction so brief losses heal without any round trip.

## Congestion awareness

### Per-subscriber server-side pacing
Server forwards RTP byte-for-byte through `SfuPeer.SendForwardedRtp` → `_pc.SendRtpRaw`, so a paced burst from the publisher gets re-bursted on each viewer's egress. Add a `PacingRtpSender` per `SfuSubscriberPeer` (the class is already codec-agnostic — move it from `VicoScreenShare.Client/Media/` into a shared assembly so the server can reference it). Each subscriber's pacer rate is configured per-viewer, so a fast viewer doesn't eat latency for a slow one. Keep the client-side pacer too — it's the right layer when the *publisher's uplink* is the bottleneck (mobile, hotel WiFi); server-side handles every viewer's downlink. Set each layer's rate so it kicks in only when its leg is the actual bottleneck and the latencies don't stack on healthy paths.

### TWCC + GCC bandwidth estimation (the right thing)
Adaptive bitrate today only reacts once loss is already happening (`LossBasedBitrateController` reads RTCP-RR fraction-lost). That's the loss-based half of Google Congestion Control. The delay-based half watches per-packet arrival timing via TWCC (Transport-Wide Congestion Control) and detects queueing buildup *before* loss starts — which is what every modern WebRTC stack (libwebrtc / browsers, Pion, Mediasoup, LiveKit) uses.

What we have:
- `LossBasedBitrateController` (loss-based half — already shipped on the publisher).
- `TransportWideCCExtension` is auto-applied to outbound RTP packets by SIPSorcery's `MediaStream.SendRtpRaw`, so the wire-level seq numbers are present on every paced packet.

What's missing:
- **Receiver-side TWCC feedback emission**: parse incoming TWCC seq numbers, accumulate per-packet arrival timestamps, emit RTCP TWCC feedback messages on a 50 ms cadence (RFC draft `draft-holmer-rmcat-transport-wide-cc-extensions`).
- **Sender-side TWCC feedback parsing + delay-based estimator**: GCC's arrival-time-filter / overuse-detector / rate-controller pipeline. Reference implementations: libwebrtc's `goog_cc_factory.cc` (canonical, C++), Pion's `pion/interceptor` (Go, more readable).

The estimator is the same algorithm in three places — only the input source differs:
- **Publisher → SFU path**: publisher-side estimator (sender role) consumes TWCC feedback from the SFU. Drives the publisher's adaptive bitrate / unified rate-res-fps controller.
- **Each subscriber's downlink**: server-side estimator (sender role) consumes TWCC feedback from each viewer's `SubscriberSession`. Drives that viewer's per-subscriber server pacer (separate entry above).
- **Subscriber session**: receiver-side TWCC feedback emitter that *produces* the data the two sender-side estimators consume. Emits on the inbound path, not just at the publisher.

So the work is: build the algorithm once, wire the receiver-side emitter on every RTP-receiving path (publisher's RTCP-feedback handler from server, each viewer's `SubscriberSession`), and instantiate the sender-side estimator at every RTP-sending path (publisher → server, server → each subscriber). One implementation, three call sites.

End game: every paced layer (client pacer + per-subscriber server pacer) drives its rate from a TWCC-backed bandwidth estimate per RTP path. Manual "My downlink (Mbps)" Settings hint can ship as a stopgap until the estimator lands; REMB (older, simpler RTCP-feedback message — single bps number, no per-packet bookkeeping) is a defensible middle stop. Multi-week work; gate on whether the manual-hint stopgap actually proves insufficient in real use first.

### Unified rate / resolution / fps controller (depends on TWCC + GCC)
Once a real bandwidth estimate exists, *all three* current adaptation knobs collapse into one consumer. The libwebrtc shape:
1. Bandwidth estimator (loss + delay-based via TWCC + GCC) produces a single `target_send_bps`.
2. An adaptation-policy layer maps `target_send_bps` → `(bitrate, resolution, fps)`. Threshold-driven with hysteresis so we don't bounce between 1080p30 and 720p60 every couple seconds. Drop fps first, then resolution, then keep starving bitrate as the last resort.
3. Encoder applies the tuple — bitrate is hot-reconfigurable today (`streamer.UpdateBitrate`), resolution/fps require encoder restart, which is expensive and is the reason hysteresis matters.

Two concrete fixes this unblocks:
- **Faster recovery**: today's `LossBasedBitrateController` climbs back to target slowly because it only probes up when loss has been zero for a sustained window — afraid of re-triggering loss. A delay-based estimator sees the queue draining and probes proactively, fixing the "minute to recover" issue (separate TODO entry above).
- **Resolution + fps adaptation**: today we starve the same pixel count with fewer bits, which produces blurry output. With the policy layer above, sustained-poor links step the pixel count and frame rate down to match the available rate.

Land this AFTER the TWCC + GCC estimator — without a real bandwidth signal, any rate/res/fps controller is guessing. With it, this is a few hundred lines of policy + threshold tuning.

## Multi-viewer scalability

### Simulcast
Publisher currently sends a single encoded stream and the SFU forwards the same bytes to every viewer. One viewer on a bad link forces quality down for everyone. Encode multiple quality layers at once and let the SFU forward the right layer to each viewer based on their downstream capacity.

## Client features

### In-room text chat
No way to share a link, paste a snippet, or leave a short message without switching to another app. Add a room-scoped text chat.

### Local session recording
Users have no way to save a session to disk. Add local recording of what's being watched (and optionally what's being shared).

### Cursor controls
No option to hide the cursor, nor to visually highlight it for presentations. Add toggles for both.

### Encrypt saved-server credentials at rest
Saved server passwords live in plaintext under the user's profile. Protect them with an OS-level key-protection facility so casual filesystem snooping doesn't expose them.

## Server operations

### Observability
No structured logs, no metrics endpoint, no room / session / bandwidth counters visible to an operator. Expose metrics in a format suitable for Prometheus or OpenTelemetry.

### Signaling rate limits
Nothing caps how many messages a single WebSocket connection can send or how quickly a client can create rooms. Add per-connection limits so one malformed or hostile client can't overwhelm the server.

## Platform reach

### Browser viewer
Watching a room requires installing the native Windows client. A WebRTC-based browser viewer would let anyone with a link join as a viewer.

### Linux and macOS capture sources
Publishing requires Windows because the capture pipeline is tied to Windows Graphics Capture. Abstract the capture source and add implementations for X11/Wayland on Linux and ScreenCaptureKit on macOS so those platforms can publish too.

## Existing features to verify or fix

### Verify intra-refresh actually takes effect
Intra-refresh is wired through the encoder's codec-API attributes, but the underlying hardware / driver support is patchy and failures are silent. Confirm by inspecting real output (NAL unit types) that intra macroblocks are actually being emitted across frames; if not, either make it work or remove the option.

### Faster adaptive-bitrate recovery
Current controller takes on the order of a minute to climb from the floor back to target after a transient loss event. Tune the probe-up rate and smooth the loss input so recoveries feel responsive.

### Real packet-level jitter buffer
The receive-side buffer today paces *decoded* frames for painting. It doesn't absorb packet-level jitter or reorder RTP packets before decode, which is what "jitter buffer" normally means. Build one that sits between the network receive path and the decoder.

## Decode pipeline

### Parallel multi-viewer decode
With three or more concurrent streams on one viewer, the decoders share one GPU device and serialize on it, capping aggregate throughput. Run each decoder on its own private device and hand results to the renderer without any shared-state handoff that can race under WPF's layout / template churn. Single-viewer performance must not regress.

