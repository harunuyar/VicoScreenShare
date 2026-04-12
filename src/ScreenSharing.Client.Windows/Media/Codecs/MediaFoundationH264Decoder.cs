using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ScreenSharing.Client.Diagnostics;
using ScreenSharing.Client.Media.Codecs;
using Vortice.MediaFoundation;

namespace ScreenSharing.Client.Windows.Media.Codecs;

/// <summary>
/// H.264 decoder on top of Media Foundation's H.264 decoder MFT. Hardware
/// DXVA decoder preferred, software fallback. Output is NV12 which we
/// convert to packed BGRA for the renderer. Frame dimensions are learned
/// from the first <c>MF_E_TRANSFORM_STREAM_CHANGE</c> the decoder raises
/// once it has parsed the stream's SPS.
/// </summary>
internal sealed unsafe class MediaFoundationH264Decoder : IVideoDecoder
{
    private const uint MF_E_TRANSFORM_STREAM_CHANGE = 0xC00D6D61u;
    private const uint MF_E_TRANSFORM_NEED_MORE_INPUT = 0xC00D6D72u;
    private const uint MF_E_TRANSFORM_TYPE_NOT_SET = 0xC00D6D60u;
    private bool _warnedTypeNotSet;

    private readonly IMFTransform _transform;
    private int _width;
    private int _height;
    private bool _outputTypeNegotiated;
    private bool _disposed;

    public MediaFoundationH264Decoder()
    {
        _transform = CreateDecoder()
                     ?? throw new InvalidOperationException("No H.264 decoder MFT is available from Media Foundation");

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

    public VideoCodec Codec => VideoCodec.H264;

    public IReadOnlyList<DecodedVideoFrame> Decode(byte[] encodedSample)
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

        return DrainOutput();
    }

    private IReadOnlyList<DecodedVideoFrame> DrainOutput()
    {
        List<DecodedVideoFrame>? results = null;

        while (true)
        {
            var streamInfo = _transform.GetOutputStreamInfo(0);
            var bufferSize = streamInfo.Size == 0 ? (_width * _height * 3 / 2) : streamInfo.Size;
            if (bufferSize <= 0) bufferSize = 1;

            var outSample = MediaFactory.MFCreateSample();
            var outBuffer = MediaFactory.MFCreateMemoryBuffer(bufferSize);
            outSample.AddBuffer(outBuffer);

            var db = new OutputDataBuffer
            {
                StreamID = 0,
                Sample = outSample,
            };
            var result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref db, out _);

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
                    // We ended up here without a negotiated output type —
                    // try once, log if it still fails, and stop draining
                    // so we don't spam the log on every frame.
                    if (!_outputTypeNegotiated)
                    {
                        NegotiateOutputType();
                        if (_outputTypeNegotiated) continue;
                    }
                    if (!_warnedTypeNotSet)
                    {
                        _warnedTypeNotSet = true;
                        DebugLog.Write("[mf] H264 decoder ProcessOutput stuck with TYPE_NOT_SET; decoder output format could not be negotiated");
                    }
                    return (IReadOnlyList<DecodedVideoFrame>?)results ?? Array.Empty<DecodedVideoFrame>();
                }

                if (result.Failure)
                {
                    DebugLog.Write($"[mf] H264 decoder ProcessOutput failed HRESULT 0x{(uint)result.Code:X8}");
                    return (IReadOnlyList<DecodedVideoFrame>?)results ?? Array.Empty<DecodedVideoFrame>();
                }

                if (!_outputTypeNegotiated || _width <= 0 || _height <= 0) continue;

                outBuffer.Lock(out nint framePtr, out _, out int curLen);
                byte[] nv12;
                try
                {
                    nv12 = new byte[curLen];
                    Marshal.Copy(framePtr, nv12, 0, curLen);
                }
                finally
                {
                    outBuffer.Unlock();
                }

                var bgra = ConvertNv12ToBgra(nv12, _width, _height);
                (results ??= new List<DecodedVideoFrame>()).Add(new DecodedVideoFrame(bgra, _width, _height));
            }
            finally
            {
                outSample.Dispose();
                outBuffer.Dispose();
            }
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

            if (outputType is null) return;

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
        // BT.601 YUV -> RGB, packed BGRA. Receiver side so correctness
        // matters more than speed; a vectorized version is a future concern.
        var bgra = new byte[width * height * 4];
        var yPlaneSize = width * height;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var Y = nv12[y * width + x];
                var uvRow = y / 2;
                var uvCol = (x / 2) * 2;
                var U = nv12[yPlaneSize + uvRow * width + uvCol + 0];
                var V = nv12[yPlaneSize + uvRow * width + uvCol + 1];

                var c = Y - 16;
                var d = U - 128;
                var e = V - 128;

                var r = Math.Clamp((298 * c + 409 * e + 128) >> 8, 0, 255);
                var g = Math.Clamp((298 * c - 100 * d - 208 * e + 128) >> 8, 0, 255);
                var b = Math.Clamp((298 * c + 516 * d + 128) >> 8, 0, 255);

                var dstIdx = (y * width + x) * 4;
                bgra[dstIdx + 0] = (byte)b;
                bgra[dstIdx + 1] = (byte)g;
                bgra[dstIdx + 2] = (byte)r;
                bgra[dstIdx + 3] = 0xFF;
            }
        }

        return bgra;
    }

    private static IMFTransform? CreateDecoder()
    {
        var inputFilter = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = VideoFormatGuids.H264,
        };
        var flags = (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagSortandfilter);
        var transform = TryCreateDecoder(flags, inputFilter, "hardware");
        if (transform is not null) return transform;

        flags = (uint)(EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagSortandfilter);
        return TryCreateDecoder(flags, inputFilter, "software");
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
        if (_disposed) return;
        _disposed = true;
        try { _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, 0); } catch { }
        try { _transform.Dispose(); } catch { }
    }
}
