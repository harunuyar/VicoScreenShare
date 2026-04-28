namespace VicoScreenShare.Client.Windows.Media.Codecs.Nvenc;

using System;
using System.Runtime.InteropServices;

/// <summary>
/// P/Invoke surface for NVIDIA's NVENC SDK (NVENCAPI 13.0).
///
/// The SDK exposes its API in two layers:
/// <list type="number">
///   <item><description>
///     Two exported entrypoints in <c>nvEncodeAPI64.dll</c>:
///     <c>NvEncodeAPICreateInstance</c> and <c>NvEncodeAPIGetMaxSupportedVersion</c>.
///   </description></item>
///   <item><description>
///     Every other call (open session, query capabilities, encode picture,
///     etc.) goes through function pointers populated into
///     <see cref="NV_ENCODE_API_FUNCTION_LIST"/> by the
///     <c>NvEncodeAPICreateInstance</c> call. There is intentionally no
///     stable export name for each individual entrypoint — the function
///     table is the API.
///   </description></item>
/// </list>
///
/// Struct version constants here match SDK 13.0; mismatches surface as
/// <see cref="NVENCSTATUS.NV_ENC_ERR_INVALID_VERSION"/> from any call,
/// which is why <see cref="NV_ENCODE_API_FUNCTION_LIST_VER"/> and friends
/// are the first thing tested.
/// </summary>
internal static partial class NvencApi
{
    public const string DllName = "nvEncodeAPI64.dll";

    // -------------------------------------------------------------------
    // Version macros — mirror nvEncodeAPI.h lines 118–126:
    //   #define NVENCAPI_MAJOR_VERSION 13
    //   #define NVENCAPI_MINOR_VERSION 0
    //   #define NVENCAPI_VERSION (MAJOR | (MINOR << 24))
    //   #define NVENCAPI_STRUCT_VERSION(ver) (NVENCAPI_VERSION | (ver << 16) | (0x7 << 28))
    // The 0x7<<28 is the "API signature" baked into every struct's version
    // field — present in NVENC ≥ 9.0; the driver rejects anything missing it.
    // -------------------------------------------------------------------

    public const uint NVENCAPI_MAJOR_VERSION = 13;
    public const uint NVENCAPI_MINOR_VERSION = 0;
    public const uint NVENCAPI_VERSION = NVENCAPI_MAJOR_VERSION | (NVENCAPI_MINOR_VERSION << 24);

    public static uint StructVersion(uint v) => NVENCAPI_VERSION | (v << 16) | (0x7u << 28);

    // Some struct versions also OR in (1u << 31) — call it the "newer struct"
    // override flag. The SDK header sets it on every struct that has been
    // versioned forward at least once (NV_ENC_CONFIG_VER, NV_ENC_PIC_PARAMS_VER,
    // NV_ENC_INITIALIZE_PARAMS_VER, NV_ENC_LOCK_BITSTREAM_VER, ...). Mismatches
    // surface as NV_ENC_ERR_INVALID_VERSION.
    private const uint NewStructFlag = 1u << 31;

    public static readonly uint NV_ENCODE_API_FUNCTION_LIST_VER = StructVersion(2);
    public static readonly uint NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER = StructVersion(1);
    public static readonly uint NV_ENC_CAPS_PARAM_VER = StructVersion(1);
    public static readonly uint NV_ENC_INITIALIZE_PARAMS_VER = StructVersion(7) | NewStructFlag;
    public static readonly uint NV_ENC_CONFIG_VER = StructVersion(9) | NewStructFlag;
    public static readonly uint NV_ENC_PRESET_CONFIG_VER = StructVersion(5) | NewStructFlag;
    public static readonly uint NV_ENC_RC_PARAMS_VER = StructVersion(1);
    public static readonly uint NV_ENC_RECONFIGURE_PARAMS_VER = StructVersion(2) | NewStructFlag;
    public static readonly uint NV_ENC_PIC_PARAMS_VER = StructVersion(7) | NewStructFlag;
    public static readonly uint NV_ENC_LOCK_BITSTREAM_VER = StructVersion(2) | NewStructFlag;
    public static readonly uint NV_ENC_CREATE_BITSTREAM_BUFFER_VER = StructVersion(1);
    public static readonly uint NV_ENC_REGISTER_RESOURCE_VER = StructVersion(5);
    public static readonly uint NV_ENC_MAP_INPUT_RESOURCE_VER = StructVersion(4);
    public static readonly uint NV_ENC_EVENT_PARAMS_VER = StructVersion(2);

    public const uint NVENC_INFINITE_GOPLENGTH = 0xFFFFFFFFu;

    // -------------------------------------------------------------------
    // The two exported entrypoints. Everything else is loaded through the
    // function table populated by NvEncodeAPICreateInstance.
    // -------------------------------------------------------------------

    [LibraryImport(DllName, EntryPoint = "NvEncodeAPIGetMaxSupportedVersion")]
    public static partial NVENCSTATUS NvEncodeAPIGetMaxSupportedVersion(out uint version);

    [LibraryImport(DllName, EntryPoint = "NvEncodeAPICreateInstance")]
    public static partial NVENCSTATUS NvEncodeAPICreateInstance(ref NV_ENCODE_API_FUNCTION_LIST functionList);

    // -------------------------------------------------------------------
    // Function-table delegates. The SDK header lays out 42 entrypoints
    // (see _NV_ENCODE_API_FUNCTION_LIST at line 4591); we declare delegates
    // only for the ones we actually call. The unused slots are kept as
    // IntPtr in the struct so layout/alignment stays correct.
    //
    // NVENCAPI calling convention is __stdcall on Windows 64-bit, which
    // is x64's default — UnmanagedFunctionPointer attribute not required.
    // -------------------------------------------------------------------

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public unsafe delegate NVENCSTATUS NvEncOpenEncodeSessionExFn(ref NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS p, IntPtr* encoder);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate NVENCSTATUS NvEncDestroyEncoderFn(IntPtr encoder);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public unsafe delegate NVENCSTATUS NvEncGetEncodeGUIDCountFn(IntPtr encoder, uint* encodeGuidCount);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public unsafe delegate NVENCSTATUS NvEncGetEncodeGUIDsFn(IntPtr encoder, Guid* guids, uint guidArraySize, uint* count);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public unsafe delegate NVENCSTATUS NvEncGetEncodeCapsFn(IntPtr encoder, Guid encodeGuid, ref NV_ENC_CAPS_PARAM capsParam, int* capsVal);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate IntPtr NvEncGetLastErrorStringFn(IntPtr encoder);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public unsafe delegate NVENCSTATUS NvEncGetEncodePresetConfigExFn(
        IntPtr encoder,
        Guid encodeGuid,
        Guid presetGuid,
        NV_ENC_TUNING_INFO tuningInfo,
        NV_ENC_PRESET_CONFIG* presetConfig);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public unsafe delegate NVENCSTATUS NvEncInitializeEncoderFn(IntPtr encoder, NV_ENC_INITIALIZE_PARAMS* p);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public unsafe delegate NVENCSTATUS NvEncReconfigureEncoderFn(IntPtr encoder, NV_ENC_RECONFIGURE_PARAMS* p);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public unsafe delegate NVENCSTATUS NvEncRegisterResourceFn(IntPtr encoder, NV_ENC_REGISTER_RESOURCE* p);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate NVENCSTATUS NvEncUnregisterResourceFn(IntPtr encoder, IntPtr registeredResource);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public unsafe delegate NVENCSTATUS NvEncMapInputResourceFn(IntPtr encoder, NV_ENC_MAP_INPUT_RESOURCE* p);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate NVENCSTATUS NvEncUnmapInputResourceFn(IntPtr encoder, IntPtr mappedResource);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public unsafe delegate NVENCSTATUS NvEncCreateBitstreamBufferFn(IntPtr encoder, NV_ENC_CREATE_BITSTREAM_BUFFER* p);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate NVENCSTATUS NvEncDestroyBitstreamBufferFn(IntPtr encoder, IntPtr bitstreamBuffer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public unsafe delegate NVENCSTATUS NvEncRegisterAsyncEventFn(IntPtr encoder, NV_ENC_EVENT_PARAMS* p);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public unsafe delegate NVENCSTATUS NvEncUnregisterAsyncEventFn(IntPtr encoder, NV_ENC_EVENT_PARAMS* p);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public unsafe delegate NVENCSTATUS NvEncEncodePictureFn(IntPtr encoder, NV_ENC_PIC_PARAMS* p);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public unsafe delegate NVENCSTATUS NvEncLockBitstreamFn(IntPtr encoder, NV_ENC_LOCK_BITSTREAM* p);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate NVENCSTATUS NvEncUnlockBitstreamFn(IntPtr encoder, IntPtr bitstreamBuffer);
}

// ============================================================================
// Enums and structs needed for Phase 2 encode path. Layouts mirror the SDK
// header byte-for-byte. Bit-field runs in the C source are collapsed into a
// single uint32 — the driver's view of the struct is the same; the only
// difference is that we set the flags via shift+OR instead of ":1" syntax.
// ============================================================================

/// <summary>nvEncodeAPI.h:271 — rate control mode.</summary>
public enum NV_ENC_PARAMS_RC_MODE : uint
{
    ConstQp = 0x0,
    Vbr = 0x1,
    Cbr = 0x2,
}

/// <summary>nvEncodeAPI.h:281 — multi-pass mode for adaptive rate control.</summary>
public enum NV_ENC_MULTI_PASS : uint
{
    Disabled = 0x0,
    TwoPassQuarterResolution = 0x1,
    TwoPassFullResolution = 0x2,
}

/// <summary>nvEncodeAPI.h:261 — frame/field encode mode. Frame for screen content.</summary>
public enum NV_ENC_PARAMS_FRAME_FIELD_MODE : uint
{
    Frame = 0x01,
    Field = 0x02,
    Mbaff = 0x03,
}

/// <summary>nvEncodeAPI.h:373 — motion vector precision. Default = quarter-pel.</summary>
public enum NV_ENC_MV_PRECISION : uint
{
    Default = 0x0,
    FullPel = 0x01,
    HalfPel = 0x02,
    QuarterPel = 0x03,
}

/// <summary>nvEncodeAPI.h:320 — interpretation of NV_ENC_PIC_PARAMS::qpDeltaMap.</summary>
public enum NV_ENC_QP_MAP_MODE : uint
{
    Disabled = 0x0,
    Emphasis = 0x1,
    Delta = 0x2,
    Map = 0x3,
}

/// <summary>nvEncodeAPI.h:1338 — lookahead level.</summary>
public enum NV_ENC_LOOKAHEAD_LEVEL : uint
{
    Level0 = 0,
    Level1 = 1,
    Level2 = 2,
    Level3 = 3,
    Autoselect = 15,
}

/// <summary>nvEncodeAPI.h:2205 — preset tuning.</summary>
public enum NV_ENC_TUNING_INFO : uint
{
    Undefined = 0,
    HighQuality = 1,
    LowLatency = 2,
    UltraLowLatency = 3,
    Lossless = 4,
    UltraHighQuality = 5,
}

/// <summary>nvEncodeAPI.h:295 — output stats granularity. Phase 2 leaves this off.</summary>
public enum NV_ENC_OUTPUT_STATS_LEVEL : uint
{
    None = 0,
    BlockLevel = 1,
    RowLevel = 2,
}

/// <summary>nvEncodeAPI.h:385 — input buffer formats. ARGB = packed BGRA on Windows.</summary>
public enum NV_ENC_BUFFER_FORMAT : uint
{
    Undefined = 0x00000000,
    Nv12 = 0x00000001,
    Yv12 = 0x00000010,
    Iyuv = 0x00000100,
    Yuv444 = 0x00001000,
    Yuv420_10Bit = 0x00010000,
    Yuv444_10Bit = 0x00100000,
    Argb = 0x01000000,
    Argb10 = 0x02000000,
    Ayuv = 0x04000000,
    Abgr = 0x10000000,
    Abgr10 = 0x20000000,
}

/// <summary>nvEncodeAPI.h:774 — input resource type. We only use DirectX.</summary>
public enum NV_ENC_INPUT_RESOURCE_TYPE : uint
{
    Directx = 0x0,
    CudaDevicePtr = 0x1,
    CudaArray = 0x2,
    OpenglTex = 0x3,
}

/// <summary>nvEncodeAPI.h:787 — surface-usage flag for register-resource.</summary>
public enum NV_ENC_BUFFER_USAGE : uint
{
    InputImage = 0x0,
    OutputMotionVector = 0x1,
    OutputBitstream = 0x2,
    OutputRecon = 0x4,
}

/// <summary>nvEncodeAPI.h:332 — input picture structure. We use Frame (progressive).</summary>
public enum NV_ENC_PIC_STRUCT : uint
{
    Frame = 0x01,
    FieldTopBottom = 0x02,
    FieldBottomTop = 0x03,
}

/// <summary>nvEncodeAPI.h:344 — display picture structure for SEI timecodes.</summary>
public enum NV_ENC_DISPLAY_PIC_STRUCT : uint
{
    Frame = 0x00,
    FieldTopBottom = 0x01,
    FieldBottomTop = 0x02,
    FrameDoubling = 0x03,
    FrameTripling = 0x04,
}

/// <summary>nvEncodeAPI.h:356 — input picture type (only used when enablePTD=0).</summary>
public enum NV_ENC_PIC_TYPE : uint
{
    P = 0x0,
    B = 0x01,
    I = 0x02,
    Idr = 0x03,
    Bi = 0x04,
    Skipped = 0x05,
    IntraRefresh = 0x06,
    NonRefP = 0x07,
    Switch = 0x08,
    Unknown = 0xFF,
}

/// <summary>nvEncodeAPI.h:683 — bitwise flags for NV_ENC_PIC_PARAMS::encodePicFlags.</summary>
[Flags]
public enum NV_ENC_PIC_FLAGS : uint
{
    None = 0x0,
    ForceIntra = 0x1,
    ForceIdr = 0x2,
    OutputSpsPps = 0x4,
    Eos = 0x8,
    DisableEncStateAdvance = 0x10,
    OutputReconFrame = 0x20,
}

/// <summary>nvEncodeAPI.h:1543 — QP triple for the three frame types.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NV_ENC_QP
{
    public uint qpInterP;
    public uint qpInterB;
    public uint qpIntra;
}

/// <summary>nvEncodeAPI.h:1555 — rate control config. The bit fields enableMinQP,
/// enableMaxQP, enableInitialRCQP, enableAQ, enableLookahead, disableIadapt,
/// disableBadapt, enableTemporalAQ, zeroReorderDelay, enableNonRefP,
/// strictGOPTarget, aqStrength (4 bits), enableExtLookahead, plus 16 reserved
/// bits — total 32 bits — collapse into <see cref="bitfields"/>. Use the
/// <see cref="RcParamsBits"/> constants and helpers below to set them.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NV_ENC_RC_PARAMS
{
    public uint version;
    public NV_ENC_PARAMS_RC_MODE rateControlMode;
    public NV_ENC_QP constQP;
    public uint averageBitRate;
    public uint maxBitRate;
    public uint vbvBufferSize;
    public uint vbvInitialDelay;
    public uint bitfields; // see RcParamsBits
    public NV_ENC_QP minQP;
    public NV_ENC_QP maxQP;
    public NV_ENC_QP initialRCQP;
    public uint temporallayerIdxMask;
    private unsafe fixed byte temporalLayerQP[8];
    public byte targetQuality;
    public byte targetQualityLSB;
    public ushort lookaheadDepth;
    public byte lowDelayKeyFrameScale;
    public sbyte yDcQPIndexOffset;
    public sbyte uDcQPIndexOffset;
    public sbyte vDcQPIndexOffset;
    public NV_ENC_QP_MAP_MODE qpMapMode;
    public NV_ENC_MULTI_PASS multiPass;
    public uint alphaLayerBitrateRatio;
    public sbyte cbQPIndexOffset;
    public sbyte crQPIndexOffset;
    public ushort reserved2;
    public NV_ENC_LOOKAHEAD_LEVEL lookaheadLevel;
    private unsafe fixed byte viewBitrateRatios[7]; // MAX_NUM_VIEWS_MINUS_1
    public byte reserved3;
    public uint reserved1;
}

/// <summary>Bit-mask layout of <see cref="NV_ENC_RC_PARAMS.bitfields"/>.
/// Order matches the C struct definition exactly — do not reorder.</summary>
public static class RcParamsBits
{
    public const uint EnableMinQp = 1u << 0;
    public const uint EnableMaxQp = 1u << 1;
    public const uint EnableInitialRCQp = 1u << 2;
    public const uint EnableAQ = 1u << 3;
    public const uint ReservedBitField1 = 1u << 4;
    public const uint EnableLookahead = 1u << 5;
    public const uint DisableIadapt = 1u << 6;
    public const uint DisableBadapt = 1u << 7;
    public const uint EnableTemporalAQ = 1u << 8;
    public const uint ZeroReorderDelay = 1u << 9;
    public const uint EnableNonRefP = 1u << 10;
    public const uint StrictGOPTarget = 1u << 11;
    public const int AqStrengthShift = 12; // 4 bits
    public const uint AqStrengthMask = 0xFu << AqStrengthShift;
    public const uint EnableExtLookahead = 1u << 16;
}

/// <summary>nvEncodeAPI.h:2167 — codec-specific config union. The size
/// is 1280 bytes (320 × uint32, set by the union's reserved member which
/// is always largest). We hold the union as opaque bytes; to mutate
/// H.264-specific fields, cast the address of this struct to
/// <see cref="NV_ENC_CONFIG_H264_OVERLAY"/> and write through that.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NV_ENC_CODEC_CONFIG
{
    public fixed byte storage[1280]; // 320 uint32 reserved words
}

/// <summary>
/// Overlay onto the leading bytes of <see cref="NV_ENC_CODEC_CONFIG"/>
/// when the codec is H.264. Mirrors the layout of
/// <c>NV_ENC_CONFIG_H264</c> at nvEncodeAPI.h:1805 up through the fields
/// we care about. Fields beyond <see cref="intraRefreshCnt"/> are not
/// declared here — the rest stays as whatever the preset filled in.
///
/// The bit-field run at the start of the C struct collapses into
/// <see cref="bitfields"/>; constants for individual bits live in
/// <see cref="H264ConfigBits"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct NV_ENC_CONFIG_H264_OVERLAY
{
    public uint bitfields;
    public uint level;
    public uint idrPeriod;
    public uint separateColourPlaneFlag;
    public uint disableDeblockingFilterIDC;
    public uint numTemporalLayers;
    public uint spsId;
    public uint ppsId;
    public uint adaptiveTransformMode;
    public uint fmoMode;
    public uint bdirectMode;
    public uint entropyCodingMode;
    public uint stereoMode;
    public uint intraRefreshPeriod;
    public uint intraRefreshCnt;
}

/// <summary>
/// Bit positions inside <see cref="NV_ENC_CONFIG_H264_OVERLAY.bitfields"/>.
/// Order matches the C struct's declaration of the leading :1 fields
/// (nvEncodeAPI.h:1807) — do not reorder.
/// </summary>
public static class H264ConfigBits
{
    public const uint EnableTemporalSVC = 1u << 0;
    public const uint EnableStereoMVC = 1u << 1;
    public const uint HierarchicalPFrames = 1u << 2;
    public const uint HierarchicalBFrames = 1u << 3;
    public const uint OutputBufferingPeriodSEI = 1u << 4;
    public const uint OutputPictureTimingSEI = 1u << 5;
    public const uint OutputAUD = 1u << 6;
    public const uint DisableSPSPPS = 1u << 7;
    public const uint OutputFramePackingSEI = 1u << 8;
    public const uint OutputRecoveryPointSEI = 1u << 9;
    public const uint EnableIntraRefresh = 1u << 10;
    public const uint EnableConstrainedEncoding = 1u << 11;
    public const uint RepeatSPSPPS = 1u << 12;
    public const uint EnableVFR = 1u << 13;
    public const uint EnableLTR = 1u << 14;
    public const uint QpPrimeYZeroTransformBypassFlag = 1u << 15;
    public const uint UseConstrainedIntraPred = 1u << 16;
    public const uint EnableFillerDataInsertion = 1u << 17;
    public const uint DisableSVCPrefixNalu = 1u << 18;
    public const uint EnableScalabilityInfoSEI = 1u << 19;
    public const uint SingleSliceIntraRefresh = 1u << 20;
    public const uint EnableTimeCode = 1u << 21;
}

/// <summary>nvEncodeAPI.h:2182 — encoder configuration. Filled by
/// GetEncodePresetConfigEx; we read the rcParams field, apply our
/// bitrate / GOP overrides, and pass it back to InitializeEncoder.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NV_ENC_CONFIG
{
    public uint version;
    public Guid profileGUID;
    public uint gopLength;
    public int frameIntervalP;
    public uint monoChromeEncoding;
    public NV_ENC_PARAMS_FRAME_FIELD_MODE frameFieldMode;
    public NV_ENC_MV_PRECISION mvPrecision;
    public NV_ENC_RC_PARAMS rcParams;
    public NV_ENC_CODEC_CONFIG encodeCodecConfig;
    private fixed uint reserved[278];
    private fixed byte reserved2[64 * 8]; // 64 void* slots
}

/// <summary>nvEncodeAPI.h:2337 — preset config wrapper that the driver
/// fills in via GetEncodePresetConfigEx. We read presetCfg, modify the
/// bits we care about, and feed it back through NV_ENC_INITIALIZE_PARAMS.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NV_ENC_PRESET_CONFIG
{
    public uint version;
    public uint reserved;
    public NV_ENC_CONFIG presetCfg;
    private fixed uint reserved1[256];
    private fixed byte reserved2[64 * 8];
}

/// <summary>nvEncodeAPI.h:2233 — encoder init parameters. The bit fields
/// in the C source (reportSliceOffsets, enableSubFrameWrite, etc.) collapse
/// into <see cref="bitfields"/>. The trailing reserved padding mirrors the
/// SDK byte-for-byte so the version check passes.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NV_ENC_INITIALIZE_PARAMS
{
    public uint version;
    public Guid encodeGUID;
    public Guid presetGUID;
    public uint encodeWidth;
    public uint encodeHeight;
    public uint darWidth;
    public uint darHeight;
    public uint frameRateNum;
    public uint frameRateDen;
    public uint enableEncodeAsync;
    public uint enablePTD;
    public uint bitfields; // reportSliceOffsets:1, enableSubFrameWrite:1, ... reservedBitFields:19
    public uint privDataSize;
    public uint reserved;
    public IntPtr privData;
    public NV_ENC_CONFIG* encodeConfig;
    public uint maxEncodeWidth;
    public uint maxEncodeHeight;
    // NVENC_EXTERNAL_ME_HINT_COUNTS_PER_BLOCKTYPE is a 16-byte struct (one
    // packed bitfield uint + 3 reserved uint32s, see SDK header line 1753).
    // The array of 2 = 32 bytes total. We treat it as opaque since we
    // never set ME hints — but the size has to match exactly or every
    // field after it is read from the wrong offset by the driver.
    private fixed byte maxMEHintCountsPerBlock[32];
    public NV_ENC_TUNING_INFO tuningInfo;
    public NV_ENC_BUFFER_FORMAT bufferFormat;
    public uint numStateBuffers;
    public NV_ENC_OUTPUT_STATS_LEVEL outputStatsLevel;
    private fixed uint reserved1[284];
    private fixed byte reserved2[64 * 8];
}

/// <summary>nvEncodeAPI.h:2302 — runtime reconfigure params.
/// <see cref="forceIDR"/> and <see cref="resetEncoder"/> live in the
/// bitfields uint (each is :1 in the C source).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NV_ENC_RECONFIGURE_PARAMS
{
    public uint version;
    public NV_ENC_INITIALIZE_PARAMS reInitEncodeParams;
    public uint bitfields; // resetEncoder:1, forceIDR:1, reserved:30
}

public static class ReconfigureBits
{
    public const uint ResetEncoder = 1u << 0;
    public const uint ForceIDR = 1u << 1;
}

/// <summary>nvEncodeAPI.h:2826 — register a D3D11 texture (or CUDA buffer)
/// with NVENC so it can be used as input. <see cref="resourceToRegister"/>
/// is the texture pointer; <see cref="registeredResource"/> is filled by
/// the driver and is what we pass to MapInputResource.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NV_ENC_REGISTER_RESOURCE
{
    public uint version;
    public NV_ENC_INPUT_RESOURCE_TYPE resourceType;
    public uint width;
    public uint height;
    public uint pitch;
    public uint subResourceIndex;
    public IntPtr resourceToRegister;
    public IntPtr registeredResource;
    public NV_ENC_BUFFER_FORMAT bufferFormat;
    public NV_ENC_BUFFER_USAGE bufferUsage;
    public IntPtr pInputFencePoint; // unused (D3D12)
    public uint chromaOffset0;
    public uint chromaOffset1;
    public uint chromaOffsetIn0;
    public uint chromaOffsetIn1;
    private fixed uint reserved1[244];
    private fixed byte reserved2[61 * 8];
}

/// <summary>nvEncodeAPI.h:2742 — map a registered resource so the encoder
/// can use it for the next frame.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NV_ENC_MAP_INPUT_RESOURCE
{
    public uint version;
    public uint subResourceIndex; // deprecated
    public IntPtr inputResource;  // deprecated
    public IntPtr registeredResource;
    public IntPtr mappedResource;
    public NV_ENC_BUFFER_FORMAT mappedBufferFmt;
    private fixed uint reserved1[251];
    private fixed byte reserved2[63 * 8];
}

/// <summary>nvEncodeAPI.h:1475 — output bitstream buffer creation params.
/// Driver fills <see cref="bitstreamBuffer"/>; we keep the handle until
/// NvEncDestroyBitstreamBuffer.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NV_ENC_CREATE_BITSTREAM_BUFFER
{
    public uint version;
    public uint size; // deprecated
    public uint memoryHeap; // deprecated (NV_ENC_MEMORY_HEAP)
    public uint reserved;
    public IntPtr bitstreamBuffer;
    public IntPtr bitstreamBufferPtr; // reserved
    private fixed uint reserved1[58];
    private fixed byte reserved2[64 * 8];
}

/// <summary>nvEncodeAPI.h:2928 — register a Win32 event handle with NVENC
/// for async-encode completion notification.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NV_ENC_EVENT_PARAMS
{
    public uint version;
    public uint reserved;
    public IntPtr completionEvent;
    private fixed uint reserved1[254];
    private fixed byte reserved2[64 * 8];
}

/// <summary>nvEncodeAPI.h:2564 — per-frame encode params.
/// <see cref="encodePicFlags"/> takes <see cref="NV_ENC_PIC_FLAGS"/> values
/// (e.g. ForceIdr for keyframe requests). Many fields after the standard
/// inputs are codec-specific or external-ME-hint plumbing we don't use.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NV_ENC_PIC_PARAMS
{
    public uint version;
    public uint inputWidth;
    public uint inputHeight;
    public uint inputPitch;
    public uint encodePicFlags; // NV_ENC_PIC_FLAGS bitwise-or
    public uint frameIdx;
    public ulong inputTimeStamp;
    public ulong inputDuration;
    public IntPtr inputBuffer;       // mappedResource from MapInputResource
    public IntPtr outputBitstream;   // bitstreamBuffer from CreateBitstreamBuffer
    public IntPtr completionEvent;   // Win32 event handle, registered async
    public NV_ENC_BUFFER_FORMAT bufferFmt;
    public NV_ENC_PIC_STRUCT pictureStruct;
    public NV_ENC_PIC_TYPE pictureType;
    // NV_ENC_CODEC_PIC_PARAMS union — the SDK header reserves 256 uint32s
    // as the union's reserved member, so the union is at least 1024 bytes
    // (1024 = max-member size; H.264/HEVC/AV1 sub-structs are smaller).
    private fixed byte codecPicParams[256 * 4];
    // meHintCountsPerBlock[2] — same 16-byte struct as in INITIALIZE_PARAMS,
    // total 32 bytes for the array.
    private fixed byte meHintCountsPerBlock[32];
    public IntPtr meExternalHints;
    private fixed uint reserved2[7];
    private fixed byte reserved5[2 * 8];
    public IntPtr qpDeltaMap;
    public uint qpDeltaMapSize;
    public uint reservedBitFields;
    public ushort meHintRefPicDist0;
    public ushort meHintRefPicDist1;
    public uint reserved4;
    public IntPtr alphaBuffer;
    public IntPtr meExternalSbHints;
    public uint meSbHintsCount;
    public uint stateBufferIdx;
    public IntPtr outputReconBuffer;
    private fixed uint reserved3[284];
    private fixed byte reserved6[57 * 8];
}

/// <summary>nvEncodeAPI.h:2675 — bitstream lock/copy params. Read
/// <see cref="bitstreamBufferPtr"/> + <see cref="bitstreamSizeInBytes"/>
/// after a successful lock.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NV_ENC_LOCK_BITSTREAM
{
    public uint version;
    public uint bitfields; // doNotWait:1, ltrFrame:1, getRCStats:1, reserved:29
    public IntPtr outputBitstream;
    public IntPtr sliceOffsets;
    public uint frameIdx;
    public uint hwEncodeStatus;
    public uint numSlices;
    public uint bitstreamSizeInBytes;
    public ulong outputTimeStamp;
    public ulong outputDuration;
    public IntPtr bitstreamBufferPtr;
    public NV_ENC_PIC_TYPE pictureType;
    public NV_ENC_PIC_STRUCT pictureStruct;
    public uint frameAvgQP;
    public uint frameSatd;
    public uint ltrFrameIdx;
    public uint ltrFrameBitmap;
    public uint temporalId;
    public uint intraMBCount;
    public uint interMBCount;
    public int averageMVX;
    public int averageMVY;
    public uint alphaLayerSizeInBytes;
    public uint outputStatsPtrSize;
    public uint reserved;
    public IntPtr outputStatsPtr;
    public uint frameIdxDisplay;
    private fixed uint reserved1[219];
    private fixed byte reserved2[63 * 8];
    private fixed uint reservedInternal[8];
}

/// <summary>
/// NVENC return-status codes (mirror <c>NVENCSTATUS</c> enum at
/// nvEncodeAPI.h:501). Numeric values are sequential from 0; we list
/// them by name so call sites read like the C source.
/// </summary>
public enum NVENCSTATUS
{
    NV_ENC_SUCCESS = 0,
    NV_ENC_ERR_NO_ENCODE_DEVICE,
    NV_ENC_ERR_UNSUPPORTED_DEVICE,
    NV_ENC_ERR_INVALID_ENCODERDEVICE,
    NV_ENC_ERR_INVALID_DEVICE,
    NV_ENC_ERR_DEVICE_NOT_EXIST,
    NV_ENC_ERR_INVALID_PTR,
    NV_ENC_ERR_INVALID_EVENT,
    NV_ENC_ERR_INVALID_PARAM,
    NV_ENC_ERR_INVALID_CALL,
    NV_ENC_ERR_OUT_OF_MEMORY,
    NV_ENC_ERR_ENCODER_NOT_INITIALIZED,
    NV_ENC_ERR_UNSUPPORTED_PARAM,
    NV_ENC_ERR_LOCK_BUSY,
    NV_ENC_ERR_NOT_ENOUGH_BUFFER,
    NV_ENC_ERR_INVALID_VERSION,
    NV_ENC_ERR_MAP_FAILED,
    NV_ENC_ERR_NEED_MORE_INPUT,
    NV_ENC_ERR_ENCODER_BUSY,
    NV_ENC_ERR_EVENT_NOT_REGISTERD,
    NV_ENC_ERR_GENERIC,
    NV_ENC_ERR_INCOMPATIBLE_CLIENT_KEY,
    NV_ENC_ERR_UNIMPLEMENTED,
    NV_ENC_ERR_RESOURCE_REGISTER_FAILED,
    NV_ENC_ERR_RESOURCE_NOT_REGISTERED,
    NV_ENC_ERR_RESOURCE_NOT_MAPPED,
    NV_ENC_ERR_NEED_MORE_OUTPUT,
}

/// <summary>
/// <c>NV_ENC_DEVICE_TYPE</c> — see nvEncodeAPI.h:799. <see cref="DirectX"/>
/// covers any DXGI-based device (D3D9, D3D10, D3D11, D3D12); the encoder
/// disambiguates from the device pointer's vtable.
/// </summary>
public enum NV_ENC_DEVICE_TYPE : uint
{
    DirectX = 0,
    Cuda = 1,
    OpenGL = 2,
}

/// <summary>
/// <c>NV_ENC_CAPS</c> — see nvEncodeAPI.h:990. Subset we actually query.
/// Numeric values are positional in the enum; do not reorder.
/// </summary>
public enum NV_ENC_CAPS : uint
{
    NumMaxBFrames = 0,
    SupportedRateControlModes,
    SupportFieldEncoding,
    SupportMonochrome,
    SupportFmo,
    SupportQpelmv,
    SupportBdirectMode,
    SupportCabac,
    SupportAdaptiveTransform,
    SupportStereoMvc,
    NumMaxTemporalLayers,
    SupportHierarchicalPFrames,
    SupportHierarchicalBFrames,
    LevelMax,
    LevelMin,
    SeparateColourPlane,
    WidthMax,
    HeightMax,
    SupportTemporalSvc,
    SupportDynResolutionChange,
    SupportDynBitrateChange,
    SupportDynForceConstQp,
    SupportDynRcmodeChange,
    SupportSubframeReadback,
    SupportConstrainedEncoding,
    SupportIntraRefresh,
    SupportCustomVbvBufSize,
    SupportDynamicSliceMode,
    SupportRefPicInvalidation,
    PreprocSupport,
    AsyncEncodeSupport,
    MbNumMax,
    MbPerSecMax,
    SupportYuv444Encode,
    SupportLosslessEncode,
    SupportSao,
    SupportMeonlyMode,
    SupportLookahead,
    SupportTemporalAq,
    Support10BitEncode,
    NumMaxLtrFrames,
    SupportWeightedPrediction,
    DynamicQueryEncoderCapacity,
    SupportBframeRefMode,
    SupportEmphasisLevelMap,
    WidthMin,
    HeightMin,
    SupportMultipleRefFrames,
    SupportAlphaLayerEncoding,
    NumEncoderEngines,
    SingleSliceIntraRefresh,
    DisableEncStateAdvance,
    OutputReconSurface,
    OutputBlockStats,
    OutputRowStats,
    SupportTemporalFilter,
    SupportLookaheadLevel,
    SupportUnidirectionalB,
    SupportMvhevcEncode,
    SupportYuv422Encode,
}

/// <summary>
/// <c>NV_ENC_CAPS_PARAM</c> at nvEncodeAPI.h:1360. Single-field query
/// struct passed to <c>nvEncGetEncodeCaps</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct NV_ENC_CAPS_PARAM
{
    public uint version;
    public NV_ENC_CAPS capsToQuery;
    // SDK reserves 62 uint32 slots after the cap field for future expansion;
    // we mirror them so the struct size on the wire matches what the driver
    // expects. Failing to do so produces NV_ENC_ERR_INVALID_VERSION.
    private unsafe fixed uint reserved[62];
}

/// <summary>
/// <c>NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS</c> at nvEncodeAPI.h:2943.
/// Used to open an encoding session bound to a specific device.
///
/// The 253-element <c>reserved1</c> and 64-pointer <c>reserved2</c>
/// trailing arrays are part of the SDK contract; the driver checks
/// the struct's total size against <c>version</c> and rejects mismatches.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS
{
    public uint version;
    public NV_ENC_DEVICE_TYPE deviceType;
    public IntPtr device;
    public IntPtr reserved;
    public uint apiVersion;
    public fixed uint reserved1[253];
    // 64 void* slots = 64 IntPtrs; using a fixed-size IntPtr array isn't
    // legal in unsafe C# fixed buffers, so we span the bytes manually.
    // 64 pointers × 8 bytes = 512 bytes.
    private fixed byte reserved2[512];
}

/// <summary>
/// <c>NV_ENCODE_API_FUNCTION_LIST</c> at nvEncodeAPI.h:4591. The driver
/// populates this with function pointers when
/// <c>NvEncodeAPICreateInstance</c> is called. We mirror the full struct
/// even for entrypoints we don't use because the layout has to match
/// byte-for-byte — IntPtr slots are fine, the driver writes pointer
/// values into them and we only read the slots we want.
///
/// The 275 trailing reserved slots are mandatory; they pad the struct
/// to the version the driver expects. Without them the size check fails.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NV_ENCODE_API_FUNCTION_LIST
{
    public uint version;
    public uint reserved;
    public IntPtr nvEncOpenEncodeSession;
    public IntPtr nvEncGetEncodeGUIDCount;
    public IntPtr nvEncGetEncodeProfileGUIDCount;
    public IntPtr nvEncGetEncodeProfileGUIDs;
    public IntPtr nvEncGetEncodeGUIDs;
    public IntPtr nvEncGetInputFormatCount;
    public IntPtr nvEncGetInputFormats;
    public IntPtr nvEncGetEncodeCaps;
    public IntPtr nvEncGetEncodePresetCount;
    public IntPtr nvEncGetEncodePresetGUIDs;
    public IntPtr nvEncGetEncodePresetConfig;
    public IntPtr nvEncInitializeEncoder;
    public IntPtr nvEncCreateInputBuffer;
    public IntPtr nvEncDestroyInputBuffer;
    public IntPtr nvEncCreateBitstreamBuffer;
    public IntPtr nvEncDestroyBitstreamBuffer;
    public IntPtr nvEncEncodePicture;
    public IntPtr nvEncLockBitstream;
    public IntPtr nvEncUnlockBitstream;
    public IntPtr nvEncLockInputBuffer;
    public IntPtr nvEncUnlockInputBuffer;
    public IntPtr nvEncGetEncodeStats;
    public IntPtr nvEncGetSequenceParams;
    public IntPtr nvEncRegisterAsyncEvent;
    public IntPtr nvEncUnregisterAsyncEvent;
    public IntPtr nvEncMapInputResource;
    public IntPtr nvEncUnmapInputResource;
    public IntPtr nvEncDestroyEncoder;
    public IntPtr nvEncInvalidateRefFrames;
    public IntPtr nvEncOpenEncodeSessionEx;
    public IntPtr nvEncRegisterResource;
    public IntPtr nvEncUnregisterResource;
    public IntPtr nvEncReconfigureEncoder;
    public IntPtr reserved1;
    public IntPtr nvEncCreateMVBuffer;
    public IntPtr nvEncDestroyMVBuffer;
    public IntPtr nvEncRunMotionEstimationOnly;
    public IntPtr nvEncGetLastErrorString;
    public IntPtr nvEncSetIOCudaStreams;
    public IntPtr nvEncGetEncodePresetConfigEx;
    public IntPtr nvEncGetSequenceParamEx;
    public IntPtr nvEncRestoreEncoderState;
    public IntPtr nvEncLookaheadPicture;
    // 275 reserved void* slots = 275 × 8 = 2200 bytes.
    private fixed byte reserved2[2200];
}
