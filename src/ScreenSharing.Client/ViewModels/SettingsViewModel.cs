using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenSharing.Client.Media;
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

        _selectedResolution = ResolutionPresets.FirstOrDefault(
            p => p.Width == _settings.Video.MaxEncoderWidth && p.Height == _settings.Video.MaxEncoderHeight)
            ?? ResolutionPresets[1];
        _selectedFrameRate = FrameRatePresets.Contains(_settings.Video.TargetFrameRate)
            ? _settings.Video.TargetFrameRate
            : 30;
    }

    public IReadOnlyList<ResolutionPreset> ResolutionPresets { get; }

    public IReadOnlyList<int> FrameRatePresets { get; }

    [ObservableProperty]
    private ResolutionPreset _selectedResolution;

    [ObservableProperty]
    private int _selectedFrameRate;

    [ObservableProperty]
    private string? _statusMessage;

    [RelayCommand]
    private void Save()
    {
        _settings.Video.MaxEncoderWidth = SelectedResolution.Width;
        _settings.Video.MaxEncoderHeight = SelectedResolution.Height;
        _settings.Video.TargetFrameRate = SelectedFrameRate;

        try
        {
            _store.Save(_settings);
            StatusMessage = "Saved. New settings apply next time you share.";
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
}

public sealed record ResolutionPreset(string DisplayName, int Width, int Height);
