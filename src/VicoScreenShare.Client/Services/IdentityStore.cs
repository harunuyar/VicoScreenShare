using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VicoScreenShare.Client.Services;

/// <summary>
/// Persists the local profile (stable user id + display name) to
/// <c>%AppData%/VicoScreenShare/profile.json</c>. Reads return a cached copy;
/// writes go through a temp file so a crash mid-write never corrupts the stored
/// profile. On first launch after a rename, inherits the file from any
/// prior-name folder (VicoMeet, ScreenSharing) so stable user id survives.
/// </summary>
public sealed class IdentityStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _lock = new();
    private readonly string _profilePath;
    private UserProfile? _cached;

    public IdentityStore() : this(DefaultProfilePath())
    {
    }

    public IdentityStore(string profilePath)
    {
        _profilePath = profilePath;
    }

    public static string DefaultProfilePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var newPath = Path.Combine(appData, "VicoScreenShare", "profile.json");
        if (!File.Exists(newPath))
        {
            foreach (var legacyFolder in new[] { "VicoMeet", "ScreenSharing" })
            {
                var legacy = Path.Combine(appData, legacyFolder, "profile.json");
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

    /// <summary>
    /// Load the profile from disk, or create a fresh one with a new GUID and empty
    /// display name if the file does not yet exist.
    /// </summary>
    public UserProfile LoadOrCreate()
    {
        lock (_lock)
        {
            if (_cached is not null)
            {
                return _cached;
            }

            if (File.Exists(_profilePath))
            {
                try
                {
                    var json = File.ReadAllText(_profilePath);
                    var parsed = JsonSerializer.Deserialize<UserProfile>(json, JsonOptions);
                    if (parsed is not null && parsed.UserId != Guid.Empty)
                    {
                        _cached = parsed;
                        return _cached;
                    }
                }
                catch (Exception)
                {
                    // Corrupted profile falls through to a fresh one; old file is overwritten on next save.
                }
            }

            _cached = new UserProfile
            {
                UserId = Guid.NewGuid(),
                DisplayName = string.Empty,
            };
            return _cached;
        }
    }

    /// <summary>
    /// Atomically persist a profile update. Writes to a temp file in the same
    /// directory and then uses <see cref="File.Move(string, string, bool)"/> to
    /// replace the target so partial writes are never visible.
    /// </summary>
    public void Save(UserProfile profile)
    {
        if (profile is null) throw new ArgumentNullException(nameof(profile));
        if (profile.UserId == Guid.Empty)
        {
            throw new ArgumentException("UserId must be set before saving.", nameof(profile));
        }

        lock (_lock)
        {
            var dir = Path.GetDirectoryName(_profilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tempPath = _profilePath + ".tmp";
            var json = JsonSerializer.Serialize(profile, JsonOptions);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _profilePath, overwrite: true);

            _cached = profile;
        }
    }
}

public sealed class UserProfile
{
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}
