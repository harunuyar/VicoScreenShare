namespace VicoScreenShare.Client.Windows.Media.Codecs.Nvenc;

using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using VicoScreenShare.Client.Diagnostics;
using VicoScreenShare.Client.Media.Codecs;
using VicoScreenShare.Client.Windows.Direct3D;
using Vortice.Direct3D11;
using IVideoEncoder = VicoScreenShare.Client.Media.Codecs.IVideoEncoder;

/// <summary>
/// Direct NVENC SDK H.264 encoder. Minimal Phase-2 surface: opens a session
/// against the shared D3D11 device, applies the P4/LOW_LATENCY preset config
/// with bitrate + GOP overrides, registers an owned input texture pool,
/// allocates bitstream buffers + completion events, and runs an async event
/// pump that mirrors the existing MFT encoder's contract. AQ, lookahead,
/// intra-refresh, and VBV overrides are Phase 3.
///
/// Public surface mirrors <see cref="MediaFoundationH264Encoder"/> so the
/// rest of the pipeline (CaptureStreamer, the async-output dispatch path)
/// works without changes.
/// </summary>
public sealed unsafe class NvencH264Encoder : IVideoEncoder, IAsyncEncodedOutputSource
{
    // Pool size: one slot per in-flight frame. Phase 2 has no lookahead, so
    // 4 slots is plenty (the encoder pipeline depth is at most 1 here);
    // bumping later when lookahead is enabled is a one-line change.
    private const int PoolSize = 4;

    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly int _gopFrames;
    private readonly NvencEncodeOptions _options;
    private readonly NvencCapabilities _capabilities;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private ITextureScaler? _textureScaler;
    private int _textureScalerSrcWidth;
    private int _textureScalerSrcHeight;

    private IntPtr _encoder;
    private NV_ENCODE_API_FUNCTION_LIST _table;

    private readonly NvencApi.NvEncDestroyEncoderFn _destroy;
    private readonly NvencApi.NvEncRegisterResourceFn _registerResource;
    private readonly NvencApi.NvEncUnregisterResourceFn _unregisterResource;
    private readonly NvencApi.NvEncMapInputResourceFn _mapInput;
    private readonly NvencApi.NvEncUnmapInputResourceFn _unmapInput;
    private readonly NvencApi.NvEncCreateBitstreamBufferFn _createBitstream;
    private readonly NvencApi.NvEncDestroyBitstreamBufferFn _destroyBitstream;
    private readonly NvencApi.NvEncRegisterAsyncEventFn _registerEvent;
    private readonly NvencApi.NvEncUnregisterAsyncEventFn _unregisterEvent;
    private readonly NvencApi.NvEncEncodePictureFn _encodePicture;
    private readonly NvencApi.NvEncLockBitstreamFn _lockBitstream;
    private readonly NvencApi.NvEncUnlockBitstreamFn _unlockBitstream;
    private readonly NvencApi.NvEncReconfigureEncoderFn _reconfigure;
    private readonly NvencApi.NvEncInitializeEncoderFn _initialize;
    private readonly NvencApi.NvEncGetEncodePresetConfigExFn _getPresetConfig;
    private readonly NvencApi.NvEncGetLastErrorStringFn? _getLastError;

    private readonly Slot[] _slots;
    private readonly SemaphoreSlim _freeSlots; // released as completion events fire
    private readonly ConcurrentQueue<int> _pendingSlots = new(); // FIFO for the pump
    private readonly ManualResetEventSlim _pendingNotify = new(false);
    private readonly ConcurrentQueue<EncodedFrame> _outputQueue = new();
    private readonly object _submitLock = new();
    private readonly Thread _pumpThread;
    private readonly CancellationTokenSource _pumpCts = new();
    private readonly IntPtr _cancelEvent;
    private long _bitrate;
    private int _pendingForceIdr;
    private uint _frameIdx;
    private bool _disposed;

    private Action? _outputAvailable;
    public event Action? OutputAvailable
    {
        add { _outputAvailable += value; }
        remove { _outputAvailable -= value; }
    }
    public bool TryDequeueEncoded(out EncodedFrame frame) => _outputQueue.TryDequeue(out frame);

    public VideoCodec Codec => VideoCodec.H264;
    public int Width => _width;
    public int Height => _height;
    public bool SupportsTextureInput => true;

    /// <summary>Per-slot bookkeeping. One slot = one in-flight frame
    /// (input texture, registered handle, mapped pointer, bitstream buffer,
    /// completion event, plus the timestamp of whatever was submitted).</summary>
    private sealed class Slot
    {
        public ID3D11Texture2D Texture = null!;
        public IntPtr Registered;     // from RegisterResource
        public IntPtr Mapped;         // from MapInputResource (cleared after each encode)
        public IntPtr Bitstream;      // from CreateBitstreamBuffer
        public IntPtr Event;          // Win32 event handle
        public TimeSpan PendingTimestamp; // valid while the slot is in _pendingSlots
        public bool Pending;
    }

    public NvencH264Encoder(int width, int height, int fps, long bitrate, int gopFrames, ID3D11Device device)
        : this(width, height, fps, bitrate, gopFrames, device, NvencEncodeOptions.Default)
    {
    }

    public NvencH264Encoder(int width, int height, int fps, long bitrate, int gopFrames, ID3D11Device device, NvencEncodeOptions options)
    {
        _width = width;
        _height = height;
        _fps = Math.Max(1, fps);
        _bitrate = Math.Max(500_000, bitrate);
        _gopFrames = Math.Max(1, gopFrames);
        _options = options;
        _device = device;
        _context = device.ImmediateContext;

        // Probe must already have validated availability; we re-probe here
        // because the function table is per-process and the encoder needs
        // its own resolved delegates against the populated table.
        var caps = NvencCapabilities.Probe(device);
        if (!caps.IsAvailable)
        {
            throw new NvencException(NVENCSTATUS.NV_ENC_ERR_NO_ENCODE_DEVICE,
                "ctor", caps.UnavailableReason);
        }
        _capabilities = caps;

        _table = default;
        _table.version = NvencApi.NV_ENCODE_API_FUNCTION_LIST_VER;
        var status = NvencApi.NvEncodeAPICreateInstance(ref _table);
        if (status != NVENCSTATUS.NV_ENC_SUCCESS)
        {
            throw new NvencException(status, "NvEncodeAPICreateInstance");
        }

        // Resolve every delegate we'll need before we start touching state —
        // a missing entry here means we're talking to an older driver than
        // we built against and we should bail before allocating anything.
        _destroy = Marshal.GetDelegateForFunctionPointer<NvencApi.NvEncDestroyEncoderFn>(_table.nvEncDestroyEncoder);
        _registerResource = Marshal.GetDelegateForFunctionPointer<NvencApi.NvEncRegisterResourceFn>(_table.nvEncRegisterResource);
        _unregisterResource = Marshal.GetDelegateForFunctionPointer<NvencApi.NvEncUnregisterResourceFn>(_table.nvEncUnregisterResource);
        _mapInput = Marshal.GetDelegateForFunctionPointer<NvencApi.NvEncMapInputResourceFn>(_table.nvEncMapInputResource);
        _unmapInput = Marshal.GetDelegateForFunctionPointer<NvencApi.NvEncUnmapInputResourceFn>(_table.nvEncUnmapInputResource);
        _createBitstream = Marshal.GetDelegateForFunctionPointer<NvencApi.NvEncCreateBitstreamBufferFn>(_table.nvEncCreateBitstreamBuffer);
        _destroyBitstream = Marshal.GetDelegateForFunctionPointer<NvencApi.NvEncDestroyBitstreamBufferFn>(_table.nvEncDestroyBitstreamBuffer);
        _registerEvent = Marshal.GetDelegateForFunctionPointer<NvencApi.NvEncRegisterAsyncEventFn>(_table.nvEncRegisterAsyncEvent);
        _unregisterEvent = Marshal.GetDelegateForFunctionPointer<NvencApi.NvEncUnregisterAsyncEventFn>(_table.nvEncUnregisterAsyncEvent);
        _encodePicture = Marshal.GetDelegateForFunctionPointer<NvencApi.NvEncEncodePictureFn>(_table.nvEncEncodePicture);
        _lockBitstream = Marshal.GetDelegateForFunctionPointer<NvencApi.NvEncLockBitstreamFn>(_table.nvEncLockBitstream);
        _unlockBitstream = Marshal.GetDelegateForFunctionPointer<NvencApi.NvEncUnlockBitstreamFn>(_table.nvEncUnlockBitstream);
        _reconfigure = Marshal.GetDelegateForFunctionPointer<NvencApi.NvEncReconfigureEncoderFn>(_table.nvEncReconfigureEncoder);
        _initialize = Marshal.GetDelegateForFunctionPointer<NvencApi.NvEncInitializeEncoderFn>(_table.nvEncInitializeEncoder);
        _getPresetConfig = Marshal.GetDelegateForFunctionPointer<NvencApi.NvEncGetEncodePresetConfigExFn>(_table.nvEncGetEncodePresetConfigEx);
        _getLastError = _table.nvEncGetLastErrorString != IntPtr.Zero
            ? Marshal.GetDelegateForFunctionPointer<NvencApi.NvEncGetLastErrorStringFn>(_table.nvEncGetLastErrorString)
            : null;

        var openSession = Marshal.GetDelegateForFunctionPointer<NvencApi.NvEncOpenEncodeSessionExFn>(
            _table.nvEncOpenEncodeSessionEx);
        var sessionParams = new NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS
        {
            version = NvencApi.NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER,
            deviceType = NV_ENC_DEVICE_TYPE.DirectX,
            device = device.NativePointer,
            apiVersion = NvencApi.NVENCAPI_VERSION,
        };
        IntPtr enc;
        status = openSession(ref sessionParams, &enc);
        if (status != NVENCSTATUS.NV_ENC_SUCCESS || enc == IntPtr.Zero)
        {
            throw new NvencException(status, "NvEncOpenEncodeSessionEx");
        }
        _encoder = enc;

        try
        {
            InitializeEncoderConfig();
            _cancelEvent = CreateEventW(IntPtr.Zero, manualReset: true, initialState: false, name: IntPtr.Zero);
            if (_cancelEvent == IntPtr.Zero)
            {
                throw new NvencException(NVENCSTATUS.NV_ENC_ERR_OUT_OF_MEMORY, "CreateEventW(cancel)");
            }
            _slots = new Slot[PoolSize];
            for (int i = 0; i < PoolSize; i++)
            {
                _slots[i] = AllocateSlot(width, height);
            }
            _freeSlots = new SemaphoreSlim(PoolSize, PoolSize);
        }
        catch
        {
            // Construction failure must not leak the session.
            _destroy(_encoder);
            _encoder = IntPtr.Zero;
            throw;
        }

        _pumpThread = new Thread(EventPumpLoop)
        {
            IsBackground = true,
            Name = "NVENC event pump",
        };
        _pumpThread.Start();

        DebugLog.Write($"[nvenc] H264 encoder initialized {width}x{height}@{_fps} {_bitrate} bps "
            + $"(P4 LOW_LATENCY, async, pool={PoolSize})");
    }

    private void InitializeEncoderConfig()
    {
        // Get the preset's recommended config, then overlay our bitrate / GOP.
        // Anything we don't override (rate-control mode, profile, CABAC, etc.)
        // stays at the preset's default — Phase 3 will start customizing more.
        // Allocate the preset config struct on the heap and explicitly zero
        // it. Stack `default(T)` plus field assignment SHOULD zero the
        // trailing fixed-size buffers, but explicit memset is what OBS does
        // and removes a class of layout/alignment surprises.
        var preset = default(NV_ENC_PRESET_CONFIG);
        var presetSpan = new Span<byte>(&preset, sizeof(NV_ENC_PRESET_CONFIG));
        presetSpan.Clear();
        preset.version = NvencApi.NV_ENC_PRESET_CONFIG_VER;
        preset.presetCfg.version = NvencApi.NV_ENC_CONFIG_VER;
        preset.presetCfg.rcParams.version = NvencApi.NV_ENC_RC_PARAMS_VER;

        var presetGuid = NvencGuids.PresetByLevel(_options.Preset);
        var status = _getPresetConfig(_encoder,
            NvencGuids.CodecH264, presetGuid,
            NV_ENC_TUNING_INFO.LowLatency, &preset);
        if (status != NVENCSTATUS.NV_ENC_SUCCESS)
        {
            throw new NvencException(status, "NvEncGetEncodePresetConfigEx", GetLastErrorString());
        }

        // Override what we care about for Phase 2 baseline.
        preset.presetCfg.frameIntervalP = 1; // IPP — no B frames (low-latency).
        preset.presetCfg.rcParams.rateControlMode = NV_ENC_PARAMS_RC_MODE.Cbr;
        preset.presetCfg.rcParams.averageBitRate = (uint)Math.Min(_bitrate, uint.MaxValue);
        preset.presetCfg.rcParams.maxBitRate = preset.presetCfg.rcParams.averageBitRate;

        ApplyQualityOptions(ref preset.presetCfg);

        var init = default(NV_ENC_INITIALIZE_PARAMS);
        init.version = NvencApi.NV_ENC_INITIALIZE_PARAMS_VER;
        init.encodeGUID = NvencGuids.CodecH264;
        init.presetGUID = presetGuid;
        init.encodeWidth = (uint)_width;
        init.encodeHeight = (uint)_height;
        init.darWidth = (uint)_width;
        init.darHeight = (uint)_height;
        init.frameRateNum = (uint)_fps;
        init.frameRateDen = 1;
        init.enableEncodeAsync = 1; // Win32 events for completion notification.
        init.enablePTD = 1;         // Picture Type Decision in driver.
        init.tuningInfo = NV_ENC_TUNING_INFO.LowLatency;
        // bufferFormat: SDK doc (line 2283) says "Used only when DX12
        // interface type is used". Setting it on D3D11 returns
        // NV_ENC_ERR_INVALID_PARAM on some drivers; leave Undefined.
        init.bufferFormat = NV_ENC_BUFFER_FORMAT.Undefined;
        init.maxEncodeWidth = (uint)_width;
        init.maxEncodeHeight = (uint)_height;
        // Pin the encodeConfig pointer for this call only — InitializeEncoder
        // copies the config into the session, after which the pointer can go
        // out of scope.
        init.encodeConfig = &preset.presetCfg;

        status = _initialize(_encoder, &init);
        if (status != NVENCSTATUS.NV_ENC_SUCCESS)
        {
            throw new NvencException(status, "NvEncInitializeEncoder", GetLastErrorString());
        }
    }

    /// <summary>
    /// Apply <see cref="NvencEncodeOptions"/> to a preset-filled config,
    /// gated on <see cref="NvencCapabilities"/>. Each unsupported option
    /// is logged once and silently dropped — the user's session still
    /// initializes, just without that feature.
    /// </summary>
    private void ApplyQualityOptions(ref NV_ENC_CONFIG cfg)
    {
        // --- Spatial AQ (universal capability — no cap bit gates it) ---
        if (_options.EnableAdaptiveQuantization)
        {
            cfg.rcParams.bitfields |= RcParamsBits.EnableAQ;
            if (_options.AqStrength > 0)
            {
                var clamped = (uint)Math.Clamp(_options.AqStrength, 1, 15);
                cfg.rcParams.bitfields = (cfg.rcParams.bitfields & ~RcParamsBits.AqStrengthMask)
                    | (clamped << RcParamsBits.AqStrengthShift);
            }
        }

        // --- Temporal AQ (Turing GPUs lack this) ---
        if (_options.EnableTemporalAq)
        {
            if (_capabilities.SupportsTemporalAq)
            {
                cfg.rcParams.bitfields |= RcParamsBits.EnableTemporalAQ;
            }
            else
            {
                DebugLog.Write("[nvenc] options: TemporalAq requested but unsupported on this GPU; falling back to spatial AQ only");
            }
        }

        // --- Lookahead ---
        if (_options.LookaheadDepth > 0)
        {
            if (_capabilities.SupportsLookahead)
            {
                cfg.rcParams.bitfields |= RcParamsBits.EnableLookahead;
                cfg.rcParams.lookaheadDepth = (ushort)Math.Clamp(_options.LookaheadDepth, 1, 32);
                DebugLog.Write($"[nvenc] options: lookahead enabled depth={cfg.rcParams.lookaheadDepth}");
            }
            else
            {
                DebugLog.Write("[nvenc] options: Lookahead requested but unsupported on this GPU; ignoring");
            }
        }

        // --- VBV buffer size override ---
        // The MFT shim's silently-ignored knob; here it actually takes effect.
        if (_options.VbvBufferSizeBits > 0)
        {
            if (_capabilities.SupportsCustomVbvBufferSize)
            {
                cfg.rcParams.vbvBufferSize = (uint)_options.VbvBufferSizeBits;
                cfg.rcParams.vbvInitialDelay = (uint)_options.VbvBufferSizeBits;
                DebugLog.Write($"[nvenc] options: vbvBufferSize={_options.VbvBufferSizeBits} bits");
            }
            else
            {
                DebugLog.Write("[nvenc] options: VbvBufferSizeBits requested but unsupported on this GPU; ignoring");
            }
        }

        // --- GOP length / IDR period ---
        // Intra-refresh requires gopLength = INFINITE (per SDK note line 1868);
        // periodic IDR is mutually exclusive with intra-refresh.
        var wantIntraRefresh = _options.EnableIntraRefresh && _capabilities.SupportsIntraRefresh;
        if (_options.EnableIntraRefresh && !_capabilities.SupportsIntraRefresh)
        {
            DebugLog.Write("[nvenc] options: IntraRefresh requested but unsupported on this GPU; ignoring");
        }

        unsafe
        {
            // Overlay the H.264-specific config onto the codec union and
            // patch the bits we want.
            fixed (NV_ENC_CODEC_CONFIG* codecCfgPtr = &cfg.encodeCodecConfig)
            {
                var h264 = (NV_ENC_CONFIG_H264_OVERLAY*)codecCfgPtr;
                // RepeatSPSPPS: cheap insurance for SIPSorcery's depacketizer
                // reading the stream from any keyframe — always safe to set.
                h264->bitfields |= H264ConfigBits.RepeatSPSPPS;

                if (wantIntraRefresh)
                {
                    h264->bitfields |= H264ConfigBits.EnableIntraRefresh;
                    var period = _options.IntraRefreshPeriodFrames > 0
                        ? (uint)_options.IntraRefreshPeriodFrames
                        : (uint)_gopFrames;
                    h264->intraRefreshPeriod = period;
                    // Length of the refresh cycle: spread across max(2, period/30) frames.
                    h264->intraRefreshCnt = (uint)Math.Max(2, (int)(period / 30));
                    h264->idrPeriod = NvencApi.NVENC_INFINITE_GOPLENGTH;
                    cfg.gopLength = NvencApi.NVENC_INFINITE_GOPLENGTH;
                    DebugLog.Write($"[nvenc] options: intraRefresh enabled period={period} cnt={h264->intraRefreshCnt}");
                }
                else
                {
                    cfg.gopLength = (uint)_gopFrames;
                    h264->idrPeriod = (uint)_gopFrames;
                }
            }
        }
    }

    private Slot AllocateSlot(int width, int height)
    {
        // Owned BGRA texture with KEYEDMUTEX-free access — RegisterResource
        // does its own synchronization. RENDER_TARGET binding lets us
        // CopyResource into the slot from any source on the same device.
        var desc = new Texture2DDescription
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
        var texture = _device.CreateTexture2D(desc);

        var reg = default(NV_ENC_REGISTER_RESOURCE);
        reg.version = NvencApi.NV_ENC_REGISTER_RESOURCE_VER;
        reg.resourceType = NV_ENC_INPUT_RESOURCE_TYPE.Directx;
        reg.width = (uint)width;
        reg.height = (uint)height;
        reg.pitch = 0; // SDK: 0 for DirectX
        reg.subResourceIndex = 0;
        reg.resourceToRegister = texture.NativePointer;
        reg.bufferFormat = NV_ENC_BUFFER_FORMAT.Argb;
        reg.bufferUsage = NV_ENC_BUFFER_USAGE.InputImage;

        var s = _registerResource(_encoder, &reg);
        if (s != NVENCSTATUS.NV_ENC_SUCCESS)
        {
            texture.Dispose();
            throw new NvencException(s, "NvEncRegisterResource");
        }

        var bs = default(NV_ENC_CREATE_BITSTREAM_BUFFER);
        bs.version = NvencApi.NV_ENC_CREATE_BITSTREAM_BUFFER_VER;
        var bsStatus = _createBitstream(_encoder, &bs);
        if (bsStatus != NVENCSTATUS.NV_ENC_SUCCESS)
        {
            _unregisterResource(_encoder, reg.registeredResource);
            texture.Dispose();
            throw new NvencException(bsStatus, "NvEncCreateBitstreamBuffer");
        }

        // Each in-flight encode needs its own completion event registered
        // with the encoder. Win32 manual-reset event so the pump can wait
        // and reset explicitly.
        var evt = CreateEventW(IntPtr.Zero, manualReset: true, initialState: false, name: IntPtr.Zero);
        if (evt == IntPtr.Zero)
        {
            _destroyBitstream(_encoder, bs.bitstreamBuffer);
            _unregisterResource(_encoder, reg.registeredResource);
            texture.Dispose();
            throw new NvencException(NVENCSTATUS.NV_ENC_ERR_OUT_OF_MEMORY, "CreateEventW(slot)");
        }

        var ep = default(NV_ENC_EVENT_PARAMS);
        ep.version = NvencApi.NV_ENC_EVENT_PARAMS_VER;
        ep.completionEvent = evt;
        var es = _registerEvent(_encoder, &ep);
        if (es != NVENCSTATUS.NV_ENC_SUCCESS)
        {
            CloseHandle(evt);
            _destroyBitstream(_encoder, bs.bitstreamBuffer);
            _unregisterResource(_encoder, reg.registeredResource);
            texture.Dispose();
            throw new NvencException(es, "NvEncRegisterAsyncEvent");
        }

        return new Slot
        {
            Texture = texture,
            Registered = reg.registeredResource,
            Bitstream = bs.bitstreamBuffer,
            Event = evt,
        };
    }

    public EncodedFrame? EncodeBgra(byte[] bgra, int stride, TimeSpan inputTimestamp)
    {
        if (_disposed)
        {
            return null;
        }
        // Stage CPU bytes into a DYNAMIC texture, then CopyResource into the
        // pool slot. Phase 2 doesn't pre-allocate a staging texture because
        // EncodeBgra is only exercised by tests / harness — the production
        // path is EncodeTexture.
        using var staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_width,
            Height = (uint)_height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.None,
        });
        var box = _context.Map(staging, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var rowBytes = Math.Min(stride, _width * 4);
            var rowPitch = (int)box.RowPitch;
            for (int y = 0; y < _height; y++)
            {
                Marshal.Copy(bgra, y * stride, IntPtr.Add(box.DataPointer, y * rowPitch), rowBytes);
            }
        }
        finally
        {
            _context.Unmap(staging, 0);
        }
        SubmitEncode(staging.NativePointer, _width, _height, inputTimestamp);
        return null;
    }

    public EncodedFrame? EncodeTexture(IntPtr nativeTexture, int sourceWidth, int sourceHeight, TimeSpan inputTimestamp)
    {
        if (_disposed)
        {
            return null;
        }
        SubmitEncode(nativeTexture, sourceWidth, sourceHeight, inputTimestamp);
        return null;
    }

    private void SubmitEncode(IntPtr sourceTexture, int sourceWidth, int sourceHeight, TimeSpan timestamp)
    {
        // Block until a slot is free. The pump returns slots to the
        // semaphore as completion events fire — backpressure on the caller
        // is what limits in-flight depth.
        if (!_freeSlots.Wait(5000))
        {
            DebugLog.Write("[nvenc] SubmitEncode: timed out waiting for free slot");
            return;
        }

        int slotIdx = -1;
        Slot? slot = null;
        // Pick the first non-pending slot. With FIFO completion-event
        // ordering this loop almost always finds slot 0 immediately.
        for (int i = 0; i < _slots.Length; i++)
        {
            if (!_slots[i].Pending)
            {
                slotIdx = i;
                slot = _slots[i];
                break;
            }
        }
        if (slot is null)
        {
            // Should not happen — semaphore guarantees at least one free.
            _freeSlots.Release();
            return;
        }

        lock (_submitLock)
        {
            // Scale/copy from source into the encoder-owned texture. The
            // source texture usually has capture dimensions while this slot
            // has encoder dimensions; CopyResource is valid only when they
            // match, so route every texture input through the same GPU scaler
            // the MFT path uses.
            using var srcTexture = new ID3D11Texture2D(sourceTexture);
            srcTexture.AddRef(); // we don't own this pointer; balance the wrapper's Release.
            try
            {
                if (sourceWidth == _width && sourceHeight == _height)
                {
                    _context.CopyResource(slot.Texture, srcTexture);
                }
                else
                {
                    EnsureTextureScaler(sourceWidth, sourceHeight);
                    _textureScaler!.Process(srcTexture, slot.Texture);
                }
            }
            catch (Exception ex)
            {
                DebugLog.Write($"[nvenc] texture scaler threw: {ex.Message}");
                _freeSlots.Release();
                return;
            }

            // Map the registered handle for this encode. The mapped pointer
            // must be freed via NvEncUnmapInputResource after the pump locks
            // the bitstream out.
            var mapParams = default(NV_ENC_MAP_INPUT_RESOURCE);
            mapParams.version = NvencApi.NV_ENC_MAP_INPUT_RESOURCE_VER;
            mapParams.registeredResource = slot.Registered;
            var ms = _mapInput(_encoder, &mapParams);
            if (ms != NVENCSTATUS.NV_ENC_SUCCESS)
            {
                DebugLog.Write($"[nvenc] MapInputResource failed: {ms}");
                _freeSlots.Release();
                return;
            }
            slot.Mapped = mapParams.mappedResource;

            var pic = default(NV_ENC_PIC_PARAMS);
            pic.version = NvencApi.NV_ENC_PIC_PARAMS_VER;
            pic.inputWidth = (uint)_width;
            pic.inputHeight = (uint)_height;
            pic.inputPitch = (uint)_width;
            pic.frameIdx = _frameIdx++;
            pic.inputTimeStamp = (ulong)timestamp.Ticks;
            pic.inputBuffer = slot.Mapped;
            pic.outputBitstream = slot.Bitstream;
            pic.completionEvent = slot.Event;
            pic.bufferFmt = NV_ENC_BUFFER_FORMAT.Argb;
            pic.pictureStruct = NV_ENC_PIC_STRUCT.Frame;
            // Force-IDR flag set+cleared atomically.
            if (Interlocked.Exchange(ref _pendingForceIdr, 0) != 0)
            {
                pic.encodePicFlags = (uint)NV_ENC_PIC_FLAGS.ForceIdr;
                DebugLog.Write("[nvenc/h264] forced IDR flag consumed on this frame");
            }

            slot.PendingTimestamp = timestamp;
            slot.Pending = true;
            _pendingSlots.Enqueue(slotIdx);

            var es = _encodePicture(_encoder, &pic);
            // EncodePicture in async mode returns NEED_MORE_INPUT only when
            // PTD with B frames is on; we have B-frames disabled (frameIntervalP=1),
            // so SUCCESS is the only expected non-fatal return.
            if (es != NVENCSTATUS.NV_ENC_SUCCESS && es != NVENCSTATUS.NV_ENC_ERR_NEED_MORE_INPUT)
            {
                DebugLog.Write($"[nvenc] EncodePicture failed: {es}");
                // Roll back: pump won't see the slot since the event won't
                // fire. Reclaim the slot directly.
                slot.Pending = false;
                _pendingSlots.TryDequeue(out _); // best effort — same slot
                _unmapInput(_encoder, slot.Mapped);
                slot.Mapped = IntPtr.Zero;
                _freeSlots.Release();
                return;
            }

            // Wake the pump; it may have been sleeping on _pendingNotify if
            // the queue was empty.
            _pendingNotify.Set();
        }
    }

    private void EnsureTextureScaler(int sourceWidth, int sourceHeight)
    {
        if (_textureScaler is not null
            && _textureScalerSrcWidth == sourceWidth
            && _textureScalerSrcHeight == sourceHeight)
        {
            return;
        }

        _textureScaler?.Dispose();
        _textureScaler = new D3D11VideoScaler(_device, sourceWidth, sourceHeight, _width, _height);
        _textureScalerSrcWidth = sourceWidth;
        _textureScalerSrcHeight = sourceHeight;
        DebugLog.Write($"[nvenc] texture pipeline built {sourceWidth}x{sourceHeight} -> {_width}x{_height} (Bilinear)");
    }

    private void EventPumpLoop()
    {
        DebugLog.Write("[nvenc] event pump started");
        // Stack-allocated handle pair, reused on every wait. CA2014 (no
        // stackalloc in loops) — keeping it outside the loop satisfies the
        // analyzer and is more efficient anyway.
        Span<IntPtr> handles = stackalloc IntPtr[2];
        handles[1] = _cancelEvent;
        try
        {
            while (!_pumpCts.IsCancellationRequested)
            {
                if (!_pendingSlots.TryPeek(out var slotIdx))
                {
                    // Nothing in flight — sleep until something is enqueued
                    // or shutdown. _pendingNotify is reset after we observe
                    // it; the submit path Sets it after each enqueue.
                    _pendingNotify.Wait(_pumpCts.Token);
                    _pendingNotify.Reset();
                    continue;
                }
                var slot = _slots[slotIdx];
                handles[0] = slot.Event;

                // SDK guarantees events arrive in submit order, so we wait
                // on the head of the FIFO. Cancel event lets shutdown bail.
                fixed (IntPtr* hp = handles)
                {
                    var wait = WaitForMultipleObjects(2, hp, false, 0xFFFFFFFFu);
                    if (wait == 1u || _pumpCts.IsCancellationRequested)
                    {
                        return;
                    }
                    if (wait != 0u)
                    {
                        DebugLog.Write($"[nvenc] WaitForMultipleObjects unexpected={wait}");
                        return;
                    }
                }
                ResetEvent(slot.Event);

                // Lock + copy bitstream out. Then unlock, unmap input,
                // recycle the slot.
                var lk = default(NV_ENC_LOCK_BITSTREAM);
                lk.version = NvencApi.NV_ENC_LOCK_BITSTREAM_VER;
                lk.outputBitstream = slot.Bitstream;
                var ls = _lockBitstream(_encoder, &lk);
                if (ls != NVENCSTATUS.NV_ENC_SUCCESS)
                {
                    DebugLog.Write($"[nvenc] LockBitstream failed: {ls}");
                    // Don't bail entirely — drop this frame and recycle.
                    _unmapInput(_encoder, slot.Mapped);
                    slot.Mapped = IntPtr.Zero;
                    slot.Pending = false;
                    _pendingSlots.TryDequeue(out _);
                    _freeSlots.Release();
                    continue;
                }
                var size = (int)lk.bitstreamSizeInBytes;
                var bytes = new byte[size];
                Marshal.Copy(lk.bitstreamBufferPtr, bytes, 0, size);
                var ts = TimeSpan.FromTicks((long)lk.outputTimeStamp);

                _unlockBitstream(_encoder, slot.Bitstream);
                _unmapInput(_encoder, slot.Mapped);
                slot.Mapped = IntPtr.Zero;
                slot.Pending = false;
                _pendingSlots.TryDequeue(out _);
                _freeSlots.Release();

                _outputQueue.Enqueue(new EncodedFrame(bytes, ts));
                _outputAvailable?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[nvenc] event pump threw: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            DebugLog.Write("[nvenc] event pump exiting");
        }
    }

    public void RequestKeyframe()
    {
        Interlocked.Exchange(ref _pendingForceIdr, 1);
    }

    public void UpdateBitrate(int bitsPerSecond)
    {
        if (_disposed)
        {
            return;
        }
        var clamped = Math.Max(500_000, bitsPerSecond);
        if (clamped == _bitrate)
        {
            return;
        }
        _bitrate = clamped;

        // Reconfigure copies the same fields as initialize. Resolution and
        // GOP can't change here; bitrate is the supported runtime knob.
        var preset = default(NV_ENC_PRESET_CONFIG);
        preset.version = NvencApi.NV_ENC_PRESET_CONFIG_VER;
        preset.presetCfg.version = NvencApi.NV_ENC_CONFIG_VER;
        preset.presetCfg.rcParams.version = NvencApi.NV_ENC_RC_PARAMS_VER;
        var presetGuid = NvencGuids.PresetByLevel(_options.Preset);
        var pcs = _getPresetConfig(_encoder, NvencGuids.CodecH264, presetGuid,
            NV_ENC_TUNING_INFO.LowLatency, &preset);
        if (pcs != NVENCSTATUS.NV_ENC_SUCCESS)
        {
            DebugLog.Write($"[nvenc] UpdateBitrate: GetEncodePresetConfigEx failed: {pcs}");
            return;
        }
        preset.presetCfg.gopLength = (uint)_gopFrames;
        preset.presetCfg.frameIntervalP = 1;
        preset.presetCfg.rcParams.rateControlMode = NV_ENC_PARAMS_RC_MODE.Cbr;
        preset.presetCfg.rcParams.averageBitRate = (uint)Math.Min(_bitrate, uint.MaxValue);
        preset.presetCfg.rcParams.maxBitRate = preset.presetCfg.rcParams.averageBitRate;
        ApplyQualityOptions(ref preset.presetCfg);

        var rec = default(NV_ENC_RECONFIGURE_PARAMS);
        rec.version = NvencApi.NV_ENC_RECONFIGURE_PARAMS_VER;
        rec.reInitEncodeParams.version = NvencApi.NV_ENC_INITIALIZE_PARAMS_VER;
        rec.reInitEncodeParams.encodeGUID = NvencGuids.CodecH264;
        rec.reInitEncodeParams.presetGUID = presetGuid;
        rec.reInitEncodeParams.encodeWidth = (uint)_width;
        rec.reInitEncodeParams.encodeHeight = (uint)_height;
        rec.reInitEncodeParams.darWidth = (uint)_width;
        rec.reInitEncodeParams.darHeight = (uint)_height;
        rec.reInitEncodeParams.frameRateNum = (uint)_fps;
        rec.reInitEncodeParams.frameRateDen = 1;
        rec.reInitEncodeParams.enableEncodeAsync = 1;
        rec.reInitEncodeParams.enablePTD = 1;
        rec.reInitEncodeParams.tuningInfo = NV_ENC_TUNING_INFO.LowLatency;
        rec.reInitEncodeParams.bufferFormat = NV_ENC_BUFFER_FORMAT.Undefined;
        rec.reInitEncodeParams.maxEncodeWidth = (uint)_width;
        rec.reInitEncodeParams.maxEncodeHeight = (uint)_height;
        rec.reInitEncodeParams.encodeConfig = &preset.presetCfg;

        var rs = _reconfigure(_encoder, &rec);
        if (rs != NVENCSTATUS.NV_ENC_SUCCESS)
        {
            DebugLog.Write($"[nvenc] UpdateBitrate: NvEncReconfigureEncoder failed: {rs}");
        }
    }

    private string? GetLastErrorString()
    {
        if (_getLastError is null || _encoder == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            var ptr = _getLastError(_encoder);
            return ptr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(ptr);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        // Stop the pump first so it doesn't race ongoing destroy calls.
        _pumpCts.Cancel();
        if (_cancelEvent != IntPtr.Zero)
        {
            SetEvent(_cancelEvent);
        }
        _pendingNotify.Set();
        _pumpThread?.Join(2000);

        // Flush by sending an EOS frame (per SDK §3402: encoded picture with
        // FLAG_EOS terminates the stream). Skipping for Phase 2 minimal —
        // the destroy call below cleans up internal state.

        if (_slots is not null)
        {
            foreach (var slot in _slots)
            {
                if (slot is null)
                {
                    continue;
                }
                if (slot.Mapped != IntPtr.Zero)
                {
                    _unmapInput?.Invoke(_encoder, slot.Mapped);
                }
                if (slot.Event != IntPtr.Zero)
                {
                    var ep = default(NV_ENC_EVENT_PARAMS);
                    ep.version = NvencApi.NV_ENC_EVENT_PARAMS_VER;
                    ep.completionEvent = slot.Event;
                    _unregisterEvent?.Invoke(_encoder, &ep);
                    CloseHandle(slot.Event);
                }
                if (slot.Bitstream != IntPtr.Zero)
                {
                    _destroyBitstream?.Invoke(_encoder, slot.Bitstream);
                }
                if (slot.Registered != IntPtr.Zero)
                {
                    _unregisterResource?.Invoke(_encoder, slot.Registered);
                }
                slot.Texture?.Dispose();
            }
        }

        if (_encoder != IntPtr.Zero)
        {
            _destroy?.Invoke(_encoder);
            _encoder = IntPtr.Zero;
        }
        try { _textureScaler?.Dispose(); } catch { }
        _textureScaler = null;
        if (_cancelEvent != IntPtr.Zero)
        {
            CloseHandle(_cancelEvent);
        }
        _pumpCts.Dispose();
        _pendingNotify.Dispose();
        _freeSlots?.Dispose();
        DebugLog.Write("[nvenc] H264 encoder disposed");
    }

    // Win32 plumbing. NVENC's async mode wants Win32 event handles; .NET's
    // ManualResetEvent doesn't expose a usable HANDLE for native code.
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateEventW(IntPtr eventAttributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, IntPtr name);

    [DllImport("kernel32", SetLastError = true)]
    private static extern uint WaitForMultipleObjects(uint count, IntPtr* handles, [MarshalAs(UnmanagedType.Bool)] bool waitAll, uint milliseconds);

    [DllImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ResetEvent(IntPtr handle);

    [DllImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetEvent(IntPtr handle);

    [DllImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
