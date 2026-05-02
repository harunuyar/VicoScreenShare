namespace VicoScreenShare.Client.Windows.Media.Codecs.Nvdec;

using System;
using VicoScreenShare.Client.Diagnostics;

/// <summary>
/// One-shot probe for NVDEC AV1 support. Mirrors
/// <see cref="Nvenc.NvencCapabilities"/> on the encoder side: load the
/// CUDA + cuvid DLLs, init a CUDA context bound to ordinal-0 device,
/// query <c>cuvidGetDecoderCaps</c> for AV1 / 4:2:0 / 8-bit, then tear
/// down. Result is cached for the lifetime of the process — the probe
/// itself takes ~30 ms at first call which we don't want to pay every
/// time the codec catalog is queried.
/// </summary>
public sealed class NvDecCapabilities
{
    public bool DllsAvailable { get; private set; }
    public bool CudaInitialized { get; private set; }
    public bool IsAv1Available { get; private set; }
    public uint Av1MaxWidth { get; private set; }
    public uint Av1MaxHeight { get; private set; }
    public uint Av1MinWidth { get; private set; }
    public uint Av1MinHeight { get; private set; }
    public byte Av1NumNvdecs { get; private set; }

    private static NvDecCapabilities? _cached;
    private static readonly object _cacheLock = new();

    public static NvDecCapabilities Probe()
    {
        if (_cached is not null)
        {
            return _cached;
        }
        lock (_cacheLock)
        {
            if (_cached is not null)
            {
                return _cached;
            }
            _cached = ProbeInternal();
            return _cached;
        }
    }

    private static NvDecCapabilities ProbeInternal()
    {
        var caps = new NvDecCapabilities();
        try
        {
            // CuInit returns CUDA_ERROR_NO_DEVICE on machines without
            // an NVIDIA GPU; the DLL load itself fails first if the
            // driver isn't present at all. We treat both as "NVDEC not
            // available" and let the catalog fall back to the MFT path.
            var initResult = NvDecApi.CuInit(0);
            caps.DllsAvailable = true;
            if (initResult != NvDecApi.CUresult.CUDA_SUCCESS)
            {
                DebugLog.Write($"[nvdec] cuInit returned {initResult}; NVDEC unavailable on this host");
                return caps;
            }
            caps.CudaInitialized = true;

            var deviceResult = NvDecApi.CuDeviceGet(out var device, 0);
            if (deviceResult != NvDecApi.CUresult.CUDA_SUCCESS)
            {
                DebugLog.Write($"[nvdec] cuDeviceGet(0) returned {deviceResult}; no CUDA-capable device");
                return caps;
            }

            var ctxResult = NvDecApi.CuCtxCreate(out var context, NvDecApi.CU_CTX_SCHED_AUTO, device);
            if (ctxResult != NvDecApi.CUresult.CUDA_SUCCESS)
            {
                DebugLog.Write($"[nvdec] cuCtxCreate returned {ctxResult}");
                return caps;
            }

            try
            {
                // CUVIDDECODECAPS expected size from nvcuvid.h is 80 bytes;
                // anything else means our LayoutKind.Sequential ordering
                // doesn't match the C struct (most likely culprit when
                // bIsSupported=0 on AV1-capable hardware).
                var structSize = System.Runtime.InteropServices.Marshal.SizeOf<NvDecApi.CUVIDDECODECAPS>();
                var probe = new NvDecApi.CUVIDDECODECAPS
                {
                    eCodecType = NvDecApi.cudaVideoCodec.AV1,
                    eChromaFormat = NvDecApi.cudaVideoChromaFormat._420,
                    nBitDepthMinus8 = 0, // 8-bit; matches NVENC AV1 Main profile
                };
                var probeResult = NvDecApi.CuvidGetDecoderCaps(ref probe);
                DebugLog.Write($"[nvdec] cuvidGetDecoderCaps returned {probeResult} (structSize={structSize})");
                if (probeResult != NvDecApi.CUresult.CUDA_SUCCESS)
                {
                    return caps;
                }
                caps.IsAv1Available = probe.bIsSupported != 0;
                caps.Av1MaxWidth = probe.nMaxWidth;
                caps.Av1MaxHeight = probe.nMaxHeight;
                caps.Av1MinWidth = probe.nMinWidth;
                caps.Av1MinHeight = probe.nMinHeight;
                caps.Av1NumNvdecs = probe.nNumNVDECs;
                DebugLog.Write($"[nvdec] AV1 caps: supported={caps.IsAv1Available} max={probe.nMaxWidth}x{probe.nMaxHeight} min={probe.nMinWidth}x{probe.nMinHeight} engines={probe.nNumNVDECs} mask=0x{probe.nOutputFormatMask:X4} histSup={probe.bIsHistogramSupported} structSize={structSize}");

                // If the API reported success but flagged AV1 as
                // unsupported, run a diagnostic sweep across other
                // codecs / chromas. If H.264/4:2:0 also reports
                // unsupported, the binding is wrong; if H.264 works
                // and AV1 doesn't, the GPU genuinely lacks AV1 decode.
                if (!caps.IsAv1Available)
                {
                    foreach (var (codec, label) in new[]
                    {
                        (NvDecApi.cudaVideoCodec.H264, "H264"),
                        (NvDecApi.cudaVideoCodec.HEVC, "HEVC"),
                        (NvDecApi.cudaVideoCodec.VP9, "VP9"),
                    })
                    {
                        var sanity = new NvDecApi.CUVIDDECODECAPS
                        {
                            eCodecType = codec,
                            eChromaFormat = NvDecApi.cudaVideoChromaFormat._420,
                            nBitDepthMinus8 = 0,
                        };
                        var sanityResult = NvDecApi.CuvidGetDecoderCaps(ref sanity);
                        DebugLog.Write($"[nvdec-sanity] {label} → result={sanityResult} supported={sanity.bIsSupported} max={sanity.nMaxWidth}x{sanity.nMaxHeight}");
                    }
                }
            }
            finally
            {
                try { NvDecApi.CuCtxDestroy(context); } catch { }
            }
        }
        catch (DllNotFoundException ex)
        {
            DebugLog.Write($"[nvdec] driver DLL missing: {ex.Message}");
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[nvdec] capability probe threw {ex.GetType().Name}: {ex.Message}");
        }
        return caps;
    }
}
