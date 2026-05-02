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
using VicoScreenShare.Client.Windows.Media.Codecs.Nvenc;

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
            new TargetHeightOption("1440p", 1440),
            new TargetHeightOption("1080p", 1080),
            new TargetHeightOption("720p", 720),
            new TargetHeightOption("540p", 540),
            new TargetHeightOption("480p", 480),
            new TargetHeightOption("360p", 360),
        };

        ScalerModeOptions = new[]
        {
            new ScalerModeOption("Bilinear (fast, recommended)", ScalerMode.Bilinear),
            new ScalerModeOption("Lanczos (sharp text, slower)", ScalerMode.Lanczos),
        };

        // Discrete frame-rate choices the dropdown offers. Cinema (24/48),
        // standard video (30/60), high-refresh gaming (120/144/240), with
        // a couple of bandwidth-saving low values for slow links.
        FrameRateOptions = new[] { 5, 15, 24, 30, 48, 60, 72, 90, 120, 144, 165, 240 };

        // Send-pacer rate cap as a multiplier on the encoder's
        // target bitrate. ×1 = pace at exactly the encoder's rate
        // (smoothest, highest one-time keyframe latency). Higher
        // multipliers shorten the keyframe-burst transit at the
        // cost of letting more of it onto the wire at once.
        SendPacingMultiplierOptions = new[] { 1, 2, 3, 4, 5 };

        // Presets named "widthP@fps" — unambiguous and matches how OBS /
        // Twitch / YouTube display quality options, which is what users
        // already know. Bitrates are H.264 screen-content targets roughly
        // aligned with Twitch's recommended gaming bitrates; user can still
        // override via the slider. 1-second keyframe interval everywhere —
        // viewers converge on join in 1 s instead of 2, and a single
        // keyframe re-sync is less painful on loss-prone links. Bilinear
        // scaler everywhere — cheap on GPU, right for game/video content;
        // users who want Lanczos for readable text can pick it in the
        // Scaler dropdown directly.
        QualityPresets = new[]
        {
            new QualityPreset("720p30",  720,  30,  4_000_000,  1.0, ScalerMode.Bilinear),
            new QualityPreset("1080p30", 1080, 30,  6_000_000,  1.0, ScalerMode.Bilinear),
            new QualityPreset("1080p60", 1080, 60,  10_000_000, 1.0, ScalerMode.Bilinear),
            new QualityPreset("1080p120",1080, 120, 16_000_000, 1.0, ScalerMode.Bilinear),
            new QualityPreset("1440p60", 1440, 60,  18_000_000, 1.0, ScalerMode.Bilinear),
            new QualityPreset("1440p120",1440, 120, 28_000_000, 1.0, ScalerMode.Bilinear),
            new QualityPreset("2160p30", 2160, 30,  24_000_000, 1.0, ScalerMode.Bilinear),
            new QualityPreset("2160p60", 2160, 60,  40_000_000, 1.0, ScalerMode.Bilinear),
        };

        var catalog = ClientHost.VideoCodecCatalog ?? new VideoCodecCatalog();
        var available = new HashSet<VideoCodec>(catalog.AvailableCodecs);
        CodecOptions = new[]
        {
            BuildCodecOption(VideoCodec.Vp8, "VP8 (software, universal)", available),
            BuildCodecOption(VideoCodec.H264, "H.264 (hardware via Media Foundation)", available),
            BuildCodecOption(VideoCodec.Av1, "AV1 (NVIDIA RTX 40+, Intel Arc, AMD RDNA 3+)", available),
        };

        _selectedTargetHeight = TargetHeightOptions.FirstOrDefault(o => o.Height == _settings.Video.TargetHeight)
            ?? TargetHeightOptions.First(o => o.Height == 1080);
        _frameRate = SnapToFrameRateOption(_settings.Video.TargetFrameRate);
        _bitrate = Math.Clamp(_settings.Video.TargetBitrate, MinBitrateBps, MaxBitrateBps);
        _bitrateSliderValue = BpsToSliderValue(_bitrate);
        _keyframeIntervalSeconds = Math.Clamp(_settings.Video.KeyframeIntervalSeconds, 0.5, 10.0);
        _selectedScalerMode = ScalerModeOptions.FirstOrDefault(o => o.Mode == _settings.Video.Scaler)
            ?? ScalerModeOptions[0];
        _selectedCodec = CodecOptions.FirstOrDefault(c => c.Codec == _settings.Video.Codec && c.IsAvailable)
            ?? CodecOptions.First(c => c.IsAvailable);
        _receiveBufferFrames = Math.Clamp(_settings.Video.ReceiveBufferFrames, 1, 240);
        _enableAdaptiveBitrate = _settings.Video.EnableAdaptiveBitrate;
        _enableSendPacing = _settings.Video.EnableSendPacing;
        _sendPacingBitrateMultiplier = Math.Clamp(_settings.Video.SendPacingBitrateMultiplier, 1, 5);
        _enableAdaptiveQuantization = _settings.Video.EnableAdaptiveQuantization;
        _enableEncoderLookahead = _settings.Video.EnableEncoderLookahead;
        _enableIntraRefresh = _settings.Video.EnableIntraRefresh;
        _selectedNvencPreset = NvencPresetOptions.FirstOrDefault(o => o.Level == _settings.Video.NvencPreset)
            ?? NvencPresetOptions.First(o => o.Level == 4);

        // Capability flags from the live encoder factory selector. Greys
        // the corresponding toggle when the GPU lacks the feature.
        var nvencCaps = ResolveNvencCapabilities();
        IsNvencAvailable = nvencCaps.IsAvailable;
        _nvencCaps = nvencCaps;
        NvencUnavailableReason = nvencCaps.IsAvailable ? null : nvencCaps.UnavailableReason;

        // H.264 backend dropdown. NVENC option is only present when the GPU
        // supports it; on non-NVIDIA hosts the user only sees Auto + MFT
        // (Auto resolves to MFT in that case).
        var backendOptions = new List<H264BackendOption>
        {
            new(H264EncoderBackend.Auto, IsNvencAvailable
                ? "Auto — NVENC SDK (recommended)"
                : "Auto — Media Foundation"),
            new(H264EncoderBackend.Mft, "Media Foundation (legacy, universal)"),
        };
        if (IsNvencAvailable)
        {
            backendOptions.Insert(2, new H264BackendOption(H264EncoderBackend.NvencSdk, "Direct NVENC SDK"));
        }
        H264BackendOptions = backendOptions;
        _selectedH264Backend = H264BackendOptions.FirstOrDefault(o => o.Backend == _settings.Video.H264Backend)
            ?? H264BackendOptions[0];

        // AV1 encoder backend dropdown. NVENC SDK option appears only when
        // the publisher's GPU has NVENC AV1 silicon (RTX 40+); MFT option
        // appears only when an Intel / AMD AV1 encoder MFT is registered.
        // Auto resolves to whichever is available, with NVENC preferred.
        IsNvencAv1Available = nvencCaps.IsAv1Available;
        IsMftAv1EncoderAvailable = VicoScreenShare.Client.Windows.Media.Codecs.MediaFoundationAv1Encoder.HasAv1EncoderInstalled();
        var av1EncoderOptions = new List<Av1EncoderBackendOption>
        {
            new(Av1EncoderBackend.Auto, IsNvencAv1Available
                ? "Auto — NVENC SDK (recommended)"
                : IsMftAv1EncoderAvailable
                    ? "Auto — Media Foundation"
                    : "Auto — (no AV1 encoder available)"),
        };
        if (IsMftAv1EncoderAvailable)
        {
            av1EncoderOptions.Add(new Av1EncoderBackendOption(Av1EncoderBackend.Mft, "Media Foundation (universal driver MFT)"));
        }
        if (IsNvencAv1Available)
        {
            av1EncoderOptions.Add(new Av1EncoderBackendOption(Av1EncoderBackend.NvencSdk, "Direct NVENC SDK"));
        }
        Av1EncoderBackendOptions = av1EncoderOptions;
        _selectedAv1EncoderBackend = Av1EncoderBackendOptions.FirstOrDefault(o => o.Backend == _settings.Video.Av1Backend)
            ?? Av1EncoderBackendOptions[0];

        // AV1 decoder backend dropdown. NVDEC option appears only when
        // the viewer's GPU has NVDEC AV1 silicon (RTX 30 / Volta+); on
        // hosts without it the user sees Auto + MFT (Auto resolves to
        // MFT in that case).
        var nvdecCaps = VicoScreenShare.Client.Windows.Media.Codecs.Nvdec.NvDecCapabilities.Probe();
        IsNvdecAv1Available = nvdecCaps.IsAv1Available;
        var av1DecoderOptions = new List<Av1DecoderBackendOption>
        {
            new(Av1DecoderBackend.Auto, IsNvdecAv1Available
                ? "Auto — NVDEC (recommended)"
                : "Auto — Media Foundation"),
            new(Av1DecoderBackend.Mft, "Media Foundation (universal, slower)"),
        };
        if (IsNvdecAv1Available)
        {
            av1DecoderOptions.Insert(2, new Av1DecoderBackendOption(Av1DecoderBackend.Nvdec, "NVDEC (direct cuvid)"));
        }
        Av1DecoderBackendOptions = av1DecoderOptions;
        _selectedAv1DecoderBackend = Av1DecoderBackendOptions.FirstOrDefault(o => o.Backend == _settings.Video.Av1DecoderBackend)
            ?? Av1DecoderBackendOptions[0];

        // Audio settings. Bitrate combo is fixed-set so the UI stays
        // simple; people who want to experiment with 160 kbps Opus can
        // edit the JSON settings file directly.
        AudioBitrateOptions = new[]
        {
            new AudioBitrateOption("64 Kbps — voice-leaning", 64_000),
            new AudioBitrateOption("96 Kbps — mixed content (recommended)", 96_000),
            new AudioBitrateOption("128 Kbps — high quality music", 128_000),
            new AudioBitrateOption("192 Kbps — near-transparent", 192_000),
        };
        _forceSystemAudio = _settings.Audio.ForceSystemAudio;
        _audioStereo = _settings.Audio.Stereo;
        _selectedAudioBitrate = AudioBitrateOptions.FirstOrDefault(o => o.Bitrate == _settings.Audio.TargetBitrate)
            ?? AudioBitrateOptions[1];

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
    public IReadOnlyList<int> FrameRateOptions { get; }
    public IReadOnlyList<int> SendPacingMultiplierOptions { get; }

    private int SnapToFrameRateOption(int requested)
    {
        var nearest = FrameRateOptions[0];
        var bestDelta = Math.Abs(requested - nearest);
        foreach (var option in FrameRateOptions)
        {
            var delta = Math.Abs(requested - option);
            if (delta < bestDelta)
            {
                nearest = option;
                bestDelta = delta;
            }
        }
        return nearest;
    }

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
    private bool _enableAdaptiveBitrate;

    [ObservableProperty]
    private bool _enableSendPacing;

    [ObservableProperty]
    private int _sendPacingBitrateMultiplier;

    [ObservableProperty]
    private bool _enableAdaptiveQuantization;

    [ObservableProperty]
    private bool _enableEncoderLookahead;

    [ObservableProperty]
    private bool _enableIntraRefresh;

    [ObservableProperty]
    private NvencPresetOption _selectedNvencPreset = null!;

    public IReadOnlyList<NvencPresetOption> NvencPresetOptions { get; } = new[]
    {
        new NvencPresetOption(1, "P1 — Fastest (lowest quality)"),
        new NvencPresetOption(2, "P2 — Faster"),
        new NvencPresetOption(3, "P3 — Fast"),
        new NvencPresetOption(4, "P4 — Balanced (default)"),
        new NvencPresetOption(5, "P5 — Slow"),
        new NvencPresetOption(6, "P6 — Slower"),
        new NvencPresetOption(7, "P7 — Slowest (highest quality)"),
    };

    [ObservableProperty]
    private H264BackendOption _selectedH264Backend = null!;

    /// <summary>The H.264 backend choices we offer. NVENC SDK appears only
    /// when the GPU supports it; on other hosts the user sees Auto + MFT.</summary>
    public IReadOnlyList<H264BackendOption> H264BackendOptions { get; }

    [ObservableProperty]
    private Av1DecoderBackendOption _selectedAv1DecoderBackend = null!;

    /// <summary>The AV1 decoder backend choices we offer. NVDEC appears
    /// only on RTX 30 / Volta+ NVIDIA GPUs; on other hosts the user
    /// sees Auto + MFT (Auto resolves to MFT in that case).</summary>
    public IReadOnlyList<Av1DecoderBackendOption> Av1DecoderBackendOptions { get; }

    /// <summary>True when this machine has NVDEC AV1 silicon. Settings UI
    /// can use this to grey out the dropdown's NVDEC option (it's
    /// hidden entirely today, but exposing the bool keeps the UI
    /// flexible for future tooltip or warning use).</summary>
    public bool IsNvdecAv1Available { get; }

    [ObservableProperty]
    private Av1EncoderBackendOption _selectedAv1EncoderBackend = null!;

    /// <summary>The AV1 encoder backend choices we offer. NVENC SDK appears
    /// only on RTX 40+ NVIDIA GPUs; MFT appears only when an Intel / AMD
    /// AV1 encoder MFT is registered (Arc / Xe2 iGPU, RDNA 3+).</summary>
    public IReadOnlyList<Av1EncoderBackendOption> Av1EncoderBackendOptions { get; }

    /// <summary>True when this machine has NVENC AV1 silicon (RTX 40+).</summary>
    public bool IsNvencAv1Available { get; }

    /// <summary>True when an AV1 encoder MFT is registered (Intel Arc / Xe2
    /// iGPU, AMD RDNA 3+, etc.). Microsoft does not ship a software AV1
    /// encoder MFT, so on machines without one of these GPUs this is
    /// false and the MFT option is hidden from the dropdown.</summary>
    public bool IsMftAv1EncoderAvailable { get; }

    /// <summary>True when the NVENC SDK encoder backend is available
    /// on this machine. Settings UI greys NVENC-only toggles otherwise.</summary>
    public bool IsNvencAvailable { get; }

    private readonly NvencCapabilities _nvencCaps;

    /// <summary>
    /// Lookahead capability for the currently selected codec — H.264 caps
    /// for H.264, AV1 caps for AV1. Greys the toggle when the GPU lacks
    /// the feature on the picked codec.
    /// </summary>
    public bool NvencSupportsLookahead => IsNvencAvailable && (
        SelectedCodec?.Codec == VideoCodec.Av1
            ? _nvencCaps.Av1SupportsLookahead
            : _nvencCaps.SupportsLookahead);

    /// <summary>Same idea for intra-refresh — AV1 caps when AV1 is picked.</summary>
    public bool NvencSupportsIntraRefresh => IsNvencAvailable && (
        SelectedCodec?.Codec == VideoCodec.Av1
            ? _nvencCaps.Av1SupportsIntraRefresh
            : _nvencCaps.SupportsIntraRefresh);

    /// <summary>Tooltip text for greyed-out NVENC controls.</summary>
    public string? NvencUnavailableReason { get; }

    /// <summary>
    /// True when the currently-selected codec resolves to a NVENC SDK
    /// path on this machine. Drives the visibility of the "NVENC quality"
    /// card so MFT-only sessions don't show it. Both H.264 and AV1 have
    /// the MFT-or-NVENC fork via their respective backend dropdowns.
    /// </summary>
    public bool IsNvencActiveForQualityCard
    {
        get
        {
            if (SelectedCodec?.Codec == VideoCodec.Av1)
            {
                if (!IsNvencAv1Available)
                {
                    return false;
                }
                var av1Pref = SelectedAv1EncoderBackend?.Backend ?? Av1EncoderBackend.Auto;
                // Auto on a NVENC-capable box stays NVENC. Mft forces MFT.
                // NvencSdk forces NVENC.
                return av1Pref != Av1EncoderBackend.Mft;
            }
            if (SelectedCodec?.Codec == VideoCodec.H264)
            {
                if (!IsNvencAvailable)
                {
                    return false;
                }
                return (SelectedH264Backend?.Backend ?? H264EncoderBackend.Auto) != H264EncoderBackend.Mft;
            }
            return false;
        }
    }

    partial void OnSelectedH264BackendChanged(H264BackendOption value)
    {
        OnPropertyChanged(nameof(IsNvencActiveForQualityCard));
    }

    partial void OnSelectedAv1EncoderBackendChanged(Av1EncoderBackendOption value)
    {
        OnPropertyChanged(nameof(IsNvencActiveForQualityCard));
    }

    partial void OnSelectedCodecChanged(CodecOption value)
    {
        OnPropertyChanged(nameof(IsNvencActiveForQualityCard));
        OnPropertyChanged(nameof(IsH264Selected));
        OnPropertyChanged(nameof(IsAv1Selected));
        OnPropertyChanged(nameof(NvencSupportsLookahead));
        OnPropertyChanged(nameof(NvencSupportsIntraRefresh));
    }

    /// <summary>True when the codec dropdown is on H.264. Drives the
    /// visibility of the H.264 backend dropdown.</summary>
    public bool IsH264Selected => SelectedCodec?.Codec == VideoCodec.H264;

    /// <summary>True when the codec dropdown is on AV1. Drives the
    /// visibility of the AV1 decoder backend dropdown.</summary>
    public bool IsAv1Selected => SelectedCodec?.Codec == VideoCodec.Av1;

    public IReadOnlyList<AudioBitrateOption> AudioBitrateOptions { get; }

    [ObservableProperty]
    private bool _forceSystemAudio;

    [ObservableProperty]
    private bool _audioStereo;

    [ObservableProperty]
    private AudioBitrateOption _selectedAudioBitrate;

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
        _settings.Video.EnableAdaptiveBitrate = EnableAdaptiveBitrate;
        _settings.Video.EnableSendPacing = EnableSendPacing;
        _settings.Video.SendPacingBitrateMultiplier = Math.Clamp(SendPacingBitrateMultiplier, 1, 5);
        _settings.Video.EnableAdaptiveQuantization = EnableAdaptiveQuantization;
        _settings.Video.EnableEncoderLookahead = EnableEncoderLookahead;
        _settings.Video.EnableIntraRefresh = EnableIntraRefresh;
        _settings.Video.H264Backend = SelectedH264Backend?.Backend ?? H264EncoderBackend.Auto;
        _settings.Video.Av1DecoderBackend = SelectedAv1DecoderBackend?.Backend ?? Av1DecoderBackend.Auto;
        _settings.Video.Av1Backend = SelectedAv1EncoderBackend?.Backend ?? Av1EncoderBackend.Auto;
        _settings.Video.NvencPreset = SelectedNvencPreset?.Level ?? 4;

        _settings.Audio.ForceSystemAudio = ForceSystemAudio;
        _settings.Audio.Stereo = AudioStereo;
        _settings.Audio.TargetBitrate = SelectedAudioBitrate.Bitrate;

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

    /// <summary>
    /// Probe NVENC capability via the shared device, falling back to an
    /// "unavailable" record if the device isn't ready or NVENC isn't on
    /// this host. Drives the IsNvencAvailable bindings used by the UI to
    /// grey NVENC-only toggles.
    /// </summary>
    private static NvencCapabilities ResolveNvencCapabilities()
    {
        var device = App.SharedDevices?.Device;
        if (device is null)
        {
            return NvencCapabilities.Probe(null);
        }
        return NvencCapabilities.Probe(device);
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
public sealed record H264BackendOption(H264EncoderBackend Backend, string DisplayName);

public sealed record Av1DecoderBackendOption(Av1DecoderBackend Backend, string DisplayName);
public sealed record Av1EncoderBackendOption(Av1EncoderBackend Backend, string DisplayName);
public sealed record NvencPresetOption(int Level, string DisplayName);
public sealed record AudioBitrateOption(string DisplayName, int Bitrate);
