namespace VicoScreenShare.Tests.Client;

using System;
using System.Diagnostics;
using FluentAssertions;
using VicoScreenShare.Client.Media;

public class MediaClockTests
{
    /// <summary>
    /// Build an NTP timestamp with the given seconds-since-1900 and a
    /// fractional second component. The fractional argument is in
    /// 0..1; we scale it by 2^32 to fit the NTP fraction field.
    /// </summary>
    private static ulong NtpFromComponents(uint secondsSince1900, double fractionalSecond)
    {
        var fraction = (ulong)(fractionalSecond * 4294967296.0);
        return ((ulong)secondsSince1900 << 32) | fraction;
    }

    [Fact]
    public void Queries_return_null_before_any_sender_report()
    {
        var clock = new MediaClock();
        clock.AudioRtpToLocalWallTicks(123).Should().BeNull();
        clock.VideoRtpToLocalWallTicks(456).Should().BeNull();
        clock.IsFullyLocked.Should().BeFalse();
    }

    [Fact]
    public void Queries_return_null_after_sr_but_before_anchor()
    {
        var clock = new MediaClock();
        clock.OnAudioSenderReport(NtpFromComponents(3000, 0.0), rtpTimestamp: 48000);
        clock.OnVideoSenderReport(NtpFromComponents(3000, 0.0), rtpTimestamp: 90000);
        clock.AudioRtpToLocalWallTicks(48000).Should().BeNull();
        clock.VideoRtpToLocalWallTicks(90000).Should().BeNull();
    }

    [Fact]
    public void Try_anchor_returns_false_without_matching_sr()
    {
        var clock = new MediaClock();
        // Anchor on video before video SR has arrived → should fail.
        clock.TryAnchor(rtpTs: 1234, MediaKind.Video, localStopwatchAtPlayout: 0)
            .Should().BeFalse();
    }

    [Fact]
    public void Anchor_latches_only_once()
    {
        var clock = new MediaClock();
        clock.OnVideoSenderReport(NtpFromComponents(3000, 0.0), rtpTimestamp: 90000);
        clock.TryAnchor(rtpTs: 90000, MediaKind.Video, localStopwatchAtPlayout: 0)
            .Should().BeTrue();
        clock.TryAnchor(rtpTs: 95000, MediaKind.Video, localStopwatchAtPlayout: 100)
            .Should().BeFalse();
    }

    [Fact]
    public void Audio_rtp_translates_via_sr_pair_then_onto_anchor()
    {
        var clock = new MediaClock();

        // Publisher's NTP wall at SR moment: (epoch + 3000.000 s).
        // Audio RTP TS at that moment: 48000 (= 1 s of Opus since
        // some arbitrary start).
        clock.OnAudioSenderReport(NtpFromComponents(3000, 0.0), rtpTimestamp: 48000);
        clock.OnVideoSenderReport(NtpFromComponents(3000, 0.0), rtpTimestamp: 90000);

        // Anchor the shared clock at this video frame: at local
        // Stopwatch tick 0, the publisher's NTP for video RTP
        // 90000 should play. Equivalent to publisher NTP at 3000.000s.
        clock.TryAnchor(rtpTs: 90000, MediaKind.Video, localStopwatchAtPlayout: 0)
            .Should().BeTrue();

        // An audio sample 1 s after the SR (audio RTP = 48000 +
        // 48000 = 96000) should map onto the anchor + 1 s in local
        // Stopwatch ticks.
        var wall = clock.AudioRtpToLocalWallTicks(96000);
        wall.Should().NotBeNull();
        // 1 second in Stopwatch ticks. Allow 1-tick rounding error
        // from the integer division in the NTP-fraction conversion.
        wall!.Value.Should().BeCloseTo(Stopwatch.Frequency, (uint)2);
    }

    [Fact]
    public void Audio_at_anchor_moment_maps_to_anchor_local_wall()
    {
        var clock = new MediaClock();
        // Audio and video SRs both pinned at NTP 3000.000.
        clock.OnAudioSenderReport(NtpFromComponents(3000, 0.0), rtpTimestamp: 1000);
        clock.OnVideoSenderReport(NtpFromComponents(3000, 0.0), rtpTimestamp: 2000);
        clock.TryAnchor(rtpTs: 2000, MediaKind.Video, localStopwatchAtPlayout: 12345);

        // Audio sample whose RTP TS resolves to the same NTP moment
        // (the SR's audio RTP TS) should play exactly at the anchor's
        // local wall.
        var wall = clock.AudioRtpToLocalWallTicks(1000);
        wall.Should().NotBeNull();
        wall!.Value.Should().Be(12345);
    }

    [Fact]
    public void Audio_translation_handles_rtp_timestamp_wrap()
    {
        var clock = new MediaClock();
        // Audio SR captured near uint.MaxValue — next packet will
        // wrap to 0. The signed-cast in the implementation should
        // treat the wrapped delta as a small positive duration, not
        // a 4-billion-sample-old timestamp.
        const uint nearMax = uint.MaxValue - 24_000; // ~0.5 s before wrap at 48 kHz
        clock.OnAudioSenderReport(NtpFromComponents(3000, 0.0), rtpTimestamp: nearMax);
        clock.OnVideoSenderReport(NtpFromComponents(3000, 0.0), rtpTimestamp: 90000);
        clock.TryAnchor(rtpTs: 90000, MediaKind.Video, localStopwatchAtPlayout: 0);

        // 1 s later in audio RTP units = wrap past zero. Use unchecked
        // because xunit defaults to checked arithmetic and uint wrap
        // is exactly the behaviour the test exercises.
        var oneSecondLater = unchecked((uint)(nearMax + MediaClock.AudioRtpClockRate));
        var wall = clock.AudioRtpToLocalWallTicks(oneSecondLater);
        wall.Should().NotBeNull();
        wall!.Value.Should().BeCloseTo(Stopwatch.Frequency, (uint)2);
    }

    [Fact]
    public void Video_translation_uses_90khz_clock_rate()
    {
        var clock = new MediaClock();
        clock.OnVideoSenderReport(NtpFromComponents(3000, 0.0), rtpTimestamp: 0);
        clock.TryAnchor(rtpTs: 0, MediaKind.Video, localStopwatchAtPlayout: 0);

        // 90,000 RTP units later = 1 second of video time.
        var wall = clock.VideoRtpToLocalWallTicks(MediaClock.VideoRtpClockRate);
        wall.Should().NotBeNull();
        wall!.Value.Should().BeCloseTo(Stopwatch.Frequency, (uint)2);
    }

    [Fact]
    public void Cross_stream_sync_audio_and_video_at_same_publisher_ntp_share_local_wall()
    {
        var clock = new MediaClock();

        // Both SRs land at publisher NTP 3000.500 (mid-second to
        // exercise the fractional path).
        const uint baseSeconds = 3000;
        const double frac = 0.5;
        clock.OnAudioSenderReport(NtpFromComponents(baseSeconds, frac), rtpTimestamp: 5_000_000);
        clock.OnVideoSenderReport(NtpFromComponents(baseSeconds, frac), rtpTimestamp: 9_000_000);

        // Anchor at video RTP 9_000_000 → local 0.
        clock.TryAnchor(rtpTs: 9_000_000, MediaKind.Video, localStopwatchAtPlayout: 0);

        // Audio sample at audio-RTP 5_000_000 corresponds to the
        // SAME publisher NTP (the SR moment), so it should play at
        // the anchor's local wall too.
        var audioWall = clock.AudioRtpToLocalWallTicks(5_000_000);
        var videoWall = clock.VideoRtpToLocalWallTicks(9_000_000);
        audioWall.Should().Be(videoWall);
        audioWall!.Value.Should().Be(0);
    }

    [Fact]
    public void IsFullyLocked_requires_both_srs_and_anchor()
    {
        var clock = new MediaClock();
        clock.IsFullyLocked.Should().BeFalse();

        clock.OnAudioSenderReport(NtpFromComponents(3000, 0.0), 1000);
        clock.IsFullyLocked.Should().BeFalse();

        clock.OnVideoSenderReport(NtpFromComponents(3000, 0.0), 2000);
        clock.IsFullyLocked.Should().BeFalse();

        clock.TryAnchor(2000, MediaKind.Video, 0);
        clock.IsFullyLocked.Should().BeTrue();
    }

    [Fact]
    public void SetVideoAnchorFromContentTime_no_ops_without_video_sr()
    {
        var clock = new MediaClock();
        clock.OnAudioSenderReport(NtpFromComponents(3000, 0.0), 1000);
        // Anchor from content time without video SR → ignored.
        clock.SetVideoAnchorFromContentTime(TimeSpan.FromMilliseconds(500), 12345);
        // Audio query still null because anchor never latched.
        clock.AudioRtpToLocalWallTicks(48000).Should().BeNull();
    }

    [Fact]
    public void SetVideoAnchorFromContentTime_latches_anchor_using_video_sr()
    {
        var clock = new MediaClock();
        // Video SR pinned at NTP 3000.000 / video RTP 0.
        clock.OnVideoSenderReport(NtpFromComponents(3000, 0.0), 0);
        clock.OnAudioSenderReport(NtpFromComponents(3000, 0.0), 0);

        // Anchor at content time = 1 s (= video RTP 90000 at 90 kHz),
        // local wall = 555.
        var oneSecond = TimeSpan.FromSeconds(1);
        clock.SetVideoAnchorFromContentTime(oneSecond, 555);

        // An audio sample whose NTP equals 3000.000 + 1 s should
        // play at the anchor's local wall.
        var audioOneSecondAfterSr = (uint)MediaClock.AudioRtpClockRate;
        var wall = clock.AudioRtpToLocalWallTicks(audioOneSecondAfterSr);
        wall.Should().NotBeNull();
        wall!.Value.Should().Be(555);
    }

    [Fact]
    public void SetVideoAnchorFromContentTime_re_anchors_on_repeat_call()
    {
        var clock = new MediaClock();
        clock.OnVideoSenderReport(NtpFromComponents(3000, 0.0), 0);
        clock.OnAudioSenderReport(NtpFromComponents(3000, 0.0), 0);

        clock.SetVideoAnchorFromContentTime(TimeSpan.Zero, 100);
        var wall1 = clock.AudioRtpToLocalWallTicks(0);
        wall1.Should().Be(100);

        // Re-anchor — TryAnchor would have refused; this overwrites.
        clock.SetVideoAnchorFromContentTime(TimeSpan.Zero, 999);
        var wall2 = clock.AudioRtpToLocalWallTicks(0);
        wall2.Should().Be(999);
    }

    [Fact]
    public void NtpToTimeSpanTicks_recovers_round_seconds()
    {
        // 1 second after NTP epoch → 1 * TicksPerSecond, plus the
        // huge offset since 1900. The relative-difference invariant
        // we actually use cancels the offset, so check by subtracting
        // two NTP-to-ticks results and asserting the delta is exact.
        var t0 = MediaClock.NtpToTimeSpanTicks(NtpFromComponents(3000, 0.0));
        var t1 = MediaClock.NtpToTimeSpanTicks(NtpFromComponents(3001, 0.0));
        (t1 - t0).Should().Be(TimeSpan.TicksPerSecond);
    }

    [Fact]
    public void NtpToTimeSpanTicks_recovers_fractional_seconds()
    {
        // 0.5 s difference at the fractional level only.
        var t0 = MediaClock.NtpToTimeSpanTicks(NtpFromComponents(3000, 0.0));
        var t1 = MediaClock.NtpToTimeSpanTicks(NtpFromComponents(3000, 0.5));
        // 0.5 s in 100-ns ticks = 5_000_000. Allow 1 tick rounding.
        (t1 - t0).Should().BeCloseTo(TimeSpan.TicksPerSecond / 2, 1);
    }
}
