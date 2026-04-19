namespace VicoScreenShare.Client.Media;

using System;

/// <summary>
/// Credit-based frame rate admission gate. Holds a running "accepted
/// frame count" and accepts each new frame only if enough time has
/// elapsed since the first accepted frame for the caller's target fps.
/// Equivalent formulation: accept the next frame if
/// <c>(frameTs - firstTs) &gt;= acceptedCount * (1s / fps)</c>, where
/// <c>acceptedCount</c> is the number of frames already accepted.
///
/// Behavior on a steady source that matches the target: every arrival
/// is admitted. On a source faster than target: excess frames drop to
/// the target rate. On a source slower than target: every arrival is
/// admitted (we can't create frames we don't have).
///
/// Thread-safety: this class is NOT thread-safe. Callers use one
/// instance per source and drive it from a single producer thread (the
/// WGC framepool callback). If a future backend needs a shared pacer
/// it should add external synchronization.
/// </summary>
public sealed class FrameRatePacer
{
    private readonly long _frameGapTicks;
    private long _firstAcceptedTicks;
    private long _acceptedCount;

    /// <param name="targetFps">Target frames per second. Clamped to
    /// [1, 240] to match the settings UI range.</param>
    public FrameRatePacer(int targetFps)
    {
        var fps = Math.Clamp(targetFps, 1, 240);
        _frameGapTicks = TimeSpan.TicksPerSecond / fps;
    }

    /// <summary>Target frame gap in <see cref="TimeSpan"/> ticks
    /// (100 ns units). Exposed for diagnostics and tests.</summary>
    public long FrameGapTicks => _frameGapTicks;

    /// <summary>Number of frames admitted since the last
    /// <see cref="Reset"/>.</summary>
    public long AcceptedCount => _acceptedCount;

    /// <summary>
    /// Decide whether to admit a frame with the given content timestamp.
    /// Accepts and advances the internal state on admission; does
    /// nothing on rejection. The first ever call always admits and
    /// anchors the clock.
    /// </summary>
    public bool ShouldAccept(TimeSpan contentTimestamp)
    {
        var ticks = contentTimestamp.Ticks;
        if (_acceptedCount == 0)
        {
            _firstAcceptedTicks = ticks;
            _acceptedCount = 1;
            return true;
        }

        // Credit rule: we've accepted N frames so far (relative to
        // first), so the next frame is "on schedule" once
        // (ticks - first) has reached N * frameGap. Expressed as
        // elapsed >= accepted * gap, which is the standard credit-based
        // rate cap. Source slower than target → every frame admitted.
        // Source faster than target → admitted every other / every
        // third / etc. frame depending on the ratio.
        var elapsed = ticks - _firstAcceptedTicks;
        if (elapsed >= _acceptedCount * _frameGapTicks)
        {
            _acceptedCount++;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Clear accepted-count state so the next <see cref="ShouldAccept"/>
    /// anchors a fresh clock. Called on source swap / StopAsync so a
    /// re-attached source doesn't inherit an old anchor.
    /// </summary>
    public void Reset()
    {
        _firstAcceptedTicks = 0;
        _acceptedCount = 0;
    }
}
