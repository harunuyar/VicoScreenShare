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

    public SettingsViewModel(
        ClientSettings settings,
        SettingsStore store,
        NavigationService navigation,
        Func<ViewModelBase> backFactory)
    {
        _settings = settings;
        _store = store;
        _navigation = navigation;
        _backFactory = backFactory;

        ResolutionPresets = new[]
        {
            new ResolutionPreset("480p  (854 x 480)", 854, 480),
            new ResolutionPreset("720p  (1280 x 720)", 1280, 720),
            new ResolutionPreset("1080p (1920 x 1080)", 1920, 1080),
            new ResolutionPreset("1440p (2560 x 1440)", 2560, 1440),
        };
        FrameRatePresets = new[] { 15, 24, 30, 60 };

        // Build codec options from the host-registered catalog so entries the
        // current machine can't support are clearly marked. Order matters —
        // VP8 first (always available) then H.264 then AV1, so the user sees
        // the universal fallback at the top of the list.
        var catalog = App.VideoCodecCatalog ?? new VideoCodecCatalog();
        var available = new HashSet<VideoCodec>(catalog.AvailableCodecs);
        CodecOptions = new[]
        {
            BuildCodecOption(VideoCodec.Vp8, "VP8 (software, universal)", available),
            BuildCodecOption(VideoCodec.H264, "H.264 (hardware via FFmpeg)", available),
            BuildCodecOption(VideoCodec.Av1, "AV1 (hardware, coming soon)", available),
        };

        _selectedResolution = ResolutionPresets.FirstOrDefault(
            p => p.Width == _settings.Video.MaxEncoderWidth && p.Height == _settings.Video.MaxEncoderHeight)
            ?? ResolutionPresets[1];
        _selectedFrameRate = FrameRatePresets.Contains(_settings.Video.TargetFrameRate)
            ? _settings.Video.TargetFrameRate
            : 30;
        _selectedCodec = CodecOptions.FirstOrDefault(c => c.Codec == _settings.Video.Codec && c.IsAvailable)
            ?? CodecOptions.First(c => c.IsAvailable);
    }

    public IReadOnlyList<ResolutionPreset> ResolutionPresets { get; }

    public IReadOnlyList<int> FrameRatePresets { get; }

    public IReadOnlyList<CodecOption> CodecOptions { get; }

    [ObservableProperty]
    private ResolutionPreset _selectedResolution;

    [ObservableProperty]
    private int _selectedFrameRate;

    [ObservableProperty]
    private CodecOption _selectedCodec;

    [ObservableProperty]
    private string? _statusMessage;

    [RelayCommand]
    private void Save()
    {
        _settings.Video.MaxEncoderWidth = SelectedResolution.Width;
        _settings.Video.MaxEncoderHeight = SelectedResolution.Height;
        _settings.Video.TargetFrameRate = SelectedFrameRate;
        _settings.Video.Codec = SelectedCodec.Codec;

        try
        {
            _store.Save(_settings);
            StatusMessage = "Saved. Codec changes take effect next time you join a room.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not save: {ex.Message}";
        }
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

public sealed record CodecOption(VideoCodec Codec, string DisplayName, bool IsAvailable);
