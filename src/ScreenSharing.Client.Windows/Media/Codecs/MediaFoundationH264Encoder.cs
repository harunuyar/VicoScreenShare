using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using ScreenSharing.Client.Diagnostics;
using ScreenSharing.Client.Media.Codecs;
using Vortice.MediaFoundation;
using IVideoEncoder = ScreenSharing.Client.Media.Codecs.IVideoEncoder;

namespace ScreenSharing.Client.Windows.Media.Codecs;

/// <summary>
/// H.264 encoder wrapping a Media Foundation <see cref="IMFTransform"/>.
///
/// Modern Windows hardware encoders (NVENC on NVIDIA, QSV on Intel, VCE on
/// AMD) are exposed as ASYNC MFTs: instead of the push-pull sync pattern
/// (ProcessInput, then loop ProcessOutput until need-more-input), they emit
/// <c>METransformNeedInput</c> and <c>METransformHaveOutput</c> events and
/// you drive ProcessInput / ProcessOutput in response to those events.
///
/// This class supports both modes:
///  - Async MFTs run a background event pump that translates the event
///    stream into a <see cref="SemaphoreSlim"/> of "needs input" credits
///    and a <see cref="ConcurrentQueue{T}"/> of encoded output bytes. The
///    caller's <see cref="EncodeI420"/> pushes one frame per call, credits
///    permitting, and drains whatever output has landed — from the
///    caller's perspective it still looks like a synchronous encoder.
///  - Sync MFTs (Microsoft's software H264 encoder and some older drivers)
///    drain output inline after each ProcessInput, same as before.
///
/// Probe order inside the Media Foundation category is:
///   1. hardware async (NVENC / QSV / VCE) — preferred
///   2. hardware sync — older driver variants
///   3. software sync (MSFT MSH264EncoderMFT) — universal fallback
/// </summary>
internal sealed unsafe class MediaFoundationH264Encoder : IVideoEncoder
{
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly long _bitrate;
    private readonly IMFTransform _transform;
    private readonly bool _isAsync;
    private readonly IMFMediaEventGenerator? _events;
    private readonly Thread? _pumpThread;
    private readonly CancellationTokenSource _pumpCts = new();
    private readonly SemaphoreSlim _needInputSignal = new(0, int.MaxValue);
    private readonly ConcurrentQueue<byte[]> _outputQueue = new();
    private readonly object _processLock = new();
    private readonly byte[] _nv12Buffer;
    private long _frameIndex;
    private long _loggedEncodedFrames;
    private long _loggedProcessInputs;
    private long _loggedTimeouts;
    private int _loggedTimingFrames;
    private bool _disposed;

    public MediaFoundationH264Encoder(int width, int height, int fps, long bitrate)
    {
        _width = width;
        _height = height;
        _fps = Math.Max(1, fps);
        _bitrate = Math.Max(500_000, bitrate);
        _nv12Buffer = new byte[width * height * 3 / 2];

        (_transform, _isAsync, var pickedLabel) = CreateEncoder(width, height, _fps, _bitrate);
        DebugLog.Write($"[mf] H264 encoder initialized {width}x{height}@{_fps} {_bitrate} bps ({pickedLabel})");

        if (_isAsync)
        {
            _events = _transform.QueryInterface<IMFMediaEventGenerator>();
            _pumpThread = new Thread(EventPumpLoop)
            {
                IsBackground = true,
                Name = "MF H264 event pump",
            };
            // Start the pump BEFORE we send the streaming messages so no
            // initial NeedInput event races past an unattached queue.
            _pumpThread.Start();
        }

        // NOTIFY_BEGIN_STREAMING allocates native resources (hw context,
        // encoder buffers). Async MFTs additionally require NOTIFY_START_OF_STREAM
        // to actually start emitting METransformNeedInput events — without
        // it, NVENC sits idle and the event pump never gets a credit to
        // release back to EncodeI420.
        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);
        if (_isAsync)
        {
            _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, 0);
        }
    }

    public VideoCodec Codec => VideoCodec.H264;

    public int Width => _width;

    public int Height => _height;

    public byte[]? EncodeBgra(byte[] bgra, int stride)
    {
        if (_disposed) return null;

        var timingStart = _loggedTimingFrames < 10 ? Stopwatch.GetTimestamp() : 0L;

        // One-pass BGRA -> NV12 straight into the encoder's NV12 buffer.
        // No I420 intermediate, no extra memory pass. Unsafe pointer loop,
        // NVENC then ingests NV12 natively with no further conversion.
        BgraToNv12Fast(bgra, stride, _nv12Buffer, _width, _height);

        var convertEnd = timingStart != 0 ? Stopwatch.GetTimestamp() : 0L;

        var encoded = _isAsync ? EncodeAsync() : EncodeSync();

        if (timingStart != 0)
        {
            _loggedTimingFrames++;
            var totalEnd = Stopwatch.GetTimestamp();
            var ticksPerMs = Stopwatch.Frequency / 1000.0;
            var convertMs = (convertEnd - timingStart) / ticksPerMs;
            var encodeMs = (totalEnd - convertEnd) / ticksPerMs;
            var totalMs = (totalEnd - timingStart) / ticksPerMs;
            DebugLog.Write($"[mf-timing #{_loggedTimingFrames}] bgra->nv12={convertMs:F1}ms encode={encodeMs:F1}ms total={totalMs:F1}ms");
        }

        if (encoded is { Length: > 0 } && _loggedEncodedFrames < 3)
        {
            _loggedEncodedFrames++;
            DebugLog.Write($"[mf] H264 encoder produced frame {_loggedEncodedFrames} ({encoded.Length} bytes)");
        }
        return encoded;
    }

    private byte[]? EncodeAsync()
    {
        // Wait briefly for a NeedInput credit. If the encoder is temporarily
        // saturated, skip feeding this frame and just drain any output that
        // the pump already queued — the capture stream keeps flowing at the
        // lower effective rate rather than blocking the capture thread.
        if (!_needInputSignal.Wait(50))
        {
            if (_loggedTimeouts < 3)
            {
                _loggedTimeouts++;
                DebugLog.Write($"[mf] async EncodeAsync timeout #{_loggedTimeouts} — no NeedInput credit within 50ms");
            }
            return TryDequeueOutput();
        }

        var sample = CreateInputSample();
        try
        {
            lock (_processLock)
            {
                if (_disposed) return null;
                try
                {
                    _transform.ProcessInput(0, sample, 0);
                    if (_loggedProcessInputs < 3)
                    {
                        _loggedProcessInputs++;
                        DebugLog.Write($"[mf] async ProcessInput #{_loggedProcessInputs} ok");
                    }
                }
                catch (Exception ex)
                {
                    DebugLog.Write($"[mf] async H264 ProcessInput threw: {ex.Message}");
                }
            }
        }
        finally
        {
            sample.Dispose();
        }

        return TryDequeueOutput();
    }

    private byte[]? EncodeSync()
    {
        var sample = CreateInputSample();
        try
        {
            lock (_processLock)
            {
                if (_disposed) return null;
                try
                {
                    _transform.ProcessInput(0, sample, 0);
                }
                catch (Exception ex)
                {
                    DebugLog.Write($"[mf] sync H264 ProcessInput threw: {ex.Message}");
                    return null;
                }
            }
        }
        finally
        {
            sample.Dispose();
        }

        return DrainOutputLocked();
    }

    private IMFSample CreateInputSample()
    {
        var buffer = MediaFactory.MFCreateMemoryBuffer(_nv12Buffer.Length);
        buffer.Lock(out nint ptr, out int maxLen, out _);
        try
        {
            fixed (byte* srcPtr = _nv12Buffer)
            {
                Buffer.MemoryCopy(srcPtr, ptr.ToPointer(), maxLen, _nv12Buffer.Length);
            }
        }
        finally
        {
            buffer.Unlock();
        }
        buffer.CurrentLength = _nv12Buffer.Length;

        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);

        var duration = 10_000_000L / _fps;
        sample.SampleTime = _frameIndex * duration;
        sample.SampleDuration = duration;
        _frameIndex++;

        buffer.Dispose();
        return sample;
    }

    private byte[]? TryDequeueOutput()
    {
        if (_outputQueue.TryDequeue(out var bytes)) return bytes;
        return null;
    }

    /// <summary>
    /// Sync-mode output drain: loop <see cref="IMFTransform.ProcessOutput"/>
    /// until the MFT says it needs more input. The encode lock is held by
    /// the caller.
    /// </summary>
    private byte[]? DrainOutputLocked()
    {
        List<byte>? accumulator = null;

        while (true)
        {
            var output = PullSingleOutput(out var result);
            if (result is DrainResult.NeedMoreInput or DrainResult.Failure)
            {
                return accumulator?.ToArray();
            }
            if (output is null) continue;
            (accumulator ??= new List<byte>()).AddRange(output);
        }
    }

    private enum DrainResult { Success, NeedMoreInput, Failure }

    // MFT_OUTPUT_STREAM_INFO_FLAGS bit: MFT allocates its own output samples
    // and requires the client to pass null in OutputDataBuffer.Sample.
    // NVENC and most hardware encoder MFTs set this flag.
    private const int MftOutputStreamProvidesSamples = 0x100;

    /// <summary>
    /// Pull one encoded sample from the MFT. Handles both the "client
    /// provides output sample" path (software MFTs) and the "MFT provides
    /// output sample" path (NVENC / most hardware MFTs) by checking the
    /// PROVIDES_SAMPLES flag on the output stream.
    /// </summary>
    private byte[]? PullSingleOutput(out DrainResult result)
    {
        var streamInfo = _transform.GetOutputStreamInfo(0);
        var mftProvidesSamples = (streamInfo.Flags & MftOutputStreamProvidesSamples) != 0;

        IMFSample? clientSample = null;
        IMFMediaBuffer? clientBuffer = null;
        if (!mftProvidesSamples)
        {
            var bufferSize = streamInfo.Size <= 0 ? (_width * _height) : streamInfo.Size;
            clientSample = MediaFactory.MFCreateSample();
            clientBuffer = MediaFactory.MFCreateMemoryBuffer(bufferSize);
            clientSample.AddBuffer(clientBuffer);
        }

        var db = new OutputDataBuffer
        {
            StreamID = 0,
            Sample = clientSample, // null when the MFT allocates for us
        };

        var hr = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref db, out _);

        IMFSample? readSample = null;
        try
        {
            if ((uint)hr.Code == 0xC00D6D72u) // MF_E_TRANSFORM_NEED_MORE_INPUT
            {
                result = DrainResult.NeedMoreInput;
                return null;
            }

            if (hr.Failure)
            {
                DebugLog.Write($"[mf] H264 ProcessOutput failed HRESULT 0x{(uint)hr.Code:X8}");
                result = DrainResult.Failure;
                return null;
            }

            // In both paths db.Sample points to the sample we should read
            // from. In the client-provided path it's the same object we
            // allocated; in the MFT-provided path it's a sample the MFT
            // filled in on our behalf and transferred ownership of.
            readSample = db.Sample;
            if (readSample is null)
            {
                DebugLog.Write("[mf] ProcessOutput returned success but db.Sample is null");
                result = DrainResult.Failure;
                return null;
            }

            // ConvertToContiguousBuffer flattens multi-buffer samples into a
            // single IMFMediaBuffer, which is the safe way to read encoded
            // output regardless of how the MFT laid the sample out.
            var flatBuffer = readSample.ConvertToContiguousBuffer();
            try
            {
                flatBuffer.Lock(out nint framePtr, out _, out int curLen);
                try
                {
                    if (curLen <= 0)
                    {
                        result = DrainResult.Success;
                        return null;
                    }
                    var bytes = new byte[curLen];
                    Marshal.Copy(framePtr, bytes, 0, curLen);
                    result = DrainResult.Success;
                    return bytes;
                }
                finally
                {
                    flatBuffer.Unlock();
                }
            }
            finally
            {
                flatBuffer.Dispose();
            }
        }
        finally
        {
            // Always release the sample we read from. Disposing our own
            // clientSample and the MFT-provided readSample are both the
            // right action. If they happen to be the same reference,
            // disposing twice via the Vortice ComObject wrapper is
            // harmless — the ref count underneath decrements once per
            // logical wrapper.
            if (readSample is not null && !ReferenceEquals(readSample, clientSample))
            {
                try { readSample.Dispose(); } catch { }
            }
            clientSample?.Dispose();
            clientBuffer?.Dispose();
        }
    }

    /// <summary>
    /// Background event pump for async MFTs. Translates NeedInput / HaveOutput
    /// events into semaphore releases and output enqueues. Runs until the
    /// cancellation token is signaled on <see cref="Dispose"/>.
    /// </summary>
    private void EventPumpLoop()
    {
        DebugLog.Write("[mf] async event pump thread started");
        var loggedEvents = 0;
        try
        {
            while (!_pumpCts.IsCancellationRequested)
            {
                IMFMediaEvent? evt;
                try
                {
                    evt = _events!.GetEvent(0); // blocking
                }
                catch (Exception ex)
                {
                    DebugLog.Write($"[mf] async event pump GetEvent threw: {ex.GetType().Name}: {ex.Message}");
                    return;
                }
                if (evt is null)
                {
                    DebugLog.Write("[mf] async event pump GetEvent returned null");
                    continue;
                }

                try
                {
                    var type = (MediaEventTypes)evt.EventType;
                    if (loggedEvents < 8)
                    {
                        loggedEvents++;
                        DebugLog.Write($"[mf] async event #{loggedEvents}: {type}");
                    }

                    if (type == MediaEventTypes.TransformNeedInput)
                    {
                        _needInputSignal.Release();
                    }
                    else if (type == MediaEventTypes.TransformHaveOutput)
                    {
                        byte[]? output;
                        lock (_processLock)
                        {
                            if (_disposed) return;
                            output = PullSingleOutput(out var result);
                            if (result == DrainResult.Failure)
                            {
                                DebugLog.Write("[mf] async pump PullSingleOutput returned Failure");
                            }
                        }
                        if (output is { Length: > 0 })
                        {
                            _outputQueue.Enqueue(output);
                        }
                        else if (loggedEvents <= 8)
                        {
                            DebugLog.Write($"[mf] async pump HaveOutput produced {output?.Length ?? -1} bytes");
                        }
                    }
                    else if (type == MediaEventTypes.TransformDrainComplete)
                    {
                        DebugLog.Write("[mf] async event pump drain complete, exiting");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    DebugLog.Write($"[mf] async event pump handler exception: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    evt.Dispose();
                }
            }
        }
        finally
        {
            DebugLog.Write("[mf] async event pump thread exiting");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _pumpCts.Cancel(); } catch { }
        try { _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, 0); } catch { }
        // The pump thread exits when GetEvent unblocks or the MFT is disposed;
        // give it a moment but don't hang forever on shutdown.
        _pumpThread?.Join(TimeSpan.FromMilliseconds(500));
        try { _events?.Dispose(); } catch { }
        try { _transform.Dispose(); } catch { }
        try { _pumpCts.Dispose(); } catch { }
        try { _needInputSignal.Dispose(); } catch { }
    }

    // --- Encoder enumeration + type negotiation ---

    private static (IMFTransform transform, bool isAsync, string label) CreateEncoder(int width, int height, int fps, long bitrate)
    {
        // Try async hardware MFTs first — NVENC / QSV / VCE. Passing both
        // HARDWARE and ASYNCMFT flags returns async hardware encoders, which
        // we unlock individually before touching them.
        var async = TryCreateEncoder(
            (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagAsyncmft | EnumFlag.EnumFlagSortandfilter),
            width, height, fps, bitrate, tryAsync: true, tryHardware: true, label: "hardware async");
        if (async is not null) return (async, true, "hardware async");

        // Fall back to sync hardware MFTs — rare on modern drivers but they
        // exist (e.g. older Intel QSV builds).
        var sync = TryCreateEncoder(
            (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagSortandfilter),
            width, height, fps, bitrate, tryAsync: false, tryHardware: true, label: "hardware sync");
        if (sync is not null) return (sync, false, "hardware sync");

        // Last resort: Microsoft's software H.264 encoder. Always available.
        var software = TryCreateEncoder(
            (uint)(EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagSortandfilter),
            width, height, fps, bitrate, tryAsync: false, tryHardware: false, label: "software");
        if (software is not null) return (software, false, "software");

        throw new InvalidOperationException("Media Foundation has no usable H.264 encoder on this machine");
    }

    private static IMFTransform? TryCreateEncoder(
        uint flags,
        int width,
        int height,
        int fps,
        long bitrate,
        bool tryAsync,
        bool tryHardware,
        string label)
    {
        var outputFilter = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = VideoFormatGuids.H264,
        };

        using var collection = MediaFactory.MFTEnumEx(
            TransformCategoryGuids.VideoEncoder,
            flags,
            inputType: null,
            outputType: outputFilter);

        foreach (var activate in collection)
        {
            IMFTransform? transform = null;
            // Read friendly name + hardware URL BEFORE activating so we can
            // log which specific transform won the probe — otherwise NVENC
            // and Microsoft's software async encoder look identical in logs.
            var friendlyName = TryGetFriendlyName(activate);
            var hardwareUrl = TryGetHardwareUrl(activate);
            DebugLog.Write($"[mf] probing {label} H264 MFT candidate: {friendlyName} ({hardwareUrl})");
            try
            {
                transform = activate.ActivateObject<IMFTransform>();

                // Unlock async MFTs before any type negotiation. Without this
                // SetInputType / SetOutputType fail with MF_E_TRANSFORM_ASYNC_LOCKED.
                if (tryAsync)
                {
                    try
                    {
                        transform.Attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
                    }
                    catch (Exception ex)
                    {
                        DebugLog.Write($"[mf] probing {label} [{friendlyName}] — async unlock failed, skipping: {ex.Message}");
                        transform.Dispose();
                        continue;
                    }
                }

                // Low-latency + CBR + target bitrate, written to the transform
                // attributes as CODECAPI properties. Must happen BEFORE the
                // type negotiation because NVENC snapshots its rate control
                // config at the point of SetOutputType. Without LowLatency=1
                // NVENC uses its quality preset with B-frames and lookahead,
                // which adds 2-3 seconds of encode latency — unusable for
                // realtime streaming.
                ApplyCodecApiAttributes(transform, bitrate, friendlyName);

                if (!TrySetTypes(transform, width, height, fps, bitrate, $"{label} [{friendlyName}]"))
                {
                    transform.Dispose();
                    continue;
                }

                DebugLog.Write($"[mf] picked {label} H264 MFT: {friendlyName} ({hardwareUrl})");
                return transform;
            }
            catch (Exception ex)
            {
                DebugLog.Write($"[mf] probing {label} H264 MFT — activate threw, skipping: {ex.Message}");
                transform?.Dispose();
            }
        }

        return null;
    }

    private static bool TrySetTypes(IMFTransform transform, int width, int height, int fps, long bitrate, string label)
    {
        var outputType = MediaFactory.MFCreateMediaType();
        outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        outputType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate);
        outputType.SetEnumValue(MediaTypeAttributeKeys.InterlaceMode, VideoInterlaceMode.Progressive);
        MediaFactory.MFSetAttributeSize(outputType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
        MediaFactory.MFSetAttributeRatio(outputType, MediaTypeAttributeKeys.FrameRate, (uint)fps, 1u);
        MediaFactory.MFSetAttributeRatio(outputType, MediaTypeAttributeKeys.PixelAspectRatio, 1u, 1u);

        try
        {
            transform.SetOutputType(0, outputType, 0);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[mf] probing {label} H264 MFT — SetOutputType rejected, skipping: {ex.Message}");
            outputType.Dispose();
            return false;
        }
        outputType.Dispose();

        var inputType = MediaFactory.MFCreateMediaType();
        inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
        inputType.SetEnumValue(MediaTypeAttributeKeys.InterlaceMode, VideoInterlaceMode.Progressive);
        MediaFactory.MFSetAttributeSize(inputType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
        MediaFactory.MFSetAttributeRatio(inputType, MediaTypeAttributeKeys.FrameRate, (uint)fps, 1u);
        MediaFactory.MFSetAttributeRatio(inputType, MediaTypeAttributeKeys.PixelAspectRatio, 1u, 1u);

        try
        {
            transform.SetInputType(0, inputType, 0);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[mf] probing {label} H264 MFT — SetInputType rejected, skipping: {ex.Message}");
            inputType.Dispose();
            return false;
        }
        inputType.Dispose();

        return true;
    }

    // ComCast removed: use transform.QueryInterface<IMFMediaEventGenerator>()
    // directly — SharpGen's base ComObject exposes the right COM QI call so
    // the returned wrapper holds an interface-specific pointer, not the
    // IMFTransform vtable reinterpreted through the wrong slots. Using the
    // wrong vtable was crashing EventPumpLoop with ExecutionEngineException
    // the moment we called GetEvent on it.

    // CODECAPI GUIDs from codecapi.h. These are standardized attribute keys
    // that hardware encoder MFTs (including NVENC) honor via their transform
    // attribute store.
    private static readonly Guid CodecApiAVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    private static readonly Guid CodecApiAVEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e1b1083");
    private static readonly Guid CodecApiAVEncCommonLowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid CodecApiAVEncMPVGOPSize = new("95f31b26-95a4-4f58-9ba5-4c1eb9e1f04b");
    private const uint RateControlModeCbr = 0;

    /// <summary>
    /// Writes realtime-streaming CODECAPI attributes onto the encoder
    /// transform. All of these are advisory — if the MFT doesn't recognize
    /// a particular key, <see cref="IMFAttributes.Set(Guid, uint)"/> either
    /// silently succeeds or throws, which we swallow. NVENC is the main
    /// consumer of LowLatency; the Microsoft software encoder also honors
    /// MeanBitRate and RateControlMode.
    /// </summary>
    private static void ApplyCodecApiAttributes(IMFTransform transform, long bitrate, string friendlyName)
    {
        void TrySet(Guid key, uint value, string name)
        {
            try { transform.Attributes.Set(key, value); }
            catch (Exception ex)
            {
                DebugLog.Write($"[mf] {friendlyName} CODECAPI {name} rejected: {ex.Message}");
            }
        }

        TrySet(CodecApiAVEncCommonLowLatency, 1u, "LowLatency");
        TrySet(CodecApiAVEncCommonRateControlMode, RateControlModeCbr, "RateControlMode=CBR");
        TrySet(CodecApiAVEncCommonMeanBitRate, (uint)Math.Min(bitrate, uint.MaxValue), "MeanBitRate");
        // 2-second GOP is the WebRTC convention — small enough that a new
        // viewer joining mid-stream gets a keyframe quickly, large enough
        // that we don't waste bitrate on redundant IDR frames.
        TrySet(CodecApiAVEncMPVGOPSize, 120u, "GopSize");
    }

    private static string TryGetFriendlyName(IMFActivate activate)
    {
        // IMFAttributes.FriendlyName reads CaptureDeviceAttributeKeys.FriendlyName
        // which only applies to video capture devices, NOT MFT transforms. For
        // MFTs the key is MftFriendlyNameAttribute.
        try
        {
            var name = activate.GetString(TransformAttributeKeys.MftFriendlyNameAttribute);
            return !string.IsNullOrEmpty(name) ? name : "(no name)";
        }
        catch
        {
            return "(unknown)";
        }
    }

    private static string TryGetHardwareUrl(IMFActivate activate)
    {
        try
        {
            var url = activate.GetString(TransformAttributeKeys.MftEnumHardwareUrlAttribute);
            return string.IsNullOrEmpty(url) ? "<software>" : url;
        }
        catch
        {
            return "<software>";
        }
    }

    /// <summary>
    /// Single-pass BGRA -> NV12 conversion with BT.601 full-range
    /// coefficients, unsafe pointer arithmetic, and 2x2 chroma averaging
    /// inlined with the Y plane computation so every source pixel is read
    /// exactly once. Processes pairs of rows at a time (the 2x2 chroma
    /// block) so we can write U/V next to the Y values they were derived
    /// from.
    ///
    /// Both <paramref name="width"/> and <paramref name="height"/> are
    /// assumed even (CaptureStreamer masks the source dims to even before
    /// calling). NV12 buffer layout: Y plane (w*h) followed by interleaved
    /// UV plane (w*h/2).
    /// </summary>
    private static unsafe void BgraToNv12Fast(byte[] bgra, int stride, byte[] nv12, int width, int height)
    {
        const int YR = 19595, YG = 38470, YB = 7471;              // 0.299/0.587/0.114 << 16
        const int UR = -11059, UG = -21709, UB = 32768;           // -0.169/-0.331/0.5
        const int VR = 32768, VG = -27439, VB = -5329;            //  0.5/-0.419/-0.081
        const int YRound = 32768;                                  // + 0.5 for Y
        const int UvRound = 128 * 65536 + 32768;                  // + 128 shifted + 0.5 for chroma

        var ySize = width * height;

        fixed (byte* srcBase = bgra)
        fixed (byte* dstBase = nv12)
        {
            var uvOut = dstBase + ySize;

            for (var y = 0; y < height; y += 2)
            {
                var src0 = srcBase + y * stride;
                var src1 = srcBase + (y + 1) * stride;
                var yOut0 = dstBase + y * width;
                var yOut1 = dstBase + (y + 1) * width;

                for (var x = 0; x < width; x += 2)
                {
                    // Four BGRA pixels per 2x2 block. Each pixel = 4 bytes:
                    // B=0, G=1, R=2, A=3.
                    int b00 = src0[0], g00 = src0[1], r00 = src0[2];
                    int b01 = src0[4], g01 = src0[5], r01 = src0[6];
                    int b10 = src1[0], g10 = src1[1], r10 = src1[2];
                    int b11 = src1[4], g11 = src1[5], r11 = src1[6];

                    // Y for each pixel: clamped 16.16 fixed-point BT.601.
                    var y00 = (YR * r00 + YG * g00 + YB * b00 + YRound) >> 16;
                    var y01 = (YR * r01 + YG * g01 + YB * b01 + YRound) >> 16;
                    var y10 = (YR * r10 + YG * g10 + YB * b10 + YRound) >> 16;
                    var y11 = (YR * r11 + YG * g11 + YB * b11 + YRound) >> 16;
                    yOut0[0] = (byte)(y00 < 0 ? 0 : y00 > 255 ? 255 : y00);
                    yOut0[1] = (byte)(y01 < 0 ? 0 : y01 > 255 ? 255 : y01);
                    yOut1[0] = (byte)(y10 < 0 ? 0 : y10 > 255 ? 255 : y10);
                    yOut1[1] = (byte)(y11 < 0 ? 0 : y11 > 255 ? 255 : y11);

                    // Chroma on the averaged 2x2 block. Sum then shift >> 2
                    // is cheaper than dividing each channel separately.
                    var sR = r00 + r01 + r10 + r11;
                    var sG = g00 + g01 + g10 + g11;
                    var sB = b00 + b01 + b10 + b11;

                    // UR/UG/UB coefficients are still in 16.16 but we sum
                    // 4 pixels, so the natural >> 18 gives the average.
                    var u = (UR * sR + UG * sG + UB * sB + (UvRound << 2)) >> 18;
                    var v = (VR * sR + VG * sG + VB * sB + (UvRound << 2)) >> 18;
                    uvOut[0] = (byte)(u < 0 ? 0 : u > 255 ? 255 : u);
                    uvOut[1] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

                    src0 += 8; src1 += 8;
                    yOut0 += 2; yOut1 += 2;
                    uvOut += 2;
                }
            }
        }
    }
}
