using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ScreenSharing.Client.Diagnostics;
using ScreenSharing.Client.Media.Codecs;
using ScreenSharing.Client.Windows.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace ScreenSharing.Client.Windows.Media.Codecs;

/// <summary>
/// H.264 decoder on top of Media Foundation's H.264 decoder MFT. Hardware
/// DXVA decoder preferred, software fallback.
///
/// Two modes:
///  - GPU texture mode (preferred, when an external D3D11 device is passed):
///    the decoder MFT is given a DXGI device manager for our shared device,
///    outputs land as NV12 D3D11 textures, and a <see cref="D3D11VideoScaler"/>
///    converts NV12 → BGRA on the GPU in one VideoProcessorBlt. A single
///    CPU readback of the BGRA result happens at the end, which is the
///    mirror of the encoder path.
///  - System-memory fallback: legacy path, used when no shared device is
///    available. Pulls NV12 from the MFT into system memory and runs a CPU
///    scalar NV12 → BGRA loop.
///
/// Frame dimensions are learned from the first <c>MF_E_TRANSFORM_STREAM_CHANGE</c>
/// the decoder raises once it has parsed the stream's SPS.
/// </summary>
internal sealed unsafe class MediaFoundationH264Decoder : IVideoDecoder
{
    // Same GUID as the encoder's AVEncCommonLowLatency. The decoder MFT's
    // default is "wait for a full H.264 reorder group before releasing any
    // output" — on a low-latency no-B-frame stream that produces a ~20
    // frame startup bubble AND a permanent 20-frame pipeline lag, both of
    // which kill real-time playback. Forcing LowLatencyMode=1 tells the
    // MFT there are no B-frames so it can release each frame as soon as
    // it's decoded.
    private static readonly Guid CodecApiAVLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    private const uint MF_E_TRANSFORM_STREAM_CHANGE = 0xC00D6D61u;
    private const uint MF_E_TRANSFORM_NEED_MORE_INPUT = 0xC00D6D72u;
    private const uint MF_E_TRANSFORM_TYPE_NOT_SET = 0xC00D6D60u;
    // MFT_OUTPUT_STREAM_INFO_FLAGS.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x00000100
    private const int MftOutputStreamProvidesSamples = 0x100;
    private bool _warnedTypeNotSet;

    private readonly IMFTransform _transform;
    private readonly ID3D11Device? _d3dDevice;
    private readonly IMFDXGIDeviceManager? _dxgiManager;
    private readonly bool _useD3dPath;
    private D3D11VideoScaler? _scaler;
    private ID3D11Texture2D? _bgraStaging;
    private int _bgraStagingWidth;
    private int _bgraStagingHeight;
    private int _width;
    private int _height;
    private bool _outputTypeNegotiated;

    private long _loggedDecodedFrames;
    private bool _disposed;

    public MediaFoundationH264Decoder() : this(externalDevice: null)
    {
    }

    public MediaFoundationH264Decoder(ID3D11Device? externalDevice)
    {
        // Bind to the caller's shared device when provided. The wrapper
        // manager lets the MFT see this exact device on SET_D3D_MANAGER
        // so the decoded NV12 textures land on the same device our
        // D3D11VideoScaler runs on — no shared handles, no cross-device
        // copies. Mirrors the encoder path.
        if (externalDevice is not null)
        {
            _d3dDevice = externalDevice;
            _dxgiManager = TryWrapExternalDevice(externalDevice);
        }

        _transform = CreateDecoder()
                     ?? throw new InvalidOperationException("No H.264 decoder MFT is available from Media Foundation");

        // Force the decoder into low-latency mode BEFORE any type
        // negotiation. Without this the MF H.264 decoder waits for a full
        // reorder group (~20 frames) before producing anything, which on
        // a low-latency stream with no B-frames is pure pipeline lag.
        try
        {
            _transform.Attributes.Set(CodecApiAVLowLatencyMode, 1u);
            DebugLog.Write("[mf] H264 decoder LowLatencyMode=1 applied");
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[mf] H264 decoder LowLatencyMode rejected: {ex.Message}");
        }

        // Attach the DXGI device manager BEFORE type negotiation so the
        // MFT's output types are the D3D11-aware ones (textures, not
        // sysmem NV12).
        // Attach the DXGI device manager BEFORE type negotiation so the
        // MFT's output types are the D3D11-aware ones (textures, not
        // sysmem NV12).
        if (_dxgiManager is not null)
        {
            try
            {
                _transform.ProcessMessage(
                    TMessageType.MessageSetD3DManager,
                    (nuint)(nint)_dxgiManager.NativePointer);
                _useD3dPath = true;
                DebugLog.Write("[mf] H264 decoder accepted SET_D3D_MANAGER (GPU path)");
            }
            catch (Exception ex)
            {
                DebugLog.Write($"[mf] H264 decoder rejected SET_D3D_MANAGER: {ex.Message} (falling back to system-memory)");
                _useD3dPath = false;
            }
        }

        // Media Foundation's H.264 decoder requires both input AND output
        // types to be set before ProcessInput is called. Without an output
        // type, ProcessOutput fails with MF_E_TRANSFORM_TYPE_NOT_SET
        // (0xC00D6D60) and the MFT goes into a wedged state where
        // ProcessInput returns MF_E_NOTACCEPTING. The initial output type
        // can be a generic NV12 — the decoder raises STREAM_CHANGE with
        // real dimensions on the first keyframe and we re-negotiate.
        var inputType = MediaFactory.MFCreateMediaType();
        inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        inputType.SetEnumValue(MediaTypeAttributeKeys.InterlaceMode, VideoInterlaceMode.Progressive);
        _transform.SetInputType(0, inputType, 0);
        inputType.Dispose();

        NegotiateOutputType();
        if (!_outputTypeNegotiated)
        {
            DebugLog.Write("[mf] H264 decoder could not pick an NV12 output type at init");
        }

        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);
    }

    /// <summary>
    /// Wrap an externally-owned D3D11 device in an IMFDXGIDeviceManager
    /// without taking ownership. Same pattern the encoder uses.
    /// </summary>
    private static IMFDXGIDeviceManager? TryWrapExternalDevice(ID3D11Device device)
    {
        try
        {
            using (var mt = device.QueryInterfaceOrNull<ID3D11Multithread>())
            {
                mt?.SetMultithreadProtected(true);
            }
            var manager = MediaFactory.MFCreateDXGIDeviceManager();
            manager.ResetDevice(device);
            return manager;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[mf] decoder TryWrapExternalDevice threw: {ex.Message}");
            return null;
        }
    }

    public VideoCodec Codec => VideoCodec.H264;

    public IReadOnlyList<DecodedVideoFrame> Decode(byte[] encodedSample, TimeSpan inputTimestamp)
    {
        if (_disposed || encodedSample is null || encodedSample.Length == 0)
        {
            return Array.Empty<DecodedVideoFrame>();
        }

        var inputBuffer = MediaFactory.MFCreateMemoryBuffer(encodedSample.Length);
        inputBuffer.Lock(out nint ptr, out _, out _);
        Marshal.Copy(encodedSample, 0, ptr, encodedSample.Length);
        inputBuffer.Unlock();
        inputBuffer.CurrentLength = encodedSample.Length;

        var inputSample = MediaFactory.MFCreateSample();
        inputSample.AddBuffer(inputBuffer);

        // Stamp with the content clock so the MFT propagates it to the
        // output sample. DrainOutput then reads the output SampleTime and
        // attaches it to each DecodedVideoFrame — when the decoder yields
        // multiple outputs at once (or a buffered older frame alongside a
        // new one) each carries its OWN original input's timestamp, not
        // the current call's value.
        inputSample.SampleTime = inputTimestamp.Ticks;

        try
        {
            _transform.ProcessInput(0, inputSample, 0);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[mf] H264 decoder ProcessInput threw: {ex.Message}");
            return Array.Empty<DecodedVideoFrame>();
        }
        finally
        {
            inputSample.Dispose();
            inputBuffer.Dispose();
        }

        return DrainOutput();
    }

    private IReadOnlyList<DecodedVideoFrame> DrainOutput()
    {
        List<DecodedVideoFrame>? results = null;

        while (true)
        {
            var streamInfo = _transform.GetOutputStreamInfo(0);
            var mftAllocatesSamples = (streamInfo.Flags & MftOutputStreamProvidesSamples) != 0;

            // Allocation path splits here:
            //  - In D3D11 / hardware mode the MFT almost always sets
            //    MFT_OUTPUT_STREAM_PROVIDES_SAMPLES: we pass a null Sample
            //    and the MFT hands back one with an IMFDXGIBuffer that
            //    wraps its internal texture pool.
            //  - In the system-memory path we allocate our own sample
            //    with a plain IMFMediaBuffer like the original code did.
            IMFSample? outSample = null;
            IMFMediaBuffer? outBuffer = null;
            if (!mftAllocatesSamples)
            {
                var bufferSize = streamInfo.Size == 0 ? (_width * _height * 3 / 2) : streamInfo.Size;
                if (bufferSize <= 0) bufferSize = 1;
                outSample = MediaFactory.MFCreateSample();
                outBuffer = MediaFactory.MFCreateMemoryBuffer(bufferSize);
                outSample.AddBuffer(outBuffer);
            }

            var db = new OutputDataBuffer
            {
                StreamID = 0,
                Sample = outSample!,
            };
            var result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref db, out _);

            // When the MFT allocates, it fills Sample on success.
            if (mftAllocatesSamples)
            {
                outSample = db.Sample;
            }

            try
            {
                if ((uint)result.Code == MF_E_TRANSFORM_NEED_MORE_INPUT)
                {
                    return (IReadOnlyList<DecodedVideoFrame>?)results ?? Array.Empty<DecodedVideoFrame>();
                }

                if ((uint)result.Code == MF_E_TRANSFORM_STREAM_CHANGE)
                {
                    NegotiateOutputType();
                    continue;
                }

                if ((uint)result.Code == MF_E_TRANSFORM_TYPE_NOT_SET)
                {
                    if (!_outputTypeNegotiated)
                    {
                        NegotiateOutputType();
                        if (_outputTypeNegotiated) continue;
                    }
                    if (!_warnedTypeNotSet)
                    {
                        _warnedTypeNotSet = true;
                        DebugLog.Write("[mf] H264 decoder ProcessOutput stuck with TYPE_NOT_SET; decoder output format could not be negotiated");
                    }
                    return (IReadOnlyList<DecodedVideoFrame>?)results ?? Array.Empty<DecodedVideoFrame>();
                }

                if (result.Failure)
                {
                    DebugLog.Write($"[mf] H264 decoder ProcessOutput failed HRESULT 0x{(uint)result.Code:X8}");
                    return (IReadOnlyList<DecodedVideoFrame>?)results ?? Array.Empty<DecodedVideoFrame>();
                }

                if (!_outputTypeNegotiated || _width <= 0 || _height <= 0 || outSample is null) continue;

                // Read the propagated SampleTime BEFORE we flatten / free
                // the sample. The MFT copies this from the input sample
                // that ACTUALLY produced this output, so a buffered older
                // frame released alongside a newer one carries its own
                // original timestamp, not the current Decode() call's.
                long outSampleTimeTicks;
                try { outSampleTimeTicks = outSample.SampleTime; }
                catch { outSampleTimeTicks = 0; }

                byte[]? bgra;
                if (_useD3dPath)
                {
                    bgra = DecodeSampleToBgraViaGpu(outSample);
                }
                else
                {
                    using var sampleBuffer = outSample.ConvertToContiguousBuffer();
                    sampleBuffer.Lock(out nint framePtr, out _, out int curLen);
                    byte[] nv12;
                    try
                    {
                        nv12 = new byte[curLen];
                        Marshal.Copy(framePtr, nv12, 0, curLen);
                    }
                    finally
                    {
                        sampleBuffer.Unlock();
                    }
                    bgra = ConvertNv12ToBgra(nv12, _width, _height);
                }

                if (bgra is null) continue;

                var outTs = TimeSpan.FromTicks(outSampleTimeTicks);
                (results ??= new List<DecodedVideoFrame>()).Add(new DecodedVideoFrame(bgra, _width, _height, outTs));

                if (_loggedDecodedFrames < 10)
                {
                    DebugLog.Write($"[ts-dec-out] pts={outTs.TotalMilliseconds:F2}ms count={results.Count} ({_width}x{_height}, path={(_useD3dPath ? "GPU" : "CPU")})");
                }
                if (_loggedDecodedFrames < 3)
                {
                    _loggedDecodedFrames++;
                }
            }
            finally
            {
                outSample?.Dispose();
                outBuffer?.Dispose();
            }
        }
    }

    /// <summary>
    /// Hot path of the GPU decode mode. Takes a decoder-output IMFSample,
    /// extracts the ID3D11Texture2D inside it via IMFDXGIBuffer, runs the
    /// D3D11 Video Processor (NV12 → BGRA) into our reusable staging
    /// texture, then maps the staging texture once and copies the BGRA
    /// bytes into a managed array. One readback per decoded frame. No
    /// CPU scalar YUV math, no allocation of a second intermediate.
    /// </summary>
    private byte[]? DecodeSampleToBgraViaGpu(IMFSample sample)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        IMFDXGIBuffer? dxgiBuffer;
        try { dxgiBuffer = buffer.QueryInterface<IMFDXGIBuffer>(); }
        catch { return null; }

        ID3D11Texture2D? sourceTexture = null;
        try
        {
            var ptr = dxgiBuffer.GetResource(typeof(ID3D11Texture2D).GUID);
            if (ptr == IntPtr.Zero) return null;
            sourceTexture = new ID3D11Texture2D(ptr);

            // DXVA decoders output into a texture ARRAY — each DPB
            // (decoded picture buffer) slot is a different array slice.
            // The SubresourceIndex from IMFDXGIBuffer tells us which
            // slice this frame's NV12 data lives in. We must pass it
            // to the scaler so the VideoProcessorBlt input view reads
            // from the correct slice, not always slice 0 (which holds
            // stale data from an earlier decode).
            uint arraySlice = 0;
            try { arraySlice = dxgiBuffer.SubresourceIndex; }
            catch { }

            EnsureScalerAndStaging(_width, _height);
            _scaler!.Process(sourceTexture, _bgraDest!, arraySlice);
            return ReadbackBgra();
        }
        finally
        {
            sourceTexture?.Dispose();
            dxgiBuffer.Dispose();
        }
    }


    /// <summary>
    /// Build (or rebuild) the scaler, destination BGRA texture, and CPU
    /// staging texture when the decoded frame size changes. Hot-path
    /// runs this once; steady state it's a no-op.
    /// </summary>
    private ID3D11Texture2D? _bgraDest;

    private void EnsureScalerAndStaging(int width, int height)
    {
        if (_scaler is not null
            && _bgraStaging is not null
            && _bgraDest is not null
            && _bgraStagingWidth == width
            && _bgraStagingHeight == height)
        {
            return;
        }

        _scaler?.Dispose();
        _bgraStaging?.Dispose();
        _bgraDest?.Dispose();

        _scaler = new D3D11VideoScaler(_d3dDevice!, width, height, width, height);

        // The Video Processor needs a DEFAULT texture with RenderTarget
        // bind flag to use as its output view. STAGING textures can't be
        // bound as render targets — we need two textures: a DEFAULT
        // target + a STAGING readback target. Per-frame: VP blt → default,
        // CopyResource → staging, Map staging.
        var dstDesc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            ArraySize = 1,
            MipLevels = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        _bgraDest = _d3dDevice!.CreateTexture2D(dstDesc);

        var stagingDesc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            ArraySize = 1,
            MipLevels = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        };
        _bgraStaging = _d3dDevice!.CreateTexture2D(stagingDesc);
        _bgraStagingWidth = width;
        _bgraStagingHeight = height;
        DebugLog.Write($"[mf] decoder GPU pipeline built {width}x{height}");
    }

    /// <summary>
    /// Called inline after <see cref="D3D11VideoScaler.Process"/> has
    /// written into <see cref="_bgraDest"/>. Copies default → staging,
    /// maps staging, copies out to a managed byte[] with one memcpy per
    /// row (strides differ between staging row pitch and packed BGRA).
    /// </summary>
    private byte[] ReadbackBgra()
    {
        // First we need to run the scaler's output (which went to _bgraDest)
        // into the staging texture. The scaler took _bgraDest as its dest
        // in Process().
        var ctx = _d3dDevice!.ImmediateContext;
        ctx.CopyResource(_bgraStaging!, _bgraDest!);

        var mapped = ctx.Map(_bgraStaging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var rowBytes = _bgraStagingWidth * 4;
            var total = _bgraStagingHeight * rowBytes;
            var bgra = new byte[total];
            fixed (byte* dst = bgra)
            {
                var src = (byte*)mapped.DataPointer;
                for (var y = 0; y < _bgraStagingHeight; y++)
                {
                    Buffer.MemoryCopy(
                        src + (long)y * mapped.RowPitch,
                        dst + (long)y * rowBytes,
                        rowBytes,
                        rowBytes);
                }
            }
            return bgra;
        }
        finally
        {
            ctx.Unmap(_bgraStaging!, 0);
        }
    }

    private void NegotiateOutputType()
    {
        // Walk the decoder's available output types looking for NV12; the
        // first match becomes the active output type. Once set, the next
        // ProcessOutput call will deliver decoded frames.
        for (int i = 0; ; i++)
        {
            IMFMediaType outputType;
            try
            {
                outputType = _transform.GetOutputAvailableType(0, i);
            }
            catch
            {
                return; // MF_E_NO_MORE_TYPES or similar — nothing to negotiate.
            }

            if (outputType is null) return;

            if (outputType.GetGUID(MediaTypeAttributeKeys.Subtype, out var subtype).Success && subtype == VideoFormatGuids.NV12)
            {
                if (outputType.GetUInt64(MediaTypeAttributeKeys.FrameSize, out ulong packed).Success)
                {
                    _width = (int)(packed >> 32);
                    _height = (int)(packed & 0xFFFFFFFFu);
                }
                _transform.SetOutputType(0, outputType, 0);
                outputType.Dispose();
                _outputTypeNegotiated = true;
                DebugLog.Write($"[mf] H264 decoder negotiated NV12 {_width}x{_height}");
                return;
            }
            outputType.Dispose();
        }
    }

    private static byte[] ConvertNv12ToBgra(byte[] nv12, int width, int height)
    {
        // BT.601 YUV -> RGB, packed BGRA. Hot path on the RTP receive
        // thread — one call per frame. A scalar double-loop with
        // Math.Clamp and array-indexed writes cost ~25 ms per 1080p
        // frame and capped the decoder pipeline at ~40 fps. This
        // version walks Y / UV / destination with raw pointers, emits
        // one 32-bit packed BGRA write per pixel, and processes each
        // chroma row's 2x2 block once (Y0, Y1, Y2, Y3 share the same
        // U/V), cutting per-pixel work to ~3 ms at 1080p.
        //
        // BT.601 16-235 limited-range coefficients scaled by 256:
        //   R = (298 * (Y-16)            + 409 * (V-128) + 128) >> 8
        //   G = (298 * (Y-16) - 100 * (U-128) - 208 * (V-128) + 128) >> 8
        //   B = (298 * (Y-16) + 516 * (U-128)            + 128) >> 8
        var bgra = new byte[width * height * 4];
        var yPlaneSize = width * height;
        // Half-width in pairs — UV plane is interleaved so stride == width.
        var halfW = width >> 1;
        var halfH = height >> 1;

        fixed (byte* srcPtr = nv12)
        fixed (byte* dstPtr = bgra)
        {
            var yBase = srcPtr;
            var uvBase = srcPtr + yPlaneSize;
            var dst = (uint*)dstPtr;

            for (var j = 0; j < halfH; j++)
            {
                var yRow0 = yBase + (j * 2) * width;
                var yRow1 = yRow0 + width;
                var uvRow = uvBase + j * width;
                var dstRow0 = dst + (j * 2) * width;
                var dstRow1 = dstRow0 + width;

                for (var i = 0; i < halfW; i++)
                {
                    var u = uvRow[i * 2 + 0] - 128;
                    var v = uvRow[i * 2 + 1] - 128;

                    // Shared chroma terms for all 4 Y samples in this 2x2 block.
                    var rTerm = 409 * v + 128;
                    var gTerm = -100 * u - 208 * v + 128;
                    var bTerm = 516 * u + 128;

                    var y00 = 298 * (yRow0[i * 2 + 0] - 16);
                    var y01 = 298 * (yRow0[i * 2 + 1] - 16);
                    var y10 = 298 * (yRow1[i * 2 + 0] - 16);
                    var y11 = 298 * (yRow1[i * 2 + 1] - 16);

                    dstRow0[i * 2 + 0] = PackBgra(y00 + bTerm, y00 + gTerm, y00 + rTerm);
                    dstRow0[i * 2 + 1] = PackBgra(y01 + bTerm, y01 + gTerm, y01 + rTerm);
                    dstRow1[i * 2 + 0] = PackBgra(y10 + bTerm, y10 + gTerm, y10 + rTerm);
                    dstRow1[i * 2 + 1] = PackBgra(y11 + bTerm, y11 + gTerm, y11 + rTerm);
                }
            }
        }

        return bgra;
    }

    private static uint PackBgra(int b256, int g256, int r256)
    {
        // Fused shift-right-8 + unsigned saturation. Branches are removed
        // by the JIT on x64 because these are straight-line integer ops.
        var b = b256 >> 8;
        var g = g256 >> 8;
        var r = r256 >> 8;
        if ((uint)b > 255) b = b < 0 ? 0 : 255;
        if ((uint)g > 255) g = g < 0 ? 0 : 255;
        if ((uint)r > 255) r = r < 0 ? 0 : 255;
        return 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
    }

    private static IMFTransform? CreateDecoder()
    {
        var inputFilter = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = VideoFormatGuids.H264,
        };
        var flags = (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagSortandfilter);
        var transform = TryCreateDecoder(flags, inputFilter, "hardware");
        if (transform is not null) return transform;

        flags = (uint)(EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagSortandfilter);
        return TryCreateDecoder(flags, inputFilter, "software");
    }

    private static IMFTransform? TryCreateDecoder(uint flags, RegisterTypeInfo inputFilter, string label)
    {
        using var collection = MediaFactory.MFTEnumEx(
            TransformCategoryGuids.VideoDecoder,
            flags,
            inputType: inputFilter,
            outputType: null);

        foreach (var activate in collection)
        {
            try
            {
                var transform = activate.ActivateObject<IMFTransform>();
                DebugLog.Write($"[mf] {label} H264 decoder activated");
                return transform;
            }
            catch (Exception ex)
            {
                DebugLog.Write($"[mf] {label} H264 decoder activate threw: {ex.Message}");
            }
        }
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, 0); } catch { }
        try { _transform.Dispose(); } catch { }
        try { _scaler?.Dispose(); } catch { }
        try { _bgraDest?.Dispose(); } catch { }
        try { _bgraStaging?.Dispose(); } catch { }
        try { _dxgiManager?.Dispose(); } catch { }
        // _d3dDevice is caller-owned — do not dispose.
    }
}
