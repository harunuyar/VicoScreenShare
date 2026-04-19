namespace VicoScreenShare.Desktop.App.ViewModels;

using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VicoScreenShare.Client;
using VicoScreenShare.Client.Media;
using VicoScreenShare.Client.Media.Codecs;
using VicoScreenShare.Client.Services;

public sealed partial class SettingsViewModel : ViewModelBase
{
    private const int MinBitrateBps = 500_000;
    private const int MaxBitrateBps = 100_000_000;

    private readonly ClientSettings _settings;
    private readonly SettingsStore _store;
    private readonly Action? _onSaved;

    /// <summary>
    /// Fired when the user asks to dismiss the Settings dialog (clicks the
    /// back/close chevron). The hosting <c>SettingsWindow</c> listens and
    /// calls <see cref="System.Windows.Window.Close"/>.
    /// </summary>
    public event Action? CloseRequested;

    public SettingsViewModel(
        ClientSettings settings,
        SettingsStore store,
        Action? onSaved = null)
    {
        _settings = settings;
        _store = store;
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

        ScalerModeOptions = new[]
        {
            new ScalerModeOption("Bilinear (fast, recommended)", ScalerMode.Bilinear),
            new ScalerModeOption("Lanczos (sharp text, slower)", ScalerMode.Lanczos),
        };

        QualityPresets = new[]
        {
            new QualityPreset("Readable",  1080, 30,  8_000_000,  2.0, ScalerMode.Lanczos),
            new QualityPreset("Smooth",    1080, 60,  12_000_000, 2.0, ScalerMode.Bilinear),
            new QualityPreset("High FPS",  1440, 120, 25_000_000, 1.0, ScalerMode.Bilinear),
            new QualityPreset("4K Cinema", 2160, 30,  40_000_000, 2.0, ScalerMode.Lanczos),
            new QualityPreset("Potato",    720,  30,  3_000_000,  2.0, ScalerMode.Bilinear),
        };

        var catalog = ClientHost.VideoCodecCatalog ?? new VideoCodecCatalog();
        var available = new HashSet<VideoCodec>(catalog.AvailableCodecs);
        CodecOptions = new[]
        {
            BuildCodecOption(VideoCodec.Vp8, "VP8 (software, universal)", available),
            BuildCodecOption(VideoCodec.H264, "H.264 (hardware via Media Foundation)", available),
            BuildCodecOption(VideoCodec.Av1, "AV1 (hardware, coming soon)", available),
        };

        _selectedTargetHeight = TargetHeightOptions.FirstOrDefault(o => o.Height == _settings.Video.TargetHeight)
            ?? TargetHeightOptions.First(o => o.Height == 1080);
        _frameRate = Math.Clamp(_settings.Video.TargetFrameRate, 10, 240);
        _bitrate = Math.Clamp(_settings.Video.TargetBitrate, MinBitrateBps, MaxBitrateBps);
        _bitrateSliderValue = BpsToSliderValue(_bitrate);
        _keyframeIntervalSeconds = Math.Clamp(_settings.Video.KeyframeIntervalSeconds, 0.5, 10.0);
        _selectedScalerMode = ScalerModeOptions.FirstOrDefault(o => o.Mode == _settings.Video.Scaler)
            ?? ScalerModeOptions[0];
        _selectedCodec = CodecOptions.FirstOrDefault(c => c.Codec == _settings.Video.Codec && c.IsAvailable)
            ?? CodecOptions.First(c => c.IsAvailable);
        _receiveBufferFrames = Math.Clamp(_settings.Video.ReceiveBufferFrames, 1, 240);

        // Dirty tracking: any change to a bound setting flips IsDirty to
        // true so the floating Save pill becomes visible. Save() resets
        // it after a successful write.
        PropertyChanged += OnAnyPropertyChanged;
    }

    private void OnAnyPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IsDirty):
            case nameof(StatusMessage):
            case nameof(BitrateDisplay):
                return;
        }
        IsDirty = true;
    }

    public IReadOnlyList<TargetHeightOption> TargetHeightOptions { get; }
    public IReadOnlyList<ScalerModeOption> ScalerModeOptions { get; }
    public IReadOnlyList<QualityPreset> QualityPresets { get; }
    public IReadOnlyList<CodecOption> CodecOptions { get; }

    [ObservableProperty]
    private TargetHeightOption _selectedTargetHeight;

    [ObservableProperty]
    private int _frameRate;

    [ObservableProperty]
    private int _bitrate;

    [ObservableProperty]
    private double _bitrateSliderValue;

    [ObservableProperty]
    private double _keyframeIntervalSeconds;

    [ObservableProperty]
    private ScalerModeOption _selectedScalerMode;

    [ObservableProperty]
    private CodecOption _selectedCodec;

    [ObservableProperty]
    private int _receiveBufferFrames;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isDirty;

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
    private void ApplyPreset(QualityPreset? preset)
    {
        if (preset is null)
        {
            return;
        }

        SelectedTargetHeight = TargetHeightOptions.FirstOrDefault(o => o.Height == preset.TargetHeight)
            ?? SelectedTargetHeight;
        FrameRate = preset.FrameRate;
        BitrateSliderValue = BpsToSliderValue(preset.Bitrate);
        KeyframeIntervalSeconds = preset.KeyframeIntervalSeconds;
        SelectedScalerMode = ScalerModeOptions.FirstOrDefault(o => o.Mode == preset.Scaler)
            ?? SelectedScalerMode;
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
        _settings.Video.Scaler = SelectedScalerMode.Mode;
        _settings.Video.Codec = SelectedCodec.Codec;
        _settings.Video.ReceiveBufferFrames = ReceiveBufferFrames;

        try
        {
            _store.Save(_settings);
            // Update the static so newly-mounted renderers pick up the
            // change. Existing (already-running) renderers keep the
            // value they were constructed with.
            App.ReceiveBufferFrames = ReceiveBufferFrames;
            StatusMessage = "Saved.";
            IsDirty = false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not save: {ex.Message}";
            return;
        }

        try { _onSaved?.Invoke(); } catch { }
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    private static CodecOption BuildCodecOption(VideoCodec codec, string label, HashSet<VideoCodec> available)
    {
        var isAvailable = available.Contains(codec);
        var display = isAvailable ? label : $"{label} — not available";
        return new CodecOption(codec, display, isAvailable);
    }

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
public sealed record ScalerModeOption(string DisplayName, ScalerMode Mode);
public sealed record QualityPreset(
    string Name,
    int TargetHeight,
    int FrameRate,
    int Bitrate,
    double KeyframeIntervalSeconds,
    ScalerMode Scaler);
public sealed record CodecOption(VideoCodec Codec, string DisplayName, bool IsAvailable);
