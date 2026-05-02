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
    private int _consecutiveZeroOutputDecodes;
    // After this many consecutive Decode() calls with non-empty input
    // produce zero output, raise KeyframeNeeded. MFT silently consumes
    // corrupted bitstream by returning NEED_MORE_INPUT from ProcessOutput
    // forever — no error code we can classify, so we have to detect it
    // by observation. Threshold sized for ~100ms at 60fps to filter out
    // legitimate "decoder is just buffering an early sequence" cases.
    private const int ZeroOutputDecodesBeforeKeyframeRequest = 6;

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
    /// Raised when the MFT enters a state we can't recover from in-place
    /// (TYPE_NOT_SET that re-negotiation didn't fix, or any other non-
    /// STREAM_CHANGE / non-NEED_MORE_INPUT failure HRESULT). The receiver
    /// listens for this and sends RTCP PLI upstream so the publisher
    /// emits an IDR to reset the decoder.
    /// </summary>
    public event Action? KeyframeNeeded;

    private void RaiseKeyframeNeeded(string reason)
    {
        DebugLog.Write($"[mf-h264] keyframe-needed: {reason}");
        try { KeyframeNeeded?.Invoke(); } catch { /* listener exceptions must not crash decoder */ }
    }

    public MediaFoundationH264Decoder() : this(externalDevice: null)
    {
    }

    public MediaFoundationH264Decoder(ID3D11Device? externalDevice)
    {
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

    public VideoCodec Codec => VideoCodec.H264;

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
            DebugLog.Write("[mf] H264 decoder flushed");
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[mf] H264 decoder flush threw: {ex.Message}");
        }
    }

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

        var output = DrainOutput();
        if (output.Count == 0)
        {
            // Non-empty input produced no output. Could be the decoder
            // legitimately buffering up to its first keyframe, OR
            // silently consuming a corrupted bitstream. After enough
            // consecutive zero-output calls, treat as the latter and
            // ask upstream for a fresh IDR — receiver-side debounce
            // ensures we don't spam.
            _consecutiveZeroOutputDecodes++;
            if (_consecutiveZeroOutputDecodes == ZeroOutputDecodesBeforeKeyframeRequest)
            {
                RaiseKeyframeNeeded($"{_consecutiveZeroOutputDecodes} consecutive Decode() calls produced no output");
            }
        }
        else
        {
            _consecutiveZeroOutputDecodes = 0;
        }
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
                        DebugLog.Write("[mf] H264 decoder ProcessOutput stuck with TYPE_NOT_SET; decoder output format could not be negotiated");
                    }
                    // Wedged — only a fresh IDR can lift this, since the
                    // decoder won't accept input until it has an output
                    // type and we couldn't negotiate one from the bitstream
                    // we've seen so far.
                    RaiseKeyframeNeeded("ProcessOutput TYPE_NOT_SET");
                    return (IReadOnlyList<DecodedVideoFrame>?)results ?? Array.Empty<DecodedVideoFrame>();
                }

                if (result.Failure)
                {
                    DebugLog.Write($"[mf] H264 decoder ProcessOutput failed HRESULT 0x{(uint)result.Code:X8}");
                    RaiseKeyframeNeeded($"ProcessOutput HRESULT 0x{(uint)result.Code:X8}");
                    return (IReadOnlyList<DecodedVideoFrame>?)results ?? Array.Empty<DecodedVideoFrame>();
                }

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
                        // Per-subscriber AddRef: the sink's
                        // `using var _ = new ID3D11Texture2D(ptr)` pattern
                        // doesn't AddRef on construction (probed directly)
                        // but does Release on dispose, so without an AddRef
                        // per subscriber we'd drain refs off _bgraDest.
                        // D3D11's deferred destruction hides the damage in
                        // simple runs, but under real load the underlying
                        // resource can be reclaimed between frames.
                        var targets = gpuSink.GetInvocationList();
                        foreach (var target in targets)
                        {
                            _bgraDest!.AddRef();
                            try
                            {
                                ((Action<IntPtr, int, int, TimeSpan>)target).Invoke(
                                    _bgraDest.NativePointer, _width, _height, outTs);
                            }
                            catch (Exception ex)
                            {
                                DebugLog.Write($"[mf] H264 GpuOutputHandler threw: {ex.Message}");
                                try { _bgraDest.Release(); } catch { }
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
            _scaler!.Process(sourceTexture, _bgraDest!, arraySlice);
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

            if (outputType is null)
            {
                return;
            }

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
            GuidSubtype = VideoFormatGuids.H264,
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
