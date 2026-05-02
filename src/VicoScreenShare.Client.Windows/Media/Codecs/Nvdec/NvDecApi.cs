namespace VicoScreenShare.Client.Windows.Media.Codecs.Nvdec;

using System;
using System.Runtime.InteropServices;

/// <summary>
/// P/Invoke surface for NVIDIA's NVDEC stack. Two driver DLLs are
/// involved, both shipping with the NVIDIA display driver — no SDK
/// install needed by the user:
/// <list type="bullet">
///   <item><description><c>nvcuda.dll</c> — CUDA driver API (context,
///     graphics interop). We only use the subset needed to bring up a
///     CUDA context bound to our D3D11 device and to map decoded frames
///     across the CUDA → D3D11 boundary.</description></item>
///   <item><description><c>nvcuvid.dll</c> — Video Codec SDK runtime.
///     Exposes the bitstream parser (<c>cuvidParseVideoData</c>) and the
///     decoder (<c>cuvidDecodePicture</c> / <c>cuvidMapVideoFrame</c>).
///     The parser fires three callbacks per coded video sequence:
///     sequence (size/profile detected), decode (a parsed picture is
///     ready to go to the silicon), and display (a decoded picture is
///     ready to read out at its presentation timestamp).</description></item>
/// </list>
/// Mirrors the direct-SDK style of <see cref="Nvenc.NvencApi"/> on the
/// encoder side — same reasoning: the MFT shim around NVDEC silently
/// no-ops several controls (mirrored to intra-refresh on the encoder),
/// and we already pay the binding cost on the encode side.
/// </summary>
public static partial class NvDecApi
{
    public const string CudaDll = "nvcuda.dll";
    public const string CuvidDll = "nvcuvid.dll";

    // -------------------------------------------------------------------
    // CUDA driver API result codes (subset). The full enum is hundreds
    // long; we expose the ones we actually surface in error messages.
    // -------------------------------------------------------------------
    public enum CUresult : int
    {
        CUDA_SUCCESS = 0,
        CUDA_ERROR_INVALID_VALUE = 1,
        CUDA_ERROR_OUT_OF_MEMORY = 2,
        CUDA_ERROR_NOT_INITIALIZED = 3,
        CUDA_ERROR_DEINITIALIZED = 4,
        CUDA_ERROR_NO_DEVICE = 100,
        CUDA_ERROR_INVALID_DEVICE = 101,
        CUDA_ERROR_INVALID_CONTEXT = 201,
        CUDA_ERROR_INVALID_HANDLE = 400,
        CUDA_ERROR_NOT_SUPPORTED = 801,
        CUDA_ERROR_UNKNOWN = 999,
    }

    // CUDA opaque handle types. All carry an IntPtr because that's
    // what the underlying driver structs are; wrapping as named
    // structs keeps us from confusing a CUcontext with a CUstream
    // at the type-system level.
    [StructLayout(LayoutKind.Sequential)] public struct CUcontext { public IntPtr Handle; public bool IsZero => Handle == IntPtr.Zero; }
    [StructLayout(LayoutKind.Sequential)] public struct CUdevice { public int Value; }
    [StructLayout(LayoutKind.Sequential)] public struct CUstream { public IntPtr Handle; }
    [StructLayout(LayoutKind.Sequential)] public struct CUvideoparser { public IntPtr Handle; public bool IsZero => Handle == IntPtr.Zero; }
    [StructLayout(LayoutKind.Sequential)] public struct CUvideodecoder { public IntPtr Handle; public bool IsZero => Handle == IntPtr.Zero; }
    [StructLayout(LayoutKind.Sequential)] public struct CUgraphicsResource { public IntPtr Handle; public bool IsZero => Handle == IntPtr.Zero; }
    [StructLayout(LayoutKind.Sequential)] public struct CUvideoctxlock { public IntPtr Handle; }
    [StructLayout(LayoutKind.Sequential)] public struct CUarray { public IntPtr Handle; }

    // -------------------------------------------------------------------
    // cudaVideoCodec enum (nvcuvid.h). AV1 is the entry we care about;
    // others are present for type-system completeness so a future H.264
    // decoder can use the same binding file.
    // -------------------------------------------------------------------
    public enum cudaVideoCodec : int
    {
        MPEG1 = 0, MPEG2, MPEG4, VC1,
        H264, JPEG, H264_SVC, H264_MVC,
        HEVC, VP8, VP9, AV1,
        NumCodecs,
    }

    public enum cudaVideoChromaFormat : int
    {
        Monochrome = 0,
        _420 = 1,
        _422 = 2,
        _444 = 3,
    }

    public enum cudaVideoSurfaceFormat : int
    {
        NV12 = 0,
        P016 = 1,
        YUV444 = 2,
        YUV444_16Bit = 3,
    }

    public enum cudaVideoDeinterlaceMode : int
    {
        Weave = 0,
        Bob,
        Adaptive,
    }

    // CUDA → D3D11 interop register flag. 0 (None) is fine for read-back
    // of decoded frames; we copy from the CUarray to the D3D texture.
    public const uint CU_GRAPHICS_REGISTER_FLAGS_NONE = 0;

    // CUDA context creation flags.
    public const uint CU_CTX_SCHED_AUTO = 0x00;

    // -------------------------------------------------------------------
    // CUVIDDECODECAPS — capability probe struct. cuvidGetDecoderCaps
    // takes one populated with the codec / chroma format / bit depth
    // we want and fills in the remaining fields. Field layout matches
    // nvcuvid.h exactly; padding bytes are explicit so Marshal sizes it
    // correctly (the original C struct uses anonymous arrays which we
    // unroll here as fixed fields).
    // -------------------------------------------------------------------
    // Explicit offsets matching nvcuvid.h exactly. Total size is 88 bytes.
    // The earlier 80-byte attempt mis-modelled `unsigned int reserved1[3]`
    // as 3 bytes instead of 12 bytes, which shifted bIsSupported and every
    // OUT field 9 bytes too low — so we kept reading 0 from a location the
    // driver never touched, reproducing supported=False on hardware that
    // definitely supports the codec. The C struct from nvcuvid.h is:
    //   eCodecType         (int, 4)            offset 0
    //   eChromaFormat      (int, 4)            offset 4
    //   nBitDepthMinus8    (uint, 4)           offset 8
    //   reserved1[3]       (uint × 3, 12)      offset 12
    //   bIsSupported       (uchar, 1)          offset 24
    //   nNumNVDECs         (uchar, 1)          offset 25
    //   nOutputFormatMask  (ushort, 2)         offset 26
    //   nMaxWidth          (uint, 4)           offset 28
    //   nMaxHeight         (uint, 4)           offset 32
    //   nMaxMBCount        (uint, 4)           offset 36
    //   nMinWidth          (ushort, 2)         offset 40
    //   nMinHeight         (ushort, 2)         offset 42
    //   bIsHistogramSup.   (uchar, 1)          offset 44
    //   nCounterBitDepth   (uchar, 1)          offset 45
    //   nMaxHistogramBins  (ushort, 2)         offset 46
    //   reserved3[10]      (uint × 10, 40)     offset 48
    //   ─────────────────────────────────────  total 88 bytes
    [StructLayout(LayoutKind.Explicit, Size = 88)]
    public struct CUVIDDECODECAPS
    {
        [FieldOffset(0)] public cudaVideoCodec eCodecType;
        [FieldOffset(4)] public cudaVideoChromaFormat eChromaFormat;
        [FieldOffset(8)] public uint nBitDepthMinus8;
        [FieldOffset(12)] public uint Reserved1_0;
        [FieldOffset(16)] public uint Reserved1_1;
        [FieldOffset(20)] public uint Reserved1_2;
        [FieldOffset(24)] public byte bIsSupported;
        [FieldOffset(25)] public byte nNumNVDECs;
        [FieldOffset(26)] public ushort nOutputFormatMask;
        [FieldOffset(28)] public uint nMaxWidth;
        [FieldOffset(32)] public uint nMaxHeight;
        [FieldOffset(36)] public uint nMaxMBCount;
        [FieldOffset(40)] public ushort nMinWidth;
        [FieldOffset(42)] public ushort nMinHeight;
        [FieldOffset(44)] public byte bIsHistogramSupported;
        [FieldOffset(45)] public byte nCounterBitDepth;
        [FieldOffset(46)] public ushort nMaxHistogramBins;
        [FieldOffset(48)] public uint Reserved3_0;
        [FieldOffset(52)] public uint Reserved3_1;
        [FieldOffset(56)] public uint Reserved3_2;
        [FieldOffset(60)] public uint Reserved3_3;
        [FieldOffset(64)] public uint Reserved3_4;
        [FieldOffset(68)] public uint Reserved3_5;
        [FieldOffset(72)] public uint Reserved3_6;
        [FieldOffset(76)] public uint Reserved3_7;
        [FieldOffset(80)] public uint Reserved3_8;
        [FieldOffset(84)] public uint Reserved3_9;
    }

    // -------------------------------------------------------------------
    // CUVIDPARSERPARAMS / CUVIDSOURCEDATAPACKET / CUVIDEOFORMAT — bitstream
    // parser surface. The parser is fed AV1 OBU temporal units (the same
    // shape MediaFoundationAv1Decoder consumes) and fires three callbacks
    // back into managed code: sequence, decode, display.
    //
    // Callback signatures: nvcuvid.h declares them as
    //   typedef int (CUDAAPI *PFNVIDSEQUENCECALLBACK)(void *, CUVIDEOFORMAT *)
    //   typedef int (CUDAAPI *PFNVIDDECODECALLBACK)(void *, CUVIDPICPARAMS *)
    //   typedef int (CUDAAPI *PFNVIDDISPLAYCALLBACK)(void *, CUVIDPARSERDISPINFO *)
    // CUDAAPI is __stdcall on Windows. We model them with managed
    // delegates flagged UnmanagedFunctionPointer(CallingConvention.StdCall).
    // -------------------------------------------------------------------
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int PfnSequenceCallback(IntPtr userData, ref CUVIDEOFORMAT format);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int PfnDecodeCallback(IntPtr userData, IntPtr picParams);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int PfnDisplayCallback(IntPtr userData, IntPtr dispInfo);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int PfnOpPointCallback(IntPtr userData, IntPtr opPointInfo);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int PfnSeiMsgCallback(IntPtr userData, IntPtr seiMsg);

    [StructLayout(LayoutKind.Sequential)]
    public struct CUVIDPARSERPARAMS
    {
        public cudaVideoCodec CodecType;
        public uint ulMaxNumDecodeSurfaces;
        public uint ulClockRate;
        public uint ulErrorThreshold;
        public uint ulMaxDisplayDelay;
        // bAnnexb : 1 + uReserved : 31 — model as one uint, low bit = AnnexB.
        public uint Bitfields;
        public uint uReserved1_0;
        public uint uReserved1_1;
        public uint uReserved1_2;
        public uint uReserved1_3;
        public IntPtr pUserData;
        public IntPtr pfnSequenceCallback;
        public IntPtr pfnDecodePicture;
        public IntPtr pfnDisplayPicture;
        public IntPtr pfnGetOperatingPoint;
        public IntPtr pfnGetSEIMsg;
        public IntPtr pExtVideoInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CUVIDSOURCEDATAPACKET
    {
        public uint flags;
        public uint payload_size;
        public IntPtr payload;
        public ulong timestamp;
    }

    // Source data packet flags.
    public const uint CUVID_PKT_ENDOFSTREAM = 0x01;
    public const uint CUVID_PKT_TIMESTAMP = 0x02;
    public const uint CUVID_PKT_DISCONTINUITY = 0x04;
    public const uint CUVID_PKT_ENDOFPICTURE = 0x08;

    [StructLayout(LayoutKind.Sequential)]
    public struct CUVIDEOFORMAT
    {
        public cudaVideoCodec codec;
        public uint frame_rate_numerator;
        public uint frame_rate_denominator;
        public byte progressive_sequence;
        public byte bit_depth_luma_minus8;
        public byte bit_depth_chroma_minus8;
        public byte min_num_decode_surfaces;
        public uint coded_width;
        public uint coded_height;
        public int display_area_left;
        public int display_area_top;
        public int display_area_right;
        public int display_area_bottom;
        public cudaVideoChromaFormat chroma_format;
        public uint bitrate;
        public int display_aspect_ratio_numerator;
        public int display_aspect_ratio_denominator;
        // video_signal_description (5 bytes packed — model as raw bytes).
        public byte vsd_video_format_and_full_range;
        public byte vsd_color_primaries;
        public byte vsd_transfer_characteristics;
        public byte vsd_matrix_coefficients;
        public byte vsd_padding;
        public uint seqhdr_data_length;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CUVIDPARSERDISPINFO
    {
        public int picture_index;
        public int progressive_frame;
        public int top_field_first;
        public int repeat_first_field;
        public ulong timestamp;
    }

    // -------------------------------------------------------------------
    // CUVIDDECODECREATEINFO — decoder construction parameters. Filled
    // from the format reported by the parser's sequence callback.
    // Layout matches nvcuvid.h. CRITICAL: nvcuvid.h declares its size
    // fields as `unsigned long`, which on Windows is **4 bytes** (NOT 8
    // like on Linux). The earlier ulong/UInt64 modelling here doubled the
    // struct size and made cuvidCreateDecoder return INVALID_VALUE because
    // the driver read out-of-bounds garbage for every field past
    // ulNumDecodeSurfaces. Use uint (4 bytes) and the size lines up with
    // the SDK header at 108 bytes.
    [StructLayout(LayoutKind.Sequential)]
    public struct CUVIDDECODECREATEINFO
    {
        public uint ulWidth;
        public uint ulHeight;
        public uint ulNumDecodeSurfaces;
        public cudaVideoCodec CodecType;
        public cudaVideoChromaFormat ChromaFormat;
        public uint ulCreationFlags;
        public uint bitDepthMinus8;
        public uint ulIntraDecodeOnly;
        public uint ulMaxWidth;
        public uint ulMaxHeight;
        public uint Reserved1;
        public short display_area_left;
        public short display_area_top;
        public short display_area_right;
        public short display_area_bottom;
        public cudaVideoSurfaceFormat OutputFormat;
        public cudaVideoDeinterlaceMode DeinterlaceMode;
        public uint ulTargetWidth;
        public uint ulTargetHeight;
        public uint ulNumOutputSurfaces;
        public CUvideoctxlock vidLock;
        public short target_rect_left;
        public short target_rect_top;
        public short target_rect_right;
        public short target_rect_bottom;
        public uint enableHistogram;
        public uint Reserved2_0;
        public uint Reserved2_1;
        public uint Reserved2_2;
        public uint Reserved2_3;
    }

    // CUVIDPROCPARAMS — passed to cuvidMapVideoFrame to control
    // de-interlacing / cropping of the output. We always pass a default-
    // initialized one (progressive, no scaling) since AV1 from NVENC
    // is progressive. Final field is `void *histogram_dptr` — required
    // for the struct to match the SDK header size of 256 bytes.
    [StructLayout(LayoutKind.Sequential)]
    public struct CUVIDPROCPARAMS
    {
        public int progressive_frame;
        public int second_field;
        public int top_field_first;
        public int unpaired_field;
        public uint reserved_flags;
        public uint reserved_zero;
        public ulong raw_input_dptr;
        public uint raw_input_pitch;
        public uint raw_input_format;
        public ulong raw_output_dptr;
        public uint raw_output_pitch;
        public uint Reserved1;
        public CUstream output_stream;
        // Reserved[46] padding — NVDEC SDK reserves a generous tail.
        // Use a fixed buffer to match struct size exactly.
        public unsafe fixed uint Reserved[46];
        public IntPtr histogram_dptr;
    }

    // -------------------------------------------------------------------
    // CUDA driver API entry points (subset).
    // -------------------------------------------------------------------
    [LibraryImport(CudaDll, EntryPoint = "cuInit")]
    public static partial CUresult CuInit(uint Flags);

    [LibraryImport(CudaDll, EntryPoint = "cuDeviceGetCount")]
    public static partial CUresult CuDeviceGetCount(out int count);

    [LibraryImport(CudaDll, EntryPoint = "cuDeviceGet")]
    public static partial CUresult CuDeviceGet(out CUdevice device, int ordinal);

    [LibraryImport(CudaDll, EntryPoint = "cuD3D11GetDevice")]
    public static partial CUresult CuD3D11GetDevice(out CUdevice pCudaDevice, IntPtr pAdapter);

    [LibraryImport(CudaDll, EntryPoint = "cuCtxCreate_v2")]
    public static partial CUresult CuCtxCreate(out CUcontext pctx, uint flags, CUdevice dev);

    [LibraryImport(CudaDll, EntryPoint = "cuCtxDestroy_v2")]
    public static partial CUresult CuCtxDestroy(CUcontext ctx);

    /// <summary>
    /// Retain a refcounted reference to the device's primary CUDA
    /// context. Unlike <see cref="CuCtxCreate"/> this does NOT create a
    /// new floating context; instead it returns the device's
    /// driver-managed primary context (creating it on first call) and
    /// bumps its refcount. Safe to call multiple times from the same
    /// process — every code path that needs a CUDA context can retain
    /// and the driver hands back the same one. Pair every Retain with
    /// a <see cref="CuDevicePrimaryCtxRelease"/> on dispose.
    ///
    /// Why we use this instead of cuCtxCreate: floating contexts have a
    /// known intermittent-AV failure mode on Windows when one floating
    /// context is destroyed and another is created on the same device
    /// while D3D11 work is happening concurrently. Switching the probe
    /// and the decoders to share the primary context eliminates the
    /// destroy/recreate cycle and the driver race that was the root
    /// cause of "[nvdec-av1-init] cuCtxCreate" crashes in the field.
    /// </summary>
    [LibraryImport(CudaDll, EntryPoint = "cuDevicePrimaryCtxRetain")]
    public static partial CUresult CuDevicePrimaryCtxRetain(out CUcontext pctx, CUdevice dev);

    [LibraryImport(CudaDll, EntryPoint = "cuDevicePrimaryCtxRelease_v2")]
    public static partial CUresult CuDevicePrimaryCtxRelease(CUdevice dev);

    [LibraryImport(CudaDll, EntryPoint = "cuCtxPushCurrent_v2")]
    public static partial CUresult CuCtxPushCurrent(CUcontext ctx);

    [LibraryImport(CudaDll, EntryPoint = "cuCtxPopCurrent_v2")]
    public static partial CUresult CuCtxPopCurrent(out CUcontext pctx);

    [LibraryImport(CuvidDll, EntryPoint = "cuvidCtxLockCreate")]
    public static partial CUresult CuvidCtxLockCreate(out CUvideoctxlock pLock, CUcontext ctx);

    [LibraryImport(CuvidDll, EntryPoint = "cuvidCtxLockDestroy")]
    public static partial CUresult CuvidCtxLockDestroy(CUvideoctxlock lck);

    [LibraryImport(CudaDll, EntryPoint = "cuMemcpy2D_v2")]
    public static partial CUresult CuMemcpy2D(IntPtr pCopy);

    [LibraryImport(CudaDll, EntryPoint = "cuGraphicsD3D11RegisterResource")]
    public static partial CUresult CuGraphicsD3D11RegisterResource(out CUgraphicsResource pCudaResource, IntPtr pD3DResource, uint Flags);

    [LibraryImport(CudaDll, EntryPoint = "cuGraphicsUnregisterResource")]
    public static partial CUresult CuGraphicsUnregisterResource(CUgraphicsResource resource);

    [LibraryImport(CudaDll, EntryPoint = "cuGraphicsResourceSetMapFlags_v2")]
    public static partial CUresult CuGraphicsResourceSetMapFlags(CUgraphicsResource resource, uint flags);

    public const uint CU_GRAPHICS_MAP_RESOURCE_FLAGS_NONE = 0x00;
    public const uint CU_GRAPHICS_MAP_RESOURCE_FLAGS_READ_ONLY = 0x01;
    public const uint CU_GRAPHICS_MAP_RESOURCE_FLAGS_WRITE_DISCARD = 0x02;

    [LibraryImport(CudaDll, EntryPoint = "cuGraphicsMapResources")]
    public static partial CUresult CuGraphicsMapResources(uint count, ref CUgraphicsResource resources, CUstream hStream);

    [LibraryImport(CudaDll, EntryPoint = "cuGraphicsUnmapResources")]
    public static partial CUresult CuGraphicsUnmapResources(uint count, ref CUgraphicsResource resources, CUstream hStream);

    [LibraryImport(CudaDll, EntryPoint = "cuGraphicsSubResourceGetMappedArray")]
    public static partial CUresult CuGraphicsSubResourceGetMappedArray(out CUarray pArray, CUgraphicsResource resource, uint arrayIndex, uint mipLevel);

    // -------------------------------------------------------------------
    // cuvid (NVDEC SDK runtime) entry points.
    // -------------------------------------------------------------------
    // cuvidGetDecoderCaps via direct IntPtr — LibraryImport's source-
    // generated marshaller for `ref struct-with-explicit-layout` was
    // returning CUDA_SUCCESS with bIsSupported=0 for EVERY codec
    // (including codecs the GPU definitely supports), suggesting the
    // function call wasn't seeing our struct address. Switching to
    // DllImport with a raw IntPtr and manual Marshal.StructureToPtr
    // sidesteps the source generator entirely.
    [DllImport(CuvidDll, EntryPoint = "cuvidGetDecoderCaps", CallingConvention = CallingConvention.StdCall)]
    public static extern CUresult CuvidGetDecoderCapsRaw(IntPtr pdc);

    public static CUresult CuvidGetDecoderCaps(ref CUVIDDECODECAPS pdc)
    {
        var size = Marshal.SizeOf<CUVIDDECODECAPS>();
        var native = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(pdc, native, fDeleteOld: false);
            var result = CuvidGetDecoderCapsRaw(native);
            pdc = Marshal.PtrToStructure<CUVIDDECODECAPS>(native);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(native);
        }
    }

    [LibraryImport(CuvidDll, EntryPoint = "cuvidCreateVideoParser")]
    public static partial CUresult CuvidCreateVideoParser(out CUvideoparser pObj, ref CUVIDPARSERPARAMS pParams);

    [LibraryImport(CuvidDll, EntryPoint = "cuvidParseVideoData")]
    public static partial CUresult CuvidParseVideoData(CUvideoparser obj, ref CUVIDSOURCEDATAPACKET pPacket);

    [LibraryImport(CuvidDll, EntryPoint = "cuvidDestroyVideoParser")]
    public static partial CUresult CuvidDestroyVideoParser(CUvideoparser obj);

    [LibraryImport(CuvidDll, EntryPoint = "cuvidCreateDecoder")]
    public static partial CUresult CuvidCreateDecoder(out CUvideodecoder phDecoder, ref CUVIDDECODECREATEINFO pdci);

    [LibraryImport(CuvidDll, EntryPoint = "cuvidDecodePicture")]
    public static partial CUresult CuvidDecodePicture(CUvideodecoder hDecoder, IntPtr pPicParams);

    [LibraryImport(CuvidDll, EntryPoint = "cuvidMapVideoFrame64")]
    public static partial CUresult CuvidMapVideoFrame(CUvideodecoder hDecoder, int nPicIdx, out ulong pDevPtr, out uint pPitch, ref CUVIDPROCPARAMS pVPP);

    [LibraryImport(CuvidDll, EntryPoint = "cuvidUnmapVideoFrame64")]
    public static partial CUresult CuvidUnmapVideoFrame(CUvideodecoder hDecoder, ulong DevPtr);

    [LibraryImport(CuvidDll, EntryPoint = "cuvidDestroyDecoder")]
    public static partial CUresult CuvidDestroyDecoder(CUvideodecoder hDecoder);

    // Convenience: lookup the parser parameters' AnnexB bitfield without
    // exposing the full bitfield wrangle to callers.
    public static uint MakeBitfields(bool annexB) => annexB ? 1u : 0u;
}

/// <summary>
/// Marshalled CUDA_MEMCPY2D struct used to copy decoded NVDEC output
/// (a CUDA device pointer) into a CUarray that's been mapped from a
/// D3D11 texture via <c>cuGraphicsSubResourceGetMappedArray</c>. The
/// shape is identical to the C struct in cuda.h. We use this to hand
/// NV12 output to the renderer without ever leaving GPU memory.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct CUDA_MEMCPY2D
{
    public IntPtr srcXInBytes;
    public IntPtr srcY;
    public CUmemorytype srcMemoryType;
    public IntPtr srcHost;
    public ulong srcDevice;
    public IntPtr srcArray;
    public IntPtr srcPitch;

    public IntPtr dstXInBytes;
    public IntPtr dstY;
    public CUmemorytype dstMemoryType;
    public IntPtr dstHost;
    public ulong dstDevice;
    public IntPtr dstArray;
    public IntPtr dstPitch;

    public IntPtr WidthInBytes;
    public IntPtr Height;
}

public enum CUmemorytype : int
{
    HOST = 0x01,
    DEVICE = 0x02,
    ARRAY = 0x03,
    UNIFIED = 0x04,
}
