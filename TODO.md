# TODO

Things worth doing but not yet. Ordered roughly by impact.

## Share modes

### Readability mode vs. motion mode
Offer two explicit share modes. Readability mode is tuned end-to-end for sharp text (pair programming, docs, UI work) — favors higher quality per pixel, sharper scaling, bit allocation that protects edges, lower frame rate if needed. Motion mode is tuned for smooth video (games, video playback) — favors frame rate and temporal smoothness, accepts softer edges. Each mode should tune the full pipeline (pre-processing, scaler, encoder preset, AQ balance, keyframe strategy, buffer depth) as a coherent set, not just flip one knob.

### Resolution and bitrate guardrails
Users can currently pick a resolution and a bitrate independently. Bad combinations (high resolution, low bitrate) produce blurry output that looks like the app is broken. Warn in the UI, or auto-correct to a pairing that's actually watchable.

## Audio

## Send path

### Packet pacing
Encoder output is written to the socket back-to-back in tight bursts. Route it through a pacer that drains at a smooth rate so a keyframe doesn't flood the link and blow through router / Wi-Fi buffers.

### Tighter rate-control buffer
Encoder rate control is configured with a one-second VBV, which lets complex scenes and keyframes spike well above target. Shorten it so the encoder is forced to drop quality instead of burst.

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
