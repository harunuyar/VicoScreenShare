namespace VicoScreenShare.Client.Media;

using System;
using System.Net;
using VicoScreenShare.Client.Diagnostics;
using VicoScreenShare.Client.Media.Codecs;
using VicoScreenShare.Client.Platform;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

/// <summary>
/// Receiver-side counterpart to <see cref="CaptureStreamer"/>. Subscribes to an
/// <see cref="RTCPeerConnection"/>'s <c>OnVideoFrameReceived</c>, hands each
/// reassembled payload to an <see cref="IVideoDecoder"/>, and raises
/// <see cref="FrameArrived"/> with a <see cref="CaptureFrameData"/> that the
/// Phase 2 <c>WriteableBitmapRenderer</c> can render directly.
///
/// The concrete codec lives behind the <see cref="IVideoDecoderFactory"/>
/// passed to the constructor — default is VP8 via <see cref="VpxDecoderFactory"/>
/// so existing call sites keep compiling, and future codecs plug in without
/// touching this class.
/// </summary>
public sealed class StreamReceiver : ICaptureSource, IDisposable
{
    private readonly RTCPeerConnection _pc;
    private readonly IVideoDecoder _decoder;
    private readonly object _decodeLock = new();
    private DateTime _lastFrameUtc = DateTime.MinValue;
    private bool _attached;
    private bool _disposed;

    public StreamReceiver(RTCPeerConnection pc, string displayName = "remote")
        : this(pc, new VpxDecoderFactory(), displayName)
    {
    }

    public StreamReceiver(RTCPeerConnection pc, IVideoDecoderFactory decoderFactory, string displayName = "remote")
    {
        _pc = pc;
        DisplayName = displayName;
        _decoder = decoderFactory.CreateDecoder();
        // Opt the decoder into GPU-resident output. Decoders that don't
        // support it (VPX, and MF decoders with no shared D3D device) use
        // the default no-op setter; their frames still flow via the CPU
        // byte[] path. MF + shared device emits here, which saves the
        // per-frame BGRA readback + upload round-trip.
        _decoder.GpuOutputHandler = OnDecoderGpuFrame;
    }

    public string DisplayName { get; }

    public long FramesReceived { get; private set; }

    public long FramesDecoded { get; private set; }

    /// <summary>Cumulative encoded bytes received on the peer connection's
    /// video track since this receiver attached. The stats overlay divides
    /// the delta between two reads by elapsed time to get a bitrate.</summary>
    public long EncodedByteCount { get; private set; }

    /// <summary>Width of the most recently decoded frame, or 0 if nothing
    /// has decoded yet.</summary>
    public int LastWidth { get; private set; }

    /// <summary>Same for height.</summary>
    public int LastHeight { get; private set; }

    /// <summary>Codec tag for the decoder instance powering this receiver.</summary>
    public VideoCodec Codec => _decoder.Codec;

    public event FrameArrivedHandler? FrameArrived;

    /// <summary>
    /// Raised synchronously from the decoder thread when a GPU-capable
    /// decoder emits a BGRA <c>ID3D11Texture2D</c> on the shared device.
    /// The native pointer is valid ONLY for the duration of the call;
    /// subscribers must <c>CopyResource</c> (or otherwise consume) before
    /// returning. When this fires, <see cref="FrameArrived"/> does NOT
    /// fire for the same frame — the decoder already skipped the CPU
    /// readback. For decoders without GPU support (VPX, sysmem MF)
    /// frames continue to arrive on <see cref="FrameArrived"/> as before.
    /// </summary>
    public event TextureArrivedHandler? TextureArrived;

    /// <summary>Alias for <see cref="FrameArrived"/> that reads clearer from the receiver side.</summary>
    public event FrameArrivedHandler? FrameDecoded
    {
        add => FrameArrived += value;
        remove => FrameArrived -= value;
    }

    public event Action? Closed;

    public System.Threading.Tasks.Task StartAsync()
    {
        if (_attached || _disposed) return System.Threading.Tasks.Task.CompletedTask;
        _attached = true;
        _pc.OnVideoFrameReceived += OnVideoFrameReceived;
        _pc.onconnectionstatechange += OnConnectionStateChange;
        return System.Threading.Tasks.Task.CompletedTask;
    }

    public System.Threading.Tasks.Task StopAsync()
    {
        if (!_attached) return System.Threading.Tasks.Task.CompletedTask;
        _attached = false;
        _pc.OnVideoFrameReceived -= OnVideoFrameReceived;
        _pc.onconnectionstatechange -= OnConnectionStateChange;
        return System.Threading.Tasks.Task.CompletedTask;
    }

    public System.Threading.Tasks.ValueTask DisposeAsync()
    {
        Dispose();
        return System.Threading.Tasks.ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        // Stop new frames first, then take the decode lock so any in-flight
        // Decode call finishes before we free the native decoder state.
        // Without this pairing we crash the CLR with ExecutionEngineException
        // when a frame arrives concurrently with Dispose.
        try { StopAsync().GetAwaiter().GetResult(); } catch { }
        lock (_decodeLock)
        {
            if (_disposed) return;
            _disposed = true;
            try { _decoder.Dispose(); } catch { }
        }
    }

    private void OnConnectionStateChange(RTCPeerConnectionState state)
    {
        if (state is RTCPeerConnectionState.closed
                  or RTCPeerConnectionState.failed
                  or RTCPeerConnectionState.disconnected)
        {
            Closed?.Invoke();
        }
    }

    private void OnVideoFrameReceived(IPEndPoint remote, uint timestamp, byte[] encodedSample, VideoFormat format)
    {
        if (encodedSample is null || encodedSample.Length == 0) return;

        System.Collections.Generic.IReadOnlyList<DecodedVideoFrame> frames;
        var now = DateTime.UtcNow;
        var gap = _lastFrameUtc == DateTime.MinValue ? TimeSpan.Zero : now - _lastFrameUtc;
        _lastFrameUtc = now;
        if (gap > TimeSpan.FromSeconds(2))
        {
            DebugLog.Write($"[recv] incoming packet after {gap.TotalSeconds:F1}s gap — stream restarted");
        }

        // Convert the RTP 90 kHz clock to a TimeSpan and hand it to the
        // decoder. The decoder propagates this through MF SampleTime so
        // each DecodedVideoFrame.Timestamp is authoritative — not
        // something we reconstruct per frame here. When the decoder yields
        // multiple outputs in one call (buffered older frame plus a new
        // one) each output carries its OWN original timestamp.
        var rtpInputTs = TimeSpan.FromTicks((long)timestamp * TimeSpan.TicksPerMillisecond / 90);

        int gpuEmittedThisCall;
        lock (_decodeLock)
        {
            if (_disposed) return;
            FramesReceived++;
            EncodedByteCount += encodedSample.Length;
            _gpuEmittedThisCall = 0;
            try
            {
                frames = _decoder.Decode(encodedSample, rtpInputTs);
            }
            catch (Exception ex)
            {
                // Decoder error mid-stream (packet loss, malformed SPS, etc.)
                // should not tear down the receive path. Log and skip.
                DebugLog.Write($"[recv] decoder threw: {ex.Message}");
                return;
            }
            gpuEmittedThisCall = _gpuEmittedThisCall;
        }

        // GPU fast path: decoder invoked our GpuOutputHandler synchronously
        // inside Decode, so TextureArrived has already fired and
        // FramesDecoded / LastWidth / LastHeight were updated there.
        // `frames` is empty by construction in that case.
        if (gpuEmittedThisCall > 0) return;

        if (frames.Count == 0) return;

        foreach (var decoded in frames)
        {
            if (decoded.Bgra is null || decoded.Bgra.Length == 0) continue;

            var bgraSize = decoded.Width * decoded.Height * 4;
            if (decoded.Bgra.Length < bgraSize) continue;

            FramesDecoded++;
            LastWidth = decoded.Width;
            LastHeight = decoded.Height;

            var frame = new CaptureFrameData(
                decoded.Bgra.AsSpan(0, bgraSize),
                decoded.Width,
                decoded.Height,
                strideBytes: decoded.Width * 4,
                format: CaptureFramePixelFormat.Bgra8,
                timestamp: decoded.Timestamp);
            FrameArrived?.Invoke(in frame);
        }
    }

    // Fires synchronously from inside _decoder.Decode when the decoder
    // opts into zero-copy GPU output. Bumps the per-frame counters the
    // CPU path normally maintains so the stats overlay still reads
    // correctly, then raises TextureArrived for subscribers (the renderer)
    // to GPU-copy the texture during this call. _gpuEmittedThisCall lets
    // OnVideoFrameReceived tell a genuine empty decode apart from a
    // GPU-path decode that legitimately returned no CPU bytes.
    private int _gpuEmittedThisCall;

    private void OnDecoderGpuFrame(IntPtr texture, int width, int height, TimeSpan timestamp)
    {
        FramesDecoded++;
        LastWidth = width;
        LastHeight = height;
        _gpuEmittedThisCall++;
        try { TextureArrived?.Invoke(texture, width, height, timestamp); }
        catch (Exception ex)
        {
            DebugLog.Write($"[recv] TextureArrived handler threw: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
