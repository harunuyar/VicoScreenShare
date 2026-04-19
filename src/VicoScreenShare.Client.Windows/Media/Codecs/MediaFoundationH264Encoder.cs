namespace VicoScreenShare.Client.Windows.Media.Codecs;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using VicoScreenShare.Client.Diagnostics;
using VicoScreenShare.Client.Media.Codecs;
using VicoScreenShare.Client.Windows.Direct3D;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;
using IVideoEncoder = VicoScreenShare.Client.Media.Codecs.IVideoEncoder;

/// <summary>
/// H.264 encoder wrapping a Media Foundation <see cref="IMFTransform"/>.
///
/// Modern Windows hardware encoders (NVENC on NVIDIA, QSV on Intel, VCE on
/// AMD) are exposed as ASYNC MFTs: instead of the push-pull sync pattern
/// (ProcessInput, then loop ProcessOutput until need-more-input), they emit
/// <c>METransformNeedInput</c> and <c>METransformHaveOutput</c> events and
/// you drive ProcessInput / ProcessOutput in response to those events.
///
/// This class supports both modes:
///  - Async MFTs run a background event pump that translates the event
///    stream into a <see cref="SemaphoreSlim"/> of "needs input" credits
///    and a <see cref="ConcurrentQueue{T}"/> of encoded output bytes. The
///    caller's <see cref="EncodeI420"/> pushes one frame per call, credits
///    permitting, and drains whatever output has landed — from the
///    caller's perspective it still looks like a synchronous encoder.
///  - Sync MFTs (Microsoft's software H264 encoder and some older drivers)
///    drain output inline after each ProcessInput, same as before.
///
/// Probe order inside the Media Foundation category is:
///   1. hardware async (NVENC / QSV / VCE) — preferred
///   2. hardware sync — older driver variants
///   3. software sync (MSFT MSH264EncoderMFT) — universal fallback
/// </summary>
public sealed unsafe class MediaFoundationH264Encoder : IVideoEncoder, IAsyncEncodedOutputSource
{
    // --- IAsyncEncodedOutputSource ---
    private Action? _outputAvailable;
    private bool _externalOutputDrain;

    public event Action? OutputAvailable
    {
        add { _outputAvailable += value; _externalOutputDrain = true; }
        remove { _outputAvailable -= value; _externalOutputDrain = (_outputAvailable != null); }
    }

    public bool TryDequeueEncoded(out EncodedFrame frame) => _outputQueue.TryDequeue(out frame);

    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly long _bitrate;
    private readonly int _gopFrames;
    private readonly IMFTransform _transform;
    private readonly bool _isAsync;
    private readonly IMFMediaEventGenerator? _events;
    private readonly ID3D11Device? _d3dDevice;
    private readonly IMFDXGIDeviceManager? _dxgiManager;
    private readonly bool _ownsD3dDevice;
    private readonly bool _useLanczos;
    private ITextureScaler? _textureScaler;
    private ID3D11Texture2D? _encoderInputTexture;
    private int _textureScalerSrcWidth;
    private int _textureScalerSrcHeight;
    private readonly bool _inputIsBgra; // true = ARGB32, false = NV12
    private readonly int _inputBufferSize;
    private readonly Thread? _pumpThread;
    private readonly CancellationTokenSource _pumpCts = new();
    private readonly SemaphoreSlim _needInputSignal = new(0, int.MaxValue);
    private readonly ConcurrentQueue<EncodedFrame> _outputQueue = new();
    private readonly object _processLock = new();
    private readonly byte[] _nv12Buffer;
    private long _loggedEncodedFrames;
    private long _loggedProcessInputs;
    private long _loggedTimeouts;
    private int _loggedTimingFrames;
    private bool _disposed;

    public MediaFoundationH264Encoder(int width, int height, int fps, long bitrate, int gopFrames, bool useLanczos = false)
        : this(width, height, fps, bitrate, gopFrames, useLanczos, externalDevice: null)
    {
    }

    public MediaFoundationH264Encoder(int width, int height, int fps, long bitrate, int gopFrames, bool useLanczos, ID3D11Device? externalDevice)
    {
        _width = width;
        _height = height;
        _fps = Math.Max(1, fps);
        _bitrate = Math.Max(500_000, bitrate);
        _gopFrames = Math.Max(1, gopFrames);
        _useLanczos = useLanczos;

        // Create (or wrap) a D3D11 device and DXGI device manager for the
        // encoder MFT BEFORE we hand it any media types. NVENC's MFT routes
        // its async event handling and its zero-copy texture ingest through
        // this manager — attaching the manager is required to enable async
        // mode on most modern hardware encoder builds.
        if (externalDevice is not null)
        {
            _d3dDevice = externalDevice;
            _dxgiManager = TryWrapExternalDevice(externalDevice);
            _ownsD3dDevice = false;
        }
        else
        {
            (_d3dDevice, _dxgiManager) = TryCreateD3DManager();
            _ownsD3dDevice = true;
        }

        var (transform, isAsync, pickedLabel, inputIsBgra) = CreateEncoder(width, height, _fps, _bitrate, _gopFrames, _dxgiManager);
        _transform = transform;
        _isAsync = isAsync;
        _inputIsBgra = inputIsBgra;
        _nv12Buffer = inputIsBgra ? Array.Empty<byte>() : new byte[width * height * 3 / 2];
        _inputBufferSize = inputIsBgra ? width * height * 4 : width * height * 3 / 2;
        DebugLog.Write($"[mf] H264 encoder initialized {width}x{height}@{_fps} {_bitrate} bps ({pickedLabel}, input={(inputIsBgra ? "BGRA" : "NV12")})");

        if (_isAsync)
        {
            _events = _transform.QueryInterface<IMFMediaEventGenerator>();
            _pumpThread = new Thread(EventPumpLoop)
            {
                IsBackground = true,
                Name = "MF H264 event pump",
            };
            // Start the pump BEFORE we send the streaming messages so no
            // initial NeedInput event races past an unattached queue.
            _pumpThread.Start();
        }

        // NOTIFY_BEGIN_STREAMING allocates native resources (hw context,
        // encoder buffers). Async MFTs additionally require NOTIFY_START_OF_STREAM
        // to actually start emitting METransformNeedInput events — without
        // it, NVENC sits idle and the event pump never gets a credit to
        // release back to EncodeI420.
        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);
        if (_isAsync)
        {
            _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, 0);
        }
    }

    public VideoCodec Codec => VideoCodec.H264;

    public int Width => _width;

    public int Height => _height;

    /// <summary>
    /// True when the encoder was built on a hardware async MFT with a
    /// D3D11 device attached AND negotiated BGRA input. In that mode the
    /// <see cref="EncodeTexture(IntPtr,int,int)"/> path runs end-to-end on
    /// the GPU: the D3D11 Video Processor scales the caller's texture onto
    /// our internal input texture, and NVENC color-converts BGRA→NV12
    /// internally. No CPU readback, no per-frame allocation.
    /// </summary>
    public bool SupportsTextureInput => _d3dDevice is not null && _inputIsBgra;

    /// <summary>
    /// Force the encoder to emit its next frame as an IDR/keyframe. Called
    /// when a new subscriber connects mid-stream or when a receiver reports
    /// unrecoverable loss.
    ///
    /// Sets <c>CODECAPI_AVEncVideoForceKeyFrame=1</c> on the transform so the
    /// next encoded frame is an IDR. Unlike <c>MFT_MESSAGE_COMMAND_FLUSH</c>
    /// this leaves the pipeline intact — async MFTs (NVENC) get wedged by
    /// FLUSH (they stop emitting output events until a full
    /// Drain/NotifyStartOfStream restart), which surfaced in production as
    /// "outgoing fps drops to 0 the moment a second viewer joins." Both VP8
    /// and H.264 honor this attribute on every MFT we've measured.
    /// </summary>
    public void RequestKeyframe()
    {
        lock (_processLock)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _transform.Attributes.Set(CodecApiAVEncVideoForceKeyFrame, 1u);
                DebugLog.Write("[mf] H264 encoder ForceKeyFrame set for next input");
            }
            catch (Exception ex)
            {
                DebugLog.Write($"[mf] H264 encoder ForceKeyFrame threw: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Reconfigure the encoder's target bitrate live. Reapplies the full CBR
    /// triple (<c>MeanBitRate</c> + <c>MaxBitRate</c> + <c>BufferSize</c>) on
    /// the running MFT so NVENC keeps hitting the rate consistently instead
    /// of undershooting. Called by the adaptive-bitrate controller when the
    /// upstream RR reports sustained loss; a few hundred ms later frames
    /// come out at the new rate.
    /// </summary>
    public void UpdateBitrate(int bitsPerSecond)
    {
        if (bitsPerSecond <= 0)
        {
            return;
        }
        lock (_processLock)
        {
            if (_disposed)
            {
                return;
            }
            var clamped = (uint)Math.Min(bitsPerSecond, uint.MaxValue);
            try
            {
                _transform.Attributes.Set(CodecApiAVEncCommonMeanBitRate, clamped);
                _transform.Attributes.Set(CodecApiAVEncCommonMaxBitRate, clamped);
                _transform.Attributes.Set(CodecApiAVEncCommonBufferSize, clamped);
                DebugLog.Write($"[mf] H264 encoder bitrate → {bitsPerSecond / 1000} kbps");
            }
            catch (Exception ex)
            {
                DebugLog.Write($"[mf] H264 encoder UpdateBitrate threw: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// The internal D3D11 device, exposed so callers (like the test harness
    /// or future zero-copy capture path) can allocate textures on the same
    /// device the encoder is using. Null when no GPU manager is attached
    /// (software encoder fallback).
    /// </summary>
    public ID3D11Device? D3D11Device => _d3dDevice;

    /// <summary>
    /// Encode a caller-owned BGRA texture, scaling it on the GPU if the
    /// source dimensions differ from this encoder's target dimensions.
    /// The caller is expected to have bumped the refcount once before
    /// invoking (the capture source does this inside its TextureArrived
    /// dispatch); our local wrapper <c>using var</c> disposes it to balance.
    /// </summary>
    public EncodedFrame? EncodeTexture(IntPtr nativeTexture, int sourceWidth, int sourceHeight, TimeSpan inputTimestamp)
    {
        if (_disposed || _d3dDevice is null || !_inputIsBgra)
        {
            return null;
        }

        if (nativeTexture == IntPtr.Zero)
        {
            return null;
        }

        using var sourceTexture = new ID3D11Texture2D(nativeTexture);

        try
        {
            EnsureTextureScaler(sourceWidth, sourceHeight);
            _textureScaler!.Process(sourceTexture, _encoderInputTexture!);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[mf] texture scaler threw: {ex.Message}");
            return null;
        }

        return EncodeTexture(_encoderInputTexture!, inputTimestamp);
    }

    /// <summary>
    /// Lazily build (or rebuild) the D3D11VideoScaler + encoder input
    /// texture when the source dimensions change. On a stable stream
    /// this runs exactly once for the lifetime of the encoder.
    /// </summary>
    private void EnsureTextureScaler(int srcWidth, int srcHeight)
    {
        if (_textureScaler is not null
            && _textureScalerSrcWidth == srcWidth
            && _textureScalerSrcHeight == srcHeight
            && _encoderInputTexture is not null)
        {
            return;
        }

        _textureScaler?.Dispose();
        _encoderInputTexture?.Dispose();

        if (_useLanczos && _d3dDevice is not null)
        {
            try
            {
                _textureScaler = new LanczosScaler(_d3dDevice, srcWidth, srcHeight, _width, _height);
            }
            catch (Exception ex)
            {
                DebugLog.Write($"[mf] Lanczos scaler failed ({ex.Message}), falling back to bilinear");
                _textureScaler = new D3D11VideoScaler(_d3dDevice, srcWidth, srcHeight, _width, _height);
            }
        }
        else
        {
            _textureScaler = new D3D11VideoScaler(_d3dDevice!, srcWidth, srcHeight, _width, _height);
        }
        _textureScalerSrcWidth = srcWidth;
        _textureScalerSrcHeight = srcHeight;

        // Lanczos writes via UAV (compute shader), so the encoder input
        // texture needs UnorderedAccess. The Video Processor writes via
        // RenderTarget. Include both so either scaler works.
        var desc = new Texture2DDescription
        {
            Width = (uint)_width,
            Height = (uint)_height,
            ArraySize = 1,
            MipLevels = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource | BindFlags.UnorderedAccess,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        _encoderInputTexture = _d3dDevice!.CreateTexture2D(desc);
        var scalerName = _textureScaler is LanczosScaler ? "Lanczos" : "Bilinear";
        DebugLog.Write($"[mf] texture pipeline built {srcWidth}x{srcHeight} -> {_width}x{_height} ({scalerName})");
    }

    /// <summary>
    /// Encode a D3D11 texture directly. Wraps the texture in a DXGI
    /// surface buffer + IMFSample with no system-memory copy, no color
    /// conversion on the CPU. The texture must:
    /// <list type="bullet">
    ///   <item>be allocated on <see cref="D3D11Device"/></item>
    ///   <item>have format NV12 (or whatever the encoder's input type was set to)</item>
    ///   <item>have width/height matching this encoder's <see cref="Width"/>/<see cref="Height"/></item>
    /// </list>
    /// Returns encoded bytes (caller owns) or null if the encoder hasn't
    /// produced output yet (async warmup, etc).
    /// </summary>
    public EncodedFrame? EncodeTexture(ID3D11Texture2D texture, TimeSpan inputTimestamp)
    {
        if (_disposed)
        {
            return null;
        }

        if (_d3dDevice is null)
        {
            DebugLog.Write("[mf] EncodeTexture called but encoder has no D3D device");
            return null;
        }

        var sample = CreateD3DInputSample(texture, inputTimestamp);
        try
        {
            if (_isAsync)
            {
                return EncodeAsyncSample(sample, inputTimestamp);
            }
            return EncodeSyncSample(sample, inputTimestamp);
        }
        finally
        {
            sample.Dispose();
        }
    }

    private IMFSample CreateD3DInputSample(ID3D11Texture2D texture, TimeSpan inputTimestamp)
    {
        var iidTexture = typeof(ID3D11Texture2D).GUID;
        // MFCreateDXGISurfaceBuffer wraps a D3D11 texture as an
        // IMFMediaBuffer that the encoder can read directly from GPU
        // memory. Subresource 0 = the only mip level we care about.
        // bottomUpWhenLinear=false because BGRA/NV12 textures from
        // capture sources are top-down.
        var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(iidTexture, texture, 0, false);

        // Set the buffer's "current length" to the texture's actual size.
        // Without this NVENC sometimes treats the buffer as empty and
        // returns no output.
        var nv12Size = _width * _height * 3 / 2;
        buffer.CurrentLength = nv12Size;

        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);

        // Stamp the input sample with the real content timestamp. The MFT
        // copies this through to the matching output sample, which the
        // event pump reads back and enqueues onto _outputQueue. That is
        // what carries the capture-side clock across the encoder pipeline
        // — without this, the async pump has no way to tell which input
        // produced a given output byte stream.
        var duration = 10_000_000L / _fps;
        sample.SampleTime = inputTimestamp.Ticks;
        sample.SampleDuration = duration;

        buffer.Dispose();
        return sample;
    }

    private EncodedFrame? EncodeAsyncSample(IMFSample sample, TimeSpan inputTimestamp)
    {
        if (!_needInputSignal.Wait(50))
        {
            // No NeedInput credit — pump is backed up. If an external
            // subscriber is draining output via OutputAvailable, don't
            // compete on the queue; just return null. Otherwise poll.
            return _externalOutputDrain ? null : TryDequeueOutput();
        }

        lock (_processLock)
        {
            if (_disposed)
            {
                return null;
            }

            try
            {
                _transform.ProcessInput(0, sample, 0);
            }
            catch (Exception ex)
            {
                DebugLog.Write($"[mf] async H264 EncodeTexture ProcessInput threw: {ex.Message}");
                return null;
            }
        }

        // When an external subscriber handles OutputAvailable, it
        // drains the queue. Don't compete with it here — return null
        // so CaptureStreamer knows to skip inline dispatch.
        return _externalOutputDrain ? null : TryDequeueOutput();
    }

    private EncodedFrame? EncodeSyncSample(IMFSample sample, TimeSpan inputTimestamp)
    {
        lock (_processLock)
        {
            if (_disposed)
            {
                return null;
            }

            try
            {
                _transform.ProcessInput(0, sample, 0);
            }
            catch (Exception ex)
            {
                DebugLog.Write($"[mf] sync H264 EncodeTexture ProcessInput threw: {ex.Message}");
                return null;
            }
            // Sync MFTs produce output inline for the input we just fed, so
            // the bytes correspond exactly to inputTimestamp. No event pump
            // means no SampleTime readback needed.
            var bytes = DrainOutputLocked();
            return bytes is null ? null : new EncodedFrame(bytes, inputTimestamp);
        }
    }

    public EncodedFrame? EncodeBgra(byte[] bgra, int stride, TimeSpan inputTimestamp)
    {
        if (_disposed)
        {
            return null;
        }

        var timingStart = _loggedTimingFrames < 3 ? Stopwatch.GetTimestamp() : 0L;

        // Two paths:
        //
        //   ARGB32 mode: NVENC accepted BGRA input, so we hand it BGRA
        //   bytes verbatim — no color conversion on the CPU at all. NVENC
        //   does BGRA -> NV12 on the GPU as part of its internal pipeline.
        //   This is what we want.
        //
        //   NV12 mode: fallback for hardware encoders that won't take
        //   ARGB32. The old BgraToNv12Fast scalar pass runs and we feed
        //   the NV12 bytes.
        if (!_inputIsBgra)
        {
            BgraToNv12Fast(bgra, stride, _nv12Buffer, _width, _height);
        }

        var convertEnd = timingStart != 0 ? Stopwatch.GetTimestamp() : 0L;

        var encoded = _isAsync ? EncodeAsync(bgra, stride, inputTimestamp) : EncodeSync(bgra, stride, inputTimestamp);

        if (timingStart != 0)
        {
            _loggedTimingFrames++;
            var totalEnd = Stopwatch.GetTimestamp();
            var ticksPerMs = Stopwatch.Frequency / 1000.0;
            var convertMs = (convertEnd - timingStart) / ticksPerMs;
            var encodeMs = (totalEnd - convertEnd) / ticksPerMs;
            var totalMs = (totalEnd - timingStart) / ticksPerMs;
            var label = _inputIsBgra ? "no-conv" : "bgra->nv12";
            DebugLog.Write($"[mf-timing #{_loggedTimingFrames}] {label}={convertMs:F1}ms encode={encodeMs:F1}ms total={totalMs:F1}ms");
        }

        if (encoded is { Bytes.Length: > 0 } && _loggedEncodedFrames < 3)
        {
            _loggedEncodedFrames++;
            DebugLog.Write($"[mf] H264 encoder produced frame {_loggedEncodedFrames} ({encoded.Value.Bytes.Length} bytes, pts={encoded.Value.Timestamp.TotalMilliseconds:F2}ms)");
        }
        return encoded;
    }

    private EncodedFrame? EncodeAsync(byte[] bgraSource, int bgraStride, TimeSpan inputTimestamp)
    {
        if (!_needInputSignal.Wait(50))
        {
            if (_loggedTimeouts < 3)
            {
                _loggedTimeouts++;
                DebugLog.Write($"[mf] async EncodeAsync timeout #{_loggedTimeouts} — no NeedInput credit within 50ms");
            }
            return _externalOutputDrain ? null : TryDequeueOutput();
        }

        var sample = CreateInputSample(bgraSource, bgraStride, inputTimestamp);
        try
        {
            lock (_processLock)
            {
                if (_disposed)
                {
                    return null;
                }

                try
                {
                    _transform.ProcessInput(0, sample, 0);
                    if (_loggedProcessInputs < 3)
                    {
                        _loggedProcessInputs++;
                        DebugLog.Write($"[mf] async ProcessInput #{_loggedProcessInputs} ok");
                    }
                }
                catch (Exception ex)
                {
                    DebugLog.Write($"[mf] async H264 ProcessInput threw: {ex.Message}");
                }
            }
        }
        finally
        {
            sample.Dispose();
        }

        return _externalOutputDrain ? null : TryDequeueOutput();
    }

    private EncodedFrame? EncodeSync(byte[] bgraSource, int bgraStride, TimeSpan inputTimestamp)
    {
        var sample = CreateInputSample(bgraSource, bgraStride, inputTimestamp);
        try
        {
            lock (_processLock)
            {
                if (_disposed)
                {
                    return null;
                }

                try
                {
                    _transform.ProcessInput(0, sample, 0);
                }
                catch (Exception ex)
                {
                    DebugLog.Write($"[mf] sync H264 ProcessInput threw: {ex.Message}");
                    return null;
                }
            }
        }
        finally
        {
            sample.Dispose();
        }

        var bytes = DrainOutputLocked();
        return bytes is null ? null : new EncodedFrame(bytes, inputTimestamp);
    }

    /// <summary>
    /// Build an IMFSample backed by a system-memory IMFMediaBuffer. In ARGB32
    /// mode the source bytes are the caller's BGRA frame copied row-by-row
    /// in case the source stride is wider than width*4. In NV12 mode they're
    /// the converted contents of <see cref="_nv12Buffer"/>. The returned
    /// sample's <c>SampleTime</c> is stamped with <paramref name="inputTimestamp"/>
    /// so the MFT can propagate the content clock to the matching output
    /// sample.
    /// </summary>
    private IMFSample CreateInputSample(byte[] bgraSource, int bgraStride, TimeSpan inputTimestamp)
    {
        var buffer = MediaFactory.MFCreateMemoryBuffer(_inputBufferSize);
        buffer.Lock(out nint ptr, out int maxLen, out _);
        try
        {
            if (_inputIsBgra)
            {
                var rowBytes = _width * 4;
                fixed (byte* srcBase = bgraSource)
                {
                    var dstBase = (byte*)ptr.ToPointer();
                    for (var y = 0; y < _height; y++)
                    {
                        Buffer.MemoryCopy(
                            srcBase + y * bgraStride,
                            dstBase + y * rowBytes,
                            maxLen - y * rowBytes,
                            rowBytes);
                    }
                }
            }
            else
            {
                fixed (byte* srcPtr = _nv12Buffer)
                {
                    Buffer.MemoryCopy(srcPtr, ptr.ToPointer(), maxLen, _nv12Buffer.Length);
                }
            }
        }
        finally
        {
            buffer.Unlock();
        }
        buffer.CurrentLength = _inputBufferSize;

        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);

        // Stamp with the real content clock. Propagation through the MFT
        // is what carries the capture-side PTS across the encoder pipeline
        // so the event pump can recover it on output.
        var duration = 10_000_000L / _fps;
        sample.SampleTime = inputTimestamp.Ticks;
        sample.SampleDuration = duration;

        buffer.Dispose();
        return sample;
    }

    private EncodedFrame? TryDequeueOutput()
    {
        if (_outputQueue.TryDequeue(out var frame))
        {
            return frame;
        }

        return null;
    }

    /// <summary>
    /// Sync-mode output drain: loop <see cref="IMFTransform.ProcessOutput"/>
    /// until the MFT says it needs more input. The encode lock is held by
    /// the caller. Sync mode is 1-in/1-out so the caller tags the returned
    /// bytes with the input timestamp directly — no SampleTime readback
    /// needed.
    /// </summary>
    private byte[]? DrainOutputLocked()
    {
        List<byte>? accumulator = null;

        while (true)
        {
            var output = PullSingleOutput(out var result, out _);
            if (result is DrainResult.NeedMoreInput or DrainResult.Failure)
            {
                return accumulator?.ToArray();
            }
            if (output is null)
            {
                continue;
            }

            (accumulator ??= new List<byte>()).AddRange(output);
        }
    }

    private enum DrainResult { Success, NeedMoreInput, Failure }

    // MFT_OUTPUT_STREAM_INFO_FLAGS bit: MFT allocates its own output samples
    // and requires the client to pass null in OutputDataBuffer.Sample.
    // NVENC and most hardware encoder MFTs set this flag.
    private const int MftOutputStreamProvidesSamples = 0x100;

    /// <summary>
    /// Pull one encoded sample from the MFT. Handles both the "client
    /// provides output sample" path (software MFTs) and the "MFT provides
    /// output sample" path (NVENC / most hardware MFTs) by checking the
    /// PROVIDES_SAMPLES flag on the output stream. Returns the raw bytes
    /// AND the sample's propagated <c>SampleTime</c> so the async event
    /// pump can tag the output with the right content timestamp before
    /// enqueueing. The timestamp is copied by the underlying MFT from the
    /// input sample that actually produced this output — NOT necessarily
    /// the most recently submitted input.
    /// </summary>
    private byte[]? PullSingleOutput(out DrainResult result, out long sampleTimeTicks)
    {
        sampleTimeTicks = 0;
        var streamInfo = _transform.GetOutputStreamInfo(0);
        var mftProvidesSamples = (streamInfo.Flags & MftOutputStreamProvidesSamples) != 0;

        IMFSample? clientSample = null;
        IMFMediaBuffer? clientBuffer = null;
        if (!mftProvidesSamples)
        {
            var bufferSize = streamInfo.Size <= 0 ? (_width * _height) : streamInfo.Size;
            clientSample = MediaFactory.MFCreateSample();
            clientBuffer = MediaFactory.MFCreateMemoryBuffer(bufferSize);
            clientSample.AddBuffer(clientBuffer);
        }

        var db = new OutputDataBuffer
        {
            StreamID = 0,
            Sample = clientSample, // null when the MFT allocates for us
        };

        var hr = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref db, out _);

        IMFSample? readSample = null;
        try
        {
            if ((uint)hr.Code == 0xC00D6D72u) // MF_E_TRANSFORM_NEED_MORE_INPUT
            {
                result = DrainResult.NeedMoreInput;
                return null;
            }

            if (hr.Failure)
            {
                DebugLog.Write($"[mf] H264 ProcessOutput failed HRESULT 0x{(uint)hr.Code:X8}");
                result = DrainResult.Failure;
                return null;
            }

            // In both paths db.Sample points to the sample we should read
            // from. In the client-provided path it's the same object we
            // allocated; in the MFT-provided path it's a sample the MFT
            // filled in on our behalf and transferred ownership of.
            readSample = db.Sample;
            if (readSample is null)
            {
                DebugLog.Write("[mf] ProcessOutput returned success but db.Sample is null");
                result = DrainResult.Failure;
                return null;
            }

            // Read the propagated SampleTime BEFORE disposing the sample.
            // Any conformant MFT (including NVENC) copies SampleTime from
            // the input that produced a given output; buffered inputs
            // keep their original timestamps even when the pipeline lags.
            try { sampleTimeTicks = readSample.SampleTime; }
            catch { sampleTimeTicks = 0; }

            // ConvertToContiguousBuffer flattens multi-buffer samples into a
            // single IMFMediaBuffer, which is the safe way to read encoded
            // output regardless of how the MFT laid the sample out.
            var flatBuffer = readSample.ConvertToContiguousBuffer();
            try
            {
                flatBuffer.Lock(out nint framePtr, out _, out int curLen);
                try
                {
                    if (curLen <= 0)
                    {
                        result = DrainResult.Success;
                        return null;
                    }
                    var bytes = new byte[curLen];
                    Marshal.Copy(framePtr, bytes, 0, curLen);
                    result = DrainResult.Success;
                    return bytes;
                }
                finally
                {
                    flatBuffer.Unlock();
                }
            }
            finally
            {
                flatBuffer.Dispose();
            }
        }
        finally
        {
            // Always release the sample we read from. Disposing our own
            // clientSample and the MFT-provided readSample are both the
            // right action. If they happen to be the same reference,
            // disposing twice via the Vortice ComObject wrapper is
            // harmless — the ref count underneath decrements once per
            // logical wrapper.
            if (readSample is not null && !ReferenceEquals(readSample, clientSample))
            {
                try { readSample.Dispose(); } catch { }
            }
            clientSample?.Dispose();
            clientBuffer?.Dispose();
        }
    }

    /// <summary>
    /// Background event pump for async MFTs. Translates NeedInput / HaveOutput
    /// events into semaphore releases and output enqueues. Runs until the
    /// cancellation token is signaled on <see cref="Dispose"/>.
    /// </summary>
    private void EventPumpLoop()
    {
        DebugLog.Write("[mf] async event pump thread started");
        var loggedEvents = 0;
        try
        {
            while (!_pumpCts.IsCancellationRequested)
            {
                IMFMediaEvent? evt;
                try
                {
                    evt = _events!.GetEvent(0); // blocking
                }
                catch (Exception ex)
                {
                    DebugLog.Write($"[mf] async event pump GetEvent threw: {ex.GetType().Name}: {ex.Message}");
                    return;
                }
                if (evt is null)
                {
                    DebugLog.Write("[mf] async event pump GetEvent returned null");
                    continue;
                }

                try
                {
                    var type = (MediaEventTypes)evt.EventType;
                    if (loggedEvents < 8)
                    {
                        loggedEvents++;
                        DebugLog.Write($"[mf] async event #{loggedEvents}: {type}");
                    }

                    if (type == MediaEventTypes.TransformNeedInput)
                    {
                        _needInputSignal.Release();
                    }
                    else if (type == MediaEventTypes.TransformHaveOutput)
                    {
                        byte[]? output;
                        long sampleTimeTicks;
                        lock (_processLock)
                        {
                            if (_disposed)
                            {
                                return;
                            }

                            output = PullSingleOutput(out var result, out sampleTimeTicks);
                            if (result == DrainResult.Failure)
                            {
                                DebugLog.Write("[mf] async pump PullSingleOutput returned Failure");
                            }
                        }
                        if (output is { Length: > 0 })
                        {
                            // SampleTime on the output was copied by the MFT
                            // from the input sample that actually produced
                            // this bitstream, NOT the most recently fed
                            // input. Enqueueing it verbatim preserves the
                            // per-frame content clock across the async
                            // pipeline depth.
                            var ts = TimeSpan.FromTicks(sampleTimeTicks);
                            _outputQueue.Enqueue(new EncodedFrame(output, ts));
                            try { _outputAvailable?.Invoke(); } catch { }
                        }
                        else if (loggedEvents <= 8)
                        {
                            DebugLog.Write($"[mf] async pump HaveOutput produced {output?.Length ?? -1} bytes");
                        }
                    }
                    else if (type == MediaEventTypes.TransformDrainComplete)
                    {
                        DebugLog.Write("[mf] async event pump drain complete, exiting");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    DebugLog.Write($"[mf] async event pump handler exception: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    evt.Dispose();
                }
            }
        }
        finally
        {
            DebugLog.Write("[mf] async event pump thread exiting");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try { _pumpCts.Cancel(); } catch { }
        try { _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, 0); } catch { }
        // The pump thread exits when GetEvent unblocks or the MFT is disposed;
        // give it a moment but don't hang forever on shutdown.
        _pumpThread?.Join(TimeSpan.FromMilliseconds(500));
        try { _events?.Dispose(); } catch { }
        try { _transform.Dispose(); } catch { }
        try { _textureScaler?.Dispose(); } catch { }
        try { _encoderInputTexture?.Dispose(); } catch { }
        try { _dxgiManager?.Dispose(); } catch { }
        // Only dispose the D3D device when we created it ourselves. External
        // devices are owned by the capture pipeline and must outlive this
        // encoder.
        if (_ownsD3dDevice)
        {
            try { _d3dDevice?.Dispose(); } catch { }
        }
        try { _pumpCts.Dispose(); } catch { }
        try { _needInputSignal.Dispose(); } catch { }
    }

    // --- Encoder enumeration + type negotiation ---

    private static (IMFTransform transform, bool isAsync, string label, bool inputIsBgra) CreateEncoder(int width, int height, int fps, long bitrate, int gopFrames, IMFDXGIDeviceManager? dxgiManager)
    {
        // Try async hardware MFTs first — NVENC / QSV / VCE. Passing both
        // HARDWARE and ASYNCMFT flags returns async hardware encoders, which
        // we unlock individually before touching them.
        var async = TryCreateEncoder(
            (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagAsyncmft | EnumFlag.EnumFlagSortandfilter),
            width, height, fps, bitrate, gopFrames, tryAsync: true, tryHardware: true, label: "hardware async", dxgiManager);
        if (async.transform is not null)
        {
            return (async.transform, true, "hardware async", async.inputIsBgra);
        }

        // Fall back to sync hardware MFTs — rare on modern drivers but they
        // exist (e.g. older Intel QSV builds).
        var sync = TryCreateEncoder(
            (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagSortandfilter),
            width, height, fps, bitrate, gopFrames, tryAsync: false, tryHardware: true, label: "hardware sync", dxgiManager);
        if (sync.transform is not null)
        {
            return (sync.transform, false, "hardware sync", sync.inputIsBgra);
        }

        // Last resort: Microsoft's software H.264 encoder. Always available.
        // Software encoder doesn't need the D3D manager, pass null.
        var software = TryCreateEncoder(
            (uint)(EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagSortandfilter),
            width, height, fps, bitrate, gopFrames, tryAsync: false, tryHardware: false, label: "software", dxgiManager: null);
        if (software.transform is not null)
        {
            return (software.transform, false, "software", software.inputIsBgra);
        }

        throw new InvalidOperationException("Media Foundation has no usable H.264 encoder on this machine");
    }

    /// <summary>
    /// Create a D3D11 device + IMFDXGIDeviceManager pair for the encoder
    /// MFT. Returning null,null means we fall through to system-memory-only
    /// mode — the encoder still works, it just won't get the GPU fast path
    /// in Phase 2+. Failures are logged but not fatal.
    /// </summary>
    /// <summary>
    /// Wrap a caller-owned D3D11 device in an IMFDXGIDeviceManager without
    /// taking ownership. Used by the capture path so the WGC framepool, the
    /// GPU scaler and the encoder all share one device.
    /// </summary>
    private static IMFDXGIDeviceManager? TryWrapExternalDevice(ID3D11Device device)
    {
        try
        {
            using (var multithread = device.QueryInterfaceOrNull<ID3D11Multithread>())
            {
                multithread?.SetMultithreadProtected(true);
            }

            var manager = MediaFactory.MFCreateDXGIDeviceManager();
            manager.ResetDevice(device);
            DebugLog.Write("[mf] wrapped external D3D11 device in DXGI device manager");
            return manager;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[mf] TryWrapExternalDevice threw: {ex.Message}; encoder will run without GPU manager");
            return null;
        }
    }

    private static (ID3D11Device?, IMFDXGIDeviceManager?) TryCreateD3DManager()
    {
        try
        {
            var hr = D3D11.D3D11CreateDevice(
                adapter: null,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                new[]
                {
                    FeatureLevel.Level_11_1,
                    FeatureLevel.Level_11_0,
                },
                out var device,
                out _,
                out _);
            if (hr.Failure || device is null)
            {
                DebugLog.Write($"[mf] D3D11CreateDevice failed (HR=0x{(uint)hr.Code:X8}); falling back to system-memory encoder");
                return (null, null);
            }

            // The MF runtime requires the device to be flagged as
            // multithread-protected before being shared via the manager,
            // because MFT events fire on a different thread than the one
            // that fed the input sample.
            using var multithread = device.QueryInterface<ID3D11Multithread>();
            multithread.SetMultithreadProtected(true);

            var manager = MediaFactory.MFCreateDXGIDeviceManager();
            manager.ResetDevice(device);
            DebugLog.Write("[mf] D3D11 device + DXGI device manager created for encoder");
            return (device, manager);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[mf] TryCreateD3DManager threw: {ex.Message}; falling back to system-memory encoder");
            return (null, null);
        }
    }

    private static (IMFTransform? transform, bool inputIsBgra) TryCreateEncoder(
        uint flags,
        int width,
        int height,
        int fps,
        long bitrate,
        int gopFrames,
        bool tryAsync,
        bool tryHardware,
        string label,
        IMFDXGIDeviceManager? dxgiManager)
    {
        var outputFilter = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = VideoFormatGuids.H264,
        };

        using var collection = MediaFactory.MFTEnumEx(
            TransformCategoryGuids.VideoEncoder,
            flags,
            inputType: null,
            outputType: outputFilter);

        foreach (var activate in collection)
        {
            IMFTransform? transform = null;
            // Read friendly name + hardware URL BEFORE activating so we can
            // log which specific transform won the probe — otherwise NVENC
            // and Microsoft's software async encoder look identical in logs.
            var friendlyName = TryGetFriendlyName(activate);
            var hardwareUrl = TryGetHardwareUrl(activate);
            DebugLog.Write($"[mf] probing {label} H264 MFT candidate: {friendlyName} ({hardwareUrl})");
            try
            {
                transform = activate.ActivateObject<IMFTransform>();

                // Unlock async MFTs before any type negotiation. Without this
                // SetInputType / SetOutputType fail with MF_E_TRANSFORM_ASYNC_LOCKED.
                if (tryAsync)
                {
                    try
                    {
                        transform.Attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
                    }
                    catch (Exception ex)
                    {
                        DebugLog.Write($"[mf] probing {label} [{friendlyName}] — async unlock failed, skipping: {ex.Message}");
                        transform.Dispose();
                        continue;
                    }
                }

                // Hand the MFT our DXGI device manager so it can either run
                // its async event pump on the same D3D11 device (Phase 1) or
                // ingest D3D11 textures directly (Phase 2+). Many hardware
                // MFTs reject SET_D3D_MANAGER, which is fine — they fall
                // back to system-memory mode automatically.
                if (dxgiManager is not null && tryHardware)
                {
                    try
                    {
                        transform.ProcessMessage(TMessageType.MessageSetD3DManager, (nuint)(nint)dxgiManager.NativePointer);
                        DebugLog.Write($"[mf] {label} [{friendlyName}] accepted SET_D3D_MANAGER");
                    }
                    catch (Exception ex)
                    {
                        DebugLog.Write($"[mf] {label} [{friendlyName}] rejected SET_D3D_MANAGER (continuing in system-memory mode): {ex.Message}");
                    }
                }

                // Low-latency + CBR + target bitrate, written to the transform
                // attributes as CODECAPI properties. Must happen BEFORE the
                // type negotiation because NVENC snapshots its rate control
                // config at the point of SetOutputType. Without LowLatency=1
                // NVENC uses its quality preset with B-frames and lookahead,
                // which adds 2-3 seconds of encode latency — unusable for
                // realtime streaming.
                ApplyCodecApiAttributes(transform, bitrate, gopFrames, friendlyName);

                var (typesOk, inputIsBgra) = TrySetTypes(transform, width, height, fps, bitrate, $"{label} [{friendlyName}]");
                if (!typesOk)
                {
                    transform.Dispose();
                    continue;
                }

                DebugLog.Write($"[mf] picked {label} H264 MFT: {friendlyName} ({hardwareUrl})");
                return (transform, inputIsBgra);
            }
            catch (Exception ex)
            {
                DebugLog.Write($"[mf] probing {label} H264 MFT — activate threw, skipping: {ex.Message}");
                transform?.Dispose();
            }
        }

        return (null, false);
    }

    private static (bool ok, bool inputIsBgra) TrySetTypes(IMFTransform transform, int width, int height, int fps, long bitrate, string label)
    {
        var outputType = MediaFactory.MFCreateMediaType();
        outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        outputType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate);
        outputType.SetEnumValue(MediaTypeAttributeKeys.InterlaceMode, VideoInterlaceMode.Progressive);
        MediaFactory.MFSetAttributeSize(outputType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
        MediaFactory.MFSetAttributeRatio(outputType, MediaTypeAttributeKeys.FrameRate, (uint)fps, 1u);
        MediaFactory.MFSetAttributeRatio(outputType, MediaTypeAttributeKeys.PixelAspectRatio, 1u, 1u);

        try
        {
            transform.SetOutputType(0, outputType, 0);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[mf] probing {label} H264 MFT — SetOutputType rejected, skipping: {ex.Message}");
            outputType.Dispose();
            return (false, false);
        }
        outputType.Dispose();

        // Try ARGB32 input first (NVENC and most HW encoders take BGRA D3D11
        // textures and convert to NV12 on the GPU internally — saves us the
        // Compute Shader / VideoProcessorMFT chain entirely). Fall back to
        // NV12 if rejected.
        if (TrySetInputType(transform, VideoFormatGuids.Argb32, width, height, fps, label, "ARGB32"))
        {
            return (true, true);
        }
        if (TrySetInputType(transform, VideoFormatGuids.NV12, width, height, fps, label, "NV12"))
        {
            return (true, false);
        }

        return (false, false);
    }

    private static bool TrySetInputType(IMFTransform transform, Guid subtype, int width, int height, int fps, string label, string subtypeName)
    {
        var inputType = MediaFactory.MFCreateMediaType();
        inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        inputType.Set(MediaTypeAttributeKeys.Subtype, subtype);
        inputType.SetEnumValue(MediaTypeAttributeKeys.InterlaceMode, VideoInterlaceMode.Progressive);
        MediaFactory.MFSetAttributeSize(inputType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
        MediaFactory.MFSetAttributeRatio(inputType, MediaTypeAttributeKeys.FrameRate, (uint)fps, 1u);
        MediaFactory.MFSetAttributeRatio(inputType, MediaTypeAttributeKeys.PixelAspectRatio, 1u, 1u);

        try
        {
            transform.SetInputType(0, inputType, 0);
            DebugLog.Write($"[mf] {label} accepted {subtypeName} input");
            inputType.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[mf] {label} rejected {subtypeName} input: {ex.Message}");
            inputType.Dispose();
            return false;
        }
    }

    // ComCast removed: use transform.QueryInterface<IMFMediaEventGenerator>()
    // directly — SharpGen's base ComObject exposes the right COM QI call so
    // the returned wrapper holds an interface-specific pointer, not the
    // IMFTransform vtable reinterpreted through the wrong slots. Using the
    // wrong vtable was crashing EventPumpLoop with ExecutionEngineException
    // the moment we called GetEvent on it.

    // CODECAPI GUIDs from codecapi.h. These are standardized attribute keys
    // that hardware encoder MFTs (including NVENC) honor via their transform
    // attribute store.
    private static readonly Guid CodecApiAVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    private static readonly Guid CodecApiAVEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e1b1083");
    private static readonly Guid CodecApiAVEncCommonMaxBitRate = new("9651632c-a5ea-4830-88a0-5e64f2e66fe1");
    private static readonly Guid CodecApiAVEncCommonBufferSize = new("0db96574-b6a4-4c8b-8106-3773de0313cd");
    private static readonly Guid CodecApiAVEncCommonQualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");
    private static readonly Guid CodecApiAVEncCommonLowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid CodecApiAVEncMPVGOPSize = new("95f31b26-95a4-4f58-9ba5-4c1eb9e1f04b");
    private static readonly Guid CodecApiAVEncVideoForceKeyFrame = new("398C1B98-8353-475A-9EF2-8F265D2C8E14");
    private const uint RateControlModeCbr = 0;

    /// <summary>
    /// Writes realtime-streaming CODECAPI attributes onto the encoder
    /// transform. All of these are advisory — if the MFT doesn't recognize
    /// a particular key, <see cref="IMFAttributes.Set(Guid, uint)"/> either
    /// silently succeeds or throws, which we swallow. NVENC is the main
    /// consumer of LowLatency + the full CBR triple (MeanBitRate +
    /// MaxBitRate + BufferSize); the Microsoft software encoder honors
    /// MeanBitRate + RateControlMode + GopSize.
    ///
    /// LowLatency=1 is load-bearing: without it the encoder runs its
    /// quality preset with B-frames and lookahead, adding 2-3 seconds of
    /// pipeline lag and unusable for real-time streaming.
    ///
    /// Full CBR triple (MeanBitRate / MaxBitRate / BufferSize) is required
    /// for NVENC to actually hit the target bitrate in CBR mode. Without
    /// MaxBitRate + a one-second VBV, NVENC undershoots by 40-60% on
    /// low-motion content and produces visible chroma artifacts on
    /// solid backgrounds.
    ///
    /// QualityVsSpeed=70 is well inside NVENC's real-time budget and
    /// avoids the aggressive speed default that also contributes to the
    /// undershoot.
    ///
    /// GopSize comes from VideoSettings.KeyframeIntervalSeconds * fps so
    /// the user can trade off keyframe bitrate vs mid-stream join latency.
    /// </summary>
    private static void ApplyCodecApiAttributes(IMFTransform transform, long bitrate, int gopFrames, string friendlyName)
    {
        void TrySet(Guid key, uint value, string name)
        {
            try { transform.Attributes.Set(key, value); }
            catch (Exception ex)
            {
                DebugLog.Write($"[mf] {friendlyName} CODECAPI {name} rejected: {ex.Message}");
            }
        }

        TrySet(CodecApiAVEncCommonLowLatency, 1u, "LowLatency");
        TrySet(CodecApiAVEncCommonRateControlMode, RateControlModeCbr, "RateControlMode=CBR");

        var clamped = (uint)Math.Min(bitrate, uint.MaxValue);
        TrySet(CodecApiAVEncCommonMeanBitRate, clamped, "MeanBitRate");
        TrySet(CodecApiAVEncCommonMaxBitRate, clamped, "MaxBitRate");
        TrySet(CodecApiAVEncCommonBufferSize, clamped, "VBVBufferSize=1s");
        TrySet(CodecApiAVEncCommonQualityVsSpeed, 70u, "QualityVsSpeed");
        TrySet(CodecApiAVEncMPVGOPSize, (uint)Math.Max(1, gopFrames), "GopSize");
    }

    private static string TryGetFriendlyName(IMFActivate activate)
    {
        // IMFAttributes.FriendlyName reads CaptureDeviceAttributeKeys.FriendlyName
        // which only applies to video capture devices, NOT MFT transforms. For
        // MFTs the key is MftFriendlyNameAttribute.
        try
        {
            var name = activate.GetString(TransformAttributeKeys.MftFriendlyNameAttribute);
            return !string.IsNullOrEmpty(name) ? name : "(no name)";
        }
        catch
        {
            return "(unknown)";
        }
    }

    private static string TryGetHardwareUrl(IMFActivate activate)
    {
        try
        {
            var url = activate.GetString(TransformAttributeKeys.MftEnumHardwareUrlAttribute);
            return string.IsNullOrEmpty(url) ? "<software>" : url;
        }
        catch
        {
            return "<software>";
        }
    }

    /// <summary>
    /// Single-pass BGRA -> NV12 conversion with BT.601 full-range
    /// coefficients, unsafe pointer arithmetic, and 2x2 chroma averaging
    /// inlined with the Y plane computation so every source pixel is read
    /// exactly once. Processes pairs of rows at a time (the 2x2 chroma
    /// block) so we can write U/V next to the Y values they were derived
    /// from.
    ///
    /// Both <paramref name="width"/> and <paramref name="height"/> are
    /// assumed even (CaptureStreamer masks the source dims to even before
    /// calling). NV12 buffer layout: Y plane (w*h) followed by interleaved
    /// UV plane (w*h/2).
    /// </summary>
    private static unsafe void BgraToNv12Fast(byte[] bgra, int stride, byte[] nv12, int width, int height)
    {
        const int YR = 19595, YG = 38470, YB = 7471;              // 0.299/0.587/0.114 << 16
        const int UR = -11059, UG = -21709, UB = 32768;           // -0.169/-0.331/0.5
        const int VR = 32768, VG = -27439, VB = -5329;            //  0.5/-0.419/-0.081
        const int YRound = 32768;                                  // + 0.5 for Y
        const int UvRound = 128 * 65536 + 32768;                  // + 128 shifted + 0.5 for chroma

        var ySize = width * height;

        fixed (byte* srcBase = bgra)
        fixed (byte* dstBase = nv12)
        {
            var uvOut = dstBase + ySize;

            for (var y = 0; y < height; y += 2)
            {
                var src0 = srcBase + y * stride;
                var src1 = srcBase + (y + 1) * stride;
                var yOut0 = dstBase + y * width;
                var yOut1 = dstBase + (y + 1) * width;

                for (var x = 0; x < width; x += 2)
                {
                    // Four BGRA pixels per 2x2 block. Each pixel = 4 bytes:
                    // B=0, G=1, R=2, A=3.
                    int b00 = src0[0], g00 = src0[1], r00 = src0[2];
                    int b01 = src0[4], g01 = src0[5], r01 = src0[6];
                    int b10 = src1[0], g10 = src1[1], r10 = src1[2];
                    int b11 = src1[4], g11 = src1[5], r11 = src1[6];

                    // Y for each pixel: clamped 16.16 fixed-point BT.601.
                    var y00 = (YR * r00 + YG * g00 + YB * b00 + YRound) >> 16;
                    var y01 = (YR * r01 + YG * g01 + YB * b01 + YRound) >> 16;
                    var y10 = (YR * r10 + YG * g10 + YB * b10 + YRound) >> 16;
                    var y11 = (YR * r11 + YG * g11 + YB * b11 + YRound) >> 16;
                    yOut0[0] = (byte)(y00 < 0 ? 0 : y00 > 255 ? 255 : y00);
                    yOut0[1] = (byte)(y01 < 0 ? 0 : y01 > 255 ? 255 : y01);
                    yOut1[0] = (byte)(y10 < 0 ? 0 : y10 > 255 ? 255 : y10);
                    yOut1[1] = (byte)(y11 < 0 ? 0 : y11 > 255 ? 255 : y11);

                    // Chroma on the averaged 2x2 block. Sum then shift >> 2
                    // is cheaper than dividing each channel separately.
                    var sR = r00 + r01 + r10 + r11;
                    var sG = g00 + g01 + g10 + g11;
                    var sB = b00 + b01 + b10 + b11;

                    // UR/UG/UB coefficients are still in 16.16 but we sum
                    // 4 pixels, so the natural >> 18 gives the average.
                    var u = (UR * sR + UG * sG + UB * sB + (UvRound << 2)) >> 18;
                    var v = (VR * sR + VG * sG + VB * sB + (UvRound << 2)) >> 18;
                    uvOut[0] = (byte)(u < 0 ? 0 : u > 255 ? 255 : u);
                    uvOut[1] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

                    src0 += 8; src1 += 8;
                    yOut0 += 2; yOut1 += 2;
                    uvOut += 2;
                }
            }
        }
    }
}
