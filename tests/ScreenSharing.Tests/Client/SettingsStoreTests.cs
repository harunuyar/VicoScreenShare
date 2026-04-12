using FluentAssertions;
using ScreenSharing.Client;
using ScreenSharing.Client.Media;
using ScreenSharing.Client.Services;

namespace ScreenSharing.Tests.Client;

public class SettingsStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;

    public SettingsStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ScreenSharing.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void LoadOrCreate_returns_defaults_when_file_missing()
    {
        var store = new SettingsStore(_settingsPath);
        var settings = store.LoadOrCreate();

        settings.Video.MaxEncoderWidth.Should().Be(1280);
        settings.Video.MaxEncoderHeight.Should().Be(720);
        settings.Video.TargetFrameRate.Should().Be(30);
        settings.ServerUri.Should().Be(new Uri("ws://localhost:5000/ws"));
        File.Exists(_settingsPath).Should().BeFalse("LoadOrCreate only persists on Save");
    }

    [Fact]
    public void Save_then_LoadOrCreate_roundtrips_video_settings()
    {
        var store = new SettingsStore(_settingsPath);
        store.Save(new ClientSettings
        {
            ServerUri = new Uri("ws://example.test:9000/ws"),
            Video = new VideoSettings
            {
                MaxEncoderWidth = 1920,
                MaxEncoderHeight = 1080,
                TargetFrameRate = 60,
            },
        });

        var loaded = new SettingsStore(_settingsPath).LoadOrCreate();

        loaded.ServerUri.Should().Be(new Uri("ws://example.test:9000/ws"));
        loaded.Video.MaxEncoderWidth.Should().Be(1920);
        loaded.Video.MaxEncoderHeight.Should().Be(1080);
        loaded.Video.TargetFrameRate.Should().Be(60);
    }

    [Fact]
    public void Save_overwrites_existing_settings()
    {
        var store = new SettingsStore(_settingsPath);
        store.Save(new ClientSettings { Video = new VideoSettings { TargetFrameRate = 30 } });
        store.Save(new ClientSettings { Video = new VideoSettings { TargetFrameRate = 15 } });

        var loaded = new SettingsStore(_settingsPath).LoadOrCreate();
        loaded.Video.TargetFrameRate.Should().Be(15);
    }

    [Fact]
    public void Save_creates_directory_if_missing()
    {
        var nestedPath = Path.Combine(_tempDir, "subdir", "nested", "settings.json");
        var store = new SettingsStore(nestedPath);
        store.Save(new ClientSettings());

        File.Exists(nestedPath).Should().BeTrue();
    }

    [Fact]
    public void LoadOrCreate_recovers_from_corrupted_file()
    {
        File.WriteAllText(_settingsPath, "{ not valid json ");
        var store = new SettingsStore(_settingsPath);
        var settings = store.LoadOrCreate();

        settings.Video.MaxEncoderWidth.Should().Be(1280);
        settings.Video.TargetFrameRate.Should().Be(30);
    }

    [Fact]
    public void Save_writes_atomically_without_leaving_temp_file()
    {
        var store = new SettingsStore(_settingsPath);
        store.Save(new ClientSettings());

        File.Exists(_settingsPath).Should().BeTrue();
        File.Exists(_settingsPath + ".tmp").Should().BeFalse(
            "atomic save should rename the temp file onto the target path");
    }
}
