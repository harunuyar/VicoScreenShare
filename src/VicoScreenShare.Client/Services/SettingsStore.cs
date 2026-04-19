namespace VicoScreenShare.Client.Services;

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using VicoScreenShare.Client.Media;
using VicoScreenShare.Client.Media.Codecs;

/// <summary>
/// Persists <see cref="ClientSettings"/> to a JSON file under
/// <c>%AppData%/VicoScreenShare/settings.json</c>. Mirrors <see cref="IdentityStore"/>:
/// atomic writes via temp-file + <see cref="File.Move(string,string,bool)"/>, and
/// <see cref="LoadOrCreate"/> returns defaults when the file is missing or
/// corrupted so the app always starts cleanly. On first launch after a rename,
/// inherits the file from any prior-name folder (VicoMeet, ScreenSharing).
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
        var newPath = Path.Combine(appData, "VicoScreenShare", "settings.json");
        if (!File.Exists(newPath))
        {
            // Carry forward from whichever prior-name folder has the file.
            foreach (var legacyFolder in new[] { "VicoMeet", "ScreenSharing" })
            {
                var legacy = Path.Combine(appData, legacyFolder, "settings.json");
                if (File.Exists(legacy))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
                    try { File.Copy(legacy, newPath); } catch { }
                    break;
                }
            }
        }
        return newPath;
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
        ArgumentNullException.ThrowIfNull(settings);

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
        /// <summary>Legacy single-server URI. Migrated into Connections on load.</summary>
        public string? ServerUri { get; set; }

        /// <summary>Address book of saved server entries (name + URI + password).</summary>
        public List<PersistedConnection>? Connections { get; set; }
        public Guid? ActiveConnectionId { get; set; }

        // New schema fields.
        public int? TargetHeight { get; set; }
        public int TargetFrameRate { get; set; } = 60;
        public int TargetBitrate { get; set; } = 12_000_000;
        public double KeyframeIntervalSeconds { get; set; } = 2.0;
        public ScalerMode Scaler { get; set; } = ScalerMode.Bilinear;
        public VideoCodec Codec { get; set; } = VideoCodec.H264;
        public int ReceiveBufferFrames { get; set; } = 5;
        public bool EnableNackRtx { get; set; } = true;
        public int NackHistoryPackets { get; set; } = 128;

        // Legacy fields kept so existing settings.json files migrate instead
        // of silently dropping back to defaults. Only read on load — never
        // written back. MaxEncoderHeight becomes the new TargetHeight;
        // MaxEncoderWidth is ignored because width is now derived from the
        // source aspect ratio at runtime.
        public int? MaxEncoderWidth { get; set; }
        public int? MaxEncoderHeight { get; set; }

        public static PersistedSettings From(ClientSettings source) => new()
        {
            // ServerUri is no longer written back — single-URI is a load-time
            // concept only. The connection list carries all URIs going forward.
            Connections = source.Connections.Select(PersistedConnection.From).ToList(),
            ActiveConnectionId = source.ActiveConnectionId,
            TargetHeight = source.Video.TargetHeight,
            TargetFrameRate = source.Video.TargetFrameRate,
            TargetBitrate = source.Video.TargetBitrate,
            KeyframeIntervalSeconds = source.Video.KeyframeIntervalSeconds,
            Scaler = source.Video.Scaler,
            Codec = source.Video.Codec,
            ReceiveBufferFrames = source.Video.ReceiveBufferFrames,
            EnableNackRtx = source.Video.EnableNackRtx,
            NackHistoryPackets = source.Video.NackHistoryPackets,
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
                    Scaler = Scaler,
                    Codec = Codec,
                    ReceiveBufferFrames = ReceiveBufferFrames > 0 && ReceiveBufferFrames <= 240 ? ReceiveBufferFrames : 5,
                    EnableNackRtx = EnableNackRtx,
                    NackHistoryPackets = NackHistoryPackets > 0 && NackHistoryPackets <= 4096 ? NackHistoryPackets : 128,
                },
            };

            // Rehydrate the connection list if present.
            if (Connections is { Count: > 0 })
            {
                foreach (var c in Connections)
                {
                    if (c.TryMaterialize(out var entry))
                    {
                        result.Connections.Add(entry);
                    }
                }
                result.ActiveConnectionId = ActiveConnectionId;
            }

            // Migration path for files written by the pre-address-book client:
            // if there are no connections but the legacy ServerUri is set,
            // synthesize a single entry from it and mark it active.
            if (result.Connections.Count == 0 && !string.IsNullOrWhiteSpace(ServerUri)
                && Uri.TryCreate(ServerUri, UriKind.Absolute, out var legacyUri))
            {
                var legacyEntry = new ServerConnection { Uri = legacyUri };
                result.Connections.Add(legacyEntry);
                result.ActiveConnectionId = legacyEntry.Id;
            }

            // Heal an orphaned ActiveConnectionId — the user might have
            // deleted the active entry via a hand edit. Fall back to the
            // first remaining entry, or leave null if list is empty.
            if (result.ActiveConnectionId is Guid id && !result.Connections.Any(c => c.Id == id))
            {
                result.ActiveConnectionId = result.Connections.FirstOrDefault()?.Id;
            }
            else if (result.ActiveConnectionId is null && result.Connections.Count > 0)
            {
                result.ActiveConnectionId = result.Connections[0].Id;
            }

            return result;
        }
    }

    private sealed class PersistedConnection
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Uri { get; set; } = string.Empty;
        public string? Password { get; set; }

        public static PersistedConnection From(ServerConnection source) => new()
        {
            Id = source.Id,
            Name = source.Name,
            Uri = source.Uri.ToString(),
            Password = string.IsNullOrEmpty(source.Password) ? null : source.Password,
        };

        public bool TryMaterialize(out ServerConnection entry)
        {
            entry = default!;
            if (!System.Uri.TryCreate(Uri, UriKind.Absolute, out var parsed))
            {
                return false;
            }

            entry = new ServerConnection
            {
                Id = Id == Guid.Empty ? Guid.NewGuid() : Id,
                Name = Name ?? string.Empty,
                Uri = parsed,
                Password = Password,
            };
            return true;
        }
    }
}
