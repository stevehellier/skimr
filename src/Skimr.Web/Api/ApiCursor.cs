using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Skimr.Infrastructure.Reading;

namespace Skimr.Web.Api;

/// <summary>
/// Opaque paging cursor for the APIs: the keyset position, base64url-encoded. Clients pass it
/// back unchanged; anything else is rejected (400), never a 500.
/// </summary>
internal static class ApiCursor
{
    public static string? Encode(ArticleCursor? cursor) =>
        cursor is null
            ? null
            : WebEncoders.Base64UrlEncode(Encoding.ASCII.GetBytes(
                cursor.PublishedAt.Ticks.ToString(CultureInfo.InvariantCulture) + ":" + cursor.ArticleId.ToString(CultureInfo.InvariantCulture)));

    public static bool TryDecode(string? text, out ArticleCursor? cursor)
    {
        cursor = null;
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        try
        {
            var parts = Encoding.ASCII.GetString(WebEncoders.Base64UrlDecode(text)).Split(':');
            if (parts.Length == 2
                && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                && ticks >= DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks
                && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                cursor = new ArticleCursor(new DateTime(ticks, DateTimeKind.Utc), id);
                return true;
            }
        }
        catch (FormatException)
        {
            // Not base64url: fall through to "invalid".
        }

        return false;
    }
}
