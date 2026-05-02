namespace VicoScreenShare.Client.Diagnostics;

using System.Globalization;
using System.Text;
using VicoScreenShare.Client.Media;
using VicoScreenShare.Client.Services;

/// <summary>
/// Formats a <see cref="ClientSettings"/> instance into a single multi-line
/// block suitable for the debug log. Used by <c>App.OnStartup</c> for the
/// initial snapshot and by <c>CaptureStreamer.Start</c> for the share-start
/// snapshot, so both paths produce the same readable shape and a tester's
/// log file always begins with a complete picture of the active config.
/// </summary>
public static class SettingsLogFormatter
{
    /// <summary>
    /// Build a settings block. <paramref name="tag"/> is the bracketed prefix
    /// (e.g. <c>"settings-startup"</c> or <c>"settings-share"</c>) — every
    /// emitted line gets that tag so a tester filtering by category sees a
    /// contiguous block.
    /// </summary>
    public static string Format(string tag, ClientSettings settings)
    {
        var v = settings.Video;
        var a = settings.Audio;
        var sb = new StringBuilder();
        var ic = CultureInfo.InvariantCulture;

        sb.AppendLine($"[{tag}] codec={v.Codec} height={v.TargetHeight} fps={v.TargetFrameRate} bitrate={FormatBps(v.TargetBitrate)} keyframeInterval={v.KeyframeIntervalSeconds.ToString("0.0", ic)}s scaler={v.Scaler}");
        sb.AppendLine($"[{tag}] receiveBuffer={v.ReceiveBufferFrames}f adaptiveBitrate={(v.EnableAdaptiveBitrate ? "on" : "off")} minAdaptiveBitrate={FormatBps(v.MinAdaptiveBitrate)} sendPacing={(v.EnableSendPacing ? "on" : "off")} pacingMultiplier={v.SendPacingBitrateMultiplier}x");
        sb.AppendLine($"[{tag}] backends: h264Enc={v.H264Backend} h264Dec={v.H264DecoderBackend} av1Enc={v.Av1Backend} av1Dec={v.Av1DecoderBackend}");
        sb.AppendLine($"[{tag}] nvenc: aq={(v.EnableAdaptiveQuantization ? "on" : "off")} lookahead={(v.EnableEncoderLookahead ? "on" : "off")} intraRefresh={(v.EnableIntraRefresh ? "on" : "off")}{(v.IntraRefreshPeriodFrames > 0 ? $"(period={v.IntraRefreshPeriodFrames})" : "")} preset=P{v.NvencPreset}");
        sb.AppendLine($"[{tag}] audio: bitrate={FormatBps(a.TargetBitrate)} stereo={(a.Stereo ? "on" : "off")} application={a.Application} frameDuration={a.FrameDurationMs}ms forceSystemAudio={(a.ForceSystemAudio ? "on" : "off")}");

        var active = settings.ActiveConnection;
        if (active is not null)
        {
            var hasPassword = !string.IsNullOrEmpty(active.Password);
            sb.Append($"[{tag}] connection: name={(string.IsNullOrEmpty(active.Name) ? "(unnamed)" : active.Name)} uri={active.Uri} hasPassword={hasPassword}");
        }
        else
        {
            sb.Append($"[{tag}] connection: (none active, {settings.Connections.Count} saved)");
        }

        return sb.ToString();
    }

    private static string FormatBps(int bps)
    {
        if (bps >= 1_000_000)
        {
            return $"{(bps / 1_000_000.0).ToString("0.##", CultureInfo.InvariantCulture)}Mbps";
        }
        if (bps >= 1_000)
        {
            return $"{(bps / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture)}kbps";
        }
        return $"{bps}bps";
    }
}
