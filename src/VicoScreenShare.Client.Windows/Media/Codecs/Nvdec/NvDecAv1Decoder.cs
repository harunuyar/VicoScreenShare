namespace VicoScreenShare.Client.Windows.Media.Codecs.Nvdec;

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using VicoScreenShare.Client.Diagnostics;
using VicoScreenShare.Client.Media.Codecs;
using VicoScreenShare.Client.Windows.Direct3D;
using Vortice.Direct3D11;

/// <summary>
/// NVDEC AV1 decoder. Direct cuvid + CUDA driver API path that bypasses
/// the Microsoft AV1 MFT shim. The MFT path was throughput-limited at
/// 4K + 144 fps + 1 s GOP — every IDR cost 30-45 ms of decoder thread
/// time and produced no output during that window, regularly draining
/// the playout queue and producing visible micro-stutters. NVDEC's
/// dedicated video silicon handles the same workload in single-digit
/// ms per IDR.
///
/// Pipeline:
/// <list type="number">
///   <item><description>cuvidCreateVideoParser — bitstream parser with
///     three callbacks. Sequence callback fires when a SeqHdr OBU
///     defines the stream's size; we use it to (re)create the decoder
///     and the NV12 staging texture. Decode callback fires per parsed
///     picture; we forward to <c>cuvidDecodePicture</c>. Display callback
///     fires once a decoded picture is ready at its presentation time;
///     we map it, copy NV12 → D3D11 NV12 texture, run the existing
///     <see cref="D3D11VideoScaler"/> for NV12 → BGRA, and fire
///     <see cref="GpuOutputHandler"/>.</description></item>
///   <item><description>CUDA-D3D11 interop: the staging NV12 texture is
///     registered as a <c>CUgraphicsResource</c> at allocation time,
///     mapped per-frame, the Y and UV planes filled via
///     <c>cuMemcpy2D</c> from the CUDA device pointer NVDEC produces,
///     unmapped. The destination D3D11 texture stays on our GPU device
///     so the rest of the renderer pipeline reads it without a CPU
///     readback.</description></item>
/// </list>
///
/// Construction parallels <see cref="MediaFoundationAv1Decoder"/>: takes
/// an external <c>ID3D11Device</c> and an optional resolution hint.
/// </summary>
public sealed class NvDecAv1Decoder : IVideoDecoder
{
    public VideoCodec Codec => VideoCodec.Av1;

    /// <summary>
    /// GPU output sink. Mirrors <see cref="MediaFoundationAv1Decoder.GpuOutputHandler"/>
    /// — fires synchronously inside <see cref="Decode"/> with an
    /// <c>ID3D11Texture2D</c> pointer to a BGRA frame. Subscribers must
    /// AddRef explicitly if they need to retain the texture (the
    /// receiver-side TextureArrived contract is self-balancing per
    /// the StreamReceiver fix landed on av1-phase4).
    /// </summary>
    public Action<IntPtr, int, int, TimeSpan>? GpuOutputHandler { get; set; }

    /// <summary>
    /// Raised when this decoder's state has diverged unrecoverably. Fires
    /// from cuvid's parser callback thread or the Decode() caller thread
    /// depending on where the failure surfaces, so subscribers must be
    /// safe to invoke off the main thread. <see cref="StreamReceiver"/>
    /// listens to this and dispatches a debounced RTCP PLI upstream.
    /// </summary>
    public event Action? KeyframeNeeded;

    private void RaiseKeyframeNeeded(string reason)
    {
        DebugLog.Write($"[nvdec] keyframe-needed: {reason}");
        try { KeyframeNeeded?.Invoke(); } catch { /* listener exceptions must not crash decoder */ }
    }

    private readonly ID3D11Device _d3dDevice;
    private readonly ID3D11DeviceContext _d3dContext;
    private readonly bool _ownsDevice;
    private readonly int _initHintWidth;
    private readonly int _initHintHeight;

    private NvDecApi.CUcontext _cuContext;
    private NvDecApi.CUvideoctxlock _ctxLock;
    private NvDecApi.CUvideoparser _parser;
    private NvDecApi.CUvideodecoder _decoder;

    // NVDEC outputs NV12 in CUDA device memory. We can't expose the UV
    // plane of a DXGI_FORMAT_NV12 D3D11 texture to CUDA (driver returns
    // INVALID_VALUE for arrayIndex=1 on every NVIDIA driver tested), so
    // we split the destination into two non-video-format textures the
    // CUDA-D3D11 interop layer handles cleanly:
    //   _yTexture  — R8_UNorm at full resolution
    //   _uvTexture — R8G8_UNorm at half resolution
    // Each holds one plane of the decoded NV12 frame; the
    // Nv12PlanesToBgraConverter shader recombines them into _bgraDest
    // for the renderer.
    private ID3D11Texture2D? _yTexture;
    private ID3D11Texture2D? _uvTexture;
    private NvDecApi.CUgraphicsResource _yCudaResource;
    private NvDecApi.CUgraphicsResource _uvCudaResource;
    private int _coded_width;
    private int _coded_height;

    // BGRA destination handed to the renderer's GpuOutputHandler. Same
    // role as MediaFoundationAv1Decoder._bgraDest. Held at the highest
    // refcount of any decoder member so the renderer can consume it
    // synchronously inside the GpuOutputHandler callback.
    private ID3D11Texture2D? _bgraDest;
    private Nv12PlanesToBgraConverter? _converter;

    // The parser callbacks fire on cuvid's internal thread with our
    // user-data IntPtr in their first arg. We hand cuvid a GCHandle to
    // `this` so the callbacks can fish the instance out and dispatch.
    private GCHandle _selfHandle;

    // Hold delegate references so the GC doesn't reclaim them while
    // cuvid still has the function-pointer addresses.
    private readonly NvDecApi.PfnSequenceCallback _seqCallback;
    private readonly NvDecApi.PfnDecodeCallback _decCallback;
    private readonly NvDecApi.PfnDisplayCallback _disCallback;

    // The display callback can fire OUTSIDE Decode() if cuvid buffers
    // pictures across calls. We collect emitted frames here for
    // Decode()'s return value when the caller uses the CPU return
    // path; in GPU-handoff mode (GpuOutputHandler set) the callback
    // fires the handler synchronously and we return empty.
    private readonly List<DecodedVideoFrame> _displayBuffer = new();

    private TimeSpan _currentInputTimestamp;
    private bool _disposed;
    private long _decodeCallCount;
    private long _decodeSpikeLogCount;

    public NvDecAv1Decoder() : this(externalDevice: null, hintWidth: 0, hintHeight: 0)
    {
    }

    public NvDecAv1Decoder(ID3D11Device? externalDevice)
        : this(externalDevice, hintWidth: 0, hintHeight: 0)
    {
    }

    public NvDecAv1Decoder(ID3D11Device? externalDevice, int hintWidth, int hintHeight)
    {
        _initHintWidth = hintWidth;
        _initHintHeight = hintHeight;

        // Production callers always pass a shared D3D11 device. The
        // headless / test path constructs its own; the platform helper
        // for that lives in the capture-host setup, not here.
        _d3dDevice = externalDevice
            ?? throw new ArgumentNullException(nameof(externalDevice),
                "NvDecAv1Decoder requires a shared D3D11 device — the renderer reads the BGRA texture on the same device, no per-decoder device allocation.");
        _d3dContext = _d3dDevice.ImmediateContext;
        _ownsDevice = false;

        // Bind a CUDA context to the same physical adapter as the D3D11
        // device. cuD3D11GetDevice walks the DXGI adapter to map back to
        // a CUDA device ordinal — this is what makes the
        // cuGraphicsD3D11RegisterResource calls work later.
        var caps = NvDecCapabilities.Probe();
        if (!caps.IsAv1Available)
        {
            throw new InvalidOperationException("NVDEC AV1 decoder is not available on this host (cuvidGetDecoderCaps reported unsupported)");
        }

        // We init a fresh CUDA context on ordinal 0. For a multi-GPU
        // host targeting a specific D3D11 adapter, we'd use
        // cuD3D11GetDevice with the device's parent IDXGIAdapter; that
        // refinement is a follow-up — most receivers run on a single GPU.
        ThrowOnCudaError(NvDecApi.CuInit(0), "cuInit");
        ThrowOnCudaError(NvDecApi.CuDeviceGet(out var cudaDevice, 0), "cuDeviceGet");
        ThrowOnCudaError(NvDecApi.CuCtxCreate(out _cuContext, NvDecApi.CU_CTX_SCHED_AUTO, cudaDevice), "cuCtxCreate");
        ThrowOnCudaError(NvDecApi.CuvidCtxLockCreate(out _ctxLock, _cuContext), "cuvidCtxLockCreate");

        _selfHandle = GCHandle.Alloc(this);
        _seqCallback = OnSequenceCallback;
        _decCallback = OnDecodeCallback;
        _disCallback = OnDisplayCallback;

        var parserParams = new NvDecApi.CUVIDPARSERPARAMS
        {
            CodecType = NvDecApi.cudaVideoCodec.AV1,
            // 16 = generous limit. cuvid uses this to pre-allocate the
            // decode-picture-buffer count internally; AV1 Main profile
            // caps the DPB at 8 ref frames so 16 is double-overhead.
            ulMaxNumDecodeSurfaces = 16,
            // 90 kHz matches the RTP timestamp clock so timestamps we
            // pass through the parser come out the other side at the
            // same scale.
            ulClockRate = 90_000,
            ulErrorThreshold = 100,
            // 0 = display each picture as soon as it's decoded. NVENC
            // AV1 emits no B-frames in our config so reorder depth is
            // zero anyway.
            ulMaxDisplayDelay = 0,
            Bitfields = NvDecApi.MakeBitfields(annexB: false),
            pUserData = GCHandle.ToIntPtr(_selfHandle),
            pfnSequenceCallback = Marshal.GetFunctionPointerForDelegate(_seqCallback),
            pfnDecodePicture = Marshal.GetFunctionPointerForDelegate(_decCallback),
            pfnDisplayPicture = Marshal.GetFunctionPointerForDelegate(_disCallback),
            pfnGetOperatingPoint = IntPtr.Zero,
            pfnGetSEIMsg = IntPtr.Zero,
            pExtVideoInfo = IntPtr.Zero,
        };

        ThrowOnCudaError(NvDecApi.CuvidCreateVideoParser(out _parser, ref parserParams), "cuvidCreateVideoParser");

        DebugLog.Write($"[nvdec] AV1 decoder initialized (hintDims={hintWidth}x{hintHeight}, max={caps.Av1MaxWidth}x{caps.Av1MaxHeight}, engines={caps.Av1NumNvdecs})");
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

        // Pin the byte[] so cuvid's parser can read the bytes via the
        // raw IntPtr in CUVIDSOURCEDATAPACKET.payload. The pin is held
        // for the duration of cuvidParseVideoData — the parser is
        // synchronous on the calling thread, callbacks fire before the
        // call returns, so the pin is bounded.
        var pinned = GCHandle.Alloc(encodedSample, GCHandleType.Pinned);
        try
        {
            var packet = new NvDecApi.CUVIDSOURCEDATAPACKET
            {
                flags = NvDecApi.CUVID_PKT_TIMESTAMP,
                payload_size = (uint)length,
                payload = pinned.AddrOfPinnedObject(),
                // RTP-90kHz scale to match parser ulClockRate.
                timestamp = (ulong)(inputTimestamp.Ticks / TimeSpan.TicksPerMillisecond * 90),
            };

            // CUDA contexts are per-thread current. cuvidParseVideoData
            // requires our context to be current on the calling thread.
            ThrowOnCudaError(NvDecApi.CuCtxPushCurrent(_cuContext), "cuCtxPushCurrent");
            try
            {
                var parseResult = NvDecApi.CuvidParseVideoData(_parser, ref packet);
                if (parseResult != NvDecApi.CUresult.CUDA_SUCCESS)
                {
                    DebugLog.Write($"[nvdec] cuvidParseVideoData returned {parseResult}");
                    // Parser hit a state it can't recover without a fresh
                    // sequence — typically a missing TileGroup OBU due to
                    // upstream packet loss. Surface to the receiver so it
                    // sends RTCP PLI; the publisher's encoder will respond
                    // with a forced IDR and the next sequence will reset
                    // our reference state.
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
                DebugLog.Write($"[nvdec-decode-spike] frame#{nDecode} decode={decodeMs:F1}ms inputBytes={length} outputs={_displayBuffer.Count} pts={inputTimestamp.TotalMilliseconds:F0}ms");
            }
        }

        // Defensive copy because we reuse _displayBuffer on the next call.
        return _displayBuffer.Count == 0
            ? Array.Empty<DecodedVideoFrame>()
            : _displayBuffer.ToArray();
    }

    public void Flush()
    {
        // No NVDEC-specific flush API beyond destroying / recreating the
        // decoder. For now this is a no-op — the parser handles
        // continuity across IDRs without help, and the watchdog/
        // recovery path on the StreamReceiver side doesn't apply
        // (those paths target the MFT's wedged-state behavior).
    }

    // -------------------------------------------------------------------
    // Parser callbacks. These fire from cuvid's thread; the userData
    // pointer is the GCHandle of `this`. Each callback returns int —
    // 0 = success / continue, anything else = failure / abort.
    // -------------------------------------------------------------------

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

    private static NvDecAv1Decoder? ResolveSelf(IntPtr userData)
    {
        if (userData == IntPtr.Zero) { return null; }
        try
        {
            var handle = GCHandle.FromIntPtr(userData);
            return handle.Target as NvDecAv1Decoder;
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
            DebugLog.Write($"[nvdec] sequence: codec={format.codec} {coded_w}x{coded_h} chroma={format.chroma_format} bitDepth={8 + format.bit_depth_luma_minus8} minSurfaces={format.min_num_decode_surfaces}");

            // Reject obviously-bogus initial sequence callbacks. cuvid's
            // parser fires this callback as soon as it sees what LOOKS
            // like a Sequence Header OBU, even if the bytes it parsed
            // were a partial or corrupt frame at stream start. We've
            // observed it firing with 0x0 dims / 4:4:4 chroma / 16-bit
            // depth — values that don't match anything our encoder ever
            // produces. If we trust them and call cuvidCreateDecoder,
            // it returns INVALID_VALUE and the decoder is wedged for
            // 5+ seconds until the real Sequence Header arrives and
            // fires another callback. Filtering at the gate avoids the
            // wedge entirely: we sit at "no decoder" until a valid
            // sequence comes in, which costs at most one extra IDR
            // round-trip vs the 5-10 second stutter the wedge produced.
            if (coded_w <= 0 || coded_h <= 0
                || format.chroma_format != NvDecApi.cudaVideoChromaFormat._420
                || format.bit_depth_luma_minus8 != 0)
            {
                DebugLog.Write($"[nvdec] sequence ignored (bogus): {coded_w}x{coded_h} chroma={format.chroma_format} bitDepth={8 + format.bit_depth_luma_minus8} — waiting for valid SeqHdr");
                // Surface to the receiver so it sends a PLI; getting a
                // fresh IDR is the fastest way to trigger a real
                // sequence callback.
                RaiseKeyframeNeeded($"bogus sequence {coded_w}x{coded_h} chroma={format.chroma_format} bd={8 + format.bit_depth_luma_minus8}");
                // Return min_num_decode_surfaces (or a safe default) —
                // returning 0 makes cuvid abort the entire parser, which
                // is worse than a no-op.
                return Math.Max(format.min_num_decode_surfaces, (byte)8);
            }

            // First time, or dimension change. Recreate the decoder and
            // the NV12 staging texture.
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
                    DebugLog.Write($"[nvdec] cuvidCreateDecoder returned {createResult}");
                    // We're now in a state where the parser fired a
                    // sequence callback but we couldn't materialize a
                    // decoder for it. Subsequent packets will all
                    // ERROR_UNKNOWN until a fresh sequence arrives —
                    // ask upstream for one immediately.
                    RaiseKeyframeNeeded($"cuvidCreateDecoder={createResult} {coded_w}x{coded_h}");
                    return 0;
                }

                EnsureStagingResources(coded_w, coded_h);
            }
            return (int)format.min_num_decode_surfaces;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[nvdec] HandleSequence threw {ex.GetType().Name}: {ex.Message}");
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
                DebugLog.Write($"[nvdec] cuvidDecodePicture returned {decodeResult}");
                RaiseKeyframeNeeded($"cuvidDecodePicture={decodeResult}");
                return 0;
            }
            return 1;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[nvdec] HandleDecode threw {ex.GetType().Name}: {ex.Message}");
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
                DebugLog.Write($"[nvdec] cuvidMapVideoFrame returned {mapResult}");
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

            // NV12 → BGRA on the GPU via custom shader pass.
            _converter.Convert(_yTexture, _uvTexture, _bgraDest);

            // Emit to the GPU sink. The caller-side TextureArrived
            // contract is self-balancing per the StreamReceiver fix:
            // the decoder does NOT pre-AddRef, each consumer is
            // responsible for its own AddRef + Release.
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
                        DebugLog.Write($"[nvdec] GpuOutputHandler threw {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            // CPU path: Decode() returns a list. Could implement this if
            // a non-GPU consumer ever needs NVDEC output, but every
            // current consumer uses the GPU sink.
            return 1;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[nvdec] HandleDisplay threw {ex.GetType().Name}: {ex.Message}");
            // Display threw mid-stream (likely CUDA copy or shader pass
            // failure). The decoder might still recover on the next IDR,
            // but we won't get one without asking — and the encoder is
            // probably running with intra-refresh, so there's no
            // periodic IDR to fall back on.
            RaiseKeyframeNeeded($"HandleDisplay {ex.GetType().Name}");
            return 0;
        }
    }

    private unsafe void CopyNv12FromCudaToTexture(ulong srcDevPtr, uint srcPitch, int width, int height)
    {
        if (_yCudaResource.IsZero || _uvCudaResource.IsZero) { return; }

        // Two separate CUDA-D3D11 mappings, one per plane. R8 and R8G8
        // are non-video formats so the interop layer exposes them
        // straightforwardly — the NV12 limitation that blocks the single-
        // texture approach (UV plane unreachable via subresource API)
        // doesn't apply.
        //
        // NVDEC packs NV12 contiguously in the source buffer:
        //   Y plane:  rows 0..height-1, srcPitch stride
        //   UV plane: rows height..(3h/2)-1, srcPitch stride (each row
        //             has width/2 UV pairs = width bytes)
        // The destination R8G8 texture is half-res (width/2 cols ×
        // height/2 rows), so the UV copy specifies width bytes per row
        // and height/2 rows.
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
        // Y plane staging texture — R8_UNorm at full resolution.
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
        _yTexture.DebugName = "nvdec-y-staging";

        // UV plane staging texture — R8G8_UNorm at half resolution. Each
        // texel is one (U,V) pair in chroma 4:2:0 layout.
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
        _uvTexture.DebugName = "nvdec-uv-staging";

        var yReg = NvDecApi.CuGraphicsD3D11RegisterResource(
            out _yCudaResource, _yTexture.NativePointer, NvDecApi.CU_GRAPHICS_REGISTER_FLAGS_NONE);
        if (yReg != NvDecApi.CUresult.CUDA_SUCCESS)
        {
            DebugLog.Write($"[nvdec] cuGraphicsD3D11RegisterResource(Y) returned {yReg}");
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
            DebugLog.Write($"[nvdec] cuGraphicsD3D11RegisterResource(UV) returned {uvReg}");
            _uvCudaResource = default;
        }
        else
        {
            NvDecApi.CuGraphicsResourceSetMapFlags(_uvCudaResource, NvDecApi.CU_GRAPHICS_MAP_RESOURCE_FLAGS_WRITE_DISCARD);
        }

        // BGRA destination for the renderer.
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
        _bgraDest.DebugName = "nvdec-bgra-out";

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
        if (!_cuContext.IsZero)
        {
            try { NvDecApi.CuCtxDestroy(_cuContext); } catch { }
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
        DebugLog.Write("[nvdec] AV1 decoder disposed");
    }

    private static void ThrowOnCudaError(NvDecApi.CUresult result, string operation)
    {
        if (result != NvDecApi.CUresult.CUDA_SUCCESS)
        {
            throw new InvalidOperationException($"NVDEC {operation} failed: {result}");
        }
    }
}
