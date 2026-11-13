using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Skimr.Domain;

public static class ArticleIdentity
{
    /// <summary>
    /// Identity of an item within its feed: the GUID, else the link, else title + date.
    /// Hashed so the unique index stays small whatever the source strings are.
    /// </summary>
    public static string DedupKey(string? guid, string? url, string? title, DateTime? published)
    {
        var identity = !string.IsNullOrWhiteSpace(guid) ? "g:" + guid.Trim()
            : !string.IsNullOrWhiteSpace(url) ? "l:" + url.Trim()
            : "h:" + title?.Trim() + "|" + published?.ToString("O", CultureInfo.InvariantCulture);
        return Hash(identity);
    }

    public static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
