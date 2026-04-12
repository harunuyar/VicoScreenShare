namespace ScreenSharing.Server.Auth;

/// <summary>
/// BCrypt-backed password hasher for optional room passwords. Callers must treat a
/// null-or-empty password as "no password" and never hash an empty string.
/// BCrypt.Verify internally uses constant-time comparison.
/// </summary>
public sealed class PasswordHasher
{
    private const int WorkFactor = 11;

    public string Hash(string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("Password must be non-empty. Check IsNullOrEmpty before hashing.", nameof(password));
        }
        return BCrypt.Net.BCrypt.HashPassword(password, WorkFactor);
    }

    public bool Verify(string password, string hash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(hash))
        {
            return false;
        }
        try
        {
            return BCrypt.Net.BCrypt.Verify(password, hash);
        }
        catch (BCrypt.Net.SaltParseException)
        {
            return false;
        }
    }
}
