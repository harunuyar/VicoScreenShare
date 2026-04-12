using System;
using System.Collections.Generic;
using System.Net;
using ScreenSharing.Client.Platform;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;

namespace ScreenSharing.Client.Media;

/// <summary>
/// Receiver-side counterpart to <see cref="CaptureStreamer"/>. Subscribes to an
/// <see cref="RTCPeerConnection"/>'s <c>OnVideoFrameReceived</c>, decodes each
/// reassembled VP8 frame, converts the decoder output to BGRA, and raises
/// <see cref="FrameArrived"/> with a <see cref="CaptureFrameData"/> that the
/// Phase 2 <c>WriteableBitmapRenderer</c> can render directly.
///
/// IMPORTANT: SIPSorcery's <see cref="VpxVideoEncoder.DecodeVideo"/> ignores the
/// <see cref="VideoPixelFormatsEnum"/> argument and always returns a 24-bit
/// packed BGR buffer — <c>sample.Sample.Length == width * height * 3</c>. An
/// earlier version of this class tried to interpret the buffer as I420 and
/// produced the "video cut into pieces stacked on top of each other" visual
/// because it was reading BGR bytes as planar YUV. Leave the direct BGR copy
/// in place and do not add a PixelConverter call back here.
/// </summary>
public sealed class StreamReceiver : ICaptureSource, IDisposable
{
    private readonly RTCPeerConnection _pc;
    private readonly VpxVideoEncoder _decoder;
    private readonly object _decodeLock = new();
    private byte[] _bgraBuffer = Array.Empty<byte>();
    private bool _attached;
    private bool _disposed;

    public StreamReceiver(RTCPeerConnection pc, string displayName = "remote")
    {
        _pc = pc;
        DisplayName = displayName;
        _decoder = new VpxVideoEncoder();
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
        // DecodeVideo call finishes before we free the native libvpx decoder.
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

        IEnumerable<VideoSample>? samples;
        lock (_decodeLock)
        {
            if (_disposed) return;
            FramesReceived++;
            try
            {
                samples = _decoder.DecodeVideo(encodedSample, VideoPixelFormatsEnum.Bgr, format.Codec);
            }
            catch
            {
                return;
            }
        }
        if (samples is null) return;

        foreach (var sample in samples)
        {
            if (sample.Sample is null || sample.Sample.Length == 0) continue;

            var width = (int)sample.Width;
            var height = (int)sample.Height;
            if (width <= 0 || height <= 0) continue;

            // See class comment: SIPSorcery's VP8 decoder returns packed BGR
            // (3 bytes per pixel) regardless of the format argument. Guard on
            // that to skip oddly-sized samples rather than blindly indexing.
            var expectedBgr = width * height * 3;
            if (sample.Sample.Length < expectedBgr) continue;

            var bgraSize = width * height * 4;
            if (_bgraBuffer.Length < bgraSize) _bgraBuffer = new byte[bgraSize];

            var src = sample.Sample;
            for (var y = 0; y < height; y++)
            {
                var srcRowStart = y * width * 3;
                var dstRowStart = y * width * 4;
                for (var x = 0; x < width; x++)
                {
                    var s = srcRowStart + x * 3;
                    var d = dstRowStart + x * 4;
                    _bgraBuffer[d + 0] = src[s + 0]; // B
                    _bgraBuffer[d + 1] = src[s + 1]; // G
                    _bgraBuffer[d + 2] = src[s + 2]; // R
                    _bgraBuffer[d + 3] = 0xFF;
                }
            }

            FramesDecoded++;

            var frame = new CaptureFrameData(
                _bgraBuffer.AsSpan(0, bgraSize),
                width,
                height,
                strideBytes: width * 4,
                format: CaptureFramePixelFormat.Bgra8,
                timestamp: TimeSpan.FromTicks((long)timestamp * TimeSpan.TicksPerMillisecond / 90));
            FrameArrived?.Invoke(in frame);
        }
    }
}
