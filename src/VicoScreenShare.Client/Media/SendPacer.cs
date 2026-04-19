namespace VicoScreenShare.Client.Media;

using System;

/// <summary>
/// Token-bucket rate limiter for the RTP send path. Caps outgoing bytes per
/// second with a configurable burst capacity, so keyframes can't fire their
/// full payload into the kernel UDP buffer faster than the downstream link
/// can drain. Intended to live between <c>CaptureStreamer</c>'s encoded-frame
/// callback and the actual <c>RTCPeerConnection.SendVideo</c> call.
/// <para>
/// Thread-safety: single writer (the pacer worker) is assumed. The clock is
/// injectable so unit tests can pump time deterministically instead of
/// sleeping. Setting <paramref name="bytesPerSecond"/> to zero or negative
/// disables the pacer (every <see cref="TryConsume"/> returns true, every
/// <see cref="EstimateWaitFor"/> returns zero).
/// </para>
/// </summary>
public sealed class SendPacer
{
    private readonly long _bytesPerSecond;
    private readonly long _burstBytes;
    private readonly Func<TimeSpan> _clock;

    private double _tokens;
    private TimeSpan _lastRefill;

    public SendPacer(long bytesPerSecond, long burstBytes, Func<TimeSpan> clock)
    {
        _bytesPerSecond = bytesPerSecond;
        _burstBytes = Math.Max(0, burstBytes);
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));

        _tokens = _burstBytes;
        _lastRefill = _clock();
    }

    /// <summary>
    /// Try to consume <paramref name="bytes"/> of budget immediately. Returns
    /// true on success; false if the bucket doesn't currently hold enough
    /// tokens. On false, callers should await <see cref="EstimateWaitFor"/>
    /// before trying again.
    /// </summary>
    public bool TryConsume(long bytes)
    {
        if (_bytesPerSecond <= 0)
        {
            return true;
        }

        Refill();
        if (_tokens < bytes)
        {
            return false;
        }
        _tokens -= bytes;
        return true;
    }

    /// <summary>
    /// How long the caller must wait (from now) before the bucket will hold
    /// <paramref name="bytes"/> worth of budget. Zero when the send fits
    /// immediately. <see cref="TimeSpan.MaxValue"/> when the request is
    /// larger than the bucket's capacity — callers should treat that as a
    /// configuration error and increase <c>burstBytes</c>.
    /// </summary>
    public TimeSpan EstimateWaitFor(long bytes)
    {
        if (_bytesPerSecond <= 0)
        {
            return TimeSpan.Zero;
        }
        if (bytes > _burstBytes)
        {
            return TimeSpan.MaxValue;
        }

        Refill();
        var deficit = bytes - _tokens;
        if (deficit <= 0)
        {
            return TimeSpan.Zero;
        }
        var seconds = deficit / (double)_bytesPerSecond;
        return TimeSpan.FromSeconds(seconds);
    }

    private void Refill()
    {
        var now = _clock();
        var elapsed = now - _lastRefill;
        if (elapsed <= TimeSpan.Zero)
        {
            return;
        }
        _tokens = Math.Min(_burstBytes, _tokens + elapsed.TotalSeconds * _bytesPerSecond);
        _lastRefill = now;
    }
}
