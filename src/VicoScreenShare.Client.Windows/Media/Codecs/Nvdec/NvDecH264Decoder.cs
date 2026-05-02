namespace VicoScreenShare.Client.Windows.Media.Codecs.Nvdec;

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using VicoScreenShare.Client.Diagnostics;
using VicoScreenShare.Client.Media.Codecs;
using VicoScreenShare.Client.Windows.Direct3D;
using Vortice.Direct3D11;

/// <summary>
/// NVDEC H.264 decoder. Direct cuvid + CUDA driver API path that bypasses
/// the Microsoft H.264 MFT decoder. The MFT path costs 30-45 ms per IDR
/// at 4K + 144 fps + short-GOP — same problem we previously fixed for
/// AV1 by introducing <see cref="NvDecAv1Decoder"/>. NVDEC's dedicated
/// video silicon handles the same workload in single-digit ms per IDR.
///
/// Mirrors <see cref="NvDecAv1Decoder"/> structurally — same CUDA context
/// setup, same parser callbacks, same NV12-via-two-textures D3D11 interop
/// path, same KeyframeNeeded surface points. The two only differ in:
/// <list type="bullet">
///   <item><description><see cref="NvDecApi.cudaVideoCodec.H264"/>
///     instead of <c>AV1</c>.</description></item>
///   <item><description><c>annexB: true</c> in the parser bitfields —
///     the H.264 RTP framer emits start-code-prefixed bitstream
///     (matching what the MFT H.264 decoder expects), where AV1 takes
///     raw OBU bytes.</description></item>
///   <item><description>Log tag <c>[nvdec-h264]</c> so a viewer subscribed
///     to one H.264 stream and one AV1 stream can distinguish their
///     decoders in the debug log.</description></item>
/// </list>
///
/// The bogus-initial-sequence guard (reject 0x0 dims / 4:4:4 / 16-bit) is
/// kept defensively even though cuvid's H.264 parser doesn't fire phantom
/// callbacks the way the AV1 parser does — keeping the two files
/// structurally uniform makes a future shared-base-class refactor easy.
/// </summary>
public sealed class NvDecH264Decoder : IVideoDecoder
{
    public VideoCodec Codec => VideoCodec.H264;

    /// <summary>
    /// GPU output sink. Mirrors <see cref="NvDecAv1Decoder.GpuOutputHandler"/>
    /// — fires synchronously inside <see cref="Decode"/> with an
    /// <c>ID3D11Texture2D</c> pointer to a BGRA frame.
    /// </summary>
    public Action<IntPtr, int, int, TimeSpan>? GpuOutputHandler { get; set; }

    /// <summary>
    /// Raised when this decoder's state has diverged unrecoverably. Fires
    /// from cuvid's parser callback thread or the Decode() caller thread
    /// depending on where the failure surfaces, so subscribers must be
    /// safe to invoke off the main thread.
    /// </summary>
    public event Action? KeyframeNeeded;

    private void RaiseKeyframeNeeded(string reason)
    {
        DebugLog.Write($"[nvdec-h264] keyframe-needed: {reason}");
        try { KeyframeNeeded?.Invoke(); } catch { /* listener exceptions must not crash decoder */ }
    }

    private readonly ID3D11Device _d3dDevice;
    private readonly ID3D11DeviceContext _d3dContext;
    private readonly bool _ownsDevice;
    private readonly int _initHintWidth;
    private readonly int _initHintHeight;

    private NvDecApi.CUcontext _cuContext;
    private NvDecApi.CUdevice _cuDevice;        // Pair-key for PrimaryCtxRelease in Dispose.
    private bool _retainedPrimaryCtx;
    private NvDecApi.CUvideoctxlock _ctxLock;
    private NvDecApi.CUvideoparser _parser;
    private NvDecApi.CUvideodecoder _decoder;

    private ID3D11Texture2D? _yTexture;
    private ID3D11Texture2D? _uvTexture;
    private NvDecApi.CUgraphicsResource _yCudaResource;
    private NvDecApi.CUgraphicsResource _uvCudaResource;
    private int _coded_width;
    private int _coded_height;

    private ID3D11Texture2D? _bgraDest;
    private Nv12PlanesToBgraConverter? _converter;

    private GCHandle _selfHandle;

    private readonly NvDecApi.PfnSequenceCallback _seqCallback;
    private readonly NvDecApi.PfnDecodeCallback _decCallback;
    private readonly NvDecApi.PfnDisplayCallback _disCallback;

    private readonly List<DecodedVideoFrame> _displayBuffer = new();

    private TimeSpan _currentInputTimestamp;
    private bool _disposed;
    private long _decodeCallCount;
    private long _decodeSpikeLogCount;

    public NvDecH264Decoder() : this(externalDevice: null, hintWidth: 0, hintHeight: 0)
    {
    }

    public NvDecH264Decoder(ID3D11Device? externalDevice)
        : this(externalDevice, hintWidth: 0, hintHeight: 0)
    {
    }

    public NvDecH264Decoder(ID3D11Device? externalDevice, int hintWidth, int hintHeight)
    {
        _initHintWidth = hintWidth;
        _initHintHeight = hintHeight;

        _d3dDevice = externalDevice
            ?? throw new ArgumentNullException(nameof(externalDevice),
                "NvDecH264Decoder requires a shared D3D11 device — the renderer reads the BGRA texture on the same device, no per-decoder device allocation.");
        _d3dContext = _d3dDevice.ImmediateContext;
        _ownsDevice = false;

        var caps = NvDecCapabilities.Probe();
        if (!caps.IsH264Available)
        {
            throw new InvalidOperationException("NVDEC H.264 decoder is not available on this host (cuvidGetDecoderCaps reported unsupported)");
        }

        // Sentinel — see NvdecCrashSentinel; mirror of the AV1 path.
        NvdecCrashSentinel.MarkAttempt("h264");
        DebugLog.Write("[nvdec-h264-init] cuInit");
        ThrowOnCudaError(NvDecApi.CuInit(0), "cuInit");
        DebugLog.Write("[nvdec-h264-init] cuDeviceGet");
        ThrowOnCudaError(NvDecApi.CuDeviceGet(out _cuDevice, 0), "cuDeviceGet");
        // Primary-context retain — see NvDecAv1Decoder for the rationale
        // (avoids the floating-context destroy/recreate race that AV'd
        // intermittently on the NVIDIA Windows driver).
        DebugLog.Write("[nvdec-h264-init] cuDevicePrimaryCtxRetain");
        ThrowOnCudaError(NvDecApi.CuDevicePrimaryCtxRetain(out _cuContext, _cuDevice), "cuDevicePrimaryCtxRetain");
        _retainedPrimaryCtx = true;
        DebugLog.Write("[nvdec-h264-init] cuvidCtxLockCreate");
        ThrowOnCudaError(NvDecApi.CuvidCtxLockCreate(out _ctxLock, _cuContext), "cuvidCtxLockCreate");

        _selfHandle = GCHandle.Alloc(this);
        _seqCallback = OnSequenceCallback;
        _decCallback = OnDecodeCallback;
        _disCallback = OnDisplayCallback;

        var parserParams = new NvDecApi.CUVIDPARSERPARAMS
        {
            CodecType = NvDecApi.cudaVideoCodec.H264,
            // 16 = generous limit. H.264 spec caps DPB at 16 frames so this
            // matches the worst-case reorder buffer; we don't expect to
            // see deeper since our encoder runs IPP (no B-frames).
            ulMaxNumDecodeSurfaces = 16,
            ulClockRate = 90_000,
            ulErrorThreshold = 100,
            ulMaxDisplayDelay = 0,
            // H.264 RTP framer emits Annex-B (start codes) — same format
            // the MFT H.264 decoder consumes. AV1 path uses raw OBU
            // bytes, hence the difference here.
            Bitfields = NvDecApi.MakeBitfields(annexB: true),
            pUserData = GCHandle.ToIntPtr(_selfHandle),
            pfnSequenceCallback = Marshal.GetFunctionPointerForDelegate(_seqCallback),
            pfnDecodePicture = Marshal.GetFunctionPointerForDelegate(_decCallback),
            pfnDisplayPicture = Marshal.GetFunctionPointerForDelegate(_disCallback),
            pfnGetOperatingPoint = IntPtr.Zero,
            pfnGetSEIMsg = IntPtr.Zero,
            pExtVideoInfo = IntPtr.Zero,
        };

        DebugLog.Write("[nvdec-h264-init] cuvidCreateVideoParser");
        ThrowOnCudaError(NvDecApi.CuvidCreateVideoParser(out _parser, ref parserParams), "cuvidCreateVideoParser");

        NvdecCrashSentinel.ClearAttempt("h264");
        DebugLog.Write($"[nvdec-h264] decoder initialized (hintDims={hintWidth}x{hintHeight}, max={caps.H264MaxWidth}x{caps.H264MaxHeight}, engines={caps.H264NumNvdecs})");
    }

    public IReadOnlyList<DecodedVideoFrame> Decode(byte[] encodedSample, TimeSpan inputTimestamp)
        => Decode(encodedSample, encodedSample?.Length ?? 0, inputTimestamp);

    public IReadOnlyList<DecodedVideoFrame> Decode(byte[] encodedSample, int length, TimeSpan inputTimestamp)
    {
        if (_disposed || encodedSample is null || length <= 0)
        {
            return Array.Empty<DecodedVideoFrame>();
        }
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _displayBuffer.Clear();
        _currentInputTimestamp = inputTimestamp;

        var pinned = GCHandle.Alloc(encodedSample, GCHandleType.Pinned);
        try
        {
            var packet = new NvDecApi.CUVIDSOURCEDATAPACKET
            {
                flags = NvDecApi.CUVID_PKT_TIMESTAMP,
                payload_size = (uint)length,
                payload = pinned.AddrOfPinnedObject(),
                timestamp = (ulong)(inputTimestamp.Ticks / TimeSpan.TicksPerMillisecond * 90),
            };

            ThrowOnCudaError(NvDecApi.CuCtxPushCurrent(_cuContext), "cuCtxPushCurrent");
            try
            {
                var parseResult = NvDecApi.CuvidParseVideoData(_parser, ref packet);
                if (parseResult != NvDecApi.CUresult.CUDA_SUCCESS)
                {
                    DebugLog.Write($"[nvdec-h264] cuvidParseVideoData returned {parseResult}");
                    RaiseKeyframeNeeded($"cuvidParseVideoData={parseResult}");
                }
            }
            finally
            {
                NvDecApi.CuCtxPopCurrent(out _);
            }
        }
        finally
        {
            pinned.Free();
        }

        var decodeMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0
            / System.Diagnostics.Stopwatch.Frequency;
        var nDecode = ++_decodeCallCount;
        if (decodeMs > 8.0)
        {
            var spikes = ++_decodeSpikeLogCount;
            if (spikes <= 200 || spikes % 50 == 0)
            {
                DebugLog.Write($"[nvdec-h264-decode-spike] frame#{nDecode} decode={decodeMs:F1}ms inputBytes={length} outputs={_displayBuffer.Count} pts={inputTimestamp.TotalMilliseconds:F0}ms");
            }
        }

        return _displayBuffer.Count == 0
            ? Array.Empty<DecodedVideoFrame>()
            : _displayBuffer.ToArray();
    }

    public void Flush()
    {
        // No NVDEC-specific flush API. Same no-op as the AV1 path.
    }

    private static int OnSequenceCallback(IntPtr userData, ref NvDecApi.CUVIDEOFORMAT format)
    {
        var self = ResolveSelf(userData);
        if (self is null) { return 0; }
        return self.HandleSequence(ref format);
    }

    private static int OnDecodeCallback(IntPtr userData, IntPtr picParams)
    {
        var self = ResolveSelf(userData);
        if (self is null) { return 0; }
        return self.HandleDecode(picParams);
    }

    private static int OnDisplayCallback(IntPtr userData, IntPtr dispInfo)
    {
        var self = ResolveSelf(userData);
        if (self is null) { return 0; }
        return self.HandleDisplay(dispInfo);
    }

    private static NvDecH264Decoder? ResolveSelf(IntPtr userData)
    {
        if (userData == IntPtr.Zero) { return null; }
        try
        {
            var handle = GCHandle.FromIntPtr(userData);
            return handle.Target as NvDecH264Decoder;
        }
        catch
        {
            return null;
        }
    }

    private int HandleSequence(ref NvDecApi.CUVIDEOFORMAT format)
    {
        try
        {
            var coded_w = (int)format.coded_width;
            var coded_h = (int)format.coded_height;
            DebugLog.Write($"[nvdec-h264] sequence: codec={format.codec} {coded_w}x{coded_h} chroma={format.chroma_format} bitDepth={8 + format.bit_depth_luma_minus8} minSurfaces={format.min_num_decode_surfaces}");

            if (coded_w <= 0 || coded_h <= 0
                || format.chroma_format != NvDecApi.cudaVideoChromaFormat._420
                || format.bit_depth_luma_minus8 != 0)
            {
                DebugLog.Write($"[nvdec-h264] sequence ignored (bogus): {coded_w}x{coded_h} chroma={format.chroma_format} bitDepth={8 + format.bit_depth_luma_minus8} — waiting for valid SPS");
                RaiseKeyframeNeeded($"bogus sequence {coded_w}x{coded_h} chroma={format.chroma_format} bd={8 + format.bit_depth_luma_minus8}");
                return Math.Max(format.min_num_decode_surfaces, (byte)8);
            }

            if (_decoder.IsZero || coded_w != _coded_width || coded_h != _coded_height)
            {
                DestroyDecoderResources();
                _coded_width = coded_w;
                _coded_height = coded_h;

                var dci = new NvDecApi.CUVIDDECODECREATEINFO
                {
                    ulWidth = (uint)coded_w,
                    ulHeight = (uint)coded_h,
                    ulNumDecodeSurfaces = Math.Max(format.min_num_decode_surfaces, (byte)8),
                    CodecType = format.codec,
                    ChromaFormat = format.chroma_format,
                    ulCreationFlags = 0,
                    bitDepthMinus8 = format.bit_depth_luma_minus8,
                    ulIntraDecodeOnly = 0,
                    ulMaxWidth = (uint)coded_w,
                    ulMaxHeight = (uint)coded_h,
                    OutputFormat = NvDecApi.cudaVideoSurfaceFormat.NV12,
                    DeinterlaceMode = NvDecApi.cudaVideoDeinterlaceMode.Weave,
                    ulTargetWidth = (uint)coded_w,
                    ulTargetHeight = (uint)coded_h,
                    ulNumOutputSurfaces = 2,
                    vidLock = _ctxLock,
                };

                var createResult = NvDecApi.CuvidCreateDecoder(out _decoder, ref dci);
                if (createResult != NvDecApi.CUresult.CUDA_SUCCESS)
                {
                    DebugLog.Write($"[nvdec-h264] cuvidCreateDecoder returned {createResult}");
                    RaiseKeyframeNeeded($"cuvidCreateDecoder={createResult} {coded_w}x{coded_h}");
                    return 0;
                }

                EnsureStagingResources(coded_w, coded_h);
            }
            return (int)format.min_num_decode_surfaces;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[nvdec-h264] HandleSequence threw {ex.GetType().Name}: {ex.Message}");
            return 0;
        }
    }

    private int HandleDecode(IntPtr picParams)
    {
        try
        {
            if (_decoder.IsZero) { return 0; }
            var decodeResult = NvDecApi.CuvidDecodePicture(_decoder, picParams);
            if (decodeResult != NvDecApi.CUresult.CUDA_SUCCESS)
            {
                DebugLog.Write($"[nvdec-h264] cuvidDecodePicture returned {decodeResult}");
                RaiseKeyframeNeeded($"cuvidDecodePicture={decodeResult}");
                return 0;
            }
            return 1;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[nvdec-h264] HandleDecode threw {ex.GetType().Name}: {ex.Message}");
            return 0;
        }
    }

    private int HandleDisplay(IntPtr dispInfoPtr)
    {
        try
        {
            if (_decoder.IsZero || _yTexture is null || _uvTexture is null || _bgraDest is null || _converter is null)
            {
                return 0;
            }

            var dispInfo = Marshal.PtrToStructure<NvDecApi.CUVIDPARSERDISPINFO>(dispInfoPtr);
            var picIdx = dispInfo.picture_index;
            var procParams = new NvDecApi.CUVIDPROCPARAMS
            {
                progressive_frame = dispInfo.progressive_frame,
                top_field_first = dispInfo.top_field_first,
                unpaired_field = dispInfo.repeat_first_field == 0 ? 1 : 0,
            };

            var mapResult = NvDecApi.CuvidMapVideoFrame(_decoder, picIdx, out var srcDevPtr, out var srcPitch, ref procParams);
            if (mapResult != NvDecApi.CUresult.CUDA_SUCCESS)
            {
                DebugLog.Write($"[nvdec-h264] cuvidMapVideoFrame returned {mapResult}");
                return 0;
            }

            try
            {
                CopyNv12FromCudaToTexture(srcDevPtr, srcPitch, _coded_width, _coded_height);
            }
            finally
            {
                NvDecApi.CuvidUnmapVideoFrame(_decoder, srcDevPtr);
            }

            _converter.Convert(_yTexture, _uvTexture, _bgraDest);

            var outTs = TimeSpan.FromTicks((long)dispInfo.timestamp * TimeSpan.TicksPerMillisecond / 90);
            var sink = GpuOutputHandler;
            if (sink is not null)
            {
                var targets = sink.GetInvocationList();
                foreach (var target in targets)
                {
                    try
                    {
                        ((Action<IntPtr, int, int, TimeSpan>)target).Invoke(
                            _bgraDest.NativePointer, _coded_width, _coded_height, outTs);
                    }
                    catch (Exception ex)
                    {
                        DebugLog.Write($"[nvdec-h264] GpuOutputHandler threw {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            return 1;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[nvdec-h264] HandleDisplay threw {ex.GetType().Name}: {ex.Message}");
            RaiseKeyframeNeeded($"HandleDisplay {ex.GetType().Name}");
            return 0;
        }
    }

    private unsafe void CopyNv12FromCudaToTexture(ulong srcDevPtr, uint srcPitch, int width, int height)
    {
        if (_yCudaResource.IsZero || _uvCudaResource.IsZero) { return; }

        var yResource = _yCudaResource;
        var uvResource = _uvCudaResource;
        var resources = stackalloc NvDecApi.CUgraphicsResource[2];
        resources[0] = yResource;
        resources[1] = uvResource;

        ThrowOnCudaError(NvDecApi.CuGraphicsMapResources(1, ref yResource, default), "cuGraphicsMapResources(Y)");
        ThrowOnCudaError(NvDecApi.CuGraphicsMapResources(1, ref uvResource, default), "cuGraphicsMapResources(UV)");
        try
        {
            ThrowOnCudaError(NvDecApi.CuGraphicsSubResourceGetMappedArray(out var yArray, yResource, 0, 0), "subResource(Y)");
            ThrowOnCudaError(NvDecApi.CuGraphicsSubResourceGetMappedArray(out var uvArray, uvResource, 0, 0), "subResource(UV)");

            var copy = stackalloc CUDA_MEMCPY2D[1];
            copy[0] = new CUDA_MEMCPY2D
            {
                srcMemoryType = CUmemorytype.DEVICE,
                srcDevice = srcDevPtr,
                srcPitch = (IntPtr)srcPitch,
                dstMemoryType = CUmemorytype.ARRAY,
                dstArray = yArray.Handle,
                WidthInBytes = (IntPtr)width,
                Height = (IntPtr)height,
            };
            ThrowOnCudaError(NvDecApi.CuMemcpy2D((IntPtr)copy), "cuMemcpy2D(Y)");

            copy[0] = new CUDA_MEMCPY2D
            {
                srcMemoryType = CUmemorytype.DEVICE,
                srcDevice = srcDevPtr + (ulong)srcPitch * (ulong)height,
                srcPitch = (IntPtr)srcPitch,
                dstMemoryType = CUmemorytype.ARRAY,
                dstArray = uvArray.Handle,
                WidthInBytes = (IntPtr)width,
                Height = (IntPtr)(height / 2),
            };
            ThrowOnCudaError(NvDecApi.CuMemcpy2D((IntPtr)copy), "cuMemcpy2D(UV)");
        }
        finally
        {
            NvDecApi.CuGraphicsUnmapResources(1, ref uvResource, default);
            NvDecApi.CuGraphicsUnmapResources(1, ref yResource, default);
        }
    }

    private void EnsureStagingResources(int width, int height)
    {
        var yDesc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Vortice.DXGI.Format.R8_UNorm,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        _yTexture = _d3dDevice.CreateTexture2D(yDesc);
        _yTexture.DebugName = "nvdec-h264-y-staging";

        var uvDesc = new Texture2DDescription
        {
            Width = (uint)(width / 2),
            Height = (uint)(height / 2),
            MipLevels = 1,
            ArraySize = 1,
            Format = Vortice.DXGI.Format.R8G8_UNorm,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        _uvTexture = _d3dDevice.CreateTexture2D(uvDesc);
        _uvTexture.DebugName = "nvdec-h264-uv-staging";

        var yReg = NvDecApi.CuGraphicsD3D11RegisterResource(
            out _yCudaResource, _yTexture.NativePointer, NvDecApi.CU_GRAPHICS_REGISTER_FLAGS_NONE);
        if (yReg != NvDecApi.CUresult.CUDA_SUCCESS)
        {
            DebugLog.Write($"[nvdec-h264] cuGraphicsD3D11RegisterResource(Y) returned {yReg}");
            _yCudaResource = default;
        }
        else
        {
            NvDecApi.CuGraphicsResourceSetMapFlags(_yCudaResource, NvDecApi.CU_GRAPHICS_MAP_RESOURCE_FLAGS_WRITE_DISCARD);
        }

        var uvReg = NvDecApi.CuGraphicsD3D11RegisterResource(
            out _uvCudaResource, _uvTexture.NativePointer, NvDecApi.CU_GRAPHICS_REGISTER_FLAGS_NONE);
        if (uvReg != NvDecApi.CUresult.CUDA_SUCCESS)
        {
            DebugLog.Write($"[nvdec-h264] cuGraphicsD3D11RegisterResource(UV) returned {uvReg}");
            _uvCudaResource = default;
        }
        else
        {
            NvDecApi.CuGraphicsResourceSetMapFlags(_uvCudaResource, NvDecApi.CU_GRAPHICS_MAP_RESOURCE_FLAGS_WRITE_DISCARD);
        }

        var bgraDesc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        _bgraDest = _d3dDevice.CreateTexture2D(bgraDesc);
        _bgraDest.DebugName = "nvdec-h264-bgra-out";

        _converter = new Nv12PlanesToBgraConverter(_d3dDevice, width, height);
    }

    private void DestroyDecoderResources()
    {
        if (!_yCudaResource.IsZero)
        {
            try { NvDecApi.CuGraphicsUnregisterResource(_yCudaResource); } catch { }
            _yCudaResource = default;
        }
        if (!_uvCudaResource.IsZero)
        {
            try { NvDecApi.CuGraphicsUnregisterResource(_uvCudaResource); } catch { }
            _uvCudaResource = default;
        }
        if (_yTexture is not null)
        {
            try { _yTexture.Dispose(); } catch { }
            _yTexture = null;
        }
        if (_uvTexture is not null)
        {
            try { _uvTexture.Dispose(); } catch { }
            _uvTexture = null;
        }
        if (_bgraDest is not null)
        {
            try { _bgraDest.Dispose(); } catch { }
            _bgraDest = null;
        }
        if (_converter is not null)
        {
            try { _converter.Dispose(); } catch { }
            _converter = null;
        }
        if (!_decoder.IsZero)
        {
            try { NvDecApi.CuvidDestroyDecoder(_decoder); } catch { }
            _decoder = default;
        }
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;

        if (!_parser.IsZero)
        {
            try { NvDecApi.CuvidDestroyVideoParser(_parser); } catch { }
            _parser = default;
        }
        DestroyDecoderResources();
        if (_ctxLock.Handle != IntPtr.Zero)
        {
            try { NvDecApi.CuvidCtxLockDestroy(_ctxLock); } catch { }
            _ctxLock = default;
        }
        if (_retainedPrimaryCtx)
        {
            try { NvDecApi.CuDevicePrimaryCtxRelease(_cuDevice); } catch { }
            _retainedPrimaryCtx = false;
            _cuContext = default;
        }
        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }
        if (_ownsDevice)
        {
            try { _d3dContext.Dispose(); } catch { }
            try { _d3dDevice.Dispose(); } catch { }
        }
        DebugLog.Write("[nvdec-h264] decoder disposed");
    }

    private static void ThrowOnCudaError(NvDecApi.CUresult result, string operation)
    {
        if (result != NvDecApi.CUresult.CUDA_SUCCESS)
        {
            throw new InvalidOperationException($"NVDEC H.264 {operation} failed: {result}");
        }
    }
}
