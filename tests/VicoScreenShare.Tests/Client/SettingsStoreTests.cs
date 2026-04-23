namespace VicoScreenShare.Tests.Client;

using FluentAssertions;
using VicoScreenShare.Client.Media;
using VicoScreenShare.Client.Services;

public class SettingsStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;

    public SettingsStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "VicoScreenShare.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Save_then_LoadOrCreate_roundtrips_audio_settings()
    {
        var store = new SettingsStore(_settingsPath);
        store.Save(new ClientSettings
        {
            Audio = new AudioSettings
            {
                Enabled = true,
                TargetBitrate = 128_000,
                Stereo = false,
                FrameDurationMs = 20,
                Application = OpusApplicationMode.Voip,
            },
        });

        var loaded = new SettingsStore(_settingsPath).LoadOrCreate();

        loaded.Audio.Enabled.Should().BeTrue();
        loaded.Audio.TargetBitrate.Should().Be(128_000);
        loaded.Audio.Stereo.Should().BeFalse();
        loaded.Audio.FrameDurationMs.Should().Be(20);
        loaded.Audio.Application.Should().Be(OpusApplicationMode.Voip);
    }

    [Fact]
    public void LoadOrCreate_defaults_audio_to_disabled_opus_mixed()
    {
        var store = new SettingsStore(_settingsPath);
        var settings = store.LoadOrCreate();

        settings.Audio.Enabled.Should().BeFalse("shared audio is opt-in");
        settings.Audio.TargetBitrate.Should().Be(96_000);
        settings.Audio.Stereo.Should().BeTrue();
        settings.Audio.Application.Should().Be(OpusApplicationMode.GeneralAudio);
    }

    [Fact]
    public void LoadOrCreate_clamps_invalid_audio_bitrate_to_default()
    {
        // Write a settings file by hand with an out-of-band Opus bitrate;
        // LoadOrCreate must fall back to the default rather than push a
        // bad value into the encoder constructor.
        var json = """
            {
              "audioEnabled": true,
              "audioTargetBitrate": 2000,
              "audioStereo": true
            }
            """;
        File.WriteAllText(_settingsPath, json);

        var loaded = new SettingsStore(_settingsPath).LoadOrCreate();
        loaded.Audio.Enabled.Should().BeTrue();
        loaded.Audio.TargetBitrate.Should().Be(96_000, "2 kbps is below Opus's 6 kbps floor, fell back to default");
    }

    [Fact]
    public void LoadOrCreate_returns_defaults_when_file_missing()
    {
        var store = new SettingsStore(_settingsPath);
        var settings = store.LoadOrCreate();

        settings.Video.TargetHeight.Should().Be(1080);
        settings.Video.TargetFrameRate.Should().Be(60);
        settings.Video.Scaler.Should().Be(ScalerMode.Bilinear);
        settings.Connections.Should().BeEmpty("fresh install has no saved connections yet");
        settings.ActiveConnectionId.Should().BeNull();
        File.Exists(_settingsPath).Should().BeFalse("LoadOrCreate only persists on Save");
    }

    [Fact]
    public void Save_then_LoadOrCreate_roundtrips_video_settings()
    {
        var store = new SettingsStore(_settingsPath);
        store.Save(new ClientSettings
        {
            Video = new VideoSettings
            {
                TargetHeight = 1440,
                TargetFrameRate = 120,
                TargetBitrate = 25_000_000,
                KeyframeIntervalSeconds = 1.0,
                Scaler = ScalerMode.Lanczos,
            },
        });

        var loaded = new SettingsStore(_settingsPath).LoadOrCreate();

        loaded.Video.TargetHeight.Should().Be(1440);
        loaded.Video.TargetFrameRate.Should().Be(120);
        loaded.Video.TargetBitrate.Should().Be(25_000_000);
        loaded.Video.KeyframeIntervalSeconds.Should().Be(1.0);
        loaded.Video.Scaler.Should().Be(ScalerMode.Lanczos);
    }

    [Fact]
    public void Save_then_LoadOrCreate_roundtrips_connection_list()
    {
        var store = new SettingsStore(_settingsPath);
        var vps = new ServerConnection
        {
            Name = "My VPS",
            Uri = new Uri("wss://vps.example.com/ws"),
            Password = "hunter2",
        };
        var local = new ServerConnection
        {
            Name = "",
            Uri = new Uri("ws://localhost:5000/ws"),
            Password = null,
        };
        store.Save(new ClientSettings
        {
            Connections = new List<ServerConnection> { local, vps },
            ActiveConnectionId = vps.Id,
        });

        var loaded = new SettingsStore(_settingsPath).LoadOrCreate();

        loaded.Connections.Should().HaveCount(2);
        loaded.ActiveConnectionId.Should().Be(vps.Id);
        var active = loaded.ActiveConnection;
        active.Should().NotBeNull();
        active!.Name.Should().Be("My VPS");
        active.Uri.Should().Be(new Uri("wss://vps.example.com/ws"));
        active.Password.Should().Be("hunter2");
    }

    [Fact]
    public void LoadOrCreate_migrates_legacy_ServerUri_into_Connections()
    {
        // Settings files written by the pre-address-book client had a single
        // top-level serverUri field. After upgrading, that should surface as
        // one saved entry, active.
        File.WriteAllText(
            _settingsPath,
            """{ "serverUri": "wss://legacy.example.com/ws", "targetFrameRate": 60 }""");

        var loaded = new SettingsStore(_settingsPath).LoadOrCreate();

        loaded.Connections.Should().HaveCount(1, "migration synthesizes one entry from legacy serverUri");
        var entry = loaded.Connections[0];
        entry.Uri.Should().Be(new Uri("wss://legacy.example.com/ws"));
        entry.Password.Should().BeNull();
        loaded.ActiveConnectionId.Should().Be(entry.Id);
    }

    [Fact]
    public void LoadOrCreate_heals_orphaned_ActiveConnectionId()
    {
        // ActiveConnectionId points at an entry that isn't in Connections —
        // hand-edited file scenario. Fall back to first available entry.
        var rogueActive = Guid.NewGuid();
        var real = Guid.NewGuid();
        File.WriteAllText(
            _settingsPath,
            $$"""
            {
              "connections": [
                { "id": "{{real}}", "name": "Real", "uri": "ws://a:1/ws" }
              ],
              "activeConnectionId": "{{rogueActive}}"
            }
            """);

        var loaded = new SettingsStore(_settingsPath).LoadOrCreate();

        loaded.ActiveConnectionId.Should().Be(real, "orphan heals to the first remaining entry");
    }

    [Fact]
    public void LoadOrCreate_migrates_legacy_MaxEncoderHeight_to_TargetHeight()
    {
        // Settings files written by the old dropdown UI stored MaxEncoderWidth /
        // MaxEncoderHeight — users shouldn't lose their preferred resolution
        // after the slider rewrite.
        File.WriteAllText(
            _settingsPath,
            """{ "maxEncoderWidth": 1920, "maxEncoderHeight": 1080, "targetFrameRate": 60, "targetBitrate": 10000000 }""");

        var loaded = new SettingsStore(_settingsPath).LoadOrCreate();

        loaded.Video.TargetHeight.Should().Be(1080);
        loaded.Video.TargetFrameRate.Should().Be(60);
        loaded.Video.TargetBitrate.Should().Be(10_000_000);
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

        settings.Video.TargetHeight.Should().Be(1080);
        settings.Video.TargetFrameRate.Should().Be(60);
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

    [Fact]
    public void Adaptive_bitrate_knobs_round_trip_through_save_and_load()
    {
        var store = new SettingsStore(_settingsPath);
        var original = new ClientSettings
        {
            Video = new VideoSettings
            {
                EnableAdaptiveBitrate = false,
                MinAdaptiveBitrate = 800_000,
            },
        };
        store.Save(original);

        var loaded = new SettingsStore(_settingsPath).LoadOrCreate();

        loaded.Video.EnableAdaptiveBitrate.Should().BeFalse();
        loaded.Video.MinAdaptiveBitrate.Should().Be(800_000);
    }

    [Fact]
    public void Min_adaptive_bitrate_out_of_range_snaps_to_default()
    {
        var store = new SettingsStore(_settingsPath);
        store.Save(new ClientSettings
        {
            Video = new VideoSettings
            {
                MinAdaptiveBitrate = -1,
            },
        });

        var loaded = new SettingsStore(_settingsPath).LoadOrCreate();
        loaded.Video.MinAdaptiveBitrate.Should().Be(500_000);
    }

    [Fact]
    public void Intra_refresh_knobs_round_trip_through_save_and_load()
    {
        var store = new SettingsStore(_settingsPath);
        var original = new ClientSettings
        {
            Video = new VideoSettings
            {
                EnableIntraRefresh = true,
                IntraRefreshPeriodFrames = 120,
            },
        };
        store.Save(original);

        var loaded = new SettingsStore(_settingsPath).LoadOrCreate();

        loaded.Video.EnableIntraRefresh.Should().BeTrue();
        loaded.Video.IntraRefreshPeriodFrames.Should().Be(120);
    }

    [Fact]
    public void Intra_refresh_period_out_of_range_snaps_to_default()
    {
        var store = new SettingsStore(_settingsPath);
        store.Save(new ClientSettings
        {
            Video = new VideoSettings
            {
                IntraRefreshPeriodFrames = 10_000,
            },
        });

        var loaded = new SettingsStore(_settingsPath).LoadOrCreate();
        loaded.Video.IntraRefreshPeriodFrames.Should().Be(60);
    }
}
