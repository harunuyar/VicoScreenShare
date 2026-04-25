# TODO

Things worth doing but not yet. Ordered roughly by impact.

## Share modes

### Readability mode vs. motion mode
Offer two explicit share modes. Readability mode is tuned end-to-end for sharp text (pair programming, docs, UI work) — favors higher quality per pixel, sharper scaling, bit allocation that protects edges, lower frame rate if needed. Motion mode is tuned for smooth video (games, video playback) — favors frame rate and temporal smoothness, accepts softer edges. Each mode should tune the full pipeline (pre-processing, scaler, encoder preset, AQ balance, keyframe strategy, buffer depth) as a coherent set, not just flip one knob.

### Resolution and bitrate guardrails
Users can currently pick a resolution and a bitrate independently. Bad combinations (high resolution, low bitrate) produce blurry output that looks like the app is broken. Warn in the UI, or auto-correct to a pairing that's actually watchable.

## Audio

## Encoder backend

### Direct NVENC SDK path (bypass Media Foundation for NVIDIA)
On the most common hardware (NVIDIA GPUs) the `NVIDIA H.264 Encoder MFT` is the Media Foundation shim NVIDIA ships, and it silently no-ops several CODECAPI properties Microsoft's API contract says it should honor. Two harness scenarios proved it before they were removed as part of cleaning up the dead user-facing settings they were tied to:

- `bench-vbv` swept `VbvFraction` 0.10 → 2.00 (a 20× range). IDR keyframe sizes were identical at every value (ratio 1.00). `CODECAPI_AVEncCommonBufferSize` is a no-op on NVENC's MFT shim.
- `bench-intra-refresh` enabled cyclic intra-refresh on a fresh encoder vs a baseline. Both produced the same number of IDRs at the same cadence and the same peak frame size (e.g. 5 IDRs / 911 KB peak in both runs over 5 s). `CODECAPI_AVEncVideoIntraRefreshMode` and `CODECAPI_AVEncVideoIntraRefreshPeriod` are also no-ops on the MFT shim.

`TrySet` did not log a rejection in either case — the attribute Set call succeeds, the encoder simply doesn't apply it. Microsoft's software H.264 MFT and AMD/Intel MFTs may behave differently; never tested. The MFT shim is the broken layer and we cannot patch it from outside.

The fix is the same one OBS, ffmpeg, and vMix use: call `NvEncodeAPI64.dll` directly. Wrap `NvEncodeAPICreateInstance`, manage `NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS`, configure `NV_ENC_RC_PARAMS` (where `vbvBufferSize`, `vbvInitialDelay`, and `intraRefreshPeriod` actually take effect), shuttle BGRA / NV12 frames in via `NvEncMapInputResource`, and pull encoded bitstreams out of `NvEncLockBitstream`. Roughly 1500–2500 lines of P/Invoke + session lifecycle + per-frame buffer marshalling, gated behind a runtime probe so non-NVIDIA users keep the MFT path.

When this lands, re-add `bench-vbv` and `bench-intra-refresh` against the new NVENC encoder factory and wire the user-facing knobs back into Settings. Until then, NVENC users get neither knob, and the only available burst-prevention mechanism is the send-side packet pacer (`PacingRtpSender`, exposed in Settings as "Smooth keyframe sends").

## Encoder quality

### Adaptive quantization
Enable the encoder's spatial and temporal AQ so bits get redistributed from flat surfaces to detailed regions. This matches the shape of screen content (large flat areas punctuated by text) and is the single highest-ROI quality change for our bitrate range.

### Encoder lookahead
Let the encoder look a few frames ahead to make smarter rate-distortion decisions. Verify it cooperates with the low-latency path — if it adds meaningful latency, gate it behind readability mode where latency budget is softer.

### Raise quality-vs-speed preset
Current preset is tuned conservatively. Push higher, measure encode-time impact on target hardware, keep the new value if quality improves without blowing the real-time budget.

## Loss resilience

### NACK
We currently recover from packet loss by asking for a full keyframe, which costs a huge burst and often causes more loss. Add selective retransmission so a single lost packet can be recovered without a keyframe.

### FEC
Add forward error correction so brief losses heal without any round trip.

## Congestion awareness

### Proactive congestion control
Adaptive bitrate today only reacts once loss is already happening. Add an estimator that watches packet arrival timing so we can scale down before the link starts dropping.

### Resolution and frame-rate adaptation
Adaptive bitrate only changes bitrate. On sustained poor links we should step resolution and / or frame rate down too, not just starve the same pixel count.

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

