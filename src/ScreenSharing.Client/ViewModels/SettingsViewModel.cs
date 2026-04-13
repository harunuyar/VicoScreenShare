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
/// Resolution and frame rate are exposed as dropdown presets rather than raw
/// sliders so users can't land on odd values that libvpx or the downscaler
/// would round anyway.
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
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

        ResolutionPresets = new[]
        {
            new ResolutionPreset("480p  (854 x 480)", 854, 480),
            new ResolutionPreset("720p  (1280 x 720)", 1280, 720),
            new ResolutionPreset("1080p (1920 x 1080)", 1920, 1080),
            new ResolutionPreset("1440p (2560 x 1440)", 2560, 1440),
        };
        FrameRatePresets = new[] { 15, 24, 30, 60 };
        BitratePresets = new[]
        {
            new BitratePreset("2 Mbps (low)", 2_000_000),
            new BitratePreset("5 Mbps (720p good)", 5_000_000),
            new BitratePreset("10 Mbps (1080p good)", 10_000_000),
            new BitratePreset("15 Mbps (1080p high)", 15_000_000),
            new BitratePreset("25 Mbps (1440p good)", 25_000_000),
            new BitratePreset("50 Mbps (1440p high)", 50_000_000),
        };

        // Build codec options from the host-registered catalog so entries the
        // current machine can't support are clearly marked. Order matters —
        // VP8 first (always available) then H.264 then AV1, so the user sees
        // the universal fallback at the top of the list.
        var catalog = App.VideoCodecCatalog ?? new VideoCodecCatalog();
        var available = new HashSet<VideoCodec>(catalog.AvailableCodecs);
        CodecOptions = new[]
        {
            BuildCodecOption(VideoCodec.Vp8, "VP8 (software, universal)", available),
            BuildCodecOption(VideoCodec.H264, "H.264 (hardware via Media Foundation)", available),
            BuildCodecOption(VideoCodec.Av1, "AV1 (hardware, coming soon)", available),
        };

        _selectedResolution = ResolutionPresets.FirstOrDefault(
            p => p.Width == _settings.Video.MaxEncoderWidth && p.Height == _settings.Video.MaxEncoderHeight)
            ?? ResolutionPresets[1];
        _selectedFrameRate = FrameRatePresets.Contains(_settings.Video.TargetFrameRate)
            ? _settings.Video.TargetFrameRate
            : 30;
        _selectedBitrate = BitratePresets.FirstOrDefault(b => b.Bitrate == _settings.Video.TargetBitrate)
            ?? BitratePresets[2];
        _selectedCodec = CodecOptions.FirstOrDefault(c => c.Codec == _settings.Video.Codec && c.IsAvailable)
            ?? CodecOptions.First(c => c.IsAvailable);
    }

    public IReadOnlyList<ResolutionPreset> ResolutionPresets { get; }

    public IReadOnlyList<int> FrameRatePresets { get; }

    public IReadOnlyList<BitratePreset> BitratePresets { get; }

    public IReadOnlyList<CodecOption> CodecOptions { get; }

    [ObservableProperty]
    private ResolutionPreset _selectedResolution;

    [ObservableProperty]
    private int _selectedFrameRate;

    [ObservableProperty]
    private BitratePreset _selectedBitrate;

    [ObservableProperty]
    private CodecOption _selectedCodec;

    [ObservableProperty]
    private string? _statusMessage;

    [RelayCommand]
    private void Save()
    {
        // Refuse to persist a codec the current host can't actually use.
        // Without this guard, the ComboBox will happily write an unavailable
        // codec into the settings file and the ctor filter on reload flips it
        // back to VP8 silently — which reads as "my setting wasn't saved."
        if (!SelectedCodec.IsAvailable)
        {
            StatusMessage = $"{SelectedCodec.DisplayName} cannot be used on this machine.";
            return;
        }

        _settings.Video.MaxEncoderWidth = SelectedResolution.Width;
        _settings.Video.MaxEncoderHeight = SelectedResolution.Height;
        _settings.Video.TargetFrameRate = SelectedFrameRate;
        _settings.Video.TargetBitrate = SelectedBitrate.Bitrate;
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

        // If the caller (typically RoomViewModel.ShowSettings) registered a
        // post-save hook, run it now so it can apply the change in place —
        // e.g. rebuild the media graph with the newly-picked codec without
        // forcing the user to leave and rejoin the room.
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
}

public sealed record ResolutionPreset(string DisplayName, int Width, int Height);

public sealed record BitratePreset(string DisplayName, int Bitrate);

public sealed record CodecOption(VideoCodec Codec, string DisplayName, bool IsAvailable);
