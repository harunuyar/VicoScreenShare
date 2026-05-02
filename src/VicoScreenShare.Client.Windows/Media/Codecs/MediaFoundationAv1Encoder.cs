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
/// AV1 encoder wrapping a Media Foundation <see cref="IMFTransform"/>.
///
/// Targets the AV1 encoder MFTs that current Windows GPU drivers register:
/// NVIDIA's "NVIDIA AV1 Encoder MFT" on RTX 40+, Intel Quick Sync on Arc /
/// Xe2 / 12th-gen+ iGPUs, and AMD AMF on RDNA 3+ (RX 7000 series). NVIDIA
/// also exposes AV1 through the NVENC SDK directly
/// (see <c>Nvenc/NvencAv1Encoder</c>) — that's the preferred path on
/// NVIDIA hardware because it gives us the NVENC-only knobs (temporal AQ,
/// lookahead, intra-refresh, custom VBV) that the MFT shim doesn't
/// expose. Picking <see cref="Av1EncoderBackend.Mft"/> instead of NVENC
/// SDK on an NVIDIA box still works — it just routes through NVIDIA's
/// MFT shim and loses the SDK-only knobs.
///
/// Microsoft does not ship a software AV1 encoder MFT — if no GPU
/// vendor's encoder MFT is registered, <see cref="HasAv1EncoderInstalled"/>
/// returns false and the factory hides this backend from the codec
/// catalog.
///
/// Mirrors <see cref="MediaFoundationH264Encoder"/> structurally — same async
/// event pump, same CODECAPI surface, same probe order. Only the output
/// subtype (<see cref="MFVideoFormatAv1"/>) and the codec-specific log /
/// error strings differ. Two parallel files instead of a parametric base
/// class so neither codec's pipeline can break the other; same convention
/// the decoder side uses (<c>MediaFoundationH264Decoder</c> +
/// <c>MediaFoundationAv1Decoder</c>).
///
/// Probe order inside the Media Foundation video-encoder category:
///   1. hardware async (NVIDIA / Intel QSV / AMD AMF AV1) — preferred
///   2. hardware sync — older driver builds
///   3. (no software fallback — Microsoft does not ship one for AV1)
/// </summary>
public sealed unsafe class MediaFoundationAv1Encoder : IVideoEncoder, IAsyncEncodedOutputSource
{
    /// <summary>
    /// MFVideoFormat_AV1 — FOURCC 'AV01' as the leading DWORD of the
    /// standard Microsoft media GUID tail. Vortice doesn't yet expose this
    /// constant, so we define it locally — same definition as the AV1
    /// decoder uses.
    /// </summary>
    private static readonly Guid MFVideoFormatAv1 = new(
        0x31305641, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);

    /// <summary>
    /// Probe Media Foundation for an AV1 encoder MFT. Used by the factory
    /// to gate AV1 encoder availability — on machines without an Intel Arc
    /// / Xe2 iGPU or AMD RDNA 3+ GPU, MFTEnumEx returns nothing for AV1
    /// and the codec catalog suppresses the MFT path so the user can't
    /// pick it. Cheap (just enumerates registry-based MFTs); safe to call
    /// from the factory's <c>IsAvailable</c> getter.
    /// </summary>
    public static bool HasAv1EncoderInstalled()
    {
        var outputFilter = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = MFVideoFormatAv1,
        };
        try
        {
            var hwAsyncFlags = (uint)(EnumFlag.EnumFlagHardware
                                      | EnumFlag.EnumFlagAsyncmft
                                      | EnumFlag.EnumFlagSortandfilter);
            using var hwAsync = MediaFactory.MFTEnumEx(
                TransformCategoryGuids.VideoEncoder, hwAsyncFlags, inputType: null, outputType: outputFilter);
            foreach (var act in hwAsync)
            {
                var name = TryGetFriendlyName(act);
                DebugLog.Write($"[mft-av1] probe found hardware async candidate: {name}");
                return true;
            }

            var hwSyncFlags = (uint)(EnumFlag.EnumFlagHardware
                                     | EnumFlag.EnumFlagSyncmft
                                     | EnumFlag.EnumFlagSortandfilter);
            using var hwSync = MediaFactory.MFTEnumEx(
                TransformCategoryGuids.VideoEncoder, hwSyncFlags, inputType: null, outputType: outputFilter);
            foreach (var act in hwSync)
            {
                var name = TryGetFriendlyName(act);
                DebugLog.Write($"[mft-av1] probe found hardware sync candidate: {name}");
                return true;
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[mft-av1] probe threw: {ex.Message}");
        }
        DebugLog.Write("[mft-av1] probe found no AV1 encoder MFT registered");
        return false;
    }

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

    public MediaFoundationAv1Encoder(int width, int height, int fps, long bitrate, int gopFrames, bool useLanczos = false)
        : this(width, height, fps, bitrate, gopFrames, useLanczos, externalDevice: null)
    {
    }

    public MediaFoundationAv1Encoder(
        int width, int height, int fps, long bitrate, int gopFrames,
        bool useLanczos, ID3D11Device? externalDevice)
    {
        _width = width;
        _height = height;
        _fps = Math.Max(1, fps);
        _bitrate = Math.Max(500_000, bitrate);
        _gopFrames = Math.Max(1, gopFrames);
        _useLanczos = useLanczos;

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
        DebugLog.Write($"[mft-av1] activated {width}x{height}@{_fps} {_bitrate} bps ({pickedLabel}, input={(inputIsBgra ? "BGRA" : "NV12")})");

        if (_isAsync)
        {
            _events = _transform.QueryInterface<IMFMediaEventGenerator>();
            _pumpThread = new Thread(EventPumpLoop)
            {
                IsBackground = true,
                Name = "MF AV1 event pump",
            };
            _pumpThread.Start();
        }

        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);
        if (_isAsync)
        {
            _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, 0);
        }
    }

    public VideoCodec Codec => VideoCodec.Av1;

    public int Width => _width;

    public int Height => _height;

    public bool SupportsTextureInput => _d3dDevice is not null && _inputIsBgra;

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
                DebugLog.Write("[mft-av1] ForceKeyFrame set for next input");
            }
            catch (Exception ex)
            {
                DebugLog.Write($"[mft-av1] ForceKeyFrame threw: {ex.Message}");
            }
        }
    }

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
                DebugLog.Write($"[mft-av1] bitrate → {bitsPerSecond / 1000} kbps");
            }
            catch (Exception ex)
            {
                DebugLog.Write($"[mft-av1] UpdateBitrate threw: {ex.Message}");
            }
        }
    }

    public ID3D11Device? D3D11Device => _d3dDevice;

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
            DebugLog.Write($"[mft-av1] texture scaler threw: {ex.Message}");
            return null;
        }

        return EncodeTexture(_encoderInputTexture!, inputTimestamp);
    }

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
                DebugLog.Write($"[mft-av1] Lanczos scaler failed ({ex.Message}), falling back to bilinear");
                _textureScaler = new D3D11VideoScaler(_d3dDevice, srcWidth, srcHeight, _width, _height);
            }
        }
        else
        {
            _textureScaler = new D3D11VideoScaler(_d3dDevice!, srcWidth, srcHeight, _width, _height);
        }
        _textureScalerSrcWidth = srcWidth;
        _textureScalerSrcHeight = srcHeight;

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
        DebugLog.Write($"[mft-av1] texture pipeline built {srcWidth}x{srcHeight} -> {_width}x{_height} ({scalerName})");
    }

    public EncodedFrame? EncodeTexture(ID3D11Texture2D texture, TimeSpan inputTimestamp)
    {
        if (_disposed)
        {
            return null;
        }

        if (_d3dDevice is null)
        {
            DebugLog.Write("[mft-av1] EncodeTexture called but encoder has no D3D device");
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
        var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(iidTexture, texture, 0, false);

        var nv12Size = _width * _height * 3 / 2;
        buffer.CurrentLength = nv12Size;

        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);

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
                DebugLog.Write($"[mft-av1] async EncodeTexture ProcessInput threw: {ex.Message}");
                return null;
            }
        }

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
                DebugLog.Write($"[mft-av1] sync EncodeTexture ProcessInput threw: {ex.Message}");
                return null;
            }
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
            DebugLog.Write($"[mft-av1-timing #{_loggedTimingFrames}] {label}={convertMs:F1}ms encode={encodeMs:F1}ms total={totalMs:F1}ms");
        }

        if (encoded is { Bytes.Length: > 0 } && _loggedEncodedFrames < 3)
        {
            _loggedEncodedFrames++;
            DebugLog.Write($"[mft-av1] produced frame {_loggedEncodedFrames} ({encoded.Value.Bytes.Length} bytes, pts={encoded.Value.Timestamp.TotalMilliseconds:F2}ms)");
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
                DebugLog.Write($"[mft-av1] async EncodeAsync timeout #{_loggedTimeouts} — no NeedInput credit within 50ms");
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
                        DebugLog.Write($"[mft-av1] async ProcessInput #{_loggedProcessInputs} ok");
                    }
                }
                catch (Exception ex)
                {
                    DebugLog.Write($"[mft-av1] async ProcessInput threw: {ex.Message}");
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
                    DebugLog.Write($"[mft-av1] sync ProcessInput threw: {ex.Message}");
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

    private const int MftOutputStreamProvidesSamples = 0x100;

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
            Sample = clientSample,
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
                DebugLog.Write($"[mft-av1] ProcessOutput failed HRESULT 0x{(uint)hr.Code:X8}");
                result = DrainResult.Failure;
                return null;
            }

            readSample = db.Sample;
            if (readSample is null)
            {
                DebugLog.Write("[mft-av1] ProcessOutput returned success but db.Sample is null");
                result = DrainResult.Failure;
                return null;
            }

            try { sampleTimeTicks = readSample.SampleTime; }
            catch { sampleTimeTicks = 0; }

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
            if (readSample is not null && !ReferenceEquals(readSample, clientSample))
            {
                try { readSample.Dispose(); } catch { }
            }
            clientSample?.Dispose();
            clientBuffer?.Dispose();
        }
    }

    private void EventPumpLoop()
    {
        DebugLog.Write("[mft-av1] async event pump thread started");
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
                    DebugLog.Write($"[mft-av1] async event pump GetEvent threw: {ex.GetType().Name}: {ex.Message}");
                    return;
                }
                if (evt is null)
                {
                    DebugLog.Write("[mft-av1] async event pump GetEvent returned null");
                    continue;
                }

                try
                {
                    var type = (MediaEventTypes)evt.EventType;
                    if (loggedEvents < 8)
                    {
                        loggedEvents++;
                        DebugLog.Write($"[mft-av1] async event #{loggedEvents}: {type}");
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
                                DebugLog.Write("[mft-av1] async pump PullSingleOutput returned Failure");
                            }
                        }
                        if (output is { Length: > 0 })
                        {
                            var ts = TimeSpan.FromTicks(sampleTimeTicks);
                            _outputQueue.Enqueue(new EncodedFrame(output, ts));
                            try { _outputAvailable?.Invoke(); } catch { }
                        }
                        else if (loggedEvents <= 8)
                        {
                            DebugLog.Write($"[mft-av1] async pump HaveOutput produced {output?.Length ?? -1} bytes");
                        }
                    }
                    else if (type == MediaEventTypes.TransformDrainComplete)
                    {
                        DebugLog.Write("[mft-av1] async event pump drain complete, exiting");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    DebugLog.Write($"[mft-av1] async event pump handler exception: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    evt.Dispose();
                }
            }
        }
        finally
        {
            DebugLog.Write("[mft-av1] async event pump thread exiting");
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
        _pumpThread?.Join(TimeSpan.FromMilliseconds(500));
        try { _events?.Dispose(); } catch { }
        try { _transform.Dispose(); } catch { }
        try { _textureScaler?.Dispose(); } catch { }
        try { _encoderInputTexture?.Dispose(); } catch { }
        try { _dxgiManager?.Dispose(); } catch { }
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
        var async = TryCreateEncoder(
            (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagAsyncmft | EnumFlag.EnumFlagSortandfilter),
            width, height, fps, bitrate, gopFrames, tryAsync: true, tryHardware: true, label: "hardware async", dxgiManager);
        if (async.transform is not null)
        {
            return (async.transform, true, "hardware async", async.inputIsBgra);
        }

        var sync = TryCreateEncoder(
            (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagSortandfilter),
            width, height, fps, bitrate, gopFrames, tryAsync: false, tryHardware: true, label: "hardware sync", dxgiManager);
        if (sync.transform is not null)
        {
            return (sync.transform, false, "hardware sync", sync.inputIsBgra);
        }

        // No software AV1 encoder MFT in stock Windows — bail rather than
        // returning a hung pipeline. Caller (the factory selector) catches
        // this and falls back to NVENC if it can, or to H.264 otherwise.
        throw new InvalidOperationException("Media Foundation has no usable AV1 encoder on this machine");
    }

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
            DebugLog.Write("[mft-av1] wrapped external D3D11 device in DXGI device manager");
            return manager;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[mft-av1] TryWrapExternalDevice threw: {ex.Message}; encoder will run without GPU manager");
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
                DebugLog.Write($"[mft-av1] D3D11CreateDevice failed (HR=0x{(uint)hr.Code:X8}); falling back to system-memory encoder");
                return (null, null);
            }

            using var multithread = device.QueryInterface<ID3D11Multithread>();
            multithread.SetMultithreadProtected(true);

            var manager = MediaFactory.MFCreateDXGIDeviceManager();
            manager.ResetDevice(device);
            DebugLog.Write("[mft-av1] D3D11 device + DXGI device manager created for encoder");
            return (device, manager);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[mft-av1] TryCreateD3DManager threw: {ex.Message}; falling back to system-memory encoder");
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
            GuidSubtype = MFVideoFormatAv1,
        };

        using var collection = MediaFactory.MFTEnumEx(
            TransformCategoryGuids.VideoEncoder,
            flags,
            inputType: null,
            outputType: outputFilter);

        foreach (var activate in collection)
        {
            IMFTransform? transform = null;
            var friendlyName = TryGetFriendlyName(activate);
            var hardwareUrl = TryGetHardwareUrl(activate);
            DebugLog.Write($"[mft-av1] probing {label} candidate: {friendlyName} ({hardwareUrl})");
            try
            {
                transform = activate.ActivateObject<IMFTransform>();

                if (tryAsync)
                {
                    try
                    {
                        transform.Attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
                    }
                    catch (Exception ex)
                    {
                        DebugLog.Write($"[mft-av1] {label} [{friendlyName}] async unlock failed, skipping: {ex.Message}");
                        transform.Dispose();
                        continue;
                    }
                }

                if (dxgiManager is not null && tryHardware)
                {
                    try
                    {
                        transform.ProcessMessage(TMessageType.MessageSetD3DManager, (nuint)(nint)dxgiManager.NativePointer);
                        DebugLog.Write($"[mft-av1] {label} [{friendlyName}] accepted SET_D3D_MANAGER");
                    }
                    catch (Exception ex)
                    {
                        DebugLog.Write($"[mft-av1] {label} [{friendlyName}] rejected SET_D3D_MANAGER (continuing in system-memory mode): {ex.Message}");
                    }
                }

                ApplyCodecApiAttributes(transform, bitrate, gopFrames, friendlyName);

                var (typesOk, inputIsBgra) = TrySetTypes(transform, width, height, fps, bitrate, $"{label} [{friendlyName}]");
                if (!typesOk)
                {
                    transform.Dispose();
                    continue;
                }

                DebugLog.Write($"[mft-av1] picked {label}: {friendlyName} ({hardwareUrl})");
                return (transform, inputIsBgra);
            }
            catch (Exception ex)
            {
                DebugLog.Write($"[mft-av1] probing {label} — activate threw, skipping: {ex.Message}");
                transform?.Dispose();
            }
        }

        return (null, false);
    }

    private static (bool ok, bool inputIsBgra) TrySetTypes(IMFTransform transform, int width, int height, int fps, long bitrate, string label)
    {
        var outputType = MediaFactory.MFCreateMediaType();
        outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        outputType.Set(MediaTypeAttributeKeys.Subtype, MFVideoFormatAv1);
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
            DebugLog.Write($"[mft-av1] probing {label} — SetOutputType rejected, skipping: {ex.Message}");
            outputType.Dispose();
            return (false, false);
        }
        outputType.Dispose();

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
            DebugLog.Write($"[mft-av1] {label} accepted {subtypeName} input");
            inputType.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[mft-av1] {label} rejected {subtypeName} input: {ex.Message}");
            inputType.Dispose();
            return false;
        }
    }

    // CODECAPI GUIDs from codecapi.h. Same set as the H.264 path — these are
    // codec-agnostic by design. Whether each AV1 MFT honors a given key
    // depends on the vendor (Intel and AMD document support; the TrySet
    // helper silently no-ops on rejection).
    private static readonly Guid CodecApiAVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    private static readonly Guid CodecApiAVEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e1b1083");
    private static readonly Guid CodecApiAVEncCommonMaxBitRate = new("9651632c-a5ea-4830-88a0-5e64f2e66fe1");
    private static readonly Guid CodecApiAVEncCommonQualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");
    private static readonly Guid CodecApiAVEncCommonLowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid CodecApiAVEncMPVGOPSize = new("95f31b26-95a4-4f58-9ba5-4c1eb9e1f04b");
    private static readonly Guid CodecApiAVEncVideoForceKeyFrame = new("398C1B98-8353-475A-9EF2-8F265D2C8E14");
    private const uint RateControlModeCbr = 0;

    private static void ApplyCodecApiAttributes(IMFTransform transform, long bitrate, int gopFrames, string friendlyName)
    {
        void TrySet(Guid key, uint value, string name)
        {
            try { transform.Attributes.Set(key, value); }
            catch (Exception ex)
            {
                DebugLog.Write($"[mft-av1] {friendlyName} CODECAPI {name} rejected: {ex.Message}");
            }
        }

        TrySet(CodecApiAVEncCommonLowLatency, 1u, "LowLatency");
        TrySet(CodecApiAVEncCommonRateControlMode, RateControlModeCbr, "RateControlMode=CBR");

        var clamped = (uint)Math.Min(bitrate, uint.MaxValue);
        TrySet(CodecApiAVEncCommonMeanBitRate, clamped, "MeanBitRate");
        TrySet(CodecApiAVEncCommonMaxBitRate, clamped, "MaxBitRate");
        TrySet(CodecApiAVEncCommonQualityVsSpeed, 70u, "QualityVsSpeed");
        TrySet(CodecApiAVEncMPVGOPSize, (uint)Math.Max(1, gopFrames), "GopSize");
    }

    private static string TryGetFriendlyName(IMFActivate activate)
    {
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
                    int b00 = src0[0], g00 = src0[1], r00 = src0[2];
                    int b01 = src0[4], g01 = src0[5], r01 = src0[6];
                    int b10 = src1[0], g10 = src1[1], r10 = src1[2];
                    int b11 = src1[4], g11 = src1[5], r11 = src1[6];

                    var y00 = (YR * r00 + YG * g00 + YB * b00 + YRound) >> 16;
                    var y01 = (YR * r01 + YG * g01 + YB * b01 + YRound) >> 16;
                    var y10 = (YR * r10 + YG * g10 + YB * b10 + YRound) >> 16;
                    var y11 = (YR * r11 + YG * g11 + YB * b11 + YRound) >> 16;
                    yOut0[0] = (byte)(y00 < 0 ? 0 : y00 > 255 ? 255 : y00);
                    yOut0[1] = (byte)(y01 < 0 ? 0 : y01 > 255 ? 255 : y01);
                    yOut1[0] = (byte)(y10 < 0 ? 0 : y10 > 255 ? 255 : y10);
                    yOut1[1] = (byte)(y11 < 0 ? 0 : y11 > 255 ? 255 : y11);

                    var sR = r00 + r01 + r10 + r11;
                    var sG = g00 + g01 + g10 + g11;
                    var sB = b00 + b01 + b10 + b11;

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
