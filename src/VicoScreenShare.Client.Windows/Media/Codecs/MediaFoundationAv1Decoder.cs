namespace VicoScreenShare.Client.Windows.Media.Codecs;

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using VicoScreenShare.Client.Diagnostics;
using VicoScreenShare.Client.Media.Codecs;
using VicoScreenShare.Client.Windows.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

/// <summary>
/// AV1 decoder on top of Media Foundation's AV1 decoder MFT (typically the
/// Microsoft "AV1 Video Extension" package, which registers a hardware-
/// preferred MFT plus a software fallback). Mirrors
/// <see cref="MediaFoundationH264Decoder"/>; the only behavioral
/// differences are the input subtype GUID (<c>MFVideoFormat_AV1</c>) and
/// that AV1 streams have no SPS/PPS — the decoder learns dimensions from
/// the Sequence OBU which the bitstream carries inline.
///
/// Two modes:
///  - GPU texture mode (preferred, when an external D3D11 device is passed):
///    the decoder MFT is given a DXGI device manager for our shared device,
///    outputs land as NV12 D3D11 textures, and a <see cref="D3D11VideoScaler"/>
///    converts NV12 → BGRA on the GPU in one VideoProcessorBlt.
///  - System-memory fallback: legacy path, used when no shared device is
///    available. Pulls NV12 from the MFT into system memory and runs a CPU
///    scalar NV12 → BGRA loop.
/// </summary>
public sealed unsafe class MediaFoundationAv1Decoder : IVideoDecoder
{
    /// <summary>
    /// MFVideoFormat_AV1 — FOURCC 'AV01' as the leading DWORD of the
    /// standard Microsoft media GUID tail. Vortice doesn't yet expose this
    /// constant, so we define it locally. Verified via MFTEnumEx probe.
    /// </summary>
    private static readonly Guid MFVideoFormatAv1 = new(
        0x31305641, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);

    /// <summary>
    /// Probe Media Foundation for an AV1 decoder. Used by the codec catalog
    /// to gate AV1 availability — on systems without the AV1 Video
    /// Extension installed, MFTEnumEx returns nothing and we fall back to
    /// H.264. Cheap (just enumerates registry-based MFTs); safe to call
    /// from the factory's <c>IsAvailable</c> getter.
    /// </summary>
    public static bool HasAv1DecoderInstalled()
    {
        var inputFilter = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = MFVideoFormatAv1,
        };
        try
        {
            var hwFlags = (uint)(EnumFlag.EnumFlagHardware
                                 | EnumFlag.EnumFlagSyncmft
                                 | EnumFlag.EnumFlagAsyncmft
                                 | EnumFlag.EnumFlagSortandfilter);
            using var hw = MediaFactory.MFTEnumEx(
                TransformCategoryGuids.VideoDecoder, hwFlags, inputType: inputFilter, outputType: null);
            foreach (var _ in hw)
            {
                return true;
            }

            var swFlags = (uint)(EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagSortandfilter);
            using var sw = MediaFactory.MFTEnumEx(
                TransformCategoryGuids.VideoDecoder, swFlags, inputType: inputFilter, outputType: null);
            foreach (var _ in sw)
            {
                return true;
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[mf] AV1 decoder probe threw: {ex.Message}");
        }
        return false;
    }

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
    // True when this decoder instance created + owns its D3D11 device
    // (as opposed to borrowing one from the caller). Owned devices are
    // used for per-stream parallel decode — each StreamReceiver's
    // decoder gets its own device so ID3D11Multithread doesn't
    // serialize submissions across three simultaneous decoders. The
    // GPU-texture handoff path is disabled for owned devices because
    // cross-device ID3D11Texture2D is not valid; callers fall back to
    // the CPU-bytes path automatically.
    private readonly bool _ownsDevice;
    private D3D11VideoScaler? _scaler;
    private ID3D11Texture2D? _bgraStaging;
    private int _bgraStagingWidth;
    private int _bgraStagingHeight;
    private int _width;
    private int _height;
    private bool _outputTypeNegotiated;

    private long _loggedDecodedFrames;
    private bool _disposed;

    // ProcessOutput failure-cascade tracking. The MFT enters a stuck
    // state after rejecting one bad sample; every subsequent call
    // returns the same failure code. After this many consecutive
    // failures, flush the MFT and restart streaming. Sized to be
    // larger than typical transient negotiation hiccups (~5 frames)
    // but small enough to recover within a few hundred ms at 144 fps.
    private int _consecutiveProcessOutputFailures;
    private const int ConsecutiveFailureFlushThreshold = 30;

    // Some failure HRESULTs are unrecoverable without a flush — there
    // is no benefit to waiting for the cascade threshold because the
    // MFT will never produce output for the next-N samples regardless.
    // We force-flush on first occurrence for these:
    //   - E_OUTOFMEMORY (0x8007000E): MS AV1 MFT internal pool ran out;
    //     observed mid-stream after ~30 s of healthy decoding. Without
    //     immediate flush the decoder enters a NEED_MORE_INPUT loop
    //     and the 75-empty watchdog only catches it ~520 ms later.
    //   - MF_E_INVALID_STREAM_DATA (0xC00D36CB): bitstream rejected;
    //     subsequent samples are useless until the next IDR arrives.
    private const uint HrOutOfMemory = 0x8007000E;
    private const uint HrInvalidStreamData = 0xC00D36CB;

    // Wedged-decoder watchdog. After the MFT rejects a sample with
    // 0xC00D36CB (MF_E_INVALID_STREAM_DATA) it can enter a "starve"
    // state where ProcessOutput stops returning failure and instead
    // returns MF_E_TRANSFORM_NEED_MORE_INPUT for every subsequent
    // call — Decode() returns an empty list, but our cascade counter
    // never increments. Without this watchdog the decoder stays
    // wedged for the rest of the stream. Threshold is set to ~0.5 s
    // worth of frames at 144 fps so legitimate B-frame reorder
    // buffering doesn't trigger it (real reorder depth is 0-3 here).
    private int _consecutiveEmptyOutputs;
    // 30 ≈ 210 ms at 144 fps. Tried 10 — too aggressive: after each
    // flush the decoder needs ~50-100 ms to ingest the next IDR and
    // start producing output, but a 70 ms threshold re-fires the
    // watchdog before the IDR is even received, dropping it during
    // the next flush and starting the cycle over (see commit log
    // 22:38:21-22:38:22 — 14 flushes in 800 ms, ~1 s user freeze).
    // 30 + the cooldown below gives the decoder enough headroom to
    // recover after a single flush.
    private const int ConsecutiveEmptyOutputsFlushThreshold = 30;

    // Threshold for raising KeyframeNeeded (NOT for local flush — that's
    // the Flush threshold above). Lower because PLI is cheap (debounced
    // at 500 ms upstream) and asking earlier shortens recovery time.
    // 6 calls at 60 fps ≈ 100 ms; at lower frame rates the watchdog at
    // the receiver level catches the same condition with a larger window.
    private const int KeyframeRequestZeroOutputThreshold = 6;

    // Cooldown after any flush: suppress the empty-streak watchdog
    // for this many ticks so the decoder has time to ingest the next
    // IDR and bootstrap a fresh DPB without us stripping it out from
    // under the MFT. 300 ms covers the typical IDR transit (~50 ms)
    // plus bootstrap (~100-200 ms) at the publisher's 1 s GOP.
    private long _flushCooldownUntilTicks;
    private static readonly long FlushCooldownTicks =
        (long)(System.Diagnostics.Stopwatch.Frequency * 0.300);

    // Set inside DrainOutput when a frame is produced (CPU return
    // value or GPU sink callback). Decode() reads this to drive the
    // wedged-decoder watchdog without false-positives in GPU mode.
    private bool _drainProducedAFrame;

    // Tracks whether we have fed the decoder an IDR/keyframe since
    // the last successful output. The empty-streak watchdog used to
    // fire purely on count; that produced false positives every time
    // a Gen2 GC pause stalled the decoder thread (decoder was just
    // slow, not wedged). Now we ONLY flush when (a) the watchdog
    // threshold is hit AND (b) we've already given the decoder an
    // IDR and even that produced no output — the only state from
    // which the decoder cannot recover by itself. Cleared on every
    // successful output. Set on Decode() when the input sample
    // carries a Sequence Header OBU (AV1 keyframe marker).
    private bool _idrFedSinceLastOutput;

    // Counts produced frames for the refcount drift probe. Sampled
    // every 240th frame (~1.7 s at 144 fps) so we get a multi-minute
    // trend without flooding the log.
    private long _bgraRefcountSampleCount;

    // Initial resolution hint passed in via the dimensioned ctor; 0
    // falls back to the historical 1920×1088 default. Used in the
    // initial NegotiateOutputType call to skip the STREAM_CHANGE
    // round-trip on the first IDR.
    private readonly int _initHintWidth;
    private readonly int _initHintHeight;

    /// <summary>
    /// When non-null, the decoder invokes this delegate synchronously inside
    /// <see cref="Decode"/> for each produced frame, handing over the
    /// <c>ID3D11Texture2D</c> pointer for the BGRA result on the decoder's
    /// shared device. In that mode the per-frame GPU→CPU readback is
    /// skipped entirely and <see cref="Decode"/> returns an empty list for
    /// those frames — the caller is expected to consume (GPU-copy) the
    /// texture during the callback. Falls back to the legacy CPU readback
    /// path when null.
    /// </summary>
    public Action<IntPtr, int, int, TimeSpan>? GpuOutputHandler { get; set; }

    /// <summary>
    /// Raised when the MFT enters a state we can't recover from in-place.
    /// See <see cref="MediaFoundationH264Decoder.KeyframeNeeded"/> for the
    /// full contract — this AV1 implementation follows the same pattern.
    /// </summary>
    public event Action? KeyframeNeeded;

    private void RaiseKeyframeNeeded(string reason)
    {
        DebugLog.Write($"[mf-av1] keyframe-needed: {reason}");
        try { KeyframeNeeded?.Invoke(); } catch { /* listener exceptions must not crash decoder */ }
    }

    public MediaFoundationAv1Decoder() : this(externalDevice: null, hintWidth: 0, hintHeight: 0)
    {
    }

    public MediaFoundationAv1Decoder(ID3D11Device? externalDevice)
        : this(externalDevice, hintWidth: 0, hintHeight: 0)
    {
    }

    /// <summary>
    /// Construct with a known input resolution hint. The MS AV1 MFT
    /// otherwise initializes to a default (1920×1088) and raises
    /// MF_E_TRANSFORM_STREAM_CHANGE on the first sample whose Sequence
    /// Header dimensions differ — that round-trip drops the IDR and
    /// costs ~1 s waiting for the next one. Passing the publisher's
    /// real resolution (sourced from the StreamStarted protocol message)
    /// lets us pre-negotiate at the correct size so STREAM_CHANGE never
    /// fires at session start. Hints of 0 fall back to the legacy
    /// 1920×1088 default for backwards compatibility.
    /// </summary>
    public MediaFoundationAv1Decoder(ID3D11Device? externalDevice, int hintWidth, int hintHeight)
    {
        _initHintWidth = hintWidth;
        _initHintHeight = hintHeight;
        // Device binding strategy:
        //
        //   externalDevice != null
        //     Borrow the caller's device. Decoder can emit D3D11 textures
        //     directly to the GpuOutputHandler — the consumer (renderer)
        //     runs on the same device so ID3D11Texture2D pointers can
        //     cross the callback boundary.
        //
        //   externalDevice == null
        //     Create our own private D3D11 device. Lets three or more
        //     StreamReceivers run their decoders in parallel without
        //     ID3D11Multithread lock contention on a shared device.
        //     GpuOutputHandler is NOT invoked — its texture would be on
        //     the private device and unusable by the caller — so we
        //     produce BGRA bytes via the existing CPU path.
        if (externalDevice is not null)
        {
            _d3dDevice = externalDevice;
            _dxgiManager = TryWrapExternalDevice(externalDevice);
        }
        else
        {
            (_d3dDevice, _dxgiManager) = TryCreatePrivateD3DManager();
            _ownsDevice = _d3dDevice is not null;
        }

        _transform = CreateDecoder()
                     ?? throw new InvalidOperationException("No AV1 decoder MFT is available from Media Foundation (install \"AV1 Video Extension\" from the Microsoft Store, or fall back to H.264)");

        // Force the decoder into low-latency mode BEFORE any type
        // negotiation. Without this the MF H.264 decoder waits for a full
        // reorder group (~20 frames) before producing anything, which on
        // a low-latency stream with no B-frames is pure pipeline lag.
        try
        {
            _transform.Attributes.Set(CodecApiAVLowLatencyMode, 1u);
            DebugLog.Write("[mf] AV1 decoder LowLatencyMode=1 applied");
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[mf] AV1 decoder LowLatencyMode rejected: {ex.Message}");
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
                DebugLog.Write("[mf] AV1 decoder accepted SET_D3D_MANAGER (GPU path)");
            }
            catch (Exception ex)
            {
                DebugLog.Write($"[mf] AV1 decoder rejected SET_D3D_MANAGER: {ex.Message} (falling back to system-memory)");
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
        // AV1 has no SPS-equivalent the MFT can probe for at init time.
        // Set both input AND output types with explicit FRAME_SIZE / FRAME_RATE
        // so the MFT can size its internal buffers; the decoder learns real
        // dimensions from the AV1 Sequence OBU and raises STREAM_CHANGE when
        // they differ. Without FRAME_SIZE on either type, AV1 MFTs reject
        // the type-set with MF_E_INVALIDMEDIATYPE — the H.264 trick of
        // letting the MFT derive the output type from a placeholder input
        // doesn't carry over.
        // Use caller-provided dimensions when available so the MFT
        // pre-negotiates at the publisher's actual resolution; otherwise
        // fall back to a generic 1920×1088 placeholder and accept the
        // STREAM_CHANGE round-trip on the first keyframe.
        var initWidth = _initHintWidth > 0 ? _initHintWidth : 1920;
        var initHeight = _initHintHeight > 0 ? _initHintHeight : 1088;
        var initFrameSize = ((ulong)(uint)initWidth << 32) | (uint)initHeight;
        const ulong defaultFrameRate = (60UL << 32) | 1UL;
        DebugLog.Write($"[mf] AV1 decoder init dimensions {initWidth}x{initHeight} (hinted={_initHintWidth > 0})");

        var inputType = MediaFactory.MFCreateMediaType();
        inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        inputType.Set(MediaTypeAttributeKeys.Subtype, MFVideoFormatAv1);
        inputType.SetEnumValue(MediaTypeAttributeKeys.InterlaceMode, VideoInterlaceMode.Progressive);
        inputType.Set(MediaTypeAttributeKeys.FrameSize, initFrameSize);
        inputType.Set(MediaTypeAttributeKeys.FrameRate, defaultFrameRate);
        _transform.SetInputType(0, inputType, 0);
        inputType.Dispose();

        NegotiateOutputType(initFrameSize, defaultFrameRate);
        if (!_outputTypeNegotiated)
        {
            DebugLog.Write("[mf] AV1 decoder could not pick an NV12 output type at init");
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

    /// <summary>
    /// Create a fresh D3D11 device + DXGI manager owned by this decoder
    /// instance. Mirrors the encoder's <c>TryCreateD3DManager</c>. The
    /// device is flagged multithread-protected because MF async MFTs
    /// invoke our event pump on an internal worker thread — without the
    /// flag, concurrent access to the device and its context crashes.
    /// Returns (null, null) on failure; the decoder then falls through
    /// to a software / sysmem path.
    /// </summary>
    private static (ID3D11Device? device, IMFDXGIDeviceManager? manager) TryCreatePrivateD3DManager()
    {
        try
        {
            var hr = D3D11.D3D11CreateDevice(
                adapter: null,
                Vortice.Direct3D.DriverType.Hardware,
                DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                new[]
                {
                    Vortice.Direct3D.FeatureLevel.Level_11_1,
                    Vortice.Direct3D.FeatureLevel.Level_11_0,
                },
                out var device,
                out _,
                out _);
            if (hr.Failure || device is null)
            {
                DebugLog.Write($"[mf] decoder D3D11CreateDevice failed (HR=0x{(uint)hr.Code:X8}); falling back to sysmem decode");
                return (null, null);
            }

            using var mt = device.QueryInterface<ID3D11Multithread>();
            mt.SetMultithreadProtected(true);

            var manager = MediaFactory.MFCreateDXGIDeviceManager();
            manager.ResetDevice(device);
            DebugLog.Write("[mf] decoder created private D3D11 device + DXGI manager");
            return (device, manager);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[mf] decoder TryCreatePrivateD3DManager threw: {ex.Message}; falling back to sysmem decode");
            return (null, null);
        }
    }

    public VideoCodec Codec => VideoCodec.Av1;

    public void Flush()
    {
        if (_disposed)
        {
            return;
        }
        // MF_MESSAGE_COMMAND_FLUSH clears the MFT's internal input queue,
        // any pending output, and any accumulated error state. After flush
        // the decoder expects the next input to be a keyframe — callers
        // must gate inter-frames until one arrives.
        try
        {
            _transform.ProcessMessage(TMessageType.MessageCommandFlush, 0);
            _consecutiveProcessOutputFailures = 0;
            DebugLog.Write("[mf] AV1 decoder flushed");
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[mf] AV1 decoder flush threw: {ex.Message}");
        }
    }

    /// <summary>
    /// Self-heal after a ProcessOutput failure cascade. Flush + restart
    /// streaming returns the MFT to a state equivalent to "just constructed
    /// and accepting input"; the next IDR (or sequence-header OBU emitted
    /// during NVENC's intra-refresh wave) bootstraps decode again. Errors
    /// are caught and logged — the decoder may stay wedged if the flush
    /// itself fails, but at this point we're already broken so a worst
    /// case of "still broken" is the same as before.
    /// </summary>
    private void TryFlushAndRestart()
    {
        if (_disposed)
        {
            return;
        }
        DebugLog.Write($"[mf] AV1 decoder failure cascade ({_consecutiveProcessOutputFailures} consecutive failures); flushing + restarting MFT");
        try { _transform.ProcessMessage(TMessageType.MessageCommandFlush, 0); } catch (Exception ex) { DebugLog.Write($"[mf] AV1 flush threw: {ex.Message}"); }
        try { _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, 0); } catch (Exception ex) { DebugLog.Write($"[mf] AV1 EndStreaming threw: {ex.Message}"); }
        try { _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0); } catch (Exception ex) { DebugLog.Write($"[mf] AV1 BeginStreaming threw: {ex.Message}"); }
        _consecutiveProcessOutputFailures = 0;
        _consecutiveEmptyOutputs = 0;
        _flushCooldownUntilTicks =
            System.Diagnostics.Stopwatch.GetTimestamp() + FlushCooldownTicks;
    }

    private long _decodeCallCount;
    private long _decodeSpikeLogCount;

    /// <summary>
    /// Walks the assembled AV1 OBU stream and returns true if any
    /// OBU has obu_type=1 (OBU_SEQUENCE_HEADER). Used by Decode() to
    /// mark that an IDR has been fed to the decoder. Each OBU has
    /// obu_has_size_field=1 + LEB128 size (the depacketizer rebuilt
    /// the stream that way), so we can walk header→ext→leb128→payload
    /// without a full parse. Bails out at the first SeqHdr found.
    /// </summary>
    private static bool ContainsSequenceHeaderObu(byte[] obuStream, int validLength)
    {
        var pos = 0;
        var safetyHops = 0;
        while (pos < validLength && safetyHops < 64)
        {
            safetyHops++;
            var header = obuStream[pos];
            var obuType = (header >> 3) & 0xF;
            if (obuType == 1)
            {
                return true;
            }
            var hasExtension = (header & 0x04) != 0;
            var hasSize = (header & 0x02) != 0;
            var headerLen = 1 + (hasExtension ? 1 : 0);
            if (pos + headerLen > validLength)
            {
                return false;
            }
            int payloadSize;
            if (hasSize)
            {
                // Inline LEB128 read so we don't depend on the
                // packetizer's helper from a sibling assembly.
                uint val = 0;
                var i = 0;
                while (i < 8 && pos + headerLen + i < validLength)
                {
                    var b = obuStream[pos + headerLen + i];
                    val |= (uint)(b & 0x7F) << (i * 7);
                    i++;
                    if ((b & 0x80) == 0)
                    {
                        break;
                    }
                }
                payloadSize = (int)val;
                pos += headerLen + i + payloadSize;
            }
            else
            {
                // No size field = OBU runs to end of stream. If this
                // isn't a SeqHdr, no SeqHdr exists in this stream.
                return false;
            }
        }
        return false;
    }

    public IReadOnlyList<DecodedVideoFrame> Decode(byte[] encodedSample, TimeSpan inputTimestamp)
        => Decode(encodedSample, encodedSample?.Length ?? 0, inputTimestamp);

    public IReadOnlyList<DecodedVideoFrame> Decode(byte[] encodedSample, int length, TimeSpan inputTimestamp)
    {
        if (_disposed || encodedSample is null || length <= 0)
        {
            return Array.Empty<DecodedVideoFrame>();
        }
        var decodeT0 = System.Diagnostics.Stopwatch.GetTimestamp();

        // Detect IDR by scanning OBU headers for an OBU_SEQUENCE_HEADER
        // (obu_type=1). NVENC AV1 emits a SeqHdr OBU only at keyframes
        // when RepeatSeqHdr is on (and RepeatSeqHdr IS on — see
        // NvencAv1Encoder.cs). The depacketizer reconstructs the OBU
        // stream with obu_has_size_field=1 + LEB128 size, so each OBU
        // is walkable by header byte + size field. If we find a SeqHdr,
        // mark that we've fed an IDR — the watchdog uses this to avoid
        // false-positive flushes during transient decoder slowness.
        if (ContainsSequenceHeaderObu(encodedSample, length))
        {
            _idrFedSinceLastOutput = true;
        }

        var inputBuffer = MediaFactory.MFCreateMemoryBuffer(length);
        inputBuffer.Lock(out nint ptr, out _, out _);
        Marshal.Copy(encodedSample, 0, ptr, length);
        inputBuffer.Unlock();
        inputBuffer.CurrentLength = length;

        var inputSample = MediaFactory.MFCreateSample();
        inputSample.AddBuffer(inputBuffer);

        // Stamp with the content clock so the MFT propagates it to the
        // output sample. DrainOutput then reads the output SampleTime and
        // attaches it to each DecodedVideoFrame — when the decoder yields
        // multiple outputs at once (or a buffered older frame alongside a
        // new one) each carries its OWN original input's timestamp, not
        // the current call's value.
        inputSample.SampleTime = inputTimestamp.Ticks;

        var processInT0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            _transform.ProcessInput(0, inputSample, 0);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[mf] AV1 decoder ProcessInput threw: {ex.Message}");
            return Array.Empty<DecodedVideoFrame>();
        }
        finally
        {
            inputSample.Dispose();
            inputBuffer.Dispose();
        }
        var processInMs = (System.Diagnostics.Stopwatch.GetTimestamp() - processInT0) * 1000.0
            / System.Diagnostics.Stopwatch.Frequency;
        if (processInMs > 4.0)
        {
            DebugLog.Write($"[recv-l4-procIn] ProcessInput={processInMs:F1}ms inputBytes={length} pts={inputTimestamp.TotalMilliseconds:F0}ms");
        }

        var drainT0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var output = DrainOutput();
        var drainMs = (System.Diagnostics.Stopwatch.GetTimestamp() - drainT0) * 1000.0
            / System.Diagnostics.Stopwatch.Frequency;
        if (drainMs > 6.0)
        {
            DebugLog.Write($"[recv-l4-drain] DrainOutput={drainMs:F1}ms outputs={output.Count} pts={inputTimestamp.TotalMilliseconds:F0}ms");
        }
        var decodeMs = (System.Diagnostics.Stopwatch.GetTimestamp() - decodeT0) * 1000.0
            / System.Diagnostics.Stopwatch.Frequency;
        var nDecode = ++_decodeCallCount;
        if (decodeMs > 8.0)
        {
            var spikes = ++_decodeSpikeLogCount;
            if (spikes <= 200 || spikes % 50 == 0)
            {
                DebugLog.Write($"[av1-decode-spike] frame#{nDecode} decode={decodeMs:F1}ms inputBytes={length} outputs={output.Count} pts={inputTimestamp.TotalMilliseconds:F0}ms");
            }
        }

        // Wedged-decoder watchdog. ProcessInput always succeeds, and
        // after a rejected sample DrainOutput keeps returning empty
        // because ProcessOutput says NEED_MORE_INPUT every time —
        // never trips the failure-cascade flush. If many Decode
        // calls in a row produce zero frames despite us feeding real
        // bitstream, the MFT is stuck. Force the same flush path the
        // failure cascade uses so the next IDR can bootstrap fresh.
        // _drainProducedAFrame is set inside DrainOutput on either
        // path (CPU result append OR GPU sink blit), so it works in
        // both GPU-handoff mode and the legacy readback mode.
        if (!_drainProducedAFrame)
        {
            // Suppress the watchdog inside the post-flush cooldown
            // window. Without this, the decoder gets ~210 ms to ingest
            // the next IDR; the next IDR's transit + bootstrap is
            // typically 50-200 ms, which is enough headroom most of
            // the time but can dip into the threshold and trigger a
            // re-flush that drops the IDR. Cooldown gives a hard
            // bottom of 300 ms before the watchdog can re-fire.
            var nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            if (nowTicks < _flushCooldownUntilTicks)
            {
                _consecutiveEmptyOutputs = 0;
            }
            else
            {
                var emptyStreak = ++_consecutiveEmptyOutputs;
                // Only flush when the threshold is hit AND we've
                // already given the decoder an IDR after the last
                // successful output. If no IDR has been fed yet, the
                // decoder is correctly waiting for one — flushing
                // would just discard the IDR we're about to feed.
                // Most observed false-positive flushes happened when
                // the decoder thread got stalled by a Gen2 GC pause
                // and recovered fine on its own once GC released it.
                if (emptyStreak == ConsecutiveEmptyOutputsFlushThreshold && _idrFedSinceLastOutput)
                {
                    DebugLog.Write($"[mf] AV1 decoder wedged ({emptyStreak} empty calls including post-IDR); flushing + restarting MFT");
                    TryFlushAndRestart();
                    _idrFedSinceLastOutput = false;
                }
                // Codec-agnostic recovery signal: ask upstream for an IDR
                // when the decoder is silently consuming bitstream
                // without producing output. Independent of the
                // _idrFedSinceLastOutput gate above (that controls
                // local flush; this controls upstream PLI). Receiver
                // debounces, so consecutive raises within the 500 ms
                // window collapse to one PLI.
                if (emptyStreak == KeyframeRequestZeroOutputThreshold)
                {
                    RaiseKeyframeNeeded($"{emptyStreak} consecutive Decode() calls produced no output");
                }
            }
        }
        else
        {
            _consecutiveEmptyOutputs = 0;
            _idrFedSinceLastOutput = false;
        }
        _drainProducedAFrame = false;

        return output;
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
                if (bufferSize <= 0)
                {
                    bufferSize = 1;
                }

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
                    // STREAM_CHANGE protocol: re-negotiate the output
                    // type, then attempt ProcessOutput AGAIN with the
                    // SAME buffered sample. MS docs say the MFT keeps
                    // the input across SetOutputType so we can recover
                    // it without losing the IDR — IF that works we
                    // skip the ~1.4 s freeze the flush path costs.
                    // Earlier we observed it not working; trying once
                    // more with `continue` so the outer loop reissues
                    // ProcessOutput. If that returns NEED_MORE_INPUT
                    // the IDR genuinely is gone and we'll fall through
                    // to the watchdog/flush path, but cost is the same
                    // as today rather than worse.
                    DebugLog.Write($"[mf] AV1 decoder STREAM_CHANGE; renegotiating, retrying ProcessOutput");
                    NegotiateOutputType();
                    continue;
                }

                if ((uint)result.Code == MF_E_TRANSFORM_TYPE_NOT_SET)
                {
                    if (!_outputTypeNegotiated)
                    {
                        NegotiateOutputType();
                        if (_outputTypeNegotiated)
                        {
                            continue;
                        }
                    }
                    if (!_warnedTypeNotSet)
                    {
                        _warnedTypeNotSet = true;
                        DebugLog.Write("[mf] AV1 decoder ProcessOutput stuck with TYPE_NOT_SET; decoder output format could not be negotiated");
                    }
                    RaiseKeyframeNeeded("ProcessOutput TYPE_NOT_SET");
                    return (IReadOnlyList<DecodedVideoFrame>?)results ?? Array.Empty<DecodedVideoFrame>();
                }

                if (result.Failure)
                {
                    var hr = (uint)result.Code;
                    var consecutive = ++_consecutiveProcessOutputFailures;
                    if (consecutive == 1 || consecutive % 30 == 0)
                    {
                        DebugLog.Write($"[mf] AV1 decoder ProcessOutput failed HRESULT 0x{hr:X8} consecutive={consecutive}");
                    }
                    if (consecutive == 1)
                    {
                        // First failure surfaces upstream immediately.
                        // Subsequent ones rely on receiver-side debounce
                        // to avoid PLI spam.
                        RaiseKeyframeNeeded($"ProcessOutput HRESULT 0x{hr:X8}");
                    }
                    // Two paths to flush:
                    // 1. Known-unrecoverable HRESULTs (OOM, invalid stream
                    //    data) — flush on first occurrence. The MFT is wedged
                    //    until flush regardless of how many more samples we
                    //    feed; waiting saves nothing and costs the user a
                    //    visible stutter window.
                    // 2. Generic cascade — 30 consecutive failures of any
                    //    other code. Belt-and-suspenders for HRESULTs we
                    //    haven't classified.
                    if (hr == HrOutOfMemory || hr == HrInvalidStreamData)
                    {
                        DebugLog.Write($"[mf] AV1 decoder hard-fail HRESULT 0x{hr:X8}; immediate flush");
                        TryFlushAndRestart();
                    }
                    else if (consecutive == ConsecutiveFailureFlushThreshold)
                    {
                        TryFlushAndRestart();
                    }
                    return (IReadOnlyList<DecodedVideoFrame>?)results ?? Array.Empty<DecodedVideoFrame>();
                }
                _consecutiveProcessOutputFailures = 0;

                if (!_outputTypeNegotiated || _width <= 0 || _height <= 0 || outSample is null)
                {
                    continue;
                }

                // Read the propagated SampleTime BEFORE we flatten / free
                // the sample. The MFT copies this from the input sample
                // that ACTUALLY produced this output, so a buffered older
                // frame released alongside a newer one carries its own
                // original timestamp, not the current Decode() call's.
                long outSampleTimeTicks;
                try { outSampleTimeTicks = outSample.SampleTime; }
                catch { outSampleTimeTicks = 0; }

                var outTs = TimeSpan.FromTicks(outSampleTimeTicks);

                // GPU texture fast path: skip the 14–32 MB BGRA readback
                // that otherwise caps us at ~100 fps on 1440p. Do the
                // NV12 → BGRA blit into _bgraDest on GPU, then hand the
                // texture pointer to the subscribed sink synchronously.
                // The sink is expected to GPU-copy the texture into its
                // own pool inside the callback; _bgraDest is reused on
                // the next frame.
                //
                // Skip the GPU handoff when we own our device: the
                // renderer that registered the handler is on its own
                // device (the shared one in App.SharedDevices) and
                // cannot use our ID3D11Texture2D. CPU readback path
                // below handles it.
                if (_useD3dPath && !_ownsDevice && GpuOutputHandler is { } gpuSink)
                {
                    if (BlitSampleToBgraGpu(outSample))
                    {
                        // Successful GPU blit = decoder produced a
                        // frame. Mark progress for the wedged-decoder
                        // watchdog (Decode() returns empty in this
                        // mode, so the watchdog can't tell otherwise).
                        _drainProducedAFrame = true;
                        // Self-balanced per-subscriber refcount contract:
                        // we DO NOT AddRef here. Each subscriber that needs
                        // the texture must AddRef itself before wrapping,
                        // and Release on dispose (or simply not touch
                        // refcount if observing only). The previous design
                        // AddRef'd once per gpuSink target then fanned out
                        // to N subscribers via TextureArrived; when N
                        // renderers each Released the count underflowed
                        // (1 - N per frame), the GPU driver could free
                        // the texture mid-frame, and downstream renderers
                        // got undefined contents.
                        var targets = gpuSink.GetInvocationList();
                        foreach (var target in targets)
                        {
                            try
                            {
                                ((Action<IntPtr, int, int, TimeSpan>)target).Invoke(
                                    _bgraDest!.NativePointer, _width, _height, outTs);
                            }
                            catch (Exception ex)
                            {
                                DebugLog.Write($"[mf] AV1 GpuOutputHandler threw: {ex.Message}");
                            }
                        }
                        // Refcount drift probe. After every subscriber
                        // has had its turn (added a ref on entry, released
                        // it when their copy finished), the underlying
                        // texture's refcount should return to whatever
                        // baseline it held when we created it. If the
                        // value climbs over time, somebody isn't releasing
                        // — that's the candidate cause for MFT internal
                        // pool starvation → 0x8007000E. We sample
                        // every Nth produced frame so we don't drown the
                        // log; the trend over minutes is what matters.
                        var n = ++_bgraRefcountSampleCount;
                        if (n <= 5 || n % 240 == 0)
                        {
                            // AddRef then Release returns the post-Release
                            // count, which equals the current count
                            // (because AddRef brings it +1 then Release
                            // brings it back). Cheap and side-effect-free.
                            try
                            {
                                var afterAdd = _bgraDest!.AddRef();
                                var afterRelease = _bgraDest.Release();
                                DebugLog.Write($"[mf-refcount] frame#{n} bgraDest refcount={afterRelease} (peak observed during AddRef={afterAdd}) subscribers={targets.Length}");
                            }
                            catch (Exception ex)
                            {
                                DebugLog.Write($"[mf-refcount] probe threw: {ex.Message}");
                            }
                        }
                        if (_loggedDecodedFrames < 3)
                        {
                            _loggedDecodedFrames++;
                        }
                    }
                    continue;
                }

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

                if (bgra is null)
                {
                    continue;
                }

                (results ??= new List<DecodedVideoFrame>()).Add(new DecodedVideoFrame(bgra, _width, _height, outTs));
                _drainProducedAFrame = true;

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
    /// Zero-copy variant of <see cref="DecodeSampleToBgraViaGpu"/>. Runs
    /// the NV12 → BGRA GPU blit into <see cref="_bgraDest"/> and STOPS —
    /// no CopyResource to staging, no Map, no CPU readback. Returns true
    /// if the blit succeeded; caller hands <see cref="_bgraDest"/> to the
    /// <see cref="GpuOutputHandler"/> sink which is expected to GPU-copy
    /// it into its own pool during the synchronous callback.
    /// </summary>
    private bool BlitSampleToBgraGpu(IMFSample sample)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        IMFDXGIBuffer? dxgiBuffer;
        try { dxgiBuffer = buffer.QueryInterface<IMFDXGIBuffer>(); }
        catch { return false; }

        ID3D11Texture2D? sourceTexture = null;
        try
        {
            var ptr = dxgiBuffer.GetResource(typeof(ID3D11Texture2D).GUID);
            if (ptr == IntPtr.Zero)
            {
                return false;
            }

            sourceTexture = new ID3D11Texture2D(ptr);

            uint arraySlice = 0;
            try { arraySlice = dxgiBuffer.SubresourceIndex; }
            catch { }

            EnsureScalerAndStaging(_width, _height);
            // Time the NV12 → BGRA blit. The decoder-side scaler runs
            // VideoProcessorBlt on the SAME D3D11 immediate context the
            // renderer's PaintFromQueue uses for its CopyResource. If
            // the painter's [gpu-copy-spike] coincides with a slow
            // [av1-blit-spike] the cause is shared-context contention
            // (D3D11 immediate context is not free-threaded — calls
            // serialize). Threshold matches the renderer side.
            var blitT0 = System.Diagnostics.Stopwatch.GetTimestamp();
            _scaler!.Process(sourceTexture, _bgraDest!, arraySlice);
            var blitMs = (System.Diagnostics.Stopwatch.GetTimestamp() - blitT0) * 1000.0
                / System.Diagnostics.Stopwatch.Frequency;
            if (blitMs > 5.0)
            {
                DebugLog.Write($"[av1-blit-spike] VideoProcessorBlt took {blitMs:F1}ms size={_width}x{_height}");
            }
            return true;
        }
        finally
        {
            sourceTexture?.Dispose();
            dxgiBuffer.Dispose();
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
            if (ptr == IntPtr.Zero)
            {
                return null;
            }

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

    private void NegotiateOutputType(ulong frameSizeHint = 0, ulong frameRateHint = 0)
    {
        // Walk the decoder's available output types looking for NV12; the
        // first match becomes the active output type. Once set, the next
        // ProcessOutput call will deliver decoded frames.
        //
        // For AV1, the MFT's available output types may have FRAME_SIZE = 0
        // before the decoder has parsed a Sequence OBU. SetOutputType
        // rejects (MF_E_INVALIDMEDIATYPE) without a non-zero FRAME_SIZE,
        // so when a hint is supplied we patch it onto the candidate before
        // applying.
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

            if (outputType is null)
            {
                return;
            }

            if (outputType.GetGUID(MediaTypeAttributeKeys.Subtype, out var subtype).Success && subtype == VideoFormatGuids.NV12)
            {
                if (outputType.GetUInt64(MediaTypeAttributeKeys.FrameSize, out ulong packed).Success && packed != 0)
                {
                    _width = (int)(packed >> 32);
                    _height = (int)(packed & 0xFFFFFFFFu);
                }
                else if (frameSizeHint != 0)
                {
                    outputType.Set(MediaTypeAttributeKeys.FrameSize, frameSizeHint);
                    _width = (int)(frameSizeHint >> 32);
                    _height = (int)(frameSizeHint & 0xFFFFFFFFu);
                    if (frameRateHint != 0)
                    {
                        outputType.Set(MediaTypeAttributeKeys.FrameRate, frameRateHint);
                    }
                }
                _transform.SetOutputType(0, outputType, 0);
                outputType.Dispose();
                _outputTypeNegotiated = true;
                DebugLog.Write($"[mf] AV1 decoder negotiated NV12 {_width}x{_height}");
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
        if ((uint)b > 255)
        {
            b = b < 0 ? 0 : 255;
        }

        if ((uint)g > 255)
        {
            g = g < 0 ? 0 : 255;
        }

        if ((uint)r > 255)
        {
            r = r < 0 ? 0 : 255;
        }

        return 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
    }

    private static IMFTransform? CreateDecoder()
    {
        var inputFilter = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = MFVideoFormatAv1,
        };

        // Enumerate HARDWARE with BOTH sync and async flags so we surface
        // whichever mode the driver actually exposes. Some modern HW
        // decoders register only as ASYNCMFT; filtering to SYNCMFT alone
        // can drop us onto a slower sync shim or even exclude the real
        // hardware entirely. MF will still drive an enumerated async MFT
        // via the legacy ProcessInput / ProcessOutput path we use today.
        var hwFlags = (uint)(EnumFlag.EnumFlagHardware
                             | EnumFlag.EnumFlagSyncmft
                             | EnumFlag.EnumFlagAsyncmft
                             | EnumFlag.EnumFlagSortandfilter);
        var transform = TryCreateDecoder(hwFlags, inputFilter, "hardware");
        if (transform is not null)
        {
            return transform;
        }

        var swFlags = (uint)(EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagSortandfilter);
        return TryCreateDecoder(swFlags, inputFilter, "software");
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
                DebugLog.Write($"[mf] {label} AV1 decoder activated");
                return transform;
            }
            catch (Exception ex)
            {
                DebugLog.Write($"[mf] {label} AV1 decoder activate threw: {ex.Message}");
            }
        }
        return null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try { _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, 0); } catch { }
        try { _transform.Dispose(); } catch { }
        try { _scaler?.Dispose(); } catch { }
        try { _bgraDest?.Dispose(); } catch { }
        try { _bgraStaging?.Dispose(); } catch { }
        try { _dxgiManager?.Dispose(); } catch { }
        // Dispose _d3dDevice only when this decoder created it. If the
        // caller passed in a shared device, they own its lifetime.
        if (_ownsDevice)
        {
            try { _d3dDevice?.Dispose(); } catch { }
        }
    }
}
