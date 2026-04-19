namespace VicoScreenShare.Client.Media;

using System;

/// <summary>
/// Pre-TWCC-style loss-based adaptive bitrate controller. Consumes observed
/// fraction-lost values from inbound RTCP Receiver Reports and produces a
/// bounded encoder target bitrate.
/// <para>
/// Three-zone loss response (RFC 3550 §6.4.1 reports fraction-lost as the
/// proportion of packets lost over the last reporting interval):
/// <list type="bullet">
///   <item><b>Above <see cref="HighLossThreshold"/>:</b> immediate back-off
///   (multiplicative, <see cref="DownScale"/>).</item>
///   <item><b>Between thresholds:</b> steady-state — hold the current
///   bitrate. The viewer is absorbing some loss but isn't in trouble.</item>
///   <item><b>Below <see cref="LowLossThreshold"/>:</b> probe upward
///   (multiplicative, <see cref="UpScale"/>) up to the configured target.</item>
/// </list>
/// A cooldown (<see cref="Interval"/>) gates adjustments so a flurry of RRs
/// can't drive the bitrate into the floor in one burst.
/// </para>
/// <para>
/// Industry-standard shape — this is what pre-TWCC WebRTC stacks used and
/// what Chrome's libwebrtc still falls back to when TWCC headers aren't
/// negotiated. Not as accurate as Google Congestion Control, but simple,
/// works with any RFC 3550 RTP peer, and fixes the "link can't carry
/// TargetBitrate so keyframes burst-drop forever" pathology.
/// </para>
/// </summary>
public sealed class LossBasedBitrateController
{
    public const double HighLossThreshold = 0.10;
    public const double LowLossThreshold = 0.02;
    public const double DownScale = 0.85;
    public const double UpScale = 1.05;
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly int _targetBitrate;
    private readonly int _minBitrate;
    private readonly Func<TimeSpan> _clock;
    private int _currentBitrate;
    private TimeSpan _lastAdjustAt;

    public LossBasedBitrateController(int targetBitrate, int minBitrate, Func<TimeSpan> clock)
    {
        if (targetBitrate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetBitrate));
        }
        if (minBitrate <= 0 || minBitrate > targetBitrate)
        {
            throw new ArgumentOutOfRangeException(nameof(minBitrate));
        }
        _targetBitrate = targetBitrate;
        _minBitrate = minBitrate;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _currentBitrate = targetBitrate;
        _lastAdjustAt = TimeSpan.MinValue;
    }

    public int CurrentBitrate => _currentBitrate;

    public event Action<int>? BitrateChanged;

    /// <summary>
    /// Feed one RR's reported fraction-lost. Values are clamped to
    /// <c>[0, 1]</c>. Adjustments happen at most once per
    /// <see cref="Interval"/> — earlier calls are observed but not acted
    /// on until the cooldown clears.
    /// </summary>
    public void Observe(double fractionLost)
    {
        var now = _clock();
        if (_lastAdjustAt != TimeSpan.MinValue && now - _lastAdjustAt < Interval)
        {
            return;
        }

        var loss = Math.Clamp(fractionLost, 0.0, 1.0);
        int next;
        if (loss > HighLossThreshold)
        {
            next = (int)Math.Floor(_currentBitrate * DownScale);
        }
        else if (loss < LowLossThreshold)
        {
            next = (int)Math.Ceiling(_currentBitrate * UpScale);
        }
        else
        {
            // Hold zone — rate's fine, but still stamp the adjust time so
            // the next out-of-zone observation gets a proper cooldown.
            _lastAdjustAt = now;
            return;
        }

        next = Math.Clamp(next, _minBitrate, _targetBitrate);
        _lastAdjustAt = now;
        if (next == _currentBitrate)
        {
            return;
        }
        _currentBitrate = next;
        BitrateChanged?.Invoke(next);
    }
}
