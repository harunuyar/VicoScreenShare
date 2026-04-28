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

    public static readonly uint NV_ENCODE_API_FUNCTION_LIST_VER = StructVersion(2);
    public static readonly uint NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER = StructVersion(1);
    public static readonly uint NV_ENC_CAPS_PARAM_VER = StructVersion(1);

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
