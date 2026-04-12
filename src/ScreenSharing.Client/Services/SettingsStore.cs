using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScreenSharing.Client.Media;

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
        public int MaxEncoderWidth { get; set; } = 1280;
        public int MaxEncoderHeight { get; set; } = 720;
        public int TargetFrameRate { get; set; } = 30;

        public static PersistedSettings From(ClientSettings source) => new()
        {
            ServerUri = source.ServerUri.ToString(),
            MaxEncoderWidth = source.Video.MaxEncoderWidth,
            MaxEncoderHeight = source.Video.MaxEncoderHeight,
            TargetFrameRate = source.Video.TargetFrameRate,
        };

        public ClientSettings ToClientSettings()
        {
            var result = new ClientSettings
            {
                Video = new VideoSettings
                {
                    MaxEncoderWidth = MaxEncoderWidth,
                    MaxEncoderHeight = MaxEncoderHeight,
                    TargetFrameRate = TargetFrameRate,
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
