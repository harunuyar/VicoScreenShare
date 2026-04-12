using FluentAssertions;
using ScreenSharing.Client.Services;

namespace ScreenSharing.Tests.Client;

public class IdentityStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _profilePath;

    public IdentityStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ScreenSharing.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _profilePath = Path.Combine(_tempDir, "profile.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void LoadOrCreate_creates_new_profile_when_file_missing()
    {
        var store = new IdentityStore(_profilePath);
        var profile = store.LoadOrCreate();

        profile.UserId.Should().NotBe(Guid.Empty);
        profile.DisplayName.Should().BeEmpty();
        File.Exists(_profilePath).Should().BeFalse("Phase 1 only persists on Save");
    }

    [Fact]
    public void Save_then_LoadOrCreate_returns_same_profile()
    {
        var store = new IdentityStore(_profilePath);
        var id = Guid.NewGuid();
        store.Save(new UserProfile { UserId = id, DisplayName = "Alice" });

        var store2 = new IdentityStore(_profilePath);
        var loaded = store2.LoadOrCreate();

        loaded.UserId.Should().Be(id);
        loaded.DisplayName.Should().Be("Alice");
    }

    [Fact]
    public void Save_overwrites_existing_profile()
    {
        var store = new IdentityStore(_profilePath);
        var id = Guid.NewGuid();
        store.Save(new UserProfile { UserId = id, DisplayName = "Alice" });
        store.Save(new UserProfile { UserId = id, DisplayName = "Bob" });

        var loaded = new IdentityStore(_profilePath).LoadOrCreate();
        loaded.DisplayName.Should().Be("Bob");
    }

    [Fact]
    public void Save_creates_directory_if_missing()
    {
        var nestedPath = Path.Combine(_tempDir, "subdir", "nested", "profile.json");
        var store = new IdentityStore(nestedPath);
        store.Save(new UserProfile { UserId = Guid.NewGuid(), DisplayName = "Nested" });

        File.Exists(nestedPath).Should().BeTrue();
    }

    [Fact]
    public void Save_rejects_empty_user_id()
    {
        var store = new IdentityStore(_profilePath);
        var act = () => store.Save(new UserProfile { UserId = Guid.Empty, DisplayName = "X" });
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void LoadOrCreate_recovers_from_corrupted_profile()
    {
        File.WriteAllText(_profilePath, "{ this is not valid json }");
        var store = new IdentityStore(_profilePath);
        var profile = store.LoadOrCreate();
        profile.UserId.Should().NotBe(Guid.Empty);
    }
}
