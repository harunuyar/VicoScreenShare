namespace VicoScreenShare.Client.Windows.Media.Codecs.Nvenc;

using System;
using System.IO;
using System.Runtime.InteropServices;
using VicoScreenShare.Client.Diagnostics;
using Vortice.Direct3D11;
using Vortice.DXGI;

/// <summary>
/// One-shot probe answering: "Can we open an NVENC SDK session for H.264
/// on the user's machine, and which optional features (AQ, lookahead, etc.)
/// will the hardware honor?" Caches the result; safe to call repeatedly.
///
/// The probe walks four gates in order, short-circuiting on the first
/// failure (each gate is cheaper than the next):
/// <list type="number">
///   <item><description><c>nvEncodeAPI64.dll</c> present in System32.</description></item>
///   <item><description>Driver advertises ≥ NVENC SDK 13 via
///     <c>NvEncodeAPIGetMaxSupportedVersion</c>.</description></item>
///   <item><description>The shared D3D11 device's adapter is NVIDIA
///     (<c>VendorId == 0x10DE</c>). Skips the laptop iGPU case where
///     the DLL is present but the active render adapter isn't NVIDIA.</description></item>
///   <item><description>Open a transient session, query the encode-GUID
///     list for H.264, query each capability bit we care about,
///     destroy the session.</description></item>
/// </list>
///
/// Failure at any gate logs once and falls through. The factory selector
/// reads <see cref="IsAvailable"/> and uses NVENC if true, MFT if false.
/// </summary>
public sealed class NvencCapabilities
{
    private static readonly object _lock = new();
    private static NvencCapabilities? _cached;

    private NvencCapabilities(
        bool isAvailable,
        string? unavailableReason,
        bool supportsTemporalAq,
        bool supportsLookahead,
        bool supportsIntraRefresh,
        bool supportsCustomVbv,
        bool supportsAsyncEncode,
        int maxWidth,
        int maxHeight,
        bool isAv1Available,
        bool av1SupportsTemporalAq,
        bool av1SupportsLookahead,
        bool av1SupportsIntraRefresh,
        bool av1SupportsCustomVbv,
        int av1MaxWidth,
        int av1MaxHeight)
    {
        IsAvailable = isAvailable;
        UnavailableReason = unavailableReason;
        SupportsTemporalAq = supportsTemporalAq;
        SupportsLookahead = supportsLookahead;
        SupportsIntraRefresh = supportsIntraRefresh;
        SupportsCustomVbvBufferSize = supportsCustomVbv;
        SupportsAsyncEncode = supportsAsyncEncode;
        MaxWidth = maxWidth;
        MaxHeight = maxHeight;
        IsAv1Available = isAv1Available;
        Av1SupportsTemporalAq = av1SupportsTemporalAq;
        Av1SupportsLookahead = av1SupportsLookahead;
        Av1SupportsIntraRefresh = av1SupportsIntraRefresh;
        Av1SupportsCustomVbvBufferSize = av1SupportsCustomVbv;
        Av1MaxWidth = av1MaxWidth;
        Av1MaxHeight = av1MaxHeight;
    }

    /// <summary>True when an NVENC SDK encoder can be constructed for this device.
    /// Equivalent to "H.264 encode is available" — the H.264 path is the
    /// minimum any NVENC-capable card supports. AV1 is queried separately
    /// via <see cref="IsAv1Available"/>.</summary>
    public bool IsAvailable { get; }

    /// <summary>
    /// Human-readable reason when <see cref="IsAvailable"/> is false. Logged once;
    /// surfaced in tooltips on greyed-out NVENC-only Settings UI.
    /// </summary>
    public string? UnavailableReason { get; }

    // ----- H.264 caps (back-compat: these are the original properties) -----
    public bool SupportsTemporalAq { get; }
    public bool SupportsLookahead { get; }
    public bool SupportsIntraRefresh { get; }
    public bool SupportsCustomVbvBufferSize { get; }
    public bool SupportsAsyncEncode { get; }
    public int MaxWidth { get; }
    public int MaxHeight { get; }

    // ----- AV1 caps (Phase 0 additions) -----
    /// <summary>True when the GPU advertises hardware AV1 encode (RTX 40+,
    /// Intel Arc, AMD RDNA 3+). False on Ada-and-older NVIDIA, AMD RDNA 2-,
    /// Intel pre-Arc.</summary>
    public bool IsAv1Available { get; }
    public bool Av1SupportsTemporalAq { get; }
    public bool Av1SupportsLookahead { get; }
    public bool Av1SupportsIntraRefresh { get; }
    public bool Av1SupportsCustomVbvBufferSize { get; }
    public int Av1MaxWidth { get; }
    public int Av1MaxHeight { get; }

    /// <summary>
    /// Probe once per process for a given D3D11 device. Subsequent calls
    /// return the cached result regardless of <paramref name="d3dDevice"/>;
    /// this reflects the assumption that the app uses one shared device.
    /// </summary>
    public static NvencCapabilities Probe(ID3D11Device? d3dDevice)
    {
        lock (_lock)
        {
            if (_cached is not null)
            {
                return _cached;
            }

            _cached = ProbeUncached(d3dDevice);
            return _cached;
        }
    }

    private static NvencCapabilities ProbeUncached(ID3D11Device? d3dDevice)
    {
        // Gate 1 — DLL present in System32. We don't try the working
        // directory or PATH; NVIDIA installs nvEncodeAPI64.dll there
        // and only there.
        var dllPath = Path.Combine(Environment.SystemDirectory, NvencApi.DllName);
        if (!File.Exists(dllPath))
        {
            var reason = $"{NvencApi.DllName} not present (no NVIDIA driver?)";
            DebugLog.Write($"[nvenc] probe: {reason}");
            return Unavailable(reason);
        }

        // Gate 2 — driver supports our compiled-against API version.
        // GetMaxSupportedVersion returns (major | (minor << 24)); we built
        // against 13.0 so anything < 13.0 is too old.
        uint maxVersion;
        try
        {
            var status = NvencApi.NvEncodeAPIGetMaxSupportedVersion(out maxVersion);
            if (status != NVENCSTATUS.NV_ENC_SUCCESS)
            {
                var reason = $"NvEncodeAPIGetMaxSupportedVersion failed: {status}";
                DebugLog.Write($"[nvenc] probe: {reason}");
                return Unavailable(reason);
            }
        }
        catch (DllNotFoundException ex)
        {
            // Race between the File.Exists check and the LoadLibrary the
            // P/Invoke triggers — possible if the user uninstalls drivers
            // between the two. Treat as unavailable.
            var reason = $"DllImport failed: {ex.Message}";
            DebugLog.Write($"[nvenc] probe: {reason}");
            return Unavailable(reason);
        }
        if (maxVersion < NvencApi.NVENCAPI_VERSION)
        {
            var reason = $"driver supports NVENC {Decode(maxVersion)} but we need ≥ {Decode(NvencApi.NVENCAPI_VERSION)}";
            DebugLog.Write($"[nvenc] probe: {reason}");
            return Unavailable(reason);
        }

        // Gate 3 — render adapter is NVIDIA. NVIDIA Optimus laptops can
        // have the DLL present but be running the active D3D11 device
        // on Intel iGPU; opening an NVENC session against the iGPU
        // device returns NV_ENC_ERR_UNSUPPORTED_DEVICE — caught at Gate 4
        // but the vendor check makes the failure mode legible.
        if (d3dDevice is null)
        {
            var reason = "no D3D11 device supplied to probe";
            DebugLog.Write($"[nvenc] probe: {reason}");
            return Unavailable(reason);
        }
        var vendorId = TryGetAdapterVendorId(d3dDevice);
        if (vendorId is null)
        {
            var reason = "could not query adapter vendor id";
            DebugLog.Write($"[nvenc] probe: {reason}");
            return Unavailable(reason);
        }
        if (vendorId.Value != 0x10DE)
        {
            var reason = $"render adapter is vendor 0x{vendorId.Value:X4}, not NVIDIA";
            DebugLog.Write($"[nvenc] probe: {reason}");
            return Unavailable(reason);
        }

        // Gate 4 — open a session, query caps, destroy.
        return ProbeSession(d3dDevice);
    }

    private static unsafe NvencCapabilities ProbeSession(ID3D11Device d3dDevice)
    {
        NV_ENCODE_API_FUNCTION_LIST table = default;
        table.version = NvencApi.NV_ENCODE_API_FUNCTION_LIST_VER;

        var apiStatus = NvencApi.NvEncodeAPICreateInstance(ref table);
        if (apiStatus != NVENCSTATUS.NV_ENC_SUCCESS)
        {
            var reason = $"NvEncodeAPICreateInstance failed: {apiStatus}";
            DebugLog.Write($"[nvenc] probe: {reason}");
            return Unavailable(reason);
        }

        // Sanity: every entrypoint we plan to call must be non-null.
        // Failing this means we're talking to a driver from before this
        // entrypoint existed (NVENC 9-era), and we should fall back.
        if (table.nvEncOpenEncodeSessionEx == IntPtr.Zero
            || table.nvEncDestroyEncoder == IntPtr.Zero
            || table.nvEncGetEncodeGUIDs == IntPtr.Zero
            || table.nvEncGetEncodeCaps == IntPtr.Zero)
        {
            var reason = "function table missing required entrypoints";
            DebugLog.Write($"[nvenc] probe: {reason}");
            return Unavailable(reason);
        }

        var openSession = Marshal.GetDelegateForFunctionPointer<NvencApi.NvEncOpenEncodeSessionExFn>(
            table.nvEncOpenEncodeSessionEx);
        var destroy = Marshal.GetDelegateForFunctionPointer<NvencApi.NvEncDestroyEncoderFn>(
            table.nvEncDestroyEncoder);
        var getGuidCount = Marshal.GetDelegateForFunctionPointer<NvencApi.NvEncGetEncodeGUIDCountFn>(
            table.nvEncGetEncodeGUIDCount);
        var getGuids = Marshal.GetDelegateForFunctionPointer<NvencApi.NvEncGetEncodeGUIDsFn>(
            table.nvEncGetEncodeGUIDs);
        var getCaps = Marshal.GetDelegateForFunctionPointer<NvencApi.NvEncGetEncodeCapsFn>(
            table.nvEncGetEncodeCaps);

        var sessionParams = new NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS
        {
            version = NvencApi.NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER,
            deviceType = NV_ENC_DEVICE_TYPE.DirectX,
            device = d3dDevice.NativePointer,
            apiVersion = NvencApi.NVENCAPI_VERSION,
        };

        IntPtr encoder;
        var openStatus = openSession(ref sessionParams, &encoder);
        if (openStatus != NVENCSTATUS.NV_ENC_SUCCESS || encoder == IntPtr.Zero)
        {
            var reason = $"NvEncOpenEncodeSessionEx failed: {openStatus}";
            DebugLog.Write($"[nvenc] probe: {reason}");
            return Unavailable(reason);
        }

        try
        {
            // Confirm H.264 is in the supported codec list. If a driver
            // somehow advertises NVENC ≥ 13 without H.264 we want a
            // clean fallback rather than mysterious downstream failures.
            // SDK doc says GetEncodeGUIDCount "can be" used to size the
            // GUID array (line 3009). In practice on NVIDIA driver
            // 581.x / SDK 13: calling GetEncodeGUIDs directly with a
            // hardcoded oversized array returns NV_ENC_SUCCESS but writes
            // nothing — count comes back equal to the array size we
            // passed in, GUIDs are zeroed. The count call is required.
            // This is also what OBS' obs-nvenc does; behavior verified
            // by bench-nvenc-probe on the user's hardware.
            uint expectedCount = 0;
            var countStatus = getGuidCount(encoder, &expectedCount);
            if (countStatus != NVENCSTATUS.NV_ENC_SUCCESS || expectedCount == 0)
            {
                var reason = $"GetEncodeGUIDCount failed: {countStatus}, count={expectedCount}";
                DebugLog.Write($"[nvenc] probe: {reason}");
                return Unavailable(reason);
            }

            var supportsH264 = false;
            var supportsAv1 = false;
            var arraySize = (int)Math.Min(expectedCount, 16u);
            var guids = stackalloc Guid[arraySize];
            uint guidCount;
            var listStatus = getGuids(encoder, guids, (uint)arraySize, &guidCount);
            if (listStatus == NVENCSTATUS.NV_ENC_SUCCESS)
            {
                for (int i = 0; i < guidCount && i < arraySize; i++)
                {
                    if (guids[i] == NvencGuids.CodecH264)
                    {
                        supportsH264 = true;
                    }
                    else if (guids[i] == NvencGuids.CodecAv1)
                    {
                        supportsAv1 = true;
                    }
                }
            }
            if (!supportsH264)
            {
                var reason = $"driver does not advertise H.264 codec GUID (listStatus={listStatus}, count={guidCount})";
                DebugLog.Write($"[nvenc] probe: {reason}");
                return Unavailable(reason);
            }

            var h264 = NvencGuids.CodecH264;
            var temporalAq = QueryCap(getCaps, encoder, h264, NV_ENC_CAPS.SupportTemporalAq);
            var lookahead = QueryCap(getCaps, encoder, h264, NV_ENC_CAPS.SupportLookahead);
            var intraRefresh = QueryCap(getCaps, encoder, h264, NV_ENC_CAPS.SupportIntraRefresh);
            var customVbv = QueryCap(getCaps, encoder, h264, NV_ENC_CAPS.SupportCustomVbvBufSize);
            var asyncEncode = QueryCap(getCaps, encoder, h264, NV_ENC_CAPS.AsyncEncodeSupport);
            var widthMax = QueryCapInt(getCaps, encoder, h264, NV_ENC_CAPS.WidthMax);
            var heightMax = QueryCapInt(getCaps, encoder, h264, NV_ENC_CAPS.HeightMax);

            // AV1 caps. Each query is gated on the codec being supported —
            // querying caps against an unsupported codec GUID returns
            // INVALID_PARAM and would noise the log. Default the values to
            // false / 0 for non-AV1 hardware.
            var av1TemporalAq = false;
            var av1Lookahead = false;
            var av1IntraRefresh = false;
            var av1CustomVbv = false;
            var av1WidthMax = 0;
            var av1HeightMax = 0;
            if (supportsAv1)
            {
                var av1 = NvencGuids.CodecAv1;
                av1TemporalAq = QueryCap(getCaps, encoder, av1, NV_ENC_CAPS.SupportTemporalAq);
                av1Lookahead = QueryCap(getCaps, encoder, av1, NV_ENC_CAPS.SupportLookahead);
                av1IntraRefresh = QueryCap(getCaps, encoder, av1, NV_ENC_CAPS.SupportIntraRefresh);
                av1CustomVbv = QueryCap(getCaps, encoder, av1, NV_ENC_CAPS.SupportCustomVbvBufSize);
                av1WidthMax = QueryCapInt(getCaps, encoder, av1, NV_ENC_CAPS.WidthMax);
                av1HeightMax = QueryCapInt(getCaps, encoder, av1, NV_ENC_CAPS.HeightMax);
            }

            DebugLog.Write(
                $"[nvenc] probe: ok h264=1 temporal_aq={(temporalAq ? 1 : 0)} "
                + $"lookahead={(lookahead ? 1 : 0)} intra_refresh={(intraRefresh ? 1 : 0)} "
                + $"custom_vbv={(customVbv ? 1 : 0)} async={(asyncEncode ? 1 : 0)} "
                + $"max={widthMax}x{heightMax}");
            DebugLog.Write(
                $"[nvenc] probe: av1={(supportsAv1 ? 1 : 0)} "
                + $"av1_temporal_aq={(av1TemporalAq ? 1 : 0)} av1_lookahead={(av1Lookahead ? 1 : 0)} "
                + $"av1_intra_refresh={(av1IntraRefresh ? 1 : 0)} av1_custom_vbv={(av1CustomVbv ? 1 : 0)} "
                + $"av1_max={av1WidthMax}x{av1HeightMax}");

            return new NvencCapabilities(
                isAvailable: true,
                unavailableReason: null,
                supportsTemporalAq: temporalAq,
                supportsLookahead: lookahead,
                supportsIntraRefresh: intraRefresh,
                supportsCustomVbv: customVbv,
                supportsAsyncEncode: asyncEncode,
                maxWidth: widthMax,
                maxHeight: heightMax,
                isAv1Available: supportsAv1,
                av1SupportsTemporalAq: av1TemporalAq,
                av1SupportsLookahead: av1Lookahead,
                av1SupportsIntraRefresh: av1IntraRefresh,
                av1SupportsCustomVbv: av1CustomVbv,
                av1MaxWidth: av1WidthMax,
                av1MaxHeight: av1HeightMax);
        }
        finally
        {
            destroy(encoder);
        }
    }

    private static unsafe bool QueryCap(NvencApi.NvEncGetEncodeCapsFn fn, IntPtr encoder, Guid codecGuid, NV_ENC_CAPS cap)
    {
        var capsParam = new NV_ENC_CAPS_PARAM
        {
            version = NvencApi.NV_ENC_CAPS_PARAM_VER,
            capsToQuery = cap,
        };
        int value;
        var status = fn(encoder, codecGuid, ref capsParam, &value);
        if (status != NVENCSTATUS.NV_ENC_SUCCESS)
        {
            DebugLog.Write($"[nvenc] cap {cap} query failed: {status}");
            return false;
        }
        return value != 0;
    }

    private static unsafe int QueryCapInt(NvencApi.NvEncGetEncodeCapsFn fn, IntPtr encoder, Guid codecGuid, NV_ENC_CAPS cap)
    {
        var capsParam = new NV_ENC_CAPS_PARAM
        {
            version = NvencApi.NV_ENC_CAPS_PARAM_VER,
            capsToQuery = cap,
        };
        int value;
        var status = fn(encoder, codecGuid, ref capsParam, &value);
        if (status != NVENCSTATUS.NV_ENC_SUCCESS)
        {
            return 0;
        }
        return value;
    }

    private static int? TryGetAdapterVendorId(ID3D11Device device)
    {
        try
        {
            using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            return (int)adapter.Description.VendorId;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[nvenc] adapter query threw: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static NvencCapabilities Unavailable(string reason) => new(
        isAvailable: false,
        unavailableReason: reason,
        supportsTemporalAq: false,
        supportsLookahead: false,
        supportsIntraRefresh: false,
        supportsCustomVbv: false,
        supportsAsyncEncode: false,
        maxWidth: 0,
        maxHeight: 0,
        isAv1Available: false,
        av1SupportsTemporalAq: false,
        av1SupportsLookahead: false,
        av1SupportsIntraRefresh: false,
        av1SupportsCustomVbv: false,
        av1MaxWidth: 0,
        av1MaxHeight: 0);

    private static string Decode(uint v) => $"{v & 0xFFFFFFu}.{(v >> 24) & 0xFFu}";

    /// <summary>
    /// Reset the cached probe result. For benchmarks and harness scenarios
    /// that want a fresh probe; production code should never need this.
    /// </summary>
    public static void ResetForTesting()
    {
        lock (_lock)
        {
            _cached = null;
        }
    }
}
