namespace VicoScreenShare.Client.Media;

using System;
using System.Diagnostics;
using System.Threading;
using VicoScreenShare.Client.Diagnostics;

/// <summary>
/// Distinguishes the two RTP streams that share a single
/// <see cref="MediaClock"/>. Each has its own RTP clock rate and its
/// own RTCP Sender Report stream.
/// </summary>
public enum MediaKind
{
    Audio,
    Video,
}

/// <summary>
/// Per-subscriber-session shared clock that maps RTP timestamps from
/// audio and video onto a single local Stopwatch wall-clock line so
/// both streams stay in lip-sync regardless of any per-stream delay.
///
/// Mechanics:
/// 1. Each incoming RTCP Sender Report carries
///    (<c>NtpTimestamp</c>, <c>RtpTimestamp</c>) — the publisher's NTP
///    wall-clock at the instant of a known RTP packet. Audio and
///    video have separate SRs, separate clock rates (48 kHz / 90 kHz),
///    and separate starting RTP timestamps, but the same publisher
///    NTP origin.
/// 2. We store the most-recent SR for each stream. Combined, they let
///    us translate any RTP timestamp from either stream into publisher
///    NTP.
/// 3. The first decoded frame (typically video) latches a shared
///    anchor: <c>(localStopwatchAtPlayout, publisherNtpAtPlayout)</c>.
///    The local-stopwatch component is whatever wall time the caller
///    intends that first frame to play at — accounting for jitter
///    buffer + decode lag — so a query for any subsequent RTP
///    timestamp returns the matching local playout time on the same
///    line.
/// 4. <see cref="AudioReceiver"/> queries
///    <see cref="AudioRtpToLocalWallTicks"/> per decoded audio packet
///    and aligns its playout (silence-pad / sample-skip) to the
///    returned wall time. Video keeps its own
///    <see cref="Rendering.PaintLoop"/> anchor for v1; the first-paint
///    moment becomes <see cref="MediaClock"/>'s anchor.
///
/// Lock-in sequence: until both an SR is known for the queried kind
/// AND the shared anchor has been latched, queries return
/// <see langword="null"/> and callers fall back to their pre-sync
/// behaviour. SR arrival happens on SIPSorcery's RTCP timer (~5 s);
/// once both audio and video SRs land + the anchor is set the
/// streams stay locked thereafter.
/// </summary>
public sealed class MediaClock
{
    private readonly object _lock = new();
    private readonly string _logTag;

    // Latest audio Sender Report. Lets us translate an audio RTP
    // timestamp to publisher NTP using a 48 kHz clock-rate.
    private long _audioNtpTicksAtSr;
    private uint _audioRtpTsAtSr;
    private bool _haveAudioSr;

    // Same for video, 90 kHz.
    private long _videoNtpTicksAtSr;
    private uint _videoRtpTsAtSr;
    private bool _haveVideoSr;

    // Shared anchor: at this local-Stopwatch tick, the publisher's NTP
    // (TimeSpan ticks since 1900-01-01) was this. From here we walk
    // either direction along the publisher's NTP line.
    private long _localAnchorTicks;
    private long _publisherNtpAnchorTicks;
    private bool _haveAnchor;

    // Paint-anchor (independent of any SR). Set by PaintLoop on
    // every (re-)anchor moment using just the local Stopwatch tick
    // and the per-stream video content time the queue handed us. No
    // RTCP Sender Report required — AudioReceiver uses it as a
    // fallback when SR-based translation can't yet produce a
    // wall-clock target. Cleared / overwritten on each PaintLoop
    // anchor event.
    private long _paintAnchorLocalTicks;
    private TimeSpan _paintAnchorVideoContentTime;
    private bool _havePaintAnchor;

    public MediaClock(string logTag = "")
    {
        _logTag = logTag ?? string.Empty;
    }

    /// <summary>
    /// Audio Opus's RTP clock rate in Hz. Always 48 kHz for WebRTC
    /// Opus. Exposed as a constant so the per-stream divisor is
    /// easy to grep for.
    /// </summary>
    public const int AudioRtpClockRate = 48_000;

    /// <summary>Video RTP clock rate in Hz (RFC 6184 §8.2.1).</summary>
    public const int VideoRtpClockRate = 90_000;

    /// <summary>
    /// Record an audio Sender Report. NTP and RTP timestamps come
    /// straight off the SR bytes; we convert NTP to 100-ns
    /// <see cref="TimeSpan"/> ticks for unit-uniform internal math.
    /// </summary>
    public void OnAudioSenderReport(ulong ntpTimestamp, uint rtpTimestamp)
    {
        var ntpTicks = NtpToTimeSpanTicks(ntpTimestamp);
        bool firstSr;
        lock (_lock)
        {
            firstSr = !_haveAudioSr;
            _audioNtpTicksAtSr = ntpTicks;
            _audioRtpTsAtSr = rtpTimestamp;
            _haveAudioSr = true;
        }
        if (firstSr)
        {
            DebugLog.Write($"[mediaclock {_logTag}] first audio SR: ntpTicks={ntpTicks} rtpTs={rtpTimestamp}");
        }
    }

    /// <summary>Record a video Sender Report.</summary>
    public void OnVideoSenderReport(ulong ntpTimestamp, uint rtpTimestamp)
    {
        var ntpTicks = NtpToTimeSpanTicks(ntpTimestamp);
        bool firstSr;
        lock (_lock)
        {
            firstSr = !_haveVideoSr;
            _videoNtpTicksAtSr = ntpTicks;
            _videoRtpTsAtSr = rtpTimestamp;
            _haveVideoSr = true;
        }
        if (firstSr)
        {
            DebugLog.Write($"[mediaclock {_logTag}] first video SR: ntpTicks={ntpTicks} rtpTs={rtpTimestamp}");
        }
    }

    /// <summary>
    /// Latch the shared anchor: the frame at <paramref name="rtpTs"/>
    /// on the given <paramref name="kind"/> stream is intended to play
    /// at <paramref name="localStopwatchAtPlayout"/>. From here on,
    /// queries return wall times relative to this anchor.
    ///
    /// Returns <see langword="true"/> when the anchor was latched on
    /// this call, <see langword="false"/> when it was already latched
    /// previously OR the matching SR hasn't arrived yet — the caller
    /// can keep trying on later frames.
    /// </summary>
    public bool TryAnchor(uint rtpTs, MediaKind kind, long localStopwatchAtPlayout)
    {
        bool latched;
        long publisherNtp;
        lock (_lock)
        {
            if (_haveAnchor)
            {
                return false;
            }
            if (!TryRtpToPublisherNtpTicksLocked(rtpTs, kind, out publisherNtp))
            {
                return false;
            }
            _publisherNtpAnchorTicks = publisherNtp;
            _localAnchorTicks = localStopwatchAtPlayout;
            _haveAnchor = true;
            latched = true;
        }
        if (latched)
        {
            DebugLog.Write($"[mediaclock {_logTag}] anchored: kind={kind} rtpTs={rtpTs} ntpAnchorTicks={publisherNtp} localAnchorTicks={localStopwatchAtPlayout}");
        }
        return latched;
    }

    /// <summary>
    /// Set or update the shared anchor from a video frame's per-stream
    /// content time (derived from the RTP timestamp at 90 kHz). Used
    /// by the renderer's paint loop, which already holds
    /// <c>(anchorWall, anchorPts)</c> for its own scheduling — calling
    /// this on first paint and on every re-anchor exposes the *same*
    /// pair to audio so the two streams ride a single timeline. Unlike
    /// <see cref="TryAnchor"/>, this overwrites a prior anchor; the
    /// renderer is the source of truth for "where video is right now"
    /// and audio follows.
    ///
    /// No-ops when no video Sender Report has arrived — without it we
    /// can't translate the per-stream content time into the publisher
    /// NTP domain that audio also lives in. Caller falls back to
    /// pre-sync behaviour (audio plays unaligned) until the first SR
    /// lands; then a future paint will latch the anchor and audio
    /// snaps onto the line on its next frame.
    /// </summary>
    public bool SetVideoAnchorFromContentTime(TimeSpan videoContentTime, long localStopwatchTicks)
    {
        // Always update the paint-anchor — this lets AudioReceiver
        // start playing as soon as video paints, regardless of
        // whether either Sender Report has arrived. SR translation
        // (below) is the more precise path, used when SR data lets
        // us put audio's RTP timestamp on the same publisher NTP
        // line as video's; the paint-anchor is the simpler fallback.
        bool firstPaintAnchor;
        lock (_lock)
        {
            firstPaintAnchor = !_havePaintAnchor;
            _paintAnchorLocalTicks = localStopwatchTicks;
            _paintAnchorVideoContentTime = videoContentTime;
            _havePaintAnchor = true;
        }
        if (firstPaintAnchor)
        {
            DebugLog.Write($"[mediaclock {_logTag}] paint-anchor set: contentTime={videoContentTime.TotalMilliseconds:F0}ms localAnchor={localStopwatchTicks}");
        }

        // Per-stream video content time is rtpTs * 100ns / RTP_unit
        // at the 90 kHz video clock. Recover the RTP TS by inverting:
        // rtpTs = ticks * 90 / TicksPerMs. Lossy by a single tick at
        // most (microsecond range), which is well below the drift
        // threshold the audio side applies anyway.
        var rtpTs = unchecked((uint)(videoContentTime.Ticks * 90L / TimeSpan.TicksPerMillisecond));
        long publisherNtp;
        bool reAnchored;
        lock (_lock)
        {
            if (!TryRtpToPublisherNtpTicksLocked(rtpTs, MediaKind.Video, out publisherNtp))
            {
                // Video SR hasn't arrived yet — we can't translate
                // the per-stream content time into the publisher NTP
                // domain. Audio receiver will use the paint-anchor
                // fallback in the meantime. Caller (PaintLoop) keeps
                // retrying on each paint until SR lands.
                return false;
            }
            reAnchored = _haveAnchor;
            _publisherNtpAnchorTicks = publisherNtp;
            _localAnchorTicks = localStopwatchTicks;
            _haveAnchor = true;
        }
        if (!reAnchored)
        {
            DebugLog.Write($"[mediaclock {_logTag}] anchored from paint: rtpTs={rtpTs} ntpAnchor={publisherNtp} localAnchor={localStopwatchTicks}");
        }
        return true;
    }

    /// <summary>
    /// Snapshot of the paint-anchor: the local Stopwatch tick at
    /// which the renderer last (re-)anchored, paired with the
    /// per-stream video content time it painted at that moment.
    /// Used by <see cref="AudioReceiver"/> as a fallback so audio
    /// can play as soon as video paints, without waiting for either
    /// Sender Report to land.
    /// </summary>
    public (long LocalStopwatchTicks, TimeSpan VideoContentTime)? GetPaintAnchor()
    {
        lock (_lock)
        {
            return _havePaintAnchor
                ? (_paintAnchorLocalTicks, _paintAnchorVideoContentTime)
                : null;
        }
    }

    /// <summary>
    /// Translate an audio RTP timestamp to local Stopwatch wall-clock
    /// ticks at which the corresponding sample should play. Returns
    /// <see langword="null"/> until both an audio SR is known and the
    /// shared anchor has been latched.
    /// </summary>
    public long? AudioRtpToLocalWallTicks(uint audioRtpTs) =>
        RtpToLocalWallTicks(audioRtpTs, MediaKind.Audio);

    /// <summary>Translate a video RTP timestamp to local wall-clock ticks.</summary>
    public long? VideoRtpToLocalWallTicks(uint videoRtpTs) =>
        RtpToLocalWallTicks(videoRtpTs, MediaKind.Video);

    /// <summary>True once both audio and video SRs are known and the anchor is latched.</summary>
    public bool IsFullyLocked
    {
        get
        {
            lock (_lock)
            {
                return _haveAnchor && _haveAudioSr && _haveVideoSr;
            }
        }
    }

    /// <summary>
    /// True once <see cref="SetVideoAnchorFromContentTime"/> or
    /// <see cref="TryAnchor"/> has latched the shared anchor at all
    /// (regardless of whether an audio Sender Report has arrived).
    /// AudioReceiver uses this to start playback as soon as video
    /// paints, even if the audio SR hasn't landed yet, by synthesizing
    /// its own anchor from the oldest queued audio packet at this
    /// moment — it then doesn't have to wait for SIPSorcery's RTCP
    /// cadence (~5 s) before audio becomes audible.
    /// </summary>
    public bool HasAnchor
    {
        get
        {
            lock (_lock)
            {
                return _haveAnchor;
            }
        }
    }

    /// <summary>
    /// Snapshot of the shared anchor: <c>(localStopwatchTicks,
    /// publisherNtpTicks)</c>. Returns <see langword="null"/> until
    /// the anchor is latched. AudioReceiver pulls this for the
    /// no-audio-SR fallback path; querying every other code path
    /// should go through the per-RTP translation methods.
    /// </summary>
    public (long LocalStopwatchTicks, long PublisherNtpTicks)? GetAnchor()
    {
        lock (_lock)
        {
            return _haveAnchor ? (_localAnchorTicks, _publisherNtpAnchorTicks) : null;
        }
    }

    /// <summary>True iff <see cref="OnAudioSenderReport"/> has been called.</summary>
    public bool HasAudioSenderReport
    {
        get
        {
            lock (_lock)
            {
                return _haveAudioSr;
            }
        }
    }

    private long? RtpToLocalWallTicks(uint rtpTs, MediaKind kind)
    {
        lock (_lock)
        {
            if (!_haveAnchor)
            {
                return null;
            }
            if (!TryRtpToPublisherNtpTicksLocked(rtpTs, kind, out var publisherNtp))
            {
                return null;
            }
            // Translate the publisher-NTP delta (100-ns TimeSpan ticks)
            // into local Stopwatch ticks. Use floating math so we don't
            // lose precision when the timer frequency isn't a clean
            // multiple of TimeSpan.TicksPerSecond (it usually isn't).
            var deltaNtpTicks = publisherNtp - _publisherNtpAnchorTicks;
            var deltaSwTicks = (long)((double)deltaNtpTicks
                * Stopwatch.Frequency
                / TimeSpan.TicksPerSecond);
            return _localAnchorTicks + deltaSwTicks;
        }
    }

    private bool TryRtpToPublisherNtpTicksLocked(uint rtpTs, MediaKind kind, out long publisherNtpTicks)
    {
        if (kind == MediaKind.Audio)
        {
            if (!_haveAudioSr)
            {
                publisherNtpTicks = 0;
                return false;
            }
            // Signed cast handles 32-bit RTP timestamp wrap correctly:
            // a "negative" delta means rtpTs is before _audioRtpTsAtSr.
            var rtpDelta = (int)(rtpTs - _audioRtpTsAtSr);
            var deltaTicks = (long)((double)rtpDelta * TimeSpan.TicksPerSecond / AudioRtpClockRate);
            publisherNtpTicks = _audioNtpTicksAtSr + deltaTicks;
            return true;
        }
        else
        {
            if (!_haveVideoSr)
            {
                publisherNtpTicks = 0;
                return false;
            }
            var rtpDelta = (int)(rtpTs - _videoRtpTsAtSr);
            var deltaTicks = (long)((double)rtpDelta * TimeSpan.TicksPerSecond / VideoRtpClockRate);
            publisherNtpTicks = _videoNtpTicksAtSr + deltaTicks;
            return true;
        }
    }

    /// <summary>
    /// Convert a 64-bit NTP timestamp (RFC 3550) to 100-ns
    /// <see cref="TimeSpan"/> ticks. Upper 32 bits = seconds since
    /// 1900-01-01 (NTP epoch); lower 32 bits = fractional second
    /// scaled by 2^32. The result has an absolute offset from
    /// <see cref="DateTime.Ticks"/> (NTP epoch is 1900, .NET is 0001)
    /// but that is irrelevant here — we only ever subtract two NTP
    /// values to get a duration.
    /// </summary>
    public static long NtpToTimeSpanTicks(ulong ntp)
    {
        var seconds = (long)(ntp >> 32);
        var fraction = (long)(ntp & 0xFFFFFFFFu);
        // Multiply before divide to keep integer precision — fraction
        // is at most 2^32 - 1, TimeSpan.TicksPerSecond is 1e7, the
        // product fits in long.
        var fractionTicks = fraction * TimeSpan.TicksPerSecond / (1L << 32);
        return seconds * TimeSpan.TicksPerSecond + fractionTicks;
    }
}
