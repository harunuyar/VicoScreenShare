using FluentAssertions;
using ScreenSharing.Server.Auth;

namespace ScreenSharing.Tests.Server;

public class PasswordHasherTests
{
    private readonly PasswordHasher _hasher = new();

    [Fact]
    public void Verify_accepts_correct_password()
    {
        var hash = _hasher.Hash("hunter2");
        _hasher.Verify("hunter2", hash).Should().BeTrue();
    }

    [Fact]
    public void Verify_rejects_wrong_password()
    {
        var hash = _hasher.Hash("hunter2");
        _hasher.Verify("hunter3", hash).Should().BeFalse();
    }

    [Fact]
    public void Verify_rejects_empty_inputs()
    {
        var hash = _hasher.Hash("hunter2");
        _hasher.Verify("", hash).Should().BeFalse();
        _hasher.Verify("hunter2", "").Should().BeFalse();
    }

    [Fact]
    public void Hash_throws_on_empty_password()
    {
        var act = () => _hasher.Hash("");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Verify_rejects_malformed_hash()
    {
        _hasher.Verify("hunter2", "not-a-valid-bcrypt-hash").Should().BeFalse();
    }
}
