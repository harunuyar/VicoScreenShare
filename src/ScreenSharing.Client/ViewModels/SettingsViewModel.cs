using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenSharing.Client.Media;
using ScreenSharing.Client.Media.Codecs;
using ScreenSharing.Client.Services;

namespace ScreenSharing.Client.ViewModels;

/// <summary>
/// Settings page bound to the same live <see cref="ClientSettings"/> instance
/// every other view model holds — Save mutates those fields in place so the
/// next <see cref="CaptureStreamer"/> built by <see cref="RoomViewModel"/>
/// picks up the new values without a restart, and persists to disk via
/// <see cref="SettingsStore"/>.
///
/// Resolution is a dropdown of target heights (width is derived from the
/// source aspect ratio at runtime so output never distorts). Framerate,
/// bitrate, GOP and scaler quality are raw sliders — the presets in
/// <see cref="QualityPresets"/> just fill all four sliders at once and the
/// user is free to tweak anything afterwards.
///
/// Bitrate is presented on a logarithmic scale (stored as 0.0-1.0 internally)
/// so dragging from 2 Mbps to 50 Mbps feels linear to the user even though
/// the underlying value is exponential. The slider value exposed to XAML is
/// <see cref="BitrateSliderValue"/>; the real bps number is
/// <see cref="Bitrate"/>.
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    // Bitrate log-scale range. 500 Kbps → 100 Mbps covers everything from
    // "very bad line" to "way more than any reasonable setup needs".
    private const int MinBitrateBps = 500_000;
    private const int MaxBitrateBps = 100_000_000;

    private readonly ClientSettings _settings;
    private readonly SettingsStore _store;
    private readonly NavigationService _navigation;
    private readonly Func<ViewModelBase> _backFactory;
    private readonly Action? _onSaved;

    public SettingsViewModel(
        ClientSettings settings,
        SettingsStore store,
        NavigationService navigation,
        Func<ViewModelBase> backFactory,
        Action? onSaved = null)
    {
        _settings = settings;
        _store = store;
        _navigation = navigation;
        _backFactory = backFactory;
        _onSaved = onSaved;

        TargetHeightOptions = new[]
        {
            new TargetHeightOption("Source (no downscale)", 0),
            new TargetHeightOption("2160p (4K)", 2160),
            new TargetHeightOption("1800p", 1800),
            new TargetHeightOption("1600p", 1600),
            new TargetHeightOption("1440p", 1440),
            new TargetHeightOption("1200p", 1200),
            new TargetHeightOption("1080p", 1080),
            new TargetHeightOption("900p", 900),
            new TargetHeightOption("864p", 864),
            new TargetHeightOption("768p", 768),
            new TargetHeightOption("720p", 720),
            new TargetHeightOption("600p", 600),
            new TargetHeightOption("540p", 540),
            new TargetHeightOption("480p", 480),
            new TargetHeightOption("360p", 360),
        };

        ScalerQualityOptions = new[]
        {
            new ScalerQualityOption("Nearest (fastest, shimmer)", ScalerQuality.Nearest),
            new ScalerQualityOption("Bilinear (recommended)", ScalerQuality.Bilinear),
            new ScalerQualityOption("Bicubic (sharper text)", ScalerQuality.Bicubic),
            new ScalerQualityOption("Lanczos (sharpest)", ScalerQuality.Lanczos),
        };

        QualityPresets = new[]
        {
            new QualityPreset("Readable",  TargetHeight: 1080, FrameRate: 30,  Bitrate:  8_000_000, KeyframeIntervalSeconds: 2.0, Scaler: ScalerQuality.Bicubic),
            new QualityPreset("Smooth",    TargetHeight: 1080, FrameRate: 60,  Bitrate: 12_000_000, KeyframeIntervalSeconds: 2.0, Scaler: ScalerQuality.Bilinear),
            new QualityPreset("High FPS",  TargetHeight: 1440, FrameRate: 120, Bitrate: 25_000_000, KeyframeIntervalSeconds: 1.0, Scaler: ScalerQuality.Bilinear),
            new QualityPreset("4K Cinema", TargetHeight: 2160, FrameRate: 30,  Bitrate: 40_000_000, KeyframeIntervalSeconds: 2.0, Scaler: ScalerQuality.Bicubic),
            new QualityPreset("Potato",    TargetHeight: 720,  FrameRate: 30,  Bitrate:  3_000_000, KeyframeIntervalSeconds: 2.0, Scaler: ScalerQuality.Bilinear),
        };

        var catalog = App.VideoCodecCatalog ?? new VideoCodecCatalog();
        var available = new HashSet<VideoCodec>(catalog.AvailableCodecs);
        CodecOptions = new[]
        {
            BuildCodecOption(VideoCodec.Vp8, "VP8 (software, universal)", available),
            BuildCodecOption(VideoCodec.H264, "H.264 (hardware via Media Foundation)", available),
            BuildCodecOption(VideoCodec.Av1, "AV1 (hardware, coming soon)", available),
        };

        // Seed the UI fields from the persisted settings.
        _selectedTargetHeight = TargetHeightOptions.FirstOrDefault(o => o.Height == _settings.Video.TargetHeight)
            ?? TargetHeightOptions.First(o => o.Height == 1080);
        _frameRate = Math.Clamp(_settings.Video.TargetFrameRate, 10, 240);
        _bitrate = Math.Clamp(_settings.Video.TargetBitrate, MinBitrateBps, MaxBitrateBps);
        _bitrateSliderValue = BpsToSliderValue(_bitrate);
        _keyframeIntervalSeconds = Math.Clamp(_settings.Video.KeyframeIntervalSeconds, 0.5, 10.0);
        _selectedScalerQuality = ScalerQualityOptions.FirstOrDefault(o => o.Quality == _settings.Video.ScalerQuality)
            ?? ScalerQualityOptions[1];
        _selectedCodec = CodecOptions.FirstOrDefault(c => c.Codec == _settings.Video.Codec && c.IsAvailable)
            ?? CodecOptions.First(c => c.IsAvailable);
    }

    public IReadOnlyList<TargetHeightOption> TargetHeightOptions { get; }

    public IReadOnlyList<ScalerQualityOption> ScalerQualityOptions { get; }

    public IReadOnlyList<QualityPreset> QualityPresets { get; }

    public IReadOnlyList<CodecOption> CodecOptions { get; }

    [ObservableProperty]
    private TargetHeightOption _selectedTargetHeight;

    [ObservableProperty]
    private int _frameRate;

    /// <summary>Real bitrate in bits per second. Derived from the log-scale
    /// slider value in <see cref="BitrateSliderValue"/>.</summary>
    [ObservableProperty]
    private int _bitrate;

    /// <summary>Slider position in [0.0, 1.0] mapped to [500 Kbps, 100 Mbps]
    /// on a log scale. XAML binds to this; setter updates <see cref="Bitrate"/>
    /// as a side effect so the display label and the persisted value stay in
    /// sync.</summary>
    [ObservableProperty]
    private double _bitrateSliderValue;

    [ObservableProperty]
    private double _keyframeIntervalSeconds;

    [ObservableProperty]
    private ScalerQualityOption _selectedScalerQuality;

    [ObservableProperty]
    private CodecOption _selectedCodec;

    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>Human-readable form of <see cref="Bitrate"/> for the slider
    /// label — e.g. "8.5 Mbps".</summary>
    public string BitrateDisplay => FormatBitrate(Bitrate);

    partial void OnBitrateSliderValueChanged(double value)
    {
        Bitrate = SliderValueToBps(value);
    }

    partial void OnBitrateChanged(int value)
    {
        OnPropertyChanged(nameof(BitrateDisplay));
    }

    [RelayCommand]
    private void ApplyPreset(QualityPreset preset)
    {
        if (preset is null) return;

        SelectedTargetHeight = TargetHeightOptions.FirstOrDefault(o => o.Height == preset.TargetHeight)
            ?? SelectedTargetHeight;
        FrameRate = preset.FrameRate;
        BitrateSliderValue = BpsToSliderValue(preset.Bitrate);
        KeyframeIntervalSeconds = preset.KeyframeIntervalSeconds;
        SelectedScalerQuality = ScalerQualityOptions.FirstOrDefault(o => o.Quality == preset.Scaler)
            ?? SelectedScalerQuality;
    }

    [RelayCommand]
    private void Save()
    {
        if (!SelectedCodec.IsAvailable)
        {
            StatusMessage = $"{SelectedCodec.DisplayName} cannot be used on this machine.";
            return;
        }

        _settings.Video.TargetHeight = SelectedTargetHeight.Height;
        _settings.Video.TargetFrameRate = FrameRate;
        _settings.Video.TargetBitrate = Bitrate;
        _settings.Video.KeyframeIntervalSeconds = KeyframeIntervalSeconds;
        _settings.Video.ScalerQuality = SelectedScalerQuality.Quality;
        _settings.Video.Codec = SelectedCodec.Codec;

        try
        {
            _store.Save(_settings);
            StatusMessage = "Saved.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not save: {ex.Message}";
            return;
        }

        try { _onSaved?.Invoke(); } catch { /* hook failures shouldn't break save */ }
    }

    [RelayCommand]
    private void GoBack()
    {
        _navigation.NavigateTo(_backFactory());
    }

    private static CodecOption BuildCodecOption(VideoCodec codec, string label, HashSet<VideoCodec> available)
    {
        var isAvailable = available.Contains(codec);
        var display = isAvailable ? label : $"{label} — not available";
        return new CodecOption(codec, display, isAvailable);
    }

    // Log-scale mapping: slider 0.0 = MinBitrateBps, 1.0 = MaxBitrateBps, and
    // each 0.1 step roughly doubles — so dragging feels linear regardless of
    // where you are on the curve.
    private static double BpsToSliderValue(int bps)
    {
        var clamped = Math.Clamp(bps, MinBitrateBps, MaxBitrateBps);
        var lnMin = Math.Log(MinBitrateBps);
        var lnMax = Math.Log(MaxBitrateBps);
        return (Math.Log(clamped) - lnMin) / (lnMax - lnMin);
    }

    private static int SliderValueToBps(double value)
    {
        var t = Math.Clamp(value, 0.0, 1.0);
        var lnMin = Math.Log(MinBitrateBps);
        var lnMax = Math.Log(MaxBitrateBps);
        var raw = Math.Exp(lnMin + t * (lnMax - lnMin));
        // Round to the nearest 100 Kbps so the display doesn't wiggle by a
        // few bps on each slider tick.
        return (int)(Math.Round(raw / 100_000) * 100_000);
    }

    private static string FormatBitrate(int bps)
    {
        if (bps >= 1_000_000)
        {
            return $"{bps / 1_000_000.0:0.0} Mbps";
        }
        return $"{bps / 1_000.0:0} Kbps";
    }
}

public sealed record TargetHeightOption(string DisplayName, int Height);

public sealed record ScalerQualityOption(string DisplayName, ScalerQuality Quality);

public sealed record QualityPreset(
    string Name,
    int TargetHeight,
    int FrameRate,
    int Bitrate,
    double KeyframeIntervalSeconds,
    ScalerQuality Scaler);

public sealed record CodecOption(VideoCodec Codec, string DisplayName, bool IsAvailable);
