using System.Security.Cryptography;
using System.Text;

namespace SshIt.Services;

/// <summary>
/// Password hashing for a locked session. Only a salt and a PBKDF2-SHA256 hash are kept, in memory,
/// for the life of the pane — nothing is written to disk and there is no recovery path once the app
/// closes, which is the point of a session lock.
/// </summary>
public static class SessionLock
{
    public const int SaltSize = 16;
    public const int HashSize = 32;
    private const int Iterations = 100_000;

    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(SaltSize);

    public static byte[] Hash(string? password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password ?? ""), salt, Iterations,
            HashAlgorithmName.SHA256, HashSize);

    public static bool Verify(string? password, byte[]? salt, byte[]? expected)
    {
        if (salt is not { Length: > 0 } || expected is not { Length: > 0 }) return false;
        var actual = Hash(password, salt);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
