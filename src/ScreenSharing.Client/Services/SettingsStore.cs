using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScreenSharing.Client.Media;
using ScreenSharing.Client.Media.Codecs;

namespace ScreenSharing.Client.Services;

/// <summary>
/// Persists <see cref="ClientSettings"/> to a JSON file under
/// <c>%AppData%/ScreenSharing/settings.json</c>. Mirrors <see cref="IdentityStore"/>:
/// atomic writes via temp-file + <see cref="File.Move(string,string,bool)"/>, and
/// <see cref="LoadOrCreate"/> returns defaults when the file is missing or
/// corrupted so the app always starts cleanly.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _lock = new();
    private readonly string _path;

    public SettingsStore() : this(DefaultPath())
    {
    }

    public SettingsStore(string path)
    {
        _path = path;
    }

    public static string DefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "ScreenSharing", "settings.json");
    }

    public ClientSettings LoadOrCreate()
    {
        lock (_lock)
        {
            if (File.Exists(_path))
            {
                try
                {
                    var json = File.ReadAllText(_path);
                    var parsed = JsonSerializer.Deserialize<PersistedSettings>(json, JsonOptions);
                    if (parsed is not null)
                    {
                        return parsed.ToClientSettings();
                    }
                }
                catch
                {
                    // Corrupted file; fall through to defaults and the next save
                    // will overwrite it cleanly.
                }
            }
            return new ClientSettings();
        }
    }

    public void Save(ClientSettings settings)
    {
        if (settings is null) throw new ArgumentNullException(nameof(settings));

        lock (_lock)
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var persisted = PersistedSettings.From(settings);
            var json = JsonSerializer.Serialize(persisted, JsonOptions);
            var tempPath = _path + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _path, overwrite: true);
        }
    }

    /// <summary>
    /// DTO for on-disk persistence. We keep this shape stable even if
    /// <see cref="ClientSettings"/> grows internal-only fields, and we control
    /// the serialization surface explicitly so it doesn't depend on property
    /// ordering in the public class.
    /// </summary>
    private sealed class PersistedSettings
    {
        public string? ServerUri { get; set; }

        // New schema fields.
        public int? TargetHeight { get; set; }
        public int TargetFrameRate { get; set; } = 60;
        public int TargetBitrate { get; set; } = 12_000_000;
        public double KeyframeIntervalSeconds { get; set; } = 2.0;
        public ScalerQuality ScalerQuality { get; set; } = ScalerQuality.Bilinear;
        public VideoCodec Codec { get; set; } = VideoCodec.H264;
        public int ReceiveBufferFrames { get; set; } = 3;

        // Legacy fields kept so existing settings.json files migrate instead
        // of silently dropping back to defaults. Only read on load — never
        // written back. MaxEncoderHeight becomes the new TargetHeight;
        // MaxEncoderWidth is ignored because width is now derived from the
        // source aspect ratio at runtime.
        public int? MaxEncoderWidth { get; set; }
        public int? MaxEncoderHeight { get; set; }

        public static PersistedSettings From(ClientSettings source) => new()
        {
            ServerUri = source.ServerUri.ToString(),
            TargetHeight = source.Video.TargetHeight,
            TargetFrameRate = source.Video.TargetFrameRate,
            TargetBitrate = source.Video.TargetBitrate,
            KeyframeIntervalSeconds = source.Video.KeyframeIntervalSeconds,
            ScalerQuality = source.Video.ScalerQuality,
            Codec = source.Video.Codec,
            ReceiveBufferFrames = source.Video.ReceiveBufferFrames,
        };

        public ClientSettings ToClientSettings()
        {
            var height = TargetHeight ?? MaxEncoderHeight ?? 1080;
            var result = new ClientSettings
            {
                Video = new VideoSettings
                {
                    TargetHeight = height,
                    TargetFrameRate = TargetFrameRate,
                    TargetBitrate = TargetBitrate,
                    KeyframeIntervalSeconds = KeyframeIntervalSeconds,
                    ScalerQuality = ScalerQuality,
                    Codec = Codec,
                    ReceiveBufferFrames = ReceiveBufferFrames > 0 ? ReceiveBufferFrames : 3,
                },
            };
            if (!string.IsNullOrWhiteSpace(ServerUri) && Uri.TryCreate(ServerUri, UriKind.Absolute, out var uri))
            {
                result.ServerUri = uri;
            }
            return result;
        }
    }
}
