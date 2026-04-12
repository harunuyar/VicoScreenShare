using System;
using System.Linq;
using System.Net;
using ScreenSharing.Client.Platform;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;

namespace ScreenSharing.Client.Media;

/// <summary>
/// Receiver-side counterpart to <see cref="CaptureStreamer"/>. Subscribes to an
/// <see cref="RTCPeerConnection"/>'s <c>OnVideoFrameReceived</c>, decodes each
/// reassembled VP8 frame, converts the I420 output to BGRA, and raises
/// <see cref="FrameDecoded"/> with a <see cref="CaptureFrameData"/> that a
/// <c>WriteableBitmapRenderer</c> (or any other <see cref="ICaptureSource"/>
/// consumer) can render directly.
///
/// The class is effectively an <see cref="ICaptureSource"/> whose frames come
/// from the network instead of a local capture, so tiles can reuse the Phase 2
/// rendering path without any branching.
/// </summary>
public sealed class StreamReceiver : ICaptureSource, IDisposable
{
    private readonly RTCPeerConnection _pc;
    private readonly VpxVideoEncoder _decoder;
    private byte[] _bgraBuffer = Array.Empty<byte>();
    private int _lastWidth;
    private int _lastHeight;
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
        if (_disposed) return;
        _disposed = true;
        try { StopAsync().GetAwaiter().GetResult(); } catch { }
        try { _decoder.Dispose(); } catch { }
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
        if (_disposed || encodedSample is null || encodedSample.Length == 0) return;

        FramesReceived++;

        IEnumerable<VideoSample>? samples;
        try
        {
            samples = _decoder.DecodeVideo(encodedSample, VideoPixelFormatsEnum.I420, format.Codec);
        }
        catch
        {
            return;
        }
        if (samples is null) return;

        foreach (var sample in samples)
        {
            if (sample.Sample is null || sample.Sample.Length == 0) continue;

            var width = (int)sample.Width;
            var height = (int)sample.Height;
            if (width <= 0 || height <= 0) continue;

            var required = I420ToBgra.RequiredBgraSize(width, height);
            if (_bgraBuffer.Length < required)
            {
                _bgraBuffer = new byte[required];
            }

            try
            {
                I420ToBgra.Convert(sample.Sample, width, height, _bgraBuffer, bgraStrideBytes: width * 4);
            }
            catch
            {
                continue;
            }

            _lastWidth = width;
            _lastHeight = height;
            FramesDecoded++;

            var frame = new CaptureFrameData(
                _bgraBuffer.AsSpan(0, required),
                width,
                height,
                strideBytes: width * 4,
                format: CaptureFramePixelFormat.Bgra8,
                timestamp: TimeSpan.FromTicks((long)timestamp * TimeSpan.TicksPerMillisecond / 90));
            FrameArrived?.Invoke(in frame);
        }
    }
}
