using System;
using System.Net;
using ScreenSharing.Client.Media.Codecs;
using ScreenSharing.Client.Platform;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace ScreenSharing.Client.Media;

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
    }

    public string DisplayName { get; }

    public long FramesReceived { get; private set; }

    public long FramesDecoded { get; private set; }

    public event FrameArrivedHandler? FrameArrived;

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
        lock (_decodeLock)
        {
            if (_disposed) return;
            FramesReceived++;
            frames = _decoder.Decode(encodedSample);
        }

        if (frames.Count == 0) return;

        foreach (var decoded in frames)
        {
            if (decoded.Bgra is null || decoded.Bgra.Length == 0) continue;

            var bgraSize = decoded.Width * decoded.Height * 4;
            if (decoded.Bgra.Length < bgraSize) continue;

            FramesDecoded++;

            var frame = new CaptureFrameData(
                decoded.Bgra.AsSpan(0, bgraSize),
                decoded.Width,
                decoded.Height,
                strideBytes: decoded.Width * 4,
                format: CaptureFramePixelFormat.Bgra8,
                timestamp: TimeSpan.FromTicks((long)timestamp * TimeSpan.TicksPerMillisecond / 90));
            FrameArrived?.Invoke(in frame);
        }
    }
}
