using System.Security.Cryptography;

namespace CareerHub.Api.Services;

/// <summary>
/// PBKDF2 password hashing using only built-in .NET crypto (no NuGet package).
/// Stored format: "{base64 salt}.{base64 hash}". A random per-password salt means
/// two users with the same password get different hashes; 100k iterations make
/// brute-forcing expensive; <see cref="CryptographicOperations.FixedTimeEquals"/>
/// avoids leaking information through comparison timing.
/// </summary>
public static class PasswordHasher
{
    private const int SaltSize = 16;
    private const int KeySize = 32;
    private const int Iterations = 100_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        return $"{Convert.ToBase64String(salt)}.{Convert.ToBase64String(key)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('.', 2);
        if (parts.Length != 2) return false;

        var salt = Convert.FromBase64String(parts[0]);
        var key = Convert.FromBase64String(parts[1]);
        var attempt = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        return CryptographicOperations.FixedTimeEquals(attempt, key);
    }
}
