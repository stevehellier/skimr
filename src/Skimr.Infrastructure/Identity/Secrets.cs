using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace Skimr.Infrastructure.Identity;

internal static class Secrets
{
    /// <summary>256 random bits, URL-safe, with a recognisable prefix (helps secret scanners).</summary>
    public static string NewToken(string prefix) =>
        prefix + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// SHA-256 hex. Tokens are 256-bit random values, so a fast hash is enough: there is
    /// nothing to brute-force. Never use this for passwords.
    /// </summary>
    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
