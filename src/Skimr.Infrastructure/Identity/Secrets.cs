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

    /// <summary>
    /// Fever's api_key: lowercase hex md5 of "UserName:password", where the password is an API
    /// token. MD5 is what the protocol mandates; Skimr stores only a SHA-256 of the result.
    /// </summary>
    public static string FeverKey(string userName, string token)
    {
#pragma warning disable CA5351 // Fever clients compute md5 themselves; this isn't our choice of hash.
        return Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(userName + ":" + token)));
#pragma warning restore CA5351
    }

    public static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
